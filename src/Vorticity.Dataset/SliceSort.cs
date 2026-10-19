using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Compute;
using Vorticity.RowEncoding;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The permutation that sorts a slice by its synopsis columns, rows of equal values keeping their order:
/// each column's values coded in the order of the values, then one stable counting sort per column, from
/// the last to the first. The rows come in time order, so the instant needs no pass of its own.
/// </summary>
/// <remarks>
/// <para>
/// A column's codes come from an open-addressing table of its distinct values, then the distinct values
/// sorted: numbers by their value, a float's zeros as one and its NaN first as <see cref="double.CompareTo(double)"/>
/// puts it, text and bytes by their bytes, false before true. A null codes last. A counting sort over
/// codes is one pass of the rows and one of the codes, so a slice costs a pass a column, as a radix
/// sort over dictionary codes does.
/// </para>
/// <para>
/// <see cref="Compare"/> is the order the codes follow, for whoever checks a slice against its table.
/// </para>
/// </remarks>
internal static class SliceSort
{
    /// <summary>The permutation of <paramref name="rows"/> rows sorted by <paramref name="columns"/>' codes, the first the most significant, ties in time order.</summary>
    internal static int[] Permutation(IReadOnlyList<(int[] Codes, int Distinct)> columns, int rows)
    {
        int[] permutation = new int[rows];
        for (int i = 0; i < rows; i++)
        {
            permutation[i] = i;
        }

        int[] scratch = new int[rows];
        for (int k = columns.Count - 1; k >= 0; k--)
        {
            CountingSort(permutation, scratch, columns[k].Codes, columns[k].Distinct + 1);
        }

        return permutation;
    }

    /// <summary>
    /// The order of <paramref name="columns"/> that adds the fewest runs at each place, greedily: first the
    /// column whose rows split into the fewest runs, then the one that splits the runs so far into the
    /// fewest, and so on, ties in the columns' given order. Measured over every <paramref name="stride"/>-th
    /// row of the <paramref name="rows"/>, 1 for all of them.
    /// </summary>
    /// <remarks>
    /// Sorted by a prefix of columns, the rows of each combination of their values lie in one run, so the
    /// runs a column adds at its place are the combinations it makes with the columns before it, which the
    /// order by numbers of distinct values counts only when the columns are independent. A sparse column,
    /// such as a shredded path null outside the shapes that hold it, splits only the runs of those shapes.
    /// </remarks>
    internal static int[] Greedy(IReadOnlyList<(int[] Codes, int Distinct)> columns, int rows, int stride = 1)
    {
        int sampled = (rows + stride - 1) / stride;
        int[] groups = new int[sampled];
        List<int> left = [];
        for (int k = 0; k < columns.Count; k++)
        {
            left.Add(k);
        }

        int[] order = new int[columns.Count];
        Dictionary<long, int> pairs = [];
        for (int place = 0; place < order.Length; place++)
        {
            int best = -1;
            int bestRuns = int.MaxValue;
            foreach (int k in left)
            {
                int runs = Combinations(groups, columns[k].Codes, stride, pairs, assign: null);
                if (runs < bestRuns)
                {
                    (best, bestRuns) = (k, runs);
                }
            }

            order[place] = best;
            left.Remove(best);
            Combinations(groups, columns[best].Codes, stride, pairs, assign: groups);
        }

        return order;
    }

    /// <summary>
    /// The runs every column adds over <paramref name="rows"/> rows sorted by <paramref name="columns"/> in
    /// their order, summed: the combinations of each prefix's values.
    /// </summary>
    internal static long Runs(IReadOnlyList<int[]> columns, int rows)
    {
        int[] groups = new int[rows];
        Dictionary<long, int> pairs = [];
        long runs = 0;
        foreach (int[] codes in columns)
        {
            runs += Combinations(groups, codes, 1, pairs, assign: groups);
        }

        return runs;
    }

    /// <summary>
    /// How many distinct pairs of a row's group in <paramref name="groups"/> and its code in
    /// <paramref name="codes"/> there are, over every <paramref name="stride"/>-th row; each sampled row's
    /// pair numbered into <paramref name="assign"/> when it is given.
    /// </summary>
    private static int Combinations(int[] groups, int[] codes, int stride, Dictionary<long, int> pairs, int[]? assign)
    {
        pairs.Clear();
        for (int i = 0; i < groups.Length; i++)
        {
            long pair = ((long)groups[i] << 32) | (uint)codes[i * stride];
            if (!pairs.TryGetValue(pair, out int number))
            {
                pairs[pair] = number = pairs.Count;
            }

            if (assign is not null)
            {
                assign[i] = number;
            }
        }

        return pairs.Count;
    }

    /// <summary>
    /// Each row's code for order entry <paramref name="entry"/> of the struct node <paramref name="root"/>, nulls
    /// last, and how many distinct values there are: a field's values, or a variant field's shapes.
    /// </summary>
    internal static (int[] Codes, int Distinct) CodesOf(
        CanonicalArena arena, int root, int entry, int rows, IReadOnlyDictionary<int, Types.Variants.VariantShapes>? shapes)
    {
        CanonicalNode node = arena.GetNode(root);
        if (entry < 0)
        {
            int field = -entry - 1;
            if (shapes is null || !shapes.TryGetValue(field, out Types.Variants.VariantShapes? coder))
            {
                throw new InvalidOperationException($"The order names the shapes of field {field}, which nothing shreds.");
            }

            return coder.Codes(arena, node.GetFieldIndex(field), rows);
        }

        int column = EncodedForms.Canonical(arena, RowEncoder.Storage(arena, node.GetFieldIndex(entry)));
        return Codes(arena, column, rows);
    }

    /// <summary>
    /// How two rows of a column compare in a slice's order: by value, nulls last. The order
    /// <see cref="CodesOf"/> codes a column in.
    /// </summary>
    internal static int Compare(CanonicalArena arena, int column, int left, int right)
    {
        bool leftValid = ArenaWords.IsValid(arena, column, left);
        bool rightValid = ArenaWords.IsValid(arena, column, right);
        if (!leftValid || !rightValid)
        {
            return leftValid == rightValid ? 0 : leftValid ? -1 : 1;
        }

        ref readonly CanonicalRecord record = ref arena.RecordRef(column);
        return record.Kind switch
        {
            CanonicalKind.Primitive => ComparePrimitive(record.PType, record.BufferA.Span, left, right),
            CanonicalKind.Bool => Bit(arena, column, left).CompareTo(Bit(arena, column, right)),
            _ => ColumnData.Bytes(arena, column, left).SequenceCompareTo(ColumnData.Bytes(arena, column, right)),
        };
    }

    /// <summary>Each row's code in the order of the values, nulls last, and how many distinct values there are.</summary>
    private static (int[] Codes, int Distinct) Codes(CanonicalArena arena, int column, int rows)
    {
        int[] codes = new int[rows];
        CanonicalNode node = arena.GetNode(column);
        ref readonly CanonicalRecord record = ref arena.RecordRef(column);
        CanonicalKind kind = record.Kind;
        PType ptype = record.PType;
        ReadOnlySpan<byte> values = kind == CanonicalKind.Primitive ? record.BufferA.Span : default;
        int width = kind == CanonicalKind.Primitive ? ptype.ByteWidth() : 0;
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, column);
        ReadOnlySpan<ulong> bools = kind == CanonicalKind.Bool ? ArenaWords.Bits(arena, column) : default;

        // The distinct values, each by the first row holding it and its key, found through a table that
        // doubles past half full: its size follows the distinct values, not the rows. A number's key is
        // its bits, so equal keys are equal values; text's is a hash, so equal keys are compared.
        List<int> firsts = [];
        List<ulong> keys = [];
        bool hashed = kind is not (CanonicalKind.Primitive or CanonicalKind.Bool);
        int bits = 8;
        int[] table = new int[1 << bits];
        for (int row = 0; row < rows; row++)
        {
            if (!valid.IsEmpty && ((valid[row >> 6] >> (row & 63)) & 1) == 0)
            {
                codes[row] = -1;
                continue;
            }

            ReadOnlySpan<byte> bytes = hashed ? LiteralReader.ViewAt(node, row) : default;
            ulong key = kind switch
            {
                CanonicalKind.Primitive => Key(ptype, values.Slice(row * width, width)),
                CanonicalKind.Bool => (bools[row >> 6] >> (row & 63)) & 1,
                _ => XxHash3.HashToUInt64(bytes),
            };
            int mask = table.Length - 1;
            for (int slot = (int)(Mix(key) >> (64 - bits)); ; slot = (slot + 1) & mask)
            {
                int entry = table[slot];
                if (entry == 0)
                {
                    firsts.Add(row);
                    keys.Add(key);
                    table[slot] = firsts.Count;
                    codes[row] = firsts.Count - 1;
                    break;
                }

                if (keys[entry - 1] == key && (!hashed || LiteralReader.ViewAt(node, firsts[entry - 1]).SequenceEqual(bytes)))
                {
                    codes[row] = entry - 1;
                    break;
                }
            }

            if (firsts.Count * 2 > table.Length)
            {
                bits++;
                table = new int[1 << bits];
                int grown = table.Length - 1;
                for (int i = 0; i < keys.Count; i++)
                {
                    int slot = (int)(Mix(keys[i]) >> (64 - bits));
                    while (table[slot] != 0)
                    {
                        slot = (slot + 1) & grown;
                    }

                    table[slot] = i + 1;
                }
            }
        }

        // The distinct values sorted, and each code turned into its value's rank; a null last.
        int distinct = firsts.Count;
        int[] byValue = new int[distinct];
        for (int i = 0; i < distinct; i++)
        {
            byValue[i] = i;
        }

        int[] rowsOf = [.. firsts];
        Array.Sort(byValue, (left, right) => Compare(arena, column, rowsOf[left], rowsOf[right]));
        int[] rank = new int[distinct];
        for (int i = 0; i < distinct; i++)
        {
            rank[byValue[i]] = i;
        }

        for (int row = 0; row < rows; row++)
        {
            codes[row] = codes[row] < 0 ? distinct : rank[codes[row]];
        }

        return (codes, distinct);
    }

    /// <summary>Sorts <paramref name="permutation"/> by <paramref name="codes"/> of its rows, stably, through <paramref name="scratch"/>.</summary>
    private static void CountingSort(int[] permutation, int[] scratch, int[] codes, int buckets)
    {
        int[] starts = new int[buckets + 1];
        foreach (int code in codes)
        {
            starts[code + 1]++;
        }

        for (int b = 0; b < buckets; b++)
        {
            starts[b + 1] += starts[b];
        }

        foreach (int row in permutation)
        {
            scratch[starts[codes[row]]++] = row;
        }

        scratch.AsSpan().CopyTo(permutation);
    }

    /// <summary>A number's bits as a key: a float's zeros and NaNs each one key.</summary>
    private static ulong Key(PType ptype, ReadOnlySpan<byte> value)
    {
        switch (ptype)
        {
            case PType.F64:
            {
                double d = MemoryMarshal.Read<double>(value);
                return BitConverter.DoubleToUInt64Bits(double.IsNaN(d) ? double.NaN : d == 0 ? 0 : d);
            }

            case PType.F32:
            {
                float f = MemoryMarshal.Read<float>(value);
                return BitConverter.SingleToUInt32Bits(float.IsNaN(f) ? float.NaN : f == 0 ? 0 : f);
            }

            case PType.F16:
            {
                Half h = MemoryMarshal.Read<Half>(value);
                return BitConverter.HalfToUInt16Bits(Half.IsNaN(h) ? Half.NaN : h == Half.Zero ? Half.Zero : h);
            }

            default:
                Span<byte> wide = stackalloc byte[8];
                wide.Clear();
                value.CopyTo(wide);
                return BinaryPrimitives.ReadUInt64LittleEndian(wide);
        }
    }

    private static ulong Mix(ulong key) => (key ^ (key >> 29)) * 0x9E3779B97F4A7C15UL;

    private static int ComparePrimitive(PType ptype, ReadOnlySpan<byte> values, int left, int right) => ptype switch
    {
        PType.I8 => ((sbyte)values[left]).CompareTo((sbyte)values[right]),
        PType.I16 => Read<short>(values, left).CompareTo(Read<short>(values, right)),
        PType.I32 => Read<int>(values, left).CompareTo(Read<int>(values, right)),
        PType.I64 => Read<long>(values, left).CompareTo(Read<long>(values, right)),
        PType.U8 => values[left].CompareTo(values[right]),
        PType.U16 => Read<ushort>(values, left).CompareTo(Read<ushort>(values, right)),
        PType.U32 => Read<uint>(values, left).CompareTo(Read<uint>(values, right)),
        PType.U64 => Read<ulong>(values, left).CompareTo(Read<ulong>(values, right)),
        PType.F16 => Zeroed(Read<Half>(values, left)).CompareTo(Zeroed(Read<Half>(values, right))),
        PType.F32 => Zeroed(Read<float>(values, left)).CompareTo(Zeroed(Read<float>(values, right))),
        _ => Zeroed(Read<double>(values, left)).CompareTo(Zeroed(Read<double>(values, right))),
    };

    private static T Read<T>(ReadOnlySpan<byte> values, int row)
        where T : unmanaged => MemoryMarshal.Read<T>(values[(row * System.Runtime.CompilerServices.Unsafe.SizeOf<T>())..]);

    private static double Zeroed(double value) => value == 0 ? 0 : value;

    private static float Zeroed(float value) => value == 0 ? 0 : value;

    private static Half Zeroed(Half value) => value == Half.Zero ? Half.Zero : value;

    private static bool Bit(CanonicalArena arena, int column, int row) =>
        ((ArenaWords.Bits(arena, column)[row >> 6] >> (row & 63)) & 1) != 0;
}
