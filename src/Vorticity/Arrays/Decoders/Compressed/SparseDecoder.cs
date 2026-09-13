// vortex.sparse - vortex-sparse-0.86.1/src/lib.rs and src/canonical.rs.
//
// Two traps, both stated in the reference's own comments:
//   * "Note that we DO NOT serialize the fill value since that is stored in the buffers" - the
//     fill value is BUFFER 0, a bare protobuf ScalarValue, and SparseMetadata carries only the
//     patch descriptor. Same shape as vortex.constant (contract §0a C1);
//   * the node has EXACTLY 2 children even when PatchesMetadata declares chunk offsets: the
//     deserialize path passes `None` for chunk offsets unconditionally, so reading a third child
//     is a bug. Every sparse node in the corpus is c2/b1.
using System;
using System.Buffers.Binary;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>Decodes <c>vortex.sparse</c>: a fill value everywhere, patched at the listed rows.</summary>
public sealed class SparseDecoder : ArrayDecoder
{
    private const string Id = "vortex.sparse";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly SparseDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.sparse"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Sparse;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

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

        Patches patches = Patches.Create(
            context, in patchesMetadata, length, indicesIndex, valuesIndex, Id);

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
        // by one. The bytes must be COPIED: they live in the ScalarStore, which ResetBatch clears.
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
            // UNINITIALIZED WHEN THERE IS A FILL, because a fill covers every row before a single
            // patch is applied: `WriteFill` writes the whole buffer for a Primitive, and row by row
            // for a Decimal or a VarBinView, and now for a Bool too. A NULL fill writes nothing at
            // all -- the null rows ARE the zeros -- so that case keeps them.
            //
            // Bool needs no special case here: `CreateUninitialized` declines for a bitmap on its
            // own, because a bitmap's last byte holds bits past the row count that nothing writes.
            //
            // It was 14% of a scattered-take profile: a zero fill of the whole node, immediately
            // tiled over.
            ValueWriter writer = fillIsNull
                ? ValueWriter.Create(context, in values, length, hasFillBuffer ? 1 : 0, Id)
                : ValueWriter.CreateUninitialized(
                    context, in values, length, hasFillBuffer ? 1 : 0, Id);
            ValidityWriter validity = ValidityWriter.Create(context, length, tracked, Id);

            if (!fillIsNull)
            {
                WriteFill(in writer, in fill, dtype, fillBytes, length);
            }

            if (tracked && !fillIsNull)
            {
                validity.SetValidRange(0, length);
            }

            for (int i = 0; i < patches.Count; i++)
            {
                int position = patches.GetPosition(i);
                bool valid = valuesValidity.IsValid(i);
                if (valid)
                {
                    writer.Copy(in values, i, position);
                }
                else
                {
                    writer.ClearRow(position);
                }

                if (tracked)
                {
                    if (valid)
                    {
                        validity.SetValid(position);
                    }
                    else
                    {
                        validity.SetInvalid(position);
                    }
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

    private static void WriteFill(
        in ValueWriter writer, in TypedScalar fill, DType dtype, ReadOnlySpan<byte> fillBytes, int length)
    {
        switch (writer.Kind)
        {
            case CanonicalKind.Primitive:
            {
                // DType.PType raises InvalidOperationException off a non-Primitive dtype, which
                // §1.4 forbids for file-driven input, so the mismatch is checked rather than
                // relied on: the patch values decoded to a Primitive but the node's own dtype is
                // something else.
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

                // BOTH VALUES ARE WRITTEN, not just `true`. Relying on the allocator's zeros for a
                // `false` fill was correct and invisible: it made the buffer's coverage depend on
                // the fill's VALUE, which is exactly the property `CreateUninitialized` above has
                // to be able to reason about. Writing the run either way says what it means, and
                // costs a vectorized fill instead of `length` read-modify-writes of the same byte
                // (PERF-AUDIT §4.2).
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
