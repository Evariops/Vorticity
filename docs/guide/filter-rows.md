# Filter rows

Push a predicate down so that whole blocks are never read, and see what it will cost before it
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

ScanStatistics stats = scan.Statistics;
```

```
plan: 14 of 123 blocks, 24 segments, 171472 bytes
  zone map pruned 109 blocks, reading 8172 bytes to decide
6116 rows, from day 900
ran: 14 blocks decoded, 109 pruned, 24 requests, 171472 bytes
```

**The filter is exact.** What comes back is the matching rows and nothing else, compacted into
the batches; it is not a hint to check again.

## The lambda is not a delegate over rows

`r` is a `Probe<Reading>`, and `r.Day` a `Sym<int>`: its operators record a predicate instead of
comparing values. The lambda runs once, when `Where` is called, and what it returns is the plan;
the sample counts the calls of a lambda over a million rows and prints `the lambda ran 1 time`. A
captured local such as `max` is read then. An optional filter is a second `Where`, joined to the
first by `&`; a filter built from optional parts starts from `Predicate.All`, the neutral element
of `&`, and `ToString()` shows what was built:

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
says what each form means, and §5.2 of [14-public-api.md](../design/14-public-api.md) why the
algebra is built this way:

| written | meaning |
|---|---|
| `r.Day >= 900`, `r.City == "Paris"` | comparison with a literal of the column's type |
| `a && b`, `a \|\| b`, `!a` | logic; `&&` and `\|\|` evaluate both sides, as `&` and `\|` |
| `r.Day.In(1, 2, 3)`, `r.Celsius.Between(10.0, 20.0)` | membership, an inclusive range |
| `r.Celsius == null`, `r.Celsius.IsNull`, `IsNotNull` | nullity |
| `r.City.StartsWith("L")`, `Contains`, `Like("P_r%")` | text |
| `v.Pages.Contains(7)` | a list holds a value |
| `v.Origin.Country.In("FR", "BE")` | a field of a nested record |
| `r.High > r.Low`, on a record with two such columns | two columns of the same type |

And what does not compile, with the compiler's own words:

```
r.Day >= "900"            CS0019: Operator '>=' cannot be applied to operands of type 'Sym<int>' and 'string'
r.Day % 2 == 0            CS0619: 'Sym<int>.operator %(Sym<int>, int)' is obsolete: 'Vortex does not push arithmetic down. Compute it on the columns after the scan.'
Math.Abs(r.Celsius) > 1   CS1503: Argument 1: cannot convert from 'Vorticity.Sym<double?>' to 'decimal'
r.City.Length > 3         CS1061: 'Sym<string>' does not contain a definition for 'Length'
```

## What happens, in order

The file statistics may settle the predicate without a read. The zone maps, a minimum, maximum and
null count per block, prune whole blocks; text columns carry truncated bounds too
(`StringBoundBytes`, 16 by default). An index, if the writer built one, prunes more
([indexes.md](indexes.md)). The kernel decides the rest value by value, on the encoded form where
the encoding allows it, and the batch is compacted to the rows that passed, or handed over whole
with a selection ([selection.md](selection.md)).

## When pruning does nothing

Counts from the sample, each with its plan:

| predicate | rows | live blocks | requests | bytes | how the count was answered |
|---|---|---|---|---|---|
| `r.Day >= 900` | 100 000 | 14 of 123 | 2 | 2 328 | exact from the structures |
| `r.Day.In(1, 2, 3)` | 3 000 | 1 of 123 | 2 | 2 464 | exact from the structures |
| `r.Celsius == null` | 20 000 | 123 of 123 | 1 | 3 108 | every block proven from its null count |
| `r.Celsius > 45.0` | 122 500 | 123 of 123 | 51 | 1 204 356 | 123 blocks evaluated |
| `r.City == "Paris"` | 125 006 | 123 of 123 | 51 | 328 948 | 123 blocks evaluated |
| `r.City.StartsWith("L")` | 249 999 | 123 of 123 | 51 | 328 948 | 123 blocks evaluated |

The rows are in `Day` order, so a block's bounds settle `Day >= 900` for 109 blocks of 123, and the
count reads only the zone maps of `Day` and one small segment of it that places the boundary.
`Celsius` walks its whole range inside every block and every city appears in every block, so no
block can be excluded and each value is compared. **This is a property of the data, not of the
predicate**: write the rows in the order of the column you filter on, or at least clustered by it.

## Before and after

`ExplainAsync` reads the statistics and zone maps, and of the data at most the few segments of a
sorted column it searches, and returns a `ScanPlan`: the live blocks, the segments and bytes they
need, and a `PruningStep` per structure consulted with what it pruned and what consulting it cost.
`Statistics`, read after the sink, says what the scan did. The two agree above, 24 segments and
171 472 bytes: the scan reads each segment once, however many live blocks share it, and both count
the zone maps consulted ([statistics-and-pruning.md](statistics-and-pruning.md)).

The cheapest questions need no scan at all: `CountAsync` and `AnyAsync` answer from the structures
wherever they suffice, and `file.MayMatch<Reading>(r => r.Day >= 5000)` compares the predicate with
the file statistics alone and answers `False` here, reading nothing.

## Watch out

* **Nulls answer unknown, and only true keeps a row.** `!(r.Celsius > 45.0)` keeps 857 500 rows,
  not 877 500: the 20 000 nulls are kept by neither the predicate nor its negation. `== null`,
  `IsNull` and `IsNotNull` never answer unknown.
* **Do not branch on a predicate.** `r.Day >= 900 ? r.City == "Paris" : r.City == "Lyon"` compiles
  and always takes the second branch (125 000 rows, all of Lyon), and so does an `if` on a
  predicate inside the lambda: a predicate is never true while the lambda runs. Branch on captured
  values, as the optional parts above do, and combine predicates with `&`, `|` and `!`.
* **A literal is compared as it is, not rounded to the column.** `r.At <= DateTime.UtcNow` on a
  microsecond timestamp compares against the stored microsecond at or below the instant, so the
  boundary row is neither gained nor lost, and `r.At == DateTime.UtcNow` matches nothing unless the
  instant falls on a whole microsecond. A decimal with more digits than the column's scale is held
  the same way.
* **A filter names members of the record.** To filter on a column, the record you scan with names
  it ([project-columns.md](project-columns.md)).
* **A scan is single-use**, but `ExplainAsync` may be called before its sink, as here.

The figures come from one run of the sample on the demonstration file of a million rows.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- filter-rows
```
