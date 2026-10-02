using System;
using BenchmarkDotNet.Attributes;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd.Benchmarks;

/// <summary>
/// What a small block costs before it decodes anything: the Huffman tree (195 weights, compressed with
/// FSE) and the three FSE tables of the 4 KiB JSON frame's block, read and built in isolation. A guard
/// for small frames, where these costs are a third of the time.
/// </summary>
public class FixedCostBenchmarks
{
    // The block's Huffman tree description: weights compressed with FSE, 41 bytes.
    private static readonly byte[] Tree =
    [
        0x28, 0x60, 0x49, 0x93, 0x36, 0x30, 0x0C, 0x0C, 0x82, 0x20, 0xD0, 0x55, 0xD8, 0xF0, 0xD5, 0x9B, 0xAE, 0x7F, 0xC5, 0x61,
        0xE8, 0x1F, 0x21, 0x80, 0xAD, 0xBA, 0x45, 0xCC, 0xFC, 0xBB, 0xBB, 0xBB, 0x37, 0x4B, 0xA9, 0x16, 0x89, 0x82, 0x8D, 0x72, 0x02,
    ];

    // The block's sequence table descriptions (LL, OF, ML in FSE mode), and what follows.
    private static readonly byte[] Tables =
    [
        0x11, 0x75, 0x47, 0x65, 0xFB, 0xDF, 0x01, 0xA1, 0xC6, 0x91, 0x9D, 0x79, 0x11, 0x90, 0x88, 0x80, 0x08, 0x20, 0x09, 0x95,
        0x21, 0xC8, 0x42, 0x11, 0x49, 0x92, 0x24, 0xAD, 0x01, 0xF1, 0xA0, 0xB9, 0x52, 0x97, 0x2D, 0x7A, 0x38, 0xB0, 0xE4, 0x9F,
    ];

    private readonly HuffmanTable _huffman = new();
    private readonly SequenceTableSet _tables = new();
    private readonly short[] _norm = new short[53];

    [Benchmark]
    public int HuffmanTree() => _huffman.Read(Tree);

    private readonly byte[] _weights = new byte[256];

    [Benchmark]
    public int HuffmanWeights() => Fse.DecodeHuffmanWeights(Tree.AsSpan(1, Tree[0]), _weights);

    private readonly short[] _weightNorm = new short[256];

    [Benchmark]
    public int WeightsNCount()
    {
        int symbol = 255;
        return Fse.ReadNCount(_weightNorm, ref symbol, out _, Tree.AsSpan(1, Tree[0]), ZstdError.HuffmanTable);
    }

    [Benchmark]
    public int SequenceTables()
    {
        int position = 0;
        position += Build(SequenceCode.LiteralLength, Tables.AsSpan(position));
        position += Build(SequenceCode.Offset, Tables.AsSpan(position));
        position += Build(SequenceCode.MatchLength, Tables.AsSpan(position));
        return position;
    }

    [Benchmark]
    public int NCountOnly()
    {
        int position = 0;
        foreach (int max in new[] { 35, 31, 52 })
        {
            int symbol = max;
            position += Fse.ReadNCount(_norm, ref symbol, out _, Tables.AsSpan(position), ZstdError.FseTable);
        }

        return position;
    }

    private int Build(SequenceCode code, ReadOnlySpan<byte> source)
    {
        int symbol = SequenceCodes.MaxSymbol(code);
        int size = Fse.ReadNCount(_norm, ref symbol, out int tableLog, source, ZstdError.FseTable);
        _tables.Build(code, _norm.AsSpan(0, symbol + 1), tableLog);
        return size;
    }
}
