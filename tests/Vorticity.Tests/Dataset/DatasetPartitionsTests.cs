// A dataset by periods whose key leads with a partition column, (Retention, At): appends split into an object
// per retention value, each partition growing its own periods, and no job taking objects of two.
//
// WHAT IS HELD: interleaved appends over three values rewrite each row about as often as the same rows appended
// in order without partitions; every object above level 0 holds one value; the answers are those of the same
// rows without partitions, from the tables under a filter on the partition; an append past the bound on values
// commits nothing; an update that moves a row to another partition writes an object per value.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Scanning;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPartitionsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Retentions = ["audit", "debug", "info"];

    private static readonly string[] Services = ["api", "auth", "billing", "cart", "search"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InterleavedPartitionsRewriteEachRowAsOftenAsAppendsInOrder()
    {
        await using MemoryObjectStore partitionedStore = new MemoryObjectStore();
        await using MemoryObjectStore plainStore = new MemoryObjectStore();
        (VortexDataset partitioned, long partitionedRewrites) = await FilledAsync(partitionedStore, partitions: true);
        (VortexDataset plain, long plainRewrites) = await FilledAsync(plainStore, partitions: false);
        await using (partitioned)
        await using (plain)
        {
            double perRow = (double)partitionedRewrites / Rows;
            double inOrder = (double)plainRewrites / Rows;
            Console.Out.Write(FormattableString.Invariant($"PARTITIONS: {perRow:F2} rewrites a row interleaved over three partitions, {inOrder:F2} in order without.\n"));
            Assert.InRange(perRow, 0, inOrder * 1.25);

            // Every object holds one partition, and each partition's two closed hours are sealed apart.
            Assert.True(await OnePartitionEachAsync(partitioned));
            int sealedObjects = 0;
            int plainSealed = 0;
            await foreach (PositionedObject held in partitioned.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
            {
                sealedObjects += held.Level == PeriodLevels.Periods ? 1 : 0;
            }

            await foreach (PositionedObject held in plain.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
            {
                plainSealed += held.Level == PeriodLevels.Periods ? 1 : 0;
            }

            Assert.Equal(2, plainSealed);
            Assert.Equal(plainSealed * Retentions.Length, sealedObjects);
        }
    }

    /// <summary>
    /// The answers of a partitioned dataset are those of the same rows without partitions, from the tables: a filter
    /// on the partition decided by the one value each object holds, a grouping by it carried as that value.
    /// </summary>
    [Fact]
    public async Task APartitionedDatasetAnswersAsThePlainOne()
    {
        await using MemoryObjectStore partitionedStore = new MemoryObjectStore();
        await using MemoryObjectStore plainStore = new MemoryObjectStore();
        (VortexDataset partitioned, _) = await FilledAsync(partitionedStore, partitions: true);
        (VortexDataset plain, _) = await FilledAsync(plainStore, partitions: false);
        await using (partitioned)
        await using (plain)
        {
            foreach (string question in new[] { "debug by service", "not audit by service, a window", "by retention and service", "every row" })
            {
                (List<string> answer, ScanMetrics metrics) = await AskAsync(partitioned, question, synopses: true);
                (List<string> rows, _) = await AskAsync(partitioned, question, synopses: false);
                (List<string> reference, _) = await AskAsync(plain, question, synopses: false);
                Console.Out.Write($"PARTITIONED {question}: {metrics.SynopsisRows} table rows, {metrics.Rows} rows.\n");
                Assert.Equal(reference, rows);
                Assert.Equal(reference, answer);
                Assert.True(metrics.SynopsisRows > 0, $"'{question}' folded no table");
            }
        }
    }

    /// <summary>An append that holds more partition values than the dataset allows commits nothing.</summary>
    [Fact]
    public async Task AnAppendPastTheBoundOnValuesCommitsNothing()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Logged.Schema, Options(partitions: true) with { MaxPartitionValues = 2 }, Ct);
        ulong version = dataset.Version;
        Logged[] rows = [.. Retentions.Select((retention, i) => new Logged(retention, Noon.AddSeconds(i), "api", i))];
        await Assert.ThrowsAsync<ArgumentException>(async () => await dataset.AppendAsync(Batches(rows), Ct));
        Assert.Equal(version, dataset.Version);
        Assert.Equal(0, await dataset.Scan<Logged>().CountAsync(Ct));

        // A draft of mixed values is split once written, and refused the same.
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Logged>(rows, Ct);
        await Assert.ThrowsAsync<ArgumentException>(async () => await dataset.AppendAsync(draft, Ct));
        Assert.Equal(version, dataset.Version);
    }

    /// <summary>
    /// An append of mixed values, as one draft or as batches, adds an object per value in one commit; an update that
    /// moves a row to another partition writes an object per value as well.
    /// </summary>
    [Fact]
    public async Task MixedRowsAreWrittenAnObjectPerValue()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Logged.Schema, Options(partitions: true), Ct);
        Logged[] rows = [.. Enumerable.Range(0, 90).Select(i => new Logged(Retentions[i % 3], Noon.AddSeconds(i), Services[i % 5], i))];
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Logged>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        await dataset.AppendAsync(Batches([.. rows.Select(r => r with { At = r.At.AddMinutes(10) })]), Ct);
        Assert.Equal(6, await CountObjectsAsync(dataset));
        Assert.True(await OnePartitionEachAsync(dataset));

        RowChangeResult moved = await dataset.UpdateAsync<Logged>(r => r.Bytes < 6, r => r with { Retention = "audit" }, Ct);
        Assert.True(moved.Rows > 0);
        Assert.True(await OnePartitionEachAsync(dataset));
        Assert.Equal(2 * rows.Count(r => r.Retention == "audit" || r.Bytes < 6), await dataset.Scan<Logged>().Where(r => r.Retention == "audit").CountAsync(Ct));
    }

    private const int Rows = 16 * 400;

    /// <summary>
    /// Two hours and a quarter of rows, every 1.25 s, appended in sixteen parts, the retention cycling row by row, and
    /// compacted until nothing is due: the rows each job rewrote, summed.
    /// </summary>
    private static async Task<(VortexDataset Dataset, long Rewrites)> FilledAsync(MemoryObjectStore store, bool partitions)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Logged.Schema, Options(partitions), Ct);
        Random random = new Random(71);
        long rewrites = 0;
        for (int part = 0; part < 16; part++)
        {
            Logged[] rows = [.. Enumerable.Range(0, 400).Select(i => new Logged(
                Retentions[random.Next(Retentions.Length)],
                Noon.AddSeconds((part * 400 + i) * 1.25),
                Services[random.Next(Services.Length)],
                random.NextInt64(1, 1L << 40)))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Logged>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);

            // Every append leaves level 0 at once, so that both datasets reach the same state: a ceiling that keeps
            // the last appends in level 0 seals the hours of some partitions and not others.
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is { } done)
            {
                rewrites += done.Rows;
            }
        }

        return (dataset, rewrites);
    }

    private static DatasetOptions Options(bool partitions) => new DatasetOptions
    {
        ClusteringKey = partitions ? [nameof(Logged.Retention), nameof(Logged.At)] : [nameof(Logged.At)],
        PartitionColumns = partitions ? 1 : 0,
        Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
        Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
    };

    private static async Task<(List<string> Groups, ScanMetrics Metrics)> AskAsync(VortexDataset dataset, string question, bool synopses)
    {
        Scan<Logged> scan = dataset.Scan<Logged>().With(new ScanOptions { UseSynopses = synopses });
        DateTimeOffset from = Noon.AddHours(1);
        DateTimeOffset to = Noon.AddHours(2);
        Vorticity.Aggregation aggregation = question switch
        {
            "debug by service" => scan.Where(r => r.Retention == "debug").GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
            "not audit by service, a window" => scan.Where(r => r.Retention != "audit" && r.At >= from && r.At < to).GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
            "by retention and service" => scan.GroupBy(r => (r.Retention, r.Service)).Select(g => (g.Key.Retention, g.Key.Service, g.Count())),
            _ => scan.GroupBy(r => r.At.Truncate(CalendarUnit.Hour)).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes))),
        };

        List<string> groups = [];
        IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(Ct);
        await using (batches)
        {
            while (await batches.MoveNextAsync())
            {
                Values.DescribeRows(batches.Current, groups);
            }
        }

        groups.Sort(StringComparer.Ordinal);
        return (groups, aggregation.Metrics);
    }

    /// <summary>The rows as the batches a scan of a file of them gives.</summary>
    private static async IAsyncEnumerable<RecordBatch> Batches(Logged[] rows)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-partitions-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Logged>(path))
            {
                await writer.WriteAsync<Logged>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            await foreach (RecordBatch batch in file.Scan<Logged>().ToBatchesAsync(Ct))
            {
                yield return batch;
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<int> CountObjectsAsync(VortexDataset dataset)
    {
        int objects = 0;
        await foreach (DataObject _ in dataset.ListObjectsAsync(Ct))
        {
            objects++;
        }

        return objects;
    }

    /// <summary>Whether every object of the version holds one value of the partition column.</summary>
    private static async Task<bool> OnePartitionEachAsync(VortexDataset dataset)
    {
        await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            if (!held.Entry.Summaries.TryGet(nameof(Logged.Retention), out ColumnSummary retention) || !retention.Min.Equals(retention.Max))
            {
                return false;
            }
        }

        return true;
    }

    [VortexRecord]
    public partial record struct Logged(string Retention, DateTimeOffset At, string Service, long Bytes);
}
