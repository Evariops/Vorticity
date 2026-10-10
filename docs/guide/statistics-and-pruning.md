# Statistics and pruning

See what a scan will skip before it runs and what it did afterwards, and learn what makes pruning
work.

```csharp
ScanPlan plan = await file.Scan<Reading>().Where(r => r.Day >= 900).ExplainAsync();
Print("Day >= 900", plan);

Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900);
await foreach (Columns<Reading> _ in scan)
{
}

Print("Day >= 900", scan.Metrics);
```

```
plan, Day >= 900: 1000000 rows, 14 of 123 blocks live, 13 segments, 211004 bytes to read, may match True
  zone map: 109 blocks pruned, 1 segments and 2124 bytes read to decide
  count: exact True, 100000 rows, 109 pruned, 13 proven, 1 decoded
ran, Day >= 900: 100000 rows in 5 batches, 12 requests, 208880 bytes, 14 blocks decoded, 109 pruned
```

`Print` writes the fields of the two records, and the sample holds it. Reading 2 124 bytes of zone
maps was enough to decide that 109 of the file's 123 blocks hold nothing for this predicate, and
those blocks were never decoded.

## The plan, before

`ExplainAsync()` reads the file's statistics, its zone maps and its indexes, plus at most the few
segments of a sorted column it searches, and returns a `ScanPlan`:

| field | what it says |
|---|---|
| `Rows` | the rows the scan covers: the whole file, a range, or the rows a take asks for |
| `Blocks`, `LiveBlocks` | the blocks those rows touch, and those no structure could prove empty |
| `Segments`, `BytesToRead` | the distinct segments the live blocks need, including what consulting the structures reads, and their size |
| `MayMatch` | false when the file's statistics prove the scan empty |
| `Pruning` | one `PruningStep` per structure, cheapest first: `Structure`, `BlocksPruned`, and the `SegmentsRead` and `BytesRead` it took to consult |
| `Count` | how a `CountAsync` of the scan would be answered |
| `Order` | the key source of an `OrderBy` scan, null otherwise ([keys-in-order.md](keys-in-order.md)) |
| `Grouping` | for a group by, a `GroupPlan`: its key components and whether each is sorted, the component the groups stream on or why none does, the aggregates, the order of the groups and the degree. Null without a group by |

`Count` is a `CountPlan`. `Exact` is true when a sorted column or a sorted-runs index answers the
count on its own, and `Rows` is then the count: 100 000 above, with nothing decoded. Otherwise,
`Pruned`, `Proven` and `Decoded` say how the blocks divide: ruled out by their bounds, decided whole
by them, or left to decode.

## The numbers, after

`scan.Metrics` is valid once a sink has run. It is a `ScanMetrics`: the `Rows` and `Batches`
delivered, the `Requests` and `BytesRequested` made to the source, `BlocksDecoded`, `BlocksPruned`,
and the `CacheHits` from the session's segment cache ([threads.md](threads.md)). For a group by,
`Grouping` adds a `GroupMetrics`: the groups found and the most held at once, the peak memory, the
lanes, what was spilled, and the time to the first batch ([queries.md](queries.md)).

The plan's bytes and the run's differ by the zone maps. The plan counts each segment once, 13 of them
and 211 004 bytes including the 2 124 bytes of zone maps it consulted. The run made 12 requests for
208 880 bytes, because the file kept the zone maps the plan had already read. A segment here holds
several blocks of one column, and the scan reads it once for all the blocks that need it. A second
scan reads it again, and over a `FileSegmentSource` or a remote source, a session `SegmentCache` is
what saves that second trip to the disk or the network. `BlocksDecoded` matches `LiveBlocks`, 14.
They arrive in 5 batches rather than 14 because the 13 blocks the zone maps prove whole, where every
row passes, are delivered up to sixteen at a time, as in a scan without a filter
([blocks-and-chunks.md](blocks-and-chunks.md)).

## A filter that prunes, and one that cannot

Measured on the demonstration file, 1 508 316 bytes:

| | live blocks | requests | bytes requested | rows |
|---|---|---|---|---|
| no filter | 123 of 123 | 51 | 1 494 068 | 1 000 000 |
| `Day >= 900` | 14 of 123 | 12 | 208 880 | 100 000 |
| `Celsius > 45` | 123 of 123 | 51 | 1 494 068 | 122 500 |
| `Day >= 5000` | 0 of 123 | 0 | 0 | 0 |

The rows are in `Day` order, so 109 blocks are provably outside `Day >= 900`. The scan reads a
seventh of the file for a tenth of the rows, because a column is read by the chunk, eight blocks
here, and the live blocks bring in the chunks they belong to. `Celsius` covers its whole range in
every block, so no block can be excluded. Finding that out cost the 3 108 bytes of its zone maps,
which the plan read here, and the run then read everything. `Day >= 5000` lies above the file's
maximum, and `MayMatch` says so. The file statistics are in memory once the file is open, so the plan
and the run read nothing at all and count every block as pruned by the file statistics.

## Three levels

| level | granularity | cost to consult |
|---|---|---|
| file statistics | the whole file | nothing, they are read at open |
| zone maps | one block | one small read |
| the values | one row | decoding the block |

`file.MayMatch` asks the first level alone, and a `false` is a proof:

```
MayMatch(Day >= 900): True
MayMatch(Day >= 5000): False
MayMatch(Celsius > 100): False
MayMatch(City == "Paris"): True
```

`file.Statistics` is what it reads: one `FieldStatistics` per top-level column, each value behind a
`TryGet` that returns false when the file does not record it:

```
  Day: bounds 0 to 999, nulls 0, sorted True, constant unknown
  Celsius: bounds 10.1 to 49.9, nulls 20000, sorted False, constant unknown
  City: bounds none exact, nulls 0, sorted False, constant unknown
  sums: Day not recorded, Celsius not recorded
```

`TryGetMin<T>` and `TryGetMax<T>` read a bound as the column's type, and answer only for an exact
bound. A file written by this library records, per column, the bounds of a numeric column, the null
count and whether the column is sorted. It does not record a sum, whether the column is constant, or
file-level bounds for a text column, whose bounds live in the zone maps instead. A file from another
writer may record more, which is why each value is a `TryGet`. These are claims the file makes about
itself. Open it with `VerifyStatistics = true` to have them checked against what is decoded
([limits.md](limits.md)).

## What makes pruning work

Clustering comes first. A block is excluded by its bounds, and a block that holds the whole range of
a column has bounds that exclude nothing. Order the rows by the column you filter on, or at least
group them by it.

Text columns get bounds too, which the writer keeps by default (`StringBoundBytes = 16`). They only
prune when the rows are clustered. Here is `City == "Paris"` over 200 000 rows, the same data twice:

```
City == "Paris" over 200000 rows, cities cycling row by row: 25 of 25 blocks live, 25000 rows
City == "Paris" over 200000 rows, clustered by city: 8 of 25 blocks live, 25000 rows
```

Clustered, Paris fills four blocks, the last one shared with the next city. The four other live
blocks each straddle two cities whose names bracket "Paris", Marseille and Toulouse for instance, so
their bounds cannot exclude it.

Indexes handle what bounds cannot. A Bloom filter finds a value scattered over every block, and an
n-gram filter finds a substring ([indexes.md](indexes.md)). They appear in `Pruning` as steps of their
own, with what they cost to consult.

## Watch out

* `ExplainAsync` is worth running even on a scan without a filter, because it gives you the shape of
  the file.
* `Blocks` and `LiveBlocks` follow `Rows(...)`: a range of ten rows plans 1 block of 1, and a take of
  two rows 2 of 2, with `Rows` equal to 2 ([read-rows-by-index.md](read-rows-by-index.md)).
* `Metrics` read before the sink has run are zeros, and a scan runs only one sink.
* `ScanOptions.UseStatistics = false` and `UseIndexes = false` turn the structures off, to check them, never
  to change a result.

What each statistic allows is defined in
[the semantics design](../design/08-semantics.md#1-statistic-precision-what-inexact-licenses), how the
scan uses them in [the write strategy](../design/11-write-strategy.md#6-reading-what-the-writer-produced),
and the plan's records in
[the public API design](../design/14-public-api.md#7-options-plans-reports-diagnostics-exceptions).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- statistics-and-pruning
```
