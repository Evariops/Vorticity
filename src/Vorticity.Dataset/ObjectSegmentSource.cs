// The read adapter of docs/13-dataset.md §11: "the reader's `ISegmentSource` over a data object is
// an adapter on it, with 03 §3.5's coalescing".
//
// COALESCING IS THE WHOLE POINT, and 03 §3.5 says why: "the reader registers every segment of a
// split before reading, which allows coalescing nearby ranges and parallelizing. This makes or
// breaks performance on object storage." A source that turned each of a split's forty segments into
// a request would pay forty round trips for what is often three. So the plan comes from the core's
// own `SegmentCoalescer` -- the same gaps and budgets the local sources use -- and the runs it
// produces are issued TOGETHER, which is what makes them one step of §9.2's critical path rather
// than one each.
//
// IT COPIES, once per segment, and that is not laziness. A `VortexBuffer`'s contract is that its
// base address satisfies its declared alignment; a run's bytes arrive in a pooled array where no
// offset guarantees anything. `MemorySegmentSource` in the core made the same trade for the same
// reason, and here the bytes crossed a network first, which dwarfs the copy.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Dataset;

/// <summary>An <see cref="ISegmentSource"/> over one object of an <see cref="IObjectStore"/>.</summary>
public sealed class ObjectSegmentSource : ISegmentSource
{
    private readonly IObjectStore _store;
    private readonly string _key;
    private readonly SegmentReadOptions _options;
    private readonly bool _ownsStore;
    private long _length = -1;
    private string? _token;
    private bool _disposed;

    /// <summary>Opens a source over <paramref name="key"/>.</summary>
    /// <param name="store">The store.</param>
    /// <param name="key">The object's key.</param>
    /// <param name="options">The coalescing budgets, or null for the defaults.</param>
    /// <param name="ownsStore">Whether disposing this source disposes the store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public ObjectSegmentSource(
        IObjectStore store, string key, SegmentReadOptions? options = null, bool ownsStore = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ObjectKey.Check(key);
        _store = store;
        _key = key;
        _options = options ?? new SegmentReadOptions();
        _ownsStore = ownsStore;
    }

    /// <summary>
    /// The object's token as of the first call that learned it, or null before any call.
    /// </summary>
    /// <remarks>
    /// §7 binds what a reader believes to the object it read. A caller that holds a token from a
    /// commit compares it to this one; a difference means the key was deleted and created again,
    /// which is the only way an immutable object's bytes change.
    /// </remarks>
    public string? Token => Volatile.Read(ref _token);

    /// <inheritdoc/>
    public async ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long known = Interlocked.Read(ref _length);
        if (known >= 0)
        {
            return known;
        }

        ObjectHead head = await _store.HeadAsync(_key, cancellationToken).ConfigureAwait(false)
            ?? throw ObjectNotFoundException.For(_key);
        Interlocked.Exchange(ref _length, head.Length);
        Volatile.Write(ref _token, head.Token);
        return head.Length;
    }

    /// <inheritdoc/>
    public async ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent);
        int length = checked((int)spec.Length);
        using ObjectRange range = await _store
            .GetRangeAsync(_key, checked((long)spec.Offset), length, cancellationToken).ConfigureAwait(false);
        Note(range);
        if (range.Length != length)
        {
            throw new VortexFormatException(
                $"Segment {spec.Offset}+{spec.Length} of '{_key}' ended after {range.Length} bytes.");
        }

        return PinnedArraySegmentOwner.CopyOf(range.Bytes.Span, 1 << spec.AlignmentExponent);
    }

    /// <inheritdoc/>
    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.IsPopulated)
        {
            return;
        }

        int registered = requests.Count;
        if (registered == 0)
        {
            requests.Complete();
            return;
        }

        // The pending slots, in offset order: the coalescer requires it and verifies it.
        List<int> slots = [];
        for (int slot = 0; slot < registered; slot++)
        {
            if (!requests.IsFilled(slot))
            {
                slots.Add(slot);
            }
        }

        if (slots.Count == 0)
        {
            requests.Complete();
            return;
        }

        slots.Sort((left, right) => requests.GetSpec(left).Offset.CompareTo(requests.GetSpec(right).Offset));
        SegmentSpec[] sorted = ArrayPool<SegmentSpec>.Shared.Rent(slots.Count);
        CoalescedRun[] runs = ArrayPool<CoalescedRun>.Shared.Rent(slots.Count);
        ObjectRange?[] ranges;
        int runCount;
        try
        {
            for (int i = 0; i < slots.Count; i++)
            {
                sorted[i] = requests.GetSpec(slots[i]);
            }

            runCount = SegmentCoalescer.Plan(sorted.AsSpan(0, slots.Count), runs.AsSpan(0, slots.Count), _options);
            ranges = new ObjectRange?[runCount];

            // ISSUED TOGETHER. The store sees `runCount` requests in flight at once, which is one
            // step of §9.2's critical path however many they are.
            Task<ObjectRange>[] reads = new Task<ObjectRange>[runCount];
            for (int r = 0; r < runCount; r++)
            {
                CoalescedRun run = runs[r];
                reads[r] = _store
                    .GetRangeAsync(_key, run.Start, checked((int)run.Length), cancellationToken)
                    .AsTask();
            }

            try
            {
                for (int r = 0; r < runCount; r++)
                {
                    ranges[r] = await reads[r].ConfigureAwait(false);
                }
            }
            catch
            {
                // Every read that did succeed still owns a buffer; none of them is the caller's.
                for (int r = 0; r < runCount; r++)
                {
                    if (ranges[r] is { } taken)
                    {
                        taken.Dispose();
                    }
                    else if (reads[r].IsCompletedSuccessfully)
                    {
                        (await reads[r].ConfigureAwait(false)).Dispose();
                    }
                }

                requests.AbandonPending();
                throw;
            }
        }
        catch
        {
            ArrayPool<CoalescedRun>.Shared.Return(runs);
            ArrayPool<SegmentSpec>.Shared.Return(sorted);
            throw;
        }

        try
        {
            int slot = 0;
            for (int r = 0; r < runCount; r++)
            {
                CoalescedRun run = runs[r];
                ObjectRange range = ranges[r]!;
                Note(range);
                if (range.Length != run.Length)
                {
                    throw new VortexFormatException(
                        $"Run {run.Start}+{run.Length} of '{_key}' ended after {range.Length} bytes.");
                }

                while (slot < slots.Count && sorted[slot].Offset < (ulong)(run.Start + run.Length))
                {
                    SegmentSpec spec = sorted[slot];
                    int at = checked((int)((long)spec.Offset - run.Start));
                    requests.SetResult(
                        slots[slot],
                        PinnedArraySegmentOwner.CopyOf(
                            range.Bytes.Span.Slice(at, checked((int)spec.Length)),
                            1 << spec.AlignmentExponent));
                    slot++;
                }
            }

            requests.Complete();
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }
        finally
        {
            for (int r = 0; r < runCount; r++)
            {
                ranges[r]?.Dispose();
            }

            ArrayPool<CoalescedRun>.Shared.Return(runs);
            ArrayPool<SegmentSpec>.Shared.Return(sorted);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<SegmentOwner> ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (alignment < 1 || alignment > VortexLimits.MaxAlignment
            || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alignment), alignment, $"A power of two in [1, {VortexLimits.MaxAlignment}] was expected.");
        }

        try
        {
            using ObjectRange range = await _store.GetRangeAsync(_key, offset, length, cancellationToken)
                .ConfigureAwait(false);
            Note(range);
            return PinnedArraySegmentOwner.CopyOf(range.Bytes.Span, alignment);
        }
        catch (ArgumentOutOfRangeException cause) when (cause.ParamName == "offset")
        {
            // The seam's own wording: "a range that starts wholly past the end is a format error".
            throw new VortexFormatException($"Range {offset}+{length} starts past the end of '{_key}'.", cause);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return _ownsStore ? _store.DisposeAsync() : ValueTask.CompletedTask;
    }

    /// <summary>Records the object's token, and refuses a read of an object that changed under us.</summary>
    /// <param name="range">What the store just handed back.</param>
    private void Note(ObjectRange range)
    {
        string? known = Volatile.Read(ref _token);
        if (known is null)
        {
            Volatile.Write(ref _token, range.Token);
            return;
        }

        if (!string.Equals(known, range.Token, StringComparison.Ordinal))
        {
            throw new VortexFormatException(
                $"'{_key}' changed under this reader: it was {known} and is now {range.Token}.");
        }
    }
}
