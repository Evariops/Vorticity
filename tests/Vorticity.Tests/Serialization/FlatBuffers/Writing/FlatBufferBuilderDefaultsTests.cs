// Default omission and the tri-state `= null` fields.
//
// FlatBuffers stores a field only when it differs from the schema default; the reader then returns
// the default from the absent slot. That is invisible for a plain `uint64 x`, but ArrayStats in
// the array schema declares `is_sorted: bool = null`, `null_count: uint64 = null` and
// `nan_count: uint64 = null`, where absent means "nobody computed it" and present-and-zero means
// "computed, and it is zero". Those are untrusted hints that results and fast paths rely on: a
// lying one cannot corrupt memory, but conflating "unknown" with "zero" produces wrong answers.
using System;
using Vorticity.Serialization.FlatBuffers;
using Xunit;

namespace Vorticity.Tests.Serialization.FlatBuffers;

public sealed class FlatBufferBuilderDefaultsTests
{
    [Fact]
    public void A_scalar_equal_to_its_default_is_omitted()
    {
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 0);            // == the implicit default 0
        builder.AddInt32(1, 0, 5);         // != the declared default 5
        builder.AddInt32(2, 5, 5);         // == the declared default 5
        builder.AddBool(3, false);
        builder.AddUInt8(4, 0);
        builder.AddFloat64(5, 0.0);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.False(table.HasField(0));
        Assert.True(table.HasField(1));
        Assert.False(table.HasField(2));
        Assert.False(table.HasField(3));
        Assert.False(table.HasField(4));
        Assert.False(table.HasField(5));

        // Absent still reads back as the schema default, which is the whole point.
        Assert.Equal(0, table.GetInt32(0));
        Assert.Equal(0, table.GetInt32(1, 5));
        Assert.Equal(5, table.GetInt32(2, 5));

        // Only the one field that differed from its default is encoded at all.
        int vtablePos = BuiltFlatBuffer.VTableOf(bytes, BuiltFlatBuffer.RootTable(bytes));
        Assert.Equal(2, BuiltFlatBuffer.SlotCount(bytes, vtablePos));
    }

    [Fact]
    public void ForceDefaults_emits_the_same_fields_verbatim()
    {
        using var builder = new FlatBufferBuilder { ForceDefaults = true };
        builder.StartTable();
        builder.AddInt32(0, 0);
        builder.AddInt32(1, 0, 5);
        builder.AddInt32(2, 5, 5);
        builder.AddBool(3, false);
        builder.AddUInt8(4, 0);
        builder.AddFloat64(5, 0.0);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        for (int fieldId = 0; fieldId <= 5; fieldId++)
        {
            Assert.True(table.HasField(fieldId), $"field {fieldId} must be present under ForceDefaults");
        }

        // A present zero must still read back as zero even when the caller asks for a non-zero
        // default: presence wins over the default.
        Assert.Equal(0, table.GetInt32(0, 99));
        Assert.Equal(0, table.GetInt32(1, 99));
        Assert.Equal(5, table.GetInt32(2, 99));
    }

    [Fact]
    public void ForceDefaults_can_be_toggled_between_tables()
    {
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddInt32(0, 0);
        int lean = builder.EndTable();

        builder.ForceDefaults = true;
        builder.StartTable();
        builder.AddInt32(0, 0);
        int fat = builder.EndTable();
        builder.ForceDefaults = false;

        builder.StartTable();
        builder.AddOffset(0, lean);
        builder.AddOffset(1, fat);
        builder.AddUInt8(2, 7);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.False(table.GetTable(0).HasField(0));
        Assert.True(table.GetTable(1).HasField(0));
        Assert.Equal(0, table.GetTable(1).GetInt32(0, 42));
    }

    [Fact]
    public void ForceDefaults_does_not_turn_a_zero_offset_into_a_reference()
    {
        // AddOffset(id, 0) means "there is no such object", not "the default object". Emitting a
        // zero uoffset would be a malformed reference the reader rejects.
        using var builder = new FlatBufferBuilder { ForceDefaults = true };
        builder.StartTable();
        builder.AddInt32(0, 1);
        int child = builder.EndTable();

        builder.StartTable();
        builder.AddOffset(0, child);
        builder.AddOffset(1, 0);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.True(table.HasField(0));
        Assert.False(table.HasField(1));
        Assert.True(table.GetTable(1).IsNull);
    }

    [Fact]
    public void The_Always_variants_distinguish_absent_from_present_and_zero()
    {
        // ArrayStats field ids in the array schema:
        //   5 is_sorted (bool = null), 8 null_count (uint64 = null), 10 nan_count (uint64 = null).
        const int IsSorted = 5;
        const int NullCount = 8;
        const int NanCount = 10;

        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddBoolAlways(IsSorted, false);      // "computed: not sorted"
        builder.AddUInt64Always(NullCount, 0);       // "computed: no nulls"
        // nan_count is deliberately never added: "not computed".
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);

        Assert.True(table.TryGetBool(IsSorted, out bool isSorted));
        Assert.False(isSorted);

        Assert.True(table.TryGetUInt64(NullCount, out ulong nullCount));
        Assert.Equal(0UL, nullCount);

        Assert.False(table.TryGetUInt64(NanCount, out ulong nanCount));
        Assert.Equal(0UL, nanCount);

        // The distinction is real in the bytes, not just in the accessor.
        Assert.NotEqual(0, BuiltFlatBuffer.Slot(bytes, BuiltFlatBuffer.RootTable(bytes), IsSorted));
        Assert.NotEqual(0, BuiltFlatBuffer.Slot(bytes, BuiltFlatBuffer.RootTable(bytes), NullCount));
        Assert.Equal(0, BuiltFlatBuffer.Slot(bytes, BuiltFlatBuffer.RootTable(bytes), NanCount));
    }

    [Fact]
    public void The_plain_variants_would_have_lost_the_same_information()
    {
        // The same values through AddBool/AddUInt64 collapse "computed: zero" into "absent". This
        // is what makes the Always variants mandatory rather than stylistic.
        const int IsSorted = 5;
        const int NullCount = 8;

        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddBool(IsSorted, false);
        builder.AddUInt64(NullCount, 0);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.False(table.TryGetBool(IsSorted, out _));
        Assert.False(table.TryGetUInt64(NullCount, out _));
    }

    [Fact]
    public void AddUInt8Always_round_trips_a_zero_enum_value()
    {
        // ArrayStats.min_precision is `Precision` (uint8) whose zero value is Inexact; a producer
        // that wants to assert it rather than imply it needs the unconditional writer.
        const int MinPrecision = 1;

        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddUInt8Always(MinPrecision, 0);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.True(table.TryGetUInt8(MinPrecision, out byte precision));
        Assert.Equal((byte)0, precision);
    }

    [Fact]
    public void Negative_zero_is_omitted_against_a_positive_zero_default_and_NaN_never_is()
    {
        // IEEE says -0.0 == 0.0, so the default comparison drops the sign - exactly what flatc
        // does, and the reason ForceDefaults exists. NaN compares unequal to everything, including
        // itself, so a NaN default field is always emitted.
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddFloat32(0, -0.0f);
        builder.AddFloat64(1, -0.0);
        builder.AddFloat32(2, float.NaN);
        builder.AddFloat64(3, double.NaN, double.NaN);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.False(table.HasField(0));
        Assert.False(table.HasField(1));
        Assert.True(table.HasField(2));
        Assert.True(table.HasField(3));
        Assert.True(float.IsNaN(table.GetFloat32(2)));
        Assert.True(double.IsNaN(table.GetFloat64(3)));

        // ForceDefaults is the escape hatch when the sign bit has to survive.
        builder.Clear();
        builder.ForceDefaults = true;
        builder.StartTable();
        builder.AddFloat64(0, -0.0);
        int forced = builder.EndTable();
        byte[] forcedBytes = builder.FinishToArray(forced);
        Assert.True(double.IsNegative(FlatBufferTable.Root(forcedBytes).GetFloat64(0)));
    }

    [Fact]
    public void A_bool_reads_back_as_true_for_any_non_zero_byte()
    {
        using var builder = new FlatBufferBuilder();
        builder.StartTable();
        builder.AddBool(0, true);
        builder.AddBool(1, false, defaultValue: true);
        int root = builder.EndTable();
        byte[] bytes = builder.FinishToArray(root);

        int rootPos = BuiltFlatBuffer.RootTable(bytes);
        Assert.Equal(1, bytes[BuiltFlatBuffer.FieldPos(bytes, rootPos, 0)]);
        Assert.Equal(0, bytes[BuiltFlatBuffer.FieldPos(bytes, rootPos, 1)]);

        FlatBufferTable table = FlatBufferTable.Root(bytes);
        Assert.True(table.GetBool(0));
        Assert.False(table.GetBool(1, defaultValue: true));
    }
}
