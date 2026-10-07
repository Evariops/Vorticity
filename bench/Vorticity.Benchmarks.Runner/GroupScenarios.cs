using System;
using System.Globalization;
using System.Threading.Tasks;

using Vorticity.Aggregating;
using Vorticity.Bench.Scenarios;

namespace Vorticity.Bench.Runner;

/// <summary>
/// The group-bys of the high-cardinality bench (bench/Vorticity.Benchmarks.Queries,
/// HighCardinality.cs), in the native runner, for the profiler: the bench compiles as it goes, and
/// the frames of a just-in-time method are no symbol an Instruments trace can name.
/// </summary>
/// <remarks>
/// <para>
/// A name is <c>group-&lt;aggregates&gt;-&lt;key&gt;</c>, run over one of the bench's spread files
/// (<c>~/.cache/vorticity/queries/spread-random-4000000.vortex</c> and its siblings, which
/// <c>vortex-queries --matrix small</c> writes): <c>total</c> (a count and a sum), <c>four</c>,
/// <c>range</c> (a count, the least and the largest of a value), <c>mean</c> (a count and the mean of a
/// float), <c>deviation</c>, <c>top</c> (ordered
/// by the count, the first hundred), <c>most</c> (ordered by the largest value, the first hundred),
/// <c>first</c> (the hundred smallest keys and their counts), <c>countdistinct</c>, <c>distinct</c>
/// (the key's distinct values) over an integer key (<c>k3</c>
/// to <c>k7</c>, <c>tenfold</c>, <c>unique</c>); <c>strided</c>, a count and a sum over the strided
/// file's long keys; <c>pairs</c>, a count and a sum by a pair of integers of the draws file
/// (<c>k100k</c>, 1.8M groups; <c>k4</c>, 4 000); <c>pages</c>, a count by a text of the pages
/// file (<c>url</c>) or by its UUID (<c>uuid</c>), a million groups each, or the least and greatest
/// URL by its integer key (<c>texts</c>); and <c>names-name</c>, a
/// count by the name of a names file (a million names, or a thousand in <c>names-1e3-4000000</c>).
/// </para>
/// <para>
/// The plan's switches follow, each after a <c>+</c>: <c>core</c> (the core at every degree),
/// <c>whole</c> (the core's groups delivered whole), <c>capacity=N</c>, <c>alpha=N</c>,
/// <c>floor=N</c>, <c>table=N</c>, <c>batch=N</c>, <c>window=N</c>, <c>probe=N</c>. The degree is
/// the runner's <c>--threads</c>.
/// </para>
/// </remarks>
internal static class GroupScenarios
{
    /// <summary>The scenario <paramref name="name"/> names, or <see langword="null"/> when it names none.</summary>
    /// <param name="name">A name of the form the remarks give.</param>
    /// <exception cref="ArgumentException">A switch is unknown or has no value.</exception>
    internal static Func<string, Task<long>>? For(string name)
    {
        if (!name.StartsWith("group-", StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = name.Split('+');
        Action<AggregationPlan>? configure = null;
        foreach (string option in parts.AsSpan(1))
        {
            configure += Switch(option);
        }

        string[] shape = parts[0].Split('-');
        if (shape.Length != 3)
        {
            return null;
        }

        if (shape[1] == "strided")
        {
            return StridedKey(shape[2]) is { } stridedKey ? path => StridedAsync(path, stridedKey, configure) : null;
        }

        if (shape[1] == "pages")
        {
            return shape[2] switch
            {
                "url" => path => UrlsAsync(path, configure),
                "uuid" => path => IdsAsync(path, configure),
                "texts" => path => TextExtremesAsync(path, configure),
                "urlsmall" => path => UrlSmallsAsync(path, configure),
                _ => null,
            };
        }

        if (shape[1] == "names" && shape[2] == "name")
        {
            return path => NamesAsync(path, configure);
        }

        if (shape[1] == "pairs")
        {
            return shape[2] switch
            {
                "k100k" => path => PairsAsync(path, static d => d.K100k, static d => d.K100, configure),
                "k4" => path => PairsAsync(path, static d => d.K4, static d => d.K1000, configure),
                _ => null,
            };
        }

        if (Key(shape[2]) is not { } key)
        {
            return null;
        }

        return shape[1] switch
        {
            "total" => path => TotalAsync(path, key, configure),
            "four" => path => FourAsync(path, key, configure),
            "range" => path => RangeAsync(path, key, configure),
            "deviation" => path => DeviationAsync(path, key, configure),
            "mean" => path => MeanAsync(path, key, configure),
            "top" => path => TopAsync(path, key, configure),
            "most" => path => MostAsync(path, key, configure),
            "first" => path => FirstAsync(path, key, configure),
            "countdistinct" => path => CountDistinctAsync(path, key, configure),
            "distinct" => path => DistinctAsync(path, key, configure),
            _ => null,
        };
    }

    /// <summary>A switch of the plan, by its name in a scenario's.</summary>
    private static Action<AggregationPlan> Switch(string option)
    {
        int at = option.IndexOf('=', StringComparison.Ordinal);
        string name = at < 0 ? option : option[..at];
        int? value = at < 0 ? null : int.Parse(option.AsSpan(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
        return name switch
        {
            "core" => static plan =>
            {
                plan.Core = true;
                plan.CoreLanes = 1;
            },
            "whole" => static plan => plan.CoreParted = false,
            "capacity" => plan => plan.CoreCapacity = Valued(value, name),
            "alpha" => plan => plan.CoreAlpha = Valued(value, name),
            "floor" => plan => plan.CoreFloor = Valued(value, name),
            "table" => plan => plan.CoreTableGroups = Valued(value, name),
            "batch" => plan => plan.CoreBatchEntries = Valued(value, name),
            "window" => plan => plan.FoldWindow = Valued(value, name),
            "probe" => plan => plan.ProbeAhead = Valued(value, name),
            _ => throw new ArgumentException($"No switch named '{name}': core, whole, capacity=N, alpha=N, floor=N, table=N, batch=N, window=N, probe=N.", nameof(option)),
        };
    }

    private static int Valued(int? value, string name) =>
        value ?? throw new ArgumentException($"The switch '{name}' takes a value: {name}=N.", nameof(value));

    private static Func<Probe<Spread>, Sym<int>>? Key(string key) => key switch
    {
        "k3" => static s => s.K3,
        "k4" => static s => s.K4,
        "k5" => static s => s.K5,
        "k6" => static s => s.K6,
        "k7" => static s => s.K7,
        "tenfold" => static s => s.Tenfold,
        "unique" => static s => s.Unique,
        _ => null,
    };

    private static Func<Probe<Strided>, Sym<long>>? StridedKey(string key) => key switch
    {
        "k3" => static s => s.K3,
        "k4" => static s => s.K4,
        "k5" => static s => s.K5,
        "k6" => static s => s.K6,
        "k7" => static s => s.K7,
        _ => null,
    };

    /// <summary>The plan of the last group by a scenario ran: its last run says what the engine did.</summary>
    private static AggregationPlan? s_last;

    /// <summary>What the engine did in the last round, on a line: its lanes, its merge and, when it ran, the core.</summary>
    internal static string? Describe()
    {
        if (s_last?.LastRun is not { } run)
        {
            return null;
        }

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"lanes={run.Lanes.Length} merge_parts={run.MergeParts} merge_us={run.MergeTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency} state_bytes={run.StateBytes} groups={s_last.LastGroups}");
        return run.Core is not { } core
            ? line
            : line + string.Create(
                CultureInfo.InvariantCulture,
                $" core_alpha={core.Alpha} core_capacity={core.Capacity} flushes={core.Flushes} flushed_groups={core.FlushedGroups} bypassed_rows={core.BypassedRows} bursts={core.Bursts} pending_peak_bytes={core.PendingPeakBytes} reloaded_bytes={core.ReloadedBytes} tables={core.Tables} splits={core.Splits} batch_bytes={core.BatchBytes}");
    }

    /// <summary>The aggregation with the scenario's switches set on its plan.</summary>
    private static Aggregation Configured(Aggregation aggregation, Action<AggregationPlan>? configure)
    {
        s_last = ((AggregationQuery)aggregation.Query).Plan;
        configure?.Invoke(s_last);
        return aggregation;
    }

    private static async Task<long> TotalAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<KeyTotal> groups in Configured(file.Scan<Spread>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.Sum(s => s.Value))), configure)
            .As<KeyTotal>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> FourAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<KeyFour> groups in Configured(file.Scan<Spread>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.Sum(s => s.Value), g.Average(s => s.Real), g.Max(s => s.Value))), configure)
            .As<KeyFour>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> RangeAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<KeyRange> groups in Configured(file.Scan<Spread>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.Min(s => s.Value), g.Max(s => s.Value))), configure)
            .As<KeyRange>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    /// <summary>A count and the mean of a float: the exact sum of 32 bytes beside a count of 4, a record of 40.</summary>
    private static async Task<long> MeanAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<KeyMean> groups in Configured(file.Scan<Spread>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.Average(s => s.Real))), configure)
            .As<KeyMean>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> DeviationAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<KeyDeviation> groups in Configured(file.Scan<Spread>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.StandardDeviation(s => s.Real))), configure)
            .As<KeyDeviation>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> TopAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<KeyCount> groups in Configured(file.Scan<Spread>()
            .GroupBy(key)
            .OrderByDescending(g => g.Count())
            .Take(100)
            .Select(g => (g.Key, g.Count())), configure)
            .As<KeyCount>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    /// <summary>The hundred smallest keys and their counts: a top on the key, whose frontier drops the rows of the keys past it.</summary>
    private static async Task<long> FirstAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<KeyCount> groups in Configured(file.Scan<Spread>()
            .GroupBy(key)
            .OrderBy(g => g.Key)
            .Take(100)
            .Select(g => (g.Key, g.Count())), configure)
            .As<KeyCount>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> MostAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<KeyMost> groups in Configured(file.Scan<Spread>()
            .GroupBy(key)
            .OrderByDescending(g => g.Max(s => s.Value))
            .Take(100)
            .Select(g => (g.Key, g.Max(s => s.Value))), configure)
            .As<KeyMost>())
        {
            rows += groups.RowCount;
        }

        return rows;
    }

    private static async Task<long> CountDistinctAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long values = 0;
        await foreach (Columns<KeyCount> groups in Configured(file.Scan<Spread>()
            .GroupBy(key)
            .Select(g => (g.Key, g.CountDistinct(s => s.Value))), configure)
            .As<KeyCount>())
        {
            values += Sum(groups.Column<long>(1).Values);
        }

        return values;
    }

    private static async Task<long> DistinctAsync(string path, Func<Probe<Spread>, Sym<int>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        Aggregation<int> distinct = file.Scan<Spread>().Select(key).Distinct();
        configure?.Invoke(((DistinctQuery)distinct.Query).Plan);
        long values = 0;
        await foreach (int value in distinct)
        {
            values++;
        }

        return values;
    }

    private static async Task<long> UrlsAsync(string path, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<NameCount> groups in Configured(file.Scan<Page>()
            .GroupBy(p => p.Url)
            .Select(g => (g.Key, g.Count())), configure)
            .As<NameCount>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> NamesAsync(string path, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<NameCount> groups in Configured(file.Scan<Named>()
            .GroupBy(n => n.Name)
            .Select(g => (g.Key, g.Count())), configure)
            .As<NameCount>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> UrlSmallsAsync(string path, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<UrlSmallCount> groups in Configured(file.Scan<Page>()
            .GroupBy(p => (p.Url, p.Small))
            .Select(g => (g.Key.Item1, g.Key.Item2, g.Count())), configure)
            .As<UrlSmallCount>())
        {
            rows += Sum(groups.Column<long>(2).Values);
        }

        return rows;
    }

    private static async Task<long> TextExtremesAsync(string path, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long groups = 0;
        await foreach (Columns<KeyTexts> batch in Configured(file.Scan<Page>()
            .GroupBy(p => p.Key)
            .Select(g => (g.Key, g.Min(p => p.Url), g.Max(p => p.Url))), configure)
            .As<KeyTexts>())
        {
            groups += batch.RowCount;
        }

        return groups;
    }

    private static async Task<long> IdsAsync(string path, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<IdCount> groups in Configured(file.Scan<Page>()
            .GroupBy(p => p.Id)
            .Select(g => (g.Key, g.Count())), configure)
            .As<IdCount>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static async Task<long> PairsAsync(string path, Func<Probe<Draw>, Sym<int>> first, Func<Probe<Draw>, Sym<int>> second, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<PairTotal> groups in Configured(file.Scan<Draw>()
            .GroupBy(d => (first(d), second(d)))
            .Select(g => (g.Key.Item1, g.Key.Item2, g.Count(), g.Sum(d => d.Value))), configure)
            .As<PairTotal>())
        {
            rows += Sum(groups.Column<long>(2).Values);
        }

        return rows;
    }

    private static async Task<long> StridedAsync(string path, Func<Probe<Strided>, Sym<long>> key, Action<AggregationPlan>? configure)
    {
        await using VortexFile file = await ScenarioSet.OpenAsync(path);
        long rows = 0;
        await foreach (Columns<LongKeyTotal> groups in Configured(file.Scan<Strided>()
            .GroupBy(key)
            .Select(g => (g.Key, g.Count(), g.Sum(s => s.Value))), configure)
            .As<LongKeyTotal>())
        {
            rows += Sum(groups.Column<long>(1).Values);
        }

        return rows;
    }

    private static long Sum(ReadOnlySpan<long> values)
    {
        long sum = 0;
        foreach (long value in values)
        {
            sum += value;
        }

        return sum;
    }
}

/// <summary>A row of the bench's spread files: a key column per cardinality, from 10³ to 10⁷ values, a key ten rows a value and one a row a value, and two values to aggregate.</summary>
[VortexRecord]
public partial record struct Spread(int K3, int K4, int K5, int K6, int K7, int Tenfold, int Unique, long Value, double Real);

/// <summary>A row of the bench's strided file: its keys shifted left by 22 bits.</summary>
[VortexRecord]
public partial record struct Strided(long K3, long K4, long K5, long K6, long K7, long Value, double Real);

/// <summary>An integer key's rows.</summary>
[VortexRecord]
public partial record struct KeyCount(int Key, long Count);

/// <summary>An integer key's rows and the sum of their values.</summary>
[VortexRecord]
public partial record struct KeyTotal(int Key, long Count, long Total);

/// <summary>An integer key's rows, the total and the largest of a value, and the mean of a float.</summary>
[VortexRecord]
public partial record struct KeyFour(int Key, long Count, long Total, double? Mean, long? Largest);

/// <summary>An integer key's rows and the mean of a float.</summary>
[VortexRecord]
public partial record struct KeyMean(int Key, long Count, double? Mean);

/// <summary>An integer key's rows and the standard deviation of a float.</summary>
[VortexRecord]
public partial record struct KeyDeviation(int Key, long Count, double? Deviation);

/// <summary>An integer key's rows and the smallest and largest of a value.</summary>
[VortexRecord]
public partial record struct KeyRange(int Key, long Count, long? Least, long? Most);

/// <summary>A group's key and its largest value.</summary>
[VortexRecord]
public partial record struct KeyMost(int Key, long? Most);

/// <summary>A long key's rows and the sum of their values.</summary>
[VortexRecord]
public partial record struct LongKeyTotal(long Key, long Count, long Total);

/// <summary>A row of the bench's draws file: keys of 4 to a million values, a value, a price.</summary>
[VortexRecord]
public partial record struct Draw(int K4, int K100, int K1000, int K100k, int K1M, long Value, double Price);

/// <summary>A pair of integer keys' rows and the sum of their values.</summary>
[VortexRecord]
public partial record struct PairTotal(int First, int Second, long Count, long Total);

/// <summary>A row of the bench's pages file: a URL of about forty bytes among a million, a UUID among a million, a small integer, an integer key, a value.</summary>
[VortexRecord]
public partial record struct Page(string Url, Guid Id, int Small, int Key, long Value);

/// <summary>A text key's rows.</summary>
[VortexRecord]
public partial record struct NameCount(string Name, long Count);

/// <summary>A row of the bench's names files: a name, a value.</summary>
[VortexRecord]
public partial record struct Named(string Name, long Value);

/// <summary>A UUID key's rows.</summary>
[VortexRecord]
public partial record struct IdCount(Guid Id, long Count);

/// <summary>An integer key's least and greatest text.</summary>
[VortexRecord]
public partial record struct KeyTexts(int Key, string? Least, string? Greatest);

/// <summary>A text and a small integer, the pair's rows.</summary>
[VortexRecord]
public partial record struct UrlSmallCount(string Url, int Small, long Count);
