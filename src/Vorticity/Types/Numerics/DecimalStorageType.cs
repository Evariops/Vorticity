// Reimplemented from the Vortex specification.
// Enum values and the precision -> width mapping are transcribed from
// vortex-array-0.86.1/src/dtype/decimal/types.rs (`DecimalType`,
// `DecimalType::smallest_decimal_value_type`, `DecimalType::byte_width`) and from
// spec/METADATA.md ("Enum `DecimalType`: I8=0, I16=1, I32=2, I64=3, I128=4, I256=5").
using System.Runtime.CompilerServices;

namespace Vorticity.Types.Numerics;

/// <summary>
/// The integer width Vortex uses to store a decimal of a given precision. This is the
/// <c>values_type</c> field of <c>vortex.decimal</c>'s metadata
/// (<c>message DecimalMetadata { DecimalType values_type = 1; }</c>, spec/METADATA.md), so the
/// numeric values are wire values and must not be renumbered.
/// </summary>
public enum DecimalStorageType : byte
{
    /// <summary>8-bit storage, selected for precision 1..2.</summary>
    I8 = 0,

    /// <summary>16-bit storage, selected for precision 3..4.</summary>
    I16 = 1,

    /// <summary>32-bit storage, selected for precision 5..9.</summary>
    I32 = 2,

    /// <summary>64-bit storage, selected for precision 10..18.</summary>
    I64 = 3,

    /// <summary>128-bit storage, selected for precision 19..38.</summary>
    I128 = 4,

    /// <summary>256-bit storage, selected for precision 39..76. See <see cref="Int256"/>.</summary>
    I256 = 5,
}

/// <summary>
/// Helpers over <see cref="DecimalStorageType"/>: the precision-to-width mapping, the byte width of
/// each storage type, and the guard a reader applies to a file-supplied value.
/// </summary>
public static class DecimalStorage
{
    /// <summary>Smallest legal decimal precision. docs/07-dotnet-mapping.md §2.</summary>
    public const byte MinPrecision = 1;

    /// <summary>
    /// Largest legal decimal precision: 76, not 38. spec/REFERENCE.md §1 correction 1 and
    /// vortex-array-0.86.1/src/dtype/decimal/mod.rs (<c>MAX_PRECISION = &lt;i256&gt;::MAX_PRECISION</c>).
    /// </summary>
    public const byte MaxPrecision = 76;

    /// <summary>
    /// Maps a precision to the storage width Vortex selects for it:
    /// 1..2 -&gt; <see cref="DecimalStorageType.I8"/>, 3..4 -&gt; <see cref="DecimalStorageType.I16"/>,
    /// 5..9 -&gt; <see cref="DecimalStorageType.I32"/>, 10..18 -&gt; <see cref="DecimalStorageType.I64"/>,
    /// 19..38 -&gt; <see cref="DecimalStorageType.I128"/>, 39..76 -&gt; <see cref="DecimalStorageType.I256"/>.
    /// </summary>
    /// <param name="precision">The decimal precision, 1..76.</param>
    /// <returns>The storage width for <paramref name="precision"/>.</returns>
    /// <exception cref="VortexFormatException"><paramref name="precision"/> is outside 1..76.</exception>
    public static DecimalStorageType ForPrecision(byte precision)
    {
        // vortex-array-0.86.1/src/dtype/decimal/types.rs::smallest_decimal_value_type
        if (precision - 1u > MaxPrecision - 1u)
        {
            ThrowPrecision(precision);
        }

        if (precision <= 2)
        {
            return DecimalStorageType.I8;
        }

        if (precision <= 4)
        {
            return DecimalStorageType.I16;
        }

        if (precision <= 9)
        {
            return DecimalStorageType.I32;
        }

        if (precision <= 18)
        {
            return DecimalStorageType.I64;
        }

        return precision <= 38 ? DecimalStorageType.I128 : DecimalStorageType.I256;
    }

    /// <summary>
    /// The width in bytes of one stored value: 1, 2, 4, 8, 16 or 32.
    /// vortex-array-0.86.1/src/dtype/decimal/types.rs::byte_width.
    /// </summary>
    /// <param name="storage">The storage type.</param>
    /// <returns>The width in bytes.</returns>
    /// <exception cref="VortexFormatException"><paramref name="storage"/> is not a defined value.</exception>
    public static int ByteWidth(DecimalStorageType storage)
    {
        switch (storage)
        {
            case DecimalStorageType.I8: return 1;
            case DecimalStorageType.I16: return 2;
            case DecimalStorageType.I32: return 4;
            case DecimalStorageType.I64: return 8;
            case DecimalStorageType.I128: return 16;
            case DecimalStorageType.I256: return Int256.ByteCount;
            default:
                ThrowStorage(storage);
                return 0;
        }
    }

    /// <summary>
    /// The inverse of <see cref="ByteWidth"/>: the storage type that stores values of
    /// <paramref name="width"/> bytes.
    /// </summary>
    /// <param name="width">One of 1, 2, 4, 8, 16 or 32.</param>
    /// <returns>The storage type of that width.</returns>
    /// <exception cref="VortexFormatException"><paramref name="width"/> is not one of the six.</exception>
    public static DecimalStorageType FromByteWidth(int width)
    {
        switch (width)
        {
            case 1: return DecimalStorageType.I8;
            case 2: return DecimalStorageType.I16;
            case 4: return DecimalStorageType.I32;
            case 8: return DecimalStorageType.I64;
            case 16: return DecimalStorageType.I128;
            case Int256.ByteCount: return DecimalStorageType.I256;
            default:
                ThrowWidth(width);
                return 0;
        }
    }

    /// <summary>
    /// Guards a value read from a file: true only for 0..5. A <c>values_type</c> outside that range
    /// is a domain violation and must be rejected, not skipped (§1.7 of the Phase 1 contract).
    /// This overload sees an already-narrowed <see cref="byte"/>; use
    /// <see cref="IsDefinedWireValue"/> when the value still has its full protobuf width.
    /// </summary>
    /// <param name="storage">The candidate value, typically cast from a protobuf enum field.</param>
    /// <returns><see langword="true"/> when the value is one of the six defined storage types.</returns>
    public static bool IsDefined(DecimalStorageType storage) =>
        (byte)storage <= (byte)DecimalStorageType.I256;

    /// <summary>
    /// Guards a raw wire value before it is narrowed to <see cref="DecimalStorageType"/>. A
    /// protobuf enum field is a varint, so <c>values_type = 256</c> is a legal encoding that would
    /// truncate to <see cref="DecimalStorageType.I8"/> if it were cast to <see cref="byte"/> first
    /// - <see cref="IsDefined"/> cannot see that, because the damage is already done by the time it
    /// is called. Decoders must range-check the varint with this, not with
    /// <see cref="IsDefined"/>.
    /// </summary>
    /// <param name="raw">The value as read from the wire, before any narrowing cast.</param>
    /// <param name="storage">The narrowed storage type, or <see cref="DecimalStorageType.I8"/>
    /// on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="raw"/> is one of 0..5.</returns>
    public static bool IsDefinedWireValue(uint raw, out DecimalStorageType storage)
    {
        if (raw > (uint)DecimalStorageType.I256)
        {
            storage = default;
            return false;
        }

        storage = (DecimalStorageType)(byte)raw;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrecision(byte precision) =>
        throw new VortexFormatException(
            $"Decimal precision {precision} is outside the supported range " +
            $"[{MinPrecision}, {MaxPrecision}].");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWidth(int width) =>
        throw new VortexFormatException(
            $"A decimal storage width of {width} bytes is not one of 1, 2, 4, 8, 16 and 32.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStorage(DecimalStorageType storage) =>
        throw new VortexFormatException(
            $"Decimal storage type {(byte)storage} is not one of the six defined values 0..5.");
}
