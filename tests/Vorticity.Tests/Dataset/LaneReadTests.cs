// The reads of a question over a dataset at one, eight and thirty-two lanes.
//
// WHAT IS HELD: a question gives the same answer whatever its lanes, and its scan requests each segment it needs
// once: at every width, the bytes it requests are those its plan computes from the layout before a data segment
// is read, the segments of the blocks it keeps and of the columns it reads. What the store sends may differ by
// the gaps a read bridges between the segments of one part, since a part's reads end where the part does, but
// never by a segment read again for a lane: a wide pass gets at most a tenth more bytes than one lane. The fallback
// of the former synopsis answer got 6.4 times the bytes at eight lanes, and hung at sixteen. Wide passes end:
// twenty of them at thirty-two lanes, each from a cold handle, finish under thirty seconds each.
//
// THE DATASET: four hours of calls, sealed in objects of slices of ten thousand rows and chunks of two blocks,
// which passes of eight and thirty-two lanes cut into parts between their chunks.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class LaneReadTests : IClassFixture<LaneReadTests.Fixture>
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Services = ["api", "auth", "billing", "cart", "search", "media"];

    private const int Hours = 4;

    private const int RowsPerHour = 120_000;

    private readonly Fixture _fixture;

    public LaneReadTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Questions() => ["count by service", "one service's routes under 5xx, per hour", "an unaligned window"];

    [Theory]
    [MemberData(nameof(Questions))]
    public async Task AQuestionReadsEachSegmentOnceAtEveryWidth(string question)
    {
        Asked one = await AskColdAsync(question, lanes: 1);
        Assert.NotEmpty(one.Rows);
        foreach (int lanes in new[] { 1, 8, 32 })
        {
            Asked wide = lanes == 1 ? one : await AskColdAsync(question, lanes);
            Assert.Equal(one.Rows, wide.Rows);
            Assert.True(
                wide.Requested == wide.Planned,
                $"{question}: its scan requested {wide.Requested} bytes at {lanes} lanes, where its plan computes {wide.Planned}");
            Assert.True(
                wide.Bytes * 10 <= one.Bytes * 11,
                $"{question}: the store sent {wide.Bytes} bytes in {wide.Requests} requests at {lanes} lanes, against {one.Bytes} in {one.Requests} at one");
        }
    }

    [Fact]
    public async Task TwentyPassesAtThirtyTwoLanesEachEnd()
    {
        List<string>? first = null;
        for (int pass = 0; pass < 20; pass++)
        {
            Asked asked = await AskColdAsync("one service's routes under 5xx, per hour", lanes: 32).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30), Ct);
            first ??= asked.Rows;
            Assert.Equal(first, asked.Rows);
        }
    }

    /// <summary>
    /// <paramref name="question"/> asked at <paramref name="lanes"/> lanes by a session and a handle opened for it, through
    /// a store that counts its reads alone, since the tests run side by side.
    /// </summary>
    private async ValueTask<Asked> AskColdAsync(string question, int lanes)
    {
        await using CountingObjectStore store = new CountingObjectStore(_fixture.Store);
        await using VortexSession session = VortexSession.Create(static _ => { });

        // Pieces of four blocks or more, so that eight lanes and thirty-two cut every hour into parts.
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session, LeastPieceRows = 4_096 }, Ct);
        long planned = (await Ask(dataset, question, lanes).ExplainAsync(Ct)).BytesToRead;
        Vorticity.Aggregation asked = Ask(dataset, question, lanes);
        store.Reset();
        List<string> rows = [];
        await using (IAsyncEnumerator<RecordBatch> batches = asked.Query.Batches(Ct))
        {
            while (await batches.MoveNextAsync())
            {
                Values.DescribeRows(batches.Current, rows);
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return new Asked(store.Requests, store.BytesRead, asked.Metrics.BytesRequested, planned, rows);
    }

    /// <summary><paramref name="question"/> over <paramref name="dataset"/>, at <paramref name="lanes"/> lanes.</summary>
    private static Vorticity.Aggregation Ask(VortexDataset dataset, string question, int lanes)
    {
        Scan<Call> scan = dataset.Scan<Call>().With(new ScanOptions { DegreeOfParallelism = lanes });
        DateTimeOffset from = Noon.AddMinutes(37.5);
        DateTimeOffset to = Noon.AddMinutes(155.25);
        return question switch
        {
            "count by service" => scan.GroupBy(c => c.Service).Select(g => (g.Key, g.Count())),
            "one service's routes under 5xx, per hour" => scan.Where(c => c.Service == "api" && c.Status >= 500)
                .GroupBy(c => (c.Route, c.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Route, g.Key.Item2, g.Count())),
            "an unaligned window" => scan.Where(c => c.At >= from && c.At < to)
                .GroupBy(c => c.Service).Select(g => (g.Key, g.Count(), g.Sum(c => c.DurationNs))),
            _ => throw new ArgumentException($"No question named '{question}'.", nameof(question)),
        };
    }

    /// <summary>
    /// What a question cost and answered: the requests and the bytes the store saw after the handle opened, the bytes its
    /// scan requested, those its plan computed, and its rows, each a line of its values, sorted.
    /// </summary>
    private sealed record Asked(long Requests, long Bytes, long Requested, long Planned, List<string> Rows);

    /// <summary>
    /// Four hours of calls from noon, 120 000 an hour, appended in parts of five minutes and compacted: an object an
    /// hour, of slices of ten thousand rows or so, in chunks of two blocks.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        internal MemoryObjectStore Store { get; } = new MemoryObjectStore();

        public async ValueTask InitializeAsync()
        {
            // Objects with no sheet nor counts: what this test holds is how the lanes read their data, which the synopses'
            // bytes would move, the jobs sizing their levels by the objects' bytes.
            await using VortexDataset dataset = await VortexDataset.CreateAsync(Store, Call.Schema, new DatasetOptions
            {
                ClusteringKey = [nameof(Call.At)],
                Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, ContentsObjectBytes = 0, SliceTargetRows = 10_000,
                Write = new VortexWriteOptions { BlockRows = 1_024, ChunkTargetBytes = 4_096 },
                SheetObjectBytes = long.MaxValue,
            });
            Random random = new Random(61);
            int row = 0;
            for (int part = 0; part < Hours * 12; part++)
            {
                Call[] rows = new Call[RowsPerHour / 12];
                for (int i = 0; i < rows.Length; i++, row++)
                {
                    int service = random.Next(Services.Length);
                    rows[i] = new Call(
                        Noon.AddSeconds(row * 3_600.0 / RowsPerHour),
                        Services[service],
                        $"/{Services[service]}/{random.Next(8)}",
                        random.Next(10) switch { < 7 => (short)200, 7 => (short)404, 8 => (short)500, _ => (short)503 },
                        random.NextInt64(1_000, 5_000_000));
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Call>(rows);
                await dataset.AppendAsync(draft);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
            {
            }
        }

        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }

    [VortexRecord]
    public partial record struct Call(DateTimeOffset At, string Service, string Route, short Status, long DurationNs);
}
