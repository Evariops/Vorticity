// A text of nearly as many values as rows read granule by granule (the query path plan, P8): a dictionary's values are read
// whole by a read of a part of its chunk, so an object a reader reads granule by granule writes such a column as FSST, whose
// bytes lie between its granules' marks.
//
// THE ORACLE IS LINQ: every answer is LINQ's; the bytes a needle question reads are a granule's share of the object's.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetSliceableTextTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 131 072 spans of four services sorted by the service then the instant, 16 granules of 8 192 rows: a trace's identifier
    /// of 32 hexadecimal digits, one span in seven sharing its trace with a span of its service a few rows before, under a Bloom
    /// filter. The spans of one trace, by service, are read through the granule its filters place it in: under a dictionary,
    /// whose values the trace's 112 000 values made nearly the column, every such read took the values of the whole chunk.
    /// </summary>
    [Fact]
    public async Task AnIdentifierOfNearlyAsManyValuesAsRowsIsReadByItsGranules()
    {
        Random random = new Random(29);
        string[] traces = new string[131_072];
        for (int i = 0; i < traces.Length; i++)
        {
            traces[i] = i >= 64 && random.Next(7) == 0
                ? traces[i - 4 * (1 + random.Next(16))]
                : $"{random.NextInt64():x16}{random.NextInt64():x16}";
        }

        Span[] spans = [.. Enumerable.Range(0, traces.Length).Select(i => new Span(
            Start.AddMilliseconds(i * 7L), $"svc-{i % 4}", traces[i], random.Next(1_000_000)))];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        long objectBytes = 0;
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Span.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Span.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Span.Service)), LayoutValue.Column(nameof(Span.At))],
                IndexedPrefix = 1,
                ObjectBytes = 256 << 20,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct))
        {
            await dataset.IndexAsync(IndexPolicy.Auto.Bloom(nameof(Span.TraceId)), cancellationToken: Ct);
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Span>(spans, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            await dataset.ConsolidateAsync(Ct);
            await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                objectBytes += held.Entry.Bytes;
            }
        }

        foreach (int row in (int[])[1_234, 77_777, 130_000])
        {
            string trace = spans[row].TraceId;
            string[] expected = [.. spans.Where(s => s.TraceId == trace).GroupBy(static s => s.Service)
                .Select(static g => $"{g.Key}:{g.Count()}:{g.Sum(static s => s.DurationNs)}").Order(StringComparer.Ordinal)];
            await using VortexDataset fresh = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0, CostProfile = CostProfile.Memory }, Ct);
            store.Reset();
            string[] answers = [.. (await fresh.Scan<Span>().Where(s => s.TraceId == trace).GroupBy(s => s.Service)
                .Select(g => (g.Key, g.Count(), g.Sum(s => s.DurationNs))).As<ServiceTotals>().ToRecordsAsync(Ct).ToListAsync(Ct))
                .Select(static a => $"{a.Service}:{a.Count}:{a.Total}")];
            Assert.Equal(expected, answers.Order(StringComparer.Ordinal));
            Console.WriteLine($"SLICEABLE: trace of row {row}, {store.BytesRead} bytes of an object of {objectBytes}");
            Assert.True(store.BytesRead * 6 < objectBytes, $"row {row}: {store.BytesRead} bytes of an object of {objectBytes}");
        }
    }

    [VortexRecord]
    public partial record struct Span(DateTime At, string Service, string TraceId, long DurationNs);

    [VortexRecord]
    public partial record struct ServiceTotals(string? Service, long Count, long Total);
}
