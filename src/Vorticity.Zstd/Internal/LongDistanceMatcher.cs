using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>libzstd's <c>rawSeq</c>: a long-distance match, and the literals before it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawSequence
{
    public uint Offset;
    public uint LiteralLength;
    public uint MatchLength;
}

/// <summary>
/// libzstd's <c>ZSTD_optLdm_t</c>: a block's long-distance sequences as the optimal parser walks the
/// block, and the match among them that covers the parser's position, if any.
/// </summary>
internal unsafe struct LongDistanceCursor
{
    /// <summary>The block's sequences: <see cref="LongDistanceMatcher.Sequences"/>.</summary>
    public RawSequence* Sequences;

    /// <summary>The number of <see cref="Sequences"/>; 0 when the matcher is off.</summary>
    public nuint Count;

    private nuint _position;
    private uint _positionInSequence;
    private uint _start;
    private uint _end;
    private uint _offset;

    /// <summary>The cursor at the start of a block of <paramref name="size"/> bytes, on its first match.</summary>
    public void Begin(uint size)
    {
        _position = 0;
        _positionInSequence = 0;
        _start = 0;
        _end = 0;
        _offset = 0;
        Next(0, size);
    }

    /// <summary>
    /// libzstd's <c>ZSTD_optLdm_processMatchCandidate</c> and <c>ZSTD_optLdm_maybeAddMatch</c>: the
    /// match covering <paramref name="position"/> appended to <paramref name="matches"/> when it is
    /// long enough and longer than the longest there.
    /// </summary>
    /// <remarks>
    /// As in libzstd 1.5.7, nothing is added once the last sequence has been reached: the cursor takes
    /// up a sequence and moves past it at once, so the block's last match is never offered.
    /// </remarks>
    /// <returns>The number of matches.</returns>
    public uint Process(OptimalMatch* matches, uint matchCount, uint position, uint remaining, uint minMatch)
    {
        if (_position >= Count)
        {
            return matchCount;
        }

        if (position >= _end)
        {
            // The parser may stand past the match's end: the overshoot is skipped.
            if (position > _end)
            {
                Skip(position - _end);
            }

            Next(position, remaining);
        }

        uint length = _end - _start - (position - _start);
        if (position < _start || position >= _end || length < minMatch)
        {
            return matchCount;
        }

        if (matchCount == 0 || (length > matches[matchCount - 1].Length && matchCount < OptimalState.OptNum))
        {
            matches[matchCount].Length = length;
            matches[matchCount].OffBase = _offset + RepeatCodeCount;
            matchCount++;
        }

        return matchCount;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_opt_getNextMatchAndUpdateSeqStore</c>: the next match's bounds in the block,
    /// clipped to its end, and the cursor moved past it.
    /// </summary>
    private void Next(uint position, uint remaining)
    {
        if (_position >= Count)
        {
            _start = uint.MaxValue;
            _end = uint.MaxValue;
            return;
        }

        RawSequence* sequence = Sequences + _position;
        uint blockEnd = position + remaining;
        uint literalsLeft = _positionInSequence < sequence->LiteralLength ? sequence->LiteralLength - _positionInSequence : 0;
        uint matchLeft = literalsLeft == 0
            ? sequence->MatchLength - (_positionInSequence - sequence->LiteralLength)
            : sequence->MatchLength;

        // The rest of the block is literals.
        if (literalsLeft >= remaining)
        {
            _start = uint.MaxValue;
            _end = uint.MaxValue;
            Skip(remaining);
            return;
        }

        // A match shorter than the minimum is rejected when offered.
        _start = position + literalsLeft;
        _end = _start + matchLeft;
        _offset = sequence->Offset;
        if (_end > blockEnd)
        {
            _end = blockEnd;
            Skip(blockEnd - position);
        }
        else
        {
            Skip(literalsLeft + matchLeft);
        }
    }

    /// <summary>libzstd's <c>ZSTD_optLdm_skipRawSeqStoreBytes</c>.</summary>
    private void Skip(uint bytes)
    {
        uint position = _positionInSequence + bytes;
        while (position != 0 && _position < Count)
        {
            RawSequence* sequence = Sequences + _position;
            uint length = sequence->LiteralLength + sequence->MatchLength;
            if (position >= length)
            {
                position -= length;
                _position++;
            }
            else
            {
                _positionInSequence = position;
                break;
            }
        }

        if (position == 0 || _position == Count)
        {
            _positionInSequence = 0;
        }
    }
}

/// <summary>
/// libzstd's long-distance matcher (zstd_ldm.c), which it turns on for btopt and the stronger
/// strategies from a window of 128 MiB (level 22, sources over 64 MiB): positions chosen by a
/// rolling gear hash, about one in 2^hashRateLog, are kept in buckets by the XXH64 of the bytes
/// before them; a position whose bucket holds an entry with the same checksum and a long enough
/// match gives a sequence, which the optimal parser weighs as one more candidate.
/// </summary>
/// <remarks>
/// libzstd clears the matcher's tables and starts its window afresh at every frame: nothing is kept
/// from one frame to the next. A frame that loads a raw-content dictionary loads it here too (see
/// <see cref="LoadDictionary"/>), a segment of its own below the source.
/// </remarks>
internal sealed unsafe class LongDistanceMatcher
{
    /// <summary>libzstd's <c>ZSTD_LDM_DEFAULT_WINDOW_LOG</c>.</summary>
    public const int DefaultWindowLog = 27;

    /// <summary>libzstd's <c>LDM_BATCH_SIZE</c>: the split points handled at a time.</summary>
    private const int BatchSize = 64;

    /// <summary>libzstd's <c>LDM_BUCKET_SIZE_LOG</c>.</summary>
    private const int DefaultBucketSizeLog = 4;

    /// <summary>libzstd's <c>LDM_MIN_MATCH_LENGTH</c>.</summary>
    private const int DefaultMinMatchLength = 64;

    /// <summary>libzstd's <c>ZSTD_LDM_BUCKETSIZELOG_MAX</c>.</summary>
    private const int BucketSizeLogMax = 8;

    private const int HashLogMin = 6;
    private const int HashLogMax = 30;
    private const uint WindowStartIndex = 2;

    private ulong[] _table = [];
    private byte[] _bucketOffsets = [];
    private readonly RawSequence[] _sequences = GC.AllocateArray<RawSequence>((FrameFormat.MaxBlockSize / 32) + 1, pinned: true);
    private readonly nuint[] _splits = GC.AllocateArray<nuint>(BatchSize, pinned: true);
    private readonly Candidate[] _candidates = GC.AllocateArray<Candidate>(BatchSize, pinned: true);
    private int _windowLog;
    private int _hashLog;
    private int _bucketSizeLog;
    private int _minMatchLength;
    private int _hashRateLog;
    private ulong _stopMask;
    private byte* _base;
    private uint _dictLimit;
    private uint _lowLimit;

    /// <summary>The base of a dictionary's content below the source, as libzstd's <c>window.dictBase</c>.</summary>
    private byte* _dictBase;

    /// <summary>libzstd's <c>ldmState.loadedDictEnd</c>: the end of a loaded dictionary's content, 0 without one.</summary>
    private uint _loadedDictEnd;

    /// <summary>
    /// The sum of the first entry of every bucket searched, read ahead of the search as libzstd
    /// prefetches it: kept so that the reads are not removed.
    /// </summary>
    private ulong _touched;

    private struct Candidate
    {
        public byte* Split;
        public uint Hash;
        public uint Checksum;
    }

    /// <summary>The best match of a bucket's entries so far: libzstd's <c>bestEntry</c> and its lengths.</summary>
    private struct LongMatch
    {
        public ulong* Entry;
        public nuint Forward;
        public nuint Backward;
    }

    /// <summary>libzstd's <c>ZSTD_resolveEnableLdm</c>: whether libzstd turns the matcher on by itself.</summary>
    public static bool EnabledFor(in CompressionParameters parameters) =>
        parameters.Strategy >= Strategy.BinaryTreeOptimal && parameters.WindowLog >= DefaultWindowLog;

    /// <summary>The sequences the last <see cref="GenerateSequences"/> found.</summary>
    public RawSequence* Sequences => (RawSequence*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_sequences));

    /// <summary>
    /// libzstd's <c>ZSTD_ldm_adjustParameters</c> with every parameter left to it, then its reset for
    /// a frame: the tables cleared, the window starting at <paramref name="source"/> (index 2).
    /// </summary>
    public void BeginFrame(in CompressionParameters parameters, byte* source)
    {
        int strategy = (int)parameters.Strategy;
        _windowLog = parameters.WindowLog;
        _hashRateLog = 7 - (strategy / 3);
        _hashLog = Math.Clamp(_windowLog - _hashRateLog, HashLogMin, HashLogMax);
        _minMatchLength = parameters.Strategy >= Strategy.BinaryTreeUltra ? DefaultMinMatchLength / 2 : DefaultMinMatchLength;
        _bucketSizeLog = Math.Min(Math.Clamp(strategy, DefaultBucketSizeLog, BucketSizeLogMax), _hashLog);

        int tableSize = 1 << _hashLog;
        int bucketCount = 1 << (_hashLog - _bucketSizeLog);
        if (_table.Length < tableSize)
        {
            _table = GC.AllocateArray<ulong>(tableSize, pinned: true);
        }
        else
        {
            Array.Clear(_table, 0, tableSize);
        }

        if (_bucketOffsets.Length < bucketCount)
        {
            _bucketOffsets = GC.AllocateArray<byte>(bucketCount, pinned: true);
        }
        else
        {
            Array.Clear(_bucketOffsets, 0, bucketCount);
        }

        // libzstd's ZSTD_ldm_gear_init: the stop mask, its bits as high as the window of a match allows.
        int maxBitsInMask = Math.Min(_minMatchLength, 64);
        _stopMask = _hashRateLog > 0 && _hashRateLog <= maxBitsInMask
            ? ((1UL << _hashRateLog) - 1) << (maxBitsInMask - _hashRateLog)
            : (1UL << _hashRateLog) - 1;

        _base = source - WindowStartIndex;
        _dictLimit = WindowStartIndex;
        _lowLimit = WindowStartIndex;
        _dictBase = null;
        _loadedDictEnd = 0;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_loadDictionaryContent</c> for the matcher, after <see cref="BeginFrame"/>: the
    /// window over <paramref name="content"/> from index 2, its split points into the table
    /// (<c>ZSTD_ldm_fillHashTable</c>), then the frame's <paramref name="source"/> past it, not
    /// contiguous: the content becomes the window's extDict. Only raw content gets here:
    /// <c>ZSTD_loadZstdDictionary</c> passes the matcher no state for a zstd-format dictionary's.
    /// </summary>
    /// <param name="content">The dictionary's content, of 8 bytes or more.</param>
    /// <param name="size">Its size.</param>
    /// <param name="source">The frame's source.</param>
    public void LoadDictionary(byte* content, nuint size, byte* source)
    {
        _base = content - WindowStartIndex;
        _loadedDictEnd = (uint)size + WindowStartIndex;
        Fill(content, content + size);

        // libzstd's ZSTD_window_update for the source: the content, over 8 bytes, is searched apart.
        _dictBase = _base;
        _lowLimit = WindowStartIndex;
        _dictLimit = _loadedDictEnd;
        _base = source - _dictLimit;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_ldm_generateSequences</c> for a block: the window kept within its maximum
    /// distance, then the block's sequences, relative to its start.
    /// </summary>
    /// <returns>The number of sequences, in <see cref="Sequences"/>.</returns>
    public nuint GenerateSequences(byte* source, nuint size)
    {
        // libzstd's ZSTD_window_enforceMaxDist, at the block's end here: a dictionary's content stays
        // whole until the block ends a window past it.
        uint blockEndIndex = (uint)(source + size - _base);
        uint maxDistance = 1u << _windowLog;
        if (blockEndIndex > maxDistance + _loadedDictEnd)
        {
            uint newLowLimit = blockEndIndex - maxDistance;
            if (_lowLimit < newLowLimit)
            {
                _lowLimit = newLowLimit;
            }

            if (_dictLimit < _lowLimit)
            {
                _dictLimit = _lowLimit;
            }

            _loadedDictEnd = 0;
        }

        return _lowLimit < _dictLimit
            ? GenerateBlock<ExtDictionary>(source, size, Sequences)
            : GenerateBlock<NoDictionary>(source, size, Sequences);
    }

    /// <summary>
    /// libzstd's <c>ZSTD_ldm_generateSequences_internal</c>, a dictionary's content below the source
    /// (<see cref="ExtDictionary"/>) or not.
    /// </summary>
    private nuint GenerateBlock<TDictionary>(byte* istart, nuint size, RawSequence* sequences)
        where TDictionary : IDictionaryMode
    {
        nuint count = 0;
        uint minMatchLength = (uint)_minMatchLength;
        uint entriesPerBucket = 1u << _bucketSizeLog;
        int bucketSizeLog = _bucketSizeLog;
        uint hashMask = (1u << (_hashLog - _bucketSizeLog)) - 1;
        byte* @base = _base;
        byte* iend = istart + size;
        byte* ilimit = iend - HashReadSize;
        byte* anchor = istart;
        byte* ip = istart;
        nuint* splits = (nuint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_splits));
        Candidate* candidates = (Candidate*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_candidates));
        ulong* table = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_table));
        if (size < minMatchLength)
        {
            return 0;
        }

        // libzstd's ZSTD_ldm_gear_reset over the first minMatchLength bytes updates a copy of the hash
        // and never stores it: it changes nothing, and is not done. The hash starts from ~0u.
        ulong rolling = uint.MaxValue;
        ulong touched = 0;
        ip += minMatchLength;

        while (ip < ilimit)
        {
            uint splitCount = 0;
            nuint hashed = Feed(ref rolling, ip, (nuint)(ilimit - ip), splits, ref splitCount);
            for (uint n = 0; n < splitCount; n++)
            {
                byte* split = ip + splits[n] - minMatchLength;
                ulong xxhash = XxHash64.HashToUInt64(new ReadOnlySpan<byte>(split, (int)minMatchLength));
                uint hash = (uint)xxhash & hashMask;
                candidates[n].Split = split;
                candidates[n].Hash = hash;
                candidates[n].Checksum = (uint)(xxhash >> 32);
                touched += table[(nuint)hash << bucketSizeLog];
            }

            for (uint n = 0; n < splitCount; n++)
            {
                byte* split = candidates[n].Split;
                uint checksum = candidates[n].Checksum;
                uint hash = candidates[n].Hash;
                ulong newEntry = (uint)(split - @base) | ((ulong)checksum << 32);

                // A split inside the last sequence is only recorded.
                if (split < anchor)
                {
                    Insert(hash, newEntry);
                    continue;
                }

                // Few entries have the split's checksum, if any: the bucket is scanned for them 8 at a
                // time, and only theirs are matched, in order.
                ulong* bucket = table + ((nuint)hash << bucketSizeLog);
                ulong* bucketEnd = bucket + entriesPerBucket;
                Vector128<uint> target = Vector128.Create(checksum);
                LongMatch best = default;
                for (ulong* group = bucket; group < bucketEnd; group += GroupSize)
                {
                    if (HasChecksum(group, target))
                    {
                        Consider<TDictionary>(group, checksum, split, anchor, iend, ref best);
                    }
                }

                if (best.Entry == null)
                {
                    Insert(hash, newEntry);
                    continue;
                }

                RawSequence* sequence = sequences + count;
                sequence->LiteralLength = (uint)(split - best.Backward - anchor);
                sequence->MatchLength = (uint)(best.Forward + best.Backward);
                sequence->Offset = (uint)(split - @base) - (uint)*best.Entry;
                count++;

                // Inserted after the search, not to clobber the best entry.
                Insert(hash, newEntry);
                anchor = split + best.Forward;

                // A match past what is hashed is a repeating pattern: the hashing resumes at its end.
                // (libzstd resets the hash there, which, as above, changes nothing.)
                if (anchor > ip + hashed)
                {
                    ip = anchor - hashed;
                    break;
                }
            }

            ip += hashed;
        }

        _touched += touched;
        return count;
    }

    /// <summary>The entries compared at a time: every bucket holds a multiple of them.</summary>
    private const int GroupSize = 8;

    /// <summary>Whether an entry of the group has the checksum <paramref name="target"/> (in every lane).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasChecksum(ulong* group, Vector128<uint> target)
    {
        if (AdvSimd.Arm64.IsSupported)
        {
            // The checksums are the entries' high halves: the odd lanes.
            Vector128<uint> first = AdvSimd.Arm64.UnzipOdd(Vector128.Load((uint*)group), Vector128.Load((uint*)(group + 2)));
            Vector128<uint> second = AdvSimd.Arm64.UnzipOdd(Vector128.Load((uint*)(group + 4)), Vector128.Load((uint*)(group + 6)));
            return (AdvSimd.CompareEqual(first, target) | AdvSimd.CompareEqual(second, target)) != Vector128<uint>.Zero;
        }

        Vector128<ulong> high = Vector128.Create(0xFFFF_FFFF_0000_0000UL);
        Vector128<ulong> checksum = target.AsUInt64() & high;
        Vector128<ulong> equal = Vector128.Equals(Vector128.Load(group) & high, checksum)
            | Vector128.Equals(Vector128.Load(group + 2) & high, checksum)
            | Vector128.Equals(Vector128.Load(group + 4) & high, checksum)
            | Vector128.Equals(Vector128.Load(group + 6) & high, checksum);
        return equal != Vector128<ulong>.Zero;
    }

    /// <summary>
    /// The entries of a group in turn, as libzstd searches its bucket: those with the checksum, within
    /// the window, that match at least the minimum forward; the best match, forward and backward,
    /// replaced only by a strictly longer one. Over an extDict, a match in it runs on into the prefix
    /// (<c>ZSTD_count_2segments</c>), and one that reaches back to the prefix's start runs back on into
    /// the extDict's end (<c>ZSTD_ldm_countBackwardsMatch_2segments</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Consider<TDictionary>(ulong* group, uint checksum, byte* split, byte* anchor, byte* iend, ref LongMatch best)
        where TDictionary : IDictionaryMode
    {
        bool extDict = TDictionary.Mode == ExtDictionary.Value;
        uint dictLimit = _dictLimit;
        uint lowestIndex = extDict ? _lowLimit : dictLimit;
        byte* lowPrefix = _base + dictLimit;
        byte* dictStart = extDict ? _dictBase + _lowLimit : null;
        byte* dictEnd = extDict ? _dictBase + dictLimit : null;
        for (ulong* entry = group; entry < group + GroupSize; entry++)
        {
            uint entryOffset = (uint)*entry;
            if ((uint)(*entry >> 32) != checksum || entryOffset <= lowestIndex)
            {
                continue;
            }

            nuint forward;
            nuint backward;
            if (extDict && entryOffset < dictLimit)
            {
                byte* match = _dictBase + entryOffset;
                forward = Count2Segments(split, match, iend, dictEnd, lowPrefix);
                if (forward < (uint)_minMatchLength)
                {
                    continue;
                }

                backward = CountBackward(split, anchor, match, dictStart);
            }
            else
            {
                byte* match = _base + entryOffset;
                forward = Count(split, match, iend);
                if (forward < (uint)_minMatchLength)
                {
                    continue;
                }

                backward = CountBackward(split, anchor, match, lowPrefix);
                if (extDict && match - backward == lowPrefix && lowPrefix != dictStart)
                {
                    backward += CountBackward(split - backward, anchor, dictEnd, dictStart);
                }
            }

            if (forward + backward > best.Forward + best.Backward)
            {
                best.Entry = entry;
                best.Forward = forward;
                best.Backward = backward;
            }
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_ldm_gear_feed</c>: the split points of the next bytes, where the hash's masked
    /// bits are all zero, up to a batch.
    /// </summary>
    /// <returns>The number of bytes hashed.</returns>
    private nuint Feed(ref ulong rolling, byte* data, nuint size, nuint* splits, ref uint splitCount)
    {
        ulong hash = rolling;
        ulong mask = _stopMask;
        ReadOnlySpan<ulong> gear = GearTable;
        nuint n = 0;
        while (n < size)
        {
            hash = (hash << 1) + gear[data[n]];
            n++;
            if ((hash & mask) == 0)
            {
                splits[splitCount++] = n;
                if (splitCount == BatchSize)
                {
                    break;
                }
            }
        }

        rolling = hash;
        return n;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_ldm_fillHashTable</c>: the split points from <paramref name="ip"/> to
    /// <paramref name="iend"/> into the table, those a whole minimum length past the start.
    /// </summary>
    private void Fill(byte* ip, byte* iend)
    {
        uint minMatchLength = (uint)_minMatchLength;
        uint hashMask = (1u << (_hashLog - _bucketSizeLog)) - 1;
        byte* istart = ip;
        nuint* splits = (nuint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_splits));
        ulong rolling = uint.MaxValue;
        while (ip < iend)
        {
            uint splitCount = 0;
            nuint hashed = Feed(ref rolling, ip, (nuint)(iend - ip), splits, ref splitCount);
            for (uint n = 0; n < splitCount; n++)
            {
                if (ip + splits[n] >= istart + minMatchLength)
                {
                    byte* split = ip + splits[n] - minMatchLength;
                    ulong xxhash = XxHash64.HashToUInt64(new ReadOnlySpan<byte>(split, (int)minMatchLength));
                    Insert((uint)xxhash & hashMask, (uint)(split - _base) | (xxhash >> 32 << 32));
                }
            }

            ip += hashed;
        }
    }

    /// <summary>libzstd's <c>ZSTD_ldm_insertEntry</c>: an entry at its bucket's next place, round robin.</summary>
    private void Insert(uint hash, ulong entry)
    {
        ref byte offset = ref _bucketOffsets[hash];
        _table[((nuint)hash << _bucketSizeLog) + offset] = entry;
        offset = (byte)((offset + 1) & ((1 << _bucketSizeLog) - 1));
    }

    /// <summary>libzstd's <c>ZSTD_ldm_countBackwardsMatch</c>.</summary>
    private static nuint CountBackward(byte* ip, byte* anchor, byte* match, byte* matchBase)
    {
        nuint length = 0;
        while (ip > anchor && match > matchBase && ip[-1] == match[-1])
        {
            ip--;
            match--;
            length++;
        }

        return length;
    }

    /// <summary>libzstd's <c>ZSTD_ldm_gearTab</c>.</summary>
    private static ReadOnlySpan<ulong> GearTable =>
    [
        0xf5b8f72c5f77775cUL, 0x84935f266b7ac412UL, 0xb647ada9ca730cccUL, 0xb065bb4b114fb1deUL,
        0x34584e7e8c3a9fd0UL, 0x4e97e17c6ae26b05UL, 0x3a03d743bc99a604UL, 0xcecd042422c4044fUL,
        0x76de76c58524259eUL, 0x9c8528f65badeacaUL, 0x86563706e2097529UL, 0x2902475fa375d889UL,
        0xafb32a9739a5ebe6UL, 0xce2714da3883e639UL, 0x21eaf821722e69eUL, 0x37b628620b628UL,
        0x49a8d455d88caf5UL, 0x8556d711e6958140UL, 0x4f7ae74fc605c1fUL, 0x829f0c3468bd3a20UL,
        0x4ffdc885c625179eUL, 0x8473de048a3daf1bUL, 0x51008822b05646b2UL, 0x69d75d12b2d1cc5fUL,
        0x8c9d4a19159154bcUL, 0xc3cc10f4abbd4003UL, 0xd06ddc1cecb97391UL, 0xbe48e6e7ed80302eUL,
        0x3481db31cee03547UL, 0xacc3f67cdaa1d210UL, 0x65cb771d8c7f96ccUL, 0x8eb27177055723ddUL,
        0xc789950d44cd94beUL, 0x934feadc3700b12bUL, 0x5e485f11edbdf182UL, 0x1e2e2a46fd64767aUL,
        0x2969ca71d82efa7cUL, 0x9d46e9935ebbba2eUL, 0xe056b67e05e6822bUL, 0x94d73f55739d03a0UL,
        0xcd7010bdb69b5a03UL, 0x455ef9fcd79b82f4UL, 0x869cb54a8749c161UL, 0x38d1a4fa6185d225UL,
        0xb475166f94bbe9bbUL, 0xa4143548720959f1UL, 0x7aed4780ba6b26baUL, 0xd0ce264439e02312UL,
        0x84366d746078d508UL, 0xa8ce973c72ed17beUL, 0x21c323a29a430b01UL, 0x9962d617e3af80eeUL,
        0xab0ce91d9c8cf75bUL, 0x530e8ee6d19a4dbcUL, 0x2ef68c0cf53f5d72UL, 0xc03a681640a85506UL,
        0x496e4e9f9c310967UL, 0x78580472b59b14a0UL, 0x273824c23b388577UL, 0x66bf923ad45cb553UL,
        0x47ae1a5a2492ba86UL, 0x35e304569e229659UL, 0x4765182a46870b6fUL, 0x6cbab625e9099412UL,
        0xddac9a2e598522c1UL, 0x7172086e666624f2UL, 0xdf5003ca503b7837UL, 0x88c0c1db78563d09UL,
        0x58d51865acfc289dUL, 0x177671aec65224f1UL, 0xfb79d8a241e967d7UL, 0x2be1e101cad9a49aUL,
        0x6625682f6e29186bUL, 0x399553457ac06e50UL, 0x35dffb4c23abb74UL, 0x429db2591f54aadeUL,
        0xc52802a8037d1009UL, 0x6acb27381f0b25f3UL, 0xf45e2551ee4f823bUL, 0x8b0ea2d99580c2f7UL,
        0x3bed519cbcb4e1e1UL, 0xff452823dbb010aUL, 0x9d42ed614f3dd267UL, 0x5b9313c06257c57bUL,
        0xa114b8008b5e1442UL, 0xc1fe311c11c13d4bUL, 0x66e8763ea34c5568UL, 0x8b982af1c262f05dUL,
        0xee8876faaa75fbb7UL, 0x8a62a4d0d172bb2aUL, 0xc13d94a3b7449a97UL, 0x6dbbba9dc15d037cUL,
        0xc786101f1d92e0f1UL, 0xd78681a907a0b79bUL, 0xf61aaf2962c9abb9UL, 0x2cfd16fcd3cb7ad9UL,
        0x868c5b6744624d21UL, 0x25e650899c74ddd7UL, 0xba042af4a7c37463UL, 0x4eb1a539465a3ecaUL,
        0xbe09dbf03b05d5caUL, 0x774e5a362b5472baUL, 0x47a1221229d183cdUL, 0x504b0ca18ef5a2dfUL,
        0xdffbdfbde2456eb9UL, 0x46cd2b2fbee34634UL, 0xf2aef8fe819d98c3UL, 0x357f5276d4599d61UL,
        0x24a5483879c453e3UL, 0x88026889192b4b9UL, 0x28da96671782dbecUL, 0x4ef37c40588e9aaaUL,
        0x8837b90651bc9fb3UL, 0xc164f741d3f0e5d6UL, 0xbc135a0a704b70baUL, 0x69cd868f7622adaUL,
        0xbc37ba89e0b9c0abUL, 0x47c14a01323552f6UL, 0x4f00794bacee98bbUL, 0x7107de7d637a69d5UL,
        0x88af793bb6f2255eUL, 0xf3c6466b8799b598UL, 0xc288c616aa7f3b59UL, 0x81ca63cf42fca3fdUL,
        0x88d85ace36a2674bUL, 0xd056bd3792389e7UL, 0xe55c396c4e9dd32dUL, 0xbefb504571e6c0a6UL,
        0x96ab32115e91e8ccUL, 0xbf8acb18de8f38d1UL, 0x66dae58801672606UL, 0x833b6017872317fbUL,
        0xb87c16f2d1c92864UL, 0xdb766a74e58b669cUL, 0x89659f85c61417beUL, 0xc8daad856011ea0cUL,
        0x76a4b565b6fe7eaeUL, 0xa469d085f6237312UL, 0xaaf0365683a3e96cUL, 0x4dbb746f8424f7b8UL,
        0x638755af4e4acc1UL, 0x3d7807f5bde64486UL, 0x17be6d8f5bbb7639UL, 0x903f0cd44dc35dcUL,
        0x67b672eafdf1196cUL, 0xa676ff93ed4c82f1UL, 0x521d1004c5053d9dUL, 0x37ba9ad09ccc9202UL,
        0x84e54d297aacfb51UL, 0xa0b4b776a143445UL, 0x820d471e20b348eUL, 0x1874383cb83d46dcUL,
        0x97edeec7a1efe11cUL, 0xb330e50b1bdc42aaUL, 0x1dd91955ce70e032UL, 0xa514cdb88f2939d5UL,
        0x2791233fd90db9d3UL, 0x7b670a4cc50f7a9bUL, 0x77c07d2a05c6dfa5UL, 0xe3778b6646d0a6faUL,
        0xb39c8eda47b56749UL, 0x933ed448addbef28UL, 0xaf846af6ab7d0bf4UL, 0xe5af208eb666e49UL,
        0x5e6622f73534cd6aUL, 0x297daeca42ef5b6eUL, 0x862daef3d35539a6UL, 0xe68722498f8e1ea9UL,
        0x981c53093dc0d572UL, 0xfa09b0bfbf86fbf5UL, 0x30b1e96166219f15UL, 0x70e7d466bdc4fb83UL,
        0x5a66736e35f2a8e9UL, 0xcddb59d2b7c1baefUL, 0xd6c7d247d26d8996UL, 0xea4e39eac8de1ba3UL,
        0x539c8bb19fa3aff2UL, 0x9f90e4c5fd508d8UL, 0xa34e5956fbaf3385UL, 0x2e2f8e151d3ef375UL,
        0x173691e9b83faec1UL, 0xb85a8d56bf016379UL, 0x8382381267408ae3UL, 0xb90f901bbdc0096dUL,
        0x7c6ad32933bcec65UL, 0x76bb5e2f2c8ad595UL, 0x390f851a6cf46d28UL, 0xc3e6064da1c2da72UL,
        0xc52a0c101cfa5389UL, 0xd78eaf84a3fbc530UL, 0x3781b9e2288b997eUL, 0x73c2f6dea83d05c4UL,
        0x4228e364c5b5ed7UL, 0x9d7a3edf0da43911UL, 0x8edcfeda24686756UL, 0x5e7667a7b7a9b3a1UL,
        0x4c4f389fa143791dUL, 0xb08bc1023da7cddcUL, 0x7ab4be3ae529b1ccUL, 0x754e6132dbe74ff9UL,
        0x71635442a839df45UL, 0x2f6fb1643fbe52deUL, 0x961e0a42cf7a8177UL, 0xf3b45d83d89ef2eaUL,
        0xee3de4cf4a6e3e9bUL, 0xcd6848542c3295e7UL, 0xe4cee1664c78662fUL, 0x9947548b474c68c4UL,
        0x25d73777a5ed8b0bUL, 0xc915b1d636b7fcUL, 0x21c2ba75d9b0d2daUL, 0x5f6b5dcf608a64a1UL,
        0xdcf333255ff9570cUL, 0x633b922418ced4eeUL, 0xc136dde0b004b34aUL, 0x58cc83b05d4b2f5aUL,
        0x5eb424dda28e42d2UL, 0x62df47369739cd98UL, 0xb4e0b42485e4ce17UL, 0x16e1f0c1f9a8d1e7UL,
        0x8ec3916707560ebfUL, 0x62ba6e2df2cc9db3UL, 0xcbf9f4ff77d83a16UL, 0x78d9d7d07d2bbcc4UL,
        0xef554ce1e02c41f4UL, 0x8d7581127eccf94dUL, 0xa9b53336cb3c8a05UL, 0x38c42c0bf45c4f91UL,
        0x640893cdf4488863UL, 0x80ec34bc575ea568UL, 0x39f324f5b48eaa40UL, 0xe9d9ed1f8eff527fUL,
        0x9224fc058cc5a214UL, 0xbaba00b04cfe7741UL, 0x309a9f120fcf52afUL, 0xa558f3ec65626212UL,
        0x424bec8b7adabe2fUL, 0x41622513a6aea433UL, 0xb88da2d5324ca798UL, 0xd287733b245528a4UL,
        0x9a44697e6d68aec3UL, 0x7b1093be2f49bb28UL, 0x50bbec632e3d8aadUL, 0x6cd90723e1ea8283UL,
        0x897b9e7431b02bf3UL, 0x219efdcb338a7047UL, 0x3b0311f0a27c0656UL, 0xdb17bf91c0db96e7UL,
        0x8cd4fd6b4e85a5b2UL, 0xfab071054ba6409dUL, 0x40d6fe831fa9dfd9UL, 0xaf358debad7d791eUL,
        0xeb8d0e25a65e3e58UL, 0xbbcbd3df14e08580UL, 0xcf751f27ecdab2bUL, 0x2b4da14f2613d8f4UL,
    ];
}
