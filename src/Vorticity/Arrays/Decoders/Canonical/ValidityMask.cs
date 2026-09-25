using System;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Row-level validity lookup over a <see cref="Validity"/> and its bitmap, if any: a read-only view
/// of the four validity states, so a caller can ask whether a row holds a value without branching
/// on the kind per row and without materializing a bitmap for the three uniform kinds. A validation
/// pass needs it because a null slot's payload is garbage by construction, and reading it would be
/// exactly the out-of-bounds access this library promises never to perform.
/// </summary>
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

    /// <summary>The mask of a column without nulls.</summary>
    internal static ValidityMask NonNullable => new(ValidityKind.NonNullable, default, 0);

    /// <summary><see langword="true"/> when no row is null; callers can then skip the per-row test.</summary>
    internal bool AllValid => _kind is ValidityKind.NonNullable or ValidityKind.AllValid;

    /// <summary><see langword="true"/> when every row is null.</summary>
    internal bool AllInvalid => _kind == ValidityKind.AllInvalid;

    /// <summary>
    /// The backing bits, for a caller that has ruled out the two uniform kinds and wants to read a
    /// range of them through <see cref="BitmapKernels"/> rather than a row at a time.
    /// </summary>
    internal ReadOnlySpan<byte> Bits => _bits;

    /// <summary>The bit this mask's row 0 sits at.</summary>
    internal int BitOffset => _bitOffset;

    /// <summary>Whether row <paramref name="index"/> holds a value.</summary>
    /// <param name="index">Row index; the caller has already bounds-checked it against the length.</param>
    internal bool IsValid(int index) => _kind switch
    {
        ValidityKind.NonNullable or ValidityKind.AllValid => true,
        ValidityKind.AllInvalid => false,
        _ => CanonicalSupport.BitAt(_bits, _bitOffset + index),
    };
}
