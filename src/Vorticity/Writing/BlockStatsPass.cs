// The fused pass of docs/11-write-strategy.md §3.2, at the size stage 1 of its §8 asks for: one
// loop per (column, block) that produces everything the zone map needs, run on the batch's own
// arena while the decode that produced it is still in cache.
//
// THREE THINGS IT DOES THAT `ZoneStatistics` DID NOT:
//
//   * IT RUNS AT INGEST, over a row RANGE of the batch, so a block is summarized from the batches
//     that cover it rather than from the chunk it lands in. That is what decouples the zone map from
//     the chunk shape.
//   * IT RESOLVES THE PHYSICAL TYPE ONCE, before the loop, into a generic instantiation the JIT
//     monomorphizes - the form of R7, W-9 and W-33. `ZoneStatistics` called `ReadInteger` per row
//     and paid a switch on `PType` for every value; those are the two calls PerRowDispatchTests
//     charged it under W-12.
//   * IT COUNTS NULLS BY WORDS. `BitmapKernels.CountSet` popcounts the range; the old loop asked
//     `IsValid` once per row to add up the same number.
//
// A CONSTANT BLOCK IS ANSWERED WITHOUT READING A VALUE. The canonical constant form (PERF-AUDIT-v2
// Z1b) holds one element and a count, and its bounds are that element whatever the row count; going
// through `node.Values` here would materialize the very column the form exists to not build. The
// emitted chunk still materializes today - `ArrayBlobWriter.Materialize` does it at the boundary -
// but the summary no longer forces it.
using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Writing;

/// <summary>Fills a <see cref="BlockStats"/> from a row range of a canonical column.</summary>
internal static class BlockStatsPass
{
    /// <summary>
    /// Folds rows <c>[start, start + count)</c> of <paramref name="nodeIndex"/> into
    /// <paramref name="stats"/>.
    /// </summary>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="start">First row of the range, inside the node.</param>
    /// <param name="count">How many rows; zero is a no-op.</param>
    /// <param name="stats">The block's accumulator, continued rather than replaced.</param>
    /// <param name="previous">
    /// The column's last row, so that a boundary at the range's first row is decided rather than
    /// guessed. Updated to this range's last row before returning.
    /// </param>
    /// <param name="widths">
    /// The block's pair of bit-width histograms (<see cref="BitPackWidths"/>) when the caller keeps
    /// them, empty otherwise. Filled for an integer primitive whose values this range actually
    /// reads; every other path sets <see cref="BlockStats.WidthsBroken"/> instead, because a
    /// histogram missing some of its rows is worse than no histogram at all.
    /// </param>
    internal static void Accumulate(
        CanonicalArena arena, int nodeIndex, int start, int count, ref BlockStats stats,
        PreviousRow? previous = null, Span<int> widths = default)
    {
        if (count <= 0)
        {
            return;
        }

        CanonicalNode node = arena.GetNode(nodeIndex);
        ValidityMask mask = ValidityMask.From(arena, node.Validity);

        bool startsBlock = !stats.IsPresent;
        stats.Rows += count;
        int valid = Nulls(in mask, start, count, ref stats);

        // BROKEN UNTIL THIS RANGE PROVES OTHERWISE. Every kind but an integer primitive contributes
        // nothing to the width histograms, and so does an integer range the progression
        // short-circuit answers without reading a value; the one path that does contribute restores
        // the flag to what the ranges before it had left it.
        bool widthsBefore = stats.WidthsBroken;
        stats.WidthsBroken = true;

        switch (node.Kind)
        {
            case CanonicalKind.Constant:
                // One element, `count` rows, and its dtype is Primitive by construction
                // (ConstantCanonicalizer builds the form for no other kind). A block of it is
                // bounded by the element itself as soon as one row of it is valid.
                stats.IsSummarizable = true;
                if (valid > 0)
                {
                    Element(node.DType.PType, node.ConstantElement, ref stats);
                }

                // Every row holds the same value, so the only boundaries are where validity turns
                // over -- which is why this is answered without reading a value per row.
                ConstantRuns(node, in mask, start, count, valid, startsBlock, ref stats, previous);
                return;

            case CanonicalKind.Primitive:
            {
                stats.IsSummarizable = true;

                // THE STEPS ARE TAKEN FIRST, and while `previous` still holds the row before this
                // range: `FixedRuns` overwrites it with the range's last row.
                //
                // A PROGRESSION ANSWERS THE OTHER TWO WITHOUT READING A VALUE. If every pair in this
                // range climbs by the same step, the range is monotone -- a wrap would have made one
                // step differ from the others -- so its extremes are its ENDPOINTS, and its runs are
                // one if the step is zero and one per row otherwise. That leaves a progression
                // costing ONE walk instead of three, which is what `ext` -- a column of timestamps,
                // ten times faster than the reference -- was paying the tree for.
                bool progression = false;
                if (node.PType.IsInteger())
                {
                    bool wasProgression = !stats.DeltaBroken;
                    bool hadPrevious = previous is not null && previous.HasValue;
                    Deltas(node, in mask, start, count, valid, ref stats, previous);

                    if (wasProgression && stats.DeltaKnown && !stats.DeltaBroken)
                    {
                        progression = true;
                        ProgressionBounds(node, stats.Delta, start, count, ref stats);
                        ProgressionRuns(
                            node, stats.Delta, start, count, hadPrevious, startsBlock, ref stats,
                            previous);
                    }
                }

                // THE WIDTHS ARE COUNTED HERE OR NOWHERE. A progression is answered from its
                // endpoints, so its values are never loaded and there is nothing to histogram --
                // and nothing that needs one either, since `vortex.sequence` claims the column
                // before bit-packing is offered it. What matters is that the block SAYS so, because
                // a later range may break the progression and fill half a histogram.
                Span<int> counts = !progression && node.PType.IsInteger() ? widths : default;
                if (!counts.IsEmpty)
                {
                    stats.WidthsBroken = widthsBefore;

                    // A null row PACKS as zero, which is width zero in both domains. Counting it
                    // here rather than in the loops keeps the typed walk free of the branch, and it
                    // is the rule `BitPackPlan` has always applied.
                    counts[0] += count - valid;
                    counts[BitPackWidths.ZigZagOffset] += count - valid;
                }

                if (!progression)
                {
                    if (valid > 0)
                    {
                        Values(node, in mask, start, count, ref stats, counts);
                    }

                    FixedRuns(
                        node.Values.Span, node.PType.ByteWidth(), in mask, start, count, startsBlock,
                        ref stats, previous);
                }

                return;
            }

            case CanonicalKind.Decimal:
                // No bounds: a decimal's comparison domain is outside this iteration's kernels. Its
                // rows still compare byte for byte, which is all run-end needs.
                FixedRuns(
                    node.Values.Span, DecimalStorage.ByteWidth(node.Storage), in mask, start, count,
                    startsBlock, ref stats, previous);
                return;

            case CanonicalKind.Bool:
                BoolRuns(node, in mask, start, count, startsBlock, ref stats, previous);
                return;

            case CanonicalKind.VarBinView:
                // No bounds: utf8 and binary have a perfectly good lexicographic min/max and no
                // place to put it without a second varbinview per zone. Their BYTES, on the other
                // hand, are what `ColumnCompressor.PlainBinarySize` walked the views to add up, once
                // per column, to price the varbin form against the view form -- so the sum is taken
                // here, where the views are in cache and the pass is already reading them.
                if (valid > 0)
                {
                    stats.TotalBytes += ViewBytes(node, in mask, start, count, valid);
                }

                ViewRuns(node, in mask, start, count, startsBlock, ref stats, previous);
                return;

            default:
                // Every other kind has no scalar bound at all, and still gets a zone map with the
                // null count alone, which is what IS NULL pruning runs on.
                return;
        }
    }

    // -------------------------------------------------------------------------------------- steps
    //
    // `vortex.sequence` is the one encoding that costs NOTHING per row: a column that is an
    // arithmetic progression becomes one node and about thirty bytes. `SequencePlan` found out by
    // walking the rows, and W-9 measured that walk reading **78 % of the rows it is offered** --
    // 1 352 columns of 1 728 really are sequences, so it runs to the end rather than bailing at row
    // three -- for 26 % of a `sequence` write and 25 % of a `primitive` one.
    //
    // The walk is here now, folded into the pass that was already reading the values. It stops the
    // moment two steps disagree, so a column that is not a progression pays a compare per row until
    // row three and nothing after: a branch the predictor gets right every time.
    //
    // ONLY THE FACT IS KEPT, NOT THE PLAN. When the steps agree, the step itself is `v[1] - v[0]`,
    // which `SequencePlan` reads from the node in constant time; there is nothing to carry.

    /// <summary>The bounds of a progression: its first and last rows, in step order.</summary>
    /// <remarks>
    /// Exact because a range whose steps all agree is MONOTONE. It cannot wrap: a wrap would make
    /// one step differ from the others in the widened arithmetic <see cref="Deltas"/> uses, and the
    /// range would not be a progression at all.
    /// </remarks>
    private static void ProgressionBounds(
        CanonicalNode node, long step, int start, int count, ref BlockStats stats)
    {
        PType ptype = node.PType;
        int width = ptype.ByteWidth();
        ReadOnlySpan<byte> values = node.Values.Span;
        ReadOnlySpan<byte> first = values.Slice(start * width, width);
        ReadOnlySpan<byte> last = values.Slice((start + count - 1) * width, width);
        ReadOnlySpan<byte> low = step < 0 ? last : first;
        ReadOnlySpan<byte> high = step < 0 ? first : last;

        if (ptype.IsSignedInteger())
        {
            stats.MergeSigned(ElementSigned(low, ptype), ElementSigned(high, ptype));
            return;
        }

        stats.MergeUnsigned(ElementUnsigned(low, ptype), ElementUnsigned(high, ptype));
    }

    /// <summary>
    /// The run boundaries of a range that is an arithmetic progression, without reading a value.
    /// </summary>
    /// <remarks>
    /// A step of zero makes every row equal — one run, no boundary. Any other step makes every row
    /// differ from the one before it, so every row of the range starts a run, the first one included
    /// when there is a row before it to differ from. Both are exactly what a walk would have
    /// counted, and neither needs one.
    /// </remarks>
    private static void ProgressionRuns(
        CanonicalNode node, long step, int start, int count, bool hadPrevious, bool startsBlock,
        ref BlockStats stats, PreviousRow? previous)
    {
        stats.HasRunBoundaries = true;

        if (step != 0)
        {
            stats.RunBoundaries += (count - 1) + (hadPrevious ? 1 : 0);
            if (startsBlock && hadPrevious)
            {
                stats.FirstRowStartsRun = true;
            }
        }

        int width = node.PType.ByteWidth();
        Store(previous, valid: true, node.Values.Span.Slice((start + count - 1) * width, width));
    }

    /// <summary>Folds this range's steps into <paramref name="stats"/>.</summary>
    private static void Deltas(
        CanonicalNode node, in ValidityMask mask, int start, int count, int valid,
        ref BlockStats stats, PreviousRow? previous)
    {
        if (stats.DeltaBroken)
        {
            return;
        }

        // A null has no value to step from or to, and `vortex.sequence` has no children to put a
        // validity bitmap in -- one null row disqualifies the whole column.
        if (valid != count)
        {
            stats.BreakDelta();
            return;
        }

        if (previous is not null && previous.HasValue && previous.IsNull)
        {
            stats.BreakDelta();
            return;
        }

        // The physical type is resolved ONCE, into a generic instantiation, exactly as W-9 resolved
        // `SequencePlan`'s own walk: the alternative is two switches per row for a property of the
        // call. Narrow types step in `long` because the difference of two 32-bit values always fits
        // in one; only 64-bit columns need the wider arithmetic, and they get their own loop.
        ReadOnlySpan<byte> values = node.Values.Span;
        ReadOnlySpan<byte> seed = previous is not null && previous.HasValue ? previous.Bytes : default;
        switch (node.PType)
        {
            case PType.I8: Narrow<sbyte>(values, seed, start, count, ref stats); return;
            case PType.I16: Narrow<short>(values, seed, start, count, ref stats); return;
            case PType.I32: Narrow<int>(values, seed, start, count, ref stats); return;
            case PType.U8: Narrow<byte>(values, seed, start, count, ref stats); return;
            case PType.U16: Narrow<ushort>(values, seed, start, count, ref stats); return;
            case PType.U32: Narrow<uint>(values, seed, start, count, ref stats); return;
            case PType.I64: Wide<long>(values, seed, start, count, ref stats); return;
            default: Wide<ulong>(values, seed, start, count, ref stats); return;
        }
    }

    /// <summary>Steps of a column narrower than 64 bits, where a difference always fits a long.</summary>
    /// <remarks>
    /// THE STATE IS IN LOCALS AND WRITTEN BACK ONCE. The first version called a method on the
    /// accumulator and re-read one of its fields for every row, which put a call and two branches
    /// inside the hot loop and made this pass MORE expensive than the `SequencePlan` walk it
    /// replaces: `sequence` 0,16 -&gt; 0,22 and `ext` 0,098 -&gt; 0,12, measured. In this shape the
    /// steady state is one subtract and one compare, which is what the walk it replaces costs.
    /// </remarks>
    private static void Narrow<T>(
        ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> seed, int start, int count, ref BlockStats stats)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes);
        long step = stats.Delta;
        bool known = stats.DeltaKnown;
        long last;
        int first;

        if (seed.IsEmpty)
        {
            last = long.CreateTruncating(values[start]);
            first = 1;
        }
        else
        {
            last = long.CreateTruncating(MemoryMarshal.Read<T>(seed));
            first = 0;
        }

        for (int i = first; i < count; i++)
        {
            long value = long.CreateTruncating(values[start + i]);
            long delta = value - last;
            if (known)
            {
                if (delta != step)
                {
                    stats.BreakDelta();
                    return;
                }
            }
            else
            {
                step = delta;
                known = true;
            }

            last = value;
        }

        if (known)
        {
            stats.SetDelta(step);
        }
    }

    /// <summary>
    /// Steps of a 64-bit column, subtracted in <see cref="Int128"/> because the difference of two
    /// 64-bit values does not fit in 64 bits in general — the reason `SequencePlan` holds one.
    /// </summary>
    /// <remarks>
    /// A step outside <see cref="long"/> is not a step a constant progression can have over three
    /// rows or more: the values would have to wrap, and a wrapped difference is a different number.
    /// It ends the progression rather than being carried.
    /// </remarks>
    private static void Wide<T>(
        ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> seed, int start, int count, ref BlockStats stats)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes);
        Int128 step = stats.Delta;
        bool known = stats.DeltaKnown;
        Int128 last;
        int first;

        if (seed.IsEmpty)
        {
            last = Int128.CreateTruncating(values[start]);
            first = 1;
        }
        else
        {
            last = Int128.CreateTruncating(MemoryMarshal.Read<T>(seed));
            first = 0;
        }

        for (int i = first; i < count; i++)
        {
            Int128 value = Int128.CreateTruncating(values[start + i]);
            Int128 delta = value - last;
            if (known)
            {
                if (delta != step)
                {
                    stats.BreakDelta();
                    return;
                }
            }
            else
            {
                if (delta < long.MinValue || delta > long.MaxValue)
                {
                    stats.BreakDelta();
                    return;
                }

                step = delta;
                known = true;
            }

            last = value;
        }

        if (known)
        {
            stats.SetDelta((long)step);
        }
    }

    // ------------------------------------------------------------------------------ run boundaries
    //
    // WHAT THESE COUNT, AND WHY IT HAS TO BE EXACT. `ColumnCompressor` declines run-end when a chunk
    // has more than `rows / 4` runs, so the verdict turns on a single comparison: a count that is
    // off by one changes the plan and therefore the file's bytes. The rule reproduced here is
    // `RowComparer.Equal`'s, in full -- two nulls are equal, a null and a value are not, two values
    // are equal byte for byte -- and `RunEndPlanTests` compares the two counts over the corpus.
    //
    // A boundary belongs to the row that STARTS the new run, so it is counted in that row's block
    // whatever batch it arrived in; the row before it may be in another batch, on an arena the scan
    // has already reset, which is what `PreviousRow` is for.

    /// <summary>Records whether the range's first row starts a run, given what came before.</summary>
    private static void Leading(
        bool differs, bool startsBlock, ref BlockStats stats, PreviousRow? previous)
    {
        // The file's very first row has no predecessor: it starts the first run rather than a
        // boundary, and `RunCount` adds that one back.
        if (previous is null || !previous.HasValue)
        {
            return;
        }

        if (differs)
        {
            stats.RunBoundaries++;
        }

        if (startsBlock)
        {
            stats.FirstRowStartsRun = differs;
        }
    }

    /// <summary>Fixed-width rows: primitives at their ptype's width, decimals at their storage's.</summary>
    private static void FixedRuns(
        ReadOnlySpan<byte> values, int width, in ValidityMask mask, int start, int count,
        bool startsBlock, ref BlockStats stats, PreviousRow? previous)
    {
        stats.HasRunBoundaries = true;

        bool firstValid = mask.IsValid(start);
        ReadOnlySpan<byte> first = firstValid ? values.Slice(start * width, width) : default;
        Leading(
            previous is not null && previous.HasValue && !previous.Equals(firstValid, first),
            startsBlock, ref stats, previous);

        if (count > 1)
        {
            stats.RunBoundaries += mask.AllValid || mask.AllInvalid
                ? Interior(values, width, mask.AllInvalid, start, count)
                : InteriorNullable(values, width, in mask, start, count);
        }

        bool lastValid = mask.IsValid(start + count - 1);
        Store(previous, lastValid, lastValid ? values.Slice((start + count - 1) * width, width) : default);
    }

    /// <summary>The uniform-validity case: either every row is a value, or none is.</summary>
    private static long Interior(
        ReadOnlySpan<byte> values, int width, bool allInvalid, int start, int count)
    {
        // Every row null means every row equal: no boundary can be inside the range.
        if (allInvalid)
        {
            return 0;
        }

        ReadOnlySpan<byte> window = values.Slice(start * width, count * width);
        return width switch
        {
            1 => Differing(MemoryMarshal.Cast<byte, byte>(window)),
            2 => Differing(MemoryMarshal.Cast<byte, ushort>(window)),
            4 => Differing(MemoryMarshal.Cast<byte, uint>(window)),
            8 => Differing(MemoryMarshal.Cast<byte, ulong>(window)),
            _ => DifferingWide(window, width),
        };
    }

    /// <summary>Adjacent rows that differ, with the element width resolved into the loop.</summary>
    private static long Differing<T>(ReadOnlySpan<T> values)
        where T : unmanaged, IEquatable<T>
    {
        long boundaries = 0;
        for (int i = 1; i < values.Length; i++)
        {
            if (!values[i].Equals(values[i - 1]))
            {
                boundaries++;
            }
        }

        return boundaries;
    }

    /// <summary>The same for 16- and 32-byte decimals, which no primitive width covers.</summary>
    private static long DifferingWide(ReadOnlySpan<byte> window, int width)
    {
        long boundaries = 0;
        for (int offset = width; offset < window.Length; offset += width)
        {
            if (!window.Slice(offset, width).SequenceEqual(window.Slice(offset - width, width)))
            {
                boundaries++;
            }
        }

        return boundaries;
    }

    /// <summary>The mixed case: validity decides first, and only then the bytes.</summary>
    /// <remarks>
    /// The width is resolved before the loop, exactly as in the uniform case: a
    /// <see cref="MemoryExtensions.SequenceEqual{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/> per row over
    /// four bytes is a call with a length check in front of it, which is what W-7b measured on the
    /// comparer this replaces.
    /// </remarks>
    private static long InteriorNullable(
        ReadOnlySpan<byte> values, int width, in ValidityMask mask, int start, int count)
    {
        ReadOnlySpan<byte> window = values.Slice(start * width, count * width);
        return width switch
        {
            1 => DifferingNullable(MemoryMarshal.Cast<byte, byte>(window), in mask, start, count),
            2 => DifferingNullable(MemoryMarshal.Cast<byte, ushort>(window), in mask, start, count),
            4 => DifferingNullable(MemoryMarshal.Cast<byte, uint>(window), in mask, start, count),
            8 => DifferingNullable(MemoryMarshal.Cast<byte, ulong>(window), in mask, start, count),
            _ => DifferingWideNullable(window, width, in mask, start, count),
        };
    }

    private static long DifferingNullable<T>(
        ReadOnlySpan<T> values, in ValidityMask mask, int start, int count)
        where T : unmanaged, IEquatable<T>
    {
        long boundaries = 0;
        bool previousValid = mask.IsValid(start);
        for (int i = 1; i < count; i++)
        {
            bool valid = mask.IsValid(start + i);
            if (valid != previousValid)
            {
                boundaries++;
            }
            else if (valid && !values[i].Equals(values[i - 1]))
            {
                boundaries++;
            }

            previousValid = valid;
        }

        return boundaries;
    }

    private static long DifferingWideNullable(
        ReadOnlySpan<byte> window, int width, in ValidityMask mask, int start, int count)
    {
        long boundaries = 0;
        bool previousValid = mask.IsValid(start);
        for (int i = 1; i < count; i++)
        {
            bool valid = mask.IsValid(start + i);
            if (valid != previousValid)
            {
                boundaries++;
            }
            else if (valid && !window.Slice(i * width, width)
                         .SequenceEqual(window.Slice((i - 1) * width, width)))
            {
                boundaries++;
            }

            previousValid = valid;
        }

        return boundaries;
    }

    /// <summary>One element repeated: only a change of validity can start a run.</summary>
    private static void ConstantRuns(
        CanonicalNode node, in ValidityMask mask, int start, int count, int valid, bool startsBlock,
        ref BlockStats stats, PreviousRow? previous)
    {
        stats.HasRunBoundaries = true;
        ReadOnlySpan<byte> element = node.ConstantElement;

        bool firstValid = mask.IsValid(start);
        Leading(
            previous is not null && previous.HasValue
                && !previous.Equals(firstValid, firstValid ? element : default),
            startsBlock, ref stats, previous);

        // Uniform validity: one value, one run. Otherwise every turnover of the bitmap is a
        // boundary, and there is still no value to read.
        if (valid != 0 && valid != count)
        {
            bool previousValid = firstValid;
            for (int i = 1; i < count; i++)
            {
                bool rowValid = mask.IsValid(start + i);
                if (rowValid != previousValid)
                {
                    stats.RunBoundaries++;
                }

                previousValid = rowValid;
            }
        }

        bool lastValid = mask.IsValid(start + count - 1);
        Store(previous, lastValid, lastValid ? element : default);
    }

    /// <summary>Booleans, sixty-four rows at a time.</summary>
    /// <remarks>
    /// THE WORD TRICK OF W-33, WHICH THIS PASS MUST NOT LOSE. <c>w ^ ((w &lt;&lt; 1) | previous)</c>
    /// has a set bit exactly where a row differs from the one before it, so a whole word's
    /// boundaries are one xor and a popcount. The bit-at-a-time version of this function cost
    /// **1,52 against a 0,78 reference** on the 1M `bool` file — measured, and the reason the trick
    /// is here rather than in a later SIMD stage.
    /// <para>
    /// Validity folds into the same words. A boundary is a change of validity, or a change of value
    /// between two rows that are both valid: <c>(V ^ V₋₁) | (V &amp; V₋₁ &amp; (B ^ B₋₁))</c>, whose
    /// two terms are disjoint by construction, so the popcount of the union is the count.
    /// </para>
    /// </remarks>
    private static void BoolRuns(
        CanonicalNode node, in ValidityMask mask, int start, int count, bool startsBlock,
        ref BlockStats stats, PreviousRow? previous)
    {
        stats.HasRunBoundaries = true;
        ReadOnlySpan<byte> bits = node.Bits.Span;
        int offset = node.BitOffset;

        Span<byte> one = stackalloc byte[1];
        bool firstValid = mask.IsValid(start);
        bool firstBit = firstValid && CanonicalSupport.BitAt(bits, offset + start);
        one[0] = (byte)(firstBit ? 1 : 0);
        Leading(
            previous is not null && previous.HasValue
                && !previous.Equals(firstValid, firstValid ? one : default),
            startsBlock, ref stats, previous);

        if (count > 1 && !mask.AllInvalid)
        {
            stats.RunBoundaries += mask.AllValid
                ? Transitions(bits, offset + start, count)
                : ValidTransitions(bits, offset + start, mask, start, count);
        }

        int last = start + count - 1;
        bool lastValid = mask.IsValid(last);
        one[0] = (byte)(lastValid && CanonicalSupport.BitAt(bits, offset + last) ? 1 : 0);
        Store(previous, lastValid, lastValid ? one : default);
    }

    /// <summary>
    /// Bits of <paramref name="count"/> rows that differ from the row before them, the first
    /// excluded.
    /// </summary>
    private static long Transitions(ReadOnlySpan<byte> bits, int firstBit, int count)
    {
        long boundaries = 0;
        ulong carry = 0;
        bool seeded = false;

        for (int row = 0; row < count; row += 64)
        {
            int take = Math.Min(64, count - row);
            ulong word = BitWords.Load(bits, firstBit + row) & BitWords.Mask(take);

            // The range's own first row has no predecessor HERE -- `Leading` already decided it
            // against the previous batch -- so the first word is seeded with its own bit 0 and
            // contributes nothing for it.
            if (!seeded)
            {
                carry = word & 1;
                seeded = true;
            }

            ulong differs = (word ^ ((word << 1) | carry)) & BitWords.Mask(take);
            boundaries += BitOperations.PopCount(differs);
            carry = (word >> (take - 1)) & 1;
        }

        return boundaries;
    }

    /// <summary>The same, with a validity bitmap deciding which comparisons happen at all.</summary>
    private static long ValidTransitions(
        ReadOnlySpan<byte> bits, int firstBit, in ValidityMask mask, int start, int count)
    {
        ReadOnlySpan<byte> validity = mask.Bits;
        int firstValidBit = mask.BitOffset + start;

        long boundaries = 0;
        ulong valueCarry = 0;
        ulong validCarry = 0;
        bool seeded = false;

        for (int row = 0; row < count; row += 64)
        {
            int take = Math.Min(64, count - row);
            ulong span = BitWords.Mask(take);
            ulong word = BitWords.Load(bits, firstBit + row) & span;
            ulong live = BitWords.Load(validity, firstValidBit + row) & span;

            if (!seeded)
            {
                valueCarry = word & 1;
                validCarry = live & 1;
                seeded = true;
            }

            ulong livePrevious = ((live << 1) | validCarry) & span;
            ulong changed = (live ^ livePrevious) & span;
            ulong both = live & livePrevious;
            ulong valueChanged = (word ^ ((word << 1) | valueCarry)) & span;
            boundaries += BitOperations.PopCount(changed | (both & valueChanged));

            valueCarry = (word >> (take - 1)) & 1;
            validCarry = (live >> (take - 1)) & 1;
        }

        return boundaries;
    }

    /// <summary>
    /// Strings and binaries, compared by their views first and by their bytes only when they must
    /// be.
    /// </summary>
    /// <remarks>
    /// Equal views prove equal values. When the views differ and the LENGTHS agree the bytes are
    /// compared, so that two equal out-of-line strings at different offsets — the common case after
    /// a compaction — do not split a run (docs/11-write-strategy.md §3.2.4). Different lengths are
    /// different values and need no comparison at all.
    /// </remarks>
    private static void ViewRuns(
        CanonicalNode node, in ValidityMask mask, int start, int count, bool startsBlock,
        ref BlockStats stats, PreviousRow? previous)
    {
        stats.HasRunBoundaries = true;
        ReadOnlySpan<byte> views = node.Views.Span;

        bool firstValid = mask.IsValid(start);
        Leading(
            previous is not null && previous.HasValue
                && !previous.Equals(firstValid, firstValid ? Value(node, views, start) : default),
            startsBlock, ref stats, previous);

        // A view is sixteen bytes: two 64-bit loads and two compares, never a `SequenceEqual` call.
        ReadOnlySpan<ulong> pairs = MemoryMarshal.Cast<byte, ulong>(views);
        bool allValid = mask.AllValid;
        bool previousValid = firstValid;
        for (int i = 1; i < count; i++)
        {
            int row = start + i;
            bool valid = allValid || mask.IsValid(row);
            if (!allValid && valid != previousValid)
            {
                stats.RunBoundaries++;
                previousValid = valid;
                continue;
            }

            previousValid = valid;
            if (!valid)
            {
                continue;
            }

            int a = (row - 1) * 2;
            int b = row * 2;
            if (pairs[a] == pairs[b] && pairs[a + 1] == pairs[b + 1])
            {
                continue;
            }

            if (!SameValue(node, views, pairs, row - 1, row))
            {
                stats.RunBoundaries++;
            }
        }

        int last = start + count - 1;
        bool lastValid = mask.IsValid(last);
        Store(previous, lastValid, lastValid ? Value(node, views, last) : default);
    }

    /// <summary>
    /// Whether two rows of a varbinview hold the same bytes, their views already known to differ.
    /// </summary>
    /// <remarks>
    /// Different sizes are different values and need no comparison at all — which is the whole of
    /// the answer for most pairs. Only two values of the SAME length that sit at different offsets
    /// reach the byte comparison, and that is the case docs/11 §3.2.4 exists for: two equal
    /// out-of-line strings written twice must not split a run.
    /// </remarks>
    private static bool SameValue(
        CanonicalNode node, ReadOnlySpan<byte> views, ReadOnlySpan<ulong> pairs, int a, int b)
    {
        // The size is the view's low four bytes, so it is the low half of the first word.
        if ((uint)pairs[a * 2] != (uint)pairs[b * 2])
        {
            return false;
        }

        return Value(node, views, a).SequenceEqual(Value(node, views, b));
    }

    /// <summary>One varbinview row's bytes, inline or through its data buffer.</summary>
    private static ReadOnlySpan<byte> Value(CanonicalNode node, ReadOnlySpan<byte> views, int row)
    {
        ReadOnlySpan<byte> view = views.Slice(row * 16, 16);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        if (size <= 12)
        {
            return view.Slice(4, size);
        }

        int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
        return node.GetDataBuffer(buffer).Span.Slice(offset, size);
    }

    private static void Store(PreviousRow? previous, bool valid, ReadOnlySpan<byte> value)
    {
        if (previous is null)
        {
            return;
        }

        if (valid)
        {
            previous.Set(value);
        }
        else
        {
            previous.SetNull();
        }
    }

    /// <summary>The bytes of the valid values in the range: what a <c>vortex.varbin</c> heap holds.</summary>
    /// <remarks>
    /// The size is the view's first four bytes whatever the value's form, inline or out of line, so
    /// this reads one field per row and never follows a view to its data buffer.
    /// </remarks>
    private static long ViewBytes(
        CanonicalNode node, in ValidityMask mask, int start, int count, int valid)
    {
        ReadOnlySpan<byte> views = node.Views.Span;
        long total = 0;

        if (valid == count)
        {
            for (int i = 0; i < count; i++)
            {
                total += BinaryPrimitives.ReadUInt32LittleEndian(views.Slice((start + i) * 16, 4));
            }

            return total;
        }

        for (int i = 0; i < count; i++)
        {
            int row = start + i;
            if (mask.IsValid(row))
            {
                total += BinaryPrimitives.ReadUInt32LittleEndian(views.Slice(row * 16, 4));
            }
        }

        return total;
    }

    /// <summary>Adds the range's nulls to <paramref name="stats"/> and returns its valid rows.</summary>
    private static int Nulls(in ValidityMask mask, int start, int count, ref BlockStats stats)
    {
        if (mask.AllValid)
        {
            return count;
        }

        if (mask.AllInvalid)
        {
            stats.NullCount += count;
            return 0;
        }

        int valid = BitmapKernels.CountSet(mask.Bits, mask.BitOffset + start, count);
        stats.NullCount += count - valid;
        return valid;
    }

    /// <summary>Bounds a block by the single element a constant column repeats.</summary>
    private static void Element(PType ptype, ReadOnlySpan<byte> element, ref BlockStats stats)
    {
        if (ptype.IsFloat())
        {
            double value = ElementFloat(element, ptype);
            if (!double.IsNaN(value))
            {
                stats.MergeFloat(value, value);
            }

            return;
        }

        if (ptype.IsSignedInteger())
        {
            long value = ElementSigned(element, ptype);
            stats.MergeSigned(value, value);
            return;
        }

        ulong bits = ElementUnsigned(element, ptype);
        stats.MergeUnsigned(bits, bits);
    }

    /// <summary>Dispatches on the physical type ONCE, then runs a monomorphic loop.</summary>
    /// <remarks>
    /// <paramref name="widths"/> is the pair of histograms of <see cref="BitPackWidths"/> when the
    /// caller wants them and empty otherwise, and the integer loops branch on it ONCE rather than
    /// per row: docs/11-write-strategy.md §3.2 puts the widths in this walk precisely because the
    /// value is already in a register here, so counting it costs a leading-zero count and an
    /// increment instead of the whole second walk `BitPackPlan` used to make.
    /// </remarks>
    private static void Values(
        CanonicalNode node, in ValidityMask mask, int start, int count, ref BlockStats stats,
        Span<int> widths = default)
    {
        ReadOnlySpan<byte> values = node.Values.Span;
        switch (node.PType)
        {
            case PType.I8: Signed<sbyte>(values, in mask, start, count, ref stats, widths); return;
            case PType.I16: Signed<short>(values, in mask, start, count, ref stats, widths); return;
            case PType.I32: Signed<int>(values, in mask, start, count, ref stats, widths); return;
            case PType.I64: Signed<long>(values, in mask, start, count, ref stats, widths); return;
            case PType.U8: Unsigned<byte>(values, in mask, start, count, ref stats, widths); return;
            case PType.U16: Unsigned<ushort>(values, in mask, start, count, ref stats, widths); return;
            case PType.U32: Unsigned<uint>(values, in mask, start, count, ref stats, widths); return;
            case PType.U64: Unsigned<ulong>(values, in mask, start, count, ref stats, widths); return;
            case PType.F16: Halves(values, in mask, start, count, ref stats); return;
            case PType.F32: Floats<float>(values, in mask, start, count, ref stats); return;
            default: Floats<double>(values, in mask, start, count, ref stats); return;
        }
    }

    /// <summary>The raw and zigzag widths of one value, counted into the pair of histograms.</summary>
    /// <remarks>
    /// THE RAW WIDTH IS TAKEN ON THE UNSIGNED READING of the bits, which is what the packer writes:
    /// a signed <c>-1</c> is an <c>sbyte</c> of eight set bits, so its raw width is eight and not
    /// sixty-four. The zigzag form interleaves the sign so that magnitude rather than position
    /// decides the width, and the mask keeps it inside the element — the same two expressions
    /// <c>BitPackPlan</c> prices with, which is why the histograms this produces are the ones it
    /// used to walk the column for.
    /// </remarks>
    /// <param name="bits">The row's value, masked to the element width.</param>
    /// <param name="elementBits">8, 16, 32 or 64.</param>
    /// <param name="widths">The pair of histograms.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Widths(ulong bits, int elementBits, Span<int> widths)
    {
        widths[64 - BitOperations.LeadingZeroCount(bits)]++;
        ulong sign = 0UL - ((bits >> (elementBits - 1)) & 1);
        ulong zigzag = ((bits << 1) ^ sign) & BitWords.Mask(elementBits);
        widths[BitPackWidths.ZigZagOffset + 64 - BitOperations.LeadingZeroCount(zigzag)]++;
    }

    private static void Signed<T>(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int start, int count, ref BlockStats stats,
        Span<int> widths)
        where T : unmanaged, IBinaryInteger<T>, ISignedNumber<T>, IMinMaxValue<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes).Slice(start, count);
        T min = T.MaxValue;
        T max = T.MinValue;
        bool counting = !widths.IsEmpty;
        int elementBits = Unsafe.SizeOf<T>() * 8;
        ulong mask64 = BitWords.Mask(elementBits);

        if (mask.AllValid)
        {
            for (int i = 0; i < values.Length; i++)
            {
                T value = values[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }

                if (counting)
                {
                    Widths(ulong.CreateTruncating(value) & mask64, elementBits, widths);
                }
            }

            stats.MergeSigned(long.CreateTruncating(min), long.CreateTruncating(max));
            return;
        }

        bool any = false;
        for (int i = 0; i < values.Length; i++)
        {
            if (!mask.IsValid(start + i))
            {
                continue;
            }

            any = true;
            T value = values[i];
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }

            if (counting)
            {
                Widths(ulong.CreateTruncating(value) & mask64, elementBits, widths);
            }
        }

        if (any)
        {
            stats.MergeSigned(long.CreateTruncating(min), long.CreateTruncating(max));
        }
    }


    private static void Unsigned<T>(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int start, int count, ref BlockStats stats,
        Span<int> widths)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>, IMinMaxValue<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes).Slice(start, count);
        T min = T.MaxValue;
        T max = T.MinValue;
        bool counting = !widths.IsEmpty;
        int elementBits = Unsafe.SizeOf<T>() * 8;

        if (mask.AllValid)
        {
            for (int i = 0; i < values.Length; i++)
            {
                T value = values[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }

                if (counting)
                {
                    Widths(ulong.CreateTruncating(value), elementBits, widths);
                }
            }

            stats.MergeUnsigned(ulong.CreateTruncating(min), ulong.CreateTruncating(max));
            return;
        }

        bool any = false;
        for (int i = 0; i < values.Length; i++)
        {
            if (!mask.IsValid(start + i))
            {
                continue;
            }

            any = true;
            T value = values[i];
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }

            if (counting)
            {
                Widths(ulong.CreateTruncating(value), elementBits, widths);
            }
        }

        if (any)
        {
            stats.MergeUnsigned(ulong.CreateTruncating(min), ulong.CreateTruncating(max));
        }
    }

    /// <summary>
    /// F32 and F64, NaN excluded, ordered by <c>Math.Min</c>'s sign-aware rule rather than by
    /// <c>&lt;</c>.
    /// </summary>
    /// <remarks>
    /// SKIPPING NaN IS LOAD-BEARING, not tidy: the reference computes min/max with `skip_nans()` and
    /// counts NaN in its own aggregate, which is what makes "a zone with max &lt;= 10 may be pruned
    /// for x &gt; 10 even when it contains NaN" sound. A NaN that reached `max` would compare false
    /// with everything and the bound would stop meaning anything.
    /// <para>
    /// The accumulation stays in <typeparamref name="T"/> and widens once at the end. Widening is
    /// monotone and exact for F32 to F64, so the answer is the one a double accumulator gives, at
    /// half the register traffic.
    /// </para>
    /// </remarks>
    private static void Floats<T>(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int start, int count, ref BlockStats stats)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes).Slice(start, count);
        T min = T.PositiveInfinity;
        T max = T.NegativeInfinity;
        bool any = false;

        if (mask.AllValid)
        {
            for (int i = 0; i < values.Length; i++)
            {
                T value = values[i];
                if (T.IsNaN(value))
                {
                    continue;
                }

                any = true;
                min = Smaller(min, value);
                max = Larger(max, value);
            }
        }
        else
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!mask.IsValid(start + i))
                {
                    continue;
                }

                T value = values[i];
                if (T.IsNaN(value))
                {
                    continue;
                }

                any = true;
                min = Smaller(min, value);
                max = Larger(max, value);
            }
        }

        if (any)
        {
            stats.MergeFloat(double.CreateTruncating(min), double.CreateTruncating(max));
        }
    }

    /// <summary>F16, widened to <see cref="float"/> - exactly, since every Half has one.</summary>
    /// <remarks>
    /// Its own loop rather than an instantiation of <see cref="Floats{T}"/>: <see cref="Half"/>
    /// arithmetic is emulated, so comparing in it would be slower than the conversion it saves.
    /// </remarks>
    private static void Halves(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int start, int count, ref BlockStats stats)
    {
        ReadOnlySpan<Half> values = MemoryMarshal.Cast<byte, Half>(bytes).Slice(start, count);
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        bool any = false;

        for (int i = 0; i < values.Length; i++)
        {
            if (!mask.AllValid && !mask.IsValid(start + i))
            {
                continue;
            }

            float value = (float)values[i];
            if (float.IsNaN(value))
            {
                continue;
            }

            any = true;
            min = Smaller(min, value);
            max = Larger(max, value);
        }

        if (any)
        {
            stats.MergeFloat(min, max);
        }
    }

    /// <summary><c>Math.Min</c>'s rule, generically: <c>-0.0</c> sorts below <c>+0.0</c>.</summary>
    private static T Smaller<T>(T a, T b)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        if (a != b)
        {
            return b < a ? b : a;
        }

        return T.IsNegative(a) ? a : b;
    }

    /// <summary><c>Math.Max</c>'s rule, generically: <c>+0.0</c> sorts above <c>-0.0</c>.</summary>
    private static T Larger<T>(T a, T b)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        if (a != b)
        {
            return b > a ? b : a;
        }

        return T.IsNegative(a) ? b : a;
    }

    // The three below read ONE value, the element of a constant column. They switch on the physical
    // type, which is what `CanonicalSupport.ReadInteger` does and what PerRowDispatchTests counts --
    // written out here rather than called so that the ratchet's table stays a list of loops.

    private static double ElementFloat(ReadOnlySpan<byte> element, PType ptype) => ptype switch
    {
        PType.F16 => (double)BinaryPrimitives.ReadHalfLittleEndian(element),
        PType.F32 => BinaryPrimitives.ReadSingleLittleEndian(element),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(element),
    };

    private static long ElementSigned(ReadOnlySpan<byte> element, PType ptype) => ptype switch
    {
        PType.I8 => (sbyte)element[0],
        PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(element),
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(element),
        _ => BinaryPrimitives.ReadInt64LittleEndian(element),
    };

    private static ulong ElementUnsigned(ReadOnlySpan<byte> element, PType ptype) => ptype switch
    {
        PType.U8 => element[0],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(element),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(element),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(element),
    };
}
