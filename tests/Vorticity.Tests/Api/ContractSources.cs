using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Tests.Api;

/// <summary>
/// A caller's source that records every range asked of it: what a scan requests, seen from the
/// only side a caller can see it from.
/// </summary>
internal sealed class CountingSource(ISegmentSource inner) : ISegmentSource
{
    private readonly List<SegmentRange> _ranges = [];

    public long Length => inner.Length;

    /// <summary>The ranges asked so far, in the order they were asked.</summary>
    public SegmentRange[] Ranges
    {
        get
        {
            lock (_ranges)
            {
                return [.. _ranges];
            }
        }
    }

    public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken)
    {
        lock (_ranges)
        {
            _ranges.Add(range);
        }

        return inner.ReadAsync(range, cancellationToken);
    }

    public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
    {
        lock (_ranges)
        {
            _ranges.AddRange(ranges.Span);
        }

        return inner.ReadAsync(ranges, leases, cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>
/// A file source that notes, at each read a scan makes, how many bytes the reading thread has
/// allocated so far: the one point inside an aggregate's or a count's loop that a test can reach.
/// </summary>
/// <remarks>
/// It forwards to the engine's own reader rather than through the adapter a caller's source gets,
/// so that what it measures is the scan and not the adapter. It allocates nothing itself once built.
/// </remarks>
internal sealed class SamplingSource : ISegmentSource, ISegmentReader
{
    private readonly MemoryMappedSegmentSource _inner;
    private readonly long[] _allocated = new long[4096];
    private readonly int[] _threads = new int[4096];
    private int _count;

    internal SamplingSource(string path) => _inner = new MemoryMappedSegmentSource(path);

    public long Length => ((ISegmentSource)_inner).Length;

    /// <summary>Starts counting afresh, for the next scan.</summary>
    internal void Clear() => _count = 0;

    /// <summary>The reads noted since <see cref="Clear"/>.</summary>
    internal int Count => _count;

    /// <summary>What the reading thread had allocated when read <paramref name="index"/> began.</summary>
    internal long AllocatedAt(int index) => _allocated[index];

    /// <summary>The thread read <paramref name="index"/> ran on.</summary>
    internal int ThreadAt(int index) => _threads[index];

    public ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken) =>
        ((ISegmentSource)_inner).ReadAsync(range, cancellationToken);

    public ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken) =>
        ((ISegmentSource)_inner).ReadAsync(ranges, leases, cancellationToken);

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) =>
        ((ISegmentReader)_inner).GetLengthAsync(cancellationToken);

    public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        Note();
        return ((ISegmentReader)_inner).ReadAsync(spec, cancellationToken);
    }

    public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
    {
        Note();
        return ((ISegmentReader)_inner).ReadManyAsync(requests, cancellationToken);
    }

    public ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
    {
        Note();
        return ((ISegmentReader)_inner).ReadRangeAsync(offset, length, alignment, cancellationToken);
    }

    public ValueTask DisposeAsync() => ((ISegmentReader)_inner).DisposeAsync();

    private void Note()
    {
        int index = _count;
        if (index < _allocated.Length)
        {
            _allocated[index] = GC.GetAllocatedBytesForCurrentThread();
            _threads[index] = Environment.CurrentManagedThreadId;
            _count = index + 1;
        }
    }
}
