// A segment source that counts what goes through it - docs/13-dataset.md §9.2, docs/11 §6.4.
//
// THE READ PATH'S COST IS ITS ROUND TRIPS. On an object store a request is tens of milliseconds and a
// byte is nearly free, so the budget of 13 §9 counts dependent requests first and bytes second.
// This decorator counts both as the reader asks for them: one request per `ReadAsync`, per
// `ReadRangeAsync`, and per `ReadManyAsync` -- a set of ranges fetched in one round, whatever
// coalescing the source below applies -- and every range and byte asked for. It is what a test holds
// a lookup to, and what a caller wraps a source in to see what a query costs.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>Counts the requests, ranges and bytes a reader asks of a source.</summary>
/// <param name="inner">The source that serves them.</param>
/// <param name="ownsInner">Whether disposing this source disposes <paramref name="inner"/>.</param>
public sealed class CountingSegmentSource(ISegmentSource inner, bool ownsInner = true) : ISegmentSource
{
    private readonly ISegmentSource _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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
