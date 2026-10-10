using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// A scan in the tree's order opens the next object ahead once its consumer reads past the first
/// batch: one object opened for a consumer that stops at the first batch, two open at most for one
/// that reads on and one without a prefetch, none held once the scan stops wherever it stops, and an
/// object opened ahead whose open fails failing the scan only once the scan reaches it.
/// </summary>
public sealed partial class DatasetReadAheadTests
{
    private const int Objects = 4;

    private const int ObjectRows = 1_000;

    [Fact]
    public async Task AScanThatStopsAtItsFirstBatchOpensOneObject()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        DatasetScanCounters metrics = new DatasetScanCounters();
        List<long> keys = await KeysAsync(dataset.ScanBuilder().WithMetrics(metrics), batches: 1);

        Assert.Equal(Enumerable.Range(0, ObjectRows).Select(k => (long)k), keys);
        Assert.Equal(1, metrics.ObjectsOpened);
        Assert.Equal(1, metrics.Cursors);
        Assert.Equal(0, dataset.LeasedObjects);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(0, 1)]
    public async Task AScanThatReadsOnOpensTheNextObjectAheadUnderAPrefetch(int prefetch, int cursors)
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        DatasetScanCounters metrics = new DatasetScanCounters();
        List<long> keys = await KeysAsync(
            dataset.ScanBuilder().WithOptions(new ScanOptions { Prefetch = prefetch }, keepEncodings: false).WithMetrics(metrics), int.MaxValue);

        Assert.Equal(Enumerable.Range(0, Objects * ObjectRows).Select(k => (long)k), keys);
        Assert.Equal(Objects, metrics.ObjectsOpened);
        Assert.Equal(cursors, metrics.Cursors);
        Assert.Equal(0, dataset.LeasedObjects);
    }

    [Fact]
    public async Task AScanStoppedInsideTheDatasetGivesBackTheObjectOpenedAhead()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await CreateAsync(store);
        DatasetScanCounters metrics = new DatasetScanCounters();
        long read = 0;
        int after = -1;
        await foreach (RecordBatch batch in dataset.ScanBuilder().WithOptions(new ScanOptions { BatchRows = 100 }, keepEncodings: false).WithMetrics(metrics).ExecuteAsync(Ct))
        {
            read += batch.RowCount;
            if (read == ObjectRows + 100)
            {
                // Inside the second object, the third opens ahead: two objects leased at once.
                for (int wait = 0; dataset.LeasedObjects < 2 && wait < 10_000; wait++)
                {
                    await Task.Delay(1, Ct);
                }

                Assert.Equal(2, dataset.LeasedObjects);
                after = 0;
            }
            else if (after >= 0 && ++after == 5)
            {
                break;
            }
        }

        // The third object, opened ahead and never reached, is given back with the second.
        Assert.Equal(ObjectRows + 600, read);
        Assert.Equal(3, metrics.ObjectsOpened);
        Assert.Equal(2, metrics.Cursors);
        Assert.Equal(0, dataset.LeasedObjects);
    }

    [Fact]
    public async Task AnObjectOpenedAheadThatCannotOpenFailsTheScanOnlyOnceItIsReached()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        string third;
        await using (VortexDataset written = await CreateAsync(store))
        {
            List<DataObject> objects = [];
            await foreach (DataObject held in written.ListObjectsAsync(Ct))
            {
                objects.Add(held);
            }

            third = objects.OrderBy(held => held.FirstRow).ElementAt(2).Key;
        }

        await store.DeleteAsync([third], Ct);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);

        // Two objects read and the scan stopped: the third, opened ahead, could not open, and
        // nobody asked for its rows.
        List<long> keys = await KeysAsync(dataset.ScanBuilder(), batches: 2);
        Assert.Equal(2 * ObjectRows, keys.Count);
        Assert.Equal(0, dataset.LeasedObjects);

        // Read on, the scan reaches it.
        await Assert.ThrowsAsync<ObjectNotFoundException>(() => KeysAsync(dataset.ScanBuilder(), int.MaxValue));
        Assert.Equal(0, dataset.LeasedObjects);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A dataset of <see cref="Objects"/> objects of one batch each, its keys counting up from 0.</summary>
    private static async Task<VortexDataset> CreateAsync(MemoryObjectStore store)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions { Seed = 17 }, Ct);
        for (int o = 0; o < Objects; o++)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>([.. Enumerable.Range(o * ObjectRows, ObjectRows).Select(k => new Row(k, k / 3.0))], Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return dataset;
    }

    /// <summary>The keys of the scan's first <paramref name="batches"/> batches, the scan stopped there.</summary>
    private static async Task<List<long>> KeysAsync(DatasetScanBuilder scan, int batches)
    {
        List<long> keys = [];
        int read = 0;
        await foreach (RecordBatch batch in scan.ExecuteAsync(Ct))
        {
            ReadOnlySpan<long> values = batch.Column(0).AsPrimitive<long>().Values;
            for (int row = 0; row < batch.RowCount; row++)
            {
                keys.Add(values[row]);
            }

            if (++read == batches)
            {
                break;
            }
        }

        return keys;
    }

    [VortexRecord]
    public partial record struct Row(long Key, double Value);
}
