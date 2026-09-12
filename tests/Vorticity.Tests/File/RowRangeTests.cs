// RowRange lives in the root Vorticity namespace (PHASE1-CONTRACTS.md §11.1, §15.5) and is
// consumed by both the layout readers and the scan, so its edges are worth pinning: it is long-
// based because a file may hold more rows than an int can address, and an inverted or negative
// range is a CALLER error and therefore Argument*, never VortexFormatException (§1.4).
using System;
using Xunit;

namespace Vorticity.Tests.File;

public sealed class RowRangeTests
{
    [Fact]
    public void EmptyIsTheCanonicalZeroRange()
    {
        RowRange empty = RowRange.Empty;
        Assert.Equal(0, empty.Start);
        Assert.Equal(0, empty.End);
        Assert.Equal(0, empty.Length);
        Assert.True(empty.IsEmpty);
        Assert.Equal(default, empty);
    }

    [Theory]
    [InlineData(0L, 0L, 0L)]
    [InlineData(0L, 1L, 1L)]
    [InlineData(1023L, 1024L, 1L)]
    [InlineData(0L, 8192L, 8192L)]
    [InlineData(8191L, 8193L, 2L)]
    [InlineData(0L, long.MaxValue, long.MaxValue)]
    public void BoundsAndLengthRoundTrip(long start, long end, long length)
    {
        RowRange range = new RowRange(start, end);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
        Assert.Equal(length, range.Length);
        Assert.Equal(length == 0, range.IsEmpty);
    }

    [Fact]
    public void ANegativeOrInvertedRangeIsACallerError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowRange(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowRange(long.MinValue, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RowRange(10, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => RowRange.FromLength(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RowRange.FromLength(0, -1));
    }

    [Fact]
    public void FromLengthRejectsAnOverflowingSum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RowRange.FromLength(long.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RowRange.FromLength(1, long.MaxValue));

        // Exactly at the boundary is legal.
        RowRange whole = RowRange.FromLength(0, long.MaxValue);
        Assert.Equal(long.MaxValue, whole.Length);
    }

    [Theory]
    [InlineData(0L, 10L, 5L, 15L, 5L, 10L)]
    [InlineData(0L, 10L, 0L, 10L, 0L, 10L)]
    [InlineData(0L, 10L, 2L, 3L, 2L, 3L)]
    [InlineData(5L, 10L, 0L, 5L, 0L, 0L)]
    [InlineData(0L, 5L, 5L, 10L, 0L, 0L)]
    [InlineData(0L, 5L, 20L, 30L, 0L, 0L)]
    public void IntersectMeetsOrCollapsesToTheCanonicalEmpty(
        long aStart, long aEnd, long bStart, long bEnd, long expectedStart, long expectedEnd)
    {
        RowRange a = new RowRange(aStart, aEnd);
        RowRange b = new RowRange(bStart, bEnd);
        RowRange expected = new RowRange(expectedStart, expectedEnd);

        Assert.Equal(expected, a.Intersect(b));

        // Intersection is commutative.
        Assert.Equal(expected, b.Intersect(a));
    }

    [Fact]
    public void IntersectingWithEmptyIsEmpty()
    {
        RowRange range = new RowRange(1024, 8192);
        Assert.Equal(RowRange.Empty, range.Intersect(RowRange.Empty));
        Assert.Equal(RowRange.Empty, RowRange.Empty.Intersect(range));
    }

    [Fact]
    public void ContainsIsHalfOpen()
    {
        RowRange range = new RowRange(1024, 1025);
        Assert.False(range.Contains(1023));
        Assert.True(range.Contains(1024));
        Assert.False(range.Contains(1025));
        Assert.False(range.Contains(-1));
        Assert.False(RowRange.Empty.Contains(0));
    }

    [Fact]
    public void EqualityIsStructuralAndHashesAgree()
    {
        RowRange a = new RowRange(8191, 8193);
        RowRange b = RowRange.FromLength(8191, 2);
        RowRange c = new RowRange(8191, 8192);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals(c));
        Assert.False(a.Equals("not a range"));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ToStringIsCultureInvariant()
    {
        Assert.Equal("[1024, 8192)", new RowRange(1024, 8192).ToString());
        Assert.Equal("[0, 0)", RowRange.Empty.ToString());
        Assert.Equal("[0, 9223372036854775807)", RowRange.FromLength(0, long.MaxValue).ToString());
    }

    [Fact]
    public void RowRangeAddressesMoreRowsThanAnIntCan()
    {
        // docs/03-architecture.md §3.4: System.Range is int-based and cannot address a file of
        // three billion rows.
        RowRange range = RowRange.FromLength(3_000_000_000L, 1_000_000_000L);
        Assert.Equal(4_000_000_000L, range.End);
        Assert.True(range.Contains(3_500_000_000L));
    }
}
