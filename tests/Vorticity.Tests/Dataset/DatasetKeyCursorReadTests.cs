using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// What a key cursor reads of its version's trees, and what it finds there: a seek finds the objects
/// that could hold its key by one path down each level, whatever the number of objects, and opens
/// only those; a walk across trees many pages deep lands where the rows, sorted, say it should.
/// </summary>
public sealed class DatasetKeyCursorReadTests
{
    private const ulong Seed = 0xC0_1D5E;
    private const int Objects = 20_000;

    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille", "Brest", "Metz"];

    // The objects around the middle are files, the others entries whose files were never written:
    // a walk that opened one of them would fail, so the test also proves the seek left them closed.
    private const int Middle = 12_345;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASeekOnAColdVersionReadsOnePathOfEachLevel(bool down)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(inner);
        DatasetOptions options = await CreateAsync(store, ct);

        // A handle opened afresh holds what the read opening the version brought back, and nothing
        // a walk read before.
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, options, ct);
        store.Reset();
        long read = dataset.Snapshot.Pages.Reads;
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, ct);
        long opening = store.Requests;

        long key = (10L * Middle) + 2;
        Assert.True(await cursor.SeekAsync(FilterLiteral.From(key), down ? SeekOp.AtOrBefore : SeekOp.AtOrAfter, ct));
        Assert.Equal(key, cursor.Key.SignedValue);
        Assert.Equal((5L * Middle) + 2, cursor.Row);
        Assert.Equal(1, cursor.Cursors);
        long pages = dataset.Snapshot.Pages.Reads - read;
        long seeking = store.Requests - opening;
        Assert.Equal(0, opening);
        Assert.True(pages <= dataset.Levels[1].Depth, $"a seek read {pages} pages of a tree {dataset.Levels[1].Depth} deep");

        // The walk goes on into the neighbour, which opens once the walk reaches its keys.
        List<long> written = [];
        for (int i = Middle - 1; i <= Middle + 1; i++)
        {
            for (int row = 0; row < 5; row++)
            {
                written.Add((10L * i) + row);
            }
        }

        int at = written.IndexOf(key);
        for (int step = 1; step <= 5; step++)
        {
            Assert.True(down ? await cursor.PrevAsync(ct) : await cursor.NextAsync(ct));
            long expected = written[down ? at - step : at + step];
            Assert.Equal(expected, cursor.Key.SignedValue);
            Assert.Equal((expected / 10 * 5) + (expected % 10), cursor.Row);
        }

        Assert.Equal(2, cursor.Cursors);
        Console.Out.Write(FormattableString.Invariant(
            $"KEY CURSOR, COLD ({(down ? "down" : "up")}): open {opening} requests, seek {seeking} requests ({pages} tree pages) over {Objects} objects.\n"));
    }

    [Fact]
    public async Task AWalkAcrossTreesOfManyPagesFindsTheRowsInKeyOrder()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, await ManyPagesAsync(store, ct), ct);

        // The oracle: every row with its place in a scan, sorted by key, then by that place.
        List<(long Key, long Row)> sorted = [];
        await foreach (ChangeRow row in dataset.Scan<ChangeRow>().ToRecordsAsync(ct))
        {
            sorted.Add((row.Key, sorted.Count));
        }

        sorted.Sort();
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, ct);
        Action<int> at = index =>
        {
            Assert.Equal(sorted[index].Key, cursor.Key.SignedValue);
            Assert.Equal(sorted[index].Row, cursor.Row);
        };
        await AssertWalksAsync(cursor, sorted.Count, at, ct);

        // Seeks every way from keys over the whole range and past both ends.
        for (long key = -7; key <= 4_010; key += 37)
        {
            int below = sorted.FindIndex(entry => entry.Key >= key) is int first and >= 0 ? first : sorted.Count;
            int through = sorted.FindIndex(entry => entry.Key > key) is int past and >= 0 ? past : sorted.Count;
            Assert.Equal(below, await cursor.RankAsync(FilterLiteral.From(key), ct));
            await AssertSeeksAsync(cursor, sorted.Count, FilterLiteral.From(key), below, through, at, ct);
        }

        for (int rank = 0; rank < sorted.Count; rank += 53)
        {
            Assert.True(await cursor.SelectAsync(rank, ct), $"rank {rank}");
            at(rank);
            await AssertStepsAsync(cursor, sorted.Count, rank, at, ct);
        }

        Assert.False(await cursor.SelectAsync(sorted.Count, ct));
    }

    [Fact]
    public async Task AWalkOfAnotherColumnAcrossTreesOfManyPagesFindsItsOrder()
    {
        // Off the clustering key no bound tells the objects apart: every one opens at the first
        // seek, and the walk breaks the many ties between them by the dataset's order of rows.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, await ManyPagesAsync(store, ct), ct);
        List<(string City, long Row)> sorted = [];
        await foreach (ChangeRow row in dataset.Scan<ChangeRow>().ToRecordsAsync(ct))
        {
            sorted.Add((row.City, sorted.Count));
        }

        sorted.Sort((left, right) => string.CompareOrdinal(left.City, right.City) is int order and not 0 ? order : left.Row.CompareTo(right.Row));
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, dataset.Snapshot, "City", distinct: false, indexes: true, ct);
        Action<int> at = index =>
        {
            Assert.Equal(sorted[index].City, Encoding.UTF8.GetString(cursor.KeyBytes));
            Assert.Equal(sorted[index].Row, cursor.Row);
        };
        await AssertWalksAsync(cursor, sorted.Count, at, ct);
        Assert.Equal(dataset.ObjectCount, cursor.Cursors);

        foreach (string city in (string[])["A", "Brest", "Brest!", "Lille", "Lyon", "M", "Metz", "Nice", "Paris", "Pz", "~"])
        {
            int below = sorted.FindIndex(entry => string.CompareOrdinal(entry.City, city) >= 0) is int first and >= 0 ? first : sorted.Count;
            int through = sorted.FindIndex(entry => string.CompareOrdinal(entry.City, city) > 0) is int past and >= 0 ? past : sorted.Count;
            Assert.Equal(below, await cursor.RankAsync(FilterLiteral.From(city), ct));
            await AssertSeeksAsync(cursor, sorted.Count, FilterLiteral.From(city), below, through, at, ct);
        }
    }

    /// <summary>The walk up from the first entry and down from the last pass every one of the sorted rows, in order.</summary>
    private static async Task AssertWalksAsync(DatasetKeyCursor cursor, int count, Action<int> at, CancellationToken ct)
    {
        int index = 0;
        for (bool more = await cursor.SeekFirstAsync(ct); more; more = await cursor.NextAsync(ct))
        {
            at(index++);
        }

        Assert.Equal(count, index);
        for (bool more = await cursor.SeekLastAsync(ct); more; more = await cursor.PrevAsync(ct))
        {
            at(--index);
        }

        Assert.Equal(0, index);
    }

    /// <summary>
    /// A seek every way lands where the sorted rows say: <paramref name="below"/> is the first entry
    /// at or past the key, <paramref name="through"/> the first past it.
    /// </summary>
    private static async Task AssertSeeksAsync(
        DatasetKeyCursor cursor, int count, FilterLiteral key, int below, int through, Action<int> at, CancellationToken ct)
    {
        foreach ((SeekOp op, int expected) in ((SeekOp, int)[])[(SeekOp.AtOrAfter, below), (SeekOp.After, through), (SeekOp.AtOrBefore, through - 1), (SeekOp.Before, below - 1)])
        {
            bool inside = expected >= 0 && expected < count;
            Assert.Equal(inside, await cursor.SeekAsync(key, op, ct));
            if (inside)
            {
                at(expected);
                await AssertStepsAsync(cursor, count, expected, at, ct);
            }
        }
    }

    /// <summary>Three steps on from entry <paramref name="index"/>, then six back: the walk turns over entries it already passed.</summary>
    private static async Task AssertStepsAsync(DatasetKeyCursor cursor, int count, int index, Action<int> at, CancellationToken ct)
    {
        foreach (int step in (int[])[1, 1, 1, -1, -1, -1, -1, -1, -1])
        {
            index += step;
            bool more = step > 0 ? await cursor.NextAsync(ct) : await cursor.PrevAsync(ct);
            Assert.Equal(index >= 0 && index < count, more);
            if (!more)
            {
                return;
            }

            at(index);
        }
    }

    /// <summary>
    /// A dataset of small objects in level 0 and in two levels above it at least, each tree many pages
    /// deep, with rows marked deleted in every level: keys drawn from a range not much wider than the
    /// rows, so that many repeat across objects, and a city from a few.
    /// </summary>
    private static async Task<DatasetOptions> ManyPagesAsync(IObjectStore store, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        DatasetOptions options = new DatasetOptions
        {
            Seed = Seed,
            ClusteringKey = ["Key"],
            Write = new VortexWriteOptions
            {
                RowBlockSize = 64,
                DataBlockTargetBytes = 2 << 10,
                Indexes = IndexPolicy.Auto.SortedRuns("City", true),
            },

            // Pages of a few entries, so that a level's tree is many leaves under internal pages,
            // and a delete marks its rows rather than rewriting their object.
            Rule = new ProllyBoundaryRule(Seed, minBytes: 256, targetBytes: 512, maxBytes: 1_024),
            MarkedObjectBytes = 0,
            MarkedShare = 1,
        };

        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, ChangeRow.Schema, options, ct);

        // Compacted past a small level 1 first, then into a level 1 large enough to keep its objects,
        // with level 0 over both.
        Random random = new Random(0x5EED);
        foreach ((int appends, long levelOne) in ((int, long)[])[(30, 16 << 10), (16, 1 << 20), (3, 0)])
        {
            for (int part = 0; part < appends; part++)
            {
                ChangeRow[] rows = new ChangeRow[120];
                for (int i = 0; i < rows.Length; i++)
                {
                    rows[i] = new ChangeRow(random.Next(4_000), random.NextDouble(), Cities[random.Next(Cities.Length)]);
                }

                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
                await dataset.AppendAsync(draft, ct);
            }

            CompactionOptions compaction = new CompactionOptions
            {
                LevelZeroCeiling = 1,
                TargetBytesAtLevelOne = levelOne,
                MaxObjectBytes = 2 << 10,
                Fanout = 4,
            };
            while (levelOne > 0 && await dataset.CompactAsync(compaction, ct) is not null)
            {
            }
        }

        Assert.True((await dataset.DeleteAsync<ChangeRow>(r => r.Measure > 0.9 | (r.Key >= 2_000 & r.Key < 2_100), ct)).ObjectsMarked > 0);

        // Level 0 over two levels of key-disjoint objects at least, each tree more than two pages deep.
        string shape = string.Join(", ", Enumerable.Range(0, dataset.Levels.Count).Select(level => $"{dataset.Levels[level].Entries} objects {dataset.Levels[level].Depth} pages deep"));
        int[] above = [.. Enumerable.Range(1, dataset.Levels.Count - 1).Where(level => !dataset.Levels[level].IsEmpty)];
        Assert.True(dataset.Levels[0].Entries > 0 && above.Length >= 2 && above.All(level => dataset.Levels[level].Depth > 2), shape);
        return options;
    }

    /// <summary>
    /// A clustered dataset whose level 1 holds <see cref="Objects"/> objects, object i's keys running
    /// from 10 i, and its rows five: the three around <see cref="Middle"/> files of those rows.
    /// </summary>
    private static async Task<DatasetOptions> CreateAsync(IObjectStore store, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);
        DatasetOptions options = new DatasetOptions { Seed = Seed, ClusteringKey = ["key"] };
        List<DatasetOperation> adds = new List<DatasetOperation>(Objects);
        await using (VortexDataset writer = await VortexDataset.CreateAsync(store, schema, options, ct))
        {
            for (int i = 0; i < Objects; i++)
            {
                adds.Add(Math.Abs(i - Middle) <= 1 ? await WriteAsync(writer, types, schema, i, ct) : Unwritten(writer.Snapshot.Schema.Key!, i));
            }
        }

        await DatasetCommitter.CommitAsync(store, adds, new CommitOptions { Seed = Seed }, ct);
        return options;
    }

    /// <summary>Object <paramref name="i"/>, written: its five rows put in the store, its entry for level 1.</summary>
    private static async Task<DatasetOperation> WriteAsync(VortexDataset dataset, DTypeArena types, DType schema, int i, CancellationToken ct)
    {
        ObjectDraft draft = dataset.StartObject();
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keys = arena.Allocate(5 * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(5 * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int row = 0; row < 5; row++)
        {
            keyValues[row] = (10L * i) + row;
            measureValues[row] = row;
        }

        int key = arena.AddPrimitive(types.Primitive(PType.I64, Nullability.NonNullable), 5, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(types.Primitive(PType.F64, Nullability.NonNullable), 5, Validity.NonNullable, PType.F64, measures);
        using (RecordBatch batch = new RecordBatch(arena, arena.AddStruct(schema, 5, Validity.NonNullable, [key, measure]), 0))
        {
            await draft.Writer.WriteAsync(batch, ct);
        }

        draft.Take();
        await draft.Writer.CompleteAsync(ct);
        await draft.Writer.DisposeAsync();
        WrittenObject written = Assert.NotNull(await dataset.SealAsync(draft, draft.Writer.RowCount, 0, ct));
        return new DatasetOperation.AddObject(written.Key, written.Entry) { Level = 1 };
    }

    /// <summary>Object <paramref name="i"/>'s entry alone, with bounds enough to make it about 200 bytes.</summary>
    private static DatasetOperation Unwritten(ClusteringKey clustering, int i)
    {
        long low = 10L * i;
        UInt128 uid = (UInt128)(ulong)i + 1;
        ObjectSummaries summaries = ObjectSummaries.From(
        [
            new ColumnSummary("key", FilterLiteral.From(low), true, FilterLiteral.From(low + 4), true, IsExact: true, 0, true),
            new ColumnSummary("measure", FilterLiteral.From(0.0), true, FilterLiteral.From(4.0), true, true, 0, true),
        ]);
        ObjectEntry entry = new ObjectEntry(CommitKey.ForData($"{i:x16}"), uid, 5, 1 << 20, uid, summaries);

        // The key a clustered dataset gives an object: its smallest key as the key encodes it, then its uid.
        byte[] smallest = clustering.Encode([FilterLiteral.From(low)]);
        byte[] treeKey = new byte[smallest.Length + 16];
        smallest.CopyTo(treeKey, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(treeKey.AsSpan(smallest.Length), (ulong)(uid >> 64));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(treeKey.AsSpan(smallest.Length + 8), (ulong)uid);
        return new DatasetOperation.AddObject(treeKey, entry) { Level = 1 };
    }
}
