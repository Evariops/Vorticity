// A commit refuses to name objects whose first put began half the retention window ago or more, by
// the writer's clock: vacuum takes an object no version names once it is a window old, and a version
// that names one it took no longer reads.
//
// THE CLOCK MOVES WHILE AN OBJECT IS PUT. A store that moves the shared manual clock on once each data
// object has landed makes the object as old as the move by the time its commit is put, whoever wrote
// it: an append, a draft, a compaction. The store dates the object before the move, as a store that
// dates an upload by its start does.
//
// THE ORACLE IS THE LATEST VERSION READ BACK WHOLE from a fresh handle: a refused commit leaves the
// version before it, and the rows written again commit once.
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class DatasetLateCommitTests
{
    private const int PerObject = 200;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    [Fact]
    public async Task AVacuumBetweenAPutAndItsCommitLeavesTheLatestVersionReadable()
    {
        // The race the guard is for: the object lands, a window passes, another process vacuums and
        // takes the object, which no version names yet, then the writer commits.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DTypeArena types, DType schema, ManualClock clock, MemoryObjectStore inner, MovingStore store) = Setup();
        await using (inner)
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options(clock), ct))
        {
            await dataset.AppendAsync(Of(types, schema, 0, PerObject), ct);
            ulong before = dataset.Version;
            List<string> swept = [];
            store.Landed = async () =>
            {
                clock.Advance(Window + TimeSpan.FromSeconds(1));
                await using VortexDataset vacuuming = await VortexDataset.OpenAsync(inner, Options(clock), ct);
                swept.AddRange((await vacuuming.VacuumAsync(new VacuumOptions { TimeProvider = clock }, ct)).Deleted);
            };

            DatasetIntegrityException refused = await Assert.ThrowsAsync<DatasetIntegrityException>(
                async () => await dataset.AppendAsync(Of(types, schema, PerObject, PerObject), ct));
            Assert.Contains("retention window", refused.Message, StringComparison.Ordinal);
            Assert.Contains(swept, key => key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal));
            Assert.Equal(before, dataset.Version);

            store.Landed = null;
            await using VortexDataset fresh = await VortexDataset.OpenAsync(inner, Options(clock), ct);
            Assert.Equal(before, fresh.Version);
            Assert.Equal(Enumerable.Range(0, PerObject).Select(i => (long)i), (await KeysAsync(fresh)).Order());
        }
    }

    [Fact]
    public async Task AnAppendWhoseObjectBeganHalfAWindowAgoIsRefusedAndCommitsWrittenAgain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DTypeArena types, DType schema, ManualClock clock, MemoryObjectStore inner, MovingStore store) = Setup();
        await using (inner)
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options(clock), ct))
        {
            ulong before = dataset.Version;
            store.Landed = Moves(clock, Window / 2);
            await Assert.ThrowsAsync<DatasetIntegrityException>(async () => await dataset.AppendAsync(Of(types, schema, 0, PerObject), ct));
            Assert.Equal(before, dataset.Version);

            // The object stays in the store, named by no version, for vacuum.
            Assert.Single(await DataKeysAsync(inner, ct));

            store.Landed = null;
            await dataset.AppendAsync(Of(types, schema, 0, PerObject), ct);
            Assert.Equal(before + 1, dataset.Version);
            await using VortexDataset fresh = await VortexDataset.OpenAsync(inner, Options(clock), ct);
            Assert.Equal(Enumerable.Range(0, PerObject).Select(i => (long)i), (await KeysAsync(fresh)).Order());
        }
    }

    [Fact]
    public async Task AnAppendJustInsideHalfTheWindowCommits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DTypeArena types, DType schema, ManualClock clock, MemoryObjectStore inner, MovingStore store) = Setup();
        await using (inner)
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options(clock), ct))
        {
            ulong before = dataset.Version;
            store.Landed = Moves(clock, (Window / 2) - TimeSpan.FromSeconds(1));
            await dataset.AppendAsync(Of(types, schema, 0, PerObject), ct);
            Assert.Equal(before + 1, dataset.Version);
        }
    }

    [Fact]
    public async Task ADraftWhoseObjectBeganHalfAWindowAgoIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DTypeArena types, DType schema, ManualClock clock, MemoryObjectStore inner, MovingStore store) = Setup();
        await using (inner)
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options(clock), ct))
        {
            ulong before = dataset.Version;
            ObjectDraft draft = dataset.StartObject();
            await foreach (RecordBatch batch in Of(types, schema, 0, PerObject))
            {
                await draft.Writer.WriteBatchAsync(batch, ct);
            }

            store.Landed = Moves(clock, Window / 2);
            await Assert.ThrowsAsync<DatasetIntegrityException>(async () => await dataset.ReplaceObjectsAsync([], [draft], ct));
            Assert.Equal(before, dataset.Version);
        }
    }

    [Fact]
    public async Task ACompactionWhoseOutputsBeganHalfAWindowAgoIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        (DTypeArena types, DType schema, ManualClock clock, MemoryObjectStore inner, MovingStore store) = Setup();
        await using (inner)
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Options(clock), ct))
        {
            for (int i = 0; i < 4; i++)
            {
                await dataset.AppendAsync(Of(types, schema, i * PerObject, PerObject), ct);
            }

            ulong before = dataset.Version;
            store.Landed = Moves(clock, Window / 2);
            await Assert.ThrowsAsync<DatasetIntegrityException>(
                async () => await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2, TargetBytesAtLevelOne = 1 << 20 }, ct));
            Assert.Equal(before, dataset.Version);

            store.Landed = null;
            await using VortexDataset fresh = await VortexDataset.OpenAsync(inner, Options(clock), ct);
            Assert.Equal(before, fresh.Version);
            Assert.Equal(Enumerable.Range(0, 4 * PerObject).Select(i => (long)i), (await KeysAsync(fresh)).Order());
        }
    }

    /// <summary>What moves the clock on by <paramref name="by"/> once an object has landed.</summary>
    private static Func<ValueTask> Moves(ManualClock clock, TimeSpan by) => () =>
    {
        clock.Advance(by);
        return ValueTask.CompletedTask;
    };

    private static (DTypeArena Types, DType Schema, ManualClock Clock, MemoryObjectStore Inner, MovingStore Store) Setup()
    {
        Decoders.EnsureRegistered();
        DTypeArena types = new DTypeArena();
        DType schema = types.Struct(
            ["key", "measure"],
            [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
            Nullability.NonNullable);
        ManualClock clock = new ManualClock(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        MemoryObjectStore inner = new MemoryObjectStore { TimeProvider = clock };
        return (types, schema, clock, inner, new MovingStore(inner));
    }

    private static DatasetOptions Options(ManualClock clock) => new DatasetOptions
    {
        Seed = 0x1A7E_C0,
        Layout = new DatasetLayout { SortKey = [LayoutValue.Column("key")] },
        RetentionWindow = Window,
        TimeProvider = clock,
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 512 },
    };

    private static async Task<List<string>> DataKeysAsync(MemoryObjectStore store, CancellationToken ct)
    {
        List<string> keys = [];
        await foreach (ObjectListing listed in store.ListAsync(CommitKey.DataPrefix, null, ct))
        {
            keys.Add(listed.Key);
        }

        return keys;
    }

    private static async Task<List<long>> KeysAsync(VortexDataset dataset)
    {
        List<long> keys = [];
        await foreach (RecordBatch batch in dataset.ScanBuilder().ExecuteAsync())
        {
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                keys.Add(values[row]);
            }
        }

        return keys;
    }

    private static async IAsyncEnumerable<RecordBatch> Of(DTypeArena types, DType schema, long from, int count)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keyBuffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(count * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int row = 0; row < count; row++)
        {
            keyValues[row] = from + row;
            measureValues[row] = (from + row) / 4.0;
        }

        int keyNode = arena.AddPrimitive(i64, count, Validity.NonNullable, PType.I64, keyBuffer);
        int measureNode = arena.AddPrimitive(f64, count, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, count, Validity.NonNullable, [keyNode, measureNode]);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        yield return batch;
        await Task.CompletedTask;
    }

    /// <summary>A store whose <see cref="Landed"/> runs once each data object has landed, before its put returns.</summary>
    private sealed class MovingStore(IObjectStore inner) : ForwardingStore(inner)
    {
        public Func<ValueTask>? Landed { get; set; }

        public override async ValueTask<PutOutcome> PutAsync(
            string key, PipeReader content, long length, PutCondition condition, CancellationToken cancellationToken)
        {
            PutOutcome outcome = await Inner.PutAsync(key, content, length, condition, cancellationToken);
            if (Landed is { } landed && key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal))
            {
                await landed();
            }

            return outcome;
        }
    }
}
