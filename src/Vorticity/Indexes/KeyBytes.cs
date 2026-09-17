// A column value as bytes, and the order of those bytes: what every index over values shares.
//
// ONE ENCODING FOR THE WRITER AND THE READER. A Bloom filter hashes these bytes, a postings run and
// a sorted run order and search them. The writer takes them straight from the canonical column --
// a primitive at its width, a decimal at its storage width, a string's bytes -- and the reader has
// to produce the same bytes from a filter literal, which is where a probe can go wrong: the kernels
// compare in three domains (i64, u64, f64), so the literal has to be narrowed into the column's own
// type, and a narrowing that is not exact claims nothing rather than a proof built on a rounding.
//
// THE ORDER IS docs/12-index-reads.md §4.4's TOTAL ORDER: numeric for integers and decimals,
// bytewise for strings and binaries, and for floats the row-encoding order -- negative NaN, the
// negatives, -0.0, +0.0, the positives, positive NaN. An equality probe asks for both zeros,
// because the scan's equality is IEEE and a run holds bit patterns.
using System;
using System.Buffers.Binary;
using Vorticity.Expressions;
using Vorticity.Types;

namespace Vorticity.Indexes;

/// <summary>How a column's values are laid out as index keys.</summary>
internal enum KeyShape : byte
{
    /// <summary>A signed integer: two's complement, little-endian.</summary>
    Signed,

    /// <summary>An unsigned integer, little-endian.</summary>
    Unsigned,

    /// <summary>An IEEE float, little-endian.</summary>
    Float,

    /// <summary>A string or a binary: the bytes themselves.</summary>
    Bytes,
}

/// <summary>A column's key layout: its shape and, for fixed widths, its width.</summary>
/// <param name="Shape">How the bytes read.</param>
/// <param name="Width">Bytes per key; <c>0</c> for <see cref="KeyShape.Bytes"/>.</param>
/// <param name="PType">The primitive type, when there is one.</param>
internal readonly record struct KeyLayout(KeyShape Shape, int Width, PType PType)
{
    /// <summary>The layout of a column, through any extension, or none for a dtype no index keys.</summary>
    /// <param name="dtype">The column's dtype.</param>
    /// <param name="layout">The layout.</param>
    /// <returns>Whether the dtype can be keyed.</returns>
    /// <remarks>
    /// NO DECIMAL. The kernels have no literal domain for one, so no probe could ever claim
    /// anything from a decimal key, and a chunk may store a decimal at a narrower width than its
    /// precision implies, which would make one column's keys two layouts.
    /// </remarks>
    internal static bool TryOf(DType dtype, out KeyLayout layout)
    {
        while (dtype.Kind == DTypeKind.Extension)
        {
            dtype = dtype.StorageType;
        }

        switch (dtype.Kind)
        {
            case DTypeKind.Primitive:
                PType ptype = dtype.PType;
                KeyShape shape = ptype.IsFloat()
                    ? KeyShape.Float
                    : ptype.IsSignedInteger() ? KeyShape.Signed : KeyShape.Unsigned;
                layout = new KeyLayout(shape, ptype.ByteWidth(), ptype);
                return true;
            case DTypeKind.Utf8 or DTypeKind.Binary:
                layout = new KeyLayout(KeyShape.Bytes, 0, default);
                return true;
            default:
                layout = default;
                return false;
        }
    }

    /// <summary>Orders two keys of this layout in the total order.</summary>
    /// <param name="left">One key.</param>
    /// <param name="right">The other.</param>
    /// <returns>The sign of <c>left - right</c>.</returns>
    internal int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        switch (Shape)
        {
            case KeyShape.Bytes:
                return Math.Sign(left.SequenceCompareTo(right));

            case KeyShape.Unsigned:
                return CompareMagnitude(left, right);

            case KeyShape.Signed:
                bool leftNegative = (left[^1] & 0x80) != 0;
                bool rightNegative = (right[^1] & 0x80) != 0;
                if (leftNegative != rightNegative)
                {
                    return leftNegative ? -1 : 1;
                }

                // Same sign: two's complement orders like the unsigned magnitude.
                return CompareMagnitude(left, right);

            default:
                return TotalFloat(left).CompareTo(TotalFloat(right));
        }
    }

    /// <summary>
    /// A fixed-width key as an unsigned integer in the same total order as <see cref="Compare"/>,
    /// and one-to-one: two different keys never share one.
    /// </summary>
    /// <param name="key">A key of this layout, whose shape is not <see cref="KeyShape.Bytes"/>.</param>
    internal ulong SortKey(ReadOnlySpan<byte> key)
    {
        if (Shape == KeyShape.Float)
        {
            return TotalFloat(key);
        }

        ulong value = Width switch
        {
            1 => key[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(key),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(key),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(key),
        };

        // Two's complement orders like the unsigned value once its sign bit is flipped.
        return Shape == KeyShape.Signed ? value ^ (1UL << ((Width * 8) - 1)) : value;
    }

    private static int CompareMagnitude(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        for (int i = left.Length - 1; i >= 0; i--)
        {
            if (left[i] != right[i])
            {
                return left[i] < right[i] ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>A float's bits mapped onto an unsigned integer in total order (docs/06 §3).</summary>
    private ulong TotalFloat(ReadOnlySpan<byte> bytes)
    {
        ulong bits;
        int width;
        switch (Width)
        {
            case 2:
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
                width = 16;
                break;
            case 4:
                bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                width = 32;
                break;
            default:
                bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
                width = 64;
                break;
        }

        // Flip the sign so positives sort above negatives, and invert a negative's magnitude so
        // that the larger magnitude sorts lower.
        ulong sign = 1UL << (width - 1);
        ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        return (bits & sign) != 0 ? (~bits & mask & ~sign) : (bits | sign);
    }

    /// <summary>
    /// The key bytes of <paramref name="literal"/> in this layout, and for a float zero the other
    /// zero's; <see langword="false"/> when no exact key exists and nothing can be claimed.
    /// </summary>
    /// <param name="literal">The filter's constant.</param>
    /// <param name="destination">At least eight bytes, for a fixed width.</param>
    /// <param name="key">The key: a slice of <paramref name="destination"/>, or the literal's own bytes.</param>
    /// <param name="otherZero">The other float zero, when <paramref name="key"/> is a zero.</param>
    /// <param name="hasOtherZero">Whether there is one.</param>
    internal bool TryEncode(
        FilterLiteral literal, Span<byte> destination, out ReadOnlySpan<byte> key,
        Span<byte> otherZero, out bool hasOtherZero)
    {
        key = default;
        hasOtherZero = false;
        switch (Shape)
        {
            case KeyShape.Bytes:
                if (literal.Kind != FilterLiteralKind.Bytes)
                {
                    return false;
                }

                key = literal.BytesValue;
                return true;

            case KeyShape.Float:
                if (!TryDouble(literal, out double d) || double.IsNaN(d) || !TryNarrow(d, destination))
                {
                    return false;
                }

                key = destination[..Width];
                if (d == 0)
                {
                    TryNarrow(double.IsNegative(d) ? 0.0 : -0.0, otherZero);
                    hasOtherZero = true;
                }

                return true;

            default:
                if (!TryInteger(literal, destination))
                {
                    return false;
                }

                key = destination[..Width];
                return true;
        }
    }

    private static bool TryDouble(FilterLiteral value, out double d)
    {
        switch (value.Kind)
        {
            case FilterLiteralKind.Float:
                d = value.FloatValue;
                return true;
            case FilterLiteralKind.Signed:
                d = value.SignedValue;
                return true;
            case FilterLiteralKind.Unsigned:
                d = value.UnsignedValue;
                return true;
            default:
                d = 0;
                return false;
        }
    }

    private bool TryNarrow(double d, Span<byte> bytes)
    {
        switch (Width)
        {
            case 8:
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, d);
                return true;
            case 4:
                float f = (float)d;
                BinaryPrimitives.WriteSingleLittleEndian(bytes, f);
                return (double)f == d;
            case 2:
                Half h = (Half)d;
                BinaryPrimitives.WriteHalfLittleEndian(bytes, h);
                return (double)h == d;
            default:
                return false;
        }
    }

    private bool TryInteger(FilterLiteral value, Span<byte> bytes)
    {
        bool signed = Shape == KeyShape.Signed;
        long s;
        ulong u;
        switch (value.Kind)
        {
            case FilterLiteralKind.Signed:
                s = value.SignedValue;
                if (!signed && s < 0)
                {
                    return false;
                }

                u = unchecked((ulong)s);
                break;
            case FilterLiteralKind.Unsigned:
                u = value.UnsignedValue;
                if (signed && u > long.MaxValue)
                {
                    return false;
                }

                s = unchecked((long)u);
                break;
            case FilterLiteralKind.Float:
                double d = value.FloatValue;
                const double Exact = 9007199254740992.0; // 2^53: past it, several integers share a double.
                if (double.IsNaN(d) || Math.Abs(d) >= Exact || d != Math.Floor(d) || (!signed && d < 0))
                {
                    return false;
                }

                s = (long)d;
                u = unchecked((ulong)s);
                break;
            default:
                return false;
        }

        if (Width < 8)
        {
            int bits = Width * 8;
            if (signed)
            {
                long min = -(1L << (bits - 1));
                long max = (1L << (bits - 1)) - 1;
                if (s < min || s > max)
                {
                    return false;
                }
            }
            else if (u >= 1UL << bits)
            {
                return false;
            }
        }

        BinaryPrimitives.WriteUInt64LittleEndian(bytes, u);
        return true;
    }
}
