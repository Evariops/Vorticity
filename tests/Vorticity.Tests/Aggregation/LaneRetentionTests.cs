using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests.Aggregation;

/// <summary>
/// The lanes' tables die with the merge. A blocking group by goes out in continuations of the lane or
/// the merge worker that finished last, on its stack, where the frames of the pass still hold the
/// lanes' partitions: at the first batch, a collection finds no lane's keys or states alive but the
/// ones a merge in series kept as the result; a merge in parts delivers its parts as they are merged
/// (PLAN-HIGH-CARDINALITY, H14), and once the last is, a collection finds none alive.
/// </summary>
public sealed partial class LaneRetentionTests
{
    private const int Rows = 240_000;

    [Theory]
    [InlineData(60_000, true)]
    [InlineData(60_000, false)]
    [InlineData(300, false)]
    public async Task NoLaneTableOutlivesTheMerge(int keys, bool inParts)
    {
        string path = await WriteAsync(keys);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 4);
            await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
            Vorticity.Aggregation byKey = file.Scan<Row>().GroupBy(r => r.Key).Select(g => (g.Key, g.Count(), g.Sum(x => x.Value)));
            byKey.Plan.MergeInParts = inParts;
            List<WeakReference> tables = [];
            int slots = 0;
            byKey.Plan.Watch = partitions =>
            {
                foreach (AggregationPartition partition in partitions)
                {
                    tables.Add(new WeakReference(partition.Keys));
                    slots = partition.Slots.Length;
                    foreach (AggregateSlot slot in partition.Slots)
                    {
                        tables.Add(new WeakReference(slot));
                    }
                }
            };

            int alive = -1;
            long rows = 0;
            await foreach (Columns<KeyTotal> batch in byKey.As<KeyTotal>().WithCancellation(Ct))
            {
                if (alive < 0 && !inParts)
                {
                    alive = Alive(tables);
                }

                foreach (long count in batch.Column<long>(1).Values)
                {
                    rows += count;
                }
            }

            if (inParts)
            {
                alive = Alive(tables);
            }

            Assert.Equal(Rows, rows);

            // A merge in series folds every lane but the largest into it; one in parts cuts the key
            // space in a power of two, never three.
            AggregationRun run = byKey.Plan.LastRun!;
            Assert.Equal(4, run.Lanes.Length);
            Assert.Equal(inParts, run.MergeParts != run.Lanes.Length - 1);

            // In parts, every group is in a part's tables; in series, in the largest lane's.
            Assert.Equal(inParts ? 0 : 1 + slots, alive);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The tables a full collection leaves alive.</summary>
    private static int Alive(List<WeakReference> tables)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return tables.Count(table => table.IsAlive);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> WriteAsync(int keys)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "lane-retention");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        Row[] rows = new Row[Rows];
        for (int row = 0; row < Rows; row++)
        {
            ulong mix = (ulong)row * 0x9E37_79B9_7F4A_7C15UL;
            rows[row] = new Row((int)((mix >> 24) % (ulong)keys), (long)((mix >> 8) % 100));
        }

        // Small chunks, where ranges are cut: enough for every lane to take some.
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { RowBlockSize = 8_192, ChunkTargetBytes = 1 << 16 }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    [VortexRecord]
    public partial record struct Row(int Key, long Value);

    [VortexRecord]
    public partial record struct KeyTotal(int Key, long Count, long Sum);
}
