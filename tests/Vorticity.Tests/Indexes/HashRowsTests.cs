// Rows hashed together, as a Bloom filter, a sheet and hashed postings hash theirs: each row's hash the XXH3-64 of its bytes,
// for every width a column's values take, rows from any start to any stop, across the vectors' batches and past them.
using System;
using System.IO.Hashing;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class HashRowsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(32)]
    public void EachRowsHashIsTheHashOfItsBytes(int width)
    {
        Random random = new Random(width);
        byte[] values = new byte[300 * width];
        random.NextBytes(values);
        ulong[] hashes = new ulong[300];
        foreach ((int start, int stop) in ((int, int)[])[(0, 0), (0, 1), (0, 64), (3, 70), (0, 128), (5, 300), (64, 257)])
        {
            Array.Clear(hashes);
            XxHash3Fixed.HashRows(values, width, start, stop, hashes);
            for (int row = start; row < stop; row++)
            {
                Assert.Equal(XxHash3.HashToUInt64(values.AsSpan(row * width, width)), hashes[row - start]);
            }
        }
    }

    [Fact]
    public void ATrigramHashesItsThreeBytesWhateverTheWordHoldsAboveThem()
    {
        byte[] three = [0x41, 0x42, 0x43];
        Assert.Equal(XxHash3.HashToUInt64(three), XxHash3Fixed.Hash3(0x00434241));
        Assert.Equal(XxHash3.HashToUInt64(three), XxHash3Fixed.Hash3(0xFF434241));
    }
}
