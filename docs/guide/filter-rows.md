# Filter rows

Push a predicate down so that whole blocks are never read, and see what a filter will cost before it
runs.

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
double? maxCelsius = 30.0;

Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900 && r.City == "Paris");
if (maxCelsius is double max)
{
    scan.Where(r => r.Celsius <= max);
}

ScanPlan plan = await scan.ExplainAsync();
long rows = 0;
int earliest = int.MaxValue;
await foreach (var (day, _, _) in scan)
{
    rows += day.Length;
    earliest = Math.Min(earliest, day[0]);
}

ScanMetrics stats = scan.Metrics;
```

```
plan: 14 of 123 blocks, 15 segments, 217052 bytes
  zone map pruned 109 blocks, reading 8172 bytes to decide
6116 rows, from day 900
ran: 14 blocks decoded, 109 pruned, 12 requests, 208880 bytes
```

The filter is exact. What comes back is the matching rows and nothing else, compacted into the
batches. It is not a hint you need to check again.

## The lambda is not a delegate over rows

`r` is a `Probe<Reading>` and `r.Day` is a `Sym<int>`, whose operators record a predicate instead of
comparing values. The lambda runs once, when `Where` is called, and what it returns is the plan. The
sample counts the calls of a lambda over a million rows and prints `the lambda ran 1 time`. A
captured local such as `max` is read at that moment.

Each call to `Where` adds a condition, joined to the previous ones with `&`, which is how the
optional filter above works. For a filter built from optional parts in one lambda, start from
`Predicate.All`, the neutral element of `&`. `ToString()` shows what was built:

```csharp
Scan<Reading> optional = file.Scan<Reading>().Where(r =>
{
    Predicate filter = Predicate.All;
    if (fromDay is int from)
    {
        filter &= r.Day >= from;
    }

    if (toDay is int to)
    {
        filter &= r.Day <= to;
    }

    if (city is not null)
    {
        filter &= r.City == city;
    }

    Console.WriteLine($"built from optional parts: {filter}");
    return filter;
});
```

```
built from optional parts: Day >= 500 and City = 'Lyon'
```

What compiles is exactly what the scan can push down. [08-semantics.md](../design/08-semantics.md)
defines what each form means, and [the symbolic algebra](../design/14-public-api.md#52-the-symbolic-algebra)
explains why it is built this way.

| written | meaning |
|---|---|
| `r.Day >= 900`, `r.City == "Paris"` | comparison with a literal of the column's type |
| `a && b`, `a \|\| b`, `!a` | logic, where `&&` and `\|\|` always evaluate both sides, like `&` and `\|` |
| `r.Day.In(1, 2, 3)`, `r.Celsius.Between(10.0, 20.0)` | membership, and an inclusive range |
| `r.Celsius == null`, `r.Celsius.IsNull`, `IsNotNull` | nullity |
| `r.City.StartsWith("L")`, `Contains`, `Like("P_r%")` | text |
| `v.Pages.Contains(7)` | a list holds a value |
| `v.Origin.Country.In("FR", "BE")` | a field of a nested record |
| `r.High > r.Low`, on a record with two such columns | two columns of the same type |

And here is what does not compile, in the compiler's own words:

```
r.Day >= "900"            CS0019: Operator '>=' cannot be applied to operands of type 'Sym<int>' and 'string'
r.Day % 2 == 0            CS0619: 'Sym<int>.operator %(Sym<int>, int)' is obsolete: 'Vortex does not push arithmetic down. Compute it on the columns after the scan.'
Math.Abs(r.Celsius) > 1   CS1503: Argument 1: cannot convert from 'Vorticity.Sym<double?>' to 'decimal'
r.City.Length > 3         CS1061: 'Sym<string>' does not contain a definition for 'Length'
```

## What happens, in order

The file statistics may settle the predicate without reading anything. Then the zone maps, which
hold a minimum, a maximum and a null count per block, prune whole blocks. Text columns carry
truncated bounds too (`StringBoundBytes`, 16 by default). An index, if the writer built one, prunes
further ([indexes.md](indexes.md)). The kernel decides the remaining rows value by value, on the
encoded form when the encoding allows it. The batch is then compacted to the rows that passed, or
handed over whole with a selection ([selection.md](selection.md)).

## When pruning does nothing

Counts from the sample, each with its plan:

| predicate | rows | live blocks | requests | bytes | how the count was answered |
|---|---|---|---|---|---|
| `r.Day >= 900` | 100 000 | 14 of 123 | 1 | 428 | exactly, from the structures |
| `r.Day.In(1, 2, 3)` | 3 000 | 1 of 123 | 1 | 412 | exactly, from the structures |
| `r.Celsius == null` | 20 000 | 123 of 123 | 0 | 0 | every block proven by its null count |
| `r.Celsius > 45.0` | 122 500 | 123 of 123 | 17 | 1 150 428 | 123 blocks evaluated |
| `r.City == "Paris"` | 125 006 | 123 of 123 | 17 | 336 956 | 123 blocks evaluated |
| `r.City.StartsWith("L")` | 249 999 | 123 of 123 | 17 | 336 956 | 123 blocks evaluated |

The rows are in `Day` order, so the bounds of 109 blocks out of 123 settle `Day >= 900` on their own,
and the count reads one small segment of `Day`, the one that holds the boundary. The zone maps it
relies on are already in memory, kept by the scans before it, which is also why `== null` reads
nothing at all.

`Celsius` covers its whole range inside every block, and every city appears in every block, so no
block can be excluded and each value has to be compared. This is a property of the data, not of the
predicate. Write the rows in the order of the column you filter on, or at least cluster them by it.

## Before and after

`ExplainAsync` reads the statistics and zone maps, plus at most the few segments of a sorted column it
searches, and returns a `ScanPlan`: the live blocks, the segments and bytes they need, and one
`PruningStep` per structure consulted, with what it pruned and what consulting it cost. `Metrics`,
read after the sink, says what the scan actually did. Above, the plan counts 15 segments and
217 052 bytes, including the 8 172 bytes of zone maps it consulted. The scan that followed read the
other 12 segments, 208 880 bytes. The file keeps the zone maps a plan or a scan has read, and the
scan reads each segment once, however many live blocks share it
([statistics-and-pruning.md](statistics-and-pruning.md)).

The cheapest questions need no scan at all. `CountAsync` and `AnyAsync` answer from the structures
whenever they are enough, and `file.MayMatch<Reading>(r => r.Day >= 5000)` compares the predicate
with the file statistics alone. It answers `False` here and reads nothing.

## Watch out

* Nulls answer unknown, and only a true answer keeps a row. `!(r.Celsius > 45.0)` keeps 857 500 rows,
  not 877 500: the 20 000 nulls are kept by neither the predicate nor its negation. `== null`,
  `IsNull` and `IsNotNull` never answer unknown.
* Do not branch on a predicate. `r.Day >= 900 ? r.City == "Paris" : r.City == "Lyon"` compiles and
  always takes the second branch (125 000 rows, all of Lyon), and so does an `if` on a predicate
  inside the lambda, because a predicate is never true while the lambda runs. Branch on captured
  values, as the optional parts above do, and combine predicates with `&`, `|` and `!`.
* A literal is compared as it is, never rounded to the column. `r.At <= DateTime.UtcNow` on a
  microsecond timestamp compares against the stored microsecond at or below the instant, so the
  boundary row is neither gained nor lost, and `r.At == DateTime.UtcNow` matches nothing unless the
  instant falls exactly on a microsecond. A decimal with more digits than the column's scale is
  handled the same way.
* A filter names members of the record. To filter on a column, the record you scan with must name it
  ([project-columns.md](project-columns.md)).
* A scan is single-use, but `ExplainAsync` may be called before its sink, as here.

The figures come from one run of the sample on the demonstration file of a million rows.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- filter-rows
```
