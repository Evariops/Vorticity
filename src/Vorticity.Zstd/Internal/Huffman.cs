using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// A Huffman decoding table for the literals: one entry per value of the next <see cref="TableLog"/>
/// bits, holding the symbol they start with and the length of its code.
/// </summary>
/// <remarks>
/// An entry is libzstd's <c>HUF_DEltX1</c> as a little-endian <see cref="ushort"/>: the code length in
/// the low byte, the symbol in the high byte. A table narrower than 11 bits is widened to 11, as
/// libzstd does for its fast decoder: entries are replicated, codes do not change.
/// </remarks>
internal sealed class HuffmanTable
{
    /// <summary>The longest code a tree may describe; libzstd's <c>HUF_TABLELOG_MAX</c>.</summary>
    public const int MaxTableLog = 12;

    /// <summary>The width every narrower table is widened to.</summary>
    public const int FastTableLog = 11;

    public const int MaxSymbols = 256;

    public readonly ushort[] Entries = new ushort[1 << MaxTableLog];

    /// <summary>The number of bits an entry is looked up with.</summary>
    public int TableLog;

    /// <summary>
    /// libzstd's <c>HUF_readDTableX1_wksp</c>: reads a tree description and builds the table.
    /// </summary>
    /// <returns>The bytes the description takes.</returns>
    public int Read(ReadOnlySpan<byte> source)
    {
        Span<byte> weights = stackalloc byte[MaxSymbols];
        Span<int> rankCount = stackalloc int[MaxTableLog + 1];
        int size = ReadWeights(source, weights, rankCount, out int symbolCount, out int tableLog);

        // Widen to the fast width: every weight but 0 moves up by the difference.
        if (tableLog < FastTableLog)
        {
            int scale = FastTableLog - tableLog;
            for (int s = 0; s < symbolCount; s++)
            {
                if (weights[s] != 0)
                {
                    weights[s] += (byte)scale;
                }
            }

            for (int w = FastTableLog; w > scale; w--)
            {
                rankCount[w] = rankCount[w - scale];
            }

            for (int w = scale; w > 0; w--)
            {
                rankCount[w] = 0;
            }

            tableLog = FastTableLog;
        }

        // Codes are assigned by increasing weight, then by symbol: the symbols of weight w take
        // 2^(w-1) consecutive entries each, starting with the lowest weight.
        Span<int> rankStart = stackalloc int[MaxTableLog + 2];
        int next = 0;
        for (int w = 1; w <= tableLog; w++)
        {
            rankStart[w] = next;
            next += rankCount[w] << (w - 1);
        }

        ushort[] entries = Entries;
        for (int s = 0; s < symbolCount; s++)
        {
            int w = weights[s];
            if (w == 0)
            {
                continue;
            }

            int length = 1 << (w - 1);
            ushort entry = (ushort)((s << 8) | (tableLog + 1 - w));
            entries.AsSpan(rankStart[w], length).Fill(entry);
            rankStart[w] += length;
        }

        TableLog = tableLog;
        return size;
    }

    /// <summary>
    /// libzstd's <c>HUF_readStats</c>: the weights of a tree, the last one implied.
    /// </summary>
    /// <returns>The bytes the description takes.</returns>
    private static int ReadWeights(ReadOnlySpan<byte> source, Span<byte> weights, Span<int> rankCount, out int symbolCount, out int tableLog)
    {
        const ZstdError error = ZstdError.HuffmanTable;
        if (source.IsEmpty)
        {
            Throw.Error(error);
        }

        int headerByte = source[0];
        int inputSize;
        int count;
        if (headerByte >= 128)
        {
            // Direct representation: 4 bits a weight, two a byte, the first in the high nibble.
            count = headerByte - 127;
            inputSize = (count + 1) / 2;
            if (inputSize + 1 > source.Length)
            {
                Throw.Error(error);
            }

            for (int n = 0; n < count; n += 2)
            {
                byte b = source[1 + (n / 2)];
                weights[n] = (byte)(b >> 4);
                weights[n + 1] = (byte)(b & 15);
            }
        }
        else
        {
            inputSize = headerByte;
            if (inputSize + 1 > source.Length)
            {
                Throw.Error(error);
            }

            count = Fse.DecodeHuffmanWeights(source.Slice(1, inputSize), weights);
        }

        rankCount.Clear();
        uint weightTotal = 0;
        for (int n = 0; n < count; n++)
        {
            int w = weights[n];
            if (w > MaxTableLog)
            {
                Throw.Error(error);
            }

            rankCount[w]++;
            weightTotal += (1u << w) >> 1;
        }

        if (weightTotal == 0)
        {
            Throw.Error(error);
        }

        tableLog = BackwardBitReader.HighBit(weightTotal) + 1;
        if (tableLog > MaxTableLog)
        {
            Throw.Error(error);
        }

        // The last weight completes the total to a power of two, which only one weight can do.
        uint rest = (1u << tableLog) - weightTotal;
        int restBit = BackwardBitReader.HighBit(rest);
        if ((1u << restBit) != rest)
        {
            Throw.Error(error);
        }

        int lastWeight = restBit + 1;
        weights[count] = (byte)lastWeight;
        rankCount[lastWeight]++;

        // A valid tree has an even number of codes of the longest length, at least two.
        if (rankCount[1] < 2 || (rankCount[1] & 1) != 0)
        {
            Throw.Error(error);
        }

        symbolCount = count + 1;
        return inputSize + 1;
    }

    /// <summary>
    /// libzstd's <c>HUF_decompress1X1_usingDTable_internal</c>: one stream filling all of
    /// <paramref name="output"/>.
    /// </summary>
    public void DecodeSingleStream(ReadOnlySpan<byte> source, Span<byte> output)
    {
        var bits = new BackwardBitReader(source, ZstdError.HuffmanStream);
        DecodeStream(ref bits, output);
        if (!bits.IsEndOfStream)
        {
            Throw.Error(ZstdError.HuffmanStream);
        }
    }

    /// <summary>
    /// libzstd's <c>HUF_decompress4X1_usingDTable_internal</c>: a six-byte jump table, then four
    /// streams that each fill a quarter of <paramref name="output"/>, rounded up, the last one taking
    /// what remains. The fast loop takes them when the table is 11 bits wide and every stream holds
    /// eight bytes; otherwise, and to finish, one symbol at a time.
    /// </summary>
    public void DecodeFourStreams(ReadOnlySpan<byte> source, Span<byte> output)
    {
        const ZstdError error = ZstdError.HuffmanStream;
        if (source.Length < 10 || output.Length < 6)
        {
            Throw.Error(error);
        }

        int length1 = BinaryPrimitives.ReadUInt16LittleEndian(source);
        int length2 = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(2));
        int length3 = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(4));
        int length4 = source.Length - 6 - length1 - length2 - length3;
        if (length4 < 0)
        {
            Throw.Error(error);
        }

        int segment = (output.Length + 3) / 4;
        if (TableLog == FastTableLog && length1 >= 8 && length2 >= 8 && length3 >= 8 && length4 >= 8
            && 3 * segment < output.Length)
        {
            DecodeFourStreamsFast(source, output, length1, length2, length3, segment);
            return;
        }

        int start2 = 6 + length1;
        int start3 = start2 + length2;
        int start4 = start3 + length3;

        var bits1 = new BackwardBitReader(source.Slice(6, length1), error);
        var bits2 = new BackwardBitReader(source.Slice(start2, length2), error);
        var bits3 = new BackwardBitReader(source.Slice(start3, length3), error);
        var bits4 = new BackwardBitReader(source.Slice(start4, length4), error);

        DecodeStream(ref bits1, output.Slice(0, segment));
        DecodeStream(ref bits2, output.Slice(segment, segment));
        DecodeStream(ref bits3, output.Slice(2 * segment, segment));
        DecodeStream(ref bits4, output.Slice(3 * segment));

        if (!(bits1.IsEndOfStream & bits2.IsEndOfStream & bits3.IsEndOfStream & bits4.IsEndOfStream))
        {
            Throw.Error(error);
        }
    }

    /// <summary>
    /// libzstd's <c>HUF_decompress4X1_usingDTable_internal_fast_c_loop</c>: the four streams decoded
    /// together, five symbols each a round, then each stream finished by <see cref="DecodeStream"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container holds a stream's next bits from its top, with a 1 set just below the last valid
    /// one: the count of trailing zeros is then the number of bits consumed since the container was
    /// loaded, and a reload steps back by its whole bytes. Every entry is looked up with the top 11
    /// bits, a constant shift.
    /// </para>
    /// <para>
    /// Bounds: a round takes at most 5 x 11 bits, under 7 bytes, from each stream, and writes 5
    /// symbols to each. Before the rounds, their number is capped by what the last stream's quarter
    /// can take and by what lies between the lowest stream position and the start of the section;
    /// the stream positions are checked to be in order, the lowest first, so that none can go below
    /// the section. The careful finish then validates each stream exactly, as the reference decoder.
    /// </para>
    /// </remarks>
    private void DecodeFourStreamsFast(ReadOnlySpan<byte> source, Span<byte> output, int length1, int length2, int length3, int segment)
    {
        const ZstdError error = ZstdError.HuffmanStream;
        int start1 = 6;
        int start2 = start1 + length1;
        int start3 = start2 + length2;
        int start4 = start3 + length3;
        if (source[start2 - 1] == 0 || source[start3 - 1] == 0 || source[start4 - 1] == 0 || source[^1] == 0)
        {
            Throw.Error(error);
        }

        ref byte input = ref MemoryMarshal.GetReference(source);
        ref byte ip0 = ref Unsafe.Add(ref input, start2 - 8);
        ref byte ip1 = ref Unsafe.Add(ref input, start3 - 8);
        ref byte ip2 = ref Unsafe.Add(ref input, start4 - 8);
        ref byte ip3 = ref Unsafe.Add(ref input, source.Length - 8);
        ulong bits0 = InitFastStream(ref ip0);
        ulong bits1 = InitFastStream(ref ip1);
        ulong bits2 = InitFastStream(ref ip2);
        ulong bits3 = InitFastStream(ref ip3);

        ref byte first = ref MemoryMarshal.GetReference(output);
        ref byte op0 = ref first;
        ref byte op1 = ref Unsafe.Add(ref first, segment);
        ref byte op2 = ref Unsafe.Add(ref first, 2 * segment);
        ref byte op3 = ref Unsafe.Add(ref first, 3 * segment);
        ref byte oend = ref Unsafe.Add(ref first, output.Length);
        ref ushort table = ref MemoryMarshal.GetArrayDataReference(Entries);

        while (true)
        {
            nint outputRounds = Unsafe.ByteOffset(ref op3, ref oend) / 5;
            nint inputRounds = Unsafe.ByteOffset(ref input, ref ip0) / 7;
            nint rounds = Math.Min(outputRounds, inputRounds);
            if (rounds == 0
                || Unsafe.IsAddressLessThan(ref ip1, ref ip0)
                || Unsafe.IsAddressLessThan(ref ip2, ref ip1)
                || Unsafe.IsAddressLessThan(ref ip3, ref ip2))
            {
                break;
            }

            ref byte olimit = ref Unsafe.Add(ref op3, rounds * 5);
            do
            {
                Symbol(ref table, ref bits0, ref op0, 0);
                Symbol(ref table, ref bits1, ref op1, 0);
                Symbol(ref table, ref bits2, ref op2, 0);
                Symbol(ref table, ref bits3, ref op3, 0);
                Symbol(ref table, ref bits0, ref op0, 1);
                Symbol(ref table, ref bits1, ref op1, 1);
                Symbol(ref table, ref bits2, ref op2, 1);
                Symbol(ref table, ref bits3, ref op3, 1);
                Symbol(ref table, ref bits0, ref op0, 2);
                Symbol(ref table, ref bits1, ref op1, 2);
                Symbol(ref table, ref bits2, ref op2, 2);
                Symbol(ref table, ref bits3, ref op3, 2);
                Symbol(ref table, ref bits0, ref op0, 3);
                Symbol(ref table, ref bits1, ref op1, 3);
                Symbol(ref table, ref bits2, ref op2, 3);
                Symbol(ref table, ref bits3, ref op3, 3);
                Symbol(ref table, ref bits0, ref op0, 4);
                Symbol(ref table, ref bits1, ref op1, 4);
                Symbol(ref table, ref bits2, ref op2, 4);
                Symbol(ref table, ref bits3, ref op3, 4);
                ip0 = ref ReloadFast(ref bits0, ref ip0);
                op0 = ref Unsafe.Add(ref op0, 5);
                ip1 = ref ReloadFast(ref bits1, ref ip1);
                op1 = ref Unsafe.Add(ref op1, 5);
                ip2 = ref ReloadFast(ref bits2, ref ip2);
                op2 = ref Unsafe.Add(ref op2, 5);
                ip3 = ref ReloadFast(ref bits3, ref ip3);
                op3 = ref Unsafe.Add(ref op3, 5);
            }
            while (Unsafe.IsAddressLessThan(ref op3, ref olimit));
        }

        // Each stream finished and checked on its own, as libzstd's HUF_initRemainingDStream.
        Finish(source, start1, start2, ref input, ref ip0, bits0, output, ref first, ref op0, 0, segment);
        Finish(source, start2, start3, ref input, ref ip1, bits1, output, ref first, ref op1, segment, segment);
        Finish(source, start3, start4, ref input, ref ip2, bits2, output, ref first, ref op2, 2 * segment, segment);
        Finish(source, start4, source.Length, ref input, ref ip3, bits3, output, ref first, ref op3, 3 * segment, output.Length - (3 * segment));

        void Finish(
            ReadOnlySpan<byte> source, int start, int end, ref byte input, ref byte ip, ulong bits,
            Span<byte> output, ref byte first, ref byte op, int segmentStart, int segmentLength)
        {
            int written = (int)Unsafe.ByteOffset(ref first, ref op) - segmentStart;
            if ((uint)written > (uint)segmentLength)
            {
                Throw.Error(error);
            }

            // The container may reach below the stream's first byte once the stream is nearly
            // consumed; the reference reader then sits at the first byte with the bits below counted
            // as consumed, and more than 64 means the stream was overrun.
            int position = (int)Unsafe.ByteOffset(ref input, ref ip) - start;
            int consumed = BitOperations.TrailingZeroCount(bits);
            if (position < 0)
            {
                consumed -= position * 8;
                position = 0;
            }

            if (consumed > 64)
            {
                Throw.Error(error);
            }

            var reader = BackwardBitReader.Resume(source.Slice(start, end - start), position, consumed);
            DecodeStream(ref reader, output.Slice(segmentStart + written, segmentLength - written));
            if (!reader.IsEndOfStream)
            {
                Throw.Error(error);
            }
        }
    }

    /// <summary>libzstd's <c>HUF_initFastDStream</c>: the last eight bytes, the marker made the sentinel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong InitFastStream(ref byte ip)
    {
        int consumed = 8 - BackwardBitReader.HighBit(Unsafe.Add(ref ip, 7));
        return (Unsafe.ReadUnaligned<ulong>(ref ip) | 1) << consumed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Symbol(ref ushort table, ref ulong bits, ref byte op, int k)
    {
        int entry = Unsafe.Add(ref table, (nint)(bits >> 53));
        bits <<= entry;
        Unsafe.Add(ref op, k) = (byte)(entry >> 8);
    }

    /// <summary>
    /// Steps a stream back by the whole bytes its container has consumed and reloads it. A ref
    /// parameter cannot be repointed for the caller, so the new position is returned.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte ReloadFast(scoped ref ulong bits, ref byte ip)
    {
        int consumed = BitOperations.TrailingZeroCount(bits);
        ref byte next = ref Unsafe.Subtract(ref ip, consumed >> 3);
        bits = (Unsafe.ReadUnaligned<ulong>(ref next) | 1) << (consumed & 7);
        return ref next;
    }

    /// <summary>Decodes one symbol per entry until <paramref name="output"/> is full.</summary>
    private void DecodeStream(ref BackwardBitReader bits, Span<byte> output)
    {
        ushort[] entries = Entries;
        int tableLog = TableLog;
        for (int i = 0; i < output.Length; i++)
        {
            // A reload leaves at least 57 bits while the stream has them; a code takes at most 12.
            if (bits.BitsConsumed > 64 - MaxTableLog)
            {
                bits.Reload();
            }

            ushort entry = entries[bits.PeekBitsFast(tableLog)];
            output[i] = (byte)(entry >> 8);
            bits.SkipBits(entry & 0xFF);
        }
    }
}
