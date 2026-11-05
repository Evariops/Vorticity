// The paths of a variant column among the rows a scan of a dataset keeps: every path and class a kept row holds is listed, a
// count marked exact is the number of kept rows that hold it, and the option for exact counts gives the census of the kept rows,
// over objects appended and compacted, with rows deleted, under no filter and filters on the key, on a column and on a path.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class VariantPathsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The questions: no filter, a range of the key, a column, a path, and a column and a path together.</summary>
    private static readonly (string Name, Func<Probe<Event>, Predicate>? Filter, Func<Event, bool> Keeps)[] Questions =
    [
        ("every row", null, _ => true),
        ("a quarter of an hour", r => r.At >= Noon.AddMinutes(10) & r.At < Noon.AddMinutes(25), r => r.At >= Noon.AddMinutes(10) && r.At < Noon.AddMinutes(25)),
        ("one source", r => r.Source == 2, r => r.Source == 2),
        ("a key that is rare", r => r.Attributes.Has("deploy"), r => r.Attributes.Has("deploy")),
        ("a source within a range", r => r.Source == 4 & r.At >= Noon.AddMinutes(5), r => r.Source == 4 && r.At >= Noon.AddMinutes(5)),
    ];

    /// <summary>
    /// Three seeds of appends of rows whose keys are data, some compacted and some deleted: under each question, every path and
    /// class a kept row holds is listed, each count marked exact is exact, and the exact option is the census of the kept rows.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NoPathAKeptRowHoldsIsMissed(int seed)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        List<Event> rows = await FillAsync(dataset, new Random(seed));

        foreach ((string name, Func<Probe<Event>, Predicate>? filter, Func<Event, bool> keeps) in Questions)
        {
            Dictionary<string, long> truth = Truth(rows.Where(keeps), r => r.Attributes);
            VariantPaths found = await Scan(dataset, filter).PathsAsync(r => r.Attributes, cancellationToken: Ct);
            Assert.True(found.Complete, name);
            Dictionary<string, VariantPath> listed = found.ToDictionary(Key);
            foreach ((string path, long held) in truth)
            {
                Assert.True(listed.TryGetValue(path, out VariantPath at), $"{name}: {path} is missing");
                if (at.Exact)
                {
                    Assert.True(held == at.Rows, $"{name}: {path} is held by {held} rows, and {at.Rows} said exact");
                }
            }

            foreach (VariantPath path in found.Where(path => path.Exact))
            {
                Assert.True(truth.ContainsKey(Key(path)), $"{name}: {Key(path)} is said exact and no kept row holds it");
            }

            VariantPaths exact = await Scan(dataset, filter).PathsAsync(r => r.Attributes, new PathsOptions { Exact = true }, Ct);
            Assert.True(exact.Exact && exact.Complete, name);
            Assert.Equal(Sorted(truth), Sorted(exact.ToDictionary(Key, path => path.Rows)));
        }
    }

    /// <summary>
    /// The objects answered side by side, eight at once, give the answer one at a time gives: each path's rows, estimates
    /// among them, whether they are exact, and what was read.
    /// </summary>
    [Fact]
    public async Task ObjectsAnsweredSideBySideAnswerAsOneAtATime()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (VortexDataset dataset = await CreateAsync(store))
        {
            await FillAsync(dataset, new Random(41));
        }

        await using VortexSession alone = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 1);
        await using VortexSession eight = VortexSession.Create(static options => options.MaxDegreeOfParallelism = 8);
        await using VortexDataset one = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = alone }, Ct);
        await using VortexDataset many = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = eight }, Ct);
        foreach ((string name, Func<Probe<Event>, Predicate>? filter, _) in Questions)
        {
            VariantPaths serial = await Scan(one, filter).PathsAsync(r => r.Attributes, cancellationToken: Ct);
            VariantPaths parallel = await Scan(many, filter).PathsAsync(r => r.Attributes, cancellationToken: Ct);
            Assert.True(serial.Count > 0, name);
            Assert.Equal(serial.Select(path => (Key(path), path.Rows, path.Exact)), parallel.Select(path => (Key(path), path.Rows, path.Exact)));
            Assert.Equal((serial.Complete, serial.Reading), (parallel.Complete, parallel.Reading));
        }
    }

    /// <summary>
    /// A window of the key, over appended objects and over compacted ones: the granules it cuts are read again and their kept
    /// rows counted, so that every path listed is held by a kept row, every path a kept row holds is listed, and a count said
    /// exact is exact. Keys that are data, a user's score, leave paths in those granules' other rows that the kept rows lack.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheGranulesAWindowCutsAreCountedSoThatItListsOnlyWhatItsRowsHold(bool compacted)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        List<Event> rows = await FillAsync(dataset, new Random(23), deletes: false);
        while (compacted && await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
        {
        }

        foreach ((DateTimeOffset from, DateTimeOffset to) in new[] { (Noon.AddMinutes(10), Noon.AddMinutes(25)), (Noon.AddSeconds(200), Noon.AddSeconds(230)) })
        {
            Dictionary<string, long> truth = Truth(rows.Where(r => r.At >= from && r.At < to), r => r.Attributes);
            VariantPaths found = await dataset.Scan<Event>().Where(r => r.At >= from & r.At < to).PathsAsync(r => r.Attributes, cancellationToken: Ct);
            Assert.True(found.Complete);
            Dictionary<string, VariantPath> listed = found.ToDictionary(Key);
            Assert.Equal(Sorted(truth).Select(path => path.Key), Sorted(listed.ToDictionary(path => path.Key, path => path.Value.Rows)).Select(path => path.Key));
            Assert.All(found.Where(path => path.Exact), path => Assert.Equal(truth[Key(path)], path.Rows));
            Assert.True(found.Reading.EdgeGranules > 0 && found.Reading.EdgeRows >= found.Reading.EdgeGranules, $"{found.Reading}");
        }
    }

    /// <summary>
    /// Every object compacted, so that each has a directory, and no filter: the answer is the directories', exact, with no
    /// object opened; under a filter on a column, the objects are opened as a count opens them.
    /// </summary>
    [Fact]
    public async Task ObjectsKeptWholeAnswerFromTheirDirectoriesUnopened()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        List<Event> rows = await FillAsync(dataset, new Random(7), deletes: false);
        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
        {
        }

        VariantPaths found = await dataset.Scan<Event>().PathsAsync(r => r.Attributes, cancellationToken: Ct);
        Assert.True(found.Exact && found.Complete);
        Assert.Equal(Sorted(Truth(rows, r => r.Attributes)), Sorted(found.ToDictionary(Key, path => path.Rows)));
        Assert.Equal(new PathsReading(found.Reading.Directories, found.Reading.Directories, 0, 0, 0, 0), found.Reading);
        Assert.True(found.Reading.Directories > 1, $"{found.Reading}");

        // What the walk opened: nothing, every object's directory read from its table of contents.
        DatasetScanCounters metrics = new DatasetScanCounters();
        PathTally tally = new PathTally();
        await dataset.ScanBuilder().WithMetrics(metrics)
            .TallyPathsAsync(new PathsQuestion([new PathColumn(new ColumnFieldExpr(nameof(Event.Attributes)), tally)], Exact: false, Memory: null), Ct);
        Assert.Equal(0, metrics.ObjectsOpened);
        Assert.True(metrics.ObjectsCounted > 1, $"{metrics.ObjectsCounted} objects counted");
        Assert.Equal(Sorted(Truth(rows, r => r.Attributes)), Sorted(tally.ToPaths(nameof(Event.Attributes)).ToDictionary(Key, path => path.Rows)));
    }

    /// <summary>
    /// Objects appended and never compacted carry the directory their writer counted: the paths of every row are theirs,
    /// exact, with no object opened, and a question on a rare key refutes the granules of an append that lack it. Told not
    /// to, an append writes none, and its rows are counted instead.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAppendWritesTheDirectoryOfItsRows(bool directories)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, directories);
        List<Event> rows = [];
        Random random = new Random(19);
        for (int part = 0; part < 6; part++)
        {
            Event[] batch = [.. Enumerable.Range(rows.Count, 400).Select(i => new Event(Noon.AddSeconds(i * 2.5), random.Next(6), Attributes(random, i), Extra(random, i)))];
            rows.AddRange(batch);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Event>(batch, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        DatasetScanCounters metrics = new DatasetScanCounters();
        PathTally tally = new PathTally();
        await dataset.ScanBuilder().WithMetrics(metrics)
            .TallyPathsAsync(new PathsQuestion([new PathColumn(new ColumnFieldExpr(nameof(Event.Attributes)), tally)], Exact: false, Memory: null), Ct);
        VariantPaths found = tally.ToPaths(nameof(Event.Attributes));
        Assert.True(found.Exact && found.Complete);
        Assert.Equal(Sorted(Truth(rows, r => r.Attributes)), Sorted(found.ToDictionary(Key, path => path.Rows)));
        Assert.Equal(directories ? 0 : (await dataset.ListObjectsAsync(Ct).ToListAsync(Ct)).Count, metrics.ObjectsOpened);

        // The rare key: a granule or two of the objects that hold it, none of the others, when the appends wrote directories.
        Assert.Equal(rows.Count(r => r.Attributes.Has("deploy")), await dataset.Scan<Event>().Where(r => r.Attributes.Has("deploy")).CountAsync(Ct));
        ScanPlan plan = await dataset.Scan<Event>().Where(r => r.Attributes.Has("deploy")).ExplainAsync(analyze: true, Ct);
        Assert.Equal(directories, plan.Pruning.Any(step => step.Structure == "path directory" && step.BlocksPruned > 0));
        if (directories)
        {
            Assert.InRange(plan.LiveBlocks, 1, 6);
        }
    }

    /// <summary>Two variant columns in one pass, one of them nullable, as each alone gives them.</summary>
    [Fact]
    public async Task SeveralColumnsAreAnsweredInOnePass()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        List<Event> rows = await FillAsync(dataset, new Random(11));

        IReadOnlyList<VariantPaths> both = await dataset.Scan<Event>().Where(r => r.Source == 1)
            .PathsAsync(r => (r.Attributes, r.Extra), new PathsOptions { Exact = true }, Ct);
        Assert.Equal(["Attributes", "Extra"], both.Select(paths => paths.Column));
        Event[] kept = [.. rows.Where(r => r.Source == 1)];
        Assert.Equal(Sorted(Truth(kept, r => r.Attributes)), Sorted(both[0].ToDictionary(Key, path => path.Rows)));
        Assert.Equal(Sorted(Truth(kept, r => r.Extra)), Sorted(both[1].ToDictionary(Key, path => path.Rows)));
        VariantPaths extra = await dataset.Scan<Event>().Where(r => r.Source == 1).PathsAsync(r => r.Extra, new PathsOptions { Exact = true }, Ct);
        Assert.Equal(Sorted(Truth(kept, r => r.Extra)), Sorted(extra.ToDictionary(Key, path => path.Rows)));

        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () => await dataset.Scan<Event>().PathsAsync(r => (r.Attributes, r.Source), cancellationToken: Ct));
        Assert.Contains("variant", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cold, a question for the paths of a column asks the store what a count of the same rows asks, and at most a request
    /// more an object, its directory; warm, nothing.
    /// </summary>
    [Fact]
    public async Task AQuestionAsksTheStoreWhatACountAsksAndADirectoryAnObject()
    {
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        await using (VortexDataset writing = await CreateAsync(store))
        {
            await FillAsync(writing, new Random(13), deletes: false);
            while (await writing.CompactAsync(new CompactionOptions { LevelZeroCeiling = 0 }, Ct) is not null)
            {
            }
        }

        foreach ((string name, Func<Probe<Event>, Predicate>? filter, _) in Questions)
        {
            long counted = await ColdAsync(store, async dataset => await Scan(dataset, filter).CountAsync(Ct));
            (long asked, long objects, long warm) = await ColdPathsAsync(store, filter);
            Assert.True(asked <= counted + objects, $"{name}: {asked} requests for the paths, {counted} for a count, {objects} objects");
            Assert.Equal(0, warm);
        }
    }

    /// <summary>The requests <paramref name="question"/> makes of a handle opened cold on <paramref name="store"/>, its open aside.</summary>
    private static async Task<long> ColdAsync(CountingObjectStore store, Func<VortexDataset, Task<long>> question)
    {
        await using VortexSession session = VortexSession.Create(static _ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        store.Reset();
        await question(dataset);
        return store.Requests;
    }

    /// <summary>The requests a question for the paths makes cold, the objects of the dataset, and the requests the same question makes warm.</summary>
    private static async Task<(long Cold, long Objects, long Warm)> ColdPathsAsync(CountingObjectStore store, Func<Probe<Event>, Predicate>? filter)
    {
        await using VortexSession session = VortexSession.Create(static _ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        long objects = (await dataset.ListObjectsAsync(Ct).ToListAsync(Ct)).Count;
        store.Reset();
        await Scan(dataset, filter).PathsAsync(r => r.Attributes, cancellationToken: Ct);
        long cold = store.Requests;
        store.Reset();
        await Scan(dataset, filter).PathsAsync(r => r.Attributes, cancellationToken: Ct);
        return (cold, objects, store.Requests);
    }

    /// <summary>
    /// A directory added to a tally a second time allocates nothing: its entries are walked as they lie and found by their
    /// bytes, none made an object.
    /// </summary>
    [Fact]
    public void ADirectoryMetAgainIsTalliedWithoutAllocating()
    {
        Random random = new Random(17);
        PathCensus census = new PathCensus(0, 64);
        for (int row = 0; row < 4_000; row++)
        {
            Variant attributes = Attributes(random, row);
            census.Add(attributes.Metadata.Span, attributes.Value.Span);
        }

        byte[] region = VariantDirectory.From(census, null, 64).ToBytes();
        census.Release();
        PathTally tally = new PathTally();
        KeptRows judge = new KeptRows(new RowRange(1_000, 3_000), null, null, null, filtered: false);
        Assert.True(tally.AddDirectory(region, judge, exact: false));
        int paths = tally.Count;
        Assert.True(paths > 300, $"{paths} paths");

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(tally.AddDirectory(region, judge, exact: false));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(paths, tally.Count);
        Assert.True(allocated == 0, $"{allocated} bytes allocated walking a directory of {paths} paths met before");
    }

    private static Scan<Event> Scan(VortexDataset dataset, Func<Probe<Event>, Predicate>? filter) =>
        filter is null ? dataset.Scan<Event>() : dataset.Scan<Event>().Where(filter);

    private static ValueTask<VortexDataset> CreateAsync(IObjectStore store, bool directories = true) =>
        VortexDataset.CreateAsync(store, Event.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Event.At)).Truncate(CalendarUnit.Hour),
                SortKey = [LayoutValue.Column(nameof(Event.At))],
                Organization = LayoutOrganization.Leveled,
            },
            ContentsObjectBytes = 0,
            Write = new VortexWriteOptions { BlockRows = 64 },
            MarkedObjectBytes = 0,
            MarkedShare = 1,
            PathDirectoryOnAppend = directories,
        }, Ct);

    /// <summary>
    /// Twelve appends of rows two and a half seconds apart, each followed by none, one or every compaction job, then two
    /// deletes: the rows left, as the dataset holds them.
    /// </summary>
    private static async Task<List<Event>> FillAsync(VortexDataset dataset, Random random, bool deletes = true)
    {
        List<Event> rows = [];
        int next = 0;
        for (int part = 0; part < 12; part++)
        {
            Event[] batch = new Event[random.Next(100, 300)];
            for (int i = 0; i < batch.Length; i++, next++)
            {
                batch[i] = new Event(Noon.AddSeconds(next * 2.5), random.Next(6), Attributes(random, next), Extra(random, next));
            }

            rows.AddRange(batch);
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Event>(batch, Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            int jobs = random.Next(3) switch { 0 => 0, 1 => 1, _ => int.MaxValue };
            for (int job = 0; job < jobs && await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null; job++)
            {
            }
        }

        if (deletes)
        {
            await dataset.DeleteAsync<Event>(r => r.Source == 3 & r.At < Noon.AddMinutes(20), Ct);
            await dataset.DeleteAsync<Event>(r => r.Attributes.Has("flag") & r.Source == 5, Ct);
            rows.RemoveAll(r => (r.Source == 3 && r.At < Noon.AddMinutes(20)) || (r.Attributes.Has("flag") && r.Source == 5));
        }

        return rows;
    }

    /// <summary>
    /// Attributes whose user keys are data, four hundred of them, among a route, a status now an integer and now a text, a
    /// nested cart, a list, a flag, a key rare enough to be in few granules, and rows that are no object or null.
    /// </summary>
    private static Variant Attributes(Random random, int row)
    {
        switch (random.Next(100))
        {
            case 0:
                return Variant.Null;
            case 1 or 2:
                return Variant.From("not an object");
        }

        List<(string, Variant)> fields = [("route", Variant.From($"/r/{random.Next(9)}"))];
        fields.Add(("status", random.Next(10) == 0 ? Variant.From("timeout") : Variant.From(200 + random.Next(4))));
        if (random.Next(2) == 0)
        {
            fields.Add(($"user.{random.Next(400)}.score", Variant.From(random.NextDouble())));
        }

        if (random.Next(5) == 0)
        {
            fields.Add(("cart", Variant.Object(("items", Variant.From(random.Next(9))), ("at", Variant.From(Noon.AddSeconds(row))))));
        }

        if (random.Next(10) == 0)
        {
            fields.Add(("tags", Variant.Array(Variant.From("a"), Variant.From("b"))));
        }

        if (random.Next(3) == 0)
        {
            fields.Add(("flag", Variant.From(random.Next(2) == 0)));
        }

        if (row % 997 < 3)
        {
            fields.Add(("deploy", Variant.From(row)));
        }

        return Variant.Object([.. fields]);
    }

    /// <summary>An extra variant: a null row a third of the time, else a small object of a few classes.</summary>
    private static Variant? Extra(Random random, int row) => random.Next(3) switch
    {
        0 => (Variant?)null,
        1 => Variant.Object(("n", Variant.From(row)), ("tags", Variant.Array(Variant.From(row)))),
        _ => Variant.Object(("ok", Variant.From(row % 2 == 0)), ("at", Variant.From(Noon))),
    };

    /// <summary>Each path and class the rows' variants hold, by its keys and class, with the rows that hold it.</summary>
    private static Dictionary<string, long> Truth(IEnumerable<Event> rows, Func<Event, Variant?> column)
    {
        Dictionary<string, long> truth = [];
        foreach (Event row in rows)
        {
            if (column(row) is { } value)
            {
                Walk(value, string.Empty, truth);
            }
        }

        return truth;

        static void Walk(Variant value, string path, Dictionary<string, long> truth)
        {
            string key = $"{path}#{ClassOf(value.Kind)}";
            truth[key] = truth.GetValueOrDefault(key) + 1;
            if (value.Kind == VariantKind.Object)
            {
                foreach (string name in value.GetKeys())
                {
                    Walk(value[name]!.Value, $"{path}/{name}", truth);
                }
            }
        }
    }

    private static VariantPathClass ClassOf(VariantKind kind) => kind switch
    {
        VariantKind.Null => VariantPathClass.Null,
        VariantKind.Bool => VariantPathClass.Boolean,
        VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64 => VariantPathClass.Integer,
        VariantKind.Float or VariantKind.Double => VariantPathClass.Double,
        VariantKind.String => VariantPathClass.String,
        VariantKind.Timestamp => VariantPathClass.Timestamp,
        VariantKind.Object => VariantPathClass.Object,
        VariantKind.Array => VariantPathClass.List,
        _ => VariantPathClass.Other,
    };

    private static string Key(VariantPath path) => $"{string.Concat(path.Keys.Select(key => "/" + key))}#{path.Class}";

    private static List<KeyValuePair<string, long>> Sorted(Dictionary<string, long> paths) =>
        [.. paths.OrderBy(path => path.Key, StringComparer.Ordinal)];

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, int Source, Variant Attributes, Variant? Extra);
}
