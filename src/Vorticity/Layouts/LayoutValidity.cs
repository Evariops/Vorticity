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

        return Classify(bits.Bits.Span, bits.BitOffset, length) switch
        {
            BitmapShape.AllClear => Validity.AllInvalid,
            BitmapShape.AllSet => Validity.AllValid,
            _ => Validity.Bitmap(canonicalIndex),
        };
    }

    private enum BitmapShape : byte
    {
        Mixed = 0,
        AllSet = 1,
        AllClear = 2,
    }

    /// <summary>Byte-at-a-time classification with masked ends; stops as soon as both are ruled out.</summary>
    private static BitmapShape Classify(ReadOnlySpan<byte> bits, int bitOffset, int length)
    {
        int firstBit = bitOffset;
        int lastBit = bitOffset + length - 1;
        int firstByte = firstBit >> 3;
        int lastByte = lastBit >> 3;

        bool allSet = true;
        bool allClear = true;

        for (int b = firstByte; b <= lastByte; b++)
        {
            int lo = b == firstByte ? (firstBit & 7) : 0;
            int hi = b == lastByte ? (lastBit & 7) : 7;
            int mask = ((1 << (hi - lo + 1)) - 1) << lo;
            int value = bits[b] & mask;

            if (value != mask)
            {
                allSet = false;
            }

            if (value != 0)
            {
                allClear = false;
            }

            if (!allSet && !allClear)
            {
                return BitmapShape.Mixed;
            }
        }

        return allSet ? BitmapShape.AllSet : BitmapShape.AllClear;
    }
}
