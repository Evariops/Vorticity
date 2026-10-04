// The query matrix of docs/design/16-queries.md §13: what each query costs against the loop a caller
// would write by hand, how long its first answer waits, what it allocates and what it holds; and,
// for a query that tracks its aggregation, what each lane did and what the merge cost.
//
//   dotnet run -c Release --project bench/Vorticity.Benchmarks.Queries                 the table
//   dotnet run -c Release --project bench/Vorticity.Benchmarks.Queries -- --check      the gate: exits 1 on a red axis
//   … -- --rounds 9 group                                                              more rounds, the queries whose name holds "group"
//   … -- --large 4000000                                                               a smaller "large" file, for a quick look
//   … -- --parallel                                                                    every query at degree 1 and at one lane per processor
//   … -- --degrees 1,2,4,8                                                             every query at each of these degrees
//
// The files are written once under ~/.cache/vorticity/queries (VORTICITY_QUERIES_CORPUS overrides it).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Vorticity;
using Vorticity.Aggregating;
using Vorticity.Benchmarks.Queries;

int rounds = Option(args, "--rounds", 5);
int large = Option(args, "--large", 16_000_000);
bool check = args.Contains("--check");
int[] degrees = Degrees(args);
string[] only = [.. args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !args[i - 1].StartsWith("--", StringComparison.Ordinal) || args[i - 1] is "--check" or "--parallel"))];

// Each file is written the first time a query asks for it, and kept.
Dictionary<string, Func<ValueTask<string>>> fixtures = new Dictionary<string, Func<ValueTask<string>>>(StringComparer.Ordinal)
{
    ["readings"] = () => Fixtures.ReadingsAsync(1_000_000),
    ["requests"] = () => Fixtures.RequestsAsync(1_000_000),
    [$"readings-{large}"] = () => Fixtures.ReadingsAsync(large),
    ["draws"] = () => Fixtures.DrawsAsync(2_000_000),
    ["names"] = () => Fixtures.NamesAsync(2_000_000),
    ["skewed"] = () => Fixtures.SkewedAsync(4_000_000),
    ["medium"] = () => Fixtures.MediumAsync(4_000_000),
    ["late"] = () => Fixtures.LateAsync(4_000_000),
    ["readings-1"] = () => Fixtures.ReadingsDatasetAsync(1_000_000, 1, deleted: false),
    ["readings-16"] = () => Fixtures.ReadingsDatasetAsync(1_000_000, 16, deleted: false),
    ["readings-16-deleted"] = () => Fixtures.ReadingsDatasetAsync(1_000_000, 16, deleted: true),
};
Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);

Dictionary<string, Measurement> measured = new Dictionary<string, Measurement>(StringComparer.Ordinal);
List<(string Key, Measurement Measurement)> engines = [];
Console.WriteLine($"{"query",-62} {"degree",6} {"ms",9} {"first ms",9} {"alloc MiB",10} {"live MiB",9} {"result",12}");
foreach (int degree in degrees)
{
    await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
    foreach ((string fileName, Scenario scenario) in Scenarios.All(large).Concat(EngineScenarios.All(large)))
    {
        if (only.Length > 0 && !only.Any(word => scenario.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        if (!files.TryGetValue(fileName, out string? path))
        {
            path = await fixtures[fileName]().ConfigureAwait(false);
            files[fileName] = path;
        }

        await using VortexFile file = await session.OpenAsync(path).ConfigureAwait(false);
        Measurement m = await Measure.RunAsync(run => scenario.Query(file, run), rounds, scenario.ProbeEvery).ConfigureAwait(false);
        string key = degree == 1 ? scenario.Name : $"{scenario.Name}, degree {degree}";
        measured[key] = m;
        if (m.Engine is not null)
        {
            engines.Add((key, m));
        }

        Console.WriteLine(
            $"{scenario.Name,-62} {degree,6} {m.Millis,9:F2} {m.FirstMillis,9:F2} {m.Allocated / 1048576.0,10:F2} {m.Live / 1048576.0,9:F2} {m.Result,12}");
    }

    // The datasets: their directories written once as the files are, each opened by the session.
    foreach ((string datasetName, DatasetScenario scenario) in DatasetScenarios.All())
    {
        if (only.Length > 0 && !only.Any(word => scenario.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        if (!files.TryGetValue(datasetName, out string? path))
        {
            path = await fixtures[datasetName]().ConfigureAwait(false);
            files[datasetName] = path;
        }

        // The store's requests a query makes, which an object store in the cloud bills and waits on:
        // the warm-up's and the memory probe's counted with the rounds'.
        await using Vorticity.Dataset.FileObjectStore directory = new Vorticity.Dataset.FileObjectStore(path);
        await using Vorticity.Dataset.CountingObjectStore store = new Vorticity.Dataset.CountingObjectStore(directory);
        await using Vorticity.Dataset.VortexDataset dataset = await Vorticity.Dataset.VortexDataset
            .OpenAsync(store, new Vorticity.Dataset.DatasetOptions { Session = session }).ConfigureAwait(false);
        long opening = store.Requests;
        Measurement m = await Measure.RunAsync(run => scenario.Query(dataset, run), rounds, scenario.ProbeEvery).ConfigureAwait(false);
        double requests = (store.Requests - opening) / (double)(rounds + 2);
        string key = degree == 1 ? scenario.Name : $"{scenario.Name}, degree {degree}";
        measured[key] = m;
        if (m.Engine is not null)
        {
            engines.Add((key, m));
        }

        Console.WriteLine(
            $"{scenario.Name,-62} {degree,6} {m.Millis,9:F2} {m.FirstMillis,9:F2} {m.Allocated / 1048576.0,10:F2} {m.Live / 1048576.0,9:F2} {m.Result,12} {requests,9:F1} requests");
    }
}

if (engines.Count > 0)
{
    // What each lane did in the best round: the busiest against the mean, and the merge's share.
    Console.WriteLine();
    Console.WriteLine($"{"engine",-74} {"lanes",5} {"max ms",8} {"mean ms",8} {"max/mean",8} {"merge ms",9} {"merge %",8} {"parts",5} {"groups",9}");
    foreach ((string key, Measurement m) in engines)
    {
        AggregationRun run = m.Engine!;
        double max = run.Lanes.Max(lane => lane.ActiveTicks) * 1_000.0 / Stopwatch.Frequency;
        double mean = run.Lanes.Average(lane => lane.ActiveTicks) * 1_000.0 / Stopwatch.Frequency;
        double merge = run.MergeTicks * 1_000.0 / Stopwatch.Frequency;
        long groups = run.Lanes.Sum(lane => (long)lane.Groups);
        Console.WriteLine(
            $"{key,-74} {run.Lanes.Length,5} {max,8:F2} {mean,8:F2} {(mean > 0 ? max / mean : 0),8:F2} {merge,9:F2} {100 * merge / m.Millis,8:F1} {run.MergeParts,5} {groups,9}");
    }
}

Console.WriteLine();
Console.WriteLine($"{"axis",-62} {"ratio",8} {"reference",10}");
bool red = false;
string most = $"degree {Environment.ProcessorCount}";
foreach ((string axis, string numerator, string denominator, double? reference) in References.Axes)
{
    string top = numerator.Replace("16M", $"{large / 1_000_000}M", StringComparison.Ordinal).Replace("degree N", most, StringComparison.Ordinal);
    string bottom = denominator.Replace("degree N", most, StringComparison.Ordinal);
    if (!measured.TryGetValue(top, out Measurement a) || !measured.TryGetValue(bottom, out Measurement b))
    {
        continue;
    }

    double ratio = a.Millis / b.Millis;
    bool over = reference is double r && ratio > r * References.Margin;
    red |= over;
    Console.WriteLine($"{axis,-62} {ratio,8:F3} {(reference is double shown ? shown.ToString("F3", CultureInfo.InvariantCulture) : "—"),10}{(over ? "  RED" : string.Empty)}");
}

return check && red ? 1 : 0;

static int Option(string[] args, string name, int fallback)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], CultureInfo.InvariantCulture, out int value) && value > 0
        ? value
        : fallback;
}

static int[] Degrees(string[] args)
{
    int at = Array.IndexOf(args, "--degrees");
    if (at >= 0 && at + 1 < args.Length)
    {
        return [.. args[at + 1].Split(',').Select(d => int.Parse(d, CultureInfo.InvariantCulture))];
    }

    return args.Contains("--parallel") ? [1, Environment.ProcessorCount] : [1];
}
