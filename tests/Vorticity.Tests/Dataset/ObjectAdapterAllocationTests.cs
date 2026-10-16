using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// A batch read through an object store's source allocates nothing once its blocks are pooled;
/// measured alone, as every allocation ceiling is. Its blocks come from the process-wide pool, and a
/// block a neighbour rents and gives back without writing it through a <c>Memory</c> has no memory
/// manager yet: the first read into it makes one, 24 bytes, on the reading thread. Beside the rest
/// of the suite, such blocks reach the top of the pool between two rounds.
/// </summary>
[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed class ObjectAdapterAllocationTests
{
    private const string Key = "data/adapter.vortex";

    [Fact]
    public async Task ABatchReadsEachRunIntoItsBlockAndAllocatesNothingOnceTheBlocksArePooled()
    {
        // Sixteen runs a gap apart, each read straight into a pooled block its segments are views of:
        // what the source allocates per batch is nothing, the store's own cost aside.
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] content = new byte[1 << 20];
        new Random(0x0B_5_0C).NextBytes(content);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await store.PutIfAbsentAsync(Key, content, ct);
        await using ObjectStoreSource source = new ObjectStoreSource(store, Key, content.Length, new SegmentReadOptions { CoalesceGapBytes = 0 });
        using SegmentRequestSet set = new SegmentRequestSet();

        long allocated = 0;
        for (int round = 0; round < 4; round++)
        {
            for (int slot = 0; slot < 16; slot++)
            {
                set.Add(new SegmentSpec((ulong)slot << 16, 1_000, 6, 0, 0));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            await source.ReadManyAsync(set, ct);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            for (int slot = 0; slot < 16; slot++)
            {
                Assert.True(set.GetBuffer(slot).Span.SequenceEqual(content.AsSpan(slot << 16, 1_000)));
            }

            set.Release();
        }

        Assert.Equal(0, allocated);
    }
}
