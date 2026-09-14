// vortex.runend - vortex-runend-0.86.1/src/array.rs (`validate_parts`, `run_end_canonicalize`)
// and src/compress.rs (`runend_decode_slice`, `trimmed_ends_iter`).
//
// Exactly two children and no buffers: ends and values, both `num_runs` long. Row r of the output
// takes the value of the first run whose (end - offset) exceeds r. Upstream only `debug_assert`s
// that the ends are strictly increasing and its `take` binary-searches them, so this is a hard
// check here; `num_runs` and `offset` are u64 and upstream converts them with `vortex_expect`,
// which is a panic - here they are checked narrowings.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.runend</c> by expanding each run over its rows.</summary>
public sealed class RunEndDecoder : ArrayDecoder
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
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// Binary-searches each wanted row to its run instead of expanding every run.
    /// </summary>
    /// <remarks>
    /// The `vortex.runend` row of the take table: "binary search in `ends`". The ends and values
    /// children are one entry per RUN, so they are decoded whole - that is not the expensive part;
    /// the expansion to one value per ROW is, and it is what this skips.
    ///
    /// The search is over the ends MINUS the offset, which is the same space `length` and the
    /// wanted rows live in, so a sliced run-end array needs no separate translation.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 0, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, Id);

        RunEndMetadata metadata = RunEndMetadata.Read(node.Metadata);

        // Class I: the ends ptype is the stride the run-end buffer is read at.
        if (!metadata.EndsPType.IsUnsignedInteger())
        {
            CompressedThrow.Format(
                $"{Id} run ends must be unsigned integers; the metadata declares " +
                $"{metadata.EndsPType.Name()}.");
        }

        int runCount = ArrayDecodeContext.CheckedLength(metadata.NumRuns, $"{Id} num_runs");
        int offset = ArrayDecodeContext.CheckedLength(metadata.Offset, $"{Id} offset");

        DType endsType = context.Types.Primitive(metadata.EndsPType, Nullability.NonNullable);
        int endsIndex = context.DecodeChild(in node, 0, endsType, runCount);
        int valuesIndex = context.DecodeChild(in node, 1, dtype, runCount);

        ReadOnlySpan<byte> ends = CompressedValues.RequireIndexChild(
            context, endsIndex, metadata.EndsPType, runCount, Id, "ends");

        ValueReader values = ValueReader.Of(context.Canonical, valuesIndex, Id);
        if (values.Length != runCount)
        {
            CompressedThrow.ChildLength(Id, "values", values.Length, runCount);
        }

        if (runCount == 0)
        {
            // "non-zero offset provided for empty RunEndArray", and with no runs there is nothing
            // to expand, so the array must be empty too.
            if (offset != 0)
            {
                CompressedThrow.Format($"{Id} has no runs but a non-zero offset of {offset}.");
            }

            if (length != 0)
            {
                CompressedThrow.Format($"{Id} has no runs but declares {length} rows.");
            }
        }

        ValidateEnds(ends, metadata.EndsPType, runCount, offset, length);

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
            // UNINITIALIZED, AND THE LOOP BELOW IS THE PROOF: `position` starts at 0, every
            // iteration writes exactly [position, endRow) and then sets position = endRow, so the
            // written rows are contiguous from 0 with no gap -- and the `position != length` check
            // after the loop turns "did not reach the end" into a format error rather than a
            // buffer holding whatever the pool last put there. Zero-filling 4 MB that `Repeat`
            // overwrites in full was 10% of a 1M-row run-end scan.
            ValueWriter writer = ValueWriter.CreateUninitialized(context, in values, length, 0, Id);
            ValidityWriter validity = ValidityWriter.Create(context, length, tracked, Id);

            ulong unsignedOffset = (ulong)offset;
            ulong unsignedLength = (ulong)(uint)length;
            int position = 0;
            for (int run = 0; run < runCount && position < length; run++)
            {
                ulong end = CompressedValues.ReadUnsigned(ends, metadata.EndsPType, run) - unsignedOffset;
                if (end > unsignedLength)
                {
                    end = unsignedLength;
                }

                int endRow = (int)end;
                if (endRow <= position)
                {
                    continue;
                }

                int count = endRow - position;
                writer.Repeat(in values, run, position, count);
                if (tracked && valuesValidity.IsValid(run))
                {
                    validity.SetValidRange(position, count);
                }

                position = endRow;
            }

            if (position != length)
            {
                CompressedThrow.Format(
                    $"{Id} runs cover {position} of {length} rows; the last run end must reach " +
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
