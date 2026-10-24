// The buckets of a hashed postings index: an index written with no bound on its rows notes every pair in one bucket, which
// the end cuts again by the next bits of the hashes, so that no sort holds more than about a bucket's aim, and the pairs
// still come out in the hashes' order with the blocks of a hash in theirs. An end that fails waits for the buckets it sorts
// ahead on the session's lanes, which read the scratch the writer then lets go.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class HashedBucketsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 300,000 pairs over 3,000 blocks with no bound on the rows, a bucket aiming at 4,096 pairs: the one bucket is cut into
    /// 128 parts, none past twice the aim, and the parts sorted one after another give every pair in the order of a stable
    /// sort by hash, a hash's blocks in their order, as one sort of the whole bucket did.
    /// </summary>
    [Fact]
    public async Task ABucketPastTwiceItsAimIsSortedInPartsOfIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-buckets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using RunScratch scratch = new RunScratch(memoryBudget: 1 << 20, directory);
            using HashedBuckets buckets = new HashedBuckets(expectedPairs: 0, bucketPairs: 4_096);
            Random random = new Random(5);
            ulong[] keys = [.. Enumerable.Range(0, 20_000).Select(_ => (ulong)random.NextInt64() ^ ((ulong)random.Next() << 63))];
            List<(ulong Hash, uint Block)> noted = [];
            HashSet<ulong> seen = [];
            for (uint block = 0; block < 3_000; block++)
            {
                seen.Clear();
                for (int i = 0; i < 100; i++)
                {
                    ulong hash = keys[random.Next(keys.Length)];
                    if (seen.Add(hash))
                    {
                        noted.Add((hash, block));
                    }

                    buckets.Note(hash, block);
                }

                buckets.CloseBlock();
                if (block % 500 == 499)
                {
                    await buckets.SpillAsync(scratch, Ct);
                }
            }

            Assert.Equal(1, buckets.Buckets);
            Assert.Equal(noted.Count, buckets.Pairs);
            await buckets.SpillAsync(scratch, Ct);
            await buckets.RefineAsync(scratch, Ct);
            Assert.Equal(128, buckets.Buckets);
            Assert.True(buckets.Largest <= 2 * 4_096, $"the largest part holds {buckets.Largest} pairs");

            BucketBuffers buffers = new BucketBuffers();
            buffers.Reserve(buckets.Largest);
            List<(ulong Hash, uint Block)> sorted = [];
            for (int bucket = 0; bucket < buckets.Buckets; bucket++)
            {
                int count = await buckets.SortedAsync(bucket, scratch, buffers, Ct);
                for (int i = 0; i < count; i++)
                {
                    sorted.Add((buffers.Hashes[i], buffers.Blocks[i]));
                }
            }

            Assert.Equal([.. noted.OrderBy(pair => pair.Hash)], sorted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A bucket within twice its aim stays whole: an index whose bound on its rows was right is sorted as before.</summary>
    [Fact]
    public async Task ABucketWithinTwiceItsAimStaysWhole()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-buckets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using RunScratch scratch = new RunScratch(memoryBudget: 1 << 20, directory);
            using HashedBuckets buckets = new HashedBuckets(expectedPairs: 40_000, bucketPairs: 4_096);
            for (uint block = 0; block < 400; block++)
            {
                for (int i = 0; i < 100; i++)
                {
                    buckets.Note(((ulong)i * 0x9E3779B97F4A7C15UL) ^ (block * 0xC2B2AE3D27D4EB4FUL), block);
                }

                buckets.CloseBlock();
            }

            int before = buckets.Buckets;
            await buckets.SpillAsync(scratch, Ct);
            await buckets.RefineAsync(scratch, Ct);
            Assert.Equal(16, before);
            Assert.Equal(before, buckets.Buckets);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// An end of hashed postings that fails as it hands over its first bucket's segments leaves only once the buckets read
    /// back and sorted ahead on the lanes have ended: the writer lets their scratch go when the failure reaches it. The sorts
    /// of the buckets after the first are held until the failure is in, so the end is seen waiting for them.
    /// </summary>
    [Fact]
    public async Task AFailedEndWaitsForTheBucketsSortedAhead()
    {
        string directory = TestScratch.Create("vorticity-hashed-end-");
        try
        {
            using RunScratch scratch = new RunScratch(memoryBudget: 1 << 20, directory);
            DTypeArena types = new DTypeArena();
            DType longs = types.Primitive(PType.I64, Nullability.NonNullable);
            Assert.True(KeyIndexBuilder.Supports(longs, out KeyLayout layout, out _, out _));
            TaskCompletionSource held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using KeyIndexBuilder builder = KeyIndexBuilder.ForHashes(layout, KeyRunOptions.DefaultSegmentEntries);
            builder.Scratch = scratch;
            builder.BlockRows = 1_000;
            builder.BucketBits = 3;
            builder.Lanes = new LaneBudget(4);
            builder.BeforeSort = bucket => bucket == 0 ? Task.CompletedTask : held.Task;
            builder.Stream = (_, _) => throw new IOException("The sink failed.");

            builder.Start(0, 0);
            CanonicalArena arena = new CanonicalArena();
            for (int block = 0; block < 8; block++)
            {
                VortexBuffer values = arena.Allocate(1_000 * sizeof(long), sizeof(long), out Span<byte> bytes);
                Span<long> rows = MemoryMarshal.Cast<byte, long>(bytes);
                for (int i = 0; i < rows.Length; i++)
                {
                    rows[i] = (block * 1_000L) + i;
                }

                builder.Accumulate(arena, arena.AddPrimitive(longs, 1_000, Validity.NonNullable, PType.I64, values), 0, 1_000);
                builder.CloseBlock();
            }

            await builder.CloseChunkAsync(0, 8, 0, 8_000, Ct);
            Task end = builder.EndOfDataAsync(Ct).AsTask();
            await Task.Delay(200, Ct);
            Assert.False(end.IsCompleted, "the end left while buckets were still sorted ahead");

            held.SetResult();
            await Assert.ThrowsAsync<IOException>(() => end);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
