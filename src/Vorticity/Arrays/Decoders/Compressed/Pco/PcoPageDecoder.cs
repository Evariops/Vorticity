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
    /// <returns>The decoded latents, joined by the chunk's mode.</returns>
    internal static ulong[] DecodeJoined(PcoChunkMeta chunk, ReadOnlySpan<byte> page, int valueCount)
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

        ulong[] primaryOut = new ulong[valueCount];
        ulong[]? secondaryOut = secondary is null ? null : new ulong[valueCount];

        int done = 0;
        while (done < valueCount)
        {
            int remaining = valueCount - done;
            int batch = Math.Min(BatchSize, remaining);

            delta?.ReadBatch(ref reader, remaining, batch, null);
            primary.ReadBatch(ref reader, remaining, batch, primaryOut.AsSpan(done, batch));
            secondary?.ReadBatch(ref reader, remaining, batch, secondaryOut!.AsSpan(done, batch));

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

    private static ulong[] Join(PcoChunkMeta chunk, ulong[] primary, ulong[]? secondary)
    {
        if (chunk.Mode == PcoModeKind.Classic)
        {
            return primary;
        }

        if (chunk.Mode != PcoModeKind.IntMult)
        {
            CompressedThrow.Format($"pco mode {chunk.Mode} is not decoded yet.");
        }

        if (secondary is null)
        {
            CompressedThrow.Format("A pco IntMult chunk has no secondary latent variable.");
        }

        ulong[] joined = new ulong[primary.Length];
        for (int i = 0; i < primary.Length; i++)
        {
            // Both operands come off the wire, so both operations wrap, exactly as upstream's do.
            joined[i] = unchecked((primary[i] * chunk.ModeBase) + secondary![i]);
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
    private readonly int[] _stateIndices = new int[4];
    private readonly ulong[] _deltaMoments;
    private readonly ulong[] _scratch = new ulong[256];
    private readonly int[] _offsetBits = new int[256];
    private readonly long[] _offsetCumulative = new int[256].Length == 0 ? [] : new long[256];

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
            PcoAnsTable.Build(variable.AnsSizeLog, variable.Bins),
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
        int preDelta = Math.Min(batch, Math.Max(0, remaining - _deltaMoments.Length));
        ReadPreDelta(ref reader, preDelta);

        if (_deltaMoments.Length > 0)
        {
            UndoConsecutiveDelta(batch);
        }

        if (!destination.IsEmpty)
        {
            _scratch.AsSpan(0, batch).CopyTo(destination);
        }
    }

    private void ReadPreDelta(ref PcoBitReader reader, int count)
    {
        if (count == 0)
        {
            return;
        }

        long offsetBitTotal = 0;
        if (_binCount > 1)
        {
            for (int i = 0; i < count; i++)
            {
                int slot = _stateIndices[i % 4];
                PcoAnsNode node = _table.Nodes[slot];
                ulong ansValue = reader.ReadUInt(node.BitsToRead);

                _scratch[i] = _table.StateLowers[slot];
                _offsetBits[i] = node.OffsetBits;
                _offsetCumulative[i] = offsetBitTotal;
                offsetBitTotal += node.OffsetBits;

                _stateIndices[i % 4] = node.NextStateIndexBase + (int)ansValue;
            }
        }
        else
        {
            // A single bin means every value is that bin: upstream skips the ANS stream entirely
            // rather than reading zero-width symbols.
            int offsetBits = _table.Nodes[0].OffsetBits;
            for (int i = 0; i < count; i++)
            {
                _scratch[i] = _table.StateLowers[0];
                _offsetBits[i] = offsetBits;
                _offsetCumulative[i] = offsetBitTotal;
                offsetBitTotal += offsetBits;
            }
        }

        // The offsets live in their own stream starting where the symbols ended.
        long basePosition = reader.BitPosition;
        for (int i = 0; i < count; i++)
        {
            if (_offsetBits[i] == 0)
            {
                continue;
            }

            ulong offset = reader.ReadAt(basePosition + _offsetCumulative[i], _offsetBits[i]);
            _scratch[i] = unchecked(_scratch[i] + offset);
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
        for (int i = 0; i < batch; i++)
        {
            _scratch[i] = unchecked(_scratch[i] + (1UL << 63));
        }

        for (int order = _deltaMoments.Length - 1; order >= 0; order--)
        {
            ulong moment = _deltaMoments[order];
            for (int i = 0; i < batch; i++)
            {
                ulong previous = _scratch[i];
                _scratch[i] = moment;
                moment = unchecked(moment + previous);
            }

            _deltaMoments[order] = moment;
        }
    }
}
