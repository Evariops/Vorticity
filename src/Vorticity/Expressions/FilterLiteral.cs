// A filter's constant operand.
//
// A tagged union rather than `object`, so building a filter boxes nothing and comparing against one
// costs no type test per row. The tags are the COMPARISON domains, not the DType domains: every
// signed integer width compares as i64, every unsigned as u64, every float as f64, and utf8 and
// binary both as bytes. That collapse is what keeps the kernel matrix to one row per canonical kind
// instead of one per physical type.
//
// Signed and unsigned are separate tags on purpose. Folding them into one would make `x > -1` on a
// u64 column either always true or never true depending on which way the fold went, and the bug
// would only appear above i64::MaxValue.
using System;

namespace Vorticity.Expressions;

/// <summary>What a <see cref="FilterLiteral"/> holds.</summary>
public enum FilterLiteralKind : byte
{
    /// <summary>SQL <c>NULL</c>: every comparison against it is <c>unknown</c>.</summary>
    Null = 0,

    /// <summary>A boolean.</summary>
    Bool = 1,

    /// <summary>A signed integer, widened to <see cref="long"/>.</summary>
    Signed = 2,

    /// <summary>An unsigned integer, widened to <see cref="ulong"/>.</summary>
    Unsigned = 3,

    /// <summary>A float, widened to <see cref="double"/>.</summary>
    Float = 4,

    /// <summary>UTF-8 text or raw bytes.</summary>
    Bytes = 5,
}

/// <summary>A constant a filter compares a column against.</summary>
public readonly struct FilterLiteral : IEquatable<FilterLiteral>
{
    private readonly ulong _bits;
    private readonly byte[]? _bytes;

    private FilterLiteral(FilterLiteralKind kind, ulong bits, byte[]? bytes)
    {
        Kind = kind;
        _bits = bits;
        _bytes = bytes;
    }

    /// <summary>Which of the union's arms is live.</summary>
    public FilterLiteralKind Kind { get; }

    /// <summary>SQL <c>NULL</c>.</summary>
    public static FilterLiteral Null => default;

    /// <summary>The signed value. Only meaningful when <see cref="Kind"/> is <see cref="FilterLiteralKind.Signed"/>.</summary>
    public long SignedValue => unchecked((long)_bits);

    /// <summary>The unsigned value. Only meaningful for <see cref="FilterLiteralKind.Unsigned"/>.</summary>
    public ulong UnsignedValue => _bits;

    /// <summary>The float value. Only meaningful for <see cref="FilterLiteralKind.Float"/>.</summary>
    public double FloatValue => BitConverter.UInt64BitsToDouble(_bits);

    /// <summary>The boolean value. Only meaningful for <see cref="FilterLiteralKind.Bool"/>.</summary>
    public bool BoolValue => _bits != 0;

    /// <summary>The bytes. Only meaningful for <see cref="FilterLiteralKind.Bytes"/>.</summary>
    public ReadOnlySpan<byte> BytesValue => _bytes ?? [];

    /// <summary>A boolean constant.</summary>
    /// <param name="value">The value.</param>
    public static FilterLiteral From(bool value) =>
        new FilterLiteral(FilterLiteralKind.Bool, value ? 1UL : 0UL, null);

    /// <summary>A signed integer constant.</summary>
    /// <param name="value">The value.</param>
    public static FilterLiteral From(long value) =>
        new FilterLiteral(FilterLiteralKind.Signed, unchecked((ulong)value), null);

    /// <summary>A signed integer constant.</summary>
    /// <param name="value">The value.</param>
    public static FilterLiteral From(int value) => From((long)value);

    /// <summary>An unsigned integer constant.</summary>
    /// <param name="value">The value.</param>
    public static FilterLiteral From(ulong value) =>
        new FilterLiteral(FilterLiteralKind.Unsigned, value, null);

    /// <summary>A float constant.</summary>
    /// <param name="value">The value.</param>
    public static FilterLiteral From(double value) =>
        new FilterLiteral(FilterLiteralKind.Float, BitConverter.DoubleToUInt64Bits(value), null);

    /// <summary>A float constant.</summary>
    /// <param name="value">The value.</param>
    public static FilterLiteral From(float value) => From((double)value);

    /// <summary>A UTF-8 text constant.</summary>
    /// <param name="value">The text; encoded once, here, never per row.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static FilterLiteral From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new FilterLiteral(
            FilterLiteralKind.Bytes, 0, System.Text.Encoding.UTF8.GetBytes(value));
    }

    /// <summary>A byte-string constant.</summary>
    /// <param name="value">The bytes; copied, so the caller's array may change afterwards.</param>
    public static FilterLiteral From(ReadOnlySpan<byte> value) =>
        new FilterLiteral(FilterLiteralKind.Bytes, 0, value.ToArray());

    /// <inheritdoc/>
    public bool Equals(FilterLiteral other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind == FilterLiteralKind.Bytes
            ? BytesValue.SequenceEqual(other.BytesValue)
            : _bits == other._bits;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FilterLiteral other && Equals(other);

    /// <inheritdoc/>
    /// <remarks>
    /// THE CONTENT, FOR A BYTE STRING, because equality reads the content and a hash that stops at
    /// the length says every literal of the same width is the same one. A set of sixteen-byte
    /// literals -- identifiers, which is what a key column holds -- then degenerates into one
    /// bucket, and every insertion compares against everything already in it.
    /// </remarks>
    public override int GetHashCode()
    {
        if (Kind != FilterLiteralKind.Bytes)
        {
            return HashCode.Combine(Kind, _bits);
        }

        HashCode hash = default;
        hash.Add(Kind);
        hash.AddBytes(BytesValue);
        return hash.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(FilterLiteral left, FilterLiteral right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(FilterLiteral left, FilterLiteral right) => !left.Equals(right);
}
