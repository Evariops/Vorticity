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
    /// FSST: the scheme high-cardinality text wants, taken where every other rule falls through.
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
    /// 7 because 6 is taken. The members are not in numeric order — <c>BitPacked</c> is 3 and sits
    /// last — so appending a member and giving it the number after its neighbour's silently aliases
    /// <c>Sequence</c>, and every sequence column then dispatches to the zstd writer and
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
    /// The bytes the chooser priced this plan at, which the encoder's actual output is checked
    /// against before the plan is reused.
    /// </summary>
    /// <remarks>
    /// For an exactly priced scheme it is the formula's number; for a trial it is the encoded size
    /// the trial produced; for the plain column it is the plain column's bytes. Zero means "not
    /// priced" — a plan the reference chooser produced, or a child a scheme invented — and a memory
    /// is never formed from it.
    /// </remarks>
    internal long PredictedBytes { get; init; }

    /// <summary>Whether plan memory produced this plan without pricing the alternatives.</summary>
    internal bool FromMemory { get; init; }

    /// <summary>
    /// For a dictionary the ingest-time table priced: the table itself, which owns the codes and
    /// the entries the encoder reads directly — nothing is copied into <see cref="Codes"/> or
    /// <see cref="Gather"/>, which are then empty.
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
    /// This is the oracle the two choosers are compared through: plan against plan over the whole
    /// corpus, which catches what byte identity alone would let through — two divergences that
    /// compensate. The arrays are fingerprinted rather than printed because a codes buffer holds one
    /// entry per row, and an order-sensitive hash of them is as good a witness as the entries
    /// themselves for the question being asked, which is whether the two choosers decided the same
    /// thing.
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
/// <remarks>
/// The decision is a set of rules over three properties — run count, distinct count and range — each
/// costing at most one pass, rather than a sampling search over every scheme the edition allows.
/// The thresholds are ratios and deliberately conservative: a column that barely benefits from an
/// encoding still pays for a second array, its metadata and a decode step on every read.
/// </remarks>
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
    /// The pass answers one question — are there at most <c>length / RunEndRatio</c> runs — and the
    /// answer is settled the moment the count passes that, so it stops there rather than reading on
    /// to the last row of a column run-end has already lost.
    /// </para>
    /// <para>
    /// A bitmap answers it sixty-four rows at a time. <c>w ^ ((w &lt;&lt; 1) | previous)</c> has a
    /// set bit exactly where a row differs from the one before it, so a whole word's boundaries are
    /// one xor and a popcount, and the positions come out of the word by trailing-zero count, which
    /// only runs as often as there are boundaries. A <c>bool</c> column walked row by row instead
    /// spends its whole write inside <see cref="RowComparer.Equal"/>, two bit reads a row.
    /// </para>
    /// <para>
    /// The bitmap path takes all-valid columns only: a null row is equal to another null row and
    /// unequal to a valid one whatever the bits say, and folding the validity mask into the word
    /// walk would cost a second bitmap and its own offset. The general path below is correct for
    /// those.
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

    /// <summary>Runs by comparing adjacent rows, abandoning once run-end has already lost.</summary>
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
    /// <see cref="ComparedRuns"/> asks <see cref="RowComparer.Equal"/> once per row, and that call
    /// is two validity lookups, a zero-width test and a switch on the width before it reads
    /// anything — per row, for a column whose kind and width were settled when the comparer was
    /// built. Resolving the width once and reading at it removes all of that from the walk.
    /// </para>
    /// <para>
    /// The comparison is on raw bits, never <c>==</c> on the logical type, and that is what makes
    /// generalizing by width rather than by ptype correct. <see cref="RowComparer"/> reads a 4-byte
    /// row as <see langword="uint"/> and an 8-byte one as <see langword="ulong"/> whatever the
    /// column holds, because -0.0 and +0.0 are different values to a writer and two NaNs with the
    /// same payload are the same value. A <c>float ==</c> loop would fuse the zeros and cut every
    /// run of NaN, changing the runs — and so the bytes. Reading unsigned at the same width
    /// reproduces <see cref="RowComparer.Equal"/> exactly, which is also why a decimal stored in
    /// four bytes may take this path.
    /// </para>
    /// <para>
    /// All-valid columns only, for the same reason <see cref="BitmapRuns"/> is: "null equals null,
    /// and null equals nothing else" is not reproducible from the value bytes, whose contents under
    /// a null row are unspecified.
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
    /// The vector step skips equal stretches wholesale, which is the shape run-end is being asked
    /// about: a column worth encoding has far fewer boundaries than rows, so most of this walk is
    /// proving that a block has none. Comparing a vector of rows with the vector one row behind it
    /// answers that for the whole vector at once; a vector that holds a boundary falls through to
    /// the scalar step, which finds its exact position. The unaligned second load is the point, not
    /// an oversight.
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

            // Row 0 has no predecessor, so it seeds `previous` with its own bit. A zero seed would
            // charge a boundary at row 0 of a column whose first row is set, adding an empty
            // leading run that still decodes correctly and only shows up as a run count disagreeing
            // with the ingest pass's exact one.
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

    /// <summary>Below this many rows, a column is a candidate only if it is big in bytes.</summary>
    private const int MinimumRows = 64;

    /// <summary>
    /// ...and this is what "big in bytes" means. A row count alone is the wrong guard: what the
    /// guard is actually for is the fixed cost of a second array and its metadata, a couple of
    /// hundred bytes, so the threshold belongs on the quantity that cost is compared against. A
    /// sixteen-row column holding a megabyte of strings is worth a dictionary; a sixteen-row column
    /// of integers is not, and neither reads as "sixteen".
    /// </summary>
    private const long MinimumBytes = 1024;

    /// <summary>
    /// Column bytes below which zstd is not even priced.
    /// </summary>
    /// <remarks>
    /// A whole zstd pass over a column only to discover it loses is paid on every chunk, so a gate
    /// there must be. It is deliberately low: the float columns that need zstd most are small, and
    /// a gate set from one file's shape excludes the next file's.
    /// </remarks>
    private const long ZstdMinimumBytes = 16 * 1024;

    /// <summary>
    /// The scheme a constant column takes without being expanded, or canonical when it has to be.
    /// </summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The column chunk, constant or not.</param>
    /// <param name="target">The edition being written.</param>
    /// <param name="stats">The ingest statistics over exactly these rows, or absent.</param>
    /// <param name="cascade">What the parent knows about this child.</param>
    /// <returns>The plan, or <see cref="ColumnPlan.Canonical"/> to expand and price as usual.</returns>
    /// <remarks>
    /// Expanding a constant costs more than deciding for it. A constant node carries one element
    /// and a row count, and the writer tiles it out to <c>rows * width</c> bytes before the chooser
    /// sees it, because every scheme below reads rows; tiling it a second time to decide dominates
    /// the write of a constant column.
    /// <para>
    /// A progression is the one scheme that needs neither the rows nor a walk — the step between
    /// two equal values is zero — and it is what the writer produces for an integer constant, so
    /// deciding it here skips the expansion entirely. Everything else still expands: the guards
    /// below are the expanded path's own, in its order, so a constant this accepts is one that path
    /// would have written the same way, and one it declines takes exactly that route.
    /// </para>
    /// </remarks>
    internal static ColumnPlan ChooseConstant(
        CanonicalArena arena, int nodeIndex, VortexEdition target, in BlockStats stats,
        Cascade cascade)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Kind != CanonicalKind.Constant
            || !Allows(target, "vortex.sequence") || cascade.SequenceIsDead)
        {
            return ColumnPlan.Canonical;
        }

        DType dtype = node.DType;
        int length = node.Length;
        long expanded = ExpandedBytes(node, dtype);
        if (expanded < 0)
        {
            return ColumnPlan.Canonical;
        }

        // The small-column guard, on the bytes the expansion would have produced.
        if (length < MinimumRows && expanded < MinimumBytes)
        {
            return ColumnPlan.Canonical;
        }

        bool measured = stats.IsPresent && stats.Rows == length;
        if (dtype.Kind == DTypeKind.Primitive && dtype.PType.IsInteger()
            && !(measured && stats.DeltaKnown && stats.DeltaBroken))
        {
            SequencePlan? sequence = SequencePlan.OfConstant(node);
            if (sequence is not null)
            {
                return ColumnPlan.ForSequence(sequence) with { PredictedBytes = 0 };
            }
        }

        // One run, reached the way the expanded path reaches it: the pass counted the runs, there is
        // one of them, and inside the ratio run-end wins before anything else is priced. A run of
        // one costs a flat 32 whatever the column holds, so there is no size to reproduce either,
        // and the values child is the constant filtered down to a row, which is a constant.
        if (!Allows(target, "vortex.runend") || cascade.RunsAreDead || length / RunEndRatio < 1
            || !measured || !stats.HasRunBoundaries || stats.RunCount != 1)
        {
            return ColumnPlan.Canonical;
        }

        return ColumnPlan.Runs([0], [length]) with { PredictedBytes = 0 };
    }

    /// <summary>
    /// What <see cref="DataBytes"/> would report once <paramref name="node"/> is expanded, or -1
    /// for a constant whose expanded form this does not account for.
    /// </summary>
    /// <remarks>
    /// A constant of a fixed-width kind expands to one value per row. One of strings expands to a
    /// view per row over a single copy of the element, and a value short enough to sit inside its
    /// view leaves no copy at all.
    /// </remarks>
    private static long ExpandedBytes(CanonicalNode node, DType dtype)
    {
        long rows = node.Length;
        switch (dtype.Kind)
        {
            case DTypeKind.Primitive:
                return rows * dtype.PType.ByteWidth();

            case DTypeKind.Utf8:
            case DTypeKind.Binary:
            {
                int element = node.ConstantElement.Length;
                return (rows * Arrays.Decoders.Canonical.CanonicalSupport.ViewSize)
                    + (element <= Arrays.Decoders.Canonical.CanonicalSupport.MaxInlineViewLength
                        ? 0
                        : element);
            }

            default:
                return -1;
        }
    }

    /// <summary>Picks a scheme for the canonical node at <paramref name="nodeIndex"/>.</summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="nodeIndex">The column chunk.</param>
    /// <param name="target">
    /// The edition being written. The candidate list is derived from it before the column is read
    /// at all, which is the only arrangement that works: otherwise a scheme is elected on suitable
    /// data and the write then fails at serialization because the target does not contain its id.
    /// Failing the write is the right last-resort assertion; it must never be the nominal path.
    /// </param>
    /// <param name="stats">
    /// What the ingest pass already knows about exactly these rows. A candidate that can be priced
    /// from them is priced from them and runs no pass of its own.
    /// <para>
    /// The summary is checked against the node rather than trusted: one whose row count disagrees
    /// with the column is not this column's, and the only honest answer is then to measure, so a
    /// plumbing slip costs a pass and can never produce a wrong plan. A child of a cascade passes
    /// none, and takes that path.
    /// </para>
    /// </param>
    /// <param name="cascade">
    /// What the parent already knows about this column, when it is a child a scheme produced. Every
    /// claim it carries is a proof written out in <see cref="Cascade"/>, so a candidate it declares
    /// dead is one this method would have declined anyway: the plan is the same plan and not a byte
    /// moves. An absent cascade — the default, and what every top-level column passes — claims
    /// nothing.
    /// </param>
    /// <param name="chunk">
    /// The cursor the summary came from, for the statistics that are too big to travel inside it:
    /// the bit-width histograms, the distinct table and its codes buffer. Absent means the same
    /// thing it means everywhere else here: measure it yourself.
    /// </param>
    /// <returns>The plan; <see cref="ColumnPlan.Canonical"/> when nothing wins.</returns>
    internal static ColumnPlan Choose(
        CanonicalArena arena, int nodeIndex, VortexEdition target = EditionRegistry.Newest,
        in BlockStats stats = default, Cascade cascade = default, ChunkStats chunk = default)
    {
        // The chooser by formulas is the one whose plan is returned. It decides under the run-end
        // rule this writer applies -- run-end wins outright inside its ratio -- because that rule is
        // the one the differential proves byte-identical over the corpus; pricing run-end against
        // the other candidates changes a handful of chunks and is weighed on its own before it is
        // taken.
        DifferentialProbe? probe = Differential.Value;
        ColumnPlan plan = ChooseByFormula(
            arena, nodeIndex, target, in stats, cascade, chunk,
            runEndCompetes: probe is not null && probe.RunEndCompetes);

        // The oracle: when a test has installed a probe, the other chooser runs on the same chunk
        // with the same inputs and the two decisions are compared plan against plan, with the
        // candidate-by-candidate one standing as the reference. The probe flows with the async
        // write and nowhere else, so two tests writing at once cannot see each other's chunks.
        if (probe is not null)
        {
            ColumnPlan reference = ChooseToday(arena, nodeIndex, target, in stats, cascade, chunk);
            // A plan memory produced is marked as such: the reference prices every candidate on
            // every chunk, so where memory skipped that and reached another verdict the two differ
            // by design, and the test counts those apart from real disagreements.
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
    /// <see langword="false"/> holds the formula chooser to the rule in force — run-end wins
    /// outright once inside its ratio — and the two must then agree on every chunk of the corpus,
    /// which is the test of the harness itself. <see langword="true"/> lets run-end compete in
    /// bytes against the other candidates, and every disagreement is then a plan that rule would
    /// change, with its cost on both sides.
    /// </param>
    /// <param name="Report">Called per disagreeing chunk with the node and the two descriptions.</param>
    internal sealed record DifferentialProbe(bool RunEndCompetes, Action<int, string, string> Report);

    /// <summary>The probe in force for the current async flow, or none.</summary>
    internal static readonly AsyncLocal<DifferentialProbe?> Differential = new AsyncLocal<DifferentialProbe?>();

    /// <summary>The chooser by order: candidates in a fixed order, the first that wins returns.</summary>
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

        // First, because where it applies nothing else can beat it: an arithmetic progression goes
        // entirely into the metadata, so a `vortex.primitive` node and its whole buffer become one
        // node and about thirty bytes. There is no byte comparison to make - every other scheme
        // costs something per row and this one costs nothing.
        //
        // The ingest pass has already answered it. Its steps are exact, so a column it disqualified
        // is not offered the walk at all, and one it confirmed is built in constant time from
        // `v[1] - v[0]`. That is worth having because most columns reaching the walk really are
        // progressions, so the walk reads almost all of their rows rather than bailing early.
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

        // The comparer is built only if something will compare. Its two consumers are the run scan
        // and the dictionary probe, and a cascade that declares both dead -- a dictionary's codes,
        // a run-end's ends -- leaves nothing to compare with.
        RowComparer comparer = cascade.RunsAreDead && cascade.DictionaryIsDead
            ? default
            : new RowComparer(arena, nodeIndex);

        // One pass for runs. It is the cheaper of the two and wins outright when it wins.
        //
        // The scan's arrays are rented and sized for the worst case, which is one run per row. Two
        // `List<int>` growing by doubling would be cheapest exactly when run-end wins -- few runs,
        // few reallocations -- and most expensive when it loses, because a column with no runs at
        // all makes both lists grow to one entry per row and then throws them away; a column of
        // distinct measurements is the common case.
        //
        // The `finally` covers the scan and the verdict and nothing after: the arrays are dead the
        // moment the plan has copied the prefixes it keeps, and the schemes weighed below never see
        // them.
        //
        // The scan does not run when its answer cannot be used, and it stops as soon as the answer
        // is settled: `Allows` is tested before the pass, so an edition without `vortex.runend`
        // never pays for a count it would throw away, and a count that has passed the point where
        // run-end can win ends the walk instead of reading the rest of an interleaved column.
        //
        // It does not run at all when the answer is already known. The ingest pass counts run
        // boundaries as it reads the rows for the zone map, so the verdict is one comparison -- and
        // the two `int[length]` rentals the scan makes on every comparable column of every chunk
        // happen only for a plan that is going to be kept.
        if (Allows(target, "vortex.runend") && !cascade.RunsAreDead)
        {
            bool known = measured && stats.HasRunBoundaries;

            // One run needs no scan at all: a column whose every row is equal has exactly one run
            // and its boundaries are [0, length). Discovering that row by row -- or bit by bit, for
            // an all-null column -- is most of such a column's write, and the count already says
            // so.
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
        // building and a very good bit width. The frame of reference is the column's minimum, which
        // the ingest pass already has, so nothing here walks every row of every integer column to
        // find it. An all-null column is decided here too -- `BitPackPlan` has nothing to measure a
        // width against and says so -- so that case does not walk the rows either.
        bool integers = node.Kind == CanonicalKind.Primitive && node.PType.IsInteger();
        BitPackPlan? packed = null;
        if (Allows(target, "fastlanes.bitpacked") && !(measured && integers && !stats.HasBounds))
        {
            // The widths come from the ingest pass when it has them, and this is the only place
            // that asks. The buffer is on the stack because the answer is a small fixed set of
            // counters and its consumer returns before this frame does.
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

        // ...but "has a good bit width" is not "is the best scheme", so the bit-packing and the
        // dictionary are compared rather than ordered. Returning the bit-packed plan as soon as it
        // beats the canonical column costs bytes on a nullable integer column a dictionary handles
        // better, which patches make reachable. Both are priced in bytes; whichever is cheaper
        // wins.
        long plain = node.Kind == CanonicalKind.VarBinView
            ? PlainBinarySize(arena, node, measured ? stats.TotalBytes : -1)
            : DataBytes(node);

        // A second pass for distinct values, over a column the runs did not capture. The run count
        // bounds the distinct count from above, so this only runs when the data is genuinely
        // interleaved rather than merely repetitive -- and the budget is what the best plan so far
        // costs, so a dictionary that cannot beat the bit-packing abandons that much sooner.
        //
        // The table answers first, the walk only when it cannot. The ingest pass has already handed
        // every row of this chunk its code, in first-seen order, and owns the distinct values;
        // pricing is then arithmetic on its counts and the plan is a copy of its buffers. The walk
        // stays as the fallback for a cursor that cannot serve -- a child a scheme produced, a chunk
        // the table abandoned -- and the writer counts every such fallback through the same
        // predicate, so it cannot go quiet.
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
    /// tried under <paramref name="budget"/> as its abort bound.
    /// </summary>
    /// <remarks>
    /// Shared by both choosers, which is what makes their comparison a comparison of the
    /// exactly-priced candidates alone: whatever the formulas decide, the trials that follow are
    /// the same code under the same ceiling, so a disagreement between the two can only come from
    /// the arithmetic.
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
        // Every ceiling below is derived from this one.
        long plain = budget;

        // Last, and only for text: a high-cardinality string column defeats runs, defeats
        // dictionaries and has no frame of reference, which is precisely the case FSST exists for.
        // Tried here rather than earlier because a dictionary is cheaper to decode when it applies.
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

        // Zstd is compared with FSST rather than reached when FSST fails, and the difference is the
        // whole point: a string column FSST wins on can still be smaller as a zstd frame, so
        // running zstd only once every other scheme has declined is cheap and useless. A scheme
        // that wins is not a scheme that wins by enough.
        //
        // The cheap candidate is priced first, and that ordering changes the cost, not the outcome.
        // Pricing zstd is one pass to build the value stream and one call into the library; pricing
        // FSST is a symbol-table training run plus a compression of the whole column, and paying
        // that on a column zstd then takes throws the entire result away. Priced this way round,
        // FSST is handed the size it has to beat and stops as soon as its code stream passes it.
        //
        // What keeps it affordable in the other direction is the size gate: columns below it are
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
            // Two bars, and FSST has to clear both, so the ceiling handed down is the tighter one.
            //
            //   * its own margin against the plain form:  encoded * 10 <= plain * 9, where the
            //     plain form is the cheaper of the two serializations and not the view form. A
            //     binary column of incompressible bytes is smaller as `vortex.varbin` - four-byte
            //     offsets rather than sixteen-byte views - than as anything FSST can do with it,
            //     so comparing against the view form alone makes FSST look like a win on exactly
            //     those columns.
            //   * beating a zstd frame that priced: zstd takes the column when
            //     zstdBytes * 10 < encoded * 9, so FSST keeps it only while encoded * 9 <= zstdBytes * 10.
            //
            // Both are integer comparisons and both are turned into a ceiling on `encoded` by
            // flooring, which is exact because `encoded` is an integer. The two together are the
            // condition under which FSST takes the column when it is priced first.
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
    /// Builds a dictionary and keeps it only when it is smaller, in bytes, than the column.
    /// </summary>
    /// <remarks>
    /// The verdict is in bytes, not in a count of distinct values per row: a count ratio stands in
    /// for the byte arithmetic without matching it, and refuses by a hair columns the arithmetic
    /// says a dictionary wins by a wide margin.
    ///
    /// The budget is the cheapest plan found so far rather than the plain column, so this both
    /// decides and abandons against the real competition.
    ///
    /// The abandonment guard bounds the work: a dictionary whose entries alone already cost more
    /// than that budget can never win, whatever the rest of the rows hold.
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

        // A chained hash in three flat arrays, not a `Dictionary<int, List<int>>`: that form costs
        // one `List<int>` per distinct hash plus its own rehashing, and all of it is thrown away
        // the moment the plan is abandoned. Here `buckets[h]` is the newest code with that hash and
        // `chain[c]` the one before it, which is the same collision list with no object per bucket.
        // All three are rented, so a column that abandons allocates nothing.
        //
        // `codes` is rented for the same reason: it is written before the abandonment test can
        // fire, so allocating it would hand every column that considers a dictionary and refuses
        // one a full row vector to throw away.
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

                    // Give up as soon as the dictionary provably cannot win, against the price the
                    // plan will actually be charged rather than a weaker stand-in for it. Assuming
                    // a one-byte code per row holds only while there are at most 256 distinct
                    // values; past that the codes are wider, and a bound that ignores it walks
                    // every row of a column whose verdict was settled at the 257th distinct value.
                    //
                    // It is still a lower bound, so nothing that would have been kept is refused
                    // here and not a byte moves: `distinct` only grows, so the code width only
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
            // them past the point the rentals go back. The codes do not. There is one per row, a
            // full row vector, and the only thing the writer does with them is narrow them into an
            // arena buffer, so the rental travels with the plan and is handed back there.
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
    /// what <c>BitPackPlan.Minimum</c> computes from its own pass over every row.
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

    /// <summary>
    /// The chooser by pricing: degenerate cases from the statistics, then every exact-cost
    /// candidate priced by formula and the cheapest kept, then the trials under that cost. Built
    /// beside <see cref="ChooseToday"/> and compared with it plan against plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What differs from <see cref="ChooseToday"/> is the shape, not the arithmetic. That one
    /// returns the first candidate that wins, in a fixed order; this one prices them all and
    /// compares. On every candidate but one the two coincide: bit-packing's cost is the same
    /// <see cref="BitPackPlan"/>, the dictionary's is the same table or walk against the same
    /// budget, and the trials are the same <see cref="Trials"/>. The one that does not is run-end,
    /// which under the rule in force wins outright once inside its ratio and here can instead be
    /// priced at its ends, its values and its framing — but only when
    /// <paramref name="runEndCompetes"/> says so. Under
    /// <see langword="false"/> it keeps the rule in force and the two choosers must agree on every
    /// chunk; under <see langword="true"/> every disagreement is a chunk the other rule would
    /// encode differently, reported with both costs.
    /// </para>
    /// <para>
    /// Run-end is materialized only if it wins: the count from the ingest pass prices the candidate
    /// and the gather runs once, on the winner, instead of gathering the runs of every column
    /// inside the ratio before anything else has been priced. The values child is priced as the
    /// runs' share of the plain column, which is exact for a fixed-width kind and an estimate for
    /// strings, whose run-end values are a gather nobody has made at that point.
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

        // 1. Degenerate cases first, and before memory: a progression costs nothing per row and
        // nothing can beat it, and the statistics answer it without a walk, so plan memory has
        // nothing to save by standing in front of it. Letting it stand there writes a remembered
        // bit-packing over every progression that follows the one chunk with a jump in it: a buffer
        // per chunk where the metadata alone would have been exact.
        ColumnPlan progression = SequenceOf(arena, node, target, in stats, cascade, measured);
        if (progression.Scheme != ColumnScheme.None)
        {
            return progression;
        }

        RowComparer comparer = cascade.RunsAreDead && cascade.DictionaryIsDead
            ? default
            : new RowComparer(arena, nodeIndex);

        // 2. Run-end from the pass, under the rule in force -- inside its ratio it wins before
        // anything else is priced -- and before memory for the same reason as the progression: a
        // count the pass took makes the verdict arithmetic, and a constant chunk reached through a
        // remembered bit-packing goes out as a zero-width packing with its framing where one run
        // costs a flat 32. A count the pass did not take is walked further down, after memory has
        // had its say.
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

        // 3. Plan memory: a column whose last plan produced the bytes it was priced at, within a
        // small tolerance, is offered that plan again -- re-priced on this chunk's own statistics,
        // which is arithmetic -- and nothing else is priced. Only a plan that still wins on its own
        // terms is reused; one that has stopped winning sends the column back to full pricing, as
        // does a column without a memory. A child a scheme invented has no cursor and so no memory,
        // and a chunk the pass did not measure is not trusted with one. What memory skips is
        // exactly what costs: the walks below and the trials at the end, never a candidate the
        // statistics have already answered above.
        if (measured && chunk.Memory is { WithinTolerance: true } memory)
        {
            ColumnPlan remembered = Reprice(
                memory.Scheme, arena, nodeIndex, node, target, in stats, cascade, chunk, plain);
            if (remembered.Scheme == memory.Scheme)
            {
                return remembered with { FromMemory = true };
            }
        }

        // 4. The candidates whose cost is exact, each priced, the cheapest kept as `best`.
        long best = plain;
        ColumnScheme bestScheme = ColumnScheme.None;

        // Run-end, when the pass did not count it: walked here, because nothing else can price it;
        // the gather still waits for the verdict.
        if (runEndAllowed && !runsCounted)
        {
            walkedRuns = TryRuns(arena, in node, in comparer, length);
            runs = walkedRuns.Scheme == ColumnScheme.None ? long.MaxValue : walkedRuns.Codes.Length;
            runEndCost = RunEndCostOf(runs, length, plain);
            if (!runEndCompetes && runEndCost != long.MaxValue)
            {
                // The rule in force: inside the ratio, run-end wins before anything else is priced.
                return MaterializeRuns(arena, in node, in comparer, length, runs, in walkedRuns, runEndCost);
            }
        }

        // Bit-packing: exact, patches included. The frame of reference's histogram comes from the
        // ingest pass when the reference is zero, and from `BitPackPlan`'s own walk otherwise.
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

        // An exactly priced winner stands, and the trials are not offered the column. Running the
        // trials under the best cost in hand instead would let a zstd frame take a column a
        // bit-packing already holds -- smaller on disk, slower to decode, and different bytes -- so
        // a trial is offered only the columns no exactly priced scheme took, under the plain
        // column's bytes as its ceiling.
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
    /// <see cref="long.MaxValue"/> outside its ratio: a flat 32 for a constant, and the ends plus
    /// the values plus a framing allowance otherwise, the values priced as the runs' share of the
    /// plain column.
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
    /// Prices one remembered scheme on this chunk, and nothing else: the plan when it still wins on
    /// its own terms, canonical (with the plain bytes) when it has stopped winning or never applied.
    /// </summary>
    /// <remarks>
    /// The verdict is "still wins" against the plain column alone, not against the field, because
    /// the field is exactly what memory exists to not price. A remembered scheme that has stopped
    /// winning returns a plan of another scheme, which the caller reads as "back to full pricing";
    /// a remembered canonical is the cheapest case of all, and the one the distinct table is turned
    /// off for.
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
    /// The final test is the same expression, on the same numbers. The walk prices
    /// <c>rows × codeWidth(entries) + EntriesSize(entries)</c> against the budget once it has seen
    /// every row; its early abandonment is a lower bound of that test and never fires on a plan the
    /// test would have kept. So the plan here is the plan there, and what changes is that no row is
    /// read to reach it.
    /// <para>
    /// The entries are the count at the last block's close, not the table's current count: the
    /// table has since probed the tail the writer will carry into the next chunk, and codes are
    /// handed out in first-seen order, so the chunk's rows use exactly the codes below that count
    /// and its heap bytes are exactly the heap at that moment.
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

        // No copy: the plan points at the table, and the encoder reads the codes and lays out the
        // entries from it directly. The table lives until the segment is written, which is after
        // the plan has been consumed.
        return ColumnPlan.Dictionary([], []) with
        {
            Table = table,
            Entries = entries,
            Rows = length,
            PredictedBytes = encoded,
        };
    }

    /// <summary>
    /// The bytes the dictionary layer was priced at, over what the encoder built: the codes at the
    /// narrowest width that indexes <paramref name="values"/>, plus the entries in the form
    /// <see cref="EntriesSize(CanonicalArena, CanonicalNode, ReadOnlySpan{int})"/> prices them.
    /// </summary>
    /// <remarks>
    /// This is what plan memory holds a dictionary to, and not the bytes its subtree produced. The
    /// prediction is codes plus entries; the children then take their own schemes -- the codes
    /// zstd, the values FSST or zstd -- and the buffers they append are a fraction of that.
    /// Comparing the prediction against the subtree instead breaks the memory on every chunk of a
    /// dictionary column, which then walks for a dictionary the table has already built. What the
    /// children make of the layer is theirs; the dictionary's own decision is what is checked.
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
    /// <paramref name="count"/> rows of <paramref name="node"/> in order -- a node that is itself
    /// the entries.
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

    /// <summary>Whether a canonical form has a row equality this compressor can compute.</summary>
    private static bool IsComparable(CanonicalKind kind) =>
        kind is CanonicalKind.Primitive or CanonicalKind.Bool or CanonicalKind.VarBinView
            or CanonicalKind.Decimal;

    /// <summary>
    /// Row equality and hashing over one canonical column.
    /// </summary>
    /// <remarks>
    /// Nullness is part of the value. Two null rows are equal and a null is equal to nothing else,
    /// which is what makes a run of nulls one run and a dictionary hold at most one null entry.
    /// This is not the filter's three-valued logic -- that answers "does this row match a
    /// predicate" and this answers "are these two rows the same value", and conflating them would
    /// make a run of nulls unrepresentable.
    ///
    /// Floats are compared by their raw bits: -0.0 and +0.0 are different values to a writer even
    /// though they compare equal, and collapsing them into one dictionary entry would change the
    /// data.
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
        /// The kind and the width are properties of the column, so they are resolved once in the
        /// constructor rather than re-derived per call — a `switch` on the kind, then
        /// `PType.ByteWidth()` or `DecimalStorage.ByteWidth()`. `Equal` and `Hash` are called once
        /// or more per row of every column of the file, so what is left of them is what the write
        /// costs; a single width test is something the compiler can fold away.
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

            // The zero-width kinds first, and not as a style choice: `CanonicalNode.Values` throws
            // on a Bool or a VarBinView, so the span may not be taken before the width has ruled
            // them out.
            if (_width == 0)
            {
                return _node.Kind == CanonicalKind.Bool
                    ? CanonicalSupport.BitAt(_node.Bits.Span, _node.BitOffset + a) ==
                      CanonicalSupport.BitAt(_node.Bits.Span, _node.BitOffset + b)
                    : Bytes(a).SequenceEqual(Bytes(b));
            }

            // The widths that are one load are read as one load. `SequenceEqual` over four bytes is
            // a call with a length check in front of it, and a handful of bytes is what the average
            // row actually is.
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
        /// It need not agree with the byte-wise hash, and does not. Nothing persists this hash: it
        /// picks a bucket, collisions are settled by <see cref="Equal"/>, and a dictionary code is
        /// handed out by order of first appearance, so the file's bytes do not depend on it at all.
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
        /// what a value hashes to.
        /// </summary>
        /// <param name="bytes">The value's bytes.</param>
        private static int Hash(ReadOnlySpan<byte> bytes) => (int)KeyHash.Bytes(bytes);
    }
}
