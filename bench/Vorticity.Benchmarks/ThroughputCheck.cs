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
        ("alp", 0.95),   // 2 runs, spread 0.94-0.95
        ("alp_no_patches", 0.93),   // 2 runs, spread 0.92-0.93
        ("alp_patched_no_chunk_offsets", 1.08),   // 2 runs, spread 0.91-1.08
        ("alprd", 1.25),   // 2 runs, spread 1.24-1.25
        ("bool", 1.00),   // 2 runs, spread 0.97-1.00
        ("bool_bit_offset3", 1.00),   // vectorized validity classify; spread 0.95-1.00
        ("bool_bit_offset7", 1.01),   // 2 runs, spread 0.97-1.01
        ("bool_bit_offset_straddle", 1.01),   // 2 runs, spread 1.00-1.01
        ("bytebool", 1.14),   // 2 runs, spread 1.12-1.14
        ("chunked", 1.34),   // 2 runs, spread 1.33-1.34
        ("chunked_empty_chunks", 1.44),   // 2 runs, spread 1.43-1.44
        ("chunked_one_chunk", 0.42),   // 2 runs, spread 0.40-0.42
        ("constant", 1.10),   // 2 runs, spread 1.06-1.10
        ("datetimeparts", 1.22),   // 2 runs, spread 1.22-1.22
        ("decimal", 0.40),   // 2 runs, spread 0.39-0.40
        ("decimal_byte_parts", 0.39),   // 2 runs, spread 0.31-0.39
        ("dict", 1.04),   // 2 runs, spread 0.95-1.04
        ("dict_nullable_codes", 0.85),   // 2 runs, spread 0.81-0.85
        ("dict_nullable_values_nonnull_codes", 1.77),   // 2 runs, spread 1.76-1.77
        ("dict_u64_codes", 1.18),   // 2 runs, spread 1.11-1.18
        ("dict_u8_codes", 0.99),   // 2 runs, spread 0.95-0.99
        ("ext", 0.45),   // 2 runs, spread 0.34-0.45
        ("fastlanes_bitpacked", 1.38),   // 2 runs, spread 1.35-1.38
        ("fastlanes_bitpacked_patched_no_chunk_offsets", 1.51),   // 2 runs, spread 1.32-1.51
        ("fastlanes_delta", 1.18),   // Undelta by reference; 3 runs, spread 1.12-1.18
        ("fastlanes_for", 1.07),   // 2 runs, spread 1.00-1.07
        ("fastlanes_rle", 0.97),   // 2 runs, spread 0.92-0.97
        ("fixed_size_list", 0.32),   // 2 runs, spread 0.31-0.32
        ("fsst", 1.51),   // 2 runs, spread 1.48-1.51
        ("list", 0.60),   // 2 runs, spread 0.58-0.60
        ("listview", 0.73),   // 2 runs, spread 0.67-0.73
        ("map", 0.16),   // 2 runs, spread 0.15-0.16
        ("masked", 0.53),   // 2 runs, spread 0.49-0.53
        ("masked_all_invalid", 0.52),   // vectorized validity classify; spread 0.49-0.52
        ("masked_all_valid", 0.58),   // vectorized validity classify; spread 0.56-0.58
        ("null", 1.31),   // 2 runs, spread 1.12-1.31
        ("onpair", 1.61),   // 2 runs, spread 1.61-1.61
        ("parquet_variant", 8.11),   // 2 runs, spread 7.69-8.11
        ("pco", 1.35),   // 2 runs, spread 1.32-1.35
        ("primitive", 0.38),   // 2 runs, spread 0.29-0.38
        ("runend", 1.30),   // tile from source, uninitialized output; 3 runs, spread 1.14-1.30
        ("sequence", 1.97),   // 2 runs, spread 1.91-1.97
        ("sparse", 1.04),   // 2 runs, spread 0.95-1.04
        ("struct", 0.08),   // 2 runs, spread 0.07-0.08
        ("varbin", 0.07),   // 2 runs, spread 0.07-0.07
        ("varbinview", 0.08),   // 2 runs, spread 0.07-0.08
        ("variant", 4.89),   // 2 runs, spread 4.57-4.89
        ("zigzag", 1.09),   // 2 runs, spread 0.94-1.09
        ("zstd", 1.27),   // 2 runs, spread 1.25-1.27
        ("zstd_buffers", 0.16),   // 2 runs, spread 0.16-0.16
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
