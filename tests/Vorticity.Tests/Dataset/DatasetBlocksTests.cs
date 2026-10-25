// The blocks of a dataset's objects, against each object's own rows: every object a job writes in slices keeps a table
// of blocks on its key's first column, read in one request with no open of the object, and every block holds one value of
// that column but where the column's runs are shorter than a block's least rows.
//
// THE ORACLE IS THE OBJECT'S ROWS: each block is held to the values of the first column the object's rows hold in order.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetBlocksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Sources whose runs are a few rows, thousands, or more than two blocks, arrived in random order and compacted: each
    /// object a job writes keeps its blocks, which cover its rows once; a block holds at most <see cref="BlockTable.MostRows"/>
    /// rows, changes source only within its first <see cref="BlockTable.LeastRows"/> rows, and ends where its source does
    /// once it holds them, or full.
    /// </summary>
    [Fact]
    public async Task EveryBlockHoldsOneSourceButWhereItsRunsAreShort()
    {
        Random random = new Random(0xB10C);
        List<Event> rows = [];
        for (int source = 0; source < 60; source++)
        {
            int runs = (source % 3) switch
            {
                0 => random.Next(1, 40),
                1 => random.Next(500, 6_000),
                _ => random.Next(20_000, 150_000),
            };

            for (int i = 0; i < runs; i++)
            {
                rows.Add(new Event($"source-{source:D3}", random.NextInt64(1_000_000_000), i));
            }
        }

        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows));
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(memory);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Event.Schema, new DatasetOptions { ClusteringKey = [nameof(Event.Source), nameof(Event.At)], Clustering = new ClusteringOptions { ObjectBytes = 2 << 20 }, ContentsObjectBytes = 1 }, Ct);
        for (int from = 0; from < rows.Count; from += 100_000)
        {
            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Event>(rows.Skip(from).Take(100_000).ToArray(), Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
        {
        }

        int objects = 0;
        int blocks = 0;
        int single = 0;
        long bytes = 0;
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync(Ct))
        {
            if (held.Entry.Contents is not { Blocks.Count: > 0 } contents)
            {
                continue;
            }

            objects++;
            store.Reset();
            BlockTable table = Assert.IsType<BlockTable>(await dataset.ReadBlocksAsync(held.Entry, 0, Ct));
            Assert.Equal(1, store.Requests);
            bytes += contents.BlocksOf(0)!.Value.Length;
            string[] sources = await SourcesAsync(dataset, held.Entry);
            Assert.Equal(held.Entry.Rows, table.TotalRows);
            for (int b = 0; b < table.Count; b++)
            {
                int start = (int)table.FirstRow(b);
                int count = (int)table.Rows[b];
                Assert.InRange(count, 1, BlockTable.MostRows);
                int changes = 0;
                for (int offset = 1; offset < count; offset++)
                {
                    if (sources[start + offset] != sources[start + offset - 1])
                    {
                        Assert.True(offset < BlockTable.LeastRows, $"block {b} changes source at row {offset} of {count}");
                        changes++;
                    }
                }

                single += changes == 0 ? 1 : 0;
                int next = start + count;
                if (next < sources.Length)
                {
                    Assert.True(
                        count == BlockTable.MostRows || (count >= BlockTable.LeastRows && sources[next] != sources[next - 1]),
                        $"block {b} of {count} rows ends inside a source's run");
                }
            }

            blocks += table.Count;
        }

        Console.WriteLine($"BLOCKS: {objects} objects, {blocks} blocks, {single} of one source, {(double)bytes / Math.Max(blocks, 1):F1} bytes a block");
        Assert.True(objects >= 2, $"{objects} objects keep blocks");
        Assert.True(single > blocks / 2, $"{single} of {blocks} blocks hold one source");
    }

    /// <summary>The source of each of the object's rows, in its order.</summary>
    private static async Task<string[]> SourcesAsync(VortexDataset dataset, ObjectEntry entry)
    {
        List<string> sources = [];
        await using ObjectLease lease = await dataset.RentAsync(entry, Ct);
        await foreach (RecordBatch batch in lease.File.ScanBuilder().ExecuteAsync().WithCancellation(Ct))
        {
            VortexColumn column = batch.Column(0);
            for (int row = 0; row < batch.RowCount; row++)
            {
                sources.Add(Encoding.UTF8.GetString(column.AsBinary().GetSpan(row)));
            }
        }

        return [.. sources];
    }

    [VortexRecord]
    public partial record struct Event(string Source, long At, long Value);
}
