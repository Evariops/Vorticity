// A compaction deletes what it replaced, after its commit and never before: once the version before it is past the
// retention window, at the end of a later job or when a step finds nothing due; and the outputs of a job whose commit
// applied nothing, at once.
//
// THE ORACLE IS THE STORE'S LISTING: the data objects it holds against those the latest version reads.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class CompactionCleanupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Eight appends merged into one object: the eight stay in the store while the window keeps the version that reads
    /// them, and go at the first step past it that finds nothing due; the store then holds what the latest version reads.
    /// </summary>
    [Fact]
    public async Task AJobDeletesWhatItReplacedOnceTheWindowPasses()
    {
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(clock), Ct);
        List<Row> all = await AppendAsync(dataset, appends: 8);

        CompactionResult? job = await dataset.CompactAsync(cancellationToken: Ct);
        Assert.NotNull(job);
        Assert.Equal(OperationOutcome.Applied, job.Outcome);
        Assert.Equal(9, (await DataKeysAsync(store)).Count);

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Null(await dataset.CompactAsync(cancellationToken: Ct));
        Assert.Equal(9, (await DataKeysAsync(store)).Count);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(await dataset.CompactAsync(cancellationToken: Ct));

        List<string> live = [.. (await dataset.ListObjectsAsync(Ct).ToListAsync(Ct)).Select(o => o.Key)];
        Assert.Equal(live.Order(StringComparer.Ordinal), (await DataKeysAsync(store)).Order(StringComparer.Ordinal));
        Assert.Equal(all.OrderBy(r => r.Sequence), (await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct)).OrderBy(r => r.Sequence));
    }

    /// <summary>A dataset that keeps two versions whatever their age deletes nothing until two commits more follow.</summary>
    [Fact]
    public async Task VersionsKeptWhateverTheirAgeHoldTheirObjects()
    {
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero));
        await using MemoryObjectStore store = new MemoryObjectStore { TimeProvider = clock };
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, Options(clock) with { RetainedVersions = 2 }, Ct);
        await AppendAsync(dataset, appends: 8);
        Assert.NotNull(await dataset.CompactAsync(cancellationToken: Ct));

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(await dataset.CompactAsync(cancellationToken: Ct));
        Assert.Equal(9, (await DataKeysAsync(store)).Count);

        await AppendAsync(dataset, appends: 2, first: 1_000);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(await dataset.CompactAsync(cancellationToken: Ct));
        Assert.Equal(3, (await DataKeysAsync(store)).Count);
    }

    private static async Task<List<string>> DataKeysAsync(IObjectStore store)
    {
        List<string> keys = [];
        await foreach (ObjectListing listing in store.ListAsync(CommitKey.DataPrefix, null, Ct))
        {
            keys.Add(listing.Key);
        }

        return keys;
    }

    private static DatasetOptions Options(TimeProvider clock) => new DatasetOptions
    {
        Layout = new DatasetLayout
        {
            Partition = LayoutValue.Column(nameof(Row.Day)),
            SortKey = [LayoutValue.Column(nameof(Row.Service)), LayoutValue.Column(nameof(Row.Sequence))],
            MergeFanout = 8,
            CalmAfter = null,
        },
        RetentionWindow = TimeSpan.FromHours(1),
        TimeProvider = clock,
        ContentsObjectBytes = 0,
    };

    private static async Task<List<Row>> AppendAsync(VortexDataset dataset, int appends, long first = 0)
    {
        List<Row> all = [];
        for (int a = 0; a < appends; a++)
        {
            Row[] rows = [.. Enumerable.Range(0, 64).Select(i => new Row(0, $"svc-{i % 5}", first + all.Count + i))];
            all.AddRange(rows);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return all;
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence);
}
