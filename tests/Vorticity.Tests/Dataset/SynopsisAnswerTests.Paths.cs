// Questions on the paths of a variant column, answered from the tables that count them as they count a declared
// column: a path as a key, under a filter, as an integer measure and as a double one. A double in an object that also
// holds a float there, which the class of doubles leaves out, a conversion, a sum of a path whose values repeat, which
// the tables key rather than sum, and a key left in the residual, whose tables count only the rows that hold it: those
// read the rows. Every answer gives the bits of its rows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SynopsisAnswerTests
{
    public static TheoryData<string, bool> PathQuestions() => new()
    {
        { "count by route", true },
        { "bytes by route and hour", true },
        { "status sum by route", false },
        { "count by service under status 500", true },
        { "count by status under a route", true },
        { "count by flag, a path left in the residual", false },
        { "count by route under either of two statuses, or neither", true },
        { "mean duration by route", true },
        { "mean duration by route, a float among them", true },
        { "count by service under a converted status", false },
    };

    /// <summary>
    /// Each question with the synopses on and off: the same bits, and the tables answer the questions on paths they
    /// count, strictly typed; the others read rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(PathQuestions))]
    public async Task AQuestionOnAPathIsAnsweredLikeOneOnAColumn(string question, bool fromTables)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        bool floats = question.EndsWith("a float among them", StringComparison.Ordinal);
        await using VortexDataset dataset = await AttributedAsync(store, floats);
        List<string>[] answers = new List<string>[2];
        ScanMetrics[] metrics = new ScanMetrics[2];
        foreach (bool synopses in new[] { true, false })
        {
            Scan<Attributed> scan = dataset.Scan<Attributed>().With(new ScanOptions { UseSynopses = synopses });
            (answers[synopses ? 0 : 1], metrics[synopses ? 0 : 1]) = await GroupsAsync(question switch
            {
                "count by route" => scan.GroupBy(r => r.Attributes["route"].AsString()).Select(g => (g.Key, g.Count())),
                "bytes by route and hour" => scan
                    .GroupBy(r => (r.Attributes["route"].AsString(), r.At.Truncate(CalendarUnit.Hour)))
                    .Select(g => (g.Key.Item1, g.Key.Item2, g.Count(), g.Sum(r => r.Attributes["bytes"].AsInt64()), g.Min(r => r.Attributes["bytes"].AsInt64()), g.Max(r => r.Attributes["bytes"].AsInt64()))),
                "status sum by route" => scan.GroupBy(r => r.Attributes["route"].AsString()).Select(g => (g.Key, g.Sum(r => r.Attributes["status"].AsInt64()))),
                "count by service under status 500" => scan.Where(r => r.Attributes["status"].AsInt64() >= 500)
                    .GroupBy(r => r.Service).Select(g => (g.Key, g.Count())),
                "count by status under a route" => scan.Where(r => r.Attributes["route"].AsString() == "/r/1")
                    .GroupBy(r => r.Attributes["status"].AsInt64()).Select(g => (g.Key, g.Count())),
                "count by flag, a path left in the residual" => scan.GroupBy(r => r.Attributes["flag"].AsString()).Select(g => (g.Key, g.Count())),
                "count by route under either of two statuses, or neither" => scan
                    .Where(r => r.Attributes["status"].AsInt64() == 404 || !(r.Attributes["status"].AsInt64() == 200 || r.Attributes["status"].AsInt64() == 201))
                    .GroupBy(r => r.Attributes["route"].AsString()).Select(g => (g.Key, g.Count())),
                "mean duration by route" or "mean duration by route, a float among them" => scan
                    .GroupBy(r => r.Attributes["route"].AsString()).Select(g => (g.Key, g.Average(r => r.Attributes["duration"].AsDouble()))),
                _ => scan.Where(r => r.Attributes["status"].ToInt64() >= 500).GroupBy(r => r.Service).Select(g => (g.Key, g.Count())),
            });
        }

        Console.Out.Write($"PATH {question}: {metrics[0].SynopsisRows} table rows and {metrics[0].Rows} rows, against {metrics[1].Rows} rows.\n");
        Assert.Equal(answers[1], answers[0]);
        Assert.NotEmpty(answers[0]);
        Assert.Equal(fromTables, metrics[0].SynopsisRows > 0);
        Assert.True(metrics[0].Rows <= metrics[1].Rows);

        // The object that holds a float reads its rows, every other answers from its tables.
        Assert.Equal(floats, metrics[0].Rows > 0 && metrics[0].Rows < metrics[1].Rows);
    }

    /// <summary>
    /// Two hours and a quarter of records from noon, a row every 1.25 s, appended in sixteen parts and compacted:
    /// attributes of a route of nine, a status mostly an integer and sent as text by one service in five, a flag on a
    /// third of the rows, a double duration, a float in one row of the last part with <paramref name="floats"/>, and
    /// bytes that rarely repeat. Slices of a hundred rows, and every table kept.
    /// </summary>
    private static async Task<VortexDataset> AttributedAsync(Vorticity.IO.IObjectStore store, bool floats = false)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Attributed.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Attributed.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 100 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
        }, Ct);
        Random random = new Random(53);
        for (int part = 0; part < 16; part++)
        {
            Attributed[] rows = [.. Enumerable.Range(0, 400).Select(i =>
            {
                string service = Services[random.Next(Services.Length)];
                long status = random.Next(4) switch { 0 => 200L, 1 => 201L, 2 => 404L, _ => 500L };
                List<(string, Variant)> fields =
                [
                    ("route", Variant.From($"/r/{random.Next(9)}")),
                    ("status", service == "search" ? Variant.From(status.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Variant.From(status)),
                    ("duration", floats && part == 15 && i == 7 ? Variant.From(12.5f) : Variant.From(Math.Round(random.NextDouble() * 250, 2))),
                    ("bytes", Variant.From(random.NextInt64(1, 1L << 40))),
                ];
                if (random.Next(3) == 0)
                {
                    fields.Add(("flag", Variant.From(random.Next(2) == 0 ? "on" : "off")));
                }

                return new Attributed(Noon.AddSeconds((part * 400 + i) * 1.25), service, Variant.Object([.. fields]));
            })];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Attributed>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Attributed(DateTimeOffset At, string Service, Variant Attributes);
}
