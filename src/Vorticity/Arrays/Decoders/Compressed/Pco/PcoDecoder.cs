using System;
using System.Buffers;
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
    /// <remarks>
    /// The pco stream holds the valid rows' values only, as upstream's writer collects them: a
    /// column without nulls is decoded straight into its output, one with nulls into a dense
    /// rental first and then spread over its valid rows, a null row zero.
    /// </remarks>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (dtype.Kind != DTypeKind.Primitive)
        {
            CompressedThrow.Format($"{Id} requires a primitive dtype; the node declares {dtype.Kind}.");
        }

        PType ptype = dtype.PType;
        if (PcoNumber.Of(ptype) is not { } number)
        {
            CompressedThrow.Format($"{Id} stores numbers of 16 bits and more; this node is {ptype.Name()}.");
            return -1;
        }

        int width = number.LatentBits / 8;
        PcoWrapperMetadata wrapper = PcoWrapperMetadata.Read(node.Metadata);

        int expected = wrapper.ChunkCount + wrapper.PageCount;
        if (node.BufferCount != expected)
        {
            CompressedThrow.Format(
                $"{Id} declares {wrapper.ChunkCount} chunks and {wrapper.PageCount} pages, " +
                $"needing {expected} buffers; the node has {node.BufferCount}.");
        }

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);
        int valid = ValidRows.Count(context, validity, length);

        // Upstream's checks: no more values than rows, every row's when none is null, and no
        // fewer than the valid rows, which take the first of them.
        if (wrapper.ValueCount > length || wrapper.ValueCount < valid || (valid == length && wrapper.ValueCount != length))
        {
            CompressedThrow.Format(
                $"{Id}'s pages hold {wrapper.ValueCount} values; the node declares {length} rows, {valid} of them valid.");
        }

        int outputBytes = ArrayDecodeContext.CheckedMultiply(length, width, Id + " rows");
        VortexBuffer output;
        if (valid == length)
        {
            output = CompressedValues.AllocateUninitialized(context, outputBytes, width, Id, out Span<byte> destination);
            DecodeValues(node, wrapper, number, destination);
        }
        else
        {
            output = CompressedValues.Allocate(context, outputBytes, width, Id, out Span<byte> destination);
            int denseBytes = (int)wrapper.ValueCount * width;
            byte[] dense = ArrayPool<byte>.Shared.Rent(Math.Max(denseBytes, 1));
            try
            {
                DecodeValues(node, wrapper, number, dense.AsSpan(0, denseBytes));
                ValidityMask mask = ValidityMask.From(context, validity);
                ValidRows.Spread(dense.AsSpan(0, valid * width), destination, in mask, length, width, Id);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(dense);
            }
        }

        return context.Canonical.AddPrimitive(dtype, length, validity, ptype, output);
    }

    /// <summary>Every page of every chunk decoded into <paramref name="destination"/>, the values back to back.</summary>
    private static void DecodeValues(in ArrayNode node, PcoWrapperMetadata wrapper, PcoNumber number, Span<byte> destination)
    {
        int width = number.LatentBits / 8;

        // A batch's working buffers and the secondary latent variable's, rented once for the whole
        // node rather than allocated per page: a large column runs to many hundreds of pages, and a
        // decode path must not allocate once per page. 64-bit numbers are decoded into the output
        // itself, narrower ones a batch at a time through a primary buffer. They belong to this
        // decode and to nothing wider; PcoBatchScratch says why sharing them beyond it is not an
        // option.
        Scratch<ulong> secondaryScratch = new Scratch<ulong>(PcoPageDecoder.BatchSize, default);
        Scratch<ulong> batchValues = new Scratch<ulong>(PcoPageDecoder.BatchSize, default);
        Scratch<int> batchOffsetBits = new Scratch<int>(PcoPageDecoder.BatchSize, default);
        Scratch<long> batchOffsetCumulative = new Scratch<long>(PcoPageDecoder.BatchSize, default);
        Scratch<ulong> primaryScratch = new Scratch<ulong>(width == 8 ? 0 : PcoPageDecoder.BatchSize, default);

        // The latent states are reset by each page, and the chunk metadata with its tables refilled
        // by each chunk, rather than rebuilt, for the same reason as the buffers above; and both are
        // kept for the next decode rather than built by each, since neither holds a file's data
        // past the page or the chunk that refills it.
        Reused reused = Interlocked.Exchange(ref Spare, null) ?? new Reused();
        try
        {
            PcoBatchScratch batchScratch = new PcoBatchScratch(
                batchValues.Span, batchOffsetBits.Span, batchOffsetCumulative.Span, reused.States, primaryScratch.Span);
            int pageBuffer = wrapper.ChunkCount;
            int written = 0;
            int chunk = 0;
            foreach (PcoWrapperMetadata.PageEnumerator pages in wrapper.GetChunks())
            {
                PcoChunkMeta meta = reused.Meta.Refill(wrapper.Header, node.GetBuffer(chunk).Span, number);
                chunk++;

                foreach (int pageValues in pages)
                {
                    PcoPageDecoder.DecodeJoined(
                        meta, node.GetBuffer(pageBuffer).Span, pageValues, secondaryScratch.Span, in batchScratch,
                        destination.Slice(written * width, pageValues * width));
                    pageBuffer++;
                    written += pageValues;
                }
            }
        }
        finally
        {
            // A dictionary is rented by its chunk; the metadata kept for the next decode holds none.
            reused.Meta.Release();
            Volatile.Write(ref Spare, reused);
            primaryScratch.Dispose();
            batchOffsetCumulative.Dispose();
            batchOffsetBits.Dispose();
            batchValues.Dispose();
            secondaryScratch.Dispose();
        }
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
