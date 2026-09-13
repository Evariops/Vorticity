// ns/value, the axis that gives ABSOLUTES instead of a ranking.
//
// bench/BASELINE.md's per-encoding table says of itself: "this is a ranking, not a measurement".
// The reason is arithmetic. Its files are 4096 rows, and ~35 us of every row in it is the fixed
// open-and-walk cost both implementations pay before a single value is decoded. `fastlanes.bitpacked`
// reads 40.9 us against Rust's 35.4 -- five microseconds of decode under thirty-five of overhead --
// which is why that row moved from 1.26x to 1.18x while its kernel got 3.6x faster. Every ratio in
// that table is pulled toward 1.0 by a constant neither side can avoid, and the smaller the real
// difference the harder it is pulled.
//
// One million rows per file changes the arithmetic rather than the estimator: the fixed cost is
// unchanged, so its SHARE falls by roughly the row ratio, and what is left is the decoder.
//
// THE INPUTS ARE NOT IN THE REPOSITORY, and Corpus.cs already drew that line for real datasets:
// "those are gigabytes that do not belong in a repository". These are 322 MB for 48 encodings.
// Generate them on demand and point this at them:
//
//   cd tools/conformance-gen && cargo run --release -j 6 -- --throughput 1000000 --out /tmp/vxthroughput
//   VORTICITY_THROUGHPUT_CORPUS=/tmp/vxthroughput \
//     dotnet run -c Release --project bench/Vorticity.Benchmarks -- --throughput
//
// They carry NO SIDECAR and nothing asserts a value from them: correctness is the 4096-row corpus's
// job, and these exist only to push the fixed cost below the noise floor.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Vorticity;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

/// <summary>Per-encoding decode throughput in nanoseconds per value.</summary>
internal static class ThroughputCheck
{
    /// <summary>The environment variable naming the generated input directory.</summary>
    private const string Variable = "VORTICITY_THROUGHPUT_CORPUS";

    /// <summary>Wall-clock warm-up per file, for the same reason RatioCheck uses one.</summary>
    private static readonly TimeSpan WarmupBudget = TimeSpan.FromSeconds(1);

    /// <summary>Timed rounds per file. Odd, so the median is an observation.</summary>
    private const int Rounds = 7;

    /// <summary>
    /// The margin over the reference ratio, the same +15% RatioCheck uses and for the same reason:
    /// the ratio is largely but not entirely deterministic, and a tighter margin gates noise.
    /// </summary>
    private const double Margin = 1.15;

    /// <summary>
    /// The ratio each encoding was measured at when the ceiling was last set, on the machine named
    /// in bench/BASELINE.md. LOWER ONE BY HAND when an improvement lands, in the same commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gate the biggest measured gap in the repository never had. bench/BASELINE.md's
    /// per-encoding table is a RANKING at 4096 rows -- ~35 us of fixed open-and-walk cost on both
    /// sides pulls every ratio in it toward 1.0 -- so a decoder could get three times slower and
    /// that table would move by a few percent. These files are a million rows each, which pushes
    /// the fixed cost under a percent and leaves the decoder.
    /// </para>
    /// <para>
    /// A ratio ABOVE the ceiling fails. A ratio far BELOW its reference is reported as STALE rather
    /// than silently accepted: a ratchet that is never lowered stops being a ratchet, and the whole
    /// point of these numbers is that they move down.
    /// </para>
    /// <para>
    /// The three encodings whose ratio is well under 1 (`struct`, `varbin`, `varbinview`) are not
    /// a mistake: upstream's scan of those files materializes far more than ours does. They are
    /// gated all the same, because a regression there is still a regression.
    /// </para>
    /// </remarks>
    private static readonly (string Encoding, double Reference)[] References =
    [
        ("alp", 2.15),   // 2 runs, spread 2.10-2.15
        ("alp_no_patches", 1.41),   // 2 runs, spread 1.37-1.41
        ("alp_patched_no_chunk_offsets", 1.28),   // 2 runs, spread 1.27-1.28
        ("alprd", 3.29),   // 2 runs, spread 2.65-3.29
        ("bool", 1.06),   // 2 runs, spread 1.05-1.06
        ("bool_bit_offset3", 1.26),   // 2 runs, spread 1.14-1.26
        ("bool_bit_offset7", 1.05),   // 2 runs, spread 1.02-1.05
        ("bool_bit_offset_straddle", 1.13),   // 2 runs, spread 1.04-1.13
        ("bytebool", 3.62),   // 2 runs, spread 3.59-3.62
        ("chunked", 1.59),   // 2 runs, spread 1.52-1.59
        ("chunked_empty_chunks", 1.70),   // 2 runs, spread 1.55-1.70
        ("chunked_one_chunk", 1.40),   // 2 runs, spread 1.37-1.40
        ("constant", 1.82),   // 2 runs, spread 1.82-1.82
        ("datetimeparts", 1.40),   // 2 runs, spread 1.34-1.40
        ("decimal", 1.38),   // 2 runs, spread 1.36-1.38
        ("decimal_byte_parts", 1.37),   // 2 runs, spread 1.31-1.37
        ("dict", 1.52),   // 2 runs, spread 1.41-1.52
        ("dict_nullable_codes", 1.03),   // 2 runs, spread 1.02-1.03
        ("dict_nullable_values_nonnull_codes", 3.69),   // 2 runs, spread 3.17-3.69
        ("dict_u64_codes", 1.40),   // 2 runs, spread 1.40-1.40
        ("dict_u8_codes", 1.58),   // 2 runs, spread 1.56-1.58
        ("ext", 1.39),   // 2 runs, spread 1.26-1.39
        ("fastlanes_bitpacked", 1.68),   // 2 runs, spread 1.68-1.68
        ("fastlanes_bitpacked_patched_no_chunk_offsets", 1.60),   // 2 runs, spread 1.59-1.60
        ("fastlanes_delta", 4.24),   // 2 runs, spread 3.70-4.24
        ("fastlanes_for", 1.48),   // 2 runs, spread 1.29-1.48
        ("fastlanes_rle", 1.06),   // 2 runs, spread 1.02-1.06
        ("fixed_size_list", 1.26),   // 2 runs, spread 1.25-1.26
        ("fsst", 1.74),   // 2 runs, spread 1.72-1.74
        ("list", 5.29),   // 2 runs, spread 4.82-5.29
        ("listview", 0.80),   // 2 runs, spread 0.78-0.80
        ("map", 0.19),   // 2 runs, spread 0.18-0.19
        ("masked", 1.32),   // 2 runs, spread 1.31-1.32
        ("masked_all_invalid", 1.95),   // 2 runs, spread 1.92-1.95
        ("masked_all_valid", 1.96),   // 2 runs, spread 1.86-1.96
        ("null", 1.14),   // 2 runs, spread 1.13-1.14
        ("onpair", 4.98),   // 2 runs, spread 4.87-4.98
        ("pco", 3.59),   // 2 runs, spread 3.41-3.59
        ("primitive", 1.37),   // 2 runs, spread 1.33-1.37
        ("runend", 1.83),   // 2 runs, spread 1.64-1.83
        ("sequence", 2.70),   // 2 runs, spread 2.57-2.70
        ("sparse", 1.45),   // 2 runs, spread 1.42-1.45
        ("struct", 0.09),   // 2 runs, spread 0.08-0.09
        ("varbin", 0.17),   // 2 runs, spread 0.15-0.17
        ("varbinview", 0.08),   // 2 runs, spread 0.08-0.08
        ("zigzag", 1.72),   // 2 runs, spread 1.69-1.72
        ("zstd", 1.40),   // 2 runs, spread 1.39-1.40
        ("zstd_buffers", 0.22),   // 2 runs, spread 0.22-0.22
    ];

    /// <summary>
    /// How far under its reference a ratio may sit before it is called stale.
    /// </summary>
    /// <remarks>
    /// Loose, because the references above are a MAX over three runs and the run-to-run spread on
    /// the sub-millisecond encodings is itself 20-25% (`constant` read 10.53, 11.00 and 13.11 with
    /// no code change between them). A tighter threshold would report staleness that is noise.
    /// </remarks>
    private const double StaleBelow = 0.70;

    /// <summary>Measures every generated file and reports ns/value for both readers.</summary>
    /// <param name="check">Whether to hold each ratio to its ceiling and exit non-zero when over.</param>
    /// <returns>0 on success, 1 when an encoding is over its ceiling, 2 when the inputs are absent.</returns>
    internal static async Task<int> RunAsync(bool check, string[] only)
    {
        string? root = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            Console.Error.WriteLine(
                $"{Variable} is unset or does not name a directory.\n" +
                "These inputs are generated, not committed -- 322 MB for 48 encodings. Produce them with:\n" +
                "  cd tools/conformance-gen && cargo run --release -j 6 -- " +
                "--throughput 1000000 --out /tmp/vxthroughput\n" +
                $"then set {Variable} to that directory.");
            return 2;
        }

        bool rust = RustReader.Available;
        if (check && !rust)
        {
            Console.Error.WriteLine(
                $"--check needs the reference: vxbench not found ({RustReader.ExpectedPath}).\n" +
                "Build it with: cd tools/vxbench-rs && cargo build --release");
            return 2;
        }

        string[] files = Directory.GetFiles(root, "*.vortex", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        if (only.Length > 0)
        {
            // Measuring one family takes half a minute where the whole axis takes five, which is
            // the difference between checking a kernel change and not bothering. The gate still
            // runs everything; this narrows the report.
            List<string> kept = [];
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                foreach (string pattern in only)
                {
                    if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        kept.Add(file);
                        break;
                    }
                }
            }

            files = [.. kept];
        }

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"no .vortex files under {root}");
            return 2;
        }

        Console.Out.WriteLine(
            $"THROUGHPUT: median of {Rounds} interleaved rounds after a " +
            $"{WarmupBudget.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s warm-up per file, " +
            $"from {root}");
        if (check)
        {
            Console.Out.WriteLine(
                $"  held to reference x {Margin.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        Console.Out.WriteLine(
            rust
                ? "  encoding                             rows batches       ours       rust   ns/value   rust ns/v   ratio" +
                  (check ? "  reference  ceiling" : string.Empty)
                : "  encoding                             rows batches       ours   ns/value   (no vxbench: absolutes only)");

        List<string> failures = [];
        List<string> stale = [];
        List<string> unreferenced = [];
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            long rows;
            try
            {
                rows = await ScanAll(file).ConfigureAwait(false);
            }
            catch (Exception e) when (e is VortexUnsupportedException or VortexFormatException)
            {
                // An encoding this build cannot read is not a failure of this axis: the corpus
                // coverage test owns that question, and reporting it here would duplicate it badly.
                Console.Out.WriteLine($"  {name,-32} unreadable: {e.GetType().Name}");
                continue;
            }

            if (rows == 0)
            {
                continue;
            }

            long batches = await CountBatches(file).ConfigureAwait(false);
            (double ours, double theirs) = await MeasureAsync(file, rust).ConfigureAwait(false);
            double nsPerValue = ours * 1000.0 / rows;

            if (rust && theirs > 0)
            {
                double rustNs = theirs * 1000.0 / rows;
                double ratio = ours / theirs;
                string suffix = string.Empty;
                if (check)
                {
                    double? reference = ReferenceFor(name);
                    if (reference is null)
                    {
                        unreferenced.Add(string.Create(
                            CultureInfo.InvariantCulture,
                            $"        (\"{name}\", {ratio:F2}),"));
                        suffix = "         --       --   NO REF";
                    }
                    else
                    {
                        double ceiling = reference.Value * Margin;
                        suffix = string.Create(
                            CultureInfo.InvariantCulture,
                            $" {reference.Value,10:F2} {ceiling,8:F2}");
                        if (ratio > ceiling)
                        {
                            suffix += "   OVER";
                            failures.Add(string.Create(
                                CultureInfo.InvariantCulture,
                                $"  {name}: {ratio:F2} over the {ceiling:F2} ceiling ({reference.Value:F2} x {Margin:F2})."));
                        }
                        else if (ratio < reference.Value * StaleBelow)
                        {
                            suffix += "   STALE";
                            stale.Add(string.Create(
                                CultureInfo.InvariantCulture,
                                $"  {name}: {ratio:F2} against a {reference.Value:F2} reference -- lower it."));
                        }
                    }
                }

                Console.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {name,-32} {rows,10} {batches,7} {ours,9:F0}us {theirs,9:F0}us {nsPerValue,10:F2} {rustNs,11:F2} {ratio,7:F2}{suffix}"));
            }
            else
            {
                Console.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {name,-32} {rows,10} {batches,7} {ours,9:F0}us {nsPerValue,10:F2}"));
            }
        }

        if (!rust)
        {
            Console.Out.WriteLine(
                $"\nvxbench not found ({RustReader.ExpectedPath}); the ours column is an absolute " +
                "and there is no ratio. Build it with: cd tools/vxbench-rs && cargo build --release");
        }

        if (unreferenced.Count > 0)
        {
            Console.Out.WriteLine(
                $"\n{unreferenced.Count} encoding(s) have no reference. Add these lines to " +
                "ThroughputCheck.References, having checked the machine is quiet:");
            foreach (string line in unreferenced)
            {
                Console.Out.WriteLine(line);
            }
        }

        foreach (string line in stale)
        {
            Console.Out.WriteLine(line);
        }

        foreach (string failure in failures)
        {
            Console.Error.WriteLine(failure);
        }

        if (!check)
        {
            return 0;
        }

        Console.Out.WriteLine(
            failures.Count == 0
                ? "Every encoding is inside its ceiling."
                : $"{failures.Count} encoding(s) above ceiling. A ratio only moves when the code " +
                  "moves: find the change, do not raise the ceiling.");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The reference ratio for one encoding, or null when it has none yet.</summary>
    /// <param name="encoding">The file's base name.</param>
    private static double? ReferenceFor(string encoding)
    {
        foreach ((string name, double reference) in References)
        {
            if (string.Equals(name, encoding, StringComparison.Ordinal))
            {
                return reference;
            }
        }

        return null;
    }

    private static async Task<(double Ours, double Theirs)> MeasureAsync(string path, bool rust)
    {
        long deadline = Stopwatch.GetTimestamp() +
            (long)(WarmupBudget.TotalSeconds * Stopwatch.Frequency);
        do
        {
            await ScanAll(path).ConfigureAwait(false);
            if (rust)
            {
                RustReader.ScanCanonical(path);
            }
        }
        while (Stopwatch.GetTimestamp() < deadline);

        double[] mine = new double[Rounds];
        double[] theirs = new double[Rounds];
        for (int round = 0; round < Rounds; round++)
        {
            // Alternating, for RatioCheck's reason: a fixed order gives one side the cache-cold
            // cost every round, which is a stable bias and therefore worse than noise.
            if (round % 2 == 0)
            {
                mine[round] = await TimeAsync(path).ConfigureAwait(false);
                theirs[round] = rust ? Time(path) : 0;
            }
            else
            {
                theirs[round] = rust ? Time(path) : 0;
                mine[round] = await TimeAsync(path).ConfigureAwait(false);
            }
        }

        return (Median(mine), Median(theirs));
    }

    private static async Task<double> TimeAsync(string path)
    {
        long start = Stopwatch.GetTimestamp();
        await ScanAll(path).ConfigureAwait(false);
        return Stopwatch.GetElapsedTime(start).TotalMicroseconds;
    }

    private static double Time(string path)
    {
        long start = Stopwatch.GetTimestamp();
        RustReader.ScanCanonical(path);
        return Stopwatch.GetElapsedTime(start).TotalMicroseconds;
    }

    private static double Median(double[] values)
    {
        double[] copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    private static async Task<long> ScanAll(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
        }

        return rows;
    }

    /// <summary>
    /// Batches the scan emits, reported beside the timing because it is what explains it.
    /// </summary>
    /// <param name="path">The file to count.</param>
    /// <remarks>
    /// A file written as ONE chunk of a million rows is read as ~977 batches, and this axis exists
    /// because that ratio is where the cost lives. Reporting the count turns "the number is large"
    /// into "the number is large FOR THIS REASON".
    /// </remarks>
    private static async Task<long> CountBatches(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long batches = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            batches++;
        }

        return batches;
    }
}
