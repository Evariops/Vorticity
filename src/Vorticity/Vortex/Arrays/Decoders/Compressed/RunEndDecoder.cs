using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Decodes <c>vortex.runend</c> by expanding each run over its rows. The node has no buffers and
/// exactly two children, the run ends and the run values, both one entry per run; row r takes the
/// value of the first run whose offset-adjusted end is above r. That the ends strictly increase is
/// a hard check here rather than an assumption, because the search a take does relies on it.
/// </summary>
internal sealed class RunEndDecoder : ArrayDecoder
{
    private const string Id = "vortex.runend";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly RunEndDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.runend"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.RunEnd;

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start: 0, count: length);
    }

    /// <inheritdoc/>
    public override bool DecodesRange(ArrayDecodeContext context, in ArrayNode node) => true;

    /// <summary>
    /// Expands the runs the range falls in and no others, the first found by a binary search over
    /// the ends; the ends and values, one entry per run, are read whole and decoded once for every
    /// range of the node.
    /// </summary>
    /// <inheritdoc/>
    public override int DecodeRange(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false, start, count);
    }

    /// <summary>
    /// Binary-searches each wanted row to its run instead of expanding every run.
    /// </summary>
    /// <remarks>
    /// The ends and values children are one entry per run, so they are decoded whole; what this
    /// skips is the expansion to one value per row. The search is over the ends minus the offset,
    /// which is the space the wanted rows already live in, so a sliced run-end array needs no
    /// separate translation.
    /// <para>
    /// A take is served one batch at a time, so this method runs once per batch over the same node.
    /// What repeats then is not the decoding of the two children but the monotonicity walk over the
    /// ends, which is a property of the node and cannot change between batches, so its verdict is
    /// remembered rather than recomputed.
    /// </para>
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true, start: 0, count: wanted.Length);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective, int start, int count)
    {
        // Read before the children are decoded, which take the grant over. A take keeps the
        // canonical form: its rows are scattered, and runs of scattered rows are not runs.
        bool keep = context.KeepsEncoding && !selective;

        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, Id);

        RunEndMetadata metadata = RunEndMetadata.Read(node.Metadata);

        // The ends ptype is the stride the run-end buffer is read at, so it has to be checked
        // before anything indexes with it.
        if (!metadata.EndsPType.IsUnsignedInteger())
        {
            CompressedThrow.Format(
                $"{Id} run ends must be unsigned integers; the metadata declares " +
                $"{metadata.EndsPType.Name()}.");
        }

        int runCount = ArrayDecodeContext.CheckedLength(metadata.NumRuns, Id, "num_runs");
        int offset = ArrayDecodeContext.CheckedLength(metadata.Offset, Id, "offset");

        // The ends and values are one entry per run and every range or batch of the node reads all
        // of them, so anything short of the whole node decodes them once for every visit.
        bool whole = !selective && start == 0 && count == length;
        DType endsType = context.Types.Primitive(metadata.EndsPType, Nullability.NonNullable);
        int endsIndex = whole
            ? context.DecodeChild(in node, 0, endsType, runCount)
            : context.DecodeWholeChild(in node, 0, endsType, runCount);
        int valuesIndex = whole
            ? context.DecodeChild(in node, 1, dtype, runCount)
            : context.DecodeWholeChild(in node, 1, dtype, runCount);

        ReadOnlySpan<byte> ends = CompressedValues.RequireIndexChild(
            context, endsIndex, metadata.EndsPType, runCount, Id, "ends");

        ValueReader values = ValueReader.Of(context.Canonical, valuesIndex, Id);
        if (values.Length != runCount)
        {
            CompressedThrow.ChildLength(Id, "values", values.Length, runCount);
        }

        if (runCount == 0)
        {
            // With no runs there is nothing to expand, so the array must be empty and its offset
            // zero.
            if (offset != 0)
            {
                CompressedThrow.Format($"{Id} has no runs but a non-zero offset of {offset}.");
            }

            if (length != 0)
            {
                CompressedThrow.Format($"{Id} has no runs but declares {length} rows.");
            }
        }

        // Once per node per scan, not once per batch: the ends belong to the node and cannot change
        // between two batches of it, so the walk is a question already answered. The context only
        // remembers the verdict for a take over an oversized node, so every other path still walks.
        if (!context.IsNodeChecked(in node))
        {
            ValidateEnds(ends, metadata.EndsPType, runCount, offset, length);
            context.MarkNodeChecked(in node);
        }

        if (keep)
        {
            // A range of a run-end node is the same runs read from a later row.
            return EncodedNodes.RunEnd(
                context, dtype, count, ends, metadata.EndsPType, runCount, offset + start, valuesIndex, Id);
        }

        ValidityReader valuesValidity = ValidityReader.Of(context.Canonical, values.Validity);
        bool tracked = !values.Validity.IsAllValid;

        if (selective)
        {
            return Gather(
                context, dtype, length, runCount, offset, metadata.EndsPType, ends, in values,
                in valuesValidity, tracked, wanted);
        }

        DataBufferSet dataBuffers = DataBufferSet.Collect(
            context.Canonical, in values, false, default);
        try
        {
            // The buffer is left uninitialized and the loop below is the proof: `position` starts
            // at 0, every iteration writes exactly [position, endRow) and then sets position to
            // endRow, so the written rows are contiguous from 0 with no gap, and the check after
            // the loop turns "did not reach the end" into a format error rather than a buffer
            // holding whatever the pool last put there. Zero-filling first would be a whole pass
            // that the expansion immediately overwrites.
            ValueWriter writer = ValueWriter.CreateUninitialized(context, in values, count, 0, Id);
            ValidityWriter validity = ValidityWriter.Create(context, count, tracked, Id);

            // A range reads the runs from the one its first row falls in, with that row as the
            // origin every end is measured from.
            int firstRun = 0;
            if (start != 0)
            {
                firstRun = FindRun(ends, metadata.EndsPType, runCount, (ulong)offset, start);
                if (firstRun < 0)
                {
                    CompressedThrow.Format($"{Id} row {start} falls outside the {length} rows its runs cover.");
                }
            }

            ulong unsignedOffset = (ulong)offset + (ulong)(uint)start;
            ulong unsignedLength = (ulong)(uint)count;

            // The general loop below is correct and stays, but it pays once per run for two answers
            // that hold for the whole node: the ends' physical type and the value width.
            // `RepeatRuns` resolves both once and runs the same loop typed, returning -1 for the
            // shapes it has no kernel for, which then walk here.
            int position = writer.RepeatRuns(
                in values, ends, metadata.EndsPType, firstRun, runCount, unsignedOffset, count,
                in valuesValidity, in validity, tracked);
            if (position < 0)
            {
                position = 0;
                for (int run = firstRun; run < runCount && position < count; run++)
                {
                    ulong end =
                        CompressedValues.ReadUnsigned(ends, metadata.EndsPType, run) - unsignedOffset;
                    if (end > unsignedLength)
                    {
                        end = unsignedLength;
                    }

                    int endRow = (int)end;
                    if (endRow <= position)
                    {
                        continue;
                    }

                    int rows = endRow - position;
                    writer.Repeat(in values, run, position, rows);
                    if (tracked && valuesValidity.IsValid(run))
                    {
                        validity.SetValidRange(position, rows);
                    }

                    position = endRow;
                }
            }

            if (position != count)
            {
                CompressedThrow.Format(
                    $"{Id} runs cover {position} of {count} rows; the last run end must reach " +
                    "offset + length.");
            }

            return writer.Complete(
                context, dtype, validity.Complete(context, dtype, Id), dataBuffers.Buffers);
        }
        finally
        {
            dataBuffers.Dispose();
        }
    }

    /// <summary>Finds each wanted row's run by binary search and copies that run's value.</summary>
    private static int Gather(
        ArrayDecodeContext context,
        DType dtype,
        int length,
        int runCount,
        int offset,
        PType endsPType,
        ReadOnlySpan<byte> ends,
        in ValueReader values,
        in ValidityReader valuesValidity,
        bool tracked,
        ReadOnlySpan<int> wanted)
    {
        int count = wanted.Length;
        Span<int> stack = stackalloc int[StackRows];
        Scratch<int> scratch = new Scratch<int>(count, stack);
        DataBufferSet dataBuffers = DataBufferSet.Collect(context.Canonical, in values, false, default);
        try
        {
            // The wanted rows ascend, so each search could start where the last one stopped; it
            // does not, because a binary search over a run count is already logarithmic and a
            // resumed linear scan is worse whenever the rows are far apart - which is the case a
            // take is for. The searches are typed and run four abreast instead, so that the loads
            // of four of them are in flight at once.
            Span<int> runs = scratch.Span;
            ulong unsignedOffset = (ulong)offset;
            switch (endsPType)
            {
                case PType.U8:
                    FindRuns(MemoryMarshal.Cast<byte, byte>(ends)[..runCount], unsignedOffset, wanted, runs);
                    break;
                case PType.U16:
                    FindRuns(MemoryMarshal.Cast<byte, ushort>(ends)[..runCount], unsignedOffset, wanted, runs);
                    break;
                case PType.U32:
                    FindRuns(MemoryMarshal.Cast<byte, uint>(ends)[..runCount], unsignedOffset, wanted, runs);
                    break;
                case PType.U64:
                    FindRuns(MemoryMarshal.Cast<byte, ulong>(ends)[..runCount], unsignedOffset, wanted, runs);
                    break;
                default:
                    for (int i = 0; i < count; i++)
                    {
                        runs[i] = FindRun(ends, endsPType, runCount, unsignedOffset, wanted[i]);
                    }

                    break;
            }

            ValueWriter writer = ValueWriter.Create(context, in values, count, 0, Id);
            ValidityWriter validity = ValidityWriter.Create(context, count, tracked, Id);
            for (int i = 0; i < count; i++)
            {
                int run = runs[i];
                if (run < 0)
                {
                    CompressedThrow.Format(
                        $"{Id} row {wanted[i]} falls outside the {length} rows its runs cover.");
                }

                writer.Copy(in values, run, i);
                if (tracked && valuesValidity.IsValid(run))
                {
                    validity.SetValid(i);
                }
            }

            return writer.Complete(
                context, dtype, validity.Complete(context, dtype, Id), dataBuffers.Buffers);
        }
        finally
        {
            dataBuffers.Dispose();
            scratch.Dispose();
        }
    }

    /// <summary>The wanted rows a take searches on the stack: a take's batch, past which the pool lends.</summary>
    private const int StackRows = 128;

    /// <summary>
    /// Each wanted row's run, the first whose end, less <paramref name="offset"/>, is above the row,
    /// or -1 for a row past every run: four searches at a time, each halving its span without a
    /// branch, and the rest one by one.
    /// </summary>
    private static void FindRuns<TEnd>(ReadOnlySpan<TEnd> ends, ulong offset, ReadOnlySpan<int> wanted, Span<int> runs)
        where TEnd : unmanaged
    {
        int runCount = ends.Length;
        if (runCount == 0)
        {
            runs[..wanted.Length].Fill(-1);
            return;
        }

        ref TEnd first = ref MemoryMarshal.GetReference(ends);
        int i = 0;
        for (; i + 4 <= wanted.Length; i += 4)
        {
            ulong t0 = (ulong)(uint)wanted[i] + offset;
            ulong t1 = (ulong)(uint)wanted[i + 1] + offset;
            ulong t2 = (ulong)(uint)wanted[i + 2] + offset;
            ulong t3 = (ulong)(uint)wanted[i + 3] + offset;
            int l0 = 0;
            int l1 = 0;
            int l2 = 0;
            int l3 = 0;
            for (int span = runCount; span > 1;)
            {
                int half = span >> 1;
                l0 = End(ref first, l0 + half - 1) <= t0 ? l0 + half : l0;
                l1 = End(ref first, l1 + half - 1) <= t1 ? l1 + half : l1;
                l2 = End(ref first, l2 + half - 1) <= t2 ? l2 + half : l2;
                l3 = End(ref first, l3 + half - 1) <= t3 ? l3 + half : l3;
                span -= half;
            }

            runs[i] = Settle(ref first, l0, t0, runCount);
            runs[i + 1] = Settle(ref first, l1, t1, runCount);
            runs[i + 2] = Settle(ref first, l2, t2, runCount);
            runs[i + 3] = Settle(ref first, l3, t3, runCount);
        }

        for (; i < wanted.Length; i++)
        {
            ulong target = (ulong)(uint)wanted[i] + offset;
            int lower = 0;
            for (int span = runCount; span > 1;)
            {
                int half = span >> 1;
                lower = End(ref first, lower + half - 1) <= target ? lower + half : lower;
                span -= half;
            }

            runs[i] = Settle(ref first, lower, target, runCount);
        }
    }

    /// <summary>The search's last step: the run left, or the one after it, or none past the last.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Settle<TEnd>(ref TEnd first, int lower, ulong target, int runCount)
        where TEnd : unmanaged
    {
        int run = End(ref first, lower) > target ? lower : lower + 1;
        return run < runCount ? run : -1;
    }

    /// <summary>Run end <paramref name="index"/>, widened, folded at instantiation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong End<TEnd>(ref TEnd first, int index)
        where TEnd : unmanaged
    {
        TEnd end = Unsafe.Add(ref first, index);
        if (typeof(TEnd) == typeof(byte))
        {
            return Unsafe.As<TEnd, byte>(ref end);
        }

        if (typeof(TEnd) == typeof(ushort))
        {
            return Unsafe.As<TEnd, ushort>(ref end);
        }

        if (typeof(TEnd) == typeof(uint))
        {
            return Unsafe.As<TEnd, uint>(ref end);
        }

        return Unsafe.As<TEnd, ulong>(ref end);
    }

    /// <summary>The first run whose (offset-adjusted) end is strictly above <paramref name="row"/>.</summary>
    private static int FindRun(
        ReadOnlySpan<byte> ends, PType endsPType, int runCount, ulong offset, int row)
    {
        ulong target = (ulong)(uint)row;
        int low = 0;
        int high = runCount - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            ulong end = CompressedValues.ReadUnsigned(ends, endsPType, middle) - offset;
            if (end > target)
            {
                found = middle;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }

        return found;
    }

    private static void ValidateEnds(
        ReadOnlySpan<byte> ends, PType endsPType, int runCount, int offset, int length)
    {
        if (runCount == 0 || length == 0)
        {
            // A zero-length logical slice may legally retain the source array's run metadata.
            return;
        }

        // The ends' type resolved once for the walk, not once an end: the walk is the whole of a
        // run-end node's checks, and an end read through its type is a call per run. The type was
        // checked unsigned before anything indexed with it.
        EndsShape shape = endsPType switch
        {
            PType.U8 => Shape(MemoryMarshal.Cast<byte, byte>(ends)[..runCount]),
            PType.U16 => Shape(MemoryMarshal.Cast<byte, ushort>(ends)[..runCount]),
            PType.U32 => Shape(MemoryMarshal.Cast<byte, uint>(ends)[..runCount]),
            PType.U64 => Shape(MemoryMarshal.Cast<byte, ulong>(ends)[..runCount]),
            _ => throw new System.Diagnostics.UnreachableException($"{Id} run ends are checked unsigned before they are walked."),
        };

        if (shape.Descent > 0)
        {
            CompressedThrow.Format(
                $"vortex.runend run ends must be strictly increasing; {shape.Fallen} follows {shape.Before}.");
        }

        if (offset != 0 && shape.First < (ulong)offset)
        {
            CompressedThrow.Format(
                $"vortex.runend first run end {shape.First} must be at least the offset {offset}.");
        }

        ulong required = (ulong)offset + (ulong)(uint)length;
        if (shape.Last < required)
        {
            CompressedThrow.Format(
                $"vortex.runend last run end {shape.Last} must be at least offset + length {required}.");
        }
    }

    /// <summary>What the checks of a node's run ends need: its first and last end, and its first descent.</summary>
    /// <param name="First">The first end.</param>
    /// <param name="Last">The last end.</param>
    /// <param name="Descent">The first end not above the one before it, or -1.</param>
    /// <param name="Before">The end before the descent.</param>
    /// <param name="Fallen">The end at the descent.</param>
    private readonly record struct EndsShape(ulong First, ulong Last, int Descent, ulong Before, ulong Fallen);

    /// <summary>
    /// The shape of a node's run ends, the walk a vector of ends at a time against the same vector
    /// one end earlier, and the descent named one end at a time only once a vector has found one.
    /// </summary>
    private static EndsShape Shape<TEnd>(ReadOnlySpan<TEnd> ends)
        where TEnd : unmanaged, IComparisonOperators<TEnd, TEnd, bool>
    {
        ref TEnd first = ref MemoryMarshal.GetReference(ends);
        int i = 1;
        if (Vector128.IsHardwareAccelerated && Vector128<TEnd>.IsSupported)
        {
            Vector128<TEnd> descents = Vector128<TEnd>.Zero;
            for (; i + Vector128<TEnd>.Count <= ends.Length; i += Vector128<TEnd>.Count)
            {
                descents |= Vector128.LessThanOrEqual(
                    Vector128.LoadUnsafe(ref first, (nuint)i), Vector128.LoadUnsafe(ref first, (nuint)(i - 1)));
            }

            if (descents != Vector128<TEnd>.Zero)
            {
                i = 1;
            }
        }

        int descent = -1;
        for (; i < ends.Length; i++)
        {
            if (ends[i] <= ends[i - 1])
            {
                descent = i;
                break;
            }
        }

        return new EndsShape(
            End(ref first, 0),
            End(ref first, ends.Length - 1),
            descent,
            descent > 0 ? End(ref first, descent - 1) : 0,
            descent > 0 ? End(ref first, descent) : 0);
    }
}
