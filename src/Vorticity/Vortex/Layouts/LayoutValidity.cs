using System;

using Vorticity.Arrays;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// Turns a struct layout's validity child into a <see cref="Validity"/>. The child arrives as a
/// layout child rather than an array child, but the checks and the collapse match what array
/// decoding does. Collapsing a constant bitmap to <see cref="ValidityKind.AllValid"/> or
/// <see cref="ValidityKind.AllInvalid"/> is part of the contract, not an optimisation: callers
/// read the kind to skip per-row checks.
/// </summary>
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
    /// Sharing the vectorized kernel rather than keeping a second byte-at-a-time loop here is what
    /// keeps both callers fast: two implementations of one question is how one of them stays slow.
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
