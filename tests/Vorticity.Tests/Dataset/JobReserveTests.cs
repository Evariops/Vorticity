// What a compaction job's input reserves before it takes it, and what its merge does when a reservation it cannot go on
// without is not granted: it ends at the key it reached, and the job commits what it merged.
//
// THE ORACLES: a scan that reserves delivers the rows a scan that reserves nothing delivers, whatever it had to wait for;
// and a dataset whose consolidation ended short of room part way holds, once the rest is consolidated, the rows it was
// appended, each once.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class JobReserveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The distinct messages the heavy object's rows name, kept in a dictionary a reader decodes whole.</summary>
    private const int Messages = 2_000;

    /// <summary>
    /// Messages of some 600 bytes each, of words that repeat: their dictionary's values are compressed, and a reader of
    /// any row of their chunk decodes them whole, 1.2 MB in a block of 2 MiB.
    /// </summary>
    private static readonly string[] Texts = [.. Enumerable.Range(0, Messages).Select(static m =>
        string.Join(' ', Enumerable.Range(0, 60).Select(w => $"word{(m * 31 + w * 7) % 97:D2}-{m % 13}")) + $" #{m:D5}")];

    /// <summary>
    /// Five hundred messages of some 64 KiB each, of words that repeat: a dictionary of 32 MB of values, compressed to
    /// little, which a reader of any row of its chunk decodes whole. What an object a small budget cannot hold open names.
    /// </summary>
    private static readonly Lazy<string[]> Long = new Lazy<string[]>(static () => [.. Enumerable.Range(0, 500).Select(static m =>
        string.Join(' ', Enumerable.Range(0, 6_400).Select(w => $"word{(m * 31 + w * 7) % 97:D2}-{m % 13}")) + $" #{m:D5}")]);

    /// <summary>
    /// A scan that reserves in its consumer's reserve reserves what a read of a chunk decodes whole before it decodes it,
    /// at what the chunk's header says: under a budget that holds it, the scan runs no split again, delivers every row,
    /// and leaves nothing reserved.
    /// </summary>
    [Fact]
    public async Task AnInputReservesWhatAChunkDecodesWholeBeforeItDecodesIt()
    {
        await using VortexSession session = VortexSession.Create(static o => o.MaxDegreeOfParallelism = 1);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, Options(session), Ct);
        ObjectEntry entry = await AppendHeavyAsync(dataset, rows: 40_000);

        QueryMemoryBudget budget = new QueryMemoryBudget(1L << 30);
        MemoryReserve reserve = new MemoryReserve(budget);
        (long rows, BatchHandover handover, long peak) = await ScanAsync(dataset, entry, budget, reserve);

        Assert.Equal(entry.Rows, rows);
        Assert.Equal(0, handover.Reruns);
        Assert.Equal(0, reserve.MadeRoom);
        Assert.Equal(0, reserve.HeldBack);

        // The dictionary's values, a megabyte and more in a block of two, and its views: reserved at once, before they were
        // decoded, where a read's segments and a batch each ask for far less.
        long values = Texts.Sum(static text => (long)text.Length);
        Assert.True(reserve.MostAtOnce >= values, $"the scan reserved {reserve.MostAtOnce:N0} bytes at once at most for a dictionary of {values:N0}");
        Assert.True(peak >= values, $"the scan held {peak:N0} bytes for a dictionary of {values:N0}");
        Assert.Equal(0, reserve.Spare);
        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>
    /// A scan whose reserve does not hold what a chunk decodes whole asks its consumer's job to make room, on the
    /// consumer's flow, and runs its split again once the room is there: other work holds the budget but a mebibyte until
    /// the scan asks, and gives it back then. The scan delivers every row, the split run again and no more.
    /// </summary>
    [Fact]
    public async Task AnInputShortOfRoomHasItsJobMakeRoomAndRunsItsSplitAgain()
    {
        await using VortexSession session = VortexSession.Create(static o => o.MaxDegreeOfParallelism = 1);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, Options(session), Ct);
        ObjectEntry entry = await AppendHeavyAsync(dataset, rows: 40_000);

        QueryMemoryBudget budget = new QueryMemoryBudget(64L << 20);
        long held = budget.CeilingBytes - (1L << 20);
        Assert.True(budget.TryReserve(held));
        int asked = 0;
        MemoryReserve reserve = new MemoryReserve(budget)
        {
            MakeRoom = level =>
            {
                // What the job gives back at its first level: here, the other work's bytes, once.
                if (level > 0 || Interlocked.Exchange(ref held, 0) is not (> 0 and long bytes))
                {
                    return new ValueTask<bool>(false);
                }

                asked++;
                budget.Release(bytes);
                return new ValueTask<bool>(true);
            },
        };

        (long rows, BatchHandover handover, _) = await ScanAsync(dataset, entry, budget, reserve);

        Assert.Equal(entry.Rows, rows);
        Assert.Equal(1, asked);
        Assert.True(reserve.MadeRoom >= 1, "no reservation had the job make room");
        Assert.InRange(handover.Reruns, 1, 8);
        Assert.Equal(0, reserve.Spare);
        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>
    /// A scan whose reserve does not hold what it cannot go on without, its consumer's job having nothing left to give
    /// back, tells its consumer so on the consumer's flow, naming its reserve and the bytes it asked for: the consumer's
    /// merge ends there. Nothing is left reserved.
    /// </summary>
    [Fact]
    public async Task AnInputShortOfRoomWithNothingLeftToGiveBackTellsItsConsumer()
    {
        await using VortexSession session = VortexSession.Create(static o => o.MaxDegreeOfParallelism = 1);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, Options(session), Ct);
        ObjectEntry entry = await AppendHeavyAsync(dataset, rows: 40_000);

        // A mebibyte: the dictionary's two do not fit, and no level gives anything back.
        QueryMemoryBudget budget = new QueryMemoryBudget(1L << 20);
        MemoryReserve reserve = new MemoryReserve(budget) { MakeRoom = static _ => new ValueTask<bool>(false) };

        ReserveExhaustedException stop = await Assert.ThrowsAsync<ReserveExhaustedException>(
            async () => await ScanAsync(dataset, entry, budget, reserve));

        Assert.Same(reserve, stop.Reserve);
        Assert.True(stop.Bytes > 0);
        Assert.Equal(0, reserve.Spare);
        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>
    /// A consolidation of four objects that follow one another along the keys, two by two, the third holding a dictionary
    /// a reader decodes whole, run under a budget that dictionary does not fit: its merge opens the first two, comes to
    /// the third at its first key, has the job give back what it started ahead, and ends there. The job commits what it
    /// merged, a piece of itself: the rows below that key in level 1, the first object gone, the second left in level 0
    /// with its first rows marked, the third and the fourth as they were. Consolidated to the end under a budget that
    /// holds them, the dataset holds every row it was appended, once.
    /// </summary>
    /// <remarks>
    /// The job is planned by a handle under a large budget and run by one under a small budget: a handle's own plan counts
    /// the third object at what its entry says it holds open, and takes no job that holds it.
    /// </remarks>
    [Fact]
    public async Task AMergeShortOfRoomEndsAtItsKeyAndCommitsWhatItMerged()
    {
        // A budget that holds the first two objects open and the writer of a small object, 11 MiB whatever it writes, and
        // not the third's dictionary, 32 MB of values in a block of the next power of two.
        const long Small = 32L << 20;
        QueryMemoryBudget budget = new QueryMemoryBudget(Small);
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = budget);
        await using VortexSession large = VortexSession.Create(static o => o.JobMemoryBudget = new QueryMemoryBudget(4L << 30));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, Options(session), Ct);

        // Services 000 to 199, 100 to 299, 200 to 399 and 300 to 499: each object starts where the one two before it ends.
        List<Line> all = [];
        await AppendAsync(dataset, all, Light(all.Count, services: 0..200));
        await AppendAsync(dataset, all, Light(all.Count, services: 100..300));
        Random random = new Random(29);
        string[] texts = Long.Value;
        await AppendAsync(dataset, all, [.. Enumerable.Range(0, 2_000).Select(i => new Line(0, $"svc-{200 + (i / 10):D3}", all.Count + i, texts[random.Next(texts.Length)]))]);
        await AppendAsync(dataset, all, Light(all.Count, services: 300..500));

        List<ObjectEntry> appended = await EntriesAsync(dataset, 0);
        Assert.Equal(4, appended.Count);
        ObjectEntry heavy = appended.MaxBy(static entry => entry.OpenBytes)!;
        Assert.True(heavy.OpenBytes >= Small, $"the heavy object holds {heavy.OpenBytes:N0} bytes open, which a budget of {Small:N0} may hold");

        await using VortexDataset wide = await VortexDataset.OpenAsync(store, Options(large), Ct);
        CompactionJob whole = Assert.Single((await CompactionPolicy.PlanConsolidationsAsync(wide, Ct)).Jobs);
        Assert.Equal((CompactionTrigger.Consolidation, 4), (whole.Trigger, whole.Inputs.Count));

        CompactionResult? stopped = await dataset.RunWithinBudgetAsync(whole, static _ => new ValueTask<CompactionJob?>((CompactionJob?)null), Ct);

        Assert.NotNull(stopped);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"STOPPED: {stopped.ObjectsIn} -> {stopped.ObjectsOut} objects, {stopped.Rows:N0} rows, by {stopped.StoppedBy}; reserves {stopped.Reserves}; held {stopped.MemoryReserved:N0} (inputs {stopped.MemoryReservedBy.Inputs:N0}, writer {stopped.MemoryReservedBy.Writer:N0})");
        Assert.True(stopped.StoppedShort, "the consolidation ran to its end under a budget its third input does not fit");
        Assert.Equal(OperationOutcome.Applied, stopped.Outcome);
        Assert.True(stopped.Piece);
        Assert.True(stopped.Reserves.MadeRoom >= 1, "the job gave nothing back before its merge ended");

        // The first object whole, and the second's services 100 to 200: every row below the third object's first key.
        string firstOfHeavy = "svc-200";
        long below = all.Count(row => row.Sequence < 8_000 && (string.CompareOrdinal(row.Service, firstOfHeavy) < 0 || row.Service == firstOfHeavy));
        Assert.Equal(below, stopped.Rows);
        Assert.Equal(2, stopped.ObjectsIn);
        Assert.Equal(0, budget.ReservedBytes);

        await dataset.RefreshAsync(Ct);
        Assert.Equal(stopped.ObjectsOut, (await EntriesAsync(dataset, 1)).Count);
        List<ObjectEntry> left = await EntriesAsync(dataset, 0);
        Assert.Equal(3, left.Count);
        Assert.Equal(all.Count - below, left.Sum(static entry => entry.Rows));
        Assert.Single(left, static entry => entry.Deletions.LeadingRun > 0);
        Assert.Equal(all.OrderBy(static row => row.Sequence), (await dataset.Scan<Line>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static row => row.Sequence));

        // The rest, under a budget that holds it: the rows left in level 0 join those the piece wrote, none of them twice.
        await wide.RefreshAsync(Ct);
        IReadOnlyList<CompactionResult> rest = await wide.ConsolidateAsync(Ct);
        Assert.NotEmpty(rest);
        Assert.All(rest, static job => Assert.False(job.StoppedShort));
        Assert.Empty(await EntriesAsync(wide, 0));
        Assert.Equal(all.OrderBy(static row => row.Sequence), (await wide.Scan<Line>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static row => row.Sequence));
    }

    /// <summary>
    /// A job's writer rents under an account that takes first from what the writer reserved before a copy it counted: the
    /// bytes reserved are counted once, a block past them asks the budget for the rest alone, and what was reserved and
    /// no block took goes back once the copy is done.
    /// </summary>
    [Fact]
    public void AWritersAccountTakesFirstFromWhatItReserved()
    {
        QueryMemoryBudget budget = new QueryMemoryBudget(64L << 20);
        MemoryReserve reserve = new MemoryReserve(budget);
        using ReservingAccount account = new ReservingAccount(budget, "writer", reserve);
        Assert.Same(reserve, account.Reserve);

        Assert.True(reserve.TryRaise(1L << 20));
        Assert.Equal(1L << 20, budget.ReservedBytes);

        // Within what was reserved: counted once.
        Assert.True(account.TryTake(600_000));
        Assert.Equal(1L << 20, budget.ReservedBytes);
        Assert.Equal((1L << 20) - 600_000, reserve.Spare);

        // Past it: what is left of the reserve, and the rest asked of the budget.
        Assert.True(account.TryTake(600_000));
        Assert.Equal(1_200_000, budget.ReservedBytes);
        Assert.Equal(0, reserve.Spare);
        reserve.Close(1L << 20);
        Assert.Equal(1_200_000, budget.ReservedBytes);

        // A copy that took less than was reserved for it gives the rest back.
        Assert.True(reserve.TryRaise(1L << 20));
        Assert.True(account.TryTake(100_000));
        reserve.Close(1L << 20);
        Assert.Equal(1_300_000, budget.ReservedBytes);

        account.Give(1_300_000);
        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>
    /// The consolidation of what a merge ended short of room left in level 0 leaves an object that merge wrote at its
    /// size as it is: the handle that committed it keeps the key it ends at, past which every row left lies
    /// (<see cref="VortexDataset.NoteTakenThrough"/>), where the rows' sparse index tells where they start to a granule
    /// only, in which the object ends, and it was read again. The object stays in level 1, the same object, and the
    /// dataset holds every row once.
    /// </summary>
    /// <remarks>
    /// The stop of <see cref="AMergeShortOfRoomEndsAtItsKeyAndCommitsWhatItMerged"/> seals the object it was writing,
    /// short of its size, which the next piece takes with its own rows and keeps no key for. The key is given here as a
    /// merge that committed objects ended by their size gives it, to the handle that consolidates the rest.
    /// </remarks>
    [Fact]
    public async Task TheConsolidationAfterAStopLeavesTheObjectsItWroteAtTheirSize()
    {
        const long Small = 32L << 20;
        QueryMemoryBudget budget = new QueryMemoryBudget(Small);
        await using VortexSession session = VortexSession.Create(o => o.JobMemoryBudget = budget);
        await using VortexSession large = VortexSession.Create(static o => o.JobMemoryBudget = new QueryMemoryBudget(4L << 30));
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Line.Schema, Options(session, indexed: true), Ct);
        List<Line> all = [];
        await AppendAsync(dataset, all, Light(all.Count, services: 0..200));
        await AppendAsync(dataset, all, Light(all.Count, services: 100..300));
        Random random = new Random(29);
        string[] texts = Long.Value;
        await AppendAsync(dataset, all, [.. Enumerable.Range(0, 2_000).Select(i => new Line(0, $"svc-{200 + (i / 10):D3}", all.Count + i, texts[random.Next(texts.Length)]))]);
        await AppendAsync(dataset, all, Light(all.Count, services: 300..500));

        await using VortexDataset wide = await VortexDataset.OpenAsync(store, Options(large, indexed: true), Ct);
        CompactionJob whole = Assert.Single((await CompactionPolicy.PlanConsolidationsAsync(wide, Ct)).Jobs);
        CompactionResult? stopped = await dataset.RunWithinBudgetAsync(whole, static _ => new ValueTask<CompactionJob?>((CompactionJob?)null), Ct);
        Assert.True(stopped is { StoppedShort: true, Outcome: OperationOutcome.Applied });

        await wide.RefreshAsync(Ct);
        ObjectEntry written = Assert.Single(await EntriesAsync(wide, 1));
        ObjectEntry marked = Assert.Single(await EntriesAsync(wide, 0), static entry => entry.Deletions.LeadingRun > 0);
        wide.NoteTakenThrough(marked.Key, marked.Deletions.LeadingRun, written.LastKey.Span);

        IReadOnlyList<CompactionResult> rest = await wide.ConsolidateAsync(Ct);

        Assert.NotEmpty(rest);
        Assert.Empty(await EntriesAsync(wide, 0));
        Assert.Contains(await EntriesAsync(wide, 1), entry => entry.Key == written.Key);
        Assert.Equal(all.Count - written.Rows, rest.Sum(static job => job.Rows));
        Assert.Equal(all.OrderBy(static row => row.Sequence), (await wide.Scan<Line>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(static row => row.Sequence));
    }

    /// <summary>
    /// The rows of <paramref name="entry"/>'s object read as a merge reads an input, its batches handed over, under an
    /// account that takes from <paramref name="reserve"/> in <paramref name="budget"/>: how many, what the scan handed
    /// them over through, and the most the budget held.
    /// </summary>
    private static async Task<(long Rows, BatchHandover Handover, long Peak)> ScanAsync(
        VortexDataset dataset, ObjectEntry entry, QueryMemoryBudget budget, MemoryReserve reserve)
    {
        BatchHandover handover = new BatchHandover { Reserve = reserve };
        using WorkAccount work = new WorkAccount(budget, "the scan of one input");
        CountedAccount account = new CountedAccount(work, reserve);
        long rows = 0;
        try
        {
            ObjectLease lease = await dataset.RentAsync(entry, Ct);
            await using (lease.ConfigureAwait(false))
            {
                ScanBuilder scan = lease.File.ScanBuilder().WithAccount(account).WithPrefetch(1).WithConsumerLane()
                    .WithMaxBatchRows(1_024).WithWindowRows(1_024).WithHandover(handover);
                await foreach (RecordBatch batch in scan.ExecuteAsync().WithCancellation(Ct).ConfigureAwait(false))
                {
                    rows += batch.RowCount;
                }
            }
        }
        finally
        {
            reserve.End();
        }

        return (rows, handover, budget.PeakBytes);
    }

    /// <summary>An object of <paramref name="rows"/> rows that name the long messages, appended, and its entry.</summary>
    private static async Task<ObjectEntry> AppendHeavyAsync(VortexDataset dataset, int rows)
    {
        Random random = new Random(11);
        List<Line> all = [];
        await AppendAsync(dataset, all, [.. Enumerable.Range(0, rows).Select(i => new Line(0, $"svc-{random.Next(0, 12):D3}", i, Texts[random.Next(Messages)]))]);
        return Assert.Single(await EntriesAsync(dataset, 0));
    }

    /// <summary>Twenty rows of a one-letter message for each service of <paramref name="services"/>, numbered from <paramref name="first"/>.</summary>
    private static Line[] Light(long first, Range services) =>
        [.. Enumerable.Range(services.Start.Value, services.End.Value - services.Start.Value)
            .SelectMany(service => Enumerable.Range(0, 20).Select(i => (service, i)))
            .Select((row, at) => new Line(0, $"svc-{row.service:D3}", first + at, "m"))];

    /// <summary>The entries of level <paramref name="level"/>, in key order.</summary>
    private static async Task<List<ObjectEntry>> EntriesAsync(VortexDataset dataset, int level)
    {
        List<ObjectEntry> entries = [];
        await foreach ((_, ObjectEntry parsed) in dataset.Snapshot.EntriesAsync(level, Ct).ConfigureAwait(false))
        {
            entries.Add(parsed);
        }

        return entries;
    }

    /// <summary>
    /// The options of a dataset under <paramref name="session"/>; with <paramref name="indexed"/>, its objects' entries
    /// carry their table of contents and the sparse index of their keys however small they are, as a large object's do,
    /// which a consolidation in pieces reads.
    /// </summary>
    private static DatasetOptions Options(VortexSession session, bool indexed = false) => new DatasetOptions
    {
        Session = session,
        TableInFileBytes = indexed ? 0 : ObjectEntry.FirstReadBytes,
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Line.Day)),
            SortKey = [LayoutValue.Column(nameof(Line.Service)), LayoutValue.Column(nameof(Line.Sequence))],
            ObjectBytes = 64 << 20,
            MergeFanout = 64,
            CalmAfter = null,
        },
        ContentsObjectBytes = 0,
    };

    private static async Task AppendAsync(VortexDataset dataset, List<Line> all, Line[] rows)
    {
        all.AddRange(rows);
        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Line>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    [VortexRecord]
    public partial record struct Line(int Day, string Service, long Sequence, string Message);
}
