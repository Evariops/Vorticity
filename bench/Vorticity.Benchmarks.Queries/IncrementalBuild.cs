using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A dataset built the way a client's ingestion builds one: each hour's rows drawn in time order and written as objects
/// of some minutes each, several hours at once, then appended hour after hour and compacted as they come, so that the
/// past days lie in objects of their own and the last hours still in level 0 and the levels above it. What <c>--qp</c>
/// builds the client's spans and log records with.
/// </summary>
internal static class IncrementalBuild
{
    /// <summary>
    /// <paramref name="hours"/> hours of <paramref name="perHour"/> rows each, the rows of an hour drawn by the generator
    /// <paramref name="hourly"/> gives for it, <paramref name="parallel"/> hours written at once, objects of
    /// <paramref name="partMinutes"/> minutes; the rows written.
    /// </summary>
    internal static async Task<long> FillAsync<TRecord>(
        IObjectStore store, DatasetOptions options, int hours, long perHour, int parallel, int partMinutes, Func<int, Func<DateTimeOffset, TRecord>> hourly)
        where TRecord : IVortexRecord<TRecord>
    {
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, TRecord.Schema, options);
        Task<List<ObjectDraft>>[] written = new Task<List<ObjectDraft>>[hours];
        int next = 0;
        int appended = 0;
        void Start()
        {
            while (next < hours && next < Volatile.Read(ref appended) + parallel)
            {
                int hour = next++;
                Func<DateTimeOffset, TRecord> draw = hourly(hour);
                written[hour] = Task.Run(() => WriteHourAsync(dataset, hour, perHour, partMinutes, draw));
            }
        }

        long begin = Stopwatch.GetTimestamp();
        long hourBegan = begin;
        Start();
        for (int hour = 0; hour < hours; hour++)
        {
            foreach (ObjectDraft draft in await written[hour])
            {
                await using (draft)
                {
                    await dataset.AppendAsync(draft);
                }
            }

            Volatile.Write(ref appended, hour + 1);
            Start();
            long compacting = Stopwatch.GetTimestamp();
            int jobs = 0;
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }) is not null)
            {
                jobs++;
            }

            // Each hour's own pace and what its compactions took beside the cumulative pace, so that a pace that wanes as
            // the dataset grows shows by the hour.
            long now = Stopwatch.GetTimestamp();
            double seconds = Stopwatch.GetElapsedTime(begin, now).TotalSeconds;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  hour {hour + 1} of {hours}: {perHour / Stopwatch.GetElapsedTime(hourBegan, now).TotalSeconds / 1e6:F2} M rows a second, {jobs} jobs in {Stopwatch.GetElapsedTime(compacting, now).TotalSeconds:F1} s; {(hour + 1) * perHour / seconds / 1e6:F2} M rows a second so far"));
            hourBegan = now;
        }

        await QuestionBench.CompactAsync(dataset);
        using Process self = Process.GetCurrentProcess();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  built in {Stopwatch.GetElapsedTime(begin).TotalSeconds:F0} s: a peak of {self.PeakWorkingSet64 / 1e9:F2} GB in memory, {GC.GetTotalAllocatedBytes() / 1e9:F1} GB allocated"));
        return hours * perHour;
    }

    /// <summary>The objects of one hour, its rows in time order, each object <paramref name="partMinutes"/> minutes of them.</summary>
    private static async Task<List<ObjectDraft>> WriteHourAsync<TRecord>(
        VortexDataset dataset, int hour, long perHour, int partMinutes, Func<DateTimeOffset, TRecord> draw)
        where TRecord : IVortexRecord<TRecord>
    {
        DateTimeOffset start = QuestionBench.Start.AddHours(hour);
        long perPart = Math.Max(1, perHour * partMinutes / 60);
        double step = 3_600.0 / perHour;
        List<ObjectDraft> drafts = [];
        TRecord[] batch = new TRecord[1 << 16];
        try
        {
            for (long first = 0; first < perHour; first += perPart)
            {
                ObjectDraft draft = dataset.StartObject();
                drafts.Add(draft);
                long last = Math.Min(perHour, first + perPart);
                for (long row = first; row < last;)
                {
                    int count = (int)Math.Min(batch.Length, last - row);
                    for (int i = 0; i < count; i++, row++)
                    {
                        batch[i] = draw(start.AddSeconds(row * step));
                    }

                    await draft.Writer.WriteAsync<TRecord>(batch.AsSpan(0, count));
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
}
