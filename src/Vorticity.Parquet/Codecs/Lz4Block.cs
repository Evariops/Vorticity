using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Parquet.Codecs;

/// <summary>
/// The LZ4 block format, Parquet's <c>LZ4_RAW</c>: sequences of a token, literals and a match, as
/// lz4's <c>lz4_Block_format.md</c> describes them, a page fed whole with no framing.
/// </summary>
/// <remarks>
/// <para>
/// The decoder writes into a destination of exactly the page's uncompressed length and refuses a
/// literal or a match past either end, and an offset of zero or before the start; inside those
/// checks it copies sixteen bytes at a time where both sides hold sixteen.
/// </para>
/// <para>
/// The encoder is this library's own: a hash of four bytes into a table of positions, greedy
/// matches extended a word at a time, and the block's end rules kept, the last five bytes literals
/// and no match starting within the last twelve. Its output is a valid block, the same for the same
/// input, and makes no claim to match lz4's bytes.
/// </para>
/// </remarks>
internal static class Lz4Block
{
    private const int MinMatch = 4;
    private const int LastLiterals = 5;
    private const int MatchLimit = 12;
    private const int MaxOffset = 65535;
    private const int HashBits = 16;

    /// <summary>The most bytes <paramref name="length"/> bytes compress to.</summary>
    internal static int MaxCompressedLength(int length) => length + length / 255 + 16;

    /// <summary>Decompresses <paramref name="source"/> into all of <paramref name="destination"/>.</summary>
    internal static unsafe void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        fixed (byte* input = source)
        fixed (byte* output = destination)
        {
            byte* ip = input;
            byte* ipEnd = input + source.Length;
            byte* op = output;
            byte* opEnd = output + destination.Length;
            while (true)
            {
                if (ip >= ipEnd)
                {
                    ThrowCorrupt();
                }

                uint token = *ip++;
                nuint literals = token >> 4;
                if (literals == 15)
                {
                    literals += ReadLength(ref ip, ipEnd);
                }

                if ((nuint)(ipEnd - ip) < literals || (nuint)(opEnd - op) < literals)
                {
                    ThrowCorrupt();
                }

                if (literals <= 16 && ipEnd - ip >= 16 && opEnd - op >= 16)
                {
                    Vector128.Store(Vector128.Load(ip), op);
                }
                else
                {
                    Buffer.MemoryCopy(ip, op, literals, literals);
                }

                ip += literals;
                op += literals;
                if (ip == ipEnd)
                {
                    break;
                }

                if (ipEnd - ip < 2)
                {
                    ThrowCorrupt();
                }

                nuint offset = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(ip, 2));
                ip += 2;
                nuint length = token & 15;
                if (length == 15)
                {
                    length += ReadLength(ref ip, ipEnd);
                }

                length += MinMatch;
                if (offset == 0 || offset > (nuint)(op - output) || length > (nuint)(opEnd - op))
                {
                    ThrowCorrupt();
                }

                op = Copy(op, opEnd, offset, length);
            }

            if (op != opEnd)
            {
                ParquetThrow.Format("An LZ4 page holds fewer bytes than its header declares.");
            }
        }
    }

    /// <summary>An extended length: bytes of 255 continue it, the first byte below 255 ends it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe nuint ReadLength(ref byte* ip, byte* ipEnd)
    {
        nuint length = 0;
        uint part;
        do
        {
            if (ip >= ipEnd)
            {
                ThrowCorrupt();
            }

            part = *ip++;
            length += part;
        }
        while (part == 255);

        return length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte* Copy(byte* op, byte* opEnd, nuint offset, nuint length)
    {
        byte* from = op - offset;
        byte* end = op + length;
        if (offset >= 16 && (nuint)(opEnd - op) >= length + 16)
        {
            do
            {
                Vector128.Store(Vector128.Load(from), op);
                from += 16;
                op += 16;
            }
            while (op < end);

            return end;
        }

        if (offset >= 8 && (nuint)(opEnd - op) >= length + 8)
        {
            do
            {
                Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<ulong>(from));
                from += 8;
                op += 8;
            }
            while (op < end);

            return end;
        }

        while (op < end)
        {
            *op++ = *from++;
        }

        return end;
    }

    /// <summary>Compresses <paramref name="source"/> into <paramref name="destination"/>, which holds at least <see cref="MaxCompressedLength"/>, and returns the bytes written.</summary>
    internal static unsafe int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < MaxCompressedLength(source.Length))
        {
            throw new ArgumentException("The destination is shorter than the longest a compression can take.", nameof(destination));
        }

        // A table no larger than the input needs: clearing 256 KiB for every small page would cost
        // more than compressing it.
        int bits = source.Length < 1 << 14 ? 12 : source.Length < 1 << 17 ? 14 : HashBits;
        int[] table = System.Buffers.ArrayPool<int>.Shared.Rent(1 << bits);
        try
        {
            Array.Fill(table, -1, 0, 1 << bits);
            fixed (byte* input = source)
            fixed (byte* output = destination)
            fixed (int* positions = table)
            {
                return (int)(CompressCore(input, source.Length, output, positions, bits) - output);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(table);
        }
    }

    private static unsafe byte* CompressCore(byte* input, int length, byte* op, int* table, int bits)
    {
        byte* end = input + length;
        byte* literal = input;
        if (length >= MatchLimit + 1)
        {
            byte* matchLimit = end - MatchLimit;
            byte* ip = input;
            int misses = 0;
            while (ip < matchLimit)
            {
                uint sequence = Unsafe.ReadUnaligned<uint>(ip);
                int hash = (int)((sequence * 2654435761u) >> (32 - bits));
                int candidate = table[hash];
                table[hash] = (int)(ip - input);
                if (candidate < 0 || (ip - input) - candidate > MaxOffset || Unsafe.ReadUnaligned<uint>(input + candidate) != sequence)
                {
                    // Faster the longer nothing matches, as lz4's own fast mode steps: one byte at a
                    // time for the first 64 misses, then two, and so on.
                    ip += 1 + (misses++ >> 6);
                    continue;
                }

                misses = 0;
                byte* match = input + candidate;
                int matched = MinMatch + MatchLength(match + MinMatch, ip + MinMatch, end - LastLiterals);
                op = EmitSequence(op, literal, (int)(ip - literal), (int)(ip - match), matched);
                ip += matched;
                literal = ip;
            }
        }

        return EmitLast(op, literal, (int)(end - literal));
    }

    private static unsafe int MatchLength(byte* a, byte* b, byte* end)
    {
        int matched = 0;
        while (b + 8 <= end)
        {
            ulong diff = Unsafe.ReadUnaligned<ulong>(a) ^ Unsafe.ReadUnaligned<ulong>(b);
            if (diff != 0)
            {
                return matched + (BitOperations.TrailingZeroCount(diff) >> 3);
            }

            a += 8;
            b += 8;
            matched += 8;
        }

        while (b < end && *a == *b)
        {
            a++;
            b++;
            matched++;
        }

        return matched;
    }

    private static unsafe byte* EmitSequence(byte* op, byte* literal, int literals, int offset, int matched)
    {
        int matchCode = matched - MinMatch;
        byte* token = op++;
        *token = (byte)((Math.Min(literals, 15) << 4) | Math.Min(matchCode, 15));
        op = WriteLength(op, literals);
        Buffer.MemoryCopy(literal, op, literals, literals);
        op += literals;
        *op++ = (byte)offset;
        *op++ = (byte)(offset >> 8);
        return WriteLength(op, matchCode);
    }

    private static unsafe byte* EmitLast(byte* op, byte* literal, int literals)
    {
        *op++ = (byte)(Math.Min(literals, 15) << 4);
        op = WriteLength(op, literals);
        Buffer.MemoryCopy(literal, op, literals, literals);
        return op + literals;
    }

    /// <summary>The part of a length past 15, as bytes of 255 and a last byte below it.</summary>
    private static unsafe byte* WriteLength(byte* op, int length)
    {
        if (length < 15)
        {
            return op;
        }

        length -= 15;
        while (length >= 255)
        {
            *op++ = 255;
            length -= 255;
        }

        *op++ = (byte)length;
        return op;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCorrupt() => throw new ParquetFormatException("An LZ4 page is corrupt: a literal or a match reaches past its data.");
}
