# Append and repair

Add rows to a file you already wrote, give an append up, and recover a file whose tail was torn.

```csharp
await using (VortexFileWriter appender = await session.AppendAsync(path))
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

`AppendAsync` opens the file, keeps its chunks, and positions the writer after its rows. The new
rows always follow the file's last row. What `appender.RowCount` says right after the open is where
the rewrite starts:

* a file that ends on a block, 16 384 rows here, is continued as it stands: `RowCount` is its row
  count;
* a file that does not, 20 000 rows here, has its last chunk read and written again with the new
  rows: `RowCount` is 16 384, where that chunk starts. The 3 616 rows of the old tail are the file's
  already and you do not write them again; the writer counts them back in at the first write,
  flush or completion, so `RowCount` after writing 5 000 rows is 25 000, what the file holds, and
  so is `report.RowCount`.

## What an append costs

An append never truncates while it writes: the rewritten chunk's old bytes stay where they were,
which is what a reader falls back to when the append is torn. So the question is how much gets
rewritten. Four appends each way, against the same rows written once:

| | resumes at | after four appends | the same rows written once |
|---|---|---|---|
| appends of 8 192 onto 16 384 | 16 384, 24 576, 32 768, 40 960 | 49 152 rows, 101 485 bytes | 77 468 bytes |
| appends of 5 000 onto 20 000 | 16 384, four times | 40 000 rows, 153 621 bytes | 67 892 bytes |

Appends that end on a block rewrite nothing, and the file is 1.31 times the size of the same rows
written once: every version's footer stays in the file, and each append's rows form chunks of their
own. Appends that do not end on a block keep resuming
at 16 384: the tail chunk grows by each append's rows and is rewritten whole every time, with its
old copy left behind, and the file reaches 2.26 times the size. Write appends in multiples of
`BlockRows`, and rewrite a file that has taken many appends ([copy-a-file.md](copy-a-file.md)),
since nothing else reclaims what they leave behind.

## Giving an append up

```
an append abandoned after a flush: 103836 bytes on disk before Abandon, 29316 after, 29316 before the append
an append disposed without CompleteAsync: 29316 bytes, 29316 before
a created file abandoned: exists False
a created file disposed without CompleteAsync: exists False
```

`Abandon()` on an append truncates the file back to the length it had, flushed bytes included; on a
created file it deletes the file. A writer disposed without `CompleteAsync` does the same. Over a
caller's `PipeWriter` there is nothing to delete, and the pipe is completed with an error instead
([stream-to-an-object.md](stream-to-an-object.md)).

## A torn tail

A crash in the middle of an append leaves bytes after the last complete version. The sample makes one
by cutting the last 100 bytes of a completed append:

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
16384 rows in 29316 bytes, 24576 after the append in 47029, cut to 46929
reading the version before the torn append: 16384 rows, valid to 29316 of 46929
  why: Malformed file: the EOF marker's magic is 0x696e6465, expected 'VTXF'.
```

The file opens as the version before the append, and everything it answers is that version's: rows,
statistics, indexes. `VortexTornTailPolicy.Refuse` in `VortexOpenOptions.TornTail` throws
`VortexFormatException` instead. An append refuses a torn file, with the same exception and a message
that says what to do.

## Repairing

```csharp
long valid = await VortexFileRepair.ValidLengthAsync(path, ct);
VortexRepairResult repaired = await VortexFileRepair.RepairAsync(path, ct);   // truncates to the last version that parses
```

```
valid length 29316; repaired: truncated True, 46929 -> 29316
the repaired file appends again: 24576 rows
```

`ValidLengthAsync` finds the longest prefix that opens without changing the file; `RepairAsync`
truncates to it and leaves a valid file alone. After a repair `TornTail` is null and the file appends
like any other.

**A file with no complete version has nothing to fall back to.** One cut short rather than appended
to, a download that stopped or a disk that filled, throws `VortexFormatException` on open, and so do
`ValidLengthAsync` and `RepairAsync`.

## Watch out

* **Nothing else may hold the file.** An append opens it for writing alone, so a `VortexFile` still
  open over the same path makes `AppendAsync` throw `IOException`.
* **The identity is drawn anew by each append**: every version of the bytes has its own. Pin it
  with `VortexWriteOptions.Identity` for a reproducible write ([writer-options.md](writer-options.md)).
* **An append takes the file's own settings** when its options are null: the block size, the index
  policy, whether it carries statistics, its metadata. The block size is the file's whatever the
  options say. A composite key's encoder is not stored, so give it again.
* **An append is not atomic.** A reader of a file being appended to sees the old version or the new
  one, never half of each, and a crash leaves the torn tail above.
* [11-write-strategy.md](../design/11-write-strategy.md), section 3.8, is the design of the append.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- append-and-repair
```

The figures above come from that run.
