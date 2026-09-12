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
//     dictionary encoded, which is where the win on text lives.
//
// Neither needs a new kernel. The values child of both schemes is the ORIGINAL column gathered down
// to its first occurrences, which is exactly CanonicalFilter.Apply -- so a dictionary of strings
// shares the data buffers it came from and copies only 16-byte views.
//
// WHAT IS DELIBERATELY NOT HERE, so its absence is a decision rather than an oversight: FoR and
// bit-packing, which is the scheme that wins on dense integers and the one that needs the FastLanes
// transposed writer. Until that exists this compressor leaves such a column canonical, which is
// correct and bigger than it should be.
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
}

/// <summary>The chosen scheme and the indices it needs.</summary>
internal readonly struct ColumnPlan
{
    private ColumnPlan(ColumnScheme scheme, int[] gather, int[] codes)
    {
        Scheme = scheme;
        Gather = gather;
        Codes = codes;
    }

    /// <summary>Leave the column alone.</summary>
    internal static ColumnPlan Canonical => new ColumnPlan(ColumnScheme.None, [], []);

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
        new ColumnPlan(ColumnScheme.RunEnd, starts, ends);

    internal static ColumnPlan Dictionary(int[] firstOccurrences, int[] codes) =>
        new ColumnPlan(ColumnScheme.Dict, firstOccurrences, codes);
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

        // A second pass for distinct values, over a column the runs did not capture. The run count
        // bounds the distinct count from above, so this only runs when the data is genuinely
        // interleaved rather than merely repetitive.
        return Dictionary(comparer, length);
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
