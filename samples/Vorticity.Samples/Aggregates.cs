using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace Vorticity.Samples;

internal static class Aggregates
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        await BestOfThreeAsync("AggAsync, four answers", async () =>
        {
            Scan<Reading> recent = file.Scan<Reading>().Where(r => r.Day >= 900);
            (double? min, double? max, long n, long cities) = await recent
                .AggAsync(a => (a.Min(r => r.Celsius), a.Max(r => r.Celsius), a.Count(), a.CountDistinct(r => r.City)));
            return $"min {min}, max {max}, {n} rows, {cities} cities; {recent.Statistics.BlocksDecoded} blocks decoded";
        });

        string[] lines = [];
        await BestOfThreeAsync("GroupBy(City)", async () =>
        {
            lines = new string[Demo.Cities.Length];
            int line = 0;
            Aggregation<(string, double?, WelfordState)> byCity = file.Scan<Reading>()
                .GroupBy(r => r.City)
                .Select(g => (g.Key, g.Average(r => r.Celsius), g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius)));
            await foreach ((string city, double? mean, WelfordState state) in byCity)
            {
                lines[line++] = $"  {city,-10} mean {mean:F4}  variance {state.Variance:F4}";
            }

            return $"{line} groups";
        });

        foreach (string line in lines)
        {
            Console.WriteLine(line);
        }

        await BestOfThreeAsync("Welford(Celsius), encoded", async () =>
        {
            Scan<Reading> scan = file.Scan<Reading>();
            return Describe(await scan.AggregateAsync<double, Welford<double>, WelfordState>(r => r.Celsius), scan);
        });

        await BestOfThreeAsync("Welford(Celsius), canonical", async () =>
        {
            Scan<Reading> scan = file.Scan<Reading>();
            return Describe(await scan.AggregateAsync<double, CanonicalWelford<double>, WelfordState>(r => r.Celsius), scan);
        });

        await BestOfThreeAsync("Welford(Day), encoded", async () =>
        {
            Scan<Reading> scan = file.Scan<Reading>();
            return Describe(await scan.AggregateAsync<int, Welford<int>, WelfordState>(r => r.Day), scan);
        });

        await BestOfThreeAsync("Welford(Day), canonical", async () =>
        {
            Scan<Reading> scan = file.Scan<Reading>();
            return Describe(await scan.AggregateAsync<int, CanonicalWelford<int>, WelfordState>(r => r.Day), scan);
        });

        await BestOfThreeAsync("GroupBy(City, Day), degree 1", () => CompositeAsync(file));

        await using VortexSession parallel = VortexSession.Create(options => options.MaxDegreeOfParallelism = Environment.ProcessorCount);
        await using VortexFile shared = await parallel.OpenAsync(path);
        int degree = parallel.Options.MaxDegreeOfParallelism;
        await BestOfThreeAsync($"GroupBy(City, Day), degree {degree}", () => CompositeAsync(shared));
        await BestOfThreeAsync($"Welford(Celsius), degree {degree}", async () =>
        {
            Scan<Reading> scan = shared.Scan<Reading>();
            return Describe(await scan.AggregateAsync<double, Welford<double>, WelfordState>(r => r.Celsius), scan);
        });

        string visits = await Demo.VisitsAsync();
        await using VortexFile visitFile = await VortexFile.OpenAsync(visits);
        try
        {
            int total = await visitFile.Scan<Visit>().SumAsync(v => v.DurationMs);
            Console.WriteLine($"sum of DurationMs: {total}");
        }
        catch (OverflowException error)
        {
            Console.WriteLine($"SumAsync(v => v.DurationMs): {error.GetType().Name}: {error.Message}");
        }
    }

    private static string Describe(WelfordState s, Scan<Reading> scan) =>
        $"{s.Count} values, mean {s.Mean:F4}, variance {s.Variance:F4}, {scan.Statistics.BlocksDecoded} blocks decoded";

    private static async Task<string> CompositeAsync(VortexFile file)
    {
        long groups = 0;
        (string City, int Day, double Variance) widest = default;
        await foreach (var (city, day, total, state) in file.Scan<Reading>()
            .GroupBy(r => (r.City, r.Day))
            .Select(g => (g.Key.City, g.Key.Day, g.Sum(r => r.Celsius), g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius))))
        {
            groups++;
            if (state.Variance > widest.Variance && total > 0)
            {
                widest = (city, day, state.Variance);
            }
        }

        return $"{groups} groups, the widest spread {widest.City} on day {widest.Day}, variance {widest.Variance:F2}";
    }

    private static async Task BestOfThreeAsync(string name, Func<Task<string>> run)
    {
        string outcome = string.Empty;
        TimeSpan best = TimeSpan.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            Stopwatch clock = Stopwatch.StartNew();
            outcome = await run();
            best = clock.Elapsed < best ? clock.Elapsed : best;
        }

        Console.WriteLine($"{name,-31} {best.TotalMilliseconds,6:F1} ms  {outcome}");
    }
}

/// <summary>A running mean and variance, folded block by block and merged across chunks.</summary>
/// <typeparam name="T">The column's storage type.</typeparam>
public readonly struct Welford<T> : IEncodedAggregator<T, WelfordState>
    where T : unmanaged, INumber<T>
{
    public static WelfordState Seed() => default;

    public static void Step(ref WelfordState s, ReadOnlySpan<T> values, ReadOnlySpan<ulong> validity, Selection rows)
    {
        foreach (int i in rows)
        {
            if (validity.IsEmpty || (validity[i >> 6] >> (i & 63) & 1) != 0)
            {
                s.Add(double.CreateTruncating(values[i]), 1);
            }
        }
    }

    public static void StepDictionary(ref WelfordState s, ReadOnlySpan<uint> codes, ReadOnlySpan<T> dictionary, Selection rows)
    {
        Span<long> weights = dictionary.Length <= 512 ? stackalloc long[dictionary.Length] : new long[dictionary.Length];
        weights.Clear();
        foreach (int i in rows)
        {
            weights[(int)codes[i]]++;
        }

        for (int code = 0; code < dictionary.Length; code++)
        {
            s.Add(double.CreateTruncating(dictionary[code]), weights[code]);
        }
    }

    public static void StepRunEnd(ref WelfordState s, ReadOnlySpan<uint> runEnds, ReadOnlySpan<T> values, Selection rows)
    {
        for (int r = 0, start = 0; r < runEnds.Length; r++)
        {
            int end = (int)runEnds[r];
            s.Add(double.CreateTruncating(values[r]), rows.IsAll ? end - start : Selected(rows.Words, start, end));
            start = end;
        }
    }

    public static void StepConstant(ref WelfordState s, T value, int count) => s.Add(double.CreateTruncating(value), count);

    public static void Merge(ref WelfordState into, in WelfordState other)
    {
        if (other.Count == 0)
        {
            return;
        }

        long count = into.Count + other.Count;
        double delta = other.Mean - into.Mean;
        into.M2 += other.M2 + delta * delta * into.Count * other.Count / count;
        into.Mean += delta * other.Count / count;
        into.Count = count;
    }

    private static int Selected(ReadOnlySpan<ulong> words, int start, int end)
    {
        int count = 0;
        while (start < end)
        {
            int bit = start & 63;
            int take = Math.Min(64 - bit, end - start);
            ulong mask = (take == 64 ? ulong.MaxValue : (1UL << take) - 1) << bit;
            count += BitOperations.PopCount(words[start >> 6] & mask);
            start += take;
        }

        return count;
    }
}

/// <summary>The same fold as <see cref="Welford{T}"/>, handed only the decoded values.</summary>
/// <typeparam name="T">The column's storage type.</typeparam>
public readonly struct CanonicalWelford<T> : IAggregator<T, WelfordState>
    where T : unmanaged, INumber<T>
{
    public static WelfordState Seed() => default;

    public static void Step(ref WelfordState s, ReadOnlySpan<T> values, ReadOnlySpan<ulong> validity, Selection rows) =>
        Welford<T>.Step(ref s, values, validity, rows);

    public static void Merge(ref WelfordState into, in WelfordState other) => Welford<T>.Merge(ref into, in other);
}

/// <summary>What the fold keeps: the count, the mean and the sum of squared deviations.</summary>
public struct WelfordState
{
    public long Count;
    public double Mean;
    public double M2;

    public readonly double Variance => Count > 1 ? M2 / (Count - 1) : double.NaN;

    public void Add(double value, long weight)
    {
        if (weight == 0)
        {
            return;
        }

        long count = Count + weight;
        double delta = value - Mean;
        Mean += delta * weight / count;
        M2 += delta * delta * Count * weight / count;
        Count = count;
    }
}
