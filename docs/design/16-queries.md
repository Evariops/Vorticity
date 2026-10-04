# Queries: select, group, aggregate, as a flow of batches

What a typed scan answers beyond its rows: values computed from them, groups, aggregates over the
groups or over the whole scan, and what follows a group by — a filter on the groups, an order, a
top-k. The surface has the shape of LINQ, in method and query syntax; the plan is built by lambdas
over symbols that run once ([14-public-api.md](14-public-api.md) §5.2); and a query runs as a flow
of batches, block by block on the encoded form, answering as soon as its first batch is final,
holding what is open rather than what it read, and stopping its reads when its consumer stops. The
rules every signature follows are [14-public-api.md](14-public-api.md)'s, the engine's
[03-architecture.md](03-architecture.md)'s, the meaning of a predicate is
[08-semantics.md](08-semantics.md)'s, the order of keys [12-index-reads.md](12-index-reads.md)
§3.4's.

> **Status.** This document describes the target, ahead of the code. Each part lands in the stage
> below, and this table goes with the last one.
>
> | stage | what lands | sections |
> |---|---|---|
> | 0 | the measures: time to first batch, peak memory, allocations, a group-by matrix, and their baselines | §13 |
> | 1 ✅ | `Select` on a grouped scan, its overloads by arity kept until stage 2, named keys, `Average` and `AverageAsync`, `OrderByDescending` on a scan, aggregates deduplicated by structure, the naming rule | §1, §4, §5.5 |
> | 2 ✅ | results as batches: a query's result is a stream of batches, `As<TRecord>` a `Scan<TRecord>` over it; one value comes as itself, several into a record, and the overloads by arity go; `Select`, `Distinct` and `Take` on a scan; the writer takes a scan | §2.1, §6.1, §7, §8 |
> | 3 ✅ | after the group by: `Where`, `OrderBy`, `ThenBy`, `Skip`, `Take`, the top-k; the group by and the `Distinct` that stream; groups in the order asked for | §2.2–§2.4, §6 |
> | 4, the filtered group, reproducible sums, variance, widened sums, chosen rows ✅ | the catalog: a filtered group, `Count(p)`, `Any`, `All`, `Variance`, `StandardDeviation`, chosen rows, sums widened and reproducible | §5 |
> | 5, `Truncate` and `Bucket` ✅ | `Truncate` and `Bucket`; keys settled by the zone maps; groups that stream through them | §3, §9.3 |
> | 6 | the engine: short ranges, composite and direct-index keys, adaptive partitioning, the parallel merge, datasets read ahead and side by side, pruning ahead of the window, finality from the zone maps | §2.5, §2.6, §9 |

## 1. The shape

```csharp
[VortexRecord]
public partial record struct CityHour(DateTime Hour, string City, long Readings, double? Mean);

Scan<CityHour> hourly =
    (from r in file.Scan<Reading>()
     where r.Day >= 900
     group r by (Hour: r.At.Truncate(CalendarUnit.Hour), r.City) into g
     where g.Count() > 10
     orderby g.Key.Hour, g.Average(x => x.Celsius) descending
     select (g.Key.Hour, g.Key.City, g.Count(), g.Average(x => x.Celsius)))
    .Take(100)
    .As<CityHour>();

await foreach (CityHour h in hourly.ToRecordsAsync())
{
    Console.WriteLine($"{h.Hour:HH:mm} {h.City,-10} {h.Readings,6} {h.Mean:F1}");
}
```

The compiler translates the query into the method chain, and both build the same plan:

```csharp
Scan<CityHour> hourly = file.Scan<Reading>()
    .Where(r => r.Day >= 900)
    .GroupBy(r => (Hour: r.At.Truncate(CalendarUnit.Hour), r.City))
    .Where(g => g.Count() > 10)
    .OrderBy(g => g.Key.Hour).ThenByDescending(g => g.Average(x => x.Celsius))
    .Select(g => (g.Key.Hour, g.Key.City, g.Count(), g.Average(x => x.Celsius)))
    .Take(100)
    .As<CityHour>();
```

**One value comes as itself, several into a record.** `select g.Count()` is an
`Aggregation<long>`, a value per group. A `select` of several values has no .NET type until a record
gives it one: the record names and types them, and `As<TRecord>()` makes the result a scan of that
record, read as batches or as records like a file's (§6.1, §7). It is the price a scan already asks
for reading two columns of a file, and it buys names, the batches by default, and no cap on the
number of values.

| on | operators | returns |
|---|---|---|
| `Scan<TRecord>` | `Where`, `Rows`, `OrderBy`, `OrderByDescending`, `With` | `Scan<TRecord>` |
| `Scan<TRecord>` | `Select(r => e)`; `Select(r => (e1, e2, …))` | `Projection<T>`; `Projection`, of several values (§7.3) |
| `Scan<TRecord>` | `GroupBy(r => …)` | `GroupedScan<TRecord, TKey>` (§4) |
| `GroupedScan` | `Where(g => …)`, `OrderBy`, `OrderByDescending`, `Skip`, `Take` | `GroupedScan`, ordered: `OrderedGroupedScan`, which adds `ThenBy` and `ThenByDescending` |
| `GroupedScan` | `Select(g => r)`; `Select(g => (r1, r2, …))` | `Aggregation<T>`; `Aggregation`, of several values (§6.1) |
| `Aggregation<T>`, `Aggregation`, `Projection<T>`, `Projection` | `Skip`, `Take`; `Distinct()` on a projection | the same kind; an aggregation for `Distinct` |
| `Aggregation<T>`, `Aggregation`, `Projection<T>`, `Projection` | `As<TRecord>()` | `Scan<TRecord>` over the result (§7.1) |
| `Aggregation<T>`, `Projection<T>` | `WithCancellation`; `ToListAsync`, `ToArrayAsync`; `ToValuesAsync` | the same kind; `ValueTask<…>` (§7.2); `IAsyncEnumerable<T>` |
| `Scan<TRecord>` | `AggAsync(a => e)`, `AggAsync<TResult>(a => (e1, e2, …))`, `CountAsync`, `AverageAsync` and the other single answers | `ValueTask<…>` (§8) |

**Builders and runs.** An operator that returns a query describes it and runs nothing; `await
foreach` runs it, `ExplainAsync` plans it without reading a data segment, and a method that returns
a `ValueTask` runs at once. The names follow the rule of [14-public-api.md](14-public-api.md):
`Async` names what runs. **A builder never reads**: what it checks — a column, a type, a record's
members — is in the schema the open read. The one read a binding may make is the time-zone database
of the operating system, once, when a zoned timestamp binds
([07-dotnet-mapping.md](07-dotnet-mapping.md) §3); the BCL has no asynchronous form of it.

**Where the plan ends.** `Aggregation<T>` and `Projection<T>`, of one value, are enumerated with
`await foreach`, by the pattern a scan is enumerated by rather than as an `IAsyncEnumerable<T>`:
VSTHRD200, an error in this repository, has a method that returns a stream to await end in `Async`,
and `Select` builds a query. `WithCancellation` gives them a token as it gives a scan one, and
`ToValuesAsync()` hands their values to `System.Linq.AsyncEnumerable`, whose operators then run on
the values delivered: a `Where`, an `OrderBy` or a `GroupBy` written there is LINQ to objects,
correct and not pushed, while their own `Skip`, `Take`, `ToListAsync` and `As` stay in the plan.
`Aggregation` and `Projection`, of several values, are not enumerable at all: `As<TRecord>()` is how
they are read. Either way, `As<TRecord>()` returns a scan, on which the plan's own operators go on
(§7.1).

**What does not compile**, which is where a query over one table ends:

| written | why |
|---|---|
| a column outside an aggregate after `into g`, or in `g => …` | the row's probe is out of scope: a group's columns exist only inside the lambda of an aggregate, so a result is a key or an aggregate (CS0103) |
| `let`, `join`, a second `from` | there is no `Select` of an anonymous type, no `Join`, no `SelectMany` (CS0029, CS1936) |
| arithmetic on a column or a result | the operators are `[Obsolete(error)]`: computing with results is C# after the sink |
| a literal of another type than the symbol's | as in a filter ([14-public-api.md](14-public-api.md) §5.2) |
| `await` on a builder, `foreach` over a `GroupedScan` | a builder is not awaitable; a group is not a value, a `Select` says what of it to deliver |
| several values read without a record: `await foreach` over a `select (a, b)`, `AggAsync(a => (x, y))` | several values have no .NET type until a record gives them one (CS8411, CS0411) |

What compiles and still cannot run throws when the operator runs (§11), and an analyzer flags it at
compile time where it can see it: VX1009 for a key component that is not a symbol, VX1010 for an
`As<TRecord>` or an `AggAsync<TResult>` whose record does not match the values; VX1011 goes with the
compiler's error of the last row and names the fix, `As<TRecord>()` and the record to declare.

## 2. The flow

### 2.1 Batches, pulled

Every source, operator and sink of a query exchanges batches — blocks in the arena of
[03-architecture.md](03-architecture.md) §3.3, encoded or canonical — and nothing else crosses an
`await`: a `MoveNextAsync` is a batch, and every loop inside a batch is synchronous
([03-architecture.md](03-architecture.md) §3.6). The consumer pulls: a stage computes its next
batch when it is asked for one, and its sources decode ahead of it by a bounded window, the
read-ahead and the lanes of [09-contracts.md](09-contracts.md) §2; nothing is buffered beyond that
window, so a consumer slower than the file is the backpressure. Every enumerator is written by hand
and allocates nothing per batch ([03-architecture.md](03-architecture.md) §3.7).

A query's **result is a stream of batches** too, one column per element of its `select`. One value
per row or per group is a view over them, a `T` at a time; several are read through the record of
`As<TRecord>()`, as batches whose columns are borrowed or as records. The only allocations a query
makes per row are the strings and objects those views create (§7.2).

### 2.2 What each operator holds, and when it answers

| operator | holds | its first batch after |
|---|---|---|
| a scan, a filter, `Select` on a scan, `Truncate`, `Bucket`, `Skip`, `Take` | its window | its first split |
| `Distinct` | the values met, or the last one on a column that streams | its first split: a value goes out the first time it is met |
| `GroupBy` on a key that streams (§2.3) | the groups still open | the first group closed |
| `GroupBy` on any other key | one state per group | the end of its input |
| `Where` on groups, `Select` of groups, `Skip`, `Take` | nothing more | its input's |
| `OrderBy` on a key that streams in that order | nothing more | its input's |
| `OrderBy` then `Take(k)` | `k` groups | the end of its input |
| `OrderBy`, any other | every group | the end of its input |
| `AggAsync` and the single answers | one state per aggregate | the end of its input; at once when the statistics answer (§8) |

What this promises, each a gate (§13):

* **The first batch of a streaming query waits for its first split**, not for the file: its time to
  first batch does not grow with the file.
* **Memory is the window and the open state**: a streaming group by holds the groups still open,
  whatever the number it delivers; a top-k holds `k`; a blocking group by one state per group at
  any degree (§9.4); nothing is held per row read.
* **Nothing is allocated per batch** in steady state on every path that delivers batches, and nothing
  per group on the path that delivers groups as batches (§7.1).

### 2.3 A group by that streams

A group is **final** when no row still to come can hold its key. On a key component the statistics
say is sorted, or a non-decreasing function of one (§3), that is as soon as its value changes: the
groups of the previous value go out, in key order, and their states are reused. On a column the zone
maps cover, it is as soon as every block still to come has bounds above the group's value, which
holds for data appended roughly in order of time without being sorted: the groups below the
smallest bound to come go out, in key order, and the memory is the groups the overlap keeps open.

So, on such a key:

* one key holds one open group; a composite key with such a component holds the groups of the
  current value of it: `(Day, City)` on a file sorted by time holds a day's cities;
* `orderby g.Key` ascending, and on a composite key an order that starts with that component, sorts
  nothing;
* `Take(n)` stops the read once `n` groups have gone out: `group r by r.At.Truncate(CalendarUnit.Day)`
  followed by `Take(7)` reads seven days of a year;
* `Where` and `Skip` on the groups stream with it.

A group by does not stream on a key with no such component, under a descending order of it or an
order by an aggregate, on a sorted column that holds a NaN, which is not sorted at all
([12-index-reads.md](12-index-reads.md) §3.4), or on a dataset whose objects' statistics do not
prove their order. The null group of the streaming component is final at the end, and comes last.
The plan says which component streams, or why none does (§10).

### 2.4 Stopping early

A `Take` satisfied, a loop left by `break`, a cancellation: the query stops asking its sources for
batches, so nothing past the window in flight is read or decoded. A `Take` narrows the read-ahead to
what it can still use: a `Take(10)` over a projection decodes one split, not a window of them.
`DisposeAsync` awaits the reads in flight and returns every pooled buffer, without blocking a
thread.

### 2.5 In parallel, in order

At a degree above one, the splits of a scan decode on their lanes and come back in row order
through a bounded window ([09-contracts.md](09-contracts.md) §2). A streaming operator runs on that
ordered stream — decode, the heavy part, is parallel, and grouping a sorted key costs one lookup per
run — or runs per range of rows and joins the groups that straddle two ranges; either way its
batches go out in order. A blocking operator runs per range on its lanes and merges (§9.4): nothing
it holds goes out before its input ends, so it has no order to keep while it runs.

### 2.6 The first batch of each source

* **A file.** The structures that prune — zone maps, indexes — are read ahead of the decode window
  rather than over the whole file before the first batch: those of the first window first, those of
  the rest in one coalesced read behind them. The plan is unchanged and each segment is still read
  once ([14-public-api.md](14-public-api.md) §9); only when moves, for one request more.
* **A dataset.** Its objects are opened ahead of the one being read, within the window, and read
  side by side at a degree above one; the first batch waits for the first object, and no object
  boundary stalls the stream ([13-dataset.md](13-dataset.md)).
* **A result.** It is a stream itself (§7.1); its first batch is its query's first.

## 3. Value expressions

A value expression is a column's symbol, or a function of one. It is valid in a row filter, as a key
component, as the input of an aggregate and as an element of a projection; not as the key of a
scan's `OrderBy`, which walks a column's key source ([12-index-reads.md](12-index-reads.md) §5).

| function | on | gives |
|---|---|---|
| `Truncate(CalendarUnit unit)` | `DateTime`, `DateTimeOffset`, `DateOnly`, and their nullable forms | the start of the unit holding the value: `Minute`, `Hour`, `Day`, `Week` (Monday, ISO 8601), `Month`, `Quarter`, `Year`; a `DateOnly` takes the units from `Day` up |
| `Truncate(CalendarUnit unit, TimeZoneInfo zone)` | `DateTime`, `DateTimeOffset` | the same, on the calendar of `zone`: a day in Paris starts at 22:00 or 23:00 UTC |
| `Bucket(TimeSpan width)` | `DateTime`, `DateTimeOffset` | `epoch + ⌊(value − epoch) / width⌋ × width`, the epoch 1970-01-01T00:00Z, the storage's own origin |
| `Bucket(T width)` | integers, floats | `⌊value / width⌋ × width`, in integers for an integer, rounded to the column's type for a float |

The result has the column's type. A naive or UTC timestamp, bound as `DateTime`, is truncated as it
is stored; a zoned one, bound as `DateTimeOffset`, on the calendar of its column's zone unless a zone
is given ([07-dotnet-mapping.md](07-dotnet-mapping.md) §3). A null stays null and a NaN stays NaN.
A width that is not positive, that the column's unit cannot divide, or a unit finer than a
`DateOnly`'s day, throws `ArgumentOutOfRangeException` when the lambda runs; a decimal's bucket is
not there yet and throws `NotSupportedException`. `CalendarUnit` is not `TimeUnit`, which is the
unit a timestamp is stored in.

Over integers both functions are **floors**: non-decreasing, never above the value, and where the
floor would fall below the storage's least value they give that value, so that one function holds
over the whole storage. A zone's calendar holds over the years a `DateTime` holds; an instant before
them takes the storage's least value, one after them the start of the last unit they hold. Three
things follow:

1. In a filter, a comparison with a constant is the range of the column's values whose image
   compares so: `r.At.Truncate(CalendarUnit.Day) == day` is `r.At >= day && r.At < day + 1 day`,
   the least value whose image reaches the constant found by bisection on the function. The scan
   takes the range, so every structure of the column — zone maps, statistics, sorted runs, indexes —
   prunes, counts and locates for it as for the range written by hand; a null row is unknown to
   both, under a negation too. Where there is no such range — a float's bucket, an `In`, two
   columns compared — a zone's bounds go through the function and prune as the column's would.
2. A key the statistics say is sorted stays sorted through it, so it still groups by runs (§9.1)
   and still streams (§2.3).
3. A block whose bounds go to one value holds one key, and §9.3 answers some aggregates of it from
   the zone map.

On the encoded form a function is evaluated once per dictionary value, per run and per constant,
and per value on a canonical block, in the storage's integers: a timestamp's `i64` in its unit,
months and years by a days-from-civil computation, no `DateTime` per value. A zone is applied
through its offset intervals, the transitions of a year found the first time an instant of it is
met and kept — two a year where daylight saving holds — so a value takes its interval's offset, an
integer truncation and the offset back: a look among the two or three intervals of its year. The
autumn's repeated hour is two hours, with two offsets, so two buckets.

Other functions — date parts, text functions, a coalesce — will take the same path: a function on a
symbol, evaluated where the block lies.

## 4. Keys

```csharp
.GroupBy(r => r.City)                                            // g.Key: Sym<string>
.GroupBy(r => (r.City, r.Day))                                   // g.Key.City, g.Key.Day
.GroupBy(r => (Hour: r.At.Truncate(CalendarUnit.Hour), r.City))  // g.Key.Hour, g.Key.City
```

`GroupBy<T>(Func<Probe<TRecord>, Sym<T>>)` groups by one component, and
`GroupBy<TKey>(Func<Probe<TRecord>, TKey>) where TKey : struct, ITuple` by a tuple of them, of any
length. The tuple's type is captured whole, so its names — written, or inferred from `r.City` — are
the names of `g.Key`. Overloads by arity, `GroupBy<TKey1, TKey2>`, would win over it and lose them,
so there are none.

`g.Key` is the tuple of symbols the key lambda returned, and each component is a result of the
group (§6.1). A composite `g.Key` as a whole is not a result: its components are.

A component is a value expression (§3) of a type that groups: an integer, a float, a decimal of 128
or 256 bits, a uuid, a bool, text, binary, a temporal type by its storage, an enum by its underlying
integer. A list, a record or a map throws `VortexSchemaException` when `GroupBy` runs; a component
that is not a symbol of the scan, a literal or a captured value, throws `ArgumentException`, and
VX1009 flags it at compile time.

Two keys are equal as the file stores them: a null is a group of its own, component by component;
every NaN is one group, and −0.0 and +0.0 are one; text and binary compare bytewise; a decimal by
its unscaled value, since a column has one scale; a timestamp by its integer.

## 5. Aggregates

The members of `Group<TRecord, TKey>`, and of `Aggregates<TRecord>` for `AggAsync` (§8), which has
every one but `Key`. Each takes a lambda over the rows' probe, as LINQ's do, and the probe exists
only inside it.

| member | result | skips | with no value |
|---|---|---|---|
| `Count()` | `long` | nothing | 0 |
| `Count(x => p)` | `long` | the rows where `p` is not true | 0 |
| `CountDistinct(x => e)` | `long` | null; every NaN counts as one value | 0 |
| `Any(x => p)`, `All(x => p)` | `bool` | the rows where `p` is not true | `false`, `true` |
| `Sum(x => e)` | §5.1 | null, NaN | 0 |
| `Average(x => e)` | `double?` | null, NaN | null |
| `Min(x => e)`, `Max(x => e)` | the symbol's type | null, NaN | null, or the default of a value type that is not nullable |
| `Variance(x => e)`, `StandardDeviation(x => e)` | `double?`, of the sample (n − 1) | null, NaN | null below two values |
| `Aggregate<T, TAggregator, TState>(x => e)` | `TState` | what the aggregator skips | `TAggregator.Seed()` |

`p` is a predicate over the rows, in the language of `Where`, and a row counts when it is true, not
unknown ([08-semantics.md](08-semantics.md) §3). `All(x => p)` is `Count(x => p) == Count()` and
`Any(x => p)` is `Count(x => p) > 0`. "With no value" happens in a group only for an aggregate of
a filtered group (§5.2) or of a column whose every row is null; in `AggAsync`, for a scan that keeps
no row.

### 5.1 Sums: exact, and the same bits whatever the cut

| column | sum | accumulated |
|---|---|---|
| `sbyte`, `short`, `int` | `long` | exactly, in integers |
| `byte`, `ushort`, `uint` | `ulong` | exactly, in integers |
| `long`, `ulong` | its own type | exactly, in integers |
| `decimal`, `VortexDecimal`, and `Int128`, `UInt128`, `BigInteger` over a decimal of scale 0 | its own type | exactly, 128 bits spilled into 320 ([aggregates.md](../guide/aggregates.md)) |
| `Half`, `float`, `double` | `double` | reproducibly: the same bits whatever the cut |

A sum widens what could overflow on the way and keeps the type of what could not: it throws
`OverflowException` only when the exact total does not fit the type it is delivered as. A nullable
column sums as its type without the nullability. The engine accumulates an integer sum in the
narrowest integer the statistics prove cannot overflow — the rows times the largest magnitude they
give — a 64-bit state where they prove it, 128 bits where they prove nothing: the width is the
engine's choice, the exactness the promise.

**A float sum is the same bits** at every degree of parallelism, in every order of rows, and under
every cut of the data into chunks, files or objects, so compacting a dataset changes no sum. It is
an indexed sum (Demmel and Nguyen; ReproBLAS): bins on one grid of exponents for every sum, bin
`j`'s unit `2^(27j − 1074)`, of which each sum keeps three, the bin its largest value calls for and
the two below, at least 53 bits under that value. A value is split from the top bin down by the
pre-rounding of an extractor `1.5 · 2^52` units wide, each part an integer of its bin's unit; a bin
above a value takes nothing of it, so its parts do not depend on when the sum's top moved, and each
bin is the exact sum of its parts in a long. When the top moves up, the bins it leaves below the
three fall out, as a final top that high would not keep them either; nothing carries from one bin
into another, which a later drop would make depend on the order. The answer is the bins' exact
total rounded once, to the nearest double. The error is then that rounding and the bits values hold
more than 53 below the largest one, which makes the sum more accurate than a plain one, whose error
grows with the rows.

The top is each sum's own, from its own values, and no statistics are read: a group of small values
keeps its precision beside a group of large ones, a result's scan and a dataset sum as a file does,
and a filter changes no grid. A part is at most 2^26 units, so a sum takes 2^36 values, some 69
billion, in one group or one scan, and throws `OverflowException` past them rather than lose its
exactness. A NaN is skipped; a sum holding +∞ and not −∞ is +∞, one holding both is NaN, as IEEE 754
gives. An average is that sum over the count; a variance comes from the reproducible sums of
`x − c` and `(x − c)²`, `c` the midpoint of the column's bounds over the whole source where its
statistics hold them, a file's, and zero where they do not, fixed per query: the nearer `c` lies to
a group's mean, the fewer digits the difference of the two sums cancels.

Every answer of §5 is therefore the same bits at every degree and in every cut, but those that
depend on the order of rows by definition: `First`, `Last`, and a tie of `MinBy` or `MaxBy`.

### 5.2 A filtered group

`g.Where(x => p)` is the group's rows where `p` is true, with every aggregate, and its key; SQL's
`FILTER (WHERE …)`:

```csharp
select (g.Key, g.Count(), g.Where(x => x.Status >= 500).Count(), g.Where(x => x.Status >= 500).Average(x => x.DurationMs))
```

`g.Count(x => p)` is `g.Where(x => p).Count()`, two `Where` join with `AND`, and `a.Where(x => p)`
filters the scan's aggregates likewise. The predicate is evaluated once per batch however many
aggregates read it, on the encoded form as a scan's filter is, and the columns it reads join the
pass, which the plan shows (§10).

### 5.3 A chosen row

`g.First()` and `g.Last()` are the group's first and last row in file order: the order of the rows
the scan keeps, its `OrderBy` ignored. `g.MinBy(x => e)` and `g.MaxBy(x => e)` are the row holding
the smallest and the largest value of `e`, a null or a NaN never one, the first in file order on a
tie. Each is a probe whose columns are results:

```csharp
select (g.Key, g.First().Price, g.Max(x => x.Price), g.Min(x => x.Price), g.Last().Price)   // open, high, low, close
select (g.Key, g.Max(x => x.Celsius), g.MaxBy(x => x.Celsius).At)                           // and when
```

A chosen row is kept as its position and the value it is chosen by; the columns read from it are
fetched after, for the chosen rows only, by position — a take, which decodes only those rows where
the encoding allows ([90-registry.md](90-registry.md)) — unless the groups are so many against the
rows that carrying the columns along costs less, which the engine decides from both counts. A
streaming group by fetches them per batch of groups it closes. Columns read from one chosen row share
it: `g.MaxBy(x => x.Celsius).At` and `.City` are one row. When `MinBy` or `MaxBy` has no candidate,
its columns are null, or the default of a value type that is not nullable. The order is the file's
at every degree of parallelism: a range of rows keeps its own, and the merge keeps the earlier
range's row. A chosen row's column is a result and not a row: it is no aggregate's input, and
naming it in one throws `InvalidOperationException`.

### 5.4 An aggregator of one's own

`IAggregator` and `IEncodedAggregator` are unchanged ([aggregates.md](../guide/aggregates.md)). The
call names three types, since the compiler infers none of them from the column, so an aggregator
ships an extension member that makes it read as a built-in:

```csharp
public static class WelfordAggregates
{
    extension<TRecord, TKey>(Group<TRecord, TKey> g)
    {
        public Sym<WelfordState> Welford(Func<Probe<TRecord>, Sym<double>> column) =>
            g.Aggregate<double, Welford<double>, WelfordState>(column);
    }
}
```

A range of rows of one group costs the aggregator its rows, not its batch's: a range of fewer rows
than its batch has words of selection is folded row by row (§9.1).

A state is a result like any other, so its type is one a column holds: a number, a decimal, text, a
date or a time, a uuid, a bool, or a `[VortexRecord]`, which a result holds as a struct column of
its members and which a record of the selection declares as a member of that type. `Select` refuses
a state of another type with `VortexSchemaException`; `AggregateAsync`, one answer of a whole
scan, delivers any state as it is.

### 5.5 One aggregate, wherever it is written

Two aggregates are one when they have the same function, the same input expression and the same
filter: the `g.Count()` of a `where`, an `orderby` and a `select` is counted once. An average shares
the sum and the count of its column with a sum and a count of it.

## 6. After the group by

### 6.1 Results: `Select`

`Select(g => r)` delivers an `Aggregation<T>`, one `T` per group: `select g.Count()`,
`select g.Key`. `Select(g => (r1, r2, …))`, of any number of elements, delivers an `Aggregation`:
one row of values per group, which a record reads (§7.1). One overload takes the whole tuple, boxed
once when the lambda runs, and the record gives the values their names and their types; the
overloads by arity of the 0.4 surface, which stopped at eight and returned tuples whose names the
compiler drops (CS8123), are gone. An element is a key component, an aggregate, or a column of a
chosen row; a symbol of anything else, captured from outside the lambda, throws
`InvalidOperationException` when `Select` runs.

### 6.2 Groups kept: `Where`

`Where(g => p)` keeps the groups where `p` is true. `p` compares results — key components,
aggregates, chosen rows' columns — with a literal of their type or with one another, joined by
`&&`, `||` and `!`: a filter's language over results, three-valued, so a null average fails
`> 30.0` and `== null` tests it ([08-semantics.md](08-semantics.md) §3). An aggregate named only in
a `Where` is computed and not delivered.

A conjunct that names only key components, in a `Where` before any `OrderBy`, `Skip` or `Take`,
filters rows instead, which keeps the same groups: `where g.Key.City == "Paris"` is
`r.City == "Paris"` and prunes as a filter does; `where g.Key.Hour >= start` is
`r.At.Truncate(CalendarUnit.Hour) >= start` and prunes through §3.

### 6.3 Order: `OrderBy`, `ThenBy`

`OrderBy`, `OrderByDescending`, then `ThenBy` and `ThenByDescending`, take a result. A result
orders as its type: numbers by value, −0.0 equal to +0.0, NaN after +∞; text and binary bytewise;
`false` before `true`; temporal values in time; a uuid bytewise; an enum by its underlying value.
Nulls come **last in both directions**, as rows in key order do
([12-index-reads.md](12-index-reads.md) §5). A result with no order, an aggregator's state, throws
`ArgumentException` when `OrderBy` runs. Ties past the last key are broken by the group's key, so an
ordered query delivers the same groups in the same order at every degree of parallelism and in every
cut of its data, its float aggregates being the same bits there (§5.1).

Without `OrderBy` the order of the groups is unspecified: it follows the encodings met and the
degree, and a group by that streams delivers them in the order of its streaming component. The 0.4
surface delivered key order when the key was a dictionary or sorted; an order is now asked for, and
`orderby g.Key` on a key that streams sorts nothing (§2.3).

### 6.4 `Skip` and `Take`

As in LINQ: after an order, the groups past the first `Skip` ones, `Take` of them; without an order,
some groups. Written before or after the `select`, the result is the same. Under an order the groups
are ranked in a heap of `Skip + Take`: the others are compared and never delivered, a cost of
`G log k` and a memory of `k` groups. Under no order, or an order a streaming group by already
delivers, a `Take` stops the read once it is served (§2.4).

The operators apply in the order written, as in LINQ: a `Where` after a `Take` filters the groups
taken.

## 7. Results are scans

### 7.1 `As<TRecord>`

`As<TRecord>()`, on an aggregation or a projection of one value or of several, returns a
`Scan<TRecord>` whose source is the query's result: the stream of §2.1, its columns named after the
members of `TRecord`, an `IVortexRecord<TRecord>` whose members, in declaration order, take the
elements of the `select` in order. It is how several values are read, and how one value becomes a
scan too. A member's type is the element's or its nullable form; otherwise `As` throws
`VortexSchemaException` naming the position and both types, and VX1010 flags what the compiler sees.
Everything a scan does, a result's scan does, and as a stream:

```csharp
await foreach (var (hour, city, readings, mean) in hourly)        // §1's Scan<CityHour>: Columns<CityHour>, borrowed
{
    ReadOnlySpan<long> counts = readings.Values;                    // no copy; text read as spans
}
```

| on a result's scan | what it does |
|---|---|
| `await foreach`, `ToBatchesAsync`, `ToRecordsAsync` | the result's batches, borrowed or owned, or its records |
| `Where`, `Select`, `GroupBy`, `AggAsync`, `CountAsync` and the single answers | the same operators over the result's batches: a filter evaluated on each, a projection, an aggregate of an aggregate — daily from hourly |
| `OrderBy`, `OrderByDescending` | free when the result arrives in that order, from a group by that streams on that key or an `orderby` of its query; otherwise the result is sorted, a blocking stage holding the result |
| `Keys(r => …)` | the key cursor over the result's keys, sorted in memory unless the result arrives in their order |
| `Rows(range)`, `Rows(indices)` | positions in the order the result is delivered, read as a skip and a take of the stream |
| `With(options)` | the options of what runs on the result; the query keeps its own |
| `ExplainAsync`, `Statistics` | the query's plan with the result's operators after it, and what the whole pipeline did |

A result has no zone maps nor indexes: its filters evaluate every batch, and a filter on a key is
better written before the `select`, where it prunes (§6.2). A result's scan is single use, as every
scan is ([09-contracts.md](09-contracts.md) §1): enumerating it runs its query.

### 7.2 Values and records

`await foreach` over an `Aggregation<T>` or a `Projection<T>` delivers one `T` per group or per row:
a view over the result's batches, and a `string` per text value. Several values are read through
`As<TRecord>()`: as batches, the default, where nothing is allocated per group and text is read as
spans, or as records through `ToRecordsAsync()`, a path of rows priced as such
([14-public-api.md](14-public-api.md) §5.6): over a million groups keyed by text, rows cost 41 ms and
46 MiB against 2 ms through the batches (§13). The `ToListAsync` and `ToArrayAsync` of a query of
one value are its own, and fill the list from the batches without an `await` per element.

### 7.3 `Select` on a scan

`scan.Select(r => e)` delivers a `Projection<T>`, one value per row the scan keeps, in file order,
or in key order under the scan's `OrderBy`; `scan.Select(r => (e1, e2, …))` delivers a
`Projection`, several values per row, read through a record (§7.1):
`scan.Select(r => (r.At.Truncate(CalendarUnit.Day), r.City)).As<DayCity>()` is a scan of computed
columns. An element is a value expression (§3), never an aggregate. The scan reads the columns that
its elements and its filter name, and computes each function a batch at a time into a column of the
result. In query syntax `select r.City` is this; `select r` alone is no projection, and the query
is the scan, delivering batches. `Take(n)` stops reading after `n` rows (§2.4).

### 7.4 `Distinct`

`projection.Distinct()` is a group by the projected values with no aggregate: one element per
distinct value, an `Aggregation<T>` for one value and an `Aggregation` read through a record for
several, unordered, and streaming whatever the key, since a value with no aggregate is final the
first time it is met (§2.2). `(from r in scan select r.City).Distinct()` groups a dictionary column
by code.

### 7.5 Writing a result

`writer.WriteAsync(scan)` writes the batches of any scan as they come: a file's, which copies it,
or a result's, which writes a rollup without building a record:

```csharp
var hourlyByEndpoint =
    from r in log.Scan<Request>()
    group r by (Hour: r.At.Truncate(CalendarUnit.Hour), r.Endpoint) into g
    select (g.Key.Hour, g.Key.Endpoint, g.Count(), g.Average(x => x.DurationMs));

await writer.WriteAsync(hourlyByEndpoint.As<EndpointHour>());
```

On a log sorted by time the groups stream (§2.3), so the rollup of a year holds the endpoints of one
hour, not of the year, and its averages are the same bits however the log was cut (§5.1).

## 8. One row: the aggregates of the whole scan

`scan.AggAsync(a => e)` computes one answer, and `scan.AggAsync<TResult>(a => (e1, e2, …))` as many
as are written, in one pass, into the record `TResult`, whose members take the answers in order as
`As` has them take a `select`'s, VX1010 included; the one type argument is all the call names:

```csharp
long readings = await scan.AggAsync(a => a.Count());
Summary s = await scan.AggAsync<Summary>(a => (a.Min(x => x.Celsius), a.Max(x => x.Celsius), a.Count()));
```

The answers come from the catalog of §5 but the key, a chosen row taken in the order of the whole
scan; `a.Where(x => p)` filters them. One answer also has its own call: `CountAsync`, `AnyAsync`,
`MinAsync`, `MaxAsync`, `SumAsync`, `AverageAsync`, `CountDistinctAsync`, `AggregateAsync`. The file statistics answer a count, a minimum, a maximum,
and the sum and the mean of an integer or a decimal column, before any read, and the `ValueTask` is
then already complete ([12-index-reads.md](12-index-reads.md) §4). A float column's sum and mean are
computed, never read from the statistics: the writer summed them in an order of its own, and an
answer taken from there would change with the cut of the data (§5.1).

## 9. Execution

One pass over the blocks the filter keeps, each in the form it arrives in, never handing a batch to
the caller's code. Keys are numbered as they are met, one state per group per aggregate.

### 9.1 Keys, block by block

| key block | groups | lookups |
|---|---|---|
| constant | one range | one for the block |
| run-end | one range per run | one per run |
| dictionary | by code: a table from code to group, kept while blocks view the same values | one per distinct code met |
| sorted by the statistics, canonical | runs detected | one per run |
| an integer whose statistics bound it within 2¹⁶ values | a direct index from the value | none |
| canonical, any other | per row, a row equal to the one before reusing its group | one per row |
| composite, every part constant, run-end or sorted | ranges cut at every part's boundaries | one per range |
| composite, fixed-width parts within 16 bytes | the parts packed into one 128-bit key | one per row |
| composite, any other | the tuple encoded into bytes, one hash | one per row |

A function of a column (§3) groups as the column does, evaluated per code, per run or per value.
A range of fewer rows than its batch has words of selection, 1 024 for 65 536 rows, is folded row by
row with the per-row path: a key of short runs costs what a per-row key costs, never more.

### 9.2 Aggregates, block by block

| block | count, minimum, maximum, sum, mean |
|---|---|
| constant | the value weighted by the rows |
| run-end | per run, weighted by its length |
| dictionary | over the distinct codes the rows name, when their ranges are long enough to repay it |
| frame of reference, bit-packing, delta | a vectorized unpack, the base applied once |
| alp, zstd, fsst | the decode, then the plain kernel |

A float sum takes its parts (§5.1) in the same kernels, a weighted value's included: a part times a
count is still a multiple of its unit, exact within the bounds. A filtered aggregate takes the
filter's mask, evaluated once per batch and per distinct filter, as its selection. A chosen row keeps
its position and the value it is chosen by (§5.3).

### 9.3 Statistics and zone maps

A whole-scan aggregate the file statistics settle reads nothing (§8). A block whose key, through its
function, maps its zone's bounds to one value is one group: when the filter keeps the block whole,
its `Count`, its `Min` and `Max` where the bounds are exact, and its null counts come from the zone
map, and the block is not decoded when no other aggregate needs it. A zone map holds no sum
([02-format.md](02-format.md) §6), so a sum or a mean decodes. On a column sorted by time,
`group r by r.At.Truncate(CalendarUnit.Day)` with counts and extremes reads the zone maps and the
blocks that straddle midnight, and nothing else. The same bounds give the finality of §2.3 and the
bounds of §5.1.

### 9.4 Parallelism and the merge

At a degree above one the rows are cut at chunk boundaries into ranges, each aggregated on a lane of
its own. A range pre-aggregates while its keys repeat; when its groups approach its rows — keys that
rarely repeat, a user or a session id — it stops, and partitions its rows by the hash of their key
among the lanes, so that each lane owns a slice of the keys and every group has one state: memory is
then the number of groups, not the degree times it. The ranges' groups merge part by part, each part
a task the query awaits, never a thread blocked on others. A chosen row and a tie keep the earlier
range's. On a dataset, a range is an object, or a part of a large one, and an object's statistics
settle what a file's would ([13-dataset.md](13-dataset.md)). A group by that streams runs as §2.5
says.

### 9.5 Memory

A blocking group by holds one state per group (§9.4 keeps it so at any degree), each of the width
§5.1 lets the engine choose, its keys once, and its output a batch at a time; a streaming one holds
its open groups; a top-k its `k`; a projection and a `Distinct` on a column that streams, their
window. Nothing is held per row read.

## 10. Plan and statistics

`ExplainAsync` on an aggregation, a projection or a result's scan returns the scan's
`ScanPlan` with `Grouping`, null without a group by. It says what costs, so that the shape of LINQ
does not hide it:

| member | says |
|---|---|
| `GroupPlan.Keys` | per component: its column, its function, whether the statistics say it is sorted, whether they bound it for a direct index |
| `GroupPlan.Streaming` | the component the groups stream on, or why none does (§2.3) |
| `GroupPlan.Aggregates` | the aggregates once deduplicated, those the statistics settle, the filters of filtered groups and the columns they add to the pass |
| `GroupPlan.RowFilter` | the conjuncts of a `Where` on keys moved to the rows (§6.2) |
| `GroupPlan.Order` | none, streamed, a heap of `k`, or a sort of every group |
| `GroupPlan.ChosenRows` | the chosen rows fetched by position after the pass, or carried along |
| `GroupPlan.Ranges`, `Partitioned` | how many ranges aggregate concurrently, and whether they partition by key |
| `GroupPlan.Memory` | the memory the statistics bound, when they bound the groups: a direct-index key's range, a streaming key's open groups |

A key's form is known only once its block is read, so the plan says what is possible and the
statistics what happened. `Statistics` gains `Groups`, `PeakGroups` — the most groups held at once,
which a streaming group by keeps small — the key blocks by how they were grouped,
`KeyBlocksByRange` (constant, run-end, sorted), `KeyBlocksByCode`, `KeyBlocksHashed` and
`KeyBlocksSettled` (§9.3), and `TimeToFirstBatch`, the time from the first `MoveNextAsync` to the
first batch delivered.

## 11. Errors, cancellation, disposal

| when | what |
|---|---|
| compile time | §1's table; VX1009, VX1010, VX1011 |
| the operator runs, the lambda with it | a key component that is not a symbol (`ArgumentException`); a key or input type that does not group or aggregate (`VortexSchemaException`); a result that is not one, a chosen row's column as an input (`InvalidOperationException`); an order on a result with none (`ArgumentException`); a function's argument out of range (`ArgumentOutOfRangeException`); `As` or `AggAsync<TResult>` on a record that does not match (`VortexSchemaException`) |
| enumeration | a sum past its type (`OverflowException`); the file's own errors ([14-public-api.md](14-public-api.md) §7) |

Cancellation is taken at every batch boundary, and in the work a blocking operator does after its
input — a merge, a sort, a top-k — at every part of it, which may be long over millions of groups.
A query left early, by `break`, a `Take` or a cancellation, disposes as §2.4 says.

## 12. Not in it, on purpose

| left out | why |
|---|---|
| a `(g, r)` lambda, `g.Average(r.Celsius)` | it puts the row's probe beside the group, so a column neither key nor aggregated compiles and fails when it runs |
| tuples of values as results, `await foreach (var (city, n) in …)` | an overload per arity, eight of them in three places, a cap at eight, and names the compiler drops (CS8123); a record names the values, reads them as batches by default, and has no cap |
| a lambda over values, `Select(g => new CityHour(g.Key.Hour, g.Count()))` | it needs a conversion from `Sym<T>` to `T`, which would let `Math.Abs(r.Celsius) > 1` compile in a filter; `As<CityHour>()` gives the record |
| expression trees, `IQueryable` | rule 3 of [14-public-api.md](14-public-api.md) |
| arithmetic on results, `g.Sum(…) / g.Count()` | C# computes with results after the sink; filtering or ordering by a ratio is LINQ to objects over the delivered groups, or a query over the result's scan |
| joins, `let`, a query over a group's rows | one table, no binding per row |
| a synchronous twin, `ToList()` | async only where there is I/O, and no synchronous twin of an asynchronous call ([03-architecture.md](03-architecture.md) §1) |
| an implicit order of groups | it changed with the writer's encodings; an order is asked for (§6.3) |
| a switch to plain float sums | the reproducible sum costs nothing measurable on a pass over runs or a sorted key, a fifth more on a group by keyed row by row, a third more at a million groups, whose 32 bytes a group leave the cache (§13); it is more accurate, and a second semantics would be a second test matrix |
| approximate distinct counts, quantiles, grouping sets | later, as aggregates and as operators of the same pipeline |
| aggregates on the tool path | a later surface, over the same engine |

## 13. How it is tested

| promise | gate |
|---|---|
| the two syntaxes build one plan | the queries of the guide, written both ways, compared plan for plan |
| §1's boundary | `tests/MustNotCompile`, with exactly the expected diagnostics |
| every answer is right | each key form of §9.1 × each aggregate of §5 × degree 1 and the machine's, against LINQ to objects over `ToRecordsAsync`, on the conformance corpus and the type matrix |
| answers do not depend on the degree or the cut | every answer of §5 but the order-bound ones, float sums included, the same bits at degrees 1 to the machine's, over a file written in two row orders, and over a dataset cut into 1, 2 and 7 objects, before and after compaction |
| a `Where` on keys prunes as a filter | `LiveBlocks` equal to the equivalent `Where` on rows |
| the first batch waits for the first split | the time to first batch of a scan, a projection, a `Distinct` and a group by that streams, on two files sixteen times apart in size, locally and over the HTTP source with latency (`tests/Vorticity.Tests/IO/HttpRangeSegmentSource.cs`) |
| memory is the window and the open state | `LiveMemoryTests`: a streaming group by's peak flat as its groups grow a hundredfold; a top-k's proportional to `k`; a high-cardinality group by's at most the groups', not the degree times them |
| a `Take` reads what it uses | `Requests` and `BlocksDecoded` of a `Take(n)` bounded by the splits that hold `n` results and the window |
| nothing is allocated per batch, nor per group as batches | `ScanAllocationTests` extended to the result stream, `As` and the projection |
| a short range costs its rows | a complexity probe whose time per row stays flat as runs shorten |
| the operator is no slower than the loop by hand | the throughput gate, a group by per key form, with the hand-written loops of [encoded-forms.md](../guide/encoded-forms.md) as floor |
| a dictionary, run-end or settled key decodes no key block | `BlocksDecoded` counts only the inputs' blocks |
| the naming rule, and no I/O in a builder | `PublicSurfaceTests`: a member that returns an awaitable or a terminal stream ends in `Async`, a builder does not; a builder over a source that counts its reads reads nothing |

The figures this document quotes come from measurements made while it was written, on an arm64
machine of 14 cores: a million groups delivered as rows with a string key, 41 ms and 46 MiB, as
batches 2 ms; the indexed sum against a plain one, a count and a mean of a million rows grouped by a
run-end key in 12.3 ms against 13.5, by a dictionary key in 4.0 against 3.2, over a million groups
in 46 against 34 ms and 103 against 77 MiB, with one bit pattern over every cut of sixteen million
values where the plain sum gave seven, and the exact sum where the plain one was 1.7 × 10⁻¹⁴ off; a
pass costing 4.4 ns a row for a fold over a column and 32 ns for a composite group by, which the
sum's overhead is set against. Stage 0 turns each into a gate.

## 14. From the 0.4 surface

| 0.4 | now |
|---|---|
| `GroupBy(…).AggAsync(g => …)` | `GroupBy(…).Select(g => …)` |
| `await foreach (var (city, n) in ….AggAsync(g => (g.Key, g.Count())))`, a tuple per group | `….Select(g => (g.Key, g.Count())).As<CityCount>()`, read as batches or `ToRecordsAsync()` |
| `var (min, max) = await scan.AggAsync(a => (…, …))` | `MinMax m = await scan.AggAsync<MinMax>(a => (…, …))` |
| at most eight values in a selection | as many as the record has members |
| `GroupBy(r => (r.City, r.Day))`, then `g.Key.Item1` | `g.Key.City` |
| `Avg`, `AvgAsync` | `Average`, `AverageAsync` |
| `OrderBy(r => r.Day, descending: true)` | `OrderByDescending(r => r.Day)` |
| groups in key order for a dictionary or sorted key | `orderby g.Key`, free on a key that streams |
| the groups at the end of the pass | as soon as they are final, on a key that streams |
| `Sum` of an `int` as an `int`, throwing past it | as a `long` |
| `Sum` of a `float` as a `float` | as a `double` |
| a float sum whose last bits followed the degree | the same bits whatever the degree and the cut, and closer to the exact sum |
| `g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius)` | the same, or `g.Welford(x => x.Celsius)` through the aggregator's extension |
