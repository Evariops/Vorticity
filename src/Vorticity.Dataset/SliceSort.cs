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
    internal static int[] Permutation(IReadOnlyList<(int[] Codes, int Distinct)> columns, int rows) =>
        Permutation(columns, rows, new SliceSortScratch());

    /// <summary>
    /// <see cref="Permutation(IReadOnlyList{ValueTuple{int[], int}}, int)"/> in the arrays of <paramref name="scratch"/>,
    /// which a sorter keeps from one slice to the next: the permutation is valid over its first <paramref name="rows"/>
    /// entries until the next sort through the same scratch.
    /// </summary>
    internal static int[] Permutation(IReadOnlyList<(int[] Codes, int Distinct)> columns, int rows, SliceSortScratch scratch)
    {
        scratch.PackedBits = 0;
        if (Packs(columns, scratch) is > 0 and <= 64)
        {
            return PackedPermutation(columns, rows, scratch);
        }

        int[] permutation = SliceSortScratch.AtLeast(ref scratch.Permutation, rows);
        for (int i = 0; i < rows; i++)
        {
            permutation[i] = i;
        }

        int[] spare = SliceSortScratch.AtLeast(ref scratch.Spare, rows);
        for (int k = columns.Count - 1; k >= 0; k--)
        {
            CountingSort(permutation, spare, columns[k].Codes, columns[k].Distinct + 1, rows, scratch);
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
    /// Each row's code for the column node <paramref name="column"/>, its values in their order, nulls last,
    /// and how many distinct values there are: a path's values, read into a column of their own.
    /// </summary>
    internal static (int[] Codes, int Distinct) CodesOfColumn(CanonicalArena arena, int column, int rows) =>
        Codes(arena, EncodedForms.Canonical(arena, RowEncoder.Storage(arena, column)), rows);

    /// <summary>
    /// The canonical node <see cref="CodesOfCanonical"/> codes for field <paramref name="entry"/> of the struct node
    /// <paramref name="root"/>, its words of validity and bits built: what is laid in the arena before codes are
    /// counted on several threads, which then only read it.
    /// </summary>
    internal static int CanonicalOf(CanonicalArena arena, int root, int entry)
    {
        int column = EncodedForms.Canonical(arena, RowEncoder.Storage(arena, arena.GetNode(root).GetFieldIndex(entry)));
        _ = ArenaWords.Validity(arena, column);
        if (arena.RecordRef(column).Kind == CanonicalKind.Bool)
        {
            _ = ArenaWords.Bits(arena, column);
        }

        return column;
    }

    /// <summary>
    /// <see cref="CodesOf"/> for the canonical column <paramref name="column"/> <see cref="CanonicalOf"/> made: it reads
    /// the arena and writes nothing to it, so that the columns of a slice are coded on threads of their own.
    /// </summary>
    internal static (int[] Codes, int Distinct) CodesOfCanonical(CanonicalArena arena, int column, int rows) => Codes(arena, column, rows);

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
        return (codes, Codes(arena, column, rows, codes, new SliceSortScratch()));
    }

    /// <summary>
    /// Each of <paramref name="rows"/> rows' code into <paramref name="codes"/>, in the order of the values, nulls last, the
    /// work done in the arrays of <paramref name="scratch"/>, which a sorter keeps a column from one slice to the next: how
    /// many distinct values there are. Nothing is allocated once the arrays have grown to a slice's size.
    /// </summary>
    internal static int Codes(CanonicalArena arena, int column, int rows, int[] codes, SliceSortScratch scratch)
    {
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
        int[] firsts = scratch.Firsts;
        ulong[] keys = scratch.Keys;
        int distinct = 0;
        bool hashed = kind is not (CanonicalKind.Primitive or CanonicalKind.Bool);
        int bits = 8;
        int[] table = SliceSortScratch.AtLeast(ref scratch.Table, 1 << bits);
        table.AsSpan(0, 1 << bits).Clear();
        int mask = (1 << bits) - 1;
        bool views = kind == CanonicalKind.VarBinView && record.PaddedViews && valid.IsEmpty;
        if (views)
        {
            distinct = DistinctViews(node, MemoryMarshal.Cast<byte, Int128>(record.BufferA.Span)[..rows], codes, scratch);
            firsts = scratch.Firsts;
        }

        for (int row = 0; row < rows && !views; row++)
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
            for (int slot = (int)(Mix(key) >> (64 - bits)); ; slot = (slot + 1) & mask)
            {
                int entry = table[slot];
                if (entry == 0)
                {
                    if (distinct == firsts.Length)
                    {
                        Array.Resize(ref scratch.Firsts, distinct * 2);
                        Array.Resize(ref scratch.Keys, distinct * 2);
                        firsts = scratch.Firsts;
                        keys = scratch.Keys;
                    }

                    firsts[distinct] = row;
                    keys[distinct] = key;
                    table[slot] = ++distinct;
                    codes[row] = distinct - 1;
                    break;
                }

                if (keys[entry - 1] == key && (!hashed || LiteralReader.ViewAt(node, firsts[entry - 1]).SequenceEqual(bytes)))
                {
                    codes[row] = entry - 1;
                    break;
                }
            }

            if (distinct * 2 > mask + 1)
            {
                bits++;
                table = SliceSortScratch.AtLeast(ref scratch.Table, 1 << bits);
                table.AsSpan(0, 1 << bits).Clear();
                mask = (1 << bits) - 1;
                for (int i = 0; i < distinct; i++)
                {
                    int slot = (int)(Mix(keys[i]) >> (64 - bits));
                    while (table[slot] != 0)
                    {
                        slot = (slot + 1) & mask;
                    }

                    table[slot] = i + 1;
                }
            }
        }

        // The distinct values sorted, and each code turned into its value's rank; a null last.
        int[] byValue = SliceSortScratch.AtLeast(ref scratch.ByValue, distinct);
        for (int i = 0; i < distinct; i++)
        {
            byValue[i] = i;
        }

        if (hashed)
        {
            Array.Sort(byValue, 0, distinct, scratch.ComparerOver(arena, column, firsts));
        }
        else
        {
            // A number's keys sort as unsigned numbers in its values' order, without a comparison called per pair.
            ulong[] ordered = SliceSortScratch.AtLeast(ref scratch.Ordered, distinct);
            for (int i = 0; i < distinct; i++)
            {
                ordered[i] = Ordered(kind, ptype, keys[i]);
            }

            Array.Sort(ordered, byValue, 0, distinct);
        }

        int[] rank = SliceSortScratch.AtLeast(ref scratch.Rank, distinct);
        for (int i = 0; i < distinct; i++)
        {
            rank[byValue[i]] = i;
        }

        for (int row = 0; row < rows; row++)
        {
            codes[row] = codes[row] < 0 ? distinct : rank[codes[row]];
        }

        return distinct;
    }

    /// <summary>
    /// The distinct values of a column of padded views and no null, numbered in the order of the rows they first appear at,
    /// each row's number into <paramref name="codes"/>: their count, their first rows in the scratch's firsts. A view of twelve
    /// bytes or fewer is its value, its length and its bytes and the zeros past them, and keys the table whole, where a
    /// value read out of its view and hashed took a call and a hash a row and a call and a comparison of bytes a probe; one
    /// past twelve bytes is told apart by its length and prefix, then by its bytes.
    /// </summary>
    private static int DistinctViews(CanonicalNode node, ReadOnlySpan<Int128> views, int[] codes, SliceSortScratch scratch)
    {
        int[] firsts = scratch.Firsts;
        ulong[] hashes = scratch.Keys;
        Int128[] keys = SliceSortScratch.AtLeast(ref scratch.Views, firsts.Length);
        int distinct = 0;
        int bits = 8;
        int[] table = SliceSortScratch.AtLeast(ref scratch.Table, 1 << bits);
        table.AsSpan(0, 1 << bits).Clear();
        int mask = (1 << bits) - 1;
        for (int row = 0; row < views.Length; row++)
        {
            Int128 view = views[row];
            uint size = (uint)(ulong)view;
            ulong hash = size <= 12
                ? Mix((ulong)view ^ Mix((ulong)(view >> 64)))
                : XxHash3.HashToUInt64(LiteralReader.ViewAt(node, row));
            for (int slot = (int)(Mix(hash) >> (64 - bits)); ; slot = (slot + 1) & mask)
            {
                int entry = table[slot];
                if (entry == 0)
                {
                    if (distinct == firsts.Length)
                    {
                        Array.Resize(ref scratch.Firsts, distinct * 2);
                        Array.Resize(ref scratch.Keys, distinct * 2);
                        Array.Resize(ref scratch.Views, distinct * 2);
                        firsts = scratch.Firsts;
                        hashes = scratch.Keys;
                        keys = scratch.Views;
                    }

                    firsts[distinct] = row;
                    hashes[distinct] = hash;
                    keys[distinct] = view;
                    table[slot] = ++distinct;
                    codes[row] = distinct - 1;
                    break;
                }

                Int128 other = keys[entry - 1];
                if (hashes[entry - 1] == hash && (other == view || (size > 12 && (ulong)other == (ulong)view
                    && LiteralReader.ViewAt(node, row).SequenceEqual(LiteralReader.ViewAt(node, firsts[entry - 1])))))
                {
                    codes[row] = entry - 1;
                    break;
                }
            }

            if (distinct * 2 > mask + 1)
            {
                bits++;
                table = SliceSortScratch.AtLeast(ref scratch.Table, 1 << bits);
                table.AsSpan(0, 1 << bits).Clear();
                mask = (1 << bits) - 1;
                for (int i = 0; i < distinct; i++)
                {
                    int slot = (int)(Mix(hashes[i]) >> (64 - bits));
                    while (table[slot] != 0)
                    {
                        slot = (slot + 1) & mask;
                    }

                    table[slot] = i + 1;
                }
            }
        }

        return distinct;
    }

    /// <summary>
    /// The bits the columns' codes take together, the first column's highest, each its own codes' with nulls': past 64, the
    /// codes do not pack into a word. The shift and the width of each column go to <paramref name="scratch"/>.
    /// </summary>
    private static int Packs(IReadOnlyList<(int[] Codes, int Distinct)> columns, SliceSortScratch scratch)
    {
        int bits = 0;
        int[] widths = SliceSortScratch.AtLeast(ref scratch.Widths, columns.Count);
        for (int k = 0; k < columns.Count; k++)
        {
            widths[k] = BitOperations.Log2((uint)Math.Max(columns[k].Distinct, 1)) + 1;
            bits += widths[k];
        }

        int[] shifts = SliceSortScratch.AtLeast(ref scratch.Shifts, columns.Count);
        int below = bits;
        for (int k = 0; k < columns.Count; k++)
        {
            below -= widths[k];
            shifts[k] = below;
        }

        return bits;
    }

    /// <summary>
    /// The permutation of rows whose codes pack into a word: each row's codes packed, the first column's highest, and the
    /// words sorted with their rows by a stable radix sort eleven bits a pass, which orders the rows as the counting sorts
    /// of the columns one after the other did, ties in the order the rows came. Where those read each column's codes at
    /// random, a pass a column, and the codes were then gathered in the rows' order at random again, the sorted words hold
    /// them in that order: <see cref="SliceSortScratch.SortedKeys"/>, for <see cref="Unpack"/>.
    /// </summary>
    private static int[] PackedPermutation(IReadOnlyList<(int[] Codes, int Distinct)> columns, int rows, SliceSortScratch scratch)
    {
        const int Digit = 11;
        const int Radix = 1 << Digit;
        int bits = 0;
        for (int k = 0; k < columns.Count; k++)
        {
            bits += scratch.Widths[k];
        }

        ulong[] keys = SliceSortScratch.AtLeast(ref scratch.SortedKeys, rows);
        ulong[] otherKeys = SliceSortScratch.AtLeast(ref scratch.SpareKeys, rows);
        int[] permutation = SliceSortScratch.AtLeast(ref scratch.Permutation, rows);
        int[] other = SliceSortScratch.AtLeast(ref scratch.Spare, rows);
        int count = columns.Count;
        int[] widths = scratch.Widths;
        int[][] codes = new int[count][];
        for (int k = 0; k < count; k++)
        {
            codes[k] = columns[k].Codes;
        }

        for (int i = 0; i < rows; i++)
        {
            ulong key = 0;
            for (int k = 0; k < count; k++)
            {
                key = (key << widths[k]) | (uint)codes[k][i];
            }

            keys[i] = key;
            permutation[i] = i;
        }

        Span<int> starts = SliceSortScratch.AtLeast(ref scratch.Starts, Radix + 1).AsSpan(0, Radix + 1);
        for (int shift = 0; shift < bits; shift += Digit)
        {
            starts.Clear();
            for (int i = 0; i < rows; i++)
            {
                starts[(int)((keys[i] >> shift) & (Radix - 1)) + 1]++;
            }

            // A digit every row holds alike moves nothing.
            if (rows == 0 || starts[(int)((keys[0] >> shift) & (Radix - 1)) + 1] == rows)
            {
                continue;
            }

            for (int d = 0; d < Radix; d++)
            {
                starts[d + 1] += starts[d];
            }

            for (int i = 0; i < rows; i++)
            {
                int at = starts[(int)((keys[i] >> shift) & (Radix - 1))]++;
                otherKeys[at] = keys[i];
                other[at] = permutation[i];
            }

            (keys, otherKeys) = (otherKeys, keys);
            (permutation, other) = (other, permutation);
        }

        (scratch.SortedKeys, scratch.SpareKeys, scratch.Permutation, scratch.Spare) = (keys, otherKeys, permutation, other);
        scratch.PackedBits = bits;
        return permutation;
    }

    /// <summary>
    /// Column <paramref name="k"/>'s codes in the rows' sorted order, out of the words <see cref="PackedPermutation"/>
    /// sorted, into <paramref name="codes"/>.
    /// </summary>
    internal static void Unpack(SliceSortScratch scratch, int k, int[] codes, int rows)
    {
        ulong[] keys = scratch.SortedKeys;
        int shift = scratch.Shifts[k];
        ulong mask = (1UL << scratch.Widths[k]) - 1;
        for (int i = 0; i < rows; i++)
        {
            codes[i] = (int)((keys[i] >> shift) & mask);
        }
    }

    /// <summary>
    /// Sorts the first <paramref name="rows"/> entries of <paramref name="permutation"/> by <paramref name="codes"/> of
    /// their rows, stably, through <paramref name="spare"/>, the buckets' starts in <paramref name="scratch"/>'s.
    /// </summary>
    private static void CountingSort(int[] permutation, int[] spare, int[] codes, int buckets, int rows, SliceSortScratch scratch)
    {
        Span<int> starts = SliceSortScratch.AtLeast(ref scratch.Starts, buckets + 1).AsSpan(0, buckets + 1);
        starts.Clear();
        foreach (int code in codes.AsSpan(0, rows))
        {
            starts[code + 1]++;
        }

        for (int b = 0; b < buckets; b++)
        {
            starts[b + 1] += starts[b];
        }

        foreach (int row in permutation.AsSpan(0, rows))
        {
            spare[starts[codes[row]]++] = row;
        }

        spare.AsSpan(0, rows).CopyTo(permutation);
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

    private const ulong SignBit = 1UL << 63;

    /// <summary>
    /// A key of <see cref="Key"/> as an unsigned number in its value's order, as <see cref="Compare"/> orders them:
    /// a signed integer's sign flipped, a float's bits flipped as its sign says, its NaNs first, a bool as it is.
    /// </summary>
    private static ulong Ordered(CanonicalKind kind, PType ptype, ulong key)
    {
        if (kind == CanonicalKind.Bool)
        {
            return key;
        }

        return ptype switch
        {
            PType.I8 => (ulong)(long)(sbyte)key ^ SignBit,
            PType.I16 => (ulong)(long)(short)key ^ SignBit,
            PType.I32 => (ulong)(long)(int)key ^ SignBit,
            PType.I64 => key ^ SignBit,
            PType.F64 => Ordered(BitConverter.UInt64BitsToDouble(key)),
            PType.F32 => Ordered(BitConverter.UInt32BitsToSingle((uint)key)),
            PType.F16 => Ordered((double)BitConverter.UInt16BitsToHalf((ushort)key)),
            _ => key,
        };
    }

    /// <summary>
    /// A float as an unsigned number in its order: 0 for a NaN, which comes first, and past it every other value,
    /// the least -∞ at 2^52 - 1, a zero its sign put away by <see cref="Key"/>.
    /// </summary>
    private static ulong Ordered(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        return (bits & SignBit) != 0 ? ~bits : bits | SignBit;
    }

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

/// <summary>
/// The arrays a column's codes and a slice's permutation are worked out in, which a sorter keeps from one slice to the
/// next, a column's on the lane that codes it: the table of the distinct values, their first rows and keys, their order
/// and their ranks, and the permutation's arrays. A sort through it allocates nothing once its arrays have grown to a
/// slice's size; it serves one sort at a time.
/// </summary>
internal sealed class SliceSortScratch
{
    internal int[] Table = [];
    internal int[] Firsts = new int[256];
    internal ulong[] Keys = new ulong[256];
    internal int[] ByValue = [];
    internal ulong[] Ordered = [];
    internal int[] Rank = [];
    internal int[] Permutation = [];
    internal int[] Spare = [];
    internal int[] Starts = [];

    // A permutation of packed codes (SliceSort.PackedPermutation): the words sorted and their spares, each column's width
    // and shift in a word, and the words' bits, 0 when the codes were sorted a column at a time.
    internal ulong[] SortedKeys = [];
    internal ulong[] SpareKeys = [];

    // The views of a column of padded views' distinct values (SliceSort.DistinctViews), by their numbers.
    internal Int128[] Views = [];
    internal int[] Widths = [];
    internal int[] Shifts = [];
    internal int PackedBits;
    private readonly FirstRows _comparer = new FirstRows();

    /// <summary><paramref name="array"/>, grown to hold <paramref name="length"/> when it is shorter.</summary>
    internal static T[] AtLeast<T>(ref T[] array, int length) =>
        array.Length >= length ? array : array = new T[Math.Max(length, array.Length * 2)];

    /// <summary>The order of distinct values by the rows <paramref name="firsts"/> finds them at, as <see cref="SliceSort.Compare"/> orders rows.</summary>
    internal IComparer<int> ComparerOver(CanonicalArena arena, int column, int[] firsts)
    {
        _comparer.Arena = arena;
        _comparer.Column = column;
        _comparer.Firsts = firsts;
        return _comparer;
    }

    private sealed class FirstRows : IComparer<int>
    {
        internal CanonicalArena? Arena;
        internal int Column;
        internal int[] Firsts = [];

        public int Compare(int left, int right) => SliceSort.Compare(Arena!, Column, Firsts[left], Firsts[right]);
    }
}
