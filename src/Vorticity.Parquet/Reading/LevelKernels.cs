using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Arrays.Decoders.Canonical;

namespace Vorticity.Parquet.Reading;

/// <summary>The kernels that turn a batch's levels, a byte per entry, into the bits of its nodes.</summary>
internal static class LevelKernels
{
    /// <summary>
    /// Sets a bit of <paramref name="bits"/>, zeroed, per level that reaches
    /// <paramref name="threshold"/>, least significant first; the bits set. Thirty-two levels at a
    /// time where the hardware compares them in one instruction.
    /// </summary>
    internal static int AtLeast(ReadOnlySpan<byte> levels, int threshold, Span<byte> bits)
    {
        int length = levels.Length;
        if (threshold <= 0)
        {
            BitmapKernels.SetRange(bits, 0, length);
            return length;
        }

        if (threshold > byte.MaxValue)
        {
            return 0;
        }

        int i = 0;
        int count = 0;
        ref byte input = ref MemoryMarshal.GetReference(levels);

        // Unsigned bytes: a level reaches the threshold where it is greater than the one below it.
        byte below = (byte)(threshold - 1);
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> limit = Vector256.Create(below);
            for (; i <= length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                uint mask = Vector256.GreaterThan(Vector256.LoadUnsafe(ref input, (nuint)i), limit).ExtractMostSignificantBits();
                BinaryPrimitives.WriteUInt32LittleEndian(bits.Slice(i >> 3, sizeof(uint)), mask);
                count += BitOperations.PopCount(mask);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> limit = Vector128.Create(below);
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                uint mask = Vector128.GreaterThan(Vector128.LoadUnsafe(ref input, (nuint)i), limit).ExtractMostSignificantBits();
                BinaryPrimitives.WriteUInt16LittleEndian(bits.Slice(i >> 3, sizeof(ushort)), (ushort)mask);
                count += BitOperations.PopCount(mask);
            }
        }

        for (; i < length; i++)
        {
            if (levels[i] > below)
            {
                bits[i >> 3] |= (byte)(1 << (i & 7));
                count++;
            }
        }

        return count;
    }
}
