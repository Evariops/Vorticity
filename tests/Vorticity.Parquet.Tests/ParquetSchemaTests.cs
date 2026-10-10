using System;
using Vorticity.Parquet;
using Vorticity.Parquet.Metadata;
using Vorticity.Parquet.Schema;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// The schema compiler: levels down the tree, the standard's list and map structures and their
/// backward-compatibility rules, annotations from logical and converted types, and refusals.
/// </summary>
public sealed class ParquetSchemaTests
{
    private static SchemaElement Group(string name, int children, FieldRepetition? repetition = FieldRepetition.Required, LogicalTypeKind logical = LogicalTypeKind.None, int converted = -1) =>
        Element(name, repetition, children, type: null, logical: logical, converted: converted);

    private static SchemaElement Leaf(string name, PhysicalType type, FieldRepetition repetition = FieldRepetition.Required, LogicalTypeInfo logical = default, int converted = -1, int length = -1, int scale = -1, int precision = -1) =>
        Element(name, repetition, -1, type, logical.Kind, converted, length, logical, scale, precision);

    private static SchemaElement Element(string name, FieldRepetition? repetition, int children, PhysicalType? type, LogicalTypeKind logical, int converted = -1, int length = -1, LogicalTypeInfo info = default, int scale = -1, int precision = -1)
    {
        info.Kind = logical;
        return new SchemaElement
        {
            Name = name,
            HasRepetition = repetition.HasValue,
            Repetition = repetition ?? FieldRepetition.Required,
            ChildCount = children,
            HasType = type.HasValue,
            Type = type ?? PhysicalType.Boolean,
            TypeLength = length,
            ConvertedType = converted,
            Scale = scale,
            Precision = precision,
            LogicalType = info,
        };
    }

    private static ParquetSchema Compile(params SchemaElement[] elements) => ParquetSchema.Compile(elements, []);

    private static SchemaElement Root(int children) => Group("schema", children, repetition: null);

    [Fact]
    public void AFlatSchemaCompilesToItsColumnsAndLevels()
    {
        LogicalTypeInfo text = new() { Kind = LogicalTypeKind.String };
        LogicalTypeInfo instant = new() { Kind = LogicalTypeKind.Timestamp, Unit = ParquetTimeUnit.Micros, IsAdjustedToUtc = true };
        ParquetSchema schema = Compile(
            Root(3),
            Leaf("id", PhysicalType.Int32),
            Leaf("name", PhysicalType.ByteArray, FieldRepetition.Optional, text),
            Leaf("at", PhysicalType.Int64, FieldRepetition.Optional, instant));

        Assert.Equal(3, schema.Columns.Length);
        Assert.Equal((0, 0), (schema.Columns[0].MaxDefinitionLevel, schema.Columns[0].MaxRepetitionLevel));
        Assert.Equal((1, 0), (schema.Columns[1].MaxDefinitionLevel, schema.Columns[1].MaxRepetitionLevel));
        Assert.Equal(VortexType.Int32, schema.Vortex[0].Type);
        Assert.Equal(VortexType.Utf8.Nullable, schema.Vortex[1].Type);
        Assert.Equal(VortexType.Timestamp(TimeUnit.Microseconds, "UTC").Nullable, schema.Vortex[2].Type);
        Assert.Equal(LeafForm.Fixed, schema.Columns[0].Form);
        Assert.Equal(LeafForm.Utf8, schema.Columns[1].Form);
        Assert.Equal(["at"], schema.Columns[2].Path);
    }

    [Fact]
    public void AThreeLevelListIsAListOfItsElement()
    {
        LogicalTypeInfo text = new() { Kind = LogicalTypeKind.String };
        ParquetSchema schema = Compile(
            Root(1),
            Group("my_list", 1, FieldRepetition.Optional, LogicalTypeKind.List),
            Group("list", 1, FieldRepetition.Repeated),
            Leaf("element", PhysicalType.ByteArray, FieldRepetition.Optional, text));

        ParquetField list = schema.Fields[0];
        Assert.Equal(FieldShape.List, list.Shape);
        Assert.Equal(VortexType.List(VortexType.Utf8.Nullable).Nullable, list.Type);
        Assert.Equal((1, 1, 2), (list.DefinedAt, list.RepeatedAt, list.ElementsAt));
        Assert.Equal(3, list.Children[0].DefinedAt);
        Assert.Equal((3, 1), (schema.Columns[0].MaxDefinitionLevel, schema.Columns[0].MaxRepetitionLevel));
        Assert.Equal(["my_list", "list", "element"], schema.Columns[0].Path);
    }

    [Fact]
    public void TheFiveBackwardCompatibilityRulesFindTheElement()
    {
        LogicalTypeInfo text = new() { Kind = LogicalTypeKind.String };

        // Rule 1: a repeated leaf is the element, required.
        ParquetSchema rule1 = Compile(
            Root(1),
            Group("my_list", 1, FieldRepetition.Optional, LogicalTypeKind.List),
            Leaf("element", PhysicalType.Int32, FieldRepetition.Repeated));
        Assert.Equal(VortexType.List(VortexType.Int32).Nullable, rule1.Fields[0].Type);

        // Rule 2: a repeated group of several fields is the element.
        ParquetSchema rule2 = Compile(
            Root(1),
            Group("my_list", 1, FieldRepetition.Optional, LogicalTypeKind.List),
            Group("element", 2, FieldRepetition.Repeated),
            Leaf("str", PhysicalType.ByteArray, FieldRepetition.Required, text),
            Leaf("num", PhysicalType.Int32));
        Assert.Equal(VortexType.List(VortexType.Struct([("str", VortexType.Utf8), ("num", VortexType.Int32)])).Nullable, rule2.Fields[0].Type);

        // Rule 3: a repeated group whose one field repeats is the element: a list of lists.
        ParquetSchema rule3 = Compile(
            Root(1),
            Group("my_list", 1, FieldRepetition.Optional, LogicalTypeKind.List),
            Group("array", 1, FieldRepetition.Repeated, LogicalTypeKind.List),
            Leaf("array", PhysicalType.Int32, FieldRepetition.Repeated));
        Assert.Equal(VortexType.List(VortexType.List(VortexType.Int32)).Nullable, rule3.Fields[0].Type);

        // Rule 4: a repeated group named array, or the list's name and _tuple, is the element.
        ParquetSchema rule4 = Compile(
            Root(1),
            Group("my_list", 1, FieldRepetition.Optional, LogicalTypeKind.List),
            Group("my_list_tuple", 1, FieldRepetition.Repeated),
            Leaf("str", PhysicalType.ByteArray, FieldRepetition.Required, text));
        Assert.Equal(VortexType.List(VortexType.Struct([("str", VortexType.Utf8)])).Nullable, rule4.Fields[0].Type);

        // Rule 5: otherwise the repeated group's one field is the element, with its repetition.
        ParquetSchema rule5 = Compile(
            Root(1),
            Group("my_list", 1, FieldRepetition.Optional, LogicalTypeKind.List),
            Group("element", 1, FieldRepetition.Repeated),
            Leaf("str", PhysicalType.ByteArray, FieldRepetition.Optional, text));
        Assert.Equal(VortexType.List(VortexType.Utf8.Nullable).Nullable, rule5.Fields[0].Type);
    }

    [Fact]
    public void ARepeatedFieldOutsideAListIsARequiredListOfRequiredElements()
    {
        ParquetSchema schema = Compile(
            Root(1),
            Leaf("num", PhysicalType.Int32, FieldRepetition.Repeated));

        ParquetField list = schema.Fields[0];
        Assert.Equal(VortexType.List(VortexType.Int32), list.Type);
        Assert.Equal((0, 1, 1), (list.DefinedAt, list.RepeatedAt, list.ElementsAt));
    }

    [Fact]
    public void AMapIsItsKeyAndValueByPosition()
    {
        LogicalTypeInfo text = new() { Kind = LogicalTypeKind.String };
        ParquetSchema schema = Compile(
            Root(1),
            Group("my_map", 1, FieldRepetition.Required, LogicalTypeKind.Map),
            Group("map", 2, FieldRepetition.Repeated),
            Leaf("str", PhysicalType.ByteArray, FieldRepetition.Required, text),
            Leaf("num", PhysicalType.Int32, FieldRepetition.Optional));

        ParquetField map = schema.Fields[0];
        Assert.Equal(FieldShape.Map, map.Shape);
        Assert.Equal(VortexType.Map(VortexType.Utf8, VortexType.Int32.Nullable), map.Type);
        Assert.Equal((0, 1, 1), (map.DefinedAt, map.RepeatedAt, map.ElementsAt));
    }

    [Fact]
    public void AMapKeyValueGroupOutsideAMapIsAMap()
    {
        ParquetSchema schema = Compile(
            Root(1),
            Group("my_map", 1, FieldRepetition.Optional, converted: (int)ConvertedType.MapKeyValue),
            Group("map", 2, FieldRepetition.Repeated),
            Leaf("key", PhysicalType.Int64),
            Leaf("value", PhysicalType.Double, FieldRepetition.Optional));

        Assert.Equal(VortexType.Map(VortexType.Int64, VortexType.Float64.Nullable).Nullable, schema.Fields[0].Type);
    }

    [Fact]
    public void ConvertedTypesAloneReadAsTheirLogicalTypes()
    {
        ParquetSchema schema = Compile(
            Root(4),
            Leaf("text", PhysicalType.ByteArray, converted: (int)ConvertedType.Utf8),
            Leaf("money", PhysicalType.FixedLenByteArray, converted: (int)ConvertedType.Decimal, length: 9, scale: 2, precision: 20),
            Leaf("at", PhysicalType.Int64, converted: (int)ConvertedType.TimestampMillis),
            Leaf("small", PhysicalType.Int32, converted: (int)ConvertedType.UInt8));

        Assert.Equal(VortexType.Utf8, schema.Vortex[0].Type);
        Assert.Equal(VortexType.Decimal(20, 2), schema.Vortex[1].Type);
        Assert.Equal(LeafForm.BigEndianDecimal, schema.Columns[1].Form);
        Assert.Equal(VortexType.Timestamp(TimeUnit.Milliseconds, "UTC"), schema.Vortex[2].Type);
        Assert.Equal(VortexType.UInt8, schema.Vortex[3].Type);
        Assert.Equal(LeafForm.Narrowed, schema.Columns[3].Form);
    }

    [Fact]
    public void AnAnnotationThePhysicalTypeDoesNotAllowIsDroppedWithItsOrder()
    {
        LogicalTypeInfo date = new() { Kind = LogicalTypeKind.Date };
        ParquetSchema schema = ParquetSchema.Compile(
            [Root(1), Leaf("d", PhysicalType.Int64, logical: date)],
            [ColumnOrderKind.TypeDefined]);

        Assert.Equal(VortexType.Int64, schema.Vortex[0].Type);
        Assert.Equal(ColumnOrderKind.Unrecognized, schema.Columns[0].Order);
    }

    /// <summary>
    /// Column orders fewer or more than the leaves, as a flipped bit of the footer's list makes them:
    /// refused as malformed before a leaf looks for its own, which a shorter list would not hold.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ColumnOrdersThatAreNotOneALeafAreRefused(int count)
    {
        ColumnOrderKind[] orders = new ColumnOrderKind[count];
        Array.Fill(orders, ColumnOrderKind.TypeDefined);
        ParquetFormatException refused = Assert.Throws<ParquetFormatException>(
            () => ParquetSchema.Compile([Root(2), Leaf("a", PhysicalType.Int64), Leaf("b", PhysicalType.Double)], orders));
        Assert.Contains($"{count} column orders for 2 columns", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFixedLengthDecimalHoldsTheDigitsItsBytesAllow()
    {
        LogicalTypeInfo fits = new() { Kind = LogicalTypeKind.Decimal, Precision = 38, Scale = 4 };
        LogicalTypeInfo past = new() { Kind = LogicalTypeKind.Decimal, Precision = 39, Scale = 4 };

        ParquetSchema schema = Compile(Root(1), Leaf("v", PhysicalType.FixedLenByteArray, logical: fits, length: 16));
        Assert.Equal(VortexType.Decimal(38, 4), schema.Vortex[0].Type);
        Assert.Throws<ParquetFormatException>(() => Compile(Root(1), Leaf("v", PhysicalType.FixedLenByteArray, logical: past, length: 16)));
    }

    [Fact]
    public void Int96AndIntervalReadAsTheirBytes()
    {
        ParquetSchema schema = Compile(
            Root(2),
            Leaf("legacy", PhysicalType.Int96),
            Leaf("span", PhysicalType.FixedLenByteArray, converted: (int)ConvertedType.Interval, length: 12));

        Assert.Equal(ParquetSchema.Int96ExtensionId, schema.Vortex[0].Type.ExtensionId);
        Assert.Equal(ParquetSchema.IntervalExtensionId, schema.Vortex[1].Type.ExtensionId);
        Assert.Equal(12, schema.Columns[0].TypeLength);
    }

    [Fact]
    public void ASchemaWhoseGroupsClaimMoreThanTheListIsMalformed() =>
        Assert.Throws<ParquetFormatException>(() => Compile(Root(3), Leaf("a", PhysicalType.Int32), Leaf("b", PhysicalType.Int32)));

    [Fact]
    public void ASchemaWithElementsNoGroupClaimsIsMalformed() =>
        Assert.Throws<ParquetFormatException>(() => Compile(Root(1), Leaf("a", PhysicalType.Int32), Leaf("b", PhysicalType.Int32)));

    [Fact]
    public void ALeafWithoutAPhysicalTypeIsMalformed() =>
        Assert.Throws<ParquetFormatException>(() => Compile(Root(1), Element("a", FieldRepetition.Required, -1, null, LogicalTypeKind.None)));

    [Fact]
    public void ASchemaNestingPastSixtyFourLevelsIsMalformed()
    {
        SchemaElement[] elements = new SchemaElement[70];
        elements[0] = Root(1);
        for (int i = 1; i < 69; i++)
        {
            elements[i] = Group("g" + i, 1);
        }

        elements[69] = Leaf("x", PhysicalType.Int32);
        Assert.Throws<ParquetFormatException>(() => Compile(elements));
    }

    [Fact]
    public void AGroupWithNoFieldIsUnsupported() =>
        Assert.Throws<ParquetUnsupportedException>(() => Compile(Root(1), Group("empty", 0)));
}
