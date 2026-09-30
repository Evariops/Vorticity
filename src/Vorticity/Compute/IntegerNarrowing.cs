using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// Integers truncated to a narrower signed type that holds every value that matters: a register at
/// a time for the pairs the vector narrowing covers, one by one for the rest.
/// </summary>
internal static class IntegerNarrowing
{
    /// <summary>Each value truncated to <paramref name="to"/>, which the caller has checked holds every one that matters.</summary>
    internal static void Truncate<TFrom>(ReadOnlySpan<TFrom> values, PType to, Span<byte> into)
        where TFrom : unmanaged, IBinaryInteger<TFrom>
    {
        // The same width is the same bytes.
        if (to.ByteWidth() == Unsafe.SizeOf<TFrom>())
        {
            MemoryMarshal.AsBytes(values).CopyTo(into);
            return;
        }

        switch (to)
        {
            case PType.I8:
                Truncate(values, MemoryMarshal.Cast<byte, sbyte>(into));
                break;
            case PType.I16:
                Truncate(values, MemoryMarshal.Cast<byte, short>(into));
                break;
            case PType.I32:
                Truncate(values, MemoryMarshal.Cast<byte, int>(into));
                break;
            default:
                Truncate(values, MemoryMarshal.Cast<byte, long>(into));
                break;
        }
    }

    /// <summary>Each value truncated to the narrower type, a register at a time and the tail one by one.</summary>
    private static void Truncate<TFrom, TTo>(ReadOnlySpan<TFrom> values, Span<TTo> into)
        where TFrom : unmanaged, IBinaryInteger<TFrom>
        where TTo : unmanaged, IBinaryInteger<TTo>
    {
        into = into[..values.Length];
        int i = Registers(values, into);
        for (; i < values.Length; i++)
        {
            into[i] = TTo.CreateTruncating(values[i]);
        }
    }

    /// <summary>
    /// The leading values truncated a register at a time, for the pairs the vector narrowing
    /// covers; how many were written, zero for a pair it does not (a 128-bit storage) and when the
    /// machine has no vectors, where the software fallback would cost more than the scalar loop.
    /// </summary>
    private static int Registers<TFrom, TTo>(ReadOnlySpan<TFrom> values, Span<TTo> into)
        where TFrom : unmanaged
        where TTo : unmanaged
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return 0;
        }

        int count = values.Length;
        int done = 0;
        ref TTo target = ref MemoryMarshal.GetReference(into);
        if (typeof(TFrom) == typeof(long))
        {
            ref long source = ref Unsafe.As<TFrom, long>(ref MemoryMarshal.GetReference(values));
            if (typeof(TTo) == typeof(int))
            {
                for (; done <= count - 4; done += 4)
                {
                    To32(ref source, done).StoreUnsafe(ref Unsafe.As<TTo, int>(ref target), (nuint)done);
                }
            }
            else if (typeof(TTo) == typeof(short))
            {
                for (; done <= count - 8; done += 8)
                {
                    To16(ref source, done).StoreUnsafe(ref Unsafe.As<TTo, short>(ref target), (nuint)done);
                }
            }
            else if (typeof(TTo) == typeof(sbyte))
            {
                for (; done <= count - 16; done += 16)
                {
                    To8(ref source, done).StoreUnsafe(ref Unsafe.As<TTo, sbyte>(ref target), (nuint)done);
                }
            }
        }
        else if (typeof(TFrom) == typeof(int))
        {
            ref int source = ref Unsafe.As<TFrom, int>(ref MemoryMarshal.GetReference(values));
            if (typeof(TTo) == typeof(short))
            {
                for (; done <= count - 8; done += 8)
                {
                    To16(ref source, done).StoreUnsafe(ref Unsafe.As<TTo, short>(ref target), (nuint)done);
                }
            }
            else if (typeof(TTo) == typeof(sbyte))
            {
                for (; done <= count - 16; done += 16)
                {
                    To8(ref source, done).StoreUnsafe(ref Unsafe.As<TTo, sbyte>(ref target), (nuint)done);
                }
            }
        }
        else if (typeof(TFrom) == typeof(short) && typeof(TTo) == typeof(sbyte))
        {
            ref short source = ref Unsafe.As<TFrom, short>(ref MemoryMarshal.GetReference(values));
            for (; done <= count - 16; done += 16)
            {
                Vector128.Narrow(Vector128.LoadUnsafe(ref source, (nuint)done), Vector128.LoadUnsafe(ref source, (nuint)(done + 8)))
                    .StoreUnsafe(ref Unsafe.As<TTo, sbyte>(ref target), (nuint)done);
            }
        }

        return done;
    }

    private static Vector128<int> To32(ref long source, int at) =>
        Vector128.Narrow(Vector128.LoadUnsafe(ref source, (nuint)at), Vector128.LoadUnsafe(ref source, (nuint)(at + 2)));

    private static Vector128<short> To16(ref long source, int at) => Vector128.Narrow(To32(ref source, at), To32(ref source, at + 4));

    private static Vector128<sbyte> To8(ref long source, int at) => Vector128.Narrow(To16(ref source, at), To16(ref source, at + 8));

    private static Vector128<short> To16(ref int source, int at) =>
        Vector128.Narrow(Vector128.LoadUnsafe(ref source, (nuint)at), Vector128.LoadUnsafe(ref source, (nuint)(at + 4)));

    private static Vector128<sbyte> To8(ref int source, int at) => Vector128.Narrow(To16(ref source, at), To16(ref source, at + 8));
}
