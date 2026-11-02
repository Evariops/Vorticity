// The shredded keys of a variant judged granule by granule (the query path plan, V1 and V2): each top-level key read in its
// class keeps its statistics over the granules as a column does, and a filter on it is judged on them, granule by granule,
// where the variant column's rows would be read.
//
// THE ORACLE IS THE SAME VALUES AS A COLUMN, LINQ AND THE ROWS' PATH: a key's statistics are those of a column that holds
// its values, every answer is LINQ's, and the granules' path reads less than the rows'.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetVariantGranulesTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 393 216 calls of six services, 65 536 each, sorted by the service, then the instant, in two appends consolidated, 48
    /// granules of one service each: a tenant that follows the service and a status code drawn at random, each a column and a
    /// key of the attributes, beside a route drawn at random, and a debug text on the calls of a service under a code of 503. Each key's statistics over the granules are the column's: the same rows, the same nulls, the
    /// same values with the same rows granule by granule, the same extremes.
    /// </summary>
    [Fact]
    public async Task AKeysStatisticsAreThoseOfItsValuesAsAColumn()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        _ = await WrittenAsync(store);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
        Vorticity.Types.DType schema = dataset.Snapshot.Schema.DType;
        int tenant = schema.IndexOfField(nameof(Call.Tenant));
        int code = schema.IndexOfField(nameof(Call.Code));
        int attributes = schema.IndexOfField(nameof(Call.Attributes));
        int objects = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            (GranuleStats?[] stats, _, _) = await dataset.ReadGranuleStatsAsync(
                held.Entry, [tenant, code, attributes, attributes], [], [], Ct, [null, null, (attributes, "tenant", PathClass.String), (attributes, "code", PathClass.Integer)]);
            Same(stats[0]!, stats[2]!);
            Same(stats[1]!, stats[3]!);
            Assert.True(held.Entry.Contents!.PathGranuleStatsOf(attributes, "tenant", PathClass.String)!.Value.SingleRows > held.Entry.Rows / 2);
            objects++;
        }

        Assert.True(objects > 0);
    }

    /// <summary>
    /// The calls of <see cref="AKeysStatisticsAreThoseOfItsValuesAsAColumn"/>, asked as QP-3 and QP-10 ask the logs of S1: the
    /// services under a status code of 500 or more, the code varying in every granule; and the services of a tenant, which
    /// a granule holds one of. Both are answered by the granules with no block decoded, each key's statistics listing its
    /// values granule by granule, where the rows' path decodes the attributes. Beside them, a predicate on a path no
    /// statistics describe, a rare debug key present, stays judged as a path, true of its rows alone, never as one on the
    /// attributes column, which no row leaves null and whose summaries would prove it of every row.
    /// </summary>
    [Fact]
    public async Task QuestionsOnKeysAreAnsweredByTheirGranules()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        Call[] calls = await WrittenAsync(store);
        string[][] expected =
        [
            [.. calls.Where(static c => c.Code >= 500).GroupBy(static c => c.Service).Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)],
            [.. calls.Where(static c => c.Tenant == "t-1").GroupBy(static c => c.Service).Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)],
            [.. calls.Where(static c => c.Service == "svc-0" && c.Code == 503).GroupBy(static c => c.Service).Select(static g => $"{g.Key}:{g.Count()}")],
        ];
        for (int q = 0; q < expected.Length; q++)
        {
            long[] bytes = new long[2];
            long[] decoded = new long[2];
            foreach (bool synopses in (bool[])[false, true])
            {
                await using VortexDataset fresh = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
                store.Reset();
                Vorticity.Aggregation question = Asked(fresh.Scan<Call>().With(new ScanOptions { UseSynopses = synopses }), q);
                List<ServiceCount> answers = await question.As<ServiceCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
                bytes[synopses ? 1 : 0] = store.BytesRead;
                decoded[synopses ? 1 : 0] = question.Metrics.BlocksDecoded;
                Assert.Equal(expected[q], answers.Select(static a => $"{a.Service}:{a.Count}").Order(StringComparer.Ordinal));
                if (synopses && q < 2)
                {
                    ScanPlan plan = await Asked(fresh.Scan<Call>(), q).ExplainAsync(Ct);
                    Assert.All(plan.Units, static unit => Assert.True(unit.Alternative == "granules", $"{unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
                }
            }

            Console.WriteLine($"KEYS: question {q}, {bytes[1]} bytes and {decoded[1]} blocks decoded through the granules, {bytes[0]} and {decoded[0]} through the rows");
            if (q == 2)
            {
                continue;
            }

            Assert.Equal(0, decoded[1]);
            Assert.True(decoded[0] > 0);
            Assert.True(bytes[1] < bytes[0], $"question {q}: {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
        }

        static Vorticity.Aggregation Asked(Scan<Call> scan, int question) => question switch
        {
            0 => scan.Where(c => c.Attributes["code"].AsInt64() >= 500).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            1 => scan.Where(c => c.Attributes["tenant"].AsString() == "t-1").GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            _ => scan.Where(c => c.Attributes["debug"].AsString().IsNotNull).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
        };
    }

    /// <summary>
    /// The calls of <see cref="AKeysStatisticsAreThoseOfItsValuesAsAColumn"/> grouped by a key of the attributes: a service's
    /// calls by route, the route varying in every granule; every call by status code; and the calls and the sum of their
    /// codes by service and tenant, a granule holding one of each, the code summed from the values its statistics list. Each
    /// is answered by the granules with no block decoded, the key's statistics giving its groups, where the rows' path
    /// decodes the attributes; the answers are LINQ's.
    /// </summary>
    [Fact]
    public async Task GroupsByAKeyAreAnsweredByItsGranules()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        Call[] calls = await WrittenAsync(store);
        string[][] expected =
        [
            [.. calls.Where(static c => c.Service == "svc-1").GroupBy(static c => c.Attributes["route"]?.AsString()).Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)],
            [.. calls.GroupBy(static c => c.Code).Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)],
            [.. calls.GroupBy(static c => (c.Service, c.Tenant)).Select(static g => $"{g.Key.Service}/{g.Key.Tenant}:{g.Count()}:{g.Sum(static c => c.Code)}").Order(StringComparer.Ordinal)],
        ];
        for (int q = 0; q < expected.Length; q++)
        {
            long[] bytes = new long[2];
            long[] decoded = new long[2];
            foreach (bool synopses in (bool[])[false, true])
            {
                await using VortexDataset fresh = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0 }, Ct);
                store.Reset();
                Vorticity.Aggregation question = Grouped(fresh.Scan<Call>().With(new ScanOptions { UseSynopses = synopses }), q);
                string[] answers = await Answered(question, q);
                bytes[synopses ? 1 : 0] = store.BytesRead;
                decoded[synopses ? 1 : 0] = question.Metrics.BlocksDecoded;
                Assert.Equal(expected[q], answers);
                if (synopses)
                {
                    ScanPlan plan = await Grouped(fresh.Scan<Call>(), q).ExplainAsync(Ct);
                    Assert.All(plan.Units, static unit => Assert.True(unit.Alternative == "granules", $"{unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
                }
            }

            Console.WriteLine($"GROUPS: question {q}, {bytes[1]} bytes and {decoded[1]} blocks decoded through the granules, {bytes[0]} and {decoded[0]} through the rows");
            Assert.Equal(0, decoded[1]);
            Assert.True(decoded[0] > 0);
            Assert.True(bytes[1] < bytes[0], $"question {q}: {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
        }

        static Vorticity.Aggregation Grouped(Scan<Call> scan, int question) => question switch
        {
            0 => scan.Where(c => c.Service == "svc-1").GroupBy(c => c.Attributes["route"].AsString()).Select(g => (g.Key, g.Count())),
            1 => scan.GroupBy(c => c.Attributes["code"].AsInt64()).Select(g => (g.Key, g.Count())),
            _ => scan.GroupBy(c => (c.Service, c.Attributes["tenant"].AsString())).Select(g => (g.Key.Item1, g.Key.Item2, g.Count(), g.Sum(x => x.Attributes["code"].AsInt64()))),
        };

        static async Task<string[]> Answered(Vorticity.Aggregation question, int q) => q switch
        {
            0 => [.. (await question.As<RouteCount>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(static a => $"{a.Route}:{a.Count}").Order(StringComparer.Ordinal)],
            1 => [.. (await question.As<CodeCount>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(static a => $"{a.Code}:{a.Count}").Order(StringComparer.Ordinal)],
            _ => [.. (await question.As<TenantSum>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(static a => $"{a.Service}/{a.Tenant}:{a.Count}:{a.Codes}").Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>The calls both tests ask, written to <paramref name="store"/> and consolidated into one object of a day.</summary>
    internal static async Task<Call[]> WrittenAsync(CountingObjectStore store, Func<int, long>? coded = null)
    {
        Random random = new Random(61);
        Call[] calls = [.. Enumerable.Range(0, 6 * 65_536).Select(i =>
        {
            int service = i % 6;
            string tenant = $"t-{service / 2}";
            long code = coded?.Invoke(i) ?? (random.Next(20) == 0 ? 500 + random.Next(4) : 200 + random.Next(4));
            List<(string, Variant)> fields = [("route", Variant.From($"/r/{random.Next(40)}")), ("tenant", Variant.From(tenant)), ("code", Variant.From(code))];
            if (service == 0 && code == 503)
            {
                fields.Add(("debug", Variant.From($"trace-{i}")));
            }

            Variant attributes = Variant.Object([.. fields]);
            return new Call(Start.AddMilliseconds(i * 31L), $"svc-{service}", tenant, code, attributes);
        })];
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.At))],
                IndexedPrefix = 1,
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct);
        foreach (Call[] half in (Call[][])[calls[..196_608], calls[196_608..]])
        {
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Call>(half, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
            {
            }
        }

        await dataset.ConsolidateAsync(Ct);
        return calls;
    }

    /// <summary>Asserts that <paramref name="path"/> describes the granules as <paramref name="column"/> does.</summary>
    private static void Same(GranuleStats column, GranuleStats path)
    {
        Assert.Equal(column.Rows, path.Rows);
        Assert.Equal(column.Nulls, path.Nulls);
        Assert.Equal(column.Measure, path.Measure);
        for (int granule = 0; granule < column.Count; granule++)
        {
            Assert.Equal(Values(column, granule), Values(path, granule));
            if (column.Measure is not null)
            {
                Assert.Equal(column.MeasureOf(granule).ToArray(), path.MeasureOf(granule).ToArray());
            }
        }
    }

    /// <summary>The values granule <paramref name="granule"/> lists, each with its rows, in order.</summary>
    private static string[] Values(GranuleStats stats, int granule)
    {
        (ReadOnlyMemory<int> ids, ReadOnlyMemory<long> rows) = stats.ValuesOf(granule);
        string[] values = new string[ids.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = $"{Convert.ToHexString(stats.ValueOf(ids.Span[i])!)}:{rows.Span[i]}";
        }

        return [.. values.Order(StringComparer.Ordinal)];
    }

    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, string Tenant, long Code, Variant Attributes);

    [VortexRecord]
    public partial record struct ServiceCount(string? Service, long Count);

    [VortexRecord]
    public partial record struct RouteCount(string? Route, long Count);

    [VortexRecord]
    public partial record struct CodeCount(long? Code, long Count);

    [VortexRecord]
    public partial record struct TenantSum(string? Service, string? Tenant, long Count, long Codes);
}
