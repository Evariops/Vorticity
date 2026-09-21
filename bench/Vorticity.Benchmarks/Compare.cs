// Two BenchmarkDotNet runs, compared case by case instead of by eye.
//
// The only exporter was the markdown table, which carries a mean and a standard deviation.
// Deciding whether a change moved a number therefore meant reading two tables side by side and
// doing arithmetic in your head -- the failure the `MannWhitney(5%)` column fixed inside one run,
// here across runs.
//
// NOT `ResultsComparer` FROM dotnet/performance, the usual tool for this. It lives in another
// repository, is not on NuGet, and would have to be cloned and built to compare two files this
// repository already knows how to produce. The test it runs -- Mann-Whitney on the raw
// measurements, a relative threshold, a noise floor -- is thirty lines, and `Statistics.cs` is
// already here because the gates needed it. A dependency-free repository does not acquire a build
// dependency on a git clone to subtract two numbers.
//
// THE TEST IS EXACT WHERE IT MATTERS. The fast profile records five iterations, and a normal
// approximation to Mann-Whitney at n = 5 is not worth printing: the exact null distribution of U is
// a dynamic program over at most 21 x 21 ranks, so it is computed rather than approximated, and the
// approximation is kept only for the larger samples `--full` produces.
//
// A THRESHOLD AS WELL AS A P-VALUE, because a difference can be real and irrelevant. A case is
// called Faster or Slower only when the median moves by more than 5% AND the test rejects at 5% --
// the same 5% as the `MannWhitney(5%)` column, so one run and two runs answer with the same rule.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vorticity.Benchmarks;

/// <summary>Compares two runs' `-report-full.json` artifacts.</summary>
internal static class Compare
{
    /// <summary>How far the median must move before a difference is worth a verdict.</summary>
    private const double Threshold = 0.05;

    /// <summary>The significance level, matching the in-run column.</summary>
    private const double Alpha = 0.05;

    /// <summary>
    /// Differences under this many nanoseconds per operation are noise, whatever the ratio says.
    /// </summary>
    /// <remarks>
    /// `ResultsComparer` calls this `--noise` and defaults to 0.5 ns. A case that runs in 3 ns and
    /// moves to 3.3 ns is a 10% regression by the ratio and a timer artefact by any other reading.
    /// </remarks>
    private const double NoiseNanoseconds = 0.5;

    /// <summary>Compares two runs and prints a verdict per case.</summary>
    /// <param name="basePath">The baseline run: a `-report-full.json`, or a directory holding some.</param>
    /// <param name="diffPath">The run to judge.</param>
    /// <returns>0 when both sides were readable, 2 when they were not.</returns>
    internal static int Run(string basePath, string diffPath)
    {
        Dictionary<string, double[]> before = Read(basePath, out string beforeProblem);
        Dictionary<string, double[]> after = Read(diffPath, out string afterProblem);
        if (before.Count == 0 || after.Count == 0)
        {
            Console.Error.WriteLine(
                $"Nothing to compare.\n  base: {beforeProblem}\n  diff: {afterProblem}\n" +
                "Each side is a *-report-full.json or a directory containing some. Produce them " +
                "with:\n  dotnet run -c Release --project bench/Vorticity.Benchmarks -- " +
                "<filter> --artifacts bench/.runs/<sha>-<label>");
            return 2;
        }

        Console.Out.WriteLine(
            $"COMPARE: {Path.GetFileName(basePath)} -> {Path.GetFileName(diffPath)}, " +
            $"Mann-Whitney at {Alpha:P0} with a {Threshold:P0} threshold and a " +
            $"{NoiseNanoseconds.ToString("F1", CultureInfo.InvariantCulture)} ns floor");
        Console.Out.WriteLine(
            "  case                                                   base      diff   ratio" +
            "      p  verdict");

        int faster = 0;
        int slower = 0;
        int same = 0;
        int only = 0;
        foreach ((string name, double[] baseline) in before.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!after.TryGetValue(name, out double[]? current))
            {
                only++;
                continue;
            }

            double a = Median(baseline);
            double b = Median(current);
            double ratio = a == 0 ? double.NaN : b / a;
            double p = MannWhitney(baseline, current);
            bool moved = Math.Abs(ratio - 1) > Threshold && Math.Abs(b - a) > NoiseNanoseconds;
            string verdict = !moved || p >= Alpha
                ? "Same"
                : ratio < 1 ? "Faster" : "Slower";
            switch (verdict)
            {
                case "Faster": faster++; break;
                case "Slower": slower++; break;
                default: same++; break;
            }

            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {Shorten(name),-48} {Format(a),9} {Format(b),9} {ratio,7:F3} {p,6:F3}  {verdict}"));
        }

        int added = after.Keys.Count(k => !before.ContainsKey(k));
        Console.Out.WriteLine(
            $"\n{faster} faster, {slower} slower, {same} same" +
            (only > 0 ? $", {only} only in base" : string.Empty) +
            (added > 0 ? $", {added} only in diff" : string.Empty) + ".");
        return 0;
    }

    /// <summary>Every benchmark's per-operation timings, keyed by full name.</summary>
    private static Dictionary<string, double[]> Read(string path, out string problem)
    {
        Dictionary<string, double[]> results = [];
        string[] files;
        if (Directory.Exists(path))
        {
            files = Directory.GetFiles(path, "*-report-full.json", SearchOption.AllDirectories);
        }
        else if (System.IO.File.Exists(path))   // `File` alone is `Vorticity.File` in this namespace
        {
            files = [path];
        }
        else
        {
            problem = $"{path} does not exist";
            return results;
        }

        if (files.Length == 0)
        {
            problem = $"no *-report-full.json under {path}";
            return results;
        }

        foreach (string file in files)
        {
            Report? report = JsonSerializer.Deserialize(
                System.IO.File.ReadAllText(file), ReportContext.Default.Report);
            foreach (Report.Benchmark benchmark in report?.Benchmarks ?? [])
            {
                if (benchmark.FullName is not { Length: > 0 } name)
                {
                    continue;
                }

                double[] values = (benchmark.Measurements ?? [])
                    .Where(m =>
                        string.Equals(m.IterationMode, "Workload", StringComparison.Ordinal) &&
                        string.Equals(m.IterationStage, "Result", StringComparison.Ordinal) &&
                        m.Operations > 0)
                    .Select(m => m.Nanoseconds / m.Operations)
                    .ToArray();
                if (values.Length > 0)
                {
                    results[name] = values;
                }
            }
        }

        problem = $"{results.Count} case(s) from {files.Length} file(s)";
        return results;
    }

    /// <summary>
    /// The two-sided p-value of the Mann-Whitney U test: exact for small samples, normal for large.
    /// </summary>
    /// <param name="x">The baseline sample.</param>
    /// <param name="y">The other sample.</param>
    private static double MannWhitney(double[] x, double[] y)
    {
        int m = x.Length;
        int n = y.Length;
        if (m == 0 || n == 0)
        {
            return 1.0;
        }

        // U counts the pairs where a baseline value is smaller, ties counting a half.
        double u = 0;
        foreach (double a in x)
        {
            foreach (double b in y)
            {
                u += a < b ? 1 : a > b ? 0 : 0.5;
            }
        }

        if ((long)m * n <= 400)
        {
            // Exact: the number of rank arrangements giving each U, by the standard recurrence.
            double[] counts = ExactCounts(m, n);
            double total = counts.Sum();
            int low = (int)Math.Floor(u);
            int high = (int)Math.Ceiling(u);
            double atMost = counts.Take(low + 1).Sum() / total;
            double atLeast = counts.Skip(high).Sum() / total;
            return Math.Min(1.0, 2 * Math.Min(atMost, atLeast));
        }

        double mean = m * n / 2.0;
        double sigma = Math.Sqrt((double)m * n * (m + n + 1) / 12.0);
        if (sigma == 0)
        {
            return 1.0;
        }

        double z = (u - mean) / sigma;
        return Math.Min(1.0, 2 * (1 - NormalCdf(Math.Abs(z))));
    }

    /// <summary>Arrangements per value of U, for <paramref name="m"/> against <paramref name="n"/>.</summary>
    private static double[] ExactCounts(int m, int n)
    {
        // f[i, j][u]: ways to interleave i of one sample with j of the other reaching U = u. The
        // recurrence adds one element from either sample, which is the same statement as
        // "the next rank belongs to x or to y".
        double[,][] table = new double[m + 1, n + 1][];
        for (int i = 0; i <= m; i++)
        {
            for (int j = 0; j <= n; j++)
            {
                double[] row = new double[(i * j) + 1];
                if (i == 0 || j == 0)
                {
                    row[0] = 1;
                }
                else
                {
                    double[] fromX = table[i - 1, j];
                    for (int u = 0; u < fromX.Length; u++)
                    {
                        row[u + j] += fromX[u];
                    }

                    double[] fromY = table[i, j - 1];
                    for (int u = 0; u < fromY.Length; u++)
                    {
                        row[u] += fromY[u];
                    }
                }

                table[i, j] = row;
            }
        }

        return table[m, n];
    }

    /// <summary>The standard normal CDF, by Abramowitz and Stegun 7.1.26 on erf.</summary>
    private static double NormalCdf(double z)
    {
        double t = 1 / (1 + (0.2316419 * Math.Abs(z)));
        double d = 0.3989422804014327 * Math.Exp(-z * z / 2);
        double p = d * t * (0.319381530 + (t * (-0.356563782 +
            (t * (1.781477937 + (t * (-1.821255978 + (t * 1.330274429))))))));
        return z > 0 ? 1 - p : p;
    }

    private static double Median(double[] values)
    {
        double[] copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy.Length % 2 == 1
            ? copy[copy.Length / 2]
            : (copy[(copy.Length / 2) - 1] + copy[copy.Length / 2]) / 2;
    }

    /// <summary>Nanoseconds in the unit that reads.</summary>
    private static string Format(double nanoseconds) => nanoseconds switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000_000:F2}ms"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000:F2}us"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{nanoseconds:F2}ns"),
    };

    /// <summary>The benchmark name without its namespace, which is the same for every case.</summary>
    private static string Shorten(string fullName) =>
        fullName.StartsWith("Vorticity.Benchmarks.", StringComparison.Ordinal)
            ? fullName["Vorticity.Benchmarks.".Length..]
            : fullName;

    /// <summary>The part of BenchmarkDotNet's full report this reads.</summary>
    internal sealed record Report(Report.Benchmark[]? Benchmarks)
    {
        /// <summary>One case.</summary>
        internal sealed record Benchmark(string? FullName, Benchmark.Measurement[]? Measurements)
        {
            /// <summary>One timed iteration.</summary>
            internal sealed record Measurement(
                string? IterationMode, string? IterationStage, double Operations, double Nanoseconds);
        }
    }
}

/// <summary>Source-generated reader for the report, so trimming keeps it.</summary>
[JsonSerializable(typeof(Compare.Report))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class ReportContext : JsonSerializerContext;
