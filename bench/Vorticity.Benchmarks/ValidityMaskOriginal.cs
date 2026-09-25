using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>
/// A frozen copy of the row lookup of <c>ValidityMask</c> as it was, a switch on the validity's
/// kind per row. Kept as the baseline of <see cref="ValidityLookupBenchmarks"/>.
/// </summary>
internal readonly ref struct ValidityMaskOriginal
{
    private readonly ReadOnlySpan<byte> _bits;
    private readonly int _bitOffset;
    private readonly ValidityKind _kind;

    private ValidityMaskOriginal(ValidityKind kind, ReadOnlySpan<byte> bits, int bitOffset)
    {
        _kind = kind;
        _bits = bits;
        _bitOffset = bitOffset;
    }

    internal static ValidityMaskOriginal From(CanonicalArena arena, Validity validity)
    {
        if (validity.Kind != ValidityKind.Bitmap)
        {
            return new ValidityMaskOriginal(validity.Kind, default, 0);
        }

        CanonicalNode bits = arena.GetNode(validity.CanonicalNodeIndex);
        return new ValidityMaskOriginal(ValidityKind.Bitmap, bits.Bits.Span, bits.BitOffset);
    }

    internal bool IsValid(int index) => _kind switch
    {
        ValidityKind.NonNullable or ValidityKind.AllValid => true,
        ValidityKind.AllInvalid => false,
        _ => CanonicalSupport.BitAt(_bits, _bitOffset + index),
    };
}
