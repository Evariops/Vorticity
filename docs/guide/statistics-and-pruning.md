# Statistics and pruning

See what a scan will skip before it runs, what it did after, and what makes pruning work.

```csharp
ScanPlan plan = await file.Scan<Reading>().Where(r => r.Day >= 900).ExplainAsync();
Print("Day >= 900", plan);

Scan<Reading> scan = file.Scan<Reading>().Where(r => r.Day >= 900);
await foreach (Columns<Reading> _ in scan)
{
}

Print("Day >= 900", scan.Statistics);
```

```
plan, Day >= 900: 1000000 rows, 14 of 123 blocks live, 22 segments, 165424 bytes to read, may match True
  zone map: 109 blocks pruned, 1 segments and 2124 bytes read to decide
  count: exact True, 100000 rows, 109 pruned, 13 proven, 1 decoded
ran, Day >= 900: 100000 rows in 14 batches, 22 requests, 165424 bytes, 14 blocks decoded, 109 pruned
```

`Print` writes the fields of the two records; the sample has it. 2 124 bytes of zone maps decided
that 109 of the file's 123 blocks hold nothing for this predicate, and they were never decoded.

## The plan, before

`ExplainAsync()` reads the file's statistics, its zone maps and its indexes, and of the data at
most the few segments of a sorted column it searches, and returns a `ScanPlan`:

| field | what it says |
|---|---|
| `Rows` | the rows the scan covers: the file's, those of a range, or the rows a take takes |
| `Blocks`, `LiveBlocks` | the blocks those rows touch, and those no structure could prove empty |
| `Segments`, `BytesToRead` | the distinct segments the live blocks need, with what consulting the structures reads, and their bytes |
| `MayMatch` | false when the file's statistics prove the scan empty |
| `Pruning` | one `PruningStep` per structure, cheapest first: `Structure`, `BlocksPruned`, and the `SegmentsRead` and `BytesRead` it took to consult |
| `Count` | how a `CountAsync` of the scan would be answered |
| `Order` | the key source of an `OrderBy` scan, null otherwise ([keys-in-order.md](keys-in-order.md)) |

`Count` is a `CountPlan`. `Exact` is true when a sorted column or a sorted-runs index answers the
count by itself, and `Rows` is then the count: 100 000 above, with nothing decoded. `Pruned`,
`Proven` and `Decoded` say how the blocks divide otherwise: ruled out by their bounds, decided whole
by them, or left to decode.

## The numbers, after

`scan.Statistics` is valid once a sink has run, and is a `ScanStatistics`: `Rows` and `Batches`
delivered, `Requests` and `BytesRequested` made to the source, `BlocksDecoded`, `BlocksPruned`,
and `CacheHits` from the session's segment cache ([threads.md](threads.md)).

**The plan's bytes and the run's agree.** The plan counts each segment once, 22 of them and
165 424 bytes with the zone maps; the run made 22 requests for 165 424 bytes. A segment here holds
several blocks of one column, and the scan reads it once for all the blocks that need it. A second
scan reads it again; over a `FileSegmentSource` or a remote source, a session `SegmentCache` is
what keeps it from reaching the disk or the network again. `BlocksDecoded` matches `LiveBlocks`:
14.

## A filter that prunes, and one that cannot

Measured on the demonstration file, 1 564 708 bytes:

| | live blocks | requests | bytes requested | rows |
|---|---|---|---|---|
| no filter | 123 of 123 | 150 | 1 540 608 | 1 000 000 |
| `Day >= 900` | 14 of 123 | 22 | 165 424 | 100 000 |
| `Celsius > 45` | 123 of 123 | 151 | 1 543 716 | 122 500 |
| `Day >= 5000` | 0 of 123 | 0 | 0 | 0 |

The rows are in `Day` order, so 109 blocks are provably outside `Day >= 900`: a tenth of the reading
for a tenth of the rows. `Celsius` walks its whole range inside every block, so no block can be
excluded, and the predicate costs one request more than no filter: the 3 108 bytes of zone maps read
to find that out. `Day >= 5000` lies above the file's own maximum, and `MayMatch` says so: the file
statistics are in memory once the file is open, so the plan and the run read nothing at all, and
count every block pruned by the file statistics.

## Three levels

| level | granularity | cost to consult |
|---|---|---|
| file statistics | the whole file | nothing: read at open |
| zone maps | one block | one small read |
| the values | one row | the block |

`file.MayMatch` asks the first level alone, and a `false` is a proof:

```
MayMatch(Day >= 900): True
MayMatch(Day >= 5000): False
MayMatch(Celsius > 100): False
MayMatch(City == "Paris"): True
```

`file.Statistics` is what it reads, one `FieldStatistics` per top-level column, each value a
`TryGet` that answers false when the file does not record it:

```
  Day: bounds 0 to 999, nulls 0, sorted True, constant unknown
  Celsius: bounds 10.1 to 49.9, nulls 20000, sorted False, constant unknown
  City: bounds none exact, nulls 0, sorted False, constant unknown
  sums: Day not recorded, Celsius not recorded
```

`TryGetMin<T>` and `TryGetMax<T>` read a bound as the column's type and answer only for an exact
one. A file this library writes records, per column, the bounds of a numeric column, the null count
and whether the column is sorted; it does not record a sum, whether the column is constant, or file
bounds for a text column, whose bounds live in the zone maps instead. A file from another writer may
record more, which is why each is a `TryGet`. They are claims the file makes about itself: open
with `VerifyStatistics = true` to have them checked against what is decoded ([limits.md](limits.md)).

## What makes pruning work

**Clustering.** A block is excluded by its bounds, and a block that holds the whole range of a
column has bounds that exclude nothing. Order the rows by the column you filter on, or group them
by it.

**Bounds for text columns**, which the writer keeps by default, `StringBoundBytes = 16`. They prune
only if the rows are clustered. `City == "Paris"` over 200 000 rows, the same data twice:

```
City == "Paris" over 200000 rows, cities cycling row by row: 25 of 25 blocks live, 25000 rows
City == "Paris" over 200000 rows, clustered by city: 8 of 25 blocks live, 25000 rows
```

Clustered, Paris fills four blocks, the last shared with the next city. The four other live blocks
each straddle two cities whose names bracket "Paris", Marseille and Toulouse for instance, so that
their bounds cannot exclude it.

**Indexes**, for what bounds cannot do: a value scattered over every block is found by a bloom
filter, a substring by an n-gram one ([indexes.md](indexes.md)). They appear in `Pruning` as steps
of their own, with what they cost to consult.

## Watch out

* `ExplainAsync` on a scan with no filter is still worth running: it gives the file's shape.
* `Blocks` and `LiveBlocks` follow `Rows(...)`: a range of ten rows plans 1 block of 1, a take of
  two rows 2 of 2, with `Rows` 2 ([read-rows-by-index.md](read-rows-by-index.md)).
* `Statistics` read before the sink has run are zeros, and a scan runs one sink only.
* `ScanOptions.Pruning = false` and `UseIndexes = false` turn the structures off, to check them,
  never to change a result.

What each statistic licenses is [08-semantics.md](../design/08-semantics.md) §1, how the scan uses
them [11-write-strategy.md](../design/11-write-strategy.md) §6, and the plan's records §7 of
[14-public-api.md](../design/14-public-api.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- statistics-and-pruning
```
