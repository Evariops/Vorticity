using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;
using Shape = Vorticity.Tests.Dataset.DatasetRankSeekTests.Shape;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// A dataset's key cursor walks down as it walks up. The walk from the last entry is the walk from
/// the first reversed, a step against the walk's direction lands next to where it was, and the
/// seeks below a key land where one sorted file's would: over keys shared by several objects,
/// objects that overlap and levels a compaction made, integer and text keys, served by the objects'
/// sorted runs or by their sorted columns. The oracle is the walk up, which the other suites hold
/// to a sort of the rows.
/// </summary>
public sealed class DatasetWalkDownTests
{
    public static TheoryData<Shape, bool, bool> Cases => DatasetRankSeekTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheWalkDownIsTheWalkUpReversed(Shape shape, bool text, bool sortedColumn)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Built built = await Built.CreateAsync(shape, text, sortedColumn, ct);

        await using DatasetKeyCursor cursor = await built.OpenAsync(distinct: false, ct);
        List<Entry> down = [];
        for (bool ok = await cursor.SeekLastAsync(ct); ok; ok = await cursor.PrevAsync(ct))
        {
            down.Add(Entry.Of(cursor));
        }

        down.Reverse();
        Assert.Equal(built.Walked, down);
        Assert.False(cursor.IsValid);
        Assert.False(await cursor.PrevAsync(ct));
        Assert.False(await cursor.NextAsync(ct));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AStepAgainstTheWalkLandsNextToWhereItWas(Shape shape, bool text, bool sortedColumn)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Built built = await Built.CreateAsync(shape, text, sortedColumn, ct);
        List<Entry> walked = built.Walked;

        // A seeded random walk, turning often, from both ends and from the middle: every step is
        // checked against the entry next to the one before it, a turn included.
        await using DatasetKeyCursor cursor = await built.OpenAsync(distinct: false, ct);
        Random random = new Random((int)shape * 4 + (text ? 2 : 0) + (sortedColumn ? 1 : 0));
        foreach (int start in (int[])[0, walked.Count / 2, walked.Count - 1])
        {
            int at = start;
            Assert.True(start == walked.Count - 1
                ? await cursor.SeekLastAsync(ct)
                : await cursor.SeekRankAsync(start, ct));
            Assert.Equal(walked[at], Entry.Of(cursor));
            for (int step = 0; step < 600; step++)
            {
                bool up = random.Next(3) == 0 ? random.Next(2) == 0 : (step / 40) % 2 == 0;
                int expected = up ? at + 1 : at - 1;
                bool moved = up ? await cursor.NextAsync(ct) : await cursor.PrevAsync(ct);
                Assert.Equal(expected >= 0 && expected < walked.Count, moved);
                if (!moved)
                {
                    // Past an end the cursor has no position; the walk starts again from that end.
                    Assert.False(cursor.IsValid);
                    Assert.True(up ? await cursor.SeekLastAsync(ct) : await cursor.SeekFirstAsync(ct));
                    expected = up ? walked.Count - 1 : 0;
                }

                at = expected;
                Assert.Equal(walked[at], Entry.Of(cursor));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ASeekBelowAKeyLandsWhereASortedFileWould(Shape shape, bool text, bool sortedColumn)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Built built = await Built.CreateAsync(shape, text, sortedColumn, ct);
        List<Entry> walked = built.Walked;

        await using DatasetKeyCursor cursor = await built.OpenAsync(distinct: false, ct);
        foreach (FilterLiteral probe in built.Probes())
        {
            foreach (SeekOp op in (SeekOp[])[SeekOp.AtOrBefore, SeekOp.Before])
            {
                int expected = LastBelow(walked, probe, inclusive: op == SeekOp.AtOrBefore);
                bool found = await cursor.SeekAsync(probe, op, ct);
                Assert.Equal(expected >= 0, found);
                if (!found)
                {
                    Assert.False(cursor.IsValid);
                    continue;
                }

                Assert.Equal(walked[expected], Entry.Of(cursor));

                // The walk goes on down from there, and turns back up through the same entries.
                int at = expected;
                for (int step = 0; step < 3 && at > 0; step++)
                {
                    Assert.True(await cursor.PrevAsync(ct));
                    Assert.Equal(walked[--at], Entry.Of(cursor));
                }

                for (int step = 0; step < 5 && at + 1 < walked.Count; step++)
                {
                    Assert.True(await cursor.NextAsync(ct));
                    Assert.Equal(walked[++at], Entry.Of(cursor));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheKeyStepsDownLandOnThePreviousKeysLastEntry(Shape shape, bool text, bool sortedColumn)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Built built = await Built.CreateAsync(shape, text, sortedColumn, ct);
        List<Entry> walked = built.Walked;

        await using DatasetKeyCursor cursor = await built.OpenAsync(distinct: false, ct);
        Assert.True(await cursor.SeekLastAsync(ct));
        int at = walked.Count - 1;
        int keys = 0;
        while (true)
        {
            // Every entry of the key is counted wherever the walk stands among them.
            Assert.Equal(CountOf(walked, walked[at].Key), await cursor.KeyCountAsync(ct));
            int previous = LastBelow(walked, walked[at].Key, inclusive: false);
            bool moved = await cursor.PrevKeyAsync(ct);
            Assert.Equal(previous >= 0, moved);
            if (!moved)
            {
                break;
            }

            at = previous;
            keys++;
            Assert.Equal(walked[at], Entry.Of(cursor));

            // Every few keys, the walk turns up a key and comes back down.
            if (keys % 5 == 0)
            {
                int next = at + 1;
                Assert.True(await cursor.NextKeyAsync(ct));
                Assert.Equal(walked[next], Entry.Of(cursor));
                Assert.True(await cursor.PrevKeyAsync(ct));
                Assert.Equal(walked[at], Entry.Of(cursor));
            }
        }

        Assert.True(keys > 0);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ADistinctWalkDownVisitsEveryKeysFirstEntry(Shape shape, bool text, bool sortedColumn)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Built built = await Built.CreateAsync(shape, text, sortedColumn, ct);
        List<Entry> firsts = [];
        foreach (Entry entry in built.Walked)
        {
            if (firsts.Count == 0 || firsts[^1].Key != entry.Key)
            {
                firsts.Add(entry);
            }
        }

        await using DatasetKeyCursor cursor = await built.OpenAsync(distinct: true, ct);
        List<Entry> up = [];
        for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextAsync(ct))
        {
            up.Add(Entry.Of(cursor));
        }

        List<Entry> down = [];
        for (bool ok = await cursor.SeekLastAsync(ct); ok; ok = await cursor.PrevAsync(ct))
        {
            down.Add(Entry.Of(cursor));
        }

        down.Reverse();
        Assert.Equal(firsts, up);
        Assert.Equal(firsts, down);

        // A distinct seek below a key lands on the first entry of the key it finds.
        Entry middle = firsts[firsts.Count / 2];
        Assert.True(await cursor.SeekAsync(middle.Key, SeekOp.AtOrBefore, ct));
        Assert.Equal(middle, Entry.Of(cursor));
        Assert.True(await cursor.SeekAsync(middle.Key, SeekOp.Before, ct));
        Assert.Equal(firsts[(firsts.Count / 2) - 1], Entry.Of(cursor));
    }

    [Fact]
    public async Task AWalkDownOnTheClusteringKeyOpensAnObjectOnlyOnceItCouldHoldTheNextKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using Built built = await Built.CreateAsync(Shape.Compacted, text: false, sortedColumn: false, ct);
        VortexDataset dataset = built.Dataset;
        int above = 0;
        long objects = 0;
        for (int level = 0; level < dataset.Levels.Count; level++)
        {
            objects += dataset.Levels[level].Entries;
            above += level > 0 && dataset.Levels[level].Entries > 0 ? 1 : 0;
        }

        long levelZero = dataset.Levels[0].Entries;
        Assert.True(objects > levelZero + above, "the levels above 0 must hold several objects for the bound to mean anything");

        // Level 0 has nothing to bound its objects from above, so a walk down opens all of them
        // at its seek; a level above it opens one object, and the next below only once the walk
        // could reach it.
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, ct);
        Assert.True(await cursor.SeekLastAsync(ct));
        Assert.True(cursor.Cursors <= levelZero + above, $"{cursor.Cursors} cursors after the last key");

        Assert.True(await cursor.SeekAsync(built.Walked[0].Key, SeekOp.AtOrBefore, ct));
        Assert.Equal(built.Walked[0].Key, cursor.Key);

        // The whole walk down reads every object, once.
        await using DatasetKeyCursor whole = await DatasetKeyCursor.OpenAsync(dataset, ct);
        long entries = 0;
        for (bool ok = await whole.SeekLastAsync(ct); ok; ok = await whole.PrevAsync(ct))
        {
            entries++;
        }

        Assert.Equal(built.Walked.Count, entries);
        Assert.Equal(objects, whole.Cursors);
    }

    /// <summary>The last entry whose key is below <paramref name="key"/>, or at it when <paramref name="inclusive"/>; -1 when none is.</summary>
    private static int LastBelow(List<Entry> walked, FilterLiteral key, bool inclusive)
    {
        int found = -1;
        for (int i = 0; i < walked.Count; i++)
        {
            int order = KeyCursor.Compare(walked[i].Key, key);
            if (order < 0 || (order == 0 && inclusive))
            {
                found = i;
            }
        }

        return found;
    }

    private static int CountOf(List<Entry> walked, FilterLiteral key) =>
        walked.FindAll(entry => entry.Key == key).Count;

    /// <summary>An entry of the walk: its key, its row among the dataset's and the object it lives in.</summary>
    private readonly record struct Entry(FilterLiteral Key, long Row, string Object)
    {
        internal static Entry Of(DatasetKeyCursor cursor) => new Entry(cursor.Key, cursor.Row, cursor.Object.Key);
    }

    /// <summary>A dataset of one of the shapes, and its walk up from the first entry.</summary>
    private sealed class Built : IAsyncDisposable
    {
        private readonly MemoryObjectStore _store;
        private readonly bool _text;
        private readonly bool _sortedColumn;

        private Built(MemoryObjectStore store, VortexDataset dataset, bool text, bool sortedColumn, List<Entry> walked)
        {
            _store = store;
            Dataset = dataset;
            _text = text;
            _sortedColumn = sortedColumn;
            Walked = walked;
        }

        internal VortexDataset Dataset { get; }

        internal List<Entry> Walked { get; }

        internal static async Task<Built> CreateAsync(Shape shape, bool text, bool sortedColumn, CancellationToken ct)
        {
            Decoders.EnsureRegistered();
            DTypeArena types = new DTypeArena();
            DType keyType = text ? types.Utf8(Nullability.NonNullable) : types.Primitive(PType.I64, Nullability.NonNullable);
            DType schema = types.Struct(
                ["key", "measure"],
                [keyType, types.Primitive(PType.F64, Nullability.NonNullable)],
                Nullability.NonNullable);

            MemoryObjectStore store = new MemoryObjectStore();
            VortexDataset dataset = await DatasetRankSeekTests.BuildAsync(store, types, schema, shape, text, sortedColumn, ct);
            List<Entry> walked = [];
            await using (DatasetKeyCursor walker = await DatasetRankSeekTests.OpenAsync(dataset, sortedColumn, ct))
            {
                for (bool ok = await walker.SeekFirstAsync(ct); ok; ok = await walker.NextAsync(ct))
                {
                    walked.Add(Entry.Of(walker));
                }
            }

            return new Built(store, dataset, text, sortedColumn, walked);
        }

        internal ValueTask<DatasetKeyCursor> OpenAsync(bool distinct, CancellationToken ct) =>
            DatasetKeyCursor.OpenAsync(Dataset, Dataset.Snapshot, "key", distinct, indexes: !_sortedColumn, ct);

        /// <summary>Every key the walk holds, the keys just beside them and keys past both ends.</summary>
        internal IEnumerable<FilterLiteral> Probes()
        {
            HashSet<FilterLiteral> seen = [];
            foreach (Entry entry in Walked)
            {
                if (!seen.Add(entry.Key))
                {
                    continue;
                }

                yield return entry.Key;
                if (_text)
                {
                    byte[] bytes = entry.Key.BytesValue.ToArray();
                    yield return FilterLiteral.From([.. bytes, (byte)'0']);
                    yield return FilterLiteral.From(bytes.AsSpan(0, bytes.Length - 1));
                }
                else
                {
                    yield return FilterLiteral.From(entry.Key.SignedValue - 1);
                    yield return FilterLiteral.From(entry.Key.SignedValue + 1);
                }
            }

            if (_text)
            {
                yield return FilterLiteral.From(Encoding.ASCII.GetBytes("a"));
                yield return FilterLiteral.From(Encoding.ASCII.GetBytes("z"));
            }
            else
            {
                yield return FilterLiteral.From(long.MinValue);
                yield return FilterLiteral.From(long.MaxValue);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Dataset.DisposeAsync();
            await _store.DisposeAsync();
        }
    }
}
