# Cancel work

Every call that touches bytes takes a `CancellationToken`. One does not, and that one is the scan.

## Cancelling a scan

`ExecuteAsync()` takes no token. The loop carries it:

```csharp
await foreach (RecordBatch batch in file.Scan().ExecuteAsync().WithCancellation(cts.Token))
{
    using (batch)
    {
        // ...
    }
}
```

`WithCancellation` is what hands the token to the async enumerator, and the scan polls it between
batches and around each read. Cancelling mid-scan throws `OperationCanceledException` out of the
loop; the batch you were holding is still yours to dispose, and the `using` above has already done
it.

**A scan enumerated without `WithCancellation` cannot be cancelled.** It will run to the end of the
file, or until you `break`. Breaking is a clean way out: the enumerator is disposed, the scan stops,
and nothing throws.

## Cancelling everything else

`OpenAsync`, `CountAsync`, `AnyAsync`, `MinAsync`, `MaxAsync`, `ExplainAsync`,
`ReadIndexesAsync`, `ReadIndexDirectoryAsync`, `ReadMetadataAsync`, `VerifyIndexesAsync`,
`WriteAsync`, `CompleteAsync`, `AppendAsync`, `RepairAsync` all take one as their last argument,
defaulted.

## What a cancelled token actually stops

A call that has no work to do does not look at the token. With a token cancelled before the call:

| | |
|---|---|
| `OpenAsync` | throws |
| the scan, with `WithCancellation` | throws |
| `CountAsync` with a filter | throws |
| `CountAsync` with no filter | **answers** — the row count is in the footer, already read |
| `AnyAsync` | **answers** — the same |
| `MinAsync`, `MaxAsync` | **answers** on a file whose statistics settle it |
| `ExplainAsync` | **answers** — it reads statistics the open already holds |
| `ReadIndexesAsync` | **answers** when the directory is already read |

This is not a bug to work around: a call that reads nothing has nothing to abandon. It does mean
you cannot use a cancelled token as an assertion that nothing ran. Check the token yourself if you
need that.

## Cancelling a write

A cancelled `WriteAsync` or `CompleteAsync` leaves a partial file on disk. There is no rollback: the
file is whatever had been flushed. Delete it, or `Abandon()` the writer before disposing it, which
is the explicit way to say the file is not wanted.

A file whose tail was left half-written by a crash is a different case, and
[append-and-repair.md](append-and-repair.md) covers it.

## Watch out

* `OperationCanceledException` is what comes out, and `TaskCanceledException` derives from it. Catch
  the base.
* The token is not stored: passing one to `OpenAsync` cancels the open and nothing after it. Each
  later call takes its own.
* Cancelling does not dispose the file. `await using` still does that.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- cancel-work
```
