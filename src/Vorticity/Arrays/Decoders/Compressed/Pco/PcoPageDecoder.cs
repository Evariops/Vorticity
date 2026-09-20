using System;
using System.Numerics;

namespace Vorticity.Arrays.Decoders.Compressed.Pco;

/// <summary>
/// Decodes one pco page into latents and then into numbers. A page goes batch by batch: per latent
/// variable, pull one symbol per value from the tANS stream, read that value's offset from a
/// second stream that starts where the symbols end, undo the delta encoding, and finally join the
/// latent variables according to the chunk's mode.
/// </summary>
/// <remarks>
/// <para>
/// The ANS states are interleaved four ways, round-robin, so value i advances state i % 4 and its
/// successor comes four values later. Decoding with a single state yields a plausible-looking
/// sequence that is wrong from the second value on.
/// </para>
/// <para>
/// The offsets are not inline with the symbols: the symbol pass records each value's offset width
/// and a running sum of those widths, and the offset pass then reads value i at that running sum
/// past the base. Reading an offset inline would consume the stream in the wrong order.
/// </para>
/// </remarks>
internal static class PcoPageDecoder
{
    /// <summary>Latent variables a page can have: the delta, the primary and the secondary.</summary>
    internal const int MaxLatentVars = 3;

    /// <summary>Largest delta order the metadata can express: it is written in three bits.</summary>
    internal const int MaxDeltaOrder = 7;

    /// <summary>Values per batch. Also the size of the buffers in <see cref="PcoBatchScratch"/>.</summary>
    internal const int BatchSize = 256;

    /// <summary>Decodes a page of 64-bit latents.</summary>
    /// <param name="chunk">The chunk's metadata.</param>
    /// <param name="page">The page's bytes.</param>
    /// <param name="valueCount">Values in this page.</param>
    /// <param name="primaryOut">At least <paramref name="valueCount"/> slots, caller-owned.</param>
    /// <param name="secondaryOut">
    /// At least <paramref name="valueCount"/> slots, caller-owned; unread when the chunk has no
    /// secondary latent variable.
    /// </param>
    /// <param name="scratch">
    /// A batch's working buffers, caller-owned so that every page of a node shares one rental.
    /// </param>
    /// <returns>
    /// The decoded latents, joined by the chunk's mode. A view over one of the two buffers, valid
    /// until the next call.
    /// </returns>
    internal static ReadOnlySpan<ulong> DecodeJoined(
        PcoChunkMeta chunk, ReadOnlySpan<byte> page, int valueCount, Span<ulong> primaryOut,
        Span<ulong> secondaryOut, in PcoBatchScratch scratch)
    {
        PcoBitReader reader = new PcoBitReader(page);

        // The latent states are the caller's and are reset, not built. Each one owns two small
        // arrays, a page needs at most three of them and never keeps them, so `PcoDecoder` builds
        // them once per node and every page refills them in place rather than leaving a page's
        // worth of objects on the managed heap.
        //
        // Reset rather than a ref struct over borrowed spans: this method returns the state, so
        // spans it captured could outlive their frame and ref-safety refuses it. Objects reused
        // per node are the same saving without that argument.
        PcoLatentState? delta = chunk.DeltaLatent is { } deltaVar
            ? scratch.States[0].Reset(ref reader, deltaVar, 0)
            : null;
        PcoLatentState primary = scratch.States[1].Reset(
            ref reader, chunk.Primary, DeltaOrderFor(chunk, primary: true));
        PcoLatentState? secondary = chunk.Secondary is { } secondaryVar
            ? scratch.States[2].Reset(
                ref reader, secondaryVar, DeltaOrderFor(chunk, primary: false))
            : null;

        reader.DrainEmptyByte("page metadata");

        // The page arrays are the caller's too: a large column runs to many hundreds of pages, and
        // a decode path must not allocate a value-sized array for each of them.
        primaryOut = primaryOut[..valueCount];
        secondaryOut = secondary is null ? default : secondaryOut[..valueCount];

        int done = 0;
        while (done < valueCount)
        {
            int remaining = valueCount - done;
            int batch = Math.Min(BatchSize, remaining);

            delta?.ReadBatch(ref reader, remaining, batch, default, in scratch);
            primary.ReadBatch(
                ref reader, remaining, batch, primaryOut.Slice(done, batch), in scratch);
            secondary?.ReadBatch(
                ref reader, remaining, batch, secondaryOut.Slice(done, batch), in scratch);

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

        // In place, into the primary: nothing reads it again, and a third array per page would be
        // the allocation the other two already avoid.
        Span<ulong> joined = primary;
        for (int i = 0; i < primary.Length; i++)
        {
            // Both operands come off the wire, so both operations wrap, exactly as upstream's do.
            joined[i] = unchecked((primary[i] * chunk.ModeBase) + secondary[i]);
        }

        return joined;
    }
}

/// <summary>The three per-batch working buffers of a page decode, owned by the caller.</summary>
/// <remarks>
/// <para>
/// These are written at the top of a batch and consumed before it ends, so nothing in them
/// survives the call; holding them per latent state instead would allocate a few kilobytes on
/// every page of every chunk.
/// </para>
/// <para>
/// They must be owned by whoever owns the decode rather than held in a <c>[ThreadStatic]</c>: a
/// thread-static is only correct while no <c>await</c> can separate taking the buffer from
/// finishing with it, and this decode is reached from an async batch enumerable, so that would
/// rest on the decode staying synchronous end to end -- something no test states and an edit
/// several layers up would not notice breaking.
/// </para>
/// </remarks>
internal readonly ref struct PcoBatchScratch
{
    /// <summary>Creates a view over three caller-owned buffers, each at least a batch long.</summary>
    /// <param name="values">The batch's latents.</param>
    /// <param name="offsetBits">Per-value offset widths.</param>
    /// <param name="offsetCumulative">Per-value offset positions.</param>
    /// <param name="states">
    /// <see cref="PcoPageDecoder.MaxLatentVars"/> reusable latent states, one per variable slot,
    /// built once for the node and reset by each page.
    /// </param>
    internal PcoBatchScratch(
        Span<ulong> values,
        Span<int> offsetBits,
        Span<long> offsetCumulative,
        PcoLatentState[] states)
    {
        Values = values;
        OffsetBits = offsetBits;
        OffsetCumulative = offsetCumulative;
        States = states;
    }

    /// <summary>The batch's latents, before they are copied out.</summary>
    internal Span<ulong> Values { get; }

    /// <summary>Each value's offset width, when the variable has more than one bin.</summary>
    internal Span<int> OffsetBits { get; }

    /// <summary>Each value's bit position within the offset stream.</summary>
    internal Span<long> OffsetCumulative { get; }

    /// <summary>The page's three latent-state slots, reset per page rather than rebuilt.</summary>
    internal PcoLatentState[] States { get; }
}

/// <summary>One latent variable's decoding state within a page.</summary>
internal sealed class PcoLatentState
{
    private PcoAnsTable _table;
    private int _ansSizeLog;
    private int _binCount;
    private int _deltaOrder;

    // Both belong to the state rather than to a batch, because both carry across the batches of
    // one page: the interleaved ANS positions are read-modify-written per value, and a delta
    // moment is updated by the untransform and read again by the next batch.
    //
    // Both are sized by the format and reused across pages. Four, because the interleaving is
    // four; seven, because the metadata writes the delta order in three bits, so it can never ask
    // for more. Only `_deltaOrder` of the moments are live, which is why every reader below slices
    // rather than walking the array.
    private readonly int[] _stateIndices = new int[4];
    private readonly ulong[] _deltaMoments = new ulong[PcoPageDecoder.MaxDeltaOrder];

    /// <summary>Creates an empty slot; a page fills it with <see cref="Reset"/>.</summary>
    internal PcoLatentState()
    {
        _table = default!;
    }

    /// <summary>Refills this slot from one variable's page metadata: delta moments and ANS states.</summary>
    /// <param name="reader">The page reader, positioned at this variable's metadata.</param>
    /// <param name="variable">The chunk-level table for this variable.</param>
    /// <param name="deltaOrder">Delta moments stored for this variable.</param>
    /// <returns>This slot, refilled, so a caller can assign it in one expression.</returns>
    internal PcoLatentState Reset(
        ref PcoBitReader reader, PcoLatentVar variable, int deltaOrder)
    {
        _table = variable.Table;
        _ansSizeLog = variable.AnsSizeLog;
        _binCount = variable.Bins.Length;
        _deltaOrder = deltaOrder;
        for (int i = 0; i < deltaOrder; i++)
        {
            _deltaMoments[i] = reader.ReadUInt(64);
        }

        PcoLatentState state = this;

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
    /// <param name="scratchBuffers">The decode's working buffers; see <see cref="PcoBatchScratch"/>.</param>
    internal void ReadBatch(
        ref PcoBitReader reader, int remaining, int batch, Span<ulong> destination,
        in PcoBatchScratch scratchBuffers)
    {
        // The values that come from the delta moments are not on the wire, so the symbol pass is
        // shorter than the batch by the delta order - but only at the end of the page.
        Span<ulong> scratch = scratchBuffers.Values;
        int preDelta = Math.Min(batch, Math.Max(0, remaining - _deltaOrder));
        ReadPreDelta(ref reader, preDelta, in scratchBuffers);

        if (_deltaOrder > 0)
        {
            UndoConsecutiveDelta(batch, in scratchBuffers);
        }

        if (!destination.IsEmpty)
        {
            scratch[..batch].CopyTo(destination);
        }
    }

    private void ReadPreDelta(ref PcoBitReader reader, int count, in PcoBatchScratch scratchBuffers)
    {
        if (count == 0)
        {
            return;
        }

        Span<ulong> scratch = scratchBuffers.Values;
        Span<int> offsetBits = scratchBuffers.OffsetBits;
        Span<long> offsetCumulative = scratchBuffers.OffsetCumulative;
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
            // A single bin means every value is that bin, and the ANS stream is skipped entirely
            // rather than read as zero-width symbols. Every value then has the same width, so the
            // per-value width and cumulative-position arrays describe nothing -- the position of
            // value i is `base + i * offsetBits`, and the fill is one vectorized store.
            int uniformBits = _table.Nodes[0].OffsetBits;
            ulong lower = _table.StateLowers[0];
            scratch[..count].Fill(lower);

            if (uniformBits == 0)
            {
                // A bin that needs no offset bits reads nothing at all: the whole batch is the
                // bin's lower bound and the reader does not move. That is what a delta-encoded
                // arithmetic ramp becomes, which is the commonest shape a pco column has, so it
                // is worth leaving before the per-value bookkeeping starts.
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
    /// Each order is one prefix sum that writes the running moment into a slot before reading what
    /// was there, so the first values of a page come from the moments themselves. The moments are
    /// carried across batches, which is why they live on this object rather than on the batch.
    /// </remarks>
    private void UndoConsecutiveDelta(int batch, in PcoBatchScratch scratchBuffers)
    {
        // The 2^63 bias is pointwise and contiguous, so it is one vector add per lane group.
        Span<ulong> values = scratchBuffers.Values[..batch];
        int biased = 0;
        if (Vector.IsHardwareAccelerated && batch >= Vector<ulong>.Count)
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

        for (int order = _deltaOrder - 1; order >= 0; order--)
        {
            ulong moment = _deltaMoments[order];
            for (int i = 0; i < batch; i++)
            {
                ulong previous = values[i];
                values[i] = moment;
                moment = unchecked(moment + previous);
            }

            _deltaMoments[order] = moment;
        }
    }
}
