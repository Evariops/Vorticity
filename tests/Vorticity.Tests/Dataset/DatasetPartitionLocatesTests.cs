// Where values lie, in the index of a consolidated partition (spec D, 7; D3, D7): each day's index names where its
// services lie in the objects it covers, the same granules each object's own index gives; a question on a value reads each
// day's index once, in one request, where it read an index of each object.
//
// THE ORACLE IS EACH OBJECT'S OWN INDEX AND LINQ: the granules an equality or a set finds through a day's pages equal those
// its objects' own indexes give; the rows read equal LINQ's.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPartitionLocatesTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two days of 600 000 rows sorted by the service then the instant, appended in four parts and consolidated into objects
    /// of 2 MiB: each day's index locates the services; for every object it covers, the granules an equality or a set finds
    /// through its pages equal those of the object's own index; a question on one service or two reads each day's index
    /// once, whole, and LINQ's rows.
    /// </summary>
    [Fact]
    public async Task ADaysIndexLocatesItsValuesAsItsObjectsDo()
    {
        Random random = new Random(29);
        Row[] rows = [.. Enumerable.Range(0, 600_000).Select(i =>
        {
            int service = random.Next(0, 50);
            return new Row(service / 8, $"svc-{service:D2}", Start.AddMilliseconds(i * 288L), i, random.NextDouble());
        })];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        LoggingStore logged = new LoggingStore(memory);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(logged, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At))],
                IndexedPrefix = 1,
                ObjectBytes = 2 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        for (int part = 0; part < 4; part++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows[(part * 150_000)..((part + 1) * 150_000)], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        await dataset.ConsolidateAsync(Ct);

        // Each day's index locates the services.
        DatasetSnapshot version = dataset.Snapshot;
        HashSet<string> indexes = [];
        await foreach (TreeEntry tree in version.Levels.PartitionIndexes.EnumerateAsync(version.Pages, Ct))
        {
            PartitionIndexEntry entry = PartitionIndexEntry.FromBytes(tree.Key.Span, tree.Value.Span);
            await using ISegmentReader reader = await SealedObjectStore.OpenDataAsync(dataset.Store, entry.ObjectKey, entry.Uid, entry.Bytes, Ct);
            PartitionIndex index = await PartitionPostings.ReadAsync(reader, Ct);
            Assert.NotNull(index.Of(nameof(Row.Service), PartitionIndex.EntryKind.Locate));
            indexes.Add(entry.ObjectKey);
        }

        Assert.Equal(2, indexes.Count);
        int service = version.Schema.DType.IndexOfField(nameof(Row.Service));
        foreach ((VortexExpr filter, Func<Probe<Row>, Predicate> asked, Func<Row, bool> linq) in new (VortexExpr, Func<Probe<Row>, Predicate>, Func<Row, bool>)[]
        {
            (Expr.Eq(Expr.Field(nameof(Row.Service)), Expr.Literal(FilterLiteral.From("svc-07"))), static r => r.Service == "svc-07", static r => r.Service == "svc-07"),
            (Expr.In(Expr.Field(nameof(Row.Service)), [FilterLiteral.From("svc-01"), FilterLiteral.From("svc-33")]),
                static r => (r.Service == "svc-01") | (r.Service == "svc-33"), static r => r.Service is "svc-01" or "svc-33"),
        })
        {
            // Through each object: the day's pages give the granules its own index gives.
            PartitionLocates partitions = PartitionLocates.Of(dataset, version, filter, [service])!;
            int objects = 0;
            await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                objects++;
                IValueGranules?[] found = (await partitions.LocatesAsync(held, Ct))!;
                ColumnLocate own = (await dataset.ReadLocatesAsync(held.Entry, [service], Ct))[0]!;
                ulong[]? viaPages = GranuleAlgebra.May(filter, version.Schema.DType, field => field == service ? found[0] : null, found[0]!.Granules);
                ulong[]? viaOwn = GranuleAlgebra.May(filter, version.Schema.DType, field => field == service ? own : null, own.Granules);
                Assert.Equal(viaOwn, viaPages);
            }

            Assert.True(objects > 2, $"{objects} objects");

            // The question: each day's index read once, whole; LINQ's rows.
            await using VortexSession session = VortexSession.Create(_ => { });
            await using VortexDataset cold = await VortexDataset.OpenAsync(logged, dataset.Options with { Session = session }, Ct);
            logged.Reads.Clear();
            List<Row> read = await cold.Scan<Row>().Where(asked).ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(rows.Where(linq), read.OrderBy(static r => r.Sequence));
            Assert.All(indexes, key => Assert.Equal(1, logged.Reads.Count(access => access.Key == key)));
            Assert.All(logged.Reads.Where(access => indexes.Contains(access.Key)), static access => Assert.Equal(access.Size, access.Length));
        }
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, DateTime At, long Sequence, double Value);
}
