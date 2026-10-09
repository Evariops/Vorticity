using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

public sealed unsafe partial class ZstdCompressor
{
    /// <summary>libzstd's <c>MIN_SEQUENCES_BLOCK_SPLITTING</c>: a part of fewer sequences is not split.</summary>
    private const nuint MinSequencesBlockSplitting = 300;

    /// <summary>libzstd's <c>ZSTD_MAX_NB_BLOCK_SPLITS</c>.</summary>
    private const int MaxBlockSplits = 196;

    /// <summary>libzstd's <c>LONGNBSEQ</c>: from this many sequences, their count takes three bytes.</summary>
    private const nuint LongSequenceCount = 0x7F00;

    /// <summary>libzstd's <c>ZSTD_MAX_HUF_HEADER_SIZE</c>: the room for a tree's description in an estimate.</summary>
    private const nuint MaxHuffmanHeaderSize = 128;

    // The counts of a part and of its two halves, then a literal histogram.
    private readonly uint[] _splitCounts = GC.AllocateArray<uint>((3 * SequenceStore.AllCodes) + 256, pinned: true);
    private readonly uint[] _partitions = GC.AllocateArray<uint>(MaxBlockSplits + 1, pinned: true);
    private readonly byte[] _splitScratch = GC.AllocateArray<byte>(1024, pinned: true);
    private uint[] _literalPrefix = [];
    private uint[] _sourcePrefix = [];
    private int _splitCount;

    /// <summary>
    /// libzstd's <c>ZSTD_resolveBlockSplitterMode</c>: the post-block splitter applies to btopt and the
    /// stronger strategies, from a window of 128 KiB.
    /// </summary>
    private bool PostSplitterEnabled => _frameParameters.Strategy >= Strategy.BinaryTreeOptimal && _frameParameters.WindowLog >= 17;

    private uint* SplitCounts(int index) => (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_splitCounts)) + (index * SequenceStore.AllCodes);

    /// <summary>
    /// libzstd's <c>ZSTD_compressBlock_splitBlock</c>: the block's sequences found, cut where the
    /// estimates say that two blocks are smaller than one, each part emitted as a block.
    /// </summary>
    /// <returns>The size of the blocks written, or -1 when they do not fit.</returns>
    private int CompressSplitBlock(byte* source, int blockSize, int lastBlock, byte* destination, int capacity)
    {
        if (blockSize < MinCompressedBlockSize + FrameFormat.BlockHeaderSize + 1 + 1)
        {
            // ZSTD_buildSeqStore's ZSTDbss_noCompress: too small to try.
            SkipJobSequences(blockSize);
            if (_previous.OffsetRepeat == FseRepeat.Valid)
            {
                _previous.OffsetRepeat = FseRepeat.Check;
            }

            return WriteRawBlock(source, blockSize, lastBlock, destination, capacity);
        }

        FindSequences(source, blockSize);
        nuint sequenceCount = _store.SequenceCount;
        if (Recorder is not null)
        {
            Record(source, blockSize, 2);
        }

        // The literals, and the source bytes, before each sequence: libzstd counts them again for
        // each part it derives.
        if (_literalPrefix.Length < SequenceStore.MaxSequences + 1)
        {
            _literalPrefix = GC.AllocateArray<uint>(SequenceStore.MaxSequences + 1, pinned: true);
            _sourcePrefix = GC.AllocateArray<uint>(SequenceStore.MaxSequences + 1, pinned: true);
        }

        uint literalTotal = 0;
        uint sourceTotalBytes = 0;
        for (nuint n = 0; n < sequenceCount; n++)
        {
            _literalPrefix[n] = literalTotal;
            _sourcePrefix[n] = sourceTotalBytes;
            SequenceRecord* record = _store.SequencesStart + n;
            uint literalLength = SequenceStore.LiteralLengthOf(record);
            literalTotal += literalLength;
            sourceTotalBytes += literalLength + SequenceStore.MatchLengthOf(record);
        }

        _literalPrefix[sequenceCount] = literalTotal;
        _sourcePrefix[sequenceCount] = sourceTotalBytes;

        // The offset histories of the compression (cRep) and of the decompression (dRep): a part
        // emitted raw or RLE leaves the decoder's behind, and a repeat code that then means another
        // offset to it is replaced by the offset it meant.
        uint* decoderRep = stackalloc uint[3];
        uint* encoderRep = stackalloc uint[3];
        for (int r = 0; r < 3; r++)
        {
            decoderRep[r] = encoderRep[r] = _previous.Rep[r];
        }

        uint* partitions = (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_partitions));
        int splits = DeriveBlockSplits(partitions, sequenceCount);
        if (splits == 0)
        {
            return CompressSection(_store.Whole, decoderRep, encoderRep, source, blockSize, lastBlock, destination, capacity, isPartition: false);
        }

        uint* counts = SplitCounts(0);
        SequenceSection current = Section(0, partitions[0], counts);
        byte* ip = source;
        int total = 0;
        nuint sourceTotal = 0;
        for (int i = 0; i <= splits; i++)
        {
            bool last = i == splits;
            nuint start = i == 0 ? 0 : partitions[i - 1];
            nuint sourceBytes = _sourcePrefix[partitions[i]] - _sourcePrefix[start];
            sourceTotal += sourceBytes;
            if (last)
            {
                // The last part takes the block's last literals too.
                sourceBytes += (nuint)blockSize - sourceTotal;
            }

            int written = CompressSection(
                current, decoderRep, encoderRep, ip, (int)sourceBytes, last ? lastBlock : 0, destination + total, capacity - total, isPartition: true);
            if (written < 0)
            {
                return -1;
            }

            ip += sourceBytes;
            total += written;
            if (!last)
            {
                current = Section(partitions[i], partitions[i + 1], counts);
            }
        }

        // The decoder's history is the next block's.
        for (int r = 0; r < 3; r++)
        {
            _previous.Rep[r] = decoderRep[r];
        }

        return total;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_deriveSeqStoreChunk</c>: the sequences from <paramref name="start"/> to
    /// <paramref name="end"/> and their literals, the last part with the block's last literals; their
    /// codes counted into <paramref name="counts"/>.
    /// </summary>
    private SequenceSection Section(nuint start, nuint end, uint* counts)
    {
        SequenceRecord* sequences = _store.SequencesStart;
        byte* literals = _store.LiteralsStart + _literalPrefix[start];
        nuint literalCount = end == _store.SequenceCount
            ? (nuint)(_store.Literals - literals)
            : _literalPrefix[end] - _literalPrefix[start];
        SequenceStore.CountCodes(sequences + start, end - start, counts);
        return new SequenceSection(literals, literalCount, sequences + start, end - start, counts);
    }

    /// <summary>
    /// libzstd's <c>ZSTD_deriveBlockSplits</c>: the indices of the sequences the block is cut before,
    /// the sequence count last.
    /// </summary>
    /// <returns>The number of cuts.</returns>
    private int DeriveBlockSplits(uint* partitions, nuint sequenceCount)
    {
        if (sequenceCount <= 4)
        {
            return 0;
        }

        _splitCount = 0;
        DeriveBlockSplits(partitions, 0, sequenceCount);
        partitions[_splitCount] = (uint)sequenceCount;
        return _splitCount;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_deriveBlockSplitsHelper</c>: a part cut in two halves when their estimated
    /// sizes add up to less than its own, each half then tried in turn.
    /// </summary>
    private void DeriveBlockSplits(uint* partitions, nuint start, nuint end)
    {
        if (end - start < MinSequencesBlockSplitting || _splitCount >= MaxBlockSplits)
        {
            return;
        }

        nuint middle = (start + end) / 2;
        SequenceSection full = Section(start, end, SplitCounts(0));
        SequenceSection first = Section(start, middle, SplitCounts(1));
        SequenceSection second = Section(middle, end, SplitCounts(2));
        nuint fullSize = EstimateSize(full);
        nuint firstSize = EstimateSize(first);
        nuint secondSize = EstimateSize(second);
        if (fullSize == nuint.MaxValue || firstSize == nuint.MaxValue || secondSize == nuint.MaxValue)
        {
            return;
        }

        if (firstSize + secondSize < fullSize)
        {
            DeriveBlockSplits(partitions, start, middle);
            partitions[_splitCount++] = (uint)middle;
            DeriveBlockSplits(partitions, middle, end);
        }
    }

    /// <summary>
    /// libzstd's <c>ZSTD_compressSeqStore_singleBlock</c>: a part, its repeat codes reconciled with the
    /// decoder's history, emitted as a compressed, RLE or raw block.
    /// </summary>
    /// <returns>The size of the block, or -1 when it does not fit.</returns>
    private int CompressSection(
        in SequenceSection section, uint* decoderRep, uint* encoderRep, byte* source, int sourceSize, int lastBlock,
        byte* destination, int capacity, bool isPartition)
    {
        uint original0 = decoderRep[0];
        uint original1 = decoderRep[1];
        uint original2 = decoderRep[2];
        if (isPartition)
        {
            ResolveOffCodes(decoderRep, encoderRep, section);
            SequenceStore.CountCodes(section.SequencesStart, section.SequenceCount, section.Counts);
        }

        byte* compressed = _blockStart + FrameFormat.BlockHeaderSize;
        nuint size = EntropyCompress(section, compressed, BlockBufferSize - FrameFormat.BlockHeaderSize, (nuint)sourceSize);
        if (!_isFirstBlock && size < RleMaxLength && IsRle(source, sourceSize))
        {
            size = 1;
        }

        int written;
        if (size == 0)
        {
            written = WriteRawBlock(source, sourceSize, lastBlock, destination, capacity);
            decoderRep[0] = original0;
            decoderRep[1] = original1;
            decoderRep[2] = original2;
        }
        else if (size == 1)
        {
            written = FrameFormat.BlockHeaderSize + 1;
            if (capacity < written)
            {
                return -1;
            }

            WriteBlockHeader(destination, lastBlock, (1 << 1) | (sourceSize << 3));
            destination[FrameFormat.BlockHeaderSize] = *source;
            decoderRep[0] = original0;
            decoderRep[1] = original1;
            decoderRep[2] = original2;
        }
        else
        {
            written = FrameFormat.BlockHeaderSize + (int)size;
            if (capacity < written)
            {
                return -1;
            }

            (_previous, _next) = (_next, _previous);
            WriteBlockHeader(destination, lastBlock, (2 << 1) | ((int)size << 3));
            Buffer.MemoryCopy(compressed, destination + FrameFormat.BlockHeaderSize, size, size);
        }

        if (_previous.OffsetRepeat == FseRepeat.Valid)
        {
            _previous.OffsetRepeat = FseRepeat.Check;
        }

        return written;
    }

    private int WriteRawBlock(byte* source, int size, int lastBlock, byte* destination, int capacity)
    {
        int written = FrameFormat.BlockHeaderSize + size;
        if (capacity < written)
        {
            return -1;
        }

        WriteBlockHeader(destination, lastBlock, size << 3);
        Buffer.MemoryCopy(source, destination + FrameFormat.BlockHeaderSize, size, size);
        return written;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_seqStore_resolveOffCodes</c>: each repeat code of the part that means another
    /// offset to the decoder than to the compression replaced by the compression's offset; both
    /// histories updated, the decoder's with what it will read.
    /// </summary>
    private static void ResolveOffCodes(uint* decoderRep, uint* encoderRep, in SequenceSection section)
    {
        for (nuint n = 0; n < section.SequenceCount; n++)
        {
            SequenceRecord* record = section.SequencesStart + n;
            uint ll0 = SequenceStore.LiteralLengthOf(record) == 0 ? 1u : 0u;
            uint offBase = record->OffBase;
            if (offBase <= MatchFinder.RepeatCodeCount)
            {
                uint decoderOffset = RepeatCodeOffset(decoderRep, offBase, ll0);
                uint encoderOffset = RepeatCodeOffset(encoderRep, offBase, ll0);
                if (decoderOffset != encoderOffset)
                {
                    SequenceStore.ReplaceOffBase(record, MatchFinder.OffsetToOffBase(encoderOffset));
                }
            }

            UpdateRep(decoderRep, record->OffBase, ll0);
            UpdateRep(encoderRep, offBase, ll0);
        }
    }

    /// <summary>libzstd's <c>ZSTD_resolveRepcodeToRawOffset</c>.</summary>
    private static uint RepeatCodeOffset(uint* rep, uint offBase, uint ll0)
    {
        uint adjusted = offBase - 1 + ll0;
        return adjusted == MatchFinder.RepeatCodeCount ? rep[0] - 1 : rep[adjusted];
    }

    /// <summary>libzstd's <c>ZSTD_updateRep</c>.</summary>
    private static void UpdateRep(uint* rep, uint offBase, uint ll0)
    {
        if (offBase > MatchFinder.RepeatCodeCount)
        {
            rep[2] = rep[1];
            rep[1] = rep[0];
            rep[0] = offBase - MatchFinder.RepeatCodeCount;
            return;
        }

        uint repCode = offBase - 1 + ll0;
        if (repCode > 0)
        {
            uint currentOffset = repCode == MatchFinder.RepeatCodeCount ? rep[0] - 1 : rep[repCode];
            rep[2] = repCode >= 2 ? rep[1] : rep[2];
            rep[1] = rep[0];
            rep[0] = currentOffset;
        }
    }

    // ---- estimates

    /// <summary>
    /// libzstd's <c>ZSTD_buildEntropyStatisticsAndEstimateSubBlockSize</c>: the tables a part would take,
    /// built into the next block state, and the size of the block they would make.
    /// </summary>
    /// <returns>The estimated size, or <see cref="nuint.MaxValue"/> for libzstd's error.</returns>
    private nuint EstimateSize(in SequenceSection section)
    {
        SymbolEncodingType literalsType = BuildLiteralStatistics(section.LiteralsStart, section.LiteralCount, out nuint descriptionSize);
        if (descriptionSize == nuint.MaxValue)
        {
            return nuint.MaxValue;
        }

        SequenceEncoder.Statistics statistics;
        if (section.SequenceCount != 0)
        {
            byte* tables = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_splitScratch));
            statistics = SequenceEncoder.BuildStatistics(section, _previous, _next, tables, _parameters.Strategy);
        }
        else
        {
            // libzstd's ZSTD_buildDummySequencesStatistics.
            statistics = default;
            _next.LiteralLengthRepeat = FseRepeat.None;
            _next.OffsetRepeat = FseRepeat.None;
            _next.MatchLengthRepeat = FseRepeat.None;
        }

        nuint literals = EstimateLiteralsSize(section.LiteralsStart, section.LiteralCount, literalsType, descriptionSize);
        nuint sequences = EstimateSequencesSize(section, statistics);
        return literals + sequences + FrameFormat.BlockHeaderSize;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_buildBlockEntropyStats_literals</c>: how a part's literals would be coded,
    /// its tree built into the next block state.
    /// </summary>
    /// <param name="literals">The part's literals.</param>
    /// <param name="size">Their number.</param>
    /// <param name="descriptionSize">The size of a new tree's description; <see cref="nuint.MaxValue"/> for libzstd's error.</param>
    private SymbolEncodingType BuildLiteralStatistics(byte* literals, nuint size, out nuint descriptionSize)
    {
        descriptionSize = 0;
        _next.Huffman = _previous.Huffman;
        _next.HuffmanRepeat = _previous.HuffmanRepeat;

        nuint minimumSize = _previous.HuffmanRepeat == HuffmanRepeat.Valid ? 6u : 63u;
        if (size <= minimumSize)
        {
            return SymbolEncodingType.Basic;
        }

        uint* count = SplitCounts(3);
        uint largest = Histogram.CountFast(count, out uint maxSymbol, literals, size);
        if (largest == size)
        {
            return SymbolEncodingType.Rle;
        }

        if (largest <= (size >> 7) + 4)
        {
            return SymbolEncodingType.Basic;
        }

        HuffmanRepeat repeat = _previous.HuffmanRepeat;
        if (repeat == HuffmanRepeat.Check && !HuffmanEncoder.ValidateCTable(_previous.Huffman, count, maxSymbol))
        {
            repeat = HuffmanRepeat.None;
        }

        HuffmanCTable fresh = _next.FreshHuffman(_previous);
        new Span<ulong>(fresh.Elements, HuffmanTable.MaxSymbols).Clear();
        fresh.TableLog = 0;
        fresh.MaxSymbolValue = 0;
        uint huffLog = HuffmanEncoder.OptimalTableLog(
            HuffmanEncoder.LiteralsTableLog, size, maxSymbol, _huffmanWorkspace, count, _parameters.Strategy >= Strategy.BinaryTreeUltra);
        huffLog = HuffmanEncoder.BuildCTable(fresh, count, maxSymbol, huffLog);

        nuint newSize = HuffmanEncoder.EstimateCompressedSize(fresh, count, maxSymbol);
        byte* description = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_splitScratch));
        nuint headerSize = HuffmanEncoder.WriteCTable(description, MaxHuffmanHeaderSize, fresh, maxSymbol, huffLog, _huffmanWorkspace.Weights);
        if (headerSize == 0)
        {
            descriptionSize = nuint.MaxValue;
            return SymbolEncodingType.Basic;
        }

        if (repeat != HuffmanRepeat.None)
        {
            nuint oldSize = HuffmanEncoder.EstimateCompressedSize(_previous.Huffman, count, maxSymbol);
            if (oldSize < size && (oldSize <= headerSize + newSize || headerSize + 12 >= size))
            {
                return SymbolEncodingType.Repeat;
            }
        }

        if (newSize + headerSize >= size)
        {
            return SymbolEncodingType.Basic;
        }

        _next.Huffman = fresh;
        _next.HuffmanRepeat = HuffmanRepeat.Check;
        descriptionSize = headerSize;
        return SymbolEncodingType.Compressed;
    }

    /// <summary>libzstd's <c>ZSTD_estimateBlockSize_literal</c>, a new tree's description counted.</summary>
    private nuint EstimateLiteralsSize(byte* literals, nuint size, SymbolEncodingType type, nuint descriptionSize)
    {
        nuint headerSize = 3 + (size >= 1 << 10 ? 1u : 0u) + (size >= 16 << 10 ? 1u : 0u);
        bool singleStream = size < 256;
        switch (type)
        {
            case SymbolEncodingType.Basic:
                return size;
            case SymbolEncodingType.Rle:
                return 1;
            default:
            {
                uint* count = SplitCounts(3);
                Histogram.CountFast(count, out uint maxSymbol, literals, size);
                nuint estimate = HuffmanEncoder.EstimateCompressedSize(_next.Huffman, count, maxSymbol);
                if (type == SymbolEncodingType.Compressed)
                {
                    estimate += descriptionSize;
                }

                if (!singleStream)
                {
                    // The four streams' jump table.
                    estimate += 6;
                }

                return estimate + headerSize;
            }
        }
    }

    /// <summary>libzstd's <c>ZSTD_estimateBlockSize_sequences</c>, the tables' descriptions counted.</summary>
    private nuint EstimateSequencesSize(in SequenceSection section, in SequenceEncoder.Statistics statistics)
    {
        nuint sequenceCount = section.SequenceCount;
        nuint headerSize = 1 + 1 + (sequenceCount >= 128 ? 1u : 0u) + (sequenceCount >= LongSequenceCount ? 1u : 0u);
        nuint estimate = EstimateSymbols(
            statistics.Offsets, section.Counts + SequenceStore.OffsetCodes, SequenceCodes.MaxOffset, _next.Offsets, default,
            SequenceCodes.OffsetDefaultNorm, SequenceEncoder.OffsetDefaultNormLog, sequenceCount);
        estimate += EstimateSymbols(
            statistics.LiteralLengths, section.Counts, SequenceCodes.MaxLiteralLength, _next.LiteralLengths, SequenceStore.LiteralLengthBits,
            SequenceCodes.LiteralLengthDefaultNorm, SequenceEncoder.LiteralLengthDefaultNormLog, sequenceCount);
        estimate += EstimateSymbols(
            statistics.MatchLengths, section.Counts + SequenceStore.MatchLengthCodes, SequenceCodes.MaxMatchLength, _next.MatchLengths,
            SequenceStore.MatchLengthBits, SequenceCodes.MatchLengthDefaultNorm, SequenceEncoder.MatchLengthDefaultNormLog, sequenceCount);
        return estimate + statistics.Size + headerSize;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_estimateBlockSize_symbolType</c>: one kind of code's FSE bits and extra bits,
    /// in bytes. An offset code is its own number of extra bits (<paramref name="extraBits"/> empty).
    /// </summary>
    private static nuint EstimateSymbols(
        SymbolEncodingType type, uint* count, int maxCode, FseCTable table, ReadOnlySpan<byte> extraBits,
        ReadOnlySpan<short> defaultNorm, int defaultNormLog, nuint sequenceCount)
    {
        // HIST_countFast_wksp's largest code present.
        uint max = (uint)maxCode;
        while (max > 0 && count[max] == 0)
        {
            max--;
        }

        nuint bits = type switch
        {
            SymbolEncodingType.Basic => SequenceEncoder.CrossEntropyCost(defaultNorm, (uint)defaultNormLog, count, max),
            SymbolEncodingType.Rle => 0,
            _ => SequenceEncoder.FseBitCost(table, count, max),
        };

        if (bits == nuint.MaxValue)
        {
            return sequenceCount * 10;
        }

        for (uint code = 0; code <= max; code++)
        {
            bits += (nuint)count[code] * (extraBits.IsEmpty ? code : extraBits[(int)code]);
        }

        return bits >> 3;
    }
}
