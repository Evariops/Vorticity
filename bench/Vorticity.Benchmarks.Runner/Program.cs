using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

using Vorticity.Bench.Scenarios;

namespace Vorticity.Bench.Runner;

/// <summary>
/// One scenario of the published report, in this process, for the report to spawn and time. It
/// prints the rows it rendered and what the process cost, and nothing else: a parent parses it.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 4 || args[0] != "--scenario"
            || !long.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long rows))
        {
            Console.Error.WriteLine("usage: Vorticity.Benchmarks.Runner --scenario <name> <file.vortex> <rows>");
            return 2;
        }

        Func<string, Task<long>>? scenario = ScenarioSet.ForReport(args[1], rows);
        if (scenario is null)
        {
            Console.Error.WriteLine($"no scenario named '{args[1]}'");
            return 2;
        }

        // The action is timed from inside the process, once it is up: what the report compares is
        // the work, and the process start is a property of the build that the parent times apart.
        long started = Stopwatch.GetTimestamp();
        long delivered = await scenario(args[2]).ConfigureAwait(false);
        long workMicros = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000);
        (long cpuMs, long rssBytes) = ProcessCost.Read();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"rows={delivered} work_us={workMicros} cpu_ms={cpuMs} rss_bytes={rssBytes}"));
        return 0;
    }
}
