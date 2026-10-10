using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>A row of the change tests: a unique key, a measure missing one row in seven, a city.</summary>
[VortexRecord]
public partial record struct ChangeRow(long Key, double? Measure, string City);

/// <summary>A record that covers only the key, which an update cannot write whole rows with.</summary>
[VortexRecord]
public partial record struct KeyOnly(long Key);

/// <summary>
/// Deletes and updates take exactly the rows their filter is true for, and leave a dataset every
/// reader still agrees on: the rows are the oracle's, the key order holds across levels in both
/// directions, compaction goes on, and a verification finds nothing wrong. The oracle is the rows
/// as they were written, filtered and changed in plain C#.
/// </summary>
public sealed class DatasetRowChangeTests
{
    private const int Objects = 5;
    private const int PerObject = 200;

    private static readonly string[] Cities = ["Paris", "Lyon", "Nice", "Lille"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADeleteTakesTheRowsItsFilterIsTrueForAndKeepsTheUnknownOnes(bool clustered)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<ChangeRow> written) = await CreateAsync(store, clustered, interleaved: true, ct);
        await using (dataset)
        {
            List<ChangeRow> before = await RowsAsync(dataset, ct);
            ulong version = dataset.Version;

            // A null measure makes the comparison unknown: those rows are not deleted.
            RowChangeResult deleted = await dataset.DeleteAsync<ChangeRow>(r => r.Measure > 50.0, ct);

            List<ChangeRow> expected = written.FindAll(row => !(row.Measure > 50.0));
            Assert.Equal(OperationOutcome.Applied, deleted.Outcome);
            Assert.Equal(version + 1, deleted.Version);
            Assert.Equal(dataset.Version, deleted.Version);
            Assert.Equal(written.Count - expected.Count, deleted.Rows);
            Assert.Equal(1, deleted.Attempts);
            Assert.Equal(Objects, deleted.ObjectsIn);
            Assert.Equal(Objects, deleted.ObjectsOut);
            Assert.Equal(expected.Count, dataset.RowCount);

            List<ChangeRow> after = await RowsAsync(dataset, ct);
            Assert.Equal(Sorted(expected), Sorted(after));
            if (!clustered)
            {
                // Without a clustering key the rows keep the order a scan read them in.
                Assert.Equal(before.FindAll(row => !(row.Measure > 50.0)), after);
            }

            Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
            await AssertKeyOrderAsync(dataset, clustered, ct);
        }
    }

    [Fact]
    public async Task AnObjectIsRemovedWholeRewrittenOrLeftAsItsRowsSay()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<ChangeRow> written) = await CreateAsync(store, clustered: true, interleaved: false, ct);
        await using (dataset)
        {
            // Object 1 holds the keys 1000 to 1199: all of them go, and the object with them.
            List<DataObject> objects = await dataset.ListObjectsAsync(ct).ToListAsync(ct);
            RowChangeResult removed = await dataset.DeleteAsync<ChangeRow>(r => r.Key >= 1_000 & r.Key < 2_000, ct);
            Assert.Equal(PerObject, removed.Rows);
            Assert.Equal(1, removed.ObjectsIn);
            Assert.Equal(0, removed.ObjectsOut);
            Assert.Equal(0, removed.BytesOut);
            Assert.Equal(objects.Single(o => o.FirstRow == PerObject).Bytes, removed.BytesIn);
            Assert.Equal(Objects - 1, dataset.ObjectCount);

            // Fifty keys of object 2, which now starts at row 200: it is rewritten with the other
            // hundred and fifty, and the others, whose summaries refute the filter, are left as they are.
            List<string> untouched = (await dataset.ListObjectsAsync(ct).ToListAsync(ct))
                .Where(o => o.FirstRow != PerObject).Select(o => o.Key).ToList();
            Assert.Equal(Objects - 2, untouched.Count);
            RowChangeResult rewritten = await dataset.DeleteAsync<ChangeRow>(r => r.Key >= 2_050 & r.Key < 2_100, ct);
            Assert.Equal(50, rewritten.Rows);
            Assert.Equal(1, rewritten.ObjectsIn);
            Assert.Equal(1, rewritten.ObjectsOut);
            List<string> now = (await dataset.ListObjectsAsync(ct).ToListAsync(ct)).Select(o => o.Key).ToList();
            Assert.Equal(Objects - 1, now.Count);
            Assert.Equal(untouched, now.Intersect(untouched));

            List<ChangeRow> expected = written.FindAll(row => !(row.Key is >= 1_000 and < 2_000 or >= 2_050 and < 2_100));
            Assert.Equal(Sorted(expected), Sorted(await RowsAsync(dataset, ct)));
        }
    }

    [Fact]
    public async Task AFilterThatMatchesNothingCommitsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<ChangeRow> written) = await CreateAsync(store, clustered: true, interleaved: true, ct);
        await using (dataset)
        {
            ulong version = dataset.Version;
            int commits = await CommitsAsync(store, ct);

            RowChangeResult none = await dataset.DeleteAsync<ChangeRow>(r => r.City == "Tokyo", ct);
            Assert.Equal(OperationOutcome.AlreadyThere, none.Outcome);
            Assert.Equal(version, none.Version);
            Assert.Equal(0, none.Rows);
            Assert.Equal(1, none.Attempts);

            RowChangeResult nothing = await dataset.DeleteAsync<ChangeRow>(_ => Predicate.None, ct);
            Assert.Equal(OperationOutcome.AlreadyThere, nothing.Outcome);
            Assert.Equal(0, nothing.Attempts);

            RowChangeResult unchanged = await dataset.UpdateAsync<ChangeRow>(r => r.Key < 0, row => row with { City = "Tokyo" }, ct);
            Assert.Equal(OperationOutcome.AlreadyThere, unchanged.Outcome);

            Assert.Equal(version, dataset.Version);
            Assert.Equal(commits, await CommitsAsync(store, ct));
            Assert.Equal(written.Count, dataset.RowCount);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUpdateChangesTheRowsItsFilterIsTrueForAndNoOthers(bool clustered)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<ChangeRow> written) = await CreateAsync(store, clustered, interleaved: true, ct);
        await using (dataset)
        {
            long levelZero = dataset.Levels[0].Entries;
            RowChangeResult updated = await dataset.UpdateAsync<ChangeRow>(
                r => r.City == "Lyon" & r.Measure < 30.0,
                row => row with { Measure = row.Measure * 10, City = "Lyon (updated)" },
                ct);

            List<ChangeRow> expected = written.ConvertAll(row => row.City == "Lyon" && row.Measure < 30.0
                ? row with { Measure = row.Measure * 10, City = "Lyon (updated)" }
                : row);
            int changed = written.Count(row => row.City == "Lyon" && row.Measure < 30.0);
            Assert.Equal(OperationOutcome.Applied, updated.Outcome);
            Assert.Equal(changed, updated.Rows);
            Assert.Equal(Objects, updated.ObjectsIn);
            Assert.Equal(Objects + 1, updated.ObjectsOut);
            Assert.Equal(written.Count, dataset.RowCount);
            Assert.Equal(Sorted(expected), Sorted(await RowsAsync(dataset, ct)));

            // The changed rows arrived together, in one new object of level 0.
            Assert.Equal(levelZero + 1, dataset.Levels[0].Entries);
            if (!clustered)
            {
                List<ChangeRow> after = await RowsAsync(dataset, ct);
                Assert.All(after.TakeLast(changed), row => Assert.Equal("Lyon (updated)", row.City));
            }

            Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
            await AssertKeyOrderAsync(dataset, clustered, ct);
        }
    }

    [Fact]
    public async Task AnUpdateThatMovesKeysLeavesTheLevelsKeyDisjoint()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<ChangeRow> written) = await CreateAsync(store, clustered: true, interleaved: false, ct);
        await using (dataset)
        {
            // Objects capped at 2 KiB, so that the rows lie in several key-disjoint objects whatever
            // level they land in.
            CompactionOptions compaction = new CompactionOptions
            {
                LevelZeroCeiling = 1,
                TargetBytesAtLevelOne = 2 << 10,
                MaxObjectBytes = 2 << 10,
            };
            while (await dataset.CompactAsync(compaction, ct) is not null)
            {
            }

            Assert.Equal(0, dataset.Levels[0].Entries);
            Assert.True(dataset.ObjectCount > 2, "the compaction must leave several key-disjoint objects");

            // Keys from the bottom of the range to above the top: the rows cannot stay in the
            // objects they came from, and a delete from one of those objects must stay in its level.
            RowChangeResult moved = await dataset.UpdateAsync<ChangeRow>(
                r => r.Key < 150, row => row with { Key = row.Key + 1_000_000 }, ct);
            RowChangeResult deleted = await dataset.DeleteAsync<ChangeRow>(r => r.Key >= 3_000 & r.Key < 3_010, ct);

            List<ChangeRow> expected = written
                .Where(row => !(row.Key is >= 3_000 and < 3_010))
                .Select(row => row.Key < 150 ? row with { Key = row.Key + 1_000_000 } : row)
                .ToList();
            Assert.Equal(150, moved.Rows);
            Assert.Equal(10, deleted.Rows);
            Assert.Equal(Sorted(expected), Sorted(await RowsAsync(dataset, ct)));
            await AssertKeyOrderAsync(dataset, clustered: true, ct);

            // Compaction takes the changed objects in, and every answer holds after it.
            while (await dataset.CompactAsync(compaction, ct) is not null)
            {
            }

            Assert.Equal(Sorted(expected), Sorted(await RowsAsync(dataset, ct)));
            await AssertKeyOrderAsync(dataset, clustered: true, ct);
            Assert.True((await dataset.VerifyAsync(cancellationToken: ct)).Holds);
        }
    }

    [Fact]
    public async Task ADeleteByTextFilterTakesTheSameRows()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, List<ChangeRow> written) = await CreateAsync(store, clustered: false, interleaved: true, ct);
        await using (dataset)
        {
            RowChangeResult deleted = await dataset.DeleteAsync(VortexExpr.Parse("City = 'Nice' or Key < 10"), ct);

            List<ChangeRow> expected = written.FindAll(row => !(row.City == "Nice" || row.Key < 10));
            Assert.Equal(written.Count - expected.Count, deleted.Rows);
            Assert.Equal(Sorted(expected), Sorted(await RowsAsync(dataset, ct)));
            await Assert.ThrowsAsync<VortexSchemaException>(async () => await dataset.DeleteAsync(VortexExpr.Parse("Nowhere = 1"), ct));
        }
    }

    [Fact]
    public async Task AnUpdateRefusesARecordThatDoesNotCoverEveryColumn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        (VortexDataset dataset, _) = await CreateAsync(store, clustered: true, interleaved: true, ct);
        await using (dataset)
        {
            ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(
                async () => await dataset.UpdateAsync<KeyOnly>(r => r.Key < 10, row => row with { Key = row.Key + 1 }, ct));
            Assert.Contains("Measure", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ADeleteThatLosesItsObjectsToACompactionIsWorkedOutAgain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore inner = new MemoryObjectStore();
        (VortexDataset setup, List<ChangeRow> written) = await CreateAsync(inner, clustered: true, interleaved: true, ct);
        await setup.DisposeAsync();

        // The delete's commit is held back once while another handle compacts level 0 and commits
        // first: the objects the delete rewrote are gone from the version its commit lands on.
        await using InterposingStore store = new InterposingStore(inner);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, Options(clustered: true), ct);
        await using VortexDataset compactor = await VortexDataset.OpenAsync(inner, Options(clustered: true), ct);
        store.BeforeCommit = async () =>
        {
            CompactionResult? compacted = await compactor.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }, ct);
            Assert.Equal(OperationOutcome.Applied, compacted!.Outcome);
        };

        RowChangeResult deleted = await dataset.DeleteAsync<ChangeRow>(r => r.City == "Paris", ct);

        Assert.Equal(2, deleted.Attempts);
        Assert.Equal(OperationOutcome.Applied, deleted.Outcome);
        List<ChangeRow> expected = written.FindAll(row => row.City != "Paris");
        Assert.Equal(written.Count - expected.Count, deleted.Rows);
        Assert.Equal(Sorted(expected), Sorted(await RowsAsync(dataset, ct)));
        await AssertKeyOrderAsync(dataset, clustered: true, ct);
    }

    /// <summary>
    /// A dataset of <see cref="Objects"/> appended objects: each holds keys of its own range, or with
    /// <paramref name="interleaved"/> the keys congruent to its number, appended out of order.
    /// </summary>
    private static async Task<(VortexDataset Dataset, List<ChangeRow> Written)> CreateAsync(
        IObjectStore store, bool clustered, bool interleaved, CancellationToken ct)
    {
        Decoders.EnsureRegistered();
        VortexDataset dataset = await VortexDataset.CreateAsync(store, ChangeRow.Schema, Options(clustered), ct);
        List<ChangeRow> written = [];
        foreach (int part in (int[])[3, 0, 4, 1, 2])
        {
            ChangeRow[] rows = new ChangeRow[PerObject];
            for (int i = 0; i < PerObject; i++)
            {
                long key = interleaved ? ((long)i * Objects) + part : (part * 1_000L) + i;
                rows[i] = new ChangeRow(key, key % 7 == 3 ? null : key % 100, Cities[(int)(key % Cities.Length)]);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<ChangeRow>(rows, ct);
            await dataset.AppendAsync(draft, ct);
            written.AddRange(rows);
        }

        return (dataset, written);
    }

    private static DatasetOptions Options(bool clustered) => new DatasetOptions
    {
        Seed = 0xDE1E7E,
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 2 << 10 },
        ClusteringKey = clustered ? ["Key"] : null,
    };

    private static async Task<List<ChangeRow>> RowsAsync(VortexDataset dataset, CancellationToken ct) =>
        await dataset.Scan<ChangeRow>().ToRecordsAsync(ct).ToListAsync(ct);

    private static List<ChangeRow> Sorted(IEnumerable<ChangeRow> rows) => [.. rows.OrderBy(row => row.Key)];

    private static async Task<int> CommitsAsync(MemoryObjectStore store, CancellationToken ct) =>
        await store.ListAsync(CommitKey.Prefix, null, ct).CountAsync(ct);

    /// <summary>On a clustered dataset, the key walk up and down gives every key once, in order.</summary>
    private static async Task AssertKeyOrderAsync(VortexDataset dataset, bool clustered, CancellationToken ct)
    {
        if (!clustered)
        {
            return;
        }

        List<long> keys = (await RowsAsync(dataset, ct)).ConvertAll(row => row.Key);
        keys.Sort();
        await using DatasetKeyCursor cursor = await DatasetKeyCursor.OpenAsync(dataset, ct);
        List<long> up = [];
        for (bool ok = await cursor.SeekFirstAsync(ct); ok; ok = await cursor.NextAsync(ct))
        {
            up.Add(cursor.Key.SignedValue);
        }

        List<long> down = [];
        for (bool ok = await cursor.SeekLastAsync(ct); ok; ok = await cursor.PreviousAsync(ct))
        {
            down.Add(cursor.Key.SignedValue);
        }

        down.Reverse();
        Assert.Equal(keys, up);
        Assert.Equal(keys, down);
    }

    /// <summary>A store that runs <see cref="BeforeCommit"/> once, before the first commit object it is asked to create.</summary>
    private sealed class InterposingStore(IObjectStore inner) : IObjectStore
    {
        internal Func<Task>? BeforeCommit { get; set; }

        public ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken) =>
            inner.GetRangeAsync(key, offset, length, cancellationToken);

        public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
            inner.HeadAsync(key, cancellationToken);

        public async ValueTask<PutOutcome> PutIfAbsentAsync(string key, PipeReader content, long length, CancellationToken cancellationToken)
        {
            if (key.StartsWith(CommitKey.Prefix, StringComparison.Ordinal) && BeforeCommit is { } interpose)
            {
                BeforeCommit = null;
                await interpose();
            }

            return await inner.PutIfAbsentAsync(key, content, length, cancellationToken);
        }

        public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
            inner.DeleteAsync(keys, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
