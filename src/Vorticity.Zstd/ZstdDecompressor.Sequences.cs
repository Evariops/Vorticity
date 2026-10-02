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
        ip += BuildSequenceTable(SequenceCode.LiteralLength, modes >> 6, section.Slice(ip), norm, SequenceCodes.DefaultLiteralLengths);
        ip += BuildSequenceTable(SequenceCode.Offset, (modes >> 4) & 3, section.Slice(ip), norm, SequenceCodes.DefaultOffsets);
        ip += BuildSequenceTable(SequenceCode.MatchLength, (modes >> 2) & 3, section.Slice(ip), norm, SequenceCodes.DefaultMatchLengths);

        headerSize = ip;
        return nbSeq;
    }

    /// <summary>libzstd's <c>ZSTD_buildSeqTable</c>: selects or builds the table of one code.</summary>
    /// <returns>The bytes the table's description takes.</returns>
    private int BuildSequenceTable(SequenceCode code, int mode, ReadOnlySpan<byte> source, Span<short> norm, SeqTable predefined)
    {
        switch (mode)
        {
            case 0: // predefined
                _sequenceTables.Use(predefined);
                return 0;

            case 1: // RLE: one symbol, every sequence
                if (source.IsEmpty || source[0] > SequenceCodes.MaxSymbol(code))
                {
                    Throw.Error(ZstdError.FseTable);
                }

                _sequenceTables.BuildRle(code, source[0]);
                return 1;

            case 2: // FSE: a table description
            {
                int maxSymbolValue = SequenceCodes.MaxSymbol(code);
                int size = Fse.ReadNCount(norm, ref maxSymbolValue, out int tableLog, source, ZstdError.FseTable);
                if (tableLog > SequenceCodes.MaxLog(code))
                {
                    Throw.Error(ZstdError.FseTable);
                }

                _sequenceTables.Build(code, norm.Slice(0, maxSymbolValue + 1), tableLog);
                return size;
            }

            default: // repeat: the previous block's table
                if (!_sequenceEntropy)
                {
                    Throw.Error(ZstdError.RepeatWithoutTable);
                }

                _sequenceTables.Repeat(code);
                return 0;
        }
    }
}
