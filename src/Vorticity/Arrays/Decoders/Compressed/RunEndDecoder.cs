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
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

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

        DataBufferSet dataBuffers = DataBufferSet.Collect(
            context.Canonical, in values, false, default);
        try
        {
            ValueWriter writer = ValueWriter.Create(context, in values, length, 0, Id);
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
