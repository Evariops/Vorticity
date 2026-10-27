// Reads in the key's order with no sorted run (spec A, 7.1; A7): objects keep no run on the key, and a read in the order
// of a column or of the whole key, upward and downward, the composite key's cursor, its seeks, and a walk of the key's
// first column, all follow each object's own order, nulls last, partitioned or not.
//
// THE ORACLE IS LINQ: the rows ordered as LINQ orders them, nulls last whichever way; the first key at or past a sought
// one, LINQ's; the first column's values, LINQ's, in order.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Keys;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyOrderReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Six appends of 10 000 rows keyed by a service and a status, one in twenty of each null, merged every second append,
    /// partitioned by day and consolidated or not: no object keeps a sorted run; a read by the service and a read by the
    /// whole key, each both ways, return every row in order, nulls last; unpartitioned, the composite cursor walks every
    /// row in order and its seeks land on LINQ's first key at or past theirs; a walk of the service gives its non-null
    /// values in order.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryReadInTheKeysOrderFollowsTheObjectsOwnOrderNullsLast(bool partitioned)
    {
        Random random = new Random(59);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, KeyRow.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = partitioned ? LayoutValue.Column(nameof(KeyRow.Day)) : null,
                SortKey = [LayoutValue.Column(nameof(KeyRow.Service)), LayoutValue.Column(nameof(KeyRow.Status))],
                ObjectBytes = 1 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        List<KeyRow> all = [];
        for (int a = 0; a < 6; a++)
        {
            KeyRow[] rows = [.. Enumerable.Range(0, 10_000).Select(i => new KeyRow(
                random.Next(0, 3),
                random.Next(0, 20) == 0 ? null : $"svc-{random.Next(0, 300):D3}",
                all.Count + i,
                random.Next(0, 20) == 0 ? null : random.Next(0, 50)))];
            all.AddRange(rows);
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<KeyRow>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            if (a % 2 == 1)
            {
                while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
                {
                }
            }
        }

        if (partitioned)
        {
            await dataset.ConsolidateAsync(Ct);
        }

        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.False(held.Entry.Contents?.Locating?.Any(static entry => entry.Kind == Vorticity.Indexes.IndexKinds.SortedRuns) == true, $"'{held.Entry.Key}' keeps a sorted run");
        }

        // The order of the key's first column, both ways.
        foreach (bool descending in (bool[])[false, true])
        {
            Scan<KeyRow> scan = descending ? dataset.Scan<KeyRow>().OrderByDescending(r => r.Service) : dataset.Scan<KeyRow>().OrderBy(r => r.Service);
            List<KeyRow> read = await scan.ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(all.Select(static row => row.Sequence), read.Select(static row => row.Sequence).Order());
            for (int i = 1; i < read.Count; i++)
            {
                string? before = read[i - 1].Service;
                string? after = read[i].Service;
                Assert.True(
                    before is null ? after is null : after is null || (descending ? string.CompareOrdinal(before, after) >= 0 : string.CompareOrdinal(before, after) <= 0),
                    $"a read by the service {(descending ? "downward" : "upward")} goes back at row {i}");
            }
        }

        // The order of the whole key, both ways.
        Dictionary<long, KeyRow> bySequence = all.ToDictionary(static row => row.Sequence);
        foreach (bool descending in (bool[])[false, true])
        {
            List<KeyRow> read = [];
            await foreach (RecordBatch batch in dataset.ScanBuilder().InKeyOrder([nameof(KeyRow.Service), nameof(KeyRow.Status)], descending).ExecuteAsync(Ct))
            {
                ReadOnlySpan<long> sequences = batch.Column(2).AsPrimitive<long>().Values;
                for (int row = 0; row < batch.RowCount; row++)
                {
                    read.Add(bySequence[sequences[row]]);
                }
            }

            Assert.Equal(all.Count, read.Count);
            for (int i = 1; i < read.Count; i++)
            {
                KeyRow before = read[i - 1];
                KeyRow after = read[i];
                Assert.True(
                    !descending ? Tuple(before, after) <= 0 : before.Service is null ? after.Service is null : after.Service is null || Tuple(before, after) >= 0,
                    $"a read by the key {(descending ? "downward" : "upward")} goes back at row {i}");
            }
        }

        // The composite key's cursor: every row once, in order, and seeks that land on LINQ's first key at or past.
        if (!partitioned)
        {
            await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, Ct);
            long entries = 0;
            byte[] previous = [];
            for (bool more = await cursor.SeekFirstAsync(Ct); more; more = await cursor.NextAsync(Ct))
            {
                byte[] key = cursor.KeyBytes.ToArray();
                Assert.True(previous.AsSpan().SequenceCompareTo(key) <= 0, $"the cursor goes back at entry {entries}");
                previous = key;
                entries++;
            }

            Assert.Equal(all.Count, entries);
            ClusteringKey composite = dataset.Snapshot.Schema.Key!;
            for (int probe = 0; probe < 60; probe++)
            {
                string service = $"svc-{random.Next(0, 300):D3}";
                int status = random.Next(0, 50);
                byte[] sought = composite.Encode([FilterLiteral.From(service), FilterLiteral.From(status)]);
                bool found = await cursor.SeekAsync(FilterLiteral.From(sought), SeekMode.AtOrAfter, Ct);
                KeyRow? first = all.Where(row => row.Service is not null && Tuple(row, new KeyRow(0, service, 0, status)) >= 0)
                    .OrderBy(static row => row, Comparer<KeyRow>.Create(Tuple)).Cast<KeyRow?>().FirstOrDefault();
                byte[]? want = first is { } row ? composite.Encode([FilterLiteral.From(row.Service!), row.Status is { } s ? FilterLiteral.From(s) : FilterLiteral.Null]) : null;
                Assert.Equal(want is not null, found);
                Assert.True(want is null || cursor.KeyBytes.SequenceEqual(want), $"a seek of ({service}, {status}) lands elsewhere than LINQ's first key at or past it");
            }
        }

        // The key's first column walked alone: its non-null values, in order.
        await using DatasetKeyCursor walk = await DatasetKeyCursor.OpenAsync(dataset.Snapshot, nameof(KeyRow.Service), distinct: false, indexes: true, Ct);
        List<string> services = [];
        for (bool more = await walk.SeekFirstAsync(Ct); more; more = await walk.NextAsync(Ct))
        {
            services.Add(System.Text.Encoding.UTF8.GetString(walk.Key.BytesValue));
        }

        Assert.Equal(all.Where(static row => row.Service is not null).Select(static row => row.Service!).Order(StringComparer.Ordinal), services);
    }

    private static int Compare(string? left, string? right) =>
        left is null ? (right is null ? 0 : 1) : right is null ? -1 : string.CompareOrdinal(left, right);

    private static int Compare(int? left, int? right) =>
        left is null ? (right is null ? 0 : 1) : right is null ? -1 : left.Value.CompareTo(right.Value);

    private static int Tuple(KeyRow left, KeyRow right)
    {
        int order = Compare(left.Service, right.Service);
        return order != 0 ? order : Compare(left.Status, right.Status);
    }

    [VortexRecord]
    public partial record struct KeyRow(int Day, string? Service, long Sequence, int? Status);
}
