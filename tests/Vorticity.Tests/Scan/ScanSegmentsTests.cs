using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// The segments a scan holds are handed to the batches that ask for them again, and the rest are
/// claimed for the batch to read.
/// </summary>
public sealed class ScanSegmentsTests
{
    private const uint SegmentBytes = 64;

    [Fact]
    public void ABatchTakesWhatTheScanHoldsAndClaimsTheRestThroughReleases()
    {
        NativeSegmentOwner block = NativeSegmentOwner.Allocate((int)SegmentBytes, 64);
        using (ScanSegments held = new ScanSegments(lanes: 1))
        {
            using SegmentRequestSet requests = new SegmentRequestSet();

            // Three hundred segments, enough for the set to grow twice and to chain its entries.
            Assert.Equal(0, Batch(held, requests, block, batch: 1, first: 0, count: 300));

            // Half of them again and as many new ones: the half is served, the rest claimed.
            Assert.Equal(150, Batch(held, requests, block, batch: 2, first: 150, count: 300));

            // Batch 2 delivered, the segments only batch 1 asked for are dropped, moving the others
            // down: what is held is found from where the drop left it, and the dropped are claimed.
            held.Release(2);
            Assert.Equal(300, Batch(held, requests, block, batch: 3, first: 0, count: 450));
        }

        // Every reference the set and the batches took was given back.
        Assert.Equal(1, block.RefCount);
        block.Release();
    }

    /// <summary>Registers, claims, reads and publishes one batch; returns how many segments the set served.</summary>
    private static int Batch(ScanSegments held, SegmentRequestSet requests, NativeSegmentOwner block, long batch, int first, int count)
    {
        for (int i = first; i < first + count; i++)
        {
            requests.Add(new SegmentSpec((ulong)i * SegmentBytes, SegmentBytes, 0, 0, 0));
        }

        Assert.False(held.Claim(requests, batch, waiter: null));
        int served = 0;
        for (int slot = 0; slot < requests.Count; slot++)
        {
            if (requests.IsFilled(slot))
            {
                served++;
            }
            else
            {
                requests.SetSharedResult(slot, block, block.Buffer);
            }
        }

        requests.Complete();
        held.Publish(requests, batch);
        requests.Release();
        return served;
    }
}
