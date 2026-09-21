// Zero-copy reinterpretation of struct and scalar vectors: Footer.segment_specs (16-byte
// SegmentSpec), Array.buffers (8-byte Buffer), ArrayNode.buffers ([uint16]) and Layout.segments
// ([uint32]) are all read this way with no traversal and no allocation.
using System;
using System.Runtime.CompilerServices;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Xunit;
using static Vorticity.Tests.Serialization.FlatBuffers.TestFlatBuffers;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferStructVectorTests
{
    [Fact]
    public void SegmentSpec_is_sixteen_bytes_with_eight_byte_alignment()
    {
        // The whole zero-copy story for Footer.segment_specs rests on this layout matching the
        // SegmentSpec struct of the footer schema, so assert it rather than assume it.
        Assert.Equal(16, Unsafe.SizeOf<SegmentSpecLike>());
        Assert.Equal(8, FlatBufferAccess.AlignmentOf<SegmentSpecLike>());
        Assert.Equal(1, FlatBufferAccess.AlignmentOf<byte>());
        Assert.Equal(2, FlatBufferAccess.AlignmentOf<ushort>());
        Assert.Equal(4, FlatBufferAccess.AlignmentOf<uint>());
        Assert.Equal(8, FlatBufferAccess.AlignmentOf<ulong>());
    }

    [Fact]
    public void Reinterprets_a_struct_vector_in_place()
    {
        using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(StructVectors);
        FlatBufferTable table = FlatBufferTable.Root(buffer.Span);

        ReadOnlySpan<SegmentSpecLike> segments = table.GetStructVector<SegmentSpecLike>(0);

        Assert.Equal(2, segments.Length);
        Assert.Equal(4096UL, segments[0].Offset);
        Assert.Equal(256u, segments[0].Length);
        Assert.Equal((byte)6, segments[0].AlignmentExponent);
        Assert.Equal((byte)0, segments[0].Compression);
        Assert.Equal((ushort)0, segments[0].Encryption);
        Assert.Equal(8192UL, segments[1].Offset);
        Assert.Equal(512u, segments[1].Length);
        Assert.Equal((byte)3, segments[1].AlignmentExponent);
        Assert.Equal((byte)1, segments[1].Compression);
        Assert.Equal((ushort)2, segments[1].Encryption);
    }

    [Fact]
    public void Reinterprets_scalar_vectors_in_place()
    {
        using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(StructVectors);
        FlatBufferTable table = FlatBufferTable.Root(buffer.Span);

        ReadOnlySpan<ushort> expectedBuffers = [10, 20, 30];
        ReadOnlySpan<uint> expectedSegments = [70000, 80000];
        Assert.True(table.GetStructVector<ushort>(1).SequenceEqual(expectedBuffers));
        Assert.True(table.GetStructVector<uint>(2).SequenceEqual(expectedSegments));
    }

    [Fact]
    public void Reinterpretation_returns_the_buffer_bytes_themselves()
    {
        // "Actual zero-copy" is a performance invariant of the reader: the span
        // handed back must alias the source buffer, not a copy of it.
        using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(StructVectors);
        FlatBufferTable table = FlatBufferTable.Root(buffer.Span);

        ReadOnlySpan<SegmentSpecLike> segments = table.GetStructVector<SegmentSpecLike>(0);

        Assert.True(Unsafe.AreSame(
            ref Unsafe.As<SegmentSpecLike, byte>(ref Unsafe.AsRef(in segments[0])),
            ref Unsafe.AsRef(in buffer.Span[40])));
    }

    [Fact]
    public void An_absent_struct_vector_is_empty()
    {
        using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(StructVectors);
        FlatBufferTable table = FlatBufferTable.Root(buffer.Span);

        Assert.True(table.GetStructVector<SegmentSpecLike>(3).IsEmpty);
        Assert.True(table.TryGetStructVector(3, out ReadOnlySpan<SegmentSpecLike> absent));
        Assert.True(absent.IsEmpty);
    }

    [Fact]
    public void Misaligned_elements_are_refused_rather_than_copied()
    {
        // The caller relies on zero-copy, so a silent fallback to a copy would be worse than an
        // error. GetStructVector throws; TryGetStructVector reports it.
        using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(MisalignedStructVector);
        FlatBufferTable table = FlatBufferTable.Root(buffer.Span);

        Assert.False(table.TryGetStructVector(0, out ReadOnlySpan<SegmentSpecLike> segments));
        Assert.True(segments.IsEmpty);

        try
        {
            _ = table.GetStructVector<SegmentSpecLike>(0).Length;
            Assert.Fail("A misaligned struct vector must not be reinterpreted in place.");
        }
        catch (VortexFormatException)
        {
        }
    }

    [Fact]
    public void A_one_byte_element_type_never_needs_alignment()
    {
        // Alignment 1: the same misaligned bytes are perfectly readable as [ubyte].
        using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(MisalignedStructVector);
        FlatBufferTable table = FlatBufferTable.Root(buffer.Span);

        Assert.True(table.TryGetStructVector(0, out ReadOnlySpan<byte> bytes));
        Assert.Equal(1, bytes.Length);
    }

    [Fact]
    public void Element_count_times_element_size_cannot_overflow_into_range()
    {
        // [36] is the SegmentSpec vector's count. 0xFFFFFFFF * 16 == 68_719_476_720, which
        // overflows a 32-bit product; computed in 64 bits it is unambiguously out of range.
        byte[] bytes = With(StructVectors, 36, 0xFF, 0xFF, 0xFF, 0xFF);

        Assert.Throws<VortexFormatException>(() =>
        {
            using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(bytes);
            _ = FlatBufferTable.Root(buffer.Span).GetStructVector<SegmentSpecLike>(0).Length;
        });
    }

    [Theory]
    [InlineData(3, false)]           // 40 + 3*16 == 88 <= 96: in range, however odd it looks
    [InlineData(4, true)]            // 40 + 4*16 == 104 > 96: out of range
    [InlineData(0x10000000, true)]   // count*16 == 2^32 exactly, i.e. 0 in a 32-bit product
    public void Element_count_is_bounded_by_the_real_buffer(int count, bool rejected)
    {
        byte[] bytes = With(StructVectors, 36,
            (byte)count, (byte)(count >> 8), (byte)(count >> 16), (byte)(count >> 24));

        if (rejected)
        {
            Assert.Throws<VortexFormatException>(() =>
            {
                using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(bytes);
                _ = FlatBufferTable.Root(buffer.Span).GetStructVector<SegmentSpecLike>(0).Length;
            });
        }
        else
        {
            using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(bytes);
            Assert.Equal(
                (int)count,
                FlatBufferTable.Root(buffer.Span).GetStructVector<SegmentSpecLike>(0).Length);
        }
    }

    [Fact]
    public void Reads_an_inline_struct_field()
    {
        FlatBufferTable table = FlatBufferTable.Root(InlineStruct);

        Assert.True(table.TryGetStruct(0, out SegmentSpecLike value));
        Assert.Equal(0xDEADUL, value.Offset);
        Assert.Equal(77u, value.Length);
        Assert.Equal((byte)4, value.AlignmentExponent);
        Assert.Equal((byte)2, value.Compression);
        Assert.Equal((ushort)9, value.Encryption);
    }

    [Fact]
    public void An_absent_inline_struct_reports_false()
    {
        FlatBufferTable table = FlatBufferTable.Root(InlineStruct);

        Assert.False(table.TryGetStruct(1, out SegmentSpecLike value));
        Assert.Equal(0UL, value.Offset);
    }

    [Fact]
    public void An_inline_struct_that_escapes_the_table_body_is_rejected()
    {
        // [6] is table_size. 16 says the 16-byte struct at slot 4 would end at 20 > 16.
        byte[] bytes = With(InlineStruct, 6, 0x10, 0x00);

        Assert.Throws<VortexFormatException>(() =>
        {
            FlatBufferTable table = FlatBufferTable.Root(bytes);
            _ = table.TryGetStruct(0, out SegmentSpecLike _);
        });
    }

    [Fact]
    public void An_eight_byte_struct_with_four_byte_alignment_is_not_over_required()
    {
        // The array schema's `struct Buffer` is 8 bytes but only 4-byte aligned. These are
        // exactly the bytes that are misaligned for a 16-byte SegmentSpec: as a Buffer vector they
        // are perfectly legal, and demanding sizeof(T) instead of alignof(T) would reject them.
        Assert.Equal(8, Unsafe.SizeOf<BufferLike>());
        Assert.Equal(4, FlatBufferAccess.AlignmentOf<BufferLike>());

        using AlignedTestBuffer buffer = AlignedTestBuffer.Copy(MisalignedStructVector);
        FlatBufferTable table = FlatBufferTable.Root(buffer.Span);

        ReadOnlySpan<BufferLike> buffers = table.GetStructVector<BufferLike>(0);

        Assert.Equal(1, buffers.Length);
        Assert.Equal((ushort)1, buffers[0].Padding);
    }
}
