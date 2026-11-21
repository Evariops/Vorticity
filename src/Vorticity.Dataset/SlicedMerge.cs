using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Execution;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// A merge cut in slices of the key, merged side by side and taken in the key's order: the slices after the one being taken
/// are merged ahead on lanes of the session's free now, each by a merge of its own bounded by its two cuts, and their runs
/// wait in order for the flow that takes them; a slice no lane merged ahead is merged by that flow itself as it comes to
/// it. The runs come in the order one merge gives them, cut again at each slice's bounds.
/// </summary>
/// <remarks>
/// <para>
/// A slice's merge keeps the windows of its inputs' batches as any merge under a ledger does, until the writers copied the
/// runs they keep of them; a window its merge moved past while its runs still wait here is dated by the flow that takes
/// them, once it took the last (<see cref="KeyOrderedMerge.Retiring"/>). A slice's merge ahead runs on its own lane and
/// flow while it merges, and is the taking flow's once it ended: the flow disposes it once every run it gave is copied.
/// </para>
/// <para>
/// The cuts are the job's, never the degree's: a merge of the same inputs gives the same runs at any degree, its slices
/// merged ahead or not, and its objects are the same bytes.
/// </para>
/// <para>
/// Each slice counts what its blocks hold apart (<see cref="CountedAccount"/>). The slices ahead start once the slice taken
/// gave its first run, its inputs open: what it holds then prices them, and a slice ahead keeps runs waiting while it holds
/// no more than it was admitted at, then merges a run as the taking flow takes one.
/// </para>
/// </remarks>
internal sealed class SlicedMerge : IMergedRuns
{
    /// <summary>The runs a slice merged ahead keeps waiting at most before its merge waits for the taking flow.</summary>
    private const int MostWaiting = 16_384;

    private readonly int _count;
    private readonly Func<int, Action<KeyOrderedMerge.Window>?, MemoryAccount?, KeyOrderedMerge> _merge;
    private readonly RunLedger _ledger;
    private readonly LaneBudget _lanes;
    private readonly CancellationTokenSource _stop;
    private readonly Ahead?[] _ahead;
    private readonly List<(KeyOrderedMerge Merge, long Mark)> _draining = [];
    private int _slice = -1;
    private int _started;
    private KeyOrderedMerge? _inline;
    private CountedAccount? _inlineAccount;
    private Ahead? _taking;
    private bool _aheadStarted;
    private long _measured;
    private Run _run;
    private bool _disposed;

    /// <param name="count">The slices, two at least.</param>
    /// <param name="merge">
    /// The merge of a slice, with what it calls as it moves past a window, for a slice merged ahead, and the account its
    /// inputs rent under, null for the job's inputs' own.
    /// </param>
    /// <param name="ledger">The runs the job's writers keep in the inputs' batches.</param>
    /// <param name="lanes">The session's lanes the slices ahead merge on.</param>
    /// <param name="cancellationToken">Stops the slices ahead.</param>
    internal SlicedMerge(
        int count, Func<int, Action<KeyOrderedMerge.Window>?, MemoryAccount?, KeyOrderedMerge> merge, RunLedger ledger, LaneBudget lanes, CancellationToken cancellationToken)
    {
        _count = count;
        _merge = merge;
        _ledger = ledger;
        _lanes = lanes;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ahead = new Ahead?[count];
    }

    /// <summary>The slices merged ahead on lanes of their own, rather than by the taking flow.</summary>
    internal int SlicesAhead { get; private set; }

    /// <summary>
    /// What the slices past the one being taken hold of the job's inputs' blocks now, each as its account counts it: what
    /// the job's memory tells from what the slice taken must hold (<see cref="JobMemory.AheadHeld"/>). Read from any
    /// flow, as an admission reads it: a slice that starts or ends meanwhile is counted or not.
    /// </summary>
    internal long AheadHeld
    {
        get
        {
            long held = 0;
            for (int slice = Math.Max(Volatile.Read(ref _slice), -1) + 1; slice < _ahead.Length; slice++)
            {
                held += Volatile.Read(ref _ahead[slice])?.Account?.Held ?? 0;
            }

            return held;
        }
    }

    /// <summary>The most slices merging at once, the one being taken and those ahead of it: 1 when none merged ahead.</summary>
    internal int PeakAtOnce { get; private set; } = 1;

    /// <summary>How many slices ahead of the one being taken may merge at once: as many as the room for their inputs holds.</summary>
    internal int MostAhead { get; init; } = int.MaxValue;

    /// <summary>
    /// Whether the job's memory admits one more slice merged ahead now, given what it and the slices ahead merging may still
    /// take past what they hold, each up to what it was admitted at; null for always. A slice not admitted merges in its turn.
    /// </summary>
    /// <remarks>
    /// Admitted one after another as a slice starts, before any opened its inputs, slices took room the first had not filled
    /// yet, and their inputs together passed the budget: what a slice ahead may still take counts until it is taken.
    /// </remarks>
    internal Func<long, bool>? Admits { get; init; }

    /// <summary>
    /// What a slice is priced at before one was measured: its inputs open at their batches. A slice ahead is admitted at
    /// this or at what a slice held at its first run, an input open times <see cref="SliceInputs"/>, whichever is more, and
    /// keeps runs waiting up to it.
    /// </summary>
    internal long Price { get; init; }

    /// <summary>The most inputs a slice reads, which what an input open held at a slice's first run is counted by.</summary>
    internal int SliceInputs { get; init; } = 1;

    /// <summary>The account of the job's inputs, in which each slice's own counts (<see cref="CountedAccount"/>); null for none.</summary>
    internal MemoryAccount? Inputs { get; init; }

    /// <summary>
    /// Has the writers copy the runs they keep of the inputs and have not handed through yet, the rows of the block they
    /// stage: what a slice's end asks, so that its merge, every input of it open, goes before the next slice opens its own,
    /// rather than once the next slice's first block is full; null for none.
    /// </summary>
    internal Func<ValueTask>? OwnRuns { get; init; }

    /// <summary>
    /// Has the job give back, on the flow that takes the runs, what it started ahead at the level asked
    /// (<see cref="JobMemory.MakeRoomAsync"/>); false once no such level is left. What a slice merged ahead asks through
    /// its runs once it is the one taken (<see cref="AskRoomAsync"/>): its own flow drives neither the writers nor the
    /// slices beside it. Null for a job that reserves nothing.
    /// </summary>
    internal Func<int, ValueTask<bool>>? MakeRoom { get; init; }

    /// <summary>The slices merged ahead that were given up, short of room, and merged again in their turn.</summary>
    internal int SlicesGivenUp { get; private set; }

    public int CurrentRows => _inline?.CurrentRows ?? _run.Rows;

    /// <inheritdoc/>
    public long CurrentInput => _inline?.CurrentInput ?? _run.Window.Input!.Rank;

    /// <inheritdoc/>
    public bool StoppedPast(ReserveExhaustedException stop, KeyTest below) =>
        (_inline ?? _taking?.Merge)?.StoppedPast(stop, below) ?? false;

    /// <inheritdoc/>
    public async ValueTask GiveBackAsync(int level)
    {
        if (_disposed)
        {
            return;
        }

        if (level == 0)
        {
            // The slices taken whose runs the writers copied since go, their inputs with them.
            await DrainAsync().ConfigureAwait(false);
        }
        else if (level == 2)
        {
            await GiveUpAheadAsync().ConfigureAwait(false);
        }

        // The slice taken, when this flow merges it: one merged ahead gives back on its own flow (AskRoomAsync).
        if (_inline is { } inline)
        {
            await inline.GiveBackAsync(level).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What an input of slice <paramref name="slice"/>, merged ahead, asks when its reserve does not hold what it cannot go
    /// on without: the flow that takes the runs has the job give back what it started ahead at <paramref name="level"/>,
    /// once it takes this slice and comes to the runs it gave before it asked, and the slice's merge then gives back its
    /// own (<see cref="KeyOrderedMerge.GiveBackAsync(int)"/>). A slice not taken yet waits there, holding what it holds: it is
    /// work ahead, which the slice taken has the job give up when it is itself short of room. On the slice's own flow.
    /// </summary>
    /// <returns>False once the job has nothing left to give back, or the slice was given up.</returns>
    internal async ValueTask<bool> AskRoomAsync(int slice, int level)
    {
        if (Volatile.Read(ref _ahead[slice]) is not { } ahead)
        {
            return false;
        }

        Task<bool> answer = ahead.AskAsync();
        ahead.Runs.Writer.TryWrite(Run.Asking(level));
        if (!await answer.ConfigureAwait(false) || ahead.GivenUp)
        {
            return false;
        }

        if (ahead.Merge is { } merge)
        {
            await merge.GiveBackAsync(level).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Gives up the slices merged ahead of the one taken: each stops, its merge goes with its inputs and the runs it kept
    /// waiting, none of which was taken, and it is merged again by the taking flow in its turn. What a job short of room
    /// gives back last of what it started ahead. On the taking flow.
    /// </summary>
    private async ValueTask GiveUpAheadAsync()
    {
        for (int slice = Math.Max(_slice, -1) + 1; slice < _started; slice++)
        {
            if (_ahead[slice] is not { } ahead || ReferenceEquals(ahead, _taking))
            {
                continue;
            }

            ahead.GiveUp();
            await ahead.Done.WaitAsync().ConfigureAwait(false);
            if (ahead.Merge is { } merge)
            {
                await merge.DisposeAsync().ConfigureAwait(false);
            }

            Volatile.Write(ref _ahead[slice], null);
            SlicesGivenUp++;
        }
    }

    public (CanonicalArena Arena, int Root, int Row, double RowBytes) CurrentRun
    {
        get
        {
            if (_inline is { } merge)
            {
                return merge.CurrentRun;
            }

            RecordBatch batch = _run.Window.Batch!;
            return (batch.Arena, batch.RootIndex, _run.Row, _run.Window.RowBytes());
        }
    }

    public ReadOnlySpan<byte> KeyAt(int row) => _inline is { } merge ? merge.KeyAt(row) : _run.Window.Keys!.Row(_run.Row + row);

    public async ValueTask<bool> MoveNextAsync()
    {
        while (true)
        {
            // A slice taken whose runs the writers copied since goes now, its inputs with it, rather than at a slice's end.
            if (_draining.Count > 0 && _draining[0].Mark <= _ledger.Copied)
            {
                await DrainAsync().ConfigureAwait(false);
            }

            if (_inline is { } merge)
            {
                if (await merge.MoveNextAsync().ConfigureAwait(false))
                {
                    // Its first run: what it holds by input open prices the slices ahead, which start now.
                    if (!_aheadStarted)
                    {
                        Measured(_inlineAccount?.Held ?? 0, merge.MostOpen);
                        StartAhead();
                    }

                    return true;
                }

                _inline = null;
                _draining.Add((merge, _ledger.Noted));
            }
            else if (_taking is { } ahead)
            {
                if (await TakeAsync(ahead).ConfigureAwait(false))
                {
                    if (!_aheadStarted)
                    {
                        StartAhead();
                    }

                    return true;
                }

                _taking = null;
                _draining.Add((ahead.Merge!, _ledger.Noted));
            }

            // The slice ended: the writers copy the runs of it they still stage, and its merge can go before the next one
            // opens its inputs; those in blocks in flight are copied as the blocks are assembled.
            if (OwnRuns is { } own && _ledger.Copied < _ledger.Noted)
            {
                await own().ConfigureAwait(false);
            }

            await DrainAsync().ConfigureAwait(false);
            if (++_slice >= _count)
            {
                return false;
            }

            _aheadStarted = false;
            if (_ahead[_slice] is { } merged)
            {
                _taking = merged;
            }
            else
            {
                _inlineAccount = Inputs is null ? null : new CountedAccount(Inputs);
                _inline = _merge(_slice, null, _inlineAccount);
            }
        }
    }

    /// <summary>The next run of a slice merged ahead, its windows dated as their last run is taken; false once it ended.</summary>
    private async ValueTask<bool> TakeAsync(Ahead ahead)
    {
        ChannelReader<Run> reader = ahead.Runs.Reader;
        while (true)
        {
            while (reader.TryRead(out Run run))
            {
                if (run.Asks > 0)
                {
                    // The slice's merge is short of room it cannot go on without, every run it gave before taken: the job
                    // gives back what it started ahead, on this flow, and the slice goes on or ends there.
                    ahead.Answer(MakeRoom is { } makeRoom && await makeRoom(run.Asks - 1).ConfigureAwait(false));
                    continue;
                }

                if (run.Retired)
                {
                    // Every run of the window is taken, and noted by the writers that keep it: it is cited up to here.
                    Volatile.Write(ref run.Window.CitedUpTo, _ledger.Noted);
                    continue;
                }

                if (Interlocked.Decrement(ref ahead.Waiting) <= Volatile.Read(ref ahead.ResumeAt))
                {
                    ahead.Room();
                }

                _run = run;
                return true;
            }

            // Its merge ended once its runs are read: what failed it, which completed its runs, is thrown here.
            if (!await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Starts the slices past the one being taken on lanes free now, as many as the room admits at the price of a slice:
    /// once the slice taken gave its first run.
    /// </summary>
    private void StartAhead()
    {
        _aheadStarted = true;
        _started = Math.Max(_started, _slice + 1);
        while (_started < _count && _started - _slice <= MostAhead && Execution.LaneWorkers.TryTake(_lanes, out long entered))
        {
            // A lane free, but not the room: the slice merges in its turn, and the lane goes back.
            long price = Math.Max(Price, Volatile.Read(ref _measured));
            if (Admits?.Invoke(price + StillToTake(price)) == false)
            {
                _lanes.Exit(entered);
                return;
            }

            int slice = _started++;
            Ahead ahead = new Ahead(Inputs is null ? null : new CountedAccount(Inputs), price);
            _ahead[slice] = ahead;
            SlicesAhead++;
            PeakAtOnce = Math.Max(PeakAtOnce, _started - _slice);
            _ = MergedAheadAsync(slice, ahead, entered);
        }
    }

    /// <summary>
    /// Runs the merge of slice <paramref name="slice"/> ahead on the lane <paramref name="entered"/>, which it gives back
    /// at its end, then tells <paramref name="ahead"/> it ended: its failure went through its runs.
    /// </summary>
    private async Task MergedAheadAsync(int slice, Ahead ahead, long entered)
    {
        try
        {
            await Execution.LaneWorkers.RunOnAsync(_lanes, entered, () => MergeAheadAsync(slice, ahead)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Its runs completed with it, and the flow that takes them throws it.
        }
        finally
        {
            ahead.Done.Release();
        }
    }

    /// <summary>
    /// Merges slice <paramref name="slice"/> ahead, on the lane taken for it, its runs into <paramref name="ahead"/>: while it
    /// holds no more than it was admitted at, up to <see cref="MostWaiting"/> of them; past it, a run as the flow takes one.
    /// </summary>
    private async Task MergeAheadAsync(int slice, Ahead ahead)
    {
        ChannelWriter<Run> writer = ahead.Runs.Writer;
        try
        {
            KeyOrderedMerge merge = ahead.Merge = _merge(slice, window => writer.TryWrite(new Run(window, 0, 0, Retired: true)), ahead.Account);
            bool first = true;
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                _stop.Token.ThrowIfCancellationRequested();
                if (ahead.GivenUp)
                {
                    throw new OperationCanceledException("The slice merged ahead was given up, the job short of room.");
                }

                (KeyOrderedMerge.Window window, int row) = merge.CurrentWindow;
                writer.TryWrite(new Run(window, row, merge.CurrentRows, Retired: false));
                long held = ahead.Account?.Held ?? 0;
                if (first)
                {
                    first = false;
                    Measured(held, merge.MostOpen);
                }

                int waiting = Interlocked.Increment(ref ahead.Waiting);
                if (waiting >= MostWaiting || held > ahead.Admitted)
                {
                    await ahead.WaitForRoomAsync(waiting / 2, _stop.Token).ConfigureAwait(false);
                }
            }

            writer.TryComplete();
        }
        catch (Exception failure)
        {
            writer.TryComplete(failure);
            throw;
        }
    }

    /// <summary>How far the merge came, the most slices merging at once, and what a slice was priced and measured at: for a job's activity.</summary>
    internal string Report() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"slice {_slice + 1} of {_count}, {PeakAtOnce} at once at most, a slice priced {Price >> 20} MiB and measured {Volatile.Read(ref _measured) >> 20}");

    /// <summary>
    /// What the slices merging may still take: the one taken, merged by the flow, up to <paramref name="price"/>, and each
    /// merged ahead, the one taken among them, up to what it was admitted at.
    /// </summary>
    /// <remarks>
    /// Its inputs open as its keys reach them: the slice taken held 16 MiB at its first run, its 23 inputs not all open, two
    /// slices ahead were admitted at 152 MiB each, and the job's inputs reached 663 MiB, which gave it up (2026-11-18).
    /// </remarks>
    private long StillToTake(long price)
    {
        long still = _inline is not null ? Math.Max(0, price - (_inlineAccount?.Held ?? 0)) : 0;
        for (int slice = Math.Max(_slice, 0); slice < _started; slice++)
        {
            if (_ahead[slice] is { } ahead)
            {
                still += Math.Max(0, ahead.Admitted - (ahead.Account?.Held ?? 0));
            }
        }

        return still;
    }

    /// <summary>
    /// Keeps what a slice held at its first run, <paramref name="held"/> over <paramref name="open"/> inputs open, as a
    /// slice's price, its <see cref="SliceInputs"/> inputs counted at that, when it is the most one measured.
    /// </summary>
    private void Measured(long held, int open)
    {
        held = held / Math.Max(1, open) * SliceInputs;
        long seen = Volatile.Read(ref _measured);
        while (held > seen)
        {
            long was = Interlocked.CompareExchange(ref _measured, held, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    /// <summary>Disposes the merges of the slices taken whose runs the writers have all copied, giving their windows back.</summary>
    private async ValueTask DrainAsync()
    {
        long copied = _ledger.Copied;
        for (int i = _draining.Count - 1; i >= 0; i--)
        {
            if (_draining[i].Mark <= copied)
            {
                KeyOrderedMerge merge = _draining[i].Merge;
                _draining.RemoveAt(i);
                await merge.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The slices ahead stop, and every merge goes, its windows with it: the caller has its writers let go of the inputs
        // before it ends the merge, or has given them up.
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (Ahead? ahead in _ahead)
        {
            if (ahead is null)
            {
                continue;
            }

            // Stopped, or failed: a failure that mattered failed the merge as its runs were taken. One waiting for room, or
            // for an answer to what it asked, goes on to its end.
            ahead.GiveUp();
            await ahead.Done.WaitAsync().ConfigureAwait(false);
            if (ahead.Merge is { } merge && ahead != _taking)
            {
                bool drained = _draining.Exists(entry => ReferenceEquals(entry.Merge, merge));
                if (!drained)
                {
                    await merge.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        if (_taking?.Merge is { } taking)
        {
            await taking.DisposeAsync().ConfigureAwait(false);
        }

        if (_inline is { } inline)
        {
            await inline.DisposeAsync().ConfigureAwait(false);
        }

        foreach ((KeyOrderedMerge merge, _) in _draining)
        {
            await merge.DisposeAsync().ConfigureAwait(false);
        }

        _draining.Clear();
        _stop.Dispose();
    }

    /// <summary>
    /// A run of a slice merged ahead, the mark that its merge moved past a window, or, with <paramref name="Asks"/>, the
    /// mark that it asks the taking flow for room at the level that is one less (<see cref="AskRoomAsync"/>).
    /// </summary>
    private readonly record struct Run(KeyOrderedMerge.Window Window, int Row, int Rows, bool Retired, int Asks = 0)
    {
        /// <summary>The mark that the slice's merge asks for room at <paramref name="level"/>.</summary>
        internal static Run Asking(int level) => new Run(null!, 0, 0, Retired: false, Asks: level + 1);
    }

    /// <summary>
    /// A slice merged ahead: the account its blocks count in, <paramref name="account"/>, what it was admitted at,
    /// <paramref name="admitted"/>, its merge, its runs waiting, and its end.
    /// </summary>
    private sealed class Ahead(CountedAccount? account, long admitted)
    {
        private TaskCompletionSource? _room;

        internal Channel<Run> Runs { get; } = Channel.CreateUnbounded<Run>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        internal KeyOrderedMerge? Merge;

        /// <summary>What its blocks hold, counted apart; null when the merge counts none.</summary>
        internal CountedAccount? Account => account;

        /// <summary>The bytes it was admitted at, up to which it keeps runs waiting.</summary>
        internal long Admitted => admitted;

        /// <summary>Released once its merge ended and its lane went back.</summary>
        internal SemaphoreSlim Done { get; } = new SemaphoreSlim(0, 1);

        /// <summary>The runs waiting, past which the merge waits for room.</summary>
        internal int Waiting;

        /// <summary>The runs waiting at or below which the merge waiting for room goes on; -1 before it waited.</summary>
        internal int ResumeAt = -1;

        /// <summary>Waits until the taking flow took its runs down to <paramref name="resumeAt"/> waiting, or the merge stops.</summary>
        internal Task WaitForRoomAsync(int resumeAt, CancellationToken stop)
        {
            TaskCompletionSource room = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _room, room);
            Interlocked.Exchange(ref ResumeAt, resumeAt);

            // Room made since the count was read, or the merge stopped or given up before that could let it go: no wait.
            if (Volatile.Read(ref Waiting) <= resumeAt || stop.IsCancellationRequested || GivenUp)
            {
                room.TrySetResult();
            }

            return room.Task;
        }

        /// <summary>Lets the merge go on.</summary>
        internal void Room() => Interlocked.Exchange(ref _room, null)?.TrySetResult();

        private TaskCompletionSource<bool>? _asked;
        private int _givenUp;

        /// <summary>Whether the slice was given up, the job short of room, or its merge ended: it merges no more.</summary>
        internal bool GivenUp => Volatile.Read(ref _givenUp) != 0;

        /// <summary>What ends with the taking flow's answer to the room the slice's merge is about to ask for through its runs.</summary>
        internal Task<bool> AskAsync()
        {
            TaskCompletionSource<bool> asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _asked, asked);

            // Given up before it asked, or as it did: told no at once.
            if (GivenUp)
            {
                asked.TrySetResult(false);
            }

            return asked.Task;
        }

        /// <summary>Tells the slice's merge whether the job made room.</summary>
        internal void Answer(bool made) => Interlocked.Exchange(ref _asked, null)?.TrySetResult(made);

        /// <summary>Has the slice's merge stop where it waits: for room among its runs, or for an answer.</summary>
        internal void GiveUp()
        {
            Volatile.Write(ref _givenUp, 1);
            Room();
            Answer(false);
        }
    }
}
