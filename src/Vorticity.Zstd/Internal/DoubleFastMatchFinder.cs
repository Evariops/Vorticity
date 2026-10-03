using static Vorticity.Zstd.Internal.MatchFinder;

namespace Vorticity.Zstd.Internal;

/// <summary>
/// libzstd's <c>ZSTD_compressBlock_doubleFast</c> without a dictionary: a table of eight-byte hashes
/// (<see cref="MatchState.HashTable"/>) for long matches, one of short hashes
/// (<see cref="MatchState.ChainTable"/>), the long one tried first.
/// </summary>
internal static unsafe class DoubleFastMatchFinder
{
    /// <summary>
    /// libzstd's <c>ZSTD_fillDoubleHashTableForCCtx</c> in its fast mode: every third position of a
    /// dictionary's content, into both tables.
    /// </summary>
    public static void FillHashTables(ref MatchState state, byte* end)
    {
        switch (state.Parameters.MinMatch)
        {
            case 5: Fill<Hash5>(ref state, end); break;
            case 6: Fill<Hash6>(ref state, end); break;
            case 7: Fill<Hash7>(ref state, end); break;
            default: Fill<Hash4>(ref state, end); break;
        }

        static void Fill<THash>(ref MatchState state, byte* end)
            where THash : IMatchHash
        {
            uint* hashLarge = state.HashTable;
            int hashLogLarge = state.Parameters.HashLog;
            uint* hashSmall = state.ChainTable;
            int hashLogSmall = state.Parameters.ChainLog;
            byte* @base = state.Base;
            byte* ip = @base + state.NextToUpdate;
            byte* iend = end - HashReadSize;
            const int FillStep = 3;
            for (; ip + FillStep - 1 <= iend; ip += FillStep)
            {
                uint current = (uint)(ip - @base);
                hashSmall[THash.Hash(ip, hashLogSmall)] = current;
                hashLarge[Hash8.Hash(ip, hashLogLarge)] = current;
            }
        }
    }

    /// <summary>The block's sequences into <paramref name="store"/>, by minimum match length.</summary>
    /// <returns>The size of the literals after the last sequence.</returns>
    public static nuint CompressBlock(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size) =>
        state.Parameters.MinMatch switch
        {
            5 => CompressBlock<Hash5>(ref state, store, rep, source, size),
            6 => CompressBlock<Hash6>(ref state, store, rep, source, size),
            7 => CompressBlock<Hash7>(ref state, store, rep, source, size),
            _ => CompressBlock<Hash4>(ref state, store, rep, source, size),
        };

    /// <summary>libzstd's <c>ZSTD_compressBlock_doubleFast_noDict_generic</c>.</summary>
    private static nuint CompressBlock<THash>(ref MatchState state, SequenceStore store, uint* rep, byte* source, nuint size)
        where THash : IMatchHash
    {
        uint* hashLong = state.HashTable;
        int hashLogLong = state.Parameters.HashLog;
        uint* hashSmall = state.ChainTable;
        int hashLogSmall = state.Parameters.ChainLog;
        byte* @base = state.Base;
        byte* istart = source;
        byte* anchor = istart;
        uint endIndex = (uint)(istart - @base + (nint)size);
        uint prefixLowestIndex = state.LowestPrefixIndex(endIndex);
        byte* prefixLowest = @base + prefixLowestIndex;
        byte* iend = istart + size;
        byte* ilimit = iend - HashReadSize;
        uint offset1 = rep[0];
        uint offset2 = rep[1];
        uint offsetSaved1 = 0;
        uint offsetSaved2 = 0;

        nuint matchLength;
        uint offset;
        uint current;

        // How many positions to search before the step grows, and where it grows next.
        const nuint StepIncrement = 1 << SearchStrength;
        byte* nextStep;
        nuint step;

        nuint hashLong0;
        nuint hashLong1;
        uint indexLong0;
        uint indexLong1;
        byte* matchLong0;
        byte* matchShort0;
        byte* matchLong1;

        byte* ip = istart;
        byte* ip1;

        // Bytes that match no candidate stand for the candidates below the window.
        byte* dummy = stackalloc byte[] { 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0, 0xe2, 0xb4 };

        ip += ip - prefixLowest == 0 ? 1 : 0;
        {
            uint start = (uint)(ip - @base);
            uint windowLow = state.LowestPrefixIndex(start);
            uint maxRep = start - windowLow;
            if (offset2 > maxRep)
            {
                offsetSaved2 = offset2;
                offset2 = 0;
            }

            if (offset1 > maxRep)
            {
                offsetSaved1 = offset1;
                offset1 = 0;
            }
        }

        // ---- one iteration per sequence stored
        while (true)
        {
            step = 1;
            nextStep = ip + StepIncrement;
            ip1 = ip + step;
            if (ip1 > ilimit)
            {
                goto Cleanup;
            }

            hashLong0 = Hash8.Hash(ip, hashLogLong);
            indexLong0 = hashLong[hashLong0];
            matchLong0 = @base + indexLong0;

            // ---- one iteration per position searched
            do
            {
                nuint hashShort0 = THash.Hash(ip, hashLogSmall);
                uint indexShort0 = hashSmall[hashShort0];
                current = (uint)(ip - @base);
                matchShort0 = @base + indexShort0;
                hashLong[hashLong0] = hashSmall[hashShort0] = current;

                // The repeat offset at ip + 1.
                if ((offset1 > 0) & (Read32(ip + 1 - offset1) == Read32(ip + 1)))
                {
                    matchLength = Count(ip + 1 + 4, ip + 1 + 4 - offset1, iend) + 4;
                    ip++;
                    store.Store((nuint)(ip - anchor), anchor, iend, RepeatCode1, matchLength);
                    goto MatchStored;
                }

                hashLong1 = Hash8.Hash(ip1, hashLogLong);

                // A long match at ip.
                {
                    byte* matchLong0Safe = indexLong0 >= prefixLowestIndex ? matchLong0 : dummy;
                    if (Read64(matchLong0Safe) == Read64(ip) && matchLong0Safe == matchLong0)
                    {
                        matchLength = Count(ip + 8, matchLong0 + 8, iend) + 8;
                        offset = (uint)(ip - matchLong0);
                        while (((ip > anchor) & (matchLong0 > prefixLowest)) && ip[-1] == matchLong0[-1])
                        {
                            ip--;
                            matchLong0--;
                            matchLength++;
                        }

                        goto MatchFound;
                    }
                }

                indexLong1 = hashLong[hashLong1];
                matchLong1 = @base + indexLong1;

                // A short match at ip.
                {
                    byte* matchShort0Safe = indexShort0 >= prefixLowestIndex ? matchShort0 : dummy;
                    if (Read32(matchShort0Safe) == Read32(ip) && matchShort0Safe == matchShort0)
                    {
                        goto SearchNextLong;
                    }
                }

                if (ip1 >= nextStep)
                {
                    step++;
                    nextStep += StepIncrement;
                }

                ip = ip1;
                ip1 += step;

                hashLong0 = hashLong1;
                indexLong0 = indexLong1;
                matchLong0 = matchLong1;
            }
            while (ip1 <= ilimit);

        Cleanup:
            offsetSaved2 = offsetSaved1 != 0 && offset1 != 0 ? offsetSaved1 : offsetSaved2;
            rep[0] = offset1 != 0 ? offset1 : offsetSaved1;
            rep[1] = offset2 != 0 ? offset2 : offsetSaved2;
            return (nuint)(iend - anchor);

        SearchNextLong:
            // A short match: a long one at ip + 1 may be better.
            matchLength = Count(ip + 4, matchShort0 + 4, iend) + 4;
            offset = (uint)(ip - matchShort0);
            if (indexLong1 > prefixLowestIndex && Read64(matchLong1) == Read64(ip1))
            {
                nuint length1 = Count(ip1 + 8, matchLong1 + 8, iend) + 8;
                if (length1 > matchLength)
                {
                    ip = ip1;
                    matchLength = length1;
                    offset = (uint)(ip - matchLong1);
                    matchShort0 = matchLong1;
                }
            }

            while (((ip > anchor) & (matchShort0 > prefixLowest)) && ip[-1] == matchShort0[-1])
            {
                ip--;
                matchShort0--;
                matchLength++;
            }

        MatchFound:
            offset2 = offset1;
            offset1 = offset;
            if (step < 4)
            {
                // ip1 is before where the search resumes: a match is four bytes at least.
                hashLong[hashLong1] = (uint)(ip1 - @base);
            }

            store.Store((nuint)(ip - anchor), anchor, iend, OffsetToOffBase(offset), matchLength);

        MatchStored:
            ip += matchLength;
            anchor = ip;

            if (ip <= ilimit)
            {
                // Complementary insertions, then the second repeat offset tried at once.
                uint indexToInsert = current + 2;
                hashLong[Hash8.Hash(@base + indexToInsert, hashLogLong)] = indexToInsert;
                hashLong[Hash8.Hash(ip - 2, hashLogLong)] = (uint)(ip - 2 - @base);
                hashSmall[THash.Hash(@base + indexToInsert, hashLogSmall)] = indexToInsert;
                hashSmall[THash.Hash(ip - 1, hashLogSmall)] = (uint)(ip - 1 - @base);

                while (ip <= ilimit && ((offset2 > 0) & (Read32(ip) == Read32(ip - offset2))))
                {
                    nuint repLength = Count(ip + 4, ip + 4 - offset2, iend) + 4;
                    (offset1, offset2) = (offset2, offset1);
                    hashSmall[THash.Hash(ip, hashLogSmall)] = (uint)(ip - @base);
                    hashLong[Hash8.Hash(ip, hashLogLong)] = (uint)(ip - @base);
                    store.Store(0, anchor, iend, RepeatCode1, repLength);
                    ip += repLength;
                    anchor = ip;
                }
            }
        }
    }
}
