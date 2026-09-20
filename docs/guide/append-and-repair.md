# Append and repair

Add rows to a file you already wrote, and recover one whose tail was torn.

## Appending

```csharp
await using VortexFileWriter appender = await VortexFileWriter.AppendAsync(path);
using RecordBatch batch = MakeBatch(rows, startRow: appender.RowCount);
await appender.WriteAsync(batch);
WriteReport report = await appender.CompleteAsync();
```

`appender.RowCount` is **where the append resumes**, and the new batch's `startRow` must be that
number. It is not always the file's row count: the writer keeps the chunks it can and rewrites the
last one, so the rows of that chunk are written again along with yours.

## What an append costs, and how to make it cost less

An append never truncates. The rewritten chunk's old bytes stay in the file — that is what lets a
reader fall back to the previous version when a write is interrupted. So the question is how much
gets rewritten, and that depends on where your batches end.

Four appends of 8 192 rows onto a file of 16 384, against four appends of 5 000 onto 20 000:

| | resumes at | after four appends |
|---|---|---|
| batches of 8 192 onto 16 384 | 16 384, 24 576, 32 768, 40 960 | 49 152 rows, 155 197 bytes |
| the same rows written once | | 49 152 rows, 104 793 bytes |
| batches of 5 000 onto 20 000 | 0, 0, 0, 0 | 40 000 rows, 351 621 bytes |
| the same rows written once | | 40 000 rows, 89 409 bytes |

**Resuming at 0 means the whole file is rewritten, and the old copy stays.** Four appends then left
a file four times the size of the same rows written once. Ending every batch on a multiple of
`RowBlockSize` — 8 192 by default, and `writer.PreferredBatchRows` says so — keeps the appends
incremental: 1.5 times instead of 3.9.

Two habits follow. Write batches that are multiples of the block size, and rewrite the file when it
has taken many appends, because nothing else reclaims what they left behind.

## What else an append changes

* **The identity is minted anew.** Every version of a file's bytes gets its own sixteen bytes, so
  `file.Identity` changes at each append. Pin it through `VortexWriteOptions.Identity` when you want
  the write to be reproducible.
* **`Abandon()` leaves the file exactly as it was** — 71 053 bytes before and after, batch written
  and all. It is the way to say the append is not wanted; disposing without `CompleteAsync` does the
  same.
* **Nothing else may hold the file.** An open `VortexFile` over the same path makes `AppendAsync`
  fail with `IOException`.
* An append refuses a file whose tail is torn: repair it first.

## A torn tail

A crash mid-append leaves bytes after the last complete version. The reader falls back to that
version by default, and says so:

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
if (file.TornTail is { } torn)
{
    Console.WriteLine($"{file.RowCount} rows, valid to {torn.ValidLength} of {torn.FileLength}: {torn.Reason}");
}
```

```
24576 rows, valid to 71053 of 71181
```

Everything the file answers is that earlier version's — rows, statistics, indexes, `FileLength`.
`VortexTornTailPolicy.Refuse` at open turns the fallback into a `VortexFormatException` instead.

## Repairing

```csharp
long valid = await VortexFileRepair.ValidLengthAsync(path);      // 71053
VortexRepairResult result = await VortexFileRepair.RepairAsync(path);
Console.WriteLine($"truncated {result.Truncated}, {result.OriginalLength} -> {result.Length}");
```

```
truncated True, 71181 -> 71053
```

`RepairAsync` truncates the file to the last length that parses. After it, `TornTail` is null and
the file appends again like any other.

**A file with no complete version has nothing to fall back to.** One cut short rather than appended
to — a download that stopped, a disk that filled mid-write — throws on open, and `ValidLengthAsync`
throws rather than returning zero.

## Watch out

* The append's schema must match the file's. Build it from the same shape, not from a guess.
* `report.RowCount` after `CompleteAsync` is the file's total, not what this append added.
* An append is not atomic. That is why the torn-tail fallback exists, and why a reader of a file
  being appended to sees either the old version or the new one, never half of each.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- append-and-repair
```
