// Choosing a bit width when a handful of values refuse to fit - vortex-fastlanes-0.86.1's
// `find_best_bit_width` / `best_bit_width` (src/bitpacking/compress.rs), and the transform that
// has to be chosen with it.
//
// THE PROBLEM, stated by its own corpus file. `types/i64_nonnull_r8192` is 0, i64::MIN, i64::MAX
// and then eight thousand values alternating either side of zero up to about 56 000. Frame of
// reference takes the MINIMUM as its reference, so every ordinary value in that column becomes
// roughly 2^63 and the span is the whole 64 bits: a scheme designed to shrink dense integers
// declines to touch the densest column in the corpus, because three rows out of 8192 say so. We
// wrote it canonically at 3.31x the reference's size.
//
// TWO THINGS FIX IT, and they are separate:
//
//   * PATCHES. Pack at the width the BULK needs and carry the rest as (index, value) pairs, which
//     `fastlanes.bitpacked` has always been able to express and our decoder has always read. The
//     width is then not a property of the data but a MINIMUM over a cost function - packed bytes
//     plus what the exceptions cost - and that is what `best_bit_width` computes.
//   * THE TRANSFORM. Patches alone do not save that file: with the reference pinned to i64::MIN,
//     HALF the column is an exception, and no width is cheap. What the reference does there is
//     `vortex.zigzag` instead - interleave the sign bit, so magnitude rather than position decides
//     the width - and then bit-pack with two patches at 17 bits. Verified, not guessed: the
//     corpus sidecar's array tree for that file reads zigzag(bitpacked) and its metadata decodes
//     to bit_width 17, two patches.
//
// So both are priced and the cheaper wins. Frame still wins outright on the columns it was already
// winning - a column of timestamps around 1.7e18 has a tiny span and an enormous magnitude - which
// is precisely why neither transform can simply replace the other.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>Which reversible map turns a column into the unsigned one that bit-packs.</summary>
internal enum BitPackTransform : byte
{
    /// <summary>Subtract the minimum. Serialized as <c>fastlanes.for</c> over the packed child.</summary>
    Frame = 0,

    /// <summary>Interleave the sign bit. Serialized as <c>vortex.zigzag</c> over the packed child.</summary>
    ZigZag = 1,
}

/// <summary>A bit-packing decision: the transform, the width, and the values that did not fit.</summary>
internal sealed class BitPackPlan
{
    /// <summary>
    /// What a frame-of-reference or zigzag node costs beyond its packed bytes: one more array
    /// node, its metadata and its buffer spec. Deliberately generous - the point is to refuse
    /// encodings that barely pay, because each one costs a decode step on every read.
    /// </summary>
    private const long NodeOverhead = 256;

    /// <summary>
    /// What the patch children cost beyond the (index, value) pairs themselves: two more array
    /// nodes, their metadata and their buffer specs.
    /// </summary>
    private const long PatchOverhead = 256;

    private BitPackPlan(
        BitPackTransform transform, ulong reference, int bitWidth, long cost, long exceptions)
    {
        Transform = transform;
        Reference = reference;
        BitWidth = bitWidth;
        Cost = cost;
        Exceptions = exceptions;
    }

    /// <summary>Which map was chosen.</summary>
    internal BitPackTransform Transform { get; }

    /// <summary>
    /// The frame of reference, as the column's own raw bits. Meaningless under
    /// <see cref="BitPackTransform.ZigZag"/>.
    /// </summary>
    internal ulong Reference { get; }

    /// <summary>Bits per packed value.</summary>
    internal int BitWidth { get; }

    /// <summary>
    /// What this plan is estimated to cost in bytes, wrapper node included, so the caller can
    /// compare it against the other schemes' estimates rather than merely against canonical.
    /// </summary>
    internal long Cost { get; }

    /// <summary>
    /// How many rows' encoded value needs more than <see cref="BitWidth"/> bits — the patches the
    /// pack will find and write, counted here from the histogram so the pack can size them.
    /// </summary>
    /// <remarks>
    /// THE PATCHES THEMSELVES ARE GATHERED INSIDE THE PACK (docs/11-write-strategy.md §3.5), which
    /// encodes every row anyway and sees each exception as it goes: <c>v ≥ 2^b</c> on the value it
    /// has just transformed. Until stage R5b this plan carried the patch arrays, produced by a walk
    /// of its own over the column -- the same transform, applied a second time, to find the same
    /// rows. The count is all the pack needs from the chooser, and the pack checks it: the
    /// histogram and the encode read the same rows under the same transform, so they agree or one
    /// of the two is wrong, and a mismatch is an exception rather than a short array.
    /// </remarks>
    internal long Exceptions { get; }

    /// <summary>
    /// Measures the column and decides whether bit-packing it pays, and how.
    /// </summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="node">The column chunk; must be an integer primitive.</param>
    /// <param name="zigzag">
    /// Whether <c>vortex.zigzag</c> may be emitted. A candidate the target edition does not carry
    /// is not weighed and then discarded; it is never weighed, so the frame's own answer stands.
    /// </param>
    /// <param name="reference">
    /// The column's minimum in raw bits, when the ingest pass has already found it
    /// (docs/11-write-strategy.md §8 stage 2); <see langword="null"/> to measure it here.
    /// <see cref="Minimum"/> is a whole pass over every row, run on every integer column of every
    /// file to recompute a number the zone map's own pass produced.
    /// </param>
    /// <param name="ingested">
    /// The chunk's pair of bit-width histograms from the ingest pass (<see cref="BitPackWidths"/>),
    /// or empty. The zigzag half is always usable; the raw half IS the framed histogram exactly when
    /// <paramref name="reference"/> is zero, and only then (docs/11-write-strategy.md §3.2.3).
    /// </param>
    /// <returns>The plan, or <see langword="null"/> when packing does not pay.</returns>
    internal static BitPackPlan? TryBuild(
        CanonicalArena arena, CanonicalNode node, bool zigzag = true, ulong? reference = null,
        ReadOnlySpan<int> ingested = default)
    {
        if (node.Kind != CanonicalKind.Primitive || !node.PType.IsInteger())
        {
            return null;
        }

        PType ptype = node.PType;
        int elementBits = ptype.ByteWidth() * 8;
        int length = node.Length;
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        ReadOnlySpan<byte> values = node.Values.Span;
        PType raw = ToUnsigned(ptype);

        ulong minimum;
        if (reference is ulong known)
        {
            minimum = known;
        }
        else if (!Minimum(length, mask, values, ptype, out minimum))
        {
            // Every row is null, or there are no rows: nothing to measure a width against.
            return null;
        }

        // One pass, both histograms. Entry w counts the values needing exactly w bits, so the
        // number of exceptions at width w is the sum of entries above w - which is what the cost
        // loop accumulates from the top down.
        Span<int> frames = stackalloc int[65];
        Span<int> zigzags = stackalloc int[65];
        frames.Clear();
        zigzags.Clear();
        bool signed = ptype.IsSignedInteger();

        // WHAT THE INGEST PASS CAN ANSWER AND WHAT IT CANNOT, and the difference is the minimum.
        // docs/11-write-strategy.md §3.2 counts the RAW and ZIGZAG widths while the rows arrive,
        // because neither depends on a quantity the chunk only has once it is whole. The FRAMED
        // histogram does — it is the widths of `v - min` — so §3.2.3 gives frame of reference a
        // bound and a conditional sweep instead, which is stage R5's work. Until then:
        //
        //   * zigzag is taken from the pass whenever the pass ran, always;
        //   * raw IS the framed histogram when the reference is zero, which is every unsigned
        //     column that starts at zero, every dictionary's codes, and every offsets column --
        //     so those walk nothing at all;
        //   * a non-zero reference still walks, and skips the zigzag half it no longer needs.
        bool ingestedWidths = ingested.Length == BitPackWidths.Length;
        bool framedFromIngest = ingestedWidths && minimum == 0;
        if (ingestedWidths)
        {
            ingested[BitPackWidths.ZigZagOffset..].CopyTo(zigzags);
            if (framedFromIngest)
            {
                ingested[..BitPackWidths.Domain].CopyTo(frames);
            }
        }

        if (!framedFromIngest)
        {
            Histogram(
                values, raw, in mask, length, minimum, signed && !ingestedWidths, frames, zigzags);
        }

        long blocks = (length + FastLanes.BlockSize - 1) / FastLanes.BlockSize;
        long perException = ptype.ByteWidth() + FsstPlan.IndexPType(length).ByteWidth();

        Best best = Cheapest(frames, elementBits, blocks, perException, BitPackTransform.Frame);
        if (signed && zigzag)
        {
            Best other = Cheapest(zigzags, elementBits, blocks, perException, BitPackTransform.ZigZag);
            if (other.Cost < best.Cost)
            {
                best = other;
            }
        }

        // The comparison the whole class exists to make honest: against the bytes the canonical
        // column would have occupied, not against a fraction of the element width.
        long canonical = (long)length * ptype.ByteWidth();
        if (best.Cost + NodeOverhead >= canonical)
        {
            return null;
        }

        ulong transformReference = best.Transform == BitPackTransform.Frame ? minimum : 0;
        return new BitPackPlan(
            best.Transform, transformReference, best.BitWidth, best.Cost + NodeOverhead,
            best.Exceptions);
    }

    /// <summary>The encoded, unsigned form of one row's raw bits.</summary>
    /// <param name="bits">The row's value, as the element width's unsigned bits.</param>
    /// <param name="transform">Which map.</param>
    /// <param name="reference">The frame of reference, for <see cref="BitPackTransform.Frame"/>.</param>
    /// <param name="elementBits">8, 16, 32 or 64.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Encode(
        ulong bits, BitPackTransform transform, ulong reference, int elementBits) =>
        transform == BitPackTransform.Frame
            ? Frame(bits, reference, elementBits)
            : ZigZag(bits, elementBits);

    /// <summary>
    /// <c>value - reference</c>, wrapping and masked to the element width.
    /// </summary>
    /// <remarks>
    /// The MASK is not cosmetic here, though it is invisible in the packed bytes: the packer
    /// truncates to the element width anyway, but the histogram does not, and an unmasked
    /// subtraction that wrapped would report 64 bits for a value that packs into three.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Frame(ulong bits, ulong reference, int elementBits) =>
        unchecked(bits - reference) & WidthMask(elementBits);

    /// <summary>
    /// <c>(v &lt;&lt; 1) ^ (v &gt;&gt; (bits - 1))</c> with an arithmetic right shift: the sign bit
    /// becomes the low bit, so -1 encodes as 1 and 1 as 2.
    /// </summary>
    /// <remarks>
    /// Written over the unsigned word rather than over a signed one so that all four element
    /// widths share it: <c>0 - (bits >> (elementBits - 1))</c> is the arithmetic shift's result,
    /// all ones or none, without a cast per width.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ZigZag(ulong bits, int elementBits)
    {
        ulong width = WidthMask(elementBits);
        ulong sign = 0UL - ((bits >> (elementBits - 1)) & 1);
        return ((bits << 1) ^ sign) & width;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong WidthMask(int elementBits) =>
        elementBits == 64 ? ulong.MaxValue : (1UL << elementBits) - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitLength(ulong value) => 64 - BitOperations.LeadingZeroCount(value);

    /// <summary>
    /// Both width histograms in one walk, with the physical type resolved before the walk starts.
    /// </summary>
    /// <remarks>
    /// THE SHAPE OF W-9 AND W-33, APPLIED TO THE LAST WRITE-SIDE LOOP THAT STILL DISPATCHED PER ROW.
    /// The walk called <c>CompressedValues.ReadUnsigned(values, raw, row)</c> on every row of every
    /// integer column of every chunk: a switch on the physical type, per value, to reach a load the
    /// type decides once. Measured by doubling this very loop and reading the axis, it was worth
    /// 18 % to 44 % of the write on twelve encodings — 34 % of <c>fastlanes_bitpacked</c>, 44 % of
    /// <c>fastlanes_for</c>, 38 % of <c>table_wide</c>.
    /// <para>
    /// Resolved into <see cref="Histogram{T}"/> the arithmetic is the element's own: subtracting the
    /// reference in <c>T</c> WRAPS at the element width, which is exactly what the
    /// 64-bit form's mask was doing by hand, and <c>LeadingZeroCount</c> counts inside that width,
    /// so the bit length is the same number. The histograms are therefore identical to the ones this
    /// replaces, and so is every plan and every byte.
    /// </para>
    /// <para>
    /// Where it will go next: docs/11-write-strategy.md §3.2 puts <c>bits_raw</c> and
    /// <c>bits_zigzag</c> in the ingest pass, so this walk disappears rather than merely becoming
    /// cheap. That move also restructures the candidate set — frame of reference cannot be
    /// histogrammed before the minimum is known, so §3.2.3 prices it by bound and sweeps only the
    /// contested blocks — which is stage 3's work, and it can move bytes. Typing the walk cannot.
    /// </para>
    /// </remarks>
    /// <param name="values">The column's value buffer.</param>
    /// <param name="raw">The element's type read as unsigned.</param>
    /// <param name="mask">The column's validity.</param>
    /// <param name="length">How many rows.</param>
    /// <param name="minimum">The frame of reference, as the element width's raw bits.</param>
    /// <param name="signed">Whether the zigzag histogram is wanted.</param>
    /// <param name="frames">Receives the widths of <c>v - minimum</c>.</param>
    /// <param name="zigzags">Receives the widths of <c>zigzag(v)</c>.</param>
    private static void Histogram(
        ReadOnlySpan<byte> values, PType raw, in ValidityMask mask, int length, ulong minimum,
        bool signed, Span<int> frames, Span<int> zigzags)
    {
        switch (raw)
        {
            case PType.U8:
                Histogram<byte>(values, in mask, length, minimum, signed, frames, zigzags);
                return;
            case PType.U16:
                Histogram<ushort>(values, in mask, length, minimum, signed, frames, zigzags);
                return;
            case PType.U32:
                Histogram<uint>(values, in mask, length, minimum, signed, frames, zigzags);
                return;
            default:
                Histogram<ulong>(values, in mask, length, minimum, signed, frames, zigzags);
                return;
        }
    }

    /// <summary>One element width's histograms.</summary>
    /// <remarks>
    /// The all-valid case is a separate loop rather than a test inside one, because a validity test
    /// per row is the other half of what W-33 measured: the branch is the same shape as the load it
    /// guards, and hoisting it is what lets the tight loop stay tight.
    /// </remarks>
    /// <typeparam name="T">The element read as an unsigned word.</typeparam>
    /// <param name="bytes">The column's value buffer.</param>
    /// <param name="mask">The column's validity.</param>
    /// <param name="length">How many rows.</param>
    /// <param name="minimum">The frame of reference, as the element width's raw bits.</param>
    /// <param name="signed">Whether the zigzag histogram is wanted.</param>
    /// <param name="frames">Receives the widths of <c>v - minimum</c>.</param>
    /// <param name="zigzags">Receives the widths of <c>zigzag(v)</c>.</param>
    private static void Histogram<T>(
        ReadOnlySpan<byte> bytes, in ValidityMask mask, int length, ulong minimum, bool signed,
        Span<int> frames, Span<int> zigzags)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> values = MemoryMarshal.Cast<byte, T>(bytes).Slice(0, length);
        T reference = T.CreateTruncating(minimum);
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int shift = elementBits - 1;

        if (mask.AllValid)
        {
            for (int row = 0; row < values.Length; row++)
            {
                T value = values[row];
                frames[Width(unchecked(value - reference), elementBits)]++;
                if (signed)
                {
                    zigzags[Width(ZigZag(value, shift), elementBits)]++;
                }
            }

            return;
        }

        for (int row = 0; row < values.Length; row++)
        {
            if (!mask.IsValid(row))
            {
                // A null row is PACKED as zero, and that is a different statement from "its value
                // is zero": under a frame of reference the raw zero would encode as `-reference`,
                // 64 bits wide, and every null in the column would count as an exception. Writing
                // it as a raw zero here and letting the transform run cost 37 kB on
                // `containers/zoned_many_zones_nulls` before the histogram was read against what
                // Pack actually writes.
                //
                // THE ZIGZAG SIDE IS GUARDED like the value path below it, because the caller may
                // already hold that half from the ingest pass and be here only for the framed one;
                // an unconditional increment would count every null twice.
                frames[0]++;
                if (signed)
                {
                    zigzags[0]++;
                }

                continue;
            }

            T value = values[row];
            frames[Width(unchecked(value - reference), elementBits)]++;
            if (signed)
            {
                zigzags[Width(ZigZag(value, shift), elementBits)]++;
            }
        }
    }

    /// <summary>How many bits <paramref name="value"/> needs, inside its own element width.</summary>
    /// <typeparam name="T">The element read as an unsigned word.</typeparam>
    /// <param name="value">The encoded value.</param>
    /// <param name="elementBits">8, 16, 32 or 64.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Width<T>(T value, int elementBits)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> =>
        elementBits - int.CreateTruncating(T.LeadingZeroCount(value));

    /// <summary>
    /// <c>(v &lt;&lt; 1) ^ (v &gt;&gt; (bits - 1))</c> in the element's own arithmetic.
    /// </summary>
    /// <remarks>
    /// The same map as the 64-bit <see cref="ZigZag(ulong, int)"/>, with the width mask left to
    /// <typeparamref name="T"/>: shifting left in <typeparamref name="T"/> drops the bit the mask
    /// would have cleared, and <c>T.Zero - (v >> (bits - 1))</c> is all ones or none for the same
    /// reason the unsigned form's <c>0UL - ...</c> is.
    /// </remarks>
    /// <typeparam name="T">The element read as an unsigned word.</typeparam>
    /// <param name="value">The row's value.</param>
    /// <param name="shift">The element width less one.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T ZigZag<T>(T value, int shift)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> =>
        unchecked((value << 1) ^ (T.Zero - (value >> shift)));

    /// <summary>
    /// The cheapest width for one histogram: <c>packed(w) + exceptions(w) * perException</c>.
    /// </summary>
    /// <remarks>
    /// Walked from the WIDEST width down, so a tie is resolved in favour of the wider one - which
    /// is the one with fewer exceptions, and therefore the one that decodes faster for the same
    /// bytes. The padding is charged honestly: FastLanes packs in blocks of 1024 and the last block
    /// is full width whatever it holds, so 100 rows at 40 bits cost 1024 of them.
    /// </remarks>
    private static Best Cheapest(
        ReadOnlySpan<int> histogram, int elementBits, long blocks, long perException,
        BitPackTransform transform)
    {
        // At the element's own width nothing can fail to fit, whatever the histogram says.
        long exceptions = 0;
        long cost = blocks * FastLanes.BlockByteLength(elementBits);
        Best best = new Best(transform, elementBits, cost, 0);

        for (int width = elementBits - 1; width >= 0; width--)
        {
            exceptions += histogram[width + 1];
            long candidate = (blocks * FastLanes.BlockByteLength(width))
                + (exceptions == 0 ? 0 : PatchOverhead + (exceptions * perException));
            if (candidate < best.Cost)
            {
                best = new Best(transform, width, candidate, exceptions);
            }
        }

        return best;
    }

    /// <summary>The minimum over the valid rows, as the element width's unsigned bits.</summary>
    /// <remarks>
    /// Read as SIGNED when the column is signed and unsigned otherwise, because the minimum of
    /// -1 and 1 is -1 under one reading and 1 under the other. The result is then carried as raw
    /// bits, which is what both the subtraction and the serialized reference want.
    /// </remarks>
    private static bool Minimum(
        int length, ValidityMask mask, ReadOnlySpan<byte> values, PType ptype, out ulong reference)
    {
        reference = 0;
        bool any = false;

        if (ptype.IsSignedInteger())
        {
            long minimum = long.MaxValue;
            for (int row = 0; row < length; row++)
            {
                if (!mask.IsValid(row))
                {
                    continue;
                }

                any = true;
                minimum = Math.Min(minimum, CanonicalSupport.ReadInteger(values, ptype, row));
            }

            reference = unchecked((ulong)minimum);
            return any;
        }

        ulong smallest = ulong.MaxValue;
        for (int row = 0; row < length; row++)
        {
            if (!mask.IsValid(row))
            {
                continue;
            }

            any = true;
            smallest = Math.Min(smallest, CompressedValues.ReadUnsigned(values, ptype, row));
        }

        reference = smallest;
        return any;
    }

    private static PType ToUnsigned(PType ptype) => ptype switch
    {
        PType.I8 => PType.U8,
        PType.I16 => PType.U16,
        PType.I32 => PType.U32,
        PType.I64 => PType.U64,
        _ => ptype,
    };

    private readonly struct Best
    {
        internal Best(BitPackTransform transform, int bitWidth, long cost, long exceptions)
        {
            Transform = transform;
            BitWidth = bitWidth;
            Cost = cost;
            Exceptions = exceptions;
        }

        internal BitPackTransform Transform { get; }

        internal int BitWidth { get; }

        internal long Cost { get; }

        /// <summary>
        /// How many valid rows this width cannot hold — the number the cost was charged for.
        /// </summary>
        /// <remarks>
        /// It is carried rather than recounted: <see cref="Cheapest"/> accumulates it to price the
        /// width, and the pack that writes the column sizes its patch arrays from it and finds the
        /// rows as it transforms them (<c>ArrayBlobWriter.Pack</c>). A gather of its own used to
        /// walk the whole column twice — once to count, once to fill — to reach the same rows under
        /// the same transform; both walks are gone, and a width that holds every row — dictionary
        /// codes, run-end ends, varbin offsets — costs the pack nothing beyond the pack.
        /// </remarks>
        internal long Exceptions { get; }
    }
}
