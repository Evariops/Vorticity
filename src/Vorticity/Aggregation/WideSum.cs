using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Types.Numerics;

namespace Vorticity.Aggregating;

/// <summary>
/// An exact sum of unscaled decimals in 320 bits of two's complement: wide enough that no count of
/// rows below 2^63 overflows it, whatever the values of a decimal of 76 digits.
/// </summary>
/// <remarks>
/// A 76-digit value takes 253 bits, so 2^63 of them take 316: a 256-bit total overflows after six
/// rows near the largest value, where the answer can still be small once the opposite values have
/// been added. The limbs are only reached when a narrower running total overflows: the ops keep an
/// <see cref="Int128"/> or <see cref="Int256"/> total in the loop and spill it here, so the common
/// case pays one wide add per span rather than one per value.
/// </remarks>
internal struct WideSum
{
    private ulong _l0;
    private ulong _l1;
    private ulong _l2;
    private ulong _l3;
    private ulong _l4;

    /// <summary>Whether the total is zero.</summary>
    internal readonly bool IsZero => (_l0 | _l1 | _l2 | _l3 | _l4) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(Int128 value)
    {
        ulong extend = value < 0 ? ulong.MaxValue : 0UL;
        AddLimbs((ulong)value, (ulong)(value >>> 64), extend, extend, extend);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(in Int256 value)
    {
        value.GetLimbs(out ulong l0, out ulong l1, out ulong l2, out ulong l3);
        AddLimbs(l0, l1, l2, l3, (l3 & 0x8000_0000_0000_0000UL) != 0 ? ulong.MaxValue : 0UL);
    }

    internal void Merge(in WideSum other) => AddLimbs(other._l0, other._l1, other._l2, other._l3, other._l4);

    /// <summary>Adds <paramref name="value"/> times <paramref name="count"/>, which is exact: 253 bits times 63 fits 320.</summary>
    internal void AddProduct(in Int256 value, long count)
    {
        if (count == 0 || value.IsZero)
        {
            return;
        }

        value.GetMagnitude(out ulong m0, out ulong m1, out ulong m2, out ulong m3);
        bool negative = value.IsNegative ^ (count < 0);
        ulong factor = count < 0 ? (ulong)-count : (ulong)count;

        // Four 64-by-64 products, carried into five limbs.
        UInt128 p = (UInt128)m0 * factor;
        ulong r0 = (ulong)p;
        p = ((UInt128)m1 * factor) + (ulong)(p >> 64);
        ulong r1 = (ulong)p;
        p = ((UInt128)m2 * factor) + (ulong)(p >> 64);
        ulong r2 = (ulong)p;
        p = ((UInt128)m3 * factor) + (ulong)(p >> 64);
        ulong r3 = (ulong)p;
        ulong r4 = (ulong)(p >> 64);
        if (negative)
        {
            Negate(ref r0, ref r1, ref r2, ref r3, ref r4);
        }

        AddLimbs(r0, r1, r2, r3, r4);
    }

    internal void AddProduct(Int128 value, long count) => AddProduct(new Int256(value), count);

    /// <summary>The total as a 256-bit integer, when it fits.</summary>
    internal readonly bool TryToInt256(out Int256 value)
    {
        ulong extend = (_l3 & 0x8000_0000_0000_0000UL) != 0 ? ulong.MaxValue : 0UL;
        if (_l4 != extend)
        {
            value = default;
            return false;
        }

        value = Int256.FromLimbs(_l0, _l1, _l2, _l3);
        return true;
    }

    /// <summary>The total, exactly, however far past 256 bits it went.</summary>
    internal readonly BigInteger ToBigInteger()
    {
        Span<byte> bytes = stackalloc byte[40];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, _l0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], _l1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], _l2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], _l3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], _l4);
        return new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
    }

    /// <summary>The total as a double, rounded once per limb from the most significant down.</summary>
    internal readonly double ToDouble()
    {
        ulong l0 = _l0, l1 = _l1, l2 = _l2, l3 = _l3, l4 = _l4;
        bool negative = (l4 & 0x8000_0000_0000_0000UL) != 0;
        if (negative)
        {
            Negate(ref l0, ref l1, ref l2, ref l3, ref l4);
        }

        double magnitude = Math.ScaleB(l4, 256) + Math.ScaleB(l3, 192) + Math.ScaleB(l2, 128) + Math.ScaleB(l1, 64) + l0;
        return negative ? -magnitude : magnitude;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddLimbs(ulong a0, ulong a1, ulong a2, ulong a3, ulong a4)
    {
        ulong carry;
        (_l0, carry) = AddCarry(_l0, a0, 0);
        (_l1, carry) = AddCarry(_l1, a1, carry);
        (_l2, carry) = AddCarry(_l2, a2, carry);
        (_l3, carry) = AddCarry(_l3, a3, carry);
        _l4 = _l4 + a4 + carry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (ulong Sum, ulong Carry) AddCarry(ulong a, ulong b, ulong carry)
    {
        ulong sum = a + b;
        ulong first = sum < a ? 1UL : 0UL;
        ulong total = sum + carry;
        return (total, first | (total < sum ? 1UL : 0UL));
    }

    private static void Negate(ref ulong l0, ref ulong l1, ref ulong l2, ref ulong l3, ref ulong l4)
    {
        l0 = ~l0;
        l1 = ~l1;
        l2 = ~l2;
        l3 = ~l3;
        l4 = ~l4;
        ulong carry;
        (l0, carry) = AddCarry(l0, 1, 0);
        (l1, carry) = AddCarry(l1, 0, carry);
        (l2, carry) = AddCarry(l2, 0, carry);
        (l3, carry) = AddCarry(l3, 0, carry);
        l4 += carry;
    }
}

/// <summary>The decimal sums' running totals, kept narrow in the loop and spilled wide on overflow.</summary>
internal static class NarrowTotals
{
    /// <summary>Adds <paramref name="values"/> to <paramref name="into"/>, through an <see cref="Int128"/> total spilled only when it would overflow.</summary>
    internal static void Add(ref WideSum into, ReadOnlySpan<Int128> values)
    {
        Int128 total = 0;
        foreach (Int128 value in values)
        {
            Int128 next = total + value;
            // Two's-complement overflow: both operands share a sign the result does not.
            if (((total ^ next) & (value ^ next)) < 0)
            {
                into.Add(total);
                next = value;
            }

            total = next;
        }

        into.Add(total);
    }

    /// <summary>Adds <paramref name="values"/> to <paramref name="into"/>, through an <see cref="Int256"/> total spilled only when it would overflow.</summary>
    internal static void Add(ref WideSum into, ReadOnlySpan<Int256> values)
    {
        Int256 total = Int256.Zero;
        foreach (Int256 value in values)
        {
            if (!Int256.TryAdd(total, value, out Int256 next))
            {
                into.Add(in total);
                next = value;
            }

            total = next;
        }

        into.Add(in total);
    }
}
