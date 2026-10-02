using System;
using System.Buffers.Binary;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

public sealed partial class ZstdDecompressor
{
    /// <summary>Room past the largest literal section, for copies that run over their end.</summary>
    private const int LiteralsMargin = 32;

    // ---- the state a frame carries from one block to the next
    private readonly byte[] _literals;
    private readonly HuffmanTable _huffman;
    private readonly SeqTable _ownLiteralLengths;
    private readonly SeqTable _ownOffsets;
    private readonly SeqTable _ownMatchLengths;
    private HuffmanTable _currentHuffman;
    private SeqTable _literalLengths;
    private SeqTable _offsets;
    private SeqTable _matchLengths;
    private bool _literalEntropy;
    private bool _sequenceEntropy;
    private uint _rep0;
    private uint _rep1;
    private uint _rep2;

    /// <summary>
    /// libzstd's <c>ZSTD_decompressBlock_internal</c>: a literals section, then a sequences section
    /// that interleaves those literals with matches into <paramref name="destination"/> from
    /// <paramref name="op"/>.
    /// </summary>
    /// <param name="block">The block's content, its header excluded.</param>
    /// <param name="destination">The whole output of the frame: the matches reach back into it.</param>
    /// <param name="op">Where this block's output starts.</param>
    /// <param name="blockSizeMax">The frame's largest block.</param>
    /// <param name="history">What precedes the frame: the dictionary's content, if any.</param>
    /// <returns>The size of the block's content.</returns>
    private int DecodeCompressedBlock(ReadOnlySpan<byte> block, Span<byte> destination, int op, int blockSizeMax, ReadOnlySpan<byte> history)
    {
        int capacity = destination.Length - op;
        int literalsSize = DecodeLiterals(block, blockSizeMax, Math.Min(blockSizeMax, capacity), out ReadOnlySpan<byte> literals);
        ReadOnlySpan<byte> sequences = block.Slice(literalsSize);

        int nbSeq = DecodeSequencesHeader(sequences, out int headerSize);
        if (nbSeq > 0 && capacity == 0)
        {
            Throw.Error(ZstdError.DestinationTooSmall);
        }

        return ExecuteSequences(sequences.Slice(headerSize), nbSeq, literals, destination, op, blockSizeMax, history);
    }

    /// <summary>libzstd's <c>ZSTD_decodeLiteralsBlock</c>.</summary>
    /// <param name="block">The block, from its literals section on.</param>
    /// <param name="blockSizeMax">The frame's largest block, which bounds the literals.</param>
    /// <param name="expectedWriteSize">The most the block may write: the literals must fit there too.</param>
    /// <param name="literals">The literals, in the block itself when stored raw.</param>
    /// <returns>The size of the literals section.</returns>
    private int DecodeLiterals(ReadOnlySpan<byte> block, int blockSizeMax, int expectedWriteSize, out ReadOnlySpan<byte> literals)
    {
        if (block.Length < 2)
        {
            Throw.Error(ZstdError.LiteralsHeader);
        }

        int literalsType = block[0] & 3;
        int sizeFormat = (block[0] >> 2) & 3;
        switch (literalsType)
        {
            case 0: // raw
            case 1: // RLE
            {
                int headerSize;
                int size;
                switch (sizeFormat)
                {
                    case 1:
                        headerSize = 2;
                        if (literalsType == 1 && block.Length < 3)
                        {
                            Throw.Error(ZstdError.LiteralsHeader);
                        }

                        size = BinaryPrimitives.ReadUInt16LittleEndian(block) >> 4;
                        break;
                    case 3:
                        headerSize = 3;
                        if (block.Length < (literalsType == 1 ? 4 : 3))
                        {
                            Throw.Error(ZstdError.LiteralsHeader);
                        }

                        size = (block[0] | (block[1] << 8) | (block[2] << 16)) >> 4;
                        break;
                    default:
                        headerSize = 1;
                        size = block[0] >> 3;
                        break;
                }

                if (size > blockSizeMax)
                {
                    Throw.Error(ZstdError.LiteralsSizeTooLarge);
                }

                if (expectedWriteSize < size)
                {
                    Throw.Error(ZstdError.DestinationTooSmall);
                }

                if (literalsType == 0)
                {
                    if (headerSize + size > block.Length)
                    {
                        Throw.Error(ZstdError.LiteralsHeader);
                    }

                    literals = block.Slice(headerSize, size);
                    return headerSize + size;
                }

                Span<byte> buffer = _literals.AsSpan(0, size);
                buffer.Fill(block[headerSize]);
                literals = buffer;
                return headerSize + 1;
            }

            default: // 2: Huffman-compressed, 3: treeless (the previous block's tree)
            {
                if (literalsType == 3 && !_literalEntropy)
                {
                    Throw.Error(ZstdError.TreelessWithoutTable);
                }

                if (block.Length < 5)
                {
                    Throw.Error(ZstdError.LiteralsHeader);
                }

                uint lhc = BinaryPrimitives.ReadUInt32LittleEndian(block);
                bool singleStream = false;
                int headerSize;
                int size;
                int compressedSize;
                switch (sizeFormat)
                {
                    case 2:
                        headerSize = 4;
                        size = (int)((lhc >> 4) & 0x3FFF);
                        compressedSize = (int)(lhc >> 18);
                        break;
                    case 3:
                        headerSize = 5;
                        size = (int)((lhc >> 4) & 0x3FFFF);
                        compressedSize = (int)(lhc >> 22) + (block[4] << 10);
                        break;
                    default:
                        singleStream = sizeFormat == 0;
                        headerSize = 3;
                        size = (int)((lhc >> 4) & 0x3FF);
                        compressedSize = (int)((lhc >> 14) & 0x3FF);
                        break;
                }

                if (size > blockSizeMax)
                {
                    Throw.Error(ZstdError.LiteralsSizeTooLarge);
                }

                if (!singleStream && size < 6)
                {
                    Throw.Error(ZstdError.LiteralsHeader);
                }

                if (compressedSize + headerSize > block.Length)
                {
                    Throw.Error(ZstdError.LiteralsHeader);
                }

                if (expectedWriteSize < size)
                {
                    Throw.Error(ZstdError.DestinationTooSmall);
                }

                ReadOnlySpan<byte> compressed = block.Slice(headerSize, compressedSize);
                Span<byte> output = _literals.AsSpan(0, size);
                HuffmanTable table;
                if (literalsType == 3)
                {
                    table = _currentHuffman;
                }
                else
                {
                    if (!singleStream && compressed.IsEmpty)
                    {
                        Throw.Error(ZstdError.HuffmanTable);
                    }

                    table = _huffman;
                    int treeSize = table.Read(compressed);
                    if (treeSize >= compressed.Length)
                    {
                        Throw.Error(ZstdError.HuffmanTable);
                    }

                    compressed = compressed.Slice(treeSize);
                }

                if (singleStream)
                {
                    table.DecodeSingleStream(compressed, output);
                }
                else
                {
                    table.DecodeFourStreams(compressed, output);
                }

                _literalEntropy = true;
                _currentHuffman = table;
                literals = output;
                return headerSize + compressedSize;
            }
        }
    }
}
