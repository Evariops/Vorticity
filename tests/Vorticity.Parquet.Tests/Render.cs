using System;
using System.Globalization;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Types;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// A batch's values written out as text, nested ones as lists, maps and structs: what a test compares
/// with the contents a file documents, or with the rows it wrote.
/// </summary>
internal static class Render
{
    /// <summary>The value of <paramref name="column"/> at <paramref name="row"/>.</summary>
    internal static string Row(RecordBatch batch, int column, int row)
    {
        StringBuilder text = new();
        Value(batch.Column(column), row, text);
        return text.ToString();
    }

    private static void Value(VortexColumn column, int row, StringBuilder text)
    {
        if (!column.IsValid(row))
        {
            text.Append("null");
            return;
        }

        if (column.DType.Kind == DTypeKind.Variant)
        {
            StructColumn parts = column.AsStruct();
            text.Append("variant(0x").Append(Convert.ToHexString(parts.GetField(0).AsBinary().GetSpan(row)))
                .Append(", 0x").Append(Convert.ToHexString(parts.GetField(1).AsBinary().GetSpan(row))).Append(')');
            return;
        }

        switch (column.Kind)
        {
            case CanonicalKind.Null:
                text.Append("null");
                return;

            case CanonicalKind.Bool:
                text.Append(column.AsBool()[row] ? "true" : "false");
                return;

            case CanonicalKind.Primitive:
                text.Append(Primitive(column, row));
                return;

            case CanonicalKind.VarBinView:
                BinaryColumn binary = column.AsBinary();
                if (binary.IsUtf8)
                {
                    text.Append('"').Append(binary.GetString(row)).Append('"');
                }
                else
                {
                    text.Append("0x").Append(Convert.ToHexString(binary.GetSpan(row)));
                }

                return;

            case CanonicalKind.Struct:
                StructColumn fields = column.AsStruct();
                text.Append('{');
                for (int i = 0; i < fields.FieldCount; i++)
                {
                    text.Append(i == 0 ? "" : ", ").Append(fields.GetFieldName(i)).Append(": ");
                    Value(fields.GetField(i), row, text);
                }

                text.Append('}');
                return;

            case CanonicalKind.ListView:
                ListColumn list = column.AsList();
                long offset = list.GetOffset(row);
                int length = list.GetLength(row);
                VortexColumn elements = list.Elements;
                bool map = column.DType.Kind == DTypeKind.Map;
                text.Append(map ? '{' : '[');
                for (int i = 0; i < length; i++)
                {
                    int element = checked((int)(offset + i));
                    text.Append(i == 0 ? "" : ", ");
                    if (map)
                    {
                        StructColumn entry = elements.AsStruct();
                        Value(entry.GetField(0), element, text);
                        text.Append(" -> ");
                        Value(entry.GetField(1), element, text);
                    }
                    else
                    {
                        Value(elements, element, text);
                    }
                }

                text.Append(map ? '}' : ']');
                return;

            case CanonicalKind.Extension:
                Value(column.AsExtension().Storage, row, text);
                return;

            case CanonicalKind.FixedSizeList:
                FixedSizeListColumn fixedList = column.AsFixedSizeList();
                fixedList.GetRange(row, out int start, out int count);
                text.Append('[');
                for (int i = 0; i < count; i++)
                {
                    text.Append(i == 0 ? "" : ", ");
                    Value(fixedList.Elements, start + i, text);
                }

                text.Append(']');
                return;

            case CanonicalKind.Decimal:
                text.Append(column.AsDecimal()[row].ToString());
                return;

            default:
                text.Append('<').Append(column.Kind).Append('>');
                return;
        }
    }

    private static string Primitive(VortexColumn column, int row) => column.DType.PType switch
    {
        PType.I8 => column.AsPrimitive<sbyte>()[row].ToString(CultureInfo.InvariantCulture),
        PType.I16 => column.AsPrimitive<short>()[row].ToString(CultureInfo.InvariantCulture),
        PType.I32 => column.AsPrimitive<int>()[row].ToString(CultureInfo.InvariantCulture),
        PType.I64 => column.AsPrimitive<long>()[row].ToString(CultureInfo.InvariantCulture),
        PType.U8 => column.AsPrimitive<byte>()[row].ToString(CultureInfo.InvariantCulture),
        PType.U16 => column.AsPrimitive<ushort>()[row].ToString(CultureInfo.InvariantCulture),
        PType.U32 => column.AsPrimitive<uint>()[row].ToString(CultureInfo.InvariantCulture),
        PType.U64 => column.AsPrimitive<ulong>()[row].ToString(CultureInfo.InvariantCulture),
        PType.F16 => column.AsPrimitive<Half>()[row].ToString(CultureInfo.InvariantCulture),
        PType.F32 => column.AsPrimitive<float>()[row].ToString(CultureInfo.InvariantCulture),
        _ => column.AsPrimitive<double>()[row].ToString(CultureInfo.InvariantCulture),
    };
}
