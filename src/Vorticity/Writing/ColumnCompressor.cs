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
//   * RANGE. A dense integer column -- an id, a measurement, a timestamp -- gets bit-packed, under
//     whichever of frame-of-reference and zigzag costs less, with the values that do not fit
//     carried as patches. That decision is BitPackPlan's, because it is a minimization rather than
//     a rule; this is the pass that asks for it.
//
// WHAT IS DELIBERATELY NOT HERE, so its absence is a decision rather than an oversight: OnPair,
// which is a whole algorithm rather than a kernel, and CASCADING -- dictionary codes that are
// themselves bit-packed, which is where the last of the reference's ratio lives.
//
// The thresholds are ratios, not sizes, and they are conservative on purpose. Compressing a column
// that barely benefits costs a second array, its metadata and a decode step on every read; the
// asymmetry favours leaving data alone.
using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Editions;
using Vorticity.Types;
using Vorticity.Types.Numerics;

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

    /// <summary>
    /// FSST: the scheme high-cardinality text wants, and the one the reference uses where every
    /// other rule here falls through.
    /// </summary>
    Fsst = 4,

    /// <summary>ALP: the scheme decimal-shaped floats want.</summary>
    Alp = 5,

    /// <summary>Frame of reference, then bit-pack: the scheme dense integers want.</summary>
    BitPacked = 3,
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

    /// <summary>
    /// The compressed column, for <see cref="ColumnScheme.Fsst"/>. Carried on the plan rather than
    /// rebuilt by the writer because deciding that FSST pays already required producing it.
    /// </summary>
    internal FsstPlan? Fsst { get; private init; }

    /// <summary>The encoded column, for <see cref="ColumnScheme.Alp"/>.</summary>
    internal AlpPlan? Alp { get; private init; }

    /// <summary>
    /// The transform, the width and the exceptions, for <see cref="ColumnScheme.BitPacked"/>.
    /// </summary>
    internal BitPackPlan? BitPack { get; private init; }

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

    internal static ColumnPlan ForFsst(FsstPlan plan) =>
        new ColumnPlan(ColumnScheme.Fsst, [], []) { Fsst = plan };

    internal static ColumnPlan ForAlp(AlpPlan plan) =>
        new ColumnPlan(ColumnScheme.Alp, [], []) { Alp = plan };

    internal static ColumnPlan ForBitPacking(BitPackPlan plan) =>
        new ColumnPlan(ColumnScheme.BitPacked, [], []) { BitPack = plan };
}

/// <summary>Decides how to encode one column chunk.</summary>
internal static class ColumnCompressor
{
    /// <summary>
    /// A run-end array must be this many times smaller than the column before it is worth the
    /// second array and the decode step.
    /// </summary>
    private const int RunEndRatio = 4;

    /// <summary>
    /// What a dictionary costs beyond its codes and entries: two more array nodes and their
    /// metadata. Deliberately generous - the point is to refuse encodings that barely pay, not to
    /// squeeze the last byte. <see cref="BitPackPlan"/> carries its own for the same reason.
    /// </summary>
    private const int DictionaryOverhead = 512;

    /// <summary>Below this many rows, a column is a candidate only if it is big in BYTES.</summary>
    private const int MinimumRows = 64;

    /// <summary>
    /// ...and this is what "big in bytes" means. A row count alone is the wrong guard: what the
    /// guard is actually for is the fixed cost of a second array and its metadata, a couple of
    /// hundred bytes, so the threshold belongs on the quantity that cost is compared against. A
    /// sixteen-row column holding a megabyte of strings is worth a dictionary; a sixteen-row column
    /// of integers is not, and neither reads as "sixteen".
    /// </summary>
    /// <remarks>
    /// Honesty about what this bought: NOTHING on the current corpus, whose one small-row-big-bytes
    /// file (`distributions/huge_string_r16`, sixteen rows around a string over a mebibyte) has
    /// sixteen distinct values and so defeats runs and dictionaries alike - the reference gets its
    /// 268x there with `vortex.onpair`, a string compressor we do not write. The rule is still the
    /// right rule, and `SmallRowCountLargeBytesColumnIsStillCompressed` pins it, but it is not the
    /// fix for that file.
    /// </remarks>
    private const long MinimumBytes = 1024;

    /// <summary>Picks a scheme for the canonical node at <paramref name="nodeIndex"/>.</summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The column chunk.</param>
    /// <param name="target">
    /// The edition being written. The candidate list is derived from it BEFORE anything is
    /// measured, which is what upstream does (`retain_allowed_encodings` on the BtrBlocks builder)
    /// and the only arrangement that works: otherwise a scheme is elected on suitable data and the
    /// write then fails at serialization because the target does not contain its id. Failing the
    /// write is the right last-resort assertion; it must never be the nominal path.
    /// </param>
    /// <returns>The plan; <see cref="ColumnPlan.Canonical"/> when nothing wins.</returns>
    internal static ColumnPlan Choose(
        CanonicalArena arena, int nodeIndex, VortexEdition target = EditionRegistry.Newest)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int length = node.Length;
        if (!IsComparable(node.Kind) || (length < MinimumRows && DataBytes(node) < MinimumBytes))
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

        if (runStarts.Count * RunEndRatio <= length && Allows(target, "vortex.runend"))
        {
            return ColumnPlan.Runs([.. runStarts], [.. runEnds]);
        }

        // Dense integers: a column of a million distinct measurements has no dictionary worth
        // building and a very good bit width.
        BitPackPlan? packed = Allows(target, "fastlanes.bitpacked")
            ? BitPackPlan.TryBuild(arena, node, zigzag: Allows(target, "vortex.zigzag"))
            : null;
        if (packed is not null && packed.Transform == BitPackTransform.Frame
            && !Allows(target, "fastlanes.for"))
        {
            packed = null;
        }

        // ...but "has a good bit width" is not "is the best scheme", and the two are COMPARED
        // rather than ordered. This used to return the bit-packed plan the moment it beat
        // canonical, which was harmless while bit-packing declined often, and became a 37 kB
        // regression on `containers/zoned_many_zones_nulls` the moment patches let it apply to a
        // nullable integer column a dictionary was already handling better. Both are priced in
        // bytes; whichever is cheaper wins.
        long plain = node.Kind == CanonicalKind.VarBinView
            ? PlainBinarySize(arena, node)
            : DataBytes(node);

        // A second pass for distinct values, over a column the runs did not capture. The run count
        // bounds the distinct count from above, so this only runs when the data is genuinely
        // interleaved rather than merely repetitive -- and the budget is what the BEST plan so far
        // costs, so a dictionary that cannot beat the bit-packing abandons that much sooner.
        long budget = packed is null ? plain : Math.Min(plain, packed.Cost);
        ColumnPlan dictionary = Allows(target, "vortex.dict")
            ? Dictionary(arena, node, comparer, length, budget)
            : ColumnPlan.Canonical;
        if (dictionary.Scheme != ColumnScheme.None)
        {
            return dictionary;
        }

        if (packed is not null)
        {
            return ColumnPlan.ForBitPacking(packed);
        }

        // LAST, and only for text: a high-cardinality string column defeats runs, defeats
        // dictionaries and has no frame of reference, which is precisely the case the reference
        // hands to FSST and we used to write out canonically. Tried here rather than earlier
        // because a dictionary is cheaper to decode when it applies.
        // Floats get their own scheme, for the same reason integers get frame of reference: a
        // column of prices or coordinates is a column of short decimals, and the integer they
        // scale to bit-packs where the double never could.
        if (node.Kind == CanonicalKind.Primitive && node.PType.IsFloat() && Allows(target, "vortex.alp"))
        {
            AlpPlan? alp = AlpPlan.TryBuild(arena, nodeIndex, plain);
            if (alp is not null)
            {
                return ColumnPlan.ForAlp(alp);
            }
        }

        if (node.Kind == CanonicalKind.VarBinView && Allows(target, "vortex.fsst"))
        {
            // Measured against the BEST plain form, not against the view form. A binary column of
            // incompressible bytes is smaller as `vortex.varbin` - four-byte offsets rather than
            // sixteen-byte views - than as anything FSST can do with it, and comparing against the
            // view form made FSST look like a win on exactly those columns. It is the reference's
            // own choice there, and it was ours only after this baseline was fixed.
            FsstPlan? fsst = FsstPlan.TryBuild(arena, nodeIndex, plain);
            if (fsst is not null)
            {
                return ColumnPlan.ForFsst(fsst);
            }
        }

        return ColumnPlan.Canonical;
    }

    /// <summary>
    /// Builds a dictionary and keeps it only when it is SMALLER, in bytes, than the column.
    /// </summary>
    /// <remarks>
    /// The rule used to be a count ratio: at most one distinct value per four rows. Like the
    /// frame-of-reference fraction it replaced, it had never been measured against what it stands
    /// for. `types/binary_nonnull_r8193` has 1153 distinct values in 8193 rows and the reference
    /// dictionary-encodes it into a quarter of our size; per chunk that is about one in 3.5, so the
    /// ratio refused it by a hair while the byte arithmetic says it wins by 100 kB.
    ///
    /// The BUDGET is the cheapest plan found so far rather than the plain column, so this both
    /// decides and abandons against the real competition.
    ///
    /// The abandonment guard stays, in a form that still bounds the work: a dictionary whose
    /// ENTRIES alone already cost more than the whole column can never win, whatever the rest of
    /// the rows hold.
    /// </remarks>
    private static ColumnPlan Dictionary(
        CanonicalArena arena, CanonicalNode node, in RowComparer comparer, int length, long budget)
    {
        Dictionary<int, List<int>> byHash = new Dictionary<int, List<int>>();
        List<int> firstOccurrences = [];
        int[] codes = new int[length];
        int minimumEntry = node.Kind switch
        {
            CanonicalKind.Primitive => node.PType.ByteWidth(),
            CanonicalKind.Decimal => DecimalStorage.ByteWidth(node.Storage),
            CanonicalKind.VarBinView => 1,
            _ => 1,
        };

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

                // Give up as soon as the dictionary provably cannot win, rather than building one
                // and then discarding it: every row needs at least a one-byte code, and every
                // entry costs at least `minimumEntry` bytes however it is written.
                if (length + ((long)firstOccurrences.Count * minimumEntry) >= budget)
                {
                    return ColumnPlan.Canonical;
                }
            }

            codes[row] = code;
        }

        // Priced, not assumed: codes at the narrowest width that indexes the entries, plus the
        // entries themselves in whatever form they will actually be written.
        long encoded = ((long)length * FsstPlan.IndexPType(firstOccurrences.Count).ByteWidth())
            + EntriesSize(arena, node, firstOccurrences);
        return encoded + DictionaryOverhead < budget
            ? ColumnPlan.Dictionary([.. firstOccurrences], codes)
            : ColumnPlan.Canonical;
    }

    /// <summary>What the gathered dictionary entries will occupy once written.</summary>
    private static long EntriesSize(CanonicalArena arena, CanonicalNode node, List<int> rows)
    {
        switch (node.Kind)
        {
            case CanonicalKind.Bool:
                return (rows.Count + 7) / 8;

            case CanonicalKind.Primitive:
                return (long)rows.Count * node.PType.ByteWidth();

            case CanonicalKind.Decimal:
                return (long)rows.Count * DecimalStorage.ByteWidth(node.Storage);

            default:
            {
                // The entries are written as varbin or varbinview, whichever is smaller, exactly
                // as any other binary column would be.
                ValidityMask mask = ValidityMask.From(arena, node.Validity);
                ReadOnlySpan<byte> views = node.Views.Span;
                long heap = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (mask.IsValid(rows[i]))
                    {
                        heap += System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                            views.Slice(rows[i] * 16, 4));
                    }
                }

                long varbin = (((long)rows.Count + 1) * FsstPlan.IndexPType(heap).ByteWidth()) + heap;
                return Math.Min(((long)rows.Count * 16) + heap, varbin);
            }
        }
    }

    /// <summary>
    /// How many bytes of data the column occupies, for the small-column guard.
    /// </summary>
    /// <remarks>
    /// Only the four comparable forms need an answer, and each of them owns its bytes directly -
    /// no recursion, and no counting of a validity child, whose size is a rounding error next to
    /// the values it qualifies.
    /// </remarks>
    private static long DataBytes(CanonicalNode node)
    {
        switch (node.Kind)
        {
            case CanonicalKind.Bool:
                return node.Bits.Length;

            case CanonicalKind.Primitive:
            case CanonicalKind.Decimal:
                return node.Values.Length;

            default:
            {
                long total = node.Views.Length;
                for (int i = 0; i < node.DataBufferCount; i++)
                {
                    total += node.GetDataBuffer(i).Length;
                }

                return total;
            }
        }
    }

    /// <summary>
    /// The smaller of the two plain serializations of a binary column: <c>vortex.varbinview</c>
    /// (16 bytes of view per row plus its data buffers) and <c>vortex.varbin</c> (one offset per
    /// row plus a contiguous heap).
    /// </summary>
    private static long PlainBinarySize(CanonicalArena arena, CanonicalNode node)
    {
        long viewForm = DataBytes(node);

        long heap = 0;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        ReadOnlySpan<byte> views = node.Views.Span;
        for (int i = 0; i < node.Length; i++)
        {
            if (mask.IsValid(i))
            {
                heap += System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    views.Slice(i * 16, 4));
            }
        }

        long varbinForm = (((long)node.Length + 1) * FsstPlan.IndexPType(heap).ByteWidth()) + heap;
        return Math.Min(viewForm, varbinForm);
    }

    /// <summary>Whether the target edition carries the array id a scheme would emit.</summary>
    private static bool Allows(VortexEdition target, string id) =>
        EditionRegistry.Contains(target, ComponentKind.Array, id);

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
