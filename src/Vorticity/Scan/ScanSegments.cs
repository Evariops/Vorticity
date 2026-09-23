using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Scanning;

/// <summary>
/// The segments one scan has read, held for the batches that ask for them again, and shared by
/// every lane of the scan.
/// </summary>
/// <remarks>
/// <para>
/// A segment spans every block of its chunk and a batch is a block or less, so consecutive batches
/// ask for the same segments. Over a memory mapping the repetition is free, since a segment is a view;
/// over anything that copies -- a positional read, an object store -- it is the whole cost of the
/// scan. Lanes take consecutive batches in turn, so a set per lane would read a segment once per
/// lane that meets it, and a scan of four lanes would reread every segment on every batch.
/// </para>
/// <para>
/// Each segment is read by exactly one batch, the first to claim it. A batch that needs a segment
/// another batch has claimed and not yet read waits for that read instead of issuing its own: a
/// batch claims all it lacks in one step under the lock, so it can only wait on a batch that claimed
/// before it, and the waits cannot form a cycle. Once a read fails the scan is failing, and the
/// batches still running read what they lack themselves.
/// </para>
/// <para>
/// A segment is needed by a contiguous run of the scan's batches, because it covers a contiguous
/// run of rows and the batches are numbered in the order the scan walks its rows, forward or back.
/// So once batch <c>n</c> is delivered, every batch before it has taken its own reference, and a
/// segment last asked for by a batch before <c>n</c> is needed by no batch from <c>n</c> on:
/// <see cref="Release"/> drops it. What is held is bounded by the batches in flight, whose
/// segments were live anyway, since every batch holds its own reference until it is released.
/// </para>
/// <para>
/// The set is its own lock: a scan of one lane never contends for it, and an uncontended monitor
/// allocates nothing.
/// </para>
/// </remarks>
internal sealed class ScanSegments : IDisposable
{
    private readonly SegmentWaiter?[] _parked;
    private Entry[] _entries = [];
    private int _count;
    private long _tickets;
    private bool _failed;

    /// <param name="lanes">How many batches may be built at once; above one, a batch may wait for another's read.</param>
    internal ScanSegments(int lanes) => _parked = lanes > 1 ? new SegmentWaiter?[lanes] : [];

    /// <summary>
    /// The next batch number, for a scan whose batches are not numbered by a split plan: a
    /// key-ordered walk numbers the zones its key source decodes and the groups of each window in
    /// the order they happen, which is the order their rows are walked in.
    /// </summary>
    internal long NextTicket() => Interlocked.Increment(ref _tickets);

    /// <summary>
    /// Fills the unread slots of a batch's registered set from what the scan holds, and claims for
    /// the batch the segments nobody holds or is reading, which the batch then reads itself.
    /// </summary>
    /// <param name="requests">The batch's set, registered and not yet read.</param>
    /// <param name="batch">The batch's number in the scan, in the order the scan walks its rows.</param>
    /// <param name="waiter">The batch's waiter, or null when no other batch runs beside it.</param>
    /// <returns>
    /// True when another batch is reading a segment this one needs: the waiter is then parked, and
    /// the caller awaits it and claims again.
    /// </returns>
    internal bool Claim(SegmentRequestSet requests, long batch, SegmentWaiter? waiter)
    {
        lock (this)
        {
            bool blocked = false;
            for (int i = 0; i < _count; i++)
            {
                ref Entry entry = ref _entries[i];
                int slot = requests.IndexOf(entry.Offset, entry.Length);
                if (slot < 0 || requests.IsFilled(slot))
                {
                    continue;
                }

                entry.LastUse = Math.Max(entry.LastUse, batch);
                if (entry.Owner is { } owner)
                {
                    requests.SetSharedResult(slot, owner, entry.View);
                }
                else if (entry.Claimant != batch)
                {
                    blocked = true;
                }
            }

            if (!_failed)
            {
                for (int slot = 0; slot < requests.Count; slot++)
                {
                    if (requests.IsFilled(slot))
                    {
                        continue;
                    }

                    SegmentSpec spec = requests.GetSpec(slot);
                    if (Find(spec.Offset, spec.Length) < 0)
                    {
                        Add(new Entry { Offset = spec.Offset, Length = spec.Length, Claimant = batch, LastUse = batch });
                    }
                }
            }

            if (!blocked || waiter is null || _failed)
            {
                return false;
            }

            waiter.Park(requests, batch);
            _parked[Array.IndexOf(_parked, null)] = waiter;
            return true;
        }
    }

    /// <summary>Holds what a batch read for the segments it claimed, and wakes the batches waiting for them.</summary>
    /// <param name="requests">The batch's set, read.</param>
    /// <param name="batch">The batch's number.</param>
    internal void Publish(SegmentRequestSet requests, long batch)
    {
        lock (this)
        {
            int kept = 0;
            for (int i = 0; i < _count; i++)
            {
                Entry entry = _entries[i];
                if (entry.Owner is null && entry.Claimant == batch)
                {
                    int slot = requests.IndexOf(entry.Offset, entry.Length);
                    if (slot < 0)
                    {
                        continue;
                    }

                    entry.Owner = requests.GetOwner(slot).Retain();
                    entry.View = requests.GetBuffer(slot);
                }

                _entries[kept++] = entry;
            }

            Truncate(kept);
            WakeUnblocked();
        }
    }

    /// <summary>Gives up the claims of a batch whose read failed, and lets every waiting batch read for itself.</summary>
    /// <param name="batch">The batch's number.</param>
    internal void Abandon(long batch)
    {
        lock (this)
        {
            _failed = true;
            int kept = 0;
            for (int i = 0; i < _count; i++)
            {
                Entry entry = _entries[i];
                if (entry.Owner is null && entry.Claimant == batch)
                {
                    continue;
                }

                _entries[kept++] = entry;
            }

            Truncate(kept);
            WakeUnblocked();
        }
    }

    /// <summary>Drops what no batch from <paramref name="batch"/> on can ask for, once <paramref name="batch"/> is delivered.</summary>
    /// <param name="batch">The batch just delivered.</param>
    internal void Release(long batch)
    {
        lock (this)
        {
            int kept = 0;
            for (int i = 0; i < _count; i++)
            {
                Entry entry = _entries[i];
                if (entry.Owner is { } owner && entry.LastUse < batch)
                {
                    owner.Release();
                    continue;
                }

                _entries[kept++] = entry;
            }

            Truncate(kept);
        }
    }

    /// <summary>Releases everything held and gives the array back, once no batch runs.</summary>
    public void Dispose()
    {
        lock (this)
        {
            for (int i = 0; i < _count; i++)
            {
                _entries[i].Owner?.Release();
            }

            _count = 0;
            if (_entries.Length > 0)
            {
                ArrayPool<Entry>.Shared.Return(_entries, clearArray: true);
                _entries = [];
            }
        }
    }

    private int Find(ulong offset, uint length)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_entries[i].Offset == offset && _entries[i].Length == length)
            {
                return i;
            }
        }

        return -1;
    }

    private void Add(Entry entry)
    {
        if (_count == _entries.Length)
        {
            // Rented rather than allocated: the array is the same size on every batch of the scan,
            // and a scan's allocation is held to a ceiling counted in bytes.
            Entry[] bigger = ArrayPool<Entry>.Shared.Rent(Math.Max(16, _entries.Length * 2));
            if (_entries.Length > 0)
            {
                Array.Copy(_entries, bigger, _count);
                ArrayPool<Entry>.Shared.Return(_entries, clearArray: true);
            }

            _entries = bigger;
        }

        _entries[_count++] = entry;
    }

    private void Truncate(int kept)
    {
        Array.Clear(_entries, kept, _count - kept);
        _count = kept;
    }

    /// <summary>Wakes each parked batch no unread claim of another batch holds back any more; every one, once the scan has failed.</summary>
    private void WakeUnblocked()
    {
        for (int w = 0; w < _parked.Length; w++)
        {
            SegmentWaiter? waiter = _parked[w];
            if (waiter is null || (!_failed && Blocked(waiter)))
            {
                continue;
            }

            _parked[w] = null;

            // The continuation runs on the thread pool, so waking under the lock runs nothing under it.
            waiter.Wake();
        }
    }

    private bool Blocked(SegmentWaiter waiter)
    {
        SegmentRequestSet requests = waiter.Requests!;
        for (int i = 0; i < _count; i++)
        {
            ref Entry entry = ref _entries[i];
            if (entry.Owner is not null || entry.Claimant == waiter.Batch)
            {
                continue;
            }

            int slot = requests.IndexOf(entry.Offset, entry.Length);
            if (slot >= 0 && !requests.IsFilled(slot))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A segment the scan holds, or one a batch has claimed and is reading when it has no owner yet.</summary>
    private struct Entry
    {
        internal ulong Offset;
        internal uint Length;

        /// <summary>The batch that reads the segment.</summary>
        internal long Claimant;

        /// <summary>The last batch, in the order of the walk, that asked for the segment.</summary>
        internal long LastUse;

        internal SegmentOwner? Owner;
        internal VortexBuffer View;
    }
}

/// <summary>A batch under construction that can wait for a segment another batch of its scan is reading.</summary>
internal abstract class SegmentWaiter : IValueTaskSource
{
    private ManualResetValueTaskSourceCore<bool> _read = new() { RunContinuationsAsynchronously = true };

    /// <summary>The set whose slots the batch waits on, while it waits.</summary>
    internal SegmentRequestSet? Requests { get; private set; }

    /// <summary>The number of the batch that waits.</summary>
    internal long Batch { get; private set; }

    /// <summary>Completes once no segment the batch needs is being read by another batch.</summary>
    internal ValueTask ReadByOthersAsync() => new ValueTask(this, _read.Version);

    internal void Park(SegmentRequestSet requests, long batch)
    {
        _read.Reset();
        Requests = requests;
        Batch = batch;
    }

    internal void Wake()
    {
        Requests = null;
        _read.SetResult(true);
    }

    void IValueTaskSource.GetResult(short token) => _read.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _read.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _read.OnCompleted(continuation, state, token, flags);
}
