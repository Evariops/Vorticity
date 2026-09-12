// The two FlatBuffers `struct`s are read by reinterpretation, so their size and their field
// offsets ARE the wire format. docs/09-contracts.md §7 requires a static size assert; these tests
// pin the offsets too, because a reordered field would keep the size and silently swap two values.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Serialization.Schemas;

public sealed class InlineStructTests
{
    [Fact]
    public void SegmentSpec_is_exactly_sixteen_bytes()
    {
        Assert.Equal(16, Unsafe.SizeOf<SegmentSpec>());
    }

    [Fact]
    public void BufferSpec_is_exactly_eight_bytes()
    {
        Assert.Equal(8, Unsafe.SizeOf<BufferSpec>());
    }

    [Fact]
    public void SegmentSpec_field_bytes_match_the_wire_layout()
    {
        // offset 0..8, length 8..12, alignment_exponent 12..13, _compression 13..14,
        // _encryption 14..16 - spec/flatbuffers/footer.fbs.
        SegmentSpec spec = new(0x0102030405060708UL, 0x11223344u, 0x55, 0x66, 0x7788);
        Span<byte> bytes = stackalloc byte[16];
        MemoryMarshal.Write(bytes, in spec);

        Assert.Equal(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, bytes[..8].ToArray());
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, bytes[8..12].ToArray());
        Assert.Equal((byte)0x55, bytes[12]);
        Assert.Equal((byte)0x66, bytes[13]);
        Assert.Equal(new byte[] { 0x88, 0x77 }, bytes[14..16].ToArray());
    }

    [Fact]
    public void BufferSpec_field_bytes_match_the_wire_layout()
    {
        // padding 0..2, alignment_exponent 2..3, compression 3..4, length 4..8 - array.fbs.
        BufferSpec spec = new(0x1234, 0x56, (byte)BufferCompression.LZ4, 0x778899AAu);
        Span<byte> bytes = stackalloc byte[8];
        MemoryMarshal.Write(bytes, in spec);

        Assert.Equal(new byte[] { 0x34, 0x12 }, bytes[..2].ToArray());
        Assert.Equal((byte)0x56, bytes[2]);
        Assert.Equal((byte)1, bytes[3]);
        Assert.Equal(new byte[] { 0xAA, 0x99, 0x88, 0x77 }, bytes[4..8].ToArray());
    }

    [Fact]
    public void SegmentSpec_vectors_reinterpret_element_for_element()
    {
        SegmentSpec[] specs =
        [
            new(0, 0, 0, 0, 0),
            new(ulong.MaxValue, uint.MaxValue, byte.MaxValue, byte.MaxValue, ushort.MaxValue),
            new(64, 1024, 6, 1, 2),
        ];

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes<SegmentSpec>(specs);
        Assert.Equal(48, bytes.Length);

        ReadOnlySpan<SegmentSpec> back = MemoryMarshal.Cast<byte, SegmentSpec>(bytes);
        for (int i = 0; i < specs.Length; i++)
        {
            Assert.Equal(specs[i].Offset, back[i].Offset);
            Assert.Equal(specs[i].Length, back[i].Length);
            Assert.Equal(specs[i].AlignmentExponent, back[i].AlignmentExponent);
            Assert.Equal(specs[i].Compression, back[i].Compression);
            Assert.Equal(specs[i].Encryption, back[i].Encryption);
        }
    }

    [Fact]
    public void End_adds_offset_and_length()
    {
        Assert.Equal(0UL, new SegmentSpec(0, 0, 0, 0, 0).End);
        Assert.Equal(1088UL, new SegmentSpec(64, 1024, 6, 0, 0).End);
        Assert.Equal(ulong.MaxValue, new SegmentSpec(ulong.MaxValue - 1, 1, 0, 0, 0).End);
    }

    [Fact]
    public void End_rejects_a_u64_overflow()
    {
        SegmentSpec spec = new(ulong.MaxValue, 1, 0, 0, 0);
        Assert.Throws<VortexFormatException>(() => spec.End);
    }

    [Fact]
    public void End_rejects_the_widest_overflow()
    {
        SegmentSpec spec = new(ulong.MaxValue - 3, uint.MaxValue, 0, 0, 0);
        Assert.Throws<VortexFormatException>(() => spec.End);
    }
}
