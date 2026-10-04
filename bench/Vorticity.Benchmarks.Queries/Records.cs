using System;
using System.Numerics;

namespace Vorticity.Benchmarks.Queries;

/// <summary>A reading, as the samples write them: the day in order, a temperature missing one row in fifty, a city in runs of seven.</summary>
[VortexRecord]
public partial record struct Reading(int Day, double? Celsius, string City);

/// <summary>A request of a log sorted by time: an endpoint from a dictionary, a user among a million, a status, a duration.</summary>
[VortexRecord]
public partial record struct Request(DateTime At, int UserId, string Endpoint, int Status, int DurationMs, double Latency);

/// <summary>A running mean and variance, the aggregator of the guide's aggregates page.</summary>
/// <typeparam name="T">The column's storage type.</typeparam>
public readonly struct Welford<T> : IEncodedAggregator<T, WelfordState>
    where T : unmanaged, INumber<T>
{
    public static WelfordState Seed() => default;

    public static void Step(ref WelfordState s, ReadOnlySpan<T> values, ReadOnlySpan<ulong> validity, Selection rows)
    {
        foreach (int i in rows)
        {
            if (validity.IsEmpty || ((validity[i >> 6] >> (i & 63)) & 1) != 0)
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
        into.M2 += other.M2 + (delta * delta * into.Count * other.Count / count);
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

/// <summary>What the fold keeps: the count, the mean and the sum of squared deviations.</summary>
[VortexRecord]
public partial struct WelfordState
{
    public long Count;
    public double Mean;
    public double M2;

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

/// <summary>A city's rows and their mean.</summary>
[VortexRecord]
public partial record struct CityStats(string City, long Count, double? Mean);

/// <summary>A day's rows and their mean.</summary>
[VortexRecord]
public partial record struct DayStats(int Day, long Count, double? Mean);

/// <summary>A city's day: its rows and their mean.</summary>
[VortexRecord]
public partial record struct CityDayStats(string City, int Day, long Count, double? Mean);

/// <summary>An endpoint's requests and their mean latency.</summary>
[VortexRecord]
public partial record struct EndpointStats(string Endpoint, long Count, double? Mean);

/// <summary>A user's requests and their mean latency.</summary>
[VortexRecord]
public partial record struct UserStats(int User, long Count, double? Mean);
