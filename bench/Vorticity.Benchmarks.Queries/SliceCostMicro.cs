using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// Two of the prices the target of a time slice trades (<c>--micro slicecost</c>): a row read at a window's edge, which
/// an edge reads whole slice by slice, and a row of a table of slices, which the slices a window keeps fold. A
/// dataset of periods of an hour, in memory behind a store that counts, by service (20), method (5) and status (5),
/// a row every 10 ms. Each question is asked of whole hours from the synopses, which folds table rows alone, and of
/// the same hours from the rows: the bytes each read a row cold, and the time each takes a row warm.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class SliceCostMicro
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    internal static async Task<int> Run(long rows, int rounds)
    {
        await using MemoryObjectStore bytes = new MemoryObjectStore();
        await using CountingObjectStore store = new CountingObjectStore(bytes);
        await using (VortexDataset written = await VortexDataset.CreateAsync(store, AnsweredRequest.Schema, new DatasetOptions
        {
            ClusteringKey = [nameof(AnsweredRequest.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour, SliceTargetRows = 6_000 },
            Write = new VortexWriteOptions { Indexes = IndexPolicy.None.WithBudgetPerMille(10_000) },
        }))
        {
            Random random = new Random(3);
            const int Part = 60_000;
            for (long at = 0; at < rows; at += Part)
            {
                AnsweredRequest[] part = new AnsweredRequest[(int)Math.Min(Part, rows - at)];
                for (int i = 0; i < part.Length; i++)
                {
                    part[i] = new AnsweredRequest(
                        Start.AddMilliseconds((at + i) * 10),
                        $"service-{random.Next(20)}",
                        $"method-{random.Next(5)}",
                        200 + (random.Next(5) * 100),
                        random.NextDouble() * 1_000,
                        random.NextInt64(1, 1L << 30));
                }

                await using ObjectDraft draft = written.StartObject();
                await draft.Writer.WriteAsync<AnsweredRequest>(part);
                await written.AppendAsync(draft);
            }

            while (await written.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
            {
            }
        }

        // Whole sealed hours, so that the synopses answer from tables alone and the rows from the same rows.
        DateTimeOffset from = Start;
        DateTimeOffset to = Start.AddHours(Math.Max(1, (int)(rows / 360_000) - 1));
        Console.WriteLine($"{rows} rows, a row every 10 ms; the questions over [{from:HH:mm}, {to:HH:mm}); best of {rounds}");
        Console.WriteLine($"{"question",-40} {"path",-9} {"rows",9} {"bytes/row",10} {"ns/row",8}");
        (string Name, Func<Scan<AnsweredRequest>, Vorticity.Aggregation> Ask)[] questions =
        [
            ("count and bytes by method", scan => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => r.Method).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes)))),
            ("count and bytes by method and status", scan => scan.Where(r => r.At >= from && r.At < to).GroupBy(r => (r.Method, r.Status)).Select(g => (g.Key.Method, g.Key.Status, g.Count(), g.Sum(r => r.Bytes)))),
        ];

        double tableBytes = 0;
        double edgeBytes = 0;
        double tableNanos = 0;
        double edgeNanos = 0;
        foreach ((string name, Func<Scan<AnsweredRequest>, Vorticity.Aggregation> ask) in questions)
        {
            foreach (bool synopses in new[] { true, false })
            {
                // Cold, by a handle of its own, for the bytes; warm, best of the rounds, for the time.
                await using VortexDataset dataset = await VortexDataset.OpenAsync(store);
                store.Reset();
                Vorticity.Aggregation cold = ask(dataset.Scan<AnsweredRequest>().With(new ScanOptions { UseSynopses = synopses }));
                await DrainAsync(cold);
                long read = synopses ? cold.Metrics.SynopsisRows : cold.Metrics.Rows;
                double perRow = (double)store.BytesRead / read;
                double best = double.MaxValue;
                for (int round = 0; round <= rounds; round++)
                {
                    Vorticity.Aggregation warm = ask(dataset.Scan<AnsweredRequest>().With(new ScanOptions { UseSynopses = synopses }));
                    long begin = Stopwatch.GetTimestamp();
                    await DrainAsync(warm);
                    if (round > 0)
                    {
                        best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                    }
                }

                double nanos = best * 1e6 / read;
                Console.WriteLine($"{name,-40} {(synopses ? "tables" : "rows"),-9} {read,9} {perRow,10:F1} {nanos,8:F1}");
                if (synopses)
                {
                    tableBytes += perRow / questions.Length;
                    tableNanos += nanos / questions.Length;
                }
                else
                {
                    edgeBytes += perRow / questions.Length;
                    edgeNanos += nanos / questions.Length;
                }
            }
        }

        // The tables of slices, as the objects' tables of contents declare them: the bytes of their regions over their
        // rows, which a question that keeps slices of an hour reads and folds.
        long sliceBytes = 0;
        long sliceRows = 0;
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(store))
        {
            await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, default))
            {
                if (Indexes.SynopsisContents.From(held.Entry.Synopsis.Span) is not { } contents)
                {
                    continue;
                }

                // The tables' lists of chunks, which the entry names apart from it, loaded first.
                await dataset.LoadSynopsisListsAsync([(held.Entry, contents, null)], default);
                foreach ((Indexes.SynopsisTableInfo info, System.Collections.Generic.IReadOnlyList<Indexes.TableChunk> chunks) in contents.Tables)
                {
                    if (info.Bucket == Indexes.SynopsisBucket.Slice)
                    {
                        foreach (Indexes.TableChunk chunk in chunks)
                        {
                            sliceBytes += chunk.Region.Length;
                        }

                        sliceRows += info.Rows;
                    }
                }
            }
        }

        // At the prices the synopsis's choice works to, a byte at 10 ns and a row grouped at 12 ns.
        double sliceRowBytes = (double)sliceBytes / Math.Max(1, sliceRows);
        double table = (sliceRowBytes * 10) + 12;
        double edge = (edgeBytes * 10) + 12;
        Console.WriteLine(
            $"the hours' tables {tableBytes:F1} bytes a row, {tableNanos:F1} ns warm; an edge row {edgeBytes:F1} bytes, {edgeNanos:F1} ns warm; " +
            $"the tables of slices {sliceRowBytes:F1} bytes a row over {sliceRows} rows");
        Console.WriteLine(
            $"at 10 ns a byte and 12 ns a row, a row of a table of slices costs {table:F0} ns and an edge row {edge:F0} ns: {table / edge:F2} edge rows a table row");
        return 0;
    }

    /// <summary>Runs an aggregation through its last batch.</summary>
    private static async Task DrainAsync(Vorticity.Aggregation aggregation)
    {
        System.Collections.Generic.IAsyncEnumerator<RecordBatch> batches = aggregation.Query.Batches(default);
        await using (batches)
        {
            while (await batches.MoveNextAsync())
            {
            }
        }
    }
}
