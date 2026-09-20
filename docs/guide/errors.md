# Errors

Two exceptions are this library's own. Everything else that comes out of it is a .NET exception
meaning you called it wrong.

## `VortexFormatException`

The bytes are not what they claim to be, or a limit says no. This is the file's fault, or the
caller's for pointing at the wrong bytes — never something to retry.

```
512 zero bytes -> VortexFormatException: Malformed file: the EOF marker's magic is 0x00000000, expected 'VTXF'.
```

It also carries refusals that protect the reader from a hostile file:

```
Decoding vortex.runend would materialize 3997696 bytes, above the 4096-byte decompression ceiling.
```

`limits.md` lists those ceilings and how to move them.

## `VortexUnsupportedException`

The file is well formed and asks for something this build does not have: an encoding, a layout, a
dtype, a compression scheme. It names what is missing:

```csharp
catch (VortexUnsupportedException e)
{
    Console.WriteLine($"{e.Kind}: {e.ComponentId}");   // for example  array: vortex.something
}
```

`Kind` is one of the strings in `VortexComponentKind` — `array`, `layout`, `dtype`, `compression`,
`aggregate`, `encryption`. Opening with `AllowUnknownComponents = true` postpones the refusal: the
file opens, and the exception comes when something actually needs the component. That is the
difference between a file you cannot open and a file you can read most of.

## The .NET ones

| | when |
|---|---|
| `FileNotFoundException` | no file at that path |
| `IOException` | the file is held by something else — an append while a reader has it open |
| `ArgumentException` | a column name that is not in the schema, in a projection or a filter |
| `ArgumentOutOfRangeException` | a row index below zero or past the last row |
| `InvalidOperationException` | a builder asked for two things at once, such as a range and an index list |
| `ObjectDisposedException` | a batch used after it was disposed |
| `OperationCanceledException` | see [cancel-work.md](cancel-work.md) |

The messages name the thing they refused:

```
The projection path 'nope' does not name a field of the file's schema. (Parameter 'paths')
The file has 1000000 rows. (Parameter 'rows')  Actual value was -1.
A scan selects rows by range or by index list, not both.
This RecordBatch has been disposed; every span borrowed from it is invalid (docs/07-dotnet-mapping.md §4).
```

## A torn tail

A file whose last append did not finish — the process died, the disk filled — has bytes after the
last complete version. By default the reader falls back to that version:

```csharp
await using VortexFile file = await VortexFile.OpenAsync(torn);
Console.WriteLine($"{file.RowCount} rows, valid up to {file.TornTail?.ValidLength} " +
    $"of {file.TornTail?.FileLength}: {file.TornTail?.Reason}");
```

```
1000000 rows, valid up to 1523369 of 1523497: Malformed file: the EOF marker's magic is 0x00000000, expected 'VTXF'.
```

`TornTail` is `null` for a file that opened whole, so it is also the way to ask *whether* this
happened. Everything the file answers is the earlier version's: rows, statistics, indexes,
`FileLength`.

Two things change that. `TornTailPolicy.Refuse` makes the open throw `VortexFormatException`
instead. And `VortexFileRepair` truncates the file back to the version that parses:

```csharp
VortexRepairResult result = await VortexFileRepair.RepairAsync(torn);
Console.WriteLine($"truncated: {result.Truncated}, {result.OriginalLength} -> {result.Length}");
```

```
truncated: True, 1523497 -> 1523369
```

A file with no complete version anywhere in it — one cut short rather than appended to — has nothing
to fall back on: it throws on open, and `ValidLengthAsync` throws too rather than returning zero.

## Watch out

* **A disposed `VortexFile` does not refuse every question.** `RowCount`, `Schema`, `FileLength` and
  `Scan()` still answer after `DisposeAsync`; only `Identity` throws `ObjectDisposedException`.
  Treat a disposed file as gone regardless — a scan started from one will fail at its first read.
* **A `RecordBatch` does refuse.** Reading it after `Dispose()` throws, which is what stops a span
  over recycled memory from being read.
* **A row range past the end is clamped, not refused.** `Rows(new RowRange(0, rows + 10))` returns
  every row and no exception; a *row index* past the end throws.
* **A filter literal of the wrong type matches nothing** instead of throwing. See
  [filter-rows.md](filter-rows.md).

## Run it

```
dotnet run --project samples/Vorticity.Samples -- errors
```
