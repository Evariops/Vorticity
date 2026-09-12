// Choosing an encoding for one column chunk - the first half of F10.
//
// docs/01-scope.md Phase 3 describes a BtrBlocks-style SAMPLING compressor: sample the data, try
// every candidate scheme the target edition allows, keep the smallest. This is not that yet. It is
// the rule-based chooser underneath it, measuring the two properties that decide almost every real
// column and costing one pass each:
//
//   * RUN COUNT. A column of long runs -- a sorted key, a status enum, a constant -- is run-end
//     encoded, and the constant case falls out for free as the single-run degenerate: a 65536-row
//     constant column becomes two elements, which is as good as vortex.constant would do.
//   * DISTINCT COUNT. A column of few distinct values -- a category, a repeated string -- is
//     dictionary encoded, which is where the win on text lives. The values child of both of these
//     is the ORIGINAL column gathered down to its first occurrences, which is exactly
//     CanonicalFilter.Apply -- so a dictionary of strings shares the data buffers it came from and
//     copies only 16-byte views.
//   * RANGE. A dense integer column -- an id, a measurement, a timestamp -- gets frame-of-reference
//     plus bit-packing: subtract the minimum, then store only the bits the span needs. This is the
//     scheme the reference's own ratios on numeric data come from.
//
// WHAT IS DELIBERATELY NOT HERE, so its absence is a decision rather than an oversight: ALP, FSST
// and OnPair, each of which is a whole algorithm rather than a kernel, and CASCADING -- dictionary
// codes that are themselves bit-packed, which is where the last of the reference's ratio lives.
//
// The thresholds are ratios, not sizes, and they are conservative on purpose. Compressing a column
// that barely benefits costs a second array, its metadata and a decode step on every read; the
// asymmetry favours leaving data alone.
using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>What the writer decided to do with one column chunk.</summary>
internal enum ColumnScheme : byte
{
    /// <summary>Write the canonical array as it is.</summary>
    None = 0,

    /// <summary>Run-end encode it: one entry per run.</summary>
    RunEnd = 1,

    /// <summary>Dictionary encode it: one code per row, one entry per distinct value.</summary>
    Dict = 2,

    /// <summary>Frame of reference, then bit-pack: the scheme dense integers want.</summary>
    BitPacked = 3,
}

/// <summary>The chosen scheme and the indices it needs.</summary>
internal readonly struct ColumnPlan
{
    private ColumnPlan(ColumnScheme scheme, int[] gather, int[] codes, ulong reference, int bitWidth)
    {
        Scheme = scheme;
        Gather = gather;
        Codes = codes;
        Reference = reference;
        BitWidth = bitWidth;
    }

    /// <summary>Leave the column alone.</summary>
    internal static ColumnPlan Canonical => new ColumnPlan(ColumnScheme.None, [], [], 0, 0);

    /// <summary>The frame of reference, as the column's own raw bits.</summary>
    internal ulong Reference { get; }

    /// <summary>Bits per packed value.</summary>
    internal int BitWidth { get; }

    /// <summary>What to do.</summary>
    internal ColumnScheme Scheme { get; }

    /// <summary>
    /// The rows to gather as the values child: one per run for <see cref="ColumnScheme.RunEnd"/>,
    /// one per distinct value for <see cref="ColumnScheme.Dict"/>.
    /// </summary>
    internal int[] Gather { get; }

    /// <summary>
    /// For <see cref="ColumnScheme.RunEnd"/>, each run's exclusive end row. For
    /// <see cref="ColumnScheme.Dict"/>, each row's dictionary entry.
    /// </summary>
    internal int[] Codes { get; }

    internal static ColumnPlan Runs(int[] starts, int[] ends) =>
        new ColumnPlan(ColumnScheme.RunEnd, starts, ends, 0, 0);

    internal static ColumnPlan Dictionary(int[] firstOccurrences, int[] codes) =>
        new ColumnPlan(ColumnScheme.Dict, firstOccurrences, codes, 0, 0);

    internal static ColumnPlan FrameOfReference(ulong reference, int bitWidth) =>
        new ColumnPlan(ColumnScheme.BitPacked, [], [], reference, bitWidth);
}

/// <summary>Decides how to encode one column chunk.</summary>
internal static class ColumnCompressor
{
    /// <summary>
    /// A run-end array must be this many times smaller than the column before it is worth the
    /// second array and the decode step.
    /// </summary>
    private const int RunEndRatio = 4;

    /// <summary>The same, for a dictionary. Higher, because the codes array is not free.</summary>
    private const int DictRatio = 4;

    /// <summary>Below this, the arrays are too small for any of it to matter.</summary>
    private const int MinimumRows = 64;

    /// <summary>Picks a scheme for the canonical node at <paramref name="nodeIndex"/>.</summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The column chunk.</param>
    /// <returns>The plan; <see cref="ColumnPlan.Canonical"/> when nothing wins.</returns>
    internal static ColumnPlan Choose(CanonicalArena arena, int nodeIndex)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int length = node.Length;
        if (length < MinimumRows || !IsComparable(node.Kind))
        {
            return ColumnPlan.Canonical;
        }

        RowComparer comparer = new RowComparer(arena, nodeIndex);

        // One pass for runs. It is the cheaper of the two and wins outright when it wins.
        List<int> runStarts = [];
        List<int> runEnds = [];
        runStarts.Add(0);
        for (int i = 1; i < length; i++)
        {
            if (!comparer.Equal(i - 1, i))
            {
                runEnds.Add(i);
                runStarts.Add(i);
            }
        }

        runEnds.Add(length);

        if (runStarts.Count * RunEndRatio <= length)
        {
            return ColumnPlan.Runs([.. runStarts], [.. runEnds]);
        }

        // Dense integers before dictionaries: a column of a million distinct measurements has no
        // dictionary worth building and a very good bit width.
        if (TryFrameOfReference(arena, node, out ColumnPlan packed))
        {
            return packed;
        }

        // A second pass for distinct values, over a column the runs did not capture. The run count
        // bounds the distinct count from above, so this only runs when the data is genuinely
        // interleaved rather than merely repetitive.
        return Dictionary(comparer, length);
    }

    /// <summary>
    /// Measures the column's range and decides whether bit-packing it pays.
    /// </summary>
    /// <remarks>
    /// The reference is the MINIMUM, so every encoded value is non-negative and the bit width is
    /// the span rather than the magnitude: a column of timestamps around 1.7e18 needs 64 bits as
    /// raw values and perhaps 20 as offsets from its own minimum. Null rows are encoded as the
    /// reference itself -- their value is never read back, and zero packs better than whatever
    /// happened to be in the buffer.
    /// </remarks>
    private static bool TryFrameOfReference(
        CanonicalArena arena, CanonicalNode node, out ColumnPlan plan)
    {
        plan = ColumnPlan.Canonical;
        if (node.Kind != CanonicalKind.Primitive || !node.PType.IsInteger())
        {
            return false;
        }

        PType ptype = node.PType;
        int elementBits = ptype.ByteWidth() * 8;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        ReadOnlySpan<byte> values = node.Values.Span;
        bool signed = ptype.IsSignedInteger();

        ulong span;
        ulong reference;
        if (signed)
        {
            long min = long.MaxValue;
            long max = long.MinValue;
            if (!RangeSigned(node.Length, mask, values, ptype, ref min, ref max))
            {
                return false;
            }

            // Unsigned subtraction of two's-complement bit patterns: max - min never overflows
            // here even when the two straddle zero, which a signed subtraction would.
            reference = unchecked((ulong)min);
            span = unchecked((ulong)max) - reference;
        }
        else
        {
            ulong min = ulong.MaxValue;
            ulong max = ulong.MinValue;
            if (!RangeUnsigned(node.Length, mask, values, ptype, ref min, ref max))
            {
                return false;
            }

            reference = min;
            span = max - min;
        }

        int bitWidth = span == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(span);

        // Half the width or better. Below that the second array, its metadata and a decode step on
        // every read are not bought back.
        if (bitWidth * 2 > elementBits)
        {
            return false;
        }

        plan = ColumnPlan.FrameOfReference(reference, bitWidth);
        return true;
    }

    private static bool RangeSigned(
        int length, ValidityMask mask, ReadOnlySpan<byte> values, PType ptype,
        ref long min, ref long max)
    {
        bool any = false;
        for (int i = 0; i < length; i++)
        {
            if (!mask.IsValid(i))
            {
                continue;
            }

            any = true;
            long value = CanonicalSupport.ReadInteger(values, ptype, i);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return any;
    }

    private static bool RangeUnsigned(
        int length, ValidityMask mask, ReadOnlySpan<byte> values, PType ptype,
        ref ulong min, ref ulong max)
    {
        bool any = false;
        for (int i = 0; i < length; i++)
        {
            if (!mask.IsValid(i))
            {
                continue;
            }

            any = true;
            ulong value = Arrays.Decoders.Compressed.CompressedValues.ReadUnsigned(values, ptype, i);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return any;
    }

    private static ColumnPlan Dictionary(in RowComparer comparer, int length)
    {
        Dictionary<int, List<int>> byHash = new Dictionary<int, List<int>>();
        List<int> firstOccurrences = [];
        int[] codes = new int[length];

        for (int row = 0; row < length; row++)
        {
            int hash = comparer.Hash(row);
            if (!byHash.TryGetValue(hash, out List<int>? candidates))
            {
                candidates = [];
                byHash.Add(hash, candidates);
            }

            int code = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (comparer.Equal(firstOccurrences[candidates[i]], row))
                {
                    code = candidates[i];
                    break;
                }
            }

            if (code < 0)
            {
                code = firstOccurrences.Count;
                firstOccurrences.Add(row);
                candidates.Add(code);

                // Give up as soon as the dictionary is too large to pay for itself, rather than
                // building the whole thing and then discarding it.
                if (firstOccurrences.Count * DictRatio > length)
                {
                    return ColumnPlan.Canonical;
                }
            }

            codes[row] = code;
        }

        return ColumnPlan.Dictionary([.. firstOccurrences], codes);
    }

    /// <summary>Whether a canonical form has a row equality this compressor can compute.</summary>
    private static bool IsComparable(CanonicalKind kind) =>
        kind is CanonicalKind.Primitive or CanonicalKind.Bool or CanonicalKind.VarBinView
            or CanonicalKind.Decimal;

    /// <summary>
    /// Row equality and hashing over one canonical column.
    /// </summary>
    /// <remarks>
    /// NULLNESS IS PART OF THE VALUE. Two null rows are equal and a null is equal to nothing else,
    /// which is what makes a run of nulls one run and a dictionary hold at most one null entry. It
    /// is NOT the filter's three-valued logic -- that answers "does this row match a predicate" and
    /// this answers "are these two rows the same value", and conflating them would make a run of
    /// nulls unrepresentable.
    ///
    /// Floats are compared by their RAW BITS for the same reason the sidecar does: -0.0 and +0.0
    /// are different values to a writer even though they compare equal, and collapsing them into
    /// one dictionary entry would change the data.
    /// </remarks>
    private readonly ref struct RowComparer
    {
        private readonly CanonicalArena _arena;
        private readonly CanonicalNode _node;
        private readonly ValidityMask _mask;

        internal RowComparer(CanonicalArena arena, int nodeIndex)
        {
            _arena = arena;
            _node = arena.GetNode(nodeIndex);
            _mask = ValidityMask.From(arena, _node.Validity);
        }

        internal bool Equal(int a, int b)
        {
            bool validA = _mask.IsValid(a);
            if (validA != _mask.IsValid(b))
            {
                return false;
            }

            if (!validA)
            {
                return true;
            }

            return _node.Kind switch
            {
                CanonicalKind.Bool =>
                    CanonicalSupport.BitAt(_node.Bits.Span, _node.BitOffset + a) ==
                    CanonicalSupport.BitAt(_node.Bits.Span, _node.BitOffset + b),
                CanonicalKind.VarBinView => Bytes(a).SequenceEqual(Bytes(b)),
                CanonicalKind.Decimal => Fixed(a, DecimalWidth).SequenceEqual(Fixed(b, DecimalWidth)),
                _ => Fixed(a, _node.PType.ByteWidth()).SequenceEqual(Fixed(b, _node.PType.ByteWidth())),
            };
        }

        internal int Hash(int row)
        {
            if (!_mask.IsValid(row))
            {
                return 0;
            }

            return _node.Kind switch
            {
                CanonicalKind.Bool =>
                    CanonicalSupport.BitAt(_node.Bits.Span, _node.BitOffset + row) ? 1 : 2,
                CanonicalKind.VarBinView => Hash(Bytes(row)),
                CanonicalKind.Decimal => Hash(Fixed(row, DecimalWidth)),
                _ => Hash(Fixed(row, _node.PType.ByteWidth())),
            };
        }

        private int DecimalWidth => Types.Numerics.DecimalStorage.ByteWidth(_node.Storage);

        private ReadOnlySpan<byte> Fixed(int row, int width) =>
            _node.Values.Span.Slice(row * width, width);

        private ReadOnlySpan<byte> Bytes(int row)
        {
            ReadOnlySpan<byte> view = _node.Views.Span.Slice(row * 16, 16);
            int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(view);
            if (size <= 12)
            {
                return view.Slice(4, size);
            }

            int buffer = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
            int offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
            return _node.GetDataBuffer(buffer).Span.Slice(offset, size);
        }

        /// <summary>FNV-1a. A hash, not a checksum: collisions are resolved by comparing.</summary>
        private static int Hash(ReadOnlySpan<byte> bytes)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash = (hash ^ bytes[i]) * 16777619u;
            }

            return (int)hash;
        }
    }
}
