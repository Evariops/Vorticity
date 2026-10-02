using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Vorticity.Zstd.Internal;
using Vorticity.Zstd.Tests.Support;
using Xunit;

namespace Vorticity.Zstd.Tests.Fuzz;

/// <summary>
/// Valid frames, broken in every way <see cref="FrameMutator"/> knows, handed to the decoder in
/// buffers fenced by inaccessible pages. Whatever the input, the decoder must return a status: no
/// other exception, no access outside its buffers (it would fault), no endless loop (a watchdog ends
/// the process). When it decodes a frame, libzstd one-shot must decode the same bytes to the same
/// content; when it refuses one, libzstd must refuse it too.
/// </summary>
/// <remarks>
/// <c>VORTICITY_ZSTD_FUZZ_ITERATIONS</c> sets the number of mutations per seed (default 2,500 for each of 8
/// seeds). Run it again with <c>DOTNET_EnableHWIntrinsic=0</c> for the scalar paths.
/// </remarks>
public sealed class FuzzTests
{
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("VORTICITY_ZSTD_FUZZ_ITERATIONS"), CultureInfo.InvariantCulture, out int n) ? n : 2500;

    public static TheoryData<int> Seeds() => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Mutated_frames_are_refused_or_decoded_like_libzstd(int seed)
    {
        FuzzCorpus corpus = FuzzCorpus.Instance;
        var random = new Random(seed);
        var mutator = new FrameMutator(random, corpus.Frames.Select(f => f.Frame).ToList());
        LibzstdOracle? oracle = LibzstdOracle.Instance;
        var decoders = new Dictionary<int, ZstdDecompressor>();
        using var source = new GuardedBuffer(4 << 20);
        using var destination = new GuardedBuffer(4 << 20);
        byte[] oracleOutput = new byte[4 << 20];
        var failures = new List<string>();
        var divergences = new Dictionary<ZstdError, int>();
        int decoded = 0;

        using var watchdog = new Watchdog(TimeSpan.FromSeconds(30));
        for (int iteration = 0; iteration < Iterations && failures.Count < 10; iteration++)
        {
            FuzzFrame original = corpus.Frames[random.Next(corpus.Frames.Count)];
            byte[] input = mutator.Mutate(original.Frame);
            int capacity = random.Next(8) switch
            {
                0 => random.Next(original.ContentSize + 1),
                1 => original.ContentSize + random.Next(1 << 16),
                _ => original.ContentSize,
            };
            capacity = Math.Min(capacity, destination.Capacity);

            Span<byte> src = random.Next(2) == 0 ? source.AtEnd(input.Length) : source.AtStart(input.Length);
            input.CopyTo(src);
            Span<byte> dst = random.Next(2) == 0 ? destination.AtEnd(capacity) : destination.AtStart(capacity);

            if (!decoders.TryGetValue(original.DictionaryIndex, out ZstdDecompressor? decoder))
            {
                decoder = original.DictionaryIndex < 0 ? new ZstdDecompressor() : new ZstdDecompressor(corpus.Dictionaries[original.DictionaryIndex]);
                decoders.Add(original.DictionaryIndex, decoder);
            }

            OperationStatus status;
            int consumed, written;
            try
            {
                watchdog.Arm(seed, iteration);
                status = decoder.Decompress(src, dst, out consumed, out written);
                watchdog.Disarm();
            }
            catch (Exception e)
            {
                failures.Add(Record(seed, iteration, input, original, $"threw {e.GetType().Name}: {e.Message}"));
                continue;
            }

            if (status != OperationStatus.Done)
            {
                if (consumed != 0 || written != 0)
                {
                    failures.Add(Record(seed, iteration, input, original, $"{status} but consumed {consumed}, wrote {written}"));
                }
            }
            else if (consumed > input.Length || written > capacity)
            {
                failures.Add(Record(seed, iteration, input, original, $"consumed {consumed} of {input.Length}, wrote {written} of {capacity}"));
            }

            if (oracle is null)
            {
                continue;
            }

            nint dictionary = original.DictionaryIndex < 0 ? 0 : corpus.NativeDictionaries[original.DictionaryIndex];
            if (status == OperationStatus.Done)
            {
                decoded++;
                int expected = oracle.Decompress(input.AsSpan(0, consumed), oracleOutput, dictionary, out int frameSize);
                if (expected < 0 || frameSize != consumed || expected != written || !oracleOutput.AsSpan(0, expected).SequenceEqual(dst.Slice(0, written)))
                {
                    failures.Add(Record(seed, iteration, input, original, $"decoded {written} bytes from {consumed}; libzstd: {expected} from {frameSize}"));
                }
            }
            else
            {
                int expected = oracle.Decompress(input, oracleOutput, dictionary, out _);
                bool fits = expected >= 0 && expected <= capacity;
                if (fits && status != OperationStatus.NeedMoreData)
                {
                    // libzstd decodes what Vorticity.Zstd refuses. Counted, and reported below.
                    divergences[decoder.LastError] = divergences.GetValueOrDefault(decoder.LastError) + 1;
                }
            }
        }

        string summary = $"seed {seed}: {Iterations} mutations, {decoded} decoded" +
            (divergences.Count == 0 ? string.Empty : "; refused where libzstd decodes: " + string.Join(", ", divergences.Select(d => $"{d.Key} x{d.Value}")));
        Console.WriteLine(summary);
        Assert.True(failures.Count == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, failures));

        // The leniencies of libzstd one-shot that Vorticity.Zstd does not share: when it picks its
        // double-symbol Huffman decoder, the last symbol of a stream may overrun the stream by a few
        // bits; and a block may regenerate more than Block_Maximum_Size, which RFC 8878 forbids and
        // libzstd's own streaming decoder refuses.
        Assert.True(divergences.Keys.All(e => e is ZstdError.HuffmanStream or ZstdError.BlockTooLarge), summary);
    }

    private static string Record(int seed, int iteration, byte[] input, FuzzFrame original, string what)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "fuzz-failures");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"seed{seed}-{iteration}.zst");
        File.WriteAllBytes(path, input);
        return $"  {path} (from {original.Name}, dictionary {original.DictionaryIndex}): {what}";
    }

    /// <summary>Ends the process if one decode runs for longer than any frame of the corpus could need.</summary>
    private sealed class Watchdog : IDisposable
    {
        private readonly Timer _timer;
        private readonly TimeSpan _limit;
        private long _armedAt;
        private int _seed;
        private int _iteration;

        public Watchdog(TimeSpan limit)
        {
            _limit = limit;
            _timer = new Timer(_ => Check(), null, 1000, 1000);
        }

        public void Arm(int seed, int iteration)
        {
            _seed = seed;
            _iteration = iteration;
            Volatile.Write(ref _armedAt, Stopwatch.GetTimestamp());
        }

        public void Disarm() => Volatile.Write(ref _armedAt, 0);

        private void Check()
        {
            long armed = Volatile.Read(ref _armedAt);
            if (armed != 0 && Stopwatch.GetElapsedTime(armed) > _limit)
            {
                Environment.FailFast($"decoder stuck: fuzz seed {_seed}, iteration {_iteration}");
            }
        }

        public void Dispose() => _timer.Dispose();
    }
}

internal sealed record FuzzFrame(string Name, byte[] Frame, int ContentSize, int DictionaryIndex);

/// <summary>The valid frames the mutations start from, built once.</summary>
internal sealed class FuzzCorpus
{
    private static readonly Lazy<FuzzCorpus> Shared = new(() => new FuzzCorpus());

    public static FuzzCorpus Instance => Shared.Value;

    public List<FuzzFrame> Frames { get; } = [];

    public List<byte[]> Dictionaries { get; } = [];

    public List<nint> NativeDictionaries { get; } = [];

    private FuzzCorpus()
    {
        // From the platform's encoder: every kind, small enough to mutate many times a second.
        foreach (string kind in DataKinds.All)
        {
            foreach ((int size, int level, string[] flags) in new (int, int, string[])[]
            {
                (300, 1, []), (5000, 3, []), (20000, 19, []), (40000, -1, []), (3000, 3, ["chk"]),
                (9000, 9, ["nofcs"]), (6000, 3, ["block=1340"]),
            })
            {
                Add(CorpusCase.Name(kind, size, level, flags));
            }
        }

        foreach (string kind in new[] { "text", "json", "walk64" })
        {
            Add(CorpusCase.Name(kind, 4096, 3, "dict=trained"));
            Add(CorpusCase.Name(kind, 4096, 19, "dict=raw"));
        }

        // From decodecorpus: the modes encoders seldom emit.
        AddDecodeCorpus("small", 120);
        AddDecodeCorpus("dict", 40);

        // zstd's golden frames.
        foreach (string path in Directory.GetFiles(TestData.PathOf("golden-decompression")))
        {
            byte[] frame = File.ReadAllBytes(path);
            Frames.Add(new FuzzFrame(Path.GetFileName(path), frame, 1 << 18, -1));
        }
    }

    private void Add(string name)
    {
        CorpusCase @case = CorpusCase.Parse(name);
        byte[] data = @case.Data;
        int dictionary = -1;
        if (@case.GetDictionary() is { } d)
        {
            dictionary = DictionaryIndex(d.Bytes);
        }

        Frames.Add(new FuzzFrame(name, @case.Compress(data), data.Length, dictionary));
    }

    private void AddDecodeCorpus(string set, int count)
    {
        int dictionary = -1;
        string dictionaryPath = TestData.PathOf(Path.Combine("decodecorpus", set, "dictionary"));
        if (File.Exists(dictionaryPath))
        {
            dictionary = DictionaryIndex(File.ReadAllBytes(dictionaryPath));
        }

        foreach (string line in File.ReadLines(TestData.PathOf(Path.Combine("decodecorpus", set, "manifest.txt"))).Take(count))
        {
            string[] parts = line.Split(' ');
            byte[] frame = TestData.Read(Path.Combine("decodecorpus", set, parts[0]));
            Frames.Add(new FuzzFrame(set + "/" + parts[0], frame, int.Parse(parts[1], CultureInfo.InvariantCulture), dictionary));
        }
    }

    private int DictionaryIndex(byte[] bytes)
    {
        int index = Dictionaries.FindIndex(d => d.AsSpan().SequenceEqual(bytes));
        if (index >= 0)
        {
            return index;
        }

        Dictionaries.Add(bytes);
        NativeDictionaries.Add(LibzstdOracle.Instance?.CreateDictionary(bytes) ?? 0);
        return Dictionaries.Count - 1;
    }
}
