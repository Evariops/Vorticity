using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using Vorticity.Zstd.Bench;

namespace Vorticity.Zstd.Perf;

/// <summary>
/// Paired, alternated timings of the three decoders in one Native AOT process: Vorticity.Zstd, the
/// platform's <see cref="ZstandardDecoder"/>, and libzstd 1.5.7 freshly compiled. Each repetition
/// times every candidate once, in an order that rotates, so that whatever the machine does at that
/// moment lands on all of them alike. Several passes show how stable the figures are.
/// </summary>
/// <remarks>
/// Usage: <c>Vorticity.Zstd.Perf [--frames a,b] [--passes N] [--reps N] [--only zstd,libzstd-ref] [--ab] [--dump dir] [--no-check] [--no-pair | --pair]
/// [--pmu default|EV1,EV2] [--compress] [--core N]</c>; <c>--pmu</c> counts hardware events per decode and sequence (macOS, under
/// sudo); <c>--core N</c> keeps the timed thread on logical processor N (Windows, where the process and
/// the thread also run at a high priority, as macOS's user-interactive QoS); <c>--ab</c>
/// adds a "before" candidate that runs with the legacy switches of the point under work (see
/// <see cref="AbSwitch"/>). <c>--compress</c> times compression instead: each frame's content, at the
/// level its name gives (3 for the reference). Before timing,
/// every candidate's output is checked against the platform's. <c>--corpus silesia,github,github-dict</c>
/// measures zstd's own benchmark data instead, level by level (see <see cref="Corpus"/>).
/// </remarks>
public static class Program
{
    private const int QosClassUserInteractive = 0x21;

    private sealed record Candidate(string Name, Func<byte[], byte[], int> Decode);

    private const int ThreadPriorityHighest = 2;

    [DllImport("libSystem.dylib")]
    private static extern int pthread_set_qos_class_self_np(int qosClass, int relativePriority);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(nint thread, int priority);

    [DllImport("kernel32.dll")]
    private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);

    public static int Main(string[] args)
    {
        // The timed thread at the user-interactive QoS: kept on the performance cores at their full
        // clock, which a default thread is not when other processes compete.
        if (OperatingSystem.IsMacOS())
        {
            _ = pthread_set_qos_class_self_np(QosClassUserInteractive, 0);
        }
        else if (OperatingSystem.IsWindows())
        {
            // The same on Windows: the process and the timed thread at a high priority, and with
            // --core N the thread kept on logical processor N, which no migration then disturbs.
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            _ = SetThreadPriority(GetCurrentThread(), ThreadPriorityHighest);
            if (Option(args, "--core") is string core)
            {
                _ = SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << int.Parse(core, CultureInfo.InvariantCulture));
            }
        }

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

        if (Option(args, "--corpus") is string corpora)
        {
            // zstd's own benchmark data, level by level: bench/zstd-corpus.sh downloads it.
            return Corpus.Run(corpora.Split(','), Option(args, "--levels"), Option(args, "--seconds"), Option(args, "--only"),
                Option(args, "--results"), Option(args, "--markdown"), Option(args, "--directions"));
        }

        if (Option(args, "--corpus-report") is string results)
        {
            return Corpus.Report(results, Option(args, "--markdown"));
        }

        if (Option(args, "--micro") is string micro)
        {
            PcSampler.Path = Option(args, "--pcprofile");
            PcSampler.Function = Option(args, "--pcfunc");
            PcSampler.Callers = Option(args, "--pccaller");
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

        // --no-pair: one block at a time, --pair: two, to measure what pairing them brings; by
        // default, as the decoder chooses for the machine.
        var zstd = new ZstdDecompressor();
        if (args.Contains("--no-pair") || args.Contains("--pair"))
        {
            zstd.PairsBlocks = args.Contains("--pair");
        }
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
            candidates = candidates.Where(c => only.Split(',').Contains(c.Name)).ToList();
        }

        Console.WriteLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
            $"AOT: {!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}, passes {passes} x {reps} reps");
        foreach (string name in frames)
        {
            if (!(name.EndsWith("-dict", StringComparison.Ordinal) ? RunWithDictionary(name, passes, reps, only) : Run(name, candidates, passes, reps)))
            {
                return 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Decompression with a dictionary (a name ending in <c>-dict</c>): the frame's content
    /// compressed by the platform with a dictionary of its kind (<see cref="BenchFrames.Dictionary"/>)
    /// prepared at its level, decoded by every candidate given that dictionary.
    /// </summary>
    private static bool RunWithDictionary(string name, int passes, int reps, string? only)
    {
        string frameName = name[..^5];
        (byte[] content, int level) = BenchFrames.LoadContent(frameName);
        byte[] dictionary = BenchFrames.Dictionary(frameName);
        using ZstandardDictionary prepared = ZstandardDictionary.Create(dictionary, level);
        byte[] buffer = new byte[ZstdCompressor.GetMaxCompressedLength(content.Length)];
        using (var encoder = new ZstandardEncoder(new ZstandardCompressionOptions { Quality = level, Dictionary = prepared }))
        {
            encoder.Compress(content, buffer, out _, out int written, isFinalBlock: true);
            buffer = buffer.AsSpan(0, written).ToArray();
        }

        var zstd = new ZstdDecompressor(dictionary);
        var platform = new ZstandardDecoder(prepared);
        var candidates = new List<Candidate>
        {
            new("zstd", (f, o) => { zstd.Decompress(f, o, out _, out int w); return w; }),
            new("platform", (f, o) => { platform.Reset(); platform.Decompress(f, o, out _, out int w); return w; }),
        };
        if (NativeReference.TryLoad() is { } native)
        {
            SpanCodec decode = native.DecompressorWith(dictionary);
            candidates.Add(new("libzstd-ref", (f, o) => decode(f, o)));
        }

        if (only is not null)
        {
            candidates = candidates.Where(c => only.Split(',').Contains(c.Name)).ToList();
        }

        foreach (Candidate candidate in candidates)
        {
            byte[] output = new byte[content.Length];
            int written = candidate.Decode(buffer, output);
            if (!NoCheck && (written != content.Length || !output.AsSpan().SequenceEqual(content)))
            {
                Console.WriteLine($"{name}: {candidate.Name} produced a different output ({written} bytes)");
                return false;
            }
        }

        Time(name, $"{buffer.Length} -> {content.Length} bytes, with a dictionary of {dictionary.Length} bytes", buffer, new byte[content.Length], candidates, passes, reps);
        return true;
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
            // The same compressor, its tables in the same pages: the two differ only by the code
            // that runs, not by where the tables landed in the caches.
            candidates.Insert(1, new("before", (s, o) =>
            {
                AbSwitch.Set(legacy: true);
                zstd.Compress(s, o, out _, out int w);
                AbSwitch.Set(legacy: false);
                return w;
            }));
        }

        if (only is not null)
        {
            candidates = candidates.Where(c => only.Split(',').Contains(c.Name)).ToList();
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
            candidates = candidates.Where(c => only.Split(',').Contains(c.Name)).ToList();
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
