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

    /// <summary>
    /// The same requests as <see cref="FillAsync"/> draws, each hour by a generator seeded by the hour, loaded a day at a
    /// time as the dataset's sealed periods, <paramref name="parallel"/> days at once (<see cref="DatasetCompactor.LoadPeriodsAsync"/>):
    /// drawn straight into the columns of batches in the order of their instants, twice, a day's hours at once to plan its
    /// object and one after the other to write it. No late row, no object of level 0 and no compaction: the dataset an
    /// incremental build ends with, written once, where one wrote each row three times over on a core or two.
    /// </summary>
    internal static async Task<(long Rows, string Needle)> LoadAsync(
        IObjectStore store, int hours, double rate, int budget, int parallel, int writeDegree, string scratch)
    {
        System.IO.Directory.CreateDirectory(scratch);
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = writeDegree);
        VortexDataset dataset = await VortexDataset.CreateAsync(store, GeneratedRequest.Schema, new DatasetOptions
        {
            Session = session,
            ClusteringKey = [nameof(GeneratedRequest.At)],
            Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Day },
            Identifiers = [nameof(GeneratedRequest.RequestId)],
            Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(budget), ScratchDirectory = scratch },
        });

        await using (dataset)
        {
            DType row = dataset.Snapshot.Schema.DType;
            TimeUnit unit = PeriodOptions.InstantOf(dataset.Key);
            long perHour = (long)(rate * 3_600);
            CheckColumns(row, unit, perHour);

            // A day's hours are its parts, each drawn by its own generator: planned at once, written one after the other.
            int days = (hours + 23) / 24;
            PeriodRows[] periods = new PeriodRows[days];
            for (int day = 0; day < days; day++)
            {
                int first = day * 24;
                periods[day] = new PeriodRows(
                    TimeSlicer.InstantOf(QuestionBench.Start.AddHours(first), unit),
                    [.. Enumerable.Range(first, Math.Min(24, hours - first)).Select(hour => (Func<CancellationToken, IAsyncEnumerable<RecordBatch>>)(token => RowsAsync(row, unit, hour, 0, perHour, perHour, 1_000 + hour, token)))]);
            }

            long begin = Stopwatch.GetTimestamp();
            IReadOnlyList<LoadedPeriod> loaded = await DatasetCompactor.LoadPeriodsAsync(dataset, periods, parallel);
            double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
            using Process self = Process.GetCurrentProcess();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  loaded {days} periods in {seconds:F0} s, {loaded.Sum(p => p.Rows) / seconds / 1e6:F2} M rows a second: {loaded.Sum(p => p.Bytes) / 1e9:F1} GB, a peak of {self.PeakWorkingSet64 / 1e9:F2} GB in memory, {GC.GetTotalAllocatedBytes() / 1e9:F1} GB allocated, {GC.CollectionCount(2)} collections of generation 2"));
            await TablesAsync(dataset);
            int needleHour = hours / 2;
            return (hours * perHour, $"req-{(needleHour * perHour) + (perHour / 2):x12}");
        }
    }

    /// <summary>The batches <see cref="LoadAsync"/> draws a request at a time into its columns.</summary>
    private const int LoadBatchRows = 1 << 16;

    /// <summary>
    /// Rows <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> of <paramref name="hour"/>, a row
    /// every 3 600 s / <paramref name="perHour"/>, drawn by a generator of <paramref name="seed"/> whose first id is the first
    /// row's number among the dataset's, in batches of the columns of <paramref name="row"/>, each valid until the next: the
    /// rows <see cref="WriteHourAsync"/> draws for a whole hour of seed 1 000 + <paramref name="hour"/>, but late ones.
    /// </summary>
    internal static async IAsyncEnumerable<RecordBatch> RowsAsync(
        DType row, TimeUnit unit, int hour, long first, long count, long perHour, int seed, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        CanonicalArena arena = new CanonicalArena();
        RecordBatch? batch = null;
        double step = (double)TimeSlicer.Units(TimeSpan.FromHours(1), unit) / perHour;
        try
        {
            RequestColumns columns = new RequestColumns(new RequestGenerator(seed, (hour * perHour) + first), row);
            long origin = TimeSlicer.InstantOf(QuestionBench.Start.AddHours(hour), unit);
            for (long drawn = first; drawn < first + count; drawn += LoadBatchRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rows = (int)Math.Min(LoadBatchRows, first + count - drawn);
                batch?.Dispose();
                arena.Reset();
                batch = RecordBatch.Over(arena, columns.Fill(arena, origin, step, drawn, rows), drawn, batch);
                yield return batch;
            }
        }
        finally
        {
            batch?.Dispose();
            arena.Reset();
        }
    }

    /// <summary>
    /// Holds <see cref="RequestColumns"/> to the requests <see cref="RequestGenerator.Next"/> draws of the same seed, read
    /// back as records: every column of every row, the instants included, before a load writes a row drawn so.
    /// </summary>
    private static void CheckColumns(DType row, TimeUnit unit, long perHour)
    {
        const int Rows = 20_000;
        const int Hour = 7;
        RequestGenerator records = new RequestGenerator(1_000 + Hour, Hour * perHour);
        CanonicalArena arena = new CanonicalArena();
        try
        {
            long origin = TimeSlicer.InstantOf(QuestionBench.Start.AddHours(Hour), unit);
            double step = (double)TimeSlicer.Units(TimeSpan.FromHours(1), unit) / perHour;
            int root = new RequestColumns(new RequestGenerator(1_000 + Hour, Hour * perHour), row).Fill(arena, origin, step, 0, Rows);
            using RecordBatch batch = RecordBatch.Over(arena, root, 0, null);
            GeneratedRequest[] read = new GeneratedRequest[Rows];
            GeneratedRequest.ReadRows(batch.As<GeneratedRequest>(), read);
            for (int i = 0; i < Rows; i++)
            {
                GeneratedRequest expected = records.Next(TimeSlicer.TimeOf(origin + (long)(i * step), unit));
                if (read[i] != expected)
                {
                    throw new InvalidOperationException($"Row {i} drawn into columns reads {read[i]}, where the generator draws {expected}.");
                }
            }
        }
        finally
        {
            arena.Reset();
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
