// The object dictionaries of a dataset's objects (spec N, 3; A6): a column of a few values written against one dictionary an
// object, its values once and a code a row, by the append whose rows take past what a chunk is priced at, then by each merge
// against the union of its inputs'; its values lying right before the column's first chunk.
//
// THE ORACLE IS THE ROWS AND THE FORMULA: the answers LINQ gives over the rows appended; the codes' bytes against their
// price, 128 bytes a bit of width a block of 1 024 rows; the bytes a cold question reads against the planner's price, and
// against the bytes of the one column it reads with its dictionary.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetDictionaryTests
{
    private const int RouteField = 2;
    private static readonly DateTimeOffset Noon = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An append of 100 000 calls, whose routes, 240 values of some ten bytes drawn apart from the service, take past the
    /// mebibyte a chunk of a column is priced at: the route is written against one dictionary of its values, its codes at
    /// the width they need, at the price the writer gave them; the service, sorted, keeps a dictionary a chunk, a run of
    /// its values each, and the size, of nearly as many values as rows, none. Routes drawn by service would take less than
    /// their price: their codes, in runs of a service's, pack narrower than the dictionary's width.
    /// </summary>
    [Fact]
    public async Task AnAppendWritesAColumnOfAFewValuesAgainstOneDictionaryAtItsPrice()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Ct);
        await AppendAsync(dataset, Calls(0, 100_000, routes: 40, new Random(0xD1C7), ofService: false), Ct);

        PositionedObject held = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        Vorticity.Indexes.ObjectContents contents = held.Entry.Contents!;
        Assert.Equal(4, contents.Dictionaries.Count);
        Vorticity.Indexes.ColumnDictionary route = contents.Dictionaries[RouteField]!.Value;
        Assert.Equal(240, route.Values);
        Assert.Null(contents.Dictionaries[0]);
        Assert.Null(contents.Dictionaries[3]);

        // 240 values take codes of 8 bits: 128 bytes a bit a block of 1 024 rows, within a fiftieth, which the last block,
        // packed to its rows, and the chunk's envelope make up.
        long codes = (held.Entry.Rows + 1023) / 1024 * 128 * 8;
        Console.WriteLine($"DICTIONARY append: {route.Values} values in {route.Length:N0} bytes, codes {contents.ColumnBytes[RouteField]:N0} bytes priced at {codes:N0}");
        Assert.InRange(contents.ColumnBytes[RouteField], codes - (codes / 50), codes + (codes / 50));
    }

    /// <summary>
    /// Two appends merged into an object whose synopses follow its data, past what its open reads: a cold count by route,
    /// read from the rows, reads the route's one chunk with its dictionary, which lies right before it, in one request past
    /// the object's open; no other column's bytes, the size's wide one after the route's among them, and within a twentieth
    /// of what the planner priced it at. The counts are the rows'.
    /// </summary>
    [Fact]
    public async Task AColumnsDictionaryIsReadWithItsFirstChunk()
    {
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        Random random = new Random(0xD1C8);
        Call[] rows = [.. Calls(0, 100_000, routes: 40, random), .. Calls(1, 100_000, routes: 40, random)];
        await using (VortexDataset writing = await CreateAsync(store, Ct))
        {
            await AppendAsync(writing, rows[..100_000], Ct);
            await AppendAsync(writing, rows[100_000..], Ct);
            while (await writing.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, Ct) is not null)
            {
            }
        }

        // Priced by a handle, then asked cold by another, with no cache: every byte it reads comes from the store.
        (long Requests, long Bytes) priced;
        await using (VortexSession pricing = VortexSession.Create(_ => { }))
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = pricing }, Ct))
        {
            priced = (await EstimateRun.ReadsOfAsync((AggregationQuery)CountByRoute(dataset, synopses: false).Query, Ct))!.Value;
        }

        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset cold = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, Ct);
        PositionedObject held = await cold.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        Assert.Equal(1, held.Level);
        store.Reset();
        List<RouteCount> counts = await CountByRoute(cold, synopses: false).As<RouteCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
        long read = store.BytesRead;
        Assert.Equal(
            rows.GroupBy(c => c.Route).Select(g => $"{g.Key}={g.Count()}").Order(StringComparer.Ordinal),
            counts.Select(c => $"{c.Route}={c.Count}").Order(StringComparer.Ordinal));

        // The open reads the object's last 64 KiB; the route, its codes and its values, the rest.
        long column = held.Entry.Contents!.ColumnBytes[RouteField] + held.Entry.Contents.Dictionaries[RouteField]!.Value.Length;
        long open = Math.Min(held.Entry.Bytes, 65_536 + 64);
        long reads = store.CountOf(ObjectOperation.Read);
        Console.WriteLine($"DICTIONARY read: {read:N0} bytes in {reads} reads, priced {priced}, open and column {open + column:N0}");
        Assert.Equal(2, reads);
        Assert.True(read <= open + column + 4_096, $"{read} bytes read past the route's {column} and an open's {open}: another column was read");
        Assert.InRange(read, priced.Bytes - (priced.Bytes / 20), priced.Bytes + (priced.Bytes / 20));
    }

    /// <summary>
    /// Two appends drawing on overlapping routes, 180 of the 240 each, merged into one object: the merge writes the route
    /// against the union of their dictionaries, translating each input's codes, and the counts by route, and those of one
    /// route, are the rows'.
    /// </summary>
    [Fact]
    public async Task AMergeWritesAColumnAgainstTheUnionOfItsInputsDictionaries()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, Ct);
        Random random = new Random(0xD1C9);
        Call[] first = Calls(0, 100_000, routes: 30, random);
        Call[] second = [.. Calls(1, 100_000, routes: 30, random).Select(c => c with { Route = Shifted(c.Route, 10) })];
        await AppendAsync(dataset, first, Ct);
        await AppendAsync(dataset, second, Ct);
        await foreach (PositionedObject input in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            Assert.Equal(180, input.Entry.Contents!.Dictionaries[RouteField]!.Value.Values);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1, TargetBytesAtLevelOne = 1L << 30 }, Ct) is not null)
        {
        }

        PositionedObject merged = await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct);
        Assert.Equal(1, merged.Level);
        Assert.Equal(240, merged.Entry.Contents!.Dictionaries[RouteField]!.Value.Values);

        Call[] all = [.. first, .. second];
        List<RouteCount> counts = await CountByRoute(dataset).As<RouteCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
        Assert.Equal(
            all.GroupBy(c => c.Route).Select(g => $"{g.Key}={g.Count()}").Order(StringComparer.Ordinal),
            counts.Select(c => $"{c.Route}={c.Count}").Order(StringComparer.Ordinal));
        string one = all[12_345].Route;
        Assert.Equal(all.Count(c => c.Route == one), await dataset.Scan<Call>().Where(c => c.Route == one).CountAsync(Ct));
    }

    private static ValueTask<VortexDataset> CreateAsync(IObjectStore store, CancellationToken cancellationToken) =>
        VortexDataset.CreateAsync(store, Call.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(Call.Service)), LayoutValue.Column(nameof(Call.At))],
                IndexedPrefix = 1,
                Organization = LayoutOrganization.Leveled,
            },
            ContentsObjectBytes = 1,
        }, cancellationToken);

    private static Vorticity.Aggregation CountByRoute(VortexDataset dataset, bool synopses = true) =>
        dataset.Scan<Call>().With(new ScanOptions { UseSynopses = synopses }).GroupBy(c => c.Route).Select(g => (g.Key, g.Count()));

    private static async Task AppendAsync(VortexDataset dataset, Call[] rows, CancellationToken cancellationToken)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Call>(rows, cancellationToken);
        await dataset.AppendAsync(draft, cancellationToken);
    }

    /// <summary>
    /// Calls of six services over two hours, each service's routes drawn from <paramref name="routes"/> of its own; or, not
    /// <paramref name="ofService"/>, from those of all six whatever the service.
    /// </summary>
    private static Call[] Calls(int part, int count, int routes, Random random, bool ofService = true) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            int service = random.Next(6);
            return new Call(
                $"svc-{service}",
                Noon.AddTicks(random.NextInt64(TimeSpan.FromHours(2).Ticks)),
                $"/svc{(ofService ? service : random.Next(6))}/r{random.Next(routes):D2}",
                random.NextInt64(1L << 40) + part);
        })];

    /// <summary>A route of one service moved <paramref name="by"/> routes on.</summary>
    private static string Shifted(string route, int by)
    {
        int at = route.LastIndexOf('r');
        return $"{route[..at]}r{int.Parse(route[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture) + by:D2}";
    }

    [VortexRecord]
    public partial record struct Call(string Service, DateTimeOffset At, string Route, long Size);

    [VortexRecord]
    public partial record struct RouteCount(string Route, long Count);
}
