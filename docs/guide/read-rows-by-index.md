# Read rows by index

Take row 4 and row 900 000 without decoding what lies between.

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
await foreach (RecordBatch batch in file.Scan().Take([4L, 900_000L]).ExecuteAsync())
{
    using (batch)
    {
        Console.WriteLine($"{batch.RowCount} row from block start {batch.StartRow}: " +
            $"day {batch.Column("day"u8).AsPrimitive<int>()[0]}");
    }
}
```

```
1 row from block start 4: day 0
1 row from block start 891842: day 900
```

## A contiguous range

```csharp
file.Scan().Rows(new RowRange(1_000, 1_010))
```

`RowRange` is a half-open `[start, end)`, so that one is ten rows. `RowRange.FromLength(1_000, 10)`
says the same thing. A range past the end of the file is clamped, not refused.

## What it costs

Taking two rows out of a million, against reading them all:

| | rounds of reading | bytes |
|---|---|---|
| `Take([4, 900_000])` | 3 | 3 092 703 |
| the whole file | 124 | 184 725 367 |

Sixty times less. The scan reads the two blocks the rows are in and decodes those, and never touches
the 121 blocks between them.

That is also the shape of the cost: a take is priced in **blocks**, not in rows. Two rows in the
same block cost one block; two rows far apart cost two. The blocks here are 8 192 rows, and
`ExplainAsync()` reports `LiveSplits` against `Splits` — 2 of 110 for this take — which is the
number to watch when a take starts looking expensive.

## Watch out

* **`StartRow` is the block's first row, not the row you asked for.** In the output above, row
  900 000 arrives in a batch whose `StartRow` is 891 842. Match the rows you get to the rows you
  asked for by the order of the list, not by `StartRow`.
* **The list is sorted and deduplicated for you.** `Take([900_000, 4, 4])` delivers two rows, in the
  file's order: the order of your list is not preserved, and a repeat is not a repeat.
* **A range and a list do not compose.** `Rows(...).Take(...)` throws
  `InvalidOperationException: A scan selects rows by range or by index list, not both.`
* A negative index, or one past the last row, throws `ArgumentOutOfRangeException` naming the file's
  row count.
* Project as well when you only want some columns: `Project(["celsius"]).Take([...])` reads the
  blocks of that column alone.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- read-rows-by-index
```
