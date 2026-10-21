using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.RowEncoding;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// The rows of one object, arriving in time order, written slice by slice, each slice sorted by the
/// object's synopsis columns, then by its instant, as its slice table says. A slice's rows are gathered,
/// their keys encoded in the order the table names, the slice's rows sorted by those keys alone, and
/// every column gathered once through the permutation.
/// </summary>
/// <remarks>
/// <para>
/// A key is the row encoding of the synopsis columns and the instant, which sorts as the tuple does:
/// a column's values by their own order, nulls last, the codes a dictionary assigned in the order of its
/// values compared in the same order. Rows of equal keys keep the order they came in, so the same rows
/// sort the same way whoever writes them.
/// </para>
/// <para>
/// A slice is held whole while it sorts, at most twice the slice target's rows, which a burst bounds.
/// </para>
/// <para>
/// The table's columns are the object's synopsis columns, which repeat over its rows; their order is the
/// first slice's to settle. It orders them greedily by the runs each adds after those before it, measured
/// on a sample of the slice, which comes back to the order of their numbers of distinct values when they
/// are independent and puts a column nested in another right after it. A variant column the object
/// shreds adds its rows' shape (<see cref="Types.Variants.VariantShapes"/>), known only once the object has
/// chosen its paths, after the table was made, when its shapes repeat twice on average in the slice. The
/// order is then the object's for every slice, and <see cref="Table"/> records it.
/// </para>
/// <para>
/// The rows come in on the caller's thread, which only copies them into a slice's buffer, its columns laid out for all of
/// its rows (<see cref="SliceFill"/>): a slice full is sorted and written on another thread, each slice in a buffer of its
/// own, <see cref="InFlight"/> of them held at most, one filling and the other sorting or written. A slice's write starts once the one before it is written, so the writes
/// keep the slices' order; its sort, as soon as its rows are in, beside the writes of the slices before it. The first
/// slice is sorted on the caller's thread, as it settles the order the others are sorted in. <see cref="DrainAsync"/>
/// waits for the last one.
/// </para>
/// </remarks>
internal sealed class SortedSlices : IDisposable
{
    /// <summary>
    /// The slices held at most: one whose rows come in, and one sorting, then written. A third, filling while the two
    /// others sort and write, held a period's load a slice ahead of its writes, which only a lone period whose writes
    /// hold it gains from: five periods loaded at once, their lanes all taken, held 18.3 GB and took 485-495 s of the
    /// processors with three, 13.3 GB and 455-460 s with two, in the same 25.2-25.5 s; a lone day took 17.1-17.2 s with
    /// three and 17.7-18.0 s with two.
    /// </summary>
    internal const int InFlight = 2;

    private readonly SliceTable _table;
    private readonly int _instant;
    private readonly DType _schema;
    private readonly IReadOnlyDictionary<int, Types.Variants.VariantShapes>? _shapes;
    private readonly Buffer[] _buffers = [new Buffer(), new Buffer(), new Buffer()];

    // Each buffer's slice being sorted or written, null when the buffer is free; and the last slice handed over, which
    // completes once every slice before it is written.
    private readonly Task?[] _flights = new Task?[InFlight];
    private Task? _last;
    private int _filling;
    private List<int>? _order;
    private int _slice;
    private long _held;

    /// <summary>
    /// A writer of the slices <paramref name="table"/> lists, the instant field <paramref name="instant"/> of
    /// <paramref name="schema"/>, the shapes of the variant fields <paramref name="shapes"/> holds a coder for
    /// placed in the order by its first slice.
    /// </summary>
    internal SortedSlices(SliceTable table, int instant, DType schema, IReadOnlyDictionary<int, Types.Variants.VariantShapes>? shapes = null)
    {
        _table = table;
        _instant = instant;
        _schema = schema;
        _shapes = shapes is { Count: > 0 } ? shapes : null;
    }

    /// <summary>Where the stages of its slices take their time is added; null for no count.</summary>
    internal WriteTimings? Timings { get; init; }

    /// <summary>
    /// The session's lanes a slice is sorted and written on, one a slice, and its columns coded and gathered on; null for
    /// sorts and writes on no lane. <see cref="Hold"/>, when set, is the lane of the step that hands the rows over, given
    /// back while it waits for a buffer to be free.
    /// </summary>
    internal LaneBudget? Lanes { get; init; }

    /// <inheritdoc cref="Lanes"/>
    internal LaneHold? Hold { get; init; }

    /// <summary>The order the slices are sorted in: the table's until the first slice settles it.</summary>
    internal IReadOnlyList<int> Order => _order ?? (IReadOnlyList<int>)_table.Order;

    /// <summary>The slices, sorted in the order the first slice settled: the table's, the shapes and paths placed in it.</summary>
    internal SliceTable Table => _order is { } order && !order.SequenceEqual(_table.Order)
        ? new SliceTable(_table.Unit, _table.Slices, order, _table.Paths)
        : _table;

    // Which of the table's paths each slice reads, once the first slice settled the order; every one before.
    private bool[]? _wanted;

    /// <summary>
    /// The paths left in the residual whose values repeat, which the first slice found: the synopsis counts
    /// their values per hour rather than sort by them.
    /// </summary>
    internal IReadOnlyList<int> CountedPaths => _counted;

    private readonly List<int> _counted = [];

    /// <summary>
    /// Takes the next rows in time order, writing each slice they complete through <paramref name="write"/>, on another
    /// thread while the rows after it come in; the last slice is written once <see cref="DrainAsync"/> completes.
    /// </summary>
    internal async ValueTask AppendAsync(RecordBatch rows, Func<SortedSlice, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
    {
        int at = 0;
        while (at < rows.RowCount)
        {
            if (_slice >= _table.Slices.Count)
            {
                throw new InvalidOperationException("Rows came past the last slice of the object's slice table.");
            }

            long wanted = _table.Slices[_slice].Rows - _held;
            int take = (int)Math.Min(wanted, rows.RowCount - at);
            RecordBatch part = at == 0 && take == rows.RowCount ? rows : rows.Window(at, take);
            Buffer buffer = _buffers[_filling];
            long begin = Stopwatch.GetTimestamp();
            if (_held == 0)
            {
                buffer.Fill.Begin(buffer.Context.Canonical, _schema, checked((int)_table.Slices[_slice].Rows));
            }

            buffer.Fill.Append(buffer.Context.Canonical, part.Arena, part.RootIndex, take);
            Timings?.AddCopy(Stopwatch.GetTimestamp() - begin);
            _held += take;
            at += take;
            if (_held == _table.Slices[_slice].Rows)
            {
                await FlushAsync(write, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether every slice of the table was handed to its write; <see cref="DrainAsync"/> says when the last is written.</summary>
    internal bool Complete => _slice == _table.Slices.Count && _held == 0;

    /// <summary>
    /// Waits for every slice handed over to be written, on no lane, and throws what the oldest that failed threw: every one,
    /// since a slice that fails to sort ends before the slices before it are written.
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
    /// Lets the buffers go, once every slice being sorted or written is done with them: a caller that gives the object up
    /// rather than drains it has its reason already, so what those threw is observed and dropped.
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
    /// Hands the slice held over to be sorted and written on another thread, written once the slice before it is, and
    /// waits for the next buffer to be free: the rows after it go there meanwhile. The first slice is sorted here, as it
    /// settles the order the others sort in.
    /// </summary>
    private async ValueTask FlushAsync(Func<SortedSlice, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
    {
        Buffer buffer = _buffers[_filling];
        int rows = checked((int)_held);
        int sorted = -1;
        if (_order is null)
        {
            sorted = JoinAndSort(buffer, rows);
        }

        Task previous = _last ?? Task.CompletedTask;
        LaneBudget? lanes = Lanes;
        Task flight = Task.Run(
            async () =>
            {
                try
                {
                    // A slice sorts on a lane of its own, beside the writes of the slices before it, then gives the lane
                    // back while it waits for its turn: the writes keep the slices' order.
                    int root = sorted;
                    if (root < 0)
                    {
                        long sorting = lanes is null ? -1 : await lanes.EnterAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            root = JoinAndSort(buffer, rows);
                        }
                        finally
                        {
                            if (sorting >= 0)
                            {
                                lanes!.Exit(sorting);
                            }
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
                        await write(new SortedSlice(batch, buffer.Codes, buffer.PathColumns, buffer.Packed), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The slice <paramref name="buffer"/> holds, its columns made one struct, those whose batches were copied apart joined,
    /// then sorted: its root.
    /// </summary>
    private int JoinAndSort(Buffer buffer, int rows)
    {
        long begin = Stopwatch.GetTimestamp();
        int whole = buffer.Fill.Finish(buffer.Context);
        long joined = Stopwatch.GetTimestamp();
        int sorted = Sort(buffer, whole, rows);
        Timings?.AddJoin(joined - begin);
        Timings?.AddSort(Stopwatch.GetTimestamp() - joined);
        return sorted;
    }

    /// <summary>
    /// The slice's rows sorted by its synopsis columns, rows of equal values in the order they came,
    /// which is the instant's: the rows arrive in time order.
    /// </summary>
    private int Sort(Buffer buffer, int node, int rows)
    {
        CanonicalArena arena = buffer.Context.Canonical;
        List<(int[] Codes, int Distinct)> columns;
        bool coded = false;
        buffer.Codes.Clear();
        buffer.PathColumns.Clear();

        // The paths' values, every path's on the first slice, then those the order and the synopsis read.
        int[] paths = ReadPaths(buffer, node);
        if (_order is null && (_shapes is not null || _table.Order.Count > 0 || _table.Paths.Count > 0))
        {
            // The first slice settles the object's order, and sorts by the codes it measured it with.
            (_order, columns) = Place(arena, node, rows, paths);
            _wanted = new bool[_table.Paths.Count];
            for (int p = 0; p < _wanted.Length; p++)
            {
                _wanted[p] = _order.Contains(SynopsisPath.Entry(p)) || _counted.Contains(SynopsisPath.Entry(p))
                    || _table.Paths[p].Class is Types.Variants.VariantClass.Integer or Types.Variants.VariantClass.Double;
            }
        }
        else
        {
            _order ??= [.. _table.Order];
            columns = Coded(buffer, arena, node, rows, paths, _order);
            coded = true;
        }

        int[]? permutation = columns.Count == 0 ? null : SliceSort.Permutation(columns, rows, buffer.SortScratch);
        buffer.Packed = permutation is not null && buffer.SortScratch.PackedBits > 0
            ? new PackedKeys(buffer.SortScratch.SortedKeys, buffer.SortScratch.Shifts, buffer.SortScratch.Widths)
            : null;
        bool identity = permutation is null || IsIdentity(permutation.AsSpan(0, rows));

        // Each path's values, in the order the rows are sorted in.
        foreach (int column in paths)
        {
            buffer.PathColumns.Add(column < 0 || identity ? column : CanonicalFilter.Apply(arena, column, permutation!.AsSpan(0, rows)));
        }

        if (permutation is null)
        {
            return node;
        }

        // Each entry's codes, in the order the rows are sorted in: what the slice's synopsis groups by, a column a lane.
        if (!identity)
        {
            buffer.Gather.Columns = columns;
            buffer.Gather.Permutation = permutation;
            buffer.Gather.Rows = rows;
            buffer.Gather.Packed = buffer.SortScratch.PackedBits > 0 ? buffer.SortScratch : null;
            if (buffer.Gather.Scratch.Length < columns.Count)
            {
                Array.Resize(ref buffer.Gather.Scratch, columns.Count);
            }

            buffer.Fan(Lanes).Run(buffer.Gather, columns.Count);
            buffer.Gather.Columns = null;
            buffer.Gather.Permutation = null;
            buffer.Gather.Packed = null;
        }

        foreach ((int[] codes, int _) in columns)
        {
            buffer.Codes.Add(codes);
        }

        return identity
            ? node
            : buffer.Columns.Gather(arena, node, permutation!, rows, buffer.Fan(Lanes), coded ? Keyed(buffer, arena, node, columns) : null);
    }

    /// <summary>
    /// The fields of the struct <paramref name="node"/> the order sorts by, each with its codes in the sorted order and the
    /// scratch of its place it was coded in, which holds its values by rank: what their gather takes their rows from.
    /// </summary>
    private SliceGather.Keyed[] Keyed(Buffer buffer, CanonicalArena arena, int node, List<(int[] Codes, int Distinct)> columns)
    {
        int fields = arena.GetNode(node).FieldCount;
        SliceGather.Keyed[] keyed = buffer.KeyedFields(fields);
        for (int k = 0; k < _order!.Count; k++)
        {
            // A field Coded coded in its place's scratch.
            int entry = _order[k];
            if (entry >= 0 && !SynopsisPath.Names(entry) && entry < fields)
            {
                keyed[entry] = new SliceGather.Keyed(columns[k].Codes, columns[k].Distinct, buffer.ColumnScratch[k]);
            }
        }

        return keyed;
    }

    /// <summary>
    /// Each entry of <paramref name="order"/>'s codes over the slice: the columns a lane each, every one laid in the arena
    /// first, the shapes and the paths after them on this thread.
    /// </summary>
    private List<(int[] Codes, int Distinct)> Coded(Buffer buffer, CanonicalArena arena, int node, int rows, int[] paths, IReadOnlyList<int> order)
    {
        (int[] Codes, int Distinct)[] coded = new (int[] Codes, int Distinct)[order.Count];
        int[] columns = new int[order.Count];
        if (buffer.ColumnScratch.Length < order.Count)
        {
            Array.Resize(ref buffer.ColumnScratch, order.Count);
        }
        int fields = 0;
        for (int k = 0; k < order.Count; k++)
        {
            int entry = order[k];
            columns[k] = entry >= 0 && !SynopsisPath.Names(entry) ? SliceSort.CanonicalOf(arena, node, entry) : -1;
            fields += columns[k] >= 0 ? 1 : 0;
        }

        if (fields > 0)
        {
            // A column's codes go to the buffer's array of its place, worked out in the scratch of its place: a slice
            // allocates nothing for them once the arrays have grown to its size.
            int[][] into = new int[order.Count][];
            for (int k = 0; k < order.Count; k++)
            {
                if (columns[k] >= 0)
                {
                    into[k] = buffer.CodeArray(k, rows);
                    buffer.ColumnScratch[k] ??= new SliceSortScratch();
                }
            }

            buffer.CodesWork.Arena = arena;
            buffer.CodesWork.Columns = columns;
            buffer.CodesWork.Rows = rows;
            buffer.CodesWork.Results = coded;
            buffer.CodesWork.Into = into;
            buffer.CodesWork.Scratch = buffer.ColumnScratch;
            buffer.Fan(Lanes).Run(buffer.CodesWork, order.Count);
            buffer.CodesWork.Arena = null;
            buffer.CodesWork.Results = null;
        }

        for (int k = 0; k < order.Count; k++)
        {
            if (columns[k] < 0)
            {
                int entry = order[k];
                coded[k] = SynopsisPath.Names(entry)
                    ? SliceSort.CodesOfColumn(arena, paths[SynopsisPath.Place(entry)], rows)
                    : SliceSort.CodesOf(arena, node, entry, rows, _shapes);
            }
        }

        return [.. coded];
    }

    /// <summary>The codes of a slice's columns, an item a column, laid in the arena before: an item reads it alone.</summary>
    private sealed class ColumnCodes : IFanWork
    {
        internal CanonicalArena? Arena;
        internal int[] Columns = [];
        internal int Rows;
        internal (int[] Codes, int Distinct)[]? Results;
        internal int[][] Into = [];
        internal SliceSortScratch[] Scratch = [];

        public void Run(WorkFan fan, int item)
        {
            if (Columns[item] >= 0)
            {
                int[] codes = Into[item];
                Results![item] = (codes, SliceSort.Codes(Arena!, Columns[item], Rows, codes, Scratch[item]));
            }
        }
    }

    /// <summary>
    /// Each column's codes in the rows' sorted order, an item a column: unpacked from the words the sort sorted, when the
    /// codes packed into them, and gathered through a scratch of its own otherwise.
    /// </summary>
    private sealed class CodesGather : IFanWork
    {
        internal List<(int[] Codes, int Distinct)>? Columns;
        internal int[]? Permutation;
        internal int Rows;
        internal int[][] Scratch = [];
        internal SliceSortScratch? Packed;

        public void Run(WorkFan fan, int item)
        {
            int[] codes = Columns![item].Codes;
            if (Packed is { } packed)
            {
                SliceSort.Unpack(packed, item, codes, Rows);
                return;
            }

            int[] permutation = Permutation!;
            int[] gathered = Scratch[item] is { } kept && kept.Length >= Rows ? kept : Scratch[item] = new int[Rows];
            for (int i = 0; i < Rows; i++)
            {
                gathered[i] = codes[permutation[i]];
            }

            gathered.AsSpan(0, Rows).CopyTo(codes);
        }
    }

    /// <summary>
    /// The rows of a slice the order is measured on, at most: on mixes of columns independent, correlated,
    /// nested and shaped, a sample of one row in 64 of a million gives the order every row gives, in 3 to
    /// 7 ms where every row takes 160 to 430 (`vortex-queries --micro runsorder`). Provisional until real
    /// mixes of logs confirm it.
    /// </summary>
    private const int OrderSampleRows = 16_384;

    /// <summary>
    /// The rows from which the first slice judges whether a column of the table's order repeats where the rows are sorted:
    /// eight blocks. A slice of a few rows, which only a small object's slices or a test's are, says too little of a
    /// column's repeats, where a slice of the design, a minute of three million rows, says it of a duration exactly.
    /// </summary>
    internal const int JudgedRows = 1 << 16;

    /// <summary>
    /// The column of each of the table's paths over the slice's rows <paramref name="node"/>, read in one pass
    /// of each variant column: every path before the order is settled, then those the order or the synopsis
    /// read, -1 for another.
    /// </summary>
    private int[] ReadPaths(Buffer buffer, int node)
    {
        CanonicalArena arena = buffer.Context.Canonical;
        IReadOnlyList<SynopsisPath> paths = _table.Paths;
        int[] columns = new int[paths.Count];
        columns.AsSpan().Fill(-1);
        if (paths.Count == 0)
        {
            return columns;
        }

        Dictionary<int, List<int>> byField = [];
        for (int p = 0; p < paths.Count; p++)
        {
            if (_wanted?[p] ?? true)
            {
                if (!byField.TryGetValue(paths[p].Field, out List<int>? places))
                {
                    byField[paths[p].Field] = places = [];
                }

                places.Add(p);
            }
        }

        CanonicalNode root = arena.GetNode(node);
        foreach ((int field, List<int> places) in byField)
        {
            (string, Types.Variants.VariantClass)[] keys = [.. places.Select(p => (paths[p].Key, paths[p].Class))];
            int[] built = Writing.VariantShredder.KeyColumns(arena, buffer.Context.Types, root.GetFieldIndex(field), keys);
            for (int i = 0; i < places.Count; i++)
            {
                columns[places[i]] = built[i];
            }
        }

        return columns;
    }

    /// <summary>
    /// The object's order and each entry's codes over this slice: the table's columns, the shapes that repeat
    /// and the paths whose values repeat among the rows that hold them, ordered greedily by the runs each adds
    /// (<see cref="SliceSort.Greedy"/>), measured on a sample.
    /// </summary>
    private (List<int> Order, List<(int[] Codes, int Distinct)> Columns) Place(CanonicalArena arena, int node, int rows, int[] paths)
    {
        // A column the object's rows repeat stays out of the order when a slice large enough to judge it does not repeat
        // its values twice on average: it forms no runs where the rows are sorted, whatever it does over the object, and
        // is a measure, as a duration is; a smaller slice leaves the object's count to decide.
        List<int> entries = [];
        List<(int[] Codes, int Distinct)> candidates = [];
        foreach (int entry in _table.Order)
        {
            (int[] Codes, int Distinct) codes = SliceSort.CodesOf(arena, node, entry, rows, _shapes);
            if (rows < JudgedRows || codes.Distinct * 2L <= rows)
            {
                entries.Add(entry);
                candidates.Add(codes);
            }
        }

        List<int> fields = [.. _shapes?.Keys ?? []];
        fields.Sort();
        foreach (int field in fields)
        {
            // Shapes that do not repeat twice on average are no synopsis column, as a column's values.
            (int[] Codes, int Distinct) shapes = SliceSort.CodesOf(arena, node, -field - 1, rows, _shapes);
            if (shapes.Distinct * 2L <= rows)
            {
                entries.Add(-field - 1);
                candidates.Add(shapes);
            }
        }

        for (int p = 0; p < paths.Length; p++)
        {
            // A path whose values repeat twice on average among the rows that hold it, as a column's must;
            // a null codes after every value, so the rows held are those coded below the distinct count. A
            // path left in the residual keys its value counts instead, since its runs could not be read.
            (int[] Codes, int Distinct) values = SliceSort.CodesOfColumn(arena, paths[p], rows);
            long held = 0;
            foreach (int code in values.Codes)
            {
                held += code < values.Distinct ? 1 : 0;
            }

            if (values.Distinct > 0 && values.Distinct * 2L <= held)
            {
                if (_table.Paths[p].Residual)
                {
                    _counted.Add(SynopsisPath.Entry(p));
                    continue;
                }

                entries.Add(SynopsisPath.Entry(p));
                candidates.Add(values);
            }
        }

        int[] greedy = SliceSort.Greedy(candidates, rows, Math.Max(1, rows / OrderSampleRows));
        List<int> order = [];
        List<(int[] Codes, int Distinct)> columns = [];
        foreach (int k in greedy)
        {
            order.Add(entries[k]);
            columns.Add(candidates[k]);
        }

        return (order, columns);
    }

    private static bool IsIdentity(ReadOnlySpan<int> permutation)
    {
        for (int i = 0; i < permutation.Length; i++)
        {
            if (permutation[i] != i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A slice's rows as they come and sort, and what its write reads beside them: its parts copied in, then each
    /// entry's codes and each path's column in the order the rows are sorted in.
    /// </summary>
    private sealed class Buffer : IDisposable
    {
        internal ScanContext Context { get; } = new ScanContext([]);

        /// <summary>What the slice's rows are copied into as they come, its columns laid out for all of them.</summary>
        internal SliceFill Fill { get; } = new SliceFill();

        internal List<int[]> Codes { get; } = [];

        /// <summary>The arrays the codes of each order place are kept in, from one slice of the buffer to the next.</summary>
        private readonly List<int[]> _codeArrays = [];

        /// <summary>The array of place <paramref name="place"/>'s codes, of at least <paramref name="rows"/> rows.</summary>
        internal int[] CodeArray(int place, int rows)
        {
            while (_codeArrays.Count <= place)
            {
                _codeArrays.Add([]);
            }

            if (_codeArrays[place].Length < rows)
            {
                _codeArrays[place] = new int[rows];
            }

            return _codeArrays[place];
        }

        internal List<int> PathColumns { get; } = [];

        /// <summary>The words the slice was sorted by, when its entries' codes packed into one.</summary>
        internal PackedKeys? Packed;

        // What the buffer's slice sorts in, its own since two slices may sort at once, kept from one of its slices to the
        // next: each order place's scratch, the permutation's, the codes' and their gather's work items.
        internal SliceSortScratch[] ColumnScratch = [];

        internal SliceSortScratch SortScratch { get; } = new SliceSortScratch();

        internal ColumnCodes CodesWork { get; } = new ColumnCodes();

        internal CodesGather Gather { get; } = new CodesGather();

        internal SliceGather Columns { get; } = new SliceGather();

        private SliceGather.Keyed[] _keyed = [];

        /// <summary>An array of at least <paramref name="fields"/> fields, none of them keyed yet, for the slice's gather.</summary>
        internal SliceGather.Keyed[] KeyedFields(int fields)
        {
            if (_keyed.Length < fields)
            {
                _keyed = new SliceGather.Keyed[fields];
            }

            Array.Clear(_keyed);
            return _keyed;
        }

        private WorkFan? _fan;

        /// <summary>The lanes of <paramref name="lanes"/> the buffer's columns are coded and gathered on, the sorting thread's included.</summary>
        internal WorkFan Fan(LaneBudget? lanes) => _fan ??= WorkFan.Rent(lanes?.Lanes ?? 1, lanes);

        /// <summary>Lets the slice's rows go, for the next slice's.</summary>
        internal void Clear()
        {
            Fill.Clear();
            Codes.Clear();
            PathColumns.Clear();
            Packed = null;
            Context.ResetBatch();
        }

        public void Dispose()
        {
            Context.Dispose();
            if (_fan is { } fan)
            {
                _fan = null;
                WorkFan.Return(fan);
            }
        }
    }
}

/// <summary>
/// A slice sorted, as its write reads it: its rows, each entry of the order's codes over them in the order they are
/// sorted in, values in their order and nulls last, and the column of each of the table's paths in the rows' arena,
/// those the order places and the numeric ones the synopsis sums, -1 for another, and, where the entries' codes pack
/// into a word, the words the rows were sorted by; valid while the slice is written.
/// </summary>
internal readonly record struct SortedSlice(RecordBatch Batch, IReadOnlyList<int[]> Codes, IReadOnlyList<int> PathColumns, PackedKeys? Packed = null);

/// <summary>
/// A sorted slice's codes packed a word a row, in the rows' sorted order, the order's first entry in the highest bits:
/// entry <c>k</c>'s code is the <c>Widths[k]</c> bits of <c>Words[row]</c> from bit <c>Shifts[k]</c>.
/// </summary>
internal sealed record PackedKeys(ulong[] Words, int[] Shifts, int[] Widths);
