using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// A group by over a dataset: its objects read side by side, each a range of the pass's queue; the
/// same bits whatever the objects the rows are cut into, the degree, the rows deleted and the
/// compaction that follows, a float sum, a mean and a variance among them, whose center a dataset
/// takes from no bound that a compaction would tighten.
/// </summary>
public sealed partial class DatasetGroupTests
{
    private const int Rows = 90_000;

    private static readonly string[] Desks = ["rates", "fx", "credit", "equity", "commodities"];

    [Fact]
    public async Task AGroupByOnADatasetIsTheSameBitsWhateverItsObjectsDegreeAndCompaction()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows);
        try
        {
            string? expected = null;
            foreach (int objects in (int[])[1, 2, 7])
            {
                await using MemoryObjectStore store = new MemoryObjectStore();
                await using VortexDataset dataset = await VortexDataset.CreateAsync(
                    store, VortexTypes.ToDType(Trade.Schema, new Vorticity.Types.DTypeArena()), Options(), Ct);
                await AppendAsync(dataset, path, objects);
                Assert.Equal(objects, dataset.ObjectCount);

                // Rows deleted in every object and marked there: what is read is the live rows, never
                // an object's file statistics, and an object cut into ranges is cut among its live rows.
                await dataset.DeleteAsync<Trade>(r => r.Size < 1_000.0f, Ct);
                Assert.Equal(objects, await MarkedAsync(dataset));
                foreach (bool compacted in (bool[])[false, true])
                {
                    while (compacted && await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
                    {
                    }

                    foreach (int degree in (int[])[1, 4])
                    {
                        string answers = await AnswersAsync(dataset, degree);
                        expected ??= answers;
                        Assert.Equal(expected, answers);
                    }
                }
            }

            // What .NET counts over the rows kept.
            foreach (IGrouping<string, Trade> desk in rows.Where(r => !(r.Size < 1_000.0f)).GroupBy(r => r.Desk))
            {
                Assert.Contains($"{desk.Key}={desk.Count()}/", expected!, StringComparison.Ordinal);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task TheCoreOnADatasetIsTheSameBitsWhateverItsObjectsDegreeAndCompaction()
    {
        // Thirty thousand keys over the objects' pieces (PLAN-HIGH-CARDINALITY, H12): the core asked
        // for, at sizes that make every batch burst and split, and turned to under a budget a fifth of
        // what the lanes' tables hold, against the lanes' tables; one set of 256 parts for every object
        // of the query, the same bits whatever the objects, the degree, the rows deleted and the
        // compaction, and no request to the store the lanes' tables would not make.
        Trade[] rows = Trades();
        string path = await WriteAsync(rows);
        try
        {
            string? expected = null;
            foreach (int objects in (int[])[1, 2, 7])
            {
                await using MemoryObjectStore memory = new MemoryObjectStore();
                await using CountingObjectStore store = new CountingObjectStore(memory);
                await using VortexDataset dataset = await VortexDataset.CreateAsync(
                    store, VortexTypes.ToDType(Trade.Schema, new Vorticity.Types.DTypeArena()), Options(), Ct);
                await AppendAsync(dataset, path, objects);
                await dataset.DeleteAsync<Trade>(r => r.Size < 1_000.0f, Ct);
                foreach (bool compacted in (bool[])[false, true])
                {
                    while (compacted && await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
                    {
                    }

                    foreach (int degree in (int[])[1, 4])
                    {
                        // Once to warm what the dataset keeps of its objects, then counted.
                        expected ??= await KeyedAsync(dataset, degree, Mode.Tables);
                        Assert.Equal(expected, await KeyedAsync(dataset, degree, Mode.Tables));
                        long before = store.Requests;
                        Assert.Equal(expected, await KeyedAsync(dataset, degree, Mode.Tables));
                        long tables = store.Requests - before;
                        foreach (Mode mode in (Mode[])[Mode.Asked, Mode.Pressed])
                        {
                            before = store.Requests;
                            Assert.Equal(expected, await KeyedAsync(dataset, degree, mode));
                            Assert.Equal(tables, store.Requests - before);
                        }
                    }
                }
            }

            // What .NET counts over the rows kept.
            Assert.Equal(rows.Where(r => !(r.Size < 1_000.0f)).Select(r => r.Account).Distinct().Count(), expected!.Split(';').Length);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task ADatasetsObjectsAreReadSideBySide()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows);
        try
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(
                store, VortexTypes.ToDType(Trade.Schema, new Vorticity.Types.DTypeArena()), new DatasetOptions { Seed = 11 }, Ct);
            await AppendAsync(dataset, path, 8);

            Vorticity.Aggregation desks = dataset.Scan<Trade>().With(new ScanOptions { DegreeOfParallelism = 4 })
                .GroupBy(r => r.Desk).Select(g => (g.Key, g.Count(), g.Sum(x => x.Price)));
            List<DeskSum> read = await ListAsync(desks.As<DeskSum>());
            AggregationRun run = desks.Plan.LastRun!;
            Assert.True(run.Lanes.Length > 1, $"{run.Lanes.Length} lanes");
            Assert.True(run.Lanes.Sum(lane => lane.Ranges) >= 8, $"{run.Lanes.Sum(lane => lane.Ranges)} ranges");
            Assert.Equal(
                rows.GroupBy(r => r.Desk).Select(g => (g.Key, (long)g.Count())).OrderBy(g => g.Key, StringComparer.Ordinal),
                read.Select(d => (d.Desk, d.Count)).OrderBy(g => g.Desk, StringComparer.Ordinal));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AnObjectIsCutBetweenItsChunksSoNoneIsReadTwice()
    {
        Trade[] rows = Trades();
        string path = await WriteAsync(rows);
        try
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(
                store, VortexTypes.ToDType(Trade.Schema, new Vorticity.Types.DTypeArena()), Options(), Ct);
            await AppendAsync(dataset, path, 1);
            await dataset.DeleteAsync<Trade>(r => r.Size < 1_000.0f, Ct);
            Assert.Equal(1, await MarkedAsync(dataset));

            // One object of many chunks, read on one lane and then on four: the four take it in
            // ranges, each a run of whole chunks, so every segment is asked for once either way.
            (string Answers, ScanStatistics Statistics, AggregationRun Run)[] reads = new (string, ScanStatistics, AggregationRun)[2];
            foreach ((int at, int degree) in ((int, int)[])[(0, 1), (1, 4)])
            {
                Scan<Trade> scan = dataset.Scan<Trade>().With(new ScanOptions { DegreeOfParallelism = degree });
                Vorticity.Aggregation desks = scan.GroupBy(r => r.Desk).Select(g => (g.Key, g.Count(), g.Sum(x => x.Price)));
                List<DeskSum> read = await ListAsync(desks.As<DeskSum>());
                reads[at] = (
                    string.Join(";", read.OrderBy(d => d.Desk, StringComparer.Ordinal).Select(d => $"{d.Desk}={d.Count}/{BitConverter.DoubleToInt64Bits(d.Sum):X}")),
                    scan.Statistics,
                    desks.Plan.LastRun!);
            }

            Assert.True(reads[1].Run.Lanes.Sum(lane => lane.Ranges) >= 4, $"{reads[1].Run.Lanes.Sum(lane => lane.Ranges)} ranges");
            Assert.Equal(reads[0].Answers, reads[1].Answers);
            Assert.Equal(reads[0].Statistics.Requests, reads[1].Statistics.Requests);
            Assert.Equal(reads[0].Statistics.BytesRequested, reads[1].Statistics.BytesRequested);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task AGroupByOnTheClusteringKeyStreamsOverTheObjects()
    {
        // Six objects of rows in no order, their days overlapping: the key-ordered read merges them.
        Random random = new Random(7);
        Tick[] ticks = [.. Enumerable.Range(0, 60_000).Select(i => new Tick(random.Next(400), Desks[i % Desks.Length], i % 97))];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, VortexTypes.ToDType(Tick.Schema, new Vorticity.Types.DTypeArena()), new DatasetOptions { Seed = 13, ClusteringKey = ["Day"] }, Ct);
        for (int o = 0; o < 6; o++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Tick>(ticks[(o * 10_000)..((o + 1) * 10_000)], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        await dataset.DeleteAsync<Tick>(r => r.Value == 0, Ct);
        Vorticity.Aggregation byDay = dataset.Scan<Tick>().GroupBy(r => r.Day).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
        Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)byDay.Query) >= 0);
        List<DayTotal> days = await ListAsync(byDay.As<DayTotal>());

        // In the key's order, as the rows' groups say, each day out once its last row is read.
        Assert.Equal(
            ticks.Where(t => t.Value != 0).GroupBy(t => t.Day).OrderBy(g => g.Key).Select(g => new DayTotal(g.Key, g.Count(), g.Sum(t => t.Value))),
            days);
        Assert.True(((AggregationQuery)byDay.Query).PeakGroups < days.Count, $"{((AggregationQuery)byDay.Query).PeakGroups} groups held");

        // The last days first, under a take: the objects merged in the key's order backwards.
        Vorticity.Aggregation lastDays = dataset.Scan<Tick>().GroupBy(r => r.Day).OrderByDescending(g => g.Key).Take(5).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
        Assert.True(StreamingGroupBatches.Streaming((AggregationQuery)lastDays.Query) >= 0);
        Assert.Equal(days.AsEnumerable().Reverse().Take(5), await ListAsync(lastDays.As<DayTotal>()));
        Assert.True(((AggregationQuery)lastDays.Query).PeakGroups < 10, $"{((AggregationQuery)lastDays.Query).PeakGroups} groups held");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Chunks of two blocks of a thousand rows, so an object holds many; deleted rows marked in the
    /// object's entry, however small the object, rather than rewritten out of it.
    /// </summary>
    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 11,
        MarkedObjectBytes = 0,
        MarkedVectorBytes = 1 << 20,
        Write = new VortexWriteOptions { BlockRows = 1_024, ChunkTargetBytes = 16 << 10 },
    };

    /// <summary>How many of the dataset's objects hold marked rows.</summary>
    private static async Task<int> MarkedAsync(VortexDataset dataset)
    {
        int marked = 0;
        await foreach (DataObject held in dataset.ObjectsAsync(Ct))
        {
            marked += held.DeletedRows > 0 ? 1 : 0;
        }

        return marked;
    }

    /// <summary>
    /// The rows cut into <paramref name="objects"/> objects of uneven sizes, appended in their order,
    /// in batches of two thousand rows: a chunk is sealed at each, so an object holds many.
    /// </summary>
    private static async Task AppendAsync(VortexDataset dataset, string path, int objects)
    {
        await using VortexFile open = await VortexFile.OpenAsync(path, Ct);
        long start = 0;
        for (int o = 0; o < objects; o++)
        {
            long end = o == objects - 1 ? Rows : start + ((Rows - start) * 2 / (objects - o + 1));
            await dataset.AppendAsync(open.Scan<Trade>().With(new ScanOptions { BatchRows = 2_048 }).Rows(new RowRange(start, end)).ToBatchesAsync(Ct), Ct);
            start = end;
        }
    }

    /// <summary>Each desk's count, its float sum's and mean's and variance's bits, and its extremes, as text.</summary>
    private static async Task<string> AnswersAsync(VortexDataset dataset, int degree)
    {
        Vorticity.Aggregation desks = dataset.Scan<Trade>().With(new ScanOptions { DegreeOfParallelism = degree })
            .GroupBy(r => r.Desk)
            .Select(g => (g.Key, g.Count(), g.Sum(x => x.Price), g.Average(x => x.Size), g.Variance(x => x.Level), g.Min(x => x.Level), g.Max(x => x.Price)));
        List<DeskStats> read = await ListAsync(desks.As<DeskStats>());
        return string.Join(";", read.OrderBy(d => d.Desk, StringComparer.Ordinal).Select(d =>
            $"{d.Desk}={d.Count}/{BitConverter.DoubleToInt64Bits(d.Sum):X}/{BitConverter.DoubleToInt64Bits(d.Mean ?? 0):X}/{BitConverter.DoubleToInt64Bits(d.Variance ?? 0):X}/{d.Lowest}/{d.Highest}"));
    }

    /// <summary>
    /// Prices over many binades with nulls; sizes as singles; levels around a large offset, which a
    /// variance centered at zero still takes exactly; thirty thousand accounts in no order.
    /// </summary>
    private static Trade[] Trades()
    {
        Random random = new Random(41);
        Trade[] rows = new Trade[Rows];
        for (int row = 0; row < Rows; row++)
        {
            double? price = row % 29 == 0 ? null : Math.ScaleB(random.NextDouble() + 0.5, random.Next(-12, 13)) * (random.Next(3) == 0 ? -1 : 1);
            rows[row] = new Trade(Desks[row % 11 % Desks.Length], price, (float)(random.NextDouble() * 1e4), 1_000_000 + random.Next(1_000), (int)((long)row * 7_919 % 30_011));
        }

        return rows;
    }

    /// <summary>How a group by holds its groups: the lanes' tables alone, the core asked for, or the lanes turned to it mid-pass as under pressure.</summary>
    private enum Mode
    {
        Tables,
        Asked,
        Pressed,
    }

    /// <summary>Each account's count, its float sum's bits and its highest level, as text, the groups held as <paramref name="mode"/> says.</summary>
    private static async Task<string> KeyedAsync(VortexDataset dataset, int degree, Mode mode)
    {
        Vorticity.Aggregation accounts = dataset.Scan<Trade>().With(new ScanOptions { DegreeOfParallelism = degree, BatchRows = 2_048 })
            .GroupBy(r => r.Account)
            .Select(g => (g.Key, g.Count(), g.Sum(x => x.Price), g.Max(x => x.Level)));
        AggregationPlan plan = accounts.Plan;
        switch (mode)
        {
            case Mode.Asked:
                plan.Core = true;
                plan.CoreLanes = 1;
                plan.CoreCapacity = 512;
                plan.CoreFloor = 16;
                plan.CoreTableGroups = 256;
                plan.CoreBatchEntries = 64;
                break;
            case Mode.Pressed:
                plan.CoreTurnAt = 2;
                break;
        }

        List<AccountStats> read = await ListAsync(accounts.As<AccountStats>());
        Assert.Equal(mode != Mode.Tables, plan.LastRun?.Core is not null);
        return string.Join(";", read.OrderBy(a => a.Account).Select(a => $"{a.Account}={a.Count}/{BitConverter.DoubleToInt64Bits(a.Sum):X}/{a.Highest}"));
    }

    private static async Task<List<T>> ListAsync<T>(Scan<T> scan)
        where T : IVortexRecord<T>
    {
        List<T> rows = [];
        await foreach (T row in scan.ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<string> WriteAsync(Trade[] rows)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "dataset-groups");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"trades-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Trade>(path))
        {
            await writer.WriteAsync<Trade>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Trade(string Desk, double? Price, float Size, int Level, int Account);

    [VortexRecord]
    public partial record struct AccountStats(int Account, long Count, double Sum, int Highest);

    [VortexRecord]
    public partial record struct DeskStats(string Desk, long Count, double Sum, double? Mean, double? Variance, int Lowest, double? Highest);

    [VortexRecord]
    public partial record struct DeskSum(string Desk, long Count, double Sum);

    [VortexRecord]
    public partial record struct Tick(int Day, string Desk, long Value);

    [VortexRecord]
    public partial record struct DayTotal(int Day, long Count, long Sum);
}
