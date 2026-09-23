using System;
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
        DataBufferSet dataBuffers = DataBufferSet.Collect(context.Canonical, in values, false, default);
        try
        {
            ValueWriter writer = ValueWriter.Create(context, in values, count, 0, Id);
            ValidityWriter validity = ValidityWriter.Create(context, count, tracked, Id);

            ulong unsignedOffset = (ulong)offset;
            for (int i = 0; i < count; i++)
            {
                // The wanted rows ascend, so each search could start where the last one stopped;
                // it does not, because a binary search over a run count is already logarithmic and
                // a resumed linear scan is worse whenever the rows are far apart - which is the
                // case a take is for.
                int run = FindRun(ends, endsPType, runCount, unsignedOffset, wanted[i]);
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
        }
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

        ulong previous = 0;
        for (int run = 0; run < runCount; run++)
        {
            ulong end = CompressedValues.ReadUnsigned(ends, endsPType, run);
            if (run != 0 && end <= previous)
            {
                CompressedThrow.Format(
                    $"vortex.runend run ends must be strictly increasing; {end} follows {previous}.");
            }

            previous = end;
        }

        ulong first = CompressedValues.ReadUnsigned(ends, endsPType, 0);
        if (offset != 0 && first < (ulong)offset)
        {
            CompressedThrow.Format(
                $"vortex.runend first run end {first} must be at least the offset {offset}.");
        }

        ulong required = (ulong)offset + (ulong)(uint)length;
        if (previous < required)
        {
            CompressedThrow.Format(
                $"vortex.runend last run end {previous} must be at least offset + length {required}.");
        }
    }
}
