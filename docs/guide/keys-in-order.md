# Keys in order

Walk a column in key order: seek, step, rank, count, and get batches in key order.

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);

await using (KeyCursor<int> cursor = await file.Scan<Reading>().Keys(r => r.Day).OpenAsync())
{
    await cursor.SeekFirstAsync();
    Console.WriteLine($"first key {cursor.Key} at row {cursor.Row}");
    await cursor.NextKeyAsync();
    Console.WriteLine($"next distinct key {cursor.Key} at row {cursor.Row}");

    if (await cursor.SeekAsync(900, SeekOp.AtOrAfter))
    {
        Console.WriteLine($"seek to 900 landed on {cursor.Key} at row {cursor.Row}");
    }

    Console.WriteLine($"rank of 900: {await cursor.RankAsync(900)}");
    Console.WriteLine($"{await cursor.CountAtKeyAsync()} entries share key {cursor.Key}");
}
```

A cursor is not a scan. It walks the keys of one column in order and tells you which row each one is
at. You read the row afterwards if you need it. This is the right tool for a lookup, a range walk, or
a question like "how many values are below this one".

`Keys(r => r.Day)` infers the key type from the member, so the cursor is a `KeyCursor<int>` and
`SeekAsync` takes an `int`. On the demonstration file, whose `Day` grows by one every thousand rows:

```
first key 0 at row 0
next distinct key 1 at row 1000
seek to 900 landed on 900 at row 900000
rank of 900: 900000
1000 entries share key 900
entry of rank 123456: key 123 at row 123456
last key 999 at row 999999
an exact seek to a key that is not there: False, cursor valid: False
rows with 100 <= day < 102, from two ranks: 2000
seek at or before 700: key 700 at row 700999, the last of its run
  read back by Rows(700999): day 700, 29.9 degrees, Strasbourg
```

## The moves

Every move is a `ValueTask<bool>`. `false` means there was nowhere to go, and `IsValid` is then false.

| move | what it does |
|---|---|
| `SeekFirstAsync`, `SeekLastAsync` | go to either end |
| `SeekAsync(key, op)` | `Exact`, `AtOrAfter`, `After`, `AtOrBefore`, `Before` |
| `SeekRankAsync(n)` | go to the entry at position *n*, counting from zero |
| `NextAsync`, `PrevAsync` | move by one entry |
| `NextKeyAsync`, `PrevKeyAsync` | move to the first entry of the next or previous distinct key |
| `RankAsync(key)` | count the entries whose key is smaller |
| `CountAtKeyAsync()` | count the entries that share the current key, without moving |

A key repeated over many rows is many entries, ordered by row. `Exact` lands on the first of them and
`AtOrBefore` on the last, which is why the seek to 700 above stops at row 700 999. Two ranks count a
range without walking it: `RankAsync(102) - RankAsync(100)` is the 2 000 rows whose day is 100 or
101. `Row` is the value to pass to [`Rows(...)`](read-rows-by-index.md) on a scan.

## What the file needs

The cursor does not sort anything. It walks a structure that is already in order, and the builder's
`ExplainAsync` says which one it found and why it turned the others down:

```csharp
KeyPlan plan = await file.Scan<Reading>().Keys(r => r.Day).ExplainAsync();
```

```
day: source SortedColumn, 1 run, 1000000 entries, rows reachable: True
  turned down SortedRuns: the file carries no index directory
  turned down Postings: it holds keys without rows, which only a Distinct() cursor takes
  turned down Dictionary: it holds keys without rows, which only a Distinct() cursor takes
```

Two sources can serve a cursor that returns rows:

* A sorted column: when the file statistics say a column is sorted, the column is its own index, and
  it costs nothing to write: just order the rows by that column. A seek is a binary search over the
  zone maps followed by one decoded block. A thousand seeks took 3.8 ms here.
* A sorted-runs index, which the writer builds when asked:

  ```csharp
  new VortexWriteOptions { Indexes = IndexPolicy.None.SortedRuns(Reading.ColumnNames.City) }
  ```

  On a 100 000-row copy of the readings whose `City` changes every row, that index has two runs and
  100 000 entries, and the cursor walks the eight cities in order, each with the rank of its first
  entry. It is not free. It took 212 880 bytes against 154 708 bytes of data. The default budget of
  100 per mille dropped it, and the write report said so. It was only built once the budget was
  raised with `WithBudgetPerMille(20_000)`. The budget weighs the index before it is encoded, here
  2 637 485 bytes, so use the figure from the report, not the size on disk, when deciding how far to
  raise it. [indexes.md](indexes.md) covers the budget.

A column with neither has no cursor:

```
celsius: source None
  OpenAsync: VortexUnsupportedException: Unsupported Vortex component: index 'vorticity.sorted.runs.v1'.
  'Celsius' has no key source (SortedColumn: the file statistics say the column is not sorted; ...).
  A cursor over an unindexed column would have to hold the column to sort it, which this library
  refuses. Write the file with IndexPolicy.SortedRuns for that column, or add it to the written
  file with VortexFileIndexer.AppendIndexesAsync.
```

`Distinct()` on the builder asks for one stop per key instead of one per entry. A postings index or a
dictionary can serve it, since it needs keys without rows. On the sorted `Day` column it stops 1 000
times and still knows its rows (`HasRows` is true).

## Over a result

The scan of a query's result, such as a group by read through `As<TRecord>()`
([aggregates.md](aggregates.md)), has no zone map and no index, so its cursor is the one that sorts.
`OpenAsync` runs the query, keeps the column's non-null values, and sorts them in memory unless they
already arrive in order, as they do from a group by that streams on that key. The plan's source is
then `InMemory`. `Row` is a position in the order the result is delivered, which `Rows(...)` on a
scan of the same result can read. A bool or a decimal column has no key order and throws
`NotSupportedException`.

## Batches in key order

```csharp
Scan<Reading> ordered = file.Scan<Reading>().OrderByDescending(r => r.Day);
```

The batches then arrive in key order instead of file order, from the same sources a cursor uses.
`ExplainAsync().Order` says which one:

```
ordered scan: source SortedColumn, 1 run, 1000000 entries, descending True
  first batch: 8192 rows, day 999 down to 991
  123 batches
```

A null key comes last in both directions. An aggregation ignores the order and gives the same answer
either way, and [aggregates.md](aggregates.md) explains what order a group by returns.

## Watch out

* `Keys` takes the whole column. On a scan that already has a `Where` or a `Rows`, it throws
  `InvalidOperationException: A key cursor walks the whole column; build it on a scan without Where
  or Rows.`
* A failed seek leaves the cursor invalid, not where it was. Seek again before reading `Key`.
* Keys follow the file's order for their type, and the cursor takes no comparer. Compare the keys
  you get from the cursor, which already arrive in that order. A text key is ordered by its UTF-8
  bytes.
* A cursor is not thread-safe, and it holds its source until it is disposed, so use `await using`.
* `SeekAsync` refuses a null key with `ArgumentNullException`.

The design behind this page (the sources of order, how a cursor is positioned, delivery in key
order) is in [12-index-reads.md](../design/12-index-reads.md), and the surface in
[the public API design](../design/14-public-api.md#57-the-key-cursor).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- keys-in-order
```
