# Append and repair

Add rows to a file you already wrote, give an append up, and recover a file whose tail was torn.

```csharp
await using (VortexFileWriter appender = await session.OpenWriterAsync(path))
{
    long resumeAt = appender.RowCount;                         // the file's row count, since it ended on a block
    await appender.WriteAsync<Reading>(more.AsSpan(), ct);
    WriteReport report = await appender.CompleteAsync(ct);
}
```

```
appended 8192 rows to 16384: resumed at 16384, the file now holds 24576
appended 5000 rows to 20000: resumed at 16384, RowCount 25000 after the write, the file now holds 25000
```

## Where an append resumes

`OpenWriterAsync` opens the file, keeps its chunks, and positions the writer after its rows. New rows
always follow the file's last row. What `appender.RowCount` says right after the open is where the
rewrite starts:

* A file that ends on a block boundary, 16 384 rows here, is continued as it stands, and `RowCount`
  is its row count.
* A file that does not, 20 000 rows here, has its last chunk read and written again together with the
  new rows, and `RowCount` is 16 384, where that chunk starts. The 3 616 rows of the old tail already
  belong to the file, so you do not write them again. The writer counts them back in at the first
  write, flush or completion, so `RowCount` after writing 5 000 rows is 25 000, what the file holds,
  and so is `report.RowCount`.

An append holds the file exclusively until it completes or is given up. A second append, or a reader,
fails its open with an `IOException` in the meantime. On Linux and macOS the lock is advisory, so a
process that does not take it, or a file system shared between machines that does not propagate it,
is not held off. Keep to one writer per file.

## What an append costs

An append never truncates while it writes. The old bytes of the rewritten chunk stay where they were,
which is what a reader falls back to when the append is torn. So the question is how much gets
rewritten. Four appends each way, against the same rows written in one go:

| | resumes at | after four appends | the same rows written once |
|---|---|---|---|
| appends of 8 192 onto 16 384 | 16 384, 24 576, 32 768, 40 960 | 49 152 rows, 101 475 bytes | 77 476 bytes |
| appends of 5 000 onto 20 000 | 16 384, four times | 40 000 rows, 153 611 bytes | 67 900 bytes |

Appends that end on a block boundary rewrite nothing, and the file ends up 1.31 times the size of
the same rows written once: every version's footer stays in the file, and each append's rows form
chunks of their own. Appends that do not end on a block boundary keep resuming at 16 384. The tail
chunk grows by each append's rows and is rewritten whole every time, with its old copy left behind,
and the file reaches 2.26 times the size. Write appends in multiples of `BlockRows`, and rewrite a
file that has taken many appends ([copy-a-file.md](copy-a-file.md)), since nothing else reclaims what
they leave behind.

## Giving an append up

```
an append abandoned after a flush: 103836 bytes on disk before Abandon, 29324 after, 29324 before the append
an append disposed without CompleteAsync: 29324 bytes, 29324 before
a created file abandoned: exists False
a created file disposed without CompleteAsync: exists False
```

`Abandon()` on an append truncates the file back to its original length, flushed bytes included. On
a created file it deletes what it wrote. A created file is written next to its path and renamed over
it by `CompleteAsync`, so a file that was already there stays as it was until then, and after an
abandon too. A writer disposed without `CompleteAsync` does the same. Over a caller's `PipeWriter`
there is nothing to delete, so the pipe is completed with an error instead
([stream-to-an-object.md](stream-to-an-object.md)).

## A torn tail

A crash in the middle of an append leaves bytes after the last complete version. The sample creates
one by cutting the last 100 bytes of a completed append:

```csharp
await using (VortexFile file = await VortexFile.OpenAsync(path))
{
    if (file.TornTail is { } torn)
    {
        Console.WriteLine($"reading the version before the torn append: {file.RowCount} rows, valid to {torn.ValidLength} of {torn.FileLength}");
        Console.WriteLine($"  why: {torn.Reason}");
    }
}
```

```
16384 rows in 29324 bytes, 24576 after the append in 47019, cut to 46919
reading the version before the torn append: 16384 rows, valid to 29324 of 46919
  why: Malformed file: the EOF marker's magic is 0x08001400, expected 'VTXF'.
```

The file opens as the version before the append, and everything it answers comes from that version:
rows, statistics, indexes. `VortexTornTailPolicy.Refuse` in `VortexOpenOptions.TornTail` throws
`VortexFormatException` instead. An append refuses a torn file with the same exception and a message
that says what to do.

`ReadPrevious`, the default, looks for that version by walking backwards from the end of the file, a
mebibyte at a time, trying at most 16 end-of-file records on the way. This has two consequences. A
file whose only footer is damaged, rather than torn, is read all the way to its first byte before the
open fails. And a file whose last version was damaged opens at the previous one without an error,
with `TornTail` as the only sign. A reader that must never read an older version without knowing it,
or that opens files it did not write, should set `Refuse`.

## Repairing

```csharp
long valid = await VortexFileRepair.GetValidLengthAsync(path, ct);
VortexRepairResult repaired = await VortexFileRepair.RepairAsync(path, ct);   // truncates to the last version that parses
```

```
valid length 29324; repaired: truncated True, 46919 -> 29324
the repaired file appends again: 24576 rows
```

`GetValidLengthAsync` finds the longest prefix that opens, without changing the file. `RepairAsync`
truncates the file to it, and leaves a valid file alone. After a repair, `TornTail` is null and the
file accepts appends like any other.

A file with no complete version has nothing to fall back to. A file cut short rather than appended
to, such as an interrupted download or a full disk, throws `VortexFormatException` on open, and so do
`GetValidLengthAsync` and `RepairAsync`.

## Watch out

* Nothing else may hold the file. An append opens it exclusively for writing, so a `VortexFile` still
  open on the same path makes `OpenWriterAsync` throw `IOException`.
* Each append draws a new identity, so every version of the bytes has its own. Pin it with
  `VortexWriteOptions.Identity` for a reproducible write ([writer-options.md](writer-options.md)).
* An append takes the file's own settings when its options are null: the block size, the index
  policy, whether it carries statistics, and its metadata. The block size is always the file's,
  whatever the options say. A composite key's encoder is not stored, so pass it again.
* An append is not atomic. A reader that opens the file sees the old version or the new one, never
  half of each, and a crash leaves the torn tail described above.
* [The write strategy](../design/11-write-strategy.md#38-appending-to-a-file) describes the design
  of the append.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- append-and-repair
```

The figures above come from that run.
