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

    /// <summary>Decodes a page of 64-bit latents, joins them and writes them shifted by <paramref name="shift"/>.</summary>
    /// <param name="chunk">The chunk's metadata.</param>
    /// <param name="page">The page's bytes.</param>
    /// <param name="valueCount">Values in this page.</param>
    /// <param name="secondaryBatch">
    /// At least <see cref="BatchSize"/> slots, caller-owned, for a batch of the secondary latent
    /// variable; unread when the chunk has none.
    /// </param>
    /// <param name="scratch">
    /// A batch's working buffers, caller-owned so that every page of a node shares one rental.
    /// </param>
    /// <param name="target">
    /// Exactly <paramref name="valueCount"/> slots: the primary latents are decoded into them and
    /// joined there, a batch at a time.
    /// </param>
    /// <param name="shift">
    /// Added to every joined latent: 2^63 turns a signed type's ordered form back into two's
    /// complement, zero leaves an unsigned type's as it is.
    /// </param>
    /// <remarks>
    /// A batch of a latent variable is not always written: one that is a single value, or that
    /// value's prefix sum -- a ramp -- is described instead (<see cref="PcoBatchShape"/>), and the
    /// join composes the descriptions. A delta-encoded arithmetic ramp, the commonest shape a pco
    /// column has, is a ramp primary joined to a constant secondary, which is a ramp again: the
    /// batch is then written once, a vector at a time, and nothing else is.
    /// </remarks>
    internal static void DecodeJoined(
        PcoChunkMeta chunk, ReadOnlySpan<byte> page, int valueCount, Span<ulong> secondaryBatch,
        in PcoBatchScratch scratch, Span<ulong> target, ulong shift)
    {
        bool classic = chunk.Mode == PcoModeKind.Classic;
        if (!classic && chunk.Mode != PcoModeKind.IntMult)
        {
            CompressedThrow.Format($"pco mode {chunk.Mode} is not decoded yet.");
        }

        if (!classic && chunk.Secondary is null)
        {
            CompressedThrow.Format("A pco IntMult chunk has no secondary latent variable.");
        }

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

        target = target[..valueCount];
        ulong modeBase = chunk.ModeBase;
        int done = 0;
        while (done < valueCount)
        {
            int remaining = valueCount - done;
            int batch = Math.Min(BatchSize, remaining);
            Span<ulong> values = target.Slice(done, batch);

            delta?.ReadBatch(ref reader, remaining, batch, default, in scratch, out _, out _);
            PcoBatchShape primaryShape = primary.ReadBatch(
                ref reader, remaining, batch, values, in scratch, out ulong primaryFirst, out ulong primaryStep);

            // Read whatever the mode, so the reader moves past it; a classic chunk has none.
            Span<ulong> second = secondary is null ? default : secondaryBatch[..batch];
            PcoBatchShape secondaryShape = PcoBatchShape.Written;
            ulong secondaryFirst = 0;
            if (secondary is not null)
            {
                secondaryShape = secondary.ReadBatch(
                    ref reader, remaining, batch, second, in scratch, out secondaryFirst, out ulong secondaryStep);
                if (secondaryShape == PcoBatchShape.Ramp)
                {
                    Ramp(second, secondaryFirst, secondaryStep);
                    secondaryShape = PcoBatchShape.Written;
                }
            }

            if (classic)
            {
                Shift(values, primaryShape, primaryFirst, primaryStep, shift);
            }
            else
            {
                Join(values, primaryShape, primaryFirst, primaryStep, second, secondaryShape, secondaryFirst, modeBase, shift);
            }

            done += batch;
        }
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

    /// <summary>
    /// A classic batch: the primary, as its shape describes it, plus <paramref name="shift"/>, into
    /// <paramref name="values"/>, which hold the primary when it is written.
    /// </summary>
    internal static void Shift(Span<ulong> values, PcoBatchShape shape, ulong first, ulong step, ulong shift)
    {
        switch (shape)
        {
            case PcoBatchShape.Constant:
                values.Fill(unchecked(first + shift));
                break;
            case PcoBatchShape.Ramp:
                Ramp(values, unchecked(first + shift), step);
                break;
            default:
                Add(values, shift);
                break;
        }
    }

    /// <summary>
    /// An IntMult batch: primary times the base plus secondary, plus <paramref name="shift"/>, into
    /// <paramref name="values"/>, which hold the primary when it is written. The secondary is
    /// written or a constant.
    /// </summary>
    /// <remarks>
    /// Every operand comes off the wire, so every operation wraps, exactly as upstream's do; a ramp
    /// times the base is then the ramp of the first value and the step, each times the base.
    /// </remarks>
    internal static void Join(
        Span<ulong> values, PcoBatchShape primary, ulong primaryFirst, ulong primaryStep,
        ReadOnlySpan<ulong> secondary, PcoBatchShape secondaryShape, ulong secondaryValue, ulong modeBase, ulong shift)
    {
        unchecked
        {
            if (secondaryShape == PcoBatchShape.Constant)
            {
                ulong added = secondaryValue + shift;
                switch (primary)
                {
                    case PcoBatchShape.Constant:
                        values.Fill((primaryFirst * modeBase) + added);
                        break;
                    case PcoBatchShape.Ramp:
                        Ramp(values, (primaryFirst * modeBase) + added, primaryStep * modeBase);
                        break;
                    default:
                        for (int i = 0; i < values.Length; i++)
                        {
                            values[i] = (values[i] * modeBase) + added;
                        }

                        break;
                }

                return;
            }

            secondary = secondary[..values.Length];
            switch (primary)
            {
                case PcoBatchShape.Constant:
                    values.Fill((primaryFirst * modeBase) + shift);
                    Add(values, secondary);
                    break;
                case PcoBatchShape.Ramp:
                    Ramp(values, (primaryFirst * modeBase) + shift, primaryStep * modeBase);
                    Add(values, secondary);
                    break;
                default:
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i] = (values[i] * modeBase) + secondary[i] + shift;
                    }

                    break;
            }
        }
    }

    /// <summary>Writes <paramref name="start"/> plus i <paramref name="step"/>s into slot i, wrapping.</summary>
    internal static void Ramp(Span<ulong> values, ulong start, ulong step)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<ulong>.Count)
        {
            int lanes = Vector<ulong>.Count;
            Vector<ulong> ramp = (Vector<ulong>.Indices * step) + new Vector<ulong>(start);
            Vector<ulong> stride = new Vector<ulong>(unchecked((ulong)lanes * step));
            for (; i <= values.Length - lanes; i += lanes)
            {
                ramp.StoreUnsafe(ref values[i]);
                ramp += stride;
            }
        }

        for (; i < values.Length; i++)
        {
            values[i] = unchecked(start + ((ulong)i * step));
        }
    }

    /// <summary>Adds <paramref name="by"/> to every value, one vector add per lane group.</summary>
    private static void Add(Span<ulong> values, ulong by)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<ulong>.Count)
        {
            Vector<ulong> added = new Vector<ulong>(by);
            int lanes = Vector<ulong>.Count;
            for (; i <= values.Length - lanes; i += lanes)
            {
                (Vector.LoadUnsafe(in values[i]) + added).StoreUnsafe(ref values[i]);
            }
        }

        for (; i < values.Length; i++)
        {
            values[i] = unchecked(values[i] + by);
        }
    }

    /// <summary>Adds <paramref name="other"/> to <paramref name="values"/> slot by slot, a vector at a time.</summary>
    private static void Add(Span<ulong> values, ReadOnlySpan<ulong> other)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<ulong>.Count)
        {
            int lanes = Vector<ulong>.Count;
            for (; i <= values.Length - lanes; i += lanes)
            {
                (Vector.LoadUnsafe(in values[i]) + Vector.LoadUnsafe(in other[i])).StoreUnsafe(ref values[i]);
            }
        }

        for (; i < values.Length; i++)
        {
            values[i] = unchecked(values[i] + other[i]);
        }
    }
}

/// <summary>What a batch of a latent variable is, when it is not written out.</summary>
internal enum PcoBatchShape : byte
{
    /// <summary>Written, value by value.</summary>
    Written,

    /// <summary>Every value is one value, not written.</summary>
    Constant,

    /// <summary>Value i is a first value plus i steps, wrapping; not written.</summary>
    Ramp,
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

    /// <summary>Decodes one batch into <paramref name="destination"/>, or describes it.</summary>
    /// <param name="reader">The page reader.</param>
    /// <param name="remaining">Values left in the page before this batch.</param>
    /// <param name="batch">Values this batch produces.</param>
    /// <param name="destination">Where to put them; empty for a delta variable.</param>
    /// <param name="scratchBuffers">The decode's working buffers; see <see cref="PcoBatchScratch"/>.</param>
    /// <param name="first">The one value, or the ramp's first, when the batch is not written.</param>
    /// <param name="step">The ramp's step, when the batch is a ramp.</param>
    /// <returns>
    /// How the batch is given: written into <paramref name="destination"/> (the batch buffer when
    /// that is empty), or, not written, as one value <paramref name="first"/>, or as the ramp from
    /// <paramref name="first"/> by <paramref name="step"/> that a first-order delta makes of one.
    /// </returns>
    internal PcoBatchShape ReadBatch(
        ref PcoBitReader reader, int remaining, int batch, Span<ulong> destination,
        in PcoBatchScratch scratchBuffers, out ulong first, out ulong step)
    {
        // A variable whose values are kept is decoded straight into them; the batch buffer takes
        // the values of one nothing keeps.
        Span<ulong> values = destination.IsEmpty ? scratchBuffers.Values[..batch] : destination[..batch];

        // The values that come from the delta moments are not on the wire, so the symbol pass is
        // shorter than the batch by the delta order - but only at the end of the page.
        int preDelta = Math.Min(batch, Math.Max(0, remaining - _deltaOrder));
        bool constant = ReadPreDelta(ref reader, preDelta, values, in scratchBuffers, out ulong lower);
        first = lower;
        step = 0;

        if (_deltaOrder == 0)
        {
            return constant ? PcoBatchShape.Constant : PcoBatchShape.Written;
        }

        if (constant && _deltaOrder == 1)
        {
            // The prefix sum of one value, biased, from the moment: see `UndoConsecutiveDelta`.
            step = unchecked(lower + (1UL << 63));
            first = _deltaMoments[0];
            _deltaMoments[0] = unchecked(first + ((ulong)batch * step));
            return PcoBatchShape.Ramp;
        }

        UndoConsecutiveDelta(values, constant, lower);
        return PcoBatchShape.Written;
    }

    /// <summary>
    /// Reads <paramref name="count"/> values before their delta is undone; true, with nothing
    /// written, when every one of them is <paramref name="lower"/>.
    /// </summary>
    private bool ReadPreDelta(
        ref PcoBitReader reader, int count, Span<ulong> scratch, in PcoBatchScratch scratchBuffers, out ulong lower)
    {
        lower = 0;
        if (count == 0)
        {
            return false;
        }

        Span<int> offsetBits = scratchBuffers.OffsetBits;
        Span<long> offsetCumulative = scratchBuffers.OffsetCumulative;
        long offsetBitTotal = 0;
        if (_binCount > 1)
        {
            ReadOnlySpan<PcoAnsNode> nodes = _table.Nodes;
            ReadOnlySpan<ulong> lowers = _table.StateLowers;
            for (int i = 0; i < count; i++)
            {
                int slot = _stateIndices[i % 4];
                PcoAnsNode node = nodes[slot];
                ulong ansValue = reader.ReadUInt(node.BitsToRead);

                scratch[i] = lowers[slot];
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
            // value i is `base + i * offsetBits`.
            int uniformBits = _table.Nodes[0].OffsetBits;
            lower = _table.StateLowers[0];

            if (uniformBits == 0)
            {
                // A bin that needs no offset bits reads nothing at all: the whole batch is the
                // bin's lower bound and the reader does not move. That is what a delta-encoded
                // arithmetic ramp becomes, which is the commonest shape a pco column has, so the
                // values are not even written: the bound describes them.
                return true;
            }

            long uniformBase = reader.BitPosition;
            for (int i = 0; i < count; i++)
            {
                scratch[i] = unchecked(
                    lower + reader.ReadAt(uniformBase + ((long)i * uniformBits), uniformBits));
            }

            reader.SeekBits(uniformBase + ((long)count * uniformBits));
            return false;
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
        return false;
    }

    /// <summary>Undoes a consecutive delta over the batch, consuming the page's moments.</summary>
    /// <param name="values">The batch, read before the delta; the values, after.</param>
    /// <param name="constant">
    /// Whether every value read is <paramref name="lower"/>, in which case none was written.
    /// </param>
    /// <param name="lower">That value.</param>
    /// <remarks>
    /// <para>
    /// Each order is one prefix sum that writes the running moment into a slot before reading what
    /// was there, so the first values of a page come from the moments themselves. The moments are
    /// carried across batches, which is why they live on this object rather than on the batch.
    /// </para>
    /// <para>
    /// A prefix sum is a chain, a dependent add a value. Over a constant it is a ramp, the moment
    /// plus i steps, which has no chain and is written a vector at a time: so the highest order is
    /// undone in closed form when the batch read was constant, which a delta-encoded arithmetic
    /// ramp's always is (a first-order one is not even written: <see cref="ReadBatch"/> describes
    /// it). The values past the wire at a page's end are then that constant too rather than
    /// whatever the buffer held; either way they reach only the moment, which the page ends with.
    /// </para>
    /// </remarks>
    private void UndoConsecutiveDelta(Span<ulong> values, bool constant, ulong lower)
    {
        int order = _deltaOrder - 1;
        if (constant)
        {
            // The 2^63 bias of the ordered form, on the one value.
            ulong step = unchecked(lower + (1UL << 63));
            ulong moment = _deltaMoments[order];
            PcoPageDecoder.Ramp(values, moment, step);
            _deltaMoments[order] = unchecked(moment + ((ulong)values.Length * step));
            order--;
        }
        else
        {
            // The 2^63 bias is pointwise and contiguous, so it is one vector add per lane group.
            int biased = 0;
            if (Vector.IsHardwareAccelerated && values.Length >= Vector<ulong>.Count)
            {
                Vector<ulong> bias = new Vector<ulong>(1UL << 63);
                int lanes = Vector<ulong>.Count;
                for (; biased <= values.Length - lanes; biased += lanes)
                {
                    (Vector.LoadUnsafe(in values[biased]) + bias).StoreUnsafe(ref values[biased]);
                }
            }

            for (; biased < values.Length; biased++)
            {
                values[biased] = unchecked(values[biased] + (1UL << 63));
            }
        }

        for (; order >= 0; order--)
        {
            ulong moment = _deltaMoments[order];
            for (int i = 0; i < values.Length; i++)
            {
                ulong previous = values[i];
                values[i] = moment;
                moment = unchecked(moment + previous);
            }

            _deltaMoments[order] = moment;
        }
    }
}
