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

/// <summary>A column as the writer writes it: its place in the schema, its Parquet types and the conversion of its values.</summary>
internal sealed class WriteColumn
{
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

    internal int MaxDefinitionLevel => Nullable ? 1 : 0;
}

/// <summary>How a column's bounds compare.</summary>
internal enum StatisticsDomain : byte
{
    None,
    Signed32,
    Signed64,
    Unsigned32,
    Unsigned64,
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
            WriteColumn column = Leaf(field.Name, i, field.Type);
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
        }

        return new WriteSchema(elements.ToArray(), columns.ToArray());
    }

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
                return new WriteColumn { Name = name, Path = path, Field = field, Physical = PhysicalType.Boolean, Nullable = nullable, Conversion = ValueConversion.Bool };
            case VortexTypeKind.Primitive:
                return Primitive(name, path, field, type.PrimitiveType, nullable);
            case VortexTypeKind.Decimal:
                return Decimal(name, path, field, type.Precision, type.Scale, nullable);
            case VortexTypeKind.Utf8:
                return new WriteColumn
                {
                    Name = name, Path = path, Field = field, Physical = PhysicalType.ByteArray, Nullable = nullable,
                    Logical = Logical(LogicalTypeKind.String), ConvertedType = (int)Metadata.ConvertedType.Utf8, Conversion = ValueConversion.ByteArray,
                };
            case VortexTypeKind.Binary:
                return new WriteColumn { Name = name, Path = path, Field = field, Physical = PhysicalType.ByteArray, Nullable = nullable, Conversion = ValueConversion.ByteArray };
            case VortexTypeKind.FixedSizeList when type.ElementType is { Kind: VortexTypeKind.Primitive, IsNullable: false } element && element.PrimitiveType == PType.U8:
                return new WriteColumn
                {
                    Name = name, Path = path, Field = field, Physical = PhysicalType.FixedLenByteArray, TypeLength = type.FixedSize, Nullable = nullable,
                    Conversion = ValueConversion.Same, ValueWidth = type.FixedSize, SourceWidth = type.FixedSize, FixedElements = true,
                };
            case VortexTypeKind.Extension:
                return Extension(name, path, field, type, nullable);
            default:
                throw new ParquetUnsupportedException(type.Kind.ToString(), ParquetComponentKind.Feature,
                    $"The column '{name}' is a {type.Kind}; this version of the writer writes flat columns only.");
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
            PType.F16 => (PhysicalType.FixedLenByteArray, Logical(LogicalTypeKind.Float16), -1, ValueConversion.Same, 2, 2, StatisticsDomain.None),
            PType.F32 => (PhysicalType.Float, default(LogicalTypeInfo), -1, ValueConversion.Same, 4, 4, StatisticsDomain.None),
            PType.F64 => (PhysicalType.Double, default(LogicalTypeInfo), -1, ValueConversion.Same, 8, 8, StatisticsDomain.None),
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
            Conversion = ValueConversion.DecimalToBigEndian, ValueWidth = bytes,
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
                    FixedElements = true,
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
