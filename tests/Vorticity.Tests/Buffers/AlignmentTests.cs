using System;
using Vorticity;
using Vorticity.Buffers;
using Xunit;

namespace Vorticity.Tests.Buffers;

public sealed class AlignmentTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(63, false)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    [InlineData(1 << 30, true)]
    [InlineData(-1, false)]
    [InlineData(-64, false)]
    // int.MinValue is the trap: 0x80000000 & 0x7FFFFFFF == 0, so the bare x & (x - 1) test calls
    // it a power of two. The sign check is what makes it false.
    [InlineData(int.MinValue, false)]
    public void IsPowerOfTwo_classifies_boundary_values(int value, bool expected) =>
        Assert.Equal(expected, Alignment.IsPowerOfTwo(value));

    [Theory]
    [InlineData(0L, 1, 0L)]
    [InlineData(0L, 64, 0L)]
    [InlineData(1L, 64, 64L)]
    [InlineData(63L, 64, 64L)]
    [InlineData(64L, 64, 64L)]
    [InlineData(65L, 64, 128L)]
    [InlineData(4096L, 4096, 4096L)]
    [InlineData(4097L, 4096, 8192L)]
    [InlineData(long.MaxValue, 1, long.MaxValue)]
    public void AlignUp_rounds_up(long value, int alignment, long expected) =>
        Assert.Equal(expected, Alignment.AlignUp(value, alignment));

    [Fact]
    public void AlignUp_accepts_the_largest_representable_aligned_position()
    {
        // long.MaxValue % 64 == 63, so MaxValue - 63 is the largest 64-aligned long. Rounding it
        // up must be the identity, not an overflow.
        Assert.Equal(long.MaxValue - 63, Alignment.AlignUp(long.MaxValue - 63, 64));
        Assert.Equal(long.MaxValue - 63, Alignment.AlignUp(long.MaxValue - 64, 64));
    }

    [Theory]
    [InlineData(long.MaxValue, 2)]
    [InlineData(long.MaxValue, 64)]
    // MaxValue - 62 needs 63 bytes of padding, which lands exactly one past MaxValue: the
    // unchecked `(v + a - 1) & ~(a - 1)` would wrap to a negative offset that then passes a naive
    // "offset < length" bounds check.
    [InlineData(long.MaxValue - 62, 64)]
    [InlineData(long.MaxValue - 1, 4)]
    public void AlignUp_throws_instead_of_wrapping_past_long_MaxValue(long value, int alignment) =>
        Assert.Throws<VortexFormatException>(() => Alignment.AlignUp(value, alignment));

    [Fact]
    public void AlignUp_does_not_wrap_where_the_naive_expression_would()
    {
        const long Value = long.MaxValue - 62;
        long naive = unchecked((Value + 63) & ~63L);
        Assert.True(naive < 0, "the naive expression is expected to wrap negative here");
        Assert.Throws<VortexFormatException>(() => Alignment.AlignUp(Value, 64));
    }

    [Theory]
    [InlineData(0L, 64, 0L)]
    [InlineData(1L, 64, 0L)]
    [InlineData(63L, 64, 0L)]
    [InlineData(64L, 64, 64L)]
    [InlineData(127L, 64, 64L)]
    [InlineData(128L, 64, 128L)]
    [InlineData(long.MaxValue, 64, long.MaxValue - 63)]
    [InlineData(long.MaxValue, 1, long.MaxValue)]
    public void AlignDown_rounds_down(long value, int alignment, long expected) =>
        Assert.Equal(expected, Alignment.AlignDown(value, alignment));

    [Theory]
    [InlineData(0L, 64, 0)]
    [InlineData(1L, 64, 63)]
    [InlineData(63L, 64, 1)]
    [InlineData(64L, 64, 0)]
    [InlineData(65L, 64, 63)]
    [InlineData(7L, 1, 0)]
    public void PaddingTo_matches_AlignUp(long position, int alignment, int expected)
    {
        Assert.Equal(expected, Alignment.PaddingTo(position, alignment));
        Assert.Equal(
            Alignment.AlignUp(position, alignment) - position,
            Alignment.PaddingTo(position, alignment));
    }

    [Fact]
    public void PaddingTo_throws_where_AlignUp_would_overflow() =>
        Assert.Throws<VortexFormatException>(() => Alignment.PaddingTo(long.MaxValue, 64));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-8)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Every_helper_rejects_a_non_power_of_two_alignment(int alignment)
    {
        Assert.Throws<VortexFormatException>(() => Alignment.AlignUp(0, alignment));
        Assert.Throws<VortexFormatException>(() => Alignment.AlignDown(0, alignment));
        Assert.Throws<VortexFormatException>(() => Alignment.PaddingTo(0, alignment));
        Assert.Throws<VortexFormatException>(() => IsAlignedNull(alignment));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(-64L)]
    [InlineData(long.MinValue)]
    public void Rounding_rejects_a_negative_position(long value)
    {
        Assert.Throws<VortexFormatException>(() => Alignment.AlignUp(value, 64));
        Assert.Throws<VortexFormatException>(() => Alignment.AlignDown(value, 64));
        Assert.Throws<VortexFormatException>(() => Alignment.PaddingTo(value, 64));
    }

    [Fact]
    public unsafe void IsAligned_reads_the_real_address()
    {
        using NativeSegmentOwner owner = NativeSegmentOwner.Allocate(256, 64);
        fixed (byte* origin = owner.WritableSpan)
        {
            Assert.True(Alignment.IsAligned(origin, 64));
            Assert.True(Alignment.IsAligned(origin, 1));
            Assert.False(Alignment.IsAligned(origin + 1, 2));
            Assert.False(Alignment.IsAligned(origin + 8, 16));
            Assert.True(Alignment.IsAligned(origin + 8, 8));
            Assert.True(Alignment.IsAligned(origin + 64, 64));
        }
    }

    [Fact]
    public unsafe void IsAligned_treats_null_as_aligned_to_everything()
    {
        Assert.True(Alignment.IsAligned(null, 64));
        Assert.True(Alignment.IsAligned(null, 1));
    }

    /// <summary>
    /// Coalescing versus alignment: if a
    /// coalesced read starts at <c>start &amp; ~63</c> and a segment's file offset <c>o</c> is a
    /// multiple of <c>2^k</c> with <c>k &lt;= 6</c>, then the segment's offset inside the buffer,
    /// <c>o - alignedStart</c>, is still a multiple of <c>2^k</c>. Alignment survives coalescing.
    /// </summary>
    [Fact]
    public void Coalescing_preserves_every_segment_alignment_up_to_the_cap()
    {
        long[] starts = [0, 1, 63, 64, 65, 4095, 4096, 1_000_003, (1L << 40) + 37];

        for (int i = 0; i < starts.Length; i++)
        {
            long alignedStart = Alignment.AlignDown(starts[i], VortexLimits.MaxAlignment);
            Assert.Equal(0, alignedStart % VortexLimits.MaxAlignment);
            Assert.True(alignedStart <= starts[i]);

            for (int k = 0; k <= VortexLimits.MaxAlignmentExponent; k++)
            {
                int segmentAlignment = 1 << k;

                // The writer guarantees each segment offset is a multiple of its own alignment.
                long segmentOffset = Alignment.AlignUp(starts[i], segmentAlignment);
                long offsetInBuffer = segmentOffset - alignedStart;

                Assert.True(offsetInBuffer >= 0);
                Assert.Equal(0, offsetInBuffer % segmentAlignment);
            }
        }
    }

    private static unsafe bool IsAlignedNull(int alignment) => Alignment.IsAligned(null, alignment);
}
