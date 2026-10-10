# Aggregates

Ask for answers instead of rows: several in one pass, one row per group, with an aggregator of your
own, on as many cores as the session allows.

```csharp
Scan<Reading> recent = file.Scan<Reading>().Where(r => r.Day >= 900);
Summary s = await recent
    .AggregateAsync<Summary>(a => (a.Min(r => r.Celsius), a.Max(r => r.Celsius), a.Count(), a.CountDistinct(r => r.City)));

[VortexRecord]
public partial record struct Summary(double? Min, double? Max, long Rows, long Cities);
```

```
AggregateAsync, four answers       1.9 ms  min 10.1, max 49.9, 100000 rows, 8 cities; 1 blocks decoded
```

## Several answers in one pass

`AggregateAsync` takes a lambda over an `Aggregates<Reading>` and computes all its answers in a single
pass over the rows the scan keeps. One answer comes back as itself, as in
`await scan.AggregateAsync(a => a.Count())`. Several answers, any number of them, go into the record
named by `AggregateAsync<TResult>`, whose members take them in order. Each member has the type of
its answer or its nullable form.

The members of `a` are `Count()`, `CountDistinct`, `Sum`, `Min`, `Max`, `Average`, `Variance`,
`StandardDeviation` and `Aggregate`, each over a column named as in a filter. There are also `Count`,
`Any` and `All` of a predicate, and `Where`, which filters the aggregates that follow it (see
[a filtered group](#a-filtered-group)). `Variance` and `StandardDeviation` are the sample statistics,
divided by `n − 1`, and null below two values.

As with a filter, the lambda runs once and describes the work. Nothing is evaluated per row in your
code. Here the `Where` let the zone maps skip 109 blocks, and the four answers came from the 14 left.
Only one of them had a column brought to its plain form, and the others were read as runs and
dictionaries.

For a single answer, the scan also has its own sinks: `CountAsync`, `AnyAsync`, `MinAsync`,
`MaxAsync`, `SumAsync`, `AverageAsync`, `CountDistinctAsync`, and `AggregateAsync` with an
[aggregator of your own](#an-aggregator-of-your-own). An aggregate runs
block by block on the encoded form, never hands a batch to your code, keeps one state per chunk and
merges the states at the end. [The query design](../design/16-queries.md#92-aggregates-block-by-block)
lists what each encoding lets it skip.

## Group by

```csharp
Scan<CitySpread> byCity = file.Scan<Reading>()
    .GroupBy(r => r.City)
    .Select(g => (g.Key, g.Average(r => r.Celsius), g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius)))
    .As<CitySpread>();
await foreach (CitySpread city in byCity.ToRecordsAsync())
{
    lines[line++] = $"  {city.City,-10} mean {city.Mean:F4}  variance {city.State.Variance:F4}";
}

[VortexRecord]
public partial record struct CitySpread(string City, double? Mean, WelfordState State);
```

```
  Paris      mean 29.9563  variance 133.2439
  Lyon       mean 29.9993  variance 133.1939
  ...
  Lille      mean 30.0009  variance 133.6092
```

`Welford<double>` is the aggregator of [the section below](#an-aggregator-of-your-own): a mean and a
variance in one pass. `g.Key` is the key, and the other members of `g` are those of `Aggregates`.

A selection of one value, such as `Select(g => g.Count())`, is an `Aggregation<long>` that you
enumerate with `await foreach`, one `long` per group. Several values have no .NET type until a record
gives them one. The selection is then an `Aggregation`, and `As<TRecord>()` turns it into a
`Scan<TRecord>` of the result, whose members take the values in order. You read it like a file's
scan: as batches by default, whose columns are borrowed and allocate nothing per group, or as
records through `ToRecordsAsync()`, one record per group. A state is a column like any other.
`WelfordState` is a `[VortexRecord]`, three columns in one.

A composite key is a tuple of columns of any length, and its names become the key's, as in
`g.Key.City` and `g.Key.Day`:

```csharp
await foreach (var (city, day, total, state) in file.Scan<Reading>()
    .GroupBy(r => (r.City, r.Day))
    .Select(g => (g.Key.City, g.Key.Day, g.Sum(r => r.Celsius), g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius)))
    .As<CityDaySpread>()
    .ToRecordsAsync())
```

The same query in query syntax builds the same plan:

```csharp
(from r in file.Scan<Reading>()
 group r by (r.City, r.Day) into g
 select (g.Key.City, g.Key.Day, g.Sum(x => x.Celsius)))
.As<CityDayTotal>()
```

The result's scan is a scan like any other. A `Where` filters its batches, a `GroupBy` aggregates it
again (the daily totals from the hourly ones), `CountAsync` and the other single answers read it, and
a writer writes it with `writer.WriteAsync(result)`, a batch at a time.

Without an `OrderBy`, the order of the groups is not defined. On one lane they come in the order
their keys were first met, and on several lanes part by part of the key space, cut the same way by
every run of a query in one process. A group by on a key the statistics say is sorted delivers its
groups in key order as they close ([queries.md](queries.md)). When your code relies on an order, ask
for it with `OrderBy`. A null key is a group of its own. Memory follows the number of groups, 8 000
for this composite key, not the number of rows.

## A filtered group

`g.Where(r => p)` is the subset of the group's rows where `p` is true, with every aggregate
available. It is SQL's `FILTER (WHERE …)`, computed beside the aggregates of the whole group in the
same pass.

```csharp
await foreach (CityHeat city in file.Scan<Reading>()
    .GroupBy(r => r.City)
    .Select(g => (
        g.Key,
        g.Count(),
        g.Count(r => r.Celsius > 45.0),
        g.Where(r => r.Celsius > 45.0).Average(r => r.Celsius),
        g.Any(r => r.Celsius >= 49.9),
        g.All(r => r.Celsius >= 10.0)))
    .As<CityHeat>()
    .ToRecordsAsync())

[VortexRecord]
public partial record struct CityHeat(string City, long Rows, long Hot, double? HotMean, bool AnyTop, bool AllWarm);
```

```
  Paris      15001 of 125006 above 45, mean 47.50, any at 49.9 False, all at 10 or more False
  Lyon       15352 of 125000 above 45, mean 47.46, any at 49.9 True, all at 10 or more False
  ...
```

`g.Count(r => p)` is shorthand for `g.Where(r => p).Count()`. `g.Any(r => p)` asks whether `p` holds
for some row of the group, and `g.All(r => p)` whether it holds for every row. The predicate uses the
language of a scan's `Where`, and a row counts where it is true, not where it is unknown. Every
temperature present is above 10, but one in fifty is missing, so `All` is false. Two `Where` calls
keep the rows both keep. In `AggregateAsync`, `a.Where`, `a.Count(p)`, `a.Any` and `a.All` do the same
over the scan's rows.

Each predicate is evaluated once per batch, however many aggregates read it, and its columns join
the pass. On a million requests grouped by endpoint, the count, the failures and their mean latency
took 3.4 ms in one pass, against 4.6 ms for the failures alone filtered before the group by. A
filter before the group by is still the right choice when the other rows are not wanted and the zone
maps can skip blocks with it.

## Chosen rows

`g.First()` and `g.Last()` are the group's first and last rows in file order. `g.MinBy(r => e)` and
`g.MaxBy(r => e)` are the rows holding the smallest and the largest value of `e`. Each one is a probe
whose columns are results of the group:

```csharp
.Select(g => (g.Key, g.First().Celsius, g.Max(r => r.Celsius), g.MaxBy(r => r.Celsius).Day, g.Last().Celsius))
.As<CityHottest>()

[VortexRecord]
public partial record struct CityHottest(string City, double? First, double? Hottest, int? HottestDay, double? Last);
```

```
  Paris      first , hottest 49.8 on day 0, last 49.8
  Lyon       first 10.7, hottest 49.9 on day 0, last 49.9
  ...
```

File order is the order in which the rows the scan keeps lie in the file, whatever its `OrderBy`, and
it is the same at every degree of parallelism. Among equal values, `MinBy` and `MaxBy` take the first
row, and a null or a NaN is never chosen. Paris's first row has no temperature, so its `First` is
null: `First()` is a row, not the first value. Columns read from one chosen row come from the same
row, so `g.MaxBy(r => r.Celsius).Day` and `.City` describe the same reading. A group with no
candidate, every value null, reads null where the member is nullable and the type's default where it
is not.

The pass keeps each group's chosen row as a position and the value it was chosen by. The columns
read from those rows are fetched once the groups are known, by position, and only those rows are
decoded where the encoding allows it. This happens for the groups the result delivers, or, when a
`Where` or an `OrderBy` after the group by compares one of these columns, for the groups that reach
it. So an `OrderByDescending` with `Take(10)` reads ten rows whatever the number of groups, and the
first and last rows of a day share one read. A chosen row's column is a result: a `Where` or an
`OrderBy` after the group by can compare it, but an aggregate cannot read it.

## Buckets of time and of numbers

```csharp
TimeZoneInfo paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
DateTime second = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
Scan<Visit> day = visitFile.Scan<Visit>().Where(v => v.StartedAt.Truncate(CalendarUnit.Day) == second);
await foreach (VisitHour hour in day
    .GroupBy(v => v.StartedAt.Truncate(CalendarUnit.Hour, paris))
    .Select(g => (g.Key, g.Count(), g.Average(v => v.DurationMs)))
    .As<VisitHour>()
    .ToRecordsAsync())

[VortexRecord]
public partial record struct VisitHour(DateTime Hour, long Visits, double? MeanMs);
```

```
GroupBy(StartedAt, by hour)        0.7 ms  24 hours; 5 blocks decoded
  2026-09-02 02:00 Paris  1200 visits, mean 29400 ms
  2026-09-02 03:00 Paris  1200 visits, mean 30600 ms
  ...
```

`Truncate(unit)` returns the start of the `Minute`, `Hour`, `Day`, `Week` (starting on Monday),
`Month`, `Quarter` or `Year` that holds an instant or a date. `Truncate(unit, zone)` does the same on
a time zone's calendar. `Bucket(width)` returns the bucket of a `TimeSpan` that holds an instant,
counted from 1970-01-01. Each of these is a column of the column's type, usable anywhere a column is:
as a key, in a filter, as an aggregate's input, in a selection.

A `DateTime` is truncated as it is stored, here in UTC, so the day is the UTC day, and its hours on
Paris's calendar start at 02:00 in Paris. On a zone's calendar, each instant takes the offset of its
interval, so the hour repeated in autumn appears as two hours, and that day has 25. A
`DateTimeOffset` read from a zoned column is truncated on its column's calendar. A null stays null.

The filter is not evaluated through the function. A comparison of a function with a constant becomes
the range of the underlying column, here `StartedAt >= 2026-09-02 && StartedAt < 2026-09-03`, so the
zone maps, the statistics and a sorted column prune, count and locate it exactly as they would the
range written by hand. The scan decoded only the 5 blocks out of 13 that hold that day. The instants
are sorted, so their hours are too, and the group by streams an hour at a time.

When every aggregate is a count, a minimum or a maximum, a block whose rows all fall in one bucket is
not read at all. Its zone map gives the bucket, the row count and the extremes of its columns, and
the group by decodes only the blocks that straddle a boundary. On a log of a million requests, one
per second, the days with their count, their fastest and their slowest request took 1.3 ms, against
4.1 ms when every block is read. Under a filter, a block is answered from its zone map when the zone
maps prove the filter keeps it whole.

On a number, `Bucket(width)` computes `⌊v / w⌋ × w`, in integers for an integer column and rounded to
the column's type for a float:

```csharp
.GroupBy(v => v.DurationMs.Bucket(15_000))
.Select(g => (g.Key, g.Count()))
.As<DurationBucket>()
```

```
  from      0 ms  25000 visits
  from  15000 ms  15000 visits
  ...
  from  75000 ms  15000 visits
```

A comparison with a float's bucket, an `In` over a function and a comparison of two functions are
evaluated through the function, and prune with the zone bounds mapped through it. Buckets of a
decimal column are not supported yet and throw `NotSupportedException`. A function has no key source
of its own, so it cannot be the key of a scan's `OrderBy` or of `Keys`.

## An aggregator of your own

```csharp
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
}
```

The sample holds the rest: `StepDictionary`, which counts the rows per code and adds each distinct
value once with its weight, the `Selected` popcount helper, and `WelfordState`, a count, a mean and a
sum of squared deviations with a weighted `Add`.

Every member is static, so the scan calls them monomorphised, without a virtual call per value. `Step`
receives a block in its plain form: the values, the validity words and the rows that passed the
filter, and it must honour both. An `IAggregator` only has `Step`, `Seed` and `Merge`, and forces the
decode of the column it reads. An `IEncodedAggregator` adds a step per encoded form, called with the
nulls already removed from `rows`. `Merge` joins the states of two chunks, which is what makes a
parallel run correct. `T` must be exactly the column's storage type, `int` for an `i32` column and
`double` for `f64`, and any other type is refused with `VortexSchemaException`.

What the encoded steps are worth, against `CanonicalWelford<T>`, the same fold with `Step` only:

```
Welford(Celsius), encoded          6.7 ms  980000 values, mean 30.0000, variance 133.2501, 123 blocks decoded
Welford(Celsius), canonical        6.6 ms  980000 values, mean 30.0000, variance 133.2501, 123 blocks decoded
Welford(Day), encoded              0.2 ms  1000000 values, mean 499.5000, variance 83333.3333, 0 blocks decoded
Welford(Day), canonical            3.8 ms  1000000 values, mean 499.5000, variance 83333.3333, 123 blocks decoded
```

`Day` is stored as runs, so `StepRunEnd` sees 1 121 runs instead of a million values and runs more
than ten times faster. `Celsius` is a dictionary whose distinct values include the null, and such a
block is handed to `Step` decoded, so the two variants cost the same
([encoded-forms.md](encoded-forms.md)). `BlocksDecoded` counts the blocks where a column reached its
plain form: every block of `Celsius`, and none of `Day`.

## On more cores

```csharp
await using VortexSession parallel = VortexSession.Create(options => options.MaxDegreeOfParallelism = Environment.ProcessorCount);
await using VortexFile shared = await parallel.OpenAsync(path);
```

```
GroupBy(City, Day), degree 1      19.2 ms  8000 groups, the widest spread Lille on day 13, variance 144.07
GroupBy(City, Day), degree 14      6.3 ms  8000 groups, the widest spread Lille on day 13, variance 144.07
Welford(Celsius), degree 14        1.5 ms  980000 values, mean 30.0000, variance 133.2501, 123 blocks decoded
```

Parallelism comes from the session and is 1 by default, because a library should not take a host's
cores without being asked. With it, chunks aggregate concurrently, one state per group per chunk,
and `Merge` joins them. The answers are identical, 3 times faster for the composite group by on 14
cores and 4.5 times for the Welford fold. A group by whose key streams keeps streaming on every lane:
ranges of rows are grouped side by side, each following the one before it in row order, so the
groups still come out in key order as they close, the first ones as soon as the first block is
grouped, and memory stays at the ranges in flight. `ScanOptions.DegreeOfParallelism` overrides the
session for one scan ([threads.md](threads.md)).

The first groups of an order on the key cost only what they keep. `OrderBy(g => g.Key).Take(10)`
keeps, on each lane, the ten best keys met so far and trims the rest as others arrive, and a row
whose integer key lies beyond them is not grouped at all. A key of a million values is held ten
groups per lane at a time. On a sorted key, `OrderByDescending(g => g.Key).Take(7)` reads the file's
chunks from the last one backwards and stops once the seventh group is out. A top on an aggregate,
such as `OrderByDescending(g => g.Count()).Take(10)`, holds every group until the end, because a
count is only known then.

## Decimals at any precision

A decimal column sums exactly, whatever its precision and its row count. The total is kept in 320
bits, spilled from a 128-bit or 256-bit total only when that would overflow, so a column alternating
the largest 76-digit value and its opposite sums to exactly what it should. The sum is converted
once, at the end:

```csharp
decimal total = await file.Scan<Invoice>().SumAsync(i => i.Amount);          // decimal(18, 2): as decimal
VortexDecimal wide = await file.Scan<Ledger>().SumAsync(l => l.Balance);     // decimal(76, 10): as VortexDecimal
double? mean = await file.Scan<Ledger>().AverageAsync(l => l.Balance);
```

A sum returned as `VortexDecimal` carries 38 digits while it fits them, like a SQL sum of a narrower
decimal, and 76 beyond that. Past 76 digits it throws `OverflowException`, just as a `decimal` sum
does past 96 bits. An `Int128`, a `UInt128` or a `BigInteger` member sums as its own type, and the
`BigInteger` sum never throws. `MinAsync` and `MaxAsync` order decimals as the numbers they are,
group keys may be 256-bit decimals, and every aggregate reads the column where it lies, dictionary
and run-end blocks included.

## Watch out

* A sum widens what could overflow. An `sbyte`, `short` or `int` column sums as a `long`, a `byte`,
  `ushort` or `uint` column as a `ulong`, and a `Half` or `float` column as a `double`. So
  `SumAsync(v => v.DurationMs)` over the `int` column of the visits file returns a `long`. A `long`,
  a `ulong` or a `decimal` column sums as itself, exactly, and throws `OverflowException` only when
  the exact total does not fit. Internally the engine keeps 64 bits per group when the file
  statistics prove the rows times the column's largest value fit, and 128 bits otherwise.
* The file carries no sum. `MinAsync`, `MaxAsync` and `CountAsync` without a filter are answered
  from the file statistics, while `SumAsync` and `AverageAsync` read the column
  ([scan-a-table.md](scan-a-table.md)).
* NaN is skipped by a float sum, mean, minimum and maximum, and counts as one value in a distinct
  count. An aggregation ignores the scan's `OrderBy`.
* A float sum gives the same bits at every degree, in every order of the rows and however the data
  is cut into files or objects, and it is closer to the exact sum than a plain loop. Each value is
  split into three integer parts on a grid of exponents, those parts are summed exactly, and the total
  is rounded once. It accepts 2^36 values, about 69 billion, in one group or one scan, and throws
  `OverflowException` beyond that. Grouped row by row it costs about a fifth more than a plain sum,
  and nothing measurable over runs and sorted keys.
* Encoded steps need generic instantiation at run time. Under Native AOT, the scan calls `Step` with
  the plain form instead ([native-aot.md](native-aot.md)).
* The first passes of a process run on code the JIT has not optimized yet. Tiered compilation starts
  every method unoptimized and recompiles the busy ones, so the best of three passes on one lane is a
  fifth to a half slower than with everything optimized from the start: 12.4 ms against 10.2 for
  `GroupBy(City)`, 19.2 against 12.7 for `GroupBy(City, Day)`. The gap grows with the lanes. On
  group bys of one to ten million keys at fourteen lanes, the first three passes took 1.6 to 3.8
  times as long as the same code compiled ahead of time. The library cannot choose this for its host,
  but the host can: `<TieredCompilation>false</TieredCompilation>` optimizes every method on its
  first call at the cost of a slower start, and Native AOT leaves nothing to warm up.

The figures come from the sample on the demonstration file of a million rows, on a 14-core machine.
Each timing is the best of three passes in a process and the median of seven processes. From one
process to the next they vary by a fifth or more.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- aggregates
```
