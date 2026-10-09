using System;
using System.Text;
using Vorticity.Parquet.Thrift;

namespace Vorticity.Parquet.Metadata;

/// <summary>A <c>LogicalType</c> annotation, the member of the union and its parameters.</summary>
internal struct LogicalTypeInfo
{
    internal LogicalTypeKind Kind;

    /// <summary>A <c>TIME</c>'s or a <c>TIMESTAMP</c>'s unit.</summary>
    internal ParquetTimeUnit Unit;

    /// <summary>Whether a <c>TIME</c> or a <c>TIMESTAMP</c> is normalized to UTC.</summary>
    internal bool IsAdjustedToUtc;

    /// <summary>An <c>INTEGER</c>'s width: 8, 16, 32 or 64.</summary>
    internal byte BitWidth;

    /// <summary>Whether an <c>INTEGER</c> is signed.</summary>
    internal bool IsSigned;

    /// <summary>A <c>DECIMAL</c>'s scale.</summary>
    internal int Scale;

    /// <summary>A <c>DECIMAL</c>'s precision.</summary>
    internal int Precision;

    /// <summary>A <c>VARIANT</c>'s specification version, or -1.</summary>
    internal int VariantVersion;

    /// <summary>A <c>GEOMETRY</c>'s or a <c>GEOGRAPHY</c>'s coordinate reference system, or null for the default.</summary>
    internal string? Crs;

    /// <summary>A <c>GEOGRAPHY</c>'s edge interpolation algorithm, or -1 for the default.</summary>
    internal int EdgeAlgorithm;

    /// <summary>Reads the <c>LogicalType</c> union; a member this build does not know is <see cref="LogicalTypeKind.Unrecognized"/>.</summary>
    internal static LogicalTypeInfo Read(ref ThriftCompactReader reader)
    {
        LogicalTypeInfo info = default;
        info.VariantVersion = -1;
        info.EdgeAlgorithm = -1;
        int members = 0;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            members++;
            ThriftCompactReader.Expect(type, ThriftType.Struct);
            switch (id)
            {
                case (short)LogicalTypeKind.Decimal:
                    info.Kind = LogicalTypeKind.Decimal;
                    ReadDecimal(ref reader, ref info);
                    break;
                case (short)LogicalTypeKind.Time:
                case (short)LogicalTypeKind.Timestamp:
                    info.Kind = (LogicalTypeKind)id;
                    ReadTemporal(ref reader, ref info);
                    break;
                case (short)LogicalTypeKind.Integer:
                    info.Kind = LogicalTypeKind.Integer;
                    ReadInteger(ref reader, ref info);
                    break;
                case (short)LogicalTypeKind.Variant:
                    info.Kind = LogicalTypeKind.Variant;
                    ReadVariant(ref reader, ref info);
                    break;
                case (short)LogicalTypeKind.Geometry:
                case (short)LogicalTypeKind.Geography:
                    info.Kind = (LogicalTypeKind)id;
                    ReadGeospatial(ref reader, ref info);
                    break;
                case (short)LogicalTypeKind.String:
                case (short)LogicalTypeKind.Map:
                case (short)LogicalTypeKind.List:
                case (short)LogicalTypeKind.Enum:
                case (short)LogicalTypeKind.Date:
                case (short)LogicalTypeKind.Unknown:
                case (short)LogicalTypeKind.Json:
                case (short)LogicalTypeKind.Bson:
                case (short)LogicalTypeKind.Uuid:
                case (short)LogicalTypeKind.Float16:
                case (short)LogicalTypeKind.File:
                    info.Kind = (LogicalTypeKind)id;
                    reader.Skip(type);
                    break;
                default:
                    info.Kind = LogicalTypeKind.Unrecognized;
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (members > 1)
        {
            ParquetThrow.Format("A LogicalType union holds more than one member.");
        }

        return info;
    }

    private static void ReadDecimal(ref ThriftCompactReader reader, ref LogicalTypeInfo info)
    {
        int found = 0;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    info.Scale = reader.ReadI32();
                    found |= 1;
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    info.Precision = reader.ReadI32();
                    found |= 2;
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (found != 3)
        {
            ParquetThrow.Format("A DECIMAL annotation lacks its scale or its precision.");
        }
    }

    private static void ReadTemporal(ref ThriftCompactReader reader, ref LogicalTypeInfo info)
    {
        int found = 0;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    info.IsAdjustedToUtc = ThriftCompactReader.BooleanField(type);
                    found |= 1;
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.Struct);
                    info.Unit = ReadTimeUnit(ref reader);
                    found |= 2;
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (found != 3)
        {
            ParquetThrow.Format($"A {info.Kind} annotation lacks its UTC adjustment or its unit.");
        }

        if (info.Unit == 0)
        {
            // The standard asks that a unit it does not list be handled as an unsupported feature,
            // not as an error in the file: the annotation is dropped and the column reads as its
            // physical type.
            info.Kind = LogicalTypeKind.Unrecognized;
        }
    }

    private static ParquetTimeUnit ReadTimeUnit(ref ThriftCompactReader reader)
    {
        ParquetTimeUnit unit = 0;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            if (id is >= 1 and <= 3 && type == ThriftType.Struct)
            {
                unit = (ParquetTimeUnit)id;
            }

            reader.Skip(type);
        }

        reader.ExitStruct(saved);
        return unit;
    }

    private static void ReadInteger(ref ThriftCompactReader reader, ref LogicalTypeInfo info)
    {
        int found = 0;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.Byte);
                    info.BitWidth = (byte)reader.ReadByte();
                    found |= 1;
                    break;
                case 2:
                    info.IsSigned = ThriftCompactReader.BooleanField(type);
                    found |= 2;
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (found != 3)
        {
            ParquetThrow.Format("An INTEGER annotation lacks its width or its sign.");
        }
    }

    private static void ReadVariant(ref ThriftCompactReader reader, ref LogicalTypeInfo info)
    {
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            if (id == 1)
            {
                ThriftCompactReader.Expect(type, ThriftType.Byte);
                info.VariantVersion = reader.ReadByte();
            }
            else
            {
                reader.Skip(type);
            }
        }

        reader.ExitStruct(saved);
    }

    private static void ReadGeospatial(ref ThriftCompactReader reader, ref LogicalTypeInfo info)
    {
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.Binary);
                    info.Crs = Encoding.UTF8.GetString(reader.ReadBinary());
                    break;
                case 2 when info.Kind == LogicalTypeKind.Geography:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    info.EdgeAlgorithm = reader.ReadI32();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
    }

    /// <summary>Writes this annotation as the <c>LogicalType</c> union.</summary>
    internal readonly void Write(ref ThriftCompactWriter writer)
    {
        short saved = writer.BeginStruct();
        short member = writer.BeginStructField((short)Kind);
        switch (Kind)
        {
            case LogicalTypeKind.Decimal:
                writer.WriteI32Field(1, Scale);
                writer.WriteI32Field(2, Precision);
                break;
            case LogicalTypeKind.Time:
            case LogicalTypeKind.Timestamp:
                writer.WriteBooleanField(1, IsAdjustedToUtc);
                short unit = writer.BeginStructField(2);
                short empty = writer.BeginStructField((short)Unit);
                writer.EndStruct(empty);
                writer.EndStruct(unit);
                break;
            case LogicalTypeKind.Integer:
                writer.WriteByteField(1, (sbyte)BitWidth);
                writer.WriteBooleanField(2, IsSigned);
                break;
            case LogicalTypeKind.Variant:
                if (VariantVersion >= 0)
                {
                    writer.WriteByteField(1, (sbyte)VariantVersion);
                }

                break;
        }

        writer.EndStruct(member);
        writer.EndStruct(saved);
    }
}

/// <summary>One <c>SchemaElement</c>: a group or a leaf of the schema, in its depth-first place.</summary>
internal struct SchemaElement
{
    internal string Name;

    /// <summary>Whether <see cref="Type"/> is set: a leaf.</summary>
    internal bool HasType;

    internal PhysicalType Type;

    /// <summary>A <c>FIXED_LEN_BYTE_ARRAY</c>'s length, or -1.</summary>
    internal int TypeLength;

    /// <summary>Whether <see cref="Repetition"/> is set; only the root has none.</summary>
    internal bool HasRepetition;

    internal FieldRepetition Repetition;

    /// <summary>A group's children, or -1 for a leaf.</summary>
    internal int ChildCount;

    /// <summary>The deprecated annotation, or -1.</summary>
    internal int ConvertedType;

    /// <summary>The deprecated decimal scale, or -1.</summary>
    internal int Scale;

    /// <summary>The deprecated decimal precision, or -1.</summary>
    internal int Precision;

    /// <summary>Whether <see cref="FieldId"/> is set.</summary>
    internal bool HasFieldId;

    internal int FieldId;

    internal LogicalTypeInfo LogicalType;

    /// <summary>Reads one <c>SchemaElement</c>.</summary>
    internal static SchemaElement Read(ref ThriftCompactReader reader)
    {
        SchemaElement element = default;
        element.TypeLength = -1;
        element.ChildCount = -1;
        element.ConvertedType = -1;
        element.Scale = -1;
        element.Precision = -1;
        element.LogicalType.VariantVersion = -1;
        element.LogicalType.EdgeAlgorithm = -1;
        bool hasName = false;
        short saved = reader.EnterStruct();
        while (reader.ReadFieldHeader(out ThriftType type, out short id))
        {
            switch (id)
            {
                case 1:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    element.Type = (PhysicalType)reader.ReadI32();
                    element.HasType = true;
                    break;
                case 2:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    element.TypeLength = reader.ReadI32();
                    break;
                case 3:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    element.Repetition = (FieldRepetition)reader.ReadI32();
                    element.HasRepetition = true;
                    break;
                case 4:
                    ThriftCompactReader.Expect(type, ThriftType.Binary);
                    element.Name = Encoding.UTF8.GetString(reader.ReadBinary());
                    hasName = true;
                    break;
                case 5:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    element.ChildCount = reader.ReadI32();
                    break;
                case 6:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    element.ConvertedType = reader.ReadI32();
                    break;
                case 7:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    element.Scale = reader.ReadI32();
                    break;
                case 8:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    element.Precision = reader.ReadI32();
                    break;
                case 9:
                    ThriftCompactReader.Expect(type, ThriftType.I32);
                    element.FieldId = reader.ReadI32();
                    element.HasFieldId = true;
                    break;
                case 10:
                    ThriftCompactReader.Expect(type, ThriftType.Struct);
                    element.LogicalType = LogicalTypeInfo.Read(ref reader);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        reader.ExitStruct(saved);
        if (!hasName)
        {
            ParquetThrow.Format("A schema element has no name.");
        }

        return element;
    }

    /// <summary>Writes this element.</summary>
    internal readonly void Write(ref ThriftCompactWriter writer)
    {
        short saved = writer.BeginStruct();
        if (HasType)
        {
            writer.WriteI32Field(1, (int)Type);
        }

        if (TypeLength >= 0)
        {
            writer.WriteI32Field(2, TypeLength);
        }

        if (HasRepetition)
        {
            writer.WriteI32Field(3, (int)Repetition);
        }

        writer.WriteStringField(4, Name);
        if (ChildCount >= 0)
        {
            writer.WriteI32Field(5, ChildCount);
        }

        if (ConvertedType >= 0)
        {
            writer.WriteI32Field(6, ConvertedType);
        }

        if (Scale >= 0)
        {
            writer.WriteI32Field(7, Scale);
        }

        if (Precision >= 0)
        {
            writer.WriteI32Field(8, Precision);
        }

        if (HasFieldId)
        {
            writer.WriteI32Field(9, FieldId);
        }

        if (LogicalType.Kind is not (LogicalTypeKind.None or LogicalTypeKind.Unrecognized))
        {
            writer.WriteFieldHeader(ThriftType.Struct, 10);
            LogicalType.Write(ref writer);
        }

        writer.EndStruct(saved);
    }
}
