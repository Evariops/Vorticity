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
        BitPackTransform transform, ulong reference, int bitWidth, long cost, int[] indices,
        ulong[] values)
    {
        Transform = transform;
        Reference = reference;
        BitWidth = bitWidth;
        Cost = cost;
        PatchIndices = indices;
        PatchValues = values;
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

    /// <summary>The rows whose encoded value needs more than <see cref="BitWidth"/> bits.</summary>
    internal int[] PatchIndices { get; }

    /// <summary>
    /// Those rows' values, in the ENCODED domain - after the transform, before the packing. That
    /// is the domain the decoder patches in: it overwrites the unpacked buffer and only then hands
    /// the result to <c>fastlanes.for</c> or <c>vortex.zigzag</c>.
    /// </summary>
    internal ulong[] PatchValues { get; }

    /// <summary>
    /// Measures the column and decides whether bit-packing it pays, and how.
    /// </summary>
    /// <param name="arena">The arena holding the node.</param>
    /// <param name="node">The column chunk; must be an integer primitive.</param>
    /// <returns>The plan, or <see langword="null"/> when packing does not pay.</returns>
    internal static BitPackPlan? TryBuild(CanonicalArena arena, CanonicalNode node)
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

        if (!Minimum(length, mask, values, ptype, out ulong reference))
        {
            // Every row is null, or there are no rows: nothing to measure a width against.
            return null;
        }

        // One pass, both histograms. Entry w counts the values needing exactly w bits, so the
        // number of exceptions at width w is the sum of entries above w - which is what the cost
        // loop accumulates from the top down.
        Span<int> frame = stackalloc int[65];
        Span<int> zigzag = stackalloc int[65];
        frame.Clear();
        zigzag.Clear();
        bool signed = ptype.IsSignedInteger();

        for (int row = 0; row < length; row++)
        {
            if (!mask.IsValid(row))
            {
                // A null row is PACKED as zero, and that is a different statement from "its value
                // is zero": under a frame of reference the raw zero would encode as `-reference`,
                // 64 bits wide, and every null in the column would count as an exception. Writing
                // it as a raw zero here and letting the transform run cost 37 kB on
                // `containers/zoned_many_zones_nulls` before the histogram was read against what
                // Pack actually writes.
                frame[0]++;
                zigzag[0]++;
                continue;
            }

            ulong bits = CompressedValues.ReadUnsigned(values, raw, row);
            frame[BitLength(Frame(bits, reference, elementBits))]++;
            if (signed)
            {
                zigzag[BitLength(ZigZag(bits, elementBits))]++;
            }
        }

        long blocks = (length + FastLanes.BlockSize - 1) / FastLanes.BlockSize;
        long perException = ptype.ByteWidth() + FsstPlan.IndexPType(length).ByteWidth();

        Best best = Cheapest(frame, elementBits, blocks, perException, BitPackTransform.Frame);
        if (signed)
        {
            Best other = Cheapest(zigzag, elementBits, blocks, perException, BitPackTransform.ZigZag);
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

        ulong transformReference = best.Transform == BitPackTransform.Frame ? reference : 0;
        (int[] indices, ulong[] patched) = Collect(
            length, mask, values, raw, best, transformReference, elementBits);

        return new BitPackPlan(
            best.Transform, transformReference, best.BitWidth, best.Cost + NodeOverhead,
            indices, patched);
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
        Best best = new Best(transform, elementBits, cost);

        for (int width = elementBits - 1; width >= 0; width--)
        {
            exceptions += histogram[width + 1];
            long candidate = (blocks * FastLanes.BlockByteLength(width))
                + (exceptions == 0 ? 0 : PatchOverhead + (exceptions * perException));
            if (candidate < best.Cost)
            {
                best = new Best(transform, width, candidate);
            }
        }

        return best;
    }

    /// <summary>Gathers the rows the chosen width cannot hold.</summary>
    private static (int[] Indices, ulong[] Values) Collect(
        int length, ValidityMask mask, ReadOnlySpan<byte> values, PType raw, in Best best,
        ulong reference, int elementBits)
    {
        if (best.BitWidth >= elementBits)
        {
            return ([], []);
        }

        ulong limit = 1UL << best.BitWidth;
        int count = 0;
        for (int row = 0; row < length; row++)
        {
            if (mask.IsValid(row)
                && Encode(CompressedValues.ReadUnsigned(values, raw, row), best.Transform, reference, elementBits) >= limit)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return ([], []);
        }

        int[] indices = new int[count];
        ulong[] patched = new ulong[count];
        int at = 0;
        for (int row = 0; row < length; row++)
        {
            if (!mask.IsValid(row))
            {
                continue;
            }

            ulong encoded = Encode(
                CompressedValues.ReadUnsigned(values, raw, row), best.Transform, reference, elementBits);
            if (encoded >= limit)
            {
                indices[at] = row;
                patched[at] = encoded;
                at++;
            }
        }

        return (indices, patched);
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
        internal Best(BitPackTransform transform, int bitWidth, long cost)
        {
            Transform = transform;
            BitWidth = bitWidth;
            Cost = cost;
        }

        internal BitPackTransform Transform { get; }

        internal int BitWidth { get; }

        internal long Cost { get; }
    }
}
