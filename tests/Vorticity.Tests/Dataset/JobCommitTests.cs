// A job's commit against writers that commit while it attempts: it rebases past the attempts an append is given, for as
// long as the job took, and once every attempt lost it reports the job abandoned, its inputs untouched for the next plan,
// rather than throwing what a failure of the store would throw.
//
// THE ORACLE IS THE OUTCOME AND THE ROWS: the job's outcome, the attempts its commit lost, and every row read back.
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class JobCommitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Another handle commits an append before each of the job's commit puts, more times than a commit attempts: the job
    /// is abandoned, not failed, its inputs stay at level 0, and the plan after the race takes them up and applies.
    /// </summary>
    [Fact]
    public async Task AJobWhoseCommitLosesEveryAttemptIsAbandonedAndTakenUpAgain()
    {
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore memory = new MemoryObjectStore();
        RacingStore store = new RacingStore(memory);
        DatasetOptions options = Options(clock);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, options, Ct);
        List<Row> all = [];
        for (int a = 0; a < 4; a++)
        {
            await AppendAsync(dataset, all, all.Count);
        }

        await using VortexDataset other = await VortexDataset.OpenAsync(memory, options, Ct);
        store.Racer = async () => await AppendAsync(other, all, all.Count);
        CompactionResult? lost = await dataset.CompactAsync(cancellationToken: Ct);

        Assert.Equal(OperationOutcome.Abandoned, lost!.Outcome);
        Assert.True(store.Raced >= 8, $"{store.Raced} commits raced the job's");

        store.Racer = null;
        await dataset.RefreshAsync(Ct);
        CompactionResult? applied = await dataset.CompactAsync(cancellationToken: Ct);
        Assert.Equal(OperationOutcome.Applied, applied!.Outcome);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>
    /// The same race under a clock that moves on with each attempt: the job took longer than its eight attempts, so its
    /// commit goes on past them, and applies once the other writer stops.
    /// </summary>
    [Fact]
    public async Task AJobsCommitRebasesForAsLongAsTheJobTook()
    {
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore memory = new MemoryObjectStore();
        SlowStore slow = new SlowStore(memory, clock);
        RacingStore store = new RacingStore(slow);
        DatasetOptions options = Options(clock);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, options, Ct);
        List<Row> all = [];
        for (int a = 0; a < 4; a++)
        {
            await AppendAsync(dataset, all, all.Count);
        }

        // Each read of a data object takes the job a minute by the clock; each race of its commit a second, ten of them.
        await using VortexDataset other = await VortexDataset.OpenAsync(memory, options, Ct);
        slow.Reading = TimeSpan.FromMinutes(1);
        store.Racer = async () =>
        {
            slow.Reading = TimeSpan.Zero;
            clock.Advance(TimeSpan.FromSeconds(1));
            await AppendAsync(other, all, all.Count);
            if (store.Raced >= 10)
            {
                store.Racer = null;
            }
        };

        CompactionResult? applied = await dataset.CompactAsync(cancellationToken: Ct);

        Assert.Equal(OperationOutcome.Applied, applied!.Outcome);
        Assert.Equal(10, store.Raced);
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>A store that runs <see cref="Racer"/> before each put of a commit object while it is set: another writer's commit first.</summary>
    private sealed class RacingStore(IObjectStore inner) : ForwardingStore(inner)
    {
        private int _racing;

        internal Func<Task>? Racer { get; set; }

        internal int Raced { get; private set; }

        public override async ValueTask<PutOutcome> PutAsync(string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
        {
            if (key.StartsWith("commit/", StringComparison.Ordinal) && Racer is { } racer && Interlocked.Exchange(ref _racing, 1) == 0)
            {
                try
                {
                    Raced++;
                    await racer();
                }
                finally
                {
                    Volatile.Write(ref _racing, 0);
                }
            }

            return await Inner.PutAsync(key, content, length, condition, cancellationToken);
        }
    }

    /// <summary>A store whose reads of data objects move the clock on by <see cref="Reading"/>: a job that takes that long a read.</summary>
    private sealed class SlowStore(IObjectStore inner, ManualClock clock) : ForwardingStore(inner)
    {
        internal TimeSpan Reading { get; set; }

        public override ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            if (key.StartsWith("data/", StringComparison.Ordinal))
            {
                clock.Advance(Reading);
            }

            return Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);
        }
    }

    private static DatasetOptions Options(ManualClock clock) => new DatasetOptions
    {
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Row.Day)),
            SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Status))],
            ObjectBytes = 64 << 20,
            MergeFanout = 4,
            CalmAfter = null,
        },
        ContentsObjectBytes = 0,
        TimeProvider = clock,
        RetentionWindow = TimeSpan.FromDays(1),
    };

    private static async Task AppendAsync(VortexDataset dataset, List<Row> all, long first)
    {
        Random random = new Random((int)first + 3);
        Row[] rows = [.. Enumerable.Range(0, 20).Select(i => new Row(0, $"svc-{random.Next(0, 12)}", first + i, random.Next(0, 100), random.NextDouble()))];
        lock (all)
        {
            all.AddRange(rows);
        }

        await using ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Row>(rows, Ct);
        await dataset.AppendAsync(draft, Ct);
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, int Status, double Measure);
}
