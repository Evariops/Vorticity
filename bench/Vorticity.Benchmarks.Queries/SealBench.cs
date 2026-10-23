using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What the compaction of a period costs at the time-window design's scale (<c>--micro seal</c>): the first
/// <c>--minutes</c> minutes of each of <c>--hours</c> hours of requests at <c>--rate</c> a second, appended in parts of
/// <c>--part-minutes</c>, the jobs due between two hours run as a dataset fed by its appends runs them; then a few rows
/// past the last hour's period and its lateness, which close it. The jobs then due run until none is, each timed with its
/// passes (the spans <c>vortex.dataset.compaction.plan</c> and <c>.write</c>) and the most private bytes the process held
/// while it ran, and the cores the process kept busy meanwhile. The periods are hours, or days with <c>--day</c>. The
/// store and the scratch lie in a directory of their own under <c>--store</c>, on the disk the design simulates its object
/// store on, removed after.
/// </summary>
internal static class SealBench
{
    internal static async Task<int> Run(
        string root, int minutes, double rate, int partMinutes, int ceiling, int writeDegree, int budget, bool lateRows = true, int hours = 1, bool days = false)
    {
        string directory = Path.Combine(root, $"seal-{Guid.NewGuid():N}");
        string scratch = Path.Combine(directory, "scratch");
        Directory.CreateDirectory(scratch);
        ConcurrentQueue<(string Name, string Detail, double Milliseconds)> spans = new ConcurrentQueue<(string, string, double)>();
        using ActivityListener listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == VortexDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName.StartsWith("vortex.dataset.compaction", StringComparison.Ordinal))
                {
                    spans.Enqueue((activity.OperationName, Describe(activity), activity.Duration.TotalMilliseconds));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = writeDegree);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(new FileObjectStore(Path.Combine(directory, "store")), GeneratedRequest.Schema, new DatasetOptions
            {
                Session = session,
                ClusteringKey = [nameof(GeneratedRequest.At)],
                Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = days ? SliceWidth.Day : SliceWidth.Hour },
                Identifiers = [nameof(GeneratedRequest.RequestId)],
                Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(budget), ScratchDirectory = scratch },
            });

            // Each hour's parts, appended in order, the jobs due run before the next hour's; then a few rows past the
            // last hour's period and its lateness, which close it.
            long perHour = (long)(rate * 3_600);
            long begin = Stopwatch.GetTimestamp();
            int between = 0;
            long betweenPeak = 0;
            for (int hour = 0; ; hour++)
            {
                foreach (ObjectDraft draft in await DesignBuild.WriteHourAsync(dataset, hour, perHour, partMinutes, minutes, lateRows))
                {
                    await using (draft)
                    {
                        await dataset.AppendAsync(draft);
                    }
                }

                if (hour == hours - 1)
                {
                    break;
                }

                while (true)
                {
                    using PeakMemory memory = new PeakMemory();
                    if (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = ceiling }) is null)
                    {
                        break;
                    }

                    between++;
                    betweenPeak = Math.Max(betweenPeak, memory.Peak);
                }
            }

            RequestGenerator generator = new RequestGenerator(9_999, perHour);
            GeneratedRequest[] tail = new GeneratedRequest[1_000];
            DateTimeOffset closing = (days ? QuestionBench.Start.AddDays(Math.Ceiling(hours / 24.0)) : QuestionBench.Start.AddHours(hours)).AddMinutes(6);
            for (int i = 0; i < tail.Length; i++)
            {
                tail[i] = generator.Next(closing.AddMilliseconds(i));
            }

            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<GeneratedRequest>(tail);
                await dataset.AppendAsync(draft);
            }

            double written = Stopwatch.GetElapsedTime(begin).TotalSeconds;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{hours * perHour * Math.Min(minutes, 60) / 60:N0} rows of {hours} hour(s) in parts of {partMinutes} minutes, generated and appended in {written:F1} s"));
            if (between > 0)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture, $"{between} jobs run between the hours, {betweenPeak / 1e9:F2} GB private at most"));
            }

            spans.Clear();
            CompactionPlan plan = await dataset.PlanCompactionAsync(new CompactionOptions { LevelZeroCeiling = ceiling });
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"plan: style {plan.Style}, objects by level [{string.Join(", ", plan.ObjectsByLevel)}], lag {plan.Lag}, job {plan.Job?.Trigger.ToString() ?? "none"}"));
            using Process self = Process.GetCurrentProcess();
            TimeSpan cpu = self.TotalProcessorTime;
            begin = Stopwatch.GetTimestamp();
            int jobs = 0;
            long peak = 0;
            while (true)
            {
                // Each job's spans, then the private bytes the process held when it started and the most it held while it ran.
                long first;
                long held;
                long native;
                long parked;
                long committed;
                using (PeakMemory memory = new PeakMemory())
                {
                    if (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = ceiling }) is null)
                    {
                        break;
                    }

                    (first, held, native, parked, committed) = (memory.First, memory.Peak, memory.PeakNative, memory.PeakParked, memory.PeakCommitted);
                }

                jobs++;
                peak = Math.Max(peak, held);
                while (spans.TryDequeue(out (string Name, string Detail, double Milliseconds) span))
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {span.Name[("vortex.dataset.".Length)..],-24} {span.Milliseconds,10:F0} ms  {span.Detail}"));
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {"private bytes",-24} {first / 1e9,10:F2} GB at the start, {held / 1e9:F2} GB at most, of which {native / 1e9:F2} GB native blocks ({parked / 1e9:F2} GB parked) and {committed / 1e9:F2} GB the collector's"));
            }

            double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
            self.Refresh();
            double busy = (self.TotalProcessorTime - cpu).TotalSeconds;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{jobs} jobs in {seconds:F1} s, {busy / seconds:F1} cores busy of {Environment.ProcessorCount}, {peak / 1e9:F2} GB private at most in a job, a peak of {self.PeakWorkingSet64 / 1e9:F2} GB in memory"));

            // What the jobs wrote: each object's slices and the order inside them.
            await foreach (PositionedObject held in dataset.Snapshot.WalkAsync(null, 0, long.MaxValue, null, default))
            {
                await using ObjectLease lease = await dataset.RentAsync(held.Entry, default);
                if (await SliceTable.ReadAsync(lease.File, default) is not { } table)
                {
                    continue;
                }

                string order = string.Join(", ", table.Order.Select(
                    entry => entry >= 0 && entry < lease.File.DType.FieldCount ? lease.File.DType.GetFieldName(entry) : entry.ToString(CultureInfo.InvariantCulture)));
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  object of {held.Entry.Rows:N0} rows, {held.Entry.Bytes:N0} bytes: {table.Slices.Count} slices of {table.Slices.Min(s => s.Rows):N0} to {table.Slices.Max(s => s.Rows):N0} rows, order [{order}]"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return 0;
    }

    /// <summary>A compaction span's tags that say what it did: its trigger, its levels, its rows.</summary>
    private static string Describe(Activity activity)
    {
        List<string> tags = [];
        foreach (string name in (string[])["vortex.trigger", "vortex.level.from", "vortex.level.to", "vortex.objects_in", "vortex.rows", "vortex.bytes_out"])
        {
            if (activity.GetTagItem(name) is { } value)
            {
                tags.Add($"{name["vortex.".Length..]}={value}");
            }
        }

        return string.Join(' ', tags);
    }
}
