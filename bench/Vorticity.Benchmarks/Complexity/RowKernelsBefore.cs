using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>
/// The dictionary gather with nullable codes and values as it was: its values' validity expanded to
/// a byte per entry at every gather. Copied from the reader, the code widening narrowed to the
/// unsigned widths the benchmark uses.
/// </summary>
internal static class RowKernelsBefore
{
    /// <summary>
    /// The gather with nullable codes, sixty-four rows of their validity at a time and no branch on
    /// a row.
    /// </summary>
    /// <returns>The row of the first out-of-range code under a valid row, or -1.</returns>
    internal static int MaskedWords<TCode, TValue>(
        ReadOnlySpan<TCode> codes, ReadOnlySpan<TValue> source, Span<TValue> target,
        ReadOnlySpan<byte> codeBits, int codeBitOffset,
        ReadOnlySpan<byte> valueBits, int valueBitOffset, bool valuesAllValid, uint limit,
        Span<byte> outputBits)
        where TCode : unmanaged
        where TValue : unmanaged
    {
        int entries = (int)limit;
        Scratch<byte> flagScratch = new Scratch<byte>(valuesAllValid ? 0 : entries, default);
        try
        {
            Span<byte> flags = flagScratch.Span;
            if (!valuesAllValid)
            {
                for (int entry = 0; entry < entries; entry++)
                {
                    int at = valueBitOffset + entry;
                    flags[entry] = (byte)((valueBits[at >> 3] >> (at & 7)) & 1);
                }
            }

            ref TCode codeRef = ref MemoryMarshal.GetReference(codes);
            ref TValue sourceRef = ref MemoryMarshal.GetReference(source);
            ref TValue targetRef = ref MemoryMarshal.GetReference(target);
            ref byte flagRef = ref MemoryMarshal.GetReference(flags);
            for (int row = 0; row < target.Length; row += 64)
            {
                int span = Math.Min(64, target.Length - row);
                ulong all = BitWords.Mask(span);
                ulong valid = BitWords.Load(codeBits, codeBitOffset + row) & all;
                ref TValue rows = ref Unsafe.Add(ref targetRef, row);
                ref TCode wordCodes = ref Unsafe.Add(ref codeRef, row);
                ulong output;
                if (AllBelow(ref wordCodes, span, limit))
                {
                    // Every code of the word names an entry, the null rows' included: each row is
                    // gathered as it stands, and the null rows emptied after.
                    output = valuesAllValid
                        ? GatherInside<TCode, TValue, AllValuesValid>(ref wordCodes, ref sourceRef, ref rows, ref flagRef, span) & valid
                        : GatherInside<TCode, TValue, ValuesFlagged>(ref wordCodes, ref sourceRef, ref rows, ref flagRef, span) & valid;
                }
                else
                {
                    bool faulted;
                    output = valuesAllValid
                        ? GatherWord<TCode, TValue, AllValuesValid>(
                            ref wordCodes, ref sourceRef, ref rows, ref flagRef, span, valid, limit, out faulted)
                        : GatherWord<TCode, TValue, ValuesFlagged>(
                            ref wordCodes, ref sourceRef, ref rows, ref flagRef, span, valid, limit, out faulted);
                    if (faulted)
                    {
                        return FirstFault(codes, valid, limit, row, span);
                    }
                }

                for (ulong nulls = ~valid & all; nulls != 0; nulls &= nulls - 1)
                {
                    Unsafe.Add(ref rows, BitOperations.TrailingZeroCount(nulls)) = default;
                }

                if (!outputBits.IsEmpty)
                {
                    Span<byte> bytes = outputBits.Slice(row >> 3, (span + 7) >> 3);
                    for (int b = 0; b < bytes.Length; b++)
                    {
                        bytes[b] = (byte)(output >> (b * 8));
                    }
                }
            }

            return -1;
        }
        finally
        {
            flagScratch.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong GatherWord<TCode, TValue, TValidity>(
        ref TCode codes, ref TValue source, ref TValue target, ref byte flags, int count, ulong valid,
        uint limit, out bool faulted)
        where TCode : unmanaged
        where TValue : unmanaged
        where TValidity : struct, IValueValidity
    {
        ulong output = 0;
        uint fault = 0;
        for (int k = 0; k < count; k++)
        {
            uint bit = (uint)(valid >> k) & 1;
            uint raw = WidenCode(Unsafe.Add(ref codes, k));
            uint inside = raw < limit ? 1u : 0u;
            fault |= bit & (inside ^ 1);
            uint code = raw & (0u - (bit & inside));
            Unsafe.Add(ref target, k) = Unsafe.Add(ref source, (nint)code);
            if (TValidity.Flagged)
            {
                output |= (ulong)(bit & Unsafe.Add(ref flags, (nint)code)) << k;
            }
        }

        faulted = fault != 0;
        return TValidity.Flagged ? output : valid;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong GatherInside<TCode, TValue, TValidity>(
        ref TCode codes, ref TValue source, ref TValue target, ref byte flags, int count)
        where TCode : unmanaged
        where TValue : unmanaged
        where TValidity : struct, IValueValidity
    {
        ulong output = 0;
        int k = 0;
        for (; k <= count - 8; k += 8)
        {
            ref TCode at = ref Unsafe.Add(ref codes, k);
            nint c0 = (nint)WidenCode(at);
            nint c1 = (nint)WidenCode(Unsafe.Add(ref at, 1));
            nint c2 = (nint)WidenCode(Unsafe.Add(ref at, 2));
            nint c3 = (nint)WidenCode(Unsafe.Add(ref at, 3));
            nint c4 = (nint)WidenCode(Unsafe.Add(ref at, 4));
            nint c5 = (nint)WidenCode(Unsafe.Add(ref at, 5));
            nint c6 = (nint)WidenCode(Unsafe.Add(ref at, 6));
            nint c7 = (nint)WidenCode(Unsafe.Add(ref at, 7));

            ref TValue into = ref Unsafe.Add(ref target, k);
            into = Unsafe.Add(ref source, c0);
            Unsafe.Add(ref into, 1) = Unsafe.Add(ref source, c1);
            Unsafe.Add(ref into, 2) = Unsafe.Add(ref source, c2);
            Unsafe.Add(ref into, 3) = Unsafe.Add(ref source, c3);
            Unsafe.Add(ref into, 4) = Unsafe.Add(ref source, c4);
            Unsafe.Add(ref into, 5) = Unsafe.Add(ref source, c5);
            Unsafe.Add(ref into, 6) = Unsafe.Add(ref source, c6);
            Unsafe.Add(ref into, 7) = Unsafe.Add(ref source, c7);

            if (TValidity.Flagged)
            {
                uint mask = Unsafe.Add(ref flags, c0) |
                    ((uint)Unsafe.Add(ref flags, c1) << 1) |
                    ((uint)Unsafe.Add(ref flags, c2) << 2) |
                    ((uint)Unsafe.Add(ref flags, c3) << 3) |
                    ((uint)Unsafe.Add(ref flags, c4) << 4) |
                    ((uint)Unsafe.Add(ref flags, c5) << 5) |
                    ((uint)Unsafe.Add(ref flags, c6) << 6) |
                    ((uint)Unsafe.Add(ref flags, c7) << 7);
                output |= (ulong)mask << k;
            }
        }

        for (; k < count; k++)
        {
            nint code = (nint)WidenCode(Unsafe.Add(ref codes, k));
            Unsafe.Add(ref target, k) = Unsafe.Add(ref source, code);
            if (TValidity.Flagged)
            {
                output |= (ulong)Unsafe.Add(ref flags, code) << k;
            }
        }

        return TValidity.Flagged ? output : ulong.MaxValue;
    }

    private static bool AllBelow<TCode>(ref TCode first, int count, uint limit)
        where TCode : unmanaged
    {
        bool signed = typeof(TCode) == typeof(sbyte) || typeof(TCode) == typeof(short) ||
            typeof(TCode) == typeof(int) || typeof(TCode) == typeof(long);
        int bits = Unsafe.SizeOf<TCode>() * 8;
        ulong span = signed ? 1UL << (bits - 1) : bits == 64 ? ulong.MaxValue : 1UL << bits;
        ulong bound = Math.Min(limit, span);
        return Unsafe.SizeOf<TCode>() switch
        {
            1 => Below(ref Unsafe.As<TCode, byte>(ref first), count, bound),
            2 => Below(ref Unsafe.As<TCode, ushort>(ref first), count, bound),
            4 => Below(ref Unsafe.As<TCode, uint>(ref first), count, bound),
            _ => Below(ref Unsafe.As<TCode, ulong>(ref first), count, bound),
        };
    }

    private static bool Below<T>(ref T first, int count, ulong bound)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>, IMinMaxValue<T>
    {
        if (bound > ulong.CreateTruncating(T.MaxValue))
        {
            return true;
        }

        T limit = T.CreateTruncating(bound);
        Vector128<T> limits = Vector128.Create(limit);
        Vector128<T> past = Vector128<T>.Zero;
        int k = 0;
        for (; k <= count - Vector128<T>.Count; k += Vector128<T>.Count)
        {
            past |= Vector128.GreaterThanOrEqual(Vector128.LoadUnsafe(ref first, (nuint)k), limits);
        }

        bool inside = past == Vector128<T>.Zero;
        for (; k < count; k++)
        {
            inside &= Unsafe.Add(ref first, k) < limit;
        }

        return inside;
    }

    private static int FirstFault<TCode>(ReadOnlySpan<TCode> codes, ulong valid, uint limit, int row, int span)
        where TCode : unmanaged
    {
        for (int k = 0; k < span; k++)
        {
            if (((valid >> k) & 1) != 0 && WidenCode(codes[row + k]) >= limit)
            {
                return row + k;
            }
        }

        return row;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint WidenCode<TCode>(TCode code)
        where TCode : unmanaged
    {
        if (typeof(TCode) == typeof(byte))
        {
            return Unsafe.As<TCode, byte>(ref code);
        }

        if (typeof(TCode) == typeof(ushort))
        {
            return Unsafe.As<TCode, ushort>(ref code);
        }

        return Unsafe.As<TCode, uint>(ref code);
    }

    private interface IValueValidity
    {
        static abstract bool Flagged { get; }
    }

    private readonly struct AllValuesValid : IValueValidity
    {
        public static bool Flagged => false;
    }

    private readonly struct ValuesFlagged : IValueValidity
    {
        public static bool Flagged => true;
    }
}
