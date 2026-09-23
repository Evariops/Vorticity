using System;
using System.Runtime.InteropServices;
using System.Threading;

using Vorticity.Arrays.Decoders.Canonical;

using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>
/// Decodes <c>vortex.pco</c>, a thin wrapper around pco's own format: metadata naming pco's file
/// header and, per chunk, its pages' value counts; buffers holding the per-chunk pco metadata
/// followed by the page bodies; zero or one validity child. Everything below that belongs to pco
/// and lives in the sibling types here.
/// </summary>
internal sealed class PcoDecoder : ArrayDecoder
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

        int expected = wrapper.ChunkCount + wrapper.PageCount;
        if (node.BufferCount != expected)
        {
            CompressedThrow.Format(
                $"{Id} declares {wrapper.ChunkCount} chunks and {wrapper.PageCount} pages, " +
                $"needing {expected} buffers; the node has {node.BufferCount}.");
        }

        if (wrapper.ValueCount != length)
        {
            CompressedThrow.Format(
                $"{Id}'s pages hold {wrapper.ValueCount} values; the node declares {length} rows.");
        }

        VortexBuffer output = CompressedValues.Allocate(
            context, length * width, width, Id, out Span<byte> destination);

        bool signed = ptype.IsSignedInteger();

        // A batch's working buffers and the secondary latent variable's, rented once for the whole
        // node rather than allocated per page: a large column runs to many hundreds of pages, and a
        // decode path must not allocate once per page. The primary is decoded into the output
        // itself. They belong to this decode and to nothing wider; PcoBatchScratch says why sharing
        // them beyond it is not an option.
        Scratch<ulong> secondaryScratch = new Scratch<ulong>(PcoPageDecoder.BatchSize, default);
        Scratch<ulong> batchValues = new Scratch<ulong>(PcoPageDecoder.BatchSize, default);
        Scratch<int> batchOffsetBits = new Scratch<int>(PcoPageDecoder.BatchSize, default);
        Scratch<long> batchOffsetCumulative = new Scratch<long>(PcoPageDecoder.BatchSize, default);

        // The latent states are reset by each page, and the chunk metadata with its tables refilled
        // by each chunk, rather than rebuilt, for the same reason as the buffers above; and both are
        // kept for the next decode rather than built by each, since neither holds a file's data
        // past the page or the chunk that refills it.
        Reused reused = Interlocked.Exchange(ref Spare, null) ?? new Reused();
        try
        {
            PcoBatchScratch batchScratch = new PcoBatchScratch(
                batchValues.Span, batchOffsetBits.Span, batchOffsetCumulative.Span, reused.States);
            int pageBuffer = wrapper.ChunkCount;
            int written = 0;
            int chunk = 0;
            foreach (PcoWrapperMetadata.PageEnumerator pages in wrapper.GetChunks())
            {
                PcoChunkMeta meta = reused.Meta.Refill(
                    wrapper.Header, node.GetBuffer(chunk).Span, latentBits: 64);
                chunk++;

                foreach (int pageValues in pages)
                {
                    // The ordered latent form is shifted so the type's minimum is zero. For an
                    // unsigned type it is the value itself, and for a signed one the shift back is
                    // a single addition of the midpoint rather than a sign test, made as the page's
                    // latents are joined into the output.
                    Span<ulong> target = MemoryMarshal.Cast<byte, ulong>(
                        destination.Slice(written * width, pageValues * width));
                    PcoPageDecoder.DecodeJoined(
                        meta, node.GetBuffer(pageBuffer).Span, pageValues,
                        secondaryScratch.Span, in batchScratch, target, signed ? 1UL << 63 : 0);
                    pageBuffer++;
                    written += pageValues;
                }
            }
        }
        finally
        {
            Volatile.Write(ref Spare, reused);
            batchOffsetCumulative.Dispose();
            batchOffsetBits.Dispose();
            batchValues.Dispose();
            secondaryScratch.Dispose();
        }

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);
        return context.Canonical.AddPrimitive(dtype, length, validity, ptype, output);
    }

    /// <summary>
    /// What no decode holds, taken by the next; a decode that finds nothing builds its own, and the
    /// last to finish leaves what it used here.
    /// </summary>
    private static Reused? Spare;

    /// <summary>What a decode keeps for the next: the latent states, and the chunk metadata with its tables.</summary>
    private sealed class Reused
    {
        internal PcoLatentState[] States { get; } = NewStates();

        internal PcoChunkMeta Meta { get; } = new PcoChunkMeta();

        private static PcoLatentState[] NewStates()
        {
            PcoLatentState[] states = new PcoLatentState[PcoPageDecoder.MaxLatentVars];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = new PcoLatentState();
            }

            return states;
        }
    }
}
