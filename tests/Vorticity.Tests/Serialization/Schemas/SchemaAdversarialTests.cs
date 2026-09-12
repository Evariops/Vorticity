// Hand-assembled buffers for the rules no builder can violate, plus the degenerate inputs every
// Root must survive. Nothing but VortexFormatException may ever escape.
using System;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Schemas;
using Xunit;

namespace Vorticity.Tests.Serialization.Schemas;

public sealed class SchemaAdversarialTests
{
    private static int Budget => VortexLimits.MaxFlatBufferTables;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_root_rejects_a_buffer_too_short_to_hold_one(int length)
    {
        byte[] bytes = new byte[length];

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = PostscriptView.Root(bytes, ref budget).MetadataCount;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = FooterView.Root(bytes, ref budget).ArraySpecCount;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = FileStatisticsView.Root(bytes, ref budget).FieldStatsCount;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = ArrayView.Root(bytes, ref budget).Buffers.Length;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = LayoutView.Root(bytes, ref budget).RowCount;
        });
    }

    [Fact]
    public void A_segment_spec_vector_can_never_have_a_byte_length_off_a_multiple_of_sixteen()
    {
        // The contract asks for this case; it is structurally unreachable and the reason is worth
        // recording. A FlatBuffers vector stores an ELEMENT count, not a byte count, and
        // GetStructVector multiplies it by sizeof(T) in 64 bits, so the byte length is 16 * n by
        // construction. What a hostile file can do instead is claim an n whose 16 * n escapes the
        // buffer, which is the case asserted here - including the 64 GiB u32 maximum, which must be
        // rejected rather than wrapped.
        foreach (uint count in new uint[] { 2u, 3u, 1000u, 0x1000_0000u, uint.MaxValue })
        {
            byte[] bytes = RawFooterWithSegmentCount(count);
            Assert.Throws<VortexFormatException>(() =>
            {
                int budget = Budget;
                _ = FooterView.Root(bytes, ref budget).SegmentSpecs.Length;
            });
        }

        // One element really is there, so the shape itself is sound.
        byte[] valid = RawFooterWithSegmentCount(1);
        int b = Budget;
        Assert.Equal(1, FooterView.Root(valid, ref b).SegmentSpecs.Length);
    }

    [Fact]
    public void A_misaligned_buffer_vector_is_rejected()
    {
        // `struct Buffer` is 4-aligned - its widest member is a uint32 - so elements at 2 mod 4
        // cannot be reinterpreted in place.
        byte[] bytes = RawArrayWithBufferVector(elementsAtMultipleOfFour: false);

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = ArrayView.Root(bytes, ref budget).Buffers.Length;
        });
    }

    [Fact]
    public void An_aligned_hand_built_buffer_vector_is_accepted()
    {
        byte[] bytes = RawArrayWithBufferVector(elementsAtMultipleOfFour: true);

        int budget = Budget;
        ReadOnlySpan<BufferSpec> buffers = ArrayView.Root(bytes, ref budget).Buffers;
        Assert.Equal(1, buffers.Length);
        Assert.Equal((ushort)0x2211, buffers[0].Padding);
        Assert.Equal((byte)0x33, buffers[0].AlignmentExponent);
        Assert.Equal((byte)0x01, buffers[0].Compression);
        Assert.Equal(0x44556677u, buffers[0].Length);
    }

    [Fact]
    public void A_spec_vector_whose_count_escapes_the_buffer_is_rejected()
    {
        // array_specs is a vector of uoffsets: a hostile count of uint.MaxValue is 16 GiB of them.
        RawBytes raw = new();
        raw.U32(16);            //  0.. 4  root -> table at 16
        raw.U16(6);             //  4.. 6  vtable_size (4 header + 1 slot)
        raw.U16(8);             //  6.. 8  table_size
        raw.U16(4);             //  8..10  slot 0: array_specs at table + 4
        raw.U16(0);             // 10..12  padding
        raw.U32(0);             // 12..16  padding
        raw.I32(12);            // 16..20  soffset -> vtable at 4
        raw.U32(4);             // 20..24  uoffset -> count at 24
        raw.U32(uint.MaxValue); // 24..28  count
        raw.U32(0);             // 28..32  one element's worth of bytes, and no more
        byte[] bytes = raw.ToArray();

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = FooterView.Root(bytes, ref budget).ArraySpecCount;
        });
    }

    [Fact]
    public void A_layout_segment_vector_whose_count_escapes_the_buffer_is_rejected()
    {
        // Layout.segments is [uint32]: uint.MaxValue elements is 16 GiB, and the multiply must be
        // done in 64 bits or it wraps into a range that looks valid.
        RawBytes raw = new();
        raw.U32(24);           //  0.. 4  root -> Layout table at 24
        raw.U16(14);           //  4.. 6  vtable_size
        raw.U16(8);            //  6.. 8  table_size: body is 24..32
        raw.U16(0);            //  8..10  encoding
        raw.U16(0);            // 10..12  row_count
        raw.U16(0);            // 12..14  metadata
        raw.U16(0);            // 14..16  children
        raw.U16(4);            // 16..18  segments at table + 4
        raw.U16(0);            // 18..20  padding
        raw.U32(0);            // 20..24  padding
        raw.I32(20);           // 24..28  soffset -> vtable at 4
        raw.U32(4);            // 28..32  uoffset -> count at 32
        raw.U32(uint.MaxValue);// 32..36  count
        raw.U32(0);            // 36..40  one element and no more
        byte[] bytes = raw.ToArray();

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = LayoutView.Root(bytes, ref budget).Segments.Length;
        });
    }

    [Fact]
    public void An_array_node_buffer_vector_whose_count_escapes_the_buffer_is_rejected()
    {
        // ArrayNode.buffers is [uint16], reached through the Array table's root field.
        RawBytes raw = new();
        raw.U32(24);            //  0.. 4  root -> Array table at 24
        raw.U16(8);             //  4.. 6  Array vtable at 4: 4 header + 2 slots
        raw.U16(8);             //  6.. 8  Array table_size: body is 24..32
        raw.U16(4);             //  8..10  slot 0: root at table + 4
        raw.U16(0);             // 10..12  slot 1: buffers absent
        raw.U16(12);            // 12..14  ArrayNode vtable at 12: 4 header + 4 slots
        raw.U16(8);             // 14..16  node table_size: body is 36..44
        raw.U16(0);             // 16..18  slot 0: encoding absent
        raw.U16(0);             // 18..20  slot 1: metadata absent
        raw.U16(0);             // 20..22  slot 2: children absent
        raw.U16(4);             // 22..24  slot 3: buffers at node + 4
        raw.I32(20);            // 24..28  Array soffset: 24 - 20 = vtable at 4
        raw.U32(8);             // 28..32  uoffset -> ArrayNode at 36
        raw.U32(0);             // 32..36  padding
        raw.I32(24);            // 36..40  node soffset: 36 - 24 = vtable at 12
        raw.U32(4);             // 40..44  uoffset -> count at 44
        raw.U32(uint.MaxValue); // 44..48  count of u16 elements
        raw.U16(0);             // 48..50  one element and no more
        raw.U16(0);
        byte[] bytes = raw.ToArray();

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = ArrayView.Root(bytes, ref budget).Root_.BufferIndices.Length;
        });
    }

    [Fact]
    public void A_vtable_slot_that_escapes_the_table_body_is_rejected()
    {
        // Layout.row_count is 8 bytes at slot 4 of an 8-byte table body: the read would run to
        // table + 12 and must be refused before it happens.
        RawBytes raw = new();
        raw.U32(16);            //  0.. 4  root -> Layout table at 16
        raw.U16(8);             //  4.. 6  vtable_size (4 header + 2 slots)
        raw.U16(8);             //  6.. 8  table_size: body is 16..24
        raw.U16(0);             //  8..10  slot 0: encoding absent
        raw.U16(4);             // 10..12  slot 1: row_count at table + 4
        raw.U32(0);             // 12..16  padding
        raw.I32(12);            // 16..20  soffset -> vtable at 4
        raw.U32(0);             // 20..24
        raw.U64(0);             // 24..32
        byte[] bytes = raw.ToArray();

        Assert.Throws<VortexFormatException>(() =>
        {
            int budget = Budget;
            _ = LayoutView.Root(bytes, ref budget).RowCount;
        });
    }

    [Fact]
    public void Writers_reject_a_caller_error_with_Argument_and_never_with_VortexFormatException()
    {
        // §1.4: file says it -> VortexFormatException; caller says it -> Argument*.
        using FlatBufferBuilder b = new();
        int segment = PostscriptWriter.WriteSegment(b, new SegmentSpec(1, 1, 0, 0, 0), CompressionScheme.None);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostscriptWriter.Write(b, 0, 0, 0, segment, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostscriptWriter.Write(b, 0, segment, 0, 0, default));
        Assert.Throws<ArgumentException>(() =>
            PostscriptWriter.WriteMetadata(b, default, segment));
        Assert.Throws<ArgumentException>(() =>
            PostscriptWriter.WriteMetadata(b, new byte[VortexLimits.MaxMetadataKeyLength + 1], segment));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostscriptWriter.WriteMetadata(b, "k"u8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ArrayWriter.Write(b, 0, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FooterWriter.Write(b, default, default, default, default, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FooterWriter.Write(
                b, default, default, default, new CompressionScheme[VortexLimits.MaxCompressionSpecs + 1], 0));
    }

    [Fact]
    public void Postscript_write_rejects_more_metadata_than_the_format_allows()
    {
        using FlatBufferBuilder b = new();
        int segment = PostscriptWriter.WriteSegment(b, new SegmentSpec(1, 1, 0, 0, 0), CompressionScheme.None);
        int[] entries = new int[VortexLimits.MaxMetadataSegments + 1];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = PostscriptWriter.WriteMetadata(
                b, System.Text.Encoding.UTF8.GetBytes("k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), segment);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostscriptWriter.Write(b, 0, segment, 0, segment, entries));
    }

    private static byte[] RawFooterWithSegmentCount(uint count)
    {
        RawBytes raw = new();
        raw.U32(16);        //  0.. 4  root -> table at 16
        raw.U16(10);        //  4.. 6  vtable_size (4 header + 3 slots)
        raw.U16(8);         //  6.. 8  table_size
        raw.U16(0);         //  8..10  array_specs absent
        raw.U16(0);         // 10..12  layout_specs absent
        raw.U16(4);         // 12..14  segment_specs at table + 4
        raw.U16(0);         // 14..16  padding
        raw.I32(12);        // 16..20  soffset -> vtable at 4
        raw.U32(8);         // 20..24  uoffset -> count at 28, elements at 32 (8-aligned)
        raw.U32(0);         // 24..28  filler
        raw.U32(count);     // 28..32  element count
        raw.U64(1);         // 32..40  exactly one SegmentSpec
        raw.U32(2);
        raw.U8(3);
        raw.U8(0);
        raw.U16(0);
        return raw.ToArray();
    }

    private static byte[] RawArrayWithBufferVector(bool elementsAtMultipleOfFour)
    {
        RawBytes raw = new();
        raw.U32(16);        //  0.. 4  root -> Array table at 16
        raw.U16(8);         //  4.. 6  vtable_size (4 header + 2 slots)
        raw.U16(8);         //  6.. 8  table_size
        raw.U16(0);         //  8..10  slot 0: root node absent
        raw.U16(4);         // 10..12  slot 1: buffers at table + 4
        raw.U32(0);         // 12..16  padding
        raw.I32(12);        // 16..20  soffset -> vtable at 4
        if (elementsAtMultipleOfFour)
        {
            raw.U32(8);     // 20..24  uoffset -> count at 28, elements at 32
            raw.U32(0);     // 24..28  filler
            raw.U32(1);     // 28..32  count
        }
        else
        {
            raw.U32(6);     // 20..24  uoffset -> count at 26, elements at 30 (2 mod 4)
            raw.U16(0);     // 24..26  filler
            raw.U32(1);     // 26..30  count
        }

        raw.U16(0x2211);    // one BufferSpec
        raw.U8(0x33);
        raw.U8(0x01);
        raw.U32(0x44556677);
        return raw.ToArray();
    }
}
