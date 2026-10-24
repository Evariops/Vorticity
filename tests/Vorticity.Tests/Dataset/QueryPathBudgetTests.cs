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
// THE PATHS are the questions `vortex-queries --micro paths` asks at scale, every one read from the rows:
// counts over whole hours, a window that cuts slices, an identifier left out, a variant's shredded path,
// measures, a group by of time (which once read every object twice in key order), a needle; and two of them
// on eight lanes, whose lanes share the objects' segments.
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
    /// allocates, 2 % above what it measured in a process of its own, as `tests/runs-alone.sh` runs it: beside other
    /// classes, the pools they left move a path by up to 3 %. Measured on 2026-10-24, every question read from the rows
    /// of objects that keep them in the order they came, with no synopsis part nor tables; the ceilings the cost formulas
    /// give come with the per-column synopses and the planner.
    /// </summary>
    private static readonly Dictionary<string, Budget> Ceilings = new(StringComparer.Ordinal)
    {
        ["count by service"] = new(4, 169564, 0, 0, 36144),
        ["count by service, at eight lanes"] = new(4, 169564, 0, 0, 58240),
        ["count by service and hour"] = new(4, 169564, 0, 0, 41648),
        ["by minute, the last hour, under a predicate"] = new(1, 42151, 0, 0, 39488),
        ["an unaligned window"] = new(3, 127413, 0, 0, 43520),
        ["an unaligned window, at eight lanes"] = new(3, 127413, 0, 0, 63808),
        ["every row but one id"] = new(4, 169564, 0, 0, 46272),
        ["measures by region and hour"] = new(4, 169564, 0, 0, 49328),
        ["count by route, a shredded path"] = new(4, 169564, 0, 0, 55888),
        ["the rows of one id"] = new(4, 169564, 0, 0, 81328),
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
    private static async Task<List<string>> AskAsync(VortexDataset dataset, string path, bool describe = true)
    {
        const string Lanes = ", at eight lanes";
        bool wide = path.EndsWith(Lanes, StringComparison.Ordinal);
        path = wide ? path[..^Lanes.Length] : path;
        Scan<Hit> scan = dataset.Scan<Hit>().With(new ScanOptions { DegreeOfParallelism = wide ? 8 : 1 });
        DateTimeOffset lastHour = Noon.AddHours(Hours - 1);
        DateTimeOffset cutFrom = Noon.AddMinutes(37.5);
        DateTimeOffset cutTo = Noon.AddMinutes(155.25);
        IAsyncEnumerator<RecordBatch> batches = path switch
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
    /// the sealed hours in time slices of a minute, their attributes shredded, and the request ids indexed.
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
