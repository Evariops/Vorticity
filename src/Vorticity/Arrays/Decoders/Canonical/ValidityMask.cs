using System;
using System.Runtime.CompilerServices;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Canonical;

/// <summary>
/// Row-level validity lookup over a <see cref="Validity"/> and its bitmap, if any: a read-only view
/// of the four validity states, so a caller can ask whether a row holds a value without branching
/// on the kind per row and without materializing a bitmap for the three uniform kinds. A validation
/// pass needs it because a null slot's payload is garbage by construction, and reading it would be
/// exactly the out-of-bounds access this library promises never to perform.
/// </summary>
/// <remarks>
/// A row's answer is one bit read whatever the kind, rather than a switch on the kind at every row:
/// a uniform kind reads bit 0 of a one-byte bitmap of its own, every row's index masked to zero.
/// </remarks>
internal readonly ref struct ValidityMask
{
    private readonly ReadOnlySpan<byte> _lookup;
    private readonly int _bitOffset;
    private readonly int _rowMask;
    private readonly ValidityKind _kind;

    private ValidityMask(ValidityKind kind, ReadOnlySpan<byte> bits, int bitOffset)
    {
        _kind = kind;
        _bitOffset = bitOffset;
        _rowMask = kind == ValidityKind.Bitmap ? -1 : 0;
        _lookup = kind switch
        {
            ValidityKind.Bitmap => bits,
            ValidityKind.AllInvalid => Nothing,
            _ => Everything,
        };
    }

    private static ReadOnlySpan<byte> Everything => [0x01];

    private static ReadOnlySpan<byte> Nothing => [0x00];

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

    /// <summary>
    /// The mask of a bitmap held outside an arena: a Parquet page's validity, decoded from its
    /// levels before the node it will belong to exists.
    /// </summary>
    /// <param name="bits">The bitmap, least significant bit first.</param>
    /// <param name="bitOffset">The bit row 0 sits at.</param>
    internal static ValidityMask Bitmap(ReadOnlySpan<byte> bits, int bitOffset) => new(ValidityKind.Bitmap, bits, bitOffset);

    /// <summary><see langword="true"/> when no row is null; callers can then skip the per-row test.</summary>
    internal bool AllValid => _kind is ValidityKind.NonNullable or ValidityKind.AllValid;

    /// <summary><see langword="true"/> when every row is null.</summary>
    internal bool AllInvalid => _kind == ValidityKind.AllInvalid;

    /// <summary>
    /// The backing bits, for a caller that has ruled out the two uniform kinds and wants to read a
    /// range of them through <see cref="BitmapKernels"/> rather than a row at a time.
    /// </summary>
    internal ReadOnlySpan<byte> Bits => _kind == ValidityKind.Bitmap ? _lookup : default;

    /// <summary>The bit this mask's row 0 sits at.</summary>
    internal int BitOffset => _bitOffset;

    /// <summary>Whether row <paramref name="index"/> holds a value.</summary>
    /// <param name="index">Row index; the caller has already bounds-checked it against the length.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsValid(int index)
    {
        int at = (_bitOffset + index) & _rowMask;
        return ((_lookup[at >> 3] >> (at & 7)) & 1) != 0;
    }
}
