// The bytes a commit writes, held under a budget.
//
// The design bounds every cost of a read by a constant of the design or a logarithm, and states the write path's price
// in rewrites of rows. What a commit writes in metadata had no bound: a commit rewrote whole pages of 64 to 256 KiB
// whatever its change, and its header copied up to 192 KiB of pages it did not change. On a dataset that receives a
// small append every two seconds, its commit objects outweighed its data many times over. These tests hold that weight
// as a ratchet: each ceiling is what a commit writes today, and a change that writes less lowers it.
//
// THE ENTRIES ARE SYNTHETIC AND THE KEYS FIXED, so the tree is the same at every run: a dataset's own appends name their
// objects by a random uid, which ends their tree keys and moves page boundaries by some ten percent from one run to the
// next. An entry here weighs what an entry of a real small object weighs, its table of contents included: about 2.5 KB.
// Only the commit objects reach the store, so the bytes it counts are theirs.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class CommitBytesBudgetTests
{
    private const ulong Seed = 0xC0_B17E5;

    /// <summary>The bytes of a small object's table of contents, as an append writes it: 2,148 measured on a real one.</summary>
    private const int ContentsBytes = 2_148;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static CommitOptions Options() => new CommitOptions
    {
        Seed = Seed,
        Template = new CommitHeader
        {
            Version = 1,
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column("k")] },
            Chunker = new ChunkerSettings(
                ProllyBoundaryRule.DefaultMinBytes,
                ProllyBoundaryRule.DefaultTargetBytes,
                ProllyBoundaryRule.DefaultMaxBytes),
        },
    };

    private static ReadOnlyMemory<byte> Key(int i) => Encoding.UTF8.GetBytes($"k{i:D9}");

    private static ObjectSummaries Summaries(int i) => ObjectSummaries.From(
    [
        new ColumnSummary("k", FilterLiteral.From((long)i), true, FilterLiteral.From(i + 999L), true, true, 0, true),
        new ColumnSummary("amount", FilterLiteral.From(i * 1.5), true, FilterLiteral.From(i * 2.5), true, true, 3, true),
        new ColumnSummary("tenant", FilterLiteral.From($"t{i:D6}"), true, FilterLiteral.From($"t{i + 40:D6}"), true, true, 0, true),
        new ColumnSummary("region", FilterLiteral.From($"r{i % 97:D3}"), true, FilterLiteral.From($"r{i % 97:D3}"), true, true, 0, true),
    ]);

    private static ObjectEntry Entry(int i)
    {
        byte[] contents = new byte[ContentsBytes];
        new Random(i).NextBytes(contents);
        return new ObjectEntry(CommitKey.ForData($"{i:x8}"), (UInt128)(uint)i + 1, 40, 24_000, (UInt128)(uint)i, Summaries(i))
            .WithTableOfContents(contents);
    }

    /// <summary>An object of key <paramref name="i"/>, its entry the 2.5 KB of a real small object's.</summary>
    internal static DatasetOperation Add(int i) => new DatasetOperation.AddObject(Key(i), Entry(i));

    /// <summary>A store whose dataset holds <paramref name="objects"/> entries at keys 0 to objects - 1, the tree committed once.</summary>
    private static async Task<CountingObjectStore> TreeOfAsync(int objects)
    {
        CountingObjectStore store = new CountingObjectStore(new MemoryObjectStore());
        if (objects > 0)
        {
            await DatasetCommitter.CommitAsync(store, [.. Enumerable.Range(0, objects).Select(i => Add(i * 2))], Options(), Ct);
        }

        return store;
    }

    /// <summary>The bytes the store took for one commit of <paramref name="operations"/>.</summary>
    private static async Task<long> CommitAsync(CountingObjectStore store, IReadOnlyList<DatasetOperation> operations)
    {
        store.Reset();
        await DatasetCommitter.CommitAsync(store, operations, Options(), Ct);
        return store.BytesWritten;
    }

    private static void Report(string scenario, long mean, long most) =>
        Console.Out.Write(FormattableString.Invariant($"COMMIT BYTES: {scenario}: mean {mean}, largest {most}.\n"));

    /// <summary>
    /// Appends one object at a time to a dataset that starts empty and that nothing merges: its tree is one leaf until its
    /// entries pass 64 KiB, about 25 of them, then a few leaves under a root.
    /// </summary>
    [Fact]
    public async Task AppendsFromAnEmptyDatasetWriteNoMoreThanTheirCeiling()
    {
        await using CountingObjectStore store = await TreeOfAsync(0);
        List<long> written = [];
        for (int i = 0; i < 200; i++)
        {
            written.Add(await CommitAsync(store, [Add(i)]));
        }

        long mean = (long)written.Average();
        Report("200 appends from an empty dataset", mean, written.Max());
        Assert.True(mean <= 78_000, $"mean {mean}");
        Assert.True(written.Max() <= 189_000, $"largest {written.Max()}");
    }

    /// <summary>Appends among the keys of a tree of 5,000 objects: the dataset whose level 0 fell behind.</summary>
    [Fact]
    public async Task AppendsAmongTheKeysOfALargeTreeWriteNoMoreThanTheirPath()
    {
        await using CountingObjectStore store = await TreeOfAsync(5_000);
        List<long> written = [];
        for (int i = 0; i < 32; i++)
        {
            written.Add(await CommitAsync(store, [Add((i * 313 % 5_000 * 2) + 1)]));
        }

        long mean = (long)written.Average();
        Report("32 appends among 5,000 objects", mean, written.Max());
        Assert.True(mean <= 176_000, $"mean {mean}");
        Assert.True(written.Max() <= 242_000, $"largest {written.Max()}");
    }

    /// <summary>
    /// Appends past the last key of a tree of 5,000 objects: the healthy dataset with history, whose appends land in its
    /// newest partition while the oldest lie first in the tree.
    /// </summary>
    [Fact]
    public async Task AppendsPastTheKeysOfALargeTreeWriteNoMoreThanTheirPath()
    {
        await using CountingObjectStore store = await TreeOfAsync(5_000);
        List<long> written = [];
        for (int i = 0; i < 32; i++)
        {
            written.Add(await CommitAsync(store, [Add(10_000 + i)]));
        }

        long mean = (long)written.Average();
        Report("32 appends past 5,000 objects", mean, written.Max());
        Assert.True(mean <= 128_000, $"mean {mean}");
        Assert.True(written.Max() <= 166_000, $"largest {written.Max()}");
    }

    /// <summary>A job that replaces 200 neighbouring objects of a tree of 5,000 by one: a tier merge of a level 0 behind.</summary>
    [Fact]
    public async Task AJobOfTwoHundredObjectsWritesNoMoreThanTheLeavesItEmpties()
    {
        await using CountingObjectStore store = await TreeOfAsync(5_000);
        List<(int, ReadOnlyMemory<byte>)> inputs = [.. Enumerable.Range(1_000, 200).Select(i => (0, Key(i * 2)))];
        long written = await CommitAsync(store, [new DatasetOperation.ReplaceObjects(inputs, [(0, Key(2_000), Entry(2_000))])]);

        Report("a job of 200 objects in 5,000", written, written);
        Assert.True(written <= 117_000, $"written {written}");
    }

    /// <summary>A delete that marks rows in one object of a tree of 5,000: its entry gains a deletion vector.</summary>
    [Fact]
    public async Task ADeleteThatMarksAnObjectWritesNoMoreThanItsPath()
    {
        await using CountingObjectStore store = await TreeOfAsync(5_000);
        ObjectEntry entry = Entry(2_000 * 2);
        ObjectEntry marked = entry.WithDeletions(DeletionVector.Of([0, 5, 9]));
        long written = await CommitAsync(
            store,
            [new DatasetOperation.ReplaceObjects([(0, Key(2_000 * 2))], [(0, Key(2_000 * 2), marked)]) { Expected = [entry] }]);

        Report("a delete marking one object of 5,000", written, written);
        Assert.True(written <= 217_000, $"written {written}");
    }
}
