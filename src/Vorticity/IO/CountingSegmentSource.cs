using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>Counts the requests, ranges and bytes a reader asks of a source.</summary>
/// <param name="inner">The source that serves them.</param>
/// <param name="ownsInner">Whether disposing this source disposes <paramref name="inner"/>.</param>
/// <remarks>
/// A read path is priced by its round trips: on an object store a request costs tens of
/// milliseconds and a byte is nearly free, so requests are the first thing to count and bytes the
/// second. One request is counted per <c>ReadAsync</c>, per <c>ReadRangeAsync</c> and per
/// <c>ReadManyAsync</c> — a set of ranges fetched in one round, whatever coalescing the source
/// below applies — along with every range and byte asked for. Wrapping a source in this one is how
/// a caller sees what a query costs.
/// </remarks>
internal sealed class CountingSegmentSource(ISegmentReader inner, bool ownsInner = true) : ISegmentReader
{
    private readonly ISegmentReader _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private long _requests;
    private long _ranges;
    private long _bytes;

    /// <summary>The rounds asked for: a single read, or a set of ranges read together.</summary>
    public long Requests => Interlocked.Read(ref _requests);

    /// <summary>The ranges asked for, across all requests.</summary>
    public long Ranges => Interlocked.Read(ref _ranges);

    /// <summary>The bytes asked for.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Sets every count back to zero.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _ranges, 0);
        Interlocked.Exchange(ref _bytes, 0);
    }

    /// <inheritdoc/>
    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) => _inner.GetLengthAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        Count(1, spec.Length);
        return _inner.ReadAsync(spec, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The walk over the slots here is the second one the set gets, and it is what a decorator
    /// costs. What is counted is the reads the inner source is about to issue, which is the set's
    /// unfilled slots; only the inner source walks them again to coalesce and read, and by then it
    /// has filled them, so there is no moment at which one walk could answer both. Counting after
    /// the call would count nothing, every slot being filled by then.
    /// </remarks>
    public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        long bytes = 0;
        int ranges = 0;
        for (int slot = 0; slot < requests.Count; slot++)
        {
            if (!requests.IsFilled(slot))
            {
                bytes += requests.GetSpec(slot).Length;
                ranges++;
            }
        }

        if (ranges > 0)
        {
            Count(ranges, bytes);
        }

        return _inner.ReadManyAsync(requests, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        Count(1, length);
        return _inner.ReadRangeAsync(offset, length, alignment, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ownsInner ? _inner.DisposeAsync() : default;

    private void Count(int ranges, long bytes)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _ranges, ranges);
        Interlocked.Add(ref _bytes, bytes);
    }
}
