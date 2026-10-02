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
}
