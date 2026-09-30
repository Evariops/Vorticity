using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Writing;

/// <summary>
/// A decimal chunk's unscaled values as the signed integers <c>vortex.decimal_byte_parts</c> keeps,
/// and a decimal stored narrower than its precision widened back to the storage a
/// <c>vortex.decimal</c> must declare. Each walk resolves the storage once and is typed for it.
/// </summary>
/// <remarks>
/// A null row's slot is carried as its storage held it, truncated: its value is never read, and the
/// integer schemes that take the parts already clear or skip the slots their validity says are null,
/// as they must for any column a caller filled.
/// </remarks>
internal static class DecimalParts
{
    /// <summary>The narrowest signed width holding every valid unscaled value, or null when one needs more than 64 bits.</summary>
    internal static PType? Width(CanonicalArena arena, in CanonicalNode node)
    {
        ValidityMask mask = ValidityMask.From(arena, node.Validity);
        ReadOnlySpan<byte> bytes = node.Values.Span;
        int rows = node.Length;
        return node.Storage switch
        {
            DecimalStorageType.I8 => PType.I8,
            DecimalStorageType.I16 => Width(MemoryMarshal.Cast<byte, short>(bytes)[..rows], in mask),
            DecimalStorageType.I32 => Width(MemoryMarshal.Cast<byte, int>(bytes)[..rows], in mask),
            DecimalStorageType.I64 => Width(MemoryMarshal.Cast<byte, long>(bytes)[..rows], in mask),
            DecimalStorageType.I128 => Width128(MemoryMarshal.Cast<byte, Int128>(bytes)[..rows], in mask),
            _ => Width256(MemoryMarshal.Cast<byte, long>(bytes)[..(rows * 4)], in mask),
        };
    }

    /// <summary>
    /// The unscaled values at <paramref name="parts"/>'s width, which <see cref="Width"/> chose: the
    /// storage's own buffer when it is that width already, a register at a time otherwise.
    /// </summary>
    internal static VortexBuffer Narrow(CanonicalArena arena, in CanonicalNode node, PType parts)
    {
        int rows = node.Length;
        int width = parts.ByteWidth();
        if (width == DecimalStorage.ByteWidth(node.Storage))
        {
            return node.Values.Slice(0, rows * width);
        }

        // Every element written below, a null slot included.
        VortexBuffer buffer = arena.AllocateUninitialized(rows * width, width, out Span<byte> into);
        ReadOnlySpan<byte> bytes = node.Values.Span;
        switch (node.Storage)
        {
            case DecimalStorageType.I16:
                IntegerNarrowing.Truncate(MemoryMarshal.Cast<byte, short>(bytes)[..rows], parts, into);
                break;
            case DecimalStorageType.I32:
                IntegerNarrowing.Truncate(MemoryMarshal.Cast<byte, int>(bytes)[..rows], parts, into);
                break;
            case DecimalStorageType.I64:
                IntegerNarrowing.Truncate(MemoryMarshal.Cast<byte, long>(bytes)[..rows], parts, into);
                break;
            case DecimalStorageType.I128:
                IntegerNarrowing.Truncate(MemoryMarshal.Cast<byte, Int128>(bytes)[..rows], parts, into);
                break;
            default:
                // The low word of each 256-bit value, which Width checked is the whole value.
                LowWords(MemoryMarshal.Cast<byte, long>(bytes)[..(rows * 4)], parts, into);
                break;
        }

        return buffer;
    }

    /// <summary>
    /// The values of a decimal stored narrower than its precision requires -- what a
    /// <c>vortex.decimal_byte_parts</c> decodes to -- at the width a <c>vortex.decimal</c> of that
    /// precision must declare, which a reader refuses to find narrower.
    /// </summary>
    internal static VortexBuffer Widen(CanonicalArena arena, in CanonicalNode node, DecimalStorageType storage)
    {
        int rows = node.Length;
        int width = DecimalStorage.ByteWidth(storage);
        VortexBuffer buffer = arena.AllocateUninitialized(rows * width, Math.Min(width, 16), out Span<byte> into);
        ReadOnlySpan<byte> bytes = node.Values.Span;
        switch (node.Storage)
        {
            case DecimalStorageType.I8:
                Widen(MemoryMarshal.Cast<byte, sbyte>(bytes)[..rows], storage, into);
                break;
            case DecimalStorageType.I16:
                Widen(MemoryMarshal.Cast<byte, short>(bytes)[..rows], storage, into);
                break;
            case DecimalStorageType.I32:
                Widen(MemoryMarshal.Cast<byte, int>(bytes)[..rows], storage, into);
                break;
            case DecimalStorageType.I64:
                Widen(MemoryMarshal.Cast<byte, long>(bytes)[..rows], storage, into);
                break;
            default:
                // Only a 256-bit storage is wider than 128 bits.
                Widen(MemoryMarshal.Cast<byte, Int128>(bytes)[..rows], storage, into);
                break;
        }

        return buffer;
    }

    /// <summary>The width of a storage of 64 bits or fewer, from the bounds the statistics pass takes, a register at a time.</summary>
    private static PType Width<T>(ReadOnlySpan<T> values, in ValidityMask mask)
        where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
    {
        T min;
        T max;
        if (mask.AllValid)
        {
            if (values.IsEmpty)
            {
                return PType.I8;
            }

            BlockStatsPass.Bounds(values, out min, out max);
        }
        else if (mask.AllInvalid || !BlockStatsPass.MaskedBounds(values, in mask, 0, out min, out max))
        {
            return PType.I8;
        }

        return Narrowest(long.CreateTruncating(min), long.CreateTruncating(max));
    }

    /// <summary>The width of 128-bit values, given up at the first valid one past 64 bits.</summary>
    private static PType? Width128(ReadOnlySpan<Int128> values, in ValidityMask mask)
    {
        long min = long.MaxValue;
        long max = long.MinValue;
        bool all = mask.AllValid;
        for (int i = 0; i < values.Length; i++)
        {
            if (!all && !mask.IsValid(i))
            {
                continue;
            }

            Int128 value = values[i];
            long low = long.CreateTruncating(value);
            if (long.CreateTruncating(value >> 64) != low >> 63)
            {
                return null;
            }

            min = Math.Min(min, low);
            max = Math.Max(max, low);
        }

        return Narrowest(min, max);
    }

    /// <summary>The width of 256-bit values, four words each, given up at the first valid one past 64 bits.</summary>
    private static PType? Width256(ReadOnlySpan<long> words, in ValidityMask mask)
    {
        long min = long.MaxValue;
        long max = long.MinValue;
        bool all = mask.AllValid;
        for (int i = 0, w = 0; w < words.Length; i++, w += 4)
        {
            if (!all && !mask.IsValid(i))
            {
                continue;
            }

            long low = words[w];
            long sign = low >> 63;
            if (words[w + 1] != sign || words[w + 2] != sign || words[w + 3] != sign)
            {
                return null;
            }

            min = Math.Min(min, low);
            max = Math.Max(max, low);
        }

        return Narrowest(min, max);
    }

    /// <summary>The narrowest signed width holding <c>[min, max]</c>; an empty range, min above max, is a byte.</summary>
    private static PType Narrowest(long min, long max) =>
        min >= sbyte.MinValue && max <= sbyte.MaxValue ? PType.I8
        : min >= short.MinValue && max <= short.MaxValue ? PType.I16
        : min >= int.MinValue && max <= int.MaxValue ? PType.I32
        : PType.I64;

    /// <summary>The low word of each 256-bit value, four words a value, at <paramref name="parts"/>'s width.</summary>
    private static void LowWords(ReadOnlySpan<long> words, PType parts, Span<byte> into)
    {
        switch (parts)
        {
            case PType.I8:
                LowWords(words, MemoryMarshal.Cast<byte, sbyte>(into));
                break;
            case PType.I16:
                LowWords(words, MemoryMarshal.Cast<byte, short>(into));
                break;
            case PType.I32:
                LowWords(words, MemoryMarshal.Cast<byte, int>(into));
                break;
            default:
                LowWords(words, MemoryMarshal.Cast<byte, long>(into));
                break;
        }
    }

    private static void LowWords<TTo>(ReadOnlySpan<long> words, Span<TTo> into)
        where TTo : unmanaged, IBinaryInteger<TTo>
    {
        for (int i = 0, w = 0; w < words.Length; i++, w += 4)
        {
            into[i] = TTo.CreateTruncating(words[w]);
        }
    }

    private static void Widen<TFrom>(ReadOnlySpan<TFrom> values, DecimalStorageType storage, Span<byte> into)
        where TFrom : unmanaged, IBinaryInteger<TFrom>
    {
        switch (storage)
        {
            case DecimalStorageType.I16:
                Extend(values, MemoryMarshal.Cast<byte, short>(into));
                break;
            case DecimalStorageType.I32:
                Extend(values, MemoryMarshal.Cast<byte, int>(into));
                break;
            case DecimalStorageType.I64:
                Extend(values, MemoryMarshal.Cast<byte, long>(into));
                break;
            case DecimalStorageType.I128:
                Extend(values, MemoryMarshal.Cast<byte, Int128>(into));
                break;
            default:
            {
                // Four words a value: its low and high words, then two of its sign.
                Span<long> words = MemoryMarshal.Cast<byte, long>(into);
                for (int i = 0, w = 0; i < values.Length; i++, w += 4)
                {
                    Int128 value = Int128.CreateTruncating(values[i]);
                    long high = long.CreateTruncating(value >> 64);
                    words[w] = long.CreateTruncating(value);
                    words[w + 1] = high;
                    words[w + 2] = high >> 63;
                    words[w + 3] = high >> 63;
                }

                break;
            }
        }
    }

    private static void Extend<TFrom, TTo>(ReadOnlySpan<TFrom> values, Span<TTo> into)
        where TFrom : unmanaged, IBinaryInteger<TFrom>
        where TTo : unmanaged, IBinaryInteger<TTo>
    {
        into = into[..values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            into[i] = TTo.CreateTruncating(values[i]);
        }
    }
}
