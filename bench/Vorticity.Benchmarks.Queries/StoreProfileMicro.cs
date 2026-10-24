using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// <c>--micro s3profile</c>: what the store of <c>--profile s3</c> makes a request wait, drawn without waiting for it.
/// The waits' quantiles against the law's, 30 ms at the median and 120 at the 99th percentile; then the turns of 11 000
/// reads of one prefix sent at once, 5 500 a second, the last two seconds later, and a read of another prefix, which
/// waits for none of them.
/// </summary>
internal static class StoreProfileMicro
{
    internal static async Task<int> Run()
    {
        await using LatencyObjectStore store = new LatencyObjectStore(new MemoryObjectStore())
        {
            Latency = TimeSpan.FromMilliseconds(30),
            LatencyP99 = TimeSpan.FromMilliseconds(120),
        };

        const int Draws = 200_000;
        double[] waits = new double[Draws];
        for (int i = 0; i < Draws; i++)
        {
            long now = Stopwatch.GetTimestamp();
            waits[i] = (store.Answered("data/a", read: true) - now) * 1e3 / Stopwatch.Frequency;
        }

        Array.Sort(waits);
        double Quantile(double q) => waits[(int)Math.Min(Draws - 1, q * Draws)];
        double median = Quantile(0.5);
        double p99 = Quantile(0.99);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"waits over {Draws:N0} draws: median {median:F1} ms, 90th {Quantile(0.9):F1}, 99th {p99:F1}, 99.9th {Quantile(0.999):F1}, mean {Mean(waits):F1}"));

        // The turns alone: no wait, 5 500 reads a second a prefix.
        store.Latency = TimeSpan.Zero;
        store.LatencyP99 = TimeSpan.Zero;
        store.ReadsPerSecondPerPrefix = 5_500;
        const int Reads = 11_000;
        long start = Stopwatch.GetTimestamp();
        long last = start;
        for (int i = 0; i < Reads; i++)
        {
            last = store.Answered("data/a", read: true);
        }

        long other = store.Answered("commit/b", read: true);
        double lastSeconds = (last - start) / (double)Stopwatch.Frequency;
        double otherSeconds = (other - start) / (double)Stopwatch.Frequency;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Reads:N0} reads of one prefix at once: the last answered after {lastSeconds:F3} s; a read of another prefix after {otherSeconds * 1e3:F2} ms"));

        bool held = Math.Abs(median - 30) < 1.5 && Math.Abs(p99 - 120) < 6 && Math.Abs(lastSeconds - 2.0) < 0.05 && otherSeconds < 0.01;
        Console.WriteLine(held ? "the profile holds its figures" : "the profile misses its figures");
        return held ? 0 : 1;
    }

    private static double Mean(double[] values)
    {
        double sum = 0;
        foreach (double value in values)
        {
            sum += value;
        }

        return sum / values.Length;
    }
}
