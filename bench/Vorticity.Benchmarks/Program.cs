// The benchmark host.
//
// `dotnet run -c Release --project bench/Vorticity.Benchmarks` runs everything in the FAST profile
// (BenchmarkConfig.cs says what that is and what it costs in fidelity); `-- --filter '*Fsst*'`
// narrows it to one class, which is what a kernel change wants, and `-- fsst` is the same thing
// without the quoting -- a bare word becomes `--filter '*word*'`; `-- --full` selects the reference
// profile, for the one class whose number is about to be written down; `-- --explore` adds back the
// CURVES, which are exploration rather than guards and are out of the default run. Selection by
// category -- `-- --anyCategories kernel` -- is BenchmarkDotNet's own, and works because every class
// is labelled `kernel`, `path` or `explore`; this file refuses to run if one is not.
//
// bench/README.md is the one-page version of all of it.
//
// `-- --ffi-check` is not a benchmark: it verifies that the native comparison harness is present
// and that both implementations AGREE on what they read. A ratio between two readers that return
// different row counts is not a ratio, and BenchmarkDotNet would report it just as confidently.
//
// `-- --profile <scenario> [seconds]` is neither: a bare loop for `dotnet-trace` to sample, with no
// benchmark harness in the profile. See ProfileScenarios.cs.
//
// `-- --ratio-check` is the gate rather than the report: the same axes, interleaved against one
// clock, each held to a ceiling. It exits non-zero when one is over, so CI can run it. See
// RatioCheck.cs for why it does its own timing instead of asserting on a BenchmarkDotNet result.
//
// `-- --throughput` is the per-encoding axis at a million rows, where the fixed open-and-walk cost
// is under a percent instead of most of the measurement; `-- --throughput --check` turns it into
// the same kind of gate, per encoding. That is where the largest measured gaps in this repository
// live, and until the gate existed nothing defended them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Running;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Benchmarks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (UnoptimizedAssemblies() is { Length: > 0 } unoptimized)
        {
            Console.Error.WriteLine(
                $"Built without optimizations: {string.Join(", ", unoptimized)}.\n" +
                "A Debug build measures the JIT's unoptimized output, which is not the code that " +
                "ships and not a number worth writing down. Rebuild with -c Release:\n" +
                "  dotnet run -c Release --project bench/Vorticity.Benchmarks -- " +
                string.Join(" ", args));
            return 2;
        }

        if (args.Length > 0 && args[0] == "--ffi-check")
        {
            return await FfiCheck().ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "--ratio-check")
        {
            // `--recalibrate` takes an optional count: `--recalibrate 5`, or bare for three. The
            // count is swallowed only when it parses as one, so `--recalibrate full scan` keeps
            // `full` as an axis filter rather than eating it.
            int recalibrate = 0;
            int counted = -1;
            int flag = Array.IndexOf(args, "--recalibrate");
            if (flag >= 0)
            {
                int passes = 0;
                bool given = flag + 1 < args.Length &&
                    int.TryParse(args[flag + 1], CultureInfo.InvariantCulture, out passes) &&
                    passes > 0;
                recalibrate = given ? passes : 3;
                counted = given ? flag + 1 : -1;
            }

            string[] axes =
            [
                .. args.Where((a, i) =>
                    i > 0 && i != counted && !a.StartsWith("--", StringComparison.Ordinal))
            ];
            bool rebase = Array.IndexOf(args, "--rebase") >= 0;
            bool onePass = Array.IndexOf(args, RatioCheck.PassFlag) >= 0;
            return await RatioCheck.RunAsync(axes, recalibrate, rebase, onePass).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "--throughput")
        {
            bool check = Array.IndexOf(args, "--check") >= 0;
            ThroughputCheck.Quick = Array.IndexOf(args, "--quick") >= 0;
            ThroughputCheck.Axis = Array.IndexOf(args, "--take") >= 0
                ? ThroughputCheck.Workload.Take
                : ThroughputCheck.Workload.Scan;
            int tpFlag = Array.IndexOf(args, "--recalibrate");
            int tpPasses = 0;
            int tpCounted = -1;
            if (tpFlag >= 0)
            {
                int n = 0;
                bool given = tpFlag + 1 < args.Length &&
                    int.TryParse(args[tpFlag + 1], CultureInfo.InvariantCulture, out n) && n > 0;
                tpPasses = given ? n : 3;
                tpCounted = given ? tpFlag + 1 : -1;
            }

            string[] only =
            [
                .. args.Where((a, i) =>
                    i > 0 && i != tpCounted && !a.StartsWith("--", StringComparison.Ordinal))
            ];
            return await ThroughputCheck.RunAsync(
                check, only, tpPasses, Array.IndexOf(args, "--rebase") >= 0).ConfigureAwait(false);
        }

        if (args.Length > 1 && args[0] == "--profile")
        {
            double seconds = args.Length > 2 && double.TryParse(
                args[2], System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 10.0;
            return await ProfileScenarios.RunAsync(args[1], seconds).ConfigureAwait(false);
        }

        // `--full` is ours, not BenchmarkDotNet's: consumed here, and read by every BenchmarkConfig
        // the [Config] attributes instantiate afterwards.
        BenchmarkConfig.Full = Array.IndexOf(args, "--full") >= 0;
        BenchmarkConfig.Exploring = Array.IndexOf(args, "--explore") >= 0;
        string[] forwarded = [.. args.Where(a => a is not ("--full" or "--explore"))];

        if (UncategorizedClasses() is { Length: > 0 } uncategorized)
        {
            Console.Error.WriteLine(
                $"These benchmark classes carry none of [{string.Join(", ", BenchmarkConfig.Categories)}]: " +
                $"{string.Join(", ", uncategorized)}.\n" +
                "A class without a category still runs, but `--anyCategories kernel` would not find " +
                "it. Add [BenchmarkCategory(BenchmarkConfig.Kernel)] or Path, or Explore for a curve.");
            return 2;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(BareWordsToFilters(forwarded));
        return 0;
    }

    /// <summary>
    /// The selection arguments, from the two shorthands this project adds to BenchmarkDotNet's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `-- fsst` means `--filter '*fsst*'`. The three glob stars are the whole reason it exists: the
    /// shell eats them unquoted, and a developer who wants one class should not have to remember
    /// that. A word is BARE when nothing that looks like an option precedes it -- which is what keeps
    /// `--list flat` and `--filter '*Fsst*'` intact, their values being preceded by their option.
    /// Our own two flags are stripped before this runs, so `--full FastLanes` arrives as a bare word.
    /// </para>
    /// <para>
    /// And NO argument means every benchmark, not a prompt. Left alone, BenchmarkSwitcher asks which
    /// class to run and reads the answer from the console, which makes `dotnet run -c Release PROJ`
    /// -- the command BENCH-AUDIT.md §4.3 calls the default run, and the one a script or a CI job
    /// would use -- do nothing at all when stdin is not a terminal. The fast profile exists so that
    /// running everything is the cheap thing to do; it should also be the thing that happens.
    /// </para>
    /// </remarks>
    private static string[] BareWordsToFilters(string[] args)
    {
        List<string> result = [];
        List<string> words = [];
        for (int i = 0; i < args.Length; i++)
        {
            bool precededByOption = i > 0 && args[i - 1].StartsWith('-');
            if (!args[i].StartsWith('-') && !precededByOption)
            {
                words.Add($"*{args[i]}*");
            }
            else
            {
                result.Add(args[i]);
            }
        }

        if (words.Count == 0 && !result.Any(a => Selectors.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            words.Add("*");
        }

        if (words.Count > 0)
        {
            result.Add("--filter");
            result.AddRange(words);
        }

        return [.. result];
    }

    /// <summary>
    /// The arguments that already say what to run, or that print instead of running. One of these
    /// present is what stops <see cref="BareWordsToFilters"/> from adding `--filter *`.
    /// </summary>
    private static readonly string[] Selectors =
        ["--filter", "-f", "--categories", "--anyCategories", "--allCategories", "--attribute",
         "--list", "--help", "-h", "--info", "--version"];

    /// <summary>
    /// The classes that hold a `[Benchmark]` but none of <see cref="BenchmarkConfig.Categories"/>.
    /// Checked rather than trusted because the failure is silent in the other direction: the run with
    /// no argument selects by EXCLUDING `explore`, so an unlabelled class runs and only its
    /// `--anyCategories` selection quietly returns nothing.
    /// </summary>
    /// <summary>
    /// The assemblies under measurement that were built without optimizations, by simple name.
    /// </summary>
    /// <remarks>
    /// BenchmarkDotNet HAS this check and it works here -- `ConfigOptions.DisableOptimizationsValidator`
    /// had turned it off, and turning it back on makes a Debug `--filter` run refuse. It is not
    /// enough on its own for two reasons. It prints its complaint and still exits 0, so a CI step
    /// would pass; and it only ever sees the BenchmarkDotNet path, while `--ratio-check`,
    /// `--throughput` and `--profile` -- the GATES, the numbers that get committed -- run outside
    /// it entirely. A Debug `--ratio-check` does not refuse: it measures an unoptimized reader
    /// against an optimized Rust one and exits 1, which reads exactly like a regression.
    ///
    /// `DebuggableAttribute.IsJITOptimizerDisabled` is the same signal BenchmarkDotNet reads. The
    /// attribute is absent altogether on an optimized assembly built without a debug type, so its
    /// absence means optimized.
    /// </remarks>
    private static string[] UnoptimizedAssemblies() =>
    [
        .. new[]
            {
                typeof(Program).Assembly,
                typeof(VortexFile).Assembly,
                typeof(Vorticity.RowEncoding.RowSortField).Assembly,
            }
            .DistinctBy(a => a.GetName().Name, StringComparer.Ordinal)
            .Where(a => a.GetCustomAttribute<DebuggableAttribute>() is { IsJITOptimizerDisabled: true })
            .Select(a => a.GetName().Name ?? "?")
            .Order(StringComparer.Ordinal),
    ];

#pragma warning disable IL2026, IL2070 // The benchmark host is never trimmed: BenchmarkSwitcher
                                       // reflects over this same assembly to find the classes at all.
    private static string[] UncategorizedClasses() =>
    [
        .. typeof(Program).Assembly.GetTypes()
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes(
                typeof(BenchmarkDotNet.Attributes.BenchmarkAttribute), inherit: true).Length > 0))
            .Where(t => t.GetCustomAttributes(
                    typeof(BenchmarkDotNet.Attributes.BenchmarkCategoryAttribute), inherit: true)
                .Cast<BenchmarkDotNet.Attributes.BenchmarkCategoryAttribute>()
                .SelectMany(a => a.Categories)
                .All(c => !BenchmarkConfig.Categories.Contains(c, StringComparer.OrdinalIgnoreCase)))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal),
    ];
#pragma warning restore IL2026, IL2070

    private static async Task<int> FfiCheck()
    {
        if (!RustReader.Available)
        {
            Console.Error.WriteLine(
                $"vxbench not found (looked for {RustReader.ExpectedPath}).\n" +
                "Build it with: cd tools/vxbench-rs && cargo build --release");
            return 2;
        }

        Console.Out.WriteLine("vxbench loaded.");
        string[] ids =
        [
            "containers/zoned_many_zones_nulls",
            "containers/uncompressed_canonical",
            "distributions/high_cardinality_i64_r8193",
            "types/utf8_nullable_r1025",
            "encodings/dict",
            "encodings/runend",
            "encodings/fastlanes_bitpacked",
        ];

        int mismatches = 0;
        foreach (string id in ids)
        {
            string path = Corpus.Path(id);
            long ours = 0;
            long ourBatches = 0;
            await using (VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None))
            {
                await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    ours += batch.RowCount;
                    ourBatches++;
                }
            }

            long theirs = RustReader.ScanAll(path);
            long footer = RustReader.OpenOnly(path);
            long theirBatches = RustReader.BatchCount(path);
            string verdict = ours == theirs && ours == footer ? "ok" : "MISMATCH";
            if (verdict != "ok")
            {
                mismatches++;
            }

            // Batch counts are REPORTED, not asserted: the split strategy is a reader choice, not
            // a format rule, and the two are allowed to differ. What is not allowed is quoting a
            // time-to-first-batch ratio without saying that the batches are different sizes.
            Console.Out.WriteLine(
                $"  {id,-42} rows ours={ours,7} rust={theirs,7}  batches ours={ourBatches,3} " +
                $"rust={theirBatches,3}  {verdict}");
        }

        Console.Out.WriteLine(
            mismatches == 0
                ? "Both readers agree on every file; the ratio is meaningful."
                : $"{mismatches} files disagree; a ratio over them would be meaningless.");
        return mismatches == 0 ? 0 : 1;
    }
}
