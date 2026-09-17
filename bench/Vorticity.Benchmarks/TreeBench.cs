// The bench docs/13-dataset.md §14 asks for: "Both run for the prolly rule and, as invariants and
// entry-set equality only, for the B+tree rule behind the same seam, whose fan-out and pages per
// commit the bench records beside the prolly's."
//
// WHAT IT MEASURES, and why none of it is a clock. §13.J's table compares the two rules on shape
// and on work, not on speed: mean fan-out, page bytes, pages written per commit, dependent page
// reads per lookup. Those are counts, they are deterministic given the seed, and a number that
// moves means the rule moved -- which is exactly what a decision behind a seam needs watching.
//
// THE ENTRIES ARE §4.1's: "entries of about 200 bytes", so the fan-out this prints is the fan-out
// that section predicts (~650 at a 128 KiB mean page). Whether the prediction holds is the first
// thing the bench is for.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks;

/// <summary>Shape and work of the two boundary rules of 13 §13.J.</summary>
internal static class TreeBench
{
    /// <summary>The dataset's seed, fixed so the numbers are comparable between runs.</summary>
    private const ulong Seed = 0x1D0C_5EED_1D0C_5EED;

    /// <summary>How many point commits the pages-per-commit figure averages over.</summary>
    private const int Commits = 64;

    /// <summary>Runs the bench.</summary>
    /// <param name="sizes">Object counts to build a tree of, or empty for the default three.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(int[] sizes)
    {
        int[] counts = sizes.Length > 0 ? sizes : [10_000, 100_000, 1_000_000];
        Console.Out.WriteLine(
            "objects   rule      depth  pages   fan-out  page bytes (mean/min/max)   written/commit  read/commit");

        foreach (int count in counts)
        {
            foreach ((string name, Func<IBoundaryRule> rule) in new (string, Func<IBoundaryRule>)[]
            {
                ("prolly", () => new ProllyBoundaryRule(Seed)),
                ("b+tree", () => new FillBoundaryRule(ProllyBoundaryRule.DefaultTargetBytes)),
            })
            {
                await MeasureAsync(count, name, rule).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task MeasureAsync(int count, string name, Func<IBoundaryRule> rule)
    {
        MemoryPageStore store = new MemoryPageStore();
        List<TreeEntry> entries = new List<TreeEntry>(count);
        for (int i = 0; i < count; i++)
        {
            entries.Add(Entry(i));
        }

        DatasetTree tree = DatasetTree.Build(entries, rule(), store);

        // The leaf pages' sizes, which is what the fan-out and the page bytes are about.
        List<int> leaves = await LeafBytesAsync(tree, store).ConfigureAwait(false);
        double fanout = (double)count / leaves.Count;

        Random random = new Random(1234);
        int pagesBefore = store.Count;
        store.ResetReads();
        DatasetTree committed = tree;
        for (int i = 0; i < Commits; i++)
        {
            int which = random.Next(count);
            committed = await committed.CommitAsync(
                [TreeChange.Put(Key(which), Value(which, i + 1), which)],
                rule(),
                store,
                store,
                CancellationToken.None).ConfigureAwait(false);
        }

        double written = (double)(store.Count - pagesBefore) / Commits;
        double read = (double)store.Reads / Commits;
        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{count,-9} {name,-9} {tree.Depth,-6} {leaves.Count,-7} {fanout,-8:F0} " +
            $"{leaves.Average(),-10:F0} {leaves.Min(),-7} {leaves.Max(),-11} {written,-15:F1} {read:F1}"));
    }

    /// <summary>An entry of about 200 bytes, as §4.1 assumes.</summary>
    private static TreeEntry Entry(int i) => new TreeEntry(Key(i), Value(i, 0), i + 1);

    /// <summary>A clustering key that sorts with `i`: a tenant, then a position inside it.</summary>
    private static ReadOnlyMemory<byte> Key(int i) =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"tenant-{i / 1_000:D5}/{i:D12}"));

    private static ReadOnlyMemory<byte> Value(int i, int version)
    {
        // A leaf entry of §4.2 without its schema: the object's key, its identity, a hash, a few
        // summaries. About 170 bytes, so an entry lands near the 200 the section assumes.
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"data/{i:x8}-0000-4000-8000-{version:x12}.vortex|{i * 7919L}|{i * 104729L}|" +
            $"{i:x16}{i:x16}|min={i}|max={i + 1000}|nulls=0|rows={i + 1}|bytes={(i * 37) + 4096}");
        return Encoding.UTF8.GetBytes(text);
    }

    private static async Task<List<int>> LeafBytesAsync(DatasetTree tree, IPageSource source)
    {
        List<PageReference> level = [tree.Root];
        for (int depth = tree.Depth; depth > 1; depth--)
        {
            List<PageReference> below = [];
            foreach (PageReference reference in level)
            {
                below.AddRange(TreePage
                    .ReadInternal(await source.ReadPageAsync(reference, CancellationToken.None).ConfigureAwait(false))
                    .Select(entry => entry.Child));
            }

            level = below;
        }

        return [.. level.Select(reference => reference.Length)];
    }
}
