// The client's question patterns over spans, held to a ratchet as the query paths are (QueryPathBudgetTests).
//
// WHAT IS HELD, per pattern: the requests and the bytes a question asks of the store, cold (a session and a handle
// opened for it, an empty synopsis cache) and warm (the same handle again, the cache filled), and the managed bytes the
// process allocates for one warm ask, every thread counted.
//
// THE PATTERNS are the client's (QP-1 to QP-10, `vortex-queries --micro paths --qp`), over spans whose attributes hold
// the HTTP status, declared shredded, and a key one span in five hundred holds, left in the residual, every one read
// from the rows: a service's routes under 5xx per hour (QP-1), a route's pods (QP-2), the services under a shredded
// status (QP-3), the spans of one trace by its identifier's postings (QP-4), the 99th percentile per service over the
// last quarter of an hour (QP-6), and the services of a residual key (QP-10).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class PatternBudgetTests : IClassFixture<PatternBudgetTests.Fixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);

    private const int Hours = 4;

    private const int RowsPerHour = 6_000;

    /// <summary>
    /// The ceilings of each pattern: requests and bytes cold, requests and bytes warm, which a build gives exactly, and the
    /// bytes one warm ask allocates, 2 % above what it allocated alone: beside the other classes that run alone, the pools
    /// they leave took 112 bytes off each. Measured on 2026-10-24, every pattern read from the rows of objects that keep
    /// them in the order they came, with no synopsis part nor tables. QP-10 allocated 159,400 bytes on CI's Linux runner
    /// where Windows allocated 154,352; Windows now allocates 148,144, and its ceiling stays 2 % above the runner's last
    /// figure until the runner gives its new one.
    /// </summary>
    private static readonly Dictionary<string, Budget> Ceilings = new(StringComparer.Ordinal)
    {
        ["QP-1 a service's routes under 5xx, per hour"] = new(8, 361244, 4, 98960, 79824),
        ["QP-2 the pods of a route"] = new(4, 262284, 0, 0, 68784),
        ["QP-3 the services under a shredded status of 500 or more"] = new(4, 262284, 0, 0, 168752),
        ["QP-4 the spans of a trace"] = new(5, 287024, 1, 24740, 73760),
        ["QP-6 the 99th percentile per service, the last quarter of an hour"] = new(2, 90311, 1, 24740, 294560),
        ["QP-10 the services of a residual key"] = new(4, 262284, 0, 0, 162592),
    };

    private readonly Fixture _fixture;

    public PatternBudgetTests(Fixture fixture) => _fixture = fixture;

    private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Patterns() => [.. Ceilings.Keys];

    [Theory]
    [MemberData(nameof(Patterns))]
    public async Task APatternStaysWithinItsBudget(string pattern)
    {
        Budget ceiling = Ceilings[pattern];
        string directory = Directory.CreateTempSubdirectory("vortex-patterns-").FullName;
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64L << 20));
            await using VortexDataset dataset = await VortexDataset.OpenAsync(_fixture.Store, new DatasetOptions { Session = session }, Ct);
            _fixture.Store.Reset();
            List<string> cold = await AskAsync(dataset, pattern);
            (long coldRequests, long coldBytes) = (_fixture.Store.Requests, _fixture.Store.BytesRead);
            _fixture.Store.Reset();
            List<string> warm = await AskAsync(dataset, pattern);
            (long warmRequests, long warmBytes) = (_fixture.Store.Requests, _fixture.Store.BytesRead);
            Assert.Equal(cold, warm);
            Assert.NotEmpty(warm);

            long allocated = long.MaxValue;
            for (int round = 0; round < 6; round++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                await AskAsync(dataset, pattern, describe: false);
                allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(precise: true) - before);
            }

            Console.Out.Write(string.Create(
                CultureInfo.InvariantCulture,
                $"PATTERN {pattern}: cold {coldRequests} requests {coldBytes} bytes (ceilings {Shown(ceiling.ColdRequests)}, {Shown(ceiling.ColdBytes)}), " +
                $"warm {warmRequests} requests {warmBytes} bytes (ceilings {Shown(ceiling.WarmRequests)}, {Shown(ceiling.WarmBytes)}), " +
                $"{allocated} bytes allocated warm (ceiling {Shown(ceiling.Allocated)}), {warm.Count} rows answered.\n"));

            Assert.True(coldRequests <= ceiling.ColdRequests, $"{pattern}: {coldRequests} requests cold, over {ceiling.ColdRequests}");
            Assert.True(coldBytes <= ceiling.ColdBytes, $"{pattern}: {coldBytes} bytes cold, over {ceiling.ColdBytes}");
            Assert.True(warmRequests <= ceiling.WarmRequests, $"{pattern}: {warmRequests} requests warm, over {ceiling.WarmRequests}");
            Assert.True(warmBytes <= ceiling.WarmBytes, $"{pattern}: {warmBytes} bytes warm, over {ceiling.WarmBytes}");
            Assert.True(allocated <= ceiling.Allocated, $"{pattern}: {allocated} bytes allocated warm, over {ceiling.Allocated}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Shown(long ceiling) => ceiling == long.MaxValue ? "none" : ceiling.ToString(CultureInfo.InvariantCulture);

    /// <summary>The rows <paramref name="pattern"/> answers, each a line of its values, sorted; none when not <paramref name="describe"/>.</summary>
    private static async Task<List<string>> AskAsync(VortexDataset dataset, string pattern, bool describe = true)
    {
        Scan<Spanned> scan = dataset.Scan<Spanned>().With(new ScanOptions { DegreeOfParallelism = 1 });
        DateTimeOffset quarter = Start.AddHours(Hours).AddMinutes(-15);
        IAsyncEnumerator<RecordBatch> batches = pattern[..pattern.IndexOf(' ', StringComparison.Ordinal)] switch
        {
            "QP-1" => scan.Where(s => s.Service == "svc-1" && s.Status >= 500).GroupBy(s => (s.Route, s.At.Truncate(CalendarUnit.Hour)))
                .Select(g => (g.Key.Route, g.Key.Item2, g.Count())).Query.Batches(Ct),
            "QP-2" => scan.Where(s => s.Route == "/s2/r3").GroupBy(s => s.Pod).Select(g => (g.Key, g.Count())).Query.Batches(Ct),
            "QP-3" => scan.Where(s => s.Attributes["http.status_code"].AsInt64() >= 500).GroupBy(s => s.Service).Select(g => (g.Key, g.Count())).Query.Batches(Ct),
            "QP-4" => scan.Where(s => s.TraceId == "t-01234").ToBatchesAsync(Ct).GetAsyncEnumerator(Ct),
            "QP-6" => scan.Where(s => s.At >= quarter).GroupBy(s => s.Service).Select(g => (g.Key, g.Count(), g.Quantile(s => s.DurationNs, 0.99))).Query.Batches(Ct),
            "QP-10" => scan.Where(s => s.Attributes["canary"].AsString() == "on").GroupBy(s => s.Service).Select(g => (g.Key, g.Count())).Query.Batches(Ct),
            _ => throw new ArgumentException($"No pattern named '{pattern}'.", nameof(pattern)),
        };

        List<string> rows = [];
        await using (batches)
        {
            while (await batches.MoveNextAsync())
            {
                if (describe)
                {
                    Values.DescribeRows(batches.Current, rows);
                }
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>What a pattern may cost: requests and bytes cold and warm, and the bytes a warm ask allocates.</summary>
    private readonly record struct Budget(long ColdRequests, long ColdBytes, long WarmRequests, long WarmBytes, long Allocated);

    /// <summary>
    /// Four hours of spans from 20:00, 6,000 an hour, appended in parts of five minutes and compacted under periods of an
    /// hour: the sealed hours in time slices, the HTTP status of the attributes shredded, a key one span in five hundred
    /// holds left in the residual, the trace ids indexed.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private readonly MemoryObjectStore _inner = new MemoryObjectStore();

        public Fixture() => Store = new CountingObjectStore(_inner);

        internal CountingObjectStore Store { get; }

        public async ValueTask InitializeAsync()
        {
            await using VortexDataset dataset = await VortexDataset.CreateAsync(Store, Spanned.Schema, new DatasetOptions
            {
                ClusteringKey = [nameof(Spanned.At)],
                Identifiers = [nameof(Spanned.TraceId)],
                ShreddedPaths = ["Attributes['http.status_code'].AsInt64()"],
                Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
                Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(10_000) },
            });
            Random random = new Random(67);
            int row = 0;
            for (int part = 0; part < Hours * 12; part++)
            {
                Spanned[] rows = new Spanned[RowsPerHour / 12];
                for (int i = 0; i < rows.Length; i++, row++)
                {
                    int service = random.Next(6);
                    short status = random.Next(10) switch { < 6 => (short)200, < 8 => (short)404, 8 => (short)500, _ => (short)503 };
                    rows[i] = new Spanned(
                        Start.AddSeconds(row * 3_600.0 / RowsPerHour),
                        $"svc-{service}",
                        $"/s{service}/r{random.Next(8)}",
                        $"svc-{service}-pod-{random.Next(3)}",
                        status,
                        random.NextInt64(1_000, 2_000_000),
                        string.Create(CultureInfo.InvariantCulture, $"t-{row / 8:D5}"),
                        row % 500 == 0
                            ? Variant.Object(("canary", Variant.From(random.Next(2) == 0 ? "on" : "off")), ("http.status_code", Variant.From((long)status)))
                            : Variant.Object(("http.status_code", Variant.From((long)status))));
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Spanned>(rows);
                await dataset.AppendAsync(draft);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            await _inner.DisposeAsync();
        }
    }

    [VortexRecord]
    public partial record struct Spanned(
        DateTimeOffset At, string Service, string Route, string Pod, short Status, long DurationNs, string TraceId, Variant Attributes);
}
