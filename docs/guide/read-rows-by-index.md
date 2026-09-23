# Read rows by index

Take row 4 and row 900 000 without decoding what lies between.

```csharp
Scan<Reading> take = file.Scan<Reading>().Rows(4, 900_000);
await foreach (Columns<Reading> cols in take)
{
    Console.WriteLine($"a block of {cols.RowCount} rows from row {cols.StartRow}, {cols.Selection.Count} selected");
    foreach (int i in cols.Selection)
    {
        Console.WriteLine($"  row {cols.StartRow + i}: day {cols.Day[i]}, {cols.City.GetString(i)}");
    }
}
```

```
a block of 8192 rows from row 4, 1 selected
  row 4: day 0, Paris
a block of 8003 rows from row 891998, 1 selected
  row 900000: day 900, Toulouse
```

A take delivers the **blocks** that hold the rows, not the rows alone, and marks the rows asked for
in the batch's `Selection`. Enumerating the selection gives their positions in the batch, and
`StartRow + i` their positions in the file.

`StartRow` is the file row of the batch's row 0. The batch is the part of the block the rows asked
for span: it starts at the block's first row, or at the first row asked for when that lies inside
the block, and it ends after the last one. Above, the first batch starts at row 4 itself; the second
starts at row 892 928, where its block begins, and ends at row 900 000.

## A contiguous range

```csharp
file.Scan<Reading>().Rows(RowRange.FromLength(1_000, 10))
```

`RowRange` is a half-open `[start, end)`; `new RowRange(1_000, 1_010)` says the same thing. A range
is delivered as it is, with no selection: `10 rows from row 1000, selection all: True`. A range that
runs past the end of the file is cut there, so `RowRange.FromLength(999_990, 100)` gives ten rows.

## What it costs

Taking two rows of a million, against reading all of them, on the demonstration file:

| | requests | bytes | blocks decoded |
|---|---|---|---|
| `Rows(4, 900_000)` | 6 | 180 352 | 2 |
| the whole file | 51 | 1 494 044 | 123 |

The take reads the segments of the two chunks the rows are in, for the record's three columns, and
decodes the two blocks that hold them; it never touches the 121 between them. That is the shape of
the cost: **a take is priced in blocks, not rows,** and what it reads is the chunk of each block, a
segment per column. Two rows in one block cost one block, two rows far apart cost two, and the
blocks here are 8 192 rows. `ExplainAsync()` gives the same figures before the read, 2 rows, 2 of
2 blocks live, 6 segments and 180 352 bytes to read, and a record with fewer members reads fewer
segments.

## With a filter

A filter on a take is evaluated on the rows asked for, and then the batch is **compacted** to the
survivors, as any filtered batch is. `Selection` is then all, and row `i` is no longer
`StartRow + i`:

```
a take with a filter: 3 rows, StartRow 4, selection all: True
  batch row 0: 10.4 degrees, so file row 4; StartRow + 0 = 4
  batch row 1: 11 degrees, so file row 10; StartRow + 1 = 5
  batch row 2: 12 degrees, so file row 20; StartRow + 2 = 6
```

That is `Rows(4, 10, 20, 900_000).Where(r => r.Celsius > 10.0)`: row 900 000 holds exactly 10.0 and
is dropped, and the three others come back packed. When the file position of each surviving row
matters, set `ScanOptions.Compact = false`: positions then hold and `Selection` says which rows
passed ([selection.md](selection.md)).

```
the same under Compact = false: 8192 rows from row 4, selected rows 4 10 20
```

## Watch out

* **The list is sorted and deduplicated.** `Rows(900_000, 4, 4)` delivers two rows, in file order:
  the order of your list is not kept and a repeat is not repeated.
* **A range and a list do not compose.** Both on one scan throws `InvalidOperationException: A scan
  selects rows by range or by index, not both.`, and analyzer VX1004 flags it at compile time
  ([diagnostics.md](diagnostics.md)).
* A row past the end throws `ArgumentOutOfRangeException: The file has 1000000 rows.` when the
  scan starts, not when `Rows` is called.
* The plan of a take or a range counts only the blocks its rows touch: 2 of 2 for the take above,
  1 of 1 for the range of ten rows, whose `Segments` and `BytesToRead` are 3 and 100 428.
* An owned batch from `ToBatchesAsync` does not carry the selection: a take through it hands over
  the whole span of each block, 16 195 rows for these two ([owned-batches.md](owned-batches.md)).
  Use the borrowed columns, or `ToRecordsAsync`, which yields the two rows only.

Blocks and the selection are the read contract of
[11-write-strategy.md](../design/11-write-strategy.md) §6; `StartRow` and `Selection` are in §5.3
of [14-public-api.md](../design/14-public-api.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- read-rows-by-index
```
