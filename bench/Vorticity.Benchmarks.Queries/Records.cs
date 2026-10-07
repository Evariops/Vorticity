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

/// <summary>An endpoint's requests, those that failed, and the mean latency of the failures.</summary>
[VortexRecord]
public partial record struct EndpointErrors(string Endpoint, long Count, long Errors, double? ErrorMean);

/// <summary>A user's requests and the time they took.</summary>
[VortexRecord]
public partial record struct UserDurations(int User, long Count, long Duration);

/// <summary>Keys of 4 to a million values and two values to aggregate, stored canonical.</summary>
[VortexRecord]
public partial record struct Draw(int K4, int K100, int K1000, int K100k, int K1M, long Value, double Price);

/// <summary>A key and a value.</summary>
[VortexRecord]
public partial record struct Keyed(int Key, long Value);

/// <summary>A text key and a value.</summary>
[VortexRecord]
public partial record struct Named(string Name, long Value);

/// <summary>An integer key's rows and the sum of their values.</summary>
[VortexRecord]
public partial record struct KeyTotal(int Key, long Count, long Total);

/// <summary>A pair of keys, its rows and the total of their values.</summary>
[VortexRecord]
public partial record struct PairTotal(int First, int Second, long Count, long Total);

/// <summary>A text key's rows.</summary>
[VortexRecord]
public partial record struct NameCount(string Name, long Count);

/// <summary>A user's slowest request and when it came.</summary>
[VortexRecord]
public partial record struct UserTop(int User, double? Slowest, DateTime? At);

/// <summary>A user's slowest request.</summary>
[VortexRecord]
public partial record struct UserSlowest(int User, double? Slowest);

/// <summary>A day of latencies: the first, the highest, the lowest and the last.</summary>
[VortexRecord]
public partial record struct DayBar(DateTime Day, double? Open, double? High, double? Low, double? Close);

/// <summary>A day's readings.</summary>
[VortexRecord]
public partial record struct DayCount(int Day, long Count);

/// <summary>A city's readings and the sum of their days, an integer.</summary>
[VortexRecord]
public partial record struct CityDays(string City, long Count, long Days);

/// <summary>An endpoint's requests and the sum of their durations, an integer.</summary>
[VortexRecord]
public partial record struct EndpointDurations(string Endpoint, long Count, long Duration);

/// <summary>A day's requests, the fastest and the slowest.</summary>
[VortexRecord]
public partial record struct InstantDay(DateTime Day, long Count, int? Fastest, double? Slowest);

/// <summary>An hour's requests and their mean latency.</summary>
[VortexRecord]
public partial record struct InstantHour(DateTime Hour, long Count, double? Mean);

/// <summary>A user's requests and their mean latency.</summary>
[VortexRecord]
public partial record struct UserStats(int User, long Count, double? Mean);

/// <summary>
/// A row of the high-cardinality matrix: a key column per cardinality, from 10³ to 10⁷ values, a key
/// ten rows a value and one a row a value, and two values to aggregate; stored canonical.
/// </summary>
[VortexRecord]
public partial record struct Spread(int K3, int K4, int K5, int K6, int K7, int Tenfold, int Unique, long Value, double Real);

/// <summary>The matrix's keys at a regular stride, identifiers whose sequence field is zero: a key shifted left by 22 bits.</summary>
[VortexRecord]
public partial record struct Strided(long K3, long K4, long K5, long K6, long K7, long Value, double Real);

/// <summary>Keys of a skewed popularity: a Zipf law of exponent 1.1 over a million values, and hot keys that change as the rows go.</summary>
[VortexRecord]
public partial record struct Skews(int Zipf, int Drift, long Value);

/// <summary>Text keys: a URL of about forty bytes among a million, a UUID among a million, a small integer; an integer key of a million values.</summary>
[VortexRecord]
public partial record struct Page(string Url, Guid Id, int Small, int Key, long Value);

/// <summary>A visit: one of ten million users, on one of 365 days, the days in order.</summary>
[VortexRecord]
public partial record struct Visit(int User, int Day);

/// <summary>An integer key's rows.</summary>
[VortexRecord]
public partial record struct KeyCount(int Key, long Count);

/// <summary>An integer key's rows and the mean of a float.</summary>
[VortexRecord]
public partial record struct KeyMean(int Key, long Count, double? Mean);

/// <summary>An integer key's rows, the total and the largest of a value, and the mean of a float.</summary>
[VortexRecord]
public partial record struct KeyFour(int Key, long Count, long Total, double? Mean, long? Largest);

/// <summary>An integer key's rows and the smallest and largest of a value.</summary>
[VortexRecord]
public partial record struct KeyRange(int Key, long Count, long? Least, long? Most);

/// <summary>A group's key and its largest value.</summary>
[VortexRecord]
public partial record struct KeyMost(int Key, long? Most);

/// <summary>An integer key's rows and the standard deviation of a float.</summary>
[VortexRecord]
public partial record struct KeyDeviation(int Key, long Count, double? Deviation);

/// <summary>A long key's rows and the sum of their values.</summary>
[VortexRecord]
public partial record struct LongKeyTotal(long Key, long Count, long Total);

/// <summary>An integer key's least and greatest text.</summary>
[VortexRecord]
public partial record struct KeyTexts(int Key, string? Least, string? Greatest);

/// <summary>A UUID key's rows.</summary>
[VortexRecord]
public partial record struct IdCount(Guid Id, long Count);

/// <summary>A text and a small integer, the pair's rows.</summary>
[VortexRecord]
public partial record struct UrlSmallCount(string Url, int Small, long Count);

/// <summary>A day's distinct users.</summary>
[VortexRecord]
public partial record struct DayUsers(int Day, long Users);
