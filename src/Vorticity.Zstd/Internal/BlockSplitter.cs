using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

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
    public static int Split(byte* block, int blockSize, int level) => level switch
    {
        0 => SplitFromBorders(block, blockSize),
        1 => SplitByChunks<Every43rd>(block, blockSize),
        2 => SplitByChunks<Every11th>(block, blockSize),
        3 => SplitByChunks<Every5th>(block, blockSize),
        _ => SplitByChunks<Every1st>(block, blockSize),
    };

    /// <summary>libzstd's <c>ZSTD_recordFingerprint_N</c>: a chunk's events, the positions sampled at a constant rate.</summary>
    private static void Record<TSampling>(Fingerprint* fingerprint, byte* source)
        where TSampling : ISampling
    {
        uint* events = fingerprint->Events;
        new Span<uint>(events, 1 << TSampling.HashLog).Clear();
        const nuint Limit = ChunkSize - 2 + 1;
        for (nuint n = 0; n < Limit; n += TSampling.Rate)
        {
            // libzstd's hash2: the byte for 8 bits, the pair hashed for more.
            nuint hash = TSampling.HashLog == 8
                ? source[n]
                : (Unsafe.ReadUnaligned<ushort>(source + n) * Knuth) >> (32 - TSampling.HashLog);
            events[hash]++;
        }

        fingerprint->EventCount = Limit / TSampling.Rate;
    }

    /// <summary>
    /// libzstd's <c>fpDistance</c>: how far two histograms of <paramref name="bins"/> bins are, each
    /// scaled by the other's total, |a[n] * B - b[n] * A| summed; with <paramref name="merge"/>, the
    /// bins of <paramref name="b"/> added into <paramref name="a"/>'s on the way (libzstd's
    /// <c>mergeEvents</c>, which a split leaves unused).
    /// </summary>
    /// <remarks>
    /// The totals are below 2^17 (15 chunks of 8 KiB at most), so are the bins: each product, a 64-bit
    /// unsigned one of 32-bit halves, is exact, and so is their difference, its sign that of the
    /// 64-bit integer modulo 2^64.
    /// </remarks>
    private static ulong Distance(Fingerprint* a, Fingerprint* b, int bins, bool merge)
    {
        uint* aEvents = a->Events;
        uint* bEvents = b->Events;
        if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<uint> aTotal = Vector128.Create((uint)a->EventCount);
            Vector128<uint> bTotal = Vector128.Create((uint)b->EventCount);
            Vector128<long> low = Vector128<long>.Zero;
            Vector128<long> high = Vector128<long>.Zero;
            for (nuint n = 0; n < (nuint)bins; n += 4)
            {
                Vector128<uint> aBins = AdvSimd.LoadVector128(aEvents + n);
                Vector128<uint> bBins = AdvSimd.LoadVector128(bEvents + n);
                Vector128<ulong> lowDifference = AdvSimd.MultiplyWideningLowerAndSubtract(
                    AdvSimd.MultiplyWideningLower(aBins.GetLower(), bTotal.GetLower()), bBins.GetLower(), aTotal.GetLower());
                Vector128<ulong> highDifference = AdvSimd.MultiplyWideningUpperAndSubtract(
                    AdvSimd.MultiplyWideningUpper(aBins, bTotal), bBins, aTotal);
                low += AdvSimd.Arm64.Abs(lowDifference.AsInt64()).AsInt64();
                high += AdvSimd.Arm64.Abs(highDifference.AsInt64()).AsInt64();
                if (merge)
                {
                    AdvSimd.Store(aEvents + n, aBins + bBins);
                }
            }

            return (ulong)AdvSimd.Arm64.AddPairwiseScalar(low + high).ToScalar();
        }

        long aCount = (long)a->EventCount;
        long bCount = (long)b->EventCount;
        ulong distance = 0;
        for (nuint n = 0; n < (nuint)bins; n++)
        {
            long difference = (aEvents[n] * bCount) - (bEvents[n] * aCount);
            long sign = difference >> 63;
            distance += (ulong)((difference ^ sign) - sign);
            if (merge)
            {
                aEvents[n] += bEvents[n];
            }
        }

        return distance;
    }

    /// <summary>libzstd's <c>compareFingerprints</c>: whether the new part is too different from the reference.</summary>
    private static bool IsDifferent(ulong distance, Fingerprint* reference, Fingerprint* next, int penalty)
    {
        ulong p50 = (ulong)reference->EventCount * (ulong)next->EventCount;
        ulong threshold = p50 * (ulong)(ThresholdBase + penalty) / ThresholdPenaltyRate;
        return distance >= threshold;
    }

    /// <summary>libzstd's <c>ZSTD_splitBlock_byChunks</c>.</summary>
    private static int SplitByChunks<TSampling>(byte* block, int blockSize)
        where TSampling : ISampling
    {
        Fingerprint past;
        Fingerprint next;
        Record<TSampling>(&past, block);
        int penalty = ThresholdPenalty;
        int position;
        for (position = ChunkSize; position <= blockSize - ChunkSize; position += ChunkSize)
        {
            Record<TSampling>(&next, block + position);

            // The past takes the next's events as the distance is summed: libzstd merges them only
            // when it does not split, past which the past is not read.
            if (IsDifferent(Distance(&past, &next, 1 << TSampling.HashLog, merge: true), &past, &next, penalty))
            {
                return position;
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
        const int Bins = 1 << 8;
        Fingerprint begin;
        Fingerprint end;
        Fingerprint middle;
        Unsafe.InitBlockUnaligned(begin.Events, 0, Bins * sizeof(uint));
        Unsafe.InitBlockUnaligned(end.Events, 0, Bins * sizeof(uint));
        Unsafe.InitBlockUnaligned(middle.Events, 0, Bins * sizeof(uint));
        Add(begin.Events, block, SegmentSize);
        Add(end.Events, block + blockSize - SegmentSize, SegmentSize);
        begin.EventCount = end.EventCount = SegmentSize;
        if (!IsDifferent(Distance(&begin, &end, Bins, merge: false), &begin, &end, 0))
        {
            return blockSize;
        }

        Add(middle.Events, block + (blockSize / 2) - (SegmentSize / 2), SegmentSize);
        middle.EventCount = SegmentSize;
        ulong distanceFromBegin = Distance(&begin, &middle, Bins, merge: false);
        ulong distanceFromEnd = Distance(&end, &middle, Bins, merge: false);
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

    /// <summary>How <see cref="Record"/> samples a chunk: every <see cref="Rate"/>th position, hashed to <see cref="HashLog"/> bits.</summary>
    private interface ISampling
    {
        static abstract nuint Rate { get; }

        static abstract int HashLog { get; }
    }

    private readonly struct Every43rd : ISampling
    {
        public static nuint Rate => 43;

        public static int HashLog => 8;
    }

    private readonly struct Every11th : ISampling
    {
        public static nuint Rate => 11;

        public static int HashLog => 9;
    }

    private readonly struct Every5th : ISampling
    {
        public static nuint Rate => 5;

        public static int HashLog => 10;
    }

    private readonly struct Every1st : ISampling
    {
        public static nuint Rate => 1;

        public static int HashLog => 10;
    }
}
