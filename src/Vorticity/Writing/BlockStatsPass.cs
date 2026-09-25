using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Writing;

/// <summary>Fills a <see cref="BlockStats"/> from a row range of a canonical column.</summary>
/// <remarks>
/// One loop per (column, block) produces everything the zone map needs, at ingest and over a row
/// range of the batch, so a block is summarized from the batches that cover it rather than from the
/// chunk it lands in — which is what lets the zone map be independent of the chunk shape. The
/// physical type is resolved once, before the loop, into a generic instantiation rather than a
/// switch on it for every value, and a constant column is summarized from its single element
/// without the column the constant form exists to not build ever being materialized.
/// </remarks>
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

        // Broken until this range proves otherwise. Every kind but an integer primitive contributes
        // nothing to the width histograms, and so does an integer range the progression
        // short-circuit answers without reading a value; the one path that does contribute restores
        // the flag to what the ranges before it had left it.
        bool widthsBefore = stats.WidthsBroken;
        stats.WidthsBroken = true;

        switch (node.Kind)
        {
            case CanonicalKind.Constant:
            {
                // One element, `count` rows. Its dtype is a primitive or a string: those are the
                // two the canonicalizer builds the form for, and each contributes what its own kind
                // contributes below -- a bound for the first, a byte count for the second, exactly
                // as the Primitive and VarBinView arms do over their own rows.
                stats.IsSummarizable = true;
                ReadOnlySpan<byte> element = node.ConstantElement;
                bool bytes = node.DType.Kind is DTypeKind.Utf8 or DTypeKind.Binary;
                bool elementValid = mask.IsValid(start);

                if (valid > 0)
                {
                    if (bytes)
                    {
                        // No bounds, for the VarBinView arm's reason. The byte total is the one
                        // thing a string block owes the compressor, and here it is one length
                        // multiplied rather than a walk over the views.
                        stats.TotalBytes += (long)valid * element.Length;
                    }
                    else
                    {
                        Element(node.DType.PType, element, ref stats);
                    }
                }

                // The steps have to be recorded here too. `Deltas` runs from the Primitive arm
                // only, so a chunk whose blocks are all constants would carry a `DeltaKnown` set by
                // some other block -- and that claim tells `SequencePlan` its walk has already been
                // done. It skips the walk, reads the step as `v[1] - v[0]` off the wrong block, and
                // writes `vortex.sequence(base, 0)` over rows that climb. A claim that the steps
                // are verified has to be made by whoever verified them.
                if (!bytes && node.DType.PType.IsInteger())
                {
                    ConstantDeltas(
                        node.DType.PType, element, count, valid, startsBlock, ref stats, previous);
                }

                // Order: one value repeated is sorted and not strict, and the seam with the row
                // before is the element against it.
                stats.OrderTracked = true;
                if (!stats.OrderUntracked && !stats.Unsorted)
                {
                    if (bytes)
                    {
                        OrderSeamBytes(
                            elementValid ? element : default, elementValid, startsBlock, ref stats,
                            previous);
                    }
                    else
                    {
                        OrderSeam(
                            node.DType.PType, element, elementValid, startsBlock, ref stats,
                            previous);
                    }

                    if (count > 1)
                    {
                        stats.Repeats = true;
                    }

                    if (valid != 0 && valid != count)
                    {
                        // Values and nulls both: nulls after a value break the order unless the
                        // nulls all come first, which a constant block's mask alone can say.
                        stats.Unsorted |= !NullsFirst(in mask, start, count);
                    }
                }

                // Every row holds the same value, so the only boundaries are where validity turns
                // over -- which is why this is answered without reading a value per row.
                ConstantRuns(node, in mask, start, count, valid, startsBlock, ref stats, previous);
                return;
            }

            case CanonicalKind.Primitive:
            {
                stats.IsSummarizable = true;

                // The order's seam is taken first too, for the file statistics' is_sorted: the
                // range's first row against the row before it, while `previous` still holds that
                // row. The range's own pairs follow -- by the step when the range is a progression,
                // by a second walk over the range otherwise (Order<T>) -- and only while the column
                // can still be sorted: a witness is sticky.
                stats.OrderTracked = true;
                if (!stats.OrderUntracked && !stats.Unsorted)
                {
                    int width = node.PType.ByteWidth();
                    bool firstValid = mask.IsValid(start);
                    OrderSeam(
                        node.PType,
                        firstValid ? node.Values.Span.Slice(start * width, width) : default,
                        firstValid, startsBlock, ref stats, previous);

                    // An all-null range never reaches the value walk, and two nulls are two
                    // equal rows: sorted, not strict (the reference says the same of a null
                    // array).
                    if (valid == 0 && count > 1)
                    {
                        stats.Repeats = true;
                    }
                }

                // The steps are taken first, and while `previous` still holds the row before this
                // range: `FixedRuns` overwrites it with the range's last row.
                //
                // A progression answers the other two without reading a value. If every pair in
                // this range climbs by the same step, the range is monotone -- a wrap would have
                // made one step differ from the others -- so its extremes are its endpoints, and
                // its runs are one if the step is zero and one per row otherwise. That leaves a
                // progression costing one walk instead of three.
                bool progression = false;
                if (node.PType.IsInteger())
                {
                    bool wasProgression = !stats.DeltaBroken;
                    bool hadPrevious = previous is not null && previous.HasValue;
                    Deltas(node, start, count, valid, startsBlock, ref stats, previous);

                    if (wasProgression && stats.DeltaKnown && !stats.DeltaBroken)
                    {
                        progression = true;

                        // A progression's order is its step: it repeats when the step is zero
                        // and descends when it is negative -- no value read.
                        if (stats.Delta < 0)
                        {
                            stats.Unsorted = true;
                        }
                        else if (stats.Delta == 0)
                        {
                            stats.Repeats = true;
                        }

                        ProgressionBounds(node, stats.Delta, start, count, ref stats);
                        ProgressionRuns(
                            node, stats.Delta, start, count, hadPrevious, startsBlock, ref stats,
                            previous);
                    }
                }

                // The widths are counted here or nowhere. A progression is answered from its
                // endpoints, so its values are never loaded and there is nothing to histogram --
                // and nothing that needs one either, since `vortex.sequence` claims the column
                // before bit-packing is offered it. What matters is that the block says so, because
                // a later range may break the progression and fill half a histogram.
                Span<int> counts = !progression && node.PType.IsInteger() ? widths : default;
                if (!counts.IsEmpty)
                {
                    stats.WidthsBroken = widthsBefore;

                    // A null row packs as zero, which is width zero in both domains. Counting it
                    // here rather than in the loops keeps the typed walk free of the branch, and it
                    // is the rule `BitPackPlan` applies.
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
                // No bounds: a decimal's comparison domain is not one these kernels cover. Its rows
                // still compare byte for byte, which is all run-end needs.
                FixedRuns(
                    node.Values.Span, DecimalStorage.ByteWidth(node.Storage), in mask, start, count,
                    startsBlock, ref stats, previous);
                return;

            case CanonicalKind.Bool:
                BoolRuns(node, in mask, start, count, startsBlock, ref stats, previous);
                return;

            case CanonicalKind.VarBinView:
                // No bounds: utf8 and binary have a perfectly good lexicographic min/max and no
                // place to put it without a second varbinview per zone. Their bytes, on the other
                // hand, are what `ColumnCompressor.PlainBinarySize` needs to price the varbin form
                // against the view form -- so the sum is taken here, where the views are in cache
                // and the pass is already reading them.
                if (valid > 0)
                {
                    stats.TotalBytes += ViewBytes(node, in mask, start, count, valid);
                }

                // The byte sum and the run walk are a real share of a write on the string
                // encodings, and they cannot be merged into the dictionary probe: the probe runs
                // only on a chunk whose remembered plan is a dictionary that held, and a dictionary
                // does not hold on the columns that pay most here. Making the two cheaper in
                // themselves, or skipping them where nothing consumes them, is what would help.
                //
                // Order, bytewise: the seam here, the pairs inside ViewRuns, which reads them
                // for the run boundaries anyway.
                stats.OrderTracked = true;
                if (!stats.OrderUntracked && !stats.Unsorted)
                {
                    bool firstValid = mask.IsValid(start);
                    OrderSeamBytes(
                        firstValid ? Value(node, node.Views.Span, start) : default,
                        firstValid, startsBlock, ref stats, previous);
                }

                ViewRuns(node, in mask, start, count, startsBlock, ref stats, previous);
                return;

            default:
                // Every other kind has no scalar bound at all, and still gets a zone map with the
                // null count alone, which is what a null-check predicate prunes on.
                return;
        }
    }

    // -------------------------------------------------------------------------------------- steps
    //
    // `vortex.sequence` is the one encoding that costs nothing per row: a column that is an
    // arithmetic progression becomes one node and about thirty bytes. Finding one deserves no walk
    // of its own -- most of the columns offered to it really are progressions, so such a walk reads
    // nearly every row rather than bailing at row three -- so it is folded into the pass that is
    // already reading the values. It stops the moment two steps disagree, so a column that is not a
    // progression pays a compare per row until row three and nothing after: a branch the predictor
    // gets right every time.
    //
    // Only the fact is kept, not the plan. When the steps agree, the step itself is `v[1] - v[0]`,
    // which `SequencePlan` reads from the node in constant time; there is nothing to carry.

    /// <summary>The bounds of a progression: its first and last rows, in step order.</summary>
    /// <remarks>
    /// Exact because a range whose steps all agree is monotone. It cannot wrap: a wrap would make
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
    /// differ from the one before it, so every row of the range starts a run. Both are exactly what
    /// a walk would have counted, and neither needs one. The first row is the seam's to decide:
    /// inside a block the row before it stepped like every other, so it differs exactly when the
    /// step is not zero; at a block's first row the row before belongs to the block before, and
    /// whether the two differ is the seam <see cref="Deltas"/> recorded — a step of zero across it
    /// is the same value, any other is a boundary, and a null before it is one too.
    /// </remarks>
    private static void ProgressionRuns(
        CanonicalNode node, long step, int start, int count, bool hadPrevious, bool startsBlock,
        ref BlockStats stats, PreviousRow? previous)
    {
        stats.HasRunBoundaries = true;

        if (step != 0)
        {
            stats.RunBoundaries += count - 1;
        }

        if (hadPrevious)
        {
            bool boundary = startsBlock
                ? stats.LeadingBroken || (stats.LeadingKnown && stats.Leading != 0)
                : step != 0;
            if (boundary)
            {
                stats.RunBoundaries++;
                if (startsBlock)
                {
                    stats.FirstRowStartsRun = true;
                }
            }
        }

        int width = node.PType.ByteWidth();
        Store(previous, valid: true, node.Values.Span.Slice((start + count - 1) * width, width));
    }

    /// <summary>Folds this range's steps into <paramref name="stats"/>.</summary>
    /// <remarks>
    /// The row before the range is stepped from in two different ways. Inside a block -- a range
    /// that continues one -- that row is the block's own and its step is one of the block's. At a
    /// block's first row it belongs to the block before, and the step into it is the seam: recorded
    /// on its own (<see cref="BlockStats.SetLeading"/>) for the merge to read when the block is not
    /// the first of a chunk, and ignored when it is. Folded into the block's own steps instead, it
    /// would make the first block of every chunk carry the jump between it and the chunk before as
    /// a break of its own.
    /// </remarks>
    private static void Deltas(
        CanonicalNode node, int start, int count, int valid, bool startsBlock,
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

        bool hadPrevious = previous is not null && previous.HasValue;
        if (hadPrevious && previous!.IsNull)
        {
            // A null before the range: inside the block it is the block's own break, and at the
            // block's first row it is the seam's -- the block's own steps are still read.
            if (!startsBlock)
            {
                stats.BreakDelta();
                return;
            }

            stats.BreakLeading();
            hadPrevious = false;
        }

        // The physical type is resolved once, into a generic instantiation: the alternative is two
        // switches per row for a property of the call. Narrow types step in `long` because the
        // difference of two 32-bit values always fits in one; only 64-bit columns need the wider
        // arithmetic, and they get their own loop.
        ReadOnlySpan<byte> values = node.Values.Span;
        ReadOnlySpan<byte> seed = hadPrevious ? previous!.Bytes : default;
        bool seam = startsBlock && hadPrevious;
        switch (node.PType)
        {
            case PType.I8: Narrow<sbyte>(values, seed, seam, start, count, ref stats); return;
            case PType.I16: Narrow<short>(values, seed, seam, start, count, ref stats); return;
            case PType.I32: Narrow<int>(values, seed, seam, start, count, ref stats); return;
            case PType.U8: Narrow<byte>(values, seed, seam, start, count, ref stats); return;
            case PType.U16: Narrow<ushort>(values, seed, seam, start, count, ref stats); return;
            case PType.U32: Narrow<uint>(values, seed, seam, start, count, ref stats); return;
            case PType.I64: Wide<long>(values, seed, seam, start, count, ref stats); return;
            default: Wide<ulong>(values, seed, seam, start, count, ref stats); return;
        }
    }

    /// <summary>The steps of a constant range, which has at most two distinct ones.</summary>
    /// <param name="ptype">The element's physical type; an integer, checked by the caller.</param>
    /// <param name="element">The one value's bytes.</param>
    /// <param name="count">Rows in the range.</param>
    /// <param name="valid">Valid rows in it.</param>
    /// <param name="startsBlock">Whether the range is the block's first.</param>
    /// <param name="stats">The accumulator.</param>
    /// <param name="previous">The row before the range, when there is one.</param>
    /// <remarks>
    /// <para>
    /// <see cref="Deltas"/>'s rules, without its walk: a range of one value repeated has
    /// <c>count - 1</c> steps of zero, plus the seam from the row before it. So there is nothing to
    /// read per row -- two subtractions answer the whole range -- but the answer must still be
    /// recorded, because what the chooser reads is the claim, not the rows.
    /// </para>
    /// <para>
    /// <see cref="Int128"/> throughout, for <see cref="Wide{T}"/>'s reason: the difference of two
    /// 64-bit values does not fit in 64 bits in general, and the wire field is a signed 64. The
    /// cost of the wider arithmetic is two subtractions per range, not per row.
    /// </para>
    /// </remarks>
    private static void ConstantDeltas(
        PType ptype, ReadOnlySpan<byte> element, int count, int valid, bool startsBlock,
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

        bool hadPrevious = previous is not null && previous.HasValue;
        if (hadPrevious && previous!.IsNull)
        {
            if (!startsBlock)
            {
                stats.BreakDelta();
                return;
            }

            stats.BreakLeading();
            hadPrevious = false;
        }

        Int128 value = ElementValue(ptype, element);
        Int128 step = stats.Delta;
        bool known = stats.DeltaKnown;

        if (hadPrevious)
        {
            Int128 delta = value - ElementValue(ptype, previous!.Bytes);
            if (startsBlock)
            {
                // The seam is recorded apart, for the merge to read when this block is not the
                // chunk's first; folded into the block's own steps it would make that block claim
                // the jump from the chunk before as a break of its own.
                if (delta < long.MinValue || delta > long.MaxValue)
                {
                    stats.BreakLeading();
                }
                else
                {
                    stats.SetLeading((long)delta);
                }
            }
            else if (known)
            {
                if (delta != step)
                {
                    stats.BreakDelta();
                    return;
                }
            }
            else if (delta < long.MinValue || delta > long.MaxValue)
            {
                stats.BreakDelta();
                return;
            }
            else
            {
                step = delta;
                known = true;
            }
        }

        // Every step inside the range is zero: one value, repeated.
        if (count > 1)
        {
            if (known && step != 0)
            {
                stats.BreakDelta();
                return;
            }

            step = 0;
            known = true;
        }

        if (known)
        {
            stats.SetDelta((long)step);
        }
    }

    /// <summary>One integer element, widened from its physical type.</summary>
    private static Int128 ElementValue(PType ptype, ReadOnlySpan<byte> bytes) => ptype switch
    {
        PType.I8 => MemoryMarshal.Read<sbyte>(bytes),
        PType.I16 => MemoryMarshal.Read<short>(bytes),
        PType.I32 => MemoryMarshal.Read<int>(bytes),
        PType.U8 => bytes[0],
        PType.U16 => MemoryMarshal.Read<ushort>(bytes),
        PType.U32 => MemoryMarshal.Read<uint>(bytes),
        PType.U64 => MemoryMarshal.Read<ulong>(bytes),
        _ => MemoryMarshal.Read<long>(bytes),
    };

    /// <summary>Steps of a column narrower than 64 bits, where a difference always fits a long.</summary>
    /// <remarks>
    /// The state is in locals and written back once. Calling a method on the accumulator and
    /// re-reading one of its fields for every row puts a call and two branches inside the hot loop,
    /// enough to make this pass more expensive than the separate `SequencePlan` walk it folds in.
    /// In this shape the steady state is one subtract and one compare, which is what that walk
    /// costs.
    /// <para>
    /// And a register of them where the lanes are wide enough: a list's elements go through here
    /// too, millions of rows at a time, and there even one compare a row is more than the separate
    /// walk cost.
    /// </para>
    /// </remarks>
    private static void Narrow<T>(
        ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> seed, bool seam, int start, int count,
        ref BlockStats stats)
        where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
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
        else if (seam)
        {
            // The step across the block boundary is the seam's, recorded apart; the block's own
            // steps start at its own first row.
            last = long.CreateTruncating(values[start]);
            stats.SetLeading(last - long.CreateTruncating(MemoryMarshal.Read<T>(seed)));
            first = 1;
        }
        else
        {
            last = long.CreateTruncating(MemoryMarshal.Read<T>(seed));
            first = 0;
        }

        // A register at a time once the step is known and the previous row is inside the span: a
        // list's elements put millions of rows of a progression through here, where the scalar loop
        // costs more than the separate `SequencePlan` walk it stands in for.
        int i = first;
        if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && Vector128<T>.Count >= 4
            && count - first > 4 * Vector128<T>.Count)
        {
            // The first pair, scalar: it names the step when none is known, and its previous row may
            // be the seed, which is not in the span.
            long head = long.CreateTruncating(values[start + i]);
            long delta = head - last;
            if (known && delta != step)
            {
                stats.BreakDelta();
                return;
            }

            step = delta;
            known = true;
            last = head;
            i++;
            if (!Progression(values, start + i, start + count, last, step))
            {
                stats.BreakDelta();
                return;
            }

            stats.SetDelta(step);
            return;
        }

        for (; i < count; i++)
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
    /// Whether rows <c>[from, end)</c> each climb by <paramref name="step"/> from the row before,
    /// the row before <paramref name="from"/> being <paramref name="previous"/> — a register at a
    /// time.
    /// </summary>
    /// <remarks>
    /// The lanes subtract in the type's own width, so a difference that wraps would compare equal
    /// to a step it is not. The endpoint closes that: a progression is monotone, so when its last
    /// row, <c>previous + (end - from)·step</c>, lies in the type's range, every row before it does
    /// too, and two values of that range that agree modulo 2^w are the same value. A range whose
    /// endpoint leaves the type's range is no progression, and says so before a lane is loaded.
    /// <para>
    /// Four lanes or more, as <see cref="OrderLanes"/>: the 64-bit columns keep their scalar walk.
    /// The tail is the scalar twin, and so is the caller's loop under
    /// <c>DOTNET_EnableHWIntrinsic=0</c>. <paramref name="from"/> is at least one past the span's
    /// first row, so the first window's previous row is in the span.
    /// </para>
    /// </remarks>
    private static bool Progression<T>(ReadOnlySpan<T> values, int from, int end, long previous, long step)
        where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
    {
        Int128 endpoint = (Int128)previous + ((Int128)(end - from) * step);
        if (endpoint < long.CreateTruncating(T.MinValue) || endpoint > long.CreateTruncating(T.MaxValue))
        {
            return false;
        }

        int lanes = Vector128<T>.Count;
        ref T head = ref MemoryMarshal.GetReference(values);
        Vector128<T> steps = Vector128.Create(T.CreateTruncating(step));
        int row = from;
        for (; row + lanes <= end; row += lanes)
        {
            Vector128<T> here = Vector128.LoadUnsafe(ref head, (nuint)row);
            Vector128<T> before = Vector128.LoadUnsafe(ref head, (nuint)(row - 1));
            if (!Vector128.EqualsAll(here - before, steps))
            {
                return false;
            }
        }

        long last = long.CreateTruncating(values[row - 1]);
        for (; row < end; row++)
        {
            long value = long.CreateTruncating(values[row]);
            if (value - last != step)
            {
                return false;
            }

            last = value;
        }

        return true;
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
        ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> seed, bool seam, int start, int count,
        ref BlockStats stats)
        where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
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
        else if (seam)
        {
            // The step across the block boundary is the seam's, recorded apart -- or no step at
            // all when it does not fit the wire field; the block's own steps start at its own row.
            last = Int128.CreateTruncating(values[start]);
            Int128 lead = last - Int128.CreateTruncating(MemoryMarshal.Read<T>(seed));
            if (lead < long.MinValue || lead > long.MaxValue)
            {
                stats.BreakLeading();
            }
            else
            {
                stats.SetLeading((long)lead);
            }

            first = 1;
        }
        else
        {
            last = Int128.CreateTruncating(MemoryMarshal.Read<T>(seed));
            first = 0;
        }

        // Two registers at a time once the first pair has named the step, as `Narrow` does, the
        // previous row being inside the span from then on.
        if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && count - first > 8)
        {
            Int128 head = Int128.CreateTruncating(values[start + first]);
            Int128 delta = head - last;
            if (known ? delta != step : delta < long.MinValue || delta > long.MaxValue)
            {
                stats.BreakDelta();
                return;
            }

            if (!WideProgression(values, start + first + 1, start + count, head, (long)delta))
            {
                stats.BreakDelta();
                return;
            }

            stats.SetDelta((long)delta);
            return;
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

    /// <summary>
    /// <see cref="Progression{T}"/> for 64-bit columns: the endpoint taken in <see cref="Int128"/>,
    /// since the row before may not fit a long, and two registers of two lanes a step.
    /// </summary>
    private static bool WideProgression<T>(ReadOnlySpan<T> values, int from, int end, Int128 previous, long step)
        where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
    {
        Int128 endpoint = previous + ((Int128)(end - from) * step);
        if (endpoint < Int128.CreateTruncating(T.MinValue) || endpoint > Int128.CreateTruncating(T.MaxValue))
        {
            return false;
        }

        int lanes = Vector128<T>.Count;
        ref T head = ref MemoryMarshal.GetReference(values);
        T each = T.CreateTruncating(step);
        Vector128<T> steps = Vector128.Create(each);
        int row = from;
        for (; row + (2 * lanes) <= end; row += 2 * lanes)
        {
            Vector128<T> low = Vector128.LoadUnsafe(ref head, (nuint)row) - Vector128.LoadUnsafe(ref head, (nuint)(row - 1));
            Vector128<T> high = Vector128.LoadUnsafe(ref head, (nuint)(row + lanes)) - Vector128.LoadUnsafe(ref head, (nuint)(row + lanes - 1));
            if (!Vector128.EqualsAll((low ^ steps) | (high ^ steps), Vector128<T>.Zero))
            {
                return false;
            }
        }

        for (; row < end; row++)
        {
            if (unchecked(Unsafe.Add(ref head, row) - Unsafe.Add(ref head, row - 1)) != each)
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------------------ run boundaries
    //
    // What these count has to be exact. `ColumnCompressor` declines run-end when a chunk has more
    // than `rows / 4` runs, so the verdict turns on a single comparison: a count that is off by one
    // changes the plan and therefore the file's bytes. The rule reproduced here is
    // `RowComparer.Equal`'s, in full -- two nulls are equal, a null and a value are not, two values
    // are equal byte for byte.
    //
    // A boundary belongs to the row that starts the new run, so it is counted in that row's block
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

    /// <summary>
    /// Adjacent rows that differ, with the element width resolved into the loop: a vector equality
    /// against the vector one element behind, its matches counted in the lanes.
    /// </summary>
    /// <remarks>
    /// The shift is a second load, not a lane shuffle. Comparing a row against the row before it
    /// means comparing the vector at <c>i</c> with the vector at <c>i - 1</c>, and an unaligned load
    /// one element back is one instruction where shuffling a lane across the vector's own boundary
    /// would need the previous iteration's last lane carried forward -- a dependency between
    /// iterations, which is exactly what this kernel exists to remove. The two loads overlap in
    /// cache and the second is free.
    /// <para>
    /// The boundaries are the lanes the equality does not set. Its all-ones lanes are subtracted
    /// into an accumulator rather than gathered into a bit mask each step, which on Arm is several
    /// instructions where the subtraction is one; the lanes are folded every 255 steps at most.
    /// </para>
    /// <para>
    /// T is always an unsigned integer here -- <see cref="Interior"/> casts the raw bytes at the
    /// element's width -- so this is a bytewise comparison and no float's NaN or negative zero can
    /// reach it. That is the rule <c>RowComparer.Equal</c> states and the one the count has to be
    /// exact against, since <c>ColumnCompressor</c> declines run-end on <c>rows / 4</c>.
    /// </para>
    /// <para>
    /// The tail is the scalar twin, and the whole method under
    /// <c>DOTNET_EnableHWIntrinsic=0</c>, where <see cref="Vector128.IsHardwareAccelerated"/> is
    /// false.
    /// </para>
    /// </remarks>
    private static long Differing<T>(ReadOnlySpan<T> values)
        where T : unmanaged, IBinaryInteger<T>
    {
        long boundaries = 0;
        int i = 1;
        int lanes = Vector128<T>.IsSupported ? Vector128<T>.Count : 0;
        if (Vector128.IsHardwareAccelerated && lanes > 0 && values.Length > lanes)
        {
            ref T head = ref MemoryMarshal.GetReference(values);
            int last = values.Length - lanes;
            while (i <= last)
            {
                // Equal lanes counted in the lanes themselves, an equality being all ones: minus
                // one per match, and at most 255 steps between folds so a byte lane cannot wrap.
                Vector128<T> equal = Vector128<T>.Zero;
                int steps = Math.Min(255, ((last - i) / lanes) + 1);
                for (int step = 0; step < steps; step++, i += lanes)
                {
                    Vector128<T> here = Vector128.LoadUnsafe(ref head, (nuint)i);
                    Vector128<T> before = Vector128.LoadUnsafe(ref head, (nuint)(i - 1));
                    equal -= Vector128.Equals(here, before);
                }

                long matches = 0;
                for (int lane = 0; lane < lanes; lane++)
                {
                    matches += long.CreateTruncating(equal.GetElement(lane));
                }

                boundaries += ((long)steps * lanes) - matches;
            }
        }

        for (; i < values.Length; i++)
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
    /// four bytes is a call with a length check in front of it, for a comparison the width makes
    /// into one instruction.
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

    /// <summary>
    /// A run starts where validity changes, or between two valid rows that differ: counted as
    /// <c>(valid ^ previous) | (valid &amp; previous &amp; differs)</c> rather than behind branches.
    /// </summary>
    private static long DifferingNullable<T>(
        ReadOnlySpan<T> values, in ValidityMask mask, int start, int count)
        where T : unmanaged, IEquatable<T>
    {
        int firstBit = mask.BitOffset + start;
        ReadOnlySpan<byte> bits = mask.Bits[..((firstBit + count + 7) >> 3)];
        ref byte bit = ref MemoryMarshal.GetReference(bits);
        ref T value = ref MemoryMarshal.GetReference(values[..count]);
        nint boundaries = 0;
        nint previous = BitAt(ref bit, firstBit);
        for (nint i = 1; i < count; i++)
        {
            nint valid = BitAt(ref bit, firstBit + i);
            nint differs = Unsafe.Add(ref value, i).Equals(Unsafe.Add(ref value, i - 1)) ? 0 : 1;
            boundaries += (valid ^ previous) | (valid & previous & differs);
            previous = valid;
        }

        return boundaries;
    }

    /// <summary>Rows from which <see cref="CountWidths{T}"/> spreads a range over four pairs of histograms.</summary>
    private const int LanesFrom = 256;

    /// <summary>A bitmap of one valid row, read at bit 0 for every row of a column without nulls.</summary>
    private static ReadOnlySpan<byte> Everything => [0x01];

    /// <summary>Bit <paramref name="at"/> of an LSB-first bitmap the caller has bounded, as 0 or 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint BitAt(ref byte bits, nint at) => (Unsafe.Add(ref bits, at >> 3) >> (int)(at & 7)) & 1;

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
    /// <c>w ^ ((w &lt;&lt; 1) | previous)</c> has a set bit exactly where a row differs from the one
    /// before it, so a whole word's boundaries are one xor and a popcount. Asking the question a
    /// bit at a time is what dominates a boolean write, which is why the word trick is here rather
    /// than left to a later vector pass.
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

            // The range's own first row has no predecessor here -- `Leading` already decided it
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
    /// Equal views prove equal values. When the views differ and the lengths agree the bytes are
    /// compared, so that two equal out-of-line strings at different offsets — the common case after
    /// a compaction — do not split a run. Different lengths are different values and need no
    /// comparison at all.
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
        //
        // One comparison answers both questions while the column can still be sorted. A run
        // boundary asks "are these two values different"; the order asks "is the second below the
        // first"; and `SequenceCompareTo` answers the first by answering the second. Asking them
        // separately -- `SameValue` and then a comparison of its own -- doubles the byte compare on
        // exactly the column that pays it most, one whose values are all the same length and all
        // distinct. Equal views are equal values and need neither.
        ReadOnlySpan<ulong> pairs = MemoryMarshal.Cast<byte, ulong>(views);

        // Resolved once: which buffer a row's bytes live in is a property of the node, and a column
        // that keeps them in one buffer -- which is nearly every one -- can be read straight from
        // it. A column with several falls through to the slow pair below, which asks the node.
        bool oneHeap = node.DataBufferCount == 1;
        ReadOnlySpan<byte> heap = oneHeap ? node.GetDataBuffer(0).Span : default;
        ReadOnlySpan<VortexBuffer> buffers = node.DataBuffers;
        bool allValid = mask.AllValid;
        bool previousValid = firstValid;
        bool tracking = !stats.OrderUntracked && !stats.Unsorted;
        bool repeats = false;

        // Counted here and added once: an increment through the reference is a store the next
        // row's increment reads back, a chain through memory on a column where every row is a
        // boundary.
        long boundaries = 0;
        ReadOnlySpan<byte> bits = allValid ? default : mask.Bits;
        for (int i = 1; i < count; i++)
        {
            if (!tracking && !mask.AllInvalid)
            {
                // Past the order, the pairs the views settle are counted by a loop of their own
                // that calls nothing; the pair it stops at is this loop's. Every row it passed
                // was valid.
                int settled = ViewBoundaries<ViewsOnly>(
                    pairs, bits, mask.BitOffset, start + i, start + count, heap, ref boundaries, ref repeats);
                if (oneHeap && settled < start + count)
                {
                    // Stopped at a pair only its bytes settle: the loop that reads them takes over.
                    settled = ViewBoundaries<WithBytes>(
                        pairs, bits, mask.BitOffset, settled, start + count, heap, ref boundaries, ref repeats);
                }

                settled -= start;
                previousValid |= settled > i;
                i = settled;
                if (i == count)
                {
                    break;
                }
            }

            int row = start + i;
            bool valid = allValid || mask.IsValid(row);
            if (!allValid && valid != previousValid)
            {
                boundaries++;
                previousValid = valid;
                if (tracking && !valid)
                {
                    // A null after a value: nulls sort below every value.
                    stats.Unsorted = true;
                    tracking = false;
                }

                continue;
            }

            previousValid = valid;
            if (!valid)
            {
                // Two nulls are two equal rows. Kept in a local and folded in once at the end, so
                // that neither this nor the equal-views case below carries a branch on `tracking`.
                repeats = true;
                continue;
            }

            int a = (row - 1) * 2;
            int b = row * 2;
            if (pairs[a] == pairs[b] && pairs[a + 1] == pairs[b + 1])
            {
                repeats = true;
                continue;
            }

            if (!tracking)
            {
                // Two inline values of one size are equal when the bytes of that size are: they
                // are in the views, so the answer is two masked word compares and no call.
                uint size = (uint)pairs[a];
                bool same = size == (uint)pairs[b]
                    && (size <= 12
                        ? InlineEqual(pairs[a], pairs[a + 1], pairs[b], pairs[b + 1], (int)size)
                        : (pairs[a] >> 32) == (pairs[b] >> 32) && (oneHeap
                            ? Compare(Value(views, heap, row - 1), Value(views, heap, row)) == 0
                            : SameValue(buffers, views, pairs, row - 1, row)));
                if (!same)
                {
                    boundaries++;
                }

                continue;
            }

            int order = oneHeap
                ? Order(views, heap, pairs, row - 1, row)
                : Compare(Value(buffers, views, row - 1), Value(buffers, views, row));
            if (order == 0)
            {
                repeats = true;
                continue;
            }

            boundaries++;
            if (order > 0)
            {
                stats.Unsorted = true;
                tracking = false;
            }
        }

        stats.RunBoundaries += boundaries;
        stats.Repeats |= repeats;

        int last = start + count - 1;
        bool lastValid = mask.IsValid(last);
        Store(previous, lastValid, lastValid ? Value(node, views, last) : default);
    }

    /// <summary>
    /// Counts the run boundaries of rows <c>[row, end)</c> of a column whose order is no longer
    /// tracked, each row against the one before it, for as long as both are valid and their views
    /// settle the question; returns the first row they do not, or <paramref name="end"/>.
    /// </summary>
    /// <remarks>
    /// Views settle every pair but two out-of-line values of one size and prefix, which only their
    /// bytes can tell apart. <see cref="ViewsOnly"/> stops there; <see cref="WithBytes"/> compares
    /// them itself over the column's one data buffer, <paramref name="heap"/>, word by word past
    /// the prefix, and stops only at a value in another buffer or too near the end of this one.
    /// Two bodies from one source, so that the loop of the common case keeps its own shape: the
    /// byte compare in it slows the pairs that never reach it. The loop calls nothing, so that what
    /// it holds stays in registers.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ViewBoundaries<TMode>(
        ReadOnlySpan<ulong> pairs, ReadOnlySpan<byte> bits, int bitOffset, int row, int end,
        ReadOnlySpan<byte> heap, ref long boundaries, ref bool repeats)
        where TMode : struct
    {
        long found = 0;
        bool repeat = false;
        for (; row < end; row++)
        {
            if (!bits.IsEmpty
                && !(CanonicalSupport.BitAt(bits, bitOffset + row - 1)
                    && CanonicalSupport.BitAt(bits, bitOffset + row)))
            {
                break;
            }

            ulong a0 = pairs[(row - 1) * 2];
            ulong a1 = pairs[((row - 1) * 2) + 1];
            ulong b0 = pairs[row * 2];
            ulong b1 = pairs[(row * 2) + 1];
            if (a0 == b0 && a1 == b1)
            {
                repeat = true;
                continue;
            }

            uint size = (uint)a0;
            if (size != (uint)b0)
            {
                found++;
                continue;
            }

            if (size <= 12)
            {
                found += InlineEqual(a0, a1, b0, b1, (int)size) ? 0 : 1;
                continue;
            }

            // Two out-of-line values of one size whose views' prefixes differ are different values;
            // only equal prefixes need the bytes.
            if ((a0 >> 32) != (b0 >> 32))
            {
                found++;
                continue;
            }

            if (typeof(TMode) == typeof(ViewsOnly))
            {
                break;
            }

            // The low half of a view's second word is its buffer, the high half its offset.
            long from = (long)(a1 >> 32);
            long to = (long)(b1 >> 32);
            if (((uint)a1 | (uint)b1) != 0 || from + size > heap.Length || to + size > heap.Length)
            {
                break;
            }

            // Past the prefix, a word at a time and the last word overlapping, no early exit: a
            // value is a few words, and a branch per word would cost more than the words.
            ref byte x = ref Unsafe.Add(ref MemoryMarshal.GetReference(heap), (nint)from + 4);
            ref byte y = ref Unsafe.Add(ref MemoryMarshal.GetReference(heap), (nint)to + 4);
            nint rest = (nint)size - 4;
            ulong differ = 0;
            for (nint k = 0; k + 8 < rest; k += 8)
            {
                differ |= Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref x, k)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref y, k));
            }

            differ |= Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref x, rest - 8)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref y, rest - 8));
            found += differ != 0 ? 1 : 0;
        }

        boundaries += found;
        repeats |= repeat;
        return row;
    }

    /// <summary>The <see cref="ViewBoundaries{TMode}"/> that leaves to its caller the pairs only bytes settle.</summary>
    private readonly struct ViewsOnly;

    /// <summary>The <see cref="ViewBoundaries{TMode}"/> that settles them over the column's one data buffer.</summary>
    private readonly struct WithBytes;

    /// <summary>
    /// Whether two inline views of <paramref name="size"/> bytes hold the same value: their first
    /// <paramref name="size"/> payload bytes, read as the view's two little-endian words.
    /// </summary>
    /// <remarks>
    /// The payload starts at the fifth byte of the view, so its first four bytes are the high half
    /// of the first word and the next eight the second word. The bytes past the size are masked
    /// out rather than trusted to be zero: a view built elsewhere need not have cleared them.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool InlineEqual(ulong a0, ulong a1, ulong b0, ulong b1, int size)
    {
        ulong head = (a0 ^ b0) >> 32;
        ulong tail = a1 ^ b1;
        ulong headMask = size >= 4 ? uint.MaxValue : (1UL << (8 * size)) - 1;
        ulong tailMask = size >= 12 ? ulong.MaxValue : size <= 4 ? 0 : (1UL << (8 * (size - 4))) - 1;
        return ((head & headMask) | (tail & tailMask)) == 0;
    }

    /// <summary>
    /// How row <paramref name="a"/>'s value compares with row <paramref name="b"/>'s, bytewise, over
    /// a column that keeps its bytes in one buffer: from their views when those decide it.
    /// </summary>
    /// <remarks>
    /// A view holds a value's first four bytes whatever its length, and the whole of a value of
    /// twelve bytes or fewer. So two inline values compare as their payloads read big-endian, then
    /// their lengths; two values whose first four bytes differ compare as those; and only two whose
    /// first four agree, one of them out of line, read their bytes.
    /// </remarks>
    private static int Order(ReadOnlySpan<byte> views, ReadOnlySpan<byte> heap, ReadOnlySpan<ulong> pairs, int a, int b)
    {
        ulong a0 = pairs[a * 2];
        ulong b0 = pairs[b * 2];
        uint aSize = (uint)a0;
        uint bSize = (uint)b0;
        ulong aHead = BinaryPrimitives.ReverseEndianness(a0 >> 32) >> 32;
        ulong bHead = BinaryPrimitives.ReverseEndianness(b0 >> 32) >> 32;
        ulong headMask = uint.MaxValue ^ (uint.MaxValue >> (8 * (int)Math.Min(Math.Min(aSize, bSize), 4u)));
        if (Math.Min(aSize, bSize) < 4)
        {
            // A value shorter than its prefix field: padding past it is not the value's.
            aHead &= headMask;
            bHead &= headMask;
        }

        if (aHead != bHead)
        {
            return aHead < bHead ? -1 : 1;
        }

        if ((aSize | bSize) <= 12)
        {
            ulong aTail = BinaryPrimitives.ReverseEndianness(pairs[(a * 2) + 1]);
            ulong bTail = BinaryPrimitives.ReverseEndianness(pairs[(b * 2) + 1]);
            int common = (int)Math.Min(aSize, bSize);
            ulong tailMask = common <= 4 ? 0 : ulong.MaxValue << (8 * (12 - common));
            aTail &= tailMask;
            bTail &= tailMask;
            if (aTail != bTail)
            {
                return aTail < bTail ? -1 : 1;
            }

            return aSize.CompareTo(bSize);
        }

        // The first four bytes were found equal in the views.
        return Compare(Value(views, heap, a), Value(views, heap, b), from: 4);
    }

    /// <summary>
    /// Two byte strings compared as <c>SequenceCompareTo</c> does, from byte <paramref name="from"/>
    /// on, the bytes before it known equal, and no call.
    /// </summary>
    /// <remarks>
    /// Sixteen bytes a step as two word differences, which on Arm is cheaper than a vector compare
    /// reduced to a test; the first word that differs is ordered read big-endian. The tail is the
    /// last word of the shorter string, overlapping bytes already found equal.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int from = 0)
    {
        int shorter = Math.Min(a.Length, b.Length);
        ref byte x = ref MemoryMarshal.GetReference(a);
        ref byte y = ref MemoryMarshal.GetReference(b);
        int i = from;
        for (; i <= shorter - 16; i += 16)
        {
            ulong x0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref x, i));
            ulong y0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref y, i));
            ulong x1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref x, i + 8));
            ulong y1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref y, i + 8));
            if (((x0 ^ y0) | (x1 ^ y1)) != 0)
            {
                return x0 != y0 ? WordOrder(x0, y0) : WordOrder(x1, y1);
            }
        }

        if (i < shorter)
        {
            if (shorter < 8)
            {
                for (; i < shorter; i++)
                {
                    byte p = Unsafe.Add(ref x, i);
                    byte q = Unsafe.Add(ref y, i);
                    if (p != q)
                    {
                        return p < q ? -1 : 1;
                    }
                }
            }
            else
            {
                if (shorter - i > 8)
                {
                    ulong p = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref x, i));
                    ulong q = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref y, i));
                    if (p != q)
                    {
                        return WordOrder(p, q);
                    }
                }

                ulong last = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref x, shorter - 8));
                ulong other = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref y, shorter - 8));
                if (last != other)
                {
                    return WordOrder(last, other);
                }
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>Two words that differ, ordered as their bytes are in memory.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WordOrder(ulong a, ulong b) =>
        BinaryPrimitives.ReverseEndianness(a) < BinaryPrimitives.ReverseEndianness(b) ? -1 : 1;

    /// <summary>
    /// Whether two rows of a varbinview hold the same bytes, their views already known to differ.
    /// </summary>
    /// <remarks>
    /// Different sizes are different values and need no comparison at all — which is the whole of
    /// the answer for most pairs. Only two values of the same length that sit at different offsets
    /// reach the byte comparison, and that is the case it exists for: two equal out-of-line strings
    /// written twice must not split a run.
    /// </remarks>
    private static bool SameValue(
        ReadOnlySpan<VortexBuffer> buffers, ReadOnlySpan<byte> views, ReadOnlySpan<ulong> pairs, int a, int b)
    {
        // The size is the view's low four bytes, so it is the low half of the first word.
        if ((uint)pairs[a * 2] != (uint)pairs[b * 2])
        {
            return false;
        }

        return Value(buffers, views, a).SequenceEqual(Value(buffers, views, b));
    }

    /// <summary>One varbinview row's bytes, inline or through its data buffer.</summary>
    internal static ReadOnlySpan<byte> Value(CanonicalNode node, ReadOnlySpan<byte> views, int row) =>
        Value(node.DataBuffers, views, row);

    /// <summary>
    /// The same, over the node's data buffers resolved once: a loop over rows that asks the node
    /// for a row's buffer pays a kind check and a record lookup per row.
    /// </summary>
    internal static ReadOnlySpan<byte> Value(ReadOnlySpan<VortexBuffer> buffers, ReadOnlySpan<byte> views, int row)
    {
        ReadOnlySpan<byte> view = views.Slice(row * 16, 16);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        if (size <= 12)
        {
            return view.Slice(4, size);
        }

        int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
        if ((uint)buffer >= (uint)buffers.Length)
        {
            return ArraysThrow.BufferIndex(buffer, buffers.Length).Span;
        }

        return buffers[buffer].Span.Slice(offset, size);
    }

    /// <summary>
    /// The same, over a heap already resolved for a column that keeps its bytes in one buffer.
    /// </summary>
    /// <remarks>
    /// Which buffer a row lives in belongs to the node, not to the row, and nearly every string
    /// column has exactly one. Asking the node per row costs a kind check, a bound and a span built
    /// over native memory, twice for every pair the run pass compares.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> Value(
        ReadOnlySpan<byte> views, ReadOnlySpan<byte> heap, int row)
    {
        ReadOnlySpan<byte> view = views.Slice(row * 16, 16);
        int size = BinaryPrimitives.ReadInt32LittleEndian(view);
        return size <= 12
            ? view.Slice(4, size)
            : heap.Slice(BinaryPrimitives.ReadInt32LittleEndian(view[12..16]), size);
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

    /// <summary>Dispatches on the physical type once, then runs a monomorphic loop.</summary>
    /// <remarks>
    /// <paramref name="widths"/> is the pair of histograms of <see cref="BitPackWidths"/> when the
    /// caller wants them and empty otherwise, and the integer loops branch on it once rather than
    /// per row. The widths belong in this walk precisely because the value is already in a register
    /// here, so counting it costs a leading-zero count and an increment instead of a whole second
    /// walk over the column by `BitPackPlan`.
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

    /// <summary>
    /// The raw width of one value, and its zigzag width when <paramref name="zigzag"/>, counted
    /// into a pair of histograms by <paramref name="valid"/>: one for a value, zero for a null.
    /// </summary>
    /// <remarks>
    /// The raw width is taken on the unsigned reading of the bits, which is what the packer writes:
    /// a signed <c>-1</c> is an <c>sbyte</c> of eight set bits, so its raw width is eight and not
    /// sixty-four. The zigzag form interleaves the sign so that magnitude rather than position
    /// decides the width, and the mask keeps it inside the element — the same two expressions
    /// <c>BitPackPlan</c> prices with, so the histograms this produces are the ones it would
    /// otherwise walk the column for.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Count<T>(ref int histograms, T value, int valid, int elementBits, ulong top, bool zigzag)
        where T : unmanaged, IBinaryInteger<T>
    {
        ulong bits = ulong.CreateTruncating(value) & top;
        Unsafe.Add(ref histograms, 64 - BitOperations.LeadingZeroCount(bits)) += valid;
        if (zigzag)
        {
            ulong sign = 0UL - ((bits >> (elementBits - 1)) & 1);
            ulong folded = ((bits << 1) ^ sign) & top;
            Unsafe.Add(ref histograms, BitPackWidths.ZigZagOffset + 64 - BitOperations.LeadingZeroCount(folded)) += valid;
        }
    }

    // ------------------------------------------------------------------------------------ order
    //
    // The file statistics' is_sorted / is_strict_sorted, tracked as the reference implementation
    // computes them: nulls below every value, equal neighbours allowed by the first flag and
    // refused by the second. A block is fed range by range, so a range's first row is judged
    // against the row before it (the seam) and its own rows against each other; the seam of a
    // block's first range is the block's, and the merge reads it.

    /// <summary>Whether every null of the range comes before every value.</summary>
    private static bool NullsFirst(in ValidityMask mask, int start, int count)
    {
        bool sawValue = false;
        for (int i = 0; i < count; i++)
        {
            bool valid = mask.IsValid(start + i);
            if (!valid && sawValue)
            {
                return false;
            }

            sawValue |= valid;
        }

        return true;
    }

    /// <summary>
    /// The seam of a primitive range with the row before it: a null after a value breaks the
    /// order, two nulls repeat, two values compare in the type's domain.
    /// </summary>
    private static void OrderSeam(
        PType ptype, ReadOnlySpan<byte> first, bool firstValid, bool startsBlock,
        ref BlockStats stats, PreviousRow? previous)
    {
        if (previous is null || !previous.HasValue)
        {
            return;
        }

        if (previous.IsNull)
        {
            stats.NoteOrderSeam(firstValid ? 1 : 0, startsBlock);
            return;
        }

        if (!firstValid)
        {
            stats.NoteOrderSeam(-1, startsBlock);
            return;
        }

        if (!TryOrder(ptype, first, previous.Bytes, out int order))
        {
            stats.OrderUntracked = true;
            return;
        }

        stats.NoteOrderSeam(order, startsBlock);
    }

    /// <summary>The seam of a byte-string range with the row before it, bytewise.</summary>
    private static void OrderSeamBytes(
        ReadOnlySpan<byte> first, bool firstValid, bool startsBlock, ref BlockStats stats,
        PreviousRow? previous)
    {
        if (previous is null || !previous.HasValue)
        {
            return;
        }

        if (previous.IsNull)
        {
            stats.NoteOrderSeam(firstValid ? 1 : 0, startsBlock);
            return;
        }

        if (!firstValid)
        {
            stats.NoteOrderSeam(-1, startsBlock);
            return;
        }

        stats.NoteOrderSeam(Math.Sign(first.SequenceCompareTo(previous.Bytes)), startsBlock);
    }

    /// <summary>The sign of <c>a - b</c> for two elements of a primitive type; false on a NaN.</summary>
    private static bool TryOrder(PType ptype, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, out int order)
    {
        if (ptype.IsSignedInteger())
        {
            order = ElementSigned(a, ptype).CompareTo(ElementSigned(b, ptype));
            return true;
        }

        if (ptype.IsUnsignedInteger())
        {
            order = ElementUnsigned(a, ptype).CompareTo(ElementUnsigned(b, ptype));
            return true;
        }

        double x = ReadFloat(a, ptype);
        double y = ReadFloat(b, ptype);
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            order = 0;
            return false;
        }

        // -0.0 and +0.0 compare equal here, as they do for the reference's `is_sorted`.
        order = x.CompareTo(y);
        return true;
    }

    private static double ReadFloat(ReadOnlySpan<byte> bytes, PType ptype) => ptype switch
    {
        PType.F16 => (double)BinaryPrimitives.ReadHalfLittleEndian(bytes),
        PType.F32 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
    };

    /// <summary>
    /// The order of a range's rows, in a second walk over the range: the bounds loop keeps the
    /// two-compare shape the JIT vectorises (see <see cref="Signed{T}"/>), and this one runs only
    /// while the column can still be sorted -- a witness is sticky, so an unsorted column pays it
    /// once, for the rows up to its first descent.
    /// </summary>
    private static void Order<T>(ReadOnlySpan<T> values, in ValidityMask mask, int start, ref BlockStats stats)
        where T : unmanaged, INumber<T>
    {
        if (stats.OrderUntracked || stats.Unsorted || values.Length == 0)
        {
            return;
        }

        if (mask.AllValid)
        {
            OrderValues(values, ref stats);
            return;
        }

        bool sawValue = false;
        bool previousNull = false;
        T last = default;
        for (int i = 0; i < values.Length; i++)
        {
            if (!mask.IsValid(start + i))
            {
                if (sawValue)
                {
                    stats.Unsorted = true;
                    return;
                }

                if (previousNull)
                {
                    stats.Repeats = true;
                }

                previousNull = true;
                continue;
            }

            previousNull = false;
            T value = values[i];
            if (T.IsNaN(value))
            {
                stats.OrderUntracked = true;
                return;
            }

            if (sawValue)
            {
                if (value < last)
                {
                    stats.Unsorted = true;
                    return;
                }

                if (value == last)
                {
                    stats.Repeats = true;
                }
            }

            sawValue = true;
            last = value;
        }
    }

    /// <summary>
    /// The pairs of an all-valid range: two compares per value, both of them predictable, and the
    /// witnesses kept in locals so the loop writes nothing through the <c>ref</c>.
    /// </summary>
    /// <remarks>
    /// Scalar where the register is narrow. Comparing a vector of values against the vector one
    /// element behind it reads well and measures badly there: a 128-bit register holds two
    /// <see cref="long"/>s, so each iteration pays two span constructions with their bounds checks
    /// and three vector compares to advance two elements. Against a loop whose every branch is
    /// perfectly predicted -- a sorted column never takes the first, an unsorted one takes it once
    /// and leaves -- the vector form is the slower of the two.
    /// <para>
    /// A NaN is not ordered by this comparison and would sail through as "not below", so it is
    /// asked for by name. The check folds away for an integer <typeparamref name="T"/>, where
    /// <c>IsNaN</c> is a constant false.
    /// </para>
    /// </remarks>
    private static void OrderValues<T>(ReadOnlySpan<T> values, ref BlockStats stats)
        where T : unmanaged, INumber<T>
    {
        bool repeats = false;
        T last = values[0];
        if (T.IsNaN(last))
        {
            stats.OrderUntracked = true;
            return;
        }

        int i = 1;
        if (Vector128.IsHardwareAccelerated && Vector128<T>.IsSupported && Vector128<T>.Count >= 4
)
        {
            // The lanes clear every window in which nothing happens; the scalar loop takes over at
            // the first one where something might -- a descent, a NaN -- and decides it row by row,
            // in the order the scalar pass would have met it.
            i = OrderLanes(values, ref repeats);
            last = values[i - 1];
        }

        for (; i < values.Length; i++)
        {
            T value = values[i];
            if (value < last)
            {
                stats.Unsorted = true;
                return;
            }

            if (value == last)
            {
                repeats = true;
            }
            else if (T.IsNaN(value))
            {
                // Neither below nor equal: the only value that compares false against both.
                stats.OrderUntracked = true;
                return;
            }

            last = value;
        }

        stats.Repeats |= repeats;
    }

    /// <summary>
    /// The pairs of an all-valid range, a register at a time: each element against the one before
    /// it, by a second load one element behind, so no lane crosses from one iteration to the next.
    /// </summary>
    /// <returns>
    /// Where the scalar loop resumes: the end of the last whole window, or the start of the first
    /// window where a lane fell or a NaN showed -- the scalar loop meets it there in row order.
    /// </returns>
    /// <remarks>
    /// Four lanes or more only. Two-lane registers -- 64-bit values -- are slower than the
    /// perfectly predicted scalar loop, and are left to it.
    /// </remarks>
    private static int OrderLanes<T>(ReadOnlySpan<T> values, ref bool repeats)
        where T : unmanaged, INumber<T>
    {
        int lanes = Vector128<T>.Count;
        ref T head = ref MemoryMarshal.GetReference(values);
        Vector128<T> equal = Vector128<T>.Zero;
        int i = 1;
        for (; i + lanes <= values.Length; i += lanes)
        {
            Vector128<T> current = Vector128.LoadUnsafe(ref head, (nuint)i);
            Vector128<T> previous = Vector128.LoadUnsafe(ref head, (nuint)(i - 1));

            // A NaN is unequal to itself; a descent is a lane below its predecessor. Either sends
            // the window back to the scalar loop.
            if (Vector128.LessThanAny(current, previous) || !Vector128.EqualsAll(current, current))
            {
                break;
            }

            equal |= Vector128.Equals(current, previous);
        }

        repeats |= equal != Vector128<T>.Zero;
        return i;
    }

    /// <summary>
    /// The extremes of a range, four to sixteen lanes at a time.
    /// </summary>
    /// <param name="values">The range; every element counts, so the caller owes it all-valid.</param>
    /// <param name="min">The smallest element.</param>
    /// <param name="max">The largest.</param>
    /// <remarks>
    /// Two accumulators per extreme, because the loop is latency-bound and not throughput-bound: a
    /// single running vector makes every iteration wait for the previous one's `Min`, and the
    /// second pair hides that behind the load. The horizontal reduce runs once per range, not once
    /// per iteration, and the tail is the scalar twin this method keeps -- which is also the whole
    /// method under <c>DOTNET_EnableHWIntrinsic=0</c>, where
    /// <see cref="Vector128.IsHardwareAccelerated"/> is false and the suite runs it.
    /// <para>
    /// A 64-bit lane has no NEON minimum and is emulated as a compare and a select, so the gain
    /// there is smaller than on the narrower widths; it is still a gain, and writing the kernel
    /// per width to say so would buy nothing the measurement does not.
    /// </para>
    /// </remarks>
    private static void Bounds<T>(ReadOnlySpan<T> values, out T min, out T max)
        where T : unmanaged, INumber<T>, IMinMaxValue<T>
    {
        min = T.MaxValue;
        max = T.MinValue;
        int i = 0;
        int lanes = Vector128<T>.IsSupported ? Vector128<T>.Count : 0;
        if (Vector128.IsHardwareAccelerated && lanes > 0 && values.Length >= lanes * 2)
        {
            ref T head = ref MemoryMarshal.GetReference(values);
            Vector128<T> lowA = Vector128.LoadUnsafe(ref head);
            Vector128<T> lowB = Vector128.LoadUnsafe(ref head, (nuint)lanes);
            Vector128<T> highA = lowA;
            Vector128<T> highB = lowB;

            int step = lanes * 2;
            int last = values.Length - step;
            for (i = step; i <= last; i += step)
            {
                Vector128<T> a = Vector128.LoadUnsafe(ref head, (nuint)i);
                Vector128<T> b = Vector128.LoadUnsafe(ref head, (nuint)(i + lanes));
                lowA = Vector128.Min(lowA, a);
                lowB = Vector128.Min(lowB, b);
                highA = Vector128.Max(highA, a);
                highB = Vector128.Max(highB, b);
            }

            Vector128<T> low = Vector128.Min(lowA, lowB);
            Vector128<T> high = Vector128.Max(highA, highB);
            for (int lane = 0; lane < lanes; lane++)
            {
                T small = low.GetElement(lane);
                T large = high.GetElement(lane);
                if (small < min)
                {
                    min = small;
                }

                if (large > max)
                {
                    max = large;
                }
            }
        }

        for (; i < values.Length; i++)
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
        }
    }

    private static void Signed<T>(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int start, int count, ref BlockStats stats,
        Span<int> widths)
        where T : unmanaged, IBinaryInteger<T>, ISignedNumber<T>, IMinMaxValue<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes).Slice(start, count);
        T min = T.MaxValue;
        T max = T.MinValue;
        Order(values, in mask, start, ref stats);

        // The bounds loop holds two compares per value and nothing else, which is the shape the JIT
        // vectorises. An `if (counting)` inside it costs the loop that vectorisation even on the
        // rows where the branch is never taken, which is most of the cost of a column that reaches
        // this loop at all. The widths are counted in a second walk over the same range, which is a
        // block or less and still in cache, and only while a plan will read them.
        if (mask.AllValid)
        {
            Bounds(values, out min, out max);
            stats.MergeSigned(long.CreateTruncating(min), long.CreateTruncating(max));
            if (!widths.IsEmpty)
            {
                CountWidths<T>(values, in mask, start, widths, zigzag: true);
            }

            return;
        }

        if (mask.AllInvalid)
        {
            return;
        }

        if (MaskedBounds(values, in mask, start, out min, out max))
        {
            stats.MergeSigned(long.CreateTruncating(min), long.CreateTruncating(max));
        }

        if (!widths.IsEmpty)
        {
            CountWidths<T>(values, in mask, start, widths, zigzag: true);
        }
    }

    /// <summary>
    /// The bounds of a range's valid rows, each null standing for the far end of either bound
    /// through a select rather than behind a branch, two rows a step on two pairs of bounds.
    /// </summary>
    /// <returns>Whether a row of the range is valid.</returns>
    private static bool MaskedBounds<T>(ReadOnlySpan<T> values, in ValidityMask mask, int start, out T min, out T max)
        where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
    {
        int count = values.Length;
        int firstBit = mask.BitOffset + start;
        ref byte bit = ref MemoryMarshal.GetReference(mask.Bits[..((firstBit + count + 7) >> 3)]);
        ref T value = ref MemoryMarshal.GetReference(values);
        T low = T.MaxValue;
        T otherLow = T.MaxValue;
        T high = T.MinValue;
        T otherHigh = T.MinValue;
        nint any = 0;
        nint i = 0;
        for (; i + 1 < count; i += 2)
        {
            nint first = BitAt(ref bit, firstBit + i);
            nint second = BitAt(ref bit, firstBit + i + 1);
            T a = Unsafe.Add(ref value, i);
            T b = Unsafe.Add(ref value, i + 1);
            T aLow = first != 0 ? a : T.MaxValue;
            T aHigh = first != 0 ? a : T.MinValue;
            T bLow = second != 0 ? b : T.MaxValue;
            T bHigh = second != 0 ? b : T.MinValue;
            low = aLow < low ? aLow : low;
            high = aHigh > high ? aHigh : high;
            otherLow = bLow < otherLow ? bLow : otherLow;
            otherHigh = bHigh > otherHigh ? bHigh : otherHigh;
            any |= first | second;
        }

        if (i < count)
        {
            nint last = BitAt(ref bit, firstBit + i);
            T a = Unsafe.Add(ref value, i);
            T aLow = last != 0 ? a : T.MaxValue;
            T aHigh = last != 0 ? a : T.MinValue;
            low = aLow < low ? aLow : low;
            high = aHigh > high ? aHigh : high;
            any |= last;
        }

        min = otherLow < low ? otherLow : low;
        max = otherHigh > high ? otherHigh : high;
        return any != 0;
    }

    /// <summary>
    /// The width histograms of rows <c>[start, start + count)</c> alone -- an integer primitive's,
    /// valid rows by their bits and null rows as width zero in both domains -- for a block whose
    /// statistics already hold them.
    /// </summary>
    /// <remarks>
    /// The carried tail, counted late. A plan holds at the end of a chunk, and the next chunk's
    /// first block is already open with the rows the emission carried: rows the pass walked before
    /// anyone wanted widths. Without them that block's histogram is partial, the chunk it opens
    /// walks for its histogram all the same, and the count on the chunk's every other block is
    /// paid for nobody. Counting the carried rows here, when the table sees them again
    /// (`ColumnWriter.Reprobe`), completes the block, and the chunk it opens is the first to read
    /// its widths instead of the second.
    /// </remarks>
    /// <param name="arena">The arena holding the column.</param>
    /// <param name="nodeIndex">The column.</param>
    /// <param name="start">First row of the range, inside the node.</param>
    /// <param name="count">How many rows.</param>
    /// <param name="widths">The block's pair of histograms (<see cref="BitPackWidths"/>).</param>
    internal static void Widths(
        CanonicalArena arena, int nodeIndex, int start, int count, Span<int> widths)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (count <= 0 || node.Kind != CanonicalKind.Primitive || !node.PType.IsInteger())
        {
            return;
        }

        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        if (!mask.AllValid)
        {
            int nulls = 0;
            for (int i = 0; i < count; i++)
            {
                if (!mask.IsValid(start + i))
                {
                    nulls++;
                }
            }

            widths[0] += nulls;
            widths[BitPackWidths.ZigZagOffset] += nulls;
        }

        ReadOnlySpan<byte> bytes = node.Values.Span;
        switch (node.PType)
        {
            case PType.I8: CountWidths(Range<sbyte>(bytes, start, count), in mask, start, widths, zigzag: true); return;
            case PType.I16: CountWidths(Range<short>(bytes, start, count), in mask, start, widths, zigzag: true); return;
            case PType.I32: CountWidths(Range<int>(bytes, start, count), in mask, start, widths, zigzag: true); return;
            case PType.I64: CountWidths(Range<long>(bytes, start, count), in mask, start, widths, zigzag: true); return;
            case PType.U8: CountWidths(Range<byte>(bytes, start, count), in mask, start, widths, zigzag: false); return;
            case PType.U16: CountWidths(Range<ushort>(bytes, start, count), in mask, start, widths, zigzag: false); return;
            case PType.U32: CountWidths(Range<uint>(bytes, start, count), in mask, start, widths, zigzag: false); return;
            default: CountWidths(Range<ulong>(bytes, start, count), in mask, start, widths, zigzag: false); return;
        }
    }

    /// <summary>Rows <c>[start, start + count)</c> of a value buffer, as <c>T</c>.</summary>
    private static ReadOnlySpan<T> Range<T>(ReadOnlySpan<byte> bytes, int start, int count)
        where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(bytes).Slice(start, count);

    /// <summary>
    /// The width histograms over a range the bounds loop has just read, valid rows only, the
    /// element's bits masked to its width so a signed value is sized as the packer sees it.
    /// </summary>
    /// <remarks>
    /// Only the domains a plan can read. Zigzag is offered to signed columns alone
    /// (<c>BitPackPlan.TryBuild</c> prices it under <c>signed &amp;&amp; zigzag</c>), so on an
    /// unsigned column the zigzag half would be a leading-zero count and an increment per row for
    /// nobody -- enough to show on the write of a plain unsigned column, for identical bytes.
    /// </remarks>
    /// <param name="values">The range's values, already sliced to it.</param>
    /// <param name="mask">The column's validity.</param>
    /// <param name="start">The range's first row inside the node, where <paramref name="mask"/> is read.</param>
    /// <param name="widths">The pair of histograms.</param>
    /// <param name="zigzag">Whether the zigzag half is counted at all: the column is signed.</param>
    private static void CountWidths<T>(
        ReadOnlySpan<T> values, in ValidityMask mask, int start, Span<int> widths, bool zigzag)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (mask.AllInvalid)
        {
            return;
        }

        // A null row adds its validity bit, zero, to the counter its garbage would have moved,
        // rather than skipping it behind a branch; a column without nulls reads the one bit of a
        // bitmap of its own at every row.
        int count = values.Length;
        int firstBit = mask.BitOffset + start;
        bool masked = !mask.AllValid;
        ReadOnlySpan<byte> bits = masked ? mask.Bits[..((firstBit + count + 7) >> 3)] : Everything;
        ref byte bit = ref MemoryMarshal.GetReference(bits);
        nint rowMask = masked ? -1 : 0;
        int used = zigzag ? BitPackWidths.Length : BitPackWidths.Domain;
        ref int own = ref MemoryMarshal.GetReference(widths[..used]);
        if (count < LanesFrom)
        {
            WidthLanes(values, ref bit, firstBit, rowMask, zigzag, ref own, ref own, ref own, ref own);
            return;
        }

        // Four pairs of histograms taken in turn, folded at the end. A run of equal widths
        // increments one counter over and over, each increment waiting on the store before it;
        // four counters make the chain four times shorter, for a fold per range.
        Span<int> spare = stackalloc int[3 * BitPackWidths.Length];
        spare.Clear();
        ref int second = ref MemoryMarshal.GetReference(spare);
        ref int third = ref Unsafe.Add(ref second, BitPackWidths.Length);
        ref int fourth = ref Unsafe.Add(ref second, 2 * BitPackWidths.Length);
        WidthLanes(values, ref bit, firstBit, rowMask, zigzag, ref own, ref second, ref third, ref fourth);
        for (int w = 0; w < used; w++)
        {
            Unsafe.Add(ref own, w) += Unsafe.Add(ref second, w) + Unsafe.Add(ref third, w) + Unsafe.Add(ref fourth, w);
        }
    }

    /// <summary>
    /// The widths of <paramref name="values"/> into four pairs of histograms taken in turn, which
    /// may all be the same pair, each row counted by its validity bit.
    /// </summary>
    private static void WidthLanes<T>(
        ReadOnlySpan<T> values, ref byte bits, nint firstBit, nint rowMask, bool zigzag,
        ref int first, ref int second, ref int third, ref int fourth)
        where T : unmanaged, IBinaryInteger<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        ulong top = BitWords.Mask(elementBits);
        ref T value = ref MemoryMarshal.GetReference(values);
        nint count = values.Length;
        nint i = 0;
        for (; i + 3 < count; i += 4)
        {
            nint at = firstBit + i;
            Count(ref first, Unsafe.Add(ref value, i), (int)BitAt(ref bits, at & rowMask), elementBits, top, zigzag);
            Count(ref second, Unsafe.Add(ref value, i + 1), (int)BitAt(ref bits, (at + 1) & rowMask), elementBits, top, zigzag);
            Count(ref third, Unsafe.Add(ref value, i + 2), (int)BitAt(ref bits, (at + 2) & rowMask), elementBits, top, zigzag);
            Count(ref fourth, Unsafe.Add(ref value, i + 3), (int)BitAt(ref bits, (at + 3) & rowMask), elementBits, top, zigzag);
        }

        for (; i < count; i++)
        {
            Count(ref first, Unsafe.Add(ref value, i), (int)BitAt(ref bits, (firstBit + i) & rowMask), elementBits, top, zigzag);
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
        Order(values, in mask, start, ref stats);

        // Same discipline as the signed loop: bounds alone in the hot loop, widths in a second walk
        // over the range still in cache, and only when a plan will read them.
        if (mask.AllValid)
        {
            Bounds(values, out min, out max);
            stats.MergeUnsigned(ulong.CreateTruncating(min), ulong.CreateTruncating(max));
            if (!widths.IsEmpty)
            {
                CountWidths<T>(values, in mask, start, widths, zigzag: false);
            }

            return;
        }

        if (mask.AllInvalid)
        {
            return;
        }

        if (MaskedBounds(values, in mask, start, out min, out max))
        {
            stats.MergeUnsigned(ulong.CreateTruncating(min), ulong.CreateTruncating(max));
        }

        if (!widths.IsEmpty)
        {
            CountWidths<T>(values, in mask, start, widths, zigzag: false);
        }
    }

    /// <summary>
    /// F32 and F64, NaN excluded, ordered by <c>Math.Min</c>'s sign-aware rule rather than by
    /// <c>&lt;</c>.
    /// </summary>
    /// <remarks>
    /// Skipping NaN is load-bearing, not tidy: the reference computes min/max with `skip_nans()` and
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
        Order(values, in mask, start, ref stats);

        if (mask.AllValid)
        {
            int i = FloatLanes(values, ref min, ref max, ref any);
            for (; i < values.Length; i++)
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

    /// <summary>
    /// The bounds of an all-valid float range, a register at a time: a NaN lane is replaced by the
    /// neutral element -- +∞ for the minimum, -∞ for the maximum -- before `Min` and `Max`, which
    /// then follow <c>Math.Min</c>'s rule on the two zeros.
    /// </summary>
    /// <returns>Where the scalar tail starts.</returns>
    /// <remarks>
    /// Two accumulators per extreme, as <see cref="Bounds{T}"/> has, for its reason: the loop is
    /// latency-bound. Whether any lane held a number is an OR of the self-equality masks, reduced
    /// once at the end.
    /// </remarks>
    private static int FloatLanes<T>(ReadOnlySpan<T> values, ref T min, ref T max, ref bool any)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        int lanes = Vector128<T>.IsSupported ? Vector128<T>.Count : 0;
        if (!Vector128.IsHardwareAccelerated || lanes == 0 || values.Length < lanes * 2)
        {
            return 0;
        }

        ref T head = ref MemoryMarshal.GetReference(values);
        Vector128<T> positive = Vector128.Create(T.PositiveInfinity);
        Vector128<T> negative = Vector128.Create(T.NegativeInfinity);
        Vector128<T> lowA = positive;
        Vector128<T> lowB = positive;
        Vector128<T> highA = negative;
        Vector128<T> highB = negative;
        Vector128<T> numbers = Vector128<T>.Zero;
        int step = lanes * 2;
        int i = 0;
        for (; i + step <= values.Length; i += step)
        {
            Vector128<T> a = Vector128.LoadUnsafe(ref head, (nuint)i);
            Vector128<T> b = Vector128.LoadUnsafe(ref head, (nuint)(i + lanes));
            Vector128<T> realA = Vector128.Equals(a, a);
            Vector128<T> realB = Vector128.Equals(b, b);
            numbers |= realA | realB;
            lowA = Vector128.Min(lowA, Vector128.ConditionalSelect(realA, a, positive));
            lowB = Vector128.Min(lowB, Vector128.ConditionalSelect(realB, b, positive));
            highA = Vector128.Max(highA, Vector128.ConditionalSelect(realA, a, negative));
            highB = Vector128.Max(highB, Vector128.ConditionalSelect(realB, b, negative));
        }

        if (numbers == Vector128<T>.Zero)
        {
            return i;
        }

        any = true;
        Vector128<T> low = Vector128.Min(lowA, lowB);
        Vector128<T> high = Vector128.Max(highA, highB);
        for (int lane = 0; lane < lanes; lane++)
        {
            min = Smaller(min, low.GetElement(lane));
            max = Larger(max, high.GetElement(lane));
        }

        return i;
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
        Order(values, in mask, start, ref stats);

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

    // The three below read one value, the element of a constant column. They switch on the physical
    // type, as `CanonicalSupport.ReadInteger` does, and are written out here rather than calling it
    // so that a switch made once per range stays distinguishable from one made per row.

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
