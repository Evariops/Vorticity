// Write-then-read-back round trips, one per accessor shape on FlatBufferTable. The reader is
// hand-written against the FORMAT (its own tests use literal byte vectors), so agreement between
// the two is the contract the builder has to meet.
using System;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.FlatBuffers;
using Xunit;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferBuilderRoundTripTests
{
    [Fact]
    public void Every_scalar_accessor_round_trips_at_its_boundary_value()
    {
        using var builder = new FlatBufferBuilder(64);
        builder.StartTable();
        builder.AddInt8(0, sbyte.MinValue);
        builder.AddUInt8(1, byte.MaxValue);
        builder.AddInt16(2, short.MinValue);
        builder.AddUInt16(3, ushort.MaxValue);
        builder.AddInt32(4, int.MinValue);
        builder.AddUInt32(5, uint.MaxValue);
        builder.AddInt64(6, long.MinValue);
        builder.AddUInt64(7, ulong.MaxValue);
        builder.AddFloat32(8, float.NegativeInfinity);
        builder.AddFloat64(9, double.Epsilon);
        builder.AddBool(10, true);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.Equal(sbyte.MinValue, table.GetInt8(0));
        Assert.Equal(byte.MaxValue, table.GetUInt8(1));
        Assert.Equal(short.MinValue, table.GetInt16(2));
        Assert.Equal(ushort.MaxValue, table.GetUInt16(3));
        Assert.Equal(int.MinValue, table.GetInt32(4));
        Assert.Equal(uint.MaxValue, table.GetUInt32(5));
        Assert.Equal(long.MinValue, table.GetInt64(6));
        Assert.Equal(ulong.MaxValue, table.GetUInt64(7));
        Assert.Equal(float.NegativeInfinity, table.GetFloat32(8));
        Assert.Equal(double.Epsilon, table.GetFloat64(9));
        Assert.True(table.GetBool(10));

        // Nothing beyond the last field exists, and the reader must say so rather than read on.
        Assert.False(table.HasField(11));
        Assert.Equal(-7, table.GetInt32(11, -7));
    }

    [Fact]
    public void Fields_may_be_added_out_of_id_order()
    {
        // The vtable, not the write order, decides where a field lives. A writer that emits fields
        // in whatever order its source hands them must still produce a readable table.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddUInt64(3, 0xDEADBEEFCAFEF00DUL);
        builder.AddUInt8(0, 9);
        builder.AddUInt32(2, 12345u);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.Equal((byte)9, table.GetUInt8(0));
        Assert.False(table.HasField(1));
        Assert.Equal(12345u, table.GetUInt32(2));
        Assert.Equal(0xDEADBEEFCAFEF00DUL, table.GetUInt64(3));
    }

    [Fact]
    public void Empty_table_round_trips_with_a_header_only_vtable()
    {
        // `table EncryptionSpec {}` in spec/flatbuffers/footer.fbs really has no fields at all.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int vtablePos = BuiltFlatBuffer.VTableOf(bytes, rootPos);
        Assert.Equal(4, BuiltFlatBuffer.VTableSize(bytes, vtablePos));
        Assert.Equal(4, BuiltFlatBuffer.TableSize(bytes, vtablePos));

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.False(table.IsNull);
        Assert.False(table.HasField(0));
        Assert.Equal(42, table.GetInt32(0, 42));
    }

    [Fact]
    public void Nested_tables_and_absent_children_round_trip()
    {
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 7);
        int child = builder.EndTable();

        builder.StartTable();
        builder.AddOffset(0, child);
        builder.AddOffset(1, 0);        // an absent optional child writes nothing
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.Equal(7, table.GetTable(0).GetInt32(0));
        Assert.True(table.GetTable(1).IsNull);
        Assert.False(table.HasField(1));
    }

    [Fact]
    public void Strings_round_trip_and_carry_an_uncounted_trailing_nul()
    {
        // A FlatBuffers string is [u32 length][utf8][NUL] and the NUL is NOT part of the length.
        // The reader rejects a string whose terminator is missing, so this is load-bearing.
        ReadOnlySpan<byte> text = "hééllo, wörld"u8;

        using var builder = new FlatBufferBuilder();
        int str = builder.CreateStringUtf8(text);
        builder.StartTable();
        builder.AddOffset(0, str);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.True(table.GetStringUtf8(0).SequenceEqual(text));

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int stringPos = BuiltFlatBuffer.Referenced(bytes, rootPos, 0);
        Assert.Equal((uint)text.Length, BuiltFlatBuffer.ReadUInt32(bytes, stringPos));
        Assert.Equal(0, bytes[stringPos + 4 + text.Length]);
    }

    [Fact]
    public void CreateString_matches_CreateStringUtf8_byte_for_byte()
    {
        const string Text = "naïve über 日本語";

        using var a = new FlatBufferBuilder();
        int sa = a.CreateString(Text);
        a.StartTable();
        a.AddOffset(0, sa);
        byte[] fromString = a.FinishToArray(a.EndTable());

        using var b = new FlatBufferBuilder();
        int sb = b.CreateStringUtf8(System.Text.Encoding.UTF8.GetBytes(Text));
        b.StartTable();
        b.AddOffset(0, sb);
        byte[] fromUtf8 = b.FinishToArray(b.EndTable());

        Assert.Equal(fromUtf8, fromString);
        Assert.True(FlatBufferTable.Root(fromString).GetStringUtf8(0)
            .SequenceEqual(System.Text.Encoding.UTF8.GetBytes(Text)));
    }

    [Fact]
    public void Empty_string_round_trips_with_only_its_terminator()
    {
        using var builder = new FlatBufferBuilder();
        int str = builder.CreateStringUtf8(default);
        builder.StartTable();
        builder.AddOffset(0, str);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        // Present but empty. The reader also returns an empty span for an ABSENT string, so the
        // distinction has to be checked through HasField.
        Assert.True(table.HasField(0));
        Assert.True(table.GetStringUtf8(0).IsEmpty);

        int stringPos = BuiltFlatBuffer.Referenced(bytes, BuiltFlatBuffer.RootTable(bytes), 0);
        Assert.Equal(0u, BuiltFlatBuffer.ReadUInt32(bytes, stringPos));
        Assert.Equal(0, bytes[stringPos + 4]);
    }

    [Fact]
    public void Inline_struct_field_round_trips_by_value()
    {
        TestFlatBuffers.SegmentSpecLike spec = new()
        {
            Offset = 0x0102030405060708UL,
            Length = 0x11223344u,
            AlignmentExponent = 6,
            Compression = 3,
            Encryption = 0xABCD,
        };

        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddUInt8(0, 1);
        builder.AddStruct(1, in spec);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.True(table.TryGetStruct(1, out TestFlatBuffers.SegmentSpecLike read));
        Assert.Equal(spec.Offset, read.Offset);
        Assert.Equal(spec.Length, read.Length);
        Assert.Equal(spec.AlignmentExponent, read.AlignmentExponent);
        Assert.Equal(spec.Compression, read.Compression);
        Assert.Equal(spec.Encryption, read.Encryption);

        // The struct is stored inline in the table body, so its offset is 8-aligned relative to
        // the buffer start - an inline struct is aligned exactly like a struct vector element.
        int structPos = BuiltFlatBuffer.FieldPos(bytes, BuiltFlatBuffer.RootTable(bytes), 1);
        Assert.Equal(0, structPos % Unsafe.SizeOf<ulong>());
    }

    [Fact]
    public void Byte_vector_round_trips_including_the_empty_case()
    {
        byte[] payload = new byte[257];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 7);
        }

        using var builder = new FlatBufferBuilder(64);
        int full = builder.CreateByteVector(payload);
        int empty = builder.CreateByteVector(default);
        builder.StartTable();
        builder.AddOffset(0, full);
        builder.AddOffset(1, empty);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.True(table.GetByteVector(0).SequenceEqual(payload));
        Assert.True(table.HasField(1));
        Assert.True(table.GetByteVector(1).IsEmpty);
        Assert.False(table.HasField(2));
        Assert.True(table.GetByteVector(2).IsEmpty);
    }

    [Fact]
    public void Vector_of_tables_round_trips_in_order()
    {
        using var builder = new FlatBufferBuilder();
        int[] children = new int[5];
        for (int i = 0; i < children.Length; i++)
        {
            builder.StartTable();
            builder.AddInt32(0, 100 + i);
            children[i] = builder.EndTable();
        }

        int vector = builder.CreateOffsetVector(children);
        builder.StartTable();
        builder.AddOffset(0, vector);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferVector read = FlatBufferTable.Root(bytes).GetVector(0);
        Assert.Equal(5, read.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(100 + i, read.GetTable(i).GetInt32(0));
        }
    }

    [Fact]
    public void Vector_of_strings_round_trips_in_order()
    {
        string[] names = ["id", "timestamp", "payload", string.Empty, "été"];

        using var builder = new FlatBufferBuilder();
        int[] offsets = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            offsets[i] = builder.CreateString(names[i]);
        }

        int vector = builder.CreateOffsetVector(offsets);
        builder.StartTable();
        builder.AddOffset(0, vector);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferVector read = FlatBufferTable.Root(bytes).GetVector(0);
        Assert.Equal(names.Length, read.Count);
        for (int i = 0; i < names.Length; i++)
        {
            Assert.True(read.GetStringUtf8(i).SequenceEqual(System.Text.Encoding.UTF8.GetBytes(names[i])));
        }
    }

    [Fact]
    public void Empty_offset_vector_round_trips()
    {
        using var builder = new FlatBufferBuilder();
        int vector = builder.CreateOffsetVector(default);
        builder.StartTable();
        builder.AddOffset(0, vector);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.True(table.HasField(0));
        Assert.Equal(0, table.GetVector(0).Count);
    }

    [Fact]
    public void Deeply_nested_tables_round_trip_up_to_the_reader_depth_cap()
    {
        // The reader caps traversal at VortexLimits.MaxFlatBufferDepth (128). Build a chain just
        // inside it and walk the whole thing back.
        const int Depth = 100;

        using var builder = new FlatBufferBuilder();
        builder.StartTable();

        // Every level's marker is i + 1, never 0: a level whose scalar equalled the default would
        // be omitted, giving that level a different vtable and blurring the dedup assertion below.
        builder.AddInt32(1, Depth + 1);
        int current = builder.EndTable();
        for (int i = Depth - 1; i >= 0; i--)
        {
            builder.StartTable();
            builder.AddOffset(0, current);
            builder.AddInt32(1, i + 1);
            current = builder.EndTable();
        }

        byte[] bytes = builder.FinishToArray(current);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        for (int i = 0; i <= Depth; i++)
        {
            Assert.Equal(i + 1, table.GetInt32(1));
            if (i < Depth)
            {
                table = table.GetTable(0);
            }
        }

        // Every level has the same shape, so the whole chain shares one vtable.
        Assert.Equal(2, builder.VTableCount);
    }

    [Fact]
    public void A_wide_table_round_trips_every_field()
    {
        const int Fields = 1000;

        using var builder = new FlatBufferBuilder(64);
        builder.StartTable();
        for (int i = 0; i < Fields; i++)
        {
            builder.AddUInt16(i, (ushort)(i * 3));
        }

        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        for (int i = 0; i < Fields; i++)
        {
            // Field 0's value is 0, which equals the default, so it is omitted - and reads back
            // as the default anyway. That is the whole point of default omission.
            Assert.Equal((ushort)(i * 3), table.GetUInt16(i));
        }

        Assert.False(table.HasField(0));
        Assert.True(table.HasField(1));
    }
}
