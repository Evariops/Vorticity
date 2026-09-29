// The two oracles that are the reason the dataset's tree is a prolly tree:
//
//   incremental edits against a rebuild from scratch, byte for byte, on randomised batches of adds,
//   removes and descriptor updates; the same operations in two orders against one root hash.
//
// WHAT "BYTE FOR BYTE" CAN MEAN HERE, and it is worth being exact. A page reference carries
// PLACEMENT as well as content (version, offset, length, hash), and an incremental commit
// leaves untouched pages where they are, in older objects, while a rebuild writes every page into
// one new object. Their bytes therefore differ by construction. What must be identical is the
// CONTENT: the same entries, cut into pages at the same places, at every level -- which is exactly
// what the boundary rule promises and what `ContentHashAsync` reduces to one number.
//
// So the oracle is: same entries, same page boundaries, same content hash. That is strictly
// stronger than the entry-set equality asked of the B+tree rule, and it is the property that
// fails the moment a boundary depends on anything but the key set and the parameters.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetTreeTests
{
    /// <summary>A seed that is the dataset's, and not the test's mood.</summary>
    private const ulong Seed = 0x5EED_0000_5EED_0000;

    /// <summary>Small pages, so a few hundred entries make a tree of real depth.</summary>
    private static IBoundaryRule Rule() => new ProllyBoundaryRule(Seed, minBytes: 256, maxBytes: 1_024);

    private static ReadOnlyMemory<byte> Key(int i) => Encoding.UTF8.GetBytes($"k{i:D8}");

    private static ReadOnlyMemory<byte> Value(int i, int version = 0) =>
        Encoding.UTF8.GetBytes($"object-{i:D8}-v{version}");

    private static TreeEntry Entry(int i, int version = 0) => new TreeEntry(Key(i), Value(i, version), i + 1);

    [Fact]
    public async Task ATreeHoldsWhatWasBuiltIntoIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        MemoryPageStore store = new MemoryPageStore();
        List<TreeEntry> entries = [.. Enumerable.Range(0, 2_000).Select(i => Entry(i))];
        DatasetTree tree = DatasetTree.Build(entries, Rule(), store);

        Assert.Equal(2_000, tree.Entries);
        Assert.Equal(entries.Sum(e => e.Rows), tree.Rows);
        Assert.InRange(tree.Depth, 2, 4);

        List<TreeEntry> walked = [];
        await foreach (TreeEntry entry in tree.EnumerateAsync(store, ct))
        {
            walked.Add(entry);
        }

        Assert.Equal(entries.Count, walked.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            Assert.Equal(entries[i].Key.ToArray(), walked[i].Key.ToArray());
            Assert.Equal(entries[i].Value.ToArray(), walked[i].Value.ToArray());
            Assert.Equal(entries[i].Rows, walked[i].Rows);
        }

        TreeEntry found = Assert.NotNull(await tree.FindAsync(Key(1_234), store, ct));
        Assert.Equal(Value(1_234).ToArray(), found.Value.ToArray());
        Assert.Null(await tree.FindAsync(Encoding.UTF8.GetBytes("k99999999"), store, ct));
    }

    [Fact]
    public async Task RelocatingAPageMovesItAndItsAncestorsAndChangesNoContent()
    {
        // A repack, at the tree: a leaf copied elsewhere takes the pages above it along, since
        // their references named its old placement, and nothing else; the content hash, which
        // ignores placement, does not move.
        CancellationToken ct = TestContext.Current.CancellationToken;
        MemoryPageStore store = new MemoryPageStore();
        List<TreeEntry> entries = [.. Enumerable.Range(0, 2_000).Select(i => Entry(i))];
        DatasetTree tree = DatasetTree.Build(entries, Rule(), store);
        UInt128 content = await tree.ContentHashAsync(store, ct);

        PageReference leaf = Assert.NotNull(await FirstLeafAsync(tree, store));
        (DatasetTree moved, int written) = await tree.RelocateAsync(reference => reference == leaf, store, store, ct);
        Assert.Equal(tree.Depth, written);
        Assert.NotEqual(tree.Root, moved.Root);
        Assert.NotEqual(leaf, await FirstLeafAsync(moved, store));
        Assert.Equal(content, await moved.ContentHashAsync(store, ct));
        Assert.Equal((tree.Entries, tree.Rows, tree.Depth), (moved.Entries, moved.Rows, moved.Depth));

        // Nothing selected, nothing written, the same tree.
        (DatasetTree same, int none) = await moved.RelocateAsync(_ => false, store, store, ct);
        Assert.Equal(0, none);
        Assert.Same(moved, same);
    }

    private static async Task<PageReference?> FirstLeafAsync(DatasetTree tree, MemoryPageStore store)
    {
        PageReference reference = tree.Root;
        for (int level = tree.Depth; level > 1; level--)
        {
            reference = TreePage.ReadInternal(await store.ReadPageAsync(reference, default))[0].Child;
        }

        return reference;
    }

    [Theory]
    [InlineData(17)]
    [InlineData(4_242)]
    [InlineData(987_654)]
    public async Task IncrementalEditsEqualARebuild(int seed)
    {
        // Oracle one, on randomised batches of adds, removes and value updates.
        CancellationToken ct = TestContext.Current.CancellationToken;
        Random random = new Random(seed);
        MemoryPageStore store = new MemoryPageStore();
        SortedDictionary<string, TreeEntry> truth = new SortedDictionary<string, TreeEntry>(StringComparer.Ordinal);

        List<TreeEntry> initial = [];
        for (int i = 0; i < 1_500; i++)
        {
            TreeEntry entry = Entry(i * 3);
            initial.Add(entry);
            truth[Encoding.UTF8.GetString(entry.Key.Span)] = entry;
        }

        DatasetTree tree = DatasetTree.Build(initial, Rule(), store);

        for (int round = 0; round < 6; round++)
        {
            SortedDictionary<string, TreeChange> batch = new SortedDictionary<string, TreeChange>(StringComparer.Ordinal);
            for (int i = 0; i < 40; i++)
            {
                int which = random.Next(4_500);
                ReadOnlyMemory<byte> key = Key(which);
                string text = Encoding.UTF8.GetString(key.Span);
                batch[text] = random.Next(3) switch
                {
                    0 => TreeChange.Remove(key),
                    _ => TreeChange.Put(key, Value(which, round + 1), which + round),
                };
            }

            List<TreeChange> changes = [.. batch.Values];
            tree = await tree.CommitAsync(changes, Rule(), store, store, ct);

            foreach (TreeChange change in changes)
            {
                string text = Encoding.UTF8.GetString(change.Key.Span);
                if (change.IsRemoval)
                {
                    truth.Remove(text);
                }
                else
                {
                    truth[text] = new TreeEntry(change.Key, change.Value!.Value, change.Rows);
                }
            }

            // The rebuild: the same key set, built from scratch into its own store.
            MemoryPageStore rebuilt = new MemoryPageStore(2);
            DatasetTree reference = DatasetTree.Build([.. truth.Values], Rule(), rebuilt);

            Assert.Equal(reference.Entries, tree.Entries);
            Assert.Equal(reference.Rows, tree.Rows);
            Assert.Equal(reference.Depth, tree.Depth);
            Assert.Equal(
                await reference.ContentHashAsync(rebuilt, ct),
                await tree.ContentHashAsync(store, ct));
            Assert.Equal(await EntriesAsync(reference, rebuilt), await EntriesAsync(tree, store));
            Assert.Equal(await ShapeAsync(reference, rebuilt), await ShapeAsync(tree, store));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ACommitThatReadsOnlyThePathsOfItsChangesEqualsARebuild(int seed)
    {
        // Oracle one on the batches a commit that opens only the pages it must finds hardest: deep
        // trees, runs of neighbouring keys whose cuts run on past the pages they touch, keys past
        // either end, and removals that keep a prefix or a suffix cut exactly where a subtree
        // begins, so that what is left can be old subtrees alone and the new root an old page.
        CancellationToken ct = TestContext.Current.CancellationToken;
        for (int round = 0; round < 60; round++)
        {
            Random random = new Random((seed * 1_000) + round);
            int min = random.Next(100, 600);
            IBoundaryRule rule = new ProllyBoundaryRule(Seed, min, min + random.Next(100, 2_000));
            ISummaryFold fold = random.Next(2) == 0 ? NoSummary.Instance : new KeyFold();
            MemoryPageStore store = new MemoryPageStore();
            int space = random.Next(10, 4_000);
            SortedDictionary<int, TreeEntry> truth = [];
            foreach (int k in Enumerable.Range(0, random.Next(0, space)).Select(_ => random.Next(space)))
            {
                truth[k] = Varied(k, random);
            }

            DatasetTree tree = DatasetTree.Build([.. truth.Values], rule, fold, store);
            for (int commit = 0; commit < 5; commit++)
            {
                SortedDictionary<int, TreeChange> batch = await BatchAsync(tree, store, truth, space, random, ct);
                tree = await tree.CommitAsync([.. batch.Values], rule, fold, store, store, ct);
                foreach ((int k, TreeChange change) in batch)
                {
                    if (change.IsRemoval)
                    {
                        truth.Remove(k);
                    }
                    else
                    {
                        truth[k] = new TreeEntry(change.Key, change.Value!.Value, change.Rows);
                    }
                }

                MemoryPageStore rebuilt = new MemoryPageStore(2);
                DatasetTree reference = DatasetTree.Build([.. truth.Values], rule, fold, rebuilt);
                Assert.Equal((reference.Entries, reference.Rows, reference.Depth), (tree.Entries, tree.Rows, tree.Depth));
                Assert.Equal(await reference.ContentHashAsync(rebuilt, ct), await tree.ContentHashAsync(store, ct));
                Assert.Equal(await EntriesAsync(reference, rebuilt), await EntriesAsync(tree, store));
                Assert.Equal(await ShapeAsync(reference, rebuilt), await ShapeAsync(tree, store));
            }
        }
    }

    private static async Task<SortedDictionary<int, TreeChange>> BatchAsync(
        DatasetTree tree, MemoryPageStore store, SortedDictionary<int, TreeEntry> truth, int space, Random random, CancellationToken ct)
    {
        SortedDictionary<int, TreeChange> batch = [];
        if (random.Next(4) == 0 && truth.Count > 0)
        {
            int pivot = truth.Keys.ElementAt(random.Next(truth.Count));
            if (tree.Depth >= 2 && random.Next(2) == 0)
            {
                // Where a child of the root begins, or a child of one of them.
                InternalEntry chosen = Pick(TreePage.ReadInternal(await store.ReadPageAsync(tree.Root, ct)), random);
                if (tree.Depth >= 3 && random.Next(2) == 0)
                {
                    chosen = Pick(TreePage.ReadInternal(await store.ReadPageAsync(chosen.Child, ct)), random);
                }

                pivot = int.Parse(Encoding.UTF8.GetString(chosen.MinKey.Span)[1..], System.Globalization.CultureInfo.InvariantCulture);
            }

            bool prefix = random.Next(2) == 0;
            foreach (int k in truth.Keys.Where(k => prefix ? k >= pivot : k < pivot))
            {
                batch[k] = TreeChange.Remove(Key(k));
            }

            return batch;
        }

        int pattern = random.Next(6);
        int from = random.Next(space);
        int count = random.Next(1, pattern == 0 ? 3 : 150);
        for (int i = 0; i < count; i++)
        {
            int k = pattern switch
            {
                0 or 1 => random.Next(space),
                2 => from + i,
                3 => space + random.Next(500),
                4 => random.Next(3),
                _ => truth.Count > 0 ? truth.Keys.ElementAt(random.Next(truth.Count)) : random.Next(space),
            };
            TreeEntry put = Varied(k, random);
            batch[k] = pattern != 3 && random.Next(3) == 0 ? TreeChange.Remove(Key(k)) : TreeChange.Put(put.Key, put.Value, put.Rows);
        }

        if (random.Next(25) == 0)
        {
            foreach (int k in truth.Keys)
            {
                batch[k] = TreeChange.Remove(Key(k));
            }
        }

        return batch;
    }

    private static InternalEntry Pick(IReadOnlyList<InternalEntry> page, Random random) => page[random.Next(page.Count)];

    private static TreeEntry Varied(int i, Random random) =>
        new TreeEntry(Key(i), Encoding.UTF8.GetBytes(new string('v', random.Next(0, 80))), random.Next(1, 1_000));

    /// <summary>A summary that differs from page to page and in length, so that it moves internal cuts.</summary>
    private sealed class KeyFold : ISummaryFold
    {
        public ReadOnlyMemory<byte> OfLeaf(TreeEntry entry) => entry.Value.Length % 2 == 0 ? entry.Key : default;

        public ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts)
        {
            byte[] joined = [.. parts[0].Span, .. parts[^1].Span];
            return joined.AsMemory(0, Math.Min(joined.Length, 24));
        }
    }

    [Fact]
    public async Task TheSameOperationsInTwoOrdersGiveOneContentHash()
    {
        // Oracle two: history independence. The same key set reached by two different
        // sequences of commits must be one tree.
        CancellationToken ct = TestContext.Current.CancellationToken;
        MemoryPageStore first = new MemoryPageStore();
        MemoryPageStore second = new MemoryPageStore(2);

        List<TreeEntry> all = [.. Enumerable.Range(0, 1_200).Select(i => Entry(i))];
        DatasetTree left = DatasetTree.Empty;
        foreach (int[] slice in new[] { all.Take(400), all.Skip(400).Take(400), all.Skip(800) }
            .Select(part => part.Select(entry => int.Parse(
                Encoding.UTF8.GetString(entry.Key.Span)[1..], System.Globalization.CultureInfo.InvariantCulture)).ToArray()))
        {
            left = await left.CommitAsync(
                [.. slice.Select(i => TreeChange.Put(Key(i), Value(i), i + 1))], Rule(), first, first, ct);
        }

        // The other order: the last third first, then the first third, then the middle.
        DatasetTree right = DatasetTree.Empty;
        foreach (IEnumerable<int> slice in new[]
        {
            Enumerable.Range(800, 400),
            Enumerable.Range(0, 400),
            Enumerable.Range(400, 400),
        })
        {
            right = await right.CommitAsync(
                [.. slice.Select(i => TreeChange.Put(Key(i), Value(i), i + 1))], Rule(), second, second, ct);
        }

        Assert.Equal(left.Entries, right.Entries);
        Assert.Equal(left.Depth, right.Depth);
        Assert.Equal(
            await left.ContentHashAsync(first, ct),
            await right.ContentHashAsync(second, ct));
        Assert.Equal(await ShapeAsync(left, first), await ShapeAsync(right, second));
    }

    [Fact]
    public async Task AValueUpdateMovesNoBoundary()
    {
        // Values never move a boundary, so an indexer that updates an object's descriptor rewrites
        // exactly `depth` pages.
        CancellationToken ct = TestContext.Current.CancellationToken;
        MemoryPageStore store = new MemoryPageStore();
        List<TreeEntry> entries = [.. Enumerable.Range(0, 1_000).Select(i => Entry(i))];
        DatasetTree tree = DatasetTree.Build(entries, Rule(), store);
        List<int> before = await ShapeAsync(tree, store);

        DatasetTree after = await tree.CommitAsync(
            [TreeChange.Put(Key(500), Encoding.UTF8.GetBytes("a much longer descriptor than before"), 7)],
            Rule(),
            store,
            store,
            ct);

        // The page holding key 500 grew, so its own size changed; the number of pages and the
        // boundaries around it did not.
        Assert.Equal(before.Count, (await ShapeAsync(after, store)).Count);
        Assert.Equal(tree.Entries, after.Entries);
        TreeEntry updated = Assert.NotNull(await after.FindAsync(Key(500), store, ct));
        Assert.Equal(7, updated.Rows);
    }

    [Fact]
    public async Task WhatACommitWritesAndReads()
    {
        // What a one-entry commit costs: its writes and its reads stay within the depth whatever the
        // object count, since it opens only the pages on the path of its change.
        foreach (int size in new[] { 500, 2_000, 8_000, 32_000 })
        {
            MemoryPageStore store = new MemoryPageStore();
            DatasetTree tree = DatasetTree.Build([.. Enumerable.Range(0, size).Select(i => Entry(i))], Rule(), store);
            int pagesBefore = store.Count;
            store.ResetReads();

            tree = await tree.CommitAsync(
                [TreeChange.Put(Key(size / 2), Value(size / 2, 9), 3)], Rule(), store, store, TestContext.Current.CancellationToken);

            long written = store.Count - pagesBefore;
            Assert.InRange(written, 1, tree.Depth + 2);
            Assert.InRange(store.Reads, 1, tree.Depth);
        }
    }

    [Fact]
    public async Task RemovingEverythingEmptiesTheTree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        MemoryPageStore store = new MemoryPageStore();
        List<TreeEntry> entries = [.. Enumerable.Range(0, 300).Select(i => Entry(i))];
        DatasetTree tree = DatasetTree.Build(entries, Rule(), store);

        DatasetTree empty = await tree.CommitAsync(
            [.. entries.Select(e => TreeChange.Remove(e.Key))], Rule(), store, store, ct);

        Assert.True(empty.IsEmpty);
        Assert.Equal(0, empty.Entries);
        Assert.Equal(UInt128.Zero, await empty.ContentHashAsync(store, ct));
        Assert.Null(await empty.FindAsync(Key(1), store, ct));
    }

    [Fact]
    public async Task ACommitOnAnEmptyTreeBuildsIt()
    {
        MemoryPageStore store = new MemoryPageStore();
        DatasetTree tree = await DatasetTree.Empty.CommitAsync(
            [.. Enumerable.Range(0, 50).Select(i => TreeChange.Put(Key(i), Value(i), 1))],
            Rule(),
            store,
            store,
            TestContext.Current.CancellationToken);

        Assert.Equal(50, tree.Entries);
        Assert.Equal(50, tree.Rows);

        // Fifty entries of about thirty bytes are over this rule's 1 024-byte cap, so the tree is
        // two levels: the cap forces a boundary whatever the hash says.
        Assert.Equal(2, tree.Depth);
    }

    [Fact]
    public async Task ABatchThatIsNotSortedIsRefused()
    {
        MemoryPageStore store = new MemoryPageStore();
        Assert.Throws<ArgumentException>(
            () => DatasetTree.Build([Entry(2), Entry(1)], Rule(), store));

        DatasetTree tree = DatasetTree.Build([Entry(1), Entry(2)], Rule(), store);
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await tree.CommitAsync(
                [TreeChange.Put(Key(5), Value(5), 1), TreeChange.Put(Key(4), Value(4), 1)],
                Rule(),
                store,
                store,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheFillRuleBuildsTheSameEntriesAndAnotherShape()
    {
        // The other boundary rule, behind the same seam: the entries are the tree's content and
        // must not change; the shape is the rule's and does.
        MemoryPageStore prolly = new MemoryPageStore();
        MemoryPageStore fill = new MemoryPageStore(2);
        List<TreeEntry> entries = [.. Enumerable.Range(0, 2_000).Select(i => Entry(i))];

        DatasetTree left = DatasetTree.Build(entries, Rule(), prolly);
        DatasetTree right = DatasetTree.Build(entries, new FillBoundaryRule(1_024), fill);

        Assert.Equal(await EntriesAsync(left, prolly), await EntriesAsync(right, fill));
        Assert.NotEqual(await ShapeAsync(left, prolly), await ShapeAsync(right, fill));

        // And the fill rule's pages are all but the last one exactly full, which is what "a fill
        // factor" means and what the prolly rule deliberately does not do.
        List<int> sizes = await PageBytesAsync(right, fill);
        Assert.All(sizes.Take(sizes.Count - 1), size => Assert.InRange(size, 1_024, 1_024 + 64));
    }

    [Fact]
    public async Task TheProllyRuleCutsBetweenItsFloorAndItsCap()
    {
        // No boundary before the floor, a forced one at the cap, a mean near the target.
        MemoryPageStore store = new MemoryPageStore();
        DatasetTree tree = DatasetTree.Build(
            [.. Enumerable.Range(0, 20_000).Select(i => Entry(i))],
            new ProllyBoundaryRule(Seed, minBytes: 4_096, maxBytes: 16_384),
            store);

        List<int> sizes = await PageBytesAsync(tree, store);
        Assert.All(sizes.Take(sizes.Count - 1), size => Assert.InRange(size, 4_096, 16_384 + 64));
        double mean = sizes.Take(sizes.Count - 1).Average();
        Assert.InRange(mean, 4_096, 16_384);
    }

    private static async Task<List<string>> EntriesAsync(DatasetTree tree, IPageSource source)
    {
        List<string> entries = [];
        await foreach (TreeEntry entry in tree.EnumerateAsync(source, default))
        {
            entries.Add(
                $"{Encoding.UTF8.GetString(entry.Key.Span)}={Encoding.UTF8.GetString(entry.Value.Span)}#{entry.Rows}");
        }

        return entries;
    }

    /// <summary>The tree's shape: how many entries each page holds, level by level.</summary>
    private static async Task<List<int>> ShapeAsync(DatasetTree tree, IPageSource source)
    {
        List<int> shape = [];
        if (tree.IsEmpty)
        {
            return shape;
        }

        List<PageReference> level = [tree.Root];
        for (int depth = tree.Depth; depth > 0; depth--)
        {
            List<PageReference> below = [];
            foreach (PageReference reference in level)
            {
                ReadOnlyMemory<byte> bytes = await source.ReadPageAsync(reference, default);
                if (depth == 1)
                {
                    shape.Add(TreePage.ReadLeaf(bytes).Count);
                    continue;
                }

                IReadOnlyList<InternalEntry> page = TreePage.ReadInternal(bytes);
                shape.Add(page.Count);
                below.AddRange(page.Select(entry => entry.Child));
            }

            level = below;
        }

        return shape;
    }

    /// <summary>Every leaf page's bytes, in order.</summary>
    private static async Task<List<int>> PageBytesAsync(DatasetTree tree, IPageSource source)
    {
        List<int> bytes = [];
        List<PageReference> level = [tree.Root];
        for (int depth = tree.Depth; depth > 1; depth--)
        {
            List<PageReference> below = [];
            foreach (PageReference reference in level)
            {
                below.AddRange(TreePage
                    .ReadInternal(await source.ReadPageAsync(reference, default))
                    .Select(entry => entry.Child));
            }

            level = below;
        }

        foreach (PageReference reference in level)
        {
            bytes.Add(reference.Length);
        }

        return bytes;
    }
}
