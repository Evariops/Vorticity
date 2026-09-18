// The two oracles of docs/13-dataset.md §14, which are the reason §13.J chose a prolly tree:
//
//   "incremental edits against a rebuild from scratch, byte for byte, on randomised batches of adds,
//    removes and descriptor updates; the same operations in two orders against one root hash."
//
// WHAT "BYTE FOR BYTE" CAN MEAN HERE, and it is worth being exact. A page reference carries
// PLACEMENT as well as content (§3: version, offset, length, hash), and an incremental commit
// leaves untouched pages where they are, in older objects, while a rebuild writes every page into
// one new object. Their bytes therefore differ by construction. What must be identical is the
// CONTENT: the same entries, cut into pages at the same places, at every level -- which is exactly
// what the boundary rule promises and what `ContentHashAsync` reduces to one number.
//
// So the oracle is: same entries, same page boundaries, same content hash. That is strictly
// stronger than the entry-set equality §14 asks of the B+tree rule, and it is the property that
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
    /// <summary>A seed that is the dataset's, as §4.1 says, and not the test's mood.</summary>
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
        MemoryPageStore store = new MemoryPageStore();
        List<TreeEntry> entries = [.. Enumerable.Range(0, 2_000).Select(i => Entry(i))];
        DatasetTree tree = DatasetTree.Build(entries, Rule(), store);

        Assert.Equal(2_000, tree.Entries);
        Assert.Equal(entries.Sum(e => e.Rows), tree.Rows);
        Assert.InRange(tree.Depth, 2, 4);

        List<TreeEntry> walked = [];
        await foreach (TreeEntry entry in tree.EnumerateAsync(store, default))
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

        TreeEntry found = Assert.NotNull(await tree.FindAsync(Key(1_234), store, default));
        Assert.Equal(Value(1_234).ToArray(), found.Value.ToArray());
        Assert.Null(await tree.FindAsync(Encoding.UTF8.GetBytes("k99999999"), store, default));
    }

    [Fact]
    public async Task RelocatingAPageMovesItAndItsAncestorsAndChangesNoContent()
    {
        // §10's repack, at the tree: a leaf copied elsewhere takes the pages above it along, since
        // their references named its old placement, and nothing else; the content hash, which
        // ignores placement, does not move.
        MemoryPageStore store = new MemoryPageStore();
        List<TreeEntry> entries = [.. Enumerable.Range(0, 2_000).Select(i => Entry(i))];
        DatasetTree tree = DatasetTree.Build(entries, Rule(), store);
        UInt128 content = await tree.ContentHashAsync(store, default);

        PageReference leaf = Assert.NotNull(await FirstLeafAsync(tree, store));
        (DatasetTree moved, int written) = await tree.RelocateAsync(reference => reference == leaf, store, store, default);
        Assert.Equal(tree.Depth, written);
        Assert.NotEqual(tree.Root, moved.Root);
        Assert.NotEqual(leaf, await FirstLeafAsync(moved, store));
        Assert.Equal(content, await moved.ContentHashAsync(store, default));
        Assert.Equal((tree.Entries, tree.Rows, tree.Depth), (moved.Entries, moved.Rows, moved.Depth));

        // Nothing selected, nothing written, the same tree.
        (DatasetTree same, int none) = await moved.RelocateAsync(_ => false, store, store, default);
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
        // Oracle one of §14, on randomised batches of adds, removes and value updates.
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
            tree = await tree.CommitAsync(changes, Rule(), store, store, default);

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
                await reference.ContentHashAsync(rebuilt, default),
                await tree.ContentHashAsync(store, default));
            Assert.Equal(await EntriesAsync(reference, rebuilt), await EntriesAsync(tree, store));
            Assert.Equal(await ShapeAsync(reference, rebuilt), await ShapeAsync(tree, store));
        }
    }

    [Fact]
    public async Task TheSameOperationsInTwoOrdersGiveOneContentHash()
    {
        // Oracle two of §14: history independence. The same key set reached by two different
        // sequences of commits must be one tree.
        MemoryPageStore first = new MemoryPageStore();
        MemoryPageStore second = new MemoryPageStore(2);

        List<TreeEntry> all = [.. Enumerable.Range(0, 1_200).Select(i => Entry(i))];
        DatasetTree left = DatasetTree.Empty;
        foreach (int[] slice in new[] { all.Take(400), all.Skip(400).Take(400), all.Skip(800) }
            .Select(part => part.Select(entry => int.Parse(
                Encoding.UTF8.GetString(entry.Key.Span)[1..], System.Globalization.CultureInfo.InvariantCulture)).ToArray()))
        {
            left = await left.CommitAsync(
                [.. slice.Select(i => TreeChange.Put(Key(i), Value(i), i + 1))], Rule(), first, first, default);
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
                [.. slice.Select(i => TreeChange.Put(Key(i), Value(i), i + 1))], Rule(), second, second, default);
        }

        Assert.Equal(left.Entries, right.Entries);
        Assert.Equal(left.Depth, right.Depth);
        Assert.Equal(
            await left.ContentHashAsync(first, default),
            await right.ContentHashAsync(second, default));
        Assert.Equal(await ShapeAsync(left, first), await ShapeAsync(right, second));
    }

    [Fact]
    public async Task AValueUpdateMovesNoBoundary()
    {
        // §4.1's first bullet: "Values never move a boundary, so an indexer that updates an
        // object's descriptor rewrites exactly `depth` pages."
        MemoryPageStore store = new MemoryPageStore();
        List<TreeEntry> entries = [.. Enumerable.Range(0, 1_000).Select(i => Entry(i))];
        DatasetTree tree = DatasetTree.Build(entries, Rule(), store);
        List<int> before = await ShapeAsync(tree, store);

        DatasetTree after = await tree.CommitAsync(
            [TreeChange.Put(Key(500), Encoding.UTF8.GetBytes("a much longer descriptor than before"), 7)],
            Rule(),
            store,
            store,
            default);

        // The page holding key 500 grew, so its own size changed; the number of pages and the
        // boundaries around it did not.
        Assert.Equal(before.Count, (await ShapeAsync(after, store)).Count);
        Assert.Equal(tree.Entries, after.Entries);
        TreeEntry updated = Assert.NotNull(await after.FindAsync(Key(500), store, default));
        Assert.Equal(7, updated.Rows);
    }

    [Fact]
    public async Task WhatACommitWritesAndReads()
    {
        // The cost this implementation actually pays, measured rather than claimed (§4.3 wants the
        // writes independent of the object count; the reads of the levels above the leaves are this
        // implementation's, and the number is here so the next one has something to beat).
        foreach (int size in new[] { 500, 2_000, 8_000 })
        {
            MemoryPageStore store = new MemoryPageStore();
            DatasetTree tree = DatasetTree.Build([.. Enumerable.Range(0, size).Select(i => Entry(i))], Rule(), store);
            int pagesBefore = store.Count;
            store.ResetReads();

            tree = await tree.CommitAsync(
                [TreeChange.Put(Key(size / 2), Value(size / 2, 9), 3)], Rule(), store, store, default);

            long written = store.Count - pagesBefore;
            Assert.InRange(written, 1, tree.Depth + 2);
            Assert.InRange(store.Reads, 1, (tree.Depth * 4) + (size / 40));
        }
    }

    [Fact]
    public async Task RemovingEverythingEmptiesTheTree()
    {
        MemoryPageStore store = new MemoryPageStore();
        List<TreeEntry> entries = [.. Enumerable.Range(0, 300).Select(i => Entry(i))];
        DatasetTree tree = DatasetTree.Build(entries, Rule(), store);

        DatasetTree empty = await tree.CommitAsync(
            [.. entries.Select(e => TreeChange.Remove(e.Key))], Rule(), store, store, default);

        Assert.True(empty.IsEmpty);
        Assert.Equal(0, empty.Entries);
        Assert.Equal(UInt128.Zero, await empty.ContentHashAsync(store, default));
        Assert.Null(await empty.FindAsync(Key(1), store, default));
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
            default);

        Assert.Equal(50, tree.Entries);
        Assert.Equal(50, tree.Rows);

        // Fifty entries of about thirty bytes are over this rule's 1 024-byte cap, so the tree is
        // two levels: the cap forces a boundary whatever the hash says (§4.1).
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
                default));
    }

    [Fact]
    public async Task TheFillRuleBuildsTheSameEntriesAndAnotherShape()
    {
        // §13.J's other implementation, behind the same seam: the entries are the tree's content and
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
        // §4.1: no boundary before the floor, a forced one at the cap, a mean near the target.
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
