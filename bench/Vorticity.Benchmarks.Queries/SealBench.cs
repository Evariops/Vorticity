using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What the compaction of a period costs at the time-window design's scale (<c>--micro seal</c>): the first
/// <c>--minutes</c> minutes of an hour of requests at <c>--rate</c> a second, appended in parts of
/// <c>--part-minutes</c>, then a few rows past the hour and its lateness, which close it; the jobs then due run
/// until none is, each timed with its passes (the spans <c>vortex.dataset.compaction.plan</c> and
/// <c>.write</c>), and the cores the process kept busy meanwhile. The store and the scratch lie in a directory
/// of their own under <c>--store</c>, on the disk the design simulates its object store on, removed after.
/// </summary>
internal static class SealBench
{
    internal static async Task<int> Run(string root, int minutes, double rate, int partMinutes, int ceiling, int writeDegree, int budget, bool lateRows = true)
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
                Periods = new PeriodOptions { Lateness = TimeSpan.FromMinutes(5), Length = SliceWidth.Hour },
                Identifiers = [nameof(GeneratedRequest.RequestId)],
                Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(budget), ScratchDirectory = scratch },
            });

            // The hour's parts, appended in order, and a few rows past the hour and its lateness, which close it.
            long perHour = (long)(rate * 3_600);
            long begin = Stopwatch.GetTimestamp();
            foreach (ObjectDraft draft in await DesignBuild.WriteHourAsync(dataset, 0, perHour, partMinutes, minutes, lateRows))
            {
                await using (draft)
                {
                    await dataset.AppendAsync(draft);
                }
            }

            RequestGenerator generator = new RequestGenerator(9_999, perHour);
            GeneratedRequest[] tail = new GeneratedRequest[1_000];
            for (int i = 0; i < tail.Length; i++)
            {
                tail[i] = generator.Next(QuestionBench.Start.AddMinutes(66).AddMilliseconds(i));
            }

            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<GeneratedRequest>(tail);
                await dataset.AppendAsync(draft);
            }

            double written = Stopwatch.GetElapsedTime(begin).TotalSeconds;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"{perHour * Math.Min(minutes, 60) / 60:N0} rows of an hour in parts of {partMinutes} minutes, generated and appended in {written:F1} s"));
            spans.Clear();
            CompactionPlan plan = await dataset.PlanCompactionAsync(new CompactionOptions { LevelZeroCeiling = ceiling });
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"plan: style {plan.Style}, objects by level [{string.Join(", ", plan.ObjectsByLevel)}], lag {plan.Lag}, job {plan.Job?.Trigger.ToString() ?? "none"}"));
            using Process self = Process.GetCurrentProcess();
            TimeSpan cpu = self.TotalProcessorTime;
            begin = Stopwatch.GetTimestamp();
            int jobs = 0;
            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = ceiling }) is not null)
            {
                jobs++;
            }

            double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
            self.Refresh();
            double busy = (self.TotalProcessorTime - cpu).TotalSeconds;
            foreach ((string name, string detail, double milliseconds) in spans)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name[("vortex.dataset.".Length)..],-24} {milliseconds,10:F0} ms  {detail}"));
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{jobs} jobs in {seconds:F1} s, {busy / seconds:F1} cores busy of {Environment.ProcessorCount}, a peak of {self.PeakWorkingSet64 / 1e9:F2} GB in memory"));
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
