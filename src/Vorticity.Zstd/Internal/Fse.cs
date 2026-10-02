using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// Finite State Entropy: the table descriptions (normalized counts) shared by the Huffman weights
/// and the three sequence codes, and the byte-symbol decoder the Huffman weights are compressed with.
/// </summary>
internal static class Fse
{
    public const int MinTableLog = 5;
    public const int TableLogAbsoluteMax = 15;

    /// <summary>libzstd's FSE_MAX_TABLELOG, for the byte-symbol tables.</summary>
    public const int MaxTableLog = 12;

    /// <summary>The largest accuracy of the Huffman weights' table.</summary>
    public const int WeightsMaxTableLog = 6;

    /// <summary>
    /// libzstd's <c>FSE_readNCount</c>: reads a table description into <paramref name="normalized"/>,
    /// whose length must exceed the incoming <paramref name="maxSymbolValue"/>. On return
    /// <paramref name="maxSymbolValue"/> is the last symbol described.
    /// </summary>
    /// <returns>The bytes the description takes.</returns>
    public static int ReadNCount(
        Span<short> normalized, ref int maxSymbolValue, out int tableLog, ReadOnlySpan<byte> header, ZstdError error)
    {
        if (header.Length < 8)
        {
            // The reader works on at least eight bytes; a shorter header is read from a zero-padded
            // copy and must not have needed the padding.
            Span<byte> padded = stackalloc byte[8];
            padded.Clear();
            header.CopyTo(padded);
            int size = ReadNCount(normalized, ref maxSymbolValue, out tableLog, padded, error);
            if (size > header.Length)
            {
                Throw.Error(error);
            }

            return size;
        }

        int end = header.Length;
        int ip = 0;
        int maxSymbolPlusOne = maxSymbolValue + 1;
        normalized.Slice(0, maxSymbolPlusOne).Clear();

        uint bitStream = BinaryPrimitives.ReadUInt32LittleEndian(header);
        int nbBits = (int)(bitStream & 0xF) + MinTableLog;
        if (nbBits > TableLogAbsoluteMax)
        {
            Throw.Error(error);
        }

        bitStream >>= 4;
        int bitCount = 4;
        tableLog = nbBits;
        int remaining = (1 << nbBits) + 1;
        int threshold = 1 << nbBits;
        nbBits++;
        int symbol = 0;
        bool previousZero = false;

        while (true)
        {
            if (previousZero)
            {
                // Runs of zero-probability symbols: each 2-bit code 0b11 means three more and
                // another code follows.
                int repeats = BitOperations.TrailingZeroCount(~bitStream | 0x80000000u) >> 1;
                while (repeats >= 12)
                {
                    symbol += 3 * 12;
                    if (ip <= end - 7)
                    {
                        ip += 3;
                    }
                    else
                    {
                        bitCount -= 8 * (end - 7 - ip);
                        bitCount &= 31;
                        ip = end - 4;
                    }

                    bitStream = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(ip)) >> bitCount;
                    repeats = BitOperations.TrailingZeroCount(~bitStream | 0x80000000u) >> 1;
                }

                symbol += 3 * repeats;
                bitStream >>= 2 * repeats;
                bitCount += 2 * repeats;
                symbol += (int)(bitStream & 3);
                bitCount += 2;

                if (symbol >= maxSymbolPlusOne)
                {
                    break;
                }

                Advance(header, end, ref ip, ref bitCount, out bitStream);
            }

            int max = (2 * threshold) - 1 - remaining;
            int count;
            if ((bitStream & (uint)(threshold - 1)) < (uint)max)
            {
                count = (int)(bitStream & (uint)(threshold - 1));
                bitCount += nbBits - 1;
            }
            else
            {
                count = (int)(bitStream & (uint)((2 * threshold) - 1));
                if (count >= threshold)
                {
                    count -= max;
                }

                bitCount += nbBits;
            }

            count--; // -1 is a symbol below one unit of probability
            remaining -= count >= 0 ? count : -count;
            normalized[symbol++] = (short)count;
            previousZero = count == 0;

            if (remaining < threshold)
            {
                if (remaining <= 1)
                {
                    break;
                }

                nbBits = BackwardBitReader.HighBit((uint)remaining) + 1;
                threshold = 1 << (nbBits - 1);
            }

            if (symbol >= maxSymbolPlusOne)
            {
                break;
            }

            Advance(header, end, ref ip, ref bitCount, out bitStream);
        }

        if (remaining != 1 || symbol > maxSymbolPlusOne || bitCount > 32)
        {
            Throw.Error(error);
        }

        maxSymbolValue = symbol - 1;
        ip += (bitCount + 7) >> 3;
        return ip;

        static void Advance(ReadOnlySpan<byte> header, int end, ref int ip, ref int bitCount, out uint bitStream)
        {
            if (ip <= end - 7 || ip + (bitCount >> 3) <= end - 4)
            {
                ip += bitCount >> 3;
                bitCount &= 7;
            }
            else
            {
                bitCount -= 8 * (end - 4 - ip);
                bitCount &= 31;
                ip = end - 4;
            }

            bitStream = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(ip)) >> bitCount;
        }
    }

    /// <summary>The step that spreads symbols over a table of <paramref name="tableSize"/> cells.</summary>
    public static int TableStep(int tableSize) => (tableSize >> 1) + (tableSize >> 3) + 3;

    /// <summary>
    /// libzstd's <c>FSE_decompress_wksp</c> with an accuracy cap of 6, which is how the weights of a
    /// Huffman tree are compressed: a table description, then a stream decoded by two interleaved
    /// states until it overflows.
    /// </summary>
    /// <returns>The number of weights decoded into <paramref name="weights"/>.</returns>
    public static int DecodeHuffmanWeights(ReadOnlySpan<byte> source, Span<byte> weights)
    {
        const ZstdError error = ZstdError.HuffmanTable;
        Span<short> normalized = stackalloc short[256];
        int maxSymbolValue = 255;
        int headerSize = ReadNCount(normalized, ref maxSymbolValue, out int tableLog, source, error);
        if (tableLog > WeightsMaxTableLog)
        {
            Throw.Error(error);
        }

        // libzstd decodes the weights in a fixed workspace sized for 6 bits and symbols up to 11;
        // what does not fit there is refused, and is refused here alike.
        if (DecompressWorkspaceSize(tableLog, maxSymbolValue) > DecompressWorkspaceSize(WeightsMaxTableLog, 11))
        {
            Throw.Error(error);
        }

        Span<FseEntry> table = stackalloc FseEntry[1 << WeightsMaxTableLog];
        bool fast = BuildTable(table, normalized.Slice(0, maxSymbolValue + 1), tableLog, error);
        return DecodeTwoStates(source.Slice(headerSize), weights, table, tableLog, fast);
    }

    /// <summary>libzstd's <c>FSE_DECOMPRESS_WKSP_SIZE</c>, in bytes.</summary>
    private static int DecompressWorkspaceSize(int tableLog, int maxSymbolValue)
    {
        int dtableU32 = 1 + (1 << tableLog);
        int buildBytes = (2 * (maxSymbolValue + 1)) + (1 << tableLog) + 8;
        int buildU32 = (buildBytes + 3) / 4;
        return (dtableU32 + 1 + buildU32 + 128 + 1) * 4;
    }

    /// <summary>
    /// libzstd's <c>FSE_buildDTable_internal</c> for byte symbols.
    /// </summary>
    /// <returns>Whether every state reads at least one bit (libzstd's fast mode).</returns>
    public static bool BuildTable(Span<FseEntry> table, ReadOnlySpan<short> normalized, int tableLog, ZstdError error)
    {
        int tableSize = 1 << tableLog;
        int highThreshold = tableSize - 1;
        Span<ushort> symbolNext = stackalloc ushort[256];
        bool fast = true;
        int largeLimit = 1 << (tableLog - 1);

        for (int s = 0; s < normalized.Length; s++)
        {
            if (normalized[s] == -1)
            {
                table[highThreshold--].Symbol = (byte)s;
                symbolNext[s] = 1;
            }
            else
            {
                if (normalized[s] >= largeLimit)
                {
                    fast = false;
                }

                symbolNext[s] = (ushort)normalized[s];
            }
        }

        int mask = tableSize - 1;
        int step = TableStep(tableSize);
        int position = 0;
        for (int s = 0; s < normalized.Length; s++)
        {
            for (int i = 0; i < normalized[s]; i++)
            {
                table[position].Symbol = (byte)s;
                do
                {
                    position = (position + step) & mask;
                }
                while (position > highThreshold);
            }
        }

        if (position != 0)
        {
            Throw.Error(error);
        }

        for (int u = 0; u < tableSize; u++)
        {
            int symbol = table[u].Symbol;
            int nextState = symbolNext[symbol]++;
            int nbBits = tableLog - BackwardBitReader.HighBit((uint)nextState);
            table[u].NbBits = (byte)nbBits;
            table[u].NewState = (ushort)((nextState << nbBits) - tableSize);
        }

        return fast;
    }

    /// <summary>libzstd's <c>FSE_decompress_usingDTable_generic</c> on a 64-bit container.</summary>
    private static int DecodeTwoStates(ReadOnlySpan<byte> source, Span<byte> output, ReadOnlySpan<FseEntry> table, int tableLog, bool fast)
    {
        const ZstdError error = ZstdError.HuffmanTable;
        var bits = new BackwardBitReader(source, error);
        int state1 = (int)bits.ReadBits(tableLog);
        bits.Reload();
        int state2 = (int)bits.ReadBits(tableLog);
        bits.Reload();
        if (bits.Reload() == BitStreamStatus.Overflow)
        {
            Throw.Error(error);
        }

        // At most 255 weights: the last symbol's weight is implied.
        int max = 255;
        int op = 0;

        // Four symbols a round while the stream has bits to spare; on a 64-bit container libzstd
        // reloads only between rounds.
        while ((bits.Reload() == BitStreamStatus.Unfinished) & (op < max - 3))
        {
            output[op] = Symbol(ref bits, ref state1, table, fast);
            output[op + 1] = Symbol(ref bits, ref state2, table, fast);
            output[op + 2] = Symbol(ref bits, ref state1, table, fast);
            output[op + 3] = Symbol(ref bits, ref state2, table, fast);
            op += 4;
        }

        // The tail: the stream ends when it overflows, and the other state then gives one more symbol.
        while (true)
        {
            if (op > max - 2)
            {
                Throw.Error(error);
            }

            output[op++] = Symbol(ref bits, ref state1, table, fast);
            if (bits.Reload() == BitStreamStatus.Overflow)
            {
                output[op++] = Symbol(ref bits, ref state2, table, fast);
                break;
            }

            if (op > max - 2)
            {
                Throw.Error(error);
            }

            output[op++] = Symbol(ref bits, ref state2, table, fast);
            if (bits.Reload() == BitStreamStatus.Overflow)
            {
                output[op++] = Symbol(ref bits, ref state1, table, fast);
                break;
            }
        }

        return op;

        static byte Symbol(ref BackwardBitReader bits, ref int state, ReadOnlySpan<FseEntry> table, bool fast)
        {
            FseEntry entry = table[state];
            uint low = fast ? bits.ReadBitsFast(entry.NbBits) : bits.ReadBits(entry.NbBits);
            state = entry.NewState + (int)low;
            return entry.Symbol;
        }
    }
}

/// <summary>One state of a byte-symbol FSE decoding table.</summary>
internal struct FseEntry
{
    public ushort NewState;
    public byte Symbol;
    public byte NbBits;
}
