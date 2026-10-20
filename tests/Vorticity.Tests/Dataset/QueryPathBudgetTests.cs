// The query paths of a dataset of periods, held to a ratchet.
//
// WHAT IS HELD, per path: the requests and the bytes a question asks of the store, cold (a session and
// a handle opened for it, an empty synopsis cache) and warm (the same handle again, the cache filled),
// and the managed bytes the process allocates for one warm ask, every thread counted. Requests and bytes
// are what an object store bills and waits on; the bytes allocated are what a node answering hundreds
// of questions a second pays in collections. All three are deterministic for a given build, so each has
// a ceiling just above what it costs, lowered by hand in the commit that earns it, and printed with its
// headroom so that a ceiling left far above its path shows.
//
// And the answer: every question that folds states answers the bits its rows give, synopses on and off.
//
// THE PATHS are the ones `vortex-queries --micro paths` measures at scale: the tables of whole hours, a
// window that cuts slices (its edges read as rows), an identifier left out (the rows its postings find,
// taken out of the tables), a variant's shredded path, the measures' states, the rows when the
// synopses are off (a group by of time once read every object twice in key order), and a needle.
//
// WHY THE FLOOR OF SEVERAL WARM ASKS: the first asks pay the JIT's tiering and the pools' first rents;
// the least of several is the steady state, and anything allocated on every ask still raises it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Tests.Scan;
using Vorticity.Tests.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class QueryPathBudgetTests : IClassFixture<QueryPathBudgetTests.Fixture>
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Services = ["api", "auth", "billing", "cart", "search", "media", "users", "orders"];

    private static readonly string[] Methods = ["GET", "GET", "GET", "POST", "PUT", "DELETE"];

    private static readonly string[] Regions = ["eu-west", "us-east", "ap-south"];

    private const int Hours = 4;

    private const int RowsPerHour = 1_800;

    /// <summary>
    /// The ceilings of each path: requests and bytes cold, requests and bytes warm, and the bytes one warm ask
    /// allocates, 2 % above what it measured alone: in the full suite, the pools other classes left hold more or
    /// less, which moved a path by 1.3 %. Measured on 2026-10-20 at the commit that set them.
    /// </summary>
    private static readonly Dictionary<string, Budget> Ceilings = new(StringComparer.Ordinal)
    {
        ["synopses: count by service"] = new(4, 6880, 0, 0, 77248),
        ["synopses: count by service and hour"] = new(4, 6880, 0, 0, 86912),
        ["synopses: by minute, the last hour, under a predicate"] = new(3, 100970, 2, 35424, 72192),
        ["synopses: an unaligned window"] = new(5, 198994, 2, 66728, 79104),
        ["synopses: every row but one id"] = new(8, 297003, 4, 34768, 71232),
        ["synopses: measures by region and hour"] = new(4, 4112, 0, 0, 95232),
        ["synopses: count by route, a shredded path"] = new(8, 268137, 4, 5902, 79360),
        ["rows: count by service and hour"] = new(8, 392747, 4, 130512, 91008),
        ["rows: count by route, a shredded path"] = new(8, 268137, 4, 5902, 68032),
        ["rows: the rows of one id"] = new(6, 335643, 2, 73408, 195392),
    };

    private readonly Fixture _fixture;

    public QueryPathBudgetTests(Fixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Paths() => [.. Ceilings.Keys];

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task APathStaysWithinItsBudget(string path)
    {
        Budget ceiling = Ceilings[path];
        string directory = Directory.CreateTempSubdirectory("vortex-budget-").FullName;
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = new SynopsisCache(directory, 64L << 20));
            await using VortexDataset dataset = await VortexDataset.OpenAsync(_fixture.Store, new DatasetOptions { Session = session }, Ct);
            _fixture.Store.Reset();
            List<string> cold = await AskAsync(dataset, path);
            (long coldRequests, long coldBytes) = (_fixture.Store.Requests, _fixture.Store.BytesRead);
            _fixture.Store.Reset();
            List<string> warm = await AskAsync(dataset, path);
            (long warmRequests, long warmBytes) = (_fixture.Store.Requests, _fixture.Store.BytesRead);
            Assert.Equal(cold, warm);
            Assert.NotEmpty(warm);

            long allocated = long.MaxValue;
            for (int round = 0; round < 6; round++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                await AskAsync(dataset, path, describe: false);
                allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(precise: true) - before);
            }

            Console.Out.Write(string.Create(
                CultureInfo.InvariantCulture,
                $"QUERY PATH {path}: cold {coldRequests} requests {coldBytes} bytes (ceilings {Shown(ceiling.ColdRequests)}, {Shown(ceiling.ColdBytes)}), " +
                $"warm {warmRequests} requests {warmBytes} bytes (ceilings {Shown(ceiling.WarmRequests)}, {Shown(ceiling.WarmBytes)}), " +
                $"{allocated} bytes allocated warm (ceiling {Shown(ceiling.Allocated)}), {warm.Count} rows answered.\n"));

            // The synopses answer the bits the rows give.
            if (path.StartsWith("synopses: ", StringComparison.Ordinal))
            {
                Assert.Equal(await AskAsync(dataset, path, synopses: false), warm);
            }

            Assert.True(coldRequests <= ceiling.ColdRequests, $"{path}: {coldRequests} requests cold, over {ceiling.ColdRequests}");
            Assert.True(coldBytes <= ceiling.ColdBytes, $"{path}: {coldBytes} bytes cold, over {ceiling.ColdBytes}");
            Assert.True(warmRequests <= ceiling.WarmRequests, $"{path}: {warmRequests} requests warm, over {ceiling.WarmRequests}");
            Assert.True(warmBytes <= ceiling.WarmBytes, $"{path}: {warmBytes} bytes warm, over {ceiling.WarmBytes}");
            Assert.True(allocated <= ceiling.Allocated, $"{path}: {allocated} bytes allocated warm, over {ceiling.Allocated}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Shown(long ceiling) => ceiling == long.MaxValue ? "none" : ceiling.ToString(CultureInfo.InvariantCulture);

    /// <summary>The rows <paramref name="path"/> answers, each a line of its values, sorted; none when not <paramref name="describe"/>.</summary>
    private static async Task<List<string>> AskAsync(VortexDataset dataset, string path, bool? synopses = null, bool describe = true)
    {
        bool fromTables = synopses ?? !path.StartsWith("rows: ", StringComparison.Ordinal);
        Scan<Hit> scan = dataset.Scan<Hit>().With(new ScanOptions { UseSynopses = fromTables, DegreeOfParallelism = 1 });
        DateTimeOffset lastHour = Noon.AddHours(Hours - 1);
        DateTimeOffset cutFrom = Noon.AddMinutes(37.5);
        DateTimeOffset cutTo = Noon.AddMinutes(155.25);
        IAsyncEnumerator<RecordBatch> batches = path[(path.IndexOf(':', StringComparison.Ordinal) + 2)..] switch
        {
            "count by service" => scan.GroupBy(r => r.Service).Select(g => (g.Key, g.Count())).Query.Batches(Ct),
            "count by service and hour" => scan.GroupBy(r => (r.Service, r.At.Truncate(CalendarUnit.Hour))).Select(g => (g.Key.Service, g.Key.Item2, g.Count())).Query.Batches(Ct),
            "by minute, the last hour, under a predicate" => scan.Where(r => r.At >= lastHour && r.Method == "GET" && r.Status == 200)
                .GroupBy(r => r.At.Truncate(CalendarUnit.Minute)).Select(g => (g.Key, g.Count())).Query.Batches(Ct),
            "an unaligned window" => scan.Where(r => r.At >= cutFrom && r.At < cutTo).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Weight))).Query.Batches(Ct),
            "every row but one id" => scan.Where(r => r.RequestId != "r-3000").GroupBy(r => r.Service).Select(g => (g.Key, g.Count())).Query.Batches(Ct),
            "measures by region and hour" => scan.GroupBy(r => (r.Region, r.At.Truncate(CalendarUnit.Hour)))
                .Select(g => (g.Key.Region, g.Key.Item2, g.Count(), g.Sum(r => r.Weight), g.Max(r => r.DurationNs), g.Average(r => r.DurationNs))).Query.Batches(Ct),
            "count by route, a shredded path" => scan.GroupBy(r => r.Attributes["route"].AsString()).Select(g => (g.Key, g.Count())).Query.Batches(Ct),
            "the rows of one id" => scan.Where(r => r.RequestId == "r-3000").ToBatchesAsync(Ct).GetAsyncEnumerator(Ct),
            _ => throw new ArgumentException($"No path named '{path}'.", nameof(path)),
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

    /// <summary>What a path may cost: requests and bytes cold and warm, and the bytes a warm ask allocates.</summary>
    private readonly record struct Budget(long ColdRequests, long ColdBytes, long WarmRequests, long WarmBytes, long Allocated);

    /// <summary>
    /// Four hours of requests from noon, a row every two seconds, appended in parts of five minutes and compacted:
    /// the sealed hours in sorted slices of a minute with every table kept, their attributes shredded, and the
    /// request ids indexed.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private readonly MemoryObjectStore _inner = new MemoryObjectStore();

        public Fixture() => Store = new CountingObjectStore(_inner);

        internal CountingObjectStore Store { get; }

        public async ValueTask InitializeAsync()
        {
            await using VortexDataset dataset = await VortexDataset.CreateAsync(Store, Hit.Schema, new DatasetOptions
            {
                ClusteringKey = [nameof(Hit.At)],
                Identifiers = [nameof(Hit.RequestId)],
                Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
                Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(10_000) },
            });
            Random random = new Random(59);
            int row = 0;
            for (int part = 0; part < Hours * 12; part++)
            {
                Hit[] rows = new Hit[RowsPerHour / 12];
                for (int i = 0; i < rows.Length; i++, row++)
                {
                    int service = random.Next(Services.Length);
                    rows[i] = new Hit(
                        Noon.AddSeconds(row * 2.0),
                        Services[service],
                        Methods[random.Next(Methods.Length)],
                        random.Next(10) switch { < 7 => (short)200, 7 => (short)404, 8 => (short)500, _ => (short)201 },
                        Regions[service % Regions.Length],
                        random.NextInt64(1_000, 5_000_000),
                        random.NextDouble(),
                        $"r-{row}",
                        random.Next(10) < 3
                            ? Variant.Object(("route", Variant.From($"/{Services[service]}/{random.Next(4)}")), ("flag", Variant.From(random.Next(2) == 0 ? "on" : "off")))
                            : Variant.Object(("route", Variant.From($"/{Services[service]}/{random.Next(4)}"))));
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Hit>(rows);
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
    public partial record struct Hit(
        DateTimeOffset At, string Service, string Method, short Status, string Region, long DurationNs, double Weight, string? RequestId, Variant Attributes);
}
