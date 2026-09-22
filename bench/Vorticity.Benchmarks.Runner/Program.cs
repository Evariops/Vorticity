using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Vorticity.Bench.Scenarios;

namespace Vorticity.Bench.Runner;

/// <summary>
/// One scenario of the published report, in this process, for the report to spawn and time. It
/// prints the rows it rendered and what the process cost, and nothing else: a parent parses it.
/// </summary>
/// <remarks>
/// With <c>--repeat</c> it runs the scenario that many times and prints a line per round before the
/// last one, which is how a profiler gets enough of a scenario out of one process: the first round
/// is the cold one the report times, with its page faults and the pools' first rents, and the
/// rounds after it are what a process that stays up pays.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        int repeat = 1;
        if (args.Length < 4 || args[0] != "--scenario"
            || !long.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long rows)
            || (args.Length != 4 && (args.Length != 6 || args[4] != "--repeat"
                || !int.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out repeat)
                || repeat < 1)))
        {
            Console.Error.WriteLine(
                "usage: Vorticity.Benchmarks.Runner --scenario <name> <file.vortex> <rows> [--repeat <n>]");
            return 2;
        }

        Func<string, Task<long>>? scenario = ScenarioSet.ForReport(args[1], rows);
        if (scenario is null)
        {
            Console.Error.WriteLine($"no scenario named '{args[1]}'");
            return 2;
        }

        // The rounds are printed once they are all done, so that formatting a line is not an
        // allocation of the round after it.
        long[] delivered = new long[repeat];
        long[] workMicros = new long[repeat];
        long[] allocated = new long[repeat];
        long before = AllocatedSoFar();
        for (int round = 0; round < repeat; round++)
        {
            // The action is timed from inside the process, once it is up: what the report compares
            // is the work, and the process start is a property of the build that the parent times
            // apart.
            long started = Stopwatch.GetTimestamp();
            delivered[round] = await scenario(args[2]).ConfigureAwait(false);
            workMicros[round] = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000);
            long after = AllocatedSoFar();
            allocated[round] = after - before;
            before = after;
        }

        if (repeat > 1)
        {
            for (int round = 0; round < repeat; round++)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"round={round} rows={delivered[round]} work_us={workMicros[round]} allocated_bytes={allocated[round]}"));
            }
        }

        (long cpuMs, long rssBytes) = ProcessCost.Read();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"rows={delivered[^1]} work_us={workMicros[^1]} cpu_ms={cpuMs} rss_bytes={rssBytes}"));
        return 0;
    }

    /// <summary>The bytes this process has allocated so far, on every thread.</summary>
    /// <remarks>
    /// Called once before the first round and once after each, and kept out of line so that it
    /// stays a symbol of its own: a debugger stopping on it knows where a round starts and ends.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long AllocatedSoFar() => GC.GetTotalAllocatedBytes(precise: true);
}
