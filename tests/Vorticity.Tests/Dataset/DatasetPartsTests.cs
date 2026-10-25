// A question cut into parts, each run apart as the same question over its rows, its partial sent as bytes and merged: the
// merge answers what the whole question answers, to the bits, its filters on groups, its order and its window applied to
// the merge alone.
//
// THE ORACLE IS THE WHOLE QUESTION: every merge is held to the answers the same question gives over every row, each value
// compared by its text, which tells apart any two doubles that differ in a bit but a NaN's payload.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetPartsTests
{
    private const int Objects = 6;
    private const int PerObject = 40_000;

    private static readonly DateTimeOffset Start = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Regions = ["eu-west", "eu-north", "us-east", "us-west", "ap-south", "sa-east", "af-south"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Six objects of four hours each, a question over the window of hours 3 to 20 grouped by region and hour: cut into
    /// four parts, each run by a question of its own, the partials read back from their bytes and merged in another order,
    /// it answers every group the whole question answers, every value to the bit.
    /// </summary>
    [Fact]
    public async Task AQuestionCutIntoPartsMergesToTheBitsOfTheWholeQuestion()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        List<string> whole = await AnswersAsync(Hourly(dataset, null));

        RowRange[] parts = await Hourly(dataset, null).PartsAsync(4, Ct);
        Assert.InRange(parts.Length, 2, 4);
        for (int p = 1; p < parts.Length; p++)
        {
            Assert.Equal(parts[p - 1].End, parts[p].Start);
        }

        List<AggregationPartial> partials = [];
        foreach (RowRange part in parts)
        {
            // Each part as another node runs it: its own question, its partial sent as bytes.
            AggregationPartial partial = await Hourly(dataset, part).PartialAsync(Ct);
            partials.Add(AggregationPartial.FromBytes(partial.Bytes.ToArray()));
        }

        partials.Reverse();
        List<string> merged = await AnswersAsync(Hourly(dataset, null).Merge(partials));
        Assert.True(whole.Count > 100, $"{whole.Count} groups");
        Assert.Equal(whole, merged);
    }

    /// <summary>
    /// The same question with a filter on groups, an order on an aggregate and a window: the parts give every group's states,
    /// and the merge applies the three, as the whole question does.
    /// </summary>
    [Fact]
    public async Task TheFiltersOrderAndWindowOnGroupsApplyToTheMerge()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        List<string> whole = await AnswersAsync(Busiest(dataset, null), ordered: true);
        List<AggregationPartial> partials = [];
        foreach (RowRange part in await Busiest(dataset, null).PartsAsync(3, Ct))
        {
            partials.Add(await Busiest(dataset, part).PartialAsync(Ct));
        }

        List<string> merged = await AnswersAsync(Busiest(dataset, null).Merge(partials), ordered: true);
        Assert.Equal(5, whole.Count);
        Assert.Equal(whole, merged);
    }

    /// <summary>A node that merges the partials of two parts gives the partial of their rows, which merges with the others' as theirs would.</summary>
    [Fact]
    public async Task APartialOfMergedPartialsMergesAsTheirs()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        List<string> whole = await AnswersAsync(Hourly(dataset, null));
        RowRange[] parts = await Hourly(dataset, null).PartsAsync(4, Ct);
        List<AggregationPartial> partials = [];
        foreach (RowRange part in parts)
        {
            partials.Add(await Hourly(dataset, part).PartialAsync(Ct));
        }

        AggregationPartial firstTwo = await Hourly(dataset, null).Merge(partials.Take(2)).PartialAsync(Ct);
        List<string> merged = await AnswersAsync(Hourly(dataset, null).Merge([firstTwo, .. partials.Skip(2)]));
        Assert.Equal(whole, merged);
    }

    /// <summary>A file's question cut at its chunks, each part read through a file of its own, merges as the whole file's answers.</summary>
    [Fact]
    public async Task AFilesQuestionCutAtItsChunksMergesToItsAnswers()
    {
        List<Request> rows = Rows(0, Objects * PerObject);
        string path = Path.Combine(Path.GetTempPath(), $"vx-parts-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Request>(path))
            {
                await writer.WriteAsync<Request>(rows.ToArray(), Ct);
                await writer.CompleteAsync(Ct);
            }

            List<string> whole;
            RowRange[] parts;
            await using (VortexFile file = await VortexFile.OpenAsync(path, Ct))
            {
                whole = await AnswersAsync(Hourly(file.Scan<Request>()));
                parts = await Hourly(file.Scan<Request>()).PartsAsync(4, Ct);
            }

            Assert.InRange(parts.Length, 2, 4);
            List<AggregationPartial> partials = [];
            foreach (RowRange part in parts)
            {
                await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
                partials.Add(await Hourly(file.Scan<Request>().Rows(part)).PartialAsync(Ct));
            }

            await using VortexFile merging = await VortexFile.OpenAsync(path, Ct);
            Assert.Equal(whole, await AnswersAsync(Hourly(merging.Scan<Request>()).Merge(partials)));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A partial of another question, whose measures differ, is refused when the merge reads it.</summary>
    [Fact]
    public async Task APartialOfAnotherQuestionIsRefused()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        AggregationPartial other = await dataset.Scan<Request>()
            .GroupBy(r => r.Region).Select(g => (g.Key, g.Count())).PartialAsync(Ct);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await AnswersAsync(Hourly(dataset, null).Merge([other])));
    }

    /// <summary>An exact distinct count, whose states are the values themselves, takes no parts.</summary>
    [Fact]
    public async Task AnExactDistinctCountTakesNoParts()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await FilledAsync(store);
        Vorticity.Aggregation exact = dataset.Scan<Request>().GroupBy(r => r.Region).Select(g => (g.Key, g.CountDistinct(r => r.User)));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await exact.PartsAsync(4, Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await exact.PartialAsync(Ct));
    }

    /// <summary>Six objects of four hours each, appended in the order of their hours.</summary>
    private static async Task<VortexDataset> FilledAsync(MemoryObjectStore store)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions { ClusteringKey = [nameof(Request.At)] }, Ct);
        for (int o = 0; o < Objects; o++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Request>(Rows(o * PerObject, PerObject).ToArray(), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return dataset;
    }

    /// <summary>
    /// Rows <c>[first, first + count)</c> of a day, a row every 0.36 s: bytes of either sign, latencies of many magnitudes,
    /// whose sums no double holds exactly, a score null one row in seven.
    /// </summary>
    private static List<Request> Rows(int first, int count)
    {
        Random random = new Random(0x9A27 + first);
        return [.. Enumerable.Range(first, count).Select(i => new Request(
            Regions[random.Next(Regions.Length)],
            Start.AddTicks(i * 3_600_000L),
            random.NextInt64(-1_000_000, 10_000_000),
            random.NextDouble() * Math.Pow(10, random.Next(-3, 7)),
            i % 7 == 0 ? null : random.Next(-500, 500),
            $"user-{random.Next(3_000)}"))];
    }

    /// <summary>Hours 3 to 20, by region and hour: counts, sums, a mean, extremes, a quantile and an approximate distinct count.</summary>
    private static Vorticity.Aggregation Hourly(VortexDataset dataset, RowRange? part) =>
        Hourly(part is { } rows ? dataset.Scan<Request>().Rows(rows) : dataset.Scan<Request>());

    private static Vorticity.Aggregation Hourly(Scan<Request> scan) => scan
        .Where(r => r.At >= Start.AddHours(3) && r.At < Start.AddHours(20))
        .GroupBy(r => (r.Region, r.At.Truncate(CalendarUnit.Hour)))
        .Select(g => (g.Key.Region, g.Key.Item2, g.Count(), g.Sum(r => r.Bytes), g.Sum(r => r.Latency), g.Average(r => r.Latency),
            g.Min(r => r.Score), g.Max(r => r.Latency), g.Quantile(r => r.Latency, 0.9), g.ApproximateCountDistinct(r => r.User)));

    /// <summary>The five regions and hours of the most bytes among those of more than 400 requests.</summary>
    private static Vorticity.Aggregation Busiest(VortexDataset dataset, RowRange? part) =>
        (part is { } rows ? dataset.Scan<Request>().Rows(rows) : dataset.Scan<Request>())
            .GroupBy(r => (r.Region, r.At.Truncate(CalendarUnit.Hour)))
            .Where(g => g.Count() > 400L)
            .OrderByDescending(g => g.Sum(r => r.Bytes))
            .Take(5)
            .Select(g => (g.Key.Region, g.Key.Item2, g.Count(), g.Sum(r => r.Bytes), g.Sum(r => r.Latency), g.Average(r => r.Latency),
                g.Min(r => r.Score), g.Max(r => r.Latency), g.Quantile(r => r.Latency, 0.9), g.ApproximateCountDistinct(r => r.User)));

    /// <summary>The answers as text, each double's shortest text telling its bits apart, in the order of their keys unless delivered in order.</summary>
    private static async Task<List<string>> AnswersAsync(Vorticity.Aggregation question, bool ordered = false)
    {
        List<HourTotal> answers = await question.As<HourTotal>().ToRecordsAsync(Ct).ToListAsync(Ct);
        IEnumerable<HourTotal> listed = ordered ? answers : answers.OrderBy(a => a.Region, StringComparer.Ordinal).ThenBy(a => a.Hour);
        return [.. listed.Select(a => a.ToString())];
    }

    [VortexRecord]
    public partial record struct Request(string Region, DateTimeOffset At, long Bytes, double Latency, int? Score, string User);

    [VortexRecord]
    public partial record struct HourTotal(
        string Region, DateTimeOffset Hour, long Count, long Bytes, double Latency, double? MeanLatency, int? LeastScore, double? GreatestLatency, double? Latency90,
        long Users);
}
