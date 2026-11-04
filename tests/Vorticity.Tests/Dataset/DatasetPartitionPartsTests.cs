// The answers by parts and the expiry by partitions (spec A, 7.6; A7): a dataset partitioned by the day of its instant
// answers a question asked day by day, newest first; an expiry before an instant takes the days it covers whole, and the
// index of each such day with them.
//
// THE ORACLE IS LINQ: each part's count is the rows' of its day; the rows left after an expiry are those LINQ keeps, the
// partition indexes one fewer.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPartitionPartsTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Six appends of 15 000 rows over three days, partitioned by the day of the instant, keyed by the service, an identifier
    /// declared, merged and consolidated: asked day by day, newest first, three parts, each a day and its count LINQ's, the
    /// last covering the question whole; an expiry before ten o'clock of the second day takes the first day whole, its rows
    /// and its index, and keeps the second.
    /// </summary>
    [Fact]
    public async Task AQuestionAskedByDaysAndAnExpiryFollowThePartitions()
    {
        Random random = new Random(71);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Row.Service))],
                ObjectBytes = 1 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            Identifiers = [nameof(Row.Sequence)],
        }, Ct);
        List<Row> all = [];
        for (int a = 0; a < 6; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 15_000).Select(i => new Row(
                $"svc-{random.Next(0, 20):D2}", Start.AddTicks((long)(random.NextDouble() * 3 * TimeSpan.TicksPerDay) / 10 * 10), all.Count + i, random.NextDouble()))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        await dataset.ConsolidateAsync(Ct);

        // Asked day by day, newest first: each part one day, its count LINQ's.
        List<ValuePart<DateTime, long>> parts = [];
        await foreach (ValuePart<DateTime, long> part in dataset.AskByValuesAsync<DateTime, long>(
            Start, Start.AddDays(3), async (from, to, ct) => await dataset.Scan<Row>().Where(r => r.At >= from & r.At < to).CountAsync(ct), cancellationToken: Ct))
        {
            parts.Add(part);
        }

        Assert.Equal(3, parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            DateTime from = Start.AddDays(2 - i);
            Assert.Equal((from, from.AddDays(1), all.LongCount(row => row.At >= from && row.At < from.AddDays(1))), (parts[i].From, parts[i].To, parts[i].Answer));
        }

        Assert.Equal(1, parts[^1].Covered);

        // An expiry before the second day's ten o'clock: the first day's objects go whole, and its index.
        int indexes = await IndexCountAsync(dataset);
        await dataset.ExpireAsync<Row>(r => r.At < Start.AddDays(1).AddHours(10), Ct);
        all.RemoveAll(row => row.At < Start.AddDays(1));
        Assert.Equal(all.Count, await dataset.Scan<Row>().CountAsync(Ct));
        Assert.Equal(indexes - 1, await IndexCountAsync(dataset));
    }

    private static async Task<int> IndexCountAsync(VortexDataset dataset)
    {
        DatasetSnapshot version = dataset.Snapshot;
        return version.Levels.PartitionIndexes.IsEmpty ? 0 : await version.Levels.PartitionIndexes.EnumerateAsync(version.Pages, Ct).CountAsync(Ct);
    }

    [VortexRecord]
    public partial record struct Row(string Service, DateTime At, long Sequence, double Value);
}
