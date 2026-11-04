// An object's summaries with what its keys prove are made once for a schema and a tree key and kept on its entry: a question
// asks them of each object a dozen times as it plans, and each was a sorted copy. Under another tree key they are bounded
// again, as the schema bounds them there.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetBoundedSummariesTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnObjectsBoundedSummariesAreMadeOnceForASchemaAndATreeKey()
    {
        Random random = new Random(71);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At))],
                ObjectBytes = 128 << 10,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        for (int append = 0; append < 4; append++)
        {
            Row[] rows = [.. Enumerable.Range(0, 10_000).Select(i =>
                new Row($"svc-{random.Next(0, 6):D2}", Start.AddTicks((long)(random.NextDouble() * 2 * TimeSpan.TicksPerDay) / 10 * 10), i))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
        {
        }

        DatasetSchema schema = dataset.Snapshot.Schema;
        List<PositionedObject> objects = [.. await dataset.ScanBuilder().ObjectsAsync(Ct).ToListAsync(Ct)];
        Assert.True(objects.Count > 1, $"{objects.Count} objects");
        for (int i = 0; i < objects.Count; i++)
        {
            PositionedObject held = objects[i];
            ObjectSummaries first = held.Summaries(schema);
            Assert.NotSame(held.Entry.Summaries, first);
            Assert.Same(first, held.Summaries(schema));

            PositionedObject moved = held with { TreeKey = objects[(i + 1) % objects.Count].TreeKey };
            ObjectSummaries there = moved.Summaries(schema);
            Assert.Equal(
                schema.Bounded(held.Entry.Summaries, VortexDataset.OrderOf(moved.TreeKey).Span, held.Entry.LastKey.Span, exact: !held.Entry.HasDeletions),
                there);
            Assert.Equal(first, held.Summaries(schema));
        }
    }

    [VortexRecord]
    public partial record struct Row(string Service, DateTime At, long Value);
}
