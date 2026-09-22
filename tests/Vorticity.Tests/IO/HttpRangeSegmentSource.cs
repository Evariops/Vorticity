using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Tests.IO;

/// <summary>
/// A range-request segment source over an arbitrary <see cref="IRangeTransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// A reference out-of-core reader, kept out of the core. It obeys no rule beyond the documentation
/// of <see cref="ISegmentReader"/> and <see cref="SegmentRequestSet"/>, so passing the suite the
/// built-in readers pass shows that documentation is enough to implement the reader seam.
/// </para>
/// <para>
/// The object-storage shape, and it differs from the local ones on purpose. A response body is a
/// stream, so segmentation happens during the copy off the socket: this source stages a coalesced
/// run in a pooled managed array and copies each segment into its own aligned buffer. That is the
/// one copy a streamed body cannot avoid, accepted as such, and it exercises
/// <see cref="SegmentRequestSet.SetResult"/> — the owner-per-slot half of the API that the
/// zero-copy local sources never touch.
/// </para>
/// <para>Thread-safe: it holds no mutable state beyond two counters.</para>
/// </remarks>
internal sealed class HttpRangeSegmentSource : ISegmentReader
{
    private readonly IRangeTransport _transport;
    private readonly SegmentReadOptions _options;
    private int _requestCount;
    private long _bytesTransferred;
    private int _disposed;

    /// <summary>Creates a source over <paramref name="transport"/>.</summary>
    /// <param name="transport">The range reader.</param>
    /// <param name="options">Coalescing budgets, or null for <see cref="SegmentReadOptions.Default"/>.</param>
    public HttpRangeSegmentSource(IRangeTransport transport, SegmentReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _options = options ?? SegmentReadOptions.Default;
    }

    /// <summary>The object length in bytes.</summary>
    public long Length => _transport.Length;

    /// <summary>How many range requests this source has issued. The coalescing metric.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>How many bytes those requests moved, gap bytes included.</summary>
    public long BytesTransferred => Interlocked.Read(ref _bytesTransferred);

    /// <inheritdoc/>
    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<long>(Length);
    }

    /// <inheritdoc/>
    public async ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        Validate(in spec, Length, out long offset, out int length);

        if (length == 0)
        {
            return PinnedArraySegmentOwner.Allocate(0, 1);
        }

        PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.Allocate(
            length, VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent));

        try
        {
            await FetchAsync(offset, length, owner, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        return owner;
    }

    /// <inheritdoc/>
    public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

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

        int[] order = ArrayPool<int>.Shared.Rent(registered);
        ulong[] keys = ArrayPool<ulong>.Shared.Rent(registered);
        SegmentSpec[] sorted = ArrayPool<SegmentSpec>.Shared.Rent(registered);
        CoalescedRun[] runs = ArrayPool<CoalescedRun>.Shared.Rent(registered);

        try
        {
            int pending = 0;
            for (int slot = 0; slot < registered; slot++)
            {
                if (requests.IsFilled(slot))
                {
                    // A zero-length segment: already satisfied, and there is nothing to request.
                    continue;
                }

                order[pending] = slot;
                keys[pending] = requests.GetSpec(slot).Offset;
                pending++;
            }

            if (pending == 0)
            {
                requests.Complete();
                return;
            }

            keys.AsSpan(0, pending).Sort(order.AsSpan(0, pending));

            for (int i = 0; i < pending; i++)
            {
                sorted[i] = requests.GetSpec(order[i]);
                Validate(in sorted[i], Length, out _, out _);
            }

            // The shared coalescer, so the 64-byte rounding rule is not re-derived here; it also
            // re-validates every spec it is handed.
            int runCount = SegmentCoalescer.Plan(sorted.AsSpan(0, pending), runs.AsSpan(0, pending), _options);

            for (int r = 0; r < runCount; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await FetchRunAsync(requests, runs[r], sorted, order, cancellationToken).ConfigureAwait(false);
            }

            requests.Complete();
        }
        catch
        {
            // Contract point 1: all-or-nothing. Everything acquired inside this call goes back and
            // IsPopulated stays false, so the caller can retry the whole set.
            requests.AbandonPending();
            throw;
        }
        finally
        {
            ArrayPool<CoalescedRun>.Shared.Return(runs);
            ArrayPool<SegmentSpec>.Shared.Return(sorted);
            ArrayPool<ulong>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<SegmentOwner> ReadRangeAsync(
        long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (alignment < 1 || alignment > VortexLimits.MaxAlignment ||
            (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, null);
        }

        if (offset > Length || (offset == Length && length > 0))
        {
            throw new VortexFormatException(
                $"A range starting at {offset} lies past the end of a {Length}-byte object.");
        }

        long available = Length - offset;
        int actual = length <= available ? length : (int)available;

        PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.Allocate(actual, alignment);
        if (actual == 0)
        {
            return owner;
        }

        try
        {
            await FetchAsync(offset, actual, owner, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        return owner;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async ValueTask FetchRunAsync(
        SegmentRequestSet requests,
        CoalescedRun run,
        SegmentSpec[] sorted,
        int[] order,
        CancellationToken cancellationToken)
    {
        byte[] staging = ArrayPool<byte>.Shared.Rent(run.Length);

        try
        {
            Interlocked.Increment(ref _requestCount);
            Interlocked.Add(ref _bytesTransferred, run.Length);

            await _transport
                .ReadRangeAsync(run.Start, staging.AsMemory(0, run.Length), cancellationToken)
                .ConfigureAwait(false);

            for (int k = run.FirstIndex; k < run.FirstIndex + run.Count; k++)
            {
                SegmentSpec spec = sorted[k];
                int relative = (int)((long)spec.Offset - run.Start);

                PinnedArraySegmentOwner owner = PinnedArraySegmentOwner.CopyOf(
                    staging.AsSpan(relative, (int)spec.Length),
                    VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent));

                // SetResult takes the reference even if it then rejects the buffer, so the catch in
                // ReadManyAsync releases it either way.
                requests.SetResult(order[k], owner);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(staging);
        }
    }

    private async ValueTask FetchAsync(
        long offset, int length, PinnedArraySegmentOwner destination, CancellationToken cancellationToken)
    {
        byte[] staging = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            Interlocked.Increment(ref _requestCount);
            Interlocked.Add(ref _bytesTransferred, length);

            await _transport
                .ReadRangeAsync(offset, staging.AsMemory(0, length), cancellationToken)
                .ConfigureAwait(false);

            staging.AsSpan(0, length).CopyTo(destination.WritableSpan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(staging);
        }
    }

    /// <summary>
    /// Everything an implementer must check before forming an address.
    /// </summary>
    private static void Validate(in SegmentSpec spec, long fileLength, out long offset, out int length)
    {
        // The library's own cap, never a local one tuned to what the corpus happens to contain.
        VortexLimits.CheckAlignmentExponent(spec.AlignmentExponent);

        if (spec.Length > int.MaxValue)
        {
            throw new VortexFormatException($"Segment length {spec.Length} is not addressable.");
        }

        ulong end = spec.Offset + spec.Length;
        if (end < spec.Offset || end > (ulong)long.MaxValue)
        {
            throw new VortexFormatException(
                $"Segment at offset {spec.Offset} with length {spec.Length} overflows.");
        }

        if ((long)end > fileLength)
        {
            throw new VortexFormatException(
                $"Segment [{spec.Offset}, {end}) escapes a {fileLength}-byte object.");
        }

        offset = (long)spec.Offset;
        length = (int)spec.Length;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
