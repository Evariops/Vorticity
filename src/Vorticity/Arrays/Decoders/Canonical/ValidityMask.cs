// A read-only view over the four validity states of Phase 1 contract §2.6, so a decoder can ask
// "is row i valid?" without branching on ValidityKind at every row and without materializing a
// bitmap for the three constant cases.
//
// It exists because upstream validates only the VALID entries of a VarBinView
// (vortex-array-0.86.1/src/arrays/varbinview/array.rs `validate`): a null slot's view is garbage by
// construction and dereferencing it is exactly the out-of-bounds read this library promises never
// to perform.
using System;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>Row-level validity lookup over a <see cref="Validity"/> and its bitmap, if any.</summary>
internal readonly ref struct ValidityMask
{
    private readonly ReadOnlySpan<byte> _bits;
    private readonly int _bitOffset;
    private readonly ValidityKind _kind;

    private ValidityMask(ValidityKind kind, ReadOnlySpan<byte> bits, int bitOffset)
    {
        _kind = kind;
        _bits = bits;
        _bitOffset = bitOffset;
    }

    /// <summary>Resolves <paramref name="validity"/> against the batch's canonical arena.</summary>
    /// <param name="context">The decode context owning the arena.</param>
    /// <param name="validity">The validity to read.</param>
    internal static ValidityMask From(ArrayDecodeContext context, Validity validity) =>
        From(context.Canonical, validity);

    /// <summary>
    /// Resolves <paramref name="validity"/> against an arena directly, for a caller that holds one
    /// without a decode context - the compute layer, which runs after decoding is finished.
    /// </summary>
    /// <param name="arena">The arena the validity's bitmap node lives in.</param>
    /// <param name="validity">The validity to read.</param>
    internal static ValidityMask From(CanonicalArena arena, Validity validity)
    {
        if (validity.Kind != ValidityKind.Bitmap)
        {
            return new ValidityMask(validity.Kind, default, 0);
        }

        CanonicalNode bits = arena.GetNode(validity.CanonicalNodeIndex);
        return new ValidityMask(ValidityKind.Bitmap, bits.Bits.Span, bits.BitOffset);
    }

    /// <summary><see langword="true"/> when no row is null; callers can then skip the per-row test.</summary>
    internal bool AllValid => _kind is ValidityKind.NonNullable or ValidityKind.AllValid;

    /// <summary><see langword="true"/> when every row is null.</summary>
    internal bool AllInvalid => _kind == ValidityKind.AllInvalid;

    /// <summary>Whether row <paramref name="index"/> holds a value.</summary>
    /// <param name="index">Row index; the caller has already bounds-checked it against the length.</param>
    internal bool IsValid(int index) => _kind switch
    {
        ValidityKind.NonNullable or ValidityKind.AllValid => true,
        ValidityKind.AllInvalid => false,
        _ => CanonicalSupport.BitAt(_bits, _bitOffset + index),
    };
}
