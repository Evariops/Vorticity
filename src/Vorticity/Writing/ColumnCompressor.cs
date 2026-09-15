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
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// An arithmetic progression, in the metadata: the only scheme that costs nothing per row.
    /// </summary>
    Sequence = 6,

    /// <summary>Frame of reference, then bit-pack: the scheme dense integers want.</summary>
    BitPacked = 3,

    /// <summary>
    /// Zstd: general-purpose compression, for text no symbol table can capture.
    /// </summary>
    /// <remarks>
    /// 7 because 6 is taken. The members of this enum are NOT in declaration order - `BitPacked` is
    /// 3 and sits last - so appending a member and giving it the next number after its neighbour's
    /// silently aliases `Sequence`, and every sequence column then dispatches to the zstd writer and
    /// dereferences a null plan. Read the numbers, not the order.
    /// </remarks>
    Zstd = 7,
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

    /// <summary>The zstd frame, when <see cref="ColumnScheme.Zstd"/>.</summary>
    internal ZstdPlan? Zstd { get; init; }

    /// <summary>The encoded column, for <see cref="ColumnScheme.Alp"/>.</summary>
    internal AlpPlan? Alp { get; private init; }

    /// <summary>The base and step, for <see cref="ColumnScheme.Sequence"/>.</summary>
    internal SequencePlan? Sequence { get; private init; }

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

    internal static ColumnPlan ForZstd(ZstdPlan plan) =>
        new ColumnPlan(ColumnScheme.Zstd, [], []) { Zstd = plan };

    internal static ColumnPlan ForAlp(AlpPlan plan) =>
        new ColumnPlan(ColumnScheme.Alp, [], []) { Alp = plan };

    internal static ColumnPlan ForBitPacking(BitPackPlan plan) =>
        new ColumnPlan(ColumnScheme.BitPacked, [], []) { BitPack = plan };

    internal static ColumnPlan ForSequence(SequencePlan plan) =>
        new ColumnPlan(ColumnScheme.Sequence, [], []) { Sequence = plan };
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
    /// The run-end plan for this column, or <see cref="ColumnPlan.Canonical"/> when runs do not pay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WRITE-AUDIT.md W-33. The pass answers ONE question -- are there at most
    /// <c>length / RunEndRatio</c> runs -- and the answer is settled the moment the count passes
    /// that. Running on to the last row afterwards was the whole of the scan on an interleaved
    /// column, which PERF-AUDIT-v2.md W-7a already names as the common case.
    /// </para>
    /// <para>
    /// A BITMAP ANSWERS IT SIXTY-FOUR ROWS AT A TIME. <c>w ^ ((w &lt;&lt; 1) | previous)</c> has a
    /// set bit exactly where a row differs from the one before it, so a whole word's boundaries are
    /// one xor and a popcount, and the positions come out of the word by trailing-zero count --
    /// which only runs as often as there are boundaries. Measured on the 1M `bool` file, the pass
    /// was **88,9 % of the write** through <c>RowComparer.Equal</c>, two <c>BitAt</c> calls a row.
    /// </para>
    /// <para>
    /// ALL-VALID ONLY, for the bitmap path: a null row is equal to another null row and unequal to a
    /// valid one whatever the bits say, and folding the validity mask into the word walk would cost
    /// a second bitmap and its own offset. The general path below is correct for those.
    /// </para>
    /// </remarks>
    private static ColumnPlan TryRuns(
        CanonicalArena arena, in CanonicalNode node, in RowComparer comparer, int length)
    {
        int ceiling = length / RunEndRatio;
        if (ceiling < 1)
        {
            return ColumnPlan.Canonical;
        }

        int[] runStarts = ArrayPool<int>.Shared.Rent(Math.Max(length, 1));
        int[] runEnds = ArrayPool<int>.Shared.Rent(Math.Max(length, 1));
        try
        {
            int runCount = node.Kind == CanonicalKind.Bool && node.Validity.IsAllValid
                ? BitmapRuns(node.Bits.Span, node.BitOffset, length, ceiling, runStarts, runEnds)
                : ComparedRuns(in comparer, length, ceiling, runStarts, runEnds);

            if (runCount > ceiling)
            {
                return ColumnPlan.Canonical;
            }

            runEnds[runCount - 1] = length;
            return ColumnPlan.Runs(
                runStarts.AsSpan(0, runCount).ToArray(), runEnds.AsSpan(0, runCount).ToArray());
        }
        finally
        {
            ArrayPool<int>.Shared.Return(runStarts);
            ArrayPool<int>.Shared.Return(runEnds);
        }
    }

    /// <summary>Runs by comparing adjacent rows, abandoning once run-end can no longer win.</summary>
    /// <returns>The run count, or a value above <paramref name="ceiling"/> when it gave up.</returns>
    private static int ComparedRuns(
        in RowComparer comparer, int length, int ceiling, Span<int> starts, Span<int> ends)
    {
        int runCount = 1;
        starts[0] = 0;
        for (int i = 1; i < length; i++)
        {
            if (comparer.Equal(i - 1, i))
            {
                continue;
            }

            if (runCount > ceiling)
            {
                return runCount;
            }

            ends[runCount - 1] = i;
            starts[runCount] = i;
            runCount++;
        }

        return runCount;
    }

    /// <summary>The same count over an all-valid bitmap, a word at a time.</summary>
    private static int BitmapRuns(
        ReadOnlySpan<byte> bits, int bitOffset, int length, int ceiling, Span<int> starts, Span<int> ends)
    {
        int runCount = 1;
        starts[0] = 0;

        // The window slides by whole words, so the shift inside the byte is the same every step and
        // is hoisted out of the loop with the base.
        int shift = bitOffset & 7;
        int firstByte = bitOffset >> 3;
        ulong previous = 0;

        for (int row = 0; row < length; row += 64)
        {
            int take = Math.Min(64, length - row);
            ulong word = BitWords.Load(bits, firstByte + (row >> 3), shift) & BitWords.Mask(take);

            // ROW 0 HAS NO PREDECESSOR, and seeding `previous` with a zero said otherwise: a column
            // whose first row is `true` was charged a boundary at row 0 and got an EMPTY leading
            // run. It decoded correctly -- `starts[0]` and `starts[1]` were both 0, so the run's
            // value was right and only its extent was empty -- which is why nothing caught it; it
            // cost one run entry and made the count disagree with the ingest pass's, which is exact.
            if (row == 0)
            {
                previous = word & 1;
            }

            // Bit j is set where row j differs from row j-1, the first taking its predecessor from
            // the previous word.
            ulong diff = (word ^ ((word << 1) | previous)) & BitWords.Mask(take);
            previous = (word >> (take - 1)) & 1;

            while (diff != 0)
            {
                if (runCount > ceiling)
                {
                    return runCount;
                }

                int at = row + System.Numerics.BitOperations.TrailingZeroCount(diff);
                ends[runCount - 1] = at;
                starts[runCount] = at;
                runCount++;
                diff &= diff - 1;
            }
        }

        return runCount;
    }

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

    /// <summary>
    /// Column bytes below which zstd is not even priced.
    /// </summary>
    /// <remarks>
    /// A whole zstd pass over a column to discover it loses is the cost §9 caught FSST paying on
    /// every chunk, so a gate there must be. 16 kB rather than the 64 kB this started at, because
    /// the f32 columns that need it most are 32 kB and the 64 kB gate declined them outright -
    /// a threshold picked from one file's shape will exclude the next file's.
    /// </remarks>
    private const long ZstdMinimumBytes = 16 * 1024;

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
    /// <param name="stats">
    /// What the ingest pass already measured over exactly these rows
    /// (docs/11-write-strategy.md §8 stage 2). A candidate that can be priced from them is priced
    /// from them and runs no pass of its own.
    /// <para>
    /// IT IS CHECKED AGAINST THE NODE rather than trusted. A summary whose row count disagrees with
    /// the column is not this column's, and the only honest answer is to measure — which is what
    /// every candidate did before this parameter existed, so a plumbing slip costs a pass and can
    /// never produce a wrong plan. A child of a cascade passes none, and takes that path.
    /// </para>
    /// </param>
    /// <returns>The plan; <see cref="ColumnPlan.Canonical"/> when nothing wins.</returns>
    internal static ColumnPlan Choose(
        CanonicalArena arena, int nodeIndex, VortexEdition target = EditionRegistry.Newest,
        in BlockStats stats = default)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int length = node.Length;
        bool measured = stats.IsPresent && stats.Rows == length;
        if (!IsComparable(node.Kind) || (length < MinimumRows && DataBytes(node) < MinimumBytes))
        {
            return ColumnPlan.Canonical;
        }

        // FIRST, because where it applies nothing else can beat it: an arithmetic progression goes
        // entirely into the metadata, so a `vortex.primitive` node and its whole buffer become one
        // node and about thirty bytes. There is no byte comparison to make - every other scheme
        // costs something per row and this one costs nothing.
        if (Allows(target, "vortex.sequence"))
        {
            SequencePlan? sequence = SequencePlan.TryBuild(arena, node);
            if (sequence is not null)
            {
                return ColumnPlan.ForSequence(sequence);
            }
        }

        RowComparer comparer = new RowComparer(arena, nodeIndex);

        // One pass for runs. It is the cheaper of the two and wins outright when it wins.
        //
        // RENTED AND SIZED FOR THE WORST CASE, which is one run per row. PERF-AUDIT-v2.md W-7a: two
        // `List<int>` growing by doubling is cheapest exactly when run-end WINS -- few runs, few
        // reallocations -- and most expensive when it loses, because a column with no runs at all
        // makes both lists grow to one entry per row and then throws them away. A column of
        // distinct measurements is the common case, so the scan paid its worst price on almost
        // every column: **57,9 % of `table_mixed`'s whole write**, 40,2 % of
        // `zoned_many_zones_nulls`'s, 34,4 % of `fsst`'s.
        //
        // The `finally` covers the scan and the verdict and nothing after: the arrays are dead the
        // moment the plan has copied the prefixes it keeps, and the schemes weighed below never see
        // them.
        //
        // IT DOES NOT RUN WHEN ITS ANSWER CANNOT BE USED, and it stops as soon as the answer is
        // settled -- WRITE-AUDIT.md W-33. `Allows` was tested AFTER the pass, so an edition without
        // `vortex.runend` paid for a count it then threw away; and the pass ran to the last row even
        // once the run count had passed the point where run-end can win, which on an interleaved
        // column is most of it.
        //
        // AND SINCE STAGE 2b IT DOES NOT RUN AT ALL WHEN THE ANSWER IS ALREADY KNOWN. The ingest
        // pass counts run boundaries as it reads the rows for the zone map, so the verdict is one
        // comparison. What that saves is not only the quarter of a pass W-33 left -- the scan RENTS
        // TWO `int[length]` ARRAYS before it starts, on every comparable column of every chunk,
        // and throws them away whenever run-end loses, which is most of the time. They are now
        // rented only when the plan is going to be kept.
        if (Allows(target, "vortex.runend"))
        {
            bool known = measured && stats.HasRunBoundaries;

            // ONE RUN NEEDS NO SCAN AT ALL, and this is the 62 % WRITE-ARCHITECTURE.md §1 charges
            // `variant` for "discovering a constant row by row" -- plus the 56 % it charges
            // `masked_all_invalid` for "discovering all-null bit by bit". A column whose every row
            // is equal has exactly one run, its boundaries are [0, length), and the scan that
            // produced that answer read every row to do it. The count already says so.
            if (known && stats.RunCount == 1 && length / RunEndRatio >= 1)
            {
                return ColumnPlan.Runs([0], [length]);
            }

            if (!known || stats.RunCount <= length / RunEndRatio)
            {
                ColumnPlan runs = TryRuns(arena, in node, in comparer, length);
                if (runs.Scheme != ColumnScheme.None)
                {
                    return runs;
                }
            }
        }

        // Dense integers: a column of a million distinct measurements has no dictionary worth
        // building and a very good bit width.
        // THE FRAME OF REFERENCE IS THE COLUMN'S MINIMUM, which the ingest pass already has: a whole
        // pass over every row of every integer column, deleted. An all-null column is decided here
        // too -- `BitPackPlan` has nothing to measure a width against and says so -- so that case
        // does not walk the rows to rediscover it either.
        bool integers = node.Kind == CanonicalKind.Primitive && node.PType.IsInteger();
        BitPackPlan? packed = null;
        if (Allows(target, "fastlanes.bitpacked") && !(measured && integers && !stats.HasBounds))
        {
            packed = BitPackPlan.TryBuild(
                arena, node, zigzag: Allows(target, "vortex.zigzag"),
                reference: measured && integers ? Reference(node, in stats) : null);
        }

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
            ? PlainBinarySize(arena, node, measured ? stats.TotalBytes : -1)
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

        // ZSTD IS COMPARED WITH FSST, NOT REACHED WHEN FSST FAILS, and the difference is the whole
        // point. The first version of this ran zstd only after every other scheme had declined,
        // which is cheap and useless: on `distributions/huge_string_r16` FSST wins - it turns 1.1 MB
        // into 139 kB - so zstd was never tried, while zstd turns the same bytes into 118. A scheme
        // that wins is not a scheme that wins by enough.
        //
        // THE CHEAP CANDIDATE IS PRICED FIRST, and that ordering is an optimization with no effect
        // on the outcome. Pricing zstd is one pass to build the value stream and one call into the
        // library; pricing FSST is a symbol-table training run plus a compression of the whole
        // column, and it was 54% of the write profile on a file where zstd then won and the entire
        // result was discarded. Priced this way round, FSST is handed the size it has to beat and
        // stops as soon as its code stream passes it.
        //
        // What keeps it affordable in the other direction is the SIZE GATE: columns below it are
        // exactly the ones where the absolute saving cannot repay either pass.
        ZstdPlan? zstd = null;
        if (node.Kind is CanonicalKind.VarBinView or CanonicalKind.Primitive
            && Allows(target, "vortex.zstd")
            && DataBytes(node) >= ZstdMinimumBytes)
        {
            zstd = ZstdPlan.TryBuild(arena, nodeIndex, plain);
        }

        FsstPlan? fsst = null;
        if (node.Kind == CanonicalKind.VarBinView && Allows(target, "vortex.fsst"))
        {
            // TWO BARS, AND FSST HAS TO CLEAR BOTH, so the ceiling handed down is the tighter one.
            //
            //   * its own margin against the plain form:  encoded * 10 <= plain * 9. Measured
            //     against the BEST plain form, not against the view form. A binary column of
            //     incompressible bytes is smaller as `vortex.varbin` - four-byte offsets rather
            //     than sixteen-byte views - than as anything FSST can do with it, and comparing
            //     against the view form made FSST look like a win on exactly those columns. It is
            //     the reference's own choice there, and it was ours only after this baseline was
            //     fixed.
            //   * beating a zstd frame that priced: zstd takes the column when
            //     zstdBytes * 10 < encoded * 9, so FSST keeps it only while encoded * 9 <= zstdBytes * 10.
            //
            // Both are integer comparisons and both are turned into a ceiling on `encoded` by
            // flooring, which is exact because `encoded` is an integer. The two together are
            // EXACTLY the condition under which FSST was returned when it was priced first.
            long ceiling = plain * 9 / 10;
            if (zstd is not null)
            {
                ceiling = Math.Min(ceiling, zstd.FrameLength * 10L / 9);
            }

            fsst = FsstPlan.TryBuild(arena, nodeIndex, ceiling);
        }

        if (fsst is not null)
        {
            // The zstd plan lost, and it is holding a pooled buffer that nothing will write.
            zstd?.Release();
            return ColumnPlan.ForFsst(fsst);
        }

        if (zstd is not null)
        {
            return ColumnPlan.ForZstd(zstd);
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
        int minimumEntry = node.Kind switch
        {
            CanonicalKind.Primitive => node.PType.ByteWidth(),
            CanonicalKind.Decimal => DecimalStorage.ByteWidth(node.Storage),
            CanonicalKind.VarBinView => 1,
            _ => 1,
        };

        // A CHAINED HASH IN THREE FLAT ARRAYS, not a `Dictionary<int, List<int>>`. The dictionary
        // form allocated one `List<int>` per distinct HASH -- 1153 of them on a single 8193-row
        // column of `types/binary_nonnull_r8193` -- plus the dictionary's own rehashing, and all of
        // it is thrown away the moment the plan is abandoned. Here `buckets[h]` is the newest code
        // with that hash and `chain[c]` the one before it, which is the same collision list with no
        // object per bucket. All three are rented, so a column that abandons allocates NOTHING.
        //
        // `codes` is rented for the same reason: it was `new int[length]` written before the
        // abandonment test could fire, so every column that considered a dictionary and refused one
        // allocated a full row vector to throw away.
        int capacity = BucketCount(length);
        int[] buckets = ArrayPool<int>.Shared.Rent(capacity);
        int[] chain = ArrayPool<int>.Shared.Rent(Math.Max(length, 1));
        int[] firstRows = ArrayPool<int>.Shared.Rent(Math.Max(length, 1));
        int[] codes = ArrayPool<int>.Shared.Rent(Math.Max(length, 1));
        try
        {
            buckets.AsSpan(0, capacity).Fill(-1);
            int mask = capacity - 1;
            int distinct = 0;

            for (int row = 0; row < length; row++)
            {
                int bucket = comparer.Hash(row) & mask;
                int code = -1;
                for (int candidate = buckets[bucket]; candidate >= 0; candidate = chain[candidate])
                {
                    if (comparer.Equal(firstRows[candidate], row))
                    {
                        code = candidate;
                        break;
                    }
                }

                if (code < 0)
                {
                    code = distinct++;
                    firstRows[code] = row;
                    chain[code] = buckets[bucket];
                    buckets[bucket] = code;

                    // Give up as soon as the dictionary provably cannot win, rather than building
                    // one and then discarding it: every row needs at least a one-byte code, and
                    // every entry costs at least `minimumEntry` bytes however it is written.
                    if (length + ((long)distinct * minimumEntry) >= budget)
                    {
                        return ColumnPlan.Canonical;
                    }
                }

                codes[row] = code;
            }

            // Priced, not assumed: codes at the narrowest width that indexes the entries, plus the
            // entries themselves in whatever form they will actually be written.
            long encoded = ((long)length * FsstPlan.IndexPType(distinct).ByteWidth())
                + EntriesSize(arena, node, firstRows.AsSpan(0, distinct));
            if (encoded + DictionaryOverhead >= budget)
            {
                return ColumnPlan.Canonical;
            }

            // Only here, where the plan is kept, does anything become a real array.
            return ColumnPlan.Dictionary(
                firstRows.AsSpan(0, distinct).ToArray(), codes.AsSpan(0, length).ToArray());
        }
        finally
        {
            ArrayPool<int>.Shared.Return(codes);
            ArrayPool<int>.Shared.Return(firstRows);
            ArrayPool<int>.Shared.Return(chain);
            ArrayPool<int>.Shared.Return(buckets);
        }
    }

    /// <summary>Buckets for <paramref name="length"/> rows: a power of two, load factor under a half.</summary>
    private static int BucketCount(int length)
    {
        int capacity = 16;
        while (capacity < length && capacity < (1 << 30))
        {
            capacity <<= 1;
        }

        return capacity;
    }

    /// <summary>What the gathered dictionary entries will occupy once written.</summary>
    private static long EntriesSize(CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> rows)
    {
        switch (node.Kind)
        {
            case CanonicalKind.Bool:
                return (rows.Length + 7) / 8;

            case CanonicalKind.Primitive:
                return (long)rows.Length * node.PType.ByteWidth();

            case CanonicalKind.Decimal:
                return (long)rows.Length * DecimalStorage.ByteWidth(node.Storage);

            default:
            {
                // The entries are written as varbin or varbinview, whichever is smaller, exactly
                // as any other binary column would be.
                ValidityMask mask = ValidityMask.From(arena, node.Validity);
                ReadOnlySpan<byte> views = node.Views.Span;
                long heap = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    if (mask.IsValid(rows[i]))
                    {
                        heap += System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                            views.Slice(rows[i] * 16, 4));
                    }
                }

                long varbin = (((long)rows.Length + 1) * FsstPlan.IndexPType(heap).ByteWidth()) + heap;
                return Math.Min(((long)rows.Length * 16) + heap, varbin);
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
    /// <remarks>
    /// <c>heapBytes</c> is the valid values' bytes when the ingest pass already added them up
    /// (<see cref="BlockStats.TotalBytes"/>), or <c>-1</c> to walk the views here. The walk is one
    /// pass over every row of every string column of the file, run once per chunk to answer a
    /// question the pass that already read those views could answer for free.
    /// </remarks>
    private static long PlainBinarySize(CanonicalArena arena, CanonicalNode node, long heapBytes)
    {
        long viewForm = DataBytes(node);
        long heap = heapBytes;

        if (heap < 0)
        {
            heap = 0;
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
        }

        long varbinForm = (((long)node.Length + 1) * FsstPlan.IndexPType(heap).ByteWidth()) + heap;
        return Math.Min(viewForm, varbinForm);
    }

    /// <summary>
    /// The frame of reference the ingest pass already found: the column's minimum, as the raw bits
    /// <see cref="BitPackPlan"/> subtracts.
    /// </summary>
    /// <remarks>
    /// Two's complement for a signed column, the value itself for an unsigned one — which is exactly
    /// what <c>BitPackPlan.Minimum</c> produced from its own pass over every row.
    /// <see langword="null"/> when no row is valid, which is that pass's "nothing to measure a width
    /// against".
    /// </remarks>
    private static ulong? Reference(CanonicalNode node, in BlockStats stats) =>
        !stats.HasBounds
            ? null
            : node.PType.IsSignedInteger()
                ? unchecked((ulong)stats.Min.SignedValue)
                : stats.Min.UnsignedValue;

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

        /// <summary>
        /// Bytes per row for a fixed-width column, or 0 for <c>Bool</c> and <c>VarBinView</c>.
        /// </summary>
        /// <remarks>
        /// PERF-AUDIT-v2.md W-7b. The kind and the width are properties of the COLUMN, and they were
        /// being re-derived on every call: `switch` on the kind, then `PType.ByteWidth()` or
        /// `DecimalStorage.ByteWidth()`. Measured on `--throughput --write` at a million rows,
        /// `Equal` is called **349 446 863 times** and `Hash` **120 108 814 times** for one pass over
        /// the corpus's columns, and doubling the pair costs **30,6 %** of a `dict` write. Resolving
        /// both once in the constructor turns the hot path into a width test the JIT can fold.
        /// </remarks>
        private readonly int _width;

        internal RowComparer(CanonicalArena arena, int nodeIndex)
        {
            _arena = arena;
            _node = arena.GetNode(nodeIndex);
            _mask = ValidityMask.From(arena, _node.Validity);
            _width = _node.Kind switch
            {
                CanonicalKind.Bool or CanonicalKind.VarBinView => 0,
                CanonicalKind.Decimal => Types.Numerics.DecimalStorage.ByteWidth(_node.Storage),
                _ => _node.PType.ByteWidth(),
            };
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

            // THE ZERO-WIDTH KINDS FIRST, and not as a style choice: `CanonicalNode.Values` THROWS on
            // a Bool or a VarBinView, so the span may not be taken before the width has ruled them
            // out. The suite caught exactly that, on 23 tests.
            if (_width == 0)
            {
                return _node.Kind == CanonicalKind.Bool
                    ? CanonicalSupport.BitAt(_node.Bits.Span, _node.BitOffset + a) ==
                      CanonicalSupport.BitAt(_node.Bits.Span, _node.BitOffset + b)
                    : Bytes(a).SequenceEqual(Bytes(b));
            }

            // THE WIDTHS THAT ARE ONE LOAD ARE ONE LOAD. `SequenceEqual` over four bytes is a call
            // with a length check in front of it, and four bytes is what the average row of this
            // corpus actually is -- 464 MB hashed over 120 M calls, 3,9 bytes a call.
            ReadOnlySpan<byte> values = _node.Values.Span;
            return _width switch
            {
                1 => values[a] == values[b],
                2 => Read<ushort>(values, a) == Read<ushort>(values, b),
                4 => Read<uint>(values, a) == Read<uint>(values, b),
                8 => Read<ulong>(values, a) == Read<ulong>(values, b),
                _ => Fixed(a).SequenceEqual(Fixed(b)),
            };
        }

        internal int Hash(int row)
        {
            if (!_mask.IsValid(row))
            {
                return 0;
            }

            if (_width == 0)
            {
                return _node.Kind == CanonicalKind.Bool
                    ? CanonicalSupport.BitAt(_node.Bits.Span, _node.BitOffset + row) ? 1 : 2
                    : Hash(Bytes(row));
            }

            ReadOnlySpan<byte> values = _node.Values.Span;
            return _width switch
            {
                1 => Mix(values[row]),
                2 => Mix(Read<ushort>(values, row)),
                4 => Mix(Read<uint>(values, row)),
                8 => Mix(Read<ulong>(values, row)),
                _ => Hash(Fixed(row)),
            };
        }

        /// <summary>Row <paramref name="row"/> of a fixed-width column, read as its own width.</summary>
        private static T Read<T>(ReadOnlySpan<byte> values, int row)
            where T : unmanaged =>
            MemoryMarshal.Read<T>(values.Slice(row * Unsafe.SizeOf<T>(), Unsafe.SizeOf<T>()));

        /// <summary>
        /// A whole fixed-width value mixed in one step, where <see cref="Hash(ReadOnlySpan{byte})"/>
        /// would have taken one step per byte.
        /// </summary>
        /// <remarks>
        /// IT NEED NOT AGREE WITH FNV-1a, and does not. Nothing persists this hash: it picks a
        /// bucket, collisions are settled by <see cref="Equal"/>, and a dictionary code is handed
        /// out by order of first appearance -- so the FILE's bytes do not depend on it at all.
        /// `WrittenSizeTests` is the proof and it is byte-exact.
        /// </remarks>
        private static int Mix(ulong value)
        {
            ulong hash = value * 0x9E3779B97F4A7C15UL;
            hash ^= hash >> 29;
            hash *= 0xBF58476D1CE4E5B9UL;
            hash ^= hash >> 32;
            return (int)hash;
        }

        private ReadOnlySpan<byte> Fixed(int row) =>
            _node.Values.Span.Slice(row * _width, _width);

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
