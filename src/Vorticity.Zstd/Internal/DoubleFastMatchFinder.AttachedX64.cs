using System.Numerics;
using System.Runtime.CompilerServices;
using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>The double-fast search with an attached dictionary, as x64 wants it.</summary>
internal static unsafe partial class DoubleFastMatchFinder
{
    /// <summary>
    /// What the x64 search with an attached dictionary keeps out of registers, in a local whose address
    /// is taken: read as the operands of its instructions, or once a match. The first fields are copies
    /// of the search's own values, which it takes back after each match (see <see cref="CompressBlockAttachedX64"/>).
    /// </summary>
    private struct AttachedCold
    {
        public byte* Base;
        public uint* HashLong;
        public uint* HashSmall;
        public uint* DictHashLong;
        public uint* DictHashSmall;
        public int Shifts;

        public byte* Anchor;
        public uint Offset1;
        public uint Offset2;
        public uint PrefixLowestIndex;

        /// <summary>The prefix's lowest index less one: <see cref="MatchFinder.IndexOverlapCheck"/> as one subtraction.</summary>
        public uint OverlapLimit;
        public uint DictStartIndex;
        public uint DictIndexDelta;
        public uint Current;
        public byte* Ilimit;
        public byte* End;
        public byte* PrefixLowest;

        /// <summary>The dictionary's base less the delta of its indices: a frame index's address there.</summary>
        public byte* DictShifted;
        public byte* DictBase;
        public byte* DictStart;
        public byte* DictEnd;
        public byte* Lit;
        public SequenceRecord* Sequence;
        public uint* Counts;
    }

    /// <summary>As <see cref="Expose(Cold*)"/>, for <see cref="AttachedCold"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Expose(AttachedCold* cold)
    {
    }

    /// <summary>
    /// <see cref="CompressBlockAttached{THash}"/> on x64: the same search, the same sequences, with the
    /// values its loop holds cut down to what x64 has registers for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// libzstd's form, which Arm64 keeps, holds some twenty values across the loop, among them the
    /// four tables, their four hash shifts, both segments' bounds, the repeat offsets and the cursors
    /// of the store, live across the calls of the matches that cross the segments: x64, with 15
    /// registers and 8 kept across a call, spilled them all, the hashes and the entries read
    /// included, a 440-byte frame whose stores and reloads made each position's probes twice as
    /// long, the misses of the dictionary's tables less overlapped.
    /// </para>
    /// <para>
    /// Here the search holds the cursor, the base and the four tables, and the four shifts in one
    /// register, rotated to each. The rest is in <see cref="AttachedCold"/>, the anchor, the repeat
    /// offset and the bounds read as operands. After a match the code reads only
    /// <see cref="AttachedCold"/>, and the search's values are taken back from it before the next
    /// position: none is live across a call. A dictionary entry is read with its tag's difference
    /// from the hash's in its low byte: one value for the tag test and the index.
    /// </para>
    /// </remarks>
    private static nuint CompressBlockAttachedX64<THash>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where THash : IMatchHash
    {
        AttachedCold cold = default;
        Expose(&cold);

        CompressionParameters parameters = state.Parameters;
        MatchState* dictionary = state.Dictionary;
        byte* @base = state.Base;
        uint* hashLong = state.HashTable;
        uint* hashSmall = state.ChainTable;
        uint* dictHashLong = dictionary->HashTable;
        uint* dictHashSmall = dictionary->ChainTable;
        uint endIndex = (uint)(source - @base) + (uint)size;
        uint prefixLowestIndex = state.LowestPrefixIndex(endIndex);
        byte* dictBase = dictionary->Base;

        // The shifts of the frame's long hash, the dictionary's (tagged), the frame's short hash, the
        // dictionary's, a byte each: a shift takes the low 6 bits of its count.
        int shifts = (64 - parameters.HashLog)
            | ((64 - (dictionary->Parameters.HashLog + TagBits)) << 8)
            | ((64 - parameters.ChainLog) << 16)
            | ((64 - (dictionary->Parameters.ChainLog + TagBits)) << 24);

        cold.Base = @base;
        cold.HashLong = hashLong;
        cold.HashSmall = hashSmall;
        cold.DictHashLong = dictHashLong;
        cold.DictHashSmall = dictHashSmall;
        cold.Shifts = shifts;
        cold.Offset1 = rep[0];
        cold.Offset2 = rep[1];
        cold.PrefixLowestIndex = prefixLowestIndex;
        cold.OverlapLimit = prefixLowestIndex - 1;
        cold.DictStartIndex = dictionary->DictLimit;
        cold.DictIndexDelta = prefixLowestIndex - dictionary->End;
        cold.End = source + size;
        cold.Ilimit = cold.End - HashReadSize;
        cold.PrefixLowest = @base + prefixLowestIndex;
        cold.DictShifted = dictBase - (nuint)cold.DictIndexDelta;
        cold.DictBase = dictBase;
        cold.DictStart = dictBase + cold.DictStartIndex;
        cold.DictEnd = dictBase + dictionary->End;
        cold.Lit = store.Literals;
        cold.Sequence = store.Sequences;
        cold.Counts = store.Counts;

        byte* ip = source;
        cold.Anchor = source;
        uint dictAndPrefixLength = (uint)((ip - cold.PrefixLowest) + (cold.DictEnd - cold.DictStart));
        ip += dictAndPrefixLength == 0 ? 1 : 0;

        while (ip < cold.Ilimit)
        {
            nuint matchLength;
            uint offset;
            uint offBase;
            byte* match;
            byte* low;

            // The dictionary's entries first, the likeliest misses.
            ulong bytes = Read64(ip);
            ulong productLong = bytes * Hash8.Multiplier;
            nuint dictHashAndTagLong = (nuint)(productLong >> (int)BitOperations.RotateRight((uint)shifts, 8));
            uint dictEntryLong = dictHashLong[dictHashAndTagLong >> TagBits] ^ ((uint)dictHashAndTagLong & TagMask);
            nuint hashLongIndex = (nuint)(productLong >> shifts);
            ulong productSmall = bytes * THash.Multiplier;
            nuint dictHashAndTagSmall = (nuint)(productSmall >> (int)BitOperations.RotateRight((uint)shifts, 24));
            uint dictEntrySmall = dictHashSmall[dictHashAndTagSmall >> TagBits] ^ ((uint)dictHashAndTagSmall & TagMask);
            nuint hashSmallIndex = (nuint)(productSmall >> (int)BitOperations.RotateRight((uint)shifts, 16));
            uint current = (uint)(ip - @base);
            uint indexLong = hashLong[hashLongIndex];
            uint indexSmall = hashSmall[hashSmallIndex];
            hashLong[hashLongIndex] = hashSmall[hashSmallIndex] = current;

            // The first repeat offset at ip + 1.
            uint repIndex = current + 1 - cold.Offset1;
            if (cold.OverlapLimit - repIndex >= 3)
            {
                byte* repMatch = repIndex < cold.PrefixLowestIndex ? cold.DictShifted + repIndex : @base + repIndex;
                if (Read32(repMatch) == Read32(ip + 1))
                {
                    cold.Current = current;
                    byte* repMatchEnd = repIndex < cold.PrefixLowestIndex ? cold.DictEnd : cold.End;
                    matchLength = CountAcross(ip + 1 + 4, repMatch + 4, cold.End, repMatchEnd, cold.PrefixLowest) + 4;
                    ip++;
                    offBase = RepeatCode1;
                    goto Store;
                }
            }

            if (indexLong >= cold.PrefixLowestIndex && Read64(@base + indexLong) == bytes)
            {
                // A long match in the prefix.
                cold.Current = current;
                match = @base + indexLong;
                matchLength = Count(ip + 8, match + 8, cold.End) + 8;
                offset = (uint)(ip - match);
                low = cold.PrefixLowest;
                goto ExtendBackward;
            }

            if ((dictEntryLong & TagMask) == 0)
            {
                // A long match in the dictionary.
                uint dictIndexLong = dictEntryLong >> TagBits;
                match = cold.DictBase + dictIndexLong;
                if (dictIndexLong > cold.DictStartIndex && Read64(match) == bytes)
                {
                    cold.Current = current;
                    offset = current - dictIndexLong - cold.DictIndexDelta;
                    matchLength = CountAcross(ip + 8, match + 8, cold.End, cold.DictEnd, cold.PrefixLowest) + 8;
                    low = cold.DictStart;
                    goto ExtendBackward;
                }
            }

            if (indexSmall > cold.PrefixLowestIndex)
            {
                // A short match in the prefix.
                match = @base + indexSmall;
                if (Read32(match) == (uint)bytes)
                {
                    goto SearchNextLong;
                }
            }
            else if ((dictEntrySmall & TagMask) == 0)
            {
                // A short match in the dictionary.
                uint dictIndexSmall = dictEntrySmall >> TagBits;
                match = cold.DictBase + dictIndexSmall;
                indexSmall = dictIndexSmall + cold.DictIndexDelta;
                if (dictIndexSmall > cold.DictStartIndex && Read32(match) == (uint)bytes)
                {
                    goto SearchNextLong;
                }
            }

            ip += ((nuint)(ip - cold.Anchor) >> SearchStrength) + 1;
            continue;

        SearchNextLong:
            cold.Current = current;
            {
                ulong nextBytes = Read64(ip + 1);
                ulong nextProduct = nextBytes * Hash8.Multiplier;
                nuint dictHashAndTagLong3 = (nuint)(nextProduct >> (int)BitOperations.RotateRight((uint)shifts, 8));
                uint dictEntryLong3 = dictHashLong[dictHashAndTagLong3 >> TagBits] ^ ((uint)dictHashAndTagLong3 & TagMask);
                nuint hashLongIndex3 = (nuint)(nextProduct >> shifts);
                uint indexLong3 = hashLong[hashLongIndex3];
                hashLong[hashLongIndex3] = current + 1;

                if (indexLong3 >= cold.PrefixLowestIndex && Read64(@base + indexLong3) == nextBytes)
                {
                    // A long match at ip + 1 in the prefix.
                    match = @base + indexLong3;
                    matchLength = Count(ip + 9, match + 8, cold.End) + 8;
                    ip++;
                    offset = (uint)(ip - match);
                    low = cold.PrefixLowest;
                    goto ExtendBackward;
                }

                if ((dictEntryLong3 & TagMask) == 0)
                {
                    // A long match at ip + 1 in the dictionary.
                    uint dictIndexLong3 = dictEntryLong3 >> TagBits;
                    byte* dictMatchLong3 = cold.DictBase + dictIndexLong3;
                    if (dictIndexLong3 > cold.DictStartIndex && Read64(dictMatchLong3) == nextBytes)
                    {
                        offset = current + 1 - dictIndexLong3 - cold.DictIndexDelta;
                        matchLength = CountAcross(ip + 1 + 8, dictMatchLong3 + 8, cold.End, cold.DictEnd, cold.PrefixLowest) + 8;
                        ip++;
                        match = dictMatchLong3;
                        low = cold.DictStart;
                        goto ExtendBackward;
                    }
                }
            }

            // No long match at ip + 1: the short match found.
            if (indexSmall < cold.PrefixLowestIndex)
            {
                offset = current - indexSmall;
                matchLength = CountAcross(ip + 4, match + 4, cold.End, cold.DictEnd, cold.PrefixLowest) + 4;
                low = cold.DictStart;
            }
            else
            {
                matchLength = Count(ip + 4, match + 4, cold.End) + 4;
                offset = (uint)(ip - match);
                low = cold.PrefixLowest;
            }

        ExtendBackward:
            {
                byte* anchor = cold.Anchor;
                while (ip > anchor && match > low && ip[-1] == match[-1])
                {
                    ip--;
                    match--;
                    matchLength++;
                }
            }

            cold.Offset2 = cold.Offset1;
            cold.Offset1 = offset;
            offBase = OffsetToOffBase(offset);

        Store:
            {
                byte* anchor = cold.Anchor;
                byte* lit = cold.Lit;
                SequenceRecord* sequence = cold.Sequence;
                SequenceStore.Store(ref lit, ref sequence, cold.Counts, (nuint)(ip - anchor), anchor, cold.End, offBase, matchLength);
                cold.Lit = lit;
                cold.Sequence = sequence;
            }

            ip += matchLength;
            cold.Anchor = ip;
            if (ip <= cold.Ilimit)
            {
                // Complementary insertion, after the limit test: the positions could pass the end - 8.
                byte* b = cold.Base;
                uint* tableLong = cold.HashLong;
                uint* tableSmall = cold.HashSmall;
                int shiftLong = cold.Shifts;
                int shiftSmall = (int)BitOperations.RotateRight((uint)shiftLong, 16);
                uint indexToInsert = cold.Current + 2;
                ulong insertBytes = Read64(b + indexToInsert);
                tableLong[(nuint)((insertBytes * Hash8.Multiplier) >> shiftLong)] = indexToInsert;
                tableLong[(nuint)((Read64(ip - 2) * Hash8.Multiplier) >> shiftLong)] = (uint)(ip - 2 - b);
                tableSmall[(nuint)((insertBytes * THash.Multiplier) >> shiftSmall)] = indexToInsert;
                tableSmall[(nuint)((Read64(ip - 1) * THash.Multiplier) >> shiftSmall)] = (uint)(ip - 1 - b);

                // The second repeat offset, at once.
                while (ip <= cold.Ilimit)
                {
                    uint current2 = (uint)(ip - cold.Base);
                    uint offset2 = cold.Offset2;
                    uint repIndex2 = current2 - offset2;
                    bool inDictionary = repIndex2 < cold.PrefixLowestIndex;
                    byte* repMatch2 = (inDictionary ? cold.DictShifted : cold.Base) + repIndex2;
                    if (cold.OverlapLimit - repIndex2 < 3 || Read32(repMatch2) != Read32(ip))
                    {
                        break;
                    }

                    nuint repLength2 = CountAcross(ip + 4, repMatch2 + 4, cold.End, inDictionary ? cold.DictEnd : cold.End, cold.PrefixLowest) + 4;
                    cold.Offset2 = cold.Offset1;
                    cold.Offset1 = offset2;
                    SequenceRecord* sequence = cold.Sequence;
                    SequenceStore.StoreOnly(ref sequence, cold.Counts, 0, RepeatCode1, repLength2);
                    cold.Sequence = sequence;
                    ulong repBytes = Read64(ip);
                    cold.HashSmall[(nuint)((repBytes * THash.Multiplier) >> (int)BitOperations.RotateRight((uint)cold.Shifts, 16))] = current2;
                    cold.HashLong[(nuint)((repBytes * Hash8.Multiplier) >> cold.Shifts)] = current2;
                    ip += repLength2;
                    cold.Anchor = ip;
                }
            }

            // The search's values, taken back: none was live across the calls.
            @base = cold.Base;
            hashLong = cold.HashLong;
            hashSmall = cold.HashSmall;
            dictHashLong = cold.DictHashLong;
            dictHashSmall = cold.DictHashSmall;
            shifts = cold.Shifts;
        }

        store.Literals = cold.Lit;
        store.Sequences = cold.Sequence;
        rep[0] = cold.Offset1;
        rep[1] = cold.Offset2;
        return (nuint)(cold.End - cold.Anchor);
    }
}
