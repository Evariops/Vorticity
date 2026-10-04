# Aggregates

Ask for answers instead of rows: several in one pass, one row per group, with an aggregator of your
own, on as many cores as the session allows.

```csharp
Scan<Reading> recent = file.Scan<Reading>().Where(r => r.Day >= 900);
(double? min, double? max, long n, long cities) = await recent
    .AggAsync(a => (a.Min(r => r.Celsius), a.Max(r => r.Celsius), a.Count(), a.CountDistinct(r => r.City)));
```

```
AggAsync, four answers             2.3 ms  min 10.1, max 49.9, 100000 rows, 8 cities; 1 blocks decoded
```

## Several answers in one pass

`AggAsync` takes a lambda over an `Aggregates<Reading>` and returns a tuple of one to eight answers,
computed in a single pass over the rows the scan keeps. The members are `Count()`,
`CountDistinct`, `Sum`, `Min`, `Max`, `Average` and `Aggregate`, each over a column named as in a
filter. Like a filter, the lambda runs once and describes the work; nothing is evaluated per row in
your code. Here the `Where` let the zone maps skip 109 blocks, and the four answers came from the
14 left, of which one had a column brought to the canonical form; the others were read as runs and
dictionaries.

For one answer there are the sinks of the scan itself: `CountAsync`, `AnyAsync`, `MinAsync`,
`MaxAsync`, `SumAsync`, `AverageAsync`, `CountDistinctAsync` and `AggregateAsync`. An aggregate runs
block by block on the encoded form, never hands a batch to your code, keeps one state per chunk,
and merges the states at the end; §5.5 of [14-public-api.md](../design/14-public-api.md) tables
what each encoding lets it skip.

## Group by

```csharp
Aggregation<(string, double?, WelfordState)> byCity = file.Scan<Reading>()
    .GroupBy(r => r.City)
    .Select(g => (g.Key, g.Average(r => r.Celsius), g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius)));
await foreach ((string city, double? mean, WelfordState state) in byCity)
{
    lines[line++] = $"  {city,-10} mean {mean:F4}  variance {state.Variance:F4}";
}
```

```
  Paris      mean 29.9563  variance 133.2439
  Lyon       mean 29.9993  variance 133.1939
  ...
  Lille      mean 30.0009  variance 133.6092
```

`Welford<double>` is the aggregator of the next section, a mean and a variance in one pass.
`g.Key` is the key, and the other members of `g` are those of `Aggregates`. The result is an
`Aggregation<T>`, enumerated with `await foreach`, with its own `ExplainAsync` and `Statistics`. A
composite key is a tuple of columns, of any length, whose names are the key's, `g.Key.City` and
`g.Key.Day`:

```csharp
await foreach (var (city, day, total, state) in file.Scan<Reading>()
    .GroupBy(r => (r.City, r.Day))
    .Select(g => (g.Key.City, g.Key.Day, g.Sum(r => r.Celsius), g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius))))
```

The same query in query syntax is the same plan:

```csharp
from r in file.Scan<Reading>()
group r by (r.City, r.Day) into g
select (g.Key.City, g.Key.Day, g.Sum(x => x.Celsius))
```

Groups arrive in key order, nulls last, when the key comes from a dictionary or from a column the
statistics say is sorted, and in no promised order otherwise; a null key is a group of its own.
Memory follows the number of groups, 8 000 for this composite key, not the number of rows.

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
value once with its weight, the `Selected` popcount helper, and `WelfordState`, a count, a mean and
a sum of squared deviations with a weighted `Add`.

Every member is static, so the scan calls them monomorphised, without a virtual call per value.
`Step` receives a block in canonical form: the values, the validity words and the rows that passed
the filter, and it must honour both. An `IAggregator` has only `Step`, `Seed` and `Merge`, and
forces the decode of the column it reads; an `IEncodedAggregator` adds a step per encoded form,
called with the nulls already folded out of `rows`. `Merge` joins the states of two chunks, which is
what makes the parallel run correct. `T` is the column's storage primitive, exactly: `int` for an
`i32` column, `double` for `f64`; another type is refused with `VortexSchemaException`.

What the encoded steps are worth, against `CanonicalWelford<T>`, the same fold with `Step` only:

```
Welford(Celsius), encoded          4.8 ms  980000 values, mean 30.0000, variance 133.2501, 123 blocks decoded
Welford(Celsius), canonical        4.8 ms  980000 values, mean 30.0000, variance 133.2501, 123 blocks decoded
Welford(Day), encoded              0.3 ms  1000000 values, mean 499.5000, variance 83333.3333, 1 blocks decoded
Welford(Day), canonical            4.4 ms  1000000 values, mean 499.5000, variance 83333.3333, 123 blocks decoded
```

`Day` is stored as runs: `StepRunEnd` sees 1 121 runs instead of a million values, more than ten
times faster. `Celsius` is a dictionary whose distinct values include the null, and such a block is
handed to `Step` decoded, so the two are equal ([encoded-forms.md](encoded-forms.md)).
`BlocksDecoded` counts the blocks where a column reached the canonical form: every block of
`Celsius`, and of `Day` only the one block the reader delivers canonical, the other 122 going to
`StepRunEnd` as runs.

## On more cores

```csharp
await using VortexSession parallel = VortexSession.Create(options => options.MaxDegreeOfParallelism = Environment.ProcessorCount);
await using VortexFile shared = await parallel.OpenAsync(path);
```

```
GroupBy(City, Day), degree 1      32.3 ms  8000 groups, the widest spread Lille on day 13, variance 144.07
GroupBy(City, Day), degree 14      9.3 ms  8000 groups, the widest spread Lille on day 13, variance 144.07
Welford(Celsius), degree 14        1.3 ms  980000 values, mean 30.0000, variance 133.2501, 123 blocks decoded
```

Parallelism is the session's, 1 by default: a library does not take a host's cores without being
asked. With it, chunks aggregate concurrently, one state per group per chunk, and `Merge` joins
them: the same answers, 3.5 times faster for the composite group by on 14 cores, 3.7 times for the
Welford fold. `ScanOptions.DegreeOfParallelism` overrides the session for one scan
([threads.md](threads.md)).

## Decimals, at any precision

A decimal column sums exactly, whatever its precision and its row count: the total is kept in 320
bits, spilled from a 128-bit or 256-bit total only when that would overflow, so a column holding
the largest value of 76 digits and its opposite in turn sums to what it should. The sum is
converted once, at the end:

```csharp
decimal total = await file.Scan<Invoice>().SumAsync(i => i.Amount);          // decimal(18, 2): as decimal
VortexDecimal wide = await file.Scan<Ledger>().SumAsync(l => l.Balance);     // decimal(76, 10): as VortexDecimal
double? mean = await file.Scan<Ledger>().AverageAsync(l => l.Balance);
```

A sum as `VortexDecimal` carries 38 digits while it fits them, as a SQL sum of a narrower decimal
does, and 76 beyond; one past 76 digits throws `OverflowException`, as a `decimal` sum past 96 bits
does. An `Int128`, a `UInt128` or a `BigInteger` member sums as its own type, the last never
throwing. `MinAsync` and `MaxAsync` order decimals as the numbers they are, the group keys of a
`GroupBy` may be decimals of 256 bits, and every aggregate reads the column where it lies, a
dictionary or run-end block included.

## Watch out

* **A sum has the column's type.** It accumulates exactly in 128 bits and throws when the result
  does not fit: `SumAsync(v => v.DurationMs)` over the `int` column of the visits file throws
  `OverflowException`. Sum it with an aggregator whose state is a `long`, as `Welford` keeps a
  `long` count.
* **The file carries no sum.** `MinAsync`, `MaxAsync` and `CountAsync` without a filter answer from
  the file statistics; `SumAsync` and `AverageAsync` read the column ([scan-a-table.md](scan-a-table.md)).
* **NaN is skipped** by a float sum, mean, minimum and maximum, and counted as one value by a
  distinct count. An aggregation ignores the scan's `OrderBy`.
* **Encoded steps need generic instantiation at run time.** Under Native AOT the scan calls `Step`
  with the canonical form instead ([native-aot.md](native-aot.md)).

The figures come from one run of the sample on the demonstration file of a million rows, on a
machine of 14 cores; each timing is the best of three passes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- aggregates
```
