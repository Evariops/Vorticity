using System;
using System.Runtime.CompilerServices;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's <c>zstd_preSplit.c</c>: where a full block is better cut before its match finding, from
/// how differently its parts distribute their bytes.
/// </summary>
internal static unsafe class BlockSplitter
{
    private const int ThresholdPenaltyRate = 16;
    private const int ThresholdBase = ThresholdPenaltyRate - 2;
    private const int ThresholdPenalty = 3;
    private const int HashLogMax = 10;
    private const int HashTableSize = 1 << HashLogMax;
    private const uint Knuth = 0x9e3779b9;
    private const int ChunkSize = 8 << 10;
    private const int SegmentSize = 512;

    /// <summary>libzstd's <c>Fingerprint</c>: a histogram of the hashes of byte pairs, or of bytes.</summary>
    private struct Fingerprint
    {
        public fixed uint Events[HashTableSize];
        public nuint EventCount;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_splitBlock</c> on a full block: <paramref name="level"/> 0 compares its ends
    /// and middle, 1 to 4 compare its 8 KiB chunks with what precedes them, sampling every 43rd,
    /// 11th, 5th or every position.
    /// </summary>
    /// <returns>The size of the first block to cut.</returns>
    public static int Split(byte* block, int blockSize, int level)
    {
        if (level == 0)
        {
            return SplitFromBorders(block, blockSize);
        }

        return SplitByChunks(block, blockSize, level - 1);
    }

    /// <summary>libzstd's <c>hash2</c>: two bytes for more than 8 bits, one byte for 8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash2(byte* p, int hashLog) =>
        hashLog == 8 ? *p : (Unsafe.ReadUnaligned<ushort>(p) * Knuth) >> (32 - hashLog);

    /// <summary>libzstd's <c>recordFingerprint_generic</c>.</summary>
    private static void Record(Fingerprint* fingerprint, byte* source, nuint size, nuint samplingRate, int hashLog)
    {
        Unsafe.InitBlockUnaligned(fingerprint->Events, 0, (uint)(sizeof(uint) << hashLog));
        nuint limit = size - 2 + 1;
        for (nuint n = 0; n < limit; n += samplingRate)
        {
            fingerprint->Events[Hash2(source + n, hashLog)]++;
        }

        fingerprint->EventCount = limit / samplingRate;
    }

    /// <summary>libzstd's <c>fpDistance</c>: how far two histograms are, each scaled by the other's total.</summary>
    private static ulong Distance(Fingerprint* a, Fingerprint* b, int hashLog)
    {
        ulong distance = 0;
        for (nuint n = 0; n < (nuint)1 << hashLog; n++)
        {
            long difference = ((long)a->Events[n] * (long)b->EventCount) - ((long)b->Events[n] * (long)a->EventCount);
            distance += (ulong)Math.Abs(difference);
        }

        return distance;
    }

    /// <summary>libzstd's <c>compareFingerprints</c>: whether the new part is too different from the reference.</summary>
    private static bool IsDifferent(Fingerprint* reference, Fingerprint* next, int penalty, int hashLog)
    {
        ulong p50 = (ulong)reference->EventCount * (ulong)next->EventCount;
        ulong deviation = Distance(reference, next, hashLog);
        ulong threshold = p50 * (ulong)(ThresholdBase + penalty) / ThresholdPenaltyRate;
        return deviation >= threshold;
    }

    /// <summary>libzstd's <c>ZSTD_splitBlock_byChunks</c>.</summary>
    private static int SplitByChunks(byte* block, int blockSize, int level)
    {
        ReadOnlySpan<int> samplingRates = [43, 11, 5, 1];
        ReadOnlySpan<int> hashLogs = [8, 9, 10, 10];
        nuint rate = (nuint)samplingRates[level];
        int hashLog = hashLogs[level];
        Fingerprint past;
        Fingerprint next;
        Record(&past, block, ChunkSize, rate, hashLog);
        int penalty = ThresholdPenalty;
        int position;
        for (position = ChunkSize; position <= blockSize - ChunkSize; position += ChunkSize)
        {
            Record(&next, block + position, ChunkSize, rate, hashLog);
            if (IsDifferent(&past, &next, penalty, hashLog))
            {
                return position;
            }

            // libzstd's mergeEvents adds all its counters; those past the hash log's are never read.
            for (int n = 0; n < 1 << hashLog; n++)
            {
                past.Events[n] += next.Events[n];
            }

            past.EventCount += next.EventCount;
            if (penalty > 0)
            {
                penalty--;
            }
        }

        return blockSize;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_splitBlock_fromBorders</c>: the byte histograms of the first and last 512
    /// bytes; when they differ, the middle one says where to cut.
    /// </summary>
    private static int SplitFromBorders(byte* block, int blockSize)
    {
        Fingerprint begin;
        Fingerprint end;
        Fingerprint middle;
        Unsafe.InitBlockUnaligned(begin.Events, 0, HashTableSize * sizeof(uint));
        Unsafe.InitBlockUnaligned(end.Events, 0, HashTableSize * sizeof(uint));
        Unsafe.InitBlockUnaligned(middle.Events, 0, HashTableSize * sizeof(uint));
        Add(begin.Events, block, SegmentSize);
        Add(end.Events, block + blockSize - SegmentSize, SegmentSize);
        begin.EventCount = end.EventCount = SegmentSize;
        if (!IsDifferent(&begin, &end, 0, 8))
        {
            return blockSize;
        }

        Add(middle.Events, block + (blockSize / 2) - (SegmentSize / 2), SegmentSize);
        middle.EventCount = SegmentSize;
        ulong distanceFromBegin = Distance(&begin, &middle, 8);
        ulong distanceFromEnd = Distance(&end, &middle, 8);
        const ulong MinDistance = SegmentSize * SegmentSize / 3;
        if ((ulong)Math.Abs((long)distanceFromBegin - (long)distanceFromEnd) < MinDistance)
        {
            return 64 << 10;
        }

        return distanceFromBegin > distanceFromEnd ? 32 << 10 : 96 << 10;

        static void Add(uint* events, byte* source, int size)
        {
            for (int i = 0; i < size; i++)
            {
                events[source[i]]++;
            }
        }
    }
}
