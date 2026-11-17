using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Vorticity.Arrays;
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
/// </remarks>
internal sealed class SlicedMerge : IMergedRuns
{
    /// <summary>The runs a slice merged ahead keeps waiting at most before its merge waits for the taking flow.</summary>
    private const int MostWaiting = 16_384;

    private readonly int _count;
    private readonly Func<int, Action<KeyOrderedMerge.Window>?, KeyOrderedMerge> _merge;
    private readonly RunLedger _ledger;
    private readonly LaneBudget _lanes;
    private readonly CancellationTokenSource _stop;
    private readonly Ahead?[] _ahead;
    private readonly List<(KeyOrderedMerge Merge, long Mark)> _draining = [];
    private int _slice = -1;
    private int _started;
    private KeyOrderedMerge? _inline;
    private Ahead? _taking;
    private Run _run;
    private bool _disposed;

    /// <param name="count">The slices, two at least.</param>
    /// <param name="merge">The merge of a slice, with what it calls as it moves past a window, for a slice merged ahead.</param>
    /// <param name="ledger">The runs the job's writers keep in the inputs' batches.</param>
    /// <param name="lanes">The session's lanes the slices ahead merge on.</param>
    /// <param name="cancellationToken">Stops the slices ahead.</param>
    internal SlicedMerge(
        int count, Func<int, Action<KeyOrderedMerge.Window>?, KeyOrderedMerge> merge, RunLedger ledger, LaneBudget lanes, CancellationToken cancellationToken)
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

    /// <summary>The most slices merging at once, the one being taken and those ahead of it: 1 when none merged ahead.</summary>
    internal int PeakAtOnce { get; private set; } = 1;

    /// <summary>How many slices ahead of the one being taken may merge at once: as many as the room for their inputs holds.</summary>
    internal int MostAhead { get; init; } = int.MaxValue;

    /// <summary>Whether the job's memory admits one more slice merged ahead now; null for always. A slice not admitted merges in its turn.</summary>
    internal Func<bool>? Admits { get; init; }

    public int CurrentRows => _inline?.CurrentRows ?? _run.Rows;

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
            if (_inline is { } merge)
            {
                if (await merge.MoveNextAsync().ConfigureAwait(false))
                {
                    return true;
                }

                _inline = null;
                _draining.Add((merge, _ledger.Noted));
            }
            else if (_taking is { } ahead)
            {
                if (await TakeAsync(ahead).ConfigureAwait(false))
                {
                    return true;
                }

                _taking = null;
                _draining.Add((ahead.Merge!, _ledger.Noted));
            }

            await DrainAsync().ConfigureAwait(false);
            if (++_slice >= _count)
            {
                return false;
            }

            StartAhead();
            if (_ahead[_slice] is { } merged)
            {
                _taking = merged;
            }
            else
            {
                _inline = _merge(_slice, null);
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
                if (run.Retired)
                {
                    // Every run of the window is taken, and noted by the writers that keep it: it is cited up to here.
                    Volatile.Write(ref run.Window.CitedUpTo, _ledger.Noted);
                    continue;
                }

                if (Interlocked.Decrement(ref ahead.Waiting) == MostWaiting / 2)
                {
                    ahead.Room();
                }

                _run = run;
                return true;
            }

            if (!await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // Its merge ended: what failed it fails the merge here.
#pragma warning disable VSTHRD003 // The slice's merge, started by this merge on a lane of its own.
                await ahead.Merged.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                return false;
            }
        }
    }

    /// <summary>Starts the slices past the one being taken on lanes free now, as many as the room admits.</summary>
    private void StartAhead()
    {
        _started = Math.Max(_started, _slice + 1);
        while (_started < _count && _started - _slice <= MostAhead && Execution.LaneWorkers.TryTake(_lanes, out long entered))
        {
            // A lane free, but not the room: the slice merges in its turn, and the lane goes back.
            if (Admits?.Invoke() == false)
            {
                _lanes.Exit(entered);
                return;
            }

            int slice = _started++;
            Ahead ahead = new Ahead();
            _ahead[slice] = ahead;
            SlicesAhead++;
            PeakAtOnce = Math.Max(PeakAtOnce, _started - _slice);
            ahead.Merged = Execution.LaneWorkers.RunOnAsync(_lanes, entered, () => MergeAheadAsync(slice, ahead));
        }
    }

    /// <summary>Merges slice <paramref name="slice"/> ahead, on the lane taken for it, its runs into <paramref name="ahead"/>.</summary>
    private async Task MergeAheadAsync(int slice, Ahead ahead)
    {
        ChannelWriter<Run> writer = ahead.Runs.Writer;
        try
        {
            KeyOrderedMerge merge = ahead.Merge = _merge(slice, window => writer.TryWrite(new Run(window, 0, 0, Retired: true)));
            while (await merge.MoveNextAsync().ConfigureAwait(false))
            {
                _stop.Token.ThrowIfCancellationRequested();
                (KeyOrderedMerge.Window window, int row) = merge.CurrentWindow;
                writer.TryWrite(new Run(window, row, merge.CurrentRows, Retired: false));
                if (Interlocked.Increment(ref ahead.Waiting) >= MostWaiting)
                {
                    await ahead.WaitForRoomAsync().ConfigureAwait(false);
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

            ahead.Room();
            try
            {
#pragma warning disable VSTHRD003 // The slice's merge, started by this merge on a lane of its own.
                await ahead.Merged.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (Exception)
            {
                // Stopped, or failed: a failure that mattered failed the merge as its runs were taken.
            }

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

    /// <summary>A run of a slice merged ahead, or the mark that its merge moved past a window.</summary>
    private readonly record struct Run(KeyOrderedMerge.Window Window, int Row, int Rows, bool Retired);

    /// <summary>A slice merged ahead: its merge, its runs waiting, and the merge's own task.</summary>
    private sealed class Ahead
    {
        private TaskCompletionSource? _room;

        internal Channel<Run> Runs { get; } = Channel.CreateUnbounded<Run>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        internal KeyOrderedMerge? Merge;

        internal Task Merged = Task.CompletedTask;

        /// <summary>The runs waiting, past which the merge waits for room.</summary>
        internal int Waiting;

        /// <summary>Waits until the taking flow made room, its runs halved.</summary>
        internal Task WaitForRoomAsync()
        {
            TaskCompletionSource room = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _room, room);

            // Room made since the count was read: no wait.
            if (Volatile.Read(ref Waiting) < MostWaiting)
            {
                room.TrySetResult();
            }

            return room.Task;
        }

        /// <summary>Lets the merge go on.</summary>
        internal void Room() => Interlocked.Exchange(ref _room, null)?.TrySetResult();
    }
}
