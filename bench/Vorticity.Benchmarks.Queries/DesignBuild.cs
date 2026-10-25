using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A dataset of requests at the time-window design's own scale, for <c>--micro paths --design</c>: hours at
/// 5 × 10⁴ requests a second, an object an hour in key slices of 3 × 10⁶ rows, a terabyte and more on the disk.
/// Each hour is drawn by a generator of its own, seeded by the hour, several hours at once, each written into
/// objects of <c>partMinutes</c> minutes while the hours before it are appended in the order of their instants,
/// each followed by the compaction it made due; the objects the compactions replaced are then vacuumed.
/// </summary>
internal static class DesignBuild
{
    /// <summary>
    /// How the design's objects are written: the index policy under <paramref name="budget"/>, the runs moved to disk in
    /// <paramref name="scratch"/>, and the request ids' zstd frames at level 1. A day of 3 h x 10 min wrote them in
    /// 343 MB at level 1, 361 at 2, 389 at 3 and 353 at 5, compressed in 1.28, 1.63, 1.91 and 4.22 s: matches of five
    /// bytes cost random identifiers more than they save. The names keep the default, 3: level 1 made them larger, 9
    /// took twice as long for a few hundred kilobytes.
    /// </summary>
    internal static VortexWriteOptions WriteOptions(int budget, string scratch) => new VortexWriteOptions
    {
        Indexes = IndexPolicy.Auto.WithBudgetPerMille(budget),
        ScratchDirectory = scratch,
        ZstdLevels = System.Collections.Immutable.ImmutableDictionary<string, int>.Empty.Add(nameof(GeneratedRequest.RequestId), 1),
    };

    internal static async Task<(long Rows, string Needle)> FillAsync(
        IObjectStore store, int hours, double rate, int budget, int parallel, int writeDegree, int partMinutes, string scratch, bool randomIds = false)
    {
        // The runs the writers move to disk wait beside the store, on its disk.
        System.IO.Directory.CreateDirectory(scratch);
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = writeDegree);
        VortexDataset dataset = await VortexDataset.CreateAsync(store, GeneratedRequest.Schema, new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(GeneratedRequest.At)],
            Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) },
            Identifiers = [nameof(GeneratedRequest.RequestId)],
            Write = WriteOptions(budget, scratch),
        });

        await using (dataset)
        {
            long perHour = (long)(rate * 3_600);
            Task<List<ObjectDraft>>[] written = new Task<List<ObjectDraft>>[hours];
            int next = 0;
            void Start()
            {
                while (next < hours && next < Volatile.Read(ref s_appended) + parallel)
                {
                    int hour = next++;
                    written[hour] = Task.Run(() => WriteHourAsync(dataset, hour, perHour, partMinutes, randomIds: randomIds));
                }
            }

            s_appended = 0;
            long begin = Stopwatch.GetTimestamp();
            Start();
            string needle = string.Empty;
            for (int hour = 0; hour < hours; hour++)
            {
                List<ObjectDraft> drafts = await written[hour];
                foreach (ObjectDraft draft in drafts)
                {
                    await using (draft)
                    {
                        await dataset.AppendAsync(draft);
                    }
                }

                Volatile.Write(ref s_appended, hour + 1);
                Start();
                while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
                {
                }

                if (hour == hours / 2)
                {
                    needle = RequestGenerator.IdOf((hour * perHour) + (perHour / 2), randomIds);
                }

                double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
                Console.WriteLine($"  hour {hour + 1} of {hours} appended and compacted, {(hour + 1) * perHour / seconds / 1e6:F2} M rows a second so far");
                if ((hour + 1) % 12 == 0)
                {
                    await VacuumAsync(dataset);
                }
            }

            await QuestionBench.CompactAsync(dataset);
            await VacuumAsync(dataset);
            using Process self = Process.GetCurrentProcess();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  built in {Stopwatch.GetElapsedTime(begin).TotalSeconds:F0} s: a peak of {self.PeakWorkingSet64 / 1e9:F2} GB in memory, {GC.GetTotalAllocatedBytes() / 1e9:F1} GB allocated, {GC.CollectionCount(2)} collections of generation 2"));
            return (hours * perHour, needle);
        }
    }

    private static int s_appended;

    /// <summary>
    /// The rows of one hour, drawn by a generator seeded by the hour, written into drafts of <paramref name="partMinutes"/> minutes each;
    /// its first <paramref name="minutes"/> minutes alone when fewer than 60.
    /// </summary>
    internal static async Task<List<ObjectDraft>> WriteHourAsync(
        VortexDataset dataset, int hour, long perHour, int partMinutes, int minutes = 60, bool lateRows = true, bool randomIds = false)
    {
        RequestGenerator generator = new RequestGenerator(1_000 + hour, hour * perHour, randomIds);
        Random late = new Random(7_000 + hour);
        DateTimeOffset start = QuestionBench.Start.AddHours(hour);
        long perPart = perHour * partMinutes / 60;
        double step = 3_600.0 / perHour;
        List<ObjectDraft> drafts = [];
        GeneratedRequest[] batch = new GeneratedRequest[1 << 18];
        try
        {
            long rows = perHour * Math.Min(minutes, 60) / 60;
            for (long first = 0; first < rows; first += perPart)
            {
                ObjectDraft draft = dataset.StartObject();
                drafts.Add(draft);
                long last = Math.Min(rows, first + perPart);
                for (long row = first; row < last;)
                {
                    int count = (int)Math.Min(batch.Length, last - row);
                    for (int i = 0; i < count; i++, row++)
                    {
                        // Mostly in time order: one row in fifty a few seconds late, one in ten thousand up to an hour.
                        DateTimeOffset at = start.AddSeconds(row * step);
                        double lag = lateRows ? late.NextDouble() : 1;
                        at = lag < 0.0001 ? at.AddSeconds(-late.Next(60, 3_600))
                            : lag < 0.02 ? at.AddSeconds(-late.Next(1, 10))
                            : at;
                        batch[i] = generator.Next(at < QuestionBench.Start ? QuestionBench.Start : at);
                    }

                    await draft.Writer.WriteAsync<GeneratedRequest>(batch.AsSpan(0, count));
                }
            }

            return drafts;
        }
        catch
        {
            foreach (ObjectDraft draft in drafts)
            {
                await draft.DisposeAsync();
            }

            throw;
        }
    }

    /// <summary>Deletes the objects no version needs any more: the clock a month ahead, past every version's window.</summary>
    private static async Task VacuumAsync(VortexDataset dataset)
    {
        VacuumResult vacuumed = await dataset.VacuumAsync(new VacuumOptions { TimeProvider = new Ahead(TimeSpan.FromDays(30)) });
        Console.WriteLine($"  vacuum: {vacuumed.Deleted.Length} objects deleted");
    }

    /// <summary>The system's clock, shifted ahead.</summary>
    private sealed class Ahead(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + shift;
    }
}
