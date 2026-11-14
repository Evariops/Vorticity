// A writer that flushes often merges its own recent objects itself, in the commit of its flush and from the rows it
// holds. It keeps its flushes as runs, each the objects one commit added and their rows. A flush writes its rows with
// those of the runs before it, from the latest back, while each holds fewer than twice the rows gathered so far, and
// replaces their objects in one commit that counts the new entries of its source. Runs then double from one to the
// next: a writer of n flushes holds log2(n) runs at most and writes each row log2(n) / 2 + 1 times on average, where
// writing every recent row again at each flush would write it n / 2 times.
//
// THE ROWS OF A FLUSH ARE ITS ENTRIES: entry n of the source is the row of key n, so a row counted twice shows as a key
// twice. A replacement that does not apply, because a merge took one of the objects it names or a delete marked a row
// of them, leaves their rows where they are, and the writer sends the new entries alone.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class RecentObjectReplacementTests
{
    private const string Queue = "queue/flushes";
    private const int Flush = 10;

    [Fact]
    public async Task FlushesFoldedIntoRunsWriteEachRowALogarithmicNumberOfTimes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        RunWriter writer = new RunWriter(dataset);
        const int Flushes = 64;
        for (int flush = 0; flush < Flushes; flush++)
        {
            Assert.Equal(OperationOutcome.Applied, (await writer.FlushAsync(flush, ct)).Outcome);

            // The runs are the binary digits of the flushes made: one a set bit, six at most here.
            Assert.Equal(BitOperations.PopCount((uint)flush + 1), writer.Runs.Count);

            // Level 0 holds the runs' objects, one a city and a run, and no size class ever fills: no merge is due.
            List<DataObject> listed = await dataset.ListObjectsAsync(ct).ToListAsync(ct);
            Assert.Equal(writer.Runs.SelectMany(run => run.Objects).Select(o => o.Key).Order(), listed.Select(o => o.Key).Order());
            Assert.Equal(2 * writer.Runs.Count, listed.Count);
            Assert.Null((await dataset.PlanCompactionAsync(null, ct)).Job);
        }

        // 64 flushes wrote 64 × (log2(64) / 2 + 1) = 256 flushes of rows, where rewriting the period at each flush writes
        // 64 × 65 / 2 = 2,080.
        Assert.Equal(256 * Flush, writer.RowsWritten);
        Assert.Equal(0, writer.Abandoned);

        // One run holds everything, every entry counted once.
        Assert.Single(writer.Runs);
        Assert.Equal((ulong)(Flushes * Flush), dataset.SourceOffset(Queue));
        Assert.Equal(Enumerable.Range(0, Flushes * Flush).Select(i => (long)i), await KeysAsync(dataset, ct));
    }

    [Fact]
    public async Task AReplacementOfRunsAMergeTookIsAbandonedAndTheirRowsStayOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        RunWriter writer = new RunWriter(dataset);
        for (int flush = 0; flush < 3; flush++)
        {
            await writer.FlushAsync(flush, ct);
        }

        // Two runs, of two flushes and of one, each an object a city.
        Assert.Equal([2, 1], writer.Runs.Select(run => run.To - run.From));

        // Another writer's appends fill a city's size class, and a merge takes the class whole, the first writer's runs
        // with it.
        await using VortexDataset other = await VortexDataset.OpenAsync(store, Options(), ct);
        int appended = 0;
        while ((await other.PlanCompactionAsync(null, ct)).Job is null)
        {
            appended++;
            await using ObjectDraft draft = other.StartObject();
            await draft.Writer.WriteAsync<ChangeRow>(Rows(1_000 * appended, Flush), ct);
            await other.AppendAsync(draft, ct);
        }

        CompactionResult? merge = await other.CompactAsync(null, ct);
        Assert.Equal(OperationOutcome.Applied, merge?.Outcome);
        HashSet<string> left = [.. (await other.ListObjectsAsync(ct).ToListAsync(ct)).Select(o => o.Key)];
        Assert.All(writer.Runs, run => Assert.Contains(run.Objects, o => !left.Contains(o.Key)));

        // The fourth flush would take both runs. Its replacement is abandoned and counts nothing: the writer forgets the
        // runs, whose rows are in the dataset already, and sends the flush's rows alone.
        ReplaceResult fourth = await writer.FlushAsync(3, ct);
        Assert.Equal(OperationOutcome.Applied, fourth.Outcome);
        Assert.Equal(1, writer.Abandoned);
        Assert.Equal([1], writer.Runs.Select(run => run.To - run.From));
        Assert.Equal(4UL * Flush, dataset.SourceOffset(Queue));

        // The run that is left is replaced as any other.
        Assert.Equal(OperationOutcome.Applied, (await writer.FlushAsync(4, ct)).Outcome);
        Assert.Equal(1, writer.Abandoned);
        Assert.Equal([2], writer.Runs.Select(run => run.To - run.From));
        Assert.Equal(
            Enumerable.Range(0, 5 * Flush).Concat(Enumerable.Range(1, appended).SelectMany(i => Enumerable.Range(1_000 * i, Flush))).Select(k => (long)k).Order(),
            await KeysAsync(dataset, ct));
    }

    [Fact]
    public async Task AReplacementOfARunADeleteMarkedIsAbandonedAndTheRowStaysDeleted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store, ct);
        RunWriter writer = new RunWriter(dataset);
        await writer.FlushAsync(0, ct);
        Assert.Equal(1, (await dataset.DeleteAsync<ChangeRow>(r => r.Key == 4, ct)).Rows);

        // The writer's run still holds row 4: written again, it would come back.
        Assert.Equal(OperationOutcome.Applied, (await writer.FlushAsync(1, ct)).Outcome);
        Assert.Equal(1, writer.Abandoned);
        Assert.Equal(2UL * Flush, dataset.SourceOffset(Queue));
        Assert.Equal(Enumerable.Range(0, 2 * Flush).Where(k => k != 4).Select(k => (long)k), await KeysAsync(dataset, ct));
    }

    [Fact]
    public async Task TheResultsNameTheObjectsTheirCommitAddedAndNoneWhenItAddedNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset a = await CreateAsync(store, ct);
        await using VortexDataset b = await VortexDataset.OpenAsync(store, Options(), ct);

        // An append that counts its range names its objects as a walk lists them, but for their place.
        SourceCommit appended;
        await using (ObjectDraft draft = a.StartObject())
        {
            await draft.Writer.WriteAsync<ChangeRow>(Rows(0, Flush), ct);
            appended = await a.AppendAsync(draft, [new SourceRange(Queue, 0, Flush)], ct);
        }

        List<DataObject> listed = await a.ListObjectsAsync(ct).ToListAsync(ct);
        Assert.Equal(2, appended.Added.Count);
        Assert.Equal(
            listed.Select(o => (o.Key, o.Level, o.Rows, o.DeletedRows, o.Bytes)).Order(),
            appended.Added.Select(o => (o.Key, o.Level, o.Rows, o.DeletedRows, o.Bytes)).Order());
        Assert.All(appended.Added, o => Assert.Equal(-1, o.FirstRow));

        // Another writer of the same range finds it counted and adds nothing.
        SourceCommit late;
        await using (ObjectDraft draft = b.StartObject())
        {
            await draft.Writer.WriteAsync<ChangeRow>(Rows(0, Flush), ct);
            late = await b.AppendAsync(draft, [new SourceRange(Queue, 0, Flush)], ct);
        }

        Assert.Equal(OperationOutcome.AlreadyThere, late.Outcome);
        Assert.Empty(late.Added);

        // A removal adds nothing, and a replacement that removes nothing names what it added.
        ReplaceResult removed = await a.RemoveObjectsAsync(listed, ct);
        Assert.Equal(OperationOutcome.Applied, removed.Outcome);
        Assert.Empty(removed.Added);
        ReplaceResult added = await ReplaceAsync(a, [], 1, 1, ct);
        Assert.Equal(
            (await a.ListObjectsAsync(ct).ToListAsync(ct)).Select(o => o.Key).Order(),
            added.Added.Select(o => o.Key).Order());
    }

    /// <summary>The writer of the guide's page on frequent appends: its runs, oldest first, and the rule that folds them.</summary>
    private sealed class RunWriter(VortexDataset dataset)
    {
        /// <summary>The runs, oldest first: the objects a commit added and the flushes whose rows they hold.</summary>
        public List<(IReadOnlyList<DataObject> Objects, int From, int To)> Runs { get; } = [];

        /// <summary>The rows every replacement wrote, those of abandoned ones included.</summary>
        public long RowsWritten { get; private set; }

        /// <summary>The replacements that came back abandoned.</summary>
        public int Abandoned { get; private set; }

        public async Task<ReplaceResult> FlushAsync(int flush, CancellationToken ct)
        {
            // The new object takes the runs before it while each holds fewer than twice the rows gathered so far.
            int kept = Runs.Count;
            int from = flush;
            while (kept > 0 && Runs[kept - 1].To - Runs[kept - 1].From < 2 * (flush + 1 - from))
            {
                from = Runs[--kept].From;
            }

            DataObject[] absorbed = [.. Runs.Skip(kept).SelectMany(run => run.Objects)];
            RowsWritten += (flush + 1 - from) * Flush;
            ReplaceResult result = await ReplaceAsync(dataset, absorbed, from, flush, ct);
            if (result.Outcome == OperationOutcome.Abandoned)
            {
                // A merge or a delete took one of those objects. Their rows are in the dataset: the runs are forgotten, and
                // the flush's rows go alone.
                Abandoned++;
                Runs.RemoveRange(kept, Runs.Count - kept);
                from = flush;
                RowsWritten += Flush;
                result = await ReplaceAsync(dataset, [], from, flush, ct);
            }

            if (result.Outcome == OperationOutcome.Applied)
            {
                Runs.RemoveRange(kept, Runs.Count - kept);
                Runs.Add((result.Added, from, flush + 1));
            }

            return result;
        }
    }

    /// <summary>
    /// One commit that writes the rows of the flushes <paramref name="from"/> to <paramref name="last"/> in one draft,
    /// replaces <paramref name="removed"/> and counts the last flush's entries.
    /// </summary>
    private static async Task<ReplaceResult> ReplaceAsync(VortexDataset dataset, IReadOnlyList<DataObject> removed, int from, int last, CancellationToken ct)
    {
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<ChangeRow>(Rows(from * Flush, (last - from + 1) * Flush), ct);
        return await dataset.ReplaceObjectsAsync(removed, [draft], [new SourceRange(Queue, (ulong)(last * Flush), (ulong)((last + 1) * Flush))], ct);
    }

    private static DatasetOptions Options() => new DatasetOptions
    {
        Seed = 0x0E_CE47,
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(ChangeRow.City)),
            SortKey = [LayoutValue.Column(nameof(ChangeRow.Key))],
            MergeFanout = 8,
            CalmAfter = null,
        },
    };

    private static async Task<VortexDataset> CreateAsync(IObjectStore store, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        return await VortexDataset.CreateAsync(store, ChangeRow.Schema, Options(), ct);
    }

    private static ChangeRow[] Rows(long first, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ChangeRow(first + i, i * 0.5, i % 2 == 0 ? "Paris" : "Lyon"))];

    private static async Task<List<long>> KeysAsync(VortexDataset dataset, CancellationToken ct)
    {
        List<long> keys = [.. (await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct)).Select(row => row.Key)];
        keys.Sort();
        return keys;
    }
}
