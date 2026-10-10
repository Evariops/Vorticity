# Queries

A group by whose groups are filtered, ordered and cut, written as a query or as a chain of methods.
Both build the same plan, and when the key allows it the groups go out as soon as they are final.

```csharp
Scan<DayCityHeat> hottest =
    (from r in readings.Scan<Reading>()
     where r.Day >= 900
     group r by (r.Day, r.City) into g
     where g.Count() > 100
     orderby g.Key.Day, g.Average(x => x.Celsius) descending
     select (g.Key.Day, g.Key.City, g.Count(), g.Average(x => x.Celsius)))
    .Take(4)
    .As<DayCityHeat>();

[VortexRecord]
public partial record struct DayCityHeat(int Day, string City, long Rows, double? Mean);
```

```
day 900 Toulouse    123 rows, mean 29.48
day 900 Marseille   121 rows, mean 29.36
day 900 Nice        126 rows, mean 28.79
day 900 Lyon        126 rows, mean 28.52
the query and the chain agree: True
```

## A query, or the chain it becomes

The compiler turns the query into the methods it names, and both forms build the same plan. The
sample runs both and compares them:

```csharp
Scan<DayCityHeat> chained = readings.Scan<Reading>()
    .Where(r => r.Day >= 900)
    .GroupBy(r => (r.Day, r.City))
    .Where(g => g.Count() > 100)
    .OrderBy(g => g.Key.Day).ThenByDescending(g => g.Average(x => x.Celsius))
    .Select(g => (g.Key.Day, g.Key.City, g.Count(), g.Average(x => x.Celsius)))
    .Take(4)
    .As<DayCityHeat>();
```

The `where` before the `group` filters rows, and is pushed down like any scan filter
([filter-rows.md](filter-rows.md)). The one after it filters groups, on their results. A key of
several columns names its components the way a tuple does, `g.Key.Day` and `g.Key.City`. The lambdas
run once to describe the work, and nothing is evaluated per row in your code.

One value comes back as itself, several go into a record. A `select` of one value is an
`Aggregation<T>`, one value per group:

```csharp
Aggregation<long> perCity = from r in readings.Scan<Reading>() group r by r.City into g select g.Count();
List<long> counts = await perCity.ToListAsync();
```

```
rows per city: 125006, 125000, 124999, 124999, 124999, 124999, 124999, 124999
```

A `select` of several values has no .NET type until a record gives it one. `As<TRecord>()` turns the
result into a scan of that record, which you read as batches or as records like a file, with as many
values as the record has members. Since a result is a scan, `writer.WriteAsync(result)` writes a
rollup as it comes, without building a record per group.

## What each operator holds

The operators after the group by apply in the order written, as in LINQ, and each holds only what its
answer needs:

| after the group by | holds |
|---|---|
| `Where` on the groups | nothing more: a group is kept or dropped as it is delivered |
| `OrderBy(g => g.Key).Take(k)` | `k` groups per lane, the best met so far. A row whose integer key lies past them is not grouped at all |
| `Take(n)` without an order | `n` groups on one lane, with no promise about which ones, and every group on several lanes |
| `OrderBy(g => g.Count())` or any other aggregate | every group until the end, since a group's value is only known then |
| on a key that streams | the groups still open |

The five shortest visit durations of the visits file, against all of them:

```csharp
List<DurationVisits> shortest = await ToListAsync(visits.Scan<Visit>()
    .GroupBy(v => v.DurationMs).OrderBy(g => g.Key).Take(5).Select(g => (g.Key, g.Count())).As<DurationVisits>());
```

```
the five shortest durations, 0 to 4 ms, in 1.05 ms; every one of the 90000 durations in 3.67 ms
```

## Groups that go out as they close

A group is final once no row still to come can hold its key. On a column the statistics say is
sorted, that happens as soon as the key changes. The groups go out in key order while the file is
being read, the first one after the first batch, and memory holds only the groups still open.

```csharp
await foreach (long rows in readings.Scan<Reading>().GroupBy(r => r.Day).Select(g => g.Count()))
```

```
GroupBy(Day): the first of 1000 groups after 0.07 ms and 1 batch(es), all of them after 0.30 ms
```

Under a descending order of such a key followed by a `Take`, the chunks are read from the last one
backwards, and reading stops once the last group asked for is out:

```csharp
List<DayRows> seven = await ToListAsync(lastDays.GroupBy(r => r.Day).OrderByDescending(g => g.Key).Take(7)
    .Select(g => (g.Key, g.Count())).As<DayRows>());
```

```
the last seven days, 999 down to 993: 16960 rows read of a million
```

Two more kinds of keys stream. A column of integers stored nearly in order, like the rows of a log
appended slightly out of order, streams on its zone maps: a group goes out once every block still to
read holds larger keys. It streams on one lane, under a `Take`, and under a tight memory budget. On
several lanes without either, the pass blocks instead, because that runs faster there. A dataset's
clustering key also streams, since the dataset reads its objects in key order. The plan says which
component streams, and [the query design](../design/16-queries.md#23-a-group-by-that-streams)
explains when each one does.

## Migrating from `AggAsync` on groups

| before | now |
|---|---|
| `GroupBy(…).AggAsync(g => …)` | `GroupBy(…).Select(g => …)` |
| `await foreach (var (city, n) in ….AggAsync(g => (g.Key, g.Count())))`, a tuple per group | `….Select(g => (g.Key, g.Count())).As<CityCount>()`, read as batches or with `ToRecordsAsync()` |
| at most eight values in a selection | as many as the record has members |
| `GroupBy(r => (r.City, r.Day))`, then `g.Key.Item1` | `g.Key.City` |
| the groups at the end of the pass | as soon as they are final, on a key that streams |

## Watch out

* Without an order, which groups a `Take` keeps is not promised. On several lanes the groups come in
  the order the lanes met them. `OrderBy(g => g.Key)` makes them the first ones.
* A descending order streams only under a `Take`, and only when nothing before the order depends on
  the order of rows: no `Skip` or `Take` before it, no `First`, `Last`, `MinBy` or `MaxBy`, and no
  aggregator of your own. Read backwards, the rows of a group arrive last first. Without a `Take`, a
  descending order runs as a blocking pass on every lane.
* A key that streams trades a little throughput for memory and a fast first answer. On a nearly
  sorted key of a million values over four million rows, the stream answers first after 1 to 2 ms
  rather than 34. On one lane it holds a fifteenth of the memory of the blocking pass and takes 0.84
  of its time. On fourteen lanes it holds a third of the memory but runs 2.1 times as long, which is
  why the pass blocks there unless a `Take` or a tight budget makes streaming worth it.
* The first passes of a process run on code the JIT has not optimized yet. The last point of
  [aggregates.md](aggregates.md#watch-out) explains what that costs.

The figures come from the sample on the demonstration files, on a 14-core machine. Each timing is
the best of three passes in a process and the median of seven processes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- queries
```
