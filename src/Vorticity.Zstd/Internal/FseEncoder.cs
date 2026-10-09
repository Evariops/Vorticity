using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Internal;

/// <summary>libzstd's <c>FSE_symbolCompressionTransform</c>: how a symbol moves an encoding state.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FseSymbolTransform
{
    public int DeltaFindState;
    public uint DeltaNbBits;
}

/// <summary>
/// libzstd's <c>FSE_CTable</c>: an encoding table, the next state of each state and symbol
/// (<see cref="StateTable"/>, by symbol) and each symbol's transform.
/// </summary>
/// <remarks>Its storage is pinned for the compressor's lifetime, so that the encoders hold plain pointers.</remarks>
internal sealed unsafe class FseCTable
{
    private readonly ushort[] _states;
    private readonly FseSymbolTransform[] _symbols;

    public FseCTable(int maxTableLog, int maxSymbolValue)
    {
        _states = GC.AllocateArray<ushort>(1 << maxTableLog, pinned: true);
        _symbols = GC.AllocateArray<FseSymbolTransform>(maxSymbolValue + 1, pinned: true);
        StateTable = (ushort*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_states));
        Symbols = (FseSymbolTransform*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_symbols));
    }

    public int TableLog;

    public int MaxSymbolValue;

    public ushort* StateTable { get; }

    public FseSymbolTransform* Symbols { get; }
}

/// <summary>An encoding state: libzstd's <c>FSE_CState_t</c>.</summary>
internal unsafe struct FseState
{
    public nint Value;
    public ushort* StateTable;
    public FseSymbolTransform* Symbols;
    public int StateLog;

    /// <summary>
    /// libzstd's <c>FSE_initCState2</c>: the state the first symbol encoded (the last decoded) leaves,
    /// from the smallest state that encodes it, which writes no bit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FseState(FseCTable table, uint symbol)
    {
        StateTable = table.StateTable;
        Symbols = table.Symbols;
        StateLog = table.TableLog;
        FseSymbolTransform transform = Symbols[symbol];
        uint nbBitsOut = (transform.DeltaNbBits + (1u << 15)) >> 16;
        uint value = (nbBitsOut << 16) - transform.DeltaNbBits;
        Value = StateTable[(int)(value >> (int)nbBitsOut) + transform.DeltaFindState];
    }

    /// <summary>libzstd's <c>FSE_encodeSymbol</c>: the state's low bits out, then its next state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(ref BitWriter bits, uint symbol)
    {
        FseSymbolTransform transform = Symbols[symbol];
        int nbBitsOut = (int)((uint)(Value + transform.DeltaNbBits) >> 16);
        bits.AddBits((ulong)Value, nbBitsOut);
        Value = StateTable[(int)(Value >> nbBitsOut) + transform.DeltaFindState];
    }

    /// <summary>libzstd's <c>FSE_flushCState</c>: the final state, which the decoder starts from.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Flush(ref BitWriter bits)
    {
        bits.AddBits((ulong)Value, StateLog);
        bits.Flush();
    }
}

/// <summary>
/// libzstd's <c>fse_compress.c</c>: normalized distributions, their descriptions, the encoding tables
/// built from them, and the byte encoder the Huffman weights are compressed with.
/// </summary>
internal static unsafe class FseEncoder
{
    /// <summary>libzstd's <c>FSE_MAX_TABLELOG</c>.</summary>
    public const int MaxTableLog = 12;

    /// <summary>libzstd's <c>FSE_DEFAULT_TABLELOG</c>.</summary>
    public const int DefaultTableLog = 11;

    /// <summary>libzstd's <c>FSE_NCOUNTBOUND</c>: the largest table description.</summary>
    public const int NCountBound = 512;

    /// <summary>libzstd's <c>FSE_minTableLog</c>: the least accuracy that represents every symbol.</summary>
    private static uint MinTableLog(nuint sourceSize, uint maxSymbolValue)
    {
        uint minBitsSource = (uint)BitOperations.Log2((uint)sourceSize) + 1;
        uint minBitsSymbols = (uint)BitOperations.Log2(maxSymbolValue) + 2;
        return Math.Min(minBitsSource, minBitsSymbols);
    }

    /// <summary>libzstd's <c>FSE_optimalTableLog_internal</c>.</summary>
    public static uint OptimalTableLog(uint maxTableLog, nuint sourceSize, uint maxSymbolValue, uint minus)
    {
        Debug.Assert(sourceSize > 1);
        // Unsigned, as libzstd's: a source of a few symbols underflows, and keeps the maximum.
        uint maxBitsSource = (uint)BitOperations.Log2((uint)(sourceSize - 1)) - minus;
        uint tableLog = maxTableLog == 0 ? DefaultTableLog : maxTableLog;
        uint minBits = MinTableLog(sourceSize, maxSymbolValue);
        if (maxBitsSource < tableLog)
        {
            tableLog = maxBitsSource;
        }

        if (minBits > tableLog)
        {
            tableLog = minBits;
        }

        return Math.Clamp(tableLog, Fse.MinTableLog, MaxTableLog);
    }

    /// <summary>libzstd's <c>FSE_optimalTableLog</c>.</summary>
    public static uint OptimalTableLog(uint maxTableLog, nuint sourceSize, uint maxSymbolValue) =>
        OptimalTableLog(maxTableLog, sourceSize, maxSymbolValue, 2);

    /// <summary>
    /// libzstd's <c>FSE_normalizeCount</c>: scales <paramref name="count"/> (which sums to
    /// <paramref name="total"/>) to a distribution over 2^<paramref name="tableLog"/> states, a symbol
    /// below one state taking -1 when <paramref name="useLowProbCount"/>, 1 otherwise.
    /// </summary>
    /// <returns>The table log, or 0 when one symbol has every count.</returns>
    public static uint NormalizeCount(short* normalized, uint tableLog, uint* count, nuint total, uint maxSymbolValue, bool useLowProbCount)
    {
        if (tableLog == 0)
        {
            tableLog = DefaultTableLog;
        }

        if (tableLog < Fse.MinTableLog || tableLog > MaxTableLog || tableLog < MinTableLog(total, maxSymbolValue))
        {
            throw new InvalidOperationException("FSE_normalizeCount: unsupported table log");
        }

        ReadOnlySpan<uint> restToBeatTable = [0, 473195, 504333, 520860, 550000, 700000, 750000, 830000];
        short lowProbCount = useLowProbCount ? (short)-1 : (short)1;
        int scale = 62 - (int)tableLog;
        ulong step = (1UL << 62) / (uint)total;
        ulong vStep = 1UL << (scale - 20);
        int stillToDistribute = 1 << (int)tableLog;
        uint largest = 0;
        short largestProbability = 0;
        uint lowThreshold = (uint)(total >> (int)tableLog);

        for (uint s = 0; s <= maxSymbolValue; s++)
        {
            if (count[s] == total)
            {
                return 0;
            }

            if (count[s] == 0)
            {
                normalized[s] = 0;
                continue;
            }

            if (count[s] <= lowThreshold)
            {
                normalized[s] = lowProbCount;
                stillToDistribute--;
            }
            else
            {
                ulong scaled = count[s] * step;
                short probability = (short)(scaled >> scale);
                if (probability < 8)
                {
                    ulong restToBeat = vStep * restToBeatTable[probability];
                    probability += (short)(scaled - ((ulong)probability << scale) > restToBeat ? 1 : 0);
                }

                if (probability > largestProbability)
                {
                    largestProbability = probability;
                    largest = s;
                }

                normalized[s] = probability;
                stillToDistribute -= probability;
            }
        }

        if (-stillToDistribute >= (normalized[largest] >> 1))
        {
            // A corner case the other method handles.
            NormalizeSecondMethod(normalized, tableLog, count, total, maxSymbolValue, lowProbCount);
        }
        else
        {
            normalized[largest] += (short)stillToDistribute;
        }

        return tableLog;
    }

    /// <summary>libzstd's <c>FSE_normalizeM2</c>, when the first method would starve the largest symbol.</summary>
    private static void NormalizeSecondMethod(short* normalized, uint tableLog, uint* count, nuint total, uint maxSymbolValue, short lowProbCount)
    {
        const short NotYetAssigned = -2;
        uint distributed = 0;
        uint lowThreshold = (uint)(total >> (int)tableLog);
        uint lowOne = (uint)((total * 3) >> (int)(tableLog + 1));

        for (uint s = 0; s <= maxSymbolValue; s++)
        {
            if (count[s] == 0)
            {
                normalized[s] = 0;
                continue;
            }

            if (count[s] <= lowThreshold)
            {
                normalized[s] = lowProbCount;
                distributed++;
                total -= count[s];
                continue;
            }

            if (count[s] <= lowOne)
            {
                normalized[s] = 1;
                distributed++;
                total -= count[s];
                continue;
            }

            normalized[s] = NotYetAssigned;
        }

        uint toDistribute = (1u << (int)tableLog) - distributed;
        if (toDistribute == 0)
        {
            return;
        }

        if (total / toDistribute > lowOne)
        {
            // Risk of rounding to zero.
            lowOne = (uint)((total * 3) / (toDistribute * 2));
            for (uint s = 0; s <= maxSymbolValue; s++)
            {
                if (normalized[s] == NotYetAssigned && count[s] <= lowOne)
                {
                    normalized[s] = 1;
                    distributed++;
                    total -= count[s];
                }
            }

            toDistribute = (1u << (int)tableLog) - distributed;
        }

        if (distributed == maxSymbolValue + 1)
        {
            // Every symbol is poor, the data likely incompressible: the rest to the largest.
            uint maxValue = 0;
            uint maxCount = 0;
            for (uint s = 0; s <= maxSymbolValue; s++)
            {
                if (count[s] > maxCount)
                {
                    maxValue = s;
                    maxCount = count[s];
                }
            }

            normalized[maxValue] += (short)toDistribute;
            return;
        }

        if (total == 0)
        {
            // Every symbol was low enough to take one state or less: the rest one at a time.
            for (uint s = 0; toDistribute > 0; s = (s + 1) % (maxSymbolValue + 1))
            {
                if (normalized[s] > 0)
                {
                    toDistribute--;
                    normalized[s]++;
                }
            }

            return;
        }

        int vStepLog = 62 - (int)tableLog;
        ulong mid = (1UL << (vStepLog - 1)) - 1;
        ulong rStep = (((1UL << vStepLog) * toDistribute) + mid) / (uint)total;
        ulong tmpTotal = mid;
        for (uint s = 0; s <= maxSymbolValue; s++)
        {
            if (normalized[s] == NotYetAssigned)
            {
                ulong end = tmpTotal + (count[s] * rStep);
                uint start = (uint)(tmpTotal >> vStepLog);
                uint stop = (uint)(end >> vStepLog);
                uint weight = stop - start;
                if (weight < 1)
                {
                    throw new InvalidOperationException("FSE_normalizeM2: a symbol rounded to nothing");
                }

                normalized[s] = (short)weight;
                tmpTotal = end;
            }
        }
    }

    /// <summary>
    /// libzstd's <c>FSE_writeNCount</c>: the description <see cref="Fse.ReadNCount"/> reads, into a
    /// buffer of at least <see cref="NCountBound"/> bytes.
    /// </summary>
    /// <returns>The size of the description.</returns>
    public static nuint WriteNCount(byte* header, short* normalized, uint maxSymbolValue, uint tableLog)
    {
        Debug.Assert(tableLog is >= Fse.MinTableLog and <= MaxTableLog);
        byte* output = header;
        int tableSize = 1 << (int)tableLog;
        uint bitStream = tableLog - Fse.MinTableLog;
        int bitCount = 4;
        int remaining = tableSize + 1;
        int threshold = tableSize;
        int nbBits = (int)tableLog + 1;
        uint symbol = 0;
        uint alphabetSize = maxSymbolValue + 1;
        bool previousIsZero = false;

        while (symbol < alphabetSize && remaining > 1)
        {
            if (previousIsZero)
            {
                uint start = symbol;
                while (symbol < alphabetSize && normalized[symbol] == 0)
                {
                    symbol++;
                }

                if (symbol == alphabetSize)
                {
                    break;
                }

                while (symbol >= start + 24)
                {
                    start += 24;
                    bitStream += 0xFFFFu << bitCount;
                    output[0] = (byte)bitStream;
                    output[1] = (byte)(bitStream >> 8);
                    output += 2;
                    bitStream >>= 16;
                }

                while (symbol >= start + 3)
                {
                    start += 3;
                    bitStream += 3u << bitCount;
                    bitCount += 2;
                }

                bitStream += (symbol - start) << bitCount;
                bitCount += 2;
                if (bitCount > 16)
                {
                    output[0] = (byte)bitStream;
                    output[1] = (byte)(bitStream >> 8);
                    output += 2;
                    bitStream >>= 16;
                    bitCount -= 16;
                }
            }

            {
                int count = normalized[symbol++];
                int max = (2 * threshold) - 1 - remaining;
                remaining -= count < 0 ? -count : count;
                count++; // +1 for extra accuracy
                if (count >= threshold)
                {
                    count += max;
                }

                bitStream += (uint)count << bitCount;
                bitCount += nbBits;
                bitCount -= count < max ? 1 : 0;
                previousIsZero = count == 1;
                if (remaining < 1)
                {
                    throw new InvalidOperationException("FSE_writeNCount: invalid distribution");
                }

                while (remaining < threshold)
                {
                    nbBits--;
                    threshold >>= 1;
                }
            }

            if (bitCount > 16)
            {
                output[0] = (byte)bitStream;
                output[1] = (byte)(bitStream >> 8);
                output += 2;
                bitStream >>= 16;
                bitCount -= 16;
            }
        }

        if (remaining != 1)
        {
            throw new InvalidOperationException("FSE_writeNCount: invalid distribution");
        }

        output[0] = (byte)bitStream;
        output[1] = (byte)(bitStream >> 8);
        output += (bitCount + 7) / 8;
        return (nuint)(output - header);
    }

    /// <summary>
    /// libzstd's <c>FSE_buildCTable_wksp</c>: the symbols spread over the table as the decoder spreads
    /// them, then each state's successor by symbol, and each symbol's transform.
    /// </summary>
    public static void BuildCTable(FseCTable table, short* normalized, uint maxSymbolValue, uint tableLog)
    {
        Debug.Assert(tableLog < 16);
        uint tableSize = 1u << (int)tableLog;
        uint tableMask = tableSize - 1;
        uint step = (uint)Fse.TableStep((int)tableSize);
        uint maxSV1 = maxSymbolValue + 1;
        ushort* cumul = stackalloc ushort[256 + 2];
        byte* tableSymbol = stackalloc byte[(1 << MaxTableLog) + 8];
        byte* spread = stackalloc byte[(1 << MaxTableLog) + 8];
        uint highThreshold = tableSize - 1;
        ushort* stateTable = table.StateTable;
        FseSymbolTransform* symbolTT = table.Symbols;
        table.TableLog = (int)tableLog;
        table.MaxSymbolValue = (int)maxSymbolValue;

        // ---- the symbols' first positions; the low-probability ones at the top of the table
        cumul[0] = 0;
        for (uint u = 1; u <= maxSV1; u++)
        {
            if (normalized[u - 1] == -1)
            {
                cumul[u] = (ushort)(cumul[u - 1] + 1);
                tableSymbol[highThreshold--] = (byte)(u - 1);
            }
            else
            {
                Debug.Assert(normalized[u - 1] >= 0);
                cumul[u] = (ushort)(cumul[u - 1] + (ushort)normalized[u - 1]);
            }
        }

        cumul[maxSV1] = (ushort)(tableSize + 1);

        // ---- the spread
        if (highThreshold == tableSize - 1)
        {
            // No low-probability symbol: laid down in order eight at a time, then scattered.
            ulong add = 0x0101010101010101UL;
            nuint position = 0;
            ulong value = 0;
            for (uint s = 0; s < maxSV1; s++, value += add)
            {
                int n = normalized[s];
                Unsafe.WriteUnaligned(spread + position, value);
                for (int i = 8; i < n; i += 8)
                {
                    Unsafe.WriteUnaligned(spread + position + (nuint)i, value);
                }

                position += (nuint)n;
            }

            uint at = 0;
            for (uint s = 0; s < tableSize; s += 2)
            {
                tableSymbol[at & tableMask] = spread[s];
                tableSymbol[(at + step) & tableMask] = spread[s + 1];
                at = (at + (2 * step)) & tableMask;
            }

            Debug.Assert(at == 0);
        }
        else
        {
            uint position = 0;
            for (uint symbol = 0; symbol < maxSV1; symbol++)
            {
                int frequency = normalized[symbol];
                for (int n = 0; n < frequency; n++)
                {
                    tableSymbol[position] = (byte)symbol;
                    position = (position + step) & tableMask;
                    while (position > highThreshold)
                    {
                        position = (position + step) & tableMask;
                    }
                }
            }

            Debug.Assert(position == 0);
        }

        // ---- the state table, sorted by symbol: each state's next state
        for (uint u = 0; u < tableSize; u++)
        {
            byte s = tableSymbol[u];
            stateTable[cumul[s]++] = (ushort)(tableSize + u);
        }

        // ---- the symbol transforms
        uint total = 0;
        for (uint s = 0; s <= maxSymbolValue; s++)
        {
            switch (normalized[s])
            {
                case 0:
                    // Filled nonetheless: the bit cost of an absent symbol reads it.
                    symbolTT[s].DeltaNbBits = ((tableLog + 1) << 16) - (1u << (int)tableLog);
                    break;
                case -1:
                case 1:
                    symbolTT[s].DeltaNbBits = (tableLog << 16) - (1u << (int)tableLog);
                    symbolTT[s].DeltaFindState = (int)(total - 1);
                    total++;
                    break;
                default:
                {
                    uint maxBitsOut = tableLog - (uint)BitOperations.Log2((uint)normalized[s] - 1);
                    uint minStatePlus = (uint)normalized[s] << (int)maxBitsOut;
                    symbolTT[s].DeltaNbBits = (maxBitsOut << 16) - minStatePlus;
                    symbolTT[s].DeltaFindState = (int)(total - (uint)normalized[s]);
                    total += (uint)normalized[s];
                    break;
                }
            }
        }
    }

    /// <summary>libzstd's <c>FSE_buildCTable_rle</c>: one symbol, which takes no bit.</summary>
    public static void BuildCTableRle(FseCTable table, byte symbol)
    {
        table.TableLog = 0;
        table.MaxSymbolValue = symbol;
        table.StateTable[0] = 0;
        table.StateTable[1] = 0;
        table.Symbols[symbol].DeltaNbBits = 0;
        table.Symbols[symbol].DeltaFindState = 0;
    }

    /// <summary>
    /// libzstd's <c>FSE_compress_usingCTable</c>: <paramref name="source"/> encoded backward with two
    /// interleaved states, as <see cref="Fse.DecodeHuffmanWeights"/> decodes it.
    /// </summary>
    /// <returns>The size of the stream, or 0 when it does not fit or is too short.</returns>
    public static nuint Compress(byte* destination, nuint capacity, byte* source, nuint size, FseCTable table)
    {
        if (size <= 2)
        {
            return 0;
        }

        var bits = new BitWriter(destination, capacity);
        if (!bits.IsValid)
        {
            return 0;
        }

        byte* ip = source + size;
        FseState state1;
        FseState state2;
        if ((size & 1) != 0)
        {
            state1 = new FseState(table, *--ip);
            state2 = new FseState(table, *--ip);
            state1.Encode(ref bits, *--ip);
            bits.Flush();
        }
        else
        {
            state2 = new FseState(table, *--ip);
            state1 = new FseState(table, *--ip);
        }

        size -= 2;
        if ((size & 2) != 0)
        {
            state2.Encode(ref bits, *--ip);
            state1.Encode(ref bits, *--ip);
            bits.Flush();
        }

        while (ip > source)
        {
            state2.Encode(ref bits, *--ip);
            state1.Encode(ref bits, *--ip);
            state2.Encode(ref bits, *--ip);
            state1.Encode(ref bits, *--ip);
            bits.Flush();
        }

        state2.Flush(ref bits);
        state1.Flush(ref bits);
        return bits.Close();
    }

    /// <summary>
    /// libzstd's <c>FSE_bitCost</c>: a symbol's cost in the table, in bits with
    /// <paramref name="accuracyLog"/> fractional bits; an absent symbol costs <c>tableLog + 1</c> bits.
    /// </summary>
    public static uint BitCost(FseSymbolTransform* symbols, uint tableLog, uint symbol, int accuracyLog)
    {
        uint minNbBits = symbols[symbol].DeltaNbBits >> 16;
        uint threshold = (minNbBits + 1) << 16;
        uint tableSize = 1u << (int)tableLog;
        uint deltaFromThreshold = threshold - (symbols[symbol].DeltaNbBits + tableSize);
        uint normalizedDeltaFromThreshold = (deltaFromThreshold << accuracyLog) >> (int)tableLog;
        uint bitMultiplier = 1u << accuracyLog;
        return ((minNbBits + 1) * bitMultiplier) - normalizedDeltaFromThreshold;
    }
}
