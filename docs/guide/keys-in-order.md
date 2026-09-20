# Keys in order

Walk a column in key order: seek, step, rank, and read distinct keys.

A cursor is not a scan. It walks the **keys** of one column in order and tells you which row each
one is at; you then read that row if you want it. It is the shape you want for a lookup, a range
walk, or a "what is the rank of this value" question.

## Opening one

```csharp
await using KeyCursor cursor = await file.Keys("day").OpenAsync();
await cursor.SeekFirstAsync();
Console.WriteLine($"first key {cursor.Key.SignedValue} at row {cursor.Row}");
```

`Keys(column)` returns a builder — `Distinct()`, `WithSource(kind)`, `OpenAsync()`,
`ExplainAsync()`. The cursor is `IAsyncDisposable`, and every move is a `ValueTask<bool>`: `false`
means there was nowhere to go, and `IsValid` is then false.

| move | what it does |
|---|---|
| `SeekFirstAsync`, `SeekLastAsync` | the ends |
| `SeekAsync(literal, op)` | `Exact`, `AtOrAfter`, `After`, `AtOrBefore`, `Before` |
| `SeekRankAsync(n)` | the key at position *n* |
| `NextAsync`, `PrevAsync` | one entry |
| `NextKeyAsync`, `PrevKeyAsync` | one distinct key, skipping repeats |
| `RankAsync(literal)` | how many entries come before a value |
| `KeyCountAsync()` | how many keys there are |

On the demonstration file, whose `day` column rises one per thousand rows:

```
first key 0 at row 0
next distinct key 1 at row 1000
seek to 900 landed on 900 at row 900000
rank of 900: 900000
1000 keys in all
last key 999 at row 999999
an exact seek to a key that is not there: False, cursor valid: False
```

## Where the order comes from

Four sources, and `ExplainAsync()` says which was taken and why the others were not:

```csharp
KeyPlan plan = await file.Keys("day").ExplainAsync();
Console.WriteLine($"source {plan.Source}, {plan.Runs} runs, {plan.EntryCount} entries");
foreach (KeySourceRejection rejected in plan.Rejected)
{
    Console.WriteLine($"  turned down {rejected.Source}: {rejected.Reason}");
}
```

```
source SortedColumn, 1 runs, 1000000 entries, rows reachable: True
  turned down SortedRuns: the index directory has no vorticity.sorted.runs.v1 entry for this column
  turned down Postings: it holds keys without rows, which only a Distinct() cursor takes
  turned down Dictionary: it holds keys without rows, which only a Distinct() cursor takes
```

`SortedColumn` is free: the file's statistics say the column is sorted, so the column *is* the
index. `SortedRuns` is an index you asked the writer for. `Postings` and `Dictionary` hold keys
without the rows they belong to, so a plain cursor cannot use them — a `Distinct()` one can.

A column with no order has no cursor at all:

```
celsius: source None
  turned down SortedColumn: the file statistics say the column is not sorted
```

That is the whole prerequisite: **order the rows by the column you want to walk**, or ask the writer
for a `SortedRuns` index over it.

## Distinct keys

```csharp
await using KeyCursor distinct = await file.Keys("day").Distinct().OpenAsync();
```

One stop per value rather than one per row, and `HasRows` says whether this source can also hand you
the rows. It is what a dictionary or a postings list is good for, and the only mode those two
sources serve.

## A scan delivered in key order

```csharp
await foreach (RecordBatch batch in file.Scan().InKeyOrder("day", descending: true).ExecuteAsync())
```

The batches then arrive in the column's order rather than the file's — the first row delivered above
has `day` 999. `ExplainAsync().Order` describes it: source, runs, entries in range, direction.

**A key-ordered scan chooses its own rows.** `Rows(...)` on top of it throws
`InvalidOperationException: A key-ordered scan walks its key source; Rows cannot also select its
rows.`

## Watch out

* `cursor.Key` is a `FilterLiteral`: read it through `SignedValue`, `UnsignedValue`, `FloatValue`,
  `BytesValue` according to `KeyKind`. `KeyBytes` is the encoded form, and `KeyFormat` is empty for
  a cursor over a plain sorted column.
* `Compare(a, b)` compares two literals the way the cursor orders them, which is the file's order
  and not .NET's.
* A failed `SeekAsync` leaves the cursor invalid, not where it was. Seek again.
* `EntryCount` is `null` when the source cannot say without reading.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- keys-in-order
```
