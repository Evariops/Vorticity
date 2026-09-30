using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Vorticity.Types;
using Vorticity.Compute;

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
/// <para>
/// Latents are held as <c>ulong</c> whatever their width. Every operation on them wraps, as
/// upstream's do, so arithmetic modulo 2^64 leaves the low bits exactly what arithmetic at the
/// latent's own width would; the bits above only have to be cleared where a latent is compared,
/// indexed or shifted right, and the numbers are truncated to their width as they are stored.
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

    /// <summary>Decodes a page, joins its latents into the chunk's numbers and writes them into <paramref name="target"/>.</summary>
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
    /// Exactly <paramref name="valueCount"/> numbers of the chunk's width, little-endian. A page of
    /// 64-bit numbers is decoded straight into them and joined there, a batch at a time; a
    /// narrower one is joined in the scratch's primary buffer and truncated into them.
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
        in PcoBatchScratch scratch, Span<byte> target)
    {
        PcoNumber number = chunk.Number;
        PcoBitReader reader = new PcoBitReader(page);

        // The latent states are the caller's and are reset, not built. Each one owns a few small
        // arrays, a page needs at most three of them and never keeps them, so `PcoDecoder` builds
        // them once per node and every page refills them in place rather than leaving a page's
        // worth of objects on the managed heap.
        //
        // Reset rather than a ref struct over borrowed spans: this method returns the state, so
        // spans it captured could outlive their frame and ref-safety refuses it. Objects reused
        // per node are the same saving without that argument.
        //
        // A lookback's states rent their windows as they reset, so the resets are inside the try
        // whose finally gives the windows back, a reset that fails halfway included.
        try
        {
            PcoDeltaKind secondaryDelta = chunk.SecondaryStateCount > 0 ? chunk.Delta : PcoDeltaKind.NoOp;
            PcoLatentState? delta = chunk.DeltaLatent is { } deltaVar
                ? scratch.States[0].Reset(ref reader, deltaVar, chunk, PcoDeltaKind.NoOp, 0, chunk.PrimaryStateCount)
                : null;
            PcoLatentState primary = scratch.States[1].Reset(
                ref reader, chunk.Primary, chunk, chunk.Delta, chunk.PrimaryStateCount, chunk.PrimaryStateCount);
            PcoLatentState? secondary = chunk.Secondary is { } secondaryVar
                ? scratch.States[2].Reset(
                    ref reader, secondaryVar, chunk, secondaryDelta, chunk.SecondaryStateCount, chunk.SecondaryStateCount)
                : null;

            reader.DrainEmptyByte("page metadata");

            bool wide = number.LatentBits == 64;
            int width = number.LatentBits / 8;
            Span<ulong> wideTarget = wide ? MemoryMarshal.Cast<byte, ulong>(target)[..valueCount] : default;
            int done = 0;
            while (done < valueCount)
            {
                int remaining = valueCount - done;
                int batch = Math.Min(BatchSize, remaining);
                Span<ulong> values = wide ? wideTarget.Slice(done, batch) : scratch.Primary[..batch];

                // A lookback's own latents come first, and are what the others look back by.
                ReadOnlySpan<ulong> lookbacks = delta is null ? default : delta.ReadLookbacks(ref reader, remaining, batch, in scratch);
                PcoBatchShape primaryShape = primary.ReadBatch(
                    ref reader, remaining, batch, values, lookbacks, in scratch, out ulong primaryFirst, out ulong primaryStep);

                // Read whatever the mode, so the reader moves past it; a classic chunk has none.
                Span<ulong> second = secondary is null ? default : secondaryBatch[..batch];
                PcoBatchShape secondaryShape = PcoBatchShape.Written;
                ulong secondaryFirst = 0;
                if (secondary is not null)
                {
                    secondaryShape = secondary.ReadBatch(
                        ref reader, remaining, batch, second, lookbacks, in scratch, out secondaryFirst, out ulong secondaryStep);
                    if (secondaryShape == PcoBatchShape.Ramp)
                    {
                        Ramp(second, secondaryFirst, secondaryStep);
                        secondaryShape = PcoBatchShape.Written;
                    }
                }

                JoinBatch(chunk, values, primaryShape, primaryFirst, primaryStep, second, secondaryShape, secondaryFirst);
                if (!wide)
                {
                    // The joined numbers' bits are the low bits of each slot.
                    IntegerNarrowing.Truncate<long>(
                        MemoryMarshal.Cast<ulong, long>(values), width == 4 ? PType.I32 : PType.I16,
                        target.Slice(done * width, batch * width));
                }

                done += batch;
            }

            reader.DrainEmptyByte("page");
        }
        finally
        {
            scratch.States[1].EndPage();
            scratch.States[2].EndPage();
        }
    }

    /// <summary>One batch's latents joined into numbers, in place, as the chunk's mode says.</summary>
    private static void JoinBatch(
        PcoChunkMeta chunk, Span<ulong> values, PcoBatchShape primaryShape, ulong primaryFirst, ulong primaryStep,
        Span<ulong> second, PcoBatchShape secondaryShape, ulong secondaryFirst)
    {
        PcoNumber number = chunk.Number;
        switch (chunk.Mode)
        {
            case PcoModeKind.Classic when number.Kind != PcoNumberKind.Float:
                // The ordered form is shifted so the type's minimum is zero. For an unsigned type it
                // is the value itself, and for a signed one the shift back is a single addition of
                // the midpoint rather than a sign test.
                Shift(values, primaryShape, primaryFirst, primaryStep, number.Kind == PcoNumberKind.Signed ? number.Mid : 0);
                break;
            case PcoModeKind.Classic:
                Materialize(values, primaryShape, primaryFirst, primaryStep);
                FloatsFromLatents(values, number);
                break;
            case PcoModeKind.IntMult:
                Join(
                    values, primaryShape, primaryFirst, primaryStep, second, secondaryShape, secondaryFirst,
                    chunk.ModeBase, number.Kind == PcoNumberKind.Signed ? number.Mid : 0);
                break;
            case PcoModeKind.FloatMult:
                Materialize(values, primaryShape, primaryFirst, primaryStep);
                Materialize(second, secondaryShape, secondaryFirst, 0);
                FloatMult(values, second, chunk);
                break;
            case PcoModeKind.FloatQuant:
                Materialize(values, primaryShape, primaryFirst, primaryStep);
                Materialize(second, secondaryShape, secondaryFirst, 0);
                FloatQuant(values, second, number, (int)chunk.ModeBase);
                break;
            default:
                Materialize(values, primaryShape, primaryFirst, primaryStep);
                Lookup(values, chunk.Dictionary);
                break;
        }
    }

    /// <summary>A described batch written out: its one value, or its ramp.</summary>
    private static void Materialize(Span<ulong> values, PcoBatchShape shape, ulong first, ulong step)
    {
        if (shape == PcoBatchShape.Constant)
        {
            values.Fill(first);
        }
        else if (shape == PcoBatchShape.Ramp)
        {
            Ramp(values, first, step);
        }
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
    /// Each latent as the float it orders, <c>from_latent_ordered</c>: a latent with the top bit set
    /// is a positive float with that bit cleared, one without it a negative float with every bit
    /// flipped -- one exclusive or either way, of the sign bit or of every bit, as the top bit says.
    /// </summary>
    private static void FloatsFromLatents(Span<ulong> values, PcoNumber number)
    {
        int top = number.LatentBits - 1;
        ulong sign = number.Mid;
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<ulong>.Count)
        {
            int lanes = Vector<ulong>.Count;
            Vector<ulong> signs = new Vector<ulong>(sign);
            for (; i <= values.Length - lanes; i += lanes)
            {
                Vector<ulong> latent = Vector.LoadUnsafe(in values[i]);
                Vector<ulong> flip = signs | ((Vector.ShiftRightLogical(latent, top) & Vector<ulong>.One) - Vector<ulong>.One);
                (latent ^ flip).StoreUnsafe(ref values[i]);
            }
        }

        for (; i < values.Length; i++)
        {
            ulong latent = values[i];
            values[i] = latent ^ (sign | unchecked(((latent >> top) & 1) - 1));
        }
    }

    /// <summary>
    /// A FloatMult batch, <c>float_mult::join_latents</c>: the primary's integer, as a float, times
    /// the base, its ordered latent moved by the secondary's adjustment and recentred.
    /// </summary>
    /// <remarks>
    /// The product is taken in the numbers' own type, so each width has its loop: it is what the
    /// encoder took, and the adjustment is what makes it exact.
    /// </remarks>
    private static void FloatMult(Span<ulong> values, ReadOnlySpan<ulong> adjustments, PcoChunkMeta chunk)
    {
        PcoNumber number = chunk.Number;
        ulong mask = number.Mask;
        ulong mid = number.Mid;
        ulong baseBits = number.FromLatentOrdered(chunk.ModeBase);
        adjustments = adjustments[..values.Length];
        switch (number.LatentBits)
        {
            case 64:
            {
                double factor = BitConverter.UInt64BitsToDouble(baseBits);
                for (int i = 0; i < values.Length; i++)
                {
                    double unadjusted = BitConverter.UInt64BitsToDouble(IntFloat(values[i], number)) * factor;
                    ulong latent = unchecked(number.FloatToLatentOrdered(BitConverter.DoubleToUInt64Bits(unadjusted)) + adjustments[i] + mid);
                    values[i] = number.FromLatentOrdered(latent);
                }

                break;
            }

            case 32:
            {
                float factor = BitConverter.UInt32BitsToSingle((uint)baseBits);
                for (int i = 0; i < values.Length; i++)
                {
                    float unadjusted = BitConverter.UInt32BitsToSingle((uint)IntFloat(values[i] & mask, number)) * factor;
                    ulong latent = unchecked(number.FloatToLatentOrdered(BitConverter.SingleToUInt32Bits(unadjusted)) + adjustments[i] + mid);
                    values[i] = number.FromLatentOrdered(latent);
                }

                break;
            }

            default:
            {
                Half factor = BitConverter.UInt16BitsToHalf((ushort)baseBits);
                for (int i = 0; i < values.Length; i++)
                {
                    Half unadjusted = BitConverter.UInt16BitsToHalf((ushort)IntFloat(values[i] & mask, number)) * factor;
                    ulong latent = unchecked(number.FloatToLatentOrdered(BitConverter.HalfToUInt16Bits(unadjusted)) + adjustments[i] + mid);
                    values[i] = number.FromLatentOrdered(latent);
                }

                break;
            }
        }
    }

    /// <summary>
    /// The float an integer latent stands for, <c>int_float_from_latent</c>, as its bits: the
    /// latent's distance from the midpoint as a float, exactly below 2^(mantissa + 1) and counted in
    /// representable steps above it, negative below the midpoint.
    /// </summary>
    /// <param name="latent">The latent, no wider than the number.</param>
    /// <param name="number">The float type.</param>
    internal static ulong IntFloat(ulong latent, PcoNumber number)
    {
        unchecked
        {
            ulong mid = number.Mid;
            bool negative = latent < mid;
            ulong magnitude = negative ? mid - 1 - latent : latent - mid;
            int digits = number.PrecisionBits + 1;
            ulong exact = 1UL << digits;
            ulong bits;
            if (magnitude < exact)
            {
                bits = number.LatentBits switch
                {
                    64 => BitConverter.DoubleToUInt64Bits(magnitude),
                    32 => BitConverter.SingleToUInt32Bits(magnitude),
                    _ => BitConverter.HalfToUInt16Bits((Half)(float)magnitude),
                };
            }
            else
            {
                // The float 2^digits, then one step of its precision per unit past it: its biased
                // exponent is the bias plus the digits, which is the midpoint's bits minus one past.
                ulong bias = (mid >> (number.PrecisionBits + 1)) - 1;
                ulong exactBits = (bias + (ulong)digits) << number.PrecisionBits;
                bits = (exactBits + (magnitude - exact)) & number.Mask;
            }

            return negative ? bits ^ mid : bits;
        }
    }

    /// <summary>
    /// A FloatQuant batch, <c>float_quant::join_latents</c>: the primary is the ordered latent's top
    /// bits, the secondary its lowest <paramref name="k"/>, flipped for a negative float so that an
    /// exactly quantized one has a secondary of zero either way.
    /// </summary>
    private static void FloatQuant(Span<ulong> values, ReadOnlySpan<ulong> lowest, PcoNumber number, int k)
    {
        ulong mask = number.Mask;
        ulong signCutoff = number.Mid >> k;
        ulong lowestMax = (1UL << k) - 1;
        lowest = lowest[..values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            ulong y = values[i] & mask;
            ulong m = lowest[i];
            ulong low = y >= signCutoff ? m : unchecked(lowestMax - m);
            values[i] = number.FromLatentOrdered(unchecked((y << k) + low));
        }
    }

    /// <summary>A Dict batch: each index replaced by the entry it names, which is already a number.</summary>
    private static void Lookup(Span<ulong> values, ReadOnlySpan<ulong> dictionary)
    {
        for (int i = 0; i < values.Length; i++)
        {
            // The indices are u32 latents: what wrapped past 32 bits wraps here as it does upstream.
            ulong index = values[i] & uint.MaxValue;
            if (index >= (ulong)dictionary.Length)
            {
                CompressedThrow.Format(
                    $"A pco dictionary index {index} is past the dictionary's {dictionary.Length} entries.");
            }

            values[i] = dictionary[(int)index];
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
                        for (int i = Multiplied(values, default, modeBase, added); i < values.Length; i++)
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
                    for (int i = Multiplied(values, secondary, modeBase, shift); i < values.Length; i++)
                    {
                        values[i] = (values[i] * modeBase) + secondary[i] + shift;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// <c>values[i] * modeBase + secondary[i] + added</c>, eight values a step, where 512-bit vectors
    /// multiply 64-bit lanes in one instruction; <paramref name="secondary"/> empty adds nothing.
    /// </summary>
    /// <returns>The values done, a multiple of eight; the rest are the caller's.</returns>
    /// <remarks>
    /// AVX-512DQ's <c>vpmullq</c> is the multiply the loop needs, and wraps as the scalar one does.
    /// NEON has no 64-bit vector multiply, which is why the loop was scalar, and it stays so there.
    /// </remarks>
    private static int Multiplied(Span<ulong> values, ReadOnlySpan<ulong> secondary, ulong modeBase, ulong added)
    {
        if (!Vector512.IsHardwareAccelerated || !Avx512DQ.IsSupported)
        {
            return 0;
        }

        ref ulong at = ref MemoryMarshal.GetReference(values);
        ref ulong other = ref MemoryMarshal.GetReference(secondary);
        bool paired = !secondary.IsEmpty;
        Vector512<ulong> factor = Vector512.Create(modeBase);
        Vector512<ulong> plus = Vector512.Create(added);
        int i = 0;
        for (; i <= values.Length - 8; i += 8)
        {
            Vector512<ulong> joined = (Vector512.LoadUnsafe(ref at, (nuint)i) * factor) + plus;
            if (paired)
            {
                joined += Vector512.LoadUnsafe(ref other, (nuint)i);
            }

            joined.StoreUnsafe(ref at, (nuint)i);
        }

        return i;
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
    internal static void Add(Span<ulong> values, ulong by)
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

/// <summary>The per-batch working buffers of a page decode, owned by the caller.</summary>
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
    /// <summary>Creates a view over caller-owned buffers, each at least a batch long.</summary>
    /// <param name="values">The batch's latents of a variable nothing keeps: a lookback's.</param>
    /// <param name="offsetBits">Per-value offset widths.</param>
    /// <param name="offsetCumulative">Per-value offset positions.</param>
    /// <param name="states">
    /// <see cref="PcoPageDecoder.MaxLatentVars"/> reusable latent states, one per variable slot,
    /// built once for the node and reset by each page.
    /// </param>
    /// <param name="primary">
    /// The batch's primary latents, joined there, for numbers narrower than 64 bits; empty for
    /// 64-bit numbers, which are decoded straight into the output.
    /// </param>
    internal PcoBatchScratch(
        Span<ulong> values,
        Span<int> offsetBits,
        Span<long> offsetCumulative,
        PcoLatentState[] states,
        Span<ulong> primary = default)
    {
        Values = values;
        OffsetBits = offsetBits;
        OffsetCumulative = offsetCumulative;
        States = states;
        Primary = primary;
    }

    /// <summary>The batch's latents of a variable nothing keeps.</summary>
    internal Span<ulong> Values { get; }

    /// <summary>Each value's offset width, when the variable has more than one bin.</summary>
    internal Span<int> OffsetBits { get; }

    /// <summary>Each value's bit position within the offset stream.</summary>
    internal Span<long> OffsetCumulative { get; }

    /// <summary>The page's three latent-state slots, reset per page rather than rebuilt.</summary>
    internal PcoLatentState[] States { get; }

    /// <summary>The batch's primary latents, for numbers narrower than 64 bits.</summary>
    internal Span<ulong> Primary { get; }
}

/// <summary>One latent variable's decoding state within a page.</summary>
internal sealed class PcoLatentState
{
    private PcoAnsTable _table;
    private PcoChunkMeta _chunk;
    private int _binCount;
    private PcoDeltaKind _delta;
    private int _stateCount;
    private int _shortfall;
    private ulong _mask;
    private ulong _mid;

    // Both belong to the state rather than to a batch, because both carry across the batches of
    // one page: the interleaved ANS positions are read-modify-written per value, and a delta
    // moment is updated by the untransform and read again by the next batch.
    //
    // Both are sized by the format and reused across pages. Four, because the interleaving is
    // four; seven, because the metadata writes the delta order in three bits, so it can never ask
    // for more. Only `_stateCount` of the moments are live, which is why every reader below slices
    // rather than walking the array.
    private readonly int[] _stateIndices = new int[4];
    private readonly ulong[] _deltaMoments = new ulong[PcoPageDecoder.MaxDeltaOrder];

    // A convolution's latents so far: its state, the order before the batch, then the batch.
    // Built the first time a convolution is met and kept, as the moments are.
    private ulong[]? _residuals;

    // A lookback's window: the latents it can look back to, then the batch's. Rented per page, as
    // upstream allocates it per page, since it is as long as the window and a window can be long.
    private ulong[]? _window;
    private int _windowLength;
    private int _windowPosition;

    /// <summary>Creates an empty slot; a page fills it with <see cref="Reset"/>.</summary>
    internal PcoLatentState()
    {
        _table = default!;
        _chunk = default!;
    }

    /// <summary>Refills this slot from one variable's page metadata: delta state and ANS states.</summary>
    /// <param name="reader">The page reader, positioned at this variable's metadata.</param>
    /// <param name="variable">The chunk-level table for this variable.</param>
    /// <param name="chunk">The chunk, whose delta encoding this variable's is.</param>
    /// <param name="delta">The delta encoding this variable takes.</param>
    /// <param name="stateCount">Latents of delta state the page stores for this variable.</param>
    /// <param name="shortfall">
    /// How far short of a page's values this variable's wire values stop: its own state's count,
    /// or, for a lookback's own latents, the primary's.
    /// </param>
    /// <returns>This slot, refilled, so a caller can assign it in one expression.</returns>
    internal PcoLatentState Reset(
        ref PcoBitReader reader, PcoLatentVar variable, PcoChunkMeta chunk, PcoDeltaKind delta, int stateCount, int shortfall)
    {
        _table = variable.Table;
        _chunk = chunk;
        _binCount = variable.Bins.Length;
        _delta = delta;
        _stateCount = stateCount;
        _shortfall = shortfall;
        int bits = variable.LatentBits;
        _mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        _mid = 1UL << (bits - 1);
        switch (delta)
        {
            case PcoDeltaKind.Consecutive:
                for (int i = 0; i < stateCount; i++)
                {
                    _deltaMoments[i] = reader.ReadUInt(bits);
                }

                break;
            case PcoDeltaKind.Lookback:
                OpenWindow(ref reader, bits, 1 << chunk.LookbackWindowLog, stateCount);
                break;
            case PcoDeltaKind.Conv1:
                _residuals ??= new ulong[PcoChunkMeta.MaxConv1Order + PcoPageDecoder.BatchSize];
                for (int i = 0; i < stateCount; i++)
                {
                    _residuals[i] = reader.ReadUInt(bits);
                }

                break;
            default:
                break;
        }

        for (int i = 0; i < 4; i++)
        {
            _stateIndices[i] = (int)reader.ReadUInt(variable.AnsSizeLog);
        }

        return this;
    }

    /// <summary>Gives back what the page rented; the slot is then empty until the next <see cref="Reset"/>.</summary>
    internal void EndPage()
    {
        if (_window is { } window)
        {
            _window = null;
            ArrayPool<ulong>.Shared.Return(window);
        }
    }

    /// <summary>
    /// A lookback's own latents for the batch: how far back each of the primary's values looks,
    /// in the batch buffer, written out even when the batch reads as one value.
    /// </summary>
    internal ReadOnlySpan<ulong> ReadLookbacks(scoped ref PcoBitReader reader, int remaining, int batch, in PcoBatchScratch scratchBuffers)
    {
        Span<ulong> lookbacks = scratchBuffers.Values[..batch];
        int count = Math.Min(batch, Math.Max(0, remaining - _shortfall));
        if (ReadPreDelta(ref reader, count, lookbacks, in scratchBuffers, out ulong lower))
        {
            lookbacks[..count].Fill(lower);
        }

        // u32 latents: what wrapped past 32 bits wraps here as it does upstream.
        for (int i = 0; i < count; i++)
        {
            lookbacks[i] &= uint.MaxValue;
        }

        return lookbacks[..count];
    }

    /// <summary>Decodes one batch into <paramref name="destination"/>, or describes it.</summary>
    /// <param name="reader">The page reader.</param>
    /// <param name="remaining">Values left in the page before this batch.</param>
    /// <param name="batch">Values this batch produces.</param>
    /// <param name="destination">Where to put them.</param>
    /// <param name="lookbacks">The batch's lookbacks, for a lookback delta; empty otherwise.</param>
    /// <param name="scratchBuffers">The decode's working buffers; see <see cref="PcoBatchScratch"/>.</param>
    /// <param name="first">The one value, or the ramp's first, when the batch is not written.</param>
    /// <param name="step">The ramp's step, when the batch is a ramp.</param>
    /// <returns>
    /// How the batch is given: written into <paramref name="destination"/>, or, not written, as one
    /// value <paramref name="first"/>, or as the ramp from <paramref name="first"/> by
    /// <paramref name="step"/> that a first-order delta makes of one.
    /// </returns>
    internal PcoBatchShape ReadBatch(
        ref PcoBitReader reader, int remaining, int batch, Span<ulong> destination, scoped ReadOnlySpan<ulong> lookbacks,
        in PcoBatchScratch scratchBuffers, out ulong first, out ulong step)
    {
        Span<ulong> values = destination[..batch];

        // The values that come from the delta state are not on the wire, so the symbol pass is
        // shorter than the batch by the state's count - but only at the end of the page.
        int preDelta = Math.Min(batch, Math.Max(0, remaining - _shortfall));
        bool constant = ReadPreDelta(ref reader, preDelta, values, in scratchBuffers, out ulong lower);
        first = lower;
        step = 0;

        switch (_delta)
        {
            case PcoDeltaKind.NoOp:
                return constant ? PcoBatchShape.Constant : PcoBatchShape.Written;
            case PcoDeltaKind.Consecutive when constant && _stateCount == 1:
                // The prefix sum of one value, recentred, from the moment: see `UndoConsecutiveDelta`.
                step = unchecked(lower + _mid);
                first = _deltaMoments[0];
                _deltaMoments[0] = unchecked(first + ((ulong)batch * step));
                return PcoBatchShape.Ramp;
            case PcoDeltaKind.Consecutive:
                UndoConsecutiveDelta(values, constant, lower);
                return PcoBatchShape.Written;
            case PcoDeltaKind.Lookback:
                if (constant)
                {
                    values[..preDelta].Fill(lower);
                }

                UndoLookback(values, preDelta, lookbacks);
                return PcoBatchShape.Written;
            default:
                if (constant)
                {
                    values[..preDelta].Fill(lower);
                }

                UndoConv1(values, preDelta);
                return PcoBatchShape.Written;
        }
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
        int order = _stateCount - 1;
        if (constant)
        {
            // The recentring of the delta, on the one value.
            ulong step = unchecked(lower + _mid);
            ulong moment = _deltaMoments[order];
            PcoPageDecoder.Ramp(values, moment, step);
            _deltaMoments[order] = unchecked(moment + ((ulong)values.Length * step));
            order--;
        }
        else
        {
            // The recentring is pointwise and contiguous, so it is one vector add per lane group.
            PcoPageDecoder.Add(values, _mid);
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

    /// <summary>
    /// A page's lookback window opened: zeros where nothing was yet, then the page's state, the
    /// first values it gives, ending where the window does.
    /// </summary>
    private void OpenWindow(ref PcoBitReader reader, int bits, int windowLength, int stateCount)
    {
        // Upstream's buffer: twice the window or twice a batch, whichever is longer, so that a
        // batch always fits past the window before it has to be moved back.
        int length = Math.Max(windowLength, PcoPageDecoder.BatchSize) * 2;
        ulong[] window = ArrayPool<ulong>.Shared.Rent(length);
        _window = window;

        // All of it, not only what comes before the state: a lookback that points before the page
        // reads zeros, as upstream's zero-filled buffer gives, and never an earlier file's values.
        window.AsSpan(0, length).Clear();
        for (int i = windowLength - stateCount; i < windowLength; i++)
        {
            window[i] = reader.ReadUInt(bits);
        }

        _windowLength = windowLength;
        _windowPosition = windowLength;
    }

    /// <summary>
    /// Undoes a lookback delta over the batch, <c>lookback::decode_in_place</c>: each latent is its
    /// residual plus the latent its lookback points to, and the batch gives the window's latents
    /// the state's count behind, so a page's first values are its state.
    /// </summary>
    /// <param name="values">The batch, read before the delta, the first <paramref name="count"/> of them from the wire; the values, after.</param>
    /// <param name="count">Values read from the wire; the rest of a page's last batch comes from the window.</param>
    /// <param name="lookbacks">How far back each value looks.</param>
    private void UndoLookback(Span<ulong> values, int count, scoped ReadOnlySpan<ulong> lookbacks)
    {
        ulong[] window = _window!;
        int windowLength = _windowLength;
        int start = _windowPosition;
        if (start + values.Length > window.Length)
        {
            window.AsSpan(start - windowLength, windowLength).CopyTo(window);
            start = windowLength;
        }

        lookbacks = lookbacks[..count];
        for (int i = 0; i < count; i++)
        {
            ulong lookback = lookbacks[i];
            if (lookback > (ulong)windowLength)
            {
                CompressedThrow.Format($"A pco lookback of {lookback} exceeds its window of {windowLength}.");
            }

            int at = start + i;
            window[at] = unchecked(values[i] + _mid + window[at - (int)lookback]);
        }

        // The values past the wire at a page's end are never looked back to: nothing follows them.
        window.AsSpan(start - _stateCount, values.Length).CopyTo(values);
        _windowPosition = start + values.Length;
    }

    /// <summary>
    /// Undoes a convolution delta over the batch, <c>conv1::decode_in_place</c>: each latent is its
    /// residual plus the prediction the order latents before it make, the bias plus each weighed,
    /// clamped at zero and shifted down by the quantization; the batch gives the latents the order
    /// behind, so a page's first values are its state.
    /// </summary>
    /// <param name="values">The batch, read before the delta, the first <paramref name="count"/> of them from the wire; the values, after.</param>
    /// <param name="count">Values read from the wire.</param>
    /// <remarks>
    /// The prediction is summed in 64 bits: the chunk's check bounds it inside the signed type
    /// twice the latent's width, i32 for 16-bit latents and i64 for 32-bit ones, so no sum
    /// overflows either and each is what upstream's is. The latents are held to their width as
    /// they are made, since each one is weighed in the next predictions.
    /// </remarks>
    private void UndoConv1(Span<ulong> values, int count)
    {
        ulong[] residuals = _residuals!;
        ReadOnlySpan<long> weights = _chunk.Conv1Weights;
        int order = weights.Length;
        long bias = _chunk.Conv1Bias;
        int quantization = _chunk.Conv1Quantization;
        ulong mask = _mask;
        for (int i = 0; i < count; i++)
        {
            long sum = bias;
            for (int j = 0; j < order; j++)
            {
                sum += weights[j] * (long)residuals[i + j];
            }

            ulong prediction = (ulong)(Math.Max(sum, 0) >> quantization);
            residuals[order + i] = unchecked(values[i] + _mid + prediction) & mask;
        }

        residuals.AsSpan(0, values.Length).CopyTo(values);

        // The next batch's state: the order latents past this batch's. At a page's end they are
        // not all made, and nothing follows to read them.
        residuals.AsSpan(values.Length, order).CopyTo(residuals);
    }
}
