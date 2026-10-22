using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// <c>--micro qp-dashboard --tsv FILE [--label TEXT]</c>: the client's questions as the bench's TSV holds them after a run
/// with <c>--qp</c>, set against the targets its review gives: a table in Markdown, a line a question, path and degree, with
/// the medians of the runs of one label (the last one by default). Cold, warm and first result each against their target, and
/// a mark for each: met, missed, or no target.
/// </summary>
/// <remarks>
/// The review's targets: QP-1 to QP-3, QP-6 and QP-8 a first result under 300 ms, a whole answer under a second cold and
/// under 100 ms warm; QP-4 and QP-5 a whole answer under a second cold. The others have none in milliseconds.
/// </remarks>
internal static class QpDashboard
{
    private static readonly Dictionary<string, (double First, double Cold, double Warm)> Targets = new(StringComparer.Ordinal)
    {
        ["QP-1"] = (300, 1_000, 100),
        ["QP-2"] = (300, 1_000, 100),
        ["QP-3"] = (300, 1_000, 100),
        ["QP-6"] = (300, 1_000, 100),
        ["QP-8"] = (300, 1_000, 100),
        ["QP-4"] = (double.NaN, 1_000, double.NaN),
        ["QP-5"] = (double.NaN, 1_000, double.NaN),
    };

    internal static int Run(string tsv, string? label)
    {
        // The marks are emoji: UTF-8 whatever the console's code page, so that the page pastes as it reads.
        Console.OutputEncoding = Encoding.UTF8;
        return Write(tsv, label);
    }

    private static int Write(string tsv, string? label)
    {
        List<string[]> rows = [.. System.IO.File.ReadAllLines(tsv).Select(line => line.Split('\t')).Where(f => f.Length >= 15 && f[2].StartsWith("QP-", StringComparison.Ordinal))];
        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"{tsv} holds no line of the client's questions: run --micro paths --qp --tsv {tsv} first.");
            return 2;
        }

        // The runs of one label and of its rounds (the label and "-1", "-2"…), or of the last label the file holds.
        string chosen = label ?? rows[^1][0];
        List<string[]> picked = [.. rows.Where(f => label is null ? f[0] == chosen : f[0] == label || f[0].StartsWith(label + "-", StringComparison.Ordinal))];
        int labels = picked.Select(f => f[0]).Distinct(StringComparer.Ordinal).Count();
        Dictionary<(string Question, string Path, string Degree), Dictionary<string, List<string[]>>> cells = [];
        List<(string Question, string Path, string Degree)> order = [];
        foreach (string[] f in picked)
        {
            (string, string, string) key = (f[2], f[3], f[5]);
            if (!cells.TryGetValue(key, out Dictionary<string, List<string[]>>? temperatures))
            {
                cells[key] = temperatures = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
                order.Add(key);
            }

            if (!temperatures.TryGetValue(f[4], out List<string[]>? runs))
            {
                temperatures[f[4]] = runs = [];
            }

            runs.Add(f);
        }

        StringBuilder page = new StringBuilder();
        page.AppendLine(CultureInfo.InvariantCulture, $"Runs labelled `{chosen}`{(labels > 1 ? $", {labels} labels" : string.Empty)}: the medians, in milliseconds; ✅ the review's target met, ❌ missed, — none.");
        page.AppendLine();
        page.AppendLine("| Question | Path | Lanes | First | Cold | Warm | Requests cold | Steps cold | MB cold | Rows read cold |");
        page.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        int met = 0;
        int missed = 0;
        foreach ((string question, string path, string degree) in order)
        {
            Dictionary<string, List<string[]>> temperatures = cells[(question, path, degree)];
            string qp = question.Split(' ')[0];
            (double first, double cold, double warm) target = Targets.TryGetValue(qp, out var known) ? known : (double.NaN, double.NaN, double.NaN);
            string Cell(string temperature, double limit)
            {
                if (!temperatures.TryGetValue(temperature, out List<string[]>? runs))
                {
                    return "—";
                }

                double ms = Median(runs.Select(f => double.Parse(f[6], CultureInfo.InvariantCulture)));
                if (double.IsNaN(limit) || path != "synopses" && temperature == "first")
                {
                    return string.Create(CultureInfo.InvariantCulture, $"{ms:F1} —");
                }

                bool held = ms < limit;
                met += held ? 1 : 0;
                missed += held ? 0 : 1;
                return string.Create(CultureInfo.InvariantCulture, $"{ms:F1} {(held ? "✅" : "❌")}");
            }

            List<string[]>? coldRuns = temperatures.GetValueOrDefault("cold");
            string Of(int column, double scale, string format) => coldRuns is null ? "—"
                : Median(coldRuns.Select(f => double.Parse(f[column], CultureInfo.InvariantCulture) / scale)).ToString(format, CultureInfo.InvariantCulture);
            page.AppendLine(CultureInfo.InvariantCulture,
                $"| {question} | {path} | {degree} | {Cell("first", target.first)} | {Cell("cold", target.cold)} | {Cell("warm", target.warm)} | {Of(9, 1, "F0")} | {Of(10, 1, "F0")} | {Of(11, 1e6, "F2")} | {Of(13, 1, "F0")} |");
        }

        page.AppendLine();
        page.AppendLine(CultureInfo.InvariantCulture, $"{met} targets met, {missed} missed.");
        Console.Write(page.ToString());
        return 0;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.OrderBy(v => v)];
        return sorted.Length == 0 ? double.NaN : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
