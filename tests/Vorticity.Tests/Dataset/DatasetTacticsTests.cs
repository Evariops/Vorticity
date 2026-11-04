// The reading tactics (spec G; G1 to G4): a window of an order read late, the column of the order alone and then the rows
// of the places it keeps; the best objects and granules first, the bound their rows set leaving the others unread; a set
// one question finds pushed into another; a small table applied to a column by its codes.
//
// THE ORACLE IS LINQ: every answer is LINQ's over the rows read back, nulls last and ties in the rows' order; the granules a
// correlated order leaves unread are most of them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetTacticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Six appends of 20 000 rows sorted by their service, a score one row in twenty null: windows of an order on the score,
    /// either way, past rows, under a filter, wider than the rows that are not null, answer as LINQ does (G1); the first
    /// column of the sort key walks in order.
    /// </summary>
    [Fact]
    public async Task AWindowOfAnOrderIsReadLate()
    {
        Random random = new Random(97);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        await AppendAsync(dataset, 6, 20_000, (i, seq) => new Row($"svc-{random.Next(0, 30):D2}", seq, random.Next(20) == 0 ? null : Math.Round(random.NextDouble() * 1000, 1)));
        List<Row> all = await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);
        (string Name, Func<Row, bool> Keep, Func<Scan<Row>, Scan<Row>> Filter, bool Descending, int Skip, int Take)[] cases =
        [
            ("desc 10", static _ => true, static s => s, true, 0, 10),
            ("asc 25 past 5", static _ => true, static s => s, false, 5, 25),
            ("desc 7 of svc-03", static r => r.Service == "svc-03", static s => s.Where(r => r.Service == "svc-03"), true, 0, 7),
            ("asc 2000", static _ => true, static s => s, false, 0, 2000),
            ("desc past the rows not null", static r => r.Service == "svc-07", static s => s.Where(r => r.Service == "svc-07"), true, 0, 20_000),
        ];
        foreach ((string name, Func<Row, bool> keep, Func<Scan<Row>, Scan<Row>> filter, bool descending, int skip, int take) in cases)
        {
            IOrderedEnumerable<Row> nullsLast = all.Where(keep).OrderBy(static r => r.Score is null);
            List<long> want = [.. (descending ? nullsLast.ThenByDescending(static r => r.Score) : nullsLast.ThenBy(static r => r.Score)).Skip(skip).Take(take).Select(static r => r.Sequence)];
            Scan<Row> scan = filter(dataset.Scan<Row>());
            scan = descending ? scan.OrderByDescending(r => r.Score) : scan.OrderBy(r => r.Score);
            Assert.True(want.SequenceEqual(await scan.Select(r => r.Sequence).Skip(skip).Take(take).ToListAsync(Ct)), name);
        }

        Assert.Equal(
            all.OrderBy(static r => r.Service, StringComparer.Ordinal).Take(15).Select(static r => r.Sequence),
            await dataset.Scan<Row>().OrderBy(r => r.Service).Select(r => r.Sequence).Take(15).ToListAsync(Ct));
    }

    /// <summary>
    /// Six appends of 40 000 rows whose score grows with their arrival, three services: the top ten either way, by the score
    /// or by the sequence, read the best granules alone, a few of some 30 (G2); the places of the best scores pushed into a
    /// second question read their rows alone (G3); a set of no value reads nothing, and one past its bound is refused.
    /// </summary>
    [Fact]
    public async Task TheBestGranulesAreReadFirstAndASetIsPushedIntoAnotherQuestion()
    {
        Random random = new Random(5);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        await AppendAsync(dataset, 6, 40_000, (i, seq) => new Row($"svc-{random.Next(0, 3)}", seq, seq));
        List<Row> all = await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);
        long granules = (all.Count + 8191) / 8192;
        foreach (bool descending in new[] { true, false })
        {
            List<long> want = [.. (descending ? all.OrderByDescending(static r => r.Score) : all.OrderBy(static r => r.Score)).Take(10).Select(static r => r.Sequence)];
            Projection<long> byScore = (descending ? dataset.Scan<Row>().OrderByDescending(r => r.Score) : dataset.Scan<Row>().OrderBy(r => r.Score)).Select(r => r.Sequence).Take(10);
            Assert.Equal(want, await byScore.ToListAsync(Ct));
            Projection<long> bySequence = (descending ? dataset.Scan<Row>().OrderByDescending(r => r.Sequence) : dataset.Scan<Row>().OrderBy(r => r.Sequence)).Select(r => r.Sequence).Take(10);
            Assert.Equal(want, await bySequence.ToListAsync(Ct));
            Console.WriteLine($"TOP {(descending ? "desc" : "asc")}: {byScore.Metrics.BlocksDecoded} and {bySequence.Metrics.BlocksDecoded} blocks decoded of ~{granules} granules");
            Assert.True(byScore.Metrics.BlocksDecoded <= granules / 3 && bySequence.Metrics.BlocksDecoded <= granules / 3, $"{byScore.Metrics.BlocksDecoded} and {bySequence.Metrics.BlocksDecoded} blocks decoded");
        }

        foreach (double floor in new[] { 239_990.0, 120_000.5 })
        {
            Scan<Row> second = await dataset.Scan<Row>().WhereInAsync(r => r.Sequence, dataset.Scan<Row>().Where(r => r.Score >= floor).Select(r => r.Sequence), cancellationToken: Ct);
            Projection<long> rows = second.Select(r => r.Sequence);
            Assert.Equal(all.Where(r => r.Score >= floor).Select(static r => r.Sequence).Order(), (await rows.ToListAsync(Ct)).Order());
            Console.WriteLine($"SEMI-JOIN {floor}: {rows.Metrics.BlocksDecoded} blocks decoded of ~{granules}");
        }

        Projection<long> nothing = (await dataset.Scan<Row>().WhereInAsync(r => r.Sequence, dataset.Scan<Row>().Where(r => r.Score < 0).Select(r => r.Sequence), cancellationToken: Ct)).Select(r => r.Sequence);
        Assert.Empty(await nothing.ToListAsync(Ct));
        Assert.Equal(0, nothing.Metrics.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dataset.Scan<Row>().WhereInAsync(r => r.Sequence, dataset.Scan<Row>().Select(r => r.Sequence), maxValues: 1000, cancellationToken: Ct));
    }

    /// <summary>
    /// A table of services to teams (G4): grouped by the team, a filter on it, its negation, its null and a disjunction, a
    /// group filter on it, an integer column through a table of its own, each as LINQ answers.
    /// </summary>
    [Fact]
    public async Task ATableAppliesToAColumnByItsCodes()
    {
        Random random = new Random(101);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        await AppendAsync(dataset, 3, 20_000, (i, seq) => new Row($"svc-{random.Next(0, 30):D2}", seq, random.NextDouble()));
        List<Row> all = await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Dictionary<string, string> teams = [];
        for (int s = 0; s < 25; s++)
        {
            teams[$"svc-{s:D2}"] = s < 10 ? "alpha" : s < 20 ? "beta" : "gamma";
        }

        string? Team(Row r) => teams.TryGetValue(r.Service, out string? team) ? team : null;
        Assert.Equal(
            all.GroupBy(Team).Select(static g => $"{g.Key ?? "null"}:{g.Count()}").Order(StringComparer.Ordinal),
            (await dataset.Scan<Row>().GroupBy(r => r.Service.Map(teams)).Select(g => (g.Key, g.Count())).As<TeamCount>().ToRecordsAsync(Ct).ToListAsync(Ct))
                .Select(static c => $"{c.Team ?? "null"}:{c.Count}").Order(StringComparer.Ordinal));
        (string Name, Func<Row, bool> Keep, Func<Scan<Row>, Scan<Row>> Filter)[] cases =
        [
            ("== beta", r => Team(r) == "beta", s => s.Where(r => r.Service.Map(teams) == "beta")),
            ("!(== beta)", r => Team(r) is { } team && team != "beta", s => s.Where(r => !(r.Service.Map(teams) == "beta"))),
            ("is null", r => Team(r) is null, s => s.Where(r => r.Service.Map(teams) == null)),
            ("starts with g or is alpha", r => Team(r) is { } team && (team.StartsWith('g') || team == "alpha"), s => s.Where(r => r.Service.Map(teams).StartsWith("g") | r.Service.Map(teams) == "alpha")),
        ];
        foreach ((string name, Func<Row, bool> keep, Func<Scan<Row>, Scan<Row>> filter) in cases)
        {
            Assert.True(all.Count(keep) == (await filter(dataset.Scan<Row>()).Select(r => r.Sequence).ToListAsync(Ct)).Count, name);
        }

        List<TeamCount> beta = await dataset.Scan<Row>().GroupBy(r => r.Service.Map(teams)).Where(g => g.Key == "beta").Select(g => (g.Key, g.Count())).As<TeamCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal([new TeamCount("beta", all.Count(r => Team(r) == "beta"))], beta);
        Dictionary<long, string> parity = new Dictionary<long, string> { [0] = "zero", [1] = "one", [2] = "two" };
        Assert.Equal(
            "null:27 one:1 two:1 zero:1",
            string.Join(" ", (await dataset.Scan<Row>().Where(r => r.Sequence < 30).GroupBy(r => r.Sequence.Map(parity)).Select(g => (g.Key, g.Count())).As<TeamCount>().ToRecordsAsync(Ct).ToListAsync(Ct))
                .Select(static b => $"{b.Team ?? "null"}:{b.Count}").Order(StringComparer.Ordinal)));
    }

    private static ValueTask<VortexDataset> CreateAsync(Vorticity.IO.IObjectStore store) =>
        VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Row.Service))], ObjectBytes = 1 << 20, MergeFanout = 4, CalmAfter = null },
            ContentsObjectBytes = 0,
        }, Ct);

    /// <summary>Appends <paramref name="appends"/> objects of <paramref name="rows"/> rows each, <paramref name="make"/> giving each row from its place and sequence, then runs the due merges.</summary>
    private static async Task AppendAsync(VortexDataset dataset, int appends, int rows, Func<int, long, Row> make)
    {
        long sequence = 0;
        for (int a = 0; a < appends; a++)
        {
            Row[] made = new Row[rows];
            for (int i = 0; i < rows; i++)
            {
                made[i] = make(i, sequence++);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(made, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(cancellationToken: Ct) is not null)
        {
        }
    }

    [VortexRecord]
    public partial record struct Row(string Service, long Sequence, double? Score);

    [VortexRecord]
    public partial record struct TeamCount(string? Team, long Count);
}
