// Adversarial tests for Int256: byte-length rejection, both endiannesses, the MinValue traps,
// ordering across the sign boundary, and every narrowing conversion at its exact boundary.
using System;
using System.Globalization;
using System.Numerics;
using Vorticity;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Types.Numerics;

public sealed class Int256Tests
{
    internal const string MinValueText =
        "-57896044618658097711785492504343953926634992332820282019728792003956564819968";

    internal const string MaxValueText =
        "57896044618658097711785492504343953926634992332820282019728792003956564819967";

    internal static byte[] Hex(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>The independent oracle: BigInteger is banned in src, allowed in tests.</summary>
    internal static BigInteger ToBig(Int256 value)
    {
        Span<byte> bytes = stackalloc byte[Int256.ByteCount];
        value.WriteLittleEndianBytes(bytes);
        return new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
    }

    internal static Int256 FromBig(BigInteger value)
    {
        Span<byte> bytes = stackalloc byte[Int256.ByteCount];
        bool ok = value.TryWriteBytes(bytes, out int written, isUnsigned: false, isBigEndian: false);
        Assert.True(ok);
        // Sign-extend the remainder.
        byte fill = value.Sign < 0 ? (byte)0xFF : (byte)0x00;
        for (int i = written; i < Int256.ByteCount; i++)
        {
            bytes[i] = fill;
        }

        return Int256.FromLittleEndianBytes(bytes);
    }

    [Fact]
    public void ByteCountIsThirtyTwo() => Assert.Equal(32, Int256.ByteCount);

    [Fact]
    public void ZeroOneMinMaxHaveTheExpectedBits()
    {
        Assert.True(Int256.Zero.IsZero);
        Assert.Equal(0, Int256.Zero.Sign);
        Assert.False(Int256.Zero.IsNegative);

        Assert.Equal(1, Int256.One.Sign);
        Assert.True(Int256.One.TryToInt64(out long one));
        Assert.Equal(1L, one);

        Assert.True(Int256.MinValue.IsNegative);
        Assert.Equal(-1, Int256.MinValue.Sign);
        Assert.False(Int256.MaxValue.IsNegative);
        Assert.Equal(1, Int256.MaxValue.Sign);
        Assert.True(Int256.MinValue < Int256.MaxValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void WrongByteCountIsRejected(int length)
    {
        byte[] bytes = new byte[length];
        Assert.Throws<VortexFormatException>(() => { _ = Int256.FromLittleEndianBytes(bytes); });
        Assert.Throws<VortexFormatException>(() => { _ = Int256.FromBigEndianBytes(bytes); });
        Assert.False(Int256.TryFromLittleEndianBytes(bytes, out Int256 le));
        Assert.True(le.IsZero);
        Assert.False(Int256.TryFromBigEndianBytes(bytes, out Int256 be));
        Assert.True(be.IsZero);
    }

    [Fact]
    public void ShortDestinationThrowsArgumentException()
    {
        byte[] tooShort = new byte[31];
        Assert.Throws<ArgumentException>(() => Int256.One.WriteLittleEndianBytes(tooShort));
        Assert.Throws<ArgumentException>(() => Int256.One.WriteBigEndianBytes(tooShort));
        byte[] empty = Array.Empty<byte>();
        Assert.Throws<ArgumentException>(() => Int256.One.WriteLittleEndianBytes(empty));
        Assert.Throws<ArgumentException>(() => Int256.One.WriteBigEndianBytes(empty));
    }

    [Fact]
    public void WriteDoesNotTouchBytesPastThirtyTwo()
    {
        byte[] destination = new byte[40];
        destination.AsSpan().Fill(0xAB);
        Int256.MaxValue.WriteLittleEndianBytes(destination);
        for (int i = 32; i < destination.Length; i++)
        {
            Assert.Equal((byte)0xAB, destination[i]);
        }

        destination.AsSpan().Fill(0xAB);
        Int256.MaxValue.WriteBigEndianBytes(destination);
        for (int i = 32; i < destination.Length; i++)
        {
            Assert.Equal((byte)0xAB, destination[i]);
        }
    }

    [Fact]
    public void MinValueRoundTripsThroughBothEndiannesses()
    {
        Span<byte> le = stackalloc byte[32];
        Span<byte> be = stackalloc byte[32];
        Int256.MinValue.WriteLittleEndianBytes(le);
        Int256.MinValue.WriteBigEndianBytes(be);

        Assert.Equal(Hex("0000000000000000000000000000000000000000000000000000000000000080"), le.ToArray());
        Assert.Equal(Hex("8000000000000000000000000000000000000000000000000000000000000000"), be.ToArray());
        Assert.Equal(Int256.MinValue, Int256.FromLittleEndianBytes(le));
        Assert.Equal(Int256.MinValue, Int256.FromBigEndianBytes(be));
    }

    [Fact]
    public void MaxValueRoundTripsThroughBothEndiannesses()
    {
        Span<byte> le = stackalloc byte[32];
        Span<byte> be = stackalloc byte[32];
        Int256.MaxValue.WriteLittleEndianBytes(le);
        Int256.MaxValue.WriteBigEndianBytes(be);

        Assert.Equal(Hex("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f"), le.ToArray());
        Assert.Equal(Hex("7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), be.ToArray());
        Assert.Equal(Int256.MaxValue, Int256.FromLittleEndianBytes(le));
        Assert.Equal(Int256.MaxValue, Int256.FromBigEndianBytes(be));
    }

    [Fact]
    public void BigAndLittleEndianAreNotTheSameFunction()
    {
        // A byte pattern whose reversal is a different value: the two readers must disagree.
        byte[] pattern = Hex("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
        Int256 le = Int256.FromLittleEndianBytes(pattern);
        Int256 be = Int256.FromBigEndianBytes(pattern);
        Assert.NotEqual(le, be);

        Span<byte> roundTrip = stackalloc byte[32];
        le.WriteLittleEndianBytes(roundTrip);
        Assert.Equal(pattern, roundTrip.ToArray());
        be.WriteBigEndianBytes(roundTrip);
        Assert.Equal(pattern, roundTrip.ToArray());
    }

    [Fact]
    public void NegateMinValueThrowsAndDoesNotWrap()
    {
        Assert.Throws<OverflowException>(() => { _ = Int256.Negate(Int256.MinValue); });
        Assert.Throws<OverflowException>(() => { _ = -Int256.MinValue; });
    }

    [Fact]
    public void NegateRoundTrips()
    {
        Int256[] values =
        {
            Int256.Zero, Int256.One, Int256.MaxValue, new Int256(long.MinValue),
            new Int256(Int128.MaxValue), Int256.FromUInt64(ulong.MaxValue),
        };

        foreach (Int256 value in values)
        {
            Int256 negated = Int256.Negate(value);
            Assert.Equal(-ToBig(value), ToBig(negated));
            Assert.Equal(value, Int256.Negate(negated));
        }
    }

    [Fact]
    public void SignExtensionFromLongAndInt128()
    {
        Assert.Equal(new BigInteger(long.MinValue), ToBig(new Int256(long.MinValue)));
        Assert.Equal(new BigInteger(long.MaxValue), ToBig(new Int256(long.MaxValue)));
        Assert.Equal(new BigInteger(-1L), ToBig(new Int256(-1L)));
        Assert.Equal(BigInteger.Zero, ToBig(new Int256(0L)));

        Assert.Equal((BigInteger)Int128.MinValue, ToBig(new Int256(Int128.MinValue)));
        Assert.Equal((BigInteger)Int128.MaxValue, ToBig(new Int256(Int128.MaxValue)));
        Assert.Equal((BigInteger)(-1), ToBig(new Int256((Int128)(-1))));

        Assert.Equal((BigInteger)ulong.MaxValue, ToBig(Int256.FromUInt64(ulong.MaxValue)));
        Assert.False(Int256.FromUInt64(ulong.MaxValue).IsNegative);
    }

    [Fact]
    public void FromLimbsPlacesTheSignBitInHi()
    {
        Int256 negativeOne = Int256.FromLimbs(
            ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
        Assert.True(negativeOne.IsNegative);
        Assert.Equal(new BigInteger(-1), ToBig(negativeOne));

        Int256 minValue = Int256.FromLimbs(0, 0, 0, 0x8000_0000_0000_0000UL);
        Assert.Equal(Int256.MinValue, minValue);
    }

    [Fact]
    public void CompareAcrossTheSignBoundaryAndBetweenNegatives()
    {
        Int256 minusTwo = new Int256(-2L);
        Int256 minusOne = new Int256(-1L);
        Int256 zero = Int256.Zero;
        Int256 one = Int256.One;

        Assert.True(Int256.MinValue < minusTwo);
        Assert.True(minusTwo < minusOne);
        Assert.True(minusOne < zero);
        Assert.True(zero < one);
        Assert.True(one < Int256.MaxValue);

        Assert.True(minusOne > Int256.MinValue);
        Int256 minusOneAgain = new Int256(-1L);
        Assert.True(minusOne >= minusOneAgain);
        Assert.True(minusOne <= minusOneAgain);
        Assert.False(minusOne < minusOneAgain);
        Assert.False(minusOne > minusOneAgain);

        // A large-magnitude negative must sort below a small-magnitude negative even though its
        // unsigned limb pattern is smaller.
        Int256 bigNegative = Int256.FromLimbs(0, 0, 0, 0x8000_0000_0000_0001UL);
        Assert.True(bigNegative < minusOne);
        Assert.True(bigNegative > Int256.MinValue);
    }

    [Fact]
    public void EqualityAndHashCodeAgree()
    {
        Int256 a = Int256.FromLimbs(1, 2, 3, 4);
        Int256 b = Int256.FromLimbs(1, 2, 3, 4);
        Int256 c = Int256.FromLimbs(1, 2, 3, 5);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals((object)c));
        Assert.False(a.Equals(null));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(0, a.CompareTo(b));
        Assert.True(a != c);
    }

    [Fact]
    public void HashCodeDistinguishesLimbPermutations()
    {
        // A hash that just XORed the limbs would collide on these.
        Assert.NotEqual(
            Int256.FromLimbs(1, 2, 0, 0).GetHashCode(),
            Int256.FromLimbs(2, 1, 0, 0).GetHashCode());
    }

    [Fact]
    public void TryToInt128AtTheBoundaries()
    {
        Assert.True(new Int256(Int128.MaxValue).TryToInt128(out Int128 max));
        Assert.Equal(Int128.MaxValue, max);
        Assert.True(new Int256(Int128.MinValue).TryToInt128(out Int128 min));
        Assert.Equal(Int128.MinValue, min);

        // One past Int128.MaxValue: 2^127.
        Int256 justOver = Int256.FromLimbs(0, 0x8000_0000_0000_0000UL, 0, 0);
        Assert.False(justOver.TryToInt128(out Int128 over));
        Assert.Equal(Int128.Zero, over);

        // One below Int128.MinValue: -(2^127) - 1.
        Int256 justUnder = FromBig((BigInteger)Int128.MinValue - 1);
        Assert.False(justUnder.TryToInt128(out _));

        Assert.False(Int256.MinValue.TryToInt128(out _));
        Assert.False(Int256.MaxValue.TryToInt128(out _));
    }

    [Fact]
    public void TryToInt64AtTheBoundaries()
    {
        Assert.True(new Int256(long.MaxValue).TryToInt64(out long max));
        Assert.Equal(long.MaxValue, max);
        Assert.True(new Int256(long.MinValue).TryToInt64(out long min));
        Assert.Equal(long.MinValue, min);

        Assert.False(Int256.FromUInt64((ulong)long.MaxValue + 1).TryToInt64(out long over));
        Assert.Equal(0L, over);
        Assert.False(FromBig(new BigInteger(long.MinValue) - 1).TryToInt64(out _));
    }

    [Fact]
    public void TryToUInt64AtTheBoundaries()
    {
        Assert.True(Int256.FromUInt64(ulong.MaxValue).TryToUInt64(out ulong max));
        Assert.Equal(ulong.MaxValue, max);
        Assert.True(Int256.Zero.TryToUInt64(out ulong zero));
        Assert.Equal(0UL, zero);

        Assert.False(new Int256(-1L).TryToUInt64(out ulong negative));
        Assert.Equal(0UL, negative);
        Assert.False(FromBig(BigInteger.Pow(2, 64)).TryToUInt64(out _));
    }

    [Fact]
    public void TryToDecimalAtTheBoundaries()
    {
        Int256 maxUnscaled = FromBig(new BigInteger(decimal.MaxValue));
        Assert.True(maxUnscaled.TryToDecimal(out decimal max));
        Assert.Equal(decimal.MaxValue, max);

        Int256 minUnscaled = FromBig(new BigInteger(decimal.MinValue));
        Assert.True(minUnscaled.TryToDecimal(out decimal min));
        Assert.Equal(decimal.MinValue, min);

        Assert.False(FromBig(new BigInteger(decimal.MaxValue) + 1).TryToDecimal(out decimal over));
        Assert.Equal(0m, over);
        Assert.False(FromBig(new BigInteger(decimal.MinValue) - 1).TryToDecimal(out _));
        Assert.False(Int256.MinValue.TryToDecimal(out _));
        Assert.False(Int256.MaxValue.TryToDecimal(out _));

        Assert.True(Int256.Zero.TryToDecimal(out decimal zero));
        Assert.Equal(0m, zero);
        Assert.True(new Int256(-1L).TryToDecimal(out decimal negativeOne));
        Assert.Equal(-1m, negativeOne);
    }
}
