# Statistics and pruning

See which blocks were skipped and why, and what makes pruning work.

## The plan

`ExplainAsync()` reads statistics and zone maps — never the data — and says what the scan is about
to do:

```csharp
ScanPlan plan = await file.Scan().Where(recent).ExplainAsync();
Console.WriteLine($"{plan.LiveBlocks} of {plan.Blocks} blocks, {plan.BytesToRead} bytes to read");
foreach (PruningStep step in plan.Pruning)
{
    Console.WriteLine($"  {step.Structure}: {step.BlocksPruned} pruned, {step.BytesRead} bytes read to decide");
}
```

```
the file: 1523369 bytes, 1000000 rows, 123 blocks of 8192 rows, 123 splits
this query: 14 live blocks, 14 live splits, 5 segments and 1518308 bytes to read, file may match: True
  zone map: 109 blocks pruned, 1 segments and 2140 bytes read to decide
count: exact True, 100000 rows, 109 splits pruned, 13 proven, 1 decoded
```

Two thousand bytes of zone maps decided that 109 blocks of 123 hold nothing for this predicate. The
count then came out of the statistics: 109 splits pruned, 13 proven whole, and one actually decoded.

## The three levels

| level | granularity | cost to consult |
|---|---|---|
| file statistics | the whole file | free, read at open |
| zone maps | one block | one small read |
| the values | one row | the block |

`MayMatch` asks the first level:

```csharp
file.MayMatch(recent);                 // True
IReadOnlyList<ColumnSummary> summaries = ColumnSummaries.Of(file);
ColumnSummaries.MayMatch(recent, summaries, file.RowCount);
new SummaryPruner(recent).MayMatch(summaries, file.RowCount);
```

A `ColumnSummary` is what the pruner sees: `Min`, `Max`, `HasMin`, `HasMax`, `NullCount`,
`IsExact`. `SummaryPruner` is the same logic kept across calls, for a predicate you ask about many
files — `ReadsColumns` says whether it needs any column at all.

`day >= 5000` is `False` at this level, with nothing read: the file's maximum settles it.

## What makes pruning work

**Clustering.** The rows must be ordered, or at least grouped, by the column you filter on. A block
is excluded by its bounds, and a block that holds the whole range of a column has bounds that
exclude nothing. This is the whole of it, and [filter-rows.md](filter-rows.md) measures the two
sides on the same file.

**Bounds for text columns, which are not written by default.** `StringBoundBytes` is 0 out of the
box, so a text column's zones carry no bounds at all. Both conditions, on `city = "Paris"` over
200 000 rows:

| | `StringBoundBytes = 0` | `= 16` |
|---|---|---|
| names cycling row by row | 25 of 25 blocks live | 25 of 25 |
| rows clustered by name | 25 of 25 | **5 of 25** |

40 000 rows come back in every case. Bounds without clustering prune nothing, and clustering without
bounds prunes nothing.

**Block size, which cuts both ways.** The same predicate on the same rows, written three times:

| `RowBlockSize` | blocks live | rounds of reading | bytes |
|---|---|---|---|
| 1 024 | 99 of 977 | 104 | 148 436 471 |
| 8 192 | 14 of 123 | 18 | 19 751 747 |
| 65 536 | 3 of 16 | 7 | 3 085 219 |

Finer blocks prune a finer fraction — 10 % of the file survives at 1 024 rows against 19 % at
65 536 — and cost far more to read anyway, because a block is a round of reading and a round fetches
the segment it sits in. Unless your source makes a read nearly free, the default of 8 192 is a floor
rather than a starting point, and going below it should be measured.

## What the file already knows

`file.Statistics` carries, per column, what the writer computed: minimum, maximum, sum, null count,
whether the column is sorted, strictly sorted or constant, and the uncompressed size. Each is
optional — a `TryGet` that answers `false` when the writer did not keep it — and the whole of it
disappears under `FileStatistics = false`, which costs 176 bytes on the demonstration file and takes
`MayMatch` away with it.

`VerifyStatistics = true` at open makes the reader check them against what it decodes. They are
claims the file makes about itself; a corrupt or hostile file can lie.

## Watch out

* `Blocks` and `Splits` are not the same count: a split is the unit a scan works on, and a block is
  the unit statistics are kept for. On a single-chunk file they coincide.
* `BytesToRead` counts the segments a plan must fetch, and a segment holds several blocks — which is
  why pruning 109 blocks of 123 above still reads most of the file's bytes.
* `ExplainAsync` on a scan with no filter is still worth running: it tells you the file's shape.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- statistics-and-pruning
```
