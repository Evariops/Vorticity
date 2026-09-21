// Columns of lists, built straight into an arena, for the tests of a list's elements blocked by
// the parent's rows and of the predicates that read them.
//
// EVERY SHAPE HAS A TRAP IN IT. Empty rows whose offset is 0 rather than the running end (what the
// chunk compactor writes), null rows that still name elements, elements whose minimum is far from
// zero (so a frame of reference taken from the wrong rows writes wrong values), strings long enough
// to leave the view, a list of lists, a map, a fixed-size list, a list of structs, one list whose
// rows name their elements in reverse, whose blocks' windows do not abut, and one whose rows trade
// their elements two by two, whose windows still do.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Types;

namespace Vorticity.Tests.Writing;

/// <summary>One column of lists beside an id, over <see cref="Rows"/> rows.</summary>
/// <param name="Name">The shape.</param>
/// <param name="Schema">The file's schema: <c>id</c>, then <c>items</c>.</param>
/// <param name="Arena">Where the rows live.</param>
/// <param name="Root">The struct of all the rows.</param>
/// <param name="Rows">How many.</param>
internal sealed record ListShape(string Name, DType Schema, CanonicalArena Arena, int Root, int Rows);

/// <summary>Builds the shapes.</summary>
internal static class ListShapes
{
    /// <summary>The shapes whose rows name their elements in order.</summary>
    internal static readonly string[] Contiguous =
        ["list_i64", "list_utf8", "list_list_i32", "map_utf8_i64", "fsl_f64", "list_struct"];

    /// <summary>The shape whose rows name their elements in reverse.</summary>
    internal const string Scattered = "scattered_i64";

    /// <summary>The shape whose rows trade their elements two by two.</summary>
    internal const string Swapped = "swapped_i64";

    /// <summary>Builds <paramref name="name"/> over <paramref name="rows"/> rows.</summary>
    /// <param name="name">One of <see cref="Contiguous"/>, or <see cref="Scattered"/>.</param>
    /// <param name="rows">How many rows.</param>
    internal static ListShape Build(string name, int rows)
    {
        DTypeArena types = new DTypeArena();
        CanonicalArena arena = new CanonicalArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        int ids = Longs(arena, i64, rows, row => row, valid: null);
        (DType items, int column) = name switch
        {
            "list_i64" => ListI64(arena, types, rows),
            "list_utf8" => ListUtf8(arena, types, rows),
            "list_list_i32" => ListListI32(arena, types, rows),
            "map_utf8_i64" => MapUtf8I64(arena, types, rows),
            "fsl_f64" => FslF64(arena, types, rows),
            "list_struct" => ListStruct(arena, types, rows),
            Scattered => Paired(arena, types, rows, row => 2L * (rows - 1 - row)),
            Swapped => Paired(arena, types, rows, row => row % 2 == 0 ? (2L * row) + 2 : (2L * row) - 2),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such shape"),
        };

        DType schema = types.Struct(["id", "items"], [i64, items], Nullability.NonNullable);
        int root = arena.AddStruct(schema, rows, Validity.NonNullable, [ids, column]);
        return new ListShape(name, schema, arena, root, rows);
    }

    /// <summary>
    /// Every value of every row, rendered row-major, so that two readings compare whatever their
    /// batching.
    /// </summary>
    /// <param name="batch">The rows.</param>
    /// <param name="into">Where the rendering goes.</param>
    internal static void Describe(RecordBatch batch, List<string> into)
    {
        for (int row = 0; row < batch.RowCount; row++)
        {
            StringBuilder text = new StringBuilder();
            for (int field = 0; field < batch.FieldCount; field++)
            {
                text.Append(field == 0 ? string.Empty : " | ");
                Render(batch.Column(field), row, text);
            }

            into.Add(text.ToString());
        }
    }

    /// <summary>One value, recursively.</summary>
    /// <param name="column">Its column.</param>
    /// <param name="row">Its row.</param>
    /// <param name="text">Where it goes.</param>
    internal static void Render(VortexColumn column, int row, StringBuilder text)
    {
        if (!column.IsValid(row))
        {
            text.Append("null");
            return;
        }

        switch (column.Kind)
        {
            case CanonicalKind.Primitive:
                text.Append(column.DType.PType switch
                {
                    PType.I32 => column.AsPrimitive<int>().Values[row].ToString(CultureInfo.InvariantCulture),
                    PType.I64 => column.AsPrimitive<long>().Values[row].ToString(CultureInfo.InvariantCulture),
                    PType.F64 => BitConverter.DoubleToUInt64Bits(column.AsPrimitive<double>().Values[row]).ToString("X16", CultureInfo.InvariantCulture),
                    PType ptype => ptype.ToString(),
                });
                return;
            case CanonicalKind.VarBinView:
                text.Append('"').Append(Encoding.UTF8.GetString(column.AsBinary().GetSpan(row))).Append('"');
                return;
            case CanonicalKind.ListView:
            {
                ListColumn list = column.AsList();
                long offset = list.GetOffset(row);
                int length = list.GetLength(row);
                VortexColumn elements = list.Elements;
                text.Append('[');
                for (int i = 0; i < length; i++)
                {
                    text.Append(i == 0 ? string.Empty : ", ");
                    Render(elements, checked((int)(offset + i)), text);
                }

                text.Append(']');
                return;
            }

            case CanonicalKind.FixedSizeList:
            {
                FixedSizeListColumn list = column.AsFixedSizeList();
                list.GetRange(row, out int start, out int count);
                VortexColumn elements = list.Elements;
                text.Append('(');
                for (int i = 0; i < count; i++)
                {
                    text.Append(i == 0 ? string.Empty : ", ");
                    Render(elements, start + i, text);
                }

                text.Append(')');
                return;
            }

            case CanonicalKind.Struct:
            {
                StructColumn fields = column.AsStruct();
                text.Append('{');
                for (int i = 0; i < fields.FieldCount; i++)
                {
                    text.Append(i == 0 ? string.Empty : ", ");
                    Render(fields.GetField(i), row, text);
                }

                text.Append('}');
                return;
            }

            default:
                text.Append(column.Kind.ToString());
                return;
        }
    }

    // ------------------------------------------------------------------------------ the shapes

    /// <summary>
    /// Nullable lists of nullable i64 far from zero: every thirteenth row null, every
    /// twenty-sixth of those still naming two elements; sizes 0 to 4; every tenth empty row at
    /// offset 0; every eleventh element null.
    /// </summary>
    private static (DType, int) ListI64(CanonicalArena arena, DTypeArena types, int rows)
    {
        DType element = types.Primitive(PType.I64, Nullability.Nullable);
        DType list = types.List(element, Nullability.Nullable);
        Sizes sizes = Lay(rows, row => row % 13 == 0 ? (row % 26 == 0 ? 2 : 0) : row % 5, emptyAtZero: row => row % 10 == 0);
        int elements = Longs(arena, element, sizes.Total, e => 1_000_000 + ((e * 7919L) % 100_003), valid: e => e % 11 != 0);
        return (list, List(arena, types, list, rows, elements, sizes, valid: row => row % 13 != 0));
    }

    /// <summary>Lists of nullable strings, some past the view's twelve inline bytes.</summary>
    private static (DType, int) ListUtf8(CanonicalArena arena, DTypeArena types, int rows)
    {
        DType element = types.Utf8(Nullability.Nullable);
        DType list = types.List(element, Nullability.NonNullable);
        Sizes sizes = Lay(rows, row => row % 4, emptyAtZero: row => row % 7 == 0);
        int elements = Strings(
            arena, types, element, sizes.Total,
            e => e % 3 == 0 ? "a-much-longer-value-" + (e % 37).ToString(CultureInfo.InvariantCulture) : "v" + (e % 37).ToString(CultureInfo.InvariantCulture),
            valid: e => e % 17 != 0);
        return (list, List(arena, types, list, rows, elements, sizes, valid: null));
    }

    /// <summary>Nullable lists of lists of signed i32.</summary>
    private static (DType, int) ListListI32(CanonicalArena arena, DTypeArena types, int rows)
    {
        DType value = types.Primitive(PType.I32, Nullability.NonNullable);
        DType inner = types.List(value, Nullability.NonNullable);
        DType outer = types.List(inner, Nullability.Nullable);
        Sizes outerSizes = Lay(rows, row => row % 3, emptyAtZero: row => row % 5 == 0);
        Sizes innerSizes = Lay(outerSizes.Total, j => j % 4, emptyAtZero: j => j % 6 == 0);
        int values = Ints(arena, value, innerSizes.Total, v => ((v * 31) % 1000) - 500);
        int lists = List(arena, types, inner, outerSizes.Total, values, innerSizes, valid: null);
        return (outer, List(arena, types, outer, rows, lists, outerSizes, valid: row => row % 9 != 4));
    }

    /// <summary>Maps from twelve-byte-or-shorter keys to nullable i64.</summary>
    private static (DType, int) MapUtf8I64(CanonicalArena arena, DTypeArena types, int rows)
    {
        DType key = types.Utf8(Nullability.NonNullable);
        DType value = types.Primitive(PType.I64, Nullability.Nullable);
        DType map = types.Map(key, value, keysSorted: false, Nullability.NonNullable);
        DType entry = types.Struct(["key", "value"], [key, value], Nullability.NonNullable);
        Sizes sizes = Lay(rows, row => row % 4, emptyAtZero: row => false);
        int keys = Strings(arena, types, key, sizes.Total, j => "k" + (j % 50).ToString(CultureInfo.InvariantCulture), valid: null);
        int values = Longs(arena, value, sizes.Total, j => j * 3L, valid: j => j % 9 != 0);
        int entries = arena.AddStruct(entry, sizes.Total, Validity.NonNullable, [keys, values]);
        return (map, List(arena, types, map, rows, entries, sizes, valid: null));
    }

    /// <summary>Nullable triples of f64.</summary>
    private static (DType, int) FslF64(CanonicalArena arena, DTypeArena types, int rows)
    {
        DType element = types.Primitive(PType.F64, Nullability.NonNullable);
        DType list = types.FixedSizeList(element, 3, Nullability.Nullable);
        int count = rows * 3;
        VortexBuffer buffer = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> bytes);
        Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
        for (int k = 0; k < count; k++)
        {
            values[k] = (k % 1000) * 0.25;
        }

        int elements = arena.AddPrimitive(element, count, Validity.NonNullable, PType.F64, buffer);
        return (list, arena.AddFixedSizeList(list, rows, Mask(arena, types, rows, row => row % 7 != 0), elements, 3));
    }

    /// <summary>Lists of structs of an i64 far from zero and a nullable string.</summary>
    private static (DType, int) ListStruct(CanonicalArena arena, DTypeArena types, int rows)
    {
        DType a = types.Primitive(PType.I64, Nullability.NonNullable);
        DType b = types.Utf8(Nullability.Nullable);
        DType item = types.Struct(["a", "b"], [a, b], Nullability.NonNullable);
        DType list = types.List(item, Nullability.NonNullable);
        Sizes sizes = Lay(rows, row => row % 3, emptyAtZero: row => row % 4 == 0);
        int avalues = Longs(arena, a, sizes.Total, k => 5_000_000 + k, valid: null);
        int bvalues = Strings(arena, types, b, sizes.Total, k => "s" + (k % 20).ToString(CultureInfo.InvariantCulture), valid: k => k % 6 != 0);
        int items = arena.AddStruct(item, sizes.Total, Validity.NonNullable, [avalues, bvalues]);
        return (list, List(arena, types, list, rows, items, sizes, valid: null));
    }

    /// <summary>
    /// Two elements a row, the row naming the pair <paramref name="offset"/> says: from the end
    /// backwards, or two rows trading theirs. The rows are even in number for the second.
    /// </summary>
    private static (DType, int) Paired(CanonicalArena arena, DTypeArena types, int rows, Func<int, long> offset)
    {
        DType element = types.Primitive(PType.I64, Nullability.NonNullable);
        DType list = types.List(element, Nullability.NonNullable);
        int total = rows * 2;
        int elements = Longs(arena, element, total, e => 7_000_000 + e, valid: null);
        VortexBuffer offsets = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> offsetBytes);
        VortexBuffer sizes = arena.Allocate(rows * sizeof(long), sizeof(long), out Span<byte> sizeBytes);
        Span<long> offsetValues = MemoryMarshal.Cast<byte, long>(offsetBytes);
        Span<long> sizeValues = MemoryMarshal.Cast<byte, long>(sizeBytes);
        for (int row = 0; row < rows; row++)
        {
            offsetValues[row] = offset(row);
            sizeValues[row] = 2;
        }

        return (list, arena.AddListView(list, rows, Validity.NonNullable, elements, offsets, PType.I64, sizes, PType.I64));
    }

    // ------------------------------------------------------------------------------ plumbing

    /// <summary>A list's sizes and offsets, and how many elements they name.</summary>
    private sealed record Sizes(long[] Offsets, long[] Lengths, int Total);

    /// <summary>
    /// Offsets as the running end, except the empty rows <paramref name="emptyAtZero"/> picks,
    /// which sit at 0 the way the chunk compactor writes them.
    /// </summary>
    private static Sizes Lay(int rows, Func<int, int> size, Func<int, bool> emptyAtZero)
    {
        long[] offsets = new long[rows];
        long[] lengths = new long[rows];
        long end = 0;
        for (int row = 0; row < rows; row++)
        {
            int length = size(row);
            lengths[row] = length;
            offsets[row] = length == 0 && emptyAtZero(row) ? 0 : end;
            end += length;
        }

        return new Sizes(offsets, lengths, checked((int)end));
    }

    private static int List(
        CanonicalArena arena, DTypeArena types, DType dtype, int rows, int elements, Sizes sizes, Func<int, bool>? valid)
    {
        // Offsets at u32 and sizes at u8, so the walk runs a pair of widths other than the
        // reference's u64.
        VortexBuffer offsets = arena.Allocate(rows * sizeof(uint), sizeof(uint), out Span<byte> offsetBytes);
        VortexBuffer lengths = arena.Allocate(Math.Max(rows, 1), 1, out Span<byte> lengthBytes);
        Span<uint> offsetValues = MemoryMarshal.Cast<byte, uint>(offsetBytes);
        for (int row = 0; row < rows; row++)
        {
            offsetValues[row] = checked((uint)sizes.Offsets[row]);
            lengthBytes[row] = checked((byte)sizes.Lengths[row]);
        }

        Validity validity = valid is null ? Validity.NonNullable : Mask(arena, types, rows, valid);
        if (valid is not null && dtype.Nullability == Nullability.NonNullable)
        {
            throw new ArgumentException("a non-nullable list has no mask", nameof(valid));
        }

        return arena.AddListView(dtype, rows, validity, elements, offsets, PType.U32, lengths, PType.U8);
    }

    private static int Longs(CanonicalArena arena, DType dtype, int count, Func<int, long> value, Func<int, bool>? valid)
    {
        VortexBuffer buffer = arena.Allocate(Math.Max(count, 1) * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = value(i);
        }

        Validity validity = valid is null ? Validity.FromNullability(dtype.Nullability) : Mask(arena, null, count, valid);
        return arena.AddPrimitive(dtype, count, validity, PType.I64, buffer.Slice(0, count * sizeof(long)));
    }

    private static int Ints(CanonicalArena arena, DType dtype, int count, Func<int, int> value)
    {
        VortexBuffer buffer = arena.Allocate(Math.Max(count, 1) * sizeof(int), sizeof(int), out Span<byte> bytes);
        Span<int> values = MemoryMarshal.Cast<byte, int>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = value(i);
        }

        return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I32, buffer.Slice(0, count * sizeof(int)));
    }

    private static int Strings(
        CanonicalArena arena, DTypeArena types, DType dtype, int count, Func<int, string> value, Func<int, bool>? valid)
    {
        byte[][] values = new byte[count][];
        int heap = 0;
        for (int i = 0; i < count; i++)
        {
            values[i] = Encoding.UTF8.GetBytes(value(i));
            heap += values[i].Length > 12 ? values[i].Length : 0;
        }

        VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> dataBytes);
        VortexBuffer views = arena.Allocate(Math.Max(count, 1) * 16, 16, out Span<byte> view);
        view.Clear();
        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            byte[] utf8 = values[i];
            Span<byte> one = view.Slice(i * 16, 16);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(one, (uint)utf8.Length);
            if (utf8.Length <= 12)
            {
                utf8.CopyTo(one[4..]);
                continue;
            }

            utf8.AsSpan(0, 4).CopyTo(one[4..]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(one[12..], offset);
            utf8.CopyTo(dataBytes[offset..]);
            offset += utf8.Length;
        }

        Validity validity = valid is null ? Validity.FromNullability(dtype.Nullability) : Mask(arena, types, count, valid);
        return arena.AddVarBinView(dtype, count, validity, views.Slice(0, count * 16), [data]);
    }

    private static Validity Mask(CanonicalArena arena, DTypeArena? types, int count, Func<int, bool> valid)
    {
        VortexBuffer bits = arena.Allocate(Math.Max((count + 7) / 8, 1), 8, out Span<byte> raw);
        raw.Clear();
        for (int i = 0; i < count; i++)
        {
            if (valid(i))
            {
                raw[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        DType bitmap = (types ?? new DTypeArena()).Bool(Nullability.NonNullable);
        return Validity.Bitmap(arena.AddBool(bitmap, count, Validity.NonNullable, bits, 0));
    }
}
