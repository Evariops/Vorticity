using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A dataset of requests at the time-window design's own scale, for <c>--micro paths --design</c>: hours of
/// periods at 5 × 10⁴ requests a second, slices of a minute of 3 × 10⁶ rows, a terabyte and more on the disk.
/// Each hour is drawn by a generator of its own, seeded by the hour, several hours at once, each written into
/// objects of <c>partMinutes</c> minutes while the hours before it are appended in the order of their instants,
/// each followed by the compaction it made due; the objects the compactions replaced are then vacuumed.
/// </summary>
internal static class DesignBuild
{
    internal static async Task<(long Rows, string Needle)> FillAsync(
        IObjectStore store, int hours, double rate, int budget, int parallel, int writeDegree, int partMinutes, string scratch)
    {
        // The runs the writers and the synopsis tables move to disk wait beside the store, on its disk.
        System.IO.Directory.CreateDirectory(scratch);
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = writeDegree);
        VortexDataset dataset = await VortexDataset.CreateAsync(store, GeneratedRequest.Schema, new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(GeneratedRequest.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
            Identifiers = [nameof(GeneratedRequest.RequestId)],
            Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(budget), ScratchDirectory = scratch },
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
                    written[hour] = Task.Run(() => WriteHourAsync(dataset, hour, perHour, partMinutes));
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
                    needle = $"req-{(hour * perHour) + (perHour / 2):x12}";
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
            await TablesAsync(dataset);
            return (hours * perHour, needle);
        }
    }

    /// <summary>Each sealed period's synopsis tables: what each holds, its rows, its bytes and its chunks.</summary>
    private static async Task TablesAsync(VortexDataset dataset)
    {
        await foreach (TreeEntry entry in dataset.Levels[PeriodLevels.Periods].EnumerateAsync(dataset.Pages, default))
        {
            ObjectEntry held = ObjectEntry.FromBytes(entry.Value.Span);
            await using ObjectLease lease = await dataset.RentAsync(held, default);
            IndexDirectory? directory = await lease.File.ReadIndexDirectoryAsync();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  period of {held.Rows:N0} rows, {lease.File.FileLength / 1e6:F0} MB:"));
            foreach (IndexEntry table in directory?.Entries.Where(e => e.Kind == IndexKinds.SynopsisTable) ?? [])
            {
                if (SynopsisTableInfo.From(table.Options) is { } info)
                {
                    long bytes = table.Runs[0].Payload.Sum(chunk => (long)chunk.Length);
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"    {info.Kind} per {info.Bucket} of {info.Keys.Length} keys: {info.Rows:N0} rows, {bytes / 1e3:F0} KB in {table.Runs[0].Payload.Count} chunks"));
                }
            }
        }
    }

    private static int s_appended;

    /// <summary>
    /// The rows of one hour, drawn by a generator seeded by the hour, written into drafts of <paramref name="partMinutes"/> minutes each;
    /// its first <paramref name="minutes"/> minutes alone when fewer than 60.
    /// </summary>
    internal static async Task<List<ObjectDraft>> WriteHourAsync(VortexDataset dataset, int hour, long perHour, int partMinutes, int minutes = 60, bool lateRows = true)
    {
        RequestGenerator generator = new RequestGenerator(1_000 + hour, hour * perHour);
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
