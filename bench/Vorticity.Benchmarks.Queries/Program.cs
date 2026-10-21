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
//   … -- --matrix small                                                                the high-cardinality matrix of every A/B, at degrees 1 and N (HighCardinality.cs)
//   … -- --matrix full                                                                 the full one, at degrees 1, 4 and N
//   … -- --latency 20                                                                  every file read as a store would serve it, 20 ms a round trip: requests and dependent steps
//   … -- --cold --matrix small                                                         each query once in a process of its own: the first query's time and page faults
//   … -- --concurrent 8 --matrix small                                                 each query alone, then eight copies at once; with DOTNET_GCHeapHardLimit, under a capped heap
//   … -- --tsv runs.tsv                                                                each measure appended as a line, which bench/queries-ab.sh reads
//   … -- --switch merge                                                                every file's query under both settings of an engine switch, in turns (Switches.cs)
//   … -- --switch merge --setting B                                                    one setting alone, for a profile of that side
//   … -- --fixture spread-random-20000000-core2025.10                                   that fixture written if it is not yet, its path printed
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
string? matrix = Text(args, "--matrix");
int latency = Option(args, "--latency", 0);
int[] degrees = Degrees(args, matrix);
string? tsv = Text(args, "--tsv");
Switches.Switch? compared = Switches.Find(Text(args, "--switch"));

// One setting of the switch alone, A or B, for a profile of that side: the query measured once.
if (compared is not null && Text(args, "--setting") is { } setting)
{
    Action<AggregationPlan> set = setting == "B" ? compared.SetB : compared.SetA;
    compared = new Switches.Switch(setting == "B" ? compared.B : compared.A, set, setting == "B" ? compared.B : compared.A, set);
}
string[] only = [.. args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !args[i - 1].StartsWith("--", StringComparison.Ordinal) || args[i - 1] is "--check" or "--parallel"))];

// --micro keys: a key's groups found alone, in memory, with the statistics' bounds and without (KeyMicro.cs).
if (Text(args, "--micro") == "keys")
{
    return KeyMicro.Run(rounds);
}

// --micro words: a short text key's groups found alone, in memory (WordMicro.cs).
if (Text(args, "--micro") == "words")
{
    return WordMicro.Run(rounds);
}

// --micro floatsum: a grouped exact float sum alone, in memory (FloatSumMicro.cs).
if (Text(args, "--micro") == "floatsum")
{
    return FloatSumMicro.Run(rounds);
}

// --micro variants: a variant's path extracted alone, in memory (VariantMicro.cs).
if (Text(args, "--micro") == "variants")
{
    return VariantMicro.Run(rounds);
}

// --micro sketches: the grids of a distribution weighed, in buckets and speed (SketchMicro.cs).
if (Text(args, "--micro") == "sketches")
{
    return SketchMicro.Run(rounds);
}

// --micro synopsistables: what the synopsis tables cost a job, counted per slice and built (SynopsisTablesMicro.cs).
if (Text(args, "--micro") == "synopsistables")
{
    return await SynopsisTablesMicro.Run(rounds);
}

// --micro slicesort: one time slice sorted as a dataset of periods writes it (SliceSortMicro.cs).
if (Text(args, "--micro") == "slicesort")
{
    return await SliceSortMicro.Run(rounds, args.Contains("--deep"));
}

// --micro load: the write path of a load of sealed periods at the design's density, a few seconds a round (LoadMicro.cs).
if (Text(args, "--micro") == "load")
{
    int periods = Option(args, "--periods", 4);
    return await LoadMicro.Run(
        Text(args, "--store") ?? "E:/vorticity-load-micro",
        periods,
        Option(args, "--parallel", periods),
        Option(args, "--minutes", 3),
        Option(args, "--rate", 50_000),
        Option(args, "--rounds", 3),
        Option(args, "--budget", 100),
        Option(args, "--write-degree", Math.Max(1, Environment.ProcessorCount / 2)),
        args.Contains("--disk"),
        Option(args, "--period-hours", 1));
}

// --micro seal: what the compaction of a period costs at the design's scale, job by job and pass by pass (SealBench.cs).
if (Text(args, "--micro") == "seal")
{
    return await SealBench.Run(
        Text(args, "--store") ?? "E:/vorticity-seal",
        Option(args, "--minutes", 10),
        Option(args, "--rate", 50_000),
        Option(args, "--part-minutes", 10),
        Option(args, "--ceiling", 2),
        Option(args, "--write-degree", Math.Max(1, Environment.ProcessorCount / 2)),
        Option(args, "--budget", 100),
        !args.Contains("--no-late"));
}

// --micro shredcost: what shredding a path costs and saves in a file's bytes (ShredCostMicro.cs).
if (Text(args, "--micro") == "shredcost")
{
    return await ShredCostMicro.Run();
}

// --micro chunkshapes: the keys the chunks of a slice shred, three ways (ChunkShapesMicro.cs).
if (Text(args, "--micro") == "chunkshapes")
{
    return ChunkShapesMicro.Run((int)Option(args, "--rows", 3_000_000), (int)Option(args, "--chunk-rows", 1_000_000));
}

// --micro runsorder: the order inside a slice by distinct values or by runs added (RunsOrderMicro.cs).
if (Text(args, "--micro") == "runsorder")
{
    return RunsOrderMicro.Run((int)Option(args, "--rows", 1_000_000), rounds);
}

// --micro shredwrite: what shredding costs a job in time, counted, chosen and written (ShredWriteMicro.cs).
if (Text(args, "--micro") == "shredwrite")
{
    return await ShredWriteMicro.Run(Option(args, "--rows", 1_000_000), rounds);
}

// --micro slicebytes: what sorted slices weigh against the same rows in time order (SliceBytesMicro.cs).
if (Text(args, "--micro") == "slicebytes")
{
    return await SliceBytesMicro.Run(Option(args, "--rows", 1_000_000));
}

// --micro sliceorder: the time order of an object sorted slice by slice (SliceOrderMicro.cs).
if (Text(args, "--micro") == "sliceorder")
{
    return await SliceOrderMicro.Run(rounds, Option(args, "--slice-rows", 500_000));
}

// --micro synopsisanswer: an aggregation over a dataset of periods with its synopses and without (SynopsisAnswerMicro.cs).
if (Text(args, "--micro") == "synopsisanswer")
{
    return await SynopsisAnswerMicro.Run(Option(args, "--rows", 2_000_000), rounds);
}

// --micro sparsecost: a sparse chunk's bytes and decode time against a dense one's, by the share of nulls (SparseCostMicro.cs).
if (Text(args, "--micro") == "sparsecost")
{
    return await SparseCostMicro.Run(Option(args, "--rows", 1_000_000), rounds);
}

// --micro splitplan --file F: the plan of splits a scan of a file's every row draws before its first read (SplitPlanMicro.cs).
if (Text(args, "--micro") == "splitplan")
{
    return await SplitPlanMicro.Run(Text(args, "--file") ?? throw new ArgumentException("--micro splitplan needs --file F."), rounds);
}

// --micro slicecost: the price of a row at a window's edge and of a row of a table of slices, the two a slice's target trades (SliceCostMicro.cs).
if (Text(args, "--micro") == "slicecost")
{
    return await SliceCostMicro.Run(Option(args, "--rows", 2_000_000), rounds);
}

// --micro generator: a minute of the time-window design's requests, and the runs of each synopsis column against its figures (RequestGenerator.cs).
if (Text(args, "--micro") == "generator")
{
    return RequestGenerator.Run((int)Option(args, "--seed", 17));
}

// --micro questions: the designs' example questions over generated requests and log records, cold and warm, synopses and rows (QuestionBench.cs).
if (Text(args, "--micro") == "questions")
{
    return await QuestionBench.Run(
        (int)Option(args, "--hours", 24), Option(args, "--rate", 20), (int)Option(args, "--latency", 30), rounds, (int)Option(args, "--budget", 100), Text(args, "--only"),
        args.Contains("--sealed"));
}

// --micro facets: the worst case of many facets, spans with a dozen repeating columns and a route under them (FacetsBench.cs).
if (Text(args, "--micro") == "facets")
{
    return await FacetsBench.Run((int)Option(args, "--hours", 12), Option(args, "--rate", 20), (int)Option(args, "--latency", 30), rounds, (int)Option(args, "--budget", 100));
}

// --micro lifecycle: what a row costs from its append to its last rewrite, and the objects a window finds (LifecycleBench.cs).
if (Text(args, "--micro") == "lifecycle")
{
    return await LifecycleBench.Run(
        (int)Option(args, "--hours", 48), Option(args, "--rate", 20), (int)Option(args, "--writers", 4), (int)Option(args, "--batch", 30),
        Text(args, "--style") == "leveled", (int)Option(args, "--period-hours", 1), Text(args, "--deletes") == "off" ? 0 : (int)Option(args, "--deletes", 2), (int)Option(args, "--fanout", 0),
        Text(args, "--late") != "off", Text(args, "--neighbours") != "off", (long)Option(args, "--max-object-mib", 0) << 20,
        Text(args, "--pick"), (int)Option(args, "--trace-overlaps", 0));
}

// --micro cachetrace: the regions a run of questions asks of the synopsis cache, replayed through three eviction policies (CacheTrace.cs).
if (Text(args, "--micro") == "cachetrace")
{
    return await CacheTrace.Run((int)Option(args, "--hours", 48), Option(args, "--rate", 20), (int)Option(args, "--questions", 2_000), (int)Option(args, "--seed", 29));
}

// --micro needles: what a needle's index weighs and what a probe costs, sorted run against a hashed index (NeedleMicro.cs).
if (Text(args, "--micro") == "needles")
{
    return await NeedleMicro.Run((int)Option(args, "--rows", 4_000_000), rounds);
}

// --micro paths: every query path of a dataset of periods at scale, over a store that waits a round trip per request (PathsBench.cs).
if (Text(args, "--micro") == "paths")
{
    return await PathsBench.Run(new PathsBench.Settings
    {
        Store = Text(args, "--store") ?? new PathsBench.Settings().Store,
        Design = args.Contains("--design"),
        Incremental = args.Contains("--incremental"),
        ParallelPeriods = Option(args, "--parallel-periods", 4),
        Hours = Option(args, "--hours", args.Contains("--design") ? 240 : 24),
        Rate = Option(args, "--rate", args.Contains("--design") ? 50_000 : 1_000),
        ParallelHours = Option(args, "--parallel-hours", 4),
        PartMinutes = Option(args, "--part-minutes", 10),
        LogHours = Option(args, "--log-hours", 12),
        LogRate = Option(args, "--log-rate", 1_000),
        Latency = args.Contains("--latency") ? int.Parse(Text(args, "--latency")!, CultureInfo.InvariantCulture) : 5,
        BytesPerSecond = Option(args, "--mbps", 0) * 1e6,
        LinkBitsPerSecond = args.Contains("--gbps") ? double.Parse(Text(args, "--gbps")!, CultureInfo.InvariantCulture) * 1e9 : 10e9,
        Rounds = Option(args, "--rounds", 5),
        RowsRounds = Option(args, "--rows-rounds", 1),
        Degrees = args.Contains("--degrees") ? degrees : [1, Environment.ProcessorCount],
        Only = Text(args, "--only"),
        Paths = Text(args, "--paths") ?? "all",
        Temperatures = Text(args, "--temperatures") ?? "all",
        Budget = Option(args, "--budget", 100),
        WriteDegree = Option(args, "--write-degree", Math.Max(1, Environment.ProcessorCount / 2)),
        Rebuild = args.Contains("--rebuild"),
        BuildOnly = args.Contains("--build-only"),
        Verify = args.Contains("--verify"),
        Explain = args.Contains("--explain"),
        Tsv = Text(args, "--tsv"),
        Label = Text(args, "--label"),
        TraceReads = Text(args, "--trace-reads"),
        LoopSeconds = Option(args, "--loop", 0),
        LoopColdSeconds = Option(args, "--loop-cold", 0),
        NoDigest = args.Contains("--no-digest"),
        MaxReads = Option(args, "--max-reads", 0),
        KeepHandles = args.Contains("--keep-handles"),
        WarmSeconds = args.Contains("--warm") ? double.Parse(Text(args, "--warm")!, CultureInfo.InvariantCulture) : 3,
    });
}

// Each file is written the first time a query asks for it, and kept.
Dictionary<string, Func<ValueTask<string>>> fixtures = new Dictionary<string, Func<ValueTask<string>>>(StringComparer.Ordinal)
{
    ["readings"] = () => Fixtures.ReadingsAsync(1_000_000),
    ["requests"] = () => Fixtures.RequestsAsync(1_000_000),
    [$"readings-{large}"] = () => Fixtures.ReadingsAsync(large),
    ["draws"] = () => Fixtures.DrawsAsync(2_000_000),
    ["names"] = () => Fixtures.NamesAsync(2_000_000),
    ["few-names"] = () => Fixtures.FewNamesAsync(4_000_000),
    ["skewed"] = () => Fixtures.SkewedAsync(4_000_000),
    [$"skewed-{large}"] = () => Fixtures.SkewedAsync(large),
    [$"spread-random-{HighCardinality.SmallRows}"] = () => Fixtures.RandomSpreadAsync(HighCardinality.SmallRows),
    [$"spread-ordered-{HighCardinality.SmallRows}"] = () => Fixtures.OrderedSpreadAsync(HighCardinality.SmallRows),
    [$"spread-strided-{HighCardinality.SmallRows}"] = () => Fixtures.StridedSpreadAsync(HighCardinality.SmallRows),
    [$"spread-random-{HighCardinality.FullRows}"] = () => Fixtures.RandomSpreadAsync(HighCardinality.FullRows),
    [$"spread-ordered-{HighCardinality.FullRows}"] = () => Fixtures.OrderedSpreadAsync(HighCardinality.FullRows),
    [$"spread-strided-{HighCardinality.FullRows}"] = () => Fixtures.StridedSpreadAsync(HighCardinality.FullRows),
    ["skews"] = () => Fixtures.SkewsAsync(8_000_000),
    ["pages"] = () => Fixtures.PagesAsync(4_000_000),
    ["visits"] = () => Fixtures.VisitsAsync(20_000_000),
    ["medium"] = () => Fixtures.MediumAsync(4_000_000),
    ["late"] = () => Fixtures.LateAsync(4_000_000),
    ["readings-1"] = () => Fixtures.ReadingsDatasetAsync(1_000_000, 1, deleted: false),
    ["readings-16"] = () => Fixtures.ReadingsDatasetAsync(1_000_000, 16, deleted: false),
    ["readings-16-deleted"] = () => Fixtures.ReadingsDatasetAsync(1_000_000, 16, deleted: true),
    [$"spread-random-{HighCardinality.SmallRows}-16"] = () => Fixtures.SpreadDatasetAsync(HighCardinality.SmallRows, 16),
    [$"spread-random-{HighCardinality.FullRows}-16"] = () => Fixtures.SpreadDatasetAsync(HighCardinality.FullRows, 16),
    [$"spread-random-{HighCardinality.FullRows}-core2025.10"] = () => Fixtures.RandomSpreadAsync(HighCardinality.FullRows, VortexEdition.Core20251000),
    [$"spread-strided-{HighCardinality.FullRows}-core2025.10"] = () => Fixtures.StridedSpreadAsync(HighCardinality.FullRows, VortexEdition.Core20251000),
    [$"phases-{HighCardinality.FullRows}"] = () => Fixtures.PhasesAsync(HighCardinality.FullRows),
};

// --fixture NAME: that file written, if it is not yet, and its path printed: what the native runner and
// bench/duckdb.sh read. Besides the names above, spread-random-N, spread-strided-N and groupby-N-K (db-benchmark's
// rows, N of them over K groups), each with -core2025.10 for the edition other readers read.
if (Text(args, "--fixture") is { } fixtureName)
{
    Func<ValueTask<string>>? fixture = fixtures.GetValueOrDefault(fixtureName) ?? Patterned(fixtureName);
    if (fixture is null)
    {
        Console.Error.WriteLine($"no fixture named '{fixtureName}': {string.Join(", ", fixtures.Keys)}, spread-random-N, spread-strided-N, groupby-N-K, each with -core2025.10");
        return 2;
    }

    Console.WriteLine(await fixture().ConfigureAwait(false));
    return 0;
}
Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);
(string File, Scenario Scenario)[] scenarios = [.. Scenarios.All(large).Concat(EngineScenarios.All(large)).Concat(HighCardinality.All()).Concat(HandKernels.All())];

// --cold-one NAME: that query once, the first of a fresh process, the file opened before it: its time
// and the pages it touched first, which the warm rounds hide.
if (Text(args, "--cold-one") is { } coldName)
{
    (string coldFile, Scenario cold) = scenarios.First(entry => entry.Scenario.Name == coldName);
    string coldPath = await fixtures[coldFile]().ConfigureAwait(false);
    await using VortexSession coldSession = VortexSession.Create(options => options.MaxDegreeOfParallelism = degrees[0]);
    await using VortexFile coldOpen = await coldSession.OpenAsync(coldPath).ConfigureAwait(false);
    long before = ProcessCounters.MinorFaults();
    Stopwatch clock = Stopwatch.StartNew();
    long coldResult = await cold.Query(coldOpen, new Run(clock, 0, 0)).ConfigureAwait(false);
    double coldMillis = clock.Elapsed.TotalMilliseconds;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cold\t{coldMillis:F3}\t{ProcessCounters.MinorFaults() - before}\t{coldResult}"));
    return 0;
}

// --cold: each query selected, at each degree, in a process of its own (--cold-one).
if (args.Contains("--cold"))
{
    Console.WriteLine($"{"query",-62} {"degree",6} {"cold ms",9} {"faults",8} {"result",12}");
    foreach (int degree in degrees)
    {
        foreach ((string fileName, Scenario scenario) in scenarios)
        {
            if (!Selected(scenario, matrix, only))
            {
                continue;
            }

            System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true };
            foreach (string argument in (string[])["--cold-one", scenario.Name, "--degrees", degree.ToString(CultureInfo.InvariantCulture), "--large", large.ToString(CultureInfo.InvariantCulture)])
            {
                start.ArgumentList.Add(argument);
            }

            using System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)!;
            string output = await child.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await child.WaitForExitAsync().ConfigureAwait(false);
            string[] cold = output.Split('\n').FirstOrDefault(line => line.StartsWith("cold\t", StringComparison.Ordinal))?.Split('\t') ?? ["cold", "-1", "-1", "-1"];
            Console.WriteLine($"{scenario.Name,-62} {degree,6} {double.Parse(cold[1], CultureInfo.InvariantCulture),9:F2} {cold[2],8} {cold[3].Trim(),12}");
        }
    }

    return 0;
}

// --concurrent N: each query selected alone, then N copies of it at once on the same open file: the
// slowest copy's time against the query's alone.
if (Option(args, "--concurrent", 0) is int copies and > 0)
{
    Console.WriteLine($"{"query",-62} {"degree",6} {"alone ms",9} {$"{copies} at once",10} {"ratio",7}");
    foreach (int degree in degrees)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
        foreach ((string fileName, Scenario scenario) in scenarios)
        {
            if (!Selected(scenario, matrix, only))
            {
                continue;
            }

            string path = await fixtures[fileName]().ConfigureAwait(false);
            await using VortexFile file = await session.OpenAsync(path).ConfigureAwait(false);

            // Under a capped heap, the query alone may be refused its memory too: told, not fatal.
            double aloneMillis = double.NaN;
            try
            {
                aloneMillis = (await Measure.RunAsync(run => scenario.Query(file, run), rounds, probeEvery: 0).ConfigureAwait(false)).Millis;
            }
            catch (Exception failure) when (failure.GetType().Name == "VortexMemoryException")
            {
            }

            (double together, bool agree, int refused) = await Measure.ConcurrentAsync(run => scenario.Query(file, run), rounds, copies).ConfigureAwait(false);
            Console.WriteLine(
                $"{scenario.Name,-62} {degree,6} {aloneMillis,9:F2} {together,10:F2} {together / aloneMillis,7:F2}{(agree ? string.Empty : "  RESULTS DIFFER")}{(double.IsNaN(aloneMillis) ? "  refused alone" : string.Empty)}{(refused > 0 ? $"  {refused} of {copies} refused their memory" : string.Empty)}");
        }
    }

    return 0;
}

Dictionary<string, Measurement> measured = new Dictionary<string, Measurement>(StringComparer.Ordinal);
List<(string Key, Measurement Measurement)> engines = [];
Console.WriteLine($"{"query",-62} {"degree",6} {"ms",9} {"first ms",9} {"alloc MiB",10} {"live MiB",9} {"gen2",5} {"faults",8} {"LOH MiB",8} {"result",12}");
foreach (int degree in degrees)
{
    await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = degree);
    foreach ((string fileName, Scenario scenario) in scenarios)
    {
        if (!Selected(scenario, matrix, only))
        {
            continue;
        }

        if (!files.TryGetValue(fileName, out string? path))
        {
            path = await fixtures[fileName]().ConfigureAwait(false);
            files[fileName] = path;
        }

        // Over a round trip of --latency ms, as an object store serves it: its requests and dependent
        // steps counted, the warm-up's and the memory pass's with the rounds'.
        LatencySource? remote = latency > 0 ? new LatencySource(path, TimeSpan.FromMilliseconds(latency)) : null;
        await using VortexFile file = remote is null
            ? await session.OpenAsync(path).ConfigureAwait(false)
            : await session.OpenAsync(remote).ConfigureAwait(false);
        (long Requests, long Steps) opened = (remote?.Requests ?? 0, remote?.Steps ?? 0);
        (string Name, Measurement M, string Versus)[] results = await MeasuredAsync(run => scenario.Query(file, run), scenario.Name, scenario.ProbeEvery).ConfigureAwait(false);
        string trips = remote is null ? string.Empty : string.Create(
            CultureInfo.InvariantCulture,
            $"  {(remote.Requests - opened.Requests) / (double)(results.Length * (rounds + 2)),7:F1} requests {(remote.Steps - opened.Steps) / (double)(results.Length * (rounds + 2)),6:F1} steps");
        foreach ((string name, Measurement m, string versus) in results)
        {
            string key = degree == 1 ? name : $"{name}, degree {degree}";
            measured[key] = m;
            if (m.Engine is not null)
            {
                engines.Add((key, m));
            }

            Console.WriteLine(
                $"{name,-62} {degree,6} {m.Millis,9:F2} {m.FirstMillis,9:F2} {m.Allocated / 1048576.0,10:F2} {m.Live / 1048576.0,9:F2} {m.Gen2,5:F1} {m.Faults,8} {m.LargeBytes / 1048576.0,8:F1} {m.Result,12}{versus}{trips}");
            Record(tsv, name, degree, m);
        }
    }

    // The datasets: their directories written once as the files are, each opened by the session.
    foreach ((string datasetName, DatasetScenario scenario) in DatasetScenarios.All())
    {
        if (matrix is not null || (only.Length > 0 && !only.Any(word => scenario.Name.Contains(word, StringComparison.OrdinalIgnoreCase))))
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
            $"{scenario.Name,-62} {degree,6} {m.Millis,9:F2} {m.FirstMillis,9:F2} {m.Allocated / 1048576.0,10:F2} {m.Live / 1048576.0,9:F2} {m.Gen2,5:F1} {m.Faults,8} {m.LargeBytes / 1048576.0,8:F1} {m.Result,12} {requests,9:F1} requests");
        Record(tsv, scenario.Name, degree, m);
    }
}

if (engines.Count > 0)
{
    // What each lane did in the best round: the busiest against the mean, and the merge's share.
    Console.WriteLine();
    Console.WriteLine($"{"engine",-74} {"lanes",5} {"ranges",6} {"max ms",8} {"mean ms",8} {"max/mean",8} {"merge ms",9} {"merge %",8} {"parts",5} {"groups",9} {"state MiB",9}");
    foreach ((string key, Measurement m) in engines)
    {
        AggregationRun run = m.Engine!;
        double max = run.Lanes.Max(lane => lane.ActiveTicks) * 1_000.0 / Stopwatch.Frequency;
        double mean = run.Lanes.Average(lane => lane.ActiveTicks) * 1_000.0 / Stopwatch.Frequency;
        double merge = run.MergeTicks * 1_000.0 / Stopwatch.Frequency;
        long groups = run.Lanes.Sum(lane => (long)lane.Groups);
        Console.WriteLine(
            $"{key,-74} {run.Lanes.Length,5} {run.Lanes.Sum(lane => lane.Ranges),6} {max,8:F2} {mean,8:F2} {(mean > 0 ? max / mean : 0),8:F2} {merge,9:F2} {100 * merge / m.Millis,8:F1} {run.MergeParts,5} {groups,9} {StateMiB(run),9:F1}");
    }

    // What the core did, when it held the groups: its merge is its end.
    foreach ((string key, Measurement m) in engines)
    {
        if (CoreOf(m.Engine!) is { } core)
        {
            Console.WriteLine($"{key,-74} {core}");
        }
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

// A query measured, or under each setting of the switch compared, the second against the first.
async Task<(string Name, Measurement M, string Versus)[]> MeasuredAsync(Func<Run, Task<long>> query, string name, int probeEvery)
{
    if (compared is null)
    {
        return [(name, await Measure.RunAsync(query, rounds, probeEvery).ConfigureAwait(false), string.Empty)];
    }

    (Measurement a, Measurement b) = await Measure.CompareAsync(query, rounds, probeEvery, compared.SetA, compared.SetB).ConfigureAwait(false);
    return
    [
        ($"{name} [{compared.A}]", a, string.Empty),
        ($"{name} [{compared.B}]", b, string.Create(CultureInfo.InvariantCulture, $"  x{b.Millis / a.Millis:F3}")),
    ];
}

static string? Text(string[] args, string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

// A fixture named by its rows rather than registered: spread-random-N, spread-strided-N or groupby-N-K, each
// with -core2025.10 for that edition; null for any other name.
static Func<ValueTask<string>>? Patterned(string name)
{
    const string Older = "-core2025.10";
    VortexEdition? edition = name.EndsWith(Older, StringComparison.Ordinal) ? VortexEdition.Core20251000 : null;
    string[] parts = (edition is null ? name : name[..^Older.Length]).Split('-');
    bool Count(string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    return parts switch
    {
        ["spread", "random", string n] when Count(n, out int rows) => () => Fixtures.RandomSpreadAsync(rows, edition),
        ["spread", "strided", string n] when Count(n, out int rows) => () => Fixtures.StridedSpreadAsync(rows, edition),
        ["groupby", string n, string k] when Count(n, out int rows) && Count(k, out int groups) && groups <= rows => () => Fixtures.GroupByAsync(rows, groups, edition),
        _ => null,
    };
}

// A measure as one line of tab-separated fields, appended: the query, its degree, its times, bytes and
// result, then its lanes when it tracks its aggregation (their count, the busiest, the mean, the merge
// and its parts, the state's bytes at the merge's end), or nothing.
static void Record(string? path, string name, int degree, Measurement m)
{
    if (path is null)
    {
        return;
    }

    string engine = string.Empty;
    if (m.Engine is { } run)
    {
        double max = run.Lanes.Max(lane => lane.ActiveTicks) * 1_000.0 / Stopwatch.Frequency;
        double mean = run.Lanes.Average(lane => lane.ActiveTicks) * 1_000.0 / Stopwatch.Frequency;
        double merge = run.MergeTicks * 1_000.0 / Stopwatch.Frequency;
        engine = string.Create(CultureInfo.InvariantCulture, $"\t{run.Lanes.Length}\t{max:F3}\t{mean:F3}\t{merge:F3}\t{run.MergeParts}\t{StateMiB(run):F2}");
    }

    System.IO.File.AppendAllText(path, string.Create(
        CultureInfo.InvariantCulture,
        $"{name}\t{degree}\t{m.Millis:F3}\t{m.FirstMillis:F3}\t{m.Allocated / 1048576.0:F2}\t{m.Live / 1048576.0:F2}\t{m.Result}\t{m.Gen2:F2}\t{m.Faults}\t{m.LargeBytes / 1048576.0:F2}{engine}\n"));
}

// The bytes the run's groups held at the merge's end, read by name so
// that an older commit, which did not count them, still builds today's bench: -1 there.
static double StateMiB(AggregationRun run) =>
    run.GetType().GetProperty("StateBytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(run) is long bytes
        ? bytes / 1048576.0
        : -1;

// What the core did, read by name as the state's bytes are: null without one.
static object? CoreOf(AggregationRun run) =>
    run.GetType().GetProperty("Core", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(run);

static int[] Degrees(string[] args, string? matrix)
{
    int at = Array.IndexOf(args, "--degrees");
    if (at >= 0 && at + 1 < args.Length)
    {
        return [.. args[at + 1].Split(',').Select(d => int.Parse(d, CultureInfo.InvariantCulture))];
    }

    // The small matrix at one lane and one a processor; the full one at four lanes too.
    return matrix switch
    {
        "small" => [1, Environment.ProcessorCount],
        "full" => [1, 4, Environment.ProcessorCount],
        _ => args.Contains("--parallel") ? [1, Environment.ProcessorCount] : [1],
    };
}

// Whether a query is run: in the matrix asked for, if one is, and named by a word asked for, if any is.
static bool Selected(Scenario scenario, string? matrix, string[] only) =>
    matrix switch
    {
        "small" => scenario.Matrix == Matrix.Small,
        "full" => scenario.Matrix != Matrix.None,
        null => true,
        _ => throw new ArgumentException($"No matrix named '{matrix}': small, full."),
    }
    && (only.Length == 0 || only.Any(word => scenario.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
