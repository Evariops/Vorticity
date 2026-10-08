# Queries

A group by, its groups filtered, ordered and cut, written as a query or as a chain of methods: the
same plan either way, and groups that go out as soon as they are final when the key allows it.

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

The compiler turns the query into the methods it names, and both build the same plan; the sample
runs both and compares them:

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

The `where` before the `group` filters rows, and is pushed down as a scan's filter is
([filter-rows.md](filter-rows.md)); the one after filters groups, on their results. A key of several
columns names its components as a tuple does, `g.Key.Day` and `g.Key.City`. The lambdas run once, to
describe the work: nothing is evaluated per row in your code.

**One value comes as itself, several into a record.** A `select` of one value is an `Aggregation<T>`,
a value per group:

```csharp
Aggregation<long> perCity = from r in readings.Scan<Reading>() group r by r.City into g select g.Count();
List<long> counts = await perCity.ToListAsync();
```

```
rows per city: 125006, 125000, 124999, 124999, 124999, 124999, 124999, 124999
```

A `select` of several values has no .NET type until a record gives it one: `As<TRecord>()` makes the
result a scan of that record, read as batches or as records like a file's, and as many values as the
record has members. A result is a scan: `writer.WriteAsync(result)` writes a rollup as it comes, with
no record built per group.

## What each operator holds

The operators after the group by apply in the order written, as in LINQ, and each holds what its
answer needs:

| after the group by | holds |
|---|---|
| `Where` on the groups | nothing more: a group is kept or dropped as it is delivered |
| `OrderBy(g => g.Key).Take(k)`, the key's order | `k` groups a lane, the best met so far, and a row whose integer key lies past them is not grouped at all |
| `Take(n)` without an order | `n` groups on one lane, which ones not promised; every group on several |
| `OrderBy(g => g.Count())` or any aggregate | every group to the end, since a group's value is known only then |
| on a key that streams | the groups still open |

The five shortest visit durations of the visits file, against every one of them:

```csharp
List<DurationVisits> shortest = await ToListAsync(visits.Scan<Visit>()
    .GroupBy(v => v.DurationMs).OrderBy(g => g.Key).Take(5).Select(g => (g.Key, g.Count())).As<DurationVisits>());
```

```
the five shortest durations, 0 to 4 ms, in 0.88 ms; every one of the 90000 durations in 4.37 ms
```

## Groups that go out as they close

A group is final once no row still to come can hold its key. On a column the statistics say is
sorted, that is as soon as the key changes: the groups go out in key order while the file is read,
the first after the first batch, and the memory is the groups still open.

```csharp
await foreach (long rows in readings.Scan<Reading>().GroupBy(r => r.Day).Select(g => g.Count()))
```

```
GroupBy(Day): the first of 1000 groups after 0.07 ms and 1 batch(es), all of them after 0.28 ms
```

Under a descending order of such a key and a `Take`, the chunks are read from the last one back,
and the read stops once the last group asked for is out:

```csharp
List<DayRows> seven = await ToListAsync(lastDays.GroupBy(r => r.Day).OrderByDescending(g => g.Key).Take(7)
    .Select(g => (g.Key, g.Count())).As<DayRows>());
```

```
the last seven days, 999 down to 993: 16960 rows read of a million
```

Two more keys stream. A column of integers stored nearly in order, the rows of a log appended a
little late, streams on its zone maps: a group goes out once every block still to read holds keys
above it. It does on one lane, under a `Take`, and under a tight memory budget; on several lanes
without either, the pass blocks, which runs faster there. A dataset's clustering key streams over its
objects, read in the key's order. The plan
says which component streams, and [16-queries.md](../design/16-queries.md) §2.3 says when each one
does.

## From `AggAsync` on groups

| before | now |
|---|---|
| `GroupBy(…).AggAsync(g => …)` | `GroupBy(…).Select(g => …)` |
| `await foreach (var (city, n) in ….AggAsync(g => (g.Key, g.Count())))`, a tuple a group | `….Select(g => (g.Key, g.Count())).As<CityCount>()`, read as batches or with `ToRecordsAsync()` |
| at most eight values in a selection | as many as the record has members |
| `GroupBy(r => (r.City, r.Day))`, then `g.Key.Item1` | `g.Key.City` |
| the groups at the end of the pass | as soon as they are final, on a key that streams |

## Watch out

* **Without an order, which groups a `Take` keeps is not promised.** On several lanes the groups
  come in the order the lanes met them. `OrderBy(g => g.Key)` makes them the first.
* **A descending order streams only under a `Take`, and with nothing before it that reads an order**:
  no window before it, no `First`, `Last`, `MinBy` or `MaxBy`, no aggregator of your own. Backwards,
  the rows of a group come last first. Read whole, a descending order is the blocking pass's, on
  every lane.
* **A key that streams costs a little throughput for its memory and its first answer.** On a nearly
  sorted key of a million values over four million rows, the stream answers first after 2 ms rather
  than 30 and holds a fifteenth of the memory, for a pass 5 to 23 % longer than holding every group.
* **The first passes of a process run on code the JIT has not optimized yet**: see the last point of
  [aggregates.md](aggregates.md#watch-out).

The figures come from one run of the sample on the demonstration files, on a machine of 14 cores;
each timing is the best of three passes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- queries
```
