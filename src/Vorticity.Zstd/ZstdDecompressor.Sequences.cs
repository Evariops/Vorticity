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
    /// <param name="section">The sequences section.</param>
    /// <param name="tables">Where the block's tables go.</param>
    /// <param name="previous">The previous block's tables when they are in another set; null when <paramref name="tables"/> holds them.</param>
    /// <param name="headerSize">The size of the header.</param>
    /// <returns>The number of sequences.</returns>
    private int DecodeSequencesHeader(ReadOnlySpan<byte> section, SequenceTableSet tables, SequenceTableSet? previous, out int headerSize)
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
        ip += BuildSequenceTable(tables, previous, SequenceCode.LiteralLength, modes >> 6, section.Slice(ip), norm, SequenceCodes.DefaultLiteralLengths);
        ip += BuildSequenceTable(tables, previous, SequenceCode.Offset, (modes >> 4) & 3, section.Slice(ip), norm, SequenceCodes.DefaultOffsets);
        ip += BuildSequenceTable(tables, previous, SequenceCode.MatchLength, (modes >> 2) & 3, section.Slice(ip), norm, SequenceCodes.DefaultMatchLengths);

        headerSize = ip;
        return nbSeq;
    }

    /// <summary>libzstd's <c>ZSTD_buildSeqTable</c>: selects or builds the table of one code.</summary>
    /// <returns>The bytes the table's description takes.</returns>
    private int BuildSequenceTable(
        SequenceTableSet tables, SequenceTableSet? previous, SequenceCode code, int mode, ReadOnlySpan<byte> source, Span<short> norm, SeqTable predefined)
    {
        switch (mode)
        {
            case 0: // predefined
                tables.Use(predefined);
                return 0;

            case 1: // RLE: one symbol, every sequence
                if (source.IsEmpty || source[0] > SequenceCodes.MaxSymbol(code))
                {
                    Throw.Error(ZstdError.FseTable);
                }

                tables.BuildRle(code, source[0]);
                return 1;

            case 2: // FSE: a table description
            {
                int maxSymbolValue = SequenceCodes.MaxSymbol(code);
                int size = Fse.ReadNCount(norm, ref maxSymbolValue, out int tableLog, source, ZstdError.FseTable);
                if (tableLog > SequenceCodes.MaxLog(code))
                {
                    Throw.Error(ZstdError.FseTable);
                }

                tables.Build(code, norm.Slice(0, maxSymbolValue + 1), tableLog);
                return size;
            }

            default: // repeat: the previous block's table
                if (!_sequenceEntropy)
                {
                    Throw.Error(ZstdError.RepeatWithoutTable);
                }

                // In the same set, the table is the one the previous block left there.
                if (previous is not null)
                {
                    tables.RepeatFrom(previous, code);
                }

                return 0;
        }
    }
}
