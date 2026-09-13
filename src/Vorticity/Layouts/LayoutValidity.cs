// The struct layout's validity child, turned into a Validity.
//
// ArrayDecodeContext.DecodeValidity (contract §2.6) does this for an ARRAY node: it decodes the
// child, checks it is a Bool of the right length, and collapses a whole-array constant to AllValid
// or AllInvalid. A struct LAYOUT's validity arrives as a layout child instead of an array child, so
// the decode step is different and the checks and the collapse are the same. Rule 3 of §2.6 makes
// the collapse mandatory, not an optimisation: docs/07-dotnet-mapping.md §1 exposes ValidityKind so
// callers can skip per-row checks.
using System;

using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Layouts;

internal static class LayoutValidity
{
    /// <summary>Turns a decoded validity child into a <see cref="Validity"/>.</summary>
    /// <param name="context">The scan holding the canonical arena.</param>
    /// <param name="canonicalIndex">The decoded child.</param>
    /// <param name="length">The parent's row count; the child must match it.</param>
    /// <param name="encodingId">The layout asking, for the error message.</param>
    /// <returns>
    /// <see cref="ValidityKind.AllValid"/> or <see cref="ValidityKind.AllInvalid"/> when the bitmap
    /// is constant, otherwise <see cref="Validity.Bitmap"/>.
    /// </returns>
    /// <exception cref="VortexFormatException">The child is not a Bool of exactly <paramref name="length"/> rows.</exception>
    internal static Validity FromChild(ScanContext context, int canonicalIndex, int length, string encodingId)
    {
        CanonicalNode bits = context.Canonical.GetNode(canonicalIndex);
        if (bits.Kind != CanonicalKind.Bool)
        {
            LayoutsThrow.Format($"A {encodingId} layout's validity child decoded to {bits.Kind}, not Bool.");
        }

        if (bits.Length != length)
        {
            LayoutsThrow.Format(
                $"A {encodingId} layout's validity child covers {bits.Length} rows; the layout covers {length}.");
        }

        if (length == 0)
        {
            return Validity.AllValid;
        }

        return Classify(bits.Bits.Span, bits.BitOffset, length, canonicalIndex);
    }

    /// <summary>
    /// The same classification <c>ArrayDecodeContext.ClassifyValidityBits</c> does, through the
    /// same kernel.
    /// </summary>
    /// <remarks>
    /// This was its own byte-at-a-time loop with an <c>b == firstByte</c> and a <c>b == lastByte</c>
    /// test inside it -- a second copy of the code that, vectorized, turned out to be the largest
    /// single measured gain of the whole audit (`masked_*` 1.32 to 0.49; PERF-AUDIT §1.9). Two
    /// implementations of one question is how one of them stays slow.
    ///
    /// <paramref name="length"/> is at least 1: the caller returns <c>AllValid</c> for a zero-row
    /// layout before reaching here, which is what the kernel's masked ends assume.
    /// </remarks>
    /// <param name="bits">The validity bitmap.</param>
    /// <param name="bitOffset">The bit the layout's row 0 sits at.</param>
    /// <param name="length">Row count, strictly positive.</param>
    /// <param name="canonicalIndex">The bitmap's node, carried by a mixed result.</param>
    private static Validity Classify(ReadOnlySpan<byte> bits, int bitOffset, int length, int canonicalIndex)
    {
        Arrays.Decoders.Canonical.BitmapKernels.Classify(
            bits, bitOffset, length, out bool anySet, out bool anyClear);

        return (anySet, anyClear) switch
        {
            (true, true) => Validity.Bitmap(canonicalIndex),
            (true, false) => Validity.AllValid,
            _ => Validity.AllInvalid,
        };
    }
}
