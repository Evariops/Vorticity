using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Dataset;

/// <summary>
/// An <see cref="ISegmentReader"/> over one object of an <see cref="IObjectStore"/>. Nearby
/// segments are coalesced into runs and the runs issued together, so a split costs a few round
/// trips instead of one per segment; each segment is then copied, since a buffer must sit at its
/// declared alignment and a run's bytes arrive in a pooled array that guarantees nothing.
/// </summary>
internal sealed class ObjectSegmentSource : ISegmentReader
{
    private readonly IObjectStore _store;
    private readonly string _key;
    private readonly SegmentReadOptions _options;
    private readonly bool _ownsStore;
    private long _length = -1;
    private string? _token;
    private bool _disposed;

    /// <summary>
    /// Opens a source over <paramref name="key"/>, with null <paramref name="options"/> for the
    /// default coalescing budgets. With <paramref name="ownsStore"/>, disposing the source disposes
    /// the store.
    /// </summary>
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
    /// The object's token as of the first call that learned it, or null before any call. A caller
    /// holding a token from a commit compares it to this one: a difference means the key was
    /// deleted and created again, the only way an immutable object's bytes change.
    /// </summary>
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

            // Issued together, so the runs cost one step of the critical path however many they are.
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
                // A failure leaves the other reads in flight, each owning a buffer this batch must
                // return: skipping them would send those buffers to the finalizer instead of the
                // pool and leave their faults unobserved, so they are all awaited before throwing.
                for (int r = 0; r < runCount; r++)
                {
                    if (ranges[r] is { } taken)
                    {
                        taken.Dispose();
                        continue;
                    }

                    try
                    {
                        (await reads[r].ConfigureAwait(false)).Dispose();
                    }
                    catch
                    {
                        // One of these is the failure on its way out; the rest may have their own.
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
            // A range that starts wholly past the end is a format error, not an argument error.
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
