using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>
/// Decodes <c>vortex.sparse</c>: a fill value everywhere, patched at the listed rows. The fill
/// value is not part of the metadata; it sits alone in the node's single buffer as a bare protobuf
/// scalar. The node always carries exactly two children, the patch indices and the patch values,
/// even when the patch descriptor declares chunk offsets, so a third child would be a bug.
/// </summary>
internal sealed class SparseDecoderBefore : ArrayDecoder
{
    private const string Id = "vortex.sparse";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly SparseDecoderBefore Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.sparse"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Sparse;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted: default, selective: false);
    }

    /// <summary>
    /// The selected rows: the fill over <c>wanted.Length</c> rows, patched only where a patch and a
    /// wanted row coincide.
    /// </summary>
    /// <remarks>
    /// The saving is the fill: a selective read writes it once per delivered row instead of once
    /// per row of the node. Everything else matches <c>Decode</c> line for line, which is why both
    /// go through the same core.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Core(context, in node, dtype, length, wanted, selective: true);
    }

    /// <inheritdoc/>
    public override bool SelectsWithoutFullDecode => true;

    // There is deliberately no pushed comparison: expanding a sparse column is a fill of one value
    // plus its patches, and answering a comparison over it would be a fill too, so both write one
    // state per row and there is nothing to win.

    private static int Core(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length,
        ReadOnlySpan<int> wanted, bool selective)
    {
        ArrayDecodeContext.RequireBufferCount(node.BufferCount, 1, Id);
        ArrayDecodeContext.RequireChildCount(node.ChildCount, 2, Id);

        SparseMetadata metadata = SparseMetadata.Read(node.Metadata);
        PatchesMetadata patchesMetadata = metadata.Patches;

        int patchCount = ArrayDecodeContext.CheckedLength(
            patchesMetadata.Length, $"{Id} patch count");

        DType indicesType = context.Types.Primitive(
            patchesMetadata.IndicesPType, Nullability.NonNullable);
        int indicesIndex = context.DecodeChild(in node, 0, indicesType, patchCount);
        int valuesIndex = context.DecodeChild(in node, 1, dtype, patchCount);

        bool walked = context.IsNodeChecked(in node);
        Patches patches = Patches.Create(
            context, in patchesMetadata, length, indicesIndex, valuesIndex, Id, walked);
        if (!walked)
        {
            context.MarkNodeChecked(in node);
        }

        // The fill value is interpreted against the array's own dtype, exactly as
        // `ScalarValue::from_proto_bytes(scalar_bytes, dtype, session)` does.
        TypedScalar fill = TypedScalarReader.Read(
            node.GetBuffer(0).Span, dtype, context.Scalars, context.Types);

        ValueReader values = ValueReader.Of(context.Canonical, valuesIndex, Id);
        ValidityReader valuesValidity = ValidityReader.Of(context.Canonical, values.Validity);

        bool fillIsNull = fill.IsNull;
        bool tracked = fillIsNull || !values.Validity.IsAllValid;

        // A VarBinView fill longer than 12 bytes cannot be inlined into its view, so it needs a
        // data buffer of its own at index 0 - which shifts every copied patch view's buffer index
        // by one. The bytes must be copied: they live in the scalar store, which a batch reset
        // clears.
        bool hasFillBuffer = false;
        VortexBuffer fillBuffer = VortexBuffer.Empty;
        ReadOnlySpan<byte> fillBytes = default;
        if (values.Kind == CanonicalKind.VarBinView && !fillIsNull)
        {
            fillBytes = dtype.Kind == DTypeKind.Utf8 ? fill.AsUtf8 : fill.AsBinary;
            if (fillBytes.Length > 12)
            {
                fillBuffer = CompressedValues.Allocate(
                    context, fillBytes.Length, 8, Id, out Span<byte> fillDestination);
                fillBytes.CopyTo(fillDestination);
                hasFillBuffer = true;
            }
        }

        DataBufferSet dataBuffers = DataBufferSet.Collect(
            context.Canonical, in values, hasFillBuffer, fillBuffer);
        try
        {
            // The buffer is left uninitialized when there is a fill, because the fill covers every
            // row before a single patch is applied. A null fill writes nothing at all, since its
            // null rows are the zeros, so that case keeps them. Bool needs no special case:
            // `CreateUninitialized` declines for a bitmap on its own, because a bitmap's last byte
            // holds bits past the row count that nothing writes. Zeroing first would mean filling
            // the whole node only to tile over it.
            //
            // The output is as long as the selection, which is where a selective read saves: the
            // fill is written once per delivered row rather than once per row of the node.
            int produced = selective ? wanted.Length : length;

            ValueWriter writer = fillIsNull
                ? ValueWriter.Create(context, in values, produced, hasFillBuffer ? 1 : 0, Id)
                : ValueWriter.CreateUninitialized(
                    context, in values, produced, hasFillBuffer ? 1 : 0, Id);
            ValidityWriter validity = ValidityWriter.Create(context, produced, tracked, Id);

            if (!fillIsNull)
            {
                WriteFill(in writer, in fill, dtype, fillBytes, produced);
            }

            if (tracked && !fillIsNull)
            {
                validity.SetValidRange(0, produced);
            }

            // The case the encoding exists for, taken whole. The general loop below asks three
            // questions per patch that are really properties of the array: what kind the writer is,
            // whether the patch value is null, and whether validity is tracked. When the fill is
            // not null and no patch value is, the per-patch validity work is redundant, because
            // `SetValidRange` above has already set every bit it would set again, so the fast path
            // is a pure scatter typed on both the index width and the value width.
            //
            // Primitive only. `Copy` also shifts a VarBinView's buffer index and writes a bit for a
            // Bool, and `ApplyAll` does neither; a Decimal is fixed-width but goes through the same
            // `Copy` and is left with the general loop rather than assumed.
            if (writer.Kind == CanonicalKind.Primitive && valuesValidity.IsAllValid && !fillIsNull)
            {
                if (selective)
                {
                    Patches.ApplySelected(in patches, values.Bytes, writer.Width, wanted, writer.Bytes);
                }
                else
                {
                    patches.ApplyAll(values.Bytes, writer.Width, writer.Bytes);
                }

                return writer.Complete(
                    context, dtype, validity.Complete(context, dtype, Id), dataBuffers.Buffers);
            }

            if (selective)
            {
                // The same loop, at the positions the selection leaves. A patch only survives if
                // its row was wanted, and it then lands at that row's index in the selection, not
                // at its index in the node. The walk is driven off the wanted side and searches the
                // patches, because a take asks for a handful of rows while the patch list can be
                // long, and walking it whole per batch would undo the saving.
                int from = 0;
                for (int w = 0; w < wanted.Length && from < patches.Count; w++)
                {
                    int found = Patches.Find(in patches, wanted[w], from);
                    if (found < 0)
                    {
                        from = ~found;
                        continue;
                    }

                    from = found + 1;
                    Place(in writer, in validity, in values, in valuesValidity, found, w, tracked);
                }
            }
            else
            {
                for (int i = 0; i < patches.Count; i++)
                {
                    Place(
                        in writer, in validity, in values, in valuesValidity,
                        i, patches.GetPosition(i), tracked);
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

    /// <summary>Writes patch <paramref name="patch"/> at output row <paramref name="at"/>.</summary>
    private static void Place(
        in ValueWriter writer, in ValidityWriter validity, in ValueReader values,
        in ValidityReader valuesValidity, int patch, int at, bool tracked)
    {
        bool valid = valuesValidity.IsValid(patch);
        if (valid)
        {
            writer.Copy(in values, patch, at);
        }
        else
        {
            writer.ClearRow(at);
        }

        if (!tracked)
        {
            return;
        }

        if (valid)
        {
            validity.SetValid(at);
        }
        else
        {
            validity.SetInvalid(at);
        }
    }

    private static void WriteFill(
        in ValueWriter writer, in TypedScalar fill, DType dtype, ReadOnlySpan<byte> fillBytes, int length)
    {
        switch (writer.Kind)
        {
            case CanonicalKind.Primitive:
            {
                // DType.PType raises InvalidOperationException off a non-Primitive dtype, and
                // file-driven input must never reach an invalid-operation throw, so the mismatch
                // is checked rather than relied on: the patch values decoded to a Primitive but
                // the node's own dtype is something else.
                if (dtype.Kind != DTypeKind.Primitive)
                {
                    CompressedThrow.Format(
                        $"{Id}'s patch values decoded to a Primitive under the dtype {dtype}.");
                }

                // WriteTo repeats the value over the whole destination in one call.
                fill.WriteTo(writer.Bytes, dtype.PType);
                return;
            }


            case CanonicalKind.Bool:

                // Both values are written, not just `true`. Leaning on the allocator's zeros for a
                // `false` fill would make the buffer's coverage depend on the fill's value, which
                // is the very property the uninitialized allocation above has to reason about. A
                // run written either way is also one vectorized fill rather than a
                // read-modify-write per row.
                writer.FillBits(0, length, fill.AsBool);
                return;

            case CanonicalKind.Decimal:
            {
                Span<byte> row = stackalloc byte[Int256.ByteCount];
                fill.AsDecimal.Unscaled.WriteLittleEndianBytes(row);
                int width = writer.Width;
                RequireNarrowable(row, width);
                for (int i = 0; i < length; i++)
                {
                    writer.WriteRow(i, row[..width]);
                }

                return;
            }

            default:
            {
                // Arrow BinaryView: [0..4) length, then either 12 inline bytes or
                // prefix / buffer index / offset. The fill's own buffer is index 0 when present.
                Span<byte> view = stackalloc byte[ValueReader.ViewWidth];
                view.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)fillBytes.Length);
                if (fillBytes.Length <= 12)
                {
                    fillBytes.CopyTo(view[4..]);
                }
                else
                {
                    fillBytes[..4].CopyTo(view[4..8]);
                }

                for (int i = 0; i < length; i++)
                {
                    writer.WriteRow(i, view);
                }

                return;
            }
        }
    }

    // The scalar's own byte width is chosen by the wire (1/2/4/8/16/32); the output's comes from
    // the patch values' canonical storage. They usually agree, and when they do not the value must
    // still be exactly representable - never silently truncated.
    private static void RequireNarrowable(ReadOnlySpan<byte> littleEndian, int width)
    {
        if (width >= littleEndian.Length)
        {
            return;
        }

        byte extension = (littleEndian[width - 1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00;
        for (int i = width; i < littleEndian.Length; i++)
        {
            if (littleEndian[i] != extension)
            {
                CompressedThrow.Format(
                    $"{Id}'s fill value does not fit the {width}-byte decimal storage of its " +
                    "patch values.");
            }
        }
    }
}
