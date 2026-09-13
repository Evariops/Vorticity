// vortex.pco - vortex-pco-0.86.1/src/array.rs, wrapping pco-1.0.3.
//
// The wrapper is thin: protobuf metadata naming pco's file header and, per chunk, its pages' value
// counts; buffers holding the per-chunk pco metadata followed by the page bodies; zero or one
// validity child. Everything else is pco's own format, in the sibling files here.
using System;

using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>Decodes <c>vortex.pco</c>.</summary>
public sealed class PcoDecoder : ArrayDecoder
{
    private const string Id = "vortex.pco";

    /// <summary>The shared, stateless instance.</summary>
    public static readonly PcoDecoder Instance = new();

    /// <inheritdoc/>
    public override ReadOnlySpan<byte> IdUtf8 => "vortex.pco"u8;

    /// <inheritdoc/>
    public override ArrayEncodingId EncodingId => ArrayEncodingId.Pco;

    /// <inheritdoc/>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (dtype.Kind != DTypeKind.Primitive)
        {
            CompressedThrow.Format($"{Id} requires a primitive dtype; the node declares {dtype.Kind}.");
        }

        PType ptype = dtype.PType;
        int width = ptype.ByteWidth();
        if (width != 8)
        {
            // Only the 64-bit latent path is ported. Refusing is the whole point of this check:
            // a narrower type would decode with the wrong latent width and produce numbers.
            CompressedThrow.Format($"{Id} is implemented for 64-bit types only; this node is {ptype.Name()}.");
        }

        PcoWrapperMetadata wrapper = PcoWrapperMetadata.Read(node.Metadata);

        int expected = wrapper.Chunks.Count + wrapper.PageCount;
        if (node.BufferCount != expected)
        {
            CompressedThrow.Format(
                $"{Id} declares {wrapper.Chunks.Count} chunks and {wrapper.PageCount} pages, " +
                $"needing {expected} buffers; the node has {node.BufferCount}.");
        }

        if (wrapper.ValueCount != length)
        {
            CompressedThrow.Format(
                $"{Id}'s pages hold {wrapper.ValueCount} values; the node declares {length} rows.");
        }

        VortexBuffer output = CompressedValues.Allocate(
            context, length * width, width, Id, out Span<byte> destination);

        int pageBuffer = wrapper.Chunks.Count;
        int written = 0;
        for (int chunk = 0; chunk < wrapper.Chunks.Count; chunk++)
        {
            PcoChunkMeta meta = PcoChunkMeta.Read(
                wrapper.Header, node.GetBuffer(chunk).Span, latentBits: 64);

            foreach (int pageValues in wrapper.Chunks[chunk])
            {
                ulong[] latents = PcoPageDecoder.DecodeJoined(
                    meta, node.GetBuffer(pageBuffer).Span, pageValues);
                pageBuffer++;

                for (int i = 0; i < pageValues; i++)
                {
                    // The ordered latent form: shifted so the type's minimum is zero. For an
                    // unsigned type it is the value itself, which is why the shift is by MID
                    // rather than by a sign test.
                    ulong value = ptype.IsSignedInteger()
                        ? unchecked(latents[i] + (1UL << 63))
                        : latents[i];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                        destination.Slice((written + i) * width, width), value);
                }

                written += pageValues;
            }
        }

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);
        return context.Canonical.AddPrimitive(dtype, length, validity, ptype, output);
    }
}
