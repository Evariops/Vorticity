using System;
using System.Collections.Generic;
using System.Numerics;
using Vorticity.Parquet.Encodings;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// Big-endian decimals of every width widened into every storage, a register at a time and a value at
/// a time, against the same numbers written by <see cref="BigInteger"/>: the extremes of each width,
/// zero, minus one, and random values, the last few of a run whose storage's worth of bytes runs past
/// the source among them.
/// </summary>
public sealed class BigEndianDecimalTests
{
    public static TheoryData<int> Slots => [4, 8, 16, 32];

    [Theory]
    [MemberData(nameof(Slots))]
    public void WidensEveryWidthIntoItsStorage(int slot)
    {
        Random random = new(slot);
        for (int width = 1; width <= slot; width++)
        {
            List<BigInteger> values = Values(width, random);
            byte[] source = new byte[values.Count * width];
            for (int i = 0; i < values.Count; i++)
            {
                Write(values[i], width, source.AsSpan(i * width, width), bigEndian: true);
            }

            byte[] expected = new byte[values.Count * slot];
            for (int i = 0; i < values.Count; i++)
            {
                Write(values[i], slot, expected.AsSpan(i * slot, slot), bigEndian: false);
            }

            byte[] fixedWidth = new byte[values.Count * slot];
            BigEndianDecimals.WidenFixed(source, width, fixedWidth, slot, values.Count);
            Assert.True(expected.AsSpan().SequenceEqual(fixedWidth), $"width {width} into {slot}");

            byte[] each = new byte[values.Count * slot];
            for (int i = 0; i < values.Count; i++)
            {
                BigEndianDecimals.Widen(source, i * width, width, each.AsSpan(i * slot, slot));
            }

            Assert.True(expected.AsSpan().SequenceEqual(each), $"width {width} into {slot}, a value at a time");
        }
    }

    [Fact]
    public void WidensAnEmptyValueToZero()
    {
        byte[] into = [1, 2, 3, 4, 5, 6, 7, 8];
        BigEndianDecimals.Widen([], 0, 0, into);
        Assert.Equal(new byte[8], into);
    }

    /// <summary>The extremes of a width, zero, minus one and one, then random values of every byte count up to it.</summary>
    private static List<BigInteger> Values(int width, Random random)
    {
        BigInteger most = (BigInteger.One << ((8 * width) - 1)) - 1;
        BigInteger least = -(BigInteger.One << ((8 * width) - 1));
        List<BigInteger> values = [most, least, BigInteger.Zero, BigInteger.MinusOne, BigInteger.One];
        for (int i = 0; i < 61; i++)
        {
            byte[] bytes = new byte[random.Next(1, width + 1)];
            random.NextBytes(bytes);
            values.Add(new BigInteger(bytes, isUnsigned: false, isBigEndian: true));
        }

        return values;
    }

    /// <summary><paramref name="value"/> as two's complement of <paramref name="length"/> bytes, its sign repeated over those it does not fill.</summary>
    private static void Write(BigInteger value, int length, Span<byte> into, bool bigEndian)
    {
        into.Fill(value.Sign < 0 ? (byte)0xFF : (byte)0);
        byte[] bytes = value.ToByteArray(isUnsigned: false, isBigEndian: bigEndian);
        Assert.True(bytes.Length <= length);
        bytes.CopyTo(bigEndian ? into[(length - bytes.Length)..] : into);
    }
}
