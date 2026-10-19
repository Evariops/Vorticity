// Keys that are data, counted: the paths a dataset's variant column holds, with their counts by class,
// equal what was appended at every version of the dataset, whether its rows lie in appended objects or
// in objects jobs wrote; and a question on a key prunes the slices of an object that do not hold it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class VariantDiscoveryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Three seeds of appends of attributes whose user keys are data, four hundred of them, among a
    /// route, a status now and then text, a nested cart, a list, a flag, and rows that are no object or
    /// null, each append followed by none, one or every compaction job: at every version, discovery
    /// gives each path and class the count of the values appended there.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DiscoveryCountsEveryPathAtEveryVersion(int seed)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Event.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 200 },
        }, Ct);
        Random random = new Random(seed);
        Dictionary<(string, PathClass), long> truth = [];
        int next = 0;
        for (int part = 0; part < 12; part++)
        {
            Event[] rows = new Event[random.Next(100, 400)];
            for (int i = 0; i < rows.Length; i++, next++)
            {
                rows[i] = new Event(Noon.AddSeconds(next * 2.5), Row(random, truth));
            }

            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Event>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            int jobs = random.Next(3) switch { 0 => 0, 1 => 1, _ => int.MaxValue };
            for (int job = 0; job < jobs && await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null; job++)
            {
            }

            Dictionary<(string, PathClass), long> found = (await VariantDiscovery.PathsAsync(dataset, nameof(Event.Attributes), Ct))
                .ToDictionary(path => (string.Join('/', path.Path), path.Class), path => path.Values);
            Assert.Equal(truth.OrderBy(p => p.Key.Item1, StringComparer.Ordinal).ThenBy(p => p.Key.Item2), found.OrderBy(p => p.Key.Item1, StringComparer.Ordinal).ThenBy(p => p.Key.Item2));
        }

        Assert.True(truth.Count(path => path.Key.Item1.StartsWith("user.", StringComparison.Ordinal)) > 300, "keys that are data");
        Assert.Contains(dataset.Levels.Occupied(), level => level.Level > 0);
    }

    /// <summary>
    /// Blocks of a hundred rows and slices of five hundred: a key a hundred rows of one slice hold
    /// leaves live the blocks of that slice alone, of the object that holds it, and the answer is the
    /// hundred rows.
    /// </summary>
    [Fact]
    public async Task AQuestionOnAKeyPrunesTheSlicesThatLackIt()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Event.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(Event.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 500 },
            Write = new VortexWriteOptions { BlockRows = 100 },
        }, Ct);
        for (int part = 0; part < 16; part++)
        {
            Event[] rows = [.. Enumerable.Range(part * 400, 400).Select(i => new Event(
                Noon.AddSeconds(i * 1.25),
                Variant.Object([("route", Variant.From($"/r/{i % 7}")), .. (i is >= 3_000 and < 3_100 ? [("deploy", Variant.From(i))] : Array.Empty<(string, Variant)>())])))];
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Event>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        Assert.Equal(100, await dataset.Scan<Event>().Where(r => r.Attributes.Has("deploy")).CountAsync(Ct));
        ScanPlan plan = await dataset.Scan<Event>().Where(r => r.Attributes.Has("deploy")).ExplainAsync(Ct);
        PruningStep step = Assert.Single(plan.Pruning, s => s.Structure == "path directory");

        // A slice holds about 500 rows, so its blocks and the two it shares with its neighbours.
        Assert.InRange(plan.LiveBlocks, 1, 7);
        Assert.Equal(plan.Blocks - plan.LiveBlocks, step.BlocksPruned);
    }

    /// <summary>A row of attributes, each of its paths and classes counted in <paramref name="truth"/> as it is built.</summary>
    private static Variant Row(Random random, Dictionary<(string, PathClass), long> truth)
    {
        void Count(string path, PathClass @class) => truth[(path, @class)] = truth.GetValueOrDefault((path, @class)) + 1;

        switch (random.Next(100))
        {
            case 0:
                Count(string.Empty, PathClass.Null);
                return Variant.Null;
            case 1 or 2:
                Count(string.Empty, PathClass.String);
                return Variant.From("not an object");
        }

        Count(string.Empty, PathClass.Object);
        List<(string, Variant)> fields = [("route", Variant.From($"/r/{random.Next(9)}"))];
        Count("route", PathClass.String);
        if (random.Next(10) == 0)
        {
            fields.Add(("status", Variant.From("timeout")));
            Count("status", PathClass.String);
        }
        else
        {
            fields.Add(("status", Variant.From(200 + random.Next(4))));
            Count("status", PathClass.Integer);
        }

        if (random.Next(2) == 0)
        {
            string key = $"user.{random.Next(400)}.score";
            fields.Add((key, Variant.From(random.NextDouble())));
            Count(key, PathClass.Double);
        }

        if (random.Next(5) == 0)
        {
            fields.Add(("cart", Variant.Object(("items", Variant.From(random.Next(9))))));
            Count("cart", PathClass.Object);
            Count("cart/items", PathClass.Integer);
        }

        if (random.Next(10) == 0)
        {
            fields.Add(("tags", Variant.Array(Variant.From("a"), Variant.From("b"))));
            Count("tags", PathClass.List);
        }

        if (random.Next(3) == 0)
        {
            fields.Add(("flag", Variant.From(random.Next(2) == 0)));
            Count("flag", PathClass.Boolean);
        }

        return Variant.Object([.. fields]);
    }

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, Variant Attributes);
}
