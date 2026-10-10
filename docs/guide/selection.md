# Selection

Take a filter's answer as a bitmap over the whole block, instead of a copy of the rows that passed.

```csharp
double total = 0;
await foreach (var cols in file.Scan<Reading>().Where(r => r.Celsius > 20.0).With(new ScanOptions { Compact = false }))
{
    ReadOnlySpan<double> values = cols.Celsius.Values;
    foreach (int i in cols.Selection)
    {
        total += values[i];
    }
}
```

```
total of the readings above 20 degrees: 25725000.0
first batch: 8192 rows from file row 0, 5970 selected, the third at file row 103
```

## Compacted or whole

By default a filtered scan compacts its batches: the rows of a block that passed are copied into the
batch, so every row you see has passed, `Selection.IsAll` is true, and row `i` is no longer file row
`StartRow + i`. With `ScanOptions.Compact = false`, the scan hands over each block as it decoded it,
every row of it, and `Columns<T>.Selection` tells you which rows passed:

| member | what it is |
|---|---|
| `IsAll` | every row passed, and `Words` is then empty |
| `Count` | how many rows passed, 5 970 of the 8 192 of the first block above |
| `Words` | the bitmap, bit `i % 64` of word `i / 64` for row `i`, laid out like `ValidityWords` |
| `Contains(i)` | whether row `i` passed |
| `foreach (int i in selection)` | the rows that passed, in order, found word by word with `TrailingZeroCount` |

Positions are preserved: row `i` of the batch is file row `StartRow + i`, so the third reading above
20 degrees is file row 103. The values of the rows that did not pass are real values from the file,
not garbage. They simply did not pass. A block the zone maps prune is never read, in either mode.

## What the copy costs

The same filtered sum both ways, with the whole record and with a record of the single column the
sum reads, and with a filter that keeps only 2 % of the rows:

| record, filter | rows kept | whole blocks | compacted |
|---|---|---|---|
| `Reading`, `Celsius > 20` | 735 000 | 8.3 ms | 14.2 ms |
| `Temperature`, `Celsius > 20` | 735 000 | 6.9 ms | 8.1 ms |
| `Reading`, `Celsius > 49` | 22 500 | 7.1 ms | 6.2 ms |

```csharp
[VortexRecord]
public partial record struct Temperature(double? Celsius);
```

Compaction copies every column the record names, whether the loop reads it or not. With `Reading` it
also copies `Day` and `City`, and `City` is text stored as runs, which is why the gap is widest there.
Narrowing the record ([project-columns.md](project-columns.md)) shrinks the copy, and a filter that
keeps few rows copies little: at 2 % of the rows, the compacted scan even comes out ahead. Both modes
allocated about 10 KB per scan, which is the scan's own.

## Which to use

* Whole blocks work best when the loop reads few of many columns, when the filter keeps much of each
  block, or when you want file positions without arithmetic.
* Compacted batches, the default, suit code that wants dense columns downstream: a SIMD sum over
  `Values`, or `ToOwned()` to keep a batch of the rows that passed
  ([owned-batches.md](owned-batches.md)).
* For an aggregate it makes no difference. `SumAsync`, `GroupBy` and the rest read the selection
  themselves and never materialise a batch ([aggregates.md](aggregates.md)).

## Watch out

* Iterate the selection, not the block. A `foreach` over `Values` of a whole block also adds the rows
  that did not pass, nulls included.
* A take is a selection too. `Rows(4, 900_000)` without a filter delivers the blocks that hold those
  rows, with a selection of them ([read-rows-by-index.md](read-rows-by-index.md)).
* Compaction decodes run-end columns. With whole blocks, the encoded forms arrive as the file holds
  them ([encoded-forms.md](encoded-forms.md)).

The figures come from one run of the sample on the demonstration file of a million rows. Each timing
is the best of three passes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- selection
```
