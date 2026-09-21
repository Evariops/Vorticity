// A Vortex file is untrusted input and every accessor in the library goes through this reader, so
// a missed bounds check here is an out-of-bounds read in production. Each rule gets its own named
// test, and the sweeps at the bottom assert the global property: nothing but
// VortexFormatException ever escapes.
using System;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Xunit;
using static Vorticity.Tests.Serialization.FlatBuffers.TestFlatBuffers;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferMalformedTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Root_rejects_a_buffer_too_short_for_the_root_offset(int length)
    {
        byte[] bytes = new byte[length];

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).IsNull;
        });
    }

    [Fact]
    public void Root_rejects_a_four_byte_buffer_whatever_the_offset_says()
    {
        // Four bytes hold the root uoffset and nothing else, so no table can fit: 0 is rejected as
        // a null reference, and every non-zero value points past the end.
        foreach (uint offset in new uint[] { 0u, 1u, 4u, 8u, uint.MaxValue })
        {
            byte[] bytes = new byte[4];
            WriteUInt32(bytes, 0, offset);

            Assert.Throws<VortexFormatException>(() =>
            {
                _ = FlatBufferTable.Root(bytes).IsNull;
            });
        }
    }

    [Fact]
    public void Root_rejects_a_zero_uoffset()
    {
        // uoffsets are unsigned and point forward, so 0 is never a valid reference. [0] is the
        // root uoffset.
        byte[] bytes = With(Scalars, 0, 0x00, 0x00, 0x00, 0x00);

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).IsNull;
        });
    }

    [Theory]
    [InlineData(77u)]           // 77 + 4 > 80: no room for the table's own soffset
    [InlineData(80u)]           // exactly at the end
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]   // would wrap to -1 in 32-bit signed arithmetic
    public void Root_rejects_an_offset_outside_the_buffer(uint offset)
    {
        byte[] bytes = With(Scalars, 0,
            (byte)offset, (byte)(offset >> 8), (byte)(offset >> 16), (byte)(offset >> 24));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).IsNull;
        });
    }

    [Theory]
    [InlineData(33)]                 // vtable would start at -1
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]       // 32 - int.MinValue overflows int, not long
    [InlineData(-100)]               // vtable would start past the end
    [InlineData(-48)]                // 32+48 = 80, leaving no room at all for the header
    public void Vtable_soffset_is_bounded_in_both_directions(int soffset)
    {
        // The soffset is SIGNED, so it must be bounded below (before the buffer) and above (past
        // the end), and the subtraction must not be allowed to wrap. [32] is the table's soffset.
        byte[] bytes = With(Scalars, 32,
            (byte)soffset, (byte)(soffset >> 8), (byte)(soffset >> 16), (byte)(soffset >> 24));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).IsNull;
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Vtable_shorter_than_its_own_header_is_rejected(int vtableSize)
    {
        // [4] is vtable_size. A vtable always carries at least [u16 vtable_size][u16 table_size].
        byte[] bytes = With(Scalars, 4, (byte)vtableSize, (byte)(vtableSize >> 8));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).IsNull;
        });
    }

    [Fact]
    public void Vtable_running_past_the_end_of_the_buffer_is_rejected()
    {
        // [4] is vtable_size; the vtable starts at 4, so 77 bytes of it would end at 81 > 80.
        byte[] bytes = With(Scalars, 4, 0x4D, 0x00);

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).IsNull;
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(49)]      // 32 + 49 = 81 > 80
    [InlineData(65535)]
    public void Table_size_outside_the_buffer_is_rejected(int tableSize)
    {
        // [6] is table_size. It counts the soffset too, so it is at least 4, and the whole table
        // must lie inside the buffer.
        byte[] bytes = With(Scalars, 6, (byte)tableSize, (byte)(tableSize >> 8));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).IsNull;
        });
    }

    [Theory]
    [InlineData(45)]      // 45 + 4 > 48
    [InlineData(48)]      // starts exactly at the end of the table body
    [InlineData(65535)]
    public void Field_slot_pointing_past_table_size_is_rejected(int slot)
    {
        // [16] is field 4's slot; field 4 is an i32, so the value must end at or before
        // table_size == 48.
        byte[] bytes = With(Scalars, 16, (byte)slot, (byte)(slot >> 8));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).GetInt32(4);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Field_slot_pointing_into_the_soffset_is_rejected(int slot)
    {
        // The first four bytes of a table are its vtable reference; no field lives there.
        byte[] bytes = With(Scalars, 16, (byte)slot, (byte)(slot >> 8));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).GetInt32(4);
        });
    }

    [Theory]
    [InlineData(0u)]            // a null reference
    [InlineData(0xFFFFFFFFu)]   // would wrap to -1 in 32-bit signed arithmetic
    [InlineData(0x7FFFFFFFu)]
    [InlineData(99u)]           // 24 + 99 = 123, leaving fewer than 4 bytes of the 126-byte buffer
    public void Field_uoffset_outside_the_buffer_is_rejected(uint uoffset)
    {
        // [24] is the root table's field 0 uoffset (the string).
        byte[] bytes = With(StringsAndVectors, 24,
            (byte)uoffset, (byte)(uoffset >> 8), (byte)(uoffset >> 16), (byte)(uoffset >> 24));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).GetStringUtf8(0).Length;
        });
    }

    [Fact]
    public void Sub_table_uoffset_of_zero_is_rejected()
    {
        // [16] is the root table's field 0 uoffset (child a).
        byte[] bytes = With(SharedVtable, 16, 0x00, 0x00, 0x00, 0x00);

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).GetTable(0).IsNull;
        });
    }

    [Theory]
    [InlineData(78u)]           // 44 + 4 + 78 = 126, leaving no room for the terminator
    [InlineData(1000u)]
    [InlineData(0xFFFFFFFFu)]
    public void String_length_escaping_the_buffer_is_rejected(uint length)
    {
        // [44] is the string's length prefix. The trailing NUL is not counted by the length, but
        // it must still be inside the buffer.
        byte[] bytes = With(StringsAndVectors, 44,
            (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).GetStringUtf8(0).Length;
        });
    }

    [Fact]
    public void String_without_its_terminator_is_rejected()
    {
        // [53] is the NUL that terminates "hello".
        byte[] bytes = With(StringsAndVectors, 53, 0x41);

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).GetStringUtf8(0).Length;
        });
    }

    [Theory]
    [InlineData(69u)]           // the elements start at 58, so 126 - 58 = 68 is the ceiling
    [InlineData(100u)]
    [InlineData(0xFFFFFFFFu)]
    public void Byte_vector_count_escaping_the_buffer_is_rejected(uint count)
    {
        // [54] is the byte vector's count.
        byte[] bytes = With(StringsAndVectors, 54,
            (byte)count, (byte)(count >> 8), (byte)(count >> 16), (byte)(count >> 24));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).GetByteVector(1).Length;
        });
    }

    [Theory]
    [InlineData(15u)]           // 69 + 15*4 = 129 > 126
    [InlineData(0x40000000u)]   // count * 4 overflows a 32-bit product to exactly 0
    [InlineData(0xFFFFFFFFu)]
    public void Offset_vector_count_escaping_the_buffer_is_rejected(uint count)
    {
        // [65] is the table vector's count.
        byte[] bytes = With(StringsAndVectors, 65,
            (byte)count, (byte)(count >> 8), (byte)(count >> 16), (byte)(count >> 24));

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(bytes).GetVector(2).Count;
        });
    }

    [Fact]
    public void Vector_element_uoffset_outside_the_buffer_is_rejected()
    {
        // [69] is element 0's uoffset inside the table vector.
        byte[] zero = With(StringsAndVectors, 69, 0x00, 0x00, 0x00, 0x00);
        byte[] past = With(StringsAndVectors, 69, 0xFF, 0xFF, 0xFF, 0xFF);

        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(zero).GetVector(2).GetTable(0).IsNull;
        });
        Assert.Throws<VortexFormatException>(() =>
        {
            _ = FlatBufferTable.Root(past).GetVector(2).GetTable(0).IsNull;
        });
    }

    [Fact]
    public void Nested_table_traversal_is_depth_capped()
    {
        byte[] bytes = TableChain(VortexLimits.MaxFlatBufferDepth + 3);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        for (int i = 0; i < VortexLimits.MaxFlatBufferDepth; i++)
        {
            table = table.GetTable(0);
            Assert.False(table.IsNull);
        }

        Assert.Throws<VortexFormatException>(() =>
        {
            FlatBufferTable walk = FlatBufferTable.Root(bytes);
            for (int i = 0; i <= VortexLimits.MaxFlatBufferDepth; i++)
            {
                walk = walk.GetTable(0);
            }
        });
    }

    [Fact]
    public void Primitive_reads_are_bounds_checked()
    {
        byte[] four = [0x01, 0x02, 0x03, 0x04];

        Assert.Equal((byte)0x01, FlatBufferAccess.ReadUInt8(four, 0));
        Assert.Equal((ushort)0x0201, FlatBufferAccess.ReadUInt16(four, 0));
        Assert.Equal(0x04030201u, FlatBufferAccess.ReadUInt32(four, 0));
        Assert.Equal(0x04030201, FlatBufferAccess.ReadInt32(four, 0));

        Assert.Throws<VortexFormatException>(() => { _ = FlatBufferAccess.ReadUInt8(four, 4); });
        Assert.Throws<VortexFormatException>(() => { _ = FlatBufferAccess.ReadUInt8(four, -1); });
        Assert.Throws<VortexFormatException>(() => { _ = FlatBufferAccess.ReadUInt16(four, 3); });
        Assert.Throws<VortexFormatException>(() => { _ = FlatBufferAccess.ReadUInt32(four, 1); });
        Assert.Throws<VortexFormatException>(() => { _ = FlatBufferAccess.ReadUInt64(four, 0); });
        // int.MaxValue must not wrap into range when the width is added to it.
        Assert.Throws<VortexFormatException>(() => { _ = FlatBufferAccess.ReadUInt32(four, int.MaxValue); });
        Assert.Throws<VortexFormatException>(() => { _ = FlatBufferAccess.ReadUInt64(four, int.MaxValue - 3); });
        Assert.Throws<VortexFormatException>(() => { _ = FlatBufferAccess.ReadUInt16(Array.Empty<byte>(), 0); });
    }

    [Fact]
    public void No_truncation_of_a_valid_buffer_escapes_as_another_exception_type()
    {
        for (int length = 0; length <= StringsAndVectors.Length; length++)
        {
            ProbeEveryAccessor(Truncate(StringsAndVectors, length));
            ProbeEveryAccessor(Truncate(Scalars, Math.Min(length, Scalars.Length)));
            ProbeEveryAccessor(Truncate(StructVectors, Math.Min(length, StructVectors.Length)));
        }
    }

    [Fact]
    public void No_single_byte_corruption_escapes_as_another_exception_type()
    {
        // Deterministic, exhaustive single-byte mutation: 0x00 clears a field, 0xFF maximises an
        // unsigned one, and 0x80 flips the sign bit of the top byte of an soffset.
        byte[][] sources =
            [Scalars, StringsAndVectors, StructVectors, SharedVtable, VtableAfterTable, InlineStruct];
        foreach (byte[] source in sources)
        {
            for (int offset = 0; offset < source.Length; offset++)
            {
                ProbeEveryAccessor(With(source, offset, 0x00));
                ProbeEveryAccessor(With(source, offset, 0xFF));
                ProbeEveryAccessor(With(source, offset, 0x80));
            }
        }
    }

    /// <summary>
    /// Reads every accessor for the first few field ids. Anything the reader considers malformed
    /// is a <see cref="VortexFormatException"/>; any other exception type fails the test, which is
    /// exactly the guarantee the reader gives every caller.
    /// </summary>
    private static void ProbeEveryAccessor(byte[] bytes)
    {
        for (int field = 0; field < 8; field++)
        {
            try
            {
                FlatBufferTable table = FlatBufferTable.Root(bytes);
                _ = table.HasField(field);
                _ = table.GetInt8(field);
                _ = table.GetUInt8(field);
                _ = table.GetInt16(field);
                _ = table.GetUInt16(field);
                _ = table.GetInt32(field);
                _ = table.GetUInt32(field);
                _ = table.GetInt64(field);
                _ = table.GetUInt64(field);
                _ = table.GetFloat32(field);
                _ = table.GetFloat64(field);
                _ = table.GetBool(field);
                _ = table.TryGetBool(field, out _);
                _ = table.TryGetUInt8(field, out _);
                _ = table.TryGetUInt64(field, out _);
                _ = table.TryGetStruct(field, out SegmentSpecLike _);
                _ = table.GetStringUtf8(field).Length;
                _ = table.GetByteVector(field).Length;
                _ = table.TryGetStructVector(field, out ReadOnlySpan<ushort> _);
                _ = table.GetTable(field).IsNull;

                FlatBufferVector vector = table.GetVector(field);
                for (int i = 0; i < vector.Count; i++)
                {
                    _ = vector.GetTable(i).IsNull;
                    _ = vector.GetStringUtf8(i).Length;
                }
            }
            catch (VortexFormatException)
            {
                // The one exception type a malformed buffer may produce.
            }
        }
    }
}
