using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Benchmarks;

/// <summary>
/// The dictionary gathers of sixteen-byte values as they were, kept here unchanged as the baseline
/// every change to <c>RowKernels.Gather</c> and <c>RowKernels.GatherMasked</c> is measured against
/// in the same process: every code valid, nullable codes, and nullable values.
/// </summary>
/// <remarks>
/// Self-contained on purpose: nothing here calls into the library, so editing the library's kernels
/// can never move this arm.
/// </remarks>
internal static class DictGatherOriginal
{
    /// <summary>Every code valid: eight rows a step, each code checked.</summary>
    internal static int Gather<TCode>(ReadOnlySpan<TCode> codes, ReadOnlySpan<Vector128<byte>> source, Span<Vector128<byte>> target)
        where TCode : unmanaged
    {
        uint limit = (uint)source.Length;
        ref TCode codeRef = ref MemoryMarshal.GetReference(codes);
        ref Vector128<byte> sourceRef = ref MemoryMarshal.GetReference(source);
        ref Vector128<byte> targetRef = ref MemoryMarshal.GetReference(target);
        int whole = target.Length & ~7;
        for (int block = 0; block < whole; block += 8)
        {
            ref TCode at = ref Unsafe.Add(ref codeRef, block);
            uint c0 = WidenCode(at);
            uint c1 = WidenCode(Unsafe.Add(ref at, 1));
            uint c2 = WidenCode(Unsafe.Add(ref at, 2));
            uint c3 = WidenCode(Unsafe.Add(ref at, 3));
            uint c4 = WidenCode(Unsafe.Add(ref at, 4));
            uint c5 = WidenCode(Unsafe.Add(ref at, 5));
            uint c6 = WidenCode(Unsafe.Add(ref at, 6));
            uint c7 = WidenCode(Unsafe.Add(ref at, 7));
            if (c0 >= limit || c1 >= limit || c2 >= limit || c3 >= limit ||
                c4 >= limit || c5 >= limit || c6 >= limit || c7 >= limit)
            {
                return block;
            }

            ref Vector128<byte> into = ref Unsafe.Add(ref targetRef, block);
            into = Unsafe.Add(ref sourceRef, (nint)c0);
            Unsafe.Add(ref into, 1) = Unsafe.Add(ref sourceRef, (nint)c1);
            Unsafe.Add(ref into, 2) = Unsafe.Add(ref sourceRef, (nint)c2);
            Unsafe.Add(ref into, 3) = Unsafe.Add(ref sourceRef, (nint)c3);
            Unsafe.Add(ref into, 4) = Unsafe.Add(ref sourceRef, (nint)c4);
            Unsafe.Add(ref into, 5) = Unsafe.Add(ref sourceRef, (nint)c5);
            Unsafe.Add(ref into, 6) = Unsafe.Add(ref sourceRef, (nint)c6);
            Unsafe.Add(ref into, 7) = Unsafe.Add(ref sourceRef, (nint)c7);
        }

        for (int row = whole; row < target.Length; row++)
        {
            uint code = WidenCode(Unsafe.Add(ref codeRef, row));
            if (code >= limit)
            {
                return row;
            }

            Unsafe.Add(ref targetRef, row) = Unsafe.Add(ref sourceRef, (nint)code);
        }

        return -1;
    }

    /// <summary>Every code valid over nullable values: a flag byte an entry, the output bit a row.</summary>
    internal static int GatherNullableValues<TCode>(
        ReadOnlySpan<TCode> codes, ReadOnlySpan<Vector128<byte>> source, Span<Vector128<byte>> target,
        ReadOnlySpan<byte> valueBits, Span<byte> outputBits)
        where TCode : unmanaged
    {
        byte[] flags = ArrayPool<byte>.Shared.Rent(source.Length);
        try
        {
            for (int entry = 0; entry < source.Length; entry++)
            {
                flags[entry] = (byte)((valueBits[entry >> 3] >> (entry & 7)) & 1);
            }

            uint limit = (uint)source.Length;
            ref TCode codeRef = ref MemoryMarshal.GetReference(codes);
            ref Vector128<byte> sourceRef = ref MemoryMarshal.GetReference(source);
            ref Vector128<byte> targetRef = ref MemoryMarshal.GetReference(target);
            ref byte flagRef = ref MemoryMarshal.GetArrayDataReference(flags);
            int whole = target.Length & ~7;
            for (int block = 0; block < whole; block += 8)
            {
                int mask = 0;
                for (int k = 0; k < 8; k++)
                {
                    int row = block + k;
                    uint code = WidenCode(Unsafe.Add(ref codeRef, row));
                    if (code >= limit)
                    {
                        return row;
                    }

                    Unsafe.Add(ref targetRef, row) = Unsafe.Add(ref sourceRef, (nint)code);
                    mask |= Unsafe.Add(ref flagRef, (nint)code) << k;
                }

                outputBits[block >> 3] = (byte)mask;
            }

            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(flags);
        }
    }

    /// <summary>Nullable codes over nullable values: sixty-four rows of the codes' validity at a time.</summary>
    internal static int GatherNullableCodes<TCode>(
        ReadOnlySpan<TCode> codes, ReadOnlySpan<Vector128<byte>> source, Span<Vector128<byte>> target,
        ReadOnlySpan<byte> codeBits, ReadOnlySpan<byte> valueBits, Span<byte> outputBits)
        where TCode : unmanaged
    {
        uint limit = (uint)source.Length;
        byte[] flags = ArrayPool<byte>.Shared.Rent(source.Length);
        try
        {
            for (int entry = 0; entry < source.Length; entry++)
            {
                flags[entry] = (byte)((valueBits[entry >> 3] >> (entry & 7)) & 1);
            }

            ref TCode codeRef = ref MemoryMarshal.GetReference(codes);
            ref Vector128<byte> sourceRef = ref MemoryMarshal.GetReference(source);
            ref Vector128<byte> targetRef = ref MemoryMarshal.GetReference(target);
            ref byte flagRef = ref MemoryMarshal.GetArrayDataReference(flags);
            for (int row = 0; row < target.Length; row += 64)
            {
                int span = Math.Min(64, target.Length - row);
                ulong all = span == 64 ? ulong.MaxValue : (1UL << span) - 1;
                ulong valid = BinaryPrimitives.ReadUInt64LittleEndian(codeBits[(row >> 3)..]) & all;
                ref Vector128<byte> rows = ref Unsafe.Add(ref targetRef, row);
                ulong output = GatherWord(ref Unsafe.Add(ref codeRef, row), ref sourceRef, ref rows, ref flagRef, span, valid, limit, out bool faulted);
                if (faulted)
                {
                    return row;
                }

                for (ulong nulls = ~valid & all; nulls != 0; nulls &= nulls - 1)
                {
                    Unsafe.Add(ref rows, BitOperations.TrailingZeroCount(nulls)) = default;
                }

                Span<byte> bytes = outputBits.Slice(row >> 3, (span + 7) >> 3);
                for (int b = 0; b < bytes.Length; b++)
                {
                    bytes[b] = (byte)(output >> (b * 8));
                }
            }

            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(flags);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong GatherWord<TCode>(
        ref TCode codes, ref Vector128<byte> source, ref Vector128<byte> target, ref byte flags, int count, ulong valid,
        uint limit, out bool faulted)
        where TCode : unmanaged
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
            output |= (ulong)(bit & Unsafe.Add(ref flags, (nint)code)) << k;
        }

        faulted = fault != 0;
        return output;
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

        if (typeof(TCode) == typeof(uint))
        {
            return Unsafe.As<TCode, uint>(ref code);
        }

        ulong value = Unsafe.As<TCode, ulong>(ref code);
        return value > uint.MaxValue ? uint.MaxValue : (uint)value;
    }
}
