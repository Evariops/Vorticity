// A question of one lane over many small objects of level 0 opens them ahead of its turn, side by side, where it waited for
// each before; it opens none that its entries settle, and gives back those it opened ahead and did not reach.
//
// THE ORACLE IS LINQ AND THE STORE: the answers are LINQ's, and the store says how many reads of data objects were in
// flight at once, and how many it served.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetQuestionReadAheadTests
{
    private const int Objects = 40;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A count over forty objects at a degree of one reads several of them at once, and answers as LINQ does.</summary>
    [Fact]
    public async Task ACountOfOneLaneReadsItsObjectsSideBySide()
    {
        (InFlightStore store, VortexDataset dataset, List<Row> rows) = await CreateAsync();
        await using (dataset)
        {
            long count = await dataset.Scan<Row>().Where(r => r.Measure > 0.5).CountAsync(Ct);

            Assert.Equal(rows.LongCount(r => r.Measure > 0.5), count);
            Assert.True(store.MostInFlight > 1, $"{store.MostInFlight} reads of data objects in flight at most");
            Assert.Equal(0, dataset.LeasedObjects);
        }
    }

    /// <summary>A read of every row reads several objects at once too, and returns every row.</summary>
    [Fact]
    public async Task AScanOfOneLaneReadsItsObjectsSideBySide()
    {
        (InFlightStore store, VortexDataset dataset, List<Row> rows) = await CreateAsync();
        await using (dataset)
        {
            List<Row> read = await dataset.Scan<Row>().ToRecordsAsync(Ct).ToListAsync(Ct);

            Assert.Equal(rows.OrderBy(r => r.Sequence), read.OrderBy(r => r.Sequence));
            Assert.True(store.MostInFlight > 1, $"{store.MostInFlight} reads of data objects in flight at most");
            Assert.Equal(0, dataset.LeasedObjects);
        }
    }

    /// <summary>A count of every row, which the objects' entries settle, opens none of them.</summary>
    [Fact]
    public async Task ACountTheEntriesSettleOpensNoObject()
    {
        (InFlightStore store, VortexDataset dataset, List<Row> rows) = await CreateAsync();
        await using (dataset)
        {
            long before = store.DataReads;
            Assert.Equal(rows.Count, await dataset.ScanBuilder().CountAsync(Ct));
            Assert.Equal(before, store.DataReads);
        }
    }

    /// <summary>A scan that stops after its first object's rows gives back what it opened ahead, and opens few.</summary>
    [Fact]
    public async Task AScanStoppedEarlyGivesBackWhatItOpenedAhead()
    {
        (InFlightStore store, VortexDataset dataset, _) = await CreateAsync();
        await using (dataset)
        {
            long before = store.DataReads;
            int taken = 0;
            await foreach (RecordBatch batch in dataset.ScanBuilder().ExecuteAsync(Ct))
            {
                if (++taken == 3)
                {
                    break;
                }
            }

            Assert.Equal(0, dataset.LeasedObjects);
            Assert.True(store.DataReads - before < Objects / 2, $"{store.DataReads - before} reads of data objects for three batches");
        }
    }

    private static async Task<(InFlightStore Store, VortexDataset Dataset, List<Row> Rows)> CreateAsync()
    {
        InFlightStore store = new InFlightStore(new MemoryObjectStore());
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                Partition = LayoutValue.Column(nameof(Row.Day)),
                SortKey = [LayoutValue.Column(nameof(Row.Service))],
                ObjectBytes = 64 << 20,
                MergeFanout = 64,
                CalmAfter = null,
            },
            ContentsObjectBytes = 0,
        }, Ct);
        Random random = new Random(29);
        List<Row> rows = [];
        for (int a = 0; a < Objects; a++)
        {
            Row[] batch = [.. Enumerable.Range(0, 50).Select(i => new Row(0, $"svc-{random.Next(0, 8)}", rows.Count + i, random.NextDouble()))];
            rows.AddRange(batch);
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Row>(batch, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        // A handle that starts cold, every object to open from the store.
        await using (dataset)
        {
        }

        return (store, await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct), rows);
    }

    /// <summary>A store whose reads of data objects each take a few milliseconds, and which counts how many are in flight at once.</summary>
    private sealed class InFlightStore(IObjectStore inner) : ForwardingStore(inner)
    {
        private int _inFlight;
        private int _most;
        private long _reads;

        internal int MostInFlight => Volatile.Read(ref _most);

        internal long DataReads => Interlocked.Read(ref _reads);

        public override async ValueTask<ObjectRead> ReadAsync(string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
        {
            if (!key.StartsWith("data/", StringComparison.Ordinal))
            {
                return await Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);
            }

            return await HeldAsync(() => Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken));
        }

        public override async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (!key.StartsWith("data/", StringComparison.Ordinal))
            {
                return await Inner.ReadTailAsync(key, destination, cancellationToken);
            }

            return await HeldAsync(() => Inner.ReadTailAsync(key, destination, cancellationToken));
        }

        private async ValueTask<ObjectRead> HeldAsync(Func<ValueTask<ObjectRead>> read)
        {
            Interlocked.Increment(ref _reads);
            int now = Interlocked.Increment(ref _inFlight);
            for (int most = Volatile.Read(ref _most); now > most; most = Volatile.Read(ref _most))
            {
                if (Interlocked.CompareExchange(ref _most, now, most) == most)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(5);
                return await read();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    [VortexRecord]
    public partial record struct Row(int Day, string Service, long Sequence, double Measure);
}
