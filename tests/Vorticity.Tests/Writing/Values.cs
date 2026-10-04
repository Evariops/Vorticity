// Rendering a batch's values as text, so a round trip is compared over VALUES rather than bytes.
//
// Two rules it exists to enforce, both of them the sidecar's:
//
//   * FLOATS BY THEIR RAW BITS. -0.0 and +0.0 render identically and every NaN renders "NaN", so a
//     writer that canonicalized a NaN payload or flipped a sign bit would pass a text comparison.
//   * NESTED SHAPES ARE DESCENDED, not summarized. A struct or a list that rendered as its kind
//     would make a round trip that lost every nested value look perfect.
//
// Every value is appended to one builder rather than returned as a string of its own: a sweep over
// a large file renders millions of values, and a string apiece keeps the collector busier than the
// reads it checks.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
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
        StringBuilder text = new StringBuilder();
        for (int field = 0; field < batch.FieldCount; field++)
        {
            VortexColumn column = batch.Column(field);
            for (int row = 0; row < batch.RowCount; row++)
            {
                text.Clear();
                Render(column, row, depth: 0, text);
                into.Add(text.ToString());
            }
        }
    }

    /// <summary>
    /// Appends ONE line per row, every column joined, so an entry's position is its row index.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="into">The sink.</param>
    /// <remarks>
    /// <see cref="Describe"/> is column-major WITHIN A BATCH, which was described here as "the same
    /// values in the same order whatever the batching" and is not: it is the same order only while
    /// both sides batch identically. The moment a writer chooses its own chunk boundaries -- which
    /// `VortexWriteOptions.RowBlockSize` makes it do -- the flattened sequences interleave
    /// differently and compare unequal over identical data. A round trip should use THIS one.
    /// </remarks>
    internal static void DescribeRows(RecordBatch batch, List<string> into)
    {
        StringBuilder text = new StringBuilder();
        for (int row = 0; row < batch.RowCount; row++)
        {
            text.Clear();
            AppendRow(batch, row, text);
            into.Add(text.ToString());
        }
    }

    /// <summary>
    /// Appends, per row, a 128-bit hash of its values: two rows hash alike exactly when
    /// <see cref="DescribeRows"/> renders them alike, an integer by its value whatever its width, a
    /// float by its bits and its width, a decimal by its unscaled value. The values are hashed as
    /// they are, without a string per value or per row.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="into">The sink.</param>
    internal static void DigestRows(RecordBatch batch, List<UInt128> into)
    {
        ArrayBufferWriter<byte> bytes = new ArrayBufferWriter<byte>(4096);
        for (int row = 0; row < batch.RowCount; row++)
        {
            bytes.ResetWrittenCount();
            for (int field = 0; field < batch.FieldCount; field++)
            {
                Encode(batch.Column(field), row, depth: 0, bytes);
            }

            into.Add(XxHash128.HashToUInt128(bytes.WrittenSpan));
        }
    }

    /// <summary>One value as a tag and its bytes, each kind self-delimiting, as <see cref="Render"/> distinguishes them.</summary>
    private static void Encode(VortexColumn column, int row, int depth, ArrayBufferWriter<byte> into)
    {
        if (depth > 8)
        {
            Tag(into, 0xFF);
            return;
        }

        if (!column.IsValid(row) || column.Kind == CanonicalKind.Null)
        {
            Tag(into, 0);
            return;
        }

        switch (column.Kind)
        {
            case CanonicalKind.Bool:
                Tag(into, column.AsBool()[row] ? (byte)2 : (byte)1);
                return;

            case CanonicalKind.Primitive:
                EncodePrimitive(column, row, into);
                return;

            case CanonicalKind.Decimal:
            {
                Span<byte> value = into.GetSpan(33);
                value[0] = 4;
                column.AsDecimal()[row].Unscaled.WriteLittleEndianBytes(value.Slice(1, 32));
                into.Advance(33);
                return;
            }

            case CanonicalKind.VarBinView:
            {
                ReadOnlySpan<byte> value = column.AsBinary().GetSpan(row);
                Span<byte> head = into.GetSpan(5);
                head[0] = 5;
                BinaryPrimitives.WriteInt32LittleEndian(head[1..], value.Length);
                into.Advance(5);
                into.Write(value);
                return;
            }

            case CanonicalKind.Struct:
            {
                StructColumn nested = column.AsStruct();
                Span<byte> head = into.GetSpan(5);
                head[0] = 6;
                BinaryPrimitives.WriteInt32LittleEndian(head[1..], nested.FieldCount);
                into.Advance(5);
                for (int i = 0; i < nested.FieldCount; i++)
                {
                    Encode(nested.GetField(i), row, depth + 1, into);
                }

                return;
            }

            case CanonicalKind.Extension:
                Tag(into, 7);
                Encode(column.AsExtension().Storage, row, depth + 1, into);
                return;

            default:
                if (column.Kind == CanonicalKind.ListView)
                {
                    ListColumn list = column.AsList();
                    EncodeRange(list.Elements, list.GetOffset(row), list.GetLength(row), depth, into);
                    return;
                }

                FixedSizeListColumn fixedList = column.AsFixedSizeList();
                int size = (int)fixedList.Size;
                EncodeRange(fixedList.Elements, (long)row * size, size, depth, into);
                return;
        }
    }

    private static void EncodeRange(VortexColumn elements, long start, long length, int depth, ArrayBufferWriter<byte> into)
    {
        Span<byte> head = into.GetSpan(9);
        head[0] = 8;
        BinaryPrimitives.WriteInt64LittleEndian(head[1..], length);
        into.Advance(9);
        for (long i = 0; i < length; i++)
        {
            Encode(elements, (int)(start + i), depth + 1, into);
        }
    }

    /// <summary>A float by its width and its bits; an integer by its value, widened, as its text is the same whatever its width.</summary>
    private static void EncodePrimitive(VortexColumn column, int row, ArrayBufferWriter<byte> into)
    {
        Span<byte> value = into.GetSpan(17);
        switch (column.DType.PType)
        {
            case PType.F16:
                value[0] = 10;
                BinaryPrimitives.WriteUInt16LittleEndian(value[1..], BitConverter.HalfToUInt16Bits(column.AsPrimitive<Half>().Values[row]));
                into.Advance(3);
                return;
            case PType.F32:
                value[0] = 11;
                BinaryPrimitives.WriteUInt32LittleEndian(value[1..], BitConverter.SingleToUInt32Bits(column.AsPrimitive<float>().Values[row]));
                into.Advance(5);
                return;
            case PType.F64:
                value[0] = 12;
                BinaryPrimitives.WriteUInt64LittleEndian(value[1..], BitConverter.DoubleToUInt64Bits(column.AsPrimitive<double>().Values[row]));
                into.Advance(9);
                return;
        }

        Int128 integer = column.DType.PType switch
        {
            PType.I8 => column.AsPrimitive<sbyte>().Values[row],
            PType.I16 => column.AsPrimitive<short>().Values[row],
            PType.I32 => column.AsPrimitive<int>().Values[row],
            PType.I64 => column.AsPrimitive<long>().Values[row],
            PType.U8 => column.AsPrimitive<byte>().Values[row],
            PType.U16 => column.AsPrimitive<ushort>().Values[row],
            PType.U32 => column.AsPrimitive<uint>().Values[row],
            _ => column.AsPrimitive<ulong>().Values[row],
        };
        value[0] = 3;
        BinaryPrimitives.WriteInt128LittleEndian(value[1..], integer);
        into.Advance(17);
    }

    private static void Tag(ArrayBufferWriter<byte> into, byte tag)
    {
        into.GetSpan(1)[0] = tag;
        into.Advance(1);
    }

    /// <summary>The line of one row: every column's value, joined by U+001F.</summary>
    private static void AppendRow(RecordBatch batch, int row, StringBuilder into)
    {
        for (int field = 0; field < batch.FieldCount; field++)
        {
            if (field > 0)
            {
                into.Append('\u001f');
            }

            Render(batch.Column(field), row, depth: 0, into);
        }
    }

    private static void Render(VortexColumn column, int row, int depth, StringBuilder into)
    {
        if (depth > 8)
        {
            into.Append("<deep>");
            return;
        }

        if (!column.IsValid(row))
        {
            into.Append("null");
            return;
        }

        switch (column.Kind)
        {
            case CanonicalKind.Null:
                into.Append("null");
                return;

            case CanonicalKind.Bool:
                into.Append(column.AsBool()[row] ? "true" : "false");
                return;

            case CanonicalKind.Primitive:
                RenderPrimitive(column, row, into);
                return;

            case CanonicalKind.Decimal:
                into.Append('d').Append(column.AsDecimal()[row].Unscaled.ToString());
                return;

            case CanonicalKind.VarBinView:
                RenderBytes(column.AsBinary().GetSpan(row), into);
                return;

            case CanonicalKind.Struct:
            {
                StructColumn nested = column.AsStruct();
                into.Append('{');
                for (int i = 0; i < nested.FieldCount; i++)
                {
                    if (i != 0)
                    {
                        into.Append(',');
                    }

                    Render(nested.GetField(i), row, depth + 1, into);
                }

                into.Append('}');
                return;
            }

            case CanonicalKind.Extension:
                into.Append('e');
                Render(column.AsExtension().Storage, row, depth + 1, into);
                return;

            default:
                // Lists render as their element count plus their elements: the elements child is
                // shared across rows, so rendering the row's own window is what compares.
                RenderList(column, row, depth, into);
                return;
        }
    }

    /// <summary>A binary or a string as "b" and its bytes in Base64.</summary>
    private static void RenderBytes(ReadOnlySpan<byte> bytes, StringBuilder into)
    {
        into.Append('b');
        int length = ((bytes.Length + 2) / 3) * 4;
        char[]? rented = length > 512 ? ArrayPool<char>.Shared.Rent(length) : null;
        Span<char> chars = rented is not null ? rented : stackalloc char[512];
        Convert.TryToBase64Chars(bytes, chars, out int written);
        into.Append(chars[..written]);
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static void RenderList(VortexColumn column, int row, int depth, StringBuilder into)
    {
        if (column.Kind == CanonicalKind.ListView)
        {
            ListColumn list = column.AsList();
            RenderRange(list.Elements, list.GetOffset(row), list.GetLength(row), depth, into);
            return;
        }

        FixedSizeListColumn fixedList = column.AsFixedSizeList();
        int size = (int)fixedList.Size;
        RenderRange(fixedList.Elements, (long)row * size, size, depth, into);
    }

    private static void RenderRange(VortexColumn elements, long start, long length, int depth, StringBuilder into)
    {
        into.Append('[');
        for (long i = 0; i < length; i++)
        {
            if (i != 0)
            {
                into.Append(',');
            }

            Render(elements, (int)(start + i), depth + 1, into);
        }

        into.Append(']');
    }

    private static void RenderPrimitive(VortexColumn column, int row, StringBuilder into)
    {
        IFormatProvider invariant = CultureInfo.InvariantCulture;
        switch (column.DType.PType)
        {
            case PType.F16:
                into.Append(invariant, $"{BitConverter.HalfToUInt16Bits(column.AsPrimitive<Half>().Values[row]):X4}");
                return;
            case PType.F32:
                into.Append(invariant, $"{BitConverter.SingleToUInt32Bits(column.AsPrimitive<float>().Values[row]):X8}");
                return;
            case PType.F64:
                into.Append(invariant, $"{BitConverter.DoubleToUInt64Bits(column.AsPrimitive<double>().Values[row]):X16}");
                return;
            case PType.I8:
                into.Append(invariant, $"{column.AsPrimitive<sbyte>().Values[row]}");
                return;
            case PType.I16:
                into.Append(invariant, $"{column.AsPrimitive<short>().Values[row]}");
                return;
            case PType.I32:
                into.Append(invariant, $"{column.AsPrimitive<int>().Values[row]}");
                return;
            case PType.I64:
                into.Append(invariant, $"{column.AsPrimitive<long>().Values[row]}");
                return;
            case PType.U8:
                into.Append(invariant, $"{column.AsPrimitive<byte>().Values[row]}");
                return;
            case PType.U16:
                into.Append(invariant, $"{column.AsPrimitive<ushort>().Values[row]}");
                return;
            case PType.U32:
                into.Append(invariant, $"{column.AsPrimitive<uint>().Values[row]}");
                return;
            default:
                into.Append(invariant, $"{column.AsPrimitive<ulong>().Values[row]}");
                return;
        }
    }
}
