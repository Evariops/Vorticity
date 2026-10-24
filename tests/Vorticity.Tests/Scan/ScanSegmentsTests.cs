using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.File;
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

    /// <summary>
    /// A batch that waits for a segment another batch is reading reads the segment it claimed itself meanwhile, and
    /// publishes it: the wait holds back neither its own read nor a batch that waits for what it claimed.
    /// </summary>
    [Fact]
    public async Task ABatchWaitingForAnothersSegmentReadsItsOwnMeanwhile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        List<(long Offset, long Length)> ranges = [];
        await using TestSegmentSource source = new TestSegmentSource(new byte[4 * SegmentBytes]) { Ranges = ranges };
        SegmentSpec a = new SegmentSpec(0, SegmentBytes, 0, 0, 0);
        SegmentSpec b = new SegmentSpec(2 * SegmentBytes, SegmentBytes, 0, 0, 0);
        using ScanSegments held = new ScanSegments(lanes: 2);
        using SegmentRequestSet first = new SegmentRequestSet();
        using SegmentRequestSet second = new SegmentRequestSet();
        using SegmentRequestSet third = new SegmentRequestSet();
        using SegmentRequestSet own = new SegmentRequestSet();
        Waiter waiter = new Waiter();

        // Batch 1 claims A and has not read it yet; batch 2 asks for A and B, claims B and waits for A.
        first.Add(in a);
        Assert.False(held.Claim(first, 1, waiter: null));
        second.Add(in a);
        second.Add(in b);
        Assert.True(held.Claim(second, 2, waiter));

        // Batch 2 reads B alone while it waits, and holds it for batch 3, which then claims nothing.
        await held.ReadOwnAsync(second, 2, source, own, metrics: null, ct);
        Assert.Equal([((long)b.Offset, (long)b.Length)], ranges);
        Assert.True(second.IsFilled(second.IndexOf(b.Offset, b.Length)));
        Assert.False(second.IsFilled(second.IndexOf(a.Offset, a.Length)));
        Assert.Equal(0, own.Count);
        Assert.False(waiter.ReadByOthersAsync().IsCompleted);
        third.Add(in b);
        Assert.False(held.Claim(third, 3, waiter: null));
        Assert.True(third.IsFilled(0));

        // Batch 1 reads and publishes A: batch 2 wakes, and its next claim takes A from what the scan holds.
        await source.ReadManyAsync(first, ct);
        held.Publish(first, 1);
        await waiter.ReadByOthersAsync();
        Assert.False(held.Claim(second, 2, waiter));
        second.Complete();
        Assert.Equal([((long)b.Offset, (long)b.Length), ((long)a.Offset, (long)a.Length)], ranges);
    }

    /// <summary>A batch that can wait for another's read, as a lane of a scan is.</summary>
    private sealed class Waiter : SegmentWaiter;

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
