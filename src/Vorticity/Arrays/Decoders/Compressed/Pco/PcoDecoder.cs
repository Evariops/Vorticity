// vortex.pco - vortex-pco-0.86.1/src/array.rs, wrapping pco-1.0.3.
//
// The wrapper is thin: protobuf metadata naming pco's file header and, per chunk, its pages' value
// counts; buffers holding the per-chunk pco metadata followed by the page bodies; zero or one
// validity child. Everything else is pco's own format, in the sibling files here.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

using Vorticity.Arrays.Decoders.Canonical;

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

        // The two page buffers, rented ONCE for the whole node rather than allocated per page: a
        // million-row column is a thousand pages, and contract §1.3 does not allow a thousand
        // allocations on a decode path.
        int widestPage = 0;
        foreach (int[] pages in wrapper.Chunks)
        {
            foreach (int pageValues in pages)
            {
                widestPage = Math.Max(widestPage, pageValues);
            }
        }

        bool signed = ptype.IsSignedInteger();
        Scratch<ulong> primaryScratch = new Scratch<ulong>(widestPage, default);
        Scratch<ulong> secondaryScratch = new Scratch<ulong>(widestPage, default);
        try
        {
            int pageBuffer = wrapper.Chunks.Count;
            int written = 0;
            for (int chunk = 0; chunk < wrapper.Chunks.Count; chunk++)
            {
                PcoChunkMeta meta = PcoChunkMeta.Read(
                    wrapper.Header, node.GetBuffer(chunk).Span, latentBits: 64);

                foreach (int pageValues in wrapper.Chunks[chunk])
                {
                    ReadOnlySpan<ulong> latents = PcoPageDecoder.DecodeJoined(
                        meta, node.GetBuffer(pageBuffer).Span, pageValues,
                        primaryScratch.Span, secondaryScratch.Span);
                    pageBuffer++;

                    // The ordered latent form: shifted so the type's minimum is zero. For an
                    // unsigned type it is the value itself, which is why the shift is by MID
                    // rather than by a sign test -- and the shift is pointwise, so it is one
                    // vector add per lane group rather than a bounds-checked eight-byte write per
                    // value.
                    Span<ulong> target = MemoryMarshal.Cast<byte, ulong>(
                        destination.Slice(written * width, pageValues * width));
                    if (signed)
                    {
                        Bias(latents, target);
                    }
                    else
                    {
                        latents.CopyTo(target);
                    }

                    written += pageValues;
                }
            }
        }
        finally
        {
            secondaryScratch.Dispose();
            primaryScratch.Dispose();
        }

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);
        return context.Canonical.AddPrimitive(dtype, length, validity, ptype, output);
    }

    /// <summary>Adds 2^63 to every latent, turning the ordered form back into two's complement.</summary>
    private static void Bias(ReadOnlySpan<ulong> latents, Span<ulong> destination)
    {
        int i = 0;
        if (Vector<ulong>.IsSupported && latents.Length >= Vector<ulong>.Count)
        {
            Vector<ulong> mid = new Vector<ulong>(1UL << 63);
            int lanes = Vector<ulong>.Count;
            for (; i <= latents.Length - lanes; i += lanes)
            {
                (Vector.LoadUnsafe(in latents[i]) + mid).StoreUnsafe(ref destination[i]);
            }
        }

        for (; i < latents.Length; i++)
        {
            destination[i] = unchecked(latents[i] + (1UL << 63));
        }
    }
}
