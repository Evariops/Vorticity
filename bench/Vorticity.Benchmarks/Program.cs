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

        if (args.Length > 0 && args[0] is "--help" or "-h")
        {
            Usage();
            return 0;
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

            // `--lanes N` swallows its count the way `--recalibrate N` does, or the number would
            // arrive as an axis filter and match nothing.
            int lanesFlag = Array.IndexOf(args, "--lanes");
            RatioCheck.Lanes = lanesFlag >= 0 && lanesFlag + 1 < args.Length &&
                int.TryParse(args[lanesFlag + 1], CultureInfo.InvariantCulture, out int lanes)
                ? lanes
                : 0;

            string[] axes =
            [
                .. args.Where((a, i) =>
                    i > 0 && i != counted && i != lanesFlag + 1 &&
                    !a.StartsWith("--", StringComparison.Ordinal))
            ];
            bool rebase = Array.IndexOf(args, "--rebase") >= 0;
            bool abSame = Array.IndexOf(args, "--ab-same") >= 0;
            bool onePass = Array.IndexOf(args, RatioCheck.PassFlag) >= 0;
            return await RatioCheck.RunAsync(axes, recalibrate, rebase, abSame, onePass)
                .ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "--rewrite")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(
                    "--rewrite <in> <out> [edition] [--row-block N] [--data-block-bytes N|off]: " +
                    "reads a file with our reader and writes it back with our writer, keeping the " +
                    "bytes. An edition name (Core20250500 …) excludes the encodings that came " +
                    "later (BENCH-AUDIT.md A5). The two block knobs decide the file's chunking, " +
                    "and a zone is a chunk, so they decide what a selective scan costs (B12): " +
                    "`--data-block-bytes off` writes chunks of exactly one row block.");
                return 2;
            }

            Vorticity.Editions.VortexEdition? edition =
                args.Length > 3 && Enum.TryParse(args[3], out Vorticity.Editions.VortexEdition e)
                    ? e
                    : null;

            int rowBlockFlag = Array.IndexOf(args, "--row-block");
            int? rowBlock = rowBlockFlag >= 0 && rowBlockFlag + 1 < args.Length &&
                int.TryParse(args[rowBlockFlag + 1], out int rows)
                    ? rows
                    : null;

            int bytesFlag = Array.IndexOf(args, "--data-block-bytes");
            long? dataBlockBytes = null;
            if (bytesFlag >= 0 && bytesFlag + 1 < args.Length)
            {
                string value = args[bytesFlag + 1];
                dataBlockBytes = value.Equals("off", StringComparison.OrdinalIgnoreCase)
                    ? RatioCheck.Off
                    : long.TryParse(value, out long bytes) ? bytes : null;
            }

            await RatioCheck.RewriteAsync(args[1], args[2], edition, rowBlock, dataBlockBytes)
                .ConfigureAwait(false);
            Console.Out.WriteLine(
                $"{args[2]}: {new System.IO.FileInfo(args[2]).Length} bytes from " +
                $"{new System.IO.FileInfo(args[1]).Length}.");
            return 0;
        }

        if (args.Length > 0 && args[0] == "--ab")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(
                    "--ab <before-directory> <file> [scenario…]: two builds of the library in one " +
                    "process (BENCH-AUDIT.md C1). bench/ab.sh <commit> builds the other side.");
                return 2;
            }

            int afterFlag = Array.IndexOf(args, "--after");
            string? after = afterFlag >= 0 && afterFlag + 1 < args.Length
                ? args[afterFlag + 1]
                : null;
            string[] scenarios =
            [
                .. args.Where((a, i) =>
                    i >= 3 && i != afterFlag + 1 && !a.StartsWith("--", StringComparison.Ordinal))
            ];
            return await AbCheck.RunAsync(args[1], after, args[2], scenarios).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "--compare")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine(
                    "--compare <base> <diff>: two *-report-full.json files, or two directories " +
                    "holding them (BENCH-AUDIT.md C2).");
                return 2;
            }

            return Compare.Run(args[1], args[2]);
        }

        if (args.Length > 0 && args[0] == "--throughput")
        {
            bool check = Array.IndexOf(args, "--check") >= 0;
            ThroughputCheck.Quick = Array.IndexOf(args, "--quick") >= 0;
            ThroughputCheck.Axis =
                Array.IndexOf(args, "--take") >= 0 ? ThroughputCheck.Workload.Take
                : Array.IndexOf(args, "--write") >= 0 ? ThroughputCheck.Workload.Write
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
                check,
                only,
                tpPasses,
                Array.IndexOf(args, "--rebase") >= 0,
                Array.IndexOf(args, "--hold") >= 0).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "--tree")
        {
            // The shape of the two boundary rules of 13 §13.J, side by side.
            int[] sizes = [.. args[1..]
                .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                .Select(a => int.TryParse(a, CultureInfo.InvariantCulture, out int size) ? size : 0)
                .Where(size => size > 0)];
            return await TreeBench.RunAsync(sizes).ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "--probe")
        {
            // The decomposition of WRITE-ARCHITECTURE.md §1.2: one line per file, five columns.
            string[] probed = [.. args[1..].Where(a => !a.StartsWith("--", StringComparison.Ordinal))];
            return await WriteProbe.RunAsync(probed, CancellationToken.None).ConfigureAwait(false);
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
        BenchmarkConfig.InProcess = Array.IndexOf(args, "--inprocess") >= 0;
        string[] forwarded =
            [.. args.Where(a => a is not ("--full" or "--explore" or "--inprocess"))];

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

    /// <summary>Prints what this project can be asked to do.</summary>
    /// <remarks>
    /// BENCH-AUDIT.md E4. `--help` reached BenchmarkDotNet's own help, which describes `--job` and
    /// `--wasmEngine` and not one of the six modes this project actually has -- so a developer
    /// asking the obvious question was told about a tool they were not using. This answers first;
    /// BenchmarkDotNet's options are still forwarded and still work.
    ///
    /// A `switch` with a usage rather than `System.CommandLine`: a NuGet dependency taken on for a
    /// help text, in a repository whose first line is "dependency-free", would cost more than the
    /// problem.
    /// </remarks>
    private static void Usage() => Console.Out.Write(
        """
        dotnet run -c Release --project bench/Vorticity.Benchmarks -- [mode] [options]

        MODES, decided by the first argument:

          (nothing)                every class, fast profile, ~2 min
          <word> [<word>…]         bare words become a filter: `fsst` is `--filter *fsst*`
          --ratio-check [axis…]    16 axes, ours over the reference, one clock, ~35 s
                                     NEVER under DOTNET_TieredCompilation=0: the pin costs our
                                     side dynamic PGO and the native reference nothing, which
                                     turns nine green axes red. ab.sh pins both its sides and
                                     says why; a ratio against native code must not.
                                     --recalibrate N   N processes, prints the table to paste
                                     --rebase          let a reference rise, only where k moved
                                     --ab-same         with --rebase, let it rise where ab.sh
                                                       reports no change in our own time
                                     --lanes N         adds `full scan, N lanes`, threads pinned
          --throughput [family…]   50 encodings at a million rows, ~55 s
                                     --check           hold each ratio to its ceiling. Refused
                                                       with a family filter: a short run is +32%
                                                       on our side (BENCH-AUDIT.md B8)
                                     --quick           23 s instead of 70; a direction
                                     --take            64 rows spread over each file
                                     --write           read back out to a discarding sink, ~8 min
                                     --recalibrate N   as above
                                     --hold            with --recalibrate, print every reference
                                                       back unchanged and refresh only the
                                                       dispersion each encoding measured
          --ffi-check              rows AND decoded values agree with the reference, < 1 s
          --probe [name…]          the write, decomposed: scan, serialize, transit, compress,
                                     five configurations a file, median of five
                                     (WRITE-ARCHITECTURE.md §1.2)
          --tree [count…]          the dataset tree's shape under both boundary rules: fan-out,
                                     page bytes, pages written and read per commit
                                     (docs/13-dataset.md §13.J, §14)
          --profile <name> [secs]  one scenario in a bare loop, for dotnet-trace
          --ab <dir> <file> [name…] two builds of the library in one process
                                     --after <dir>     judge a commit against its parent, not HEAD
          --compare <base> <diff>  two recorded runs, Mann-Whitney per case
          --help                   this

        PROFILE FLAGS, anywhere:

          --full        the reference profile, OUT of process: slower, isolated, allows --disasm
          --inprocess   keep --full in this process
          --explore     also run the `explore` classes, which are curves rather than gates

        Everything else is forwarded to BenchmarkDotNet: --filter, --anyCategories, --list flat,
        --artifacts <dir>, --disasm (needs --full). Scripts: bench/gate.sh, bench/compare.sh,
        bench/ab.sh, bench/gen-throughput.sh. One page: bench/README.md.

        """);

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

            // THE ROW COUNT IS NOT THE CHECK, it is the cheap half of it (BENCH-AUDIT.md A3). A
            // lazy scan answers `len()` from metadata without decoding a byte, which is how a
            // ratio came to compare a decode against an absence of one. The checksum folds every
            // decoded VALUE in file order on both sides, by one encoding written down in
            // Checksum.cs and in vxbench's `vxbench_scan_checksum`.
            long ourSum = await Checksum.OfFileAsync(path).ConfigureAwait(false);
            long theirSum = RustReader.ScanChecksum(path);
            bool counts = ours == theirs && ours == footer;
            bool values = ourSum == theirSum;
            string verdict = counts && values ? "ok" : !counts ? "ROWS DIFFER" : "VALUES DIFFER";
            if (verdict != "ok")
            {
                mismatches++;
            }

            // Batch counts are REPORTED, not asserted: the split strategy is a reader choice, not
            // a format rule, and the two are allowed to differ. What is not allowed is quoting a
            // time-to-first-batch ratio without saying that the batches are different sizes.
            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {id,-42} rows ours={ours,7} rust={theirs,7}  batches ours={ourBatches,3} " +
                $"rust={theirBatches,3}  values {(values ? "match" : $"{ourSum:x16} vs {theirSum:x16}")}" +
                $"  {verdict}"));
        }

        Console.Out.WriteLine(
            mismatches == 0
                ? "Both readers agree on every row AND every value; the ratio is meaningful."
                : $"{mismatches} files disagree; a ratio over them would be meaningless.");
        return mismatches == 0 ? 0 : 1;
    }
}
