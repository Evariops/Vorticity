# Read rows by index

Take row 4 and row 900 000 without decoding what lies between them.

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
a block of 7073 rows from row 892928, 1 selected
  row 900000: day 900, Toulouse
```

A take delivers batches around the rows you asked for, not the rows alone, and marks the requested
rows in the batch's `Selection`. Enumerating the selection gives their positions in the batch, and
`StartRow + i` gives their positions in the file.

The take covers the range from the first requested row to the last one. The scan cuts that range
into batches of about one block (8 192 rows), aligned on the file's chunks, and skips every batch
that holds no requested row. Above, the first batch starts at row 4, the first row asked for, and
spans 8 192 rows. The second batch is the end of the range, from row 892 928 up to and including
row 900 000. `StartRow` is always the file row of the batch's row 0.

## A contiguous range

```csharp
file.Scan<Reading>().Rows(RowRange.FromLength(1_000, 10))
```

`RowRange` is a half-open interval `[start, end)`, so `new RowRange(1_000, 1_010)` says the same
thing. A range is delivered as it is, with no selection: `10 rows from row 1000, selection all: True`.
A range that runs past the end of the file is cut there, so `RowRange.FromLength(999_990, 100)` gives
ten rows.

## What it costs

Taking two rows out of a million, against reading all of them, on the demonstration file:

| | requests | bytes | blocks decoded |
|---|---|---|---|
| `Rows(4, 900_000)` | 6 | 180 352 | 2 |
| the whole file | 51 | 1 494 068 | 123 |

The take reads the segments of the two chunks that hold the rows, for the record's three columns,
and decodes the two blocks that hold them. It never touches the 121 blocks in between. That is the
shape of the cost: a take is priced in blocks, not in rows, and for each block it reads the chunk
that contains it, one segment per column. Two rows in one block cost one block, two rows far apart
cost two, and blocks hold 8 192 rows here. `ExplainAsync()` gives the same figures before the read
(2 rows, 2 of 2 blocks live, 6 segments, 180 352 bytes to read), and a record with fewer members
reads fewer segments.

## With a filter

A filter on a take is evaluated on the requested rows, and the batch is then compacted to the
survivors, like any filtered batch. `Selection` is then all, and row `i` is no longer `StartRow + i`:

```
a take with a filter: 3 rows, StartRow 4, selection all: True
  batch row 0: 10.4 degrees, so file row 4; StartRow + 0 = 4
  batch row 1: 11 degrees, so file row 10; StartRow + 1 = 5
  batch row 2: 12 degrees, so file row 20; StartRow + 2 = 6
```

That is `Rows(4, 10, 20, 900_000).Where(r => r.Celsius > 10.0)`. Row 900 000 holds exactly 10.0 and
is dropped, and the three others come back packed. When the file position of each surviving row
matters, set `ScanOptions.Compact = false`. Positions are then preserved, and `Selection` says which
rows passed ([selection.md](selection.md)).

```
the same under Compact = false: 8192 rows from row 4, selected rows 4 10 20
```

## Watch out

* The list is sorted and deduplicated. `Rows(900_000, 4, 4)` delivers two rows in file order: the
  order of your list is not kept, and a repeated index is not repeated.
* A range and a list do not combine. Using both on one scan throws
  `InvalidOperationException: A scan selects rows by range or by index, not both.`, and analyzer
  VX1004 flags it at compile time ([diagnostics.md](diagnostics.md)).
* A row past the end throws `ArgumentOutOfRangeException: The file has 1000000 rows.` when the scan
  starts, not when `Rows` is called.
* The plan of a take or a range only counts the blocks its rows touch: 2 of 2 for the take above, and
  1 of 1 for the range of ten rows, whose `Segments` and `BytesToRead` are 3 and 100 428.
* Owned batches keep the selection too. Through `ToBatchesAsync`, each batch still covers the same
  span of rows, and its `Selection` marks the two requested rows
  ([owned-batches.md](owned-batches.md)). `ToRecordsAsync` yields just the two rows.

Blocks and selections are part of the read contract described in
[the write strategy](../design/11-write-strategy.md#6-reading-what-the-writer-produced), and
`StartRow` and `Selection` in
[the public API design](../design/14-public-api.md#53-columnstrecord-and-columnt).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- read-rows-by-index
```
