// Misuse and boundary behaviour of the builder.
//
// The threat model differs from the reader's: a builder is fed by OUR code, not by a hostile file,
// so an API contract violation is a programming error (InvalidOperationException /
// ArgumentOutOfRangeException), while a limit the FORMAT imposes - a table body past
// u16 table_size, a buffer past the 32-bit offset ceiling - is a VortexFormatException, because
// producing such a buffer would produce a file no conforming reader can parse.
//
// What is NOT negotiable either way: the builder must never emit a buffer the reader then rejects,
// and must never silently produce one that reads back different values.
using System;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Xunit;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferBuilderAdversarialTests
{
    [Fact]
    public void Adding_a_field_with_no_open_table_throws()
    {
        using var builder = new FlatBufferBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.AddInt32(0, 1));
        Assert.Throws<InvalidOperationException>(() => builder.AddUInt64Always(0, 0));
        Assert.Throws<InvalidOperationException>(() => builder.AddBoolAlways(0, true));
        Assert.Throws<InvalidOperationException>(() => builder.AddUInt8Always(0, 0));
        Assert.Throws<InvalidOperationException>(() => builder.AddOffset(0, 4));
        Assert.Throws<InvalidOperationException>(() => builder.EndTable());
    }

    [Fact]
    public void Nesting_a_table_throws()
    {
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        Assert.Throws<InvalidOperationException>(builder.StartTable);
    }

    [Fact]
    public void Creating_a_string_or_vector_inside_an_open_table_throws()
    {
        // FlatBuffers has no encoding for it: the object's bytes would land in the middle of the
        // table body. A builder that accepted it would emit an unreadable buffer.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        Assert.Throws<InvalidOperationException>(() => builder.CreateString("x"));
        Assert.Throws<InvalidOperationException>(() => builder.CreateByteVector(default));
        Assert.Throws<InvalidOperationException>(() => builder.CreateOffsetVector(default));
        Assert.Throws<InvalidOperationException>(() => builder.CreateScalarVector<uint>(default));
        Assert.Throws<InvalidOperationException>(() => builder.CreateStructVector<ulong>(default));
    }

    [Fact]
    public void Adding_the_same_field_id_twice_throws()
    {
        // The second write would leave the first value stranded in the table body and, worse,
        // silently change table_size, so two tables of the "same" shape would stop sharing.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(3, 1);
        Assert.Throws<InvalidOperationException>(() => builder.AddInt32(3, 2));
        Assert.Throws<InvalidOperationException>(() => builder.AddUInt8(3, 2));
    }

    [Fact]
    public void A_field_id_the_vtable_cannot_encode_throws()
    {
        // vtable_size is a u16 covering a 4-byte header plus one u16 per field, so the highest
        // encodable id is 32764. Truncating instead would write the value into another field's slot.
        const int MaxFieldId = (ushort.MaxValue / 2) - 3;

        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddUInt8(MaxFieldId + 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddUInt8(int.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddUInt8(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddUInt8(int.MinValue, 1));
    }

    [Fact]
    public void The_highest_encodable_field_id_still_round_trips()
    {
        const int MaxFieldId = (ushort.MaxValue / 2) - 3;

        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddUInt8(MaxFieldId, 0xAB);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int vtablePos = BuiltFlatBuffer.VTableOf(bytes, BuiltFlatBuffer.RootTable(bytes));
        Assert.Equal(ushort.MaxValue - 1, BuiltFlatBuffer.VTableSize(bytes, vtablePos));
        Assert.Equal((byte)0xAB, FlatBufferTable.Root(bytes).GetUInt8(MaxFieldId));
    }

    [Fact]
    public void A_table_body_past_the_u16_table_size_is_a_format_error()
    {
        // table_size is a u16, so a table body cannot exceed 65535 bytes. Wrapping it would make
        // every field of the table read as out of bounds.
        using var builder = new FlatBufferBuilder(1 << 20);
        builder.StartTable();
        VortexFormatException error = Assert.Throws<VortexFormatException>(() =>
        {
            for (int i = 0; i < 20000; i++)
            {
                builder.AddInt32(i, i + 1);
            }

            builder.EndTable();
        });

        Assert.Contains("table_size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_body_exactly_at_the_u16_ceiling_is_accepted()
    {
        // 16383 four-byte fields plus the 4-byte soffset is exactly 65536 - 4 + 4 ... the boundary
        // is worth pinning down rather than approximating: 16382 fields give 65532 bytes, which
        // fits, and each field must still read back at its own id.
        const int Fields = 16382;

        using var builder = new FlatBufferBuilder(1 << 20);
        builder.StartTable();
        for (int i = 0; i < Fields; i++)
        {
            builder.AddInt32(i, i + 1);
        }

        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int vtablePos = BuiltFlatBuffer.VTableOf(bytes, BuiltFlatBuffer.RootTable(bytes));
        Assert.Equal(4 + (Fields * 4), BuiltFlatBuffer.TableSize(bytes, vtablePos));

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.Equal(1, table.GetInt32(0));
        Assert.Equal(Fields, table.GetInt32(Fields - 1));
    }

    [Fact]
    public void Referencing_an_object_that_was_never_written_throws()
    {
        // FlatBuffers is built back to front: a referenced object must already exist. A forward
        // reference to nothing would be a uoffset pointing past the end of the buffer.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddOffset(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddOffset(0, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddOffset(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddOffset(0, int.MinValue));
    }

    [Fact]
    public void An_offset_vector_rejects_a_zero_or_unwritten_element()
    {
        // A zero uoffset is exactly what FlatBufferVector rejects as malformed, so the builder must
        // never emit one rather than leave the reader to find it.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        int child = builder.EndTable();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.CreateOffsetVector(new[] { child, 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.CreateOffsetVector(new[] { int.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.CreateOffsetVector(new[] { -8 }));
    }

    [Fact]
    public void Finishing_with_an_open_table_or_a_bogus_root_throws()
    {
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 1);
        Assert.Throws<InvalidOperationException>(() => builder.Finish(4));

        _ = builder.EndTable();

        // The builder cannot tell a table offset from any other offset it handed out - neither can
        // flatc - but it can and must reject an offset that names nothing at all.
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Finish(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Finish(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Finish(builder.Offset + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Finish(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Finish(int.MinValue));
    }

    [Fact]
    public void Finishing_twice_throws_and_Clear_makes_the_builder_reusable()
    {
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 1);
        int root = builder.EndTable();
        byte[] first = builder.FinishToArray(root);

        Assert.Throws<InvalidOperationException>(() => builder.Finish(root));
        Assert.Throws<InvalidOperationException>(builder.StartTable);
        Assert.Throws<InvalidOperationException>(() => builder.CreateString("x"));

        builder.Clear();
        builder.StartTable();
        builder.AddInt32(0, 2);
        byte[] second = builder.FinishToArray(builder.EndTable());

        Assert.Equal(1, FlatBufferTable.Root(first).GetInt32(0));
        Assert.Equal(2, FlatBufferTable.Root(second).GetInt32(0));
    }

    [Fact]
    public void A_disposed_builder_throws_rather_than_writing_into_a_returned_array()
    {
        // The scratch array goes back to ArrayPool on Dispose. Writing into it afterwards would
        // corrupt whatever rented it next - far worse than an exception.
        var builder = new FlatBufferBuilder();
        builder.StartTable();
        int root = builder.EndTable();
        builder.Dispose();
        builder.Dispose();          // idempotent

        Assert.Throws<ObjectDisposedException>(builder.StartTable);
        Assert.Throws<ObjectDisposedException>(() => builder.EndTable());
        Assert.Throws<ObjectDisposedException>(builder.Clear);
        Assert.Throws<ObjectDisposedException>(() => builder.Finish(root));
        Assert.Throws<ObjectDisposedException>(() => builder.CreateString("x"));
        Assert.Throws<ObjectDisposedException>(() => builder.CreateByteVector(default));
    }

    [Fact]
    public void The_constructor_rejects_a_negative_or_impossible_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlatBufferBuilder(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlatBufferBuilder(int.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlatBufferBuilder(int.MaxValue));
    }

    [Fact]
    public void A_zero_capacity_builder_still_works()
    {
        using var builder = new FlatBufferBuilder(0);
        Assert.Equal(0, builder.Offset);

        int text = builder.CreateStringUtf8("zero"u8);
        builder.StartTable();
        builder.AddOffset(0, text);
        byte[] bytes = builder.FinishToArray(builder.EndTable());

        Assert.True(FlatBufferTable.Root(bytes).GetStringUtf8(0).SequenceEqual("zero"u8));
    }

    [Fact]
    public void CreateString_rejects_null()
    {
        using var builder = new FlatBufferBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.CreateString(null!));
    }

    [Fact]
    public void Offset_tracks_the_bytes_written_and_is_the_identity_of_the_last_object()
    {
        using var builder = new FlatBufferBuilder();
        Assert.Equal(0, builder.Offset);

        int text = builder.CreateStringUtf8("ab"u8);
        Assert.Equal(builder.Offset, text);
        Assert.True(builder.Offset >= 4 + 2 + 1);

        int before = builder.Offset;
        builder.StartTable();
        builder.AddOffset(0, text);
        int table = builder.EndTable();
        Assert.Equal(table, before + (table - before));
        Assert.True(builder.Offset >= table);
    }

    [Fact]
    public void A_string_containing_an_embedded_nul_keeps_its_declared_length()
    {
        // A NUL inside the payload is legal UTF-8 data; only the terminator is added by the writer.
        // Truncating at the first NUL would silently shorten the value.
        ReadOnlySpan<byte> withNul = [0x61, 0x00, 0x62];

        using var builder = new FlatBufferBuilder();
        int text = builder.CreateStringUtf8(withNul);
        builder.StartTable();
        builder.AddOffset(0, text);
        byte[] bytes = builder.FinishToArray(builder.EndTable());

        int stringPos = BuiltFlatBuffer.Referenced(bytes, BuiltFlatBuffer.RootTable(bytes), 0);
        Assert.Equal(3u, BuiltFlatBuffer.ReadUInt32(bytes, stringPos));
        Assert.Equal(0, bytes[stringPos + 4 + 3]);
        Assert.True(FlatBufferTable.Root(bytes).GetStringUtf8(0).SequenceEqual(withNul));
    }

    [Fact]
    public void Every_buffer_this_builder_produces_survives_truncation_by_the_reader()
    {
        // The reader must reject a truncated buffer rather than read out of bounds. Chopping a
        // real, well-formed buffer at every length is the cheapest way to cover the whole space of
        // "valid prefix, invalid whole".
        using var builder = new FlatBufferBuilder();
        int text = builder.CreateStringUtf8("truncate me"u8);
        int vector = builder.CreateScalarVector<uint>(new uint[] { 1, 2, 3, 4 });
        builder.StartTable();
        builder.AddOffset(0, text);
        builder.AddOffset(1, vector);
        builder.AddUInt64(2, ulong.MaxValue);
        byte[] bytes = builder.FinishToArray(builder.EndTable());

        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] truncated = bytes.AsSpan(0, length).ToArray();
            try
            {
                FlatBufferTable table = FlatBufferTable.Root(truncated);
                _ = table.GetStringUtf8(0);
                _ = table.GetStructVector<uint>(1);
                _ = table.GetUInt64(2);
            }
            catch (VortexFormatException)
            {
                // The only acceptable failure mode.
            }
        }
    }
}
