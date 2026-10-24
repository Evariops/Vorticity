using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// The rows of one object, arriving in time order, written slice by slice in that order, as its slice table says, or as a
/// stream cuts its slices as the rows come (<see cref="SliceStream"/>). A slice's rows are gathered, their columns made
/// one, and written whole.
/// </summary>
/// <remarks>
/// <para>
/// A slice is held whole while it is joined, at most twice the slice target's rows, which a burst bounds.
/// </para>
/// <para>
/// The rows come in on the caller's thread, which only copies them into a slice's buffer, its columns laid out for all of
/// its rows (<see cref="SliceFill"/>), or, for a stream's, for those the slice before held and the most the rows rose from
/// one slice to the next, grown past them when a slice holds more: a slice
/// full is joined and written on another thread, each slice in a buffer of its own, <see cref="InFlight"/> of them held at
/// most, one filling and the other joined or written. A slice's write starts once the one before it is written, so the
/// writes keep the slices' order; its join, as soon as its rows are in, beside the writes of the slices before it. A
/// stream's last slice ends with the rows (<see cref="EndAsync"/>); <see cref="DrainAsync"/> waits for the last one to be
/// written.
/// </para>
/// </remarks>
internal sealed class SortedSlices : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The slices held at most: one whose rows come in, and one joined, then written. A third, filling while the two
    /// others sort and write, held a period's load a slice ahead of its writes, which only a lone period whose writes
    /// hold it gains from: five periods loaded at once, their lanes all taken, held 18.3 GB and took 485-495 s of the
    /// processors with three, 13.3 GB and 455-460 s with two, in the same 25.2-25.5 s; a lone day took 17.1-17.2 s with
    /// three and 17.7-18.0 s with two (<c>vortex-queries --micro load --periods 5</c>, then <c>--periods 1</c>).
    /// </summary>
    internal const int InFlight = 2;

    /// <summary>
    /// The rows a stream's first slice holds at least, whose pace chooses the width of its slices: eight blocks. A pace
    /// read off a few rows, which only a small object's or a test's are, says too little of the object's.
    /// </summary>
    internal const int JudgedRows = 1 << 16;

    private readonly TimeUnit _unit;
    private readonly string _instantPath;
    private readonly DType _schema;
    private readonly Buffer[] _buffers = [.. Enumerable.Range(0, InFlight).Select(_ => new Buffer())];

    // The slices the table lists, or the stream that cuts them as the rows come; the rows a slice of the stream is laid
    // out for, those of the slice before it and the most they rose from one slice to the next; and the write of its
    // slices, which its last one goes to once the rows end.
    private readonly IReadOnlyList<TimeSlice>? _slices;
    private readonly SliceStream? _stream;
    private readonly long _target;
    private int _room;
    private long _before = -1;
    private long _rise;
    private Func<RecordBatch, CancellationToken, ValueTask>? _write;

    // Each buffer's slice being joined or written, null when the buffer is free; and the last slice handed over, which
    // completes once every slice before it is written.
    private readonly Task?[] _flights = new Task?[InFlight];
    private Task? _last;
    private int _filling;
    private int _slice;
    private long _held;

    /// <summary>A writer of the slices <paramref name="table"/> lists, the instant field <paramref name="instant"/> of <paramref name="schema"/>.</summary>
    internal SortedSlices(SliceTable table, int instant, DType schema)
        : this(table.Unit, instant, schema)
    {
        _slices = table.Slices;
    }

    /// <summary>
    /// A writer of the slices <paramref name="stream"/> cuts as the rows come: the stream's first slice is laid out for
    /// <paramref name="rows"/> rows, those its pace holds, each after it for those of the slice before and the most the
    /// rows rose from one slice to the next, twice <paramref name="target"/> at most, a burst's bound. A stream at a steady
    /// pace lays its slices out for what they hold and a sixty-fourth more, its noise: a slice a few rows past its room
    /// doubles it, and the room it outgrew stays with the buffer. A margin of an eighth held 2.3 GB more than the rows at
    /// the start of five periods loaded at once, and 0.35 GB after, its room unwritten.
    /// </summary>
    internal SortedSlices(SliceStream stream, int instant, DType schema, double rows, long target)
        : this(stream.Unit, instant, schema)
    {
        _stream = stream;
        _target = target;
        _room = Room(rows);
    }

    private SortedSlices(TimeUnit unit, int instant, DType schema)
    {
        _unit = unit;
        _instantPath = schema.GetFieldName(instant);
        _schema = schema;
    }

    /// <summary>Where the stages of its slices take their time is added; null for no count.</summary>
    internal WriteTimings? Timings { get; init; }

    /// <summary>
    /// The session's lanes a slice is joined and written on, one a slice; null for joins and writes on no lane.
    /// <see cref="Hold"/>, when set, is the lane of the step that hands the rows over, given back while it waits for a
    /// buffer to be free.
    /// </summary>
    internal LaneBudget? Lanes { get; init; }

    /// <inheritdoc cref="Lanes"/>
    internal LaneHold? Hold { get; init; }

    /// <summary>The slices, the table's or those the stream cut: the stream's once its last slice is handed over (<see cref="EndAsync"/>).</summary>
    internal SliceTable Table => new SliceTable(_unit, _stream?.Slices ?? _slices!);

    /// <summary>
    /// Takes the next rows in time order, writing each slice they complete through <paramref name="write"/>, on another
    /// thread while the rows after it come in; the last slice is written once <see cref="DrainAsync"/> completes.
    /// </summary>
    internal async ValueTask AppendAsync(RecordBatch rows, Func<RecordBatch, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
    {
        _write = write;
        int at = 0;
        while (at < rows.RowCount)
        {
            (at, bool full) = Fill(rows, at);
            if (full)
            {
                await FlushAsync(write, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Copies the rows of <paramref name="rows"/> from <paramref name="at"/> that the slice being filled takes into its
    /// buffer: the row after them, and whether the slice is whole, the table's rows of it held, or the stream's ended.
    /// </summary>
    private (int At, bool Full) Fill(RecordBatch rows, int at)
    {
        int take;
        bool full;
        if (_stream is { } stream)
        {
            (take, full) = stream.Take(ColumnPath.Instants(rows, _instantPath)[at..], _held);
        }
        else
        {
            if (_slice >= _slices!.Count)
            {
                throw new InvalidOperationException("Rows came past the last slice of the object's slice table.");
            }

            take = (int)Math.Min(_slices[_slice].Rows - _held, rows.RowCount - at);
            full = _held + take == _slices[_slice].Rows;
        }

        if (take > 0)
        {
            RecordBatch part = at == 0 && take == rows.RowCount ? rows : rows.Window(at, take);
            Buffer buffer = _buffers[_filling];
            long begin = Stopwatch.GetTimestamp();
            if (_held == 0)
            {
                // A slice of the stream is laid out for the rows it is expected to hold, and grows past them.
                buffer.Fill.Begin(buffer.Context.Canonical, _schema, _stream is null ? checked((int)_slices![_slice].Rows) : _room, exact: _stream is null);
            }

            buffer.Fill.Append(buffer.Context.Canonical, part.Arena, part.RootIndex, take);
            Timings?.AddCopy(Stopwatch.GetTimestamp() - begin);
            _held += take;
        }

        return (at + take, full);
    }

    /// <summary>
    /// Hands the slice the stream holds over to its write, its last: the rows ended. Nothing for slices of a table, whose
    /// last is handed over with its last row.
    /// </summary>
    internal ValueTask EndAsync(CancellationToken cancellationToken) =>
        _stream is not null && _held > 0 ? FlushAsync(_write!, cancellationToken) : ValueTask.CompletedTask;

    /// <summary>Whether every slice was handed to its write, each of the table's; <see cref="DrainAsync"/> says when the last is written.</summary>
    internal bool Complete => _held == 0 && (_stream is not null || _slice == _slices!.Count);

    /// <summary>
    /// The rows a slice of the stream is laid out for: the rows <paramref name="rows"/> it is expected to hold and a
    /// sixty-fourth more, a burst's bound at most.
    /// </summary>
    private int Room(double rows) =>
        (int)Math.Clamp(Math.Min(rows + (rows / 64), 2.0 * _target), 1, int.MaxValue);

    /// <summary>
    /// Waits for every slice handed over to be written, on no lane, and throws what the oldest that failed threw: every one,
    /// since a slice that fails to join ends before the slices before it are written.
    /// </summary>
    internal async ValueTask DrainAsync()
    {
        _last = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        for (int i = 1; i <= InFlight; i++)
        {
            int b = (_filling + i) % InFlight;
            if (_flights[b] is { } flight)
            {
                _flights[b] = null;
                try
                {
                    await WaitAsync(flight).ConfigureAwait(false);
                }
                catch (Exception thrown)
                {
                    failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(thrown);
                }
            }
        }

        failure?.Throw();
    }

    /// <summary>
    /// Lets the buffers go, once every slice being joined or written is done with them, waited for on no lane: a caller that
    /// gives the object up rather than drains it has its reason already, so what those threw is observed and dropped.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _last = null;
        for (int b = 0; b < _flights.Length; b++)
        {
            if (_flights[b] is { } flight)
            {
                _flights[b] = null;
                try
                {
                    await WaitAsync(flight).ConfigureAwait(false);
                }
                catch (Exception) when (flight.IsFaulted || flight.IsCanceled)
                {
                    // The flight's own failure, observed: the caller gives the object up for a reason of its own.
                }
            }
        }

        foreach (Buffer buffer in _buffers)
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// <see cref="DisposeAsync"/> for a caller that holds no lane of the session's, which may wait for the slices in flight
    /// on its thread: one that holds a lane gives the object up through <see cref="DisposeAsync"/>, which gives the lane
    /// back while it waits.
    /// </summary>
    public void Dispose()
    {
        _last = null;
        for (int b = 0; b < _flights.Length; b++)
        {
            if (_flights[b] is { } flight)
            {
                _flights[b] = null;
                ((IAsyncResult)flight).AsyncWaitHandle.WaitOne();
                _ = flight.Exception;
            }
        }

        foreach (Buffer buffer in _buffers)
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Hands the slice held over to be joined and written on another thread, written once the slice before it is, and
    /// waits for the next buffer to be free: the rows after it go there meanwhile.
    /// </summary>
    private async ValueTask FlushAsync(Func<RecordBatch, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
    {
        Buffer buffer = _buffers[_filling];
        if (_stream is { } stream)
        {
            int rows = checked((int)_held);
            stream.Close(rows);
            _rise = _before < 0 ? 0 : Math.Max(_rise, rows - _before);
            _before = rows;
            _room = Room(rows + _rise);
        }

        Task previous = _last ?? Task.CompletedTask;
        LaneBudget? lanes = Lanes;
        Task flight = Task.Run(
            async () =>
            {
                try
                {
                    // A slice joins on a lane of its own, beside the writes of the slices before it, then gives the lane
                    // back while it waits for its turn: the writes keep the slices' order.
                    int root;
                    long joining = lanes is null ? -1 : await lanes.EnterAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        root = Join(buffer);
                    }
                    finally
                    {
                        if (joining >= 0)
                        {
                            lanes!.Exit(joining);
                        }
                    }

                    // The slice before's flight, started on the pool, which no context of the caller's runs.
#pragma warning disable VSTHRD003
                    await previous.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    long entered = lanes is null ? -1 : await lanes.EnterAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        using RecordBatch batch = new RecordBatch(buffer.Context.Canonical, root, 0);
                        await write(batch, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (entered >= 0)
                        {
                            lanes!.Exit(entered);
                        }
                    }
                }
                finally
                {
                    buffer.Clear();
                }
            },
            CancellationToken.None);
        _flights[_filling] = flight;
        _last = flight;
        _held = 0;
        _slice++;

        // The next rows go to the next buffer, once the slice it held is written.
        _filling = (_filling + 1) % InFlight;
        if (_flights[_filling] is { } busy)
        {
            long waiting = Stopwatch.GetTimestamp();
            _flights[_filling] = null;
            await WaitAsync(busy).ConfigureAwait(false);
            Timings?.AddWait(Stopwatch.GetTimestamp() - waiting);
        }
    }

    /// <summary>Waits for <paramref name="flight"/>, giving the lane of the step that hands the rows over back meanwhile.</summary>
    private async ValueTask WaitAsync(Task flight)
    {
        if (Hold is { } hold)
        {
            await hold.WaitAsync(flight, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            // A slice's flight, started on the pool, which no context of the caller's runs.
#pragma warning disable VSTHRD003
            await flight.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
    }

    /// <summary>The slice <paramref name="buffer"/> holds, its columns made one struct, those whose batches were copied apart joined: its root.</summary>
    private int Join(Buffer buffer)
    {
        long begin = Stopwatch.GetTimestamp();
        int whole = buffer.Fill.Finish(buffer.Context);
        Timings?.AddJoin(Stopwatch.GetTimestamp() - begin);
        return whole;
    }

    /// <summary>A slice's rows as they come, then joined for its write.</summary>
    private sealed class Buffer : IDisposable
    {
        internal ScanContext Context { get; } = new ScanContext([]);

        /// <summary>What the slice's rows are copied into as they come, its columns laid out for all of them.</summary>
        internal SliceFill Fill { get; } = new SliceFill();

        /// <summary>Lets the slice's rows go, for the next slice's.</summary>
        internal void Clear()
        {
            Fill.Clear();
            Context.ResetBatch();
        }

        public void Dispose() => Context.Dispose();
    }
}
