using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's <c>ZSTD_window_t</c> and the tables of its <c>ZSTD_MatchState_t</c>, for a frame whose
/// whole source is in memory: an index is a position relative to <see cref="Base"/>.
/// </summary>
/// <remarks>
/// <para>
/// A frame's first byte has the index the previous frame ended at, as libzstd's context continues
/// its indices from one frame to the next: the entries the tables keep from earlier frames then lie
/// below the frame's <see cref="DictLimit"/>, invalid without clearing the tables. They are cleared
/// only when the indices restart, near the top of their range.
/// </para>
/// <para>
/// <see cref="Base"/> points that many bytes before the source, which is pinned: it is never read
/// below <see cref="DictLimit"/>.
/// </para>
/// </remarks>
internal unsafe struct MatchState
{
    public byte* Base;

    /// <summary>The lowest index of the prefix the matches may reach.</summary>
    public uint DictLimit;

    public uint LowLimit;

    public uint NextToUpdate;

    /// <summary>
    /// libzstd's <c>window.dictBase</c>: the base of the indices from <see cref="LowLimit"/> up to
    /// <see cref="DictLimit"/>, a segment apart from the source (a dictionary loaded into the tables,
    /// or copied there): libzstd's extDict.
    /// </summary>
    public byte* DictionaryBase;

    /// <summary>libzstd's <c>window.nextSrc</c>, as an index: where the window's bytes end.</summary>
    public uint End;

    /// <summary>
    /// libzstd's <c>loadedDictEnd</c>: the end of a dictionary's indices, while the frame may still
    /// reach it; 0 without one.
    /// </summary>
    public uint LoadedDictEnd;

    /// <summary>
    /// libzstd's <c>dictMatchState</c>: a prepared dictionary's own state, searched in place beside
    /// the frame's tables (attached), while the frame may reach it; null otherwise.
    /// </summary>
    public MatchState* Dictionary;

    public uint* HashTable;

    /// <summary>The second table: the short hashes of the double-fast strategy, the chains of the lazy ones.</summary>
    public uint* ChainTable;

    /// <summary>The row-based match finder's tags, a byte beside each entry of <see cref="HashTable"/>.</summary>
    public byte* TagTable;

    /// <summary>libzstd's <c>hashTable3</c>: the optimal parser's 3-byte hashes, for a minimum length of 3.</summary>
    public uint* HashTable3;

    /// <summary>libzstd's <c>hashLog3</c>.</summary>
    public int HashLog3;

    /// <summary>libzstd's <c>hashCache</c>: the row hashes of the next positions.</summary>
    public uint* HashCache;

    /// <summary>libzstd's <c>rowHashLog</c>: the hash log less the row log, the bits that pick a row.</summary>
    public int RowHashLog;

    /// <summary>
    /// The row hash's multiplier, for the minimum length: a field the JIT cannot fold into a
    /// constant, which it rebuilt in three instructions at every position of a loop.
    /// </summary>
    public ulong RowHashMultiplier;

    /// <summary>The row hash's shift: 64 less its bits, the row's and the tag's.</summary>
    public int RowHashShift;

    /// <summary>The candidates a row search compares at most: 2^searchLog, capped at the row's entries.</summary>
    public uint RowAttempts;

    /// <summary>Room for a row search's candidates, 64 at most.</summary>
    public uint* Candidates;

    /// <summary>libzstd's <c>lazySkipping</c>: the lazy parser inserts only the positions it searches.</summary>
    public bool LazySkipping;

    /// <summary>
    /// Where the row-based match finder's reads ahead end up, in place of libzstd's prefetches: kept,
    /// so that the JIT keeps the loads.
    /// </summary>
    public uint Touched;

    public CompressionParameters Parameters;

    /// <summary>
    /// libzstd's <c>ZSTD_getLowestPrefixIndex</c>: the lowest index a match from
    /// <paramref name="current"/> may reach in the prefix, within the window; with a dictionary, the
    /// prefix's start, the dictionary being valid as long as it is within the window at all.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint LowestPrefixIndex(uint current)
    {
        uint maxDistance = 1u << Parameters.WindowLog;
        uint withinWindow = current - DictLimit > maxDistance ? current - maxDistance : DictLimit;
        return LoadedDictEnd != 0 ? DictLimit : withinWindow;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_getLowestMatchIndex</c>: as <see cref="LowestPrefixIndex"/>, from
    /// <see cref="LowLimit"/>, an extDict included.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint LowestMatchIndex(uint current)
    {
        uint maxDistance = 1u << Parameters.WindowLog;
        uint withinWindow = current - LowLimit > maxDistance ? current - maxDistance : LowLimit;
        return LoadedDictEnd != 0 ? LowLimit : withinWindow;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_window_enforceMaxDist</c>: the window moves up so that it ends at
    /// <paramref name="blockEnd"/>, which libzstd passes the block's start as; once it moves past a
    /// dictionary's end, the dictionary is dropped.
    /// </summary>
    public void EnforceMaxDistance(byte* blockEnd)
    {
        uint blockEndIndex = (uint)(blockEnd - Base);
        uint maxDistance = 1u << Parameters.WindowLog;
        if (blockEndIndex > maxDistance + LoadedDictEnd)
        {
            uint newLowLimit = blockEndIndex - maxDistance;
            if (LowLimit < newLowLimit)
            {
                LowLimit = newLowLimit;
            }

            if (DictLimit < LowLimit)
            {
                DictLimit = LowLimit;
            }

            LoadedDictEnd = 0;
            Dictionary = null;
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_checkDictValidity</c> at a block's end: the dictionary dropped for the whole
    /// block when the block reaches past the window from its end, or when the window no longer follows
    /// it.
    /// </summary>
    public void CheckDictionaryValidity(byte* blockEnd)
    {
        uint blockEndIndex = (uint)(blockEnd - Base);
        uint maxDistance = 1u << Parameters.WindowLog;
        if (blockEndIndex > LoadedDictEnd + maxDistance || LoadedDictEnd != DictLimit)
        {
            LoadedDictEnd = 0;
            Dictionary = null;
        }
    }
}

/// <summary>
/// A hash of the next bytes of the source: libzstd's <c>ZSTD_hashPtr</c> for one length, from the
/// eight bytes there, read once by the match finder for every use it has of them.
/// </summary>
internal unsafe interface IMatchHash
{
    /// <summary>The prime, shifted so that the hash is the top bits of the eight bytes times it.</summary>
    static abstract ulong Multiplier { get; }

    static abstract nuint Hash(ulong bytes, int hashLog);
}

/// <summary>
/// libzstd's <c>ZSTD_hash4Ptr</c>, <c>(u * prime) &gt;&gt; (32 - h)</c> in 32 bits, as
/// <c>(u * (prime &lt;&lt; 32)) &gt;&gt; (64 - h)</c> in 64: the same bits, without the zero extension
/// the JIT put after a 32-bit hash.
/// </summary>
internal readonly struct Hash4 : IMatchHash
{
    public static ulong Multiplier => 2654435761UL << 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Hash(ulong bytes, int hashLog) => (nuint)((bytes * Multiplier) >> (64 - hashLog));
}

/// <summary>
/// libzstd's <c>ZSTD_hash5Ptr</c>: <c>(u &lt;&lt; 24) * prime</c>, as <c>u * (prime &lt;&lt; 24)</c>, equal
/// modulo 2^64 and a shift shorter (clang finds it; the JIT does not).
/// </summary>
internal readonly struct Hash5 : IMatchHash
{
    public static ulong Multiplier => 889523592379UL << 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Hash(ulong bytes, int hashLog) => (nuint)((bytes * Multiplier) >> (64 - hashLog));
}

/// <summary>libzstd's <c>ZSTD_hash6Ptr</c>, the shift folded into the prime as for <see cref="Hash5"/>.</summary>
internal readonly struct Hash6 : IMatchHash
{
    public static ulong Multiplier => 227718039650203UL << 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Hash(ulong bytes, int hashLog) => (nuint)((bytes * Multiplier) >> (64 - hashLog));
}

/// <summary>libzstd's <c>ZSTD_hash7Ptr</c>, the shift folded into the prime as for <see cref="Hash5"/>.</summary>
internal readonly struct Hash7 : IMatchHash
{
    public static ulong Multiplier => 58295818150454627UL << 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Hash(ulong bytes, int hashLog) => (nuint)((bytes * Multiplier) >> (64 - hashLog));
}

/// <summary>libzstd's <c>ZSTD_hash8Ptr</c>.</summary>
internal readonly struct Hash8 : IMatchHash
{
    public static ulong Multiplier => 0xCF1BBCDCB7A56463UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Hash(ulong bytes, int hashLog) => (nuint)((bytes * Multiplier) >> (64 - hashLog));
}

/// <summary>What the match finders share.</summary>
internal static unsafe class MatchFinder
{
    /// <summary>libzstd's <c>HASH_READ_SIZE</c>: a hash reads eight bytes, which must be in the source.</summary>
    public const int HashReadSize = 8;

    /// <summary>libzstd's <c>kSearchStrength</c>: how soon an unfruitful search speeds up.</summary>
    public const int SearchStrength = 8;

    /// <summary>libzstd's <c>REPCODE1_TO_OFFBASE</c>.</summary>
    public const uint RepeatCode1 = 1;

    /// <summary>
    /// libzstd's <c>ZSTD_count</c> for <paramref name="ip"/> 16 bytes or more before
    /// <paramref name="end"/>: the first 16 bytes compared without a branch, two words whose first
    /// difference each gives a length, the second's added when the first is whole; then
    /// <see cref="Count"/> past them. A search measures several candidates whose lengths vary, and
    /// the loop's exit, mispredicted, cost more than the words.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint MatchLength(byte* ip, byte* match, byte* end)
    {
        nuint first = (nuint)BitOperations.TrailingZeroCount(Read64(ip) ^ Read64(match)) >> 3;
        nuint second = (nuint)BitOperations.TrailingZeroCount(Read64(ip + 8) ^ Read64(match + 8)) >> 3;
        nuint length = first + (second & (0 - (first >> 3)));
        if (length == 16)
        {
            length += Count(ip + 16, match + 16, end);
        }

        return length;
    }

    /// <summary>libzstd's <c>ZSTD_REP_NUM</c>: offset codes up to it are repeat codes.</summary>
    public const uint RepeatCodeCount = 3;

    /// <summary>libzstd's <c>OFFSET_TO_OFFBASE</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint OffsetToOffBase(uint offset) => offset + 3;

    /// <summary>
    /// An index, raised to <paramref name="low"/> when below it: where a candidate outside the window
    /// can be read harmlessly. By the sign of their 64-bit difference, without a branch: the JIT does
    /// not turn a select into <c>csel</c> inside a loop, and whether a table entry is in the window is
    /// unpredictable while those of earlier frames remain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint ClampToWindow(uint index, uint low)
    {
        long below = (long)index - low;
        return (nuint)(index - (below & (below >> 63)));
    }

    /// <summary>
    /// <see cref="ClampToWindow(uint, uint)"/> on indices read from their table into native integers:
    /// the load widens them, where a 32-bit index cost a zero extension at each use.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint ClampToWindow(nuint index, nuint low)
    {
        nint below = (nint)(index - low);
        return index - (nuint)(below & (below >> 63));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(byte* p) => Unsafe.ReadUnaligned<uint>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(byte* p) => Unsafe.ReadUnaligned<ulong>(p);

    /// <summary>
    /// libzstd's <c>ZSTD_count_2segments</c>: a match in a segment of its own (an extDict, a
    /// dictionary), which may run past <paramref name="matchEnd"/> into the prefix, from
    /// <paramref name="prefixStart"/>.
    /// </summary>
    public static nuint Count2Segments(byte* input, byte* match, byte* inputEnd, byte* matchEnd, byte* prefixStart)
    {
        byte* virtualEnd = input + (matchEnd - match);
        if (virtualEnd > inputEnd)
        {
            virtualEnd = inputEnd;
        }

        nuint length = Count(input, match, virtualEnd);
        return match + length != matchEnd ? length : length + Count(input + length, prefixStart, inputEnd);
    }

    /// <summary>
    /// libzstd's <c>ZSTD_index_overlap_check</c>: a repeat offset's index, but for the three just below
    /// the prefix, whose four bytes would straddle the segments.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IndexOverlapCheck(uint prefixLowestIndex, uint repIndex) => (prefixLowestIndex - 1) - repIndex >= 3;

    /// <summary>libzstd's <c>ZSTD_SHORT_CACHE_TAG_BITS</c>: the tag a prepared dictionary's fast tables keep beside each index.</summary>
    public const int TagBits = CompressionParameters.DictionaryTagBits;

    /// <summary>The tag's mask.</summary>
    public const uint TagMask = (1u << TagBits) - 1;

    /// <summary>libzstd's <c>ZSTD_writeTaggedIndex</c>: an index and its hash's low 8 bits, at the hash's other bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTaggedIndex(uint* table, nuint hashAndTag, uint index) =>
        table[hashAndTag >> TagBits] = (index << TagBits) | ((uint)hashAndTag & TagMask);

    /// <summary>
    /// libzstd's <c>ZSTD_count</c>: how many bytes from <paramref name="input"/> equal those from
    /// <paramref name="match"/>, up to <paramref name="inputLimit"/>, which no read passes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nuint Count(byte* input, byte* match, byte* inputLimit)
    {
        byte* start = input;
        byte* loopLimit = inputLimit - (sizeof(ulong) - 1);
        if (input < loopLimit)
        {
            ulong diff = Read64(match) ^ Read64(input);
            if (diff != 0)
            {
                return (nuint)(BitOperations.TrailingZeroCount(diff) >> 3);
            }

            input += sizeof(ulong);
            match += sizeof(ulong);
            while (input < loopLimit)
            {
                diff = Read64(match) ^ Read64(input);
                if (diff == 0)
                {
                    input += sizeof(ulong);
                    match += sizeof(ulong);
                    continue;
                }

                input += BitOperations.TrailingZeroCount(diff) >> 3;
                return (nuint)(input - start);
            }
        }

        if (input < inputLimit - 3 && Read32(match) == Read32(input))
        {
            input += 4;
            match += 4;
        }

        if (input < inputLimit - 1 && Unsafe.ReadUnaligned<ushort>(match) == Unsafe.ReadUnaligned<ushort>(input))
        {
            input += 2;
            match += 2;
        }

        if (input < inputLimit && *match == *input)
        {
            input++;
        }

        return (nuint)(input - start);
    }
}
