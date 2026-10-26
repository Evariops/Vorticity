using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What building the client's log records costs (<c>--micro logwrite</c>): <c>--hours</c> hours of them at <c>--rate</c> a
/// second (<see cref="LogGeneratorV2"/>), built as <c>--micro paths --qp</c> builds them, the way a client's ingestion does:
/// written in objects of <c>--part-minutes</c> minutes, four hours at once, appended and compacted hour after hour,
/// clustered by day, each job shredding their attributes. The time, the processors' time, and what each row cost them.
/// The store and its scratch lie in a directory of their own under <c>--store</c>, removed after.
/// </summary>
internal static class LogWriteMicro
{
    internal static async Task<int> Run(string root, int hours, double rate, int partMinutes, int writeDegree, int budget)
    {
        string directory = Path.Combine(root, $"logwrite-{Guid.NewGuid():N}");
        string scratch = Path.Combine(directory, "scratch");
        Directory.CreateDirectory(scratch);
        try
        {
            await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = writeDegree);
            DatasetOptions options = new DatasetOptions
            {
                Session = session,
                ClusteringKey = [nameof(LogRow.At)],
                Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) },
                Write = new VortexWriteOptions { Indexes = IndexPolicy.Auto.WithBudgetPerMille(budget), ScratchDirectory = scratch },
            };

            using Process self = Process.GetCurrentProcess();
            TimeSpan cpu = self.TotalProcessorTime;
            long begin = Stopwatch.GetTimestamp();
            long rows = await IncrementalBuild.FillAsync<LogRow>(
                new FileObjectStore(Path.Combine(directory, "store")), options, hours, (long)(rate * 3_600), 4, partMinutes,
                hour => new LogGeneratorV2(3_000 + hour).Next);
            double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
            self.Refresh();
            double busy = (self.TotalProcessorTime - cpu).TotalSeconds;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{rows:N0} log records built in {seconds:F1} s, {busy:F1} s of the processors: {rows / seconds / 1e3:F0} k rows a second, {busy * 1e9 / rows:F0} ns of a processor a row"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return 0;
    }
}
