using System;
using System.Buffers.Binary;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

public sealed partial class ZstdDecompressor
{
    /// <summary>
    /// libzstd's <c>ZSTD_decodeSeqHeaders</c>: the number of sequences, then the table of each code,
    /// as a mode and, for the FSE mode, a description.
    /// </summary>
    /// <returns>The number of sequences.</returns>
    private int DecodeSequencesHeader(ReadOnlySpan<byte> section, out int headerSize)
    {
        const ZstdError error = ZstdError.SequencesHeader;
        if (section.IsEmpty)
        {
            Throw.Error(error);
        }

        int ip = 1;
        int nbSeq = section[0];
        if (nbSeq > 0x7F)
        {
            if (nbSeq == 0xFF)
            {
                if (ip + 2 > section.Length)
                {
                    Throw.Error(error);
                }

                nbSeq = BinaryPrimitives.ReadUInt16LittleEndian(section.Slice(ip)) + 0x7F00;
                ip += 2;
            }
            else
            {
                if (ip >= section.Length)
                {
                    Throw.Error(error);
                }

                nbSeq = ((nbSeq - 0x80) << 8) + section[ip++];
            }
        }

        if (nbSeq == 0)
        {
            // No sequence: the section ends here.
            if (ip != section.Length)
            {
                Throw.Error(error);
            }

            headerSize = ip;
            return 0;
        }

        if (ip + 1 > section.Length || (section[ip] & 3) != 0)
        {
            Throw.Error(error);
        }

        int modes = section[ip++];
        Span<short> norm = stackalloc short[SequenceCodes.MaxMatchLength + 1];
        ip += BuildSequenceTable(
            modes >> 6, section.Slice(ip), norm, SequenceCodes.MaxLiteralLength, SequenceCodes.LiteralLengthMaxLog,
            SequenceCodes.LiteralLengthBase, SequenceCodes.LiteralLengthBits,
            SequenceCodes.DefaultLiteralLengths, _ownLiteralLengths, ref _literalLengths);
        ip += BuildSequenceTable(
            (modes >> 4) & 3, section.Slice(ip), norm, SequenceCodes.MaxOffset, SequenceCodes.OffsetMaxLog,
            SequenceCodes.OffsetBase, SequenceCodes.OffsetBits,
            SequenceCodes.DefaultOffsets, _ownOffsets, ref _offsets);
        ip += BuildSequenceTable(
            (modes >> 2) & 3, section.Slice(ip), norm, SequenceCodes.MaxMatchLength, SequenceCodes.MatchLengthMaxLog,
            SequenceCodes.MatchLengthBase, SequenceCodes.MatchLengthBits,
            SequenceCodes.DefaultMatchLengths, _ownMatchLengths, ref _matchLengths);

        headerSize = ip;
        return nbSeq;
    }

    /// <summary>libzstd's <c>ZSTD_buildSeqTable</c>: selects or builds the table of one code.</summary>
    /// <returns>The bytes the table's description takes.</returns>
    private int BuildSequenceTable(
        int mode, ReadOnlySpan<byte> source, Span<short> norm, int maxSymbol, int maxLog,
        ReadOnlySpan<uint> baseValue, ReadOnlySpan<byte> bits,
        SeqTable predefined, SeqTable own, ref SeqTable current)
    {
        switch (mode)
        {
            case 0: // predefined
                current = predefined;
                return 0;

            case 1: // RLE: one symbol, every sequence
                if (source.IsEmpty || source[0] > maxSymbol)
                {
                    Throw.Error(ZstdError.FseTable);
                }

                SequenceCodes.BuildRle(own, source[0], baseValue, bits);
                current = own;
                return 1;

            case 2: // FSE: a table description
            {
                int maxSymbolValue = maxSymbol;
                int size = Fse.ReadNCount(norm, ref maxSymbolValue, out int tableLog, source, ZstdError.FseTable);
                if (tableLog > maxLog)
                {
                    Throw.Error(ZstdError.FseTable);
                }

                SequenceCodes.BuildTable(own, norm.Slice(0, maxSymbolValue + 1), tableLog, baseValue, bits);
                current = own;
                return size;
            }

            default: // repeat: the previous block's table
                if (!_sequenceEntropy)
                {
                    Throw.Error(ZstdError.RepeatWithoutTable);
                }

                return 0;
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_decompressSequences_body</c>: decodes each sequence and executes it at once,
    /// then copies the literals left after the last one.
    /// </summary>
    /// <returns>The size of the block's content.</returns>
    private int ExecuteSequences(
        ReadOnlySpan<byte> bitstream, int nbSeq, ReadOnlySpan<byte> literals, Span<byte> destination, int op, ReadOnlySpan<byte> history)
    {
        int start = op;
        int oend = destination.Length;
        int litPtr = 0;

        if (nbSeq > 0)
        {
            _sequenceEntropy = true;
            ulong rep0 = _rep0;
            ulong rep1 = _rep1;
            ulong rep2 = _rep2;

            ReadOnlySpan<SeqSymbol> llTable = _literalLengths.Entries;
            ReadOnlySpan<SeqSymbol> ofTable = _offsets.Entries;
            ReadOnlySpan<SeqSymbol> mlTable = _matchLengths.Entries;

            var bits = new BackwardBitReader(bitstream, ZstdError.SequenceBitstream);
            int llState = (int)bits.ReadBits(_literalLengths.TableLog);
            bits.Reload();
            int ofState = (int)bits.ReadBits(_offsets.TableLog);
            bits.Reload();
            int mlState = (int)bits.ReadBits(_matchLengths.TableLog);
            bits.Reload();

            for (; nbSeq > 0; nbSeq--)
            {
                // ---- decode: libzstd's ZSTD_decodeSequence, reloads included
                SeqSymbol ll = llTable[llState];
                SeqSymbol ml = mlTable[mlState];
                SeqSymbol of = ofTable[ofState];
                int llBits = ll.NbAdditionalBits;
                int mlBits = ml.NbAdditionalBits;
                int ofBits = of.NbAdditionalBits;
                int totalBits = llBits + mlBits + ofBits;
                ulong offset;
                if (ofBits > 1)
                {
                    offset = of.BaseValue + bits.ReadBitsFast(ofBits);
                    rep2 = rep1;
                    rep1 = rep0;
                    rep0 = offset;
                }
                else
                {
                    bool ll0 = ll.BaseValue == 0;
                    if (ofBits == 0)
                    {
                        // Repeat code 1, or 2 when no literal precedes the match.
                        offset = ll0 ? rep1 : rep0;
                        if (ll0)
                        {
                            rep1 = rep0;
                        }

                        rep0 = offset;
                    }
                    else
                    {
                        // Repeat codes 2 and 3, shifted by one when no literal precedes the match;
                        // the last of them is the most recent offset minus one.
                        ulong code = of.BaseValue + (ll0 ? 1u : 0u) + bits.ReadBitsFast(1);
                        ulong temp = code switch
                        {
                            1 => rep1,
                            2 => rep2,
                            _ => rep0 - 1,
                        };

                        // 0 is no offset: make it one no history can satisfy.
                        if (temp == 0)
                        {
                            temp = ulong.MaxValue;
                        }

                        if (code != 1)
                        {
                            rep2 = rep1;
                        }

                        rep1 = rep0;
                        rep0 = temp;
                        offset = temp;
                    }
                }

                uint matchLength = ml.BaseValue;
                if (mlBits > 0)
                {
                    matchLength += bits.ReadBitsFast(mlBits);
                }

                if (totalBits >= 57 - (SequenceCodes.LiteralLengthMaxLog + SequenceCodes.MatchLengthMaxLog + SequenceCodes.OffsetMaxLog))
                {
                    bits.Reload();
                }

                uint litLength = ll.BaseValue;
                if (llBits > 0)
                {
                    litLength += bits.ReadBitsFast(llBits);
                }

                if (nbSeq != 1)
                {
                    llState = ll.NextState + (int)bits.ReadBits(ll.NbBits);
                    mlState = ml.NextState + (int)bits.ReadBits(ml.NbBits);
                    ofState = of.NextState + (int)bits.ReadBits(of.NbBits);
                    bits.Reload();
                }

                // ---- execute: libzstd's ZSTD_execSequenceEnd, checks in its order
                if ((long)litLength + matchLength > oend - op)
                {
                    Throw.Error(ZstdError.DestinationTooSmall);
                }

                if (litLength > (uint)(literals.Length - litPtr))
                {
                    Throw.Error(ZstdError.LiteralsOverrun);
                }

                literals.Slice(litPtr, (int)litLength).CopyTo(destination.Slice(op));
                op += (int)litLength;
                litPtr += (int)litLength;

                int remaining = (int)matchLength;
                if (offset > (ulong)op)
                {
                    // Before the frame: into the dictionary, possibly running on into the frame.
                    if (offset > (ulong)op + (ulong)history.Length)
                    {
                        Throw.Error(ZstdError.OffsetTooLarge);
                    }

                    int match = history.Length - (int)(offset - (ulong)op);
                    int fromHistory = Math.Min(remaining, history.Length - match);
                    history.Slice(match, fromHistory).CopyTo(destination.Slice(op));
                    op += fromHistory;
                    remaining -= fromHistory;
                    for (int i = 0; i < remaining; i++)
                    {
                        destination[op + i] = destination[i];
                    }

                    op += remaining;
                }
                else
                {
                    // Byte by byte: a match may overlap its own output.
                    int match = op - (int)offset;
                    for (int i = 0; i < remaining; i++)
                    {
                        destination[op + i] = destination[match + i];
                    }

                    op += remaining;
                }
            }

            if (!bits.IsEndOfStream)
            {
                Throw.Error(ZstdError.SequenceBitstream);
            }

            _rep0 = (uint)rep0;
            _rep1 = (uint)rep1;
            _rep2 = (uint)rep2;
        }

        // The literals after the last sequence.
        int last = literals.Length - litPtr;
        if (last > oend - op)
        {
            Throw.Error(ZstdError.DestinationTooSmall);
        }

        literals.Slice(litPtr).CopyTo(destination.Slice(op));
        op += last;
        return op - start;
    }
}
