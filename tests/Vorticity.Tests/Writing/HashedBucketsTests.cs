// The buckets of a hashed postings index: an index written with no bound on its rows notes every pair in one bucket, which
// the end cuts again by the next bits of the hashes, so that no sort holds more than about a bucket's aim, and the pairs
// still come out in the hashes' order with the blocks of a hash in theirs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
}
