using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Vorticity.Types.Numerics;

/// <summary>
/// A 256-bit signed two's-complement integer, exactly as wide as Vortex's i256 decimal storage.
/// Decimal precision runs beyond what <see cref="Int128"/> holds, so a column such as
/// decimal(40,10) cannot be read without this type. It carries, compares and renders such a value
/// and is deliberately not a general arithmetic type: the only arithmetic here is the private
/// unsigned division by a power of ten that exact rendering needs.
/// </summary>
/// <remarks>
/// The wire representation is little-endian two's complement, and <c>vortex.decimal</c>'s value
/// buffer is a dense array of those 32-byte values. The big-endian accessors exist for callers that
/// need the opposite order, such as <c>vortex.uuid</c>-style storage and the row encoder; reading
/// an i256 out of a file never goes through them.
/// </remarks>
public readonly struct Int256 : IEquatable<Int256>, IComparable<Int256>
{
    /// <summary>Number of bytes in the two's-complement representation. Always 32.</summary>
    public const int ByteCount = 32;

    /// <summary>
    /// Maximum number of decimal digits, excluding a sign: both 2^255-1 and 2^255 have 77 digits.
    /// </summary>
    public const int MaxDigitCount = 77;

    /// <summary>
    /// Maximum number of characters <see cref="TryFormat"/> can write: 77 digits plus a minus sign.
    /// </summary>
    public const int MaxFormattedLength = MaxDigitCount + 1;

    /// <summary>The largest power of ten below 2^64, used as the rendering radix.</summary>
    private const ulong Pow10Chunk = 10_000_000_000_000_000_000UL;

    /// <summary>Decimal digits produced by one <see cref="Pow10Chunk"/> division.</summary>
    private const int Pow10ChunkDigits = 19;

    private const ulong SignBit = 0x8000_0000_0000_0000UL;

    // Little-endian limb order: _l0 is least significant, _l3 carries the sign bit.
    private readonly ulong _l0;
    private readonly ulong _l1;
    private readonly ulong _l2;
    private readonly ulong _l3;

    private Int256(ulong l0, ulong l1, ulong l2, ulong l3)
    {
        _l0 = l0;
        _l1 = l1;
        _l2 = l2;
        _l3 = l3;
    }

    /// <summary>Creates a value from a signed 64-bit integer, sign-extending it.</summary>
    /// <param name="value">The value to widen.</param>
    public Int256(long value)
    {
        ulong extend = value < 0 ? ulong.MaxValue : 0UL;
        _l0 = (ulong)value;
        _l1 = extend;
        _l2 = extend;
        _l3 = extend;
    }

    /// <summary>Creates a value from a signed 128-bit integer, sign-extending it.</summary>
    /// <param name="value">The value to widen.</param>
    public Int256(Int128 value)
    {
        ulong lower = (ulong)value;
        ulong upper = (ulong)(value >>> 64);
        ulong extend = (upper & SignBit) != 0 ? ulong.MaxValue : 0UL;
        _l0 = lower;
        _l1 = upper;
        _l2 = extend;
        _l3 = extend;
    }

    /// <summary>Zero.</summary>
    public static Int256 Zero => default;

    /// <summary>One.</summary>
    public static Int256 One => new Int256(1UL, 0UL, 0UL, 0UL);

    /// <summary>
    /// -2^255, the one value whose magnitude has no positive counterpart. <see cref="Negate"/>
    /// throws on it; every other path here handles it by working on the unsigned magnitude.
    /// </summary>
    public static Int256 MinValue => new Int256(0UL, 0UL, 0UL, SignBit);

    /// <summary>2^255 - 1.</summary>
    public static Int256 MaxValue =>
        new Int256(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ~SignBit);

    /// <summary>Creates a value from an unsigned 64-bit integer, zero-extending it.</summary>
    /// <param name="value">The value to widen.</param>
    /// <returns>The widened value; always non-negative.</returns>
    public static Int256 FromUInt64(ulong value) => new Int256(value, 0UL, 0UL, 0UL);

    /// <summary>
    /// Composes a value from its four 64-bit limbs, least significant first. Exposed so a decoder
    /// can build one from a span without a copy.
    /// </summary>
    /// <param name="lo0">Bits 0..64, least significant.</param>
    /// <param name="lo1">Bits 64..128.</param>
    /// <param name="lo2">Bits 128..192.</param>
    /// <param name="hi">Bits 192..256; its most significant bit is the sign bit.</param>
    /// <returns>The composed value.</returns>
    public static Int256 FromLimbs(ulong lo0, ulong lo1, ulong lo2, ulong hi) =>
        new Int256(lo0, lo1, lo2, hi);

    /// <summary>
    /// Reads exactly 32 bytes of little-endian two's complement, which is the order an i256 takes
    /// on the wire.
    /// </summary>
    /// <param name="bytes">Exactly 32 bytes.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="VortexFormatException"><paramref name="bytes"/> is not 32 bytes long.</exception>
    public static Int256 FromLittleEndianBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount)
        {
            ThrowByteCount(bytes.Length);
        }

        return new Int256(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)));
    }

    /// <summary>Reads exactly 32 bytes of big-endian two's complement.</summary>
    /// <param name="bytes">Exactly 32 bytes, most significant first.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="VortexFormatException"><paramref name="bytes"/> is not 32 bytes long.</exception>
    public static Int256 FromBigEndianBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount)
        {
            ThrowByteCount(bytes.Length);
        }

        // Written out rather than delegating to the little-endian reader over a reversed span: the
        // two orders stay independent implementations, so a mistake in one cannot hide in the other.
        return new Int256(
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(24)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(16)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }

    /// <summary>Non-throwing form of <see cref="FromLittleEndianBytes"/>.</summary>
    /// <param name="bytes">Candidate bytes; only a length of exactly 32 succeeds.</param>
    /// <param name="value">The decoded value, or <see cref="Zero"/> on failure.</param>
    /// <returns><see langword="true"/> when the span was exactly 32 bytes.</returns>
    public static bool TryFromLittleEndianBytes(ReadOnlySpan<byte> bytes, out Int256 value)
    {
        if (bytes.Length != ByteCount)
        {
            value = default;
            return false;
        }

        value = new Int256(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)));
        return true;
    }

    /// <summary>Non-throwing form of <see cref="FromBigEndianBytes"/>.</summary>
    /// <param name="bytes">Candidate bytes; only a length of exactly 32 succeeds.</param>
    /// <param name="value">The decoded value, or <see cref="Zero"/> on failure.</param>
    /// <returns><see langword="true"/> when the span was exactly 32 bytes.</returns>
    public static bool TryFromBigEndianBytes(ReadOnlySpan<byte> bytes, out Int256 value)
    {
        if (bytes.Length != ByteCount)
        {
            value = default;
            return false;
        }

        value = new Int256(
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(24)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(16)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes));
        return true;
    }

    /// <summary>Writes exactly 32 bytes of little-endian two's complement.</summary>
    /// <param name="destination">At least 32 bytes; only the first 32 are written.</param>
    /// <exception cref="ArgumentException">The destination is shorter than 32 bytes.</exception>
    public void WriteLittleEndianBytes(Span<byte> destination)
    {
        if (destination.Length < ByteCount)
        {
            ThrowDestinationTooShort(nameof(destination), destination.Length);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, _l0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8), _l1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(16), _l2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(24), _l3);
    }

    /// <summary>Writes exactly 32 bytes of big-endian two's complement.</summary>
    /// <param name="destination">At least 32 bytes; only the first 32 are written.</param>
    /// <exception cref="ArgumentException">The destination is shorter than 32 bytes.</exception>
    public void WriteBigEndianBytes(Span<byte> destination)
    {
        if (destination.Length < ByteCount)
        {
            ThrowDestinationTooShort(nameof(destination), destination.Length);
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _l3);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), _l2);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(16), _l1);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(24), _l0);
    }

    /// <summary><see langword="true"/> when the sign bit is set.</summary>
    public bool IsNegative => (_l3 & SignBit) != 0;

    /// <summary><see langword="true"/> when every bit is zero.</summary>
    public bool IsZero => (_l0 | _l1 | _l2 | _l3) == 0;

    /// <summary>-1, 0 or +1.</summary>
    public int Sign => IsZero ? 0 : (IsNegative ? -1 : 1);

    /// <summary>
    /// Two's-complement negation.
    /// </summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The negated value.</returns>
    /// <exception cref="OverflowException">
    /// <paramref name="value"/> is <see cref="MinValue"/>: it is the one value with no positive
    /// counterpart, and wrapping silently would corrupt a rendered decimal.
    /// </exception>
    public static Int256 Negate(Int256 value)
    {
        if (value._l3 == SignBit && (value._l0 | value._l1 | value._l2) == 0)
        {
            ThrowNegateOverflow();
        }

        NegateUnchecked(value._l0, value._l1, value._l2, value._l3,
            out ulong n0, out ulong n1, out ulong n2, out ulong n3);
        return new Int256(n0, n1, n2, n3);
    }

    /// <summary>Two's-complement negation; see <see cref="Negate"/>.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The negated value.</returns>
    /// <exception cref="OverflowException"><paramref name="value"/> is <see cref="MinValue"/>.</exception>
    public static Int256 operator -(Int256 value) => Negate(value);

    /// <summary>Orders two values as signed 256-bit integers.</summary>
    /// <param name="other">The value to compare against.</param>
    /// <returns>Negative, zero or positive.</returns>
    public int CompareTo(Int256 other)
    {
        // The top limb is compared signed; the rest unsigned.
        long a3 = (long)_l3;
        long b3 = (long)other._l3;
        if (a3 != b3)
        {
            return a3 < b3 ? -1 : 1;
        }

        if (_l2 != other._l2)
        {
            return _l2 < other._l2 ? -1 : 1;
        }

        if (_l1 != other._l1)
        {
            return _l1 < other._l1 ? -1 : 1;
        }

        if (_l0 != other._l0)
        {
            return _l0 < other._l0 ? -1 : 1;
        }

        return 0;
    }

    /// <summary>Bitwise equality, which for two's complement is numeric equality.</summary>
    /// <param name="other">The value to compare against.</param>
    /// <returns><see langword="true"/> when the values are equal.</returns>
    public bool Equals(Int256 other) =>
        _l0 == other._l0 && _l1 == other._l1 && _l2 == other._l2 && _l3 == other._l3;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Int256 other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_l0, _l1, _l2, _l3);

    /// <summary>Equality.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns><see langword="true"/> when the values are equal.</returns>
    public static bool operator ==(Int256 a, Int256 b) => a.Equals(b);

    /// <summary>Inequality.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns><see langword="true"/> when the values differ.</returns>
    public static bool operator !=(Int256 a, Int256 b) => !a.Equals(b);

    /// <summary>Signed less-than.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="a"/> is smaller.</returns>
    public static bool operator <(Int256 a, Int256 b) => a.CompareTo(b) < 0;

    /// <summary>Signed less-than-or-equal.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="a"/> is not larger.</returns>
    public static bool operator <=(Int256 a, Int256 b) => a.CompareTo(b) <= 0;

    /// <summary>Signed greater-than.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="a"/> is larger.</returns>
    public static bool operator >(Int256 a, Int256 b) => a.CompareTo(b) > 0;

    /// <summary>Signed greater-than-or-equal.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="a"/> is not smaller.</returns>
    public static bool operator >=(Int256 a, Int256 b) => a.CompareTo(b) >= 0;

    /// <summary>Narrows to <see cref="Int128"/> when the value fits.</summary>
    /// <param name="value">The narrowed value, or zero on failure.</param>
    /// <returns><see langword="true"/> when bits 128..256 are the sign extension of bit 127.</returns>
    public bool TryToInt128(out Int128 value)
    {
        ulong extend = (_l1 & SignBit) != 0 ? ulong.MaxValue : 0UL;
        if (_l2 != extend || _l3 != extend)
        {
            value = default;
            return false;
        }

        value = new Int128(_l1, _l0);
        return true;
    }

    /// <summary>Narrows to <see cref="long"/> when the value fits.</summary>
    /// <param name="value">The narrowed value, or zero on failure.</param>
    /// <returns><see langword="true"/> when bits 64..256 are the sign extension of bit 63.</returns>
    public bool TryToInt64(out long value)
    {
        ulong extend = (_l0 & SignBit) != 0 ? ulong.MaxValue : 0UL;
        if (_l1 != extend || _l2 != extend || _l3 != extend)
        {
            value = 0;
            return false;
        }

        value = (long)_l0;
        return true;
    }

    /// <summary>Narrows to <see cref="ulong"/> when the value fits.</summary>
    /// <param name="value">The narrowed value, or zero on failure.</param>
    /// <returns><see langword="true"/> when the value is in 0..2^64-1.</returns>
    public bool TryToUInt64(out ulong value)
    {
        if ((_l1 | _l2 | _l3) != 0)
        {
            value = 0;
            return false;
        }

        value = _l0;
        return true;
    }

    /// <summary>
    /// Converts the raw integer to <see cref="decimal"/> when its magnitude fits
    /// <see cref="decimal"/>'s 96-bit unscaled range. No scale is applied here;
    /// <see cref="VortexDecimal.TryToDecimal"/> applies the scale.
    /// </summary>
    /// <param name="value">The converted value, or zero on failure.</param>
    /// <returns><see langword="true"/> when the magnitude is below 2^96.</returns>
    public bool TryToDecimal(out decimal value)
    {
        GetMagnitude(out ulong m0, out ulong m1, out ulong m2, out ulong m3);
        if (!FitsIn96Bits(m1, m2, m3))
        {
            value = 0m;
            return false;
        }

        value = MakeDecimal(m0, m1, IsNegative, 0);
        return true;
    }

    /// <summary>
    /// Exact, culture-invariant, base 10, minus sign for negatives, no grouping, no scale applied.
    /// Allocates a <see cref="string"/>; diagnostics and <c>ToString</c> paths only.
    /// </summary>
    /// <returns>The decimal rendering.</returns>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[MaxFormattedLength];
        if (!TryFormat(buffer, out int written))
        {
            // Unreachable: the magnitude of a 256-bit signed value is at most 2^255, which is
            // MaxDigitCount digits. Never degrade to an empty string - that would be a wrong
            // value passed off as right.
            throw new UnreachableException();
        }

        return new string(buffer.Slice(0, written));
    }

    /// <summary>Allocation-free rendering; see <see cref="ToString()"/>.</summary>
    /// <param name="destination">
    /// Receives the rendering; needs at most <see cref="MaxFormattedLength"/> (78) chars.
    /// </param>
    /// <param name="charsWritten">Characters written, or 0 when the destination was too small.</param>
    /// <returns><see langword="true"/> when the value fit.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        Span<char> digits = stackalloc char[MaxDigitCount];
        int digitCount = FormatMagnitudeDigits(digits);
        bool negative = IsNegative;
        int total = digitCount + (negative ? 1 : 0);
        if (destination.Length < total)
        {
            charsWritten = 0;
            return false;
        }

        int at = 0;
        if (negative)
        {
            destination[at++] = '-';
        }

        digits.Slice(MaxDigitCount - digitCount, digitCount).CopyTo(destination.Slice(at));
        charsWritten = total;
        return true;
    }

    /// <summary>
    /// Renders the unsigned magnitude, right-aligned in <paramref name="digits"/>, and returns the
    /// number of characters produced. <paramref name="digits"/> must be
    /// <see cref="MaxDigitCount"/> long.
    /// </summary>
    internal int FormatMagnitudeDigits(Span<char> digits)
    {
        // Defensive: the loop below writes backwards from the end and is bounded only by the fact
        // that a magnitude is at most 2^255. A short span would index out of range, and an
        // IndexOutOfRangeException is not one of the exception types this library contracts to
        // raise.
        if (digits.Length < MaxDigitCount)
        {
            ThrowDigitBufferTooSmall(digits.Length);
        }

        GetMagnitude(out ulong m0, out ulong m1, out ulong m2, out ulong m3);
        int pos = MaxDigitCount;
        while (true)
        {
            ulong remainder = DivRemPow10Chunk(ref m0, ref m1, ref m2, ref m3);
            bool last = (m0 | m1 | m2 | m3) == 0;
            int limit = last ? 1 : Pow10ChunkDigits;
            for (int i = 0; i < limit || remainder != 0; i++)
            {
                pos--;
                digits[pos] = (char)('0' + (int)(remainder % 10));
                remainder /= 10;
            }

            if (last)
            {
                break;
            }
        }

        return MaxDigitCount - pos;
    }

    /// <summary>
    /// The unsigned magnitude limbs. Correct for <see cref="MinValue"/>, whose magnitude 2^255 is
    /// representable as an unsigned 256-bit value even though it is not representable as a signed
    /// one - which is why rendering never calls <see cref="Negate"/>.
    /// </summary>
    internal void GetMagnitude(out ulong m0, out ulong m1, out ulong m2, out ulong m3)
    {
        if ((_l3 & SignBit) == 0)
        {
            m0 = _l0;
            m1 = _l1;
            m2 = _l2;
            m3 = _l3;
            return;
        }

        NegateUnchecked(_l0, _l1, _l2, _l3, out m0, out m1, out m2, out m3);
    }

    /// <summary>
    /// Multiplies an unsigned magnitude by ten in place. Returns <see langword="false"/> when the
    /// product does not fit 256 unsigned bits, in which case the limbs are left unspecified.
    /// </summary>
    internal static bool TryMultiplyMagnitudeByTen(
        ref ulong m0, ref ulong m1, ref ulong m2, ref ulong m3)
    {
        UInt128 t = (UInt128)m0 * 10;
        m0 = (ulong)t;
        t = (UInt128)m1 * 10 + (ulong)(t >> 64);
        m1 = (ulong)t;
        t = (UInt128)m2 * 10 + (ulong)(t >> 64);
        m2 = (ulong)t;
        t = (UInt128)m3 * 10 + (ulong)(t >> 64);
        m3 = (ulong)t;
        return (ulong)(t >> 64) == 0;
    }

    /// <summary>Orders two unsigned magnitudes.</summary>
    internal static int CompareMagnitudes(
        ulong a0, ulong a1, ulong a2, ulong a3,
        ulong b0, ulong b1, ulong b2, ulong b3)
    {
        if (a3 != b3)
        {
            return a3 < b3 ? -1 : 1;
        }

        if (a2 != b2)
        {
            return a2 < b2 ? -1 : 1;
        }

        if (a1 != b1)
        {
            return a1 < b1 ? -1 : 1;
        }

        if (a0 != b0)
        {
            return a0 < b0 ? -1 : 1;
        }

        return 0;
    }

    /// <summary><see langword="true"/> when the magnitude is below 2^96, decimal's unscaled range.</summary>
    internal static bool FitsIn96Bits(ulong m1, ulong m2, ulong m3) =>
        m2 == 0 && m3 == 0 && (m1 >> 32) == 0;

    /// <summary>Builds a <see cref="decimal"/> from a magnitude already known to fit 96 bits.</summary>
    internal static decimal MakeDecimal(ulong m0, ulong m1, bool negative, byte scale) =>
        new decimal((int)(uint)m0, (int)(uint)(m0 >> 32), (int)(uint)m1, negative, scale);

    /// <summary>
    /// Divides the unsigned magnitude in place by <see cref="Pow10Chunk"/> and returns the
    /// remainder. Schoolbook long division over the four limbs; each step is one 128-by-64 divide,
    /// which is exact because the running remainder is always below the divisor.
    /// </summary>
    private static ulong DivRemPow10Chunk(ref ulong l0, ref ulong l1, ref ulong l2, ref ulong l3)
    {
        UInt128 cur = l3;
        l3 = (ulong)(cur / Pow10Chunk);
        UInt128 rem = cur % Pow10Chunk;

        cur = (rem << 64) | l2;
        l2 = (ulong)(cur / Pow10Chunk);
        rem = cur % Pow10Chunk;

        cur = (rem << 64) | l1;
        l1 = (ulong)(cur / Pow10Chunk);
        rem = cur % Pow10Chunk;

        cur = (rem << 64) | l0;
        l0 = (ulong)(cur / Pow10Chunk);
        rem = cur % Pow10Chunk;

        return (ulong)rem;
    }

    /// <summary>Two's-complement negation with no MinValue guard: ~x + 1 across the four limbs.</summary>
    private static void NegateUnchecked(
        ulong l0, ulong l1, ulong l2, ulong l3,
        out ulong n0, out ulong n1, out ulong n2, out ulong n3)
    {
        n0 = ~l0 + 1;
        ulong carry = n0 == 0 ? 1UL : 0UL;
        n1 = ~l1 + carry;
        carry = carry != 0 && n1 == 0 ? 1UL : 0UL;
        n2 = ~l2 + carry;
        carry = carry != 0 && n2 == 0 ? 1UL : 0UL;
        n3 = ~l3 + carry;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowByteCount(int length) =>
        throw new VortexFormatException(
            $"An i256 value is exactly {ByteCount} bytes; got {length}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDestinationTooShort(string paramName, int length) =>
        throw new ArgumentException(
            $"Writing an i256 needs {ByteCount} bytes; the destination holds {length}.", paramName);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDigitBufferTooSmall(int length) =>
        throw new VortexFormatException(
            $"Rendering an i256 needs a {MaxDigitCount}-char digit buffer; got {length}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNegateOverflow() =>
        throw new OverflowException(
            "Negating Int256.MinValue overflows: -2^255 has no positive counterpart in 256 bits.");
}
