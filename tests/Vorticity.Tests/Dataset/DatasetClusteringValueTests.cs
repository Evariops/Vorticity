// The objects a dataset's jobs write, as the application asks them cut: where its clustering value changes, at the bytes
// an object aims at, or both. The value is any function of the key's first column the library knows to be non-decreasing,
// of any type: a day of an instant, a tenant's name, a bucket of a number.
//
// THE DATA CROSSES EVERY CUT IN EVERY OBJECT LEVEL 0 HOLDS: each append spreads its rows over every value, so that no
// level-0 object holds one value and every job out of level 0 has to cut its runs. The oracle is the rows themselves,
// read back in key order against the rows appended sorted, and each object's own summaries, which say what it holds.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Keys;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetClusteringValueTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreationRefusesAValueItCannotCut()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();

        // A value is a function of the key's first column: no key, no value; a size alone needs none.
        await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(
            store, Event.Schema, new DatasetOptions { Clustering = new ClusteringOptions { Value = ClusteringValue.Column } }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(
            store, Event.Schema, Options([nameof(Event.Name)], ClusteringValue.Truncate(CalendarUnit.Day)), Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await VortexDataset.CreateAsync(
            store, Event.Schema, Options([nameof(Event.At)], ClusteringValue.Bucket(10)), Ct));

        await using MemoryObjectStore sized = new MemoryObjectStore();
        await using VortexDataset unkeyed = await VortexDataset.CreateAsync(
            sized, Event.Schema, new DatasetOptions { Clustering = new ClusteringOptions { ObjectBytes = 1L << 20 } }, Ct);
        Assert.Null(unkeyed.Snapshot.Schema.Cut);
    }

    [Fact]
    public async Task EveryHeaderCarriesTheClusteringAndAHandleFollowsIt()
    {
        ClusteringOptions clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day), ObjectBytes = 1L << 20 };
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Event.Schema, new DatasetOptions { ClusteringKey = [nameof(Event.At)], Clustering = clustering }, Ct))
        {
            await AppendAsync(dataset, [.. Enumerable.Range(0, 10).Select(i => new Event(Midnight.AddHours(i), $"e{i}"))]);
        }

        (_, CommitObject? latest) = await DatasetCommitter.LatestAsync(store, Ct);
        Assert.Equal(clustering, latest!.Header.Clustering);
        Assert.Equal(DatasetFeatures.Clustering, latest.Header.Features & DatasetFeatures.Clustering);

        // A handle follows the dataset's, whatever its own options ask.
        await using VortexDataset reopened = await VortexDataset.OpenAsync(
            store, new DatasetOptions { Clustering = new ClusteringOptions { ObjectBytes = 5 } }, Ct);
        Assert.Equal(clustering, reopened.Options.Clustering);
        Assert.Equal(clustering.Value, reopened.Snapshot.Schema.Cut!.Value);

        // A dataset that declares none carries none.
        await using MemoryObjectStore plain = new MemoryObjectStore();
        await using VortexDataset leveled = await VortexDataset.CreateAsync(plain, Event.Schema, new DatasetOptions { ClusteringKey = [nameof(Event.At)] }, Ct);
        (_, CommitObject? first) = await DatasetCommitter.LatestAsync(plain, Ct);
        Assert.Null(first!.Header.Clustering);
        Assert.Equal(DatasetFeatures.None, first.Header.Features & DatasetFeatures.Clustering);
        Assert.Null(leveled.Snapshot.Schema.Cut);
    }

    [Fact]
    public async Task AJobCutsItsObjectsWhereTheDayChanges()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Event.Schema, Options([nameof(Event.At), nameof(Event.Name)], ClusteringValue.Truncate(CalendarUnit.Day)), Ct);

        // Each append holds rows of every day, out of order: the job out of level 0 cuts its runs.
        Random random = new Random(0xDA7);
        List<Event> appended = [];
        for (int append = 0; append < 6; append++)
        {
            Event[] rows = [.. Enumerable.Range(0, 200).Select(i => new Event(Midnight.AddSeconds(random.Next(3 * 86_400)), $"a{append}-{i}"))];
            appended.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        await DrainAsync(dataset, Compaction());

        List<Event> read = await RowsAsync<Event>(dataset);
        Assert.Equal([.. appended.OrderBy(r => r.At).ThenBy(r => r.Name, StringComparer.Ordinal)], [.. read.OrderBy(r => r.At).ThenBy(r => r.Name, StringComparer.Ordinal)]);

        HashSet<DateTime> days = [];
        foreach ((int level, ObjectEntry entry) in await ObjectsAsync(dataset))
        {
            Assert.True(level > 0, "every row has left level 0");
            (FilterLiteral min, FilterLiteral max) = Bounds(entry, nameof(Event.At));
            Assert.Equal(DayOf(min), DayOf(max));
            days.Add(DayOf(min));
        }

        Assert.Equal(3, days.Count);
    }

    [Fact]
    public async Task ATenantLeadingTheKeyKeepsItsRowsInObjectsOfItsOwn()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Tenanted.Schema, Options([nameof(Tenanted.Tenant), nameof(Tenanted.Id)], ClusteringValue.Column), Ct);

        string[] tenants = ["acme", "globex", "initech", "umbrella", "umbrella-labs"];
        List<Tenanted> appended = [];
        for (int append = 0; append < 8; append++)
        {
            Tenanted[] rows = [.. Enumerable.Range(0, 150).Select(i => new Tenanted(tenants[(i * 7 + append) % tenants.Length], (append * 1_000L) + i, i * 0.5))];
            appended.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        await DrainAsync(dataset, Compaction());

        List<Tenanted> read = await RowsAsync<Tenanted>(dataset);
        Assert.Equal([.. appended.OrderBy(r => r.Tenant, StringComparer.Ordinal).ThenBy(r => r.Id)], [.. read.OrderBy(r => r.Tenant, StringComparer.Ordinal).ThenBy(r => r.Id)]);

        // A file states no bounds of a text column, so each object's rows say which tenants it holds.
        HashSet<string> held = [];
        foreach ((int level, ObjectEntry entry) in await ObjectsAsync(dataset))
        {
            Assert.True(level > 0, "every row has left level 0");
            await using ObjectStoreSource bytes = new ObjectStoreSource(store, entry.Key);
            await using VortexFile file = await VortexFile.OpenAsync(bytes, new VortexOpenOptions(), Ct);
            HashSet<string> own = [];
            await foreach (Tenanted row in file.Scan<Tenanted>().ToRecordsAsync(Ct))
            {
                own.Add(row.Tenant);
            }

            held.Add(Assert.Single(own));
        }

        Assert.Equal(tenants.Length, held.Count);
    }

    [Fact]
    public async Task ObjectsStayWithinTheBytesAimedAtWhateverTheLevelsTarget()
    {
        const long Aimed = 8L << 10;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Numbered.Schema, Options([nameof(Numbered.Id)], value: null, objectBytes: Aimed), Ct);

        Random random = new Random(0x5172E);
        long rows = 0;
        for (int append = 0; append < 12; append++)
        {
            Numbered[] batch = [.. Enumerable.Range(0, 500).Select(_ => new Numbered(random.NextInt64(1_000_000), random.NextDouble()))];
            rows += batch.Length;
            await AppendAsync(dataset, batch);
        }

        // The levels would take objects of a mebibyte: the dataset's size binds every writer's options.
        await DrainAsync(dataset, Compaction() with { TargetBytesAtLevelOne = 1L << 20 });

        List<(int Level, ObjectEntry Entry)> objects = await ObjectsAsync(dataset);
        Assert.Equal(rows, dataset.RowCount);
        Assert.True(objects.Count > 4, $"{objects.Count} object(s) for {rows} rows at {Aimed} bytes each");
        Console.Out.Write(FormattableString.Invariant(
            $"CLUSTERING SIZE: {objects.Count} objects, aimed at {Aimed} bytes, the largest {objects.Max(held => held.Entry.Bytes)}, the median {objects.Select(held => held.Entry.Bytes).Order().ElementAt(objects.Count / 2)}.\n"));
        Assert.All(objects, held => Assert.True(held.Entry.Bytes <= 2 * Aimed, $"an object of {held.Entry.Bytes} bytes, aimed at {Aimed}"));
    }

    [Fact]
    public async Task AKeyRepeatedAcrossAppendsStillRollsAtTheBytesAimedAt()
    {
        // A key of one text column, each tenant's rows in every append: a job's inputs share each key, and the one that
        // emits a key last runs on into the next, so that the key changes inside its run rather than where a run starts.
        const long Aimed = 8L << 10;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Tenanted.Schema, Options([nameof(Tenanted.Tenant)], value: null, objectBytes: Aimed), Ct);
        string[] tenants = [.. Enumerable.Range(0, 24).Select(t => $"tenant-{t:D2}")];
        for (int append = 0; append < 8; append++)
        {
            await AppendAsync(dataset, [.. Enumerable.Range(0, 480).Select(i => new Tenanted(tenants[i % tenants.Length], (append * 1_000L) + i, i * 0.5))]);
        }

        await DrainAsync(dataset, Compaction());

        // Objects of a few tenants each above level 0, by the bounds their keys give, none of a level sharing a tenant with
        // another.
        ClusteringKey key = dataset.Snapshot.Schema.Key!;
        List<(int Level, string First, string Last, long Bytes)> objects = [];
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, Ct))
        {
            if (held.Level == 0)
            {
                continue;
            }

            Assert.True(held.Summaries(key).TryGet(nameof(Tenanted.Tenant), out ColumnSummary tenant) && tenant.HasMin && tenant.HasMax);
            objects.Add((held.Level, System.Text.Encoding.UTF8.GetString(tenant.Min.BytesValue), System.Text.Encoding.UTF8.GetString(tenant.Max.BytesValue), held.Entry.Bytes));
        }

        Console.Out.Write(FormattableString.Invariant(
            $"CLUSTERING REPEATED KEY: {objects.Count} objects, aimed at {Aimed} bytes, the largest {objects.Max(o => o.Bytes)}.\n"));
        Assert.True(objects.Count > 4, $"{objects.Count} object(s) for {tenants.Length} tenants at {Aimed} bytes each");
        foreach (IGrouping<int, (int Level, string First, string Last, long Bytes)> level in objects.GroupBy(o => o.Level))
        {
            (int, string First, string Last, long)[] ordered = [.. level.OrderBy(o => o.First, StringComparer.Ordinal)];
            for (int i = 1; i < ordered.Length; i++)
            {
                Assert.True(string.CompareOrdinal(ordered[i - 1].Last, ordered[i].First) < 0, $"level {level.Key}: {ordered[i - 1].Last} in two objects");
            }
        }
    }

    [Fact]
    public async Task KeysInOrderMergeTheNeighboursOfOneValueAndCloseEachValue()
    {
        Stream cut = await InOrderAsync(ClusteringValue.Bucket(1_000));
        Stream whole = await InOrderAsync(value: null);

        Console.Out.Write(FormattableString.Invariant(
            $"CLUSTERING NEIGHBOURS, keys in order: {cut.Appended} rows; cut by 1000 ids, {cut.Objects} objects above level 0 for {cut.Values} values, {cut.Rewritten} rows rewritten ({(double)cut.Rewritten / cut.Appended:F2}x); uncut, {whole.Objects} objects, {whole.Rewritten} rows rewritten ({(double)whole.Rewritten / whole.Appended:F2}x).\n"));

        // Each value ends in objects of its own, a few a value: its last small ones merged once the next value started.
        Assert.True(cut.Objects <= 3 * cut.Values, $"{cut.Objects} objects for {cut.Values} values");
        Assert.True(cut.Rewritten <= 2 * whole.Rewritten, $"{cut.Rewritten} rows rewritten cut, {whole.Rewritten} uncut");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public async Task LateRowsRewriteTheObjectsOfTheirValueAlone(int latePercent)
    {
        // Rows mostly in order, a share of each append late, its ids drawn among all those before: each late row merges
        // into the objects of its value it overlaps on its way up, which the cut keeps within one value. Measured against
        // the same stream uncut.
        Stream cut = await InOrderAsync(ClusteringValue.Bucket(1_000), latePercent);
        Stream whole = await InOrderAsync(value: null, latePercent);

        Console.Out.Write(FormattableString.Invariant(
            $"CLUSTERING LATE ROWS, {latePercent} % late: {cut.Appended} rows; cut by 1000 ids, {cut.Objects} objects above level 0 for {cut.Values} values, {cut.Rewritten} rows rewritten ({(double)cut.Rewritten / cut.Appended:F2}x); uncut, {whole.Objects} objects, {whole.Rewritten} rows rewritten ({(double)whole.Rewritten / whole.Appended:F2}x).\n"));
        Assert.True(cut.Rewritten <= 2 * whole.Rewritten, $"{cut.Rewritten} rows rewritten cut, {whole.Rewritten} uncut");
    }

    /// <summary>
    /// Appends of 100 ids, compaction drained after each, then every object above level 0 checked to hold one value: the
    /// rows appended, rewritten, the objects above level 0 and the values they hold. The ids follow each other, but for
    /// <paramref name="latePercent"/> of each append, drawn among the ids before it.
    /// </summary>
    private static async Task<Stream> InOrderAsync(ClusteringValue? value, int latePercent = 0)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Numbered.Schema, Options([nameof(Numbered.Id)], value), Ct);
        Random random = new Random(0x1A7E);
        List<long> ids = [];
        long next = 0;
        long rewritten = 0;
        for (int round = 0; round < 96; round++)
        {
            Numbered[] rows = new Numbered[100];
            for (int i = 0; i < rows.Length; i++)
            {
                long id = next > 0 && random.Next(100) < latePercent ? random.NextInt64(next) : next++;
                rows[i] = new Numbered(id, i);
                ids.Add(id);
            }

            await AppendAsync(dataset, rows);
            rewritten += await DrainAsync(dataset, Compaction());
        }

        long appended = ids.Count;
        Assert.Equal(appended, dataset.RowCount);
        Assert.Equal(ids.Order(), await dataset.Scan<Numbered>().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(Ct));
        HashSet<long> values = [];
        long objects = 0;
        foreach ((int level, ObjectEntry entry) in await ObjectsAsync(dataset))
        {
            if (level == 0)
            {
                continue;
            }

            objects++;
            (FilterLiteral min, FilterLiteral max) = Bounds(entry, nameof(Numbered.Id));
            if (value is not null)
            {
                Assert.Equal(min.SignedValue / 1_000, max.SignedValue / 1_000);
            }

            values.Add(min.SignedValue / 1_000);
        }

        return new Stream(appended, rewritten, objects, values.Count);
    }

    private readonly record struct Stream(long Appended, long Rewritten, long Objects, long Values);

    /// <summary>Runs every job due until none is, and gives the rows they rewrote; a stream of jobs that never ends fails.</summary>
    private static async Task<long> DrainAsync(VortexDataset dataset, CompactionOptions options)
    {
        long rows = 0;
        for (int step = 0; ; step++)
        {
            Assert.True(step < 10_000, "compaction never runs out of jobs");
            if (await dataset.CompactAsync(options, Ct) is not { } done)
            {
                return rows;
            }

            Assert.Equal(OperationOutcome.Applied, done.Outcome);
            rows += done.Rows;
        }
    }

    private static async Task AppendAsync<T>(VortexDataset dataset, T[] rows)
        where T : IVortexRecord<T>
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<T>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    private static async Task<List<T>> RowsAsync<T>(VortexDataset dataset)
        where T : IVortexRecord<T>
    {
        List<T> rows = [];
        await foreach (T row in dataset.Scan<T>().ToRecordsAsync(Ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<List<(int Level, ObjectEntry Entry)>> ObjectsAsync(VortexDataset dataset)
    {
        List<(int Level, ObjectEntry Entry)> objects = [];
        await foreach (PositionedObject held in dataset.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            objects.Add((held.Level, held.Entry));
        }

        return objects;
    }

    private static (FilterLiteral Min, FilterLiteral Max) Bounds(ObjectEntry entry, string path)
    {
        bool found = entry.Summaries.TryGet(path, out ColumnSummary column);
        Assert.True(
            found && column.HasMin && column.HasMax,
            $"an object states no bounds on '{path}': found {found}, min {column.HasMin}, max {column.HasMax}, exact {column.IsExact}, nulls {column.HasNullCount}, rows {entry.Rows}, columns {entry.Summaries.Count}");
        return (column.Min, column.Max);
    }

    // A key of one instant, which holds no null: the objects a job writes in its order carry no sorted run, which would
    // repeat the column, and a walk of the key, a seek on it and the rows read in its order go through the column itself.
    [Fact]
    public async Task AKeyOfOneInstantWalksItsSortedColumnAndKeepsNoRun()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Event.Schema, Options([nameof(Event.At)], ClusteringValue.Truncate(CalendarUnit.Day)), Ct);
        Random random = new Random(0x50C);
        List<Event> appended = [];
        for (int append = 0; append < 4; append++)
        {
            Event[] rows = [.. Enumerable.Range(0, 300).Select(i => new Event(Midnight.AddSeconds(random.Next(2 * 86_400)), $"a{append}-{i}"))];
            appended.AddRange(rows);
            await AppendAsync(dataset, rows);
        }

        await DrainAsync(dataset, Compaction());
        foreach ((int level, ObjectEntry entry) in await ObjectsAsync(dataset))
        {
            Assert.True(level > 0, "every row has left level 0");
            await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
            IndexDirectory? directory = await lease.File.ReadIndexDirectoryAsync(Ct);
            Assert.DoesNotContain(directory?.Entries ?? [], index => index.Kind == IndexKinds.SortedRuns);
            Assert.Equal(KeySourceKind.SortedColumn, (await lease.File.Keys(nameof(Event.At)).ExplainAsync(Ct)).Source);
        }

        DateTimeOffset from = Midnight.AddHours(30);
        List<Event> later = await dataset.Scan<Event>().Where(r => r.At >= from).ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(appended.Count(r => r.At >= from), later.Count);
        List<Event> ordered = await RowsAsync<Event>(dataset);
        Assert.Equal([.. appended.Select(r => r.At).Order()], ordered.Select(r => r.At));
    }

    private static DateTime DayOf(FilterLiteral micros) => DateTimeOffset.UnixEpoch.AddTicks(micros.SignedValue * 10).UtcDateTime.Date;

    private static DatasetOptions Options(string[] key, ClusteringValue? value, long objectBytes = 0) => new DatasetOptions
    {
        Seed = 0xC1A5_7E12,
        ClusteringKey = key,
        Clustering = new ClusteringOptions { Value = value, ObjectBytes = objectBytes },

        // Small blocks, so that an object's size moves as its rows are written and a roll at a target is something a job sees.
        ChunkBytes = 0,
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 512 },
    };

    private static CompactionOptions Compaction() => new CompactionOptions
    {
        LevelZeroCeiling = 2,
        TargetBytesAtLevelOne = 2 << 10,
        Fanout = 3,
        MaxObjectBytes = 1L << 30,
    };

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, string Name);

    [VortexRecord]
    public partial record struct Tenanted(string Tenant, long Id, double Amount);

    [VortexRecord]
    public partial record struct Numbered(long Id, double Amount);
}
