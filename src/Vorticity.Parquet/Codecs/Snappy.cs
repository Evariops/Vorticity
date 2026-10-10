using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Vorticity.Serialization;

namespace Vorticity.Parquet.Codecs;

/// <summary>
/// The raw Snappy format, read and written: a varint of the uncompressed length, then literals and
/// copies, as snappy's <c>format_description.txt</c> describes them. Parquet feeds a page to the
/// codec whole, with no framing.
/// </summary>
/// <remarks>
/// <para>
/// The decoder writes into a destination of exactly the declared length and refuses everything a
/// hostile stream can claim: a length other than the destination's, a literal or a copy past either
/// end, an offset of zero or before the start. Where the input holds sixty-four bytes past a tag and
/// the output eighty, an element is read from a table of its tag and copied whole a vector at a time,
/// its offset the one length checked; elsewhere each length is checked, and a short pattern is
/// repeated by doubling it.
/// </para>
/// <para>
/// The encoder is this library's own: blocks of 64 KiB, a hash of four bytes into a table of the
/// block's positions, greedy matches extended a word at a time, and the skip snappy uses to cross
/// incompressible bytes quickly. Its output is a valid stream, the same for the same input, and
/// makes no claim to match snappy's bytes.
/// </para>
/// </remarks>
internal static class Snappy
{
    private const int BlockSize = 1 << 16;
    private const int HashBits = 14;
    private const int InputMargin = 15;

    /// <summary>The input past a tag the decoder's fast elements read, whatever the tag: a literal's sixty-four bytes.</summary>
    private const int FastInput = 65;

    /// <summary>The output past an element's start the decoder's fast elements write: a copy of sixty-four, and a short pattern's eight past its end.</summary>
    private const int FastOutput = 80;

    /// <summary>
    /// Per tag of a copy of a one-byte or two-byte offset, what the decoder's fast elements read in
    /// place of the tag's fields: its length in the low byte, and above it the high bits a one-byte
    /// offset takes from the tag.
    /// </summary>
    private static readonly uint[] Elements = CopyElements();

    private static uint[] CopyElements()
    {
        uint[] elements = new uint[256];
        for (uint tag = 0; tag < 256; tag++)
        {
            elements[tag] = (tag & 3) switch
            {
                1 => (((tag >> 2) & 7) + 4) | ((tag >> 5) << 16),
                2 => (tag >> 2) + 1,
                _ => 0,
            };
        }

        return elements;
    }

    /// <summary>The most bytes <paramref name="length"/> bytes compress to.</summary>
    internal static int MaxCompressedLength(int length) => 32 + length + length / 6;

    /// <summary>The uncompressed length a stream declares in its preamble.</summary>
    internal static int UncompressedLength(ReadOnlySpan<byte> source)
    {
        int position = 0;
        return ReadLength(source, ref position);
    }

    /// <summary>Decompresses <paramref name="source"/> into all of <paramref name="destination"/>.</summary>
    internal static unsafe void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int start = 0;
        int declared = ReadLength(source, ref start);
        if (declared != destination.Length)
        {
            ThrowDeclared(declared, destination.Length);
        }

        fixed (byte* input = source)
        fixed (byte* output = destination)
        {
            byte* ip = input + start;
            byte* ipEnd = input + source.Length;
            byte* op = output;
            byte* opEnd = output + destination.Length;
            while (ip < ipEnd)
            {
                // As far as the elements go without checks of their own lengths, then one with them.
                (nint fastIn, nint fastOut) = FastElements(ip, ipEnd - FastInput, op, opEnd - FastOutput, output);
                ip = (byte*)fastIn;
                op = (byte*)fastOut;
                if (ip >= ipEnd)
                {
                    break;
                }

                uint tag = *ip++;
                nuint length;
                nuint offset;
                switch (tag & 3)
                {
                    case 0:
                        length = (tag >> 2) + 1;
                        if (length > 60)
                        {
                            int extra = (int)length - 60;
                            if (ipEnd - ip < extra)
                            {
                                ThrowCorrupt();
                            }

                            uint value = 0;
                            for (int i = 0; i < extra; i++)
                            {
                                value |= (uint)ip[i] << (8 * i);
                            }

                            ip += extra;
                            length = (nuint)value + 1;
                        }

                        if ((nuint)(ipEnd - ip) < length || (nuint)(opEnd - op) < length)
                        {
                            ThrowCorrupt();
                        }

                        if (length <= 16 && ipEnd - ip >= 16 && opEnd - op >= 16)
                        {
                            Vector128.Store(Vector128.Load(ip), op);
                        }
                        else
                        {
                            Buffer.MemoryCopy(ip, op, length, length);
                        }

                        ip += length;
                        op += length;
                        continue;
                    case 1:
                        if (ip >= ipEnd)
                        {
                            ThrowCorrupt();
                        }

                        length = ((tag >> 2) & 7) + 4;
                        offset = ((tag >> 5) << 8) | *ip++;
                        break;
                    case 2:
                        if (ipEnd - ip < 2)
                        {
                            ThrowCorrupt();
                        }

                        length = (tag >> 2) + 1;
                        offset = Unsafe.ReadUnaligned<ushort>(ip);
                        if (!BitConverter.IsLittleEndian)
                        {
                            offset = BinaryPrimitives.ReverseEndianness((ushort)offset);
                        }

                        ip += 2;
                        break;
                    default:
                        if (ipEnd - ip < 4)
                        {
                            ThrowCorrupt();
                        }

                        length = (tag >> 2) + 1;
                        offset = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ip, 4));
                        ip += 4;
                        break;
                }

                if (offset == 0 || offset > (nuint)(op - output) || length > (nuint)(opEnd - op))
                {
                    ThrowCorrupt();
                }

                op = Copy(op, opEnd, offset, length);
            }

            if (op != opEnd)
            {
                ParquetThrow.Format("A Snappy page holds fewer bytes than it declares.");
            }
        }
    }

    /// <summary>
    /// Decodes elements from <paramref name="ip"/> while their tag starts before
    /// <paramref name="ipFast"/> and their output before <paramref name="opFast"/>, up to a literal
    /// longer than sixty bytes or a copy of a four-byte offset; where the input and the output are then.
    /// </summary>
    /// <remarks>
    /// Past a tag that starts before <paramref name="ipFast"/> lie the sixty-four bytes a literal
    /// and a copy's offset read whatever the tag says, and past an output before
    /// <paramref name="opFast"/> the eighty a copy of sixty-four writes whole and a short pattern
    /// eight past its end: an element is read from tables in place of its tag's fields and copied a
    /// vector at a time, with no check of its own lengths but its offset's. A method apart from the
    /// elements with checks, so that its few values live in registers.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe (nint In, nint Out) FastElements(byte* ip, byte* ipFast, byte* op, byte* opFast, byte* output)
    {
        ref uint elements = ref MemoryMarshal.GetArrayDataReference(Elements);
        while (ip < ipFast && op < opFast)
        {
            uint tag = *ip;
            uint type = tag & 3;
            if (type == 0)
            {
                nuint literal = (tag >> 2) + 1;
                if (literal > 60)
                {
                    break;
                }

                Vector128.Store(Vector128.Load(ip + 1), op);
                if (literal > 16)
                {
                    Vector128.Store(Vector128.Load(ip + 17), op + 16);
                    Vector128.Store(Vector128.Load(ip + 33), op + 32);
                    Vector128.Store(Vector128.Load(ip + 49), op + 48);
                }

                ip += literal + 1;
                op += literal;
                continue;
            }

            // A four-byte offset, which no block of snappy's 64 KiB needs, goes with the checks: the
            // next tag is then the tag and one or two bytes on, which each element waits for.
            if (type == 3)
            {
                break;
            }

            // The offset's one or two bytes, under the high bits a one-byte offset takes from the tag.
            uint element = Unsafe.Add(ref elements, (nint)tag);
            nuint offset = (nuint)(Unsafe.ReadUnaligned<uint>(ip + 1) & ((1u << (int)(8 * type)) - 1)) | (element >> 8);
            if (offset - 1 >= (nuint)(op - output))
            {
                throw Corrupt();
            }

            ip += type + 1;
            byte* from = op - offset;
            nuint length = element & 0xFF;
            if (offset >= 16)
            {
                // Each sixteen bytes read lie before those written: the copy's own, already
                // written, where it repeats itself.
                Vector128.Store(Vector128.Load(from), op);
                Vector128.Store(Vector128.Load(from + 16), op + 16);
                Vector128.Store(Vector128.Load(from + 32), op + 32);
                Vector128.Store(Vector128.Load(from + 48), op + 48);
                op += length;
                continue;
            }

            // A pattern shorter than a vector, doubled in place until it is a word, then a word at
            // a time: each eight bytes read lie before those written.
            byte* end = op + length;
            while ((nuint)(op - from) < 8)
            {
                Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<ulong>(from));
                op += op - from;
            }

            while (op < end)
            {
                Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<ulong>(from));
                from += 8;
                op += 8;
            }

            op = end;
        }

        return ((nint)ip, (nint)op);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowDeclared(int declared, int length) =>
        ParquetThrow.Format($"A Snappy page declares {declared} bytes where its header declares {length}.");

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="offset"/> back, which may overlap
    /// what it writes: sixteen bytes at a time when the source is that far behind and the output
    /// has room, the pattern doubled first when it is shorter.
    /// </summary>
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

        if ((nuint)(opEnd - op) >= length + 16)
        {
            // A pattern shorter than a word is doubled in place until it is one: each copy of eight
            // bytes reads the pattern as far as it is written, and lays down as many bytes as the
            // distance between the two, which then doubles.
            while ((nuint)(op - from) < 8)
            {
                Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<ulong>(from));
                op += op - from;
            }

            while (op < end)
            {
                Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<ulong>(from));
                from += 8;
                op += 8;
            }

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

        int written = Varint.Write(destination, (uint)source.Length);
        Span<ushort> table = stackalloc ushort[1 << HashBits];
        fixed (byte* input = source)
        fixed (byte* output = destination)
        {
            byte* op = output + written;
            for (int blockStart = 0; blockStart < source.Length; blockStart += BlockSize)
            {
                int blockLength = Math.Min(BlockSize, source.Length - blockStart);
                op = CompressBlock(input + blockStart, blockLength, op, table);
            }

            return (int)(op - output);
        }
    }

    /// <summary>One block of at most 64 KiB, so that every offset fits the table's 16 bits.</summary>
    private static unsafe byte* CompressBlock(byte* block, int length, byte* op, Span<ushort> table)
    {
        byte* end = block + length;
        byte* literal = block;
        if (length >= InputMargin + 1)
        {
            table.Clear();
            byte* limit = end - InputMargin;
            byte* ip = block + 1;
            uint next = Hash(Unsafe.ReadUnaligned<uint>(ip));
            while (true)
            {
                // Probe forward, faster the longer nothing matches: one byte at a time for the first
                // 32 probes, then two, and so on.
                byte* candidate;
                uint skip = 32;
                byte* nextIp = ip;
                do
                {
                    ip = nextIp;
                    uint hash = next;
                    nextIp = ip + (skip++ >> 5);
                    if (nextIp > limit)
                    {
                        goto Emit;
                    }

                    next = Hash(Unsafe.ReadUnaligned<uint>(nextIp));
                    candidate = block + table[(int)hash];
                    table[(int)hash] = (ushort)(ip - block);
                }
                while (Unsafe.ReadUnaligned<uint>(ip) != Unsafe.ReadUnaligned<uint>(candidate));

                op = EmitLiteral(op, literal, (int)(ip - literal));

                // Emit the match and every match that follows it at once.
                do
                {
                    byte* matchStart = ip;
                    int matched = 4 + MatchLength(candidate + 4, ip + 4, end);
                    ip += matched;
                    op = EmitCopy(op, (int)(matchStart - candidate), matched);
                    literal = ip;
                    if (ip >= limit)
                    {
                        goto Emit;
                    }

                    // Insert the position before the match's end and probe at its end.
                    table[(int)Hash(Unsafe.ReadUnaligned<uint>(ip - 1))] = (ushort)(ip - 1 - block);
                    uint hash = Hash(Unsafe.ReadUnaligned<uint>(ip));
                    candidate = block + table[(int)hash];
                    table[(int)hash] = (ushort)(ip - block);
                }
                while (Unsafe.ReadUnaligned<uint>(ip) == Unsafe.ReadUnaligned<uint>(candidate));

                next = Hash(Unsafe.ReadUnaligned<uint>(ip + 1));
                ip++;
            }
        }

    Emit:
        if (literal < end)
        {
            op = EmitLiteral(op, literal, (int)(end - literal));
        }

        return op;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(uint bytes) => (bytes * 0x1E35A7BDu) >> (32 - HashBits);

    /// <summary>How many more bytes match, a word at a time, without reading past <paramref name="end"/>.</summary>
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

    private static unsafe byte* EmitLiteral(byte* op, byte* literal, int length)
    {
        if (length == 0)
        {
            return op;
        }

        int n = length - 1;
        if (n < 60)
        {
            *op++ = (byte)(n << 2);
        }
        else
        {
            int bytes = n < 1 << 8 ? 1 : n < 1 << 16 ? 2 : n < 1 << 24 ? 3 : 4;
            *op++ = (byte)((59 + bytes) << 2);
            for (int i = 0; i < bytes; i++)
            {
                *op++ = (byte)(n >> (8 * i));
            }
        }

        Buffer.MemoryCopy(literal, op, length, length);
        return op + length;
    }

    /// <summary>A copy of any length, cut into copies of at most 64 bytes; the one-byte offset form where it fits.</summary>
    private static unsafe byte* EmitCopy(byte* op, int offset, int length)
    {
        while (length >= 68)
        {
            op = EmitCopyAtMost64(op, offset, 64);
            length -= 64;
        }

        if (length > 64)
        {
            op = EmitCopyAtMost64(op, offset, 60);
            length -= 60;
        }

        return EmitCopyAtMost64(op, offset, length);
    }

    private static unsafe byte* EmitCopyAtMost64(byte* op, int offset, int length)
    {
        if (length < 12 && offset < 2048)
        {
            *op++ = (byte)(1 | ((length - 4) << 2) | ((offset >> 8) << 5));
            *op++ = (byte)offset;
        }
        else
        {
            *op++ = (byte)(2 | ((length - 1) << 2));
            *op++ = (byte)offset;
            *op++ = (byte)(offset >> 8);
        }

        return op;
    }

    private static int ReadLength(ReadOnlySpan<byte> source, ref int position)
    {
        uint length = Varint.Read32<LengthErrors>(source, ref position);
        if (length > int.MaxValue)
        {
            ThrowCorrupt();
        }

        return (int)length;
    }

    /// <summary>The preamble's refusals.</summary>
    private readonly struct LengthErrors : IVarintErrors
    {
        public static ulong Truncated(int position) => ParquetThrow.Format<ulong>("A Snappy page ends inside its length.");

        public static ulong Malformed(int position, byte value) => ParquetThrow.Format<ulong>("A Snappy page declares a length past 32 bits.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowCorrupt() => throw Corrupt();

    /// <summary>What a corrupt stream throws, made apart from the loop that throws it, which then holds nothing past the throw.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ParquetFormatException Corrupt() => new("A Snappy page is corrupt: a literal or a copy reaches past its data.");

}
