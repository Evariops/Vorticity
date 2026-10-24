using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vorticity.Aggregating;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The grid of a distribution weighed (<c>--micro sketches</c>): for entries of a thousand latencies,
/// the buckets each grid occupies, the bytes of those sparse buckets, and the time to find a value's
/// bucket over ten million values. Three grids at a relative error near 1 %: the library's log-linear
/// grid found from the double's bits (k = 6, 0.77 %), DDSketch's logarithmic grid at 1 %, and the decimal
/// grid of buckets <c>[d · 10^e, (d + 1) · 10^e)</c>, d from 100 to 999 (0.5 %).
/// </summary>
/// <remarks>
/// The latencies are log-normal around 50 ms over about four decades, as a request's are. A sparse
/// bucket is taken at five bytes, a varint of its index's gap and one of its count, as a run or a table
/// stores it. Run it with <c>DOTNET_TieredCompilation=0</c>.
/// </remarks>
internal static class SketchMicro
{
    private const int Values = 10_000_000;

    private const int Entry = 1_000;

    private const double SparseBytes = 5;

    internal static int Run(int rounds)
    {
        Random random = new Random(7);
        double[] values = new double[Values];
        for (int i = 0; i < values.Length; i++)
        {
            // Log-normal: e^(ln 50 + 1.2 · N(0, 1)), N from Box-Muller.
            double normal = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            values[i] = Math.Exp(Math.Log(50) + (1.2 * normal));
        }

        double gamma = (1 + 0.01) / (1 - 0.01);
        double logGamma = Math.Log(gamma);
        Console.WriteLine($"{"grid",-28} {"buckets/entry",14} {"B/entry",8} {"ns/value",9}");
        Measure("log-linear bits, k = 6", values, rounds, static v => (int)(BitConverter.DoubleToInt64Bits(v) >> 46));
        Measure("DDSketch logarithmic, 1 %", values, rounds, v => (int)Math.Ceiling(Math.Log(v) / logGamma));
        Measure("decimal, d · 10^e", values, rounds, Decimal);
        Distinct(rounds);
        return 0;
    }

    /// <summary>The approximate distinct count's fold: ten million longs hashed and added, a million of them distinct.</summary>
    private static void Distinct(int rounds)
    {
        long[] keys = new long[Values];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = (i * 7919L) % 1_000_000;
        }

        double best = double.MaxValue;
        long estimate = 0;
        for (int round = 0; round < rounds; round++)
        {
            CardinalitySketch sketch = new CardinalitySketch();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < keys.Length; i++)
            {
                sketch.Add(CardinalitySlot.Hash(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<long>(ref keys[i]))));
            }

            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            estimate = sketch.Estimate();
        }

        Console.WriteLine($"{"distinct, 2^14 registers",-28} {"estimate " + estimate,14} {"16 KiB",8} {best * 1e6 / keys.Length,9:F2}");
    }

    /// <summary>The decimal grid's bucket: the exponent of the third significant digit, then the first three digits.</summary>
    private static int Decimal(double value)
    {
        int exponent = (int)Math.Floor(Math.Log10(value)) - 2;
        double scaled = value / Math.Pow(10, exponent);
        int digits = Math.Clamp((int)scaled, 100, 999);
        return ((exponent + 400) * 900) + (digits - 100);
    }

    private static void Measure(string name, double[] values, int rounds, Func<double, int> bucket)
    {
        // The buckets an entry of a thousand values occupies, on average over the entries.
        HashSet<int> occupied = [];
        long total = 0;
        int entries = values.Length / Entry;
        for (int e = 0; e < entries; e++)
        {
            occupied.Clear();
            for (int i = e * Entry; i < (e + 1) * Entry; i++)
            {
                occupied.Add(bucket(values[i]));
            }

            total += occupied.Count;
        }

        double perEntry = (double)total / entries;
        double best = double.MaxValue;
        long sink = 0;
        for (int round = 0; round < rounds; round++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < values.Length; i++)
            {
                sink += bucket(values[i]);
            }

            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        Console.WriteLine($"{name,-28} {perEntry,14:F1} {perEntry * SparseBytes,8:F0} {best * 1e6 / values.Length,9:F2}{(sink == 42 ? "" : "")}");
    }
}
