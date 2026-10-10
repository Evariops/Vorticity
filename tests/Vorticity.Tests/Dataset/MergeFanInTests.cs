// Sixty-four objects whose keys interleave row by row, which is the merge at its widest.
//
// The choosing step picks the input holding the smallest key and works out how many of its rows may
// go before another takes over, both from the same pass over the open inputs. Interleaving the
// objects modulo their count makes every run exactly one row, so that pass is paid as often as it
// can be and any disagreement between the inputs shows on the first key rather than the last.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class MergeFanInTests
{
    private const int Objects = 64;
    private const int PerObject = 32;

    [Fact]
    public async Task MergesSixtyFourObjectsInterleavedRowByRow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);

        // Appended back to front, so a tree that kept arrival order rather than key order is caught.
        for (int residue = Objects - 1; residue >= 0; residue--)
        {
            await dataset.AppendAsync(Batches(types, schema, residue), ct);
        }

        List<long> expected = [];
        for (long key = 0; key < (long)Objects * PerObject; key++)
        {
            expected.Add(key);
        }

        DatasetScanCounters metrics = new DatasetScanCounters();
        List<long> walked = [];
        await foreach (RecordBatch batch in dataset.ScanBuilder().WithMetrics(metrics).InKeyOrder("key")
            .ExecuteAsync(ct).WithCancellation(CancellationToken.None))
        {
            using (batch)
            {
                ReadOnlySpan<long> keys = batch.Column(0).AsPrimitive<long>().Values;
                for (int row = 0; row < batch.RowCount; row++)
                {
                    walked.Add(keys[row]);
                }
            }
        }

        Assert.Equal(expected, walked);

        // Every object's range covers the whole dataset, so every one of them is held open at once.
        Assert.Equal(Objects, metrics.Cursors);

        // And the descending merge is the exact reverse, which is what the ranks on ties buy.
        List<long> back = [];
        await foreach (RecordBatch batch in dataset.ScanBuilder().InKeyOrder("key", descending: true)
            .ExecuteAsync(ct).WithCancellation(CancellationToken.None))
        {
            using (batch)
            {
                ReadOnlySpan<long> keys = batch.Column(0).AsPrimitive<long>().Values;
                for (int row = 0; row < batch.RowCount; row++)
                {
                    back.Add(keys[row]);
                }
            }
        }

        expected.Reverse();
        Assert.Equal(expected, back);
    }

    // Two groups of twelve objects, one after the other in key order: the merge holds a group open
    // at once, more than it looks over in full, lets it close, then opens the next one by one. Past
    // its own minimum, which ranks it, each object holds a random half of a small range of keys, so
    // the keys tie across objects that stand at different places, and the ties come in the order of
    // the objects' minima.
    [Fact]
    public async Task MergesGroupsThatOpenAndCloseWithTiesInTheOrderOfTheirObjects()
    {
        const int Group = 12;
        const int Range = 40;
        CancellationToken ct = TestContext.Current.CancellationToken;
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        Random random = new Random(20260927);

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        List<(long Key, int Source)> expected = [];
        for (int source = (2 * Group) - 1; source >= 0; source--)
        {
            long start = source < Group ? 0 : 100_000;
            List<long> keys = [start + (source % Group)];
            for (int i = 0; i < Range; i++)
            {
                if (random.Next(2) == 0)
                {
                    keys.Add(start + Group + i);
                }
            }

            foreach (long key in keys)
            {
                expected.Add((key, source));
            }

            await dataset.AppendAsync(Rows(types, schema, [.. keys], source), ct);
        }

        expected.Sort();

        List<(long Key, int Source)> walked = [];
        await foreach (RecordBatch batch in dataset.ScanBuilder().InKeyOrder("key")
            .ExecuteAsync(ct).WithCancellation(CancellationToken.None))
        {
            using (batch)
            {
                ReadOnlySpan<long> keys = batch.Column(0).AsPrimitive<long>().Values;
                ReadOnlySpan<double> sources = batch.Column(1).AsPrimitive<double>().Values;
                for (int row = 0; row < batch.RowCount; row++)
                {
                    walked.Add((keys[row], (int)sources[row]));
                }
            }
        }

        Assert.Equal(expected, walked);
    }

    /// <summary>One object holding <paramref name="keys"/>, each row's measure the object's number.</summary>
    private static async IAsyncEnumerable<RecordBatch> Rows(DTypeArena types, DType schema, long[] keys, int source)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keyBuffer = arena.Allocate(keys.Length * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(keys.Length * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        keys.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(keyBytes));
        MemoryMarshal.Cast<byte, double>(measureBytes).Fill(source);
        int key = arena.AddPrimitive(i64, keys.Length, Validity.NonNullable, PType.I64, keyBuffer);
        int measure = arena.AddPrimitive(f64, keys.Length, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, keys.Length, Validity.NonNullable, [key, measure]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // The walk holds all sixty-four objects open at once, far more than the cache keeps between
    // scans. The next walk opens the others first, in the same order, and must still find the ones
    // the cache kept.
    [Fact]
    public async Task AWalkAgainFindsTheObjectsTheCacheKept()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);
        DatasetOptions options = Clustered();

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, options, ct);
        for (int residue = 0; residue < Objects; residue++)
        {
            await dataset.AppendAsync(Batches(types, schema, residue), ct);
        }

        Assert.Equal(Objects * PerObject, await WalkAsync(dataset.ScanBuilder(), ct));
        DatasetScanCounters metrics = new DatasetScanCounters();
        Assert.Equal(Objects * PerObject, await WalkAsync(dataset.ScanBuilder().WithMetrics(metrics), ct));

        Assert.Equal(Objects, metrics.ObjectsOpened);
        Assert.Equal(options.MaxOpenObjects, metrics.CacheHits);
    }

    // Each object is smaller than the window an open reads at its end, so the open reads it whole
    // and its rows cost the store nothing more: one read per object the walk opens, and none for
    // an object the cache kept open.
    [Fact]
    public async Task AWalkReadsTheStoreOnlyToOpenAnObject()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        DTypeArena types = new DTypeArena();
        DType schema = Schema(types);

        await using MemoryObjectStore inner = new MemoryObjectStore();
        DataReads store = new DataReads(inner);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, Clustered(), ct);
        for (int residue = 0; residue < Objects; residue++)
        {
            await dataset.AppendAsync(Batches(types, schema, residue), ct);
        }

        Assert.Equal(Objects * PerObject, await WalkAsync(dataset.ScanBuilder(), ct));
        Interlocked.Exchange(ref store.Count, 0);
        DatasetScanCounters metrics = new DatasetScanCounters();
        Assert.Equal(Objects * PerObject, await WalkAsync(dataset.ScanBuilder().WithMetrics(metrics), ct));

        Assert.Equal(metrics.ObjectsOpened - metrics.CacheHits, Volatile.Read(ref store.Count));
    }

    /// <summary>A store that counts the ranges read from data objects, and not from commits.</summary>
    private sealed class DataReads(IObjectStore inner) : IObjectStore
    {
        internal long Count;

        public ValueTask<ObjectRange> GetRangeAsync(string key, long offset, int length, CancellationToken cancellationToken)
        {
            if (key.StartsWith(CommitKey.DataPrefix, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Count);
            }

            return inner.GetRangeAsync(key, offset, length, cancellationToken);
        }

        public ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken) =>
            inner.HeadAsync(key, cancellationToken);

        public ValueTask<PutOutcome> PutIfAbsentAsync(
            string key, System.IO.Pipelines.PipeReader content, long length, CancellationToken cancellationToken) =>
            inner.PutIfAbsentAsync(key, content, length, cancellationToken);

        public ValueTask DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
            inner.DeleteAsync(keys, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
            inner.ListAsync(prefix, startAfter, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<long> WalkAsync(DatasetScanBuilder scan, CancellationToken ct)
    {
        long rows = 0;
        await foreach (RecordBatch batch in scan.InKeyOrder("key").ExecuteAsync(ct).WithCancellation(CancellationToken.None))
        {
            using (batch)
            {
                rows += batch.RowCount;
            }
        }

        return rows;
    }

    private static DType Schema(DTypeArena types) => types.Struct(
        ["key", "measure"],
        [types.Primitive(PType.I64, Nullability.NonNullable), types.Primitive(PType.F64, Nullability.NonNullable)],
        Nullability.NonNullable);

    private static DatasetOptions Clustered() => new DatasetOptions
    {
        Seed = 0xC1057E_5EED,
        Write = new VortexWriteOptions { RowBlockSize = 64, DataBlockTargetBytes = 8 << 10 },
        ClusteringKey = ["key"],
    };

    /// <summary>One object: the keys congruent to <paramref name="residue"/>, shuffled.</summary>
    private static async IAsyncEnumerable<RecordBatch> Batches(DTypeArena types, DType schema, int residue)
    {
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType f64 = types.Primitive(PType.F64, Nullability.NonNullable);

        long[] ordered = new long[PerObject];
        for (int i = 0; i < PerObject; i++)
        {
            ordered[i] = ((long)i * Objects) + residue;
        }

        // Shuffled within the object, so the column is not sorted and only the mandatory run can
        // deliver it in key order.
        Random shuffle = new Random(residue + 1);
        for (int i = ordered.Length - 1; i > 0; i--)
        {
            int j = shuffle.Next(i + 1);
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }

        CanonicalArena arena = new CanonicalArena();
        VortexBuffer keys = arena.Allocate(PerObject * sizeof(long), sizeof(long), out Span<byte> keyBytes);
        VortexBuffer measures = arena.Allocate(PerObject * sizeof(double), sizeof(double), out Span<byte> measureBytes);
        Span<long> keyValues = MemoryMarshal.Cast<byte, long>(keyBytes);
        Span<double> measureValues = MemoryMarshal.Cast<byte, double>(measureBytes);
        for (int i = 0; i < PerObject; i++)
        {
            keyValues[i] = ordered[i];
            measureValues[i] = ordered[i] * 0.5;
        }

        int key = arena.AddPrimitive(i64, PerObject, Validity.NonNullable, PType.I64, keys);
        int measure = arena.AddPrimitive(f64, PerObject, Validity.NonNullable, PType.F64, measures);
        int root = arena.AddStruct(schema, PerObject, Validity.NonNullable, [key, measure]);
        yield return new RecordBatch(arena, root, 0);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
