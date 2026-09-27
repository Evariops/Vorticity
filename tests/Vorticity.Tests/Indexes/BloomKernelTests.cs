using System;
using System.IO.Hashing;
using System.Runtime.InteropServices;

using Vorticity.Indexes;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

/// <summary>
/// The Bloom writer's kernels against their definitions: XXH3 of four and eight bytes hashed eight
/// at a time against the library's hash of the same bytes, a block's bits set and tested a vector
/// at a time against the Parquet rule written out a word at a time, and a column's rows hashed in
/// batches into the same set a value at a time would build.
/// </summary>
public sealed class BloomKernelTests
{
    private static readonly uint[] Salts = [0x47b6137b, 0x44974d91, 0x8824ad5b, 0xa2b7289d, 0x705495c7, 0x2df1424b, 0x9efc4947, 0x5c6bfb31];

    [Fact]
    public void BatchedHashesAreXxHash3()
    {
        if (!XxHash3Fixed.IsVectorized)
        {
            return;
        }

        Random random = new Random(20260927);
        uint[] words = new uint[256];
        ulong[] longs = new ulong[256];
        random.NextBytes(MemoryMarshal.AsBytes(words.AsSpan()));
        random.NextBytes(MemoryMarshal.AsBytes(longs.AsSpan()));
        words[0] = 0;
        words[1] = uint.MaxValue;
        longs[0] = 0;
        longs[1] = ulong.MaxValue;
        ulong[] hashes = new ulong[256];

        XxHash3Fixed.Hash4(words, hashes);
        for (int i = 0; i < words.Length; i++)
        {
            Assert.Equal(XxHash3.HashToUInt64(MemoryMarshal.AsBytes(words.AsSpan(i, 1))), hashes[i]);
        }

        XxHash3Fixed.Hash8(longs, hashes);
        for (int i = 0; i < longs.Length; i++)
        {
            Assert.Equal(XxHash3.HashToUInt64(MemoryMarshal.AsBytes(longs.AsSpan(i, 1))), hashes[i]);
        }
    }

    [Fact]
    public void ABlockSetsAndTestsTheSaltedBits()
    {
        Random random = new Random(20260928);
        foreach (int blocks in new[] { 1, 3, 64 })
        {
            uint[] filter = new uint[blocks * SplitBlockBloom.WordsPerBlock];
            uint[] expected = new uint[filter.Length];
            ulong[] inserted = new ulong[200];
            for (int n = 0; n < inserted.Length; n++)
            {
                ulong hash = (ulong)random.NextInt64();
                inserted[n] = hash;
                SplitBlockBloom.Insert(filter, hash);
                Insert(expected, hash);
                Assert.Equal(expected, filter);
            }

            foreach (ulong hash in inserted)
            {
                Assert.True(SplitBlockBloom.Contains(filter, hash));
            }

            for (int n = 0; n < 2_000; n++)
            {
                ulong hash = (ulong)random.NextInt64();
                Assert.Equal(Contains(expected, hash), SplitBlockBloom.Contains(filter, hash));
            }
        }
    }

    [Fact]
    public void BatchedRowsBuildTheSetOneValueAtATimeWould()
    {
        Random random = new Random(20260929);
        foreach (int width in new[] { 4, 8 })
        {
            foreach ((int start, int stop) in new[] { (0, 1_000), (3, 67), (5, 69), (64, 64), (1, 200) })
            {
                byte[] values = new byte[1_000 * width];
                random.NextBytes(values);

                // Repeats, as a column's values have.
                values.AsSpan(0, 64 * width).CopyTo(values.AsSpan(128 * width));

                using HashSet64 batched = new HashSet64();
                using HashSet64 single = new HashSet64();
                BloomBuilder.HashRows(values, width, start, stop, batched);
                for (int row = start; row < stop; row++)
                {
                    single.Add(XxHash3.HashToUInt64(values.AsSpan(row * width, width)));
                }

                Assert.Equal(single.Count, batched.Count);
                uint[] fromBatched = new uint[64 * SplitBlockBloom.WordsPerBlock];
                uint[] fromSingle = new uint[fromBatched.Length];
                batched.InsertInto(fromBatched);
                single.InsertInto(fromSingle);
                Assert.Equal(fromSingle, fromBatched);
            }
        }
    }

    /// <summary>The Parquet split-block rule, a word at a time.</summary>
    private static void Insert(uint[] filter, ulong hash)
    {
        int block = (int)(((hash >> 32) * (ulong)(uint)(filter.Length / 8)) >> 32);
        for (int i = 0; i < 8; i++)
        {
            filter[(block * 8) + i] |= 1u << (int)(((uint)hash * Salts[i]) >> 27);
        }
    }

    private static bool Contains(uint[] filter, ulong hash)
    {
        int block = (int)(((hash >> 32) * (ulong)(uint)(filter.Length / 8)) >> 32);
        for (int i = 0; i < 8; i++)
        {
            if ((filter[(block * 8) + i] & (1u << (int)(((uint)hash * Salts[i]) >> 27))) == 0)
            {
                return false;
            }
        }

        return true;
    }
}
