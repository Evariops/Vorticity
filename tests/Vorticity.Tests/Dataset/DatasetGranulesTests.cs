// The granules of a dataset's objects, against each object's own rows: every object a job writes in its key's order cuts
// its rows into granules of the writer's block rows, whose statistics are read in one request with no open of the object,
// and counts the rows of the granules that hold one value of the key's indexed prefix.
//
// THE ORACLE IS THE OBJECT'S ROWS: each granule is held to the values of the key's first column the object's rows hold in
// order.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetGranulesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Sources whose runs are a few rows, thousands, or more than two granules, arrived in random order and compacted under
    /// a key of the source then the instant, the source alone indexed: each object a job writes keeps statistics over its
    /// granules, which cover its rows once, each of the writer's block rows but the last; the rows of those of one source
    /// are the ones its table of contents counts.
    /// </summary>
    [Fact]
    public async Task EveryGranuleHoldsABlockOfRowsAndThoseOfOneSourceAreCounted()
    {
        Random random = new Random(0xB10C);
        List<Event> rows = [];
        for (int source = 0; source < 60; source++)
        {
            int runs = (source % 3) switch
            {
                0 => random.Next(1, 40),
                1 => random.Next(500, 6_000),
                _ => random.Next(20_000, 150_000),
            };

            for (int i = 0; i < runs; i++)
            {
                rows.Add(new Event($"source-{source:D3}", random.NextInt64(1_000_000_000), i));
            }
        }

        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Event.Source)), LayoutValue.Column(nameof(Event.At))],
                IndexedPrefix = 1,
                ObjectBytes = 2 << 20,
            },
            ContentsObjectBytes = 1,
            SheetObjectBytes = 0,
        }, Ct);
        for (int from = 0; from < rows.Count; from += 100_000)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Event>(rows.Skip(from).Take(100_000).ToArray(), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        int blockRows = new VortexWriteOptions().BlockRows;
        int objects = 0;
        int granules = 0;
        int single = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is not { GranuleStats.Count: > 0 } contents)
            {
                continue;
            }

            objects++;
            store.Reset();
            (GranuleStats?[] stats, _, _) = await dataset.ReadGranuleStatsAsync(held.Entry, [0], [], [], Ct);
            Assert.Equal(1, store.Requests);
            GranuleStats cut = Assert.IsType<GranuleStats>(stats[0]);
            string[] sources = await SourcesAsync(dataset, held.Entry);
            Assert.Equal(held.Entry.Rows, cut.TotalRows);
            long pure = 0;
            for (int g = 0; g < cut.Count; g++)
            {
                int start = (int)cut.FirstRow(g);
                int count = (int)cut.Rows[g];
                Assert.Equal((long)g * blockRows, start);
                Assert.Equal(g == cut.Count - 1 ? sources.Length - start : blockRows, count);
                bool one = sources[start] == sources[start + count - 1];
                pure += one ? count : 0;
                single += one ? 1 : 0;
            }

            Assert.Equal(pure, contents.PureGranuleRows);
            granules += cut.Count;
        }

        Console.WriteLine($"GRANULES: {objects} objects, {granules} granules, {single} of one source");
        Assert.True(objects >= 2, $"{objects} objects keep statistics over their granules");
        Assert.InRange(single, 1, granules - 1);
    }

    /// <summary>The source of each of the object's rows, in its order.</summary>
    private static async Task<string[]> SourcesAsync(VortexDataset dataset, ObjectEntry entry)
    {
        await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
        return [.. (await lease.File.Scan<Event>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(row => row.Source)];
    }

    [VortexRecord]
    public partial record struct Event(string Source, long At, long Value);
}
