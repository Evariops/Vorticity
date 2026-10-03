using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

public sealed unsafe partial class ZstdCompressor
{
    /// <summary>
    /// Room for a block's compressed form: a block that does not fit the largest block, less the
    /// minimum gain, is written raw, so this only needs the margin its sections may overrun by.
    /// </summary>
    private const int BlockBufferSize = FrameFormat.MaxBlockSize + 2048;

    /// <summary>libzstd's <c>MIN_CBLOCK_SIZE</c>.</summary>
    private const int MinCompressedBlockSize = 2;

    /// <summary>libzstd's <c>rleMaxLength</c>: a compressed block shorter than this may be RLE instead.</summary>
    private const int RleMaxLength = 25;

    /// <summary>libzstd's <c>SUSPECT_UNCOMPRESSIBLE_LITERAL_RATIO</c>.</summary>
    private const int SuspectUncompressibleLiteralRatio = 20;

    private readonly byte[] _blockBuffer;
    private readonly byte* _blockStart;
    private readonly SequenceStore _store = new();
    private readonly HuffmanWorkspace _huffmanWorkspace = new();
    private BlockState _previous = new();
    private BlockState _next = new();
    private uint[] _hashTable = [];
    private uint[] _chainTable = [];
    private byte[] _tagTable = [];
    private readonly uint[] _hashCache = GC.AllocateArray<uint>(LazyMatchFinder.RowHashCacheSize, pinned: true);
    private MatchState _matchState;
    private CompressionParameters _parameters;
    private bool _isFirstBlock;

    /// <summary>The index the next frame starts at: where the last one ended.</summary>
    private uint _nextIndex = WindowStartIndex;

    /// <summary>
    /// libzstd's <c>ZSTD_resetCCtx_internal</c> for a frame: the tables sized for its parameters, the
    /// window placed after the last frame's (restarting, tables cleared, only near the index limit),
    /// and the block state every frame starts from.
    /// </summary>
    private void BeginFrame(CompressionParameters parameters, byte* source, int sourceSize)
    {
        _parameters = parameters;
        bool restart = (ulong)_nextIndex + (ulong)sourceSize > IndexLimit;
        bool rows = parameters.UsesRowMatchFinder;
        int hashSize = 1 << parameters.HashLog;
        int chainSize = parameters.Strategy == Strategy.Fast || rows ? 0 : 1 << parameters.ChainLog;
        int tagSize = rows ? hashSize : 0;
        if (_hashTable.Length < hashSize)
        {
            _hashTable = GC.AllocateArray<uint>(hashSize, pinned: true);
        }
        else if (restart)
        {
            Array.Clear(_hashTable);
        }

        if (_chainTable.Length < chainSize)
        {
            _chainTable = GC.AllocateArray<uint>(chainSize, pinned: true);
        }
        else if (restart)
        {
            Array.Clear(_chainTable);
        }

        if (_tagTable.Length < tagSize)
        {
            _tagTable = GC.AllocateArray<byte>(tagSize, pinned: true);
        }
        else if (restart)
        {
            Array.Clear(_tagTable);
        }

        if (restart)
        {
            _nextIndex = WindowStartIndex;
        }

        Array.Clear(_hashCache);
        uint start = _nextIndex;
        _matchState = new MatchState
        {
            Base = source - start,
            DictLimit = start,
            LowLimit = start,
            NextToUpdate = start,
            HashTable = _hashTable.Length == 0 ? null : (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_hashTable)),
            ChainTable = _chainTable.Length == 0 ? null : (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_chainTable)),
            TagTable = _tagTable.Length == 0 ? null : (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_tagTable)),
            HashCache = (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_hashCache)),
            RowHashLog = parameters.HashLog - Math.Clamp(parameters.SearchLog, 4, 6),
            Parameters = parameters,
        };

        _nextIndex = start + (uint)sourceSize;
        _previous.Reset();
        _isFirstBlock = true;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_compress_frameChunk</c>: the source cut into blocks, each compressed, RLE or
    /// raw, the last one flagged.
    /// </summary>
    /// <returns>The size of the blocks, or -1 when they do not fit.</returns>
    private int CompressBlocks(byte* source, int sourceSize, byte* destination, int capacity)
    {
        int blockSizeMax = _parameters.BlockSizeMax(sourceSize);
        int remaining = sourceSize;
        byte* ip = source;
        int op = 0;
        long savings = 0;
        while (remaining > 0)
        {
            int blockSize = OptimalBlockSize(ip, remaining, blockSizeMax, savings);
            int lastBlock = blockSize == remaining ? 1 : 0;
            // As libzstd, from the block's start: the window covers it whole, the matches then
            // limited to it by the match finder.
            _matchState.EnforceMaxDistance(ip);
            if (_matchState.NextToUpdate < _matchState.LowLimit)
            {
                _matchState.NextToUpdate = _matchState.LowLimit;
            }

            byte* compressed = _blockStart + FrameFormat.BlockHeaderSize;
            nuint size = CompressBlock(compressed, BlockBufferSize - FrameFormat.BlockHeaderSize, ip, blockSize);
            int blockTotal;
            if (size == 0)
            {
                // Raw: the block as it is.
                blockTotal = FrameFormat.BlockHeaderSize + blockSize;
                if (capacity - op < blockTotal)
                {
                    return -1;
                }

                WriteBlockHeader(destination + op, lastBlock, blockSize << 3);
                Buffer.MemoryCopy(ip, destination + op + FrameFormat.BlockHeaderSize, blockSize, blockSize);
            }
            else if (size == 1)
            {
                // RLE: one byte the block repeats.
                blockTotal = FrameFormat.BlockHeaderSize + 1;
                if (capacity - op < blockTotal)
                {
                    return -1;
                }

                WriteBlockHeader(destination + op, lastBlock, (1 << 1) | (blockSize << 3));
                destination[op + FrameFormat.BlockHeaderSize] = *compressed;
            }
            else
            {
                blockTotal = FrameFormat.BlockHeaderSize + (int)size;
                if (capacity - op < blockTotal)
                {
                    return -1;
                }

                WriteBlockHeader(destination + op, lastBlock, (2 << 1) | ((int)size << 3));
                Buffer.MemoryCopy(compressed, destination + op + FrameFormat.BlockHeaderSize, size, size);
            }

            // The savings so far allow the next blocks to be split: see OptimalBlockSize.
            savings += blockSize - blockTotal;
            ip += blockSize;
            remaining -= blockSize;
            op += blockTotal;
            _isFirstBlock = false;
        }

        return op;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_optimalBlockSize</c>: full blocks, split before their match finding once
    /// the frame has saved three bytes or more (never the first block, nor incompressible data), at
    /// the depth the strategy allows.
    /// </summary>
    private int OptimalBlockSize(byte* source, int size, int blockSizeMax, long savings)
    {
        const int FullBlock = 128 << 10;
        if (size < FullBlock || blockSizeMax < FullBlock)
        {
            return Math.Min(size, blockSizeMax);
        }

        if (savings < 3)
        {
            return FullBlock;
        }

        ReadOnlySpan<byte> splitLevels = [0, 0, 1, 2, 2, 3, 3, 4, 4, 4];
        return BlockSplitter.Split(source, blockSizeMax, splitLevels[(int)_parameters.Strategy]);
    }

    /// <summary>
    /// libzstd's <c>ZSTD_compressBlock_internal</c> for a block of a frame: its sequences, then its
    /// entropy coding; an RLE block if it is one byte repeated and compresses to little; the block
    /// state handed on only if it is compressed.
    /// </summary>
    /// <returns>The size of the compressed block, 1 for an RLE block (its byte written), 0 for a raw one.</returns>
    private nuint CompressBlock(byte* destination, nuint capacity, byte* source, int size)
    {
        nuint compressedSize;
        if (size < MinCompressedBlockSize + FrameFormat.BlockHeaderSize + 1 + 1)
        {
            // Too small to try.
            compressedSize = 0;
        }
        else
        {
            FindSequences(source, size);
            compressedSize = EntropyCompress(destination, capacity, (nuint)size);
            if (!_isFirstBlock && compressedSize < RleMaxLength && IsRle(source, size))
            {
                // libzstd never makes the first block RLE: zstd up to 1.4.3 refused such frames.
                compressedSize = 1;
                destination[0] = source[0];
            }
        }

        if (Recorder is not null)
        {
            Record(source, size, compressedSize);
        }

        if (compressedSize > 1)
        {
            (_previous, _next) = (_next, _previous);
        }

        if (_previous.OffsetRepeat == FseRepeat.Valid)
        {
            _previous.OffsetRepeat = FseRepeat.Check;
        }

        return compressedSize;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_buildSeqStore</c>: the block's sequences and literals into the store, the
    /// repeat offsets of the next block state updated from the previous one's.
    /// </summary>
    private void FindSequences(byte* source, int size)
    {
        _store.Reset();

        // A limited update after a very long match.
        uint current = (uint)(source - _matchState.Base);
        if (current > _matchState.NextToUpdate + 384)
        {
            _matchState.NextToUpdate = current - Math.Min(192, current - _matchState.NextToUpdate - 384);
        }

        uint* rep = _next.Rep;
        rep[0] = _previous.Rep[0];
        rep[1] = _previous.Rep[1];
        rep[2] = _previous.Rep[2];
        nuint lastLiterals = _parameters.Strategy switch
        {
            Strategy.Fast => FastMatchFinder.CompressBlock(ref _matchState, _store, rep, source, (nuint)size),
            Strategy.DoubleFast => DoubleFastMatchFinder.CompressBlock(ref _matchState, _store, rep, source, (nuint)size),
            _ => LazyMatchFinder.CompressBlock(ref _matchState, _store, rep, source, (nuint)size),
        };

        _store.StoreLastLiterals(source + size - (nint)lastLiterals, lastLiterals);
    }

    /// <summary>libzstd's <c>ZSTD_isRLE</c>: whether every byte equals the first.</summary>
    private static bool IsRle(byte* source, int size)
    {
        return new ReadOnlySpan<byte>(source + 1, size - 1).IndexOfAnyExcept(source[0]) < 0;
    }

    /// <summary>libzstd's <c>ZSTD_minGain</c>: what a compressed block or literals section must save.</summary>
    private static nuint MinGain(nuint size, Strategy strategy)
    {
        int minLog = strategy >= Strategy.BinaryTreeUltra ? (int)strategy - 1 : 6;
        return (size >> minLog) + 2;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_entropyCompressSeqStore</c>: the literals section, then the sequences section.
    /// </summary>
    /// <returns>The size of the compressed block, or 0 when it is not worth it.</returns>
    private nuint EntropyCompress(byte* destination, nuint capacity, nuint blockSize)
    {
        Strategy strategy = _parameters.Strategy;
        byte* op = destination;
        byte* oend = destination + capacity;
        nuint sequenceCount = _store.SequenceCount;
        nuint literalCount = _store.LiteralCount;

        // ---- literals; suspected incompressible from their ratio to the sequences
        bool suspectUncompressible = sequenceCount == 0 || literalCount / sequenceCount >= SuspectUncompressibleLiteralRatio;
        bool literalCompressionDisabled = strategy == Strategy.Fast && _parameters.TargetLength > 0;
        op += CompressLiterals(op, capacity, _store.LiteralsStart, literalCount, literalCompressionDisabled, suspectUncompressible);

        // ---- the sequences section's header, then its tables and bitstream
        if (oend - op < 3 + 1)
        {
            return 0;
        }

        op += SequenceEncoder.WriteSequenceCount(op, sequenceCount);
        if (sequenceCount == 0)
        {
            // The tables carry over as if repeated.
            _next.CopySequenceTablesFrom(_previous);
        }
        else
        {
            byte* modes = op++;
            SequenceEncoder.Statistics stats = SequenceEncoder.BuildStatistics(_store, _previous, _next, op, strategy);
            *modes = (byte)(((int)stats.LiteralLengths << 6) + ((int)stats.Offsets << 4) + ((int)stats.MatchLengths << 2));
            op += stats.Size;

            nuint bitstreamSize = SequenceEncoder.EncodeSequences(
                op, (nuint)(oend - op), _next.LiteralLengths, _next.Offsets, _next.MatchLengths, _store.SequencesStart, sequenceCount);
            if (bitstreamSize == 0)
            {
                return 0;
            }

            op += bitstreamSize;

            // zstd up to 1.3.4 refuses a last table description of 2 bytes followed by a bitstream of
            // one: such a block is written raw.
            if (stats.LastCountSize != 0 && stats.LastCountSize + bitstreamSize < 4)
            {
                return 0;
            }
        }

        nuint compressedSize = (nuint)(op - destination);
        if (compressedSize >= blockSize - MinGain(blockSize, strategy))
        {
            return 0;
        }

        return compressedSize;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_compressLiterals</c>: raw below a size, Huffman-coded with the previous tree or
    /// a new one, one stream below 256 literals, four from there, RLE when they are one byte repeated,
    /// raw when the coding does not gain enough. <c>_next</c>'s tree is the one used.
    /// </summary>
    /// <returns>The size of the literals section.</returns>
    private nuint CompressLiterals(byte* destination, nuint capacity, byte* source, nuint size, bool disabled, bool suspectUncompressible)
    {
        Strategy strategy = _parameters.Strategy;
        nuint headerSize = 3 + (size >= 1 << 10 ? 1u : 0u) + (size >= 16 << 10 ? 1u : 0u);
        bool singleStream = size < 256;
        SymbolEncodingType type = SymbolEncodingType.Compressed;

        // The previous tree, assumed reused.
        _next.Huffman.CopyFrom(_previous.Huffman);
        _next.HuffmanRepeat = _previous.HuffmanRepeat;
        if (disabled || size < MinLiteralsToCompress(strategy, _previous.HuffmanRepeat))
        {
            return WriteRawLiterals(destination, source, size);
        }

        Debug.Assert(capacity >= headerSize + 1);
        HuffmanRepeat repeat = _previous.HuffmanRepeat;
        bool preferRepeat = strategy < Strategy.Lazy && size <= 1024;
        bool optimalDepth = strategy >= Strategy.BinaryTreeUltra;
        if (repeat == HuffmanRepeat.Valid && headerSize == 3)
        {
            singleStream = true;
        }

        nuint compressed = HuffmanEncoder.Compress(
            destination + headerSize, capacity - headerSize, source, size, singleStream, _next.Huffman, _huffmanWorkspace,
            ref repeat, preferRepeat, optimalDepth, suspectUncompressible);
        if (repeat != HuffmanRepeat.None)
        {
            type = SymbolEncodingType.Repeat;
        }

        nuint minGain = MinGain(size, strategy);
        if (compressed == 0 || compressed >= size - minGain)
        {
            _next.Huffman.CopyFrom(_previous.Huffman);
            _next.HuffmanRepeat = _previous.HuffmanRepeat;
            return WriteRawLiterals(destination, source, size);
        }

        if (compressed == 1)
        {
            // One symbol, or, for fewer than 8 literals, possibly one byte of stream: checked.
            if (size >= 8 || new ReadOnlySpan<byte>(source + 1, (int)size - 1).IndexOfAnyExcept(source[0]) < 0)
            {
                _next.Huffman.CopyFrom(_previous.Huffman);
                _next.HuffmanRepeat = _previous.HuffmanRepeat;
                return WriteRleLiterals(destination, source, size);
            }
        }

        if (type == SymbolEncodingType.Compressed)
        {
            _next.HuffmanRepeat = HuffmanRepeat.Check;
        }

        uint lhc;
        switch (headerSize)
        {
            case 3:
                lhc = (uint)type + ((singleStream ? 0u : 1u) << 2) + ((uint)size << 4) + ((uint)compressed << 14);
                destination[0] = (byte)lhc;
                destination[1] = (byte)(lhc >> 8);
                destination[2] = (byte)(lhc >> 16);
                break;
            case 4:
                lhc = (uint)type + (2u << 2) + ((uint)size << 4) + ((uint)compressed << 18);
                Unsafe.WriteUnaligned(destination, lhc);
                break;
            default:
                lhc = (uint)type + (3u << 2) + ((uint)size << 4) + ((uint)compressed << 22);
                Unsafe.WriteUnaligned(destination, lhc);
                destination[4] = (byte)(compressed >> 10);
                break;
        }

        return headerSize + compressed;
    }

    /// <summary>
    /// libzstd's <c>ZSTD_minLiteralsToCompress</c>: 64 literals for the fast strategies, halving for
    /// each stronger one down to 8; 6 with a tree known valid.
    /// </summary>
    private static nuint MinLiteralsToCompress(Strategy strategy, HuffmanRepeat repeat)
    {
        int shift = Math.Min(9 - (int)strategy, 3);
        return repeat == HuffmanRepeat.Valid ? 6u : (nuint)8 << shift;
    }

    /// <summary>libzstd's <c>ZSTD_noCompressLiterals</c>.</summary>
    private static nuint WriteRawLiterals(byte* destination, byte* source, nuint size)
    {
        nuint headerSize = WriteLiteralsHeader(destination, (uint)SymbolEncodingType.Basic, size);
        Buffer.MemoryCopy(source, destination + headerSize, size, size);
        return headerSize + size;
    }

    /// <summary>libzstd's <c>ZSTD_compressRleLiteralsBlock</c>.</summary>
    private static nuint WriteRleLiterals(byte* destination, byte* source, nuint size)
    {
        nuint headerSize = WriteLiteralsHeader(destination, (uint)SymbolEncodingType.Rle, size);
        destination[headerSize] = *source;
        return headerSize + 1;
    }

    /// <summary>The header of a raw or RLE literals section: the size in 5, 12 or 20 bits.</summary>
    private static nuint WriteLiteralsHeader(byte* destination, uint type, nuint size)
    {
        if (size <= 31)
        {
            destination[0] = (byte)(type + ((uint)size << 3));
            return 1;
        }

        if (size <= 4095)
        {
            Unsafe.WriteUnaligned(destination, (ushort)(type + (1u << 2) + ((uint)size << 4)));
            return 2;
        }

        uint value = type + (3u << 2) + ((uint)size << 4);
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
        return 3;
    }
}
