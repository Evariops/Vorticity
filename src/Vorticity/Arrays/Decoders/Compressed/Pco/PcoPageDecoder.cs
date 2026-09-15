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
    /// <summary>Latent variables a page can have: the delta, the primary and the secondary.</summary>
    internal const int MaxLatentVars = 3;

    /// <summary>Largest delta order the metadata can express: it is written in three bits.</summary>
    internal const int MaxDeltaOrder = 7;

    /// <summary>Values per batch. Also the size of the buffers in <see cref="PcoBatchScratch"/>.</summary>
    internal const int BatchSize = 256;

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
    /// <param name="scratch">
    /// A batch's working buffers, caller-owned so that a thousand pages rent them once.
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

        // THE THREE STATES ARE THE CALLER'S AND ARE RESET, NOT BUILT. PERF-AUDIT-v2.md R13: each one
        // used to be a fresh object with an `int[4]` and a `ulong[deltaOrder]` of its own, and a
        // page has up to three -- on a million-row column that was **1 954 states and 226 664
        // bytes, 59 % of everything the scan allocated on the managed heap**. A page needs at most
        // three and never keeps them, so `PcoDecoder` builds three per NODE and every page resets
        // them in place.
        //
        // Reset rather than a ref struct over borrowed spans, which was tried first: `Read` returns
        // the state, so spans it captured could outlive their frame and the compiler says so
        // (CS8352). Three objects per node is the same saving without arguing with ref-safety.
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

/// <summary>The three per-batch working buffers of a page decode, owned by the caller.</summary>
/// <remarks>
/// <para>
/// These are written at the top of a batch and consumed before it ends -- nothing in them
/// survives the call -- but they were instance fields of <see cref="PcoLatentState"/>, so a page
/// carrying one latent variable allocated 5 kB of them and a chunk of a thousand pages dropped
/// that a thousand times, against contract 1.3's "no managed allocation on a decode path".
/// </para>
/// <para>
/// THEY WERE THREAD-STATIC FOR ONE COMMIT AND THAT WAS THE WRONG ANSWER. A <c>[ThreadStatic]</c>
/// is only correct while no <c>await</c> separates taking the buffer from finishing with it, and
/// this decode is reached from <c>BatchAsyncEnumerable</c>: the property holds today because the
/// decode happens to be synchronous throughout, which is not a property any test states and not
/// one an edit three layers up would notice breaking. The buffers belong to whoever owns the
/// decode, and <c>PcoDecoder</c> already rents the two page buffers exactly that way.
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
    /// built once for the node and reset by each page (R13).
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

    /// <summary>The page's three latent-state slots, reset per page rather than rebuilt (R13).</summary>
    internal PcoLatentState[] States { get; }
}

/// <summary>One latent variable's decoding state within a page.</summary>
internal sealed class PcoLatentState
{
    private PcoAnsTable _table;
    private int _ansSizeLog;
    private int _binCount;
    private int _deltaOrder;

    // PER STATE, because both carry across the batches of one page: the four interleaved ANS
    // positions are read-modify-written per value, and a delta moment is updated by the untransform
    // and read again by the next batch.
    //
    // SIZED BY THE FORMAT AND REUSED ACROSS PAGES (R13). Four, because the interleaving is four;
    // seven, because the metadata writes the delta order in three bits
    // (`PcoChunkMeta.DeltaOrderBits`), so it can never ask for more. Only `_deltaOrder` of the
    // moments are live, which is why every reader below slices rather than walking the array.
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
            // A single bin means every value is that bin: upstream skips the ANS stream entirely
            // rather than reading zero-width symbols. Every value then has the SAME width, so the
            // per-value width and cumulative-position arrays describe nothing -- the position of
            // value i is `base + i * offsetBits`, and the fill is one vectorized store.
            int uniformBits = _table.Nodes[0].OffsetBits;
            ulong lower = _table.StateLowers[0];
            scratch[..count].Fill(lower);

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
