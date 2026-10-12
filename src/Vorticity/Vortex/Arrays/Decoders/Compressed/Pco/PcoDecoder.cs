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
    public override bool SelectsWithoutFullDecode => true;

    /// <inheritdoc/>
    /// <remarks>A row costs the page that holds its value, a thousand values or more, and its chunk's tables.</remarks>
    public override bool SelectsByRow => false;

    /// <inheritdoc/>
    /// <remarks>
    /// Only a selection whose rows lie <see cref="SparsePages"/> pages apart or more: a row costs
    /// the page that holds its value, so rows closer than that decode most of the pages they span,
    /// which the whole node does at less cost, decoded once and kept for every batch of the take.
    /// </remarks>
    public override bool SelectsWithoutFullDecodeOf(ArrayDecodeContext context, in ArrayNode node, DType dtype)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScanContext scan = context.Scan;
        if (scan.SelectionCount < 2 || !scan.TryGetSelectionBounds(out int first, out int last))
        {
            return true;
        }

        long pageValues = Math.Max(1, PcoWrapperMetadata.FirstPageValues(node.Metadata));
        return (long)scan.SelectionCount * pageValues * SparsePages <= (long)last - first + 1;
    }

    /// <summary>How many pages apart, on average, the rows of a selection the pages serve lie at least.</summary>
    private const int SparsePages = 4;

    /// <inheritdoc/>
    /// <remarks>
    /// The pco stream holds the valid rows' values only, as upstream's writer collects them: a
    /// column without nulls is decoded straight into its output, one with nulls into a dense
    /// rental first and then spread over its valid rows, a null row zero.
    /// </remarks>
    public override int Decode(ArrayDecodeContext context, in ArrayNode node, DType dtype, int length)
    {
        ArgumentNullException.ThrowIfNull(context);
        PcoNumber number = NumberOf(dtype);
        int width = number.LatentBits / 8;
        PcoWrapperMetadata wrapper = PcoWrapperMetadata.Read(node.Metadata);
        Validity validity = CheckedValidity(context, in node, dtype, length, in wrapper, out int valid);

        int outputBytes = ArrayDecodeContext.CheckedMultiply(length, width, Id + " rows");
        VortexBuffer output;
        if (valid == length)
        {
            output = CompressedValues.AllocateUninitialized(context, outputBytes, width, Id, out Span<byte> destination);
            DecodeValues(node, wrapper, number, destination, ranks: default);
        }
        else
        {
            output = CompressedValues.Allocate(context, outputBytes, width, Id, out Span<byte> destination);
            int denseBytes = (int)wrapper.ValueCount * width;
            byte[] dense = ArrayPool<byte>.Shared.Rent(Math.Max(denseBytes, 1));
            try
            {
                DecodeValues(node, wrapper, number, dense.AsSpan(0, denseBytes), ranks: default);
                ValidityMask mask = ValidityMask.From(context, validity);
                ValidRows.Spread(dense.AsSpan(0, valid * width), destination, in mask, length, width, Id);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(dense);
            }
        }

        return context.Canonical.AddPrimitive(dtype, length, validity, dtype.PType, output);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A page decodes alone, given its chunk's metadata: only the pages that hold a wanted row's
    /// value are decoded, each into a rental of its own, and the wanted values copied out of it. A
    /// row's value is found among the valid rows' values by its rank, the valid rows before it.
    /// </remarks>
    public override int DecodeSelected(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, ReadOnlySpan<int> wanted)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (wanted.IsEmpty)
        {
            return base.DecodeSelected(context, in node, dtype, length, wanted);
        }

        PcoNumber number = NumberOf(dtype);
        int width = number.LatentBits / 8;
        PcoWrapperMetadata wrapper = PcoWrapperMetadata.Read(node.Metadata);
        Validity validity = CheckedValidity(context, in node, dtype, length, in wrapper, out int valid);

        Span<int> stack = stackalloc int[64];
        using Scratch<int> ranks = new Scratch<int>(wanted.Length, stack);
        bool everyValue = Rank(ValidityMask.From(context, validity), wanted, ranks.Span);

        int outputBytes = ArrayDecodeContext.CheckedMultiply(wanted.Length, width, Id + " rows");
        Span<byte> destination;
        VortexBuffer output = everyValue
            ? CompressedValues.AllocateUninitialized(context, outputBytes, width, Id, out destination)
            : CompressedValues.Allocate(context, outputBytes, width, Id, out destination);
        if (valid != 0)
        {
            DecodeValues(node, wrapper, number, destination, ranks.Span);
        }

        Validity selected = context.DecodeValiditySelected(in node, 0, dtype.Nullability, length, wanted);
        return context.Canonical.AddPrimitive(dtype, wanted.Length, selected, dtype.PType, output);
    }

    /// <summary>The number type <paramref name="dtype"/> is stored as.</summary>
    private static PcoNumber NumberOf(DType dtype)
    {
        if (dtype.Kind != DTypeKind.Primitive)
        {
            CompressedThrow.Format($"{Id} requires a primitive dtype; the node declares {dtype.Kind}.");
        }

        if (PcoNumber.Of(dtype.PType) is not { } number)
        {
            CompressedThrow.Format($"{Id} stores numbers of 16 bits and more; this node is {dtype.PType.Name()}.");
            return default;
        }

        return number;
    }

    /// <summary>
    /// The node's validity, its buffers and its values counted against the wrapper's: upstream's
    /// checks, no more values than rows, every row's when none is null, and no fewer than the
    /// valid rows, which take the first of them.
    /// </summary>
    private static Validity CheckedValidity(
        ArrayDecodeContext context, in ArrayNode node, DType dtype, int length, in PcoWrapperMetadata wrapper, out int valid)
    {
        int expected = wrapper.ChunkCount + wrapper.PageCount;
        if (node.BufferCount != expected)
        {
            CompressedThrow.Format(
                $"{Id} declares {wrapper.ChunkCount} chunks and {wrapper.PageCount} pages, " +
                $"needing {expected} buffers; the node has {node.BufferCount}.");
        }

        Validity validity = context.DecodeValidity(in node, 0, dtype.Nullability, length);
        valid = ValidRows.Count(context, validity, length);
        if (wrapper.ValueCount > length || wrapper.ValueCount < valid || (valid == length && wrapper.ValueCount != length))
        {
            CompressedThrow.Format(
                $"{Id}'s pages hold {wrapper.ValueCount} values; the node declares {length} rows, {valid} of them valid.");
        }

        return validity;
    }

    /// <summary>
    /// Each wanted row's value among the valid rows' values, its rank, into <paramref name="ranks"/>;
    /// -1 for a null row.
    /// </summary>
    /// <returns>Whether every wanted row holds a value.</returns>
    private static bool Rank(in ValidityMask mask, ReadOnlySpan<int> wanted, Span<int> ranks)
    {
        if (mask.AllValid)
        {
            wanted.CopyTo(ranks);
            return true;
        }

        if (mask.AllInvalid)
        {
            ranks.Fill(-1);
            return false;
        }

        // The rows are ascending, so each rank is the last plus the valid rows between the two.
        bool every = true;
        int rank = 0;
        int rankedTo = 0;
        for (int i = 0; i < wanted.Length; i++)
        {
            int row = wanted[i];
            rank += BitmapKernels.CountSet(mask.Bits, mask.BitOffset + rankedTo, row - rankedTo);
            rankedTo = row;
            bool holds = mask.IsValid(row);
            ranks[i] = holds ? rank : -1;
            every &= holds;
        }

        return every;
    }

    /// <summary>
    /// Every page of every chunk decoded into <paramref name="destination"/>, the values back to
    /// back; or, given the <paramref name="ranks"/> of the values a selection wants, -1 for a row
    /// that has none, only the pages that hold one decoded, up to the last of them, and each wanted
    /// value copied into its row of <paramref name="destination"/>.
    /// </summary>
    private static void DecodeValues(
        in ArrayNode node, PcoWrapperMetadata wrapper, PcoNumber number, Span<byte> destination, ReadOnlySpan<int> ranks)
    {
        int width = number.LatentBits / 8;
        bool selected = !ranks.IsEmpty;

        // A batch's working buffers and the secondary latent variable's, rented once for the whole
        // node rather than allocated per page: a large column runs to many hundreds of pages, and a
        // decode path must not allocate once per page. 64-bit numbers are decoded into the output
        // itself, narrower ones a batch at a time through a primary buffer. They belong to this
        // decode and to nothing wider; PcoBatchScratch says why sharing them beyond it is not an
        // option. A selection decodes each page it reads into a rental of its own, rented by the
        // first and again only by a larger one.
        Scratch<ulong> secondaryScratch = new Scratch<ulong>(PcoPageDecoder.BatchSize, default);
        Scratch<ulong> batchValues = new Scratch<ulong>(PcoPageDecoder.BatchSize, default);
        Scratch<int> batchOffsetBits = new Scratch<int>(PcoPageDecoder.BatchSize, default);
        Scratch<ulong> primaryScratch = new Scratch<ulong>(width == 8 ? 0 : PcoPageDecoder.BatchSize, default);
        byte[]? pageRental = null;

        // The latent states are reset by each page, and the chunk metadata with its tables refilled
        // by each chunk, rather than rebuilt, for the same reason as the buffers above; and both are
        // kept for the next decode rather than built by each, since neither holds a file's data
        // past the page or the chunk that refills it.
        Reused reused = Interlocked.Exchange(ref Spare, null) ?? new Reused();
        try
        {
            PcoBatchScratch batchScratch = new PcoBatchScratch(
                batchValues.Span, batchOffsetBits.Span, reused.States, primaryScratch.Span);
            int pageBuffer = wrapper.ChunkCount;
            int written = 0;
            int chunk = 0;
            int next = 0;
            int metaChunk = -1;
            bool done = false;
            PcoChunkMeta? meta = null;
            foreach (PcoWrapperMetadata.PageEnumerator pages in wrapper.GetChunks())
            {
                if (!selected)
                {
                    meta = reused.Meta.Refill(wrapper.Header, node.GetBuffer(chunk).Span, number);
                }

                foreach (int pageValues in pages)
                {
                    if (!selected)
                    {
                        PcoPageDecoder.DecodeJoined(
                            meta!, node.GetBuffer(pageBuffer).Span, pageValues, secondaryScratch.Span, in batchScratch,
                            destination.Slice(written * width, pageValues * width));
                    }
                    else
                    {
                        // The null rows have no value to wait for, and the pages past the last
                        // wanted value nothing to give.
                        while (next < ranks.Length && ranks[next] < 0)
                        {
                            next++;
                        }

                        if (next == ranks.Length)
                        {
                            done = true;
                            break;
                        }

                        int end = written + pageValues;
                        if (ranks[next] < end)
                        {
                            // A chunk's tables are built the first time one of its pages is wanted.
                            if (metaChunk != chunk)
                            {
                                meta = reused.Meta.Refill(wrapper.Header, node.GetBuffer(chunk).Span, number);
                                metaChunk = chunk;
                            }

                            int pageBytes = pageValues * width;
                            if (pageRental is null || pageRental.Length < pageBytes)
                            {
                                byte[]? smaller = pageRental;
                                pageRental = null;
                                if (smaller is not null)
                                {
                                    ArrayPool<byte>.Shared.Return(smaller);
                                }

                                pageRental = ArrayPool<byte>.Shared.Rent(pageBytes);
                            }

                            Span<byte> page = pageRental.AsSpan(0, pageBytes);
                            PcoPageDecoder.DecodeJoined(
                                meta!, node.GetBuffer(pageBuffer).Span, pageValues, secondaryScratch.Span, in batchScratch, page);
                            for (; next < ranks.Length && ranks[next] < end; next++)
                            {
                                int rank = ranks[next];
                                if (rank >= 0)
                                {
                                    page.Slice((rank - written) * width, width).CopyTo(destination.Slice(next * width, width));
                                }
                            }
                        }
                    }

                    pageBuffer++;
                    written += pageValues;
                }

                if (done)
                {
                    break;
                }

                chunk++;
            }
        }
        finally
        {
            // A dictionary is rented by its chunk; the metadata kept for the next decode holds none.
            reused.Meta.Release();
            Volatile.Write(ref Spare, reused);
            if (pageRental is not null)
            {
                ArrayPool<byte>.Shared.Return(pageRental);
            }

            primaryScratch.Dispose();
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
