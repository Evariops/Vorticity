// The key's pruning (spec A, 7.2 and 7.3; A7): a filter on the key's components judged on the boxes its keys bound, the
// objects by their first and last keys, then each kept object's granules by an exclusion search over its sparse index.
//
// THE ORACLE IS THE ROWS AND A MODEL OF THE GRANULES: each question returns and counts the rows LINQ keeps; in each object,
// the granules that hold a row it keeps are kept, and the search keeps at most one granule more at each end of a range it
// keeps, where a key that starts or ends a granule may still lie in the filter's box.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetKeyPruningTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Eight appends of 20 000 rows over two days, partitioned by day, keyed by the service then the hour of the instant,
    /// merged and consolidated. Questions on the service, on the hour alone, on both, on a range of services and on the day
    /// and an hour each return the rows LINQ keeps and count them; the selective ones pass objects or granules over by the
    /// key; and each object's kept granules hold every granule a kept row lies in, and at most one more at each end of a
    /// range.
    /// </summary>
    [Fact]
    public async Task AFilterOnTheKeyKeepsTheGranulesItsRowsLieInAndReadsLinqsRows()
    {
        Random random = new Random(61);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour)],
                ObjectBytes = 1 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,

            // Columns of several chunks, so that the sparse index spares chunks and is read (VortexDataset.ReadsKeyIndex).
            ChunkBytes = 64 << 10,
            Write = new VortexWriteOptions { BlockRows = 1_024 },
        }, Ct);
        List<Row> all = [];
        for (int a = 0; a < 8; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 20_000).Select(i =>
            {
                int day = random.Next(0, 2);
                DateTime at = Start.AddDays(day).AddTicks((long)(random.NextDouble() * TimeSpan.TicksPerDay) / 10 * 10);
                return new Row(day, $"svc-{random.Next(0, 3):D2}", at, all.Count + i, random.NextDouble());
            })];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }

        await dataset.ConsolidateAsync(Ct);
        DateTime ten = Start.AddHours(10);
        DateTime eleven = Start.AddHours(11);
        (string Label, VortexExpr Filter, Func<Row, bool> Keep, bool Selective)[] questions =
        [
            ("Service == svc-01", Eq(nameof(Row.Service), FilterLiteral.From("svc-01")), static row => row.Service == "svc-01", true),
            ("At in [10h, 11h) of day 0", Expr.And(Ge(nameof(Row.At), Micros(ten)), Lt(nameof(Row.At), Micros(eleven))), row => row.At >= ten && row.At < eleven, true),
            ("svc-01 and At in [10h, 11h)", Expr.And(Eq(nameof(Row.Service), FilterLiteral.From("svc-01")), Expr.And(Ge(nameof(Row.At), Micros(ten)), Lt(nameof(Row.At), Micros(eleven)))),
                row => row.Service == "svc-01" && row.At >= ten && row.At < eleven, true),
            ("Service in [svc-01, svc-02)", Expr.And(Ge(nameof(Row.Service), FilterLiteral.From("svc-01")), Lt(nameof(Row.Service), FilterLiteral.From("svc-02"))),
                static row => string.CompareOrdinal(row.Service, "svc-01") >= 0 && string.CompareOrdinal(row.Service, "svc-02") < 0, true),
            ("Day == 1 and At in [day 1 10h, 11h)", Expr.And(Eq(nameof(Row.Day), FilterLiteral.From(1)), Expr.And(Ge(nameof(Row.At), Micros(ten.AddDays(1))), Lt(nameof(Row.At), Micros(eleven.AddDays(1))))),
                row => row.Day == 1 && row.At >= ten.AddDays(1) && row.At < eleven.AddDays(1), true),
        ];

        foreach ((string label, VortexExpr filter, Func<Row, bool> keep, bool selective) in questions)
        {
            HashSet<long> want = [.. all.Where(keep).Select(static row => row.Sequence)];
            DatasetScanCounters counters = new DatasetScanCounters();
            HashSet<long> read = [];
            await foreach (RecordBatch batch in dataset.ScanBuilder().Where(filter).WithMetrics(counters).ExecuteAsync(Ct))
            {
                ReadOnlySpan<long> sequences = batch.Column(3).AsPrimitive<long>().Values;
                for (int row = 0; row < batch.RowCount; row++)
                {
                    read.Add(sequences[row]);
                }
            }

            Assert.True(read.SetEquals(want), $"{label}: {read.Count} rows read, LINQ keeps {want.Count}");
            Assert.Equal(want.Count, await dataset.ScanBuilder().Where(filter).CountAsync(Ct));
            Assert.True(!selective || counters.ObjectsPrunedByKey + counters.GranulesPrunedByKey > 0, $"{label}: nothing passed over by the key");
            await GranulesAsync(dataset, filter, keep, label);
        }
    }

    /// <summary>
    /// In each object, the granules the key's pruning keeps of <paramref name="filter"/> against those its kept rows lie in:
    /// every one of those, and at most one more at each end of each range kept.
    /// </summary>
    private static async Task GranulesAsync(VortexDataset dataset, VortexExpr filter, Func<Row, bool> keep, string label)
    {
        DatasetSchema schema = dataset.Snapshot.Schema;
        KeyPruning pruning = KeyPruning.Of(schema, filter) ?? throw new InvalidOperationException($"{label}: no pruning");
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is not { } contents || await dataset.KeyIndexAsync(held.Entry, contents, Ct) is not { } index)
            {
                continue;
            }

            ReadOnlySpan<byte> order = VortexDataset.OrderOf(held.TreeKey).Span;
            byte[] partition = order[..schema.Tree!.PrefixLength(order, 1)].ToArray();
            List<(int First, int End)> kept = pruning.Granules(index, partition);
            HashSet<int> needed = [];
            List<Row> rows;
            await using (ObjectLease lease = await dataset.RentAsync(held.Entry, Ct))
            {
                rows = await lease.File.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);
            }

            for (int row = 0; row < rows.Count; row++)
            {
                if (keep(rows[row]))
                {
                    needed.Add(row / index.GranuleRows);
                }
            }

            HashSet<int> granules = [.. kept.SelectMany(range => Enumerable.Range(range.First, range.End - range.First))];
            Assert.True(needed.IsSubsetOf(granules), $"{label}, '{held.Entry.Key}': a granule that holds a kept row is passed over");
            Assert.True(granules.Count <= needed.Count + (2 * kept.Count), $"{label}, '{held.Entry.Key}': {granules.Count} granules kept in {kept.Count} ranges for {needed.Count} holding a kept row");
        }
    }

    private static VortexExpr Eq(string path, FilterLiteral value) => Expr.Eq(Expr.Field(path), Expr.Literal(value));

    private static VortexExpr Ge(string path, FilterLiteral value) => Expr.Ge(Expr.Field(path), Expr.Literal(value));

    private static VortexExpr Lt(string path, FilterLiteral value) => Expr.Lt(Expr.Field(path), Expr.Literal(value));

    private static FilterLiteral Micros(DateTime at) => FilterLiteral.From((at - DateTime.UnixEpoch).Ticks / 10);

    [VortexRecord]
    public partial record struct Row(int Day, string Service, DateTime At, long Sequence, double Value);
}
