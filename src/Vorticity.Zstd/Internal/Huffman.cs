using System;
using System.Buffers.Binary;

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
    /// what remains.
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
