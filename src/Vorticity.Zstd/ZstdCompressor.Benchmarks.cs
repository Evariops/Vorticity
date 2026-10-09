using System;
using System.Collections.Generic;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

public sealed unsafe partial class ZstdCompressor
{
    /// <summary>
    /// A block as compression saw it, for the micro-benchmarks: where it starts, its size, whether it
    /// was emitted compressed, and what its match finder stored.
    /// </summary>
    internal sealed class BlockRecord
    {
        public int Start;
        public int Size;
        public bool Compressed;
        public byte[] Literals = [];
        public SequenceRecord[] Sequences = [];
        public uint[] Counts = [];
    }

    /// <summary>When set, every block compressed is recorded into it.</summary>
    internal List<BlockRecord>? Recorder { get; set; }

    private byte* _recordedSource;

    private void Record(byte* source, int size, nuint compressedSize)
    {
        var record = new BlockRecord { Start = (int)(source - _recordedSource), Size = size, Compressed = compressedSize > 1 };
        if (size >= MinCompressedBlockSize + FrameFormat.BlockHeaderSize + 1 + 1)
        {
            record.Literals = new ReadOnlySpan<byte>(_store.LiteralsStart, (int)_store.LiteralCount).ToArray();
            record.Sequences = new ReadOnlySpan<SequenceRecord>(_store.SequencesStart, (int)_store.SequenceCount).ToArray();
            record.Counts = new ReadOnlySpan<uint>(_store.Counts, SequenceStore.AllCodes).ToArray();
        }

        Recorder!.Add(record);
    }

    /// <summary>Compresses <paramref name="source"/> recording its blocks.</summary>
    internal List<BlockRecord> RecordBlocks(ReadOnlySpan<byte> source)
    {
        Recorder = [];
        byte[] output = new byte[GetMaxCompressedLength(source.Length)];
        fixed (byte* src = source)
        {
            _recordedSource = src;
            Compress(source, output, out _, out _);
        }

        List<BlockRecord> blocks = Recorder;
        Recorder = null;
        return blocks;
    }

    /// <summary>
    /// The match finding of a frame alone, on the blocks <see cref="RecordBlocks"/> found: each block's
    /// repeat offsets handed on as compression hands them.
    /// </summary>
    /// <returns>The number of sequences found.</returns>
    internal int FindSequencesOnly(ReadOnlySpan<byte> source, List<BlockRecord> blocks)
    {
        int total = 0;
        fixed (byte* src = source)
        {
            CompressionParameters parameters = CompressionParameters.ForFrame(Level, source.Length, LongDistanceMatching);
            BeginFrame(parameters, src, source.Length, LongDistanceMatching || LongDistanceMatcher.EnabledFor(parameters));
            foreach (BlockRecord block in blocks)
            {
                byte* ip = src + block.Start;
                _matchState.EnforceMaxDistance(ip);
                if (_matchState.NextToUpdate < _matchState.LowLimit)
                {
                    _matchState.NextToUpdate = _matchState.LowLimit;
                }

                if (block.Sequences.Length + block.Literals.Length == 0)
                {
                    continue;
                }

                FindSequences(ip, block.Size);
                total += (int)_store.SequenceCount;
                if (block.Compressed)
                {
                    _previous.Rep[0] = _next.Rep[0];
                    _previous.Rep[1] = _next.Rep[1];
                    _previous.Rep[2] = _next.Rep[2];
                }
            }
        }

        return total;
    }

    /// <summary>
    /// The entropy coding of recorded blocks alone, literals and/or sequences, each block's tables
    /// handed on as compression hands them.
    /// </summary>
    /// <returns>The total size written.</returns>
    internal long EntropyOnly(List<BlockRecord> blocks, int level, bool literals, bool sequences)
    {
        _parameters = CompressionParameters.ForFrame(level, 1 << 20);
        _previous.Reset();
        long total = 0;
        byte* destination = _blockStart + FrameFormat.BlockHeaderSize;
        nuint capacity = BlockBufferSize - FrameFormat.BlockHeaderSize;
        foreach (BlockRecord block in blocks)
        {
            if (block.Sequences.Length + block.Literals.Length == 0)
            {
                continue;
            }

            _store.Reset();
            block.Literals.CopyTo(new Span<byte>(_store.LiteralsStart, block.Literals.Length));
            if (sequences)
            {
                block.Sequences.CopyTo(new Span<SequenceRecord>(_store.SequencesStart, block.Sequences.Length));
            }

            _store.Literals = _store.LiteralsStart + block.Literals.Length;
            _store.Sequences = _store.SequencesStart + block.Sequences.Length;
            block.Counts.CopyTo(new Span<uint>(_store.Counts, SequenceStore.AllCodes));
            if (literals && sequences)
            {
                total += (long)EntropyCompress(_store.Whole, destination, capacity, (nuint)block.Size);
                if (block.Compressed)
                {
                    (_previous, _next) = (_next, _previous);
                }

                continue;
            }

            if (literals)
            {
                nuint sequenceCount = _store.SequenceCount;
                bool suspect = sequenceCount == 0 || _store.LiteralCount / sequenceCount >= SuspectUncompressibleLiteralRatio;
                bool disabled = _parameters.Strategy == Strategy.Fast && _parameters.TargetLength > 0;
                total += (long)CompressLiterals(destination, capacity, _store.LiteralsStart, _store.LiteralCount, disabled, suspect);
                if (block.Compressed)
                {
                    _previous.Huffman = _next.Huffman;
                    _previous.HuffmanRepeat = _next.HuffmanRepeat;
                }
            }

            if (sequences && _store.SequenceCount > 0)
            {
                SequenceEncoder.Statistics stats = SequenceEncoder.BuildStatistics(_store.Whole, _previous, _next, destination, _parameters.Strategy);
                total += (long)stats.Size;
                total += (long)SequenceEncoder.EncodeSequences(
                    destination + stats.Size, capacity - stats.Size, _next.LiteralLengths, _next.Offsets, _next.MatchLengths,
                    _store.SequencesStart, _store.SequenceCount);
                if (block.Compressed)
                {
                    _previous.CopySequenceTablesFrom(_next);
                }
            }
        }

        return total;
    }
}
