// The variants in streams (spec E; E1 to E4): each path that comes back gets a stream of its own, typed, a rare one and the
// keys that are data stay in the residual; the directory names the granules each path lies in; a question on a path reads
// its streams; a path asked three times is shredded by the next job, however rare; a key led by a path prunes by it.
//
// THE ORACLE IS LINQ AND THE ROWS: every question equals LINQ over the rows; each path's granules in the directory are
// those its rows lie in, in the object's order.
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

public sealed partial class DatasetVariantStreamsTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// One day of 300 000 rows whose attributes hold a route, a status and a tenant on every row, a retry on one in twenty,
    /// a debug text on one service's rows now and then, and a key that is data on one in a hundred, appended in two halves
    /// and consolidated: route, status and retry typed (E1), debug and the keys that are data in the residual; each path's
    /// granules named as its rows lie (E2); questions on each path, and a key read under a filter on another, as LINQ
    /// answers (E3). The debug text stays in the residual however often it is asked: no question changes what a job writes.
    /// </summary>
    [Fact]
    public async Task EachPathThatComesBackHasItsStream()
    {
        Random random = new Random(11);
        Row[] rows = [.. Enumerable.Range(0, 300_000).Select(i =>
        {
            int service = random.Next(30);
            List<(string, Variant)> attributes =
            [
                ("route", Variant.From($"/r/{random.Next(20)}")),
                ("status", Variant.From(200 + (random.Next(10) == 0 ? 300 : random.Next(4)))),
                ("tenant", Variant.From($"t-{service / 3}")),
            ];
            if (i % 20 == 0)
            {
                attributes.Add(("retry", Variant.From(random.Next(1, 5))));
            }

            if (service == 3 && i % 40 == 7)
            {
                attributes.Add(("debug", Variant.From($"trace-{random.Next(50)}")));
            }

            if (i % 100 == 3)
            {
                attributes.Add(($"user.{random.Next(10_000)}.score", Variant.From(random.Next(100))));
            }

            return new Row(Start.AddMilliseconds(i * 37L), $"svc-{service:D2}", i, Variant.Object([.. attributes]));
        })];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At))],
                IndexedPrefix = 1,
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        await AppendAsync(dataset, rows[..150_000]);
        await AppendAsync(dataset, rows[150_000..]);
        await dataset.ConsolidateAsync(Ct);

        // The streams, and where each path lies by granule.
        const int Attributes = 3;
        List<Row> ordered = await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);
        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        VariantDirectory directory = (await dataset.ReadPathDirectoryAsync(held.Entry, Attributes, Ct))!;
        Dictionary<string, PathPlace> places = [];
        int keysThatAreData = 0;
        foreach (PathEntry entry in directory.Entries)
        {
            if (entry.Path is [{ } key] && key.StartsWith("user.", StringComparison.Ordinal))
            {
                keysThatAreData += entry.Place == PathPlace.Residual ? 1 : 0;
            }
            else if (entry.Path.Length == 1 && entry.Values > 0)
            {
                places[$"{entry.Path[0]}:{entry.Class}"] = entry.Place;
            }
        }

        Assert.Equal(PathPlace.Typed, places["route:String"]);
        Assert.Equal(PathPlace.Typed, places["status:Integer"]);
        Assert.Equal(PathPlace.Typed, places["retry:Integer"]);
        Assert.Equal(PathPlace.Residual, places["debug:String"]);
        Assert.True(keysThatAreData > 0);
        Assert.NotEmpty(held.Entry.Contents!.PathLocates);
        Assert.Equal(8192, directory.GroupRows);
        foreach (string key in (string[])["route", "status", "retry", "debug"])
        {
            SortedSet<int> truth = [];
            for (int row = 0; row < held.Entry.Rows; row++)
            {
                if (ordered[row].Attributes[key] is not null)
                {
                    truth.Add((int)(row / directory.GroupRows));
                }
            }

            SortedSet<int> named = [];
            foreach (PathEntry entry in directory.Of([key]))
            {
                for (int group = 0; group < directory.GroupCount; group++)
                {
                    if (entry.In(group))
                    {
                        named.Add(group);
                    }
                }
            }

            Assert.True(named.SetEquals(truth), $"{key}: {named.Count} granules named, {truth.Count} hold it");
        }

        // Questions on each path.
        string userKey = rows.SelectMany(static r => r.Attributes.GetKeys()).First(static k => k.StartsWith("user.", StringComparison.Ordinal));
        foreach ((string label, Func<Row, bool> linq, Func<Probe<Row>, Predicate> asked) in new (string, Func<Row, bool>, Func<Probe<Row>, Predicate>)[]
        {
            ("route == /r/7", static r => r.Attributes["route"]?.AsString() == "/r/7", static r => r.Attributes["route"].AsString() == "/r/7"),
            ("tenant == t-2", static r => r.Attributes["tenant"]?.AsString() == "t-2", static r => r.Attributes["tenant"].AsString() == "t-2"),
            ("status >= 500", static r => r.Attributes["status"]?.AsInt64() >= 500, static r => r.Attributes["status"].AsInt64() >= 500),
            ("retry == 3", static r => r.Attributes["retry"]?.AsInt64() == 3, static r => r.Attributes["retry"].AsInt64() == 3),
            ("debug == trace-7", static r => r.Attributes["debug"]?.AsString() == "trace-7", static r => r.Attributes["debug"].AsString() == "trace-7"),
            ("has a user's score", r => r.Attributes[userKey] is not null, r => r.Attributes.Has(userKey)),
        })
        {
            Assert.True(
                rows.Where(linq).Select(static r => r.Sequence).Order().SequenceEqual((await dataset.Scan<Row>().Where(asked).Select(r => r.Sequence).ToListAsync(Ct)).Order()),
                label);
        }

        Assert.Equal(
            rows.Where(static r => r.Attributes["tenant"]?.AsString() == "t-2").Select(static r => r.Attributes["status"]?.AsInt64() ?? -1).Order(),
            (await dataset.Scan<Row>().Where(r => r.Attributes["tenant"].AsString() == "t-2").Select(r => r.Attributes["status"].AsInt64()).ToListAsync(Ct)).Select(static v => v ?? -1).Order());

        // Counts grouped by a typed path, and by a typed path and the day: the path's stream read as the key.
        Assert.Equal(
            rows.GroupBy(static r => r.Attributes["route"]?.AsString()).Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal),
            (await dataset.Scan<Row>().GroupBy(r => r.Attributes["route"].AsString()).Select(g => (g.Key, g.Count())).As<RouteCount>().ToRecordsAsync(Ct).ToListAsync(Ct))
                .Select(static c => $"{c.Route}:{c.Count}").Order(StringComparer.Ordinal));
        Assert.Equal(
            rows.GroupBy(static r => (r.Attributes["route"]?.AsString(), r.At.Date)).Select(static g => $"{g.Key.Item1}/{g.Key.Date:yyyy-MM-dd}:{g.Count()}").Order(StringComparer.Ordinal),
            (await dataset.Scan<Row>().GroupBy(r => (r.Attributes["route"].AsString(), r.At.Truncate(CalendarUnit.Day))).Select(g => (g.Key.Item1, g.Key.Item2, g.Count())).As<RouteDayCount>().ToRecordsAsync(Ct).ToListAsync(Ct))
                .Select(static c => $"{c.Route}/{c.Day:yyyy-MM-dd}:{c.Count}").Order(StringComparer.Ordinal));

        // No promotion: the debug text asked three times in all stays in the residual of the next job that writes its rows.
        for (int time = 0; time < 3; time++)
        {
            await dataset.Scan<Row>().Where(r => r.Attributes["debug"].AsString() == "trace-9").Select(r => r.Sequence).ToListAsync(Ct);
        }

        await AppendAsync(dataset, [.. rows[..2_000].Select(static r => r with { Sequence = r.Sequence + 300_000 })]);
        await dataset.ConsolidateAsync(Ct);
        await foreach (PositionedObject rewritten in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (await dataset.ReadPathDirectoryAsync(rewritten.Entry, Attributes, Ct) is not { } paths)
            {
                continue;
            }

            foreach (PathEntry entry in paths.Of(["debug"]))
            {
                Assert.NotEqual(PathPlace.Typed, entry.Place);
            }
        }
    }

    /// <summary>
    /// The same 120 000 rows appended in halves and consolidated by a session of one lane, which counts an object's paths
    /// before it writes each batch, and by one of eight, which counts them on another lane while the writer takes the batch:
    /// the object's path directory is the same, byte for byte, its paths' counts and the granules they lie in alike.
    /// </summary>
    [Fact]
    public async Task AnObjectsPathsCountedBesideItsWriteAreThoseCountedInTurn()
    {
        Random random = new Random(23);
        Row[] rows = [.. Enumerable.Range(0, 120_000).Select(i =>
        {
            List<(string, Variant)> attributes = [("route", Variant.From($"/r/{random.Next(20)}")), ("status", Variant.From(200 + random.Next(4)))];
            if (i % 7 == 0)
            {
                attributes.Add(("retry", Variant.From(random.Next(1, 5))));
            }

            if (i % 50 == 3)
            {
                attributes.Add(($"user.{random.Next(5_000)}.score", Variant.From(random.Next(100))));
            }

            return new Row(Start.AddMilliseconds(i * 37L), $"svc-{random.Next(12):D2}", i, Variant.Object([.. attributes]));
        })];

        byte[][] directories = new byte[2][];
        foreach ((int lanes, int at) in ((int, int)[])[(1, 0), (8, 1)])
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = lanes);
            await using MemoryObjectStore store = new MemoryObjectStore();
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
            {
                Session = session,
                Layout = new DatasetLayout
                {
                    Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                    SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.At))],
                    IndexedPrefix = 1,
                    ObjectBytes = 256 << 20,
                    MergeFanout = 4,
                    CalmAfter = null,
                },
                ContentsObjectBytes = 0,
                SheetObjectBytes = 0,
                Write = new VortexWriteOptions { BlockRows = 1_024 },
            }, Ct);
            await AppendAsync(dataset, rows[..60_000]);
            await AppendAsync(dataset, rows[60_000..]);
            await dataset.ConsolidateAsync(Ct);
            PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
            directories[at] = (await dataset.ReadPathDirectoryAsync(held.Entry, 3, Ct))!.ToBytes();
        }

        Assert.Equal(directories[0], directories[1]);
    }

    /// <summary>
    /// 200 000 rows sorted by their attributes' tenant, then the instant, consolidated in objects of 1 MiB (E4): an equality
    /// on the tenant reads LINQ's rows, and the objects its key's range refutes are passed over by the key.
    /// </summary>
    [Fact]
    public async Task AKeyLedByAPathPrunesByIt()
    {
        Random random = new Random(5);
        Row[] rows = [.. Enumerable.Range(0, 200_000).Select(i => new Row(Start.AddMilliseconds(i * 37L), $"svc-{random.Next(30):D2}", i, Variant.Object(
            ("tenant", Variant.From($"t-{random.Next(40):D2}")), ("status", Variant.From(200 + random.Next(4))), ("route", Variant.From($"/r/{random.Next(20)}")))))];
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Path(nameof(Row.Attributes), "tenant", Vorticity.Types.Variants.VariantClass.String), LayoutValue.Column(nameof(Row.At))],
                IndexedPrefix = 1,
                ObjectBytes = 1 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        for (int part = 0; part < 4; part++)
        {
            await AppendAsync(dataset, rows[(part * 50_000)..((part + 1) * 50_000)]);
        }

        await dataset.ConsolidateAsync(Ct);
        foreach (string tenant in (string[])["t-07", "t-33"])
        {
            VortexExpr filter = new ComparisonExpr(
                new Vorticity.Compute.VariantFieldExpr(new ColumnFieldExpr(nameof(Row.Attributes)), [Vorticity.Compute.VariantStep.Of("tenant")], Vorticity.Compute.VariantRead.AsString),
                ComparisonOp.Equal, FilterLiteral.From(tenant));
            DatasetScanCounters counters = new DatasetScanCounters();
            List<long> got = [];
            await foreach (RecordBatch batch in dataset.ScanBuilder().Where(filter).WithMetrics(counters).ExecuteAsync(Ct))
            {
                ReadOnlySpan<long> sequences = batch.Column(2).AsPrimitive<long>().Values;
                ReadOnlySpan<ulong> words = batch.SelectionWords;
                for (int row = 0; row < batch.RowCount; row++)
                {
                    if (words.IsEmpty || ((words[row >> 6] >> (row & 63)) & 1) != 0)
                    {
                        got.Add(sequences[row]);
                    }
                }
            }

            Assert.Equal(rows.Where(r => r.Attributes["tenant"]?.AsString() == tenant).Select(static r => r.Sequence).Order(), got.Order());
            Assert.True(counters.ObjectsPrunedByKey + counters.SubtreesSkipped + counters.ObjectsSkipped > 0, $"{tenant}: no object passed over by the key");
        }
    }

    private static async Task AppendAsync(VortexDataset dataset, Row[] rows)
    {
        await using (ObjectDraft draft = dataset.StartObject())
        {
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }
    }

    [VortexRecord]
    public partial record struct Row(DateTime At, string Service, long Sequence, Variant Attributes);

    [VortexRecord]
    public partial record struct RouteCount(string? Route, long Count);

    [VortexRecord]
    public partial record struct RouteDayCount(string? Route, DateTime Day, long Count);
}
