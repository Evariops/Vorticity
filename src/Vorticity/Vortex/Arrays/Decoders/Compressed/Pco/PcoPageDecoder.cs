using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
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
/// The offsets are not inline with the symbols: the symbol pass records each value's offset width,
/// and the offset pass then reads the values' offsets one after the other from where the symbols
/// end. Reading an offset inline would consume the stream in the wrong order.
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
        if (Native(chunk))
        {
            if (number.LatentBits == 32)
            {
                DecodeNative(
                    chunk, page, valueCount, MemoryMarshal.Cast<ulong, uint>(secondaryBatch), in scratch,
                    MemoryMarshal.Cast<byte, uint>(target)[..valueCount]);
            }
            else
            {
                DecodeNative(
                    chunk, page, valueCount, MemoryMarshal.Cast<ulong, ushort>(secondaryBatch), in scratch,
                    MemoryMarshal.Cast<byte, ushort>(target)[..valueCount]);
            }

            return;
        }

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
                        Progression.Fill(second, secondaryFirst, secondaryStep);
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

    /// <summary>
    /// Whether a chunk's page decodes at its numbers' own width (<see cref="DecodeNative{T}"/>):
    /// numbers of 16 or 32 bits whose latents are all as wide, no delta but a consecutive one, and
    /// a mode whose join stays within that width, which a dictionary's does not.
    /// </summary>
    /// <remarks>
    /// Wrapping arithmetic at the latent's width gives the low bits that wrapping arithmetic modulo
    /// 2^64 gives, which are all a number keeps: the two paths decode the same numbers. The narrow
    /// one holds four or eight latents to a 128-bit vector where the wide one holds two, and NEON
    /// multiplies 16- and 32-bit lanes where it has no 64-bit multiply at all; and it decodes into
    /// the output, where the wide one joins in a buffer and truncates into it.
    /// </remarks>
    private static bool Native(PcoChunkMeta chunk)
    {
        int bits = chunk.Number.LatentBits;
        return bits < 64
            && chunk.DeltaLatent is null
            && chunk.Delta is PcoDeltaKind.NoOp or PcoDeltaKind.Consecutive
            && chunk.Mode is not PcoModeKind.Dict
            && chunk.Primary.LatentBits == bits
            && (chunk.Secondary is not { } secondary || secondary.LatentBits == bits);
    }

    /// <summary>
    /// <see cref="DecodeJoined"/> for a chunk <see cref="Native"/> accepts: each batch decoded and
    /// joined at the numbers' width <typeparamref name="T"/>, straight into <paramref name="target"/>.
    /// </summary>
    private static void DecodeNative<T>(
        PcoChunkMeta chunk, ReadOnlySpan<byte> page, int valueCount, Span<T> secondaryBatch,
        in PcoBatchScratch scratch, Span<T> target)
        where T : unmanaged, IBinaryInteger<T>
    {
        PcoBitReader reader = new PcoBitReader(page);
        try
        {
            PcoDeltaKind secondaryDelta = chunk.SecondaryStateCount > 0 ? chunk.Delta : PcoDeltaKind.NoOp;
            PcoLatentState primary = scratch.States[1].Reset(
                ref reader, chunk.Primary, chunk, chunk.Delta, chunk.PrimaryStateCount, chunk.PrimaryStateCount);
            PcoLatentState? secondary = chunk.Secondary is { } secondaryVar
                ? scratch.States[2].Reset(
                    ref reader, secondaryVar, chunk, secondaryDelta, chunk.SecondaryStateCount, chunk.SecondaryStateCount)
                : null;

            reader.DrainEmptyByte("page metadata");

            int done = 0;
            while (done < valueCount)
            {
                int remaining = valueCount - done;
                int batch = Math.Min(BatchSize, remaining);
                Span<T> values = target.Slice(done, batch);
                PcoBatchShape primaryShape = primary.ReadBatch(
                    ref reader, remaining, batch, values, default, in scratch, out ulong primaryFirst, out ulong primaryStep);

                Span<T> second = secondary is null ? default : secondaryBatch[..batch];
                PcoBatchShape secondaryShape = PcoBatchShape.Written;
                ulong secondaryFirst = 0;
                if (secondary is not null)
                {
                    secondaryShape = secondary.ReadBatch(
                        ref reader, remaining, batch, second, default, in scratch, out secondaryFirst, out ulong secondaryStep);
                    if (secondaryShape == PcoBatchShape.Ramp)
                    {
                        Progression.Fill(second, T.CreateTruncating(secondaryFirst), T.CreateTruncating(secondaryStep));
                        secondaryShape = PcoBatchShape.Written;
                    }
                }

                JoinNative(chunk, values, primaryShape, primaryFirst, primaryStep, second, secondaryShape, secondaryFirst);
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

    /// <summary><see cref="JoinBatch"/> at the numbers' width, for a chunk <see cref="Native"/> accepts.</summary>
    private static void JoinNative<T>(
        PcoChunkMeta chunk, Span<T> values, PcoBatchShape primaryShape, ulong primaryFirst, ulong primaryStep,
        Span<T> second, PcoBatchShape secondaryShape, ulong secondaryFirst)
        where T : unmanaged, IBinaryInteger<T>
    {
        PcoNumber number = chunk.Number;
        switch (chunk.Mode)
        {
            case PcoModeKind.Classic when number.Kind != PcoNumberKind.Float:
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
            default:
                Materialize(values, primaryShape, primaryFirst, primaryStep);
                Materialize(second, secondaryShape, secondaryFirst, 0);
                FloatQuant(values, second, number, (int)chunk.ModeBase);
                break;
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
    private static void Materialize<T>(Span<T> values, PcoBatchShape shape, ulong first, ulong step)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (shape == PcoBatchShape.Constant)
        {
            values.Fill(T.CreateTruncating(first));
        }
        else if (shape == PcoBatchShape.Ramp)
        {
            Progression.Fill(values, T.CreateTruncating(first), T.CreateTruncating(step));
        }
    }

    /// <summary>
    /// A classic batch: the primary, as its shape describes it, plus <paramref name="shift"/>, into
    /// <paramref name="values"/>, which hold the primary when it is written.
    /// </summary>
    internal static void Shift<T>(Span<T> values, PcoBatchShape shape, ulong first, ulong step, ulong shift)
        where T : unmanaged, IBinaryInteger<T>
    {
        switch (shape)
        {
            case PcoBatchShape.Constant:
                values.Fill(T.CreateTruncating(unchecked(first + shift)));
                break;
            case PcoBatchShape.Ramp:
                Progression.Fill(values, T.CreateTruncating(unchecked(first + shift)), T.CreateTruncating(step));
                break;
            default:
                Add(values, T.CreateTruncating(shift));
                break;
        }
    }

    /// <summary>
    /// Each latent as the float it orders, <c>from_latent_ordered</c>: a latent with the top bit set
    /// is a positive float with that bit cleared, one without it a negative float with every bit
    /// flipped -- one exclusive or either way, of the sign bit or of every bit, as the top bit says.
    /// </summary>
    private static void FloatsFromLatents<T>(Span<T> values, PcoNumber number)
        where T : unmanaged, IBinaryInteger<T>
    {
        int top = number.LatentBits - 1;
        T sign = T.CreateTruncating(number.Mid);
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<T>.Count)
        {
            int lanes = Vector<T>.Count;
            Vector<T> signs = new Vector<T>(sign);
            for (; i <= values.Length - lanes; i += lanes)
            {
                Vector<T> latent = Vector.LoadUnsafe(in values[i]);
                Vector<T> flip = signs | (((latent >>> top) & Vector<T>.One) - Vector<T>.One);
                (latent ^ flip).StoreUnsafe(ref values[i]);
            }
        }

        for (; i < values.Length; i++)
        {
            T latent = values[i];
            values[i] = latent ^ (sign | unchecked(((latent >>> top) & T.One) - T.One));
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
    private static void FloatMult<T>(Span<T> values, ReadOnlySpan<T> adjustments, PcoChunkMeta chunk)
        where T : unmanaged, IBinaryInteger<T>
    {
        PcoNumber number = chunk.Number;
        ulong mask = number.Mask;
        ulong mid = number.Mid;
        ulong baseBits = number.FromLatentOrdered(chunk.ModeBase);
        IntFloats floats = new IntFloats(number);
        adjustments = adjustments[..values.Length];
        switch (number.LatentBits)
        {
            case 64:
            {
                double factor = BitConverter.UInt64BitsToDouble(baseBits);
                for (int i = 0; i < values.Length; i++)
                {
                    double unadjusted = BitConverter.UInt64BitsToDouble(floats.Bits(ulong.CreateTruncating(values[i]), 64)) * factor;
                    ulong latent = unchecked(FloatToLatent(BitConverter.DoubleToUInt64Bits(unadjusted), mid, mask)
                        + ulong.CreateTruncating(adjustments[i]) + mid);
                    values[i] = T.CreateTruncating(FloatFromLatent(latent, mid, mask));
                }

                break;
            }

            case 32:
            {
                float factor = BitConverter.UInt32BitsToSingle((uint)baseBits);
                for (int i = 0; i < values.Length; i++)
                {
                    float unadjusted = BitConverter.UInt32BitsToSingle((uint)floats.Bits(ulong.CreateTruncating(values[i]) & mask, 32)) * factor;
                    ulong latent = unchecked(FloatToLatent(BitConverter.SingleToUInt32Bits(unadjusted), mid, mask)
                        + ulong.CreateTruncating(adjustments[i]) + mid);
                    values[i] = T.CreateTruncating(FloatFromLatent(latent, mid, mask));
                }

                break;
            }

            default:
            {
                Half factor = BitConverter.UInt16BitsToHalf((ushort)baseBits);
                for (int i = 0; i < values.Length; i++)
                {
                    Half unadjusted = BitConverter.UInt16BitsToHalf((ushort)floats.Bits(ulong.CreateTruncating(values[i]) & mask, 16)) * factor;
                    ulong latent = unchecked(FloatToLatent(BitConverter.HalfToUInt16Bits(unadjusted), mid, mask)
                        + ulong.CreateTruncating(adjustments[i]) + mid);
                    values[i] = T.CreateTruncating(FloatFromLatent(latent, mid, mask));
                }

                break;
            }
        }
    }

    /// <summary>
    /// What turning a latent into a float's bits works out of the float type alone, once for a batch rather than
    /// once a value: the midpoint, the magnitudes a float holds exactly, and the bits of the first
    /// one it does not.
    /// </summary>
    private readonly struct IntFloats
    {
        private readonly ulong _mid;
        private readonly ulong _mask;
        private readonly ulong _exact;
        private readonly ulong _exactBits;

        internal IntFloats(PcoNumber number)
        {
            int precision = number.PrecisionBits;
            int digits = precision + 1;
            _mid = number.Mid;
            _mask = number.Mask;
            _exact = 1UL << digits;

            // The float 2^digits, then one step of its precision per unit past it: its biased
            // exponent is the bias plus the digits, which is the midpoint's bits minus one past.
            ulong bias = (_mid >> (precision + 1)) - 1;
            _exactBits = (bias + (ulong)digits) << precision;
        }

        /// <summary>The bits of the float <paramref name="latent"/> stands for, the type <paramref name="latentBits"/> wide.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong Bits(ulong latent, int latentBits)
        {
            unchecked
            {
                bool negative = latent < _mid;
                ulong magnitude = negative ? _mid - 1 - latent : latent - _mid;
                ulong bits = magnitude < _exact
                    ? latentBits switch
                    {
                        64 => BitConverter.DoubleToUInt64Bits(magnitude),
                        32 => BitConverter.SingleToUInt32Bits(magnitude),
                        _ => BitConverter.HalfToUInt16Bits((Half)(float)magnitude),
                    }
                    : (_exactBits + (magnitude - _exact)) & _mask;
                return negative ? bits ^ _mid : bits;
            }
        }
    }

    /// <summary>A float's ordered latent as its bits, <see cref="PcoNumber.FromLatentOrdered"/> for a float.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FloatFromLatent(ulong latent, ulong mid, ulong mask) =>
        ((latent & mid) != 0 ? latent ^ mid : ~latent) & mask;

    /// <summary>A float's bits as its ordered latent, <see cref="PcoNumber.FloatToLatentOrdered"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FloatToLatent(ulong bits, ulong mid, ulong mask) =>
        ((bits & mid) != 0 ? ~bits : bits ^ mid) & mask;

    /// <summary>
    /// A FloatQuant batch, <c>float_quant::join_latents</c>: the primary is the ordered latent's top
    /// bits, the secondary its lowest <paramref name="k"/>, flipped for a negative float so that an
    /// exactly quantized one has a secondary of zero either way.
    /// </summary>
    private static void FloatQuant<T>(Span<T> values, ReadOnlySpan<T> lowest, PcoNumber number, int k)
        where T : unmanaged, IBinaryInteger<T>
    {
        ulong mask = number.Mask;
        ulong mid = number.Mid;
        ulong signCutoff = mid >> k;
        ulong lowestMax = (1UL << k) - 1;
        lowest = lowest[..values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            ulong y = ulong.CreateTruncating(values[i]) & mask;
            ulong m = ulong.CreateTruncating(lowest[i]);
            ulong low = y >= signCutoff ? m : unchecked(lowestMax - m);
            values[i] = T.CreateTruncating(FloatFromLatent(unchecked((y << k) + low), mid, mask));
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
    internal static void Join<T>(
        Span<T> values, PcoBatchShape primary, ulong primaryFirst, ulong primaryStep,
        ReadOnlySpan<T> secondary, PcoBatchShape secondaryShape, ulong secondaryValue, ulong modeBase, ulong shift)
        where T : unmanaged, IBinaryInteger<T>
    {
        unchecked
        {
            T factor = T.CreateTruncating(modeBase);
            if (secondaryShape == PcoBatchShape.Constant)
            {
                T added = T.CreateTruncating(secondaryValue + shift);
                switch (primary)
                {
                    case PcoBatchShape.Constant:
                        values.Fill(T.CreateTruncating(primaryFirst * modeBase) + added);
                        break;
                    case PcoBatchShape.Ramp:
                        Progression.Fill(values, T.CreateTruncating(primaryFirst * modeBase) + added, T.CreateTruncating(primaryStep * modeBase));
                        break;
                    default:
                        for (int i = Multiplied(values, default, factor, added); i < values.Length; i++)
                        {
                            values[i] = (values[i] * factor) + added;
                        }

                        break;
                }

                return;
            }

            T plus = T.CreateTruncating(shift);
            secondary = secondary[..values.Length];
            switch (primary)
            {
                case PcoBatchShape.Constant:
                    values.Fill(T.CreateTruncating((primaryFirst * modeBase) + shift));
                    Add(values, secondary);
                    break;
                case PcoBatchShape.Ramp:
                    Progression.Fill(values, T.CreateTruncating((primaryFirst * modeBase) + shift), T.CreateTruncating(primaryStep * modeBase));
                    Add(values, secondary);
                    break;
                default:
                    for (int i = Multiplied(values, secondary, factor, plus); i < values.Length; i++)
                    {
                        values[i] = (values[i] * factor) + secondary[i] + plus;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// <c>values[i] * modeBase + secondary[i] + added</c>, a vector at a time where the vector unit
    /// multiplies lanes of <typeparamref name="T"/>; <paramref name="secondary"/> empty adds nothing.
    /// </summary>
    /// <returns>The values done, a whole number of vectors; the rest are the caller's.</returns>
    /// <remarks>
    /// Lanes of 16 and 32 bits multiply everywhere. 64-bit lanes take AVX-512DQ's <c>vpmullq</c>,
    /// eight at a time, which wraps as the scalar multiply does; NEON has no 64-bit vector multiply,
    /// so 64-bit latents stay scalar there, which is what decoding at the numbers' width is for.
    /// </remarks>
    private static int Multiplied<T>(Span<T> values, ReadOnlySpan<T> secondary, T modeBase, T added)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(ulong))
        {
            return MultipliedWide(
                MemoryMarshal.Cast<T, ulong>(values), MemoryMarshal.Cast<T, ulong>(secondary),
                ulong.CreateTruncating(modeBase), ulong.CreateTruncating(added));
        }

        // Lanes of 16 or 32 bits, which every vector unit multiplies, NEON included.
        if (!Vector.IsHardwareAccelerated || values.Length < Vector<T>.Count)
        {
            return 0;
        }

        int lanes = Vector<T>.Count;
        ref T at = ref MemoryMarshal.GetReference(values);
        ref T other = ref MemoryMarshal.GetReference(secondary);
        bool paired = !secondary.IsEmpty;
        Vector<T> factor = new Vector<T>(modeBase);
        Vector<T> plus = new Vector<T>(added);
        int i = 0;
        for (; i <= values.Length - lanes; i += lanes)
        {
            Vector<T> joined = (Vector.LoadUnsafe(ref at, (nuint)i) * factor) + plus;
            if (paired)
            {
                joined += Vector.LoadUnsafe(ref other, (nuint)i);
            }

            joined.StoreUnsafe(ref at, (nuint)i);
        }

        return i;
    }

    /// <summary><see cref="Multiplied{T}"/> for 64-bit latents.</summary>
    private static int MultipliedWide(Span<ulong> values, ReadOnlySpan<ulong> secondary, ulong modeBase, ulong added)
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

    /// <summary>Adds <paramref name="by"/> to every value, one vector add per lane group.</summary>
    internal static void Add<T>(Span<T> values, T by)
        where T : unmanaged, IBinaryInteger<T>
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<T>.Count)
        {
            Vector<T> added = new Vector<T>(by);
            int lanes = Vector<T>.Count;
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
    private static void Add<T>(Span<T> values, ReadOnlySpan<T> other)
        where T : unmanaged, IBinaryInteger<T>
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && values.Length >= Vector<T>.Count)
        {
            int lanes = Vector<T>.Count;
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
        PcoLatentState[] states,
        Span<ulong> primary = default)
    {
        Values = values;
        OffsetBits = offsetBits;
        States = states;
        Primary = primary;
    }

    /// <summary>The batch's latents of a variable nothing keeps.</summary>
    internal Span<ulong> Values { get; }

    /// <summary>Each value's offset width, when the variable has more than one bin.</summary>
    internal Span<int> OffsetBits { get; }

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
    internal PcoBatchShape ReadBatch<T>(
        ref PcoBitReader reader, int remaining, int batch, Span<T> destination, scoped ReadOnlySpan<ulong> lookbacks,
        in PcoBatchScratch scratchBuffers, out ulong first, out ulong step)
        where T : unmanaged, IBinaryInteger<T>
    {
        Span<T> values = destination[..batch];

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
            {
                Span<ulong> wide = Wide(values);
                if (constant)
                {
                    wide[..preDelta].Fill(lower);
                }

                UndoLookback(wide, preDelta, lookbacks);
                return PcoBatchShape.Written;
            }

            default:
            {
                Span<ulong> wide = Wide(values);
                if (constant)
                {
                    wide[..preDelta].Fill(lower);
                }

                UndoConv1(wide, preDelta);
                return PcoBatchShape.Written;
            }
        }
    }

    /// <summary>
    /// A batch of 64-bit latents as such: a lookback and a convolution are decoded at 64 bits only,
    /// <c>PcoPageDecoder.Native</c> sending their chunks down the wide path.
    /// </summary>
    private static Span<ulong> Wide<T>(Span<T> values)
        where T : unmanaged
    {
        if (typeof(T) != typeof(ulong))
        {
            throw new UnreachableException("A lookback or a convolution delta is decoded at 64 bits only.");
        }

        return MemoryMarshal.Cast<T, ulong>(values);
    }

    /// <summary>
    /// Reads <paramref name="count"/> values before their delta is undone; true, with nothing
    /// written, when every one of them is <paramref name="lower"/>.
    /// </summary>
    private bool ReadPreDelta<T>(
        ref PcoBitReader reader, int count, Span<T> scratch, in PcoBatchScratch scratchBuffers, out ulong lower)
        where T : unmanaged, IBinaryInteger<T>
    {
        lower = 0;
        if (count == 0)
        {
            return false;
        }

        Span<int> offsetBits = scratchBuffers.OffsetBits;
        if (_binCount > 1)
        {
            ReadOnlySpan<PcoAnsNode> nodes = _table.Nodes;
            ReadOnlySpan<ulong> lowers = _table.StateLowers;

            // Four symbols at a time while their word lies inside the page, the few past the last
            // such step one by one, each read checked.
            int i = _table.SizeLog <= MaxSymbolBits ? ReadSymbols(ref reader, count, nodes, lowers, scratch, offsetBits) : 0;
            for (; i < count; i++)
            {
                int slot = _stateIndices[i % 4];
                PcoAnsNode node = nodes[slot];
                ulong ansValue = reader.ReadUInt(node.BitsToRead);

                scratch[i] = T.CreateTruncating(lowers[slot]);
                offsetBits[i] = node.OffsetBits;

                _stateIndices[i % 4] = node.NextStateIndexBase + (int)ansValue;
            }

            // Bins of one value each store no offset: the lower bounds the symbols wrote are the
            // values, and the offsets' pass would add a zero to each, as upstream does not.
            if (_table.MaxOffsetBits == 0)
            {
                return false;
            }
        }
        else
        {
            // A single bin means every value is that bin, and the ANS stream is skipped entirely
            // rather than read as zero-width symbols. Every value then has the same width, so the
            // per-value width array describes nothing -- the position of value i is
            // `base + i * offsetBits`.
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

            // The values whose words lie inside the page unchecked, the rest checked.
            long uniformBase = reader.BitPosition;
            long limit = reader.WordLimit;
            int quick = uniformBase >= limit ? 0 : (int)Math.Min(count, ((limit - uniformBase - 1) / uniformBits) + 1);
            ref byte origin = ref reader.Origin;
            for (int i = 0; i < quick; i++)
            {
                scratch[i] = T.CreateTruncating(unchecked(lower + PcoBitReader.Peek(ref origin, uniformBase + ((long)i * uniformBits), uniformBits)));
            }

            for (int i = quick; i < count; i++)
            {
                scratch[i] = T.CreateTruncating(unchecked(
                    lower + reader.ReadAt(uniformBase + ((long)i * uniformBits), uniformBits)));
            }

            reader.SeekBits(uniformBase + ((long)count * uniformBits));
            return false;
        }

        // The offsets live in their own stream starting where the symbols ended, each value's
        // right after the one before it: read unchecked while their words lie inside the page, a
        // width of zero keeping none of the word it loads, and the few past them checked.
        long position = reader.BitPosition;
        long wordLimit = reader.WordLimit;
        ref byte offsets = ref reader.Origin;
        int at = 0;
        if (_table.MaxOffsetBits <= MaxWordBits && (uint)count <= (uint)scratch.Length && (uint)count <= (uint)offsetBits.Length)
        {
            // Every offset inside the one word a read loads: no second word, and no test for a
            // width of 64, the branch a value `PcoBitReader.Peek` takes for the widest latents.
            // Each slot addressed once through a reference, and its mask loaded from a table where
            // no bzhi keeps the low bits: indexed reads made the address twice and the mask from the
            // width, nineteen instructions a value on arm64.
            ref T value = ref MemoryMarshal.GetReference(scratch);
            ref int widths = ref MemoryMarshal.GetReference(offsetBits);
            ref ulong masks = ref MemoryMarshal.GetArrayDataReference(LowMasks);
            for (; at < count && position < wordLimit; at++)
            {
                int width = Unsafe.Add(ref widths, at);
                ulong word = PcoBitReader.Word(ref offsets, position);
                ulong bits = Bmi2.X64.IsSupported ? Bmi2.X64.ZeroHighBits(word, (ulong)width) : word & Unsafe.Add(ref masks, width);
                ref T slot = ref Unsafe.Add(ref value, at);
                slot = unchecked(slot + T.CreateTruncating(bits));
                position += width;
            }
        }

        for (; at < count && position < wordLimit; at++)
        {
            int width = offsetBits[at];
            scratch[at] = unchecked(scratch[at] + T.CreateTruncating(PcoBitReader.Peek(ref offsets, position, width)));
            position += width;
        }

        for (; at < count; at++)
        {
            int width = offsetBits[at];
            if (width != 0)
            {
                scratch[at] = unchecked(scratch[at] + T.CreateTruncating(reader.ReadAt(position, width)));
                position += width;
            }
        }

        reader.SeekBits(position);
        return false;
    }

    /// <summary>
    /// The widest table <see cref="ReadSymbols"/> reads four symbols of out of one word: four reads
    /// of 14 bits take no more than the 57 a word holds past its byte, and pco's size log is 14 at
    /// most.
    /// </summary>
    private const int MaxSymbolBits = 14;

    /// <summary>The widest read one word serves: 64 bits less the seven a position past its byte can skip.</summary>
    private const int MaxWordBits = 57;

    /// <summary>The low <c>i</c> bits set, for every width a word serves: an offset's mask.</summary>
    private static readonly ulong[] LowMasks = BuildLowMasks();

    private static ulong[] BuildLowMasks()
    {
        ulong[] masks = new ulong[MaxWordBits + 1];
        for (int width = 0; width <= MaxWordBits; width++)
        {
            masks[width] = (1UL << width) - 1;
        }

        return masks;
    }

    /// <summary>
    /// A batch's symbols four at a time, for as long as the four lie inside the page with their
    /// word: the four interleaved states held in registers, and the four symbols of a step read out
    /// of one word unchecked, the table no wider than <see cref="MaxSymbolBits"/>.
    /// </summary>
    /// <returns>The symbols read, a multiple of four: the caller reads the rest one by one.</returns>
    /// <remarks>
    /// The buffers and the tables are checked once rather than per symbol, and a state indexes the
    /// tables masked to their size, a power of two. The mask changes no state a table produces:
    /// a node's next state base plus the most it reads stays below the size, so a corrupt page
    /// decodes to the same values the checked loop gives, and never reads past the tables.
    /// </remarks>
    private int ReadSymbols<T>(
        ref PcoBitReader reader, int count, ReadOnlySpan<PcoAnsNode> nodes, ReadOnlySpan<ulong> lowers,
        Span<T> scratch, Span<int> offsetBits)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<ulong> packed = _table.Packed;
        if ((uint)count > (uint)scratch.Length || (uint)count > (uint)offsetBits.Length
            || lowers.Length != nodes.Length || packed.Length != nodes.Length || !BitOperations.IsPow2(nodes.Length))
        {
            return 0;
        }

        // Native-sized indices, which address the tables without a sign extension each. Each node
        // is one word (`PcoAnsTable.Packed`) holding the mask of the bits it reads, a table no wider
        // than MaxSymbolBits: neither the test for a width of 64 that `PcoBitReader.Low` makes, a
        // taken branch a symbol on arm64, nor the mask made from the count is on a state's chain.
        nint mask = nodes.Length - 1;
        ref ulong table = ref MemoryMarshal.GetReference(packed);
        ref ulong bounds = ref MemoryMarshal.GetReference(lowers);
        ref T values = ref MemoryMarshal.GetReference(scratch);
        ref int widths = ref MemoryMarshal.GetReference(offsetBits);
        ref byte origin = ref reader.Origin;
        long limit = reader.WordLimit;
        long position = reader.BitPosition;
        nint state0 = _stateIndices[0] & mask;
        nint state1 = _stateIndices[1] & mask;
        nint state2 = _stateIndices[2] & mask;
        nint state3 = _stateIndices[3] & mask;
        nint i = 0;
        nint end = count & ~3;
        while (i < end && position < limit)
        {
            // The steps that surely start inside the page, a step reading at most four symbols of
            // MaxSymbolBits: the loop over them checks no position, and the bound and the count
            // the JIT reloaded from the stack at every step are read once for them all.
            nint stop = (nint)Math.Min(end, i + (4 * (((limit - position - 1) / (4 * MaxSymbolBits)) + 1)));
            for (; i < stop; i += 4)
            {
                ulong word = PcoBitReader.Word(ref origin, position);

                ulong node = Unsafe.Add(ref table, state0);
                Unsafe.Add(ref values, i) = T.CreateTruncating(Unsafe.Add(ref bounds, state0));
                Unsafe.Add(ref widths, i) = (int)((node >> PcoAnsTable.PackedOffsetShift) & 0xFF);
                int read = (int)((node >> PcoAnsTable.PackedReadShift) & 0xFF);
                state0 = ((nint)(ushort)node + (nint)(word & (node >> PcoAnsTable.PackedMaskShift))) & mask;
                word >>= read;

                node = Unsafe.Add(ref table, state1);
                Unsafe.Add(ref values, i + 1) = T.CreateTruncating(Unsafe.Add(ref bounds, state1));
                Unsafe.Add(ref widths, i + 1) = (int)((node >> PcoAnsTable.PackedOffsetShift) & 0xFF);
                int bits = (int)((node >> PcoAnsTable.PackedReadShift) & 0xFF);
                state1 = ((nint)(ushort)node + (nint)(word & (node >> PcoAnsTable.PackedMaskShift))) & mask;
                word >>= bits;
                read += bits;

                node = Unsafe.Add(ref table, state2);
                Unsafe.Add(ref values, i + 2) = T.CreateTruncating(Unsafe.Add(ref bounds, state2));
                Unsafe.Add(ref widths, i + 2) = (int)((node >> PcoAnsTable.PackedOffsetShift) & 0xFF);
                bits = (int)((node >> PcoAnsTable.PackedReadShift) & 0xFF);
                state2 = ((nint)(ushort)node + (nint)(word & (node >> PcoAnsTable.PackedMaskShift))) & mask;
                word >>= bits;
                read += bits;

                node = Unsafe.Add(ref table, state3);
                Unsafe.Add(ref values, i + 3) = T.CreateTruncating(Unsafe.Add(ref bounds, state3));
                Unsafe.Add(ref widths, i + 3) = (int)((node >> PcoAnsTable.PackedOffsetShift) & 0xFF);
                bits = (int)((node >> PcoAnsTable.PackedReadShift) & 0xFF);
                state3 = ((nint)(ushort)node + (nint)(word & (node >> PcoAnsTable.PackedMaskShift))) & mask;
                position += read + bits;
            }
        }

        _stateIndices[0] = (int)state0;
        _stateIndices[1] = (int)state1;
        _stateIndices[2] = (int)state2;
        _stateIndices[3] = (int)state3;
        reader.SeekBits(position);
        return (int)i;
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
    private void UndoConsecutiveDelta<T>(Span<T> values, bool constant, ulong lower)
        where T : unmanaged, IBinaryInteger<T>
    {
        // The moments are kept as 64-bit words, and the running one stays one through the sums: at a
        // narrower width only its low bits are stored, which a store of that width does by itself,
        // where a running moment of 16 bits zero-extended itself after every add, a second cycle a
        // value in a chain of one on arm64.
        int order = _stateCount - 1;
        if (constant)
        {
            // The recentring of the delta, on the one value.
            ulong step = unchecked(lower + _mid);
            ulong moment = _deltaMoments[order];
            Progression.Fill(values, T.CreateTruncating(moment), T.CreateTruncating(step));
            _deltaMoments[order] = unchecked(moment + ((ulong)values.Length * step));
            order--;
        }
        else if (order >= 0)
        {
            // The recentring folded into the highest order's prefix sum, which reads each value
            // once anyway: a pass of its own over the batch was a seventh of a 16-bit pco decode on
            // arm64, where a vector holds two of these 64-bit latents.
            _deltaMoments[order] = PrefixSums(values, _deltaMoments[order], _mid);
            order--;
        }
        else
        {
            // The recentring is pointwise and contiguous, so it is one vector add per lane group.
            PcoPageDecoder.Add(values, T.CreateTruncating(_mid));
        }

        for (; order >= 0; order--)
        {
            _deltaMoments[order] = PrefixSums(values, _deltaMoments[order], 0);
        }
    }

    /// <summary>
    /// One order of a consecutive delta undone: each value replaced by <paramref name="moment"/>
    /// plus the values before it, each first moved by <paramref name="bias"/>.
    /// </summary>
    /// <returns>The moment past the batch, for the next one.</returns>
    /// <remarks>
    /// <para>
    /// A prefix sum is a chain of dependent adds, one a value. At 16 and 32 bits it is taken a
    /// 128-bit vector at a time instead: within the vector by adding it to itself shifted up one,
    /// two and four lanes, then moved by the moment, which the vector's last sum carries to the
    /// next with one broadcast and one add. The chain is then one step a vector of eight or four
    /// values, where the scalar loop took two cycles a value on arm64.
    /// </para>
    /// <para>
    /// The running moment is a 64-bit word and only its low bits are stored, which a store of the
    /// value's width does by itself: a moment summed as a ushort zero-extended itself after every
    /// add, a second cycle a value in a chain of one.
    /// </para>
    /// </remarks>
    internal static ulong PrefixSums<T>(Span<T> values, ulong moment, ulong bias)
        where T : unmanaged, IBinaryInteger<T>
    {
        int i = 0;
        if ((typeof(T) == typeof(ushort) || typeof(T) == typeof(uint)) && (AdvSimd.IsSupported || Sse2.IsSupported)
            && values.Length >= Vector128<T>.Count)
        {
            int lanes = Vector128<T>.Count;
            ref T at = ref MemoryMarshal.GetReference(values);
            Vector128<T> biases = Vector128.Create(T.CreateTruncating(bias));
            Vector128<T> carry = Vector128.Create(T.CreateTruncating(moment));
            for (; i <= values.Length - lanes; i += lanes)
            {
                Vector128<T> value = Vector128.LoadUnsafe(ref at, (nuint)i) + biases;
                Vector128<T> sums;
                if (typeof(T) == typeof(ushort))
                {
                    sums = value + LanesUp(value, 2);
                    sums += LanesUp(sums, 4);
                    sums += LanesUp(sums, 8);
                }
                else
                {
                    sums = value + LanesUp(value, 4);
                    sums += LanesUp(sums, 8);
                }

                // Each value's sum less itself is the sum of the values before it.
                (carry + sums - value).StoreUnsafe(ref at, (nuint)i);
                carry += Last(sums);
            }

            moment = ulong.CreateTruncating(carry.ToScalar());
        }

        for (; i < values.Length; i++)
        {
            ulong previous = unchecked(ulong.CreateTruncating(values[i]) + bias);
            values[i] = T.CreateTruncating(moment);
            moment = unchecked(moment + previous);
        }

        return moment;
    }

    /// <summary>The vector's lanes moved up by <paramref name="bytes"/>, zeros shifted in at the bottom.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> LanesUp<T>(Vector128<T> value, [ConstantExpected(Min = 2, Max = 8)] int bytes)
        where T : unmanaged
    {
        Vector128<byte> lanes = value.AsByte();
        if (AdvSimd.IsSupported)
        {
            // The bottom 16 - bytes bytes of the value, above bytes zeros.
            Vector128<byte> zero = Vector128<byte>.Zero;
            return (bytes switch
            {
                2 => AdvSimd.ExtractVector128(zero, lanes, 14),
                4 => AdvSimd.ExtractVector128(zero, lanes, 12),
                _ => AdvSimd.ExtractVector128(zero, lanes, 8),
            }).As<byte, T>();
        }

        return (bytes switch
        {
            2 => Sse2.ShiftLeftLogical128BitLane(lanes, 2),
            4 => Sse2.ShiftLeftLogical128BitLane(lanes, 4),
            _ => Sse2.ShiftLeftLogical128BitLane(lanes, 8),
        }).As<byte, T>();
    }

    /// <summary>The vector's last lane in every lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> Last<T>(Vector128<T> value)
        where T : unmanaged
    {
        if (AdvSimd.IsSupported)
        {
            return typeof(T) == typeof(ushort)
                ? AdvSimd.DuplicateSelectedScalarToVector128(value.AsUInt16(), 7).As<ushort, T>()
                : AdvSimd.DuplicateSelectedScalarToVector128(value.AsUInt32(), 3).As<uint, T>();
        }

        return Vector128.Create(value.GetElement(Vector128<T>.Count - 1));
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
