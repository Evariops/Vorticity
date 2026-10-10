using System;
using System.Collections.Generic;
using Vorticity.Parquet.Metadata;
using Vorticity.Types;

namespace Vorticity.Parquet.Writing;

/// <summary>How a column's canonical values become its PLAIN bytes.</summary>
internal enum ValueConversion : byte
{
    /// <summary>A bitmap: BOOLEAN.</summary>
    Bool,

    /// <summary>The canonical bytes are the PLAIN bytes: same width, same order.</summary>
    Same,

    /// <summary><c>i8</c> sign-extended to INT32.</summary>
    WidenInt8,

    /// <summary><c>i16</c> sign-extended to INT32.</summary>
    WidenInt16,

    /// <summary><c>u8</c> zero-extended to INT32.</summary>
    WidenUInt8,

    /// <summary><c>u16</c> zero-extended to INT32.</summary>
    WidenUInt16,

    /// <summary>A decimal of at most 9 digits, at whatever width it is stored, as INT32.</summary>
    DecimalToInt32,

    /// <summary>A decimal of at most 18 digits as INT64.</summary>
    DecimalToInt64,

    /// <summary>A wider decimal as the fewest big-endian bytes its precision needs.</summary>
    DecimalToBigEndian,

    /// <summary>Views: BYTE_ARRAY, a little-endian length then the bytes.</summary>
    ByteArray,

    /// <summary>Every value null.</summary>
    Null,
}

/// <summary>How a step of a nested column's path goes down to the field below it.</summary>
internal enum ShredKind : byte
{
    /// <summary>To a struct's field, the struct's rows its own.</summary>
    Struct,

    /// <summary>To a list's elements: its offsets and sizes say which.</summary>
    List,

    /// <summary>To a fixed-size list's elements: every row the same number of them, back to back.</summary>
    FixedList,

    /// <summary>To a map's keys or its values: its entries, a list's, then their key or value.</summary>
    Map,
}

/// <summary>
/// A field above a nested column, from the top-level one down: whether it may be null, the levels
/// it defines, and the way to the next field down.
/// </summary>
/// <param name="Kind">How the step goes down.</param>
/// <param name="Child">A struct's field, or 0 for a map's key and 1 for its value.</param>
/// <param name="Nullable">Whether the field may be null.</param>
/// <param name="DefinedAt">The definition level from which the field is not null.</param>
/// <param name="RepeatedAt">A list's or a map's repetition level: where a new element starts.</param>
/// <param name="ElementsAt">The definition level from which a list or a map holds an element.</param>
internal readonly record struct ShredStep(ShredKind Kind, int Child, bool Nullable, int DefinedAt, int RepeatedAt, int ElementsAt);

/// <summary>A column as the writer writes it: its place in the schema, its Parquet types and the conversion of its values.</summary>
internal sealed record WriteColumn
{
    private readonly int _maxDefinitionLevel = -1;

    internal required string Name { get; init; }

    internal required string[] Path { get; init; }

    /// <summary>The column's field in the root struct.</summary>
    internal int Field { get; init; }

    internal PhysicalType Physical { get; init; }

    /// <summary>A FIXED_LEN_BYTE_ARRAY's length, or -1.</summary>
    internal int TypeLength { get; init; } = -1;

    internal bool Nullable { get; init; }

    internal LogicalTypeInfo Logical { get; init; }

    internal int ConvertedType { get; init; } = -1;

    internal int Scale { get; init; } = -1;

    internal int Precision { get; init; } = -1;

    internal ValueConversion Conversion { get; init; }

    /// <summary>The bytes of one PLAIN value of a fixed width; 0 for BOOLEAN and BYTE_ARRAY.</summary>
    internal int ValueWidth { get; init; }

    /// <summary>The bytes a canonical value takes, for the conversions that read a fixed width.</summary>
    internal int SourceWidth { get; init; }

    /// <summary>Whether the canonical node is an extension whose storage holds the values.</summary>
    internal bool ThroughStorage { get; init; }

    /// <summary>
    /// Whether the values node is a fixed-size list of bytes, whose elements hold each row's value
    /// back to back: a FIXED_LEN_BYTE_ARRAY's, a UUID's and an INTERVAL's.
    /// </summary>
    internal bool FixedElements { get; init; }

    /// <summary>How min and max compare: as signed or unsigned integers, as floats, or bytewise.</summary>
    internal StatisticsDomain Domain { get; init; }

    /// <summary>The fields above a nested column, from the top-level one down; empty for a top-level column.</summary>
    internal ShredStep[] Steps { get; init; } = [];

    /// <summary>Whether the column sits under a struct, a list or a map, and is written by its levels.</summary>
    internal bool Nested => Steps.Length > 0;

    internal int MaxDefinitionLevel
    {
        get => _maxDefinitionLevel >= 0 ? _maxDefinitionLevel : Nullable ? 1 : 0;
        init => _maxDefinitionLevel = value;
    }

    internal int MaxRepetitionLevel { get; init; }
}

/// <summary>How a column's bounds compare.</summary>
internal enum StatisticsDomain : byte
{
    /// <summary>No order the standard defines: no bounds.</summary>
    None,
    Signed32,
    Signed64,
    Unsigned32,
    Unsigned64,

    /// <summary>FLOAT in IEEE 754's total order.</summary>
    Float32,

    /// <summary>DOUBLE in IEEE 754's total order.</summary>
    Float64,

    /// <summary>FLOAT16, a FIXED_LEN_BYTE_ARRAY(2), in IEEE 754's total order.</summary>
    Float16,

    /// <summary>BOOLEAN, false first.</summary>
    Boolean,

    /// <summary>BYTE_ARRAY as unsigned bytes.</summary>
    Binary,

    /// <summary>BYTE_ARRAY of UTF-8 as unsigned bytes, a cut bound kept UTF-8.</summary>
    Utf8,

    /// <summary>FIXED_LEN_BYTE_ARRAY as unsigned bytes.</summary>
    Fixed,

    /// <summary>A FIXED_LEN_BYTE_ARRAY decimal: big-endian two's complement.</summary>
    Decimal,
}

/// <summary>
/// The Vortex schema a writer is given, mapped to Parquet's: each column's physical type, its
/// annotation as a logical type and, where one corresponds, as its converted type too, as the
/// standard asks of a writer.
/// </summary>
internal sealed class WriteSchema
{
    private WriteSchema(SchemaElement[] elements, WriteColumn[] columns)
    {
        Elements = elements;
        Columns = columns;
    }

    /// <summary>The schema list the footer carries, the root first.</summary>
    internal SchemaElement[] Elements { get; }

    internal WriteColumn[] Columns { get; }

    /// <summary>Maps <paramref name="schema"/>, a struct of columns.</summary>
    /// <remarks>
    /// A struct is a group of its fields. A list is the standard's three-level structure, an
    /// optional or required group annotated <c>LIST</c> around a repeated group <c>list</c> around
    /// the <c>element</c>; a fixed-size list of other than bytes is one too, its size not kept. A
    /// map is a group annotated <c>MAP</c> around a repeated group <c>key_value</c> of a required
    /// <c>key</c> and the <c>value</c>. A variant is a group annotated <c>VARIANT(1)</c> of its
    /// <c>metadata</c> and its <c>value</c>, both required binary: the standard's unshredded form.
    /// </remarks>
    internal static WriteSchema Map(VortexSchema schema)
    {
        List<SchemaElement> elements = [];
        List<WriteColumn> columns = [];
        elements.Add(new SchemaElement
        {
            Name = "schema",
            ChildCount = schema.Count,
            TypeLength = -1,
            ConvertedType = -1,
            Scale = -1,
            Precision = -1,
        });
        for (int i = 0; i < schema.Count; i++)
        {
            VortexField field = schema[i];
            Field(field.Name, field.Type, i, new Place([field.Name], [], 0, 0), elements, columns);
        }

        return new WriteSchema(elements.ToArray(), columns.ToArray());
    }

    /// <summary>Where a field is written: its path, the fields above it, and the levels they define.</summary>
    private readonly record struct Place(string[] Path, ShredStep[] Steps, int Definition, int Repetition);

    /// <summary>Adds the elements of the field <paramref name="name"/> of <paramref name="type"/>, depth first, and its columns.</summary>
    private static void Field(string name, VortexType type, int field, Place place, List<SchemaElement> elements, List<WriteColumn> columns)
    {
        bool nullable = type.IsNullable;
        int definedAt = place.Definition + (nullable ? 1 : 0);
        FieldRepetition repetition = nullable ? FieldRepetition.Optional : FieldRepetition.Required;
        switch (type.Kind)
        {
            case VortexTypeKind.Struct:
                ReadOnlySpan<VortexField> fields = type.Fields;
                if (fields.Length == 0)
                {
                    throw new ParquetUnsupportedException("STRUCT", ParquetComponentKind.Feature,
                        $"The struct '{name}' has no field, and a Parquet group needs one to carry its rows.");
                }

                elements.Add(Group(name, repetition, fields.Length, default, -1));
                for (int i = 0; i < fields.Length; i++)
                {
                    ShredStep step = new(ShredKind.Struct, i, nullable, definedAt, 0, 0);
                    Field(fields[i].Name, fields[i].Type, field, new Place([.. place.Path, fields[i].Name], [.. place.Steps, step], definedAt, place.Repetition), elements, columns);
                }

                return;

            case VortexTypeKind.List:
            case VortexTypeKind.FixedSizeList when !IsBytes(type):
            {
                elements.Add(Group(name, repetition, 1, Logical(LogicalTypeKind.List), (int)Metadata.ConvertedType.List));
                elements.Add(Group("list", FieldRepetition.Repeated, 1, default, -1));
                ShredKind kind = type.Kind == VortexTypeKind.List ? ShredKind.List : ShredKind.FixedList;
                ShredStep step = new(kind, 0, nullable, definedAt, place.Repetition + 1, definedAt + 1);
                Place element = new([.. place.Path, "list", "element"], [.. place.Steps, step], definedAt + 1, place.Repetition + 1);
                Field("element", type.ElementType!, field, element, elements, columns);
                return;
            }

            case VortexTypeKind.Variant:
            {
                LogicalTypeInfo logical = Logical(LogicalTypeKind.Variant);
                logical.VariantVersion = 1;
                elements.Add(Group(name, repetition, 2, logical, -1));
                ReadOnlySpan<string> parts = ["metadata", "value"];
                for (int i = 0; i < parts.Length; i++)
                {
                    ShredStep step = new(ShredKind.Struct, i, nullable, definedAt, 0, 0);
                    Field(parts[i], VortexType.Binary, field, new Place([.. place.Path, parts[i]], [.. place.Steps, step], definedAt, place.Repetition), elements, columns);
                }

                return;
            }

            case VortexTypeKind.Map:
            {
                elements.Add(Group(name, repetition, 1, Logical(LogicalTypeKind.Map), (int)Metadata.ConvertedType.Map));
                elements.Add(Group("key_value", FieldRepetition.Repeated, 2, default, -1));
                ReadOnlySpan<VortexField> entry = type.Fields;
                for (int i = 0; i < 2; i++)
                {
                    ShredStep step = new(ShredKind.Map, i, nullable, definedAt, place.Repetition + 1, definedAt + 1);
                    Place child = new([.. place.Path, "key_value", entry[i].Name], [.. place.Steps, step], definedAt + 1, place.Repetition + 1);
                    Field(entry[i].Name, entry[i].Type, field, child, elements, columns);
                }

                return;
            }

            default:
                WriteColumn column = Leaf(name, field, type);
                if (place.Steps.Length > 0)
                {
                    column = column with
                    {
                        Path = place.Path,
                        Steps = place.Steps,
                        MaxDefinitionLevel = definedAt,
                        MaxRepetitionLevel = place.Repetition,
                    };
                }

                columns.Add(column);
                elements.Add(new SchemaElement
                {
                    Name = column.Name,
                    HasType = true,
                    Type = column.Physical,
                    TypeLength = column.TypeLength,
                    HasRepetition = true,
                    Repetition = column.Nullable ? FieldRepetition.Optional : FieldRepetition.Required,
                    ChildCount = -1,
                    ConvertedType = column.ConvertedType,
                    Scale = column.Scale,
                    Precision = column.Precision,
                    LogicalType = column.Logical,
                });
                return;
        }
    }

    private static SchemaElement Group(string name, FieldRepetition repetition, int children, LogicalTypeInfo logical, int converted) => new()
    {
        Name = name,
        HasRepetition = true,
        Repetition = repetition,
        ChildCount = children,
        TypeLength = -1,
        ConvertedType = converted,
        Scale = -1,
        Precision = -1,
        LogicalType = logical,
    };

    /// <summary>Whether a fixed-size list is of bytes that may not be null: a FIXED_LEN_BYTE_ARRAY.</summary>
    private static bool IsBytes(VortexType type) =>
        type.ElementType is { Kind: VortexTypeKind.Primitive, IsNullable: false } element && element.PrimitiveType == PType.U8;

    private static WriteColumn Leaf(string name, int field, VortexType type)
    {
        bool nullable = type.IsNullable;
        string[] path = [name];
        switch (type.Kind)
        {
            case VortexTypeKind.Null:
                return new WriteColumn
                {
                    Name = name, Path = path, Field = field, Physical = PhysicalType.Int32, Nullable = true,
                    Logical = Logical(LogicalTypeKind.Unknown), Conversion = ValueConversion.Null, ValueWidth = 4,
                };
            case VortexTypeKind.Bool:
                return new WriteColumn { Name = name, Path = path, Field = field, Physical = PhysicalType.Boolean, Nullable = nullable, Conversion = ValueConversion.Bool, Domain = StatisticsDomain.Boolean };
            case VortexTypeKind.Primitive:
                return Primitive(name, path, field, type.PrimitiveType, nullable);
            case VortexTypeKind.Decimal:
                return Decimal(name, path, field, type.Precision, type.Scale, nullable);
            case VortexTypeKind.Utf8:
                return new WriteColumn
                {
                    Name = name, Path = path, Field = field, Physical = PhysicalType.ByteArray, Nullable = nullable,
                    Logical = Logical(LogicalTypeKind.String), ConvertedType = (int)Metadata.ConvertedType.Utf8, Conversion = ValueConversion.ByteArray,
                    Domain = StatisticsDomain.Utf8,
                };
            case VortexTypeKind.Binary:
                return new WriteColumn { Name = name, Path = path, Field = field, Physical = PhysicalType.ByteArray, Nullable = nullable, Conversion = ValueConversion.ByteArray, Domain = StatisticsDomain.Binary };
            case VortexTypeKind.FixedSizeList when IsBytes(type):
                return new WriteColumn
                {
                    Name = name, Path = path, Field = field, Physical = PhysicalType.FixedLenByteArray, TypeLength = type.FixedSize, Nullable = nullable,
                    Conversion = ValueConversion.Same, ValueWidth = type.FixedSize, SourceWidth = type.FixedSize, FixedElements = true,
                    Domain = StatisticsDomain.Fixed,
                };
            case VortexTypeKind.Extension:
                return Extension(name, path, field, type, nullable);
            default:
                throw new ParquetUnsupportedException(type.Kind.ToString(), ParquetComponentKind.Feature,
                    $"The column '{name}' is a {type.Kind}, which has no Parquet type to be written as.");
        }
    }

    private static WriteColumn Primitive(string name, string[] path, int field, PType ptype, bool nullable)
    {
        (PhysicalType physical, LogicalTypeInfo logical, int converted, ValueConversion conversion, int width, int source, StatisticsDomain domain) = ptype switch
        {
            PType.I8 => (PhysicalType.Int32, Integer(8, true), (int)Metadata.ConvertedType.Int8, ValueConversion.WidenInt8, 4, 1, StatisticsDomain.Signed32),
            PType.I16 => (PhysicalType.Int32, Integer(16, true), (int)Metadata.ConvertedType.Int16, ValueConversion.WidenInt16, 4, 2, StatisticsDomain.Signed32),
            PType.I32 => (PhysicalType.Int32, default(LogicalTypeInfo), -1, ValueConversion.Same, 4, 4, StatisticsDomain.Signed32),
            PType.I64 => (PhysicalType.Int64, default(LogicalTypeInfo), -1, ValueConversion.Same, 8, 8, StatisticsDomain.Signed64),
            PType.U8 => (PhysicalType.Int32, Integer(8, false), (int)Metadata.ConvertedType.UInt8, ValueConversion.WidenUInt8, 4, 1, StatisticsDomain.Unsigned32),
            PType.U16 => (PhysicalType.Int32, Integer(16, false), (int)Metadata.ConvertedType.UInt16, ValueConversion.WidenUInt16, 4, 2, StatisticsDomain.Unsigned32),
            PType.U32 => (PhysicalType.Int32, Integer(32, false), (int)Metadata.ConvertedType.UInt32, ValueConversion.Same, 4, 4, StatisticsDomain.Unsigned32),
            PType.U64 => (PhysicalType.Int64, Integer(64, false), (int)Metadata.ConvertedType.UInt64, ValueConversion.Same, 8, 8, StatisticsDomain.Unsigned64),
            PType.F16 => (PhysicalType.FixedLenByteArray, Logical(LogicalTypeKind.Float16), -1, ValueConversion.Same, 2, 2, StatisticsDomain.Float16),
            PType.F32 => (PhysicalType.Float, default(LogicalTypeInfo), -1, ValueConversion.Same, 4, 4, StatisticsDomain.Float32),
            PType.F64 => (PhysicalType.Double, default(LogicalTypeInfo), -1, ValueConversion.Same, 8, 8, StatisticsDomain.Float64),
            _ => throw new ArgumentOutOfRangeException(nameof(ptype), ptype, "Not a primitive type."),
        };
        return new WriteColumn
        {
            Name = name, Path = path, Field = field, Physical = physical, TypeLength = ptype == PType.F16 ? 2 : -1, Nullable = nullable,
            Logical = logical, ConvertedType = converted, Conversion = conversion, ValueWidth = width, SourceWidth = source, Domain = domain,
        };
    }

    private static WriteColumn Decimal(string name, string[] path, int field, int precision, int scale, bool nullable)
    {
        if (scale < 0)
        {
            throw new ParquetUnsupportedException($"DECIMAL({precision}, {scale})", ParquetComponentKind.LogicalType,
                $"The column '{name}' has a negative scale, which a Parquet decimal cannot hold.");
        }

        LogicalTypeInfo logical = Logical(LogicalTypeKind.Decimal);
        logical.Precision = precision;
        logical.Scale = scale;
        if (precision <= 9)
        {
            return new WriteColumn
            {
                Name = name, Path = path, Field = field, Physical = PhysicalType.Int32, Nullable = nullable, Logical = logical,
                ConvertedType = (int)Metadata.ConvertedType.Decimal, Scale = scale, Precision = precision,
                Conversion = ValueConversion.DecimalToInt32, ValueWidth = 4, Domain = StatisticsDomain.Signed32,
            };
        }

        if (precision <= 18)
        {
            return new WriteColumn
            {
                Name = name, Path = path, Field = field, Physical = PhysicalType.Int64, Nullable = nullable, Logical = logical,
                ConvertedType = (int)Metadata.ConvertedType.Decimal, Scale = scale, Precision = precision,
                Conversion = ValueConversion.DecimalToInt64, ValueWidth = 8, Domain = StatisticsDomain.Signed64,
            };
        }

        int bytes = 1;
        while (Digits(bytes) < precision)
        {
            bytes++;
        }

        return new WriteColumn
        {
            Name = name, Path = path, Field = field, Physical = PhysicalType.FixedLenByteArray, TypeLength = bytes, Nullable = nullable,
            Logical = logical, ConvertedType = (int)Metadata.ConvertedType.Decimal, Scale = scale, Precision = precision,
            Conversion = ValueConversion.DecimalToBigEndian, ValueWidth = bytes, Domain = StatisticsDomain.Decimal,
        };
    }

    /// <summary>The digits a two's-complement number of <paramref name="bytes"/> bytes holds.</summary>
    private static int Digits(int bytes) =>
        (int)Math.Floor(System.Numerics.BigInteger.Log10((System.Numerics.BigInteger.One << (8 * bytes - 1)) - 1) + 1e-9);

    private static WriteColumn Extension(string name, string[] path, int field, VortexType type, bool nullable)
    {
        switch (type.ExtensionId)
        {
            case ExtensionIds.Date:
                if (type.Unit != TimeUnit.Days)
                {
                    throw new ParquetUnsupportedException("DATE", ParquetComponentKind.LogicalType,
                        $"The column '{name}' counts its dates in {type.Unit}; a Parquet DATE counts days.");
                }

                return new WriteColumn
                {
                    Name = name, Path = path, Field = field, Physical = PhysicalType.Int32, Nullable = nullable, Logical = Logical(LogicalTypeKind.Date),
                    ConvertedType = (int)Metadata.ConvertedType.Date, Conversion = ValueConversion.Same, ValueWidth = 4, SourceWidth = 4,
                    ThroughStorage = true, Domain = StatisticsDomain.Signed32,
                };
            case ExtensionIds.Time:
            case ExtensionIds.Timestamp:
                return Temporal(name, path, field, type, nullable);
            case ExtensionIds.Uuid:
                return new WriteColumn
                {
                    Name = name, Path = path, Field = field, Physical = PhysicalType.FixedLenByteArray, TypeLength = 16, Nullable = nullable,
                    Logical = Logical(LogicalTypeKind.Uuid), Conversion = ValueConversion.Same, ValueWidth = 16, SourceWidth = 16, ThroughStorage = true,
                    FixedElements = true, Domain = StatisticsDomain.Fixed,
                };
            case Schema.ParquetSchema.IntervalExtensionId:
                return new WriteColumn
                {
                    Name = name, Path = path, Field = field, Physical = PhysicalType.FixedLenByteArray, TypeLength = 12, Nullable = nullable,
                    ConvertedType = (int)Metadata.ConvertedType.Interval, Conversion = ValueConversion.Same, ValueWidth = 12, SourceWidth = 12, ThroughStorage = true,
                    FixedElements = true,
                };
            case Schema.ParquetSchema.Int96ExtensionId:
                throw new ParquetUnsupportedException("INT96", ParquetComponentKind.PhysicalType, "The standard deprecates INT96 for writers.");
            default:
                WriteColumn storage = Leaf(name, field, type.StorageType!);
                return new WriteColumn
                {
                    Name = storage.Name, Path = storage.Path, Field = field, Physical = storage.Physical, TypeLength = storage.TypeLength,
                    Nullable = nullable, Logical = storage.Logical, ConvertedType = storage.ConvertedType, Scale = storage.Scale,
                    Precision = storage.Precision, Conversion = storage.Conversion, ValueWidth = storage.ValueWidth,
                    SourceWidth = storage.SourceWidth, ThroughStorage = true, FixedElements = storage.FixedElements, Domain = storage.Domain,
                };
        }
    }

    private static WriteColumn Temporal(string name, string[] path, int field, VortexType type, bool nullable)
    {
        bool timestamp = type.ExtensionId == ExtensionIds.Timestamp;
        TimeUnit unit = type.Unit ?? TimeUnit.Microseconds;
        ParquetTimeUnit parquetUnit = unit switch
        {
            TimeUnit.Milliseconds => ParquetTimeUnit.Millis,
            TimeUnit.Microseconds => ParquetTimeUnit.Micros,
            TimeUnit.Nanoseconds => ParquetTimeUnit.Nanos,
            _ => throw new ParquetUnsupportedException(unit.ToString(), ParquetComponentKind.LogicalType,
                $"The column '{name}' counts {unit}, a unit Parquet's TIME and TIMESTAMP do not have."),
        };
        LogicalTypeInfo logical = Logical(timestamp ? LogicalTypeKind.Timestamp : LogicalTypeKind.Time);
        logical.Unit = parquetUnit;
        logical.IsAdjustedToUtc = timestamp && type.TimeZone is not null;

        // The forward-compatibility tables annotate a local time and timestamp with the legacy
        // annotation too; nanoseconds have none.
        int converted = (timestamp, parquetUnit) switch
        {
            (true, ParquetTimeUnit.Millis) => (int)Metadata.ConvertedType.TimestampMillis,
            (true, ParquetTimeUnit.Micros) => (int)Metadata.ConvertedType.TimestampMicros,
            (false, ParquetTimeUnit.Millis) => (int)Metadata.ConvertedType.TimeMillis,
            (false, ParquetTimeUnit.Micros) => (int)Metadata.ConvertedType.TimeMicros,
            _ => -1,
        };
        bool narrow = !timestamp && parquetUnit == ParquetTimeUnit.Millis;
        return new WriteColumn
        {
            Name = name, Path = path, Field = field, Physical = narrow ? PhysicalType.Int32 : PhysicalType.Int64, Nullable = nullable,
            Logical = logical, ConvertedType = converted, Conversion = ValueConversion.Same, ValueWidth = narrow ? 4 : 8,
            SourceWidth = narrow ? 4 : 8, ThroughStorage = true, Domain = narrow ? StatisticsDomain.Signed32 : StatisticsDomain.Signed64,
        };
    }

    private static LogicalTypeInfo Logical(LogicalTypeKind kind) => new() { Kind = kind, VariantVersion = -1, EdgeAlgorithm = -1 };

    private static LogicalTypeInfo Integer(byte bits, bool signed)
    {
        LogicalTypeInfo info = Logical(LogicalTypeKind.Integer);
        info.BitWidth = bits;
        info.IsSigned = signed;
        return info;
    }
}
