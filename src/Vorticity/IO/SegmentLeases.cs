using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.IO;

/// <summary>The public reads of the built-in sources, answered by their engine reads.</summary>
internal static class SegmentLeases
{
    internal static SegmentSpec SpecOf(SegmentRange range, byte alignmentExponent = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(range.Offset, nameof(range));
        ArgumentOutOfRangeException.ThrowIfNegative(range.Length, nameof(range));
        return new SegmentSpec((ulong)range.Offset, (uint)range.Length, alignmentExponent, 0, 0);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<SegmentLease> ReadAsync(ISegmentReader reader, SegmentRange range, CancellationToken cancellationToken)
    {
        SegmentOwner owner = await reader.ReadAsync(SpecOf(range), cancellationToken).ConfigureAwait(false);
        return SegmentOwnerMemory.Lease(owner);
    }

    internal static async ValueTask ReadAsync(
        ISegmentReader reader, ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken)
    {
        if (leases.Length < ranges.Length)
        {
            throw new ArgumentException($"{leases.Length} leases for {ranges.Length} ranges.", nameof(leases));
        }

        SegmentRequestSet set = new SegmentRequestSet(Math.Max(ranges.Length, 1));
        try
        {
            int[] slots = new int[ranges.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = set.Add(SpecOf(ranges.Span[i]));
            }

            await reader.ReadManyAsync(set, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < slots.Length; i++)
            {
                SegmentOwner owner = set.GetOwner(slots[i]).Retain();
                leases.Span[i] = SegmentOwnerMemory.Lease(owner, set.GetBuffer(slots[i]));
            }
        }
        finally
        {
            set.Release();
        }
    }
}
