// Reads in the order of the columns a key's components order, computed ones included, partitioned or not (the primary key
// plan, K2): rows ordered by (f(c), c) are ordered by c, and a read in that order needs no sorted run.
//
// THE ORACLE IS LINQ: the rows ordered as LINQ orders them, nulls last whichever way.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyOrderReadTests
{
    /// <summary>
    /// Under the key <c>(Service, Bucket(Sequence, 1000), Sequence)</c>, partitioned by day or not, merged and consolidated:
    /// no object keeps a sorted run, and a read by the service then the sequence, both ways, returns every row in order. It
    /// asked for a sorted run of each object before.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadInTheOrderOfAComputedKeysColumnsNeedsNoRun(bool partitioned)
    {
        Random random = new Random(66);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, KeyRow.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = partitioned ? LayoutValue.Column(nameof(KeyRow.Day)) : null,
                SortKey = [LayoutValue.Column(nameof(KeyRow.Service)), LayoutValue.Column(nameof(KeyRow.Sequence)).Bucket(1_000), LayoutValue.Column(nameof(KeyRow.Sequence))],
                ObjectBytes = 256 << 10,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        List<KeyRow> all = await AppendAsync(dataset, random, appends: 6, rows: 10_000);
        Assert.Equal([nameof(KeyRow.Service), nameof(KeyRow.Sequence)], dataset.Snapshot.Schema.ObjectOrder!.Paths);

        Dictionary<long, KeyRow> bySequence = all.ToDictionary(static row => row.Sequence);
        foreach (bool descending in (bool[])[false, true])
        {
            List<KeyRow> read = await ReadAsync(dataset, [nameof(KeyRow.Service), nameof(KeyRow.Sequence)], descending, bySequence, null);
            Assert.Equal(all.Count, read.Count);
            for (int i = 1; i < read.Count; i++)
            {
                (string? before, string? after) = (read[i - 1].Service, read[i].Service);
                int order = before is null ? (after is null ? 0 : 1) : after is null ? -1 : string.CompareOrdinal(before, after);
                order = order != 0 ? order : read[i - 1].Sequence.CompareTo(read[i].Sequence);
                Assert.True(
                    descending ? (before is null ? after is null : after is null || order >= 0) : order <= 0,
                    $"a read by the service then the sequence {(descending ? "downward" : "upward")} goes back at row {i}");
            }
        }
    }

    /// <summary>
    /// <paramref name="appends"/> appends of <paramref name="rows"/> rows over three days and 300 services, one in twenty
    /// null, merged every second append, then consolidated; no object keeps a sorted run.
    /// </summary>
    private static async Task<List<KeyRow>> AppendAsync(VortexDataset dataset, Random random, int appends, int rows)
    {
        List<KeyRow> all = [];
        for (int a = 0; a < appends; a++)
        {
            KeyRow[] batch = [.. Enumerable.Range(0, rows).Select(i => new KeyRow(
                random.Next(0, 3),
                random.Next(0, 20) == 0 ? null : $"svc-{random.Next(0, 300):D3}",
                all.Count + i,
                random.Next(0, 20) == 0 ? null : random.Next(0, 50)))];
            all.AddRange(batch);
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<KeyRow>(batch, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            if (a % 2 == 1)
            {
                while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
                {
                }
            }
        }

        await dataset.ConsolidateAsync(Ct);
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.False(held.Entry.Contents?.Locating?.Any(static entry => entry.Kind == Vorticity.Indexes.IndexKinds.SortedRuns) == true, $"'{held.Entry.Key}' keeps a sorted run");
        }

        return all;
    }

    /// <summary>Every row a read in the order of <paramref name="paths"/> returns, in its order, its most open cursors counted in <paramref name="counters"/>.</summary>
    private static async Task<List<KeyRow>> ReadAsync(
        VortexDataset dataset, string[] paths, bool descending, Dictionary<long, KeyRow> bySequence, DatasetScanCounters? counters)
    {
        List<KeyRow> read = [];
        DatasetScanBuilder scan = dataset.ScanBuilder().InKeyOrder(paths, descending);
        if (counters is not null)
        {
            scan.WithMetrics(counters);
        }

        await foreach (RecordBatch batch in scan.ExecuteAsync(Ct))
        {
            ReadOnlySpan<long> sequences = batch.Column(2).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                read.Add(bySequence[sequences[row]]);
            }
        }

        return read;
    }
}
