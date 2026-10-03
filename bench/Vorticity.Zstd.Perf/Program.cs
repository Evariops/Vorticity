using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using Vorticity.Zstd.Bench;

namespace Vorticity.Zstd.Perf;

/// <summary>
/// Paired, alternated timings of the three decoders in one Native AOT process: Vorticity.Zstd, the
/// platform's <see cref="ZstandardDecoder"/>, and libzstd 1.5.7 freshly compiled. Each repetition
/// times every candidate once, in an order that rotates, so that whatever the machine does at that
/// moment lands on all of them alike. Several passes show how stable the figures are.
/// </summary>
/// <remarks>
/// Usage: <c>Vorticity.Zstd.Perf [--frames a,b] [--passes N] [--reps N] [--only zstd] [--ab] [--dump dir] [--no-check] [--no-pair]
/// [--pmu default|EV1,EV2] [--compress]</c>; <c>--pmu</c> counts hardware events per decode and sequence (under sudo); <c>--ab</c>
/// adds a "before" candidate that runs with the legacy switches of the point under work (see
/// <see cref="AbSwitch"/>). <c>--compress</c> times compression instead: each frame's content, at the
/// level its name gives (3 for the reference). Before timing,
/// every candidate's output is checked against the platform's.
/// </remarks>
public static class Program
{
    private sealed record Candidate(string Name, Func<byte[], byte[], int> Decode);

    public static int Main(string[] args)
    {
        string[] frames = Option(args, "--frames")?.Split(',') ?? BenchFrames.Names;
        if (Option(args, "--dump") is string directory)
        {
            // The frames as files, for tools outside the process.
            System.IO.Directory.CreateDirectory(directory);
            foreach (string name in frames)
            {
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, name + ".zst"), BenchFrames.Load(name));
            }

            return 0;
        }

        if (Option(args, "--micro") is string micro)
        {
            PcSampler.Path = Option(args, "--pcprofile");
            return Micro.Run(micro, frames, int.Parse(Option(args, "--repeat") ?? "1", CultureInfo.InvariantCulture));
        }

        if (Option(args, "--pmu") is string events)
        {
            // Hardware counters of Vorticity.Zstd's decodes alone: sudo Vorticity.Zstd.Perf --pmu default|EV1,EV2...
            Pmu? pmu = Pmu.TryCreate(events == "default" ? Pmu.DefaultEvents : events.Split(','));
            if (pmu is null)
            {
                return 1;
            }

            var decoder = new ZstdDecompressor();
            foreach (string name in frames)
            {
                byte[] frame = BenchFrames.Load(name);
                byte[] output = new byte[BenchFrames.ContentSize(frame)];
                int pmuReps = int.Parse(Option(args, "--reps") ?? "1000", CultureInfo.InvariantCulture);
                pmu.Measure(name, (f, o) => { decoder.Decompress(f, o, out _, out int w); return w; }, frame, output, pmuReps, Micro.CountSequences(frame), "sequence");
            }

            return 0;
        }

        int passes = int.Parse(Option(args, "--passes") ?? "5", CultureInfo.InvariantCulture);
        int reps = int.Parse(Option(args, "--reps") ?? "300", CultureInfo.InvariantCulture);
        string? only = Option(args, "--only");
        Batch = Option(args, "--batch") is string batch ? int.Parse(batch, CultureInfo.InvariantCulture) : null;
        NoCheck = args.Contains("--no-check");

        if (args.Contains("--compress"))
        {
            Console.WriteLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
                $"AOT: {!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}, passes {passes} x {reps} reps, compression");
            foreach (string name in frames)
            {
                if (!RunCompress(name, passes, reps, only, args.Contains("--ab")))
                {
                    return 1;
                }
            }

            return 0;
        }

        // --no-pair: one block at a time, to measure what pairing them brings.
        var zstd = new ZstdDecompressor { PairsBlocks = !args.Contains("--no-pair") };
        var platform = new ZstandardDecoder();
        NativeReference? native = NativeReference.TryLoad();

        var candidates = new List<Candidate>
        {
            new("zstd", (f, o) => { zstd.Reset(); zstd.Decompress(f, o, out _, out int w); return w; }),
            new("platform", (f, o) => { platform.Reset(); platform.Decompress(f, o, out _, out int w); return w; }),
        };
        if (native is not null)
        {
            candidates.Add(new("libzstd-ref", (f, o) => native.Decompress(f, o)));
        }

        if (args.Contains("--ab"))
        {
            // The point under measurement, toggled per call: "before" sets the legacy switches.
            var before = new ZstdDecompressor();
            candidates.Insert(1, new("before", (f, o) =>
            {
                AbSwitch.Set(legacy: true);
                before.Decompress(f, o, out _, out int w);
                AbSwitch.Set(legacy: false);
                return w;
            }));
        }
        else
        {
            Console.WriteLine($"(no {NativeReference.LibraryPath}: run tools/native-ref/build.sh for the third reference)");
        }

        if (only is not null)
        {
            candidates = candidates.Where(c => c.Name == only).ToList();
        }

        Console.WriteLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
            $"AOT: {!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}, passes {passes} x {reps} reps");
        foreach (string name in frames)
        {
            if (!Run(name, candidates, passes, reps))
            {
                return 1;
            }
        }

        return 0;
    }

    private static bool Run(string name, List<Candidate> candidates, int passes, int reps)
    {
        byte[] frame = BenchFrames.Load(name);
        int size = BenchFrames.ContentSize(frame);

        // Every candidate must produce the platform's output before anything is timed.
        byte[] expected = new byte[size];
        using (var check = new ZstandardDecoder())
        {
            check.Decompress(frame, expected, out _, out _);
        }

        foreach (Candidate candidate in candidates)
        {
            byte[] output = new byte[size];
            int written = candidate.Decode(frame, output);
            if (!NoCheck && (written != size || !output.AsSpan().SequenceEqual(expected)))
            {
                Console.WriteLine($"{name}: {candidate.Name} produced a different output ({written} bytes)");
                return false;
            }
        }

        Time(name, $"{frame.Length} -> {size} bytes", frame, new byte[size], candidates, passes, reps);
        return true;
    }

    /// <summary>
    /// Compression of a frame's content by each candidate: Vorticity.Zstd must write the platform's frame,
    /// byte for byte, wherever its level is implemented; each frame's size is shown. A name ending in
    /// <c>-ldm</c> turns the long-distance matcher on, and leaves the reference build out; one ending
    /// in <c>-dict</c> compresses with a dictionary of the content's kind (<see cref="BenchFrames.Dictionary"/>),
    /// prepared at the level, every candidate the same way.
    /// </summary>
    private static bool RunCompress(string name, int passes, int reps, string? only, bool ab)
    {
        int workers = name.LastIndexOf("-mt", StringComparison.Ordinal) is int at and > 0 && int.TryParse(name.AsSpan(at + 3), out int n) ? n : 0;
        if (workers > 0)
        {
            return RunCompressInJobs(name, name[..name.LastIndexOf("-mt", StringComparison.Ordinal)], workers, passes, reps, only, ab);
        }

        bool longDistance = name.EndsWith("-ldm", StringComparison.Ordinal);
        bool withDictionary = name.EndsWith("-dict", StringComparison.Ordinal);
        string frameName = longDistance ? name[..^4] : withDictionary ? name[..^5] : name;
        (byte[] content, int level) = BenchFrames.LoadContent(frameName);
        byte[]? dictionary = withDictionary ? BenchFrames.Dictionary(frameName) : null;
        byte[] expected = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
        ZstandardDictionary? prepared = dictionary is null ? null : ZstandardDictionary.Create(dictionary, level);
        var platform = new ZstandardEncoder(new ZstandardCompressionOptions { Quality = level, EnableLongDistanceMatching = longDistance, Dictionary = prepared });
        if (platform.Compress(content, expected, out _, out int expectedSize, isFinalBlock: true) != System.Buffers.OperationStatus.Done)
        {
            Console.WriteLine($"{name}: the platform could not compress");
            return false;
        }

        ZstdCompressor NewCompressor()
        {
            ZstdCompressor compressor = dictionary is null ? new ZstdCompressor(level) : new ZstdCompressor(level, dictionary);
            compressor.LongDistanceMatching = longDistance;
            return compressor;
        }

        var zstd = NewCompressor();
        NativeReference? native = longDistance ? null : NativeReference.TryLoad();
        var candidates = new List<Candidate>
        {
            new("zstd", (s, o) => { zstd.Compress(s, o, out _, out int w); return w; }),
            new("platform", (s, o) => { platform.Reset(); platform.Compress(s, o, out _, out int w, isFinalBlock: true); return w; }),
        };
        if (native is not null)
        {
            candidates.Add(dictionary is null
                ? new("libzstd-ref", (s, o) => native.Compress(s, o, level))
                : new("libzstd-ref", native.WithDictionary(dictionary, level)));
        }

        if (ab)
        {
            var before = NewCompressor();
            candidates.Insert(1, new("before", (s, o) =>
            {
                AbSwitch.Set(legacy: true);
                before.Compress(s, o, out _, out int w);
                AbSwitch.Set(legacy: false);
                return w;
            }));
        }

        if (only is not null)
        {
            candidates = candidates.Where(c => c.Name == only).ToList();
        }

        string sizes = string.Empty;
        foreach (Candidate candidate in candidates)
        {
            byte[] output = new byte[expected.Length];
            int written = candidate.Decode(content, output);
            sizes += $" {candidate.Name} {written}";
            if (!NoCheck && !output.AsSpan(0, written).SequenceEqual(expected.AsSpan(0, expectedSize)))
            {
                Console.WriteLine($"{name}: {candidate.Name} wrote a different frame ({written} bytes, the platform {expectedSize})");
                return false;
            }
        }

        Time(name, $"{content.Length} bytes at level {level} ->{sizes}", content, new byte[expected.Length], candidates, passes, reps);
        return true;
    }

    /// <summary>
    /// Compression in jobs (<c>-mt&lt;N&gt;</c>): Vorticity.Zstd's CompressAsync at a degree of N, against
    /// libzstd with N workers (the library built with threads), which must write the same frame.
    /// </summary>
    private static bool RunCompressInJobs(string name, string frameName, int workers, int passes, int reps, string? only, bool ab)
    {
        (byte[] content, int level) = BenchFrames.LoadContent(frameName);
        var zstd = new ZstdCompressor(level);
        var candidates = new List<Candidate>
        {
            new("zstd", (s, o) => zstd.CompressAsync(s, o, workers).AsTask().GetAwaiter().GetResult()),
        };
        if (ab)
        {
            var before = new ZstdCompressor(level);
            candidates.Add(new("before", (s, o) =>
            {
                AbSwitch.Set(legacy: true);
                int w = before.CompressAsync(s, o, workers).AsTask().GetAwaiter().GetResult();
                AbSwitch.Set(legacy: false);
                return w;
            }));
        }

        if (NativeReference.TryLoadThreaded() is { } threaded)
        {
            candidates.Add(new("libzstd-mt", threaded.WithWorkers(level, workers)));
        }

        if (only is not null)
        {
            candidates = candidates.Where(c => c.Name == only).ToList();
        }

        byte[] expected = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
        int expectedSize = candidates[^1].Decode(content, expected);
        string sizes = string.Empty;
        foreach (Candidate candidate in candidates)
        {
            byte[] output = new byte[expected.Length];
            int written = candidate.Decode(content, output);
            sizes += $" {candidate.Name} {written}";
            if (!NoCheck && !output.AsSpan(0, written).SequenceEqual(expected.AsSpan(0, expectedSize)))
            {
                Console.WriteLine($"{name}: {candidate.Name} wrote a different frame ({written} bytes, {candidates[^1].Name} {expectedSize})");
                return false;
            }
        }

        Time(name, $"{content.Length} bytes at level {level} with {workers} workers ->{sizes}", content, new byte[expected.Length], candidates, passes, reps);
        return true;
    }

    /// <summary>Times every candidate on the same input, alternated, and prints their statistics.</summary>
    private static void Time(string name, string title, byte[] input, byte[] buffer, List<Candidate> candidates, int passes, int reps)
    {
        byte[] frame = input;
        int count = candidates.Count;
        var samples = new double[count][];
        var passMedians = new double[count][];
        for (int c = 0; c < count; c++)
        {
            samples[c] = new double[passes * reps];
            passMedians[c] = new double[passes];
        }

        // Warm-up: tiering and caches; one round when a round takes seconds.
        bool slow = false;
        for (int i = 0; i < 50 && !slow; i++)
        {
            long round = Stopwatch.GetTimestamp();
            foreach (Candidate candidate in candidates)
            {
                candidate.Decode(frame, buffer);
            }

            slow = Stopwatch.GetElapsedTime(round).TotalSeconds > 1;
        }

        // A sample times enough decodes back to back to last some 50 us: a small frame's single
        // decode is a few timer ticks, too coarse a measure.
        long probe = Stopwatch.GetTimestamp();
        for (int i = 0; i < (slow ? 0 : 10); i++)
        {
            candidates[0].Decode(frame, buffer);
        }

        double probeUs = Stopwatch.GetElapsedTime(probe).TotalMicroseconds / 10;
        int batch = Batch ?? (slow ? 1 : Math.Max(1, (int)(50 / Math.Max(probeUs, 0.01))));
        double tick = 1_000_000.0 / Stopwatch.Frequency / batch;
        for (int p = 0; p < passes; p++)
        {
            var pass = new double[count][];
            for (int c = 0; c < count; c++)
            {
                pass[c] = new double[reps];
            }

            for (int r = 0; r < reps; r++)
            {
                for (int k = 0; k < count; k++)
                {
                    int c = (r + k) % count;
                    Candidate candidate = candidates[c];
                    long start = Stopwatch.GetTimestamp();
                    for (int b = 0; b < batch; b++)
                    {
                        candidate.Decode(frame, buffer);
                    }

                    pass[c][r] = (Stopwatch.GetTimestamp() - start) * tick;
                }
            }

            for (int c = 0; c < count; c++)
            {
                Array.Copy(pass[c], 0, samples[c], p * reps, reps);
                Array.Sort(pass[c]);
                passMedians[c][p] = pass[c][reps / 2];
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{name}: {title}" + (batch > 1 ? $", {batch} runs a sample" : string.Empty));
        Console.WriteLine($"  {"decoder",-12} {"min",9} {"p25",9} {"median",9} {"p75",9}   medians per pass (us)");
        var medians = new double[count];
        for (int c = 0; c < count; c++)
        {
            double[] all = samples[c];
            Array.Sort(all);
            medians[c] = all[all.Length / 2];
            // Two decimals: the small frames take a few microseconds, where a tenth is 4%.
            Console.WriteLine($"  {candidates[c].Name,-12} {all[0],9:F2} {all[all.Length / 4],9:F2} {medians[c],9:F2} {all[3 * all.Length / 4],9:F2}   " +
                string.Join(" ", passMedians[c].Select(m => m.ToString("F2", CultureInfo.InvariantCulture))));
        }

        int zstdIndex = candidates.FindIndex(c => c.Name == "zstd");
        if (zstdIndex >= 0)
        {
            for (int c = 0; c < count; c++)
            {
                if (c != zstdIndex)
                {
                    Console.WriteLine($"  speedup vs {candidates[c].Name}: {medians[c] / medians[zstdIndex]:F3}x " +
                        $"(per pass: {string.Join(" ", Enumerable.Range(0, passes).Select(p => (passMedians[c][p] / passMedians[zstdIndex][p]).ToString("F2", CultureInfo.InvariantCulture)))})");
                }
            }
        }
    }

    /// <summary>
    /// From <c>--no-check</c>: time a decoder whose output differs, which a probe that alters the
    /// decoding on purpose (to find what bounds it) makes.
    /// </summary>
    private static bool NoCheck { get; set; }

    /// <summary>The decodes a sample times, from <c>--batch</c>; by default enough for some 50 us.</summary>
    private static int? Batch { get; set; }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>The legacy switches of the optimization point under work, for <c>--ab</c>.</summary>
internal static class AbSwitch
{
    public static void Set(bool legacy)
    {
        _ = legacy;
    }
}
