// pco page decoding - pco-1.0.3/src/{page_latent_decompressor,wrapped/page_decompressor}.rs.
//
// A page is decoded in batches of 256. Per batch and per latent variable: pull one symbol per value
// from the tANS stream, then read that value's offset from a SECOND stream that starts where the
// symbols end, then undo the delta encoding. Finally the latent variables are joined into numbers
// according to the chunk's mode.
//
// FOUR INTERLEAVED ANS STATES, round-robin, so value i advances state i % 4 and its successor comes
// four values later. Decoding with one state would produce a plausible-looking sequence that is
// wrong from the second value on.
//
// THE OFFSETS ARE NOT INLINE with the symbols. The symbol pass records each value's offset WIDTH and
// a running sum of those widths; the offset pass then reads value i at `base + csum[i]` for
// `width[i]` bits. Reading offsets inline would consume the stream in the wrong order.
using System;
using System.Numerics;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>Decodes one pco page into latents and then into numbers.</summary>
internal static class PcoPageDecoder
{
    /// <summary>Values per batch.</summary>
    private const int BatchSize = 256;

    /// <summary>Interleaved ANS states.</summary>
    private const int Interleaving = 4;

    /// <summary>Decodes a page of 64-bit latents.</summary>
    /// <param name="chunk">The chunk's metadata.</param>
    /// <param name="page">The page's bytes.</param>
    /// <param name="valueCount">Values in this page.</param>
    /// <param name="primaryOut">At least <paramref name="valueCount"/> slots, caller-owned.</param>
    /// <param name="secondaryOut">
    /// At least <paramref name="valueCount"/> slots, caller-owned; unread when the chunk has no
    /// secondary latent variable.
    /// </param>
    /// <returns>
    /// The decoded latents, joined by the chunk's mode. A view over one of the two buffers, valid
    /// until the next call.
    /// </returns>
    internal static ReadOnlySpan<ulong> DecodeJoined(
        PcoChunkMeta chunk, ReadOnlySpan<byte> page, int valueCount, Span<ulong> primaryOut,
        Span<ulong> secondaryOut)
    {
        PcoBitReader reader = new PcoBitReader(page);

        PcoLatentState? delta = chunk.DeltaLatent is { } deltaVar
            ? PcoLatentState.Read(ref reader, deltaVar, 0, valueCount)
            : null;
        PcoLatentState primary = PcoLatentState.Read(
            ref reader, chunk.Primary, DeltaOrderFor(chunk, primary: true), valueCount);
        PcoLatentState? secondary = chunk.Secondary is { } secondaryVar
            ? PcoLatentState.Read(
                ref reader, secondaryVar, DeltaOrderFor(chunk, primary: false), valueCount)
            : null;

        reader.DrainEmptyByte("page metadata");

        // THE THREE PAGE ARRAYS ARE THE CALLER'S. A million-row pco column is a thousand pages,
        // and this used to allocate two or three `ulong[valueCount]` for each of them -- contract
        // §1.3's "no managed allocation on a decode path", a thousand times over.
        primaryOut = primaryOut[..valueCount];
        secondaryOut = secondary is null ? default : secondaryOut[..valueCount];

        int done = 0;
        while (done < valueCount)
        {
            int remaining = valueCount - done;
            int batch = Math.Min(BatchSize, remaining);

            delta?.ReadBatch(ref reader, remaining, batch, null);
            primary.ReadBatch(ref reader, remaining, batch, primaryOut.Slice(done, batch));
            secondary?.ReadBatch(ref reader, remaining, batch, secondaryOut.Slice(done, batch));

            done += batch;
        }

        return Join(chunk, primaryOut, secondaryOut);
    }

    /// <summary>The delta order applied to one latent variable.</summary>
    /// <remarks>
    /// The primary always takes the chunk's delta; the secondary takes it only when the metadata
    /// says so, and a lookback delta's own latent is never itself delta-encoded.
    /// </remarks>
    private static int DeltaOrderFor(PcoChunkMeta chunk, bool primary)
    {
        if (chunk.Delta != PcoDeltaKind.Consecutive)
        {
            return 0;
        }

        return primary || chunk.SecondaryUsesDelta ? chunk.DeltaOrder : 0;
    }

    private static ReadOnlySpan<ulong> Join(
        PcoChunkMeta chunk, Span<ulong> primary, Span<ulong> secondary)
    {
        if (chunk.Mode == PcoModeKind.Classic)
        {
            return primary;
        }

        if (chunk.Mode != PcoModeKind.IntMult)
        {
            CompressedThrow.Format($"pco mode {chunk.Mode} is not decoded yet.");
        }

        if (secondary.IsEmpty)
        {
            CompressedThrow.Format("A pco IntMult chunk has no secondary latent variable.");
        }

        // In place, into the primary: nothing reads it again, and a third array per page was the
        // same allocation the other two were.
        Span<ulong> joined = primary;
        for (int i = 0; i < primary.Length; i++)
        {
            // Both operands come off the wire, so both operations wrap, exactly as upstream's do.
            joined[i] = unchecked((primary[i] * chunk.ModeBase) + secondary[i]);
        }

        return joined;
    }
}

/// <summary>One latent variable's decoding state within a page.</summary>
internal sealed class PcoLatentState
{
    private readonly PcoAnsTable _table;
    private readonly int _ansSizeLog;
    private readonly int _binCount;
    // PER STATE, because both carry across the batches of one page: the four interleaved ANS
    // positions are read-modify-written per value, and a delta moment is updated by the untransform
    // and read again by the next batch.
    private readonly int[] _stateIndices = new int[4];
    private readonly ulong[] _deltaMoments;

    /// <summary>
    /// The per-batch working buffers, shared per thread instead of allocated per page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These three are written at the top of <c>Decode</c> and consumed before it returns - nothing
    /// in them survives the call - but they were instance fields, so a page carrying one latent
    /// variable allocated 5 kB of them and a chunk of many pages dropped that per page. The two
    /// fields above are the ones that genuinely carry state, and they stay where they are.
    /// </para>
    /// <para>
    /// Thread-static rather than pooled, for the reason <c>FsstSymbols</c> gives for its counting
    /// tables: <c>ArrayPool&lt;T&gt;.Shared</c> is process-global and this is a fixed, bounded 5 kB
    /// per thread that has ever decoded pco.
    /// </para>
    /// </remarks>
    [ThreadStatic]
    private static ulong[]? ScratchValues;

    /// <inheritdoc cref="ScratchValues"/>
    [ThreadStatic]
    private static int[]? ScratchOffsetBits;

    /// <inheritdoc cref="ScratchValues"/>
    [ThreadStatic]
    private static long[]? ScratchOffsetCumulative;

    /// <summary>Values per batch; must match <c>PcoPageDecoder.BatchSize</c>.</summary>
    private const int BatchSize = 256;

    private static ulong[] Scratch => ScratchValues ??= new ulong[BatchSize];

    private static int[] OffsetBits => ScratchOffsetBits ??= new int[BatchSize];

    private static long[] OffsetCumulative => ScratchOffsetCumulative ??= new long[BatchSize];

    private PcoLatentState(PcoAnsTable table, int ansSizeLog, int binCount, ulong[] deltaMoments)
    {
        _table = table;
        _ansSizeLog = ansSizeLog;
        _binCount = binCount;
        _deltaMoments = deltaMoments;
    }

    /// <summary>Reads this variable's page metadata: its delta moments and four ANS states.</summary>
    /// <param name="reader">The page reader, positioned at this variable's metadata.</param>
    /// <param name="variable">The chunk-level table for this variable.</param>
    /// <param name="deltaOrder">Delta moments stored for this variable.</param>
    /// <param name="valueCount">Values in the page; unused beyond validation.</param>
    /// <returns>The state.</returns>
    internal static PcoLatentState Read(
        ref PcoBitReader reader, PcoLatentVar variable, int deltaOrder, int valueCount)
    {
        _ = valueCount;

        ulong[] moments = new ulong[deltaOrder];
        for (int i = 0; i < deltaOrder; i++)
        {
            moments[i] = reader.ReadUInt(64);
        }

        PcoLatentState state = new PcoLatentState(
            variable.Table,
            variable.AnsSizeLog,
            variable.Bins.Length,
            moments);

        for (int i = 0; i < 4; i++)
        {
            state._stateIndices[i] = (int)reader.ReadUInt(variable.AnsSizeLog);
        }

        return state;
    }

    /// <summary>Decodes one batch into <paramref name="destination"/>.</summary>
    /// <param name="reader">The page reader.</param>
    /// <param name="remaining">Values left in the page before this batch.</param>
    /// <param name="batch">Values this batch produces.</param>
    /// <param name="destination">Where to put them; empty for a delta variable.</param>
    internal void ReadBatch(
        ref PcoBitReader reader, int remaining, int batch, Span<ulong> destination)
    {
        // The values that come from the delta moments are not on the wire, so the symbol pass is
        // shorter than the batch by the delta order - but only at the end of the page.
        ulong[] scratch = Scratch;
        int preDelta = Math.Min(batch, Math.Max(0, remaining - _deltaMoments.Length));
        ReadPreDelta(ref reader, preDelta);

        if (_deltaMoments.Length > 0)
        {
            UndoConsecutiveDelta(batch);
        }

        if (!destination.IsEmpty)
        {
            scratch.AsSpan(0, batch).CopyTo(destination);
        }
    }

    private void ReadPreDelta(ref PcoBitReader reader, int count)
    {
        if (count == 0)
        {
            return;
        }

        ulong[] scratch = Scratch;
        int[] offsetBits = OffsetBits;
        long[] offsetCumulative = OffsetCumulative;
        long offsetBitTotal = 0;
        if (_binCount > 1)
        {
            for (int i = 0; i < count; i++)
            {
                int slot = _stateIndices[i % 4];
                PcoAnsNode node = _table.Nodes[slot];
                ulong ansValue = reader.ReadUInt(node.BitsToRead);

                scratch[i] = _table.StateLowers[slot];
                offsetBits[i] = node.OffsetBits;
                offsetCumulative[i] = offsetBitTotal;
                offsetBitTotal += node.OffsetBits;

                _stateIndices[i % 4] = node.NextStateIndexBase + (int)ansValue;
            }
        }
        else
        {
            // A single bin means every value is that bin: upstream skips the ANS stream entirely
            // rather than reading zero-width symbols. Every value then has the SAME width, so the
            // per-value width and cumulative-position arrays describe nothing -- the position of
            // value i is `base + i * offsetBits`, and the fill is one vectorized store.
            int uniformBits = _table.Nodes[0].OffsetBits;
            ulong lower = _table.StateLowers[0];
            scratch.AsSpan(0, count).Fill(lower);

            if (uniformBits == 0)
            {
                // ...AND A BIN THAT NEEDS NO OFFSET BITS READS NOTHING AT ALL. The whole batch is
                // the bin's lower bound, and the reader does not move. That is what a delta-encoded
                // arithmetic ramp becomes -- the commonest shape a pco column has -- and it was
                // running two loops per value to discover it, plus 20 bytes of bookkeeping written
                // per value and read back to be skipped.
                return;
            }

            long uniformBase = reader.BitPosition;
            for (int i = 0; i < count; i++)
            {
                scratch[i] = unchecked(
                    lower + reader.ReadAt(uniformBase + ((long)i * uniformBits), uniformBits));
            }

            reader.SeekBits(uniformBase + ((long)count * uniformBits));
            return;
        }

        // The offsets live in their own stream starting where the symbols ended.
        long basePosition = reader.BitPosition;
        for (int i = 0; i < count; i++)
        {
            if (offsetBits[i] == 0)
            {
                continue;
            }

            ulong offset = reader.ReadAt(basePosition + offsetCumulative[i], offsetBits[i]);
            scratch[i] = unchecked(scratch[i] + offset);
        }

        reader.SeekBits(basePosition + offsetBitTotal);
    }

    /// <summary>Undoes a consecutive delta over the batch, consuming the page's moments.</summary>
    /// <remarks>
    /// Each order is one prefix sum that WRITES the running moment into a slot before reading what
    /// was there, so the first values of a page come from the moments themselves. The moments are
    /// carried across batches, which is why they live on this object rather than on the batch.
    /// </remarks>
    private void UndoConsecutiveDelta(int batch)
    {
        // The 2^63 bias is pointwise and contiguous, so it is one vector add per lane group.
        ulong[] scratch = Scratch;
        Span<ulong> values = scratch.AsSpan(0, batch);
        int biased = 0;
        if (Vector<ulong>.IsSupported && batch >= Vector<ulong>.Count)
        {
            Vector<ulong> bias = new Vector<ulong>(1UL << 63);
            int lanes = Vector<ulong>.Count;
            for (; biased <= batch - lanes; biased += lanes)
            {
                (Vector.LoadUnsafe(in values[biased]) + bias).StoreUnsafe(ref values[biased]);
            }
        }

        for (; biased < batch; biased++)
        {
            values[biased] = unchecked(values[biased] + (1UL << 63));
        }

        for (int order = _deltaMoments.Length - 1; order >= 0; order--)
        {
            ulong moment = _deltaMoments[order];
            for (int i = 0; i < batch; i++)
            {
                ulong previous = scratch[i];
                scratch[i] = moment;
                moment = unchecked(moment + previous);
            }

            _deltaMoments[order] = moment;
        }
    }
}
