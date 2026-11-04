// The nested paths of a variant judged granule by granule (the query path plan, V3): a primitive key a job shreds inside an
// object of the attributes, Attributes['http']['status'].AsInt64(), keeps its statistics over the granules as a top-level key
// does, and a filter or a key on it is answered by them where the variant column's rows would be read.
//
// THE ORACLE IS LINQ AND THE ROWS' PATH: every answer is LINQ's; the granules' path reads less than the rows'.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetNestedPathGranulesTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 262 144 calls of four services, 65 536 each, sorted by the service, then the instant, 32 granules of one service
    /// each, whose attributes hold an object of HTTP, its method among three and its status among eight, five in a hundred a
    /// server error: the services under a status of 500 or more, and every call by method, are answered by the granules of
    /// the nested keys with no block decoded, their statistics listing the values granule by granule, where the rows'
    /// path decodes the attributes.
    /// </summary>
    [Fact]
    public async Task QuestionsOnANestedKeyAreAnsweredByItsGranules()
    {
        Random random = new Random(79);
        Call[] calls = [.. Enumerable.Range(0, 4 * 65_536).Select(i =>
        {
            string method = (string[])["GET", "POST", "PUT"] is var methods ? methods[random.Next(3)] : string.Empty;
            long status = random.Next(20) == 0 ? 500 + random.Next(4) : 200 + random.Next(4);
            return new Call(Start.AddMilliseconds(i * 29L), $"svc-{i % 4}", method, status,
                Variant.Object(("http", Variant.Object(("method", Variant.From(method)), ("status", Variant.From(status))))));
        })];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
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
        }, Ct))
        {
            foreach (Call[] half in (Call[][])[calls[..131_072], calls[131_072..]])
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
            int attributes = dataset.Snapshot.Schema.DType.IndexOfField(nameof(Call.Attributes));
            await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                Assert.NotNull(held.Entry.Contents!.PathGranuleStatsOf(attributes, "http\u001fstatus", PathClass.Integer));
                Assert.NotNull(held.Entry.Contents.PathGranuleStatsOf(attributes, "http\u001fmethod", PathClass.String));
            }
        }

        string[][] expected =
        [
            [.. calls.Where(static c => c.Status >= 500).GroupBy(static c => c.Service).Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)],
            [.. calls.GroupBy(static c => c.Method).Select(static g => $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)],
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
                string[] answers = [.. (await question.As<GroupCount>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(static a => $"{a.Key}:{a.Count}").Order(StringComparer.Ordinal)];
                bytes[synopses ? 1 : 0] = store.BytesRead;
                decoded[synopses ? 1 : 0] = question.Metrics.BlocksDecoded;
                Assert.Equal(expected[q], answers);
                if (synopses)
                {
                    ScanPlan plan = await Asked(fresh.Scan<Call>(), q).ExplainAsync(Ct);
                    Assert.All(plan.Units, static unit => Assert.True(unit.Alternative == "granules", $"{unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
                }
            }

            Console.WriteLine($"NESTED: question {q}, {bytes[1]} bytes and {decoded[1]} blocks decoded through the granules, {bytes[0]} and {decoded[0]} through the rows");
            Assert.Equal(0, decoded[1]);
            Assert.True(bytes[1] < bytes[0], $"question {q}: {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
        }

        static Vorticity.Aggregation Asked(Scan<Call> scan, int question) => question switch
        {
            0 => scan.Where(c => c.Attributes["http"]["status"].AsInt64() >= 500).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            _ => scan.GroupBy(c => c.Attributes["http"]["method"].AsString()).Select(g => (g.Key, g.Count())),
        };
    }

    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, string Method, long Status, Variant Attributes);

    [VortexRecord]
    public partial record struct GroupCount(string? Key, long Count);
}
