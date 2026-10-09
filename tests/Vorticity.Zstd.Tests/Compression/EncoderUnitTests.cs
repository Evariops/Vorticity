using System;
using System.Buffers;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Compression;

/// <summary>
/// The entropy encoders against the decoder's own readers: what one writes, the other must read back
/// exactly.
/// </summary>
public sealed unsafe class EncoderUnitTests
{
    public static TheoryData<int> Seeds() => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Normalized_distributions_read_back(int seed)
    {
        var random = new Random(seed);
        uint* count = stackalloc uint[256];
        short* normalized = stackalloc short[256];
        byte* header = stackalloc byte[FseEncoder.NCountBound];
        short[] read = new short[256];
        for (int round = 0; round < 200; round++)
        {
            uint maxSymbol = (uint)random.Next(1, random.Next(2) == 0 ? 53 : 256);
            nuint total = 0;
            for (uint s = 0; s <= maxSymbol; s++)
            {
                count[s] = random.Next(4) == 0 ? 0 : (uint)random.Next(1, random.Next(2) == 0 ? 10 : 5000);
                total += count[s];
            }

            // At least two symbols, the last one present: the encoder's preconditions.
            count[0] += 1;
            count[maxSymbol] = Math.Max(count[maxSymbol], 1);
            total = 0;
            for (uint s = 0; s <= maxSymbol; s++)
            {
                total += count[s];
            }

            uint tableLog = FseEncoder.OptimalTableLog((uint)random.Next(5, 13), total, maxSymbol);
            bool lowProbability = random.Next(2) == 0;
            Assert.Equal(tableLog, FseEncoder.NormalizeCount(normalized, tableLog, count, total, maxSymbol, lowProbability));

            int sum = 0;
            for (uint s = 0; s <= maxSymbol; s++)
            {
                sum += Math.Abs(normalized[s]);
                Assert.True(count[s] == 0 ? normalized[s] == 0 : normalized[s] != 0, $"symbol {s}");
            }

            Assert.Equal(1 << (int)tableLog, sum);

            nuint size = FseEncoder.WriteNCount(header, normalized, maxSymbol, tableLog);
            int maxSymbolValue = 255;
            int consumed = Fse.ReadNCount(read, ref maxSymbolValue, out int readLog, new ReadOnlySpan<byte>(header, (int)size), ZstdError.FseTable);
            Assert.Equal((int)size, consumed);
            Assert.Equal((int)tableLog, readLog);
            Assert.Equal((int)maxSymbol, maxSymbolValue);
            for (uint s = 0; s <= maxSymbol; s++)
            {
                Assert.Equal(normalized[s], read[s]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Huffman_streams_decode_back(int seed)
    {
        var random = new Random(seed);
        var workspace = new HuffmanWorkspace();
        var previous = new HuffmanCTable();
        var fresh = new HuffmanCTable();
        var table = new HuffmanTable();
        byte[] output = new byte[1 << 17];
        for (int round = 0; round < 100; round++)
        {
            int size = random.Next(4) switch
            {
                0 => random.Next(64, 300),
                1 => random.Next(300, 5000),
                _ => random.Next(5000, 1 << 17),
            };

            // Skewed bytes from a random alphabet: some symbols frequent, many rare.
            byte[] literals = new byte[size];
            int alphabet = random.Next(2, 257);
            double skew = 1 + (random.NextDouble() * 4);
            for (int i = 0; i < size; i++)
            {
                literals[i] = (byte)(Math.Pow(random.NextDouble(), skew) * alphabet);
            }

            byte[] destination = new byte[size + 1024];
            HuffmanRepeat repeat = HuffmanRepeat.None;
            bool singleStream = size < 256;
            nuint compressed;
            fixed (byte* src = literals)
            fixed (byte* dst = destination)
            {
                compressed = HuffmanEncoder.Compress(
                    dst, (nuint)destination.Length, src, (nuint)size, singleStream, previous, fresh, workspace, ref repeat,
                    preferRepeat: false, optimalDepth: random.Next(2) == 0, suspectUncompressible: false);
            }

            if (compressed <= 1)
            {
                continue;
            }

            ReadOnlySpan<byte> section = destination.AsSpan(0, (int)compressed);
            int treeSize = table.Read(section);
            if (singleStream)
            {
                table.DecodeSingleStream(section.Slice(treeSize), output.AsSpan(0, size));
            }
            else
            {
                table.DecodeFourStreams(section.Slice(treeSize), output.AsSpan(0, size));
            }

            Assert.True(literals.AsSpan().SequenceEqual(output.AsSpan(0, size)), $"round {round}: {size} literals");
        }
    }
}

/// <summary>A ratchet: a warm compressor allocates nothing per frame.</summary>
public sealed class CompressionAllocationTests
{
    /// <summary>Bytes a warm compressor may allocate per frame. Never raise it.</summary>
    private const long MaxBytesPerFrame = 0;

    public static TheoryData<string> Frames() =>
    [
        CorpusCase.Name("text", 400_000, 3),
        CorpusCase.Name("json", 131_073, 1, "chk"),
        CorpusCase.Name("repeats", 400_000, 4),
        CorpusCase.Name("mixed", 1_000_000, -1),
        CorpusCase.Name("urls", 4096, 3),
        CorpusCase.Name("walk64", 70_000, 2),
    ];

    [Theory]
    [MemberData(nameof(Frames))]
    public void A_warm_compressor_allocates_nothing(string name)
    {
#if DEBUG
        Assert.Skip("allocations are a property of the optimized build");
#endif
        CorpusCase @case = CorpusCase.Parse(name);
        byte[] data = @case.Data;
        var compressor = new ZstdCompressor(@case.Level) { AppendChecksum = @case.Checksum };
        byte[] output = new byte[ZstdCompressor.GetMaxCompressedLength(data.Length)];
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(OperationStatus.Done, compressor.Compress(data, output, out _, out _));
        }

        const int Frames = 10;
        long perFrame = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Frames; i++)
            {
                compressor.Compress(data, output, out _, out _);
            }

            perFrame = Math.Min(perFrame, (GC.GetAllocatedBytesForCurrentThread() - before) / Frames);
        }

        Assert.True(perFrame <= MaxBytesPerFrame, $"{name}: {perFrame} bytes allocated per frame");
    }
}
