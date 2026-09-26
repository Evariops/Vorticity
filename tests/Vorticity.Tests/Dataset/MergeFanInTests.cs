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

        DatasetScanMetrics metrics = new DatasetScanMetrics();
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
        DatasetScanMetrics metrics = new DatasetScanMetrics();
        Assert.Equal(Objects * PerObject, await WalkAsync(dataset.ScanBuilder().WithMetrics(metrics), ct));

        Assert.Equal(Objects, metrics.ObjectsOpened);
        Assert.Equal(options.MaxOpenObjects, metrics.CacheHits);
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
