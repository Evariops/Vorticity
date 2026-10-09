using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Fuzz;

/// <summary>
/// Two compressed blocks are decoded side by side (ZstdDecompressor.Pairs.cs) when they have many
/// short sequences. Whatever the input, that must be invisible: the same status, the same error,
/// the same sizes and the same bytes as decoding the blocks one at a time, for valid frames and for
/// frames broken in every way <see cref="FrameMutator"/> knows, with room to spare or too little.
/// <c>VORTICITY_ZSTD_FUZZ_ITERATIONS</c> sets the inputs per seed (default 1,500).
/// </summary>
public sealed class PairEquivalenceTests
{
    public static TheoryData<int> Seeds() => [1, 2, 3, 4, 5, 6];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Paired_blocks_decode_and_fail_as_blocks_one_at_a_time(int seed)
    {
        List<(byte[] Frame, int ContentSize, byte[]? Dictionary)> frames = PairCorpus.Frames;
        var random = new Random(seed);
        var mutator = new FrameMutator(random, frames.Select(f => f.Frame).ToList());
        var decoders = new Dictionary<byte[], (ZstdDecompressor Paired, ZstdDecompressor Alone)>(ReferenceEqualityComparer.Instance);
        long pairedSequences = 0;
        byte[] pairedOutput = new byte[1 << 20];
        byte[] aloneOutput = new byte[1 << 20];
        var failures = new List<string>();
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("VORTICITY_ZSTD_FUZZ_ITERATIONS"), out int n) ? n : 1500;
        for (int iteration = 0; iteration < iterations && failures.Count < 10; iteration++)
        {
            (byte[] frame, int contentSize, byte[]? dictionary) = frames[random.Next(frames.Count)];
            if (!decoders.TryGetValue(dictionary ?? Array.Empty<byte>(), out var pair))
            {
                pair = dictionary is null
                    ? (new ZstdDecompressor { PairsBlocks = true }, new ZstdDecompressor { PairsBlocks = false })
                    : (new ZstdDecompressor(dictionary) { PairsBlocks = true }, new ZstdDecompressor(dictionary) { PairsBlocks = false });
                decoders.Add(dictionary ?? Array.Empty<byte>(), pair);
            }

            (ZstdDecompressor paired, ZstdDecompressor alone) = pair;
            byte[] input = random.Next(6) == 0 ? frame : mutator.Mutate(frame);
            int capacity = random.Next(6) switch
            {
                0 => random.Next(contentSize + 1),
                1 => Math.Min(contentSize + random.Next(64), pairedOutput.Length),
                _ => contentSize,
            };

            OperationStatus pairedStatus = paired.Decompress(input, pairedOutput.AsSpan(0, capacity), out int pairedConsumed, out int pairedWritten);
            OperationStatus aloneStatus = alone.Decompress(input, aloneOutput.AsSpan(0, capacity), out int aloneConsumed, out int aloneWritten);
            if (pairedStatus != aloneStatus || paired.LastError != alone.LastError
                || pairedConsumed != aloneConsumed || pairedWritten != aloneWritten
                || !pairedOutput.AsSpan(0, pairedWritten).SequenceEqual(aloneOutput.AsSpan(0, aloneWritten)))
            {
                failures.Add($"iteration {iteration}: paired {pairedStatus}/{paired.LastError} {pairedConsumed}->{pairedWritten}, "
                    + $"alone {aloneStatus}/{alone.LastError} {aloneConsumed}->{aloneWritten}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        foreach ((ZstdDecompressor paired, ZstdDecompressor alone) in decoders.Values)
        {
            pairedSequences += paired.PairedSequences;
            Assert.Equal(0, alone.PairedSequences);
        }

        Assert.True(pairedSequences > 0, "no block was decoded in a pair");
    }

    /// <summary>Frames of several blocks with many short sequences each: the ones that pair.</summary>
    private static class PairCorpus
    {
        private static readonly Lazy<List<(byte[], int, byte[]?)>> Shared = new(Build);

        public static List<(byte[] Frame, int ContentSize, byte[]? Dictionary)> Frames => Shared.Value;

        private static List<(byte[], int, byte[]?)> Build()
        {
            var frames = new List<(byte[], int, byte[]?)>();
            foreach (string name in new[]
            {
                CorpusCase.Name("text", 300000, 1), CorpusCase.Name("urls", 300000, 3), CorpusCase.Name("json", 200000, 1),
                CorpusCase.Name("mixed", 300000, 1), CorpusCase.Name("walk64", 300000, 3, "chk"),
                CorpusCase.Name("text", 60000, 3, "block=4096"), CorpusCase.Name("urls", 60000, 1, "block=2048"),
                CorpusCase.Name("json", 60000, 3, "block=3000", "nofcs"), CorpusCase.Name("text", 40000, 3, "dict=trained", "block=4096"),
                CorpusCase.Name("json", 40000, 1, "dict=raw", "block=2048"), CorpusCase.Name("walk64", 40000, 3, "dict=trained", "block=4096"),
            })
            {
                CorpusCase @case = CorpusCase.Parse(name);
                byte[] data = @case.Data;
                frames.Add((@case.Compress(data), data.Length, @case.GetDictionary()?.Bytes));
            }

            return frames;
        }
    }
}
