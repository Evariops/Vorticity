// An object read in two passes: the columns of its filter first, then the others for the rows it keeps alone, when its
// sheets say the filter keeps rows fewer than half the chunks of the widest column read.
//
// THE ORACLE IS THE ROWS: the rows a rare value selects are the rows; and the bytes the two passes read are held against
// the same filter written as a range, which no sheet estimates, read in one pass.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetTwoPhaseTests
{
    private const int Rare = 12_345;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 100 000 events in time order, each with a body of 400 random bytes in chunks of 512 rows and a code of twenty thousand
    /// values, so many that the sheets expect a few rows of any, a rare code held by thirty of them scattered over every
    /// object: a filter on the rare code reads its code column, then the chunks of the bodies that hold its rows, a quarter
    /// of the bytes the same filter takes as a range, which no sheet estimates and which reads every body.
    /// </summary>
    [Fact]
    public async Task ARareValueReadsTheChunksOfItsRowsAlone()
    {
        Random random = new Random(0xD6C);
        HashSet<int> rare = [];
        while (rare.Count < 30)
        {
            rare.Add(random.Next(100_000));
        }

        Event[] rows = [.. Enumerable.Range(0, 100_000).Select(i => new Event(
            Start.AddSeconds(i), rare.Contains(i) ? Rare : Other(random), new string((char)('a' + random.Next(26)), 8) + Convert.ToBase64String(RandomBytes(random, 294))))];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Event.At))], ObjectBytes = 32 << 20 },
            ContentsObjectBytes = 1,
            ChunkBytes = 64 << 10,
            Write = new VortexWriteOptions { BlockRows = 512, ChunkTargetBytes = 64 << 10 },
        };
        await using (VortexDataset writing = await VortexDataset.CreateAsync(store, Event.Schema, options, Ct))
        {
            for (int from = 0; from < rows.Length; from += 25_000)
            {
                await using ObjectDraft draft = writing.StartObject();
                await draft.Writer.WriteAsync<Event>(rows.AsSpan(from, 25_000).ToArray(), Ct);
                await writing.AppendAsync(draft, Ct);
            }

            while (await writing.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
            {
            }
        }

        string[] expected = [.. rows.Where(r => r.Code == Rare).Select(r => r.Body)];
        (string[] equal, long equalBytes, long equalReads) = await AskAsync(store, options, e => e.Code == Rare);
        (string[] range, long rangeBytes, long rangeReads) = await AskAsync(store, options, e => e.Code >= Rare && e.Code <= Rare);
        Console.WriteLine($"TWO PASSES: {expected.Length} rows; two passes {equalBytes:N0} bytes in {equalReads} reads, one pass {rangeBytes:N0} bytes in {rangeReads} reads");
        Assert.Equal(expected, equal);
        Assert.Equal(expected, range);
        Assert.True(equalBytes * 4 < rangeBytes, $"{equalBytes} bytes in two passes, {rangeBytes} in one");
        Assert.True(equalReads * 3 < rangeReads, $"{equalReads} reads in two passes, {rangeReads} in one");
    }

    /// <summary>
    /// The same over a variant: 100 000 events whose attributes hold a user of twenty thousand and a body of 294 random
    /// bytes, in objects of some 5 000 rows whose jobs shred both keys, and a rare user's thirty events in a row, which one
    /// object holds, asked by a prefix that no locate of the user answers. Each object's first pass judges the filter on the
    /// user's ranges alone, where it read the attributes whole: the rare user's records read 3.2 MB where they read 35.2,
    /// less than a quarter of the bytes the bodies alone take.
    /// </summary>
    [Fact]
    public async Task ARareKeyOfAVariantIsJudgedOnItsRangesAlone()
    {
        Random random = new Random(0xD6D);
        Hit[] rows = [.. Enumerable.Range(0, 100_000).Select(i => new Hit(Start.AddSeconds(i), Variant.Object(
        [
            ("user", Variant.From(i is >= 61_000 and < 61_030 ? "rare" : $"u-{random.Next(20_000)}")),
            ("body", Variant.From(Convert.ToBase64String(RandomBytes(random, 294)))),
        ])))];
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        DatasetOptions options = new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Hit.At))], ObjectBytes = 2 << 20 },
            ContentsObjectBytes = 1,
            ChunkBytes = 64 << 10,
            Write = new VortexWriteOptions { BlockRows = 512, ChunkTargetBytes = 64 << 10 },
        };
        await using (VortexDataset writing = await VortexDataset.CreateAsync(store, Hit.Schema, options, Ct))
        {
            for (int from = 0; from < rows.Length; from += 25_000)
            {
                await using ObjectDraft draft = writing.StartObject();
                await draft.Writer.WriteAsync<Hit>(rows.AsSpan(from, 25_000).ToArray(), Ct);
                await writing.AppendAsync(draft, Ct);
            }

            while (await writing.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, Ct) is not null)
            {
            }
        }

        string[] expected = [.. rows[61_000..61_030].Select(r => r.Attributes["body"]?.AsString()!)];
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options with { Session = session }, Ct);
        store.Reset();
        List<string> bodies = [];
        await foreach (Hit row in dataset.Scan<Hit>().Where(r => r.Attributes["user"].AsString().StartsWith("rare")).ToRecordsAsync(Ct))
        {
            bodies.Add(row.Attributes["body"]?.AsString()!);
        }

        long needle = store.BytesRead;
        store.Reset();
        int read = (await dataset.Scan<Hit>().Select(r => r.Attributes["body"].AsString()).ToListAsync(Ct)).Count;
        long whole = store.BytesRead;
        Console.WriteLine($"TWO PASSES OVER A KEY: {expected.Length} rows, {needle:N0} bytes; the bodies {whole:N0} bytes");
        Assert.Equal(expected, bodies);
        Assert.Equal(rows.Length, read);
        Assert.True(needle * 4 < whole, $"{needle} bytes for the rare user, {whole} for the bodies");
    }

    /// <summary>A code of twenty thousand, any but the rare one, which lies among them: no zone map leaves it out.</summary>
    private static int Other(Random random) => random.Next(20_000) is var code && code != Rare ? code : Rare + 1;

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>The bodies <paramref name="filter"/> selects, in order, with a handle of its own and no cache: the bytes and reads it took of the store.</summary>
    private static async Task<(string[] Bodies, long Bytes, long Reads)> AskAsync(CountingObjectStore store, DatasetOptions options, Func<Probe<Event>, Predicate> filter)
    {
        await using VortexSession session = VortexSession.Create(_ => { });
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options with { Session = session }, Ct);
        store.Reset();
        List<string> bodies = [];
        await foreach (Event row in dataset.Scan<Event>().Where(filter).ToRecordsAsync(Ct))
        {
            bodies.Add(row.Body);
        }

        return ([.. bodies], store.BytesRead, store.CountOf(ObjectOperation.Read));
    }

    [VortexRecord]
    public partial record struct Event(DateTimeOffset At, int Code, string Body);

    [VortexRecord]
    public partial record struct Hit(DateTimeOffset At, Variant Attributes);
}
