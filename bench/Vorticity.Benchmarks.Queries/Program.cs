// The query matrix of docs/design/16-queries.md §13: what each query costs against the loop a caller
// would write by hand, how long its first answer waits, what it allocates and what it holds.
//
//   dotnet run -c Release --project bench/Vorticity.Benchmarks.Queries                 the table
//   dotnet run -c Release --project bench/Vorticity.Benchmarks.Queries -- --check      the gate: exits 1 on a red axis
//   … -- --rounds 9 group                                                              more rounds, the queries whose name holds "group"
//   … -- --large 4000000                                                               a smaller "large" file, for a quick look
//
// The files are written once under ~/.cache/vorticity/queries (VORTICITY_QUERIES_CORPUS overrides it).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Vorticity;
using Vorticity.Benchmarks.Queries;

int rounds = Option(args, "--rounds", 5);
int large = Option(args, "--large", 16_000_000);
bool check = args.Contains("--check");
int[] parallel = args.Contains("--parallel") ? [1, Environment.ProcessorCount] : [1];
string[] only = [.. args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !args[i - 1].StartsWith("--", StringComparison.Ordinal) || args[i - 1] is "--check" or "--parallel"))];

Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["readings"] = await Fixtures.ReadingsAsync(1_000_000).ConfigureAwait(false),
    ["requests"] = await Fixtures.RequestsAsync(1_000_000).ConfigureAwait(false),
    [$"readings-{large}"] = await Fixtures.ReadingsAsync(large).ConfigureAwait(false),
};

Dictionary<string, Measurement> measured = new Dictionary<string, Measurement>(StringComparer.Ordinal);
Console.WriteLine($"{"query",-62} {"degree",6} {"ms",9} {"first ms",9} {"alloc MiB",10} {"live MiB",9} {"result",12}");
foreach (int degree in parallel)
{
    await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
    foreach ((string fileName, Scenario scenario) in Scenarios.All(large))
    {
        if (only.Length > 0 && !only.Any(word => scenario.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        await using VortexFile file = await session.OpenAsync(files[fileName]).ConfigureAwait(false);
        Measurement m = await Measure.RunAsync(run => scenario.Query(file, run), rounds, scenario.ProbeEvery).ConfigureAwait(false);
        string key = degree == 1 ? scenario.Name : $"{scenario.Name}, degree {degree}";
        measured[key] = m;
        Console.WriteLine(
            $"{scenario.Name,-62} {degree,6} {m.Millis,9:F2} {m.FirstMillis,9:F2} {m.Allocated / 1048576.0,10:F2} {m.Live / 1048576.0,9:F2} {m.Result,12}");
    }
}

Console.WriteLine();
Console.WriteLine($"{"axis",-56} {"ratio",8} {"reference",10}");
bool red = false;
foreach ((string axis, string numerator, string denominator, double? reference) in References.Axes)
{
    string top = numerator.Replace("16M", $"{large / 1_000_000}M", StringComparison.Ordinal);
    if (!measured.TryGetValue(top, out Measurement a) || !measured.TryGetValue(denominator, out Measurement b))
    {
        continue;
    }

    double ratio = a.Millis / b.Millis;
    bool over = reference is double r && ratio > r * References.Margin;
    red |= over;
    Console.WriteLine($"{axis,-56} {ratio,8:F3} {(reference is double shown ? shown.ToString("F3", CultureInfo.InvariantCulture) : "—"),10}{(over ? "  RED" : string.Empty)}");
}

return check && red ? 1 : 0;

static int Option(string[] args, string name, int fallback)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], CultureInfo.InvariantCulture, out int value) && value > 0
        ? value
        : fallback;
}
