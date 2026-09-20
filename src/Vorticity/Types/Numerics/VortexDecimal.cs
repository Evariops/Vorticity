using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Vorticity.Types.Numerics;

/// <summary>
/// One decimal value: the unscaled integer plus the DType's precision and scale. Precision runs to
/// 76 and the scale spans a whole <see cref="sbyte"/>, a range <see cref="decimal"/> cannot carry
/// without silent loss, so this type is never lossy and conversion to <see cref="decimal"/> is
/// explicit and fallible.
/// </summary>
/// <remarks>
/// The value is <c>Unscaled * 10^-Scale</c>. A negative scale is legal and means trailing zeros:
/// unscaled 123 at scale -2 is 12300.
/// </remarks>
public readonly struct VortexDecimal : IEquatable<VortexDecimal>, IComparable<VortexDecimal>
{
    /// <summary>
    /// Maximum number of characters <see cref="TryFormat"/> can write: a sign, up to
    /// <see cref="Int256.MaxDigitCount"/> (77) digits and, at the most negative scale an
    /// <see cref="sbyte"/> can hold, 128 appended zeros.
    /// </summary>
    public const int MaxFormattedLength = 1 + Int256.MaxDigitCount + 128;

    private readonly Int256 _unscaled;
    private readonly byte _precision;
    private readonly sbyte _scale;

    /// <summary>Creates a decimal from an unscaled i256 and the DType's precision and scale.</summary>
    /// <param name="unscaled">The unscaled integer.</param>
    /// <param name="precision">The DType's precision, 1..76.</param>
    /// <param name="scale">
    /// The DType's scale. Any <see cref="sbyte"/> renders exactly; the DType-level bounds
    /// (<c>scale &lt;= 76</c>, and <c>scale &lt;= precision</c> when positive) are enforced where
    /// the DType is parsed, not here.
    /// </param>
    /// <exception cref="VortexFormatException"><paramref name="precision"/> is outside 1..76.</exception>
    public VortexDecimal(Int256 unscaled, byte precision, sbyte scale)
    {
        // Validating precision here keeps Storage total for every constructed value; the only way
        // to obtain an out-of-range precision is `default(VortexDecimal)`.
        _ = DecimalStorage.ForPrecision(precision);
        _unscaled = unscaled;
        _precision = precision;
        _scale = scale;
    }

    /// <summary>Creates a decimal whose unscaled value fits <see cref="Int128"/>.</summary>
    /// <param name="unscaled">The unscaled integer.</param>
    /// <param name="precision">The DType's precision, 1..76.</param>
    /// <param name="scale">The DType's scale.</param>
    /// <returns>The decimal.</returns>
    /// <exception cref="VortexFormatException"><paramref name="precision"/> is outside 1..76.</exception>
    public static VortexDecimal FromInt128(Int128 unscaled, byte precision, sbyte scale) =>
        new VortexDecimal(new Int256(unscaled), precision, scale);

    /// <summary>Creates a decimal whose unscaled value fits <see cref="long"/>.</summary>
    /// <param name="unscaled">The unscaled integer.</param>
    /// <param name="precision">The DType's precision, 1..76.</param>
    /// <param name="scale">The DType's scale.</param>
    /// <returns>The decimal.</returns>
    /// <exception cref="VortexFormatException"><paramref name="precision"/> is outside 1..76.</exception>
    public static VortexDecimal FromInt64(long unscaled, byte precision, sbyte scale) =>
        new VortexDecimal(new Int256(unscaled), precision, scale);

    /// <summary>The unscaled integer.</summary>
    public Int256 Unscaled => _unscaled;

    /// <summary>The DType's precision, 1..76.</summary>
    public byte Precision => _precision;

    /// <summary>The DType's scale; the value is <c>Unscaled * 10^-Scale</c>.</summary>
    public sbyte Scale => _scale;

    /// <summary>
    /// The storage width Vortex uses for this precision. Derived, not stored:
    /// <see cref="DecimalStorage.ForPrecision"/>.
    /// </summary>
    /// <exception cref="VortexFormatException">
    /// The precision is outside 1..76, which is only reachable through <c>default</c>.
    /// </exception>
    public DecimalStorageType Storage => DecimalStorage.ForPrecision(_precision);

    /// <summary><see langword="true"/> when the unscaled value is negative.</summary>
    public bool IsNegative => _unscaled.IsNegative;

    /// <summary>Converts to <see cref="decimal"/>, applying the scale exactly.</summary>
    /// <returns>The converted value.</returns>
    /// <exception cref="OverflowException">
    /// The value is outside <see cref="decimal"/>'s range, or the scale is outside -28..28.
    /// </exception>
    public decimal ToDecimal()
    {
        if (!TryToDecimal(out decimal value))
        {
            ThrowDecimalOverflow();
        }

        return value;
    }

    /// <summary>
    /// Converts to <see cref="decimal"/>, applying the scale exactly and never throwing.
    /// </summary>
    /// <param name="value">The converted value, or zero on failure.</param>
    /// <returns>
    /// <see langword="false"/> when the scale is outside <see cref="decimal"/>'s own -28..28 range,
    /// or when the scaled magnitude does not fit its 96-bit unscaled range.
    /// </returns>
    public bool TryToDecimal(out decimal value)
    {
        value = 0m;

        // decimal's scale is 0..28. A negative Vortex scale is applied by multiplying, so it is
        // bounded by the same 28 in the other direction.
        if (_scale > 28 || _scale < -28)
        {
            return false;
        }

        _unscaled.GetMagnitude(out ulong m0, out ulong m1, out ulong m2, out ulong m3);

        if (_scale < 0)
        {
            int zeros = -(int)_scale;
            for (int i = 0; i < zeros; i++)
            {
                if (!Int256.TryMultiplyMagnitudeByTen(ref m0, ref m1, ref m2, ref m3))
                {
                    return false;
                }
            }
        }

        if (!Int256.FitsIn96Bits(m1, m2, m3))
        {
            return false;
        }

        byte decimalScale = _scale > 0 ? (byte)_scale : (byte)0;
        value = Int256.MakeDecimal(m0, m1, _unscaled.IsNegative, decimalScale);
        return true;
    }

    /// <summary>Narrows the unscaled value to <see cref="Int128"/>.</summary>
    /// <param name="value">The unscaled value, or zero on failure.</param>
    /// <returns><see langword="false"/> when the unscaled value does not fit 128 bits.</returns>
    public bool TryToInt128(out Int128 value) => _unscaled.TryToInt128(out value);

    /// <summary>
    /// Exact and culture-invariant. A positive scale inserts a point and left-pads with zeros; a
    /// scale of zero renders the unscaled digits; a negative scale appends <c>-scale</c> zeros,
    /// except for zero itself, which renders <c>0</c>.
    /// </summary>
    /// <returns>The rendering.</returns>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[MaxFormattedLength];
        if (!TryFormat(buffer, out int written))
        {
            // Unreachable: MaxFormattedLength covers a sign, 77 digits and the 128 zeros the most
            // negative sbyte scale appends.
            throw new UnreachableException();
        }

        return new string(buffer.Slice(0, written));
    }

    /// <summary>Allocation-free rendering; see <see cref="ToString()"/>.</summary>
    /// <param name="destination">
    /// Receives the rendering; needs at most <see cref="MaxFormattedLength"/> chars.
    /// </param>
    /// <param name="charsWritten">Characters written, or 0 when the destination was too small.</param>
    /// <returns><see langword="true"/> when the value fit.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;

        Span<char> digits = stackalloc char[Int256.MaxDigitCount];
        int digitCount = _unscaled.FormatMagnitudeDigits(digits);
        ReadOnlySpan<char> magnitude = digits.Slice(Int256.MaxDigitCount - digitCount, digitCount);
        bool negative = _unscaled.IsNegative;
        bool isZero = _unscaled.IsZero;
        int scale = _scale;

        if (scale <= 0)
        {
            // Zero with a negative scale renders "0", not padded zeros: it is the one case where
            // "append -scale zeros" is wrong.
            int zeros = isZero ? 0 : -scale;
            int total = (negative ? 1 : 0) + digitCount + zeros;
            if (destination.Length < total)
            {
                return false;
            }

            int at = 0;
            if (negative)
            {
                destination[at++] = '-';
            }

            magnitude.CopyTo(destination.Slice(at));
            at += digitCount;
            destination.Slice(at, zeros).Fill('0');
            charsWritten = total;
            return true;
        }

        if (digitCount > scale)
        {
            int total = (negative ? 1 : 0) + digitCount + 1;
            if (destination.Length < total)
            {
                return false;
            }

            int at = 0;
            if (negative)
            {
                destination[at++] = '-';
            }

            int whole = digitCount - scale;
            magnitude.Slice(0, whole).CopyTo(destination.Slice(at));
            at += whole;
            destination[at++] = '.';
            magnitude.Slice(whole).CopyTo(destination.Slice(at));
            charsWritten = total;
            return true;
        }

        // digitCount <= scale: "0." then (scale - digitCount) zeros then the digits.
        {
            int total = (negative ? 1 : 0) + 2 + scale;
            if (destination.Length < total)
            {
                return false;
            }

            int at = 0;
            if (negative)
            {
                destination[at++] = '-';
            }

            destination[at++] = '0';
            destination[at++] = '.';
            int pad = scale - digitCount;
            destination.Slice(at, pad).Fill('0');
            at += pad;
            magnitude.CopyTo(destination.Slice(at));
            charsWritten = total;
            return true;
        }
    }

    /// <summary>
    /// Compares numerically. Two values with different scales are compared by value, which requires
    /// scaling one of them; when that scaling overflows 256 unsigned bits the other value is
    /// provably smaller in magnitude, so the comparison is still exact and never throws.
    /// </summary>
    /// <param name="other">The value to compare against.</param>
    /// <returns>Negative, zero or positive.</returns>
    public int CompareTo(VortexDecimal other)
    {
        int signA = _unscaled.Sign;
        int signB = other._unscaled.Sign;
        if (signA != signB)
        {
            return signA < signB ? -1 : 1;
        }

        if (signA == 0)
        {
            return 0;
        }

        if (_scale == other._scale)
        {
            return _unscaled.CompareTo(other._unscaled);
        }

        _unscaled.GetMagnitude(out ulong a0, out ulong a1, out ulong a2, out ulong a3);
        other._unscaled.GetMagnitude(out ulong b0, out ulong b1, out ulong b2, out ulong b3);

        // value = unscaled * 10^-scale, so the operand with the smaller scale is the one that must
        // be multiplied up to the common scale max(this, other).
        int diff = _scale - other._scale;
        int magnitudeOrder;
        if (diff > 0)
        {
            magnitudeOrder = ScaleUp(ref b0, ref b1, ref b2, ref b3, diff)
                ? Int256.CompareMagnitudes(a0, a1, a2, a3, b0, b1, b2, b3)
                : -1;
        }
        else
        {
            magnitudeOrder = ScaleUp(ref a0, ref a1, ref a2, ref a3, -diff)
                ? Int256.CompareMagnitudes(a0, a1, a2, a3, b0, b1, b2, b3)
                : 1;
        }

        return signA > 0 ? magnitudeOrder : -magnitudeOrder;
    }

    /// <summary>
    /// Structural equality: equal unscaled value <em>and</em> equal precision <em>and</em> equal
    /// scale. This is deliberately not numeric equality - <c>decimal(4,2) 100</c> and
    /// <c>decimal(4,1) 10</c> are both 1.00 and are not <see cref="Equals(VortexDecimal)"/>.
    /// Use <see cref="CompareTo"/> for a numeric comparison.
    /// </summary>
    /// <param name="other">The value to compare against.</param>
    /// <returns><see langword="true"/> when all three components are equal.</returns>
    public bool Equals(VortexDecimal other) =>
        _precision == other._precision && _scale == other._scale &&
        _unscaled.Equals(other._unscaled);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VortexDecimal other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_unscaled, _precision, _scale);

    private static bool ScaleUp(
        ref ulong m0, ref ulong m1, ref ulong m2, ref ulong m3, int power)
    {
        for (int i = 0; i < power; i++)
        {
            if (!Int256.TryMultiplyMagnitudeByTen(ref m0, ref m1, ref m2, ref m3))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDecimalOverflow() =>
        throw new OverflowException(
            "The decimal value is outside System.Decimal's range: its scale must be within -28..28 " +
            "and its scaled magnitude below 2^96.");
}
