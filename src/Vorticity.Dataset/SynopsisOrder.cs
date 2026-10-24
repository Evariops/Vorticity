using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.RowEncoding;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The synopsis columns of an object, the columns whose values repeat, chosen from their numbers of
/// distinct values over the object's rows, fewest first, then the instant. The object's first slice then
/// settles their order by the runs each adds (<see cref="SortedSlices"/>), which keeps this one for
/// independent columns. One order serves every slice of an object, and the slice table records it.
/// </summary>
/// <remarks>
/// <para>
/// A column is a synopsis column when its values repeat at least twice on average, the length past
/// which a run stored once, its value and its end, costs less than its rows each holding the value.
/// An identifier never is, nor the instant, which sorts last, nor a column whose values have no order
/// of codes yet: a decimal, a struct, a list, a variant. Equal counts are told apart by the columns'
/// order in the schema, so the same rows give the same order whoever writes them.
/// </para>
/// <para>
/// The counts are approximate, a <see cref="CardinalitySketch"/> per column over a fixed-seed hash, the
/// same estimate on every machine: exact up to 64 values, within about 1 % past them, which orders
/// columns whose counts differ by more and leaves the rest to the schema's order.
/// </para>
/// </remarks>
internal static class SynopsisOrder
{
    /// <summary>
    /// The columns that may be synopsis columns: top-level, of a type whose values have an order of codes, not the
    /// instant, a partition column, whose one value an object holds, nor an identifier.
    /// </summary>
    internal static int[] Candidates(DType schema, ClusteringKey key, IReadOnlyList<string>? identifiers)
    {
        List<int> candidates = [];
        for (int field = 0; field < schema.FieldCount; field++)
        {
            string name = schema.GetFieldName(field);
            if (string.Equals(name, key.InstantPath, StringComparison.Ordinal)
                || key.PartitionPaths.Contains(name)
                || (identifiers is not null && Contains(identifiers, name))
                || !Coded(schema.GetField(field)))
            {
                continue;
            }

            candidates.Add(field);
        }

        return [.. candidates];
    }

    /// <summary>
    /// The synopsis columns among <paramref name="counted"/>, each a field and its distinct values over
    /// <paramref name="rows"/> rows: those whose values repeat twice on average, fewest values first, the
    /// schema's order breaking ties.
    /// </summary>
    internal static int[] Choose(ReadOnlySpan<(int Field, long Distinct)> counted, long rows)
    {
        List<(int Field, long Distinct)> kept = [];
        foreach ((int field, long distinct) in counted)
        {
            if (distinct > 0 && distinct * 2 <= rows)
            {
                kept.Add((field, distinct));
            }
        }

        kept.Sort(static (left, right) => left.Distinct != right.Distinct ? left.Distinct.CompareTo(right.Distinct) : left.Field.CompareTo(right.Field));
        int[] order = new int[kept.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = kept[i].Field;
        }

        return order;
    }

    /// <summary>
    /// Whether the values of field <paramref name="field"/> of the struct <paramref name="node"/>, <paramref name="rows"/> of
    /// them, may repeat twice on average: a sketch of their distinct values says it of a column far from the bound, a value
    /// a row as a duration or a weight holds, without its codes and the table of its values, whose coding holds some 200 MB
    /// for three million rows. The bound takes in the sketch's error, six of its standard errors of 0.81 %: a column it
    /// leaves out repeats less than twice in all likelihood, one it lets in is coded and judged on its count.
    /// </summary>
    internal static bool MayRepeat(CanonicalArena arena, int node, DType schema, int field, int rows)
    {
        CardinalitySketch sketch = new CardinalitySketch();
        CountColumn(arena, RowEncoder.Storage(arena, arena.GetNode(node).GetFieldIndex(field)), schema.GetField(field), sketch);
        return sketch.Estimate() * 2 <= rows * (1 + (6 * 0.0081));
    }

    /// <summary>Counts the distinct values of the columns <paramref name="fields"/> of <paramref name="batch"/> into <paramref name="sketches"/>, one each.</summary>
    internal static void Count(RecordBatch batch, ReadOnlySpan<int> fields, CardinalitySketch[] sketches)
    {
        CanonicalArena arena = batch.Arena;
        CanonicalNode root = arena.GetNode(batch.RootIndex);
        for (int i = 0; i < fields.Length; i++)
        {
            int node = RowEncoder.Storage(arena, root.GetFieldIndex(fields[i]));
            CountColumn(arena, node, batch.DType.GetField(fields[i]), sketches[i]);
        }
    }

    private static void CountColumn(CanonicalArena arena, int node, DType type, CardinalitySketch sketch)
    {
        int canonical = EncodedForms.Canonical(arena, node);
        ref readonly CanonicalRecord record = ref arena.RecordRef(canonical);
        int rows = record.Length;

        // The column's validity read once, where a row's went through the arena's records.
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, canonical);
        if (record.Kind == CanonicalKind.Primitive)
        {
            int width = record.PType.ByteWidth();
            ReadOnlySpan<byte> values = record.BufferA.Span;
            bool isFloat = record.PType is PType.F16 or PType.F32 or PType.F64;
            Span<byte> scratch = stackalloc byte[sizeof(double)];
            for (int row = 0; row < rows; row++)
            {
                if (!valid.IsEmpty && ((valid[row >> 6] >> (row & 63)) & 1) == 0)
                {
                    continue;
                }

                ReadOnlySpan<byte> value = values.Slice(row * width, width);
                sketch.Add(CardinalitySlot.Hash(isFloat ? Normalized(record.PType, value, scratch) : value));
            }

            return;
        }

        if (record.Kind == CanonicalKind.Bool)
        {
            // A bool has two values at most: counted by the values it holds.
            ReadOnlySpan<ulong> bits = ArenaWords.Bits(arena, canonical);
            for (int row = 0; row < rows; row++)
            {
                if (valid.IsEmpty || ((valid[row >> 6] >> (row & 63)) & 1) != 0)
                {
                    sketch.Add(CardinalitySlot.Hash(((bits[row >> 6] >> (row & 63)) & 1) != 0 ? [1] : [0]));
                }
            }

            return;
        }

        if (record.Kind == CanonicalKind.VarBinView)
        {
            // A row whose view is the valid row's before it holds that row's value, whose hash the sketch holds already:
            // a sketch is the same whether a hash is added once or twice, and names drawn a row at a time often repeat.
            ReadOnlySpan<Int128> views = MemoryMarshal.Cast<byte, Int128>(record.BufferA.Span)[..rows];
            ViewValues strings = new ViewValues(arena.GetNode(canonical));
            bool any = false;
            Int128 before = default;
            for (int row = 0; row < rows; row++)
            {
                if (!valid.IsEmpty && ((valid[row >> 6] >> (row & 63)) & 1) == 0)
                {
                    continue;
                }

                Int128 view = views[row];
                if (any && view == before)
                {
                    continue;
                }

                (any, before) = (true, view);
                sketch.Add(CardinalitySlot.Hash(strings.At(row)));
            }

            return;
        }

        for (int row = 0; row < rows; row++)
        {
            if (valid.IsEmpty || ((valid[row >> 6] >> (row & 63)) & 1) != 0)
            {
                sketch.Add(CardinalitySlot.Hash(ColumnData.Bytes(arena, canonical, row)));
            }
        }
    }

    /// <summary>A float's bytes with its zeros and its NaNs each taken as one value.</summary>
    private static ReadOnlySpan<byte> Normalized(PType type, ReadOnlySpan<byte> value, Span<byte> scratch)
    {
        switch (type)
        {
            case PType.F64:
            {
                double d = MemoryMarshal.Read<double>(value);
                d = double.IsNaN(d) ? double.NaN : d == 0 ? 0 : d;
                MemoryMarshal.Write(scratch, in d);
                return scratch[..sizeof(double)];
            }

            case PType.F32:
            {
                float f = MemoryMarshal.Read<float>(value);
                f = float.IsNaN(f) ? float.NaN : f == 0 ? 0 : f;
                MemoryMarshal.Write(scratch, in f);
                return scratch[..sizeof(float)];
            }

            default:
            {
                Half h = MemoryMarshal.Read<Half>(value);
                h = Half.IsNaN(h) ? Half.NaN : h == Half.Zero ? Half.Zero : h;
                MemoryMarshal.Write(scratch, in h);
                return scratch[..2];
            }
        }
    }

    /// <summary>Whether a column's values have an order of codes: a number, a bool, text or bytes, or an extension stored as one.</summary>
    private static bool Coded(DType type)
    {
        DType storage = type;
        while (!storage.IsDefault && storage.Kind == DTypeKind.Extension)
        {
            storage = storage.StorageType;
        }

        return storage.Kind is DTypeKind.Primitive or DTypeKind.Bool or DTypeKind.Utf8 or DTypeKind.Binary;
    }

    private static bool Contains(IReadOnlyList<string> names, string name)
    {
        foreach (string candidate in names)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
