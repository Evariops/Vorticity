// The absence of a value proven granule by granule (the query path plan, B1 and B2): a filter that leaves one value of a
// column out, x != v, is answered by the granules its object's indexes do not place v in, judged under the rest of the
// filter, whatever the column: a declared identifier's postings, the Bloom filters of a column that is none, or its locate.
//
// THE ORACLE IS LINQ AND THE ROWS' PATH: every answer is LINQ's; the granules' path reads less than the rows'.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetAbsenceProofsTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 262 144 calls of four services, 65 536 each, sorted by the service, the host, then the instant, 32 granules of one
    /// service each: a ticket of its own on every call, under a Bloom filter and no identifier; a host among sixteen of its
    /// service's, half a granule each, which the object's locate places; a size drawn at random, a measure; and a status of
    /// five values a granule lists. Every call but one ticket's by service, and the calls and sizes summed of every host but
    /// one by service, are answered by the granules the ticket's filters or the host's locate do not place the value in,
    /// under the rest of the filter, the one or two they place it in read as rows: the host, which varies in most granules,
    /// would forbid the sum under the whole filter. Every call but those of a status, which no index places, by the
    /// statistics of the status, value by value. Asked of a store close by, which weighs bytes before requests: the object,
    /// of 4 MB, reads its rows in three requests, as many as a granule of them, which would tie the paths anywhere else.
    /// </summary>
    [Fact]
    public async Task AValueLeftOutIsPlacedByTheIndexesOfItsColumn()
    {
        Random random = new Random(71);
        Call[] calls = [.. Enumerable.Range(0, 4 * 65_536).Select(i => new Call(
            Start.AddMilliseconds(i * 29L), $"svc-{i % 4}", $"ticket-{random.Next(1 << 30):D10}-{i}", $"host-{i % 4}-{random.Next(16):D2}",
            200 + (random.Next(10) == 0 ? 300 : random.Next(4)), random.Next(1_000_000)))];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Call.At)).Truncate(CalendarUnit.Day),
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.Host)), LayoutValue.Column(nameof(Call.At))],
                IndexedPrefix = 1,
                ObjectBytes = 256 << 20,
                MergeFanout = 4,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
            SheetObjectBytes = 0,
        }, Ct))
        {
            await dataset.IndexAsync(IndexPolicy.Auto.Bloom(nameof(Call.Ticket)), cancellationToken: Ct);
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
            Vorticity.Types.DType schema = dataset.Snapshot.Schema.DType;
            await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
            {
                ObjectContents contents = held.Entry.Contents!;
                Assert.Contains(contents.Locating ?? [], entry => entry.Column == (uint)schema.IndexOfField(nameof(Call.Ticket)) && entry.Kind == IndexKinds.BloomSbbf);
                Assert.NotNull(contents.LocateOf(schema.IndexOfField(nameof(Call.Host))));
            }
        }

        string ticket = calls[77_777].Ticket;
        string host = calls[12_345].Host;
        for (int q = 0; q < 3; q++)
        {
            string[] expected = [.. calls.Where(c => q switch { 0 => c.Ticket != ticket, 1 => c.Host != host, _ => c.Status != 500 })
                .GroupBy(static c => c.Service).Select(g => q == 1 ? $"{g.Key}:{g.Count()}:{g.Sum(static c => c.Size)}" : $"{g.Key}:{g.Count()}").Order(StringComparer.Ordinal)];
            long[] bytes = new long[2];
            foreach (bool synopses in (bool[])[false, true])
            {
                await using VortexDataset fresh = await VortexDataset.OpenAsync(store, new DatasetOptions { ContentsObjectBytes = 0, SheetObjectBytes = 0, CostProfile = CostProfile.Memory }, Ct);
                store.Reset();
                Vorticity.Aggregation question = Asked(fresh.Scan<Call>().With(new ScanOptions { UseSynopses = synopses }), q, ticket, host);
                string[] answers = q == 1
                    ? [.. (await question.As<ServiceTotals>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(static a => $"{a.Service}:{a.Count}:{a.Total}")]
                    : [.. (await question.As<ServiceCount>().ToRecordsAsync(Ct).ToListAsync(Ct)).Select(static a => $"{a.Service}:{a.Count}")];
                bytes[synopses ? 1 : 0] = store.BytesRead;
                Assert.Equal(expected, answers.Order(StringComparer.Ordinal));
                if (synopses)
                {
                    ScanPlan plan = await Asked(fresh.Scan<Call>(), q, ticket, host).ExplainAsync(Ct);
                    Assert.All(plan.Units, static unit => Assert.True(unit.Alternative == "granules", $"{unit.Alternative}: {unit.Detail}; {string.Join("; ", unit.Rejected)}"));
                }
            }

            Console.WriteLine($"ABSENCE: question {q}, {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
            Assert.True(bytes[1] * 3 < bytes[0], $"question {q}: {bytes[1]} bytes through the granules, {bytes[0]} through the rows");
        }

        static Vorticity.Aggregation Asked(Scan<Call> scan, int question, string ticket, string host) => question switch
        {
            0 => scan.Where(c => c.Ticket != ticket).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            1 => scan.Where(c => c.Host != host).GroupBy(c => c.Service).Select(g => (g.Key, g.Count(), g.Sum(c => c.Size))),
            _ => scan.Where(c => c.Status != 500).GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
        };
    }

    [VortexRecord]
    public partial record struct Call(DateTime At, string Service, string Ticket, string Host, long Status, long Size);

    [VortexRecord]
    public partial record struct ServiceCount(string? Service, long Count);

    [VortexRecord]
    public partial record struct ServiceTotals(string? Service, long Count, long Total);
}
