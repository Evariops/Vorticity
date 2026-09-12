// Rendering a batch's values as text, so a round trip is compared over VALUES rather than bytes.
//
// Two rules it exists to enforce, both of them the sidecar's:
//
//   * FLOATS BY THEIR RAW BITS. -0.0 and +0.0 render identically and every NaN renders "NaN", so a
//     writer that canonicalized a NaN payload or flipped a sign bit would pass a text comparison.
//   * NESTED SHAPES ARE DESCENDED, not summarized. A struct or a list that rendered as its kind
//     would make a round trip that lost every nested value look perfect.
using System;
using System.Collections.Generic;
using System.Globalization;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Types;

namespace Vorticity.Tests.Writing;

/// <summary>Renders every value of a batch, for comparison across a round trip.</summary>
internal static class Values
{
    /// <summary>Appends one line per value of every column.</summary>
    /// <param name="batch">The batch.</param>
    /// <param name="into">The sink.</param>
    internal static void Describe(RecordBatch batch, List<string> into)
    {
        for (int field = 0; field < batch.FieldCount; field++)
        {
            VortexColumn column = batch.Column(field);
            for (int row = 0; row < batch.RowCount; row++)
            {
                into.Add(Render(column, row, depth: 0));
            }
        }
    }

    /// <summary>
    /// Appends ONE line per row, every column joined, so an entry's position is its row index.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="into">The sink.</param>
    /// <remarks>
    /// <see cref="Describe"/> is column-major, which is right for a round trip - it compares the
    /// same values in the same order whatever the batching - and wrong for anything that has to
    /// index by row, because across several batches an entry's position is not its row.
    /// </remarks>
    internal static void DescribeRows(RecordBatch batch, List<string> into)
    {
        for (int row = 0; row < batch.RowCount; row++)
        {
            System.Text.StringBuilder line = new System.Text.StringBuilder();
            for (int field = 0; field < batch.FieldCount; field++)
            {
                if (field > 0)
                {
                    line.Append('\u001f');
                }

                line.Append(Render(batch.Column(field), row, depth: 0));
            }

            into.Add(line.ToString());
        }
    }

    private static string Render(VortexColumn column, int row, int depth)
    {
        if (depth > 8)
        {
            return "<deep>";
        }

        if (!column.IsValid(row))
        {
            return "null";
        }

        switch (column.Kind)
        {
            case CanonicalKind.Null:
                return "null";

            case CanonicalKind.Bool:
                return column.AsBool()[row] ? "true" : "false";

            case CanonicalKind.Primitive:
                return RenderPrimitive(column, row);

            case CanonicalKind.Decimal:
                return "d" + column.AsDecimal()[row].Unscaled.ToString();

            case CanonicalKind.VarBinView:
                return "b" + Convert.ToBase64String(column.AsBinary().GetSpan(row));

            case CanonicalKind.Struct:
            {
                StructColumn nested = column.AsStruct();
                System.Text.StringBuilder text = new System.Text.StringBuilder("{");
                for (int i = 0; i < nested.FieldCount; i++)
                {
                    if (i != 0)
                    {
                        text.Append(',');
                    }

                    text.Append(Render(nested.GetField(i), row, depth + 1));
                }

                return text.Append('}').ToString();
            }

            case CanonicalKind.Extension:
                return "e" + Render(column.AsExtension().Storage, row, depth + 1);

            default:
                // Lists render as their element count plus their elements: the elements child is
                // shared across rows, so rendering the row's own window is what compares.
                return RenderList(column, row, depth);
        }
    }

    private static string RenderList(VortexColumn column, int row, int depth)
    {
        if (column.Kind == CanonicalKind.ListView)
        {
            ListColumn list = column.AsList();
            return RenderRange(list.Elements, list.GetOffset(row), list.GetLength(row), depth);
        }

        FixedSizeListColumn fixedList = column.AsFixedSizeList();
        int size = (int)fixedList.Size;
        return RenderRange(fixedList.Elements, (long)row * size, size, depth);
    }

    private static string RenderRange(VortexColumn elements, long start, long length, int depth)
    {
        System.Text.StringBuilder text = new System.Text.StringBuilder("[");
        for (long i = 0; i < length; i++)
        {
            if (i != 0)
            {
                text.Append(',');
            }

            text.Append(Render(elements, (int)(start + i), depth + 1));
        }

        return text.Append(']').ToString();
    }

    private static string RenderPrimitive(VortexColumn column, int row) => column.DType.PType switch
    {
        PType.F16 => BitConverter.HalfToUInt16Bits(column.AsPrimitive<Half>().Values[row])
            .ToString("X4", CultureInfo.InvariantCulture),
        PType.F32 => BitConverter.SingleToUInt32Bits(column.AsPrimitive<float>().Values[row])
            .ToString("X8", CultureInfo.InvariantCulture),
        PType.F64 => BitConverter.DoubleToUInt64Bits(column.AsPrimitive<double>().Values[row])
            .ToString("X16", CultureInfo.InvariantCulture),
        PType.I8 => column.AsPrimitive<sbyte>().Values[row].ToString(CultureInfo.InvariantCulture),
        PType.I16 => column.AsPrimitive<short>().Values[row].ToString(CultureInfo.InvariantCulture),
        PType.I32 => column.AsPrimitive<int>().Values[row].ToString(CultureInfo.InvariantCulture),
        PType.I64 => column.AsPrimitive<long>().Values[row].ToString(CultureInfo.InvariantCulture),
        PType.U8 => column.AsPrimitive<byte>().Values[row].ToString(CultureInfo.InvariantCulture),
        PType.U16 => column.AsPrimitive<ushort>().Values[row].ToString(CultureInfo.InvariantCulture),
        PType.U32 => column.AsPrimitive<uint>().Values[row].ToString(CultureInfo.InvariantCulture),
        _ => column.AsPrimitive<ulong>().Values[row].ToString(CultureInfo.InvariantCulture),
    };
}
