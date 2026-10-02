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
/// Usage: <c>Vorticity.Zstd.Perf [--frames a,b] [--passes N] [--reps N] [--only zstd] [--ab] [--dump dir]</c>; <c>--ab</c>
/// adds a "before" candidate that runs with the legacy switches of the point under work (see
/// <see cref="AbSwitch"/>). Before timing,
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

        int passes = int.Parse(Option(args, "--passes") ?? "5", CultureInfo.InvariantCulture);
        int reps = int.Parse(Option(args, "--reps") ?? "300", CultureInfo.InvariantCulture);
        string? only = Option(args, "--only");

        var zstd = new ZstdDecompressor();
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
            if (written != size || !output.AsSpan().SequenceEqual(expected))
            {
                Console.WriteLine($"{name}: {candidate.Name} produced a different output ({written} bytes)");
                return false;
            }
        }

        byte[] buffer = new byte[size];
        int count = candidates.Count;
        var samples = new double[count][];
        var passMedians = new double[count][];
        for (int c = 0; c < count; c++)
        {
            samples[c] = new double[passes * reps];
            passMedians[c] = new double[passes];
        }

        // Warm-up: tiering and caches.
        for (int i = 0; i < 50; i++)
        {
            foreach (Candidate candidate in candidates)
            {
                candidate.Decode(frame, buffer);
            }
        }

        double tick = 1_000_000.0 / Stopwatch.Frequency;
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
                    long start = Stopwatch.GetTimestamp();
                    candidates[c].Decode(frame, buffer);
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
        Console.WriteLine($"{name}: {frame.Length} -> {size} bytes");
        Console.WriteLine($"  {"decoder",-12} {"min",9} {"p25",9} {"median",9} {"p75",9}   medians per pass (us)");
        var medians = new double[count];
        for (int c = 0; c < count; c++)
        {
            double[] all = samples[c];
            Array.Sort(all);
            medians[c] = all[all.Length / 2];
            Console.WriteLine($"  {candidates[c].Name,-12} {all[0],9:F1} {all[all.Length / 4],9:F1} {medians[c],9:F1} {all[3 * all.Length / 4],9:F1}   " +
                string.Join(" ", passMedians[c].Select(m => m.ToString("F1", CultureInfo.InvariantCulture))));
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

        return true;
    }

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
