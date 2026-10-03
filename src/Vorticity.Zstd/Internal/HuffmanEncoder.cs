using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Zstd.Internal;

/// <summary>libzstd's <c>HUF_repeat</c>: whether the previous block's tree may serve again.</summary>
internal enum HuffmanRepeat
{
    /// <summary>No previous tree, or one that cannot be used.</summary>
    None,

    /// <summary>A previous tree that may lack some symbols: checked before use.</summary>
    Check,

    /// <summary>A previous tree known to encode every symbol (a dictionary's).</summary>
    Valid,
}

/// <summary>
/// libzstd's <c>HUF_CElt</c> table: for each byte, the length of its code in the low byte and the code
/// itself in the top bits, as the encoder adds it.
/// </summary>
internal sealed unsafe class HuffmanCTable
{
    private readonly ulong[] _elements = GC.AllocateArray<ulong>(HuffmanTable.MaxSymbols, pinned: true);

    public HuffmanCTable()
    {
        Elements = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_elements));
    }

    public ulong* Elements { get; }

    public int TableLog;

    public int MaxSymbolValue;

    public void CopyFrom(HuffmanCTable other)
    {
        TableLog = other.TableLog;
        MaxSymbolValue = other.MaxSymbolValue;
        Unsafe.CopyBlockUnaligned(Elements, other.Elements, HuffmanTable.MaxSymbols * sizeof(ulong));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NbBits(ulong element) => (int)(element & 0xFF);
}

/// <summary>The tables a Huffman compression builds before it knows whether it keeps them.</summary>
internal sealed class HuffmanWorkspace
{
    /// <summary>libzstd's <c>table->CTable</c>: the new tree.</summary>
    public readonly HuffmanCTable Table = new();

    /// <summary>The encoding table of the tree's weights.</summary>
    public readonly FseCTable Weights = new(HuffmanEncoder.WeightsMaxTableLog, HuffmanTable.MaxTableLog);
}

/// <summary>libzstd's <c>huf_compress.c</c>: the Huffman trees and streams of the literals.</summary>
internal static unsafe class HuffmanEncoder
{
    /// <summary>libzstd's <c>LitHufLog</c>: the longest code the literals use.</summary>
    public const int LiteralsTableLog = 11;

    /// <summary>libzstd's <c>MAX_FSE_TABLELOG_FOR_HUFF_HEADER</c>.</summary>
    public const int WeightsMaxTableLog = 6;

    private const int StartNode = HuffmanTable.MaxSymbols;

    /// <summary>libzstd's <c>nodeElt</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Node
    {
        public uint Count;
        public ushort Parent;
        public byte Symbol;
        public byte NbBits;
    }

    /// <summary>libzstd's <c>rankPos</c>.</summary>
    private struct RankPosition
    {
        public ushort Base;
        public ushort Current;
    }

    // ---- HUF_sort: buckets by count, the large counts by their logarithm, sorted within

    private const int RankPositionTableSize = 192;
    private const int RankPositionMaxCountLog = 32;
    private const int RankPositionLogBucketsBegin = RankPositionTableSize - 1 - RankPositionMaxCountLog - 1;

    /// <summary>
    /// <c>RANK_POSITION_DISTINCT_COUNT_CUTOFF</c>: counts below it have a bucket each. libzstd's
    /// comment says 166; its definition, <c>158 + highbit(158)</c>, makes it 165.
    /// </summary>
    private const int RankPositionDistinctCountCutoff = RankPositionLogBucketsBegin + 7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BucketIndex(uint count) =>
        count < RankPositionDistinctCountCutoff ? count : (uint)BitOperations.Log2(count) + RankPositionLogBucketsBegin;

    /// <summary>
    /// libzstd's <c>HUF_sort</c>: the symbols by decreasing count, equal counts by increasing symbol
    /// except in the buckets the quicksort orders.
    /// </summary>
    private static void Sort(Node* nodes, uint* count, uint maxSymbolValue, RankPosition* rankPosition)
    {
        uint maxSymbolValue1 = maxSymbolValue + 1;
        Unsafe.InitBlockUnaligned(rankPosition, 0, (uint)(sizeof(RankPosition) * RankPositionTableSize));
        for (uint n = 0; n < maxSymbolValue1; n++)
        {
            rankPosition[BucketIndex(count[n])].Base++;
        }

        for (int n = RankPositionTableSize - 1; n > 0; n--)
        {
            rankPosition[n - 1].Base += rankPosition[n].Base;
            rankPosition[n - 1].Current = rankPosition[n - 1].Base;
        }

        for (uint n = 0; n < maxSymbolValue1; n++)
        {
            uint c = count[n];
            uint r = BucketIndex(c) + 1;
            uint position = rankPosition[r].Current++;
            nodes[position].Count = c;
            nodes[position].Symbol = (byte)n;
        }

        for (int n = RankPositionDistinctCountCutoff; n < RankPositionTableSize - 1; n++)
        {
            int bucketSize = rankPosition[n].Current - rankPosition[n].Base;
            if (bucketSize > 1)
            {
                QuickSort(nodes + rankPosition[n].Base, 0, bucketSize - 1);
            }
        }
    }

    /// <summary>libzstd's <c>HUF_insertionSort</c>, by decreasing count.</summary>
    private static void InsertionSort(Node* nodes, int low, int high)
    {
        int size = high - low + 1;
        nodes += low;
        for (int i = 1; i < size; i++)
        {
            Node key = nodes[i];
            int j = i - 1;
            while (j >= 0 && nodes[j].Count < key.Count)
            {
                nodes[j + 1] = nodes[j];
                j--;
            }

            nodes[j + 1] = key;
        }
    }

    /// <summary>libzstd's <c>HUF_quickSortPartition</c>: the last element as the pivot.</summary>
    private static int Partition(Node* nodes, int low, int high)
    {
        uint pivot = nodes[high].Count;
        int i = low - 1;
        for (int j = low; j < high; j++)
        {
            if (nodes[j].Count > pivot)
            {
                i++;
                (nodes[i], nodes[j]) = (nodes[j], nodes[i]);
            }
        }

        (nodes[i + 1], nodes[high]) = (nodes[high], nodes[i + 1]);
        return i + 1;
    }

    /// <summary>libzstd's <c>HUF_simpleQuickSort</c>, by decreasing count: the order of equal counts is its own.</summary>
    private static void QuickSort(Node* nodes, int low, int high)
    {
        const int InsertionSortThreshold = 8;
        if (high - low < InsertionSortThreshold)
        {
            InsertionSort(nodes, low, high);
            return;
        }

        while (low < high)
        {
            int index = Partition(nodes, low, high);
            if (index - low < high - index)
            {
                QuickSort(nodes, low, index - 1);
                low = index + 1;
            }
            else
            {
                QuickSort(nodes, index + 1, high);
                high = index - 1;
            }
        }
    }

    /// <summary>
    /// libzstd's <c>HUF_buildTree</c>: the unbounded Huffman tree of the sorted symbols, its internal
    /// nodes from <see cref="StartNode"/>, and every symbol's depth.
    /// </summary>
    /// <returns>The last symbol with a count: the rarest.</returns>
    private static int BuildTree(Node* nodes, uint maxSymbolValue)
    {
        Node* nodes0 = nodes - 1;
        int nodeNb = StartNode;
        int nonNullRank = (int)maxSymbolValue;
        while (nodes[nonNullRank].Count == 0)
        {
            nonNullRank--;
        }

        int lowS = nonNullRank;
        int nodeRoot = nodeNb + lowS - 1;
        int lowN = nodeNb;
        nodes[nodeNb].Count = nodes[lowS].Count + nodes[lowS - 1].Count;
        nodes[lowS].Parent = nodes[lowS - 1].Parent = (ushort)nodeNb;
        nodeNb++;
        lowS -= 2;
        for (int n = nodeNb; n <= nodeRoot; n++)
        {
            nodes[n].Count = 1u << 30;
        }

        nodes0[0].Count = 1u << 31; // a barrier below the first symbol

        while (nodeNb <= nodeRoot)
        {
            int n1 = nodes[lowS].Count < nodes[lowN].Count ? lowS-- : lowN++;
            int n2 = nodes[lowS].Count < nodes[lowN].Count ? lowS-- : lowN++;
            nodes[nodeNb].Count = nodes[n1].Count + nodes[n2].Count;
            nodes[n1].Parent = nodes[n2].Parent = (ushort)nodeNb;
            nodeNb++;
        }

        nodes[nodeRoot].NbBits = 0;
        for (int n = nodeRoot - 1; n >= StartNode; n--)
        {
            nodes[n].NbBits = (byte)(nodes[nodes[n].Parent].NbBits + 1);
        }

        for (int n = 0; n <= nonNullRank; n++)
        {
            nodes[n].NbBits = (byte)(nodes[nodes[n].Parent].NbBits + 1);
        }

        return nonNullRank;
    }

    /// <summary>
    /// libzstd's <c>HUF_setMaxHeight</c>: the codes longer than <paramref name="targetNbBits"/> cut to
    /// it, and the cost repaid by lengthening the cheapest shorter ones.
    /// </summary>
    /// <returns>The longest code after the adjustment.</returns>
    private static uint SetMaxHeight(Node* nodes, uint lastNonNull, uint targetNbBits)
    {
        uint largestBits = nodes[lastNonNull].NbBits;
        if (largestBits <= targetNbBits)
        {
            return largestBits;
        }

        int totalCost = 0;
        uint baseCost = 1u << (int)(largestBits - targetNbBits);
        int n = (int)lastNonNull;
        while (nodes[n].NbBits > targetNbBits)
        {
            totalCost += (int)(baseCost - (1u << (int)(largestBits - nodes[n].NbBits)));
            nodes[n].NbBits = (byte)targetNbBits;
            n--;
        }

        while (nodes[n].NbBits == targetNbBits)
        {
            n--;
        }

        totalCost >>= (int)(largestBits - targetNbBits);

        const uint NoSymbol = 0xF0F0F0F0;
        uint* rankLast = stackalloc uint[HuffmanTable.MaxTableLog + 2];
        Unsafe.InitBlockUnaligned(rankLast, 0xF0, (HuffmanTable.MaxTableLog + 2) * sizeof(uint));
        {
            uint currentNbBits = targetNbBits;
            for (int position = n; position >= 0; position--)
            {
                if (nodes[position].NbBits >= currentNbBits)
                {
                    continue;
                }

                currentNbBits = nodes[position].NbBits;
                rankLast[targetNbBits - currentNbBits] = (uint)position;
            }
        }

        while (totalCost > 0)
        {
            // Lengthen the next power of two above the cost, which gains back half the rank.
            uint nBitsToDecrease = (uint)BitOperations.Log2((uint)totalCost) + 1;
            for (; nBitsToDecrease > 1; nBitsToDecrease--)
            {
                uint highPosition = rankLast[nBitsToDecrease];
                uint lowPosition = rankLast[nBitsToDecrease - 1];
                if (highPosition == NoSymbol)
                {
                    continue;
                }

                if (lowPosition == NoSymbol)
                {
                    break;
                }

                uint highTotal = nodes[highPosition].Count;
                uint lowTotal = 2 * nodes[lowPosition].Count;
                if (highTotal <= lowTotal)
                {
                    break;
                }
            }

            while (nBitsToDecrease <= HuffmanTable.MaxTableLog && rankLast[nBitsToDecrease] == NoSymbol)
            {
                nBitsToDecrease++;
            }

            totalCost -= 1 << (int)(nBitsToDecrease - 1);
            nodes[rankLast[nBitsToDecrease]].NbBits++;

            if (rankLast[nBitsToDecrease - 1] == NoSymbol)
            {
                rankLast[nBitsToDecrease - 1] = rankLast[nBitsToDecrease];
            }

            if (rankLast[nBitsToDecrease] == 0)
            {
                rankLast[nBitsToDecrease] = NoSymbol;
            }
            else
            {
                rankLast[nBitsToDecrease]--;
                if (nodes[rankLast[nBitsToDecrease]].NbBits != targetNbBits - nBitsToDecrease)
                {
                    rankLast[nBitsToDecrease] = NoSymbol;
                }
            }
        }

        while (totalCost < 0)
        {
            // Overshot: the largest symbols of the longest rank take one bit less.
            if (rankLast[1] == NoSymbol)
            {
                while (nodes[n].NbBits == targetNbBits)
                {
                    n--;
                }

                nodes[n + 1].NbBits--;
                rankLast[1] = (uint)(n + 1);
                totalCost++;
                continue;
            }

            nodes[rankLast[1] + 1].NbBits--;
            rankLast[1]++;
            totalCost++;
        }

        return targetNbBits;
    }

    /// <summary>
    /// libzstd's <c>HUF_buildCTable_wksp</c>: the canonical codes of a histogram, at most
    /// <paramref name="maxNbBits"/> long.
    /// </summary>
    /// <returns>The longest code.</returns>
    public static uint BuildCTable(HuffmanCTable table, uint* count, uint maxSymbolValue, uint maxNbBits)
    {
        if (maxNbBits == 0)
        {
            maxNbBits = LiteralsTableLog;
        }

        Node* nodes0 = stackalloc Node[2 * HuffmanTable.MaxSymbols];
        RankPosition* rankPosition = stackalloc RankPosition[RankPositionTableSize];
        Unsafe.InitBlockUnaligned(nodes0, 0, (uint)(sizeof(Node) * 2 * HuffmanTable.MaxSymbols));
        Node* nodes = nodes0 + 1;

        Sort(nodes, count, maxSymbolValue, rankPosition);
        int nonNullRank = BuildTree(nodes, maxSymbolValue);
        maxNbBits = SetMaxHeight(nodes, (uint)nonNullRank, maxNbBits);
        if (maxNbBits > HuffmanTable.MaxTableLog)
        {
            throw new InvalidOperationException("HUF_buildCTable: tree too deep");
        }

        // HUF_buildCTableFromTree: the starting code of each length, from the longest.
        ushort* nbPerRank = stackalloc ushort[HuffmanTable.MaxTableLog + 1];
        ushort* valuePerRank = stackalloc ushort[HuffmanTable.MaxTableLog + 1];
        Unsafe.InitBlockUnaligned(nbPerRank, 0, (HuffmanTable.MaxTableLog + 1) * sizeof(ushort));
        Unsafe.InitBlockUnaligned(valuePerRank, 0, (HuffmanTable.MaxTableLog + 1) * sizeof(ushort));
        int alphabetSize = (int)maxSymbolValue + 1;
        for (int n = 0; n <= nonNullRank; n++)
        {
            nbPerRank[nodes[n].NbBits]++;
        }

        ushort min = 0;
        for (int n = (int)maxNbBits; n > 0; n--)
        {
            valuePerRank[n] = min;
            min += nbPerRank[n];
            min >>= 1;
        }

        ulong* elements = table.Elements;
        for (int n = 0; n < alphabetSize; n++)
        {
            elements[nodes[n].Symbol] = nodes[n].NbBits;
        }

        for (int n = 0; n < alphabetSize; n++)
        {
            int nbBits = HuffmanCTable.NbBits(elements[n]);
            ulong value = valuePerRank[nbBits]++;
            if (nbBits > 0)
            {
                elements[n] |= value << (64 - nbBits);
            }
        }

        table.TableLog = (int)maxNbBits;
        table.MaxSymbolValue = (int)maxSymbolValue;
        return maxNbBits;
    }

    /// <summary>libzstd's <c>HUF_estimateCompressedSize</c>, in whole bytes.</summary>
    public static nuint EstimateCompressedSize(HuffmanCTable table, uint* count, uint maxSymbolValue)
    {
        nuint nbBits = 0;
        for (uint s = 0; s <= maxSymbolValue; s++)
        {
            nbBits += (nuint)HuffmanCTable.NbBits(table.Elements[s]) * count[s];
        }

        return nbBits >> 3;
    }

    /// <summary>libzstd's <c>HUF_validateCTable</c>: whether the table has a code for every symbol counted.</summary>
    public static bool ValidateCTable(HuffmanCTable table, uint* count, uint maxSymbolValue)
    {
        if (table.MaxSymbolValue < maxSymbolValue)
        {
            return false;
        }

        bool bad = false;
        for (uint s = 0; s <= maxSymbolValue; s++)
        {
            bad |= (count[s] != 0) & (HuffmanCTable.NbBits(table.Elements[s]) == 0);
        }

        return !bad;
    }

    /// <summary>
    /// libzstd's <c>HUF_writeCTable_wksp</c>: the tree as the weights of all symbols but the last,
    /// FSE-compressed when that is shorter than half of them, otherwise four bits each.
    /// </summary>
    /// <returns>The size of the description.</returns>
    public static nuint WriteCTable(byte* destination, nuint capacity, HuffmanCTable table, uint maxSymbolValue, uint huffLog, FseCTable weightsTable)
    {
        byte* bitsToWeight = stackalloc byte[HuffmanTable.MaxTableLog + 1];
        byte* weights = stackalloc byte[HuffmanTable.MaxSymbols];
        bitsToWeight[0] = 0;
        for (uint n = 1; n < huffLog + 1; n++)
        {
            bitsToWeight[n] = (byte)(huffLog + 1 - n);
        }

        for (uint n = 0; n < maxSymbolValue; n++)
        {
            weights[n] = bitsToWeight[HuffmanCTable.NbBits(table.Elements[n])];
        }

        nuint size = CompressWeights(destination + 1, capacity - 1, weights, maxSymbolValue, weightsTable);
        if (size > 1 && size < maxSymbolValue / 2)
        {
            destination[0] = (byte)size;
            return size + 1;
        }

        if (maxSymbolValue > 256 - 128)
        {
            throw new InvalidOperationException("HUF_writeCTable: too many weights for the direct form");
        }

        destination[0] = (byte)(128 + (maxSymbolValue - 1));
        weights[maxSymbolValue] = 0;
        for (uint n = 0; n < maxSymbolValue; n += 2)
        {
            destination[(n / 2) + 1] = (byte)((weights[n] << 4) + weights[n + 1]);
        }

        return ((maxSymbolValue + 1) / 2) + 1;
    }

    /// <summary>
    /// libzstd's <c>HUF_compressWeights</c>: the weights FSE-compressed, with an accuracy of 6 at most.
    /// </summary>
    /// <returns>The size, 0 when they do not compress, 1 when they are all equal.</returns>
    private static nuint CompressWeights(byte* destination, nuint capacity, byte* weights, nuint count, FseCTable table)
    {
        if (count <= 1)
        {
            return 0;
        }

        uint* histogram = stackalloc uint[HuffmanTable.MaxTableLog + 1];
        short* normalized = stackalloc short[HuffmanTable.MaxTableLog + 1];
        uint maxSymbolValue = HuffmanTable.MaxTableLog;
        uint maxCount = Histogram.CountSimple(histogram, ref maxSymbolValue, weights, count);
        if (maxCount == count)
        {
            return 1;
        }

        if (maxCount == 1)
        {
            return 0;
        }

        uint tableLog = FseEncoder.OptimalTableLog(WeightsMaxTableLog, count, maxSymbolValue);
        FseEncoder.NormalizeCount(normalized, tableLog, histogram, count, maxSymbolValue, useLowProbCount: false);
        byte* op = destination;
        op += FseEncoder.WriteNCount(op, normalized, maxSymbolValue, tableLog);

        FseEncoder.BuildCTable(table, normalized, maxSymbolValue, tableLog);
        nuint size = FseEncoder.Compress(op, capacity - (nuint)(op - destination), weights, count, table);
        if (size == 0)
        {
            return 0;
        }

        op += size;
        return (nuint)(op - destination);
    }

    /// <summary>
    /// libzstd's <c>HUF_optimalTableLog</c>: the cheap estimate, or with <paramref name="optimalDepth"/>
    /// (strategies from btultra) every depth tried until the description and the codes grow.
    /// </summary>
    public static uint OptimalTableLog(uint maxTableLog, nuint size, uint maxSymbolValue, HuffmanWorkspace workspace, uint* count, bool optimalDepth)
    {
        if (!optimalDepth)
        {
            return FseEncoder.OptimalTableLog(maxTableLog, size, maxSymbolValue, 1);
        }

        byte* description = stackalloc byte[HuffmanTable.MaxSymbols + 64];
        uint cardinality = 0;
        for (uint i = 0; i <= maxSymbolValue; i++)
        {
            cardinality += count[i] != 0 ? 1u : 0u;
        }

        uint minTableLog = (uint)BitOperations.Log2(cardinality) + 1;
        nuint optimalSize = nuint.MaxValue - 1;
        uint optimalLog = maxTableLog;
        for (uint guess = minTableLog; guess <= maxTableLog; guess++)
        {
            uint maxBits = BuildCTable(workspace.Table, count, maxSymbolValue, guess);
            if (maxBits < guess && guess > minTableLog)
            {
                break;
            }

            nuint headerSize = WriteCTable(description, HuffmanTable.MaxSymbols + 64, workspace.Table, maxSymbolValue, maxBits, workspace.Weights);
            nuint newSize = EstimateCompressedSize(workspace.Table, count, maxSymbolValue) + headerSize;
            if (newSize > optimalSize + 1)
            {
                break;
            }

            if (newSize < optimalSize)
            {
                optimalSize = newSize;
                optimalLog = guess;
            }
        }

        return optimalLog;
    }

    /// <summary>
    /// libzstd's <c>HUF_compress1X_usingCTable_internal</c>: one stream, the symbols from the last, so
    /// that the decoder reads them from the first.
    /// </summary>
    /// <returns>The size of the stream, or 0 when it does not fit.</returns>
    public static nuint Compress1X(byte* destination, nuint capacity, byte* source, nuint size, HuffmanCTable table)
    {
        if (capacity < 8)
        {
            return 0;
        }

        var bits = new HuffmanWriter(destination, capacity);
        if (!bits.IsValid)
        {
            return 0;
        }

        ulong* elements = table.Elements;
        for (nuint n = size; n > 0;)
        {
            bits.Add(elements[source[--n]]);
            if (bits.BitPosition > 64 - HuffmanTable.MaxTableLog)
            {
                bits.Flush();
            }
        }

        return bits.Close();
    }

    /// <summary>
    /// libzstd's <c>HUF_compress4X_usingCTable_internal</c>: a jump table of three sizes, then four
    /// streams, each a quarter of the symbols rounded up, the last taking what remains.
    /// </summary>
    /// <returns>The size of the section, or 0 when it does not fit or a stream exceeds 64 KiB.</returns>
    public static nuint Compress4X(byte* destination, nuint capacity, byte* source, nuint size, HuffmanCTable table)
    {
        nuint segmentSize = (size + 3) / 4;
        byte* ip = source;
        byte* iend = source + size;
        byte* oend = destination + capacity;
        byte* op = destination;
        if (capacity < 6 + 1 + 1 + 1 + 8 || size < 12)
        {
            return 0;
        }

        op += 6;
        for (int stream = 0; stream < 4; stream++)
        {
            nuint length = stream < 3 ? segmentSize : (nuint)(iend - ip);
            nuint compressed = Compress1X(op, (nuint)(oend - op), ip, length, table);
            if (compressed == 0 || compressed > 65535)
            {
                return 0;
            }

            if (stream < 3)
            {
                Unsafe.WriteUnaligned(destination + (2 * stream), (ushort)compressed);
            }

            op += compressed;
            ip += length;
        }

        return (nuint)(op - destination);
    }

    /// <summary>libzstd's <c>HUF_compressCTable_internal</c>: refused unless it saves two bytes or more.</summary>
    private static nuint CompressWithTable(byte* start, byte* op, byte* end, byte* source, nuint size, bool singleStream, HuffmanCTable table)
    {
        nuint compressed = singleStream
            ? Compress1X(op, (nuint)(end - op), source, size, table)
            : Compress4X(op, (nuint)(end - op), source, size, table);
        if (compressed == 0)
        {
            return 0;
        }

        op += compressed;
        if ((nuint)(op - start) >= size - 1)
        {
            return 0;
        }

        return (nuint)(op - start);
    }

    /// <summary>
    /// libzstd's <c>HUF_compress_internal</c> as <c>ZSTD_compressLiterals</c> calls it: the literals
    /// Huffman-coded with the previous tree or a new one, whichever the heuristics prefer.
    /// </summary>
    /// <remarks>
    /// <paramref name="previous"/> is <c>nextHuf->CTable</c>, a copy of the previous block's tree,
    /// replaced by the new one when that one is used. <paramref name="repeat"/> says on entry whether
    /// the previous tree may serve, and on return whether it did (<see cref="HuffmanRepeat.None"/>
    /// otherwise).
    /// </remarks>
    /// <returns>The size of the section after the literals header, 0 when not compressible, 1 for one repeated byte.</returns>
    public static nuint Compress(
        byte* destination, nuint capacity, byte* source, nuint size, bool singleStream, HuffmanCTable previous, HuffmanWorkspace workspace,
        ref HuffmanRepeat repeat, bool preferRepeat, bool optimalDepth, bool suspectUncompressible)
    {
        const int SuspectIncompressibleSampleSize = 4096;
        const int SuspectIncompressibleSampleRatio = 10;
        byte* op = destination;
        byte* end = destination + capacity;
        if (size == 0 || capacity == 0)
        {
            return 0;
        }

        // A valid previous tree for a small input.
        if (preferRepeat && repeat == HuffmanRepeat.Valid)
        {
            return CompressWithTable(destination, op, end, source, size, singleStream, previous);
        }

        uint* count = stackalloc uint[HuffmanTable.MaxSymbols];
        if (suspectUncompressible && size >= SuspectIncompressibleSampleSize * SuspectIncompressibleSampleRatio)
        {
            // A sample of each end first.
            uint maxBegin = 255;
            uint maxEnd = 255;
            nuint largestTotal = Histogram.CountSimple(count, ref maxBegin, source, SuspectIncompressibleSampleSize);
            largestTotal += Histogram.CountSimple(count, ref maxEnd, source + size - SuspectIncompressibleSampleSize, SuspectIncompressibleSampleSize);
            if (largestTotal <= ((2 * SuspectIncompressibleSampleSize) >> 7) + 4)
            {
                return 0;
            }
        }

        uint largest = Histogram.CountFast(count, out uint maxSymbolValue, source, size);
        if (largest == size)
        {
            *destination = *source;
            return 1;
        }

        if (largest <= (size >> 7) + 4)
        {
            return 0;
        }

        if (repeat == HuffmanRepeat.Check && !ValidateCTable(previous, count, maxSymbolValue))
        {
            repeat = HuffmanRepeat.None;
        }

        if (preferRepeat && repeat != HuffmanRepeat.None)
        {
            return CompressWithTable(destination, op, end, source, size, singleStream, previous);
        }

        HuffmanCTable table = workspace.Table;
        uint huffLog = OptimalTableLog(LiteralsTableLog, size, maxSymbolValue, workspace, count, optimalDepth);
        huffLog = BuildCTable(table, count, maxSymbolValue, huffLog);

        nuint headerSize = WriteCTable(op, capacity, table, maxSymbolValue, huffLog, workspace.Weights);
        if (repeat != HuffmanRepeat.None)
        {
            // The previous tree, unless the new one saves more than its description.
            nuint oldSize = EstimateCompressedSize(previous, count, maxSymbolValue);
            nuint newSize = EstimateCompressedSize(table, count, maxSymbolValue);
            if (oldSize <= headerSize + newSize || headerSize + 12 >= size)
            {
                return CompressWithTable(destination, op, end, source, size, singleStream, previous);
            }
        }

        if (headerSize + 12 >= size)
        {
            return 0;
        }

        op += headerSize;
        repeat = HuffmanRepeat.None;
        previous.CopyFrom(table);
        return CompressWithTable(destination, op, end, source, size, singleStream, table);
    }
}

/// <summary>
/// libzstd's <c>HUF_CStream_t</c>, one container: codes added at the top as the container shifts
/// right, flushed from the top. The bytes are those of a <see cref="BitWriter"/> adding each code.
/// </summary>
internal unsafe struct HuffmanWriter
{
    private ulong _container;
    private int _bitPosition;
    private readonly byte* _start;
    private byte* _ptr;
    private readonly byte* _end;

    public HuffmanWriter(byte* start, nuint capacity)
    {
        _start = start;
        _ptr = start;
        _end = start + capacity - sizeof(ulong);
        IsValid = capacity > sizeof(ulong);
    }

    public readonly bool IsValid { get; }

    public readonly int BitPosition => _bitPosition;

    /// <summary>libzstd's <c>HUF_addBits</c>: a code, from its <see cref="HuffmanCTable"/> element.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(ulong element)
    {
        int nbBits = (int)(element & 0xFF);
        _container = (_container >> nbBits) | (element & ~0xFFUL);
        _bitPosition += nbBits;
    }

    /// <summary>libzstd's <c>HUF_flushBits</c>: the whole bytes out, never past <see cref="_end"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush()
    {
        int nbBits = _bitPosition;
        if (nbBits == 0)
        {
            return;
        }

        Unsafe.WriteUnaligned(_ptr, _container >> (64 - nbBits));
        _ptr += nbBits >> 3;
        if (_ptr > _end)
        {
            _ptr = _end;
        }

        _bitPosition &= 7;
    }

    /// <summary>libzstd's <c>HUF_closeCStream</c>: the end marker, then the last bytes.</summary>
    /// <returns>The size of the stream, or 0 when it did not fit.</returns>
    public nuint Close()
    {
        Add(1UL | (1UL << 63));
        Flush();
        if (_ptr >= _end)
        {
            return 0;
        }

        return (nuint)(_ptr - _start) + (_bitPosition > 0 ? 1u : 0u);
    }
}
