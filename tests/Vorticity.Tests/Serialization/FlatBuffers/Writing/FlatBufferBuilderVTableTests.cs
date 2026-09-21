// Vtable deduplication, which is not optional: without it, wide-schema metadata inflates and the
// written file starts with a self-inflicted handicap against its size target of 105% of what the
// reference implementation writes.
//
// These assertions are STRUCTURAL - they read the soffsets out of the finished bytes - because a
// builder that emits one vtable per table still produces a buffer that reads back correctly. Only
// the bytes can tell the two apart.
//
// The shapes below follow from one rule. `table_size` is measured from the table's start to
// its end INCLUDING the alignment padding a field forced, so two tables of identical shape share a
// vtable only when they also start at the same alignment. That is what flatc does too, and it is
// why every fixture here uses 4-byte fields and 4-byte-sized vtables: the head then stays 4-byte
// aligned table after table and the dedup counts are deterministic rather than luck.
using System;
using Vorticity.Serialization.FlatBuffers;
using Xunit;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferBuilderVTableTests
{
    [Fact]
    public void Two_tables_of_identical_shape_share_exactly_one_vtable()
    {
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 1);
        builder.AddInt32(1, 2);
        int first = builder.EndTable();

        builder.StartTable();
        builder.AddInt32(0, 3);
        builder.AddInt32(1, 4);
        int second = builder.EndTable();

        // The root is deliberately a THIRD shape (an extra ubyte field). Two offset fields would
        // give the root the same vtable as the children and make the count below vacuous.
        builder.StartTable();
        builder.AddOffset(0, first);
        builder.AddOffset(1, second);
        builder.AddUInt8(2, 7);
        int root = builder.EndTable();

        Assert.Equal(2, builder.VTableCount);
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int firstPos = BuiltFlatBuffer.Referenced(bytes, rootPos, 0);
        int secondPos = BuiltFlatBuffer.Referenced(bytes, rootPos, 1);

        // Exactly one vtable serves both children, and both tables' soffsets resolve to it.
        int[] shared = BuiltFlatBuffer.DistinctVTables(bytes, firstPos, secondPos);
        Assert.Single(shared);
        Assert.Equal(shared[0], BuiltFlatBuffer.VTableOf(bytes, firstPos));
        Assert.Equal(shared[0], BuiltFlatBuffer.VTableOf(bytes, secondPos));
        Assert.Equal(2, BuiltFlatBuffer.DistinctVTables(bytes, rootPos, firstPos, secondPos).Length);

        // ... and the one shared vtable still describes both tables correctly.
        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.Equal(1, table.GetTable(0).GetInt32(0));
        Assert.Equal(2, table.GetTable(0).GetInt32(1));
        Assert.Equal(3, table.GetTable(1).GetInt32(0));
        Assert.Equal(4, table.GetTable(1).GetInt32(1));
    }

    [Fact]
    public void A_deduplicated_vtable_follows_its_table_while_a_fresh_one_precedes_it()
    {
        // Both directions are legal and BOTH occur naturally: a fresh vtable is written just after
        // its table, so it PRECEDES it in the finished buffer and the soffset is positive; a
        // reused one was written earlier in build order, so it FOLLOWS its table and the soffset
        // is negative. That is why the reader must bound soffsets in both directions.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 1);
        builder.AddInt32(1, 2);
        int fresh = builder.EndTable();

        builder.StartTable();
        builder.AddInt32(0, 3);
        builder.AddInt32(1, 4);
        int reused = builder.EndTable();

        builder.StartTable();
        builder.AddOffset(0, fresh);
        builder.AddOffset(1, reused);
        builder.AddUInt8(2, 7);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int freshPos = BuiltFlatBuffer.Referenced(bytes, rootPos, 0);
        int reusedPos = BuiltFlatBuffer.Referenced(bytes, rootPos, 1);

        Assert.True(BuiltFlatBuffer.ReadInt32(bytes, freshPos) > 0, "a fresh vtable precedes its table");
        Assert.True(BuiltFlatBuffer.ReadInt32(bytes, reusedPos) < 0, "a reused vtable follows its table");
        Assert.Equal(BuiltFlatBuffer.VTableOf(bytes, freshPos), BuiltFlatBuffer.VTableOf(bytes, reusedPos));

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.Equal(1, table.GetTable(0).GetInt32(0));
        Assert.Equal(4, table.GetTable(1).GetInt32(1));
    }

    [Fact]
    public void Same_field_ids_with_different_value_widths_do_not_share_a_vtable()
    {
        // A vtable encodes table_size and the per-field byte offsets, not just which fields are
        // present. Merging on "same ids" alone would produce a table whose i64 is read as an i32.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 1);
        builder.AddInt32(1, 2);
        int narrow = builder.EndTable();

        builder.StartTable();
        builder.AddInt64(0, long.MinValue);
        builder.AddInt8(1, -1);
        int wide = builder.EndTable();

        builder.StartTable();
        builder.AddOffset(0, narrow);
        builder.AddOffset(1, wide);
        builder.AddUInt8(2, 7);
        int root = builder.EndTable();

        Assert.Equal(3, builder.VTableCount);
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int narrowPos = BuiltFlatBuffer.Referenced(bytes, rootPos, 0);
        int widePos = BuiltFlatBuffer.Referenced(bytes, rootPos, 1);
        Assert.Equal(2, BuiltFlatBuffer.DistinctVTables(bytes, narrowPos, widePos).Length);
        Assert.NotEqual(
            BuiltFlatBuffer.Slot(bytes, narrowPos, 1),
            BuiltFlatBuffer.Slot(bytes, widePos, 1));

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.Equal(1, table.GetTable(0).GetInt32(0));
        Assert.Equal(2, table.GetTable(0).GetInt32(1));
        Assert.Equal(long.MinValue, table.GetTable(1).GetInt64(0));
        Assert.Equal((sbyte)-1, table.GetTable(1).GetInt8(1));
    }

    [Fact]
    public void Same_ids_and_widths_but_different_presence_do_not_share_a_vtable()
    {
        // A zero slot is part of the vtable's bytes, so "fields 0 and 2" and "fields 0, 1 and 2"
        // describe different tables even though every value is four bytes wide.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 1);
        builder.AddInt32(2, 3);
        int sparse = builder.EndTable();

        builder.StartTable();
        builder.AddInt32(0, 1);
        builder.AddInt32(1, 2);
        builder.AddInt32(2, 3);
        int dense = builder.EndTable();

        builder.StartTable();
        builder.AddOffset(0, sparse);
        builder.AddOffset(1, dense);
        builder.AddUInt8(2, 7);
        int root = builder.EndTable();

        Assert.Equal(3, builder.VTableCount);
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int sparsePos = BuiltFlatBuffer.Referenced(bytes, rootPos, 0);
        int densePos = BuiltFlatBuffer.Referenced(bytes, rootPos, 1);
        Assert.Equal(2, BuiltFlatBuffer.DistinctVTables(bytes, sparsePos, densePos).Length);
        Assert.Equal(0, BuiltFlatBuffer.Slot(bytes, sparsePos, 1));
        Assert.NotEqual(0, BuiltFlatBuffer.Slot(bytes, densePos, 1));

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.False(table.GetTable(0).HasField(1));
        Assert.Equal(3, table.GetTable(0).GetInt32(2));
        Assert.True(table.GetTable(1).HasField(1));
        Assert.Equal(2, table.GetTable(1).GetInt32(1));
    }

    [Fact]
    public void A_field_omitted_as_its_default_costs_no_vtable_slot()
    {
        // A field id past the end of the vtable reads as absent, so a table that mentions field 5
        // only to pass its default value must not pay for six slots.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 1);
        builder.AddInt32(5, 0);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int vtablePos = BuiltFlatBuffer.VTableOf(bytes, rootPos);
        Assert.Equal(1, BuiltFlatBuffer.SlotCount(bytes, vtablePos));
        Assert.Equal(6, BuiltFlatBuffer.VTableSize(bytes, vtablePos));

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.False(table.HasField(5));
        Assert.Equal(0, table.GetInt32(5));
    }

    [Fact]
    public void Interior_absent_fields_are_kept_as_zero_slots()
    {
        // Only fields past the LAST present one disappear. Dropping an interior zero slot would
        // renumber every field after it.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(3, 99);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int vtablePos = BuiltFlatBuffer.VTableOf(bytes, rootPos);
        Assert.Equal(4, BuiltFlatBuffer.SlotCount(bytes, vtablePos));
        Assert.Equal(0, BuiltFlatBuffer.Slot(bytes, rootPos, 0));
        Assert.Equal(0, BuiltFlatBuffer.Slot(bytes, rootPos, 1));
        Assert.Equal(0, BuiltFlatBuffer.Slot(bytes, rootPos, 2));
        Assert.NotEqual(0, BuiltFlatBuffer.Slot(bytes, rootPos, 3));
        Assert.Equal(99, FlatBufferTable.Root(bytes).GetInt32(3));
    }

    [Fact]
    public void Every_empty_table_shares_one_vtable()
    {
        // `table EncryptionSpec {}` in the footer schema. A Footer may carry several.
        using var builder = new FlatBufferBuilder();
        int[] specs = new int[8];
        for (int i = 0; i < specs.Length; i++)
        {
            builder.StartTable();
            specs[i] = builder.EndTable();
        }

        int vector = builder.CreateOffsetVector(specs);
        builder.StartTable();
        builder.AddOffset(0, vector);
        int root = builder.EndTable();

        // One header-only vtable for all eight, plus the root's.
        Assert.Equal(2, builder.VTableCount);
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferVector read = FlatBufferTable.Root(bytes).GetVector(0);
        Assert.Equal(8, read.Count);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        int elements = BuiltFlatBuffer.ElementsOf(bytes, rootPos, 0);
        int[] positions = new int[8];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = BuiltFlatBuffer.Follow(bytes, elements + (i * 4));
        }

        Assert.Single(BuiltFlatBuffer.DistinctVTables(bytes, positions));
    }

    [Fact]
    public void A_wide_uniform_schema_converges_on_one_vtable_per_shape()
    {
        // This is the case vtable dedup exists for: one metadata table per column of a wide
        // struct. Without dedup that is one vtable per column and the metadata inflates.
        const int Columns = 2000;

        using var builder = new FlatBufferBuilder(64);
        int[] nodes = new int[Columns];
        for (int i = 0; i < Columns; i++)
        {
            builder.StartTable();
            builder.AddUInt32(0, (uint)i + 1);
            builder.AddUInt32(1, (uint)i + 2);
            nodes[i] = builder.EndTable();
        }

        int vector = builder.CreateOffsetVector(nodes);
        builder.StartTable();
        builder.AddOffset(0, vector);
        int root = builder.EndTable();

        Assert.Equal(2, builder.VTableCount);
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferVector read = FlatBufferTable.Root(bytes).GetVector(0);
        Assert.Equal(Columns, read.Count);
        for (int i = 0; i < Columns; i++)
        {
            FlatBufferTable node = read.GetTable(i);
            Assert.Equal((uint)i + 1, node.GetUInt32(0));
            Assert.Equal((uint)i + 2, node.GetUInt32(1));
        }

        // 16 bytes per column with dedup (12-byte table + a 4-byte uoffset); 24 without, because
        // each table would drag its own 8-byte vtable. The bound discriminates between the two.
        Assert.True(
            bytes.Length < Columns * 20,
            $"Deduplicated metadata for {Columns} uniform tables should stay under " +
            $"{Columns * 20} bytes; it was {bytes.Length}.");
    }

    [Fact]
    public void Clear_forgets_vtables_from_the_previous_buffer()
    {
        // Vtable offsets are relative to the buffer being built. Carrying them across a Clear
        // would point a table at bytes the new buffer does not contain.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 1);
        int first = builder.EndTable();
        byte[] before = builder.FinishToArray(first);

        builder.Clear();
        Assert.Equal(0, builder.Offset);

        builder.StartTable();
        builder.AddInt32(0, 2);
        int second = builder.EndTable();
        byte[] after = builder.FinishToArray(second);

        Assert.Equal(1, builder.VTableCount);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(1, FlatBufferTable.Root(before).GetInt32(0));
        Assert.Equal(2, FlatBufferTable.Root(after).GetInt32(0));
    }
}
