// Scalar accessors, schema defaults and the two vtable rules that are easy to get wrong in the
// "over-reject" direction: a vtable that follows its table, and a vtable shared by two tables.
using System;
using Vorticity;
using Vorticity.Serialization.FlatBuffers;
using Xunit;
using static Vorticity.Tests.Serialization.FlatBuffers.TestFlatBuffers;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferTableTests
{
    [Fact]
    public void Reads_every_scalar_field()
    {
        FlatBufferTable table = FlatBufferTable.Root(Scalars);

        Assert.Equal((sbyte)-128, table.GetInt8(0));
        Assert.Equal((byte)255, table.GetUInt8(1));
        Assert.Equal((short)-30000, table.GetInt16(2));
        Assert.Equal((ushort)65535, table.GetUInt16(3));
        Assert.Equal(-123456, table.GetInt32(4));
        Assert.Equal(4000000000u, table.GetUInt32(5));
        Assert.Equal(long.MinValue, table.GetInt64(6));
        Assert.Equal(ulong.MaxValue, table.GetUInt64(7));
        Assert.Equal(1.5f, table.GetFloat32(8));
        Assert.Equal(-2.25d, table.GetFloat64(9));
        Assert.True(table.GetBool(10));
    }

    [Fact]
    public void Field_beyond_the_vtable_returns_the_schema_default()
    {
        // The vtable declares 12 slots, so field 12 and everything after it is absent.
        FlatBufferTable table = FlatBufferTable.Root(Scalars);

        Assert.False(table.HasField(12));
        Assert.Equal(-7, table.GetInt32(12, -7));
        Assert.Equal(99UL, table.GetUInt64(4096, 99));
        Assert.True(table.GetBool(12, defaultValue: true));
        Assert.Equal(3.5f, table.GetFloat32(12, 3.5f));
    }

    [Fact]
    public void Field_with_a_zero_slot_returns_the_schema_default()
    {
        // Field 11 IS in the vtable, but its slot is 0: absent, not malformed.
        FlatBufferTable table = FlatBufferTable.Root(Scalars);

        Assert.False(table.HasField(11));
        Assert.Equal(5UL, table.GetUInt64(11, 5));
        Assert.False(table.TryGetUInt64(11, out ulong value));
        Assert.Equal(0UL, value);
    }

    [Fact]
    public void Negative_field_id_is_treated_as_absent()
    {
        // Field ids come from our own transcribed schemas, never from the file, so a negative id
        // is a caller bug rather than malformed input: it reads as absent, it does not throw.
        FlatBufferTable table = FlatBufferTable.Root(Scalars);

        Assert.False(table.HasField(-1));
        Assert.Equal(11, table.GetInt32(-1, 11));
        Assert.Equal(11, table.GetInt32(int.MinValue, 11));
    }

    [Fact]
    public void HasField_reports_presence_per_slot()
    {
        FlatBufferTable table = FlatBufferTable.Root(Scalars);

        Assert.True(table.HasField(0));
        Assert.True(table.HasField(10));
        Assert.False(table.HasField(11));
    }

    [Fact]
    public void Tristate_accessors_distinguish_absent_from_zero()
    {
        // The array schema declares ArrayStats.is_sorted / null_count as `= null` fields, where
        // "absent" means unknown and must not collapse into false / 0.
        FlatBufferTable table = FlatBufferTable.Root(Scalars);

        Assert.True(table.TryGetBool(10, out bool isSorted));
        Assert.True(isSorted);
        Assert.True(table.TryGetUInt64(7, out ulong nullCount));
        Assert.Equal(ulong.MaxValue, nullCount);
        Assert.True(table.TryGetUInt8(1, out byte precision));
        Assert.Equal((byte)255, precision);

        Assert.False(table.TryGetBool(11, out bool absentBool));
        Assert.False(absentBool);
        Assert.False(table.TryGetUInt8(11, out byte absentByte));
        Assert.Equal((byte)0, absentByte);
    }

    [Fact]
    public void Bool_is_true_for_any_non_zero_byte()
    {
        // [78] holds field 10's bool byte.
        byte[] bytes = With(Scalars, 78, 0x02);

        Assert.True(FlatBufferTable.Root(bytes).GetBool(10));
        Assert.False(FlatBufferTable.Root(With(Scalars, 78, 0x00)).GetBool(10, defaultValue: true));
    }

    [Fact]
    public void Vtable_may_follow_its_table()
    {
        // A negative soffset is legal: the vtable sits after the table here.
        FlatBufferTable table = FlatBufferTable.Root(VtableAfterTable);

        Assert.Equal(42, table.GetInt32(0));
        Assert.False(table.HasField(1));
    }

    [Fact]
    public void Two_tables_may_share_one_vtable()
    {
        // Vtable dedup is what every real builder does; rejecting a revisited position would
        // reject perfectly valid files.
        FlatBufferTable root = FlatBufferTable.Root(SharedVtable);

        FlatBufferTable a = root.GetTable(0);
        FlatBufferTable b = root.GetTable(1);

        Assert.False(a.IsNull);
        Assert.False(b.IsNull);
        Assert.Equal(111u, a.GetUInt32(0));
        Assert.Equal(222u, b.GetUInt32(0));
    }

    [Fact]
    public void Absent_sub_table_is_null_and_reads_as_defaults()
    {
        FlatBufferTable root = FlatBufferTable.Root(StringsAndVectors);

        FlatBufferTable absent = root.GetTable(5);

        Assert.True(absent.IsNull);
        Assert.False(absent.HasField(0));
        Assert.Equal(17, absent.GetInt32(0, 17));
        Assert.Equal(0, absent.GetVector(0).Count);
        Assert.True(absent.GetStringUtf8(0).IsEmpty);
        Assert.True(absent.GetTable(0).IsNull);
    }

    [Fact]
    public void Default_table_is_null()
    {
        FlatBufferTable table = default;

        Assert.True(table.IsNull);
        Assert.False(table.HasField(0));
        Assert.Equal(1, table.GetInt32(0, 1));
    }

    [Fact]
    public void Slot_may_end_exactly_at_table_size()
    {
        // Boundary in the accepting direction: slot 44 + a 4-byte value == table_size 48.
        // [16] is field 4's slot.
        byte[] bytes = With(Scalars, 16, 0x2C, 0x00);

        FlatBufferTable table = FlatBufferTable.Root(bytes);

        Assert.True(table.HasField(4));
        Assert.Equal(0x0001FF80, table.GetInt32(4));
    }

    [Fact]
    public void Odd_vtable_size_is_tolerated_and_the_half_slot_is_absent()
    {
        // Nothing in the format lets a reader benefit from rejecting an odd vtable_size, and
        // rejecting it would be an over-rejection: the trailing half slot is simply unaddressable.
        // [4] is vtable_size; 27 leaves 11 whole slots plus one dangling byte.
        byte[] bytes = With(Scalars, 4, 0x1B, 0x00);

        FlatBufferTable table = FlatBufferTable.Root(bytes);

        Assert.Equal(-123456, table.GetInt32(4));
        Assert.False(table.HasField(11));
    }

    /// <summary>
    /// Forward-only uoffsets exclude cycles but not SHARING: two slots may resolve to the same
    /// table, so the graph is a DAG and a consumer that walks it per path is exponential in depth
    /// while every depth, offset and bounds rule is satisfied. The optional table budget is what
    /// bounds total work — the reference verifiers' <c>max_tables</c>.
    /// </summary>
    [Fact]
    public void A_table_budget_counts_every_table_the_traversal_visits()
    {
        int budget = 10;

        FlatBufferTable root = FlatBufferTable.Root(SharedVtable, ref budget);
        Assert.Equal(9, budget);

        // Two children sharing one vtable: two tables, charged once each.
        Assert.Equal(111, root.GetTable(0).GetInt32(0));
        Assert.Equal(8, budget);
        Assert.Equal(222, root.GetTable(1).GetInt32(0));
        Assert.Equal(7, budget);

        // Re-reading the SAME child charges again: the budget counts visits, which is the point.
        Assert.Equal(111, root.GetTable(0).GetInt32(0));
        Assert.Equal(6, budget);

        FlatBufferTable other = FlatBufferTable.Root(StringsAndVectors, ref budget);
        Assert.Equal(5, budget);

        // An absent sub-table is not a table and costs nothing.
        Assert.True(other.GetTable(5).IsNull);
        Assert.Equal(5, budget);

        // Tables reached through a vector are charged too; the vector itself is not a table.
        FlatBufferVector elements = other.GetVector(2);
        Assert.Equal(5, budget);
        Assert.Equal(7, elements.GetTable(0).GetInt32(0));
        Assert.Equal(9, elements.GetTable(1).GetInt32(0));
        Assert.Equal(3, budget);
    }

    [Fact]
    public void A_traversal_past_its_table_budget_is_rejected()
    {
        int budget = 1;
        FlatBufferTable root = FlatBufferTable.Root(SharedVtable, ref budget);
        Assert.Equal(0, budget);

        try
        {
            _ = root.GetTable(0).GetInt32(0);
            Assert.Fail("A traversal past its table budget must be rejected.");
        }
        catch (VortexFormatException ex)
        {
            Assert.Contains("tables", ex.Message, StringComparison.Ordinal);
        }

        // Through a vector as well.
        int vectorBudget = 1;
        FlatBufferTable other = FlatBufferTable.Root(StringsAndVectors, ref vectorBudget);
        FlatBufferVector elements = other.GetVector(2);
        try
        {
            _ = elements.GetTable(0).GetInt32(0);
            Assert.Fail("A vector element past the table budget must be rejected.");
        }
        catch (VortexFormatException)
        {
        }

        // A budget-less root keeps working: the traversal's owner bounds its own work instead.
        Assert.Equal(111, FlatBufferTable.Root(SharedVtable).GetTable(0).GetInt32(0));
    }

    [Fact]
    public void A_table_with_no_fields_at_all_is_valid()
    {
        // The dtype schema's `table Null {}` and the footer schema's `table EncryptionSpec {}`
        // serialize to a 4-byte vtable and a 4-byte table. Rejecting that as "too short" would
        // make every Null dtype unreadable.
        FlatBufferTable table = FlatBufferTable.Root(EmptyTable);

        Assert.False(table.IsNull);
        Assert.False(table.HasField(0));
        Assert.Equal(6, table.GetInt32(0, 6));
        Assert.True(table.GetTable(0).IsNull);
    }
}
