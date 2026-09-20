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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
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
    /// The bytes the chooser priced this plan at — docs/11-write-strategy.md §3.4.3's prediction,
    /// which the encoder's actual output is checked against before the plan is reused.
    /// </summary>
    /// <remarks>
    /// For an exact scheme it is the formula's number; for a trial it is the encoded size the trial
    /// measured; for the plain column it is the plain column's bytes. Zero means "not priced" — a
    /// plan the reference chooser produced, or a child a scheme invented — and a memory is never
    /// formed from it.
    /// </remarks>
    internal long PredictedBytes { get; init; }

    /// <summary>Whether plan memory produced this plan without pricing the alternatives.</summary>
    internal bool FromMemory { get; init; }

    /// <summary>
    /// For a dictionary the ingest-time table priced: the table itself, which owns the codes and
    /// the entries the encoder reads directly (docs/11-write-strategy.md §3.5) — nothing is copied
    /// into <see cref="Codes"/> or <see cref="Gather"/>, which are then empty.
    /// </summary>
    /// <remarks>
    /// Valid until the chunk is released: the plan is consumed inside the same emission that chose
    /// it, and the table is reset only after the segment is written.
    /// </remarks>
    internal DistinctTable? Table { get; init; }

    /// <summary>With <see cref="Table"/>, how many codes are the chunk's entries.</summary>
    internal int Entries { get; init; }

    /// <summary>How many rows the chunk has: how much of <see cref="Codes"/> is a code.</summary>
    internal int Rows { get; init; }

    /// <summary>Whether <see cref="Codes"/> came from the pool and the encoder owes it back.</summary>
    internal bool CodesRented { get; init; }

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
        new ColumnPlan(ColumnScheme.Dict, firstOccurrences, codes) { Rows = codes.Length };

    /// <summary>
    /// The same, over a <paramref name="codes"/> array rented for <paramref name="rows"/> entries
    /// and longer than them, which the encoder hands back once it has narrowed it into the arena.
    /// </summary>
    internal static ColumnPlan RentedDictionary(int[] firstOccurrences, int[] codes, int rows) =>
        new ColumnPlan(ColumnScheme.Dict, firstOccurrences, codes)
        {
            Rows = rows,
            CodesRented = true,
        };

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

    /// <summary>Hands a rented codes array back, for a plan that will not be written.</summary>
    internal void ReleaseCodes()
    {
        if (CodesRented)
        {
            ArrayPool<int>.Shared.Return(Codes);
        }
    }

    /// <summary>
    /// One line that says what this plan would write: the scheme, its parameters, and a fingerprint
    /// of the arrays it carries. Two plans with the same description write the same bytes.
    /// </summary>
    /// <remarks>
    /// THE ORACLE OF STAGE R3 (IMPL-PLAN.md §1.2): the chooser by formulas is built beside the one
    /// it replaces and the two are compared PLAN AGAINST PLAN over the whole corpus, which catches
    /// what byte identity alone would let through — two divergences that compensate. The arrays are
    /// fingerprinted rather than printed because a codes buffer is a million entries, and an
    /// order-sensitive hash of them is as good a witness as the entries themselves for the
    /// question being asked, which is "did the two choosers decide the same thing".
    /// </remarks>
    internal string Describe() => Scheme switch
    {
        ColumnScheme.None => "canonical",
        ColumnScheme.RunEnd =>
            $"runend runs={Codes.Length} ends={Fingerprint(Codes):x} starts={Fingerprint(Gather):x}",
        ColumnScheme.Dict when Table is not null =>
            $"dict entries={Entries} rows={Rows} codes={Fingerprint(Table.Codes[..Rows]):x} " +
            $"first={Fingerprint(Table.FirstRows[..Entries]):x}",
        ColumnScheme.Dict =>
            $"dict entries={Gather.Length} rows={Rows} codes={Fingerprint(Codes.AsSpan(0, Rows)):x} " +
            $"first={Fingerprint(Gather):x}",
        ColumnScheme.BitPacked =>
            $"bitpacked {BitPack!.Transform} reference={BitPack.Reference} width={BitPack.BitWidth} " +
            $"patches={BitPack.Exceptions} cost={BitPack.Cost}",
        ColumnScheme.Sequence => $"sequence base={Sequence!.BaseBits} step={Sequence.Step}",
        ColumnScheme.Fsst => $"fsst size={Fsst!.EncodedSize}",
        ColumnScheme.Zstd => $"zstd frame={Zstd!.FrameLength}",
        ColumnScheme.Alp =>
            $"alp e={Alp!.ExponentE} f={Alp.ExponentF} size={Alp.EncodedSize} " +
            $"patches={Alp.PatchIndices.Length}",
        _ => Scheme.ToString(),
    };

    /// <summary>An order-sensitive 64-bit hash of an index array.</summary>
    private static ulong Fingerprint(ReadOnlySpan<int> values)
    {
        ulong hash = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < values.Length; i++)
        {
            hash = (hash ^ (uint)values[i]) * 0xBF58476D1CE4E5B9UL;
            hash ^= hash >> 31;
        }

        return hash;
    }
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
                : FixedRuns(in node, length, ceiling, runStarts, runEnds, out int fixedRuns)
                    ? fixedRuns
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

    /// <summary>
    /// The same count over a fixed-width, all-valid column, read as raw bits at its own width.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this shape was handled, in which case <paramref name="runs"/>
    /// holds the count; <see langword="false"/> when the caller must fall back to
    /// <see cref="ComparedRuns"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// PERF-GAPS.md W3.1. <see cref="ComparedRuns"/> asks <see cref="RowComparer.Equal"/> once per
    /// row, and that call is two validity lookups, a zero-width test and a switch on the width
    /// before it reads anything -- per row, for a column whose kind and width were settled when the
    /// comparer was built. On the `sparse` write axis the pass was 2,68 ms of 4,2, and the profile
    /// had been charging it to the chooser's own time.
    /// </para>
    /// <para>
    /// RAW BITS, NEVER <c>==</c> ON THE LOGICAL TYPE, and that is what makes generalizing by WIDTH
    /// rather than by ptype correct. <see cref="RowComparer"/> reads a 4-byte row as
    /// <see langword="uint"/> and an 8-byte one as <see langword="ulong"/> whatever the column
    /// holds, and its header says why: -0.0 and +0.0 are different values to a writer, and two NaNs
    /// with the same payload are the same value. A <c>float ==</c> loop would fuse the zeros and cut
    /// every run of NaN, changing the runs -- and so the bytes. Reading unsigned at the same width
    /// reproduces <see cref="RowComparer.Equal"/> exactly, which is also why a `Decimal` stored in
    /// four bytes may take this path.
    /// </para>
    /// <para>
    /// ALL-VALID ONLY, for the same reason <see cref="BitmapRuns"/> is: "null equals null, and null
    /// equals nothing else" is not reproducible from the value bytes, whose contents under a null
    /// row are unspecified.
    /// </para>
    /// </remarks>
    private static bool FixedRuns(
        in CanonicalNode node, int length, int ceiling, Span<int> starts, Span<int> ends, out int runs)
    {
        runs = 0;
        if (!node.Validity.IsAllValid
            || node.Kind is not (CanonicalKind.Primitive or CanonicalKind.Decimal))
        {
            return false;
        }

        int width = node.Kind == CanonicalKind.Decimal
            ? Types.Numerics.DecimalStorage.ByteWidth(node.Storage)
            : node.PType.ByteWidth();

        ReadOnlySpan<byte> values = node.Values.Span;
        switch (width)
        {
            case 1:
                runs = TypedRuns<byte>(values, length, ceiling, starts, ends);
                return true;
            case 2:
                runs = TypedRuns<ushort>(values, length, ceiling, starts, ends);
                return true;
            case 4:
                runs = TypedRuns<uint>(values, length, ceiling, starts, ends);
                return true;
            case 8:
                runs = TypedRuns<ulong>(values, length, ceiling, starts, ends);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// <see cref="FixedRuns"/> with the width resolved to a type, so the comparison is one load.
    /// </summary>
    /// <remarks>
    /// THE VECTOR STEP SKIPS EQUAL STRETCHES WHOLESALE, which is the shape run-end is being asked
    /// about: a column worth encoding has far fewer boundaries than rows, so most of this walk is
    /// proving that a block has none. Comparing `v[i..i+W]` with `v[i-1..i-1+W]` answers that for a
    /// whole vector at once; a vector that holds a boundary falls through to the scalar step, which
    /// finds its exact position. The unaligned second load is the point, not an oversight.
    /// </remarks>
    private static int TypedRuns<T>(
        ReadOnlySpan<byte> bytes, int length, int ceiling, Span<int> starts, Span<int> ends)
        where T : unmanaged, IEquatable<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes)[..length];
        ref T first = ref MemoryMarshal.GetReference(values);

        int runCount = 1;
        starts[0] = 0;

        bool wide = Vector.IsHardwareAccelerated && Vector<T>.Count > 1;
        int step = Vector<T>.Count;
        int i = 1;
        while (i < length)
        {
            if (wide && i + step <= length
                && Vector.EqualsAll(
                    Vector.LoadUnsafe(ref first, (nuint)i),
                    Vector.LoadUnsafe(ref first, (nuint)(i - 1))))
            {
                i += step;
                continue;
            }

            if (Unsafe.Add(ref first, i).Equals(Unsafe.Add(ref first, i - 1)))
            {
                i++;
                continue;
            }

            if (runCount > ceiling)
            {
                return runCount;
            }

            ends[runCount - 1] = i;
            starts[runCount] = i;
            runCount++;
            i++;
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
    /// <param name="cascade">
    /// What the PARENT already knows about this column, when it is a child a scheme produced
    /// (docs/11-write-strategy.md §3.4.4). Every claim it carries is a proof written out in
    /// <see cref="Cascade"/>, so a candidate it declares dead is one this method would have
    /// declined anyway: the plan is the same plan and not a byte moves. An absent cascade — the
    /// default, and what every top-level column passes — claims nothing.
    /// </param>
    /// <param name="chunk">
    /// The cursor the summary came from, for the statistics that are too big to travel inside it —
    /// today the pair of bit-width histograms, tomorrow the distinct table and its codes buffer.
    /// Absent means the same thing it means everywhere else here: measure it yourself.
    /// </param>
    /// <returns>The plan; <see cref="ColumnPlan.Canonical"/> when nothing wins.</returns>
    internal static ColumnPlan Choose(
        CanonicalArena arena, int nodeIndex, VortexEdition target = EditionRegistry.Newest,
        in BlockStats stats = default, Cascade cascade = default, ChunkStats chunk = default)
    {
        // STAGE R4: THE CHOOSER BY FORMULAS IS THE CHOOSER. It decides under today's run-end rule
        // -- run-end wins outright inside its ratio -- because that is the rule the differential
        // proved byte-identical on 856 files; the spec's rule changes three chunks and is measured
        // separately before it is taken.
        DifferentialProbe? probe = Differential.Value;
        ColumnPlan plan = ChooseByFormula(
            arena, nodeIndex, target, in stats, cascade, chunk,
            runEndCompetes: probe is not null && probe.RunEndCompetes);

        // STAGE R3'S ORACLE, ROLES SWAPPED: when a test has installed a probe, the chooser this one
        // replaced runs on the same chunk with the same inputs and the two decisions are compared
        // plan against plan. It stays as the reference until stage R5 makes its walks unreachable.
        // The probe flows with the async write and nowhere else, so two tests writing at once
        // cannot see each other's chunks.
        if (probe is not null)
        {
            ColumnPlan reference = ChooseToday(arena, nodeIndex, target, in stats, cascade, chunk);
            // A plan memory produced is marked as such: the reference prices every candidate on
            // every chunk, so where memory skipped that and reached another verdict the two differ
            // by design (§3.4.3), and the test counts those apart from real disagreements.
            string chosen = plan.Describe();
            string expected = reference.Describe();
            if (chosen != expected)
            {
                probe.Report(nodeIndex, (plan.FromMemory ? "memory " : "") + chosen, expected);
            }

            // The reference plan is thrown away, and a zstd frame, a set of ALP integers or a row
            // of dictionary codes among its candidates is holding a pooled buffer that nothing
            // will write.
            reference.Zstd?.Release();
            reference.Alp?.Release();
            reference.ReleaseCodes();
        }

        return plan;
    }

    /// <summary>What a test installs to compare the two choosers, and which rule to compare under.</summary>
    /// <param name="RunEndCompetes">
    /// <see langword="false"/> holds the formula chooser to today's rule — run-end wins outright
    /// once inside its ratio — and the two must then agree on every chunk of the corpus, which is
    /// the test of the harness itself. <see langword="true"/> lets run-end compete in bytes as
    /// docs/11-write-strategy.md §3.4.2 prices it, and every disagreement is a plan the spec's rule
    /// would change, with its cost on both sides.
    /// </param>
    /// <param name="Report">Called per disagreeing chunk with the node and the two descriptions.</param>
    internal sealed record DifferentialProbe(bool RunEndCompetes, Action<int, string, string> Report);

    /// <summary>The probe in force for the current async flow, or none.</summary>
    internal static readonly AsyncLocal<DifferentialProbe?> Differential = new AsyncLocal<DifferentialProbe?>();

    /// <summary>The chooser as it stands: candidates in a fixed order, the first that wins returns.</summary>
    private static ColumnPlan ChooseToday(
        CanonicalArena arena, int nodeIndex, VortexEdition target, in BlockStats stats,
        Cascade cascade, ChunkStats chunk)
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
        //
        // AND SINCE STAGE 2e THE INGEST PASS HAS ALREADY ANSWERED IT. Its steps are exact, so a
        // column it disqualified is not offered the walk at all, and one it confirmed is built in
        // constant time from `v[1] - v[0]`. W-9 measured that walk reading **78 % of the rows it is
        // offered** -- most columns that reach it really are progressions, so it runs to the end
        // rather than bailing at row three.
        if (Allows(target, "vortex.sequence") && !cascade.SequenceIsDead)
        {
            bool knownSteps = measured && stats.DeltaKnown;
            if (!knownSteps || !stats.DeltaBroken)
            {
                SequencePlan? sequence = SequencePlan.TryBuild(arena, node, stepsAreConstant: knownSteps);
                if (sequence is not null)
                {
                    return ColumnPlan.ForSequence(sequence);
                }
            }
        }

        // THE COMPARER IS BUILT ONLY IF SOMETHING WILL COMPARE. Its two consumers are the run scan
        // and the dictionary probe, and a cascade that declares both dead -- a dictionary's codes,
        // a run-end's ends -- leaves nothing to compare with.
        RowComparer comparer = cascade.RunsAreDead && cascade.DictionaryIsDead
            ? default
            : new RowComparer(arena, nodeIndex);

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
        if (Allows(target, "vortex.runend") && !cascade.RunsAreDead)
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
            // THE WIDTHS COME FROM THE INGEST PASS WHEN IT HAS THEM (11 §3.2), and this is the only
            // place that asks. The buffer is a stack one because the answer is 130 counters and its
            // consumer returns before this frame does.
            Span<int> ingested = stackalloc int[BitPackWidths.Length];
            bool haveWidths = measured && integers && !stats.WidthsBroken && chunk.Widths(ingested);

            packed = BitPackPlan.TryBuild(
                arena, node, zigzag: Allows(target, "vortex.zigzag"),
                reference: cascade.Reference
                    ?? (measured && integers ? Reference(node, in stats) : null),
                ingested: haveWidths ? ingested : default);
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
        // THE TABLE FIRST, THE WALK ONLY WHEN IT CANNOT ANSWER (docs/11-write-strategy.md §3.2.2).
        // The ingest pass has already handed every row of this chunk its code, in first-seen order,
        // and owns the distinct values; pricing is arithmetic on its counts and the plan is a copy
        // of its buffers. The walk stays as the fallback for a cursor that cannot serve -- a child a
        // scheme produced, a chunk the table abandoned -- and the writer counts every such fallback
        // through the same predicate, so it cannot go quiet.
        long budget = packed is null ? plain : Math.Min(plain, packed.Cost);
        ColumnPlan dictionary = ColumnPlan.Canonical;
        if (Allows(target, "vortex.dict") && !cascade.DictionaryIsDead)
        {
            dictionary = chunk.TableServes(length)
                ? TabledDictionary(node, in chunk, length, budget)
                : Dictionary(arena, node, comparer, length, budget);
        }
        if (dictionary.Scheme != ColumnScheme.None)
        {
            return dictionary;
        }

        if (packed is not null)
        {
            return ColumnPlan.ForBitPacking(packed);
        }

        return Trials(arena, nodeIndex, node, target, plain, cascade.IsValuesChild);
    }

    /// <summary>
    /// The three schemes whose cost is not a function of the statistics — ALP, zstd, FSST — each
    /// tried under <paramref name="budget"/> as its abort bound (docs/11-write-strategy.md §3.4.1,
    /// step 3).
    /// </summary>
    /// <remarks>
    /// SHARED BY BOTH CHOOSERS, which is what makes their comparison a comparison of the EXACT
    /// tier alone: whatever the formulas decide, the trials that follow are the same code under
    /// the same ceiling, so a disagreement between the two can only come from the arithmetic.
    /// </remarks>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The column chunk.</param>
    /// <param name="node">The same chunk, resolved.</param>
    /// <param name="target">The edition being written.</param>
    /// <param name="budget">The bytes the trial has to beat: the best exact plan's, or the plain column's.</param>
    /// <param name="gatesOff">
    /// Whether the size gate in front of zstd is lifted — the values child of a scheme is small by
    /// construction and its trial cheap, so the guard has nothing to guard
    /// (<see cref="Cascade.IsValuesChild"/>).
    /// </param>
    private static ColumnPlan Trials(
        CanonicalArena arena, int nodeIndex, CanonicalNode node, VortexEdition target, long budget,
        bool gatesOff = false)
    {
        // Named as the body has always named it: every ceiling below is derived from this one.
        long plain = budget;

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
                return ColumnPlan.ForAlp(alp) with { PredictedBytes = alp.EncodedSize };
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
            && (gatesOff || DataBytes(node) >= ZstdMinimumBytes))
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
            return ColumnPlan.ForFsst(fsst) with { PredictedBytes = fsst.EncodedSize };
        }

        if (zstd is not null)
        {
            return ColumnPlan.ForZstd(zstd) with { PredictedBytes = zstd.FrameLength };
        }

        return ColumnPlan.Canonical with { PredictedBytes = plain };
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
        bool kept = false;
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

                    // GIVE UP AS SOON AS THE DICTIONARY PROVABLY CANNOT WIN, and the bound is the
                    // price the plan will actually be charged rather than a weaker stand-in for it.
                    //
                    // The old bound assumed a ONE-BYTE code per row. That is true only while there
                    // are at most 256 distinct values, and it is what made this loop read every row
                    // of a column it was going to refuse: `fastlanes_bitpacked` has 1 024 distinct
                    // values in a 262 144-row chunk, so its codes are two bytes and the dictionary
                    // costs half a megabyte against a bit-packing that costs 320 kB -- decided at
                    // the 257th distinct value, and discovered at the 262 144th row.
                    // WRITE-ARCHITECTURE.md §1 charges that loop **34 %** of that file's write,
                    // **63 %** of `alp_no_patches`, and 37 to 47 % of `onpair` and `zstd`, in every
                    // case with the note "cannot win".
                    //
                    // IT IS STILL A LOWER BOUND, so nothing that would have been kept is now
                    // refused and not a byte moves: `distinct` only grows, so the code width only
                    // grows; and `EntriesSize` is at least `distinct * minimumEntry` for every kind
                    // that reaches this line. The final test below is this same expression with the
                    // entries priced exactly.
                    long codeBytes = (long)length * FsstPlan.IndexPType(distinct).ByteWidth();
                    if (codeBytes + ((long)distinct * minimumEntry) + DictionaryOverhead >= budget)
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

            // The entries become a real array: there are `distinct` of them, and the writer keeps
            // them past the point the rentals go back. The codes do not. There is one per row -- a
            // full row vector, 16 kB of a 4 096-row write and a fifth of two of the allocation
            // axes -- and the only thing the writer does with them is narrow them into an arena
            // buffer, so the rental travels with the plan and is handed back there.
            kept = true;
            return ColumnPlan.RentedDictionary(firstRows.AsSpan(0, distinct).ToArray(), codes, length)
                with { PredictedBytes = encoded };
        }
        finally
        {
            if (!kept)
            {
                ArrayPool<int>.Shared.Return(codes);
            }

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
    internal static bool Allows(VortexEdition target, string id) =>
        EditionRegistry.Contains(target, ComponentKind.Array, id);

    /// <summary>Whether a canonical form has a row equality this compressor can compute.</summary>
    /// <summary>
    /// The chooser of docs/11-write-strategy.md §3.4.1: degenerate cases from the statistics, then
    /// every exact-cost candidate priced by formula and the cheapest kept, then the trials under
    /// that cost. Built beside <see cref="ChooseToday"/> and compared with it plan against plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT DIFFERS FROM TODAY IS THE SHAPE, NOT THE ARITHMETIC. Today's chooser returns the first
    /// candidate that wins, in a fixed order; this one prices them all and compares. On every
    /// candidate but one the two coincide: bit-packing's cost is the same <see cref="BitPackPlan"/>,
    /// the dictionary's is the same table or walk against the same budget, the trials are the same
    /// <see cref="Trials"/>. The one that does not is RUN-END, which today wins outright once
    /// inside its ratio and here is priced at §3.4.2's <c>ends + values + 256</c> — and only when
    /// <paramref name="runEndCompetes"/> says so. Under <see langword="false"/> it keeps today's
    /// rule and the two choosers must agree on every chunk; under <see langword="true"/> every
    /// disagreement is a chunk the spec's rule would encode differently, reported with both costs.
    /// </para>
    /// <para>
    /// RUN-END IS MATERIALIZED ONLY IF IT WINS. Today the scan gathers the runs before anything else
    /// has been priced, on every column inside the ratio; here the count from the ingest pass
    /// prices the candidate and the gather runs once, on the winner. The values child is priced as
    /// the runs' share of the plain column, which is exact for a fixed-width kind and an estimate
    /// for strings — the exact bound §3.4.2 gives is per fixed width, and a string run-end's values
    /// are a gather nobody has made yet.
    /// </para>
    /// </remarks>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The column chunk.</param>
    /// <param name="target">The edition being written.</param>
    /// <param name="stats">The ingest statistics over exactly these rows, or absent.</param>
    /// <param name="cascade">What the parent knows about this child.</param>
    /// <param name="chunk">The cursor the statistics came from.</param>
    /// <param name="runEndCompetes">Whether run-end is priced against the others or wins outright.</param>
    private static ColumnPlan ChooseByFormula(
        CanonicalArena arena, int nodeIndex, VortexEdition target, in BlockStats stats,
        Cascade cascade, ChunkStats chunk, bool runEndCompetes)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int length = node.Length;
        bool measured = stats.IsPresent && stats.Rows == length;
        if (!IsComparable(node.Kind) || (length < MinimumRows && DataBytes(node) < MinimumBytes))
        {
            return ColumnPlan.Canonical;
        }

        long plain = node.Kind == CanonicalKind.VarBinView
            ? PlainBinarySize(arena, node, measured ? stats.TotalBytes : -1)
            : DataBytes(node);

        // 1. DEGENERATE FIRST, AND BEFORE MEMORY: a progression costs nothing per row and nothing
        // can beat it -- and the statistics answer it without a walk, so there is nothing for plan
        // memory to save by standing in front of it. It did stand there once: on the 1M-row
        // `chunked` file a bit-packing that held to the byte on the one chunk with a jump in it was
        // re-priced on every progression that followed, held again, and was written -- 295 KB a
        // chunk where 32 bytes were exact, the file twice its size, and +70 % on the clock that
        // six hypotheses about the time never explained. `PlanMemoryTests` holds the shape.
        ColumnPlan progression = SequenceOf(arena, node, target, in stats, cascade, measured);
        if (progression.Scheme != ColumnScheme.None)
        {
            return progression;
        }

        RowComparer comparer = cascade.RunsAreDead && cascade.DictionaryIsDead
            ? default
            : new RowComparer(arena, nodeIndex);

        // 2. RUN-END FROM THE PASS, under today's rule -- inside its ratio it wins before anything
        // else is priced -- and before memory for the same reason as the progression: a count the
        // pass took makes the verdict arithmetic, and a constant chunk after a held bit-packing was
        // written as a width-0 packing with 256 bytes of framing where one run costs 32. A count
        // the pass did not take is walked further down, after memory has had its say.
        bool runEndAllowed =
            Allows(target, "vortex.runend") && !cascade.RunsAreDead && length / RunEndRatio >= 1;
        bool runsCounted = runEndAllowed && measured && stats.HasRunBoundaries;
        ColumnPlan walkedRuns = ColumnPlan.Canonical;
        long runEndCost = long.MaxValue;
        long runs = 0;
        if (runsCounted)
        {
            runs = stats.RunCount;
            runEndCost = RunEndCostOf(runs, length, plain);
            if (!runEndCompetes && runEndCost != long.MaxValue)
            {
                return MaterializeRuns(arena, in node, in comparer, length, runs, in walkedRuns, runEndCost);
            }
        }

        // 3. PLAN MEMORY (§3.4.3): a column whose last plan produced the bytes it was priced at, to
        // five per cent, is offered that plan again -- re-priced on this chunk's own statistics,
        // which is arithmetic -- and nothing else is priced. Only a plan that still wins on its
        // own terms is reused; one that no longer does sends the column back to full pricing, and
        // so does a column with no memory yet. A child a scheme invented has no cursor and so no
        // memory, and a chunk the pass did not measure is not trusted with one. What memory skips
        // is exactly what costs: the walks below and the trials at the end -- never a candidate
        // the statistics have already answered above.
        if (measured && chunk.Memory is { WithinTolerance: true } memory)
        {
            ColumnPlan remembered = Reprice(
                memory.Scheme, arena, nodeIndex, node, target, in stats, cascade, chunk, plain);
            if (remembered.Scheme == memory.Scheme)
            {
                return remembered with { FromMemory = true };
            }
        }

        // 4. EXACT CANDIDATES, each priced, the cheapest kept as `best`.
        long best = plain;
        ColumnScheme bestScheme = ColumnScheme.None;

        // Run-end, when the pass did not count it: walked now, as today, because nothing else can
        // price it; the gather still waits for the verdict.
        if (runEndAllowed && !runsCounted)
        {
            walkedRuns = TryRuns(arena, in node, in comparer, length);
            runs = walkedRuns.Scheme == ColumnScheme.None ? long.MaxValue : walkedRuns.Codes.Length;
            runEndCost = RunEndCostOf(runs, length, plain);
            if (!runEndCompetes && runEndCost != long.MaxValue)
            {
                // Today's rule: inside the ratio, run-end wins before anything else is priced.
                return MaterializeRuns(arena, in node, in comparer, length, runs, in walkedRuns, runEndCost);
            }
        }

        // Bit-packing: exact, patches included. Frame of reference's histogram comes from the
        // ingest when the reference is zero and from its own walk otherwise (BitPackPlan; 11 §3.2.3
        // as delivered: the bound and its second sweep were superseded by the exact walk).
        bool integers = node.Kind == CanonicalKind.Primitive && node.PType.IsInteger();
        BitPackPlan? packed = null;
        if (Allows(target, "fastlanes.bitpacked") && !(measured && integers && !stats.HasBounds))
        {
            Span<int> ingested = stackalloc int[BitPackWidths.Length];
            bool haveWidths = measured && integers && !stats.WidthsBroken && chunk.Widths(ingested);
            if (haveWidths)
            {
                chunk.NoteWidthsServed();
            }

            packed = BitPackPlan.TryBuild(
                arena, node, zigzag: Allows(target, "vortex.zigzag"),
                reference: cascade.Reference
                    ?? (measured && integers ? Reference(node, in stats) : null),
                ingested: haveWidths ? ingested : default);
        }

        if (packed is not null && packed.Transform == BitPackTransform.Frame
            && !Allows(target, "fastlanes.for"))
        {
            packed = null;
        }

        if (packed is not null && packed.Cost < best)
        {
            best = packed.Cost;
            bestScheme = ColumnScheme.BitPacked;
        }

        if (runEndCompetes && runEndCost < best)
        {
            best = runEndCost;
            bestScheme = ColumnScheme.RunEnd;
        }

        // The dictionary, against the best so far: it returns a plan only when it beats it.
        if (Allows(target, "vortex.dict") && !cascade.DictionaryIsDead)
        {
            ColumnPlan dictionary = chunk.TableServes(length)
                ? TabledDictionary(node, in chunk, length, best)
                : Dictionary(arena, node, comparer, length, best);
            if (dictionary.Scheme != ColumnScheme.None)
            {
                return dictionary;
            }
        }

        // AN EXACT WINNER STANDS, AND THE TRIALS ARE NOT OFFERED THE COLUMN. This is where §3.4.1's
        // "trials under the best cost in hand" met §2's "bytes identical" and lost: read literally,
        // a zstd frame is allowed to beat a bit-packing, and on this corpus it does -- 149 chunks in
        // the first differential run, `fastlanes_bitpacked` among them at 1 961 bytes of zstd
        // against 5 376 of packing. Smaller on disk, slower to decode, and not what the reference
        // writes. The reading that keeps the guarantee is today's: a trial is tried on the columns
        // no exact scheme took, under the plain column's bytes as its ceiling. The number is kept
        // in IMPL-PLAN.md as an open question for a later stage, not decided here by accident.
        switch (bestScheme)
        {
            case ColumnScheme.RunEnd:
                return MaterializeRuns(arena, in node, in comparer, length, runs, in walkedRuns, runEndCost);

            case ColumnScheme.BitPacked:
                return ColumnPlan.ForBitPacking(packed!) with { PredictedBytes = packed!.BufferBytes };

            default:
                return Trials(arena, nodeIndex, node, target, plain, cascade.IsValuesChild);
        }
    }

    /// <summary>
    /// Run-end's bytes for <paramref name="runs"/> runs over <paramref name="length"/> rows, or
    /// <see cref="long.MaxValue"/> outside its ratio: 32 for a constant, and docs/11 §3.4.2's
    /// <c>ends + values + 256</c> otherwise, the values priced as the runs' share of the plain column.
    /// </summary>
    /// <param name="runs">The run count, from the pass or from a walk.</param>
    /// <param name="length">The chunk's rows.</param>
    /// <param name="plain">The plain column's bytes.</param>
    private static long RunEndCostOf(long runs, int length, long plain)
    {
        if (runs == 1)
        {
            return 32;
        }

        if (runs > length / RunEndRatio)
        {
            return long.MaxValue;
        }

        long ends = runs * FsstPlan.IndexPType(length).ByteWidth();
        return ends + (plain * runs / length) + 256;
    }

    /// <summary>The progression plan when the column is one, priced; canonical otherwise.</summary>
    private static ColumnPlan SequenceOf(
        CanonicalArena arena, CanonicalNode node, VortexEdition target, in BlockStats stats,
        Cascade cascade, bool measured)
    {
        if (!Allows(target, "vortex.sequence") || cascade.SequenceIsDead)
        {
            return ColumnPlan.Canonical;
        }

        bool knownSteps = measured && stats.DeltaKnown;
        if (knownSteps && stats.DeltaBroken)
        {
            return ColumnPlan.Canonical;
        }

        SequencePlan? sequence = SequencePlan.TryBuild(arena, node, stepsAreConstant: knownSteps);
        // A progression writes no buffer -- the base and the step go in the metadata -- so the
        // prediction plan memory checks is zero, and holds exactly when the encoder wrote none.
        return sequence is null
            ? ColumnPlan.Canonical
            : ColumnPlan.ForSequence(sequence) with { PredictedBytes = 0 };
    }

    /// <summary>
    /// Prices ONE remembered scheme on this chunk, and nothing else: the plan when it still wins on
    /// its own terms, canonical (with the plain bytes) when it no longer does or never applied.
    /// </summary>
    /// <remarks>
    /// The verdict is "still wins" against the plain column alone, not against the field, because
    /// the field is exactly what memory exists to not price. A remembered scheme that has stopped
    /// winning returns a plan of another scheme, which the caller reads as "back to full pricing";
    /// a remembered CANONICAL is the cheapest case of all, and the one docs/11 §3.2.2 turns the
    /// distinct table off for.
    /// </remarks>
    private static ColumnPlan Reprice(
        ColumnScheme scheme, CanonicalArena arena, int nodeIndex, CanonicalNode node,
        VortexEdition target, in BlockStats stats, Cascade cascade, ChunkStats chunk, long plain)
    {
        int length = node.Length;
        switch (scheme)
        {
            case ColumnScheme.None:
                return ColumnPlan.Canonical with { PredictedBytes = plain };

            case ColumnScheme.Sequence:
                return SequenceOf(arena, node, target, in stats, cascade, measured: true);

            case ColumnScheme.RunEnd:
            {
                if (!Allows(target, "vortex.runend") || cascade.RunsAreDead
                    || !stats.HasRunBoundaries || length / RunEndRatio < 1)
                {
                    return ColumnPlan.Canonical;
                }

                long runs = stats.RunCount;
                long cost = RunEndCostOf(runs, length, plain);
                if (cost == long.MaxValue)
                {
                    return ColumnPlan.Canonical;
                }

                RowComparer comparer = new RowComparer(arena, nodeIndex);
                ColumnPlan none = ColumnPlan.Canonical;
                return MaterializeRuns(arena, in node, in comparer, length, runs, in none, cost);
            }

            case ColumnScheme.BitPacked:
            {
                bool integers = node.Kind == CanonicalKind.Primitive && node.PType.IsInteger();
                if (!Allows(target, "fastlanes.bitpacked") || !integers || !stats.HasBounds)
                {
                    return ColumnPlan.Canonical;
                }

                Span<int> ingested = stackalloc int[BitPackWidths.Length];
                bool haveWidths = !stats.WidthsBroken && chunk.Widths(ingested);
                if (haveWidths)
                {
                    chunk.NoteWidthsServed();
                }

                BitPackPlan? packed = BitPackPlan.TryBuild(
                    arena, node, zigzag: Allows(target, "vortex.zigzag"),
                    reference: cascade.Reference ?? Reference(node, in stats),
                    ingested: haveWidths ? ingested : default);
                if (packed is null
                    || (packed.Transform == BitPackTransform.Frame && !Allows(target, "fastlanes.for")))
                {
                    return ColumnPlan.Canonical;
                }

                return ColumnPlan.ForBitPacking(packed) with { PredictedBytes = packed.BufferBytes };
            }

            case ColumnScheme.Dict:
                return Allows(target, "vortex.dict") && !cascade.DictionaryIsDead
                    && chunk.TableServes(length)
                    ? TabledDictionary(node, in chunk, length, plain)
                    : ColumnPlan.Canonical;

            default:
                return TrialOf(scheme, arena, nodeIndex, node, target, plain);
        }
    }

    /// <summary>One of the three trials alone, under the plain column's bytes.</summary>
    private static ColumnPlan TrialOf(
        ColumnScheme scheme, CanonicalArena arena, int nodeIndex, CanonicalNode node,
        VortexEdition target, long plain)
    {
        switch (scheme)
        {
            case ColumnScheme.Alp:
            {
                if (node.Kind != CanonicalKind.Primitive || !node.PType.IsFloat()
                    || !Allows(target, "vortex.alp"))
                {
                    return ColumnPlan.Canonical;
                }

                AlpPlan? alp = AlpPlan.TryBuild(arena, nodeIndex, plain);
                return alp is null
                    ? ColumnPlan.Canonical
                    : ColumnPlan.ForAlp(alp) with { PredictedBytes = alp.EncodedSize };
            }

            case ColumnScheme.Zstd:
            {
                if (node.Kind is not (CanonicalKind.VarBinView or CanonicalKind.Primitive)
                    || !Allows(target, "vortex.zstd") || DataBytes(node) < ZstdMinimumBytes)
                {
                    return ColumnPlan.Canonical;
                }

                ZstdPlan? zstd = ZstdPlan.TryBuild(arena, nodeIndex, plain);
                return zstd is null
                    ? ColumnPlan.Canonical
                    : ColumnPlan.ForZstd(zstd) with { PredictedBytes = zstd.FrameLength };
            }

            case ColumnScheme.Fsst:
            {
                if (node.Kind != CanonicalKind.VarBinView || !Allows(target, "vortex.fsst"))
                {
                    return ColumnPlan.Canonical;
                }

                FsstPlan? fsst = FsstPlan.TryBuild(arena, nodeIndex, plain * 9 / 10);
                return fsst is null
                    ? ColumnPlan.Canonical
                    : ColumnPlan.ForFsst(fsst) with { PredictedBytes = fsst.EncodedSize };
            }

            default:
                return ColumnPlan.Canonical;
        }
    }

    /// <summary>The run-end plan for a winner: one run needs no gather, a walked count already has it.</summary>
    private static ColumnPlan MaterializeRuns(
        CanonicalArena arena, in CanonicalNode node, in RowComparer comparer, int length, long runs,
        in ColumnPlan walked, long cost)
    {
        ColumnPlan plan = walked.Scheme != ColumnScheme.None
            ? walked
            : runs == 1
                ? ColumnPlan.Runs([0], [length])
                : TryRuns(arena, in node, in comparer, length);
        // The 256 is framing, which the encoder's buffers do not hold; the prediction plan memory
        // checks is the ends and the values alone.
        return plan with { PredictedBytes = Math.Max(0, cost - 256) };
    }

    /// <summary>
    /// The dictionary plan from the ingest-time table: the same verdict <see cref="Dictionary"/>
    /// reaches by walking the chunk, reached by arithmetic on what the table already holds.
    /// </summary>
    /// <remarks>
    /// THE FINAL TEST IS THE SAME EXPRESSION, on the same numbers. The walk prices
    /// <c>rows × codeWidth(entries) + EntriesSize(entries)</c> against the budget once it has seen
    /// every row; its early abandonment is a lower bound of that test and never fires on a plan the
    /// test would have kept. So the plan here is the plan there — <c>WrittenSizeTests</c> is the
    /// proof — and what changes is that no row is read to reach it.
    /// <para>
    /// THE ENTRIES ARE THE COUNT AT THE LAST BLOCK'S CLOSE, not the table's current count: the table
    /// has since probed the tail the writer will carry into the next chunk, and codes are handed out
    /// in first-seen order, so the chunk's rows use exactly the codes below that count and its heap
    /// bytes are exactly the heap at that moment.
    /// </para>
    /// </remarks>
    /// <param name="node">The column chunk.</param>
    /// <param name="chunk">The cursor whose table serves this chunk.</param>
    /// <param name="length">The chunk's row count.</param>
    /// <param name="budget">The cheapest plan so far, in bytes.</param>
    private static ColumnPlan TabledDictionary(
        CanonicalNode node, in ChunkStats chunk, int length, long budget)
    {
        DistinctTable table = chunk.Table!;
        (int entries, long heap) = chunk.TableAtClose;

        long encoded = ((long)length * FsstPlan.IndexPType(entries).ByteWidth())
            + EntriesSizeOf(node, entries, heap);
        if (encoded + DictionaryOverhead >= budget)
        {
            return ColumnPlan.Canonical;
        }

        // NO COPY: the plan points at the table, and the encoder reads the codes and lays out the
        // entries from it directly (§3.5). The table lives until the segment is written, which is
        // after the plan has been consumed.
        return ColumnPlan.Dictionary([], []) with
        {
            Table = table,
            Entries = entries,
            Rows = length,
            PredictedBytes = encoded,
        };
    }

    /// <summary>
    /// The bytes the dictionary LAYER was priced at, measured on what the encoder built: the codes
    /// at the narrowest width that indexes <paramref name="values"/>, plus the entries in the form
    /// <see cref="EntriesSize(CanonicalArena, CanonicalNode, ReadOnlySpan{int})"/> prices them.
    /// </summary>
    /// <remarks>
    /// THIS IS WHAT PLAN MEMORY HOLDS A DICTIONARY TO, and not the bytes its subtree produced. The
    /// prediction is codes plus entries; the children then take their own schemes -- the codes
    /// zstd, the values FSST or zstd -- and the buffers they append are a fraction of that. On the
    /// 1M-row `dict_u8_codes` every chunk predicted 66 738 bytes and produced 363: the memory
    /// "broke" sixteen times out of sixteen, the distinct table was never expected to serve, and
    /// every chunk walked for the dictionary the table had already built. What the children make
    /// of the layer is theirs; the dictionary's own decision held exactly.
    /// </remarks>
    /// <param name="arena">The arena holding the values child.</param>
    /// <param name="values">The values child as the encoder built it, one row per entry.</param>
    /// <param name="rows">The rows of the column the codes index.</param>
    internal static long DictionaryLayerBytes(CanonicalArena arena, CanonicalNode values, int rows)
    {
        int entries = values.Length;
        return ((long)rows * FsstPlan.IndexPType(entries).ByteWidth()) + EntriesSize(arena, values, entries);
    }

    /// <summary>
    /// <see cref="EntriesSize(CanonicalArena, CanonicalNode, ReadOnlySpan{int})"/> over the first
    /// <paramref name="count"/> rows of <paramref name="node"/> in order -- a node that IS the
    /// entries.
    /// </summary>
    private static long EntriesSize(CanonicalArena arena, CanonicalNode node, int count)
    {
        switch (node.Kind)
        {
            case CanonicalKind.Bool:
                return (count + 7) / 8;

            case CanonicalKind.Primitive:
                return (long)count * node.PType.ByteWidth();

            case CanonicalKind.Decimal:
                return (long)count * DecimalStorage.ByteWidth(node.Storage);

            default:
            {
                ValidityMask mask = ValidityMask.From(arena, node.Validity);
                ReadOnlySpan<byte> views = node.Views.Span;
                long heap = 0;
                for (int i = 0; i < count; i++)
                {
                    if (mask.IsValid(i))
                    {
                        heap += System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                            views.Slice(i * 16, 4));
                    }
                }

                long varbin = (((long)count + 1) * FsstPlan.IndexPType(heap).ByteWidth()) + heap;
                return Math.Min(((long)count * 16) + heap, varbin);
            }
        }
    }

    /// <summary>
    /// <see cref="EntriesSize(CanonicalArena, CanonicalNode, ReadOnlySpan{int})"/> from counts
    /// instead of rows: what <paramref name="entries"/> distinct values holding
    /// <paramref name="heap"/> bytes cost as a values child.
    /// </summary>
    /// <param name="node">The column chunk, for its kind and widths.</param>
    /// <param name="entries">Distinct values, the null counted as one.</param>
    /// <param name="heap">For a view column, the bytes of the non-null distinct values.</param>
    private static long EntriesSizeOf(CanonicalNode node, int entries, long heap)
    {
        switch (node.Kind)
        {
            case CanonicalKind.Bool:
                return (entries + 7) / 8;

            case CanonicalKind.Primitive:
                return (long)entries * node.PType.ByteWidth();

            case CanonicalKind.Decimal:
                return (long)entries * DecimalStorage.ByteWidth(node.Storage);

            default:
            {
                long varbin = (((long)entries + 1) * FsstPlan.IndexPType(heap).ByteWidth()) + heap;
                return Math.Min(((long)entries * 16) + heap, varbin);
            }
        }
    }

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
        private static int Mix(ulong value) => (int)KeyHash.Mix(value);

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

        /// <summary>
        /// The write path's one byte-string hash, <see cref="KeyHash.Bytes"/>: it lives there so
        /// that this comparer and the ingest-time <see cref="DistinctTable"/> cannot disagree on
        /// what a value hashes to. Its history — FNV-1a measured at 21 % to 57 % of a string
        /// column's write, XxHash3 with a folded short arm, and the 3 % to 6 % it costs `struct`
        /// and `varbin` — is written at the top of that file.
        /// </summary>
        /// <param name="bytes">The value's bytes.</param>
        private static int Hash(ReadOnlySpan<byte> bytes) => (int)KeyHash.Bytes(bytes);
    }
}
