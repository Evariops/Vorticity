# Queries: select, group, aggregate, as a flow of batches

What a typed scan answers beyond its rows: values computed from them, groups, aggregates over the
groups or over the whole scan, and what follows a group by (a filter on the groups, an order, a
top-k). The API has the shape of LINQ, in method and query syntax, and the plan is built by lambdas
over symbols that run once (see [the symbolic algebra](14-public-api.md#52-the-symbolic-algebra)). A
query runs as a flow of batches, block by block on the encoded form. It answers as soon as its first
batch is final, holds what is still open rather than what it read, and stops its reads when its
consumer stops. The rules every signature follows are those of [14-public-api.md](14-public-api.md),
the engine is described in [03-architecture.md](03-architecture.md), the meaning of a predicate in
[08-semantics.md](08-semantics.md), and the order of keys in [key order per
dtype](12-index-reads.md#34-key-order-per-dtype).

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

One value comes as itself, and several come into a record. `select g.Count()` is an
`Aggregation<long>`, one value per group. A `select` of several values has no .NET type until a
record gives it one: the record names and types the values, and `As<TRecord>()` makes the result a
scan of that record, read as batches or as records like a file's (see [results](#61-results-select)
and [results are scans](#7-results-are-scans)). It is the same price a scan already asks for reading
two columns of a file, and it buys names, batches by default, and no cap on the number of values.

| on | operators | returns |
|---|---|---|
| `Scan<TRecord>` | `Where`, `Rows`, `OrderBy`, `OrderByDescending`, `With` | `Scan<TRecord>` |
| `Scan<TRecord>` | `Select(r => e)`, or `Select(r => (e1, e2, …))` | `Projection<T>`, or `Projection` for several values ([select on a scan](#73-select-on-a-scan)) |
| `Scan<TRecord>` | `GroupBy(r => …)` | `GroupedScan<TRecord, TKey>` ([keys](#4-keys)) |
| `GroupedScan` | `Where(g => …)`, `OrderBy`, `OrderByDescending`, `Skip`, `Take` | `GroupedScan`, and once ordered `OrderedGroupedScan`, which adds `ThenBy` and `ThenByDescending` |
| `GroupedScan` | `Select(g => r)`, or `Select(g => (r1, r2, …))` | `Aggregation<T>`, or `Aggregation` for several values ([results](#61-results-select)) |
| `Aggregation<T>`, `Aggregation`, `Projection<T>`, `Projection` | `Skip`, `Take`, and `Distinct()` on a projection | the same kind, or an aggregation for `Distinct` |
| `Aggregation<T>`, `Aggregation`, `Projection<T>`, `Projection` | `As<TRecord>()` | `Scan<TRecord>` over the result ([`As<TRecord>`](#71-astrecord)) |
| `Aggregation<T>`, `Projection<T>` | `WithCancellation`, `ToListAsync`, `ToArrayAsync`, `ToValuesAsync` | the same kind, `ValueTask<…>` ([values and records](#72-values-and-records)), or `IAsyncEnumerable<T>` |
| `Scan<TRecord>` | `AggregateAsync(a => e)`, `AggregateAsync<TResult>(a => (e1, e2, …))`, `CountAsync`, `AverageAsync` and the other single answers | `ValueTask<…>` ([one row](#8-one-row-the-aggregates-of-the-whole-scan)) |

An operator that returns a query only describes it and runs nothing. `await foreach` runs it,
`ExplainAsync` plans it without reading a data segment, and a method that returns a `ValueTask` runs
at once. The names follow the rule of [14-public-api.md](14-public-api.md): `Async` names what runs.
A builder never reads, because what it checks (a column, a type, a record's members) is in the schema
the open already read. The one read a binding may make is the operating system's time-zone database,
once, when a zoned timestamp binds (see [temporal
extensions](07-dotnet-mapping.md#3-temporal-extensions-one-resolution-at-binding)), and the BCL has
no asynchronous form of it.

The plan ends at the right place. `Aggregation<T>` and `Projection<T>`, of one value, are enumerated
with `await foreach`, through the pattern a scan is enumerated by, rather than as an
`IAsyncEnumerable<T>`. VSTHRD200, an error in this repository, requires a method that returns a
stream to await to end in `Async`, and `Select` only builds a query. `WithCancellation` gives them a
token as it gives a scan one, and `ToValuesAsync()` hands their values to
`System.Linq.AsyncEnumerable`, whose operators then run on the values delivered. A `Where`, an
`OrderBy` or a `GroupBy` written there is LINQ to objects, correct but not pushed down, while their
own `Skip`, `Take`, `ToListAsync` and `As` stay in the plan. `Aggregation` and `Projection`, of
several values, are not enumerable at all, and `As<TRecord>()` is how they are read. Either way,
`As<TRecord>()` returns a scan, on which the plan's own operators continue (see
[`As<TRecord>`](#71-astrecord)).

What does not compile marks where a query over one table ends:

| written | why |
|---|---|
| a column outside an aggregate after `into g`, or in `g => …` | the row's probe is out of scope. A group's columns only exist inside the lambda of an aggregate, so a result is a key or an aggregate (CS0103) |
| `let`, `join`, a second `from` | there is no `Select` of an anonymous type, no `Join` and no `SelectMany` (CS0029, CS1936) |
| arithmetic on a column or a result | the operators are `[Obsolete(error)]`, since computing with results is C# after the sink |
| a literal of a different type from the symbol's | as in a filter ([the symbolic algebra](14-public-api.md#52-the-symbolic-algebra)) |
| `await` on a builder, `foreach` over a `GroupedScan` | a builder is not awaitable, and a group is not a value: a `Select` says what to deliver of it |
| several values read without a record: `await foreach` over a `select (a, b)`, `AggregateAsync(a => (x, y))` | several values have no .NET type until a record gives them one (CS8411, CS0411) |

What compiles but still cannot run throws when the operator runs ([errors](#11-errors-cancellation-disposal)),
and an analyzer flags it at compile time where it can see it: VX1009 for a key component that is not
a symbol, and VX1010 for an `As<TRecord>` or an `AggregateAsync<TResult>` whose record does not match
the values. VX1011 accompanies the compiler's error of the last row and names the fix, `As<TRecord>()`
and the record to declare.

## 2. The flow

### 2.1 Batches, pulled

Every source, operator and sink of a query exchanges batches (blocks in the arena described in [the
object model](03-architecture.md#33-arrays-an-arena-not-an-object-graph), encoded or canonical), and
nothing else crosses an `await`. A `MoveNextAsync` is a batch, and every loop inside a batch is
synchronous (see [two phases](03-architecture.md#36-two-phases-materialize-then-execute)). The
consumer pulls: a stage computes its next batch when asked for one, and its sources decode ahead of it
by a bounded window, the read-ahead and the lanes described in
[parallelism](09-contracts.md#2-parallelism). Nothing is buffered beyond that window, so a consumer
slower than the file is the backpressure. Every enumerator is written by hand and allocates nothing
per batch (see [async enumeration](03-architecture.md#37-async-enumeration)).

A query's result is a stream of batches too, with one column per element of its `select`. One value
per row or per group is a view over them, a `T` at a time, and several values are read through the
record of `As<TRecord>()`, as batches whose columns are borrowed or as records. The only allocations a
query makes per row are the strings and objects those views create (see [values and
records](#72-values-and-records)).

### 2.2 What each operator holds, and when it answers

| operator | holds | its first batch after |
|---|---|---|
| a scan, a filter, `Select` on a scan, `Truncate`, `Bucket`, `Skip`, `Take` | its window | its first split |
| `Distinct` | the values met, or only the last one on a column that streams, and through the core its caches, parts and spill ([`Distinct`](#74-distinct)) | its first split, since a value goes out the first time it is met (through the core, once it enters its part's set) |
| `GroupBy` on a key that streams ([below](#23-a-group-by-that-streams)) | the groups still open | the first group closed |
| `GroupBy` on any other key | one state per group | the end of its input |
| `Where` on groups, `Select` of groups, `Skip`, `Take` | nothing more | its input's |
| `OrderBy` on a key that streams in that order | nothing more | its input's |
| `OrderBy` then `Take(k)` | `k` groups | the end of its input |
| `OrderBy`, any other | every group | the end of its input |
| `AggregateAsync` and the single answers | one state per aggregate | the end of its input, or at once when the statistics answer ([one row](#8-one-row-the-aggregates-of-the-whole-scan)) |

This gives three promises, each backed by a gate ([how it is tested](#13-how-it-is-tested)):

* The first batch of a streaming query only waits for its first split, not for the file, so its time
  to first batch does not grow with the file.
* Memory is the window plus the open state. A streaming group by holds the groups still open, however
  many it delivers. A top-k on the key holds one and a half times `k` groups per lane, plus a batch. A
  blocking group by, including a top-k on an aggregate, holds one state per group at any degree (see
  [parallelism and the merge](#94-parallelism-and-the-merge)), since a group's value is only known at
  its end. Nothing is held per row read.
* Nothing is allocated per batch in steady state on every path that delivers batches, and nothing per
  group on the path that delivers groups as batches ([`As<TRecord>`](#71-astrecord)).

### 2.3 A group by that streams

A group is final when no row still to come can hold its key. On a key component the statistics say
is sorted, or on a non-decreasing function of one ([value expressions](#3-value-expressions)), that
happens as soon as the value changes: the groups of the previous value go out in key order, and their
states are reused.

On a column the zone maps cover, a group is final as soon as every block still to come has bounds
above the group's value. That holds for data appended roughly in time order without being sorted: the
groups below the smallest bound still to come go out in key order, and the memory is the groups the
overlap keeps open. The zone maps are read before the pass, for a key of one integer column as
stored (a timestamp's included), under no order other than the key's ascending one. The groups stream
when no value waits past its own block for more than an eighth of the blocks, and otherwise go out
once the pass has run. Each range a lane groups absorbs the few groups the ranges before it left open,
and stands for them from then on, rather than folding all of its own into them.

Compared with the blocking pass, a nearly sorted key of a million values over four million rows
answers first after 1 to 2 ms rather than 34. On one lane it holds a fifteenth of the memory and runs
in 0.84 of the time. On fourteen lanes it holds a third of the memory but runs 2.1 times as long
(12 ms against 5.7), because its ranges are taken in series in row order, while the blocking pass
builds its parts side by side. So on several lanes the pass blocks, unless the query asks for a window
(`Take`) or its budget is short of 32 bytes per row of the source, where the stream's memory pays. On
one lane it streams.

On such a key:

* one key holds one open group, and a composite key with such a component holds the groups of that
  component's current value, so `(Day, City)` on a file sorted by time holds one day's cities
* `orderby g.Key` ascending, and on a composite key an order that starts with that component, sorts
  nothing
* `Take(n)` stops the read once `n` groups have gone out, so `group r by
  r.At.Truncate(CalendarUnit.Day)` followed by `Take(7)` reads seven days of a year
* `Where` and `Skip` on the groups stream along with it

A group by does not stream on a key with no such component, under an order by an aggregate, or on a
sorted column that holds a NaN, which is not sorted at all (see [key order per
dtype](12-index-reads.md#34-key-order-per-dtype)). On a dataset it only streams on the first column of
the clustering key, or a function of it, where the dataset reads its rows in key order, merging the
objects and opening each one once it may hold the next row (see [order beyond the
lookup](13-dataset.md#66-order-beyond-the-lookup)). The null group of the streaming component is
final at the end, and comes last.

Under a descending order of the component, a group by streams backwards when a window follows the
order, which stops the read: `orderby g.Key descending take 7` reads the last seven days of a year,
not the whole year. A sorted column's splits come last one first, each in file order, which is all a
group by needs: the group a split leaves open is the one of its first row, the others are final, and
the groups closed together are sorted. A dataset reads its rows in key order backwards. Nothing before
the order may depend on row order (a window, a first or last row, a tie, a custom aggregate), since
backwards the rows of a group come last first and a window would take the groups from the other end.
Without a window, a descending order uses the blocking pass, on every lane and with the blocks the
zone maps settle (see [statistics and zone maps](#93-statistics-and-zone-maps)). The plan says which
component streams, or why none does ([plan and metrics](#10-plan-and-metrics)).

### 2.4 Stopping early

When a `Take` is satisfied, a loop is left by `break` or a cancellation arrives, the query stops
asking its sources for batches, so nothing past the window in flight is read or decoded. A `Take`
narrows the read-ahead to what it can still use, so `Take(10)` over a projection decodes one split,
not a window of them. `DisposeAsync` awaits the reads in flight and returns every pooled buffer,
without blocking a thread.

### 2.5 In parallel, in order

At a degree above one, the splits of a scan decode on their lanes and come back in row order through
a bounded window ([parallelism](09-contracts.md#2-parallelism)). A streaming operator either runs on
that ordered stream (decoding, the heavy part, is parallel, and grouping a sorted key costs one lookup
per run), or runs per range of rows and joins the groups that straddle two ranges. Either way its
batches go out in order.

A group by that streams does the second at a degree above one, because grouping is as heavy as
decoding on a composite key or a custom aggregate, and a single thread would hold the lanes back. Its
ranges are cut at the file's boundaries, the first one a block and each next one twice as long, up to
a quarter of a lane's share, with as many in flight as lanes. Each range is grouped on its lane, then
follows the groups still open in row order, which merges the group that straddles the boundary and
carries along the last value met. A blocking operator runs per range on its lanes and merges at the
end (see [parallelism and the merge](#94-parallelism-and-the-merge)). Nothing it holds goes out before
its input ends, so it has no order to keep while it runs.

### 2.6 The first batch of each source

* For a file, the first batch of a filtered scan waits for the zone maps of the columns it filters
  (one segment each, however many zones it holds), then for the first window's data, so a file sixteen
  times larger asks for as many segments. Nothing walks the whole file's zones first. The scan only
  asks an exact source (a sorted column, sorted runs) for the rows it proves where the file may hold
  one, which its statistics and its index directory say without a read. Reading the first window's
  zones before the others' would leave the first batch behind the same two dependent reads, save the
  bytes of the other zones and cost one more request, so the zone maps are read whole (see [the
  performance contract](14-public-api.md#9-the-performance-contract-and-the-gates-that-hold-it)).
* For a dataset, the first batch only waits for the first object. Once the consumer reads past it,
  with prefetch on, the next object is opened and its first batch read and decoded while the current
  one is still being read. An object's open, its first request and its first decode wait on nothing
  the consumer does, at most two objects are open, and a consumer that stops at the first batch opens
  one. A group by reads the objects side by side at a degree above one (see [parallelism and the
  merge](#94-parallelism-and-the-merge) and [13-dataset.md](13-dataset.md)).
* For a result, which is a stream itself ([`As<TRecord>`](#71-astrecord)), the first batch is its
  query's first.

## 3. Value expressions

A value expression is a column's symbol, or a function of one. It is valid in a row filter, as a key
component, as the input of an aggregate and as an element of a projection, but not as the key of a
scan's `OrderBy`, which walks a column's key source (see [rows in key
order](12-index-reads.md#5-rows-in-key-order)).

| function | on | gives |
|---|---|---|
| `Truncate(CalendarUnit unit)` | `DateTime`, `DateTimeOffset`, `DateOnly`, and their nullable forms | the start of the unit holding the value: `Minute`, `Hour`, `Day`, `Week` (Monday, ISO 8601), `Month`, `Quarter`, `Year`. A `DateOnly` takes the units from `Day` up |
| `Truncate(CalendarUnit unit, TimeZoneInfo zone)` | `DateTime`, `DateTimeOffset` | the same, on the calendar of `zone`, so a day in Paris starts at 22:00 or 23:00 UTC |
| `Bucket(TimeSpan width)` | `DateTime`, `DateTimeOffset` | `epoch + ⌊(value − epoch) / width⌋ × width`, with the epoch at 1970-01-01T00:00Z, the storage's own origin |
| `Bucket(T width)` | integers, floats | `⌊value / width⌋ × width`, in integers for an integer, rounded to the column's type for a float |

The result has the column's type. A naive or UTC timestamp, bound as `DateTime`, is truncated as it
is stored, and a zoned one, bound as `DateTimeOffset`, on the calendar of its column's zone unless a
zone is given (see [temporal
extensions](07-dotnet-mapping.md#3-temporal-extensions-one-resolution-at-binding)). A null stays null
and a NaN stays NaN. A width that is not positive or that the column's unit cannot divide, or a unit
finer than a `DateOnly`'s day, throws `ArgumentOutOfRangeException` when the lambda runs. A decimal's
bucket is not implemented yet and throws `NotSupportedException`. `CalendarUnit` is not `TimeUnit`,
which is the unit a timestamp is stored in.

Over integers both functions are floors: non-decreasing and never above the value. Where the floor
would fall below the storage's least value, they give that value, so one function holds over the
whole storage. A zone's calendar holds over the years a `DateTime` covers. An instant before them
takes the storage's least value, and one after them the start of the last unit they cover. Three
things follow:

1. In a filter, a comparison with a constant becomes the range of column values whose image compares
   that way. `r.At.Truncate(CalendarUnit.Day) == day` is `r.At >= day && r.At < day + 1 day`, the
   least value whose image reaches the constant being found by bisection on the function. The scan
   takes the range, so every structure of the column (zone maps, statistics, sorted runs, indexes)
   prunes, counts and locates for it as for the range written by hand, and a null row is unknown to
   both, under a negation too. Where there is no such range (a float's bucket, an `In`, two columns
   compared), a zone's bounds go through the function and prune as the column's would.
2. A key the statistics say is sorted stays sorted through the function, so it still groups by runs
   ([keys, block by block](#91-keys-block-by-block)) and still streams ([a group by that
   streams](#23-a-group-by-that-streams)).
3. A block whose bounds map to one value holds one key, and some aggregates of it can be answered
   from the zone map ([statistics and zone maps](#93-statistics-and-zone-maps)).

On the encoded form a function is evaluated once per dictionary value, per run and per constant, and
per value on a canonical block, in the storage's integers: a timestamp's `i64` in its unit, months
and years by a days-from-civil computation, and no `DateTime` per value. A zone is applied through
its offset intervals. The transitions of a year are found the first time an instant of that year is
met and then kept (two a year where daylight saving applies), so a value takes its interval's offset,
an integer truncation and the offset back, a lookup among the two or three intervals of its year. The
repeated hour in autumn is two hours, with two offsets, and so two buckets.

Other functions (date parts, text functions, a coalesce) will take the same path: a function on a
symbol, evaluated where the block lies.

## 4. Keys

```csharp
.GroupBy(r => r.City)                                            // g.Key: Sym<string>
.GroupBy(r => (r.City, r.Day))                                   // g.Key.City, g.Key.Day
.GroupBy(r => (Hour: r.At.Truncate(CalendarUnit.Hour), r.City))  // g.Key.Hour, g.Key.City
```

`GroupBy<T>(Func<Probe<TRecord>, Sym<T>>)` groups by one component, and
`GroupBy<TKey>(Func<Probe<TRecord>, TKey>) where TKey : struct, ITuple` by a tuple of them, of any
length. The tuple's type is captured whole, so its names, written or inferred from `r.City`, become
the names of `g.Key`. Overloads by arity, such as `GroupBy<TKey1, TKey2>`, would win over it and lose
the names, so there are none.

`g.Key` is the tuple of symbols the key lambda returned, and each component is a result of the group
([results](#61-results-select)). A composite `g.Key` as a whole is not a result, only its components
are.

A component is a value expression ([value expressions](#3-value-expressions)) of a type that groups:
an integer, a float, a decimal of 128 or 256 bits, a uuid, a bool, text, binary, a temporal type by
its storage, or an enum by its underlying integer. A list, a record or a map throws
`VortexSchemaException` when `GroupBy` runs. A component that is not a symbol of the scan, such as a
literal or a captured value, throws `ArgumentException`, and VX1009 flags it at compile time.

Two keys are equal as the file stores them. A null is a group of its own, component by component.
Every NaN falls into one group, and −0.0 and +0.0 are one group. Text and binary compare bytewise, a
decimal by its unscaled value (since a column has one scale), and a timestamp by its integer.

## 5. Aggregates

The aggregates are the members of `Group<TRecord, TKey>`, and of `Aggregates<TRecord>` for
`AggregateAsync` ([one row](#8-one-row-the-aggregates-of-the-whole-scan)), which has every one but `Key`. Each takes a
lambda over the rows' probe, as LINQ's do, and the probe only exists inside it.

| member | result | skips | with no value |
|---|---|---|---|
| `Count()` | `long` | nothing | 0 |
| `Count(x => p)` | `long` | the rows where `p` is not true | 0 |
| `CountDistinct(x => e)` | `long` | null, and every NaN counts as one value | 0 |
| `Any(x => p)`, `All(x => p)` | `bool` | the rows where `p` is not true | `false`, `true` |
| `Sum(x => e)` | see [sums](#51-sums-exact-and-the-same-bits-whatever-the-cut) | null, NaN | 0 |
| `Average(x => e)` | `double?` | null, NaN | null |
| `Min(x => e)`, `Max(x => e)` | the symbol's type | null, NaN | null, or the default of a value type that is not nullable |
| `Variance(x => e)`, `StandardDeviation(x => e)` | `double?`, of the sample (n − 1) | null, NaN | null below two values |
| `Aggregate<T, TAggregator, TState>(x => e)` | `TState` | what the aggregator skips | `TAggregator.Seed()` |

`p` is a predicate over the rows, in the language of `Where`, and a row counts when it is true, not
when it is unknown (see [three-valued logic](08-semantics.md#3-three-valued-logic)).
`All(x => p)` is `Count(x => p) == Count()`, and `Any(x => p)` is `Count(x => p) > 0`. "With no
value" only happens in a group for an aggregate of a filtered group ([a filtered
group](#52-a-filtered-group)) or of a column whose every row is null, and in `AggregateAsync` for a
scan that keeps no row.

### 5.1 Sums: exact, and the same bits whatever the cut

| column | sum | accumulated |
|---|---|---|
| `sbyte`, `short`, `int` | `long` | exactly, in integers |
| `byte`, `ushort`, `uint` | `ulong` | exactly, in integers |
| `long`, `ulong` | its own type | exactly, in integers |
| `decimal`, `VortexDecimal`, and `Int128`, `UInt128`, `BigInteger` over a decimal of scale 0 | its own type | exactly, in 128 bits spilled into 320 ([aggregates.md](../guide/aggregates.md)) |
| `Half`, `float`, `double` | `double` | reproducibly: the same bits whatever the split |

A sum widens what could overflow on the way and keeps the type of what could not, and it only throws
`OverflowException` when the exact total does not fit the type it is delivered as. A nullable column
sums as its type without the nullability. The engine accumulates an integer sum in the narrowest
integer the statistics prove cannot overflow (the rows times the largest magnitude they give): a
64-bit state where they prove it, 128 bits where they prove nothing. The width is the engine's choice,
and the exactness is the promise.

A float sum is the same bits at every degree of parallelism, in every row order, and under every
split of the data into chunks, files or objects, so compacting a dataset changes no sum. It is an
indexed sum (Demmel and Nguyen, ReproBLAS). All sums share one grid of exponent bins, bin `j` having
the unit `2^(27j − 1074)`, and each sum keeps three of them: the bin its largest value calls for and
the two below, at least 53 bits under that value. A value is split from the top bin down by the
pre-rounding of an extractor `1.5 · 2^52` units wide, each part being an integer multiple of its
bin's unit. A bin above a value takes nothing of it, so the value's parts do not depend on when the
sum's top moved, and each bin is the exact sum of its parts in a `long`. When the top moves up, the
bins it leaves below the three fall out, as a final top that high would not keep them either. Nothing
carries from one bin into another, since a later drop would make that depend on the order. The answer
is the bins' exact total, rounded once to the nearest double. The error is that rounding plus the bits
values hold more than 53 below the largest one, which makes the sum more accurate than a plain one,
whose error grows with the rows.

The top is each sum's own, taken from its own values, and no statistics are read, so a group of small
values keeps its precision next to a group of large ones, a result's scan and a dataset sum exactly
as a file does, and a filter changes no grid. A part is at most 2^26 units, so a sum takes 2^36
values, some 69 billion, in one group or one scan, and throws `OverflowException` past that rather
than lose its exactness. A NaN is skipped. A sum holding +∞ and not −∞ is +∞, and one holding both is
NaN, as IEEE 754 says.

An average is that sum divided by the count. A variance comes from the reproducible sums of `x − c`
and `(x − c)²`, where `c` is the midpoint of the column's bounds over the whole source where its
statistics hold them (a file's), and zero where they do not, fixed per query. The nearer `c` lies to
a group's mean, the fewer digits the difference of the two sums cancels. A dataset's `c` is zero,
because the bounds of its entries, which a deletion loosens and a compaction tightens, would move `c`,
and the bits with it, from one compaction to the next.

Every answer in this section is therefore the same bits at every degree and in every split, except
those that depend on row order by definition: `First`, `Last`, and a tie of `MinBy` or `MaxBy`.

### 5.2 A filtered group

`g.Where(x => p)` is the group's rows where `p` is true, with every aggregate and its key, which is
SQL's `FILTER (WHERE …)`:

```csharp
select (g.Key, g.Count(), g.Where(x => x.Status >= 500).Count(), g.Where(x => x.Status >= 500).Average(x => x.DurationMs))
```

`g.Count(x => p)` is `g.Where(x => p).Count()`, two `Where` calls join with `AND`, and
`a.Where(x => p)` filters the scan's aggregates the same way. The predicate is evaluated once per
batch however many aggregates read it, on the encoded form as a scan's filter is, and the columns it
reads join the pass, which the plan shows ([plan and metrics](#10-plan-and-metrics)).

### 5.3 A chosen row

`g.First()` and `g.Last()` are the group's first and last rows in file order, the order of the rows
the scan keeps, ignoring its `OrderBy`. `g.MinBy(x => e)` and `g.MaxBy(x => e)` are the row holding
the smallest and largest value of `e` (never a null or a NaN), the first in file order on a tie. Each
is a probe whose columns are results:

```csharp
select (g.Key, g.First().Price, g.Max(x => x.Price), g.Min(x => x.Price), g.Last().Price)   // open, high, low, close
select (g.Key, g.Max(x => x.Celsius), g.MaxBy(x => x.Celsius).At)                           // and when
```

A chosen row is kept as its position plus the value it is chosen by, and the columns read from it are
fetched once the groups are known, by position: for the groups the result delivers, or, when a
`Where` or an `OrderBy` on groups reads one, for the groups that reach the first of them. One take
serves every chosen row, reads each row once whatever choices it serves, and only decodes those rows
where the encoding allows it ([90-registry.md](90-registry.md)): a top ten reads the chunks of ten
rows, and an open and a close read their chunks once. Carrying the columns along in the pass would
only spare the take's second decode, a few percent of a query that reads every group's row, so the
engine always fetches. A streaming group by fetches them per batch of groups it closes, after its
operators.

Columns read from one chosen row share it, so `g.MaxBy(x => x.Celsius).At` and `.City` come from the
same row. When `MinBy` or `MaxBy` has no candidate, its columns are null, or the default of a value
type that is not nullable. The order is the file's at every degree of parallelism: a range of rows
keeps its own, and the merge keeps the earlier range's row. A chosen row's column is a result, not a
row, so it is no aggregate's input, and naming it in one throws `InvalidOperationException`.

### 5.4 An aggregator of one's own

`IAggregator` and `IEncodedAggregator` work as described in [aggregates.md](../guide/aggregates.md).
The call names three types, since the compiler infers none of them from the column, so an aggregator
ships an extension member that makes it read like a built-in:

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

A range of rows of one group costs the aggregator its rows, not its batch's. A range of fewer rows
than its batch has words of selection is folded row by row ([keys, block by
block](#91-keys-block-by-block)).

A state is a result like any other, so its type must be one a column can hold: a number, a decimal,
text, a date or a time, a uuid, a bool, or a `[VortexRecord]`, which a result holds as a struct
column of its members and which a record of the selection declares as a member of that type. `Select`
refuses a state of any other type with `VortexSchemaException`, while `AggregateAsync` of a single
answer over a whole scan delivers any state as it is.

### 5.5 One aggregate, wherever it is written

Two aggregates are the same when they have the same function, the same input expression and the same
filter, so the `g.Count()` of a `where`, an `orderby` and a `select` is counted once. An average
shares the sum and the count of its column with a sum and a count of that column.

## 6. After the group by

### 6.1 Results: `Select`

`Select(g => r)` delivers an `Aggregation<T>`, one `T` per group: `select g.Count()`,
`select g.Key`. `Select(g => (r1, r2, …))`, with any number of elements, delivers an `Aggregation`,
one row of values per group, which a record reads ([`As<TRecord>`](#71-astrecord)). A single overload
takes the whole tuple, boxed once when the lambda runs, and the record gives the values their names
and types. The per-arity overloads of version 0.4, which stopped at eight and returned tuples whose
names the compiler drops (CS8123), are gone. An element is a key component, an aggregate, or a column
of a chosen row. A symbol of anything else, captured from outside the lambda, throws
`InvalidOperationException` when `Select` runs.

### 6.2 Groups kept: `Where`

`Where(g => p)` keeps the groups where `p` is true. `p` compares results (key components, aggregates,
chosen rows' columns) with a literal of their type or with one another, joined by `&&`, `||` and `!`.
It is a filter's language over results, three-valued, so a null average fails `> 30.0` and
`== null` tests it (see [three-valued logic](08-semantics.md#3-three-valued-logic)). An aggregate
named only in a `Where` is computed but not delivered.

A conjunct that only names key components, in a `Where` before any `OrderBy`, `Skip` or `Take`,
filters rows instead, which keeps the same groups. `where g.Key.City == "Paris"` becomes
`r.City == "Paris"` and prunes like a filter, and `where g.Key.Hour >= start` becomes
`r.At.Truncate(CalendarUnit.Hour) >= start` and prunes through its function ([value
expressions](#3-value-expressions)).

### 6.3 Order: `OrderBy`, `ThenBy`

`OrderBy` and `OrderByDescending`, then `ThenBy` and `ThenByDescending`, take a result. A result
orders as its type does: numbers by value, with −0.0 equal to +0.0 and NaN after +∞, text and binary
bytewise, `false` before `true`, temporal values in time, a uuid bytewise, and an enum by its
underlying value. Nulls come last in both directions, as rows in key order do (see [rows in key
order](12-index-reads.md#5-rows-in-key-order)). A result with no order, such as an aggregator's state,
throws `ArgumentException` when `OrderBy` runs. Ties past the last key are broken by the group's key,
so an ordered query delivers the same groups in the same order at every degree of parallelism and
under every split of its data, its float aggregates being the same bits there ([sums](#51-sums-exact-and-the-same-bits-whatever-the-cut)).

Without `OrderBy`, the order of the groups is unspecified. It follows the encodings met and the
degree, and a group by that streams delivers them in the order of its streaming component. Version 0.4
delivered key order when the key was a dictionary or sorted. An order now has to be asked for, and
`orderby g.Key` on a key that streams sorts nothing ([a group by that
streams](#23-a-group-by-that-streams)).

### 6.4 `Skip` and `Take`

These work as in LINQ: after an order, the groups past the first `Skip`, `Take` of them, and without
an order, some groups. Written before or after the `select`, the result is the same.

Under an order, the groups are ranked in a heap of `Skip + Take` on the order's results alone. The
others are compared but never delivered, at a cost of `G log k` and a memory of `k` groups. The groups
that tie with the last one kept are then ranked by their key, read from the groups' index where it
orders as the key's column would, so a top-k on a count that a million text keys share copies none of
them. Many groups for a few kept are ranked in chunks at once, as many chunks as the degree, each of
65 536 groups and at least sixteen times `k`: each chunk's first `k` on a task of its own, with the
results it orders by read into arrays the chunk's size, then those candidates once. Every group of
the overall first `k` is among the first `k` of its chunk, ties being broken by the key in both, so
the groups and their order are those of a single ranking. On a million text keys, fourteen chunks rank
in 0.68 ms against 2.6. A ranking that would build a column rather than read its results into arrays
(a text result, a key its index does not order) ranks at once. Under no order, or under an order a
streaming group by already delivers, a `Take` stops the read once it is served ([stopping
early](#24-stopping-early)).

Under no order, on a key that does not stream, the windows that open the operators reach `Skip + Take`
groups, without a promise about which ones. One lane keeps the first it meets alone, the others being
dropped once they pass one and a half times as many, and dropped again whenever their key comes back,
so the groups kept hold every one of their rows. Several lanes hold every group, since each would keep
groups the others drop.

An order on the key alone, with the windows right after it, does not need to hold the other groups at
all. Each lane of the pass keeps the `Skip + Take` best keys it has met, ranked as the result ranks
them, and trims back to them once they pass one and a half times as many. A trimmed key lies past the
worst a lane keeps, which only gets better, so it never comes back into the top, and the groups kept
hold all their rows. Once a lane has trimmed, a row whose integer key lies past the worst it keeps is
neither grouped nor folded. A filter on the groups before the window keeps every group, since it could
remove some of the top.

An order on an integer column's `Max` alone, from the largest, or its `Min`, from the smallest, with
the query reading nothing else of the groups but their key, keeps its top the same way. A trimmed
group held a value past the worst a lane keeps, which only gets better, so if it is made again it only
enters the top with a better value than every one it held, and the rows it lost change nothing. Once a
lane has trimmed, a row whose value falls short of the worst it keeps (by the order's 64-bit keys, ties
kept for the key to rank) is neither grouped nor folded: one pass, and no table of every group. On a
million keys over twenty million rows, `order by max take 100` takes a sixth of the time at one lane
and at fourteen. A float's extreme keeps every group, since its NaN comes first from the largest and
is passed over once a number comes, so a group's extreme would not only get better.

The operators apply in the order written, as in LINQ, so a `Where` after a `Take` filters the groups
taken.

## 7. Results are scans

### 7.1 `As<TRecord>`

`As<TRecord>()`, on an aggregation or a projection of one value or several, returns a
`Scan<TRecord>` whose source is the query's result: the stream of batches described in [batches,
pulled](#21-batches-pulled), with columns named after the members of `TRecord`, an
`IVortexRecord<TRecord>` whose members, in declaration order, take the elements of the `select` in
order. It is how several values are read, and how one value becomes a scan too. A member's type must
be the element's or its nullable form. Otherwise `As` throws `VortexSchemaException` naming the
position and both types, and VX1010 flags what the compiler sees. Everything a scan does, a result's
scan does, and as a stream:

```csharp
await foreach (var (hour, city, readings, mean) in hourly)        // the Scan<CityHour> of the shape above: Columns<CityHour>, borrowed
{
    ReadOnlySpan<long> counts = readings.Values;                    // no copy, and text read as spans
}
```

| on a result's scan | what it does |
|---|---|
| `await foreach`, `ToBatchesAsync`, `ToRecordsAsync` | the result's batches, borrowed or owned, or its records |
| `Where`, `Select`, `GroupBy`, `AggregateAsync`, `CountAsync` and the single answers | the same operators over the result's batches: a filter evaluated on each, a projection, an aggregate of an aggregate (daily from hourly) |
| `OrderBy`, `OrderByDescending` | free when the result arrives in that order, from a group by that streams on that key or an `orderby` in its query. Otherwise the result is sorted, a blocking stage under the session's memory budget, in memory while it fits and in runs written to scratch and merged back past that ([memory](#95-memory)) |
| `Keys(r => …)` | the key cursor over the result's keys, sorted in memory unless the result arrives in their order, reserved under the session's memory budget until the cursor is disposed |
| `Rows(range)`, `Rows(indices)` | positions in the order the result is delivered, read as a skip and a take of the stream |
| `With(options)` | the options of what runs on the result, while the query keeps its own |
| `ExplainAsync`, `Metrics` | the query's plan with the result's operators after it, and what the whole pipeline did |

A result has no zone maps and no indexes, so its filters evaluate every batch, and a filter on a key
is better written before the `select`, where it prunes ([groups kept](#62-groups-kept-where)). A
result's scan is single use, as every scan is ([concurrency](09-contracts.md#1-concurrency-and-thread-safety)),
and enumerating it runs its query.

### 7.2 Values and records

`await foreach` over an `Aggregation<T>` or a `Projection<T>` delivers one `T` per group or per row, a
view over the result's batches with a `string` per text value. Several values are read through
`As<TRecord>()`: as batches, the default, where nothing is allocated per group and text is read as
spans, or as records through `ToRecordsAsync()`, a path of rows priced accordingly (see [the
sinks](14-public-api.md#56-the-sinks)). Over a million groups keyed by text, rows cost 41 ms and
46 MiB, against 2 ms through the batches. The `ToListAsync` and `ToArrayAsync` of a query of one
value are its own, and fill the list from the batches without an `await` per element.

### 7.3 `Select` on a scan

`scan.Select(r => e)` delivers a `Projection<T>`, one value per row the scan keeps, in file order, or
in key order under the scan's `OrderBy`. `scan.Select(r => (e1, e2, …))` delivers a `Projection`,
several values per row, read through a record ([`As<TRecord>`](#71-astrecord)), so
`scan.Select(r => (r.At.Truncate(CalendarUnit.Day), r.City)).As<DayCity>()` is a scan of computed
columns. An element is a value expression ([value expressions](#3-value-expressions)), never an
aggregate. The scan reads the columns that its elements and its filter name, and computes each
function a batch at a time into a column of the result. In query syntax, `select r.City` is this,
while `select r` alone is no projection, and the query is the scan itself, delivering batches.
`Take(n)` stops reading after `n` rows ([stopping early](#24-stopping-early)).

### 7.4 `Distinct`

`projection.Distinct()` is a group by the projected values with no aggregate: one element per distinct
value, an `Aggregation<T>` for one value and an `Aggregation` read through a record for several,
unordered. It streams whatever the key, since a value with no aggregate is final the first time it is
met ([what each operator holds](#22-what-each-operator-holds-and-when-it-answers)).
`(from r in scan select r.City).Distinct()` groups a dictionary column by code.

On one lane, under a window, on a component that streams, or on a key the statistics bound to two
million values or fewer whose index fits in half its budget, the values are taken on the reader's
thread as they are met, the key numbered the way a group by numbers it (by its value, in pages, within
the statistics' bounds). Otherwise, on a key the core holds, they are many lanes' work. The pass runs
on its own task, on one lane fewer than the degree, with the reader counting in it, through the lean
core (α at 1, a part applied from 256 entries, batches of a kilobyte), and each value is announced as
it enters its part's set, under the part's lock, so once, as the rows come. Under its budget the core
spills. An evicted part announces nothing more until the end, where the runs of keys it already
announced come back first, silently, and then its other entries, each new value announced. On 10⁷
values over 20M rows at fourteen lanes, the values take 0.42 of the reader's thread's time, the first
in 7 ms. On 10⁶ values, which the statistics bound, the reader's thread hashes them faster in a table
that stays in cache.

### 7.5 Writing a result

`writer.WriteAsync(scan)` writes the batches of any scan as they come: a file's, which copies it, or a
result's, which writes a rollup without building a record:

```csharp
var hourlyByEndpoint =
    from r in log.Scan<Request>()
    group r by (Hour: r.At.Truncate(CalendarUnit.Hour), r.Endpoint) into g
    select (g.Key.Hour, g.Key.Endpoint, g.Count(), g.Average(x => x.DurationMs));

await writer.WriteAsync(hourlyByEndpoint.As<EndpointHour>());
```

On a log sorted by time the groups stream ([a group by that streams](#23-a-group-by-that-streams)), so
the rollup of a year holds the endpoints of one hour, not of the year, and its averages are the same
bits however the log was split ([sums](#51-sums-exact-and-the-same-bits-whatever-the-cut)).

## 8. One row: the aggregates of the whole scan

`scan.AggregateAsync(a => e)` computes one answer, and
`scan.AggregateAsync<TResult>(a => (e1, e2, …))` computes as many as are written, in one pass, into
the record `TResult`, whose members take the answers in order the way `As` has them take a
`select`'s values, VX1010 included. The one type argument is all the call names:

```csharp
long readings = await scan.AggregateAsync(a => a.Count());
Summary s = await scan.AggregateAsync<Summary>(a => (a.Min(x => x.Celsius), a.Max(x => x.Celsius), a.Count()));
```

The answers come from the catalog of [aggregates](#5-aggregates), except the key, with a chosen row
taken in the order of the whole scan, and `a.Where(x => p)` filters them. A single answer also has its
own call: `CountAsync`, `AnyAsync`, `MinAsync`, `MaxAsync`, `SumAsync`, `AverageAsync`,
`CountDistinctAsync`, and `AggregateAsync<T, TAggregator, TState>` for [an aggregator of one's
own](#54-an-aggregator-of-ones-own). The file statistics answer a count, a minimum and a maximum
before any read, and the sum and the mean of an integer or a decimal column when they carry a sum
(this library's writer records none, but another writer may), and the `ValueTask` is then already
complete (see [answers without rows](12-index-reads.md#4-answers-without-rows)). A float column's sum
and mean are always computed, never read from the statistics, because the writer summed in an order of
its own, and an answer taken from there would change with the split of the data
([sums](#51-sums-exact-and-the-same-bits-whatever-the-cut)).

## 9. Execution

Execution is one pass over the blocks the filter keeps, each in the form it arrives in, never handing
a batch to the caller's code. Keys are numbered as they are met, with one state per group per
aggregate.

### 9.1 Keys, block by block

| key block | groups | lookups |
|---|---|---|
| constant | one range | one for the block |
| run-end | one range per run | one per run |
| dictionary | by code, through a table from code to group kept while blocks view the same values | one per distinct code met |
| sorted by the statistics, canonical | runs detected | one per run |
| canonical, an integer whose statistics bound it within 2¹⁶ values, or within four times the rows | the value minus the least one numbers its group, in pages of 4 096 numbers allocated as values meet them, in front of the index. The pages are carved from slabs of one, two, four, up to sixteen pages. A batch's rows are found 4 096 at a time in two passes, each row's page read with no branch on the keys, then the rows left (values met for the first time) in their order, a chunk of new values sending the next one through the lookup alone | one per distinct value met |
| canonical, any other | per row, a row equal to the previous one reusing its group. A fixed-width key is hashed in two passes, each row's home slot probed as its hash is computed, the rows left looked up in their order. The slots start on a cache line, so a line of slots is one line, and the low bits that a sample of the keys all leave zero are left out of the homes, so keys at a power-of-two stride lie in a row. A short text of 12 bytes at most is grouped as a 16-byte word, its canonical view, homed by rapidhash (a CRC's homes are linear, which no seed separates), until a longer value turns the lane's table into bytes for good, each group keeping its number. A key wider than a word (a decimal, a UUID, a short text's word) past 4 096 groups keeps its hash and its group in its slot, eight slots to a line, at most four tenths full, its key read from the groups' keys where the hash agrees, every slot of a batch read before every candidate's key. A text key is handled 256 rows at a time: their hashes, then every row's home slot, then every candidate's bytes, no row waiting on another, the rows left looked up in their order | one per row |
| composite of two to eight parts, every part constant, run-end or sorted | each part grouped by its own index, and the ranges cut at every part's boundaries | one per range |
| composite of two to four fixed-width parts whose values and nulls fit 64 or 128 bits, none sorted, with a product of spans past 2¹⁶ | the parts' values themselves packed into one word, hashed into a table whose 4-byte slot holds the group's number and the hash's bits below the home's, so a new tuple finds its free slot without reading a word, in one cache line. A window of 256 rows reads its home slots first, then the words of the groups whose bits agree, and the parts are written to the result a chunk at a time | one per row |
| composite of two to eight parts, any other | each part grouped by its own index, as above, and the parts' numbers packed into one word of 64, 128 or 256 bits: a table indexed by the numbers while the product of their counts is under 2¹⁶, and past that a hashed table of group numbers, each next to a byte holding seven bits of the hash, read first | one per tuple met through the table, and past it, one per row whose tuple differs from the previous row's |
| composite of nine parts or more | the tuple encoded into bytes, one hash | one per row |

A function of a column ([value expressions](#3-value-expressions)) groups as the column does,
evaluated per code, per run or per value. An aggregate takes a batch's ranges in one call. When they
are shorter than its batch has words of selection (1 024 for 65 536 rows), an aggregate for which a
range costs more than its rows folds them row by row, so a key of short runs costs what a per-row key
costs, never more. A count, and a sum, a minimum or a maximum of a run-end or constant column, take
the ranges whatever their length.

### 9.2 Aggregates, block by block

| block | count, minimum, maximum, sum, mean |
|---|---|
| constant | the value weighted by the rows |
| run-end | per run, weighted by its length |
| dictionary | over the distinct codes the rows name, when their ranges are long enough to repay it |
| frame of reference, bit-packing, delta | a vectorized unpack, with the base applied once |
| alp, zstd, fsst | the decode, then the plain kernel |

A float sum takes its parts ([sums](#51-sums-exact-and-the-same-bits-whatever-the-cut)) in the same
kernels, weighted values included, since a part times a count is still a multiple of its unit, exact
within the bounds. A filtered aggregate takes the filter's mask, evaluated once per batch and per
distinct filter, as its selection. A chosen row keeps its position and the value it is chosen by ([a
chosen row](#53-a-chosen-row)).

Rows grouped one by one, every row selected and none null, fold in plain loops, and a count rides on
the pass of the first fixed-size aggregate that shares its record, so the record is reached once for
both. Past 16 MiB of records, an aggregate whose state is larger than 16 bytes (an exact float sum or
a variance) does not carry the count, because its pass, reaching each record first, would hold few
rows in flight behind each cache miss. The count then folds first, alone, its short pass bringing the
records into cache for the heavy one. An integer's minimum or maximum stores its choice as a select
while the groups have seen fewer than 32 rows each on average, when a new extreme is as likely as not,
and branches after that, when the branch predicts well. A text's minimum or maximum compares a chunk
of rows against their groups' values before it offers any, and writes its values' bytes into the
result's column as they lie, in pages of 128 KiB, and the answers of a batch of groups are read under
one view of the records.

### 9.3 Statistics and zone maps

A whole-scan aggregate the file statistics settle reads nothing ([one
row](#8-one-row-the-aggregates-of-the-whole-scan)). A block whose key, through its function, maps its
zone's bounds to one value is one group. When the filter keeps the block whole, its `Count`, its `Min`
and `Max` where the bounds are exact, and its null counts come from the zone map, and the block is not
decoded when no other aggregate needs it. A zone map holds no sum (see [layouts](02-format.md#6-layouts-layoutfbs)),
so a sum or a mean decodes. On a column sorted by time, `group r by r.At.Truncate(CalendarUnit.Day)`
with counts and extremes reads the zone maps plus the blocks that straddle midnight, and nothing else.
The same bounds give the finality used by [a group by that streams](#23-a-group-by-that-streams) and
the centre `c` used by [sums](#51-sums-exact-and-the-same-bits-whatever-the-cut).

Only a key with a function asks the zone maps for this, since a column's own zone holds a single key
only when it holds a single value, which its encoding already folds as one. A block settles when the
filter's zone maps prove it keeps the block whole, no key column holds a null or a NaN in it, and
every aggregate is a count of the rows, a minimum or a maximum. It is folded in row order, as a block
of as many rows holding each column's minimum and, on its last row, its maximum, so a sorted key still
streams and the groups come in the order they would have. A dataset's objects do not settle, because
their files still count the rows a deletion removed.

### 9.4 Parallelism and the merge

At a degree above one the rows are cut into ranges that end where a chunk does, so no chunk is read
twice, with more ranges than lanes and the last ones shrinking down to a chunk. Each lane takes ranges
from a queue, one after the other, into the one partition it holds, so a lane on a slower core takes
fewer, and none waits on the others while ranges are left. A lane's partition is a table of the groups
it met, its key's index ([keys, block by block](#91-keys-block-by-block)) and its states in records
([memory](#95-memory)), as a single lane would hold them, and a key every lane meets is held by every
lane until the merge. On a source whose reads are round trips, a lane reads two slices of its rows
ahead of its decoding.

#### The merge

The lanes' groups merge at the end. When twice their entries are not fewer than the lanes times the
entries a merge in series moves (few groups, or too few lanes), they merge in series into the lane
holding the most, whose states stay where they are. Otherwise the key space is cut by the top bits of
a hash of the keys, seeded once per process independently of the tables' own seeds, into a power of
two of parts, at most twice the degree and at most 256, each with at least 512 of the largest lane's
groups. Lanes that number one span of values by value cut it by value instead, into runs of a power of
two of numbers, each part numbered by value over its run, with no hash, no probe and no growing table.
Each part is merged from every lane into a table of its own, sized from the entries per group the
finished parts had, by a task the query's workers take from a queue, never a thread blocked on
another, and the parts are read as one without a copy.

With no order or window over the groups, the parts are delivered the way a core's are
([memory](#95-memory)): each is built into its batches by the worker that merged it and its table let
go once built, the reader merging and building the next part rather than waiting, and the lanes'
tables go once the last part is merged. A composite key's per-column indexes merge once, first. The
pairs of a distinct count, chained by group, merge separately: past 65 536, the largest lane's are
taken as they are and the others' cut into parts by the hash of their group and value, four parts per
worker, side by side. A group by that streams on a key the statistics bound to at most 4 096 groups
holds each group's values in a set of its own instead, a value found at the first read. The sets of
the groups a batch closes are cleared for those it opens, and a set only one range met moves whole
into the range that follows it. A distinct count with no key holds its values alone, each in its slot,
homed by the top bits of its hash. A part is then a run of every lane's slots, which its worker walks
with nothing cut first, its own table homing them by the bits below the part's, which they share. A
chosen row and a tie keep the earlier range's.

On a dataset, a range is an object, and an object holding two shares of the rows or more is cut
between its chunks, as a file's rows are, so no chunk is read by two ranges. The objects that are cut
are opened side by side to learn where their chunks end, and the ranges then find them already open.
No object settles ([statistics and zone maps](#93-statistics-and-zone-maps), and see
[13-dataset.md](13-dataset.md)). A group by that streams runs as [in parallel, in
order](#25-in-parallel-in-order) describes, its ranges grouped side by side and followed in row order,
and a top-k on the key as [`Skip` and `Take`](#64-skip-and-take) describes. An integer key's groups are
numbered by value as over a file. The summaries of the version's roots bound its values over every
object, the roots being read before the pass the way its walk reads them first, and the rows of every
object, deleted ones counted, bound what the table may span. The core holds one set of parts for every
object of the query, and asks the store for nothing the lanes' tables would not. Over 1, 2 and 7
objects with rows deleted, before and after compaction, at one lane and at four, asked for or turned
to, its answers are the same bits as the lanes' tables.

#### The core

A second engine, the core, holds each group once. It handles a key of one fixed-width column or of
raw words ([keys, block by block](#91-keys-block-by-block)) whose states all lie in records, at eight
lanes and more. It is used when a plan asks for it, or when the key is one integer column numbered by
value over a million values or more that a lane's first batch shows scattered over its span.

Before it folds a row, the lane places the batch's values in 4 096 bins over the span, one bit each,
and turns to the core with its table empty when they fall in three quarters or more of the bins, as
the same number of values drawn at random would. Its lanes' tables would then each hold most of the
groups, out of cache. A key in row order, whose batches each fall in a few bins, and a narrower span
keep the lanes' tables. The same judgment holds for a file with zone maps, one without, and a dataset,
since a zone's least and greatest values would make a key in row order with a sentinel (0 among
growing identifiers) look as if it covered its span. On a file of an edition with no zone maps, ten
million keys at fourteen lanes take 77 ms this way, against 154 on the lanes' tables and 79 on the
same rows with zone maps.

A hashed key, which nothing bounds before the pass, turns to the core once a lane's first batch shows
half a million values or more (the values of a uniform key that made as many groups from as many
rows), provided the source gives each lane 200 000 rows or more, so the lanes turn while their tables
only hold a batch. Lanes that may turn this way take their first range i / N of the way through the
queue (lane i of N), then the queue's next ones, unless the zones settle blocks in row order, because
with all of them on the first rows, a key whose cardinality changes along the rows would mislead them
all together. The first lane whose rows say to turn makes them all turn, not most of them. On 2·10⁷
rows at fourteen lanes, a key of 10³ values on its first quarter and 10⁷ on the rest takes 65.6 ms
against 64.2 with the core asked for. The other way round, it takes 40.6 against 40.0, where a
majority of lanes that met the 10³ values kept the lanes' tables and took 59.0.

A hashed key that is local in the rows (sessions of two rows) reads as 4·10⁴ values on a lane's first
rows. Past them, a lane counts its new groups one window of 65 536 rows at a time, never one row at a
time. A key drawn from a fixed set of values brings them at a falling rate, e^(−r/K), which the first
rows already judged. One whose rate, after three windows, has kept four fifths of itself over two is
projected linearly (its groups plus the rate times the rows left), and past half a million, with rows
left of twice its groups to repay emptying its table, the lane turns, once. A key whose last window's
keys rise in row order never does. The sessions take 134 ms this way, against 205 on the lanes'
tables and 98 with the core asked for, and a hundred thousand random values turn on nothing.

Inside the core, the key space is cut into 256 parts by the top byte of the merge's hash. Each lane
folds its rows into a cache of bounded capacity, a table of the same kind. A full cache's groups
leave as entries (a record with its key), in batches of the part their key falls in, each lane's
batches cut from slabs of its own. A part applies its batches into sub-tables of at most 512 KiB,
which split on the next bit of the hash as they fill, in bursts, under the part's lock (only taken
when free), once its pending batches pass α times its groups and 16 384 entries, α being derived from
the degree, between 1 and 8. A lane whose cache misses more than half its rows over four capacities'
worth of them sends its rows straight into batches for the next thirty-two. At the end every part
applies what is pending, its sub-tables split ahead, and the sub-tables are the result, read as one.
The arrays of its sub-tables and slabs come from a shelf of the query and go back to the process's
shelf, kept within a budget and swept after garbage collections.

On the bench the core holds two to three times less memory than the tables of fourteen lanes, and at
fourteen lanes takes ×0.69 of the time at a million random keys, ×0.46 at ten million, and ×0.64 on
ten rows per key. It costs time at one lane, at 10⁵ keys (×2.5) and on keys in row order (×1.2 to
×2.6), which is why it is what the governor turns to under pressure ([memory](#95-memory)) and what a
scattered wide key turns to from its first batch, rather than the default.

### 9.5 Memory

A blocking group by holds, on each lane, a table of the groups the lane met: their keys, and their
states in records, each of the width the engine chooses for the sum ([sums](#51-sums-exact-and-the-same-bits-whatever-the-cut)).
A group every lane meets is held by every lane until the merge, whose parts then hold it once more.
The core, where it runs, holds it once, in the sub-tables of its part, plus a cache per lane and the
batches pending below α times the groups. Nothing is held per row read, and the output comes a batch
at a time. A streaming group by holds its open groups. A top-k on the key holds one and a half times
`k` per lane, plus a batch ([`Skip` and `Take`](#64-skip-and-take)), and one on an aggregate holds
every group, like a blocking group by. A projection and a `Distinct` on a column that streams hold
their window.

| a `long` key, `Count` and a sum of a `long` | bytes per group |
|---|---|
| one lane | ≈ 25, on a million keys |
| fourteen lanes, a table each, and the merge | ≈ 188 on a million keys over four million rows, each lane meeting a quarter of them, and up to fourteen times one lane's when each meets every key |
| fourteen lanes, the core | two to three times less than their tables, on the bench's wide keys |

A row of data touches one cache line of states, whatever its aggregates. A probe of a fixed-width key
reads its slot, a probe of a wider key past 4 096 groups reads its slot and the key of the group it
names, and a probe of a text reads its slot and its bytes. A UUID key with a count takes ≈ 39 bytes
per group at one lane on a million keys, where slots that held the key took ≈ 142. A group is
numbered by an `int`, and the tables double, so an open table holds at most 2²⁹ groups and a list
2³⁰, for a distinct count's pairs and values alike. Past that, a group by fails with a
`VortexUnsupportedException` that says so.

#### The governor

A query's memory is reserved from a budget: the process's by default, or a `QueryMemoryBudget` the
host creates and gives to the sessions that share it, whose ceiling bounds their queries together. A
query reserves in its budget, and the budget in the process's, whatever the sum of the ceilings the
host sets. The process's ceiling is its limit (the managed heap's,
`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`, and in a container three quarters of the
container's, which also counts the engine's native memory), minus an eighth for the garbage
collector, minus the rest of the process: what its heap held beyond the queries' tables at the last
full collection, what the collector kept committed beyond its heap, and what the queries released
since. A limit of 4 GB is not 4 GB for the queries.

A query reserves where its memory grows, at the moment it does: each array of a lane's tables before
it is allocated (a doubling holding the old arrays next to the new ones for the copy, and no more),
the parts and the cut of a merge, the scratch a sort, a top-k or a fetch rents, and the groups of its
result and their order until they are delivered. A lane's working memory besides its tables, a batch's
scratch and its decoding, is admitted with the query, at 16 bytes per row of its batches between
64 KiB and a megabyte, or a megabyte when the scan sets no batch size. A query whose budget cannot
give every lane that much starts on half as many lanes, down to one. Over a source that copies what it
reads (a store, or positional reads), a lane's two splits read ahead of its decode are admitted with
it, each as large as the layout's largest split, which a walk of the layout finds before the pass.
Under a budget short of them, the lanes read one split ahead, then none, before they are halved, and
give them back when the pass ends. A mapping reads in place, its reads being views of it, so nothing
more is admitted. What the queries release stays in the heap until a collection, so when that memory,
or what the collector keeps committed beyond its heap, stands between a reservation and the ceiling, a
collection that gives it back comes first. A query that is refused fails with a
`VortexMemoryException` naming the operator, its groups and its bytes, with everything it held given
back.

#### Under pressure

A group by whose lanes' tables outgrow the budget ends in the core rather than failing, on a key the
core can hold, at two lanes or more. A lane whose budget would not let its table double once more
turns. The first one creates a lean core (α at 1, a part applied from 256 pending entries, a cache a
quarter of a lane's, batches of a kilobyte), and the others follow at their next batch, one at a time,
if their table holds more than the core would cost them, each lane waiting for its turn on a task and
folding nothing meanwhile. A turning lane's groups are counted by part and copied into an array of
their own per part, exactly their size, which the budget counts past its ceiling and takes back as the
part applies it. The table is given back, and the lane carries on with a cache. A part's stack whose
groups the budget cannot take yet waits while a lane still holds a table, and its lane waits for the
next table given back. Meanwhile, and while arrays of their own wait on their stacks, the core takes
past its ceiling whatever an application asks for beyond it.

A lane that runs out of rows without turning merges its table with the others that did not turn
either, in series into the largest, each one given back once merged, and the last lane to stop empties
that one into the core. A query whose pass fits but whose merge by parts does not merges its tables the
same way, in series, each given back once merged, and needs no core. Under 70 % of the memory the
lanes' tables would hold at most, fourteen lanes on 10⁵ keys end exact, their peak within 1.4 % of the
budget. A text key, a composite holding one, a text's extremes and a distinct count stay on the lanes'
tables, which spill (see [the lanes' spill](#the-lanes-spill)).

#### The share

A budget counts the queries that hold memory under it. Past seven eighths of its ceiling, a query that
would hold more than its share (the ceiling divided by the active queries) is told its lanes cannot
grow, and they turn to the core first. A reservation itself is never refused for the share alone,
since a query with nothing to give back would then fail where it fit.

#### The spill

When nothing is left to give back, the core writes the parts holding the most (their groups and their
pending entries, a record and a key each) to a scratch file of the query, largest first, until the
query holds three quarters of its budget. The file lives in `VortexSessionOptions.ScratchDirectory`,
the system's temporary directory by default but never a `tmpfs`, where the process's spills keep a
tenth of the free space, and under the host's `VortexSessionOptions.ScratchBudget` when it gives one,
which a sort's runs also count against. Every byte is reserved before it is written and given back
when the scratch closes. A lane whose rows the core cannot take spills before its next batch, on a
task.

At the end, the parts held in memory are delivered first, then each spilled part alone: its runs read
back a page at a time and applied into its sub-tables again, which merges the groups they share, then
delivered and given back. A part is a 256th of the key space, so a part held again is that share of
the groups plus a page. Under a budget, the pass sizes itself on it: a lane's batch takes a quarter of
the ceiling divided by the lanes, the core's caches and open batches an eighth, its sub-tables split at
a 256th, a lane turns once the budget could not take every growing lane's next doubling, and the
scratch's page is a sixteenth. Under a tenth of what the lanes' tables would hold, fourteen lanes on
10⁶ keys end exact within 6 % of the budget.

#### The lanes' spill

The groups the core cannot hold (a text key or a composite holding one, a text's extremes, a distinct
count by key) stay on the lanes' tables, which spill themselves. Before a batch, a lane asks the budget
for what its table would take if the batch were all new groups, each array's next growth, on every
lane at once. Past that, the lane's table goes to its scratch as a run, in 256 sections by the top
byte of its keys' hash under the merge's seed, each group with its key, its record and the states its
slots keep apart (a text's bytes, a distinct count's values). The lane carries on with its table
emptied in place, its arrays kept.

While other lanes run, a pressed lane may first merge its table into one that the retiring lanes share
and retire at the end of its range. A sketch of each table's keys (HyperLogLog on their hashes) tells
what the shared table lacks of the lane's, and so how much it grows by, a table of keys wider than a
word filling to six tenths rather than doubling past the budget. Past what the budget grants, or once
half the groups merged into it came new to it, the lanes' keys being apart, the lanes write their own
runs.

At the end every table goes to the scratch, and the result comes back part by part, as a merge in
parts delivers it. A part is a stretch of every run's sections, merged into a table that grows from a
shelf of its own under the budget, built into batches and let go, with as few parts as keep one within
half the room the budget leaves, and as many built ahead as that half holds (none when one part takes
it all). A distinct count over the whole scan spills its set alone, by the top byte of its values'
hash, its parts counted from sets sized by their sections. A `Distinct` on a key the core cannot take
spills the reader's index: the values met before its first run are announced as they came, the others
at the end, each part read back with the first run's values silently first. Such a query's batches of
new groups on every lane come to a sixteenth of the ceiling, 128 bytes per row, and lanes whose
working memory passes a quarter of it start on half as many. A state holding references fails the
query past its budget, with a typed exception.

On the bench, a count by 10⁶ URLs over 4M rows ends exact at one lane in 0.38 s under half of the
90 MB its table holds, writing 7 runs, and in 0.33 s under a tenth, writing 62, against 0.20 s with
the budget it needs. Fourteen lanes, which hold 564 MB in 0.067 s, end in 0.29 s under half of it,
writing 35 runs, and in 0.30 s under a tenth, writing 160, with every peak within 6 % of its budget.

#### The sort in runs

An order over groups that spilled sorts them in runs. The groups held in memory, then each spilled
part as it comes back, pass through the filters before the order, and their rows (the order's results,
the key's components that break ties, then the result's columns) gather in a store while the budget
holds them, at most a quarter of its ceiling. Past that, the store's rows are sorted and written to
the scratch as a run, a Vortex file of their own, written as fast as the writer writes and without
statistics. The runs are then merged back one batch of each at a time, the first run's rows first
among equals, one contiguous stretch of one run at a time, and the windows after the order, and the
result's own, are cut across the merged rows. A result's scan sorted by `OrderBy` uses the same sort
under its session's budget. Each column ranks across two runs' batches as within one, with nulls last
and NaN after +∞, and the runs' files are deleted once the sort is done, whether it was read to its
end or not. A result ten times its budget sorts exactly, its peak within the budget. A window without
an order over groups that spilled, or a filter after the order, needs every group at once, and fails
saying so.

#### Part by part

A core's result with no order or window over its groups is delivered part by part. Once the lanes have
deposited their caches, one worker per lane but one takes the parts in turn. It applies one, applies
the operators on groups to its groups (a filter on them, the fetch of chosen rows sized to the part),
writes them into stores of the result's columns while they are in its cache, lets the part go, and
hands its batches to the reader, at most one part per worker ahead of it. The reader applies the next
part itself when none is waiting. The null groups follow as a part of their own, then the spilled
parts. The first batch leaves once the first part is applied, and the result's window is cut across
the parts' batches, each batch holding at most one part's groups. On 10⁷ keys, the first batch comes a
quarter sooner at one lane and a third sooner at fourteen, where the whole result takes 0.58 of the
time, its batches built on every lane. The lanes' tables merged in parts ([the merge](#the-merge)) are
delivered the same way, each part built by whoever merged it. A part keeps what its table held of the
query's memory until the reader is done with its batches, which the session's pool holds outside any
query's count. At fourteen lanes, with its batches no longer built on the reader's thread alone once
the merge is done, a million keys took 0.83 of the time and four aggregates 0.68.

Not done yet: a key cursor over a result larger than its budget, which a merge of runs cannot seek in.

## 10. Plan and metrics

`ExplainAsync` on an aggregation returns the scan's `ScanPlan` with `Grouping`, a `GroupPlan`, which
is null without a group by. It states what costs, so the shape of LINQ does not hide it, from the
query and the statistics alone:

| member | says |
|---|---|
| `Keys` | per component, a `GroupKeyPlan`: its column, whether the statistics say it is sorted, and the values they bound an integer column to, which a table of its groups indexes directly |
| `Streaming`, `NotStreaming` | the component the groups stream on, or why none does ([a group by that streams](#23-a-group-by-that-streams)) |
| `Aggregates` | the aggregates the pass computes, once the selection's duplicates are merged |
| `RowFilter` | the conjuncts of a `Where` on keys moved to the rows ([groups kept](#62-groups-kept-where)) |
| `Order`, `Kept` | `GroupOrdering.None`, `Streamed` on the key the groups close in, `Top` with the groups a heap keeps for a window, or a `Sort` of every group |
| `ChosenRows` | the columns of chosen rows the result reads |
| `Degree` | the most lanes the pass may run on |
| `Core`, `CacheCapacity`, `Alpha` | whether the core holds the groups, the plan having asked for it at that degree, and its caches' capacity and α ([the core](#the-core)) |
| `MostGroups` | the most groups the statistics allow, when they bound every component |

A key's form is only known once its block is read, so the plan says what is possible and the
metrics say what happened. Once the result is read, its `ScanMetrics` carry `Grouping`, a
`GroupMetrics` with:

* `Groups`, the groups the pass found before any operator on them, and `PeakGroups`, the most held at
  once, which a streaming group by keeps small, and which a core delivering part by part limits to
  the parts applied and not yet built
* `PeakBytes`, the most it held of its memory budget, and `Lanes` and `MergeParts`
* what the core did: `Core`, `CacheEvictions`, `BypassedRows`, `Bursts`, `PendingBytes`,
  `ReloadedBytes`, `Tables` and `TableSplits`
* `SpilledParts`, the core's parts written to scratch, `SpilledRuns`, the lanes' tables written, and
  `SpilledBytes`, what either wrote, zero until spilling starts
* the key blocks by how they were grouped: `KeyBlocksByRange` (constant, run-end), `KeyBlocksByCode`
  and `KeyBlocksHashed`
* `TimeToFirstBatch`, the time from the first `MoveNextAsync` to the first batch
* `CoreReason`, why the core held the groups (asked for, a lane's first rows, its first batch's
  spread, the projection of its new groups past its first rows, or the memory budget), `None` when the
  lanes' tables did, and `TurnedAfterRows`, the rows the first lane to turn had folded into its own
  table, 0 on its first batch and -1 when no lane turned

Both are properties outside the records' constructors. None of this goes through the scan's own
counters, which every batch touches.

## 11. Errors, cancellation, disposal

| when | what |
|---|---|
| compile time | the table in [the shape](#1-the-shape), and VX1009, VX1010, VX1011 |
| the operator runs, and the lambda with it | a key component that is not a symbol (`ArgumentException`), a key or input type that does not group or aggregate (`VortexSchemaException`), a result that is not one or a chosen row's column used as an input (`InvalidOperationException`), an order on a result that has none (`ArgumentException`), a function's argument out of range (`ArgumentOutOfRangeException`), `As` or `AggregateAsync<TResult>` on a record that does not match (`VortexSchemaException`) |
| enumeration | a sum past its type (`OverflowException`), a query over its memory budget (`VortexMemoryException`), and the file's own errors (see [exceptions](14-public-api.md#7-options-plans-reports-diagnostics-exceptions)) |

Cancellation is checked at every batch boundary, and, in the work a blocking operator does after its
input (a merge, a sort, a top-k), at every part of it, since that may be long over millions of groups.
A query left early, by `break`, a `Take` or a cancellation, is disposed as [stopping
early](#24-stopping-early) describes.

## 12. Not in it, on purpose

| left out | why |
|---|---|
| a `(g, r)` lambda, `g.Average(r.Celsius)` | it puts the row's probe next to the group, so a column that is neither key nor aggregated would compile and fail when it runs |
| tuples of values as results, `await foreach (var (city, n) in …)` | an overload per arity, eight of them in three places, a cap at eight, and names the compiler drops (CS8123). A record names the values, reads them as batches by default, and has no cap |
| a lambda over values, `Select(g => new CityHour(g.Key.Hour, g.Count()))` | it needs a conversion from `Sym<T>` to `T`, which would let `Math.Abs(r.Celsius) > 1` compile in a filter. `As<CityHour>()` gives the record |
| expression trees, `IQueryable` | rule 3 of [14-public-api.md](14-public-api.md) |
| arithmetic on results, `g.Sum(…) / g.Count()` | C# computes with results after the sink. Filtering or ordering by a ratio is LINQ to objects over the delivered groups, or a query over the result's scan |
| joins, `let`, a query over a group's rows | one table, no binding per row |
| a synchronous twin, `ToList()` | async only where there is I/O, and no synchronous twin of an asynchronous call (see [the founding constraints](03-architecture.md#1-founding-constraints)) |
| an implicit order of groups | it used to change with the writer's encodings, so an order is asked for ([order](#63-order-orderby-thenby)). Without one, the order is not defined. At one lane the groups come in the order they were first met, and at more, in the merge's order: the largest lane's, then the others' as they merged in, or part by part of the key space, the parts cut by a hash seeded once per process, so two runs of a query in one process cut them alike. The repository's tests run with an internal switch that shuffles it, the same way for a process, so no test relies on it |
| the same bits for a custom aggregator whose merge is not associative | every native aggregate gives the same bits whatever the caches' capacity, α and the order its states merge in ([how it is tested](#13-how-it-is-tested)), its consolidation made of merges alone. A merge that is not associative depends on that order, as it already depends on the order of the merges at more than one lane |
| a switch to plain float sums | the reproducible sum costs nothing measurable on a pass over runs or a sorted key, a fifth more on a group by keyed row by row, and a third more at a million groups, whose 32 bytes per group leave the cache. It is more accurate, and a second semantics would be a second test matrix |
| approximate distinct counts, quantiles, grouping sets | later, as aggregates and as operators of the same pipeline |
| aggregates on the tool path | a later API, over the same engine |

## 13. How it is tested

| promise | gate |
|---|---|
| the two syntaxes build one plan | the guide's queries, written both ways, compared plan for plan |
| the compile-time boundary of [the shape](#1-the-shape) | `tests/MustNotCompile`, with exactly the expected diagnostics |
| every answer is right | each key form of [keys, block by block](#91-keys-block-by-block) × each [aggregate](#5-aggregates) × degree 1 and the machine's, against LINQ to objects over `ToRecordsAsync`, on the conformance corpus and the type matrix |
| answers do not depend on the degree or the split | every answer except the order-bound ones, float sums included, is the same bits at degrees 1 to the machine's, over a file written in two row orders, and over a dataset split into 1, 2 and 7 objects, before and after compaction, and also at every capacity of the lanes' caches, every α the internal switch forces (1, 2, 4, 8), and spilled under a heap ten times too small |
| no batch is left behind | a holder of a part slowed on purpose while the lanes deposit: every deposited batch is applied and the pending bytes counted at the governor, and a pass cancelled in the middle of its bursts gives its budget back to zero |
| a group by whose lanes' tables outgrow its budget still ends under it | `CorePressureTests`: fourteen lanes on 10⁵ keys under 85, 70 and 50 % of what their tables reserve at most, refused with the core off and exact with it, every reservation given back, with twice as many lanes as the pool may run threads and every wait on a task |
| pending batches are bounded | the entries a part holds pending never exceed α times its groups, nor the rows its caches missed |
| the sub-tables are read back little | the bytes of sub-tables a burst reads again stay under ≈ 2s/α per entry, s being a group's bytes in a sub-table, and none on keys met twice at fourteen lanes once the sub-tables are grown |
| memory is at most that of the plain lane tables | the peak, caches included, is at most that of a table on each lane, at the same degree, at degree 1 and at the machine's, on keys met twice, ten times, and once |
| the round trips stay where they are | the requests and the dependent steps of a group by over a source with latency, as ratchets in `GroupRoundTripTests` |
| queries share fairly | two to eight queries at once under one budget: none holds more than its share once the threshold is reached, and the one past it is governed first |
| a `Where` on keys prunes like a filter | `LiveBlocks` equals that of the equivalent `Where` on rows |
| the first batch only waits for the first split | the time to first batch of a scan, a projection, a `Distinct` and a group by that streams, on two files sixteen times apart in size, locally and over the HTTP source with latency (`tests/Vorticity.Tests/IO/HttpRangeSegmentSource.cs`) |
| memory is the window plus the open state | `LiveMemoryTests`: a streaming group by's peak stays flat as its groups grow a hundredfold, and a high-cardinality group by's follows its groups, not the degree times its groups. `KeyTopTests`: a top-k on the key holds its groups within the degree times one and a half `k` plus a batch, at degrees 1 and 4, and on an integer's extreme the same at 1, 4 and 14, ties ranked by the key as LINQ ranks them |
| a `Take` reads what it uses | `Requests` and `BlocksDecoded` of a `Take(n)` bounded by the splits that hold `n` results and the window |
| nothing is allocated per batch, nor per group as batches | `ScanAllocationTests` extended to the result stream, `As` and the projection |
| a short range costs its rows | a complexity probe whose time per row stays flat as runs shorten |
| the operator is no slower than the hand-written loop | the throughput gate, a group by per key form, with the hand-written loops of [encoded-forms.md](../guide/encoded-forms.md) as the floor |
| a dictionary, run-end or settled key decodes no key block | `BlocksDecoded` counts only the inputs' blocks |
| the naming rule, and no I/O in a builder | `PublicSurfaceTests`: a member that returns an awaitable or a terminal stream ends in `Async`, and a builder does not, and a builder over a source that counts its reads reads nothing |

The figures this document quotes come from measurements made while it was written, on a 14-core
arm64 machine: a million groups delivered as rows with a string key in 41 ms and 46 MiB, and as
batches in 2 ms. For the indexed sum against a plain one, a count and a mean of a million rows grouped
by a run-end key took 12.3 ms against 13.5, by a dictionary key 4.0 against 3.2, and over a million
groups 46 against 34 ms and 103 against 77 MiB, with one bit pattern over every split of sixteen
million values where the plain sum gave seven, and the exact sum where the plain one was
1.7 × 10⁻¹⁴ off. A pass costs 4.4 ns per row for a fold over a column and 32 ns for a composite group
by, which is what the sum's overhead is set against. The query bench turns each one into a gate, and
`WorkCounterTests` holds the work of a group by, counted at one lane.

## 14. Migrating from 0.4

| 0.4 | now |
|---|---|
| `GroupBy(…).AggAsync(g => …)` | `GroupBy(…).Select(g => …)` |
| `await foreach (var (city, n) in ….AggAsync(g => (g.Key, g.Count())))`, a tuple per group | `….Select(g => (g.Key, g.Count())).As<CityCount>()`, read as batches or with `ToRecordsAsync()` |
| `var (min, max) = await scan.AggAsync(a => (…, …))` | `MinMax m = await scan.AggregateAsync<MinMax>(a => (…, …))` |
| at most eight values in a selection | as many as the record has members |
| `GroupBy(r => (r.City, r.Day))`, then `g.Key.Item1` | `g.Key.City` |
| `Avg`, `AvgAsync` | `Average`, `AverageAsync` |
| `OrderBy(r => r.Day, descending: true)` | `OrderByDescending(r => r.Day)` |
| groups in key order for a dictionary or sorted key | `orderby g.Key`, free on a key that streams |
| the groups at the end of the pass | as soon as they are final, on a key that streams |
| `Sum` of an `int` as an `int`, throwing past it | as a `long` |
| `Sum` of a `float` as a `float` | as a `double` |
| a float sum whose last bits depended on the degree | the same bits whatever the degree and the split, and closer to the exact sum |
| `g.Aggregate<double, Welford<double>, WelfordState>(r => r.Celsius)` | the same, or `g.Welford(x => x.Celsius)` through the aggregator's extension |
