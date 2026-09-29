// What a commit into a dataset's tree costs, against the entries it holds and the changes it takes.
//
// A tree of `Objects` object entries, built under the prolly rule at a mean page of `PageBytes`
// (131 072 is the dataset's own) and the dataset's summary fold, takes `Changes` new entries spread
// over its key range. The pages a commit writes go to a sink that keeps none, so every iteration
// commits against the same tree; the setup prints the pages one commit reads. Run in a checkout of
// the original and in the tree.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>A commit into a tree, against its entries and the batch's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class TreeCommitBenchmarks
{
    /// <summary>Entries of the tree.</summary>
    [Params(100_000, 1_000_000)]
    public int Objects { get; set; }

    /// <summary>New entries the commit adds, spread evenly over the key range.</summary>
    [Params(1, 64)]
    public int Changes { get; set; }

    /// <summary>The mean page size the rule aims for; its floor is half of it and its cap twice.</summary>
    [Params(4_096, 131_072)]
    public int PageBytes { get; set; }

    private readonly Store _store = new Store();
    private readonly Discard _discard = new Discard();
    private ProllyBoundaryRule _rule = null!;
    private DatasetTree _tree = null!;
    private TreeChange[] _changes = null!;

    /// <summary>Builds the tree and the batch, and prints what one commit reads.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _rule = new ProllyBoundaryRule(0x5EED_0000_5EED_0000, PageBytes / 2, PageBytes, PageBytes * 2);
        List<TreeEntry> entries = new List<TreeEntry>(Objects);
        for (int i = 0; i < Objects; i++)
        {
            entries.Add(new TreeEntry(Key(i, string.Empty), Value(i), 1_000));
        }

        _tree = DatasetTree.Build(entries, _rule, ObjectSummaryFold.Instance, _store);
        _changes = new TreeChange[Changes];
        for (int c = 0; c < Changes; c++)
        {
            int at = (int)((((long)c * 2) + 1) * Objects / (2L * Changes));
            _changes[c] = TreeChange.Put(Key(at, "5"), Value(Objects + c), 1_000);
        }

        _store.Reads = 0;
        long entriesAfter = Commit();
        Console.WriteLine(
            $"// {Objects} entries, depth {_tree.Depth}, {Changes} changes: {_store.Reads} pages read per commit");
        if (entriesAfter != Objects + Changes)
        {
            throw new InvalidOperationException("The commit did not add its entries.");
        }
    }

    /// <summary>The batch committed into the tree.</summary>
    [Benchmark]
    public long Commit() =>
        _tree.CommitAsync(_changes, _rule, ObjectSummaryFold.Instance, _store, _discard, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult().Entries;

    private static byte[] Key(int i, string suffix) => Encoding.UTF8.GetBytes($"k{i:D9}{suffix}");

    private static byte[] Value(int i)
    {
        ObjectSummaries summaries = ObjectSummaries.From(
        [
            new ColumnSummary("k", FilterLiteral.From((long)i), true, FilterLiteral.From(i + 999L), true, true, 0, true),
        ]);
        return new ObjectEntry(CommitKey.ForData($"{i:x8}"), (UInt128)(uint)i + 1, 1_000, 1 << 20, (UInt128)(uint)i, summaries)
            .ToBytes();
    }

    /// <summary>The tree's pages, counting the reads.</summary>
    private sealed class Store : IPageSource, IPageSink
    {
        private readonly Dictionary<PageReference, ReadOnlyMemory<byte>> _pages = [];
        private long _offset;

        public long Reads { get; set; }

        public PageReference WritePage(ReadOnlySpan<byte> page)
        {
            PageReference reference = new PageReference(1, _offset, page.Length, System.IO.Hashing.XxHash128.HashToUInt128(page));
            _pages[reference] = page.ToArray();
            _offset += page.Length;
            return reference;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadPageAsync(PageReference reference, CancellationToken cancellationToken)
        {
            Reads++;
            return new ValueTask<ReadOnlyMemory<byte>>(_pages[reference]);
        }
    }

    /// <summary>Names the pages a commit writes, and keeps none of them.</summary>
    private sealed class Discard : IPageSink
    {
        private long _offset;

        public PageReference WritePage(ReadOnlySpan<byte> page)
        {
            PageReference reference = new PageReference(2, _offset, page.Length, System.IO.Hashing.XxHash128.HashToUInt128(page));
            _offset += page.Length;
            return reference;
        }
    }
}
