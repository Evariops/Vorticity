// The benchmark host.
//
// `dotnet run -c Release --project bench/Vorticity.Benchmarks` runs everything;
// `-- --filter '*Decode*'` narrows it, which is what a kernel change wants.
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
using System;
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
        if (args.Length > 0 && args[0] == "--ffi-check")
        {
            return await FfiCheck().ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "--ratio-check")
        {
            return await RatioCheck.RunAsync().ConfigureAwait(false);
        }

        if (args.Length > 0 && args[0] == "--throughput")
        {
            return await ThroughputCheck.RunAsync().ConfigureAwait(false);
        }

        if (args.Length > 1 && args[0] == "--profile")
        {
            double seconds = args.Length > 2 && double.TryParse(
                args[2], System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 10.0;
            return await ProfileScenarios.RunAsync(args[1], seconds).ConfigureAwait(false);
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

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
