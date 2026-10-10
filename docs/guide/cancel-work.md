# Cancel work

Stop a scan, a sink, a cursor or a writer with a `CancellationToken`, and know what each one leaves
behind.

```csharp
using (CancellationTokenSource cts = new CancellationTokenSource())
{
    int batches = 0;
    try
    {
        await foreach (Columns<Reading> cols in file.Scan<Reading>().WithCancellation(cts.Token))
        {
            if (cols.RowCount > 0 && ++batches == 2)
            {
                await cts.CancelAsync();
            }
        }

        Console.WriteLine($"the scan ended on its own after {batches} batches");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"borrowed columns: cancelled after {batches} batches");
    }
}
```

```
borrowed columns: cancelled after 2 batches
```

Every call that reads or writes takes a token as its last argument, with a default value. When the
token fires, what comes out is an `OperationCanceledException`. Catch that type, since
`TaskCanceledException` derives from it.

## A scan of borrowed columns

`await foreach` passes no token by itself, and a scan whose batches are borrowed `ref struct` columns
is not an `IAsyncEnumerable<T>`, so the scan carries the token: call `WithCancellation(ct)` in the
builder chain, like `With(options)`. `MoveNextAsync` checks it at each batch boundary and passes it to
the reads underneath. The enumerator, which returns the scan's buffers, is disposed on the way out,
cancelled or not. A loop written out by hand passes the token to `GetAsyncEnumerator(ct)` instead,
which takes precedence.

Breaking out of an `await foreach` is the other clean way to stop. The enumerator is disposed, the
scan stops, and nothing throws.

## Rows and owned batches

`ToRecordsAsync(ct)` and `ToBatchesAsync(ct)` take the token directly, and `WithCancellation` works on
them too. They check it at batch boundaries:

```
rows: cancelled after 65536 rows
owned batches: cancelled after 3, each disposed by its using
```

The rows of the batch in hand are still yielded. So the cancellation above, requested at the
10 000th row, surfaced at the end of the batch that held it, the first one, of 65 536 rows. An owned
batch already handed out stays yours to dispose ([owned-batches.md](owned-batches.md)).

## A token cancelled before the call

A call that has nothing to read does not look at the token:

| call | with a cancelled token |
|---|---|
| `OpenAsync` | throws |
| `CountAsync`, no filter | answers, since the row count is in the footer |
| `CountAsync`, with a filter | throws |
| `AnyAsync`, no filter | answers |
| `MaxAsync` | answers, since the file's statistics hold it |
| `SumAsync`, with or without a filter | throws, since this library's files record no sum |
| `ExplainAsync` | throws |
| `GetIndexesAsync` | answers, since the index directory is already read |
| borrowed columns, `ToRecordsAsync` | throw |
| `Keys(...).OpenAsync`, `KeyCursor.SeekAsync` | throw |

This is not a bug to work around. A call that reads nothing has nothing to abandon. It does mean that
a cancelled token does not guarantee nothing ran, so check the token yourself if you need that.

## A cursor

A cancelled move throws and leaves the cursor without a position. `IsValid` returns false, and `Key`,
`Row` and `CountAtKeyAsync` throw `InvalidOperationException` until a move succeeds. Seek again:

```
  the cursor after it: valid False
    its Key: InvalidOperationException: The cursor's last move was cancelled or failed, so it has no position; seek again.
  a new seek with no token: True, key 900
```

## A writer

| call | what a cancellation leaves |
|---|---|
| `WriteAsync` | throws before taking anything when the token is already cancelled. `RowCount` still said 50 000 after a cancelled second write of 50 000 rows, and a builder keeps its rows. Cancelled while the rows are being written, the rows are taken |
| `FlushAsync` | throws |
| `CompleteAsync` | throws, and the writer is done: a second `CompleteAsync` throws `ObjectDisposedException` |

A writer disposed without completing abandons its file. A created file is deleted, and an append is
truncated back to what the file was: 4 220 bytes and 50 000 rows before, and the same after a
cancelled `CompleteAsync` of an append that had flushed 50 000 more rows. `Abandon()` does the same
explicitly. A file whose tail was torn by a crash is a different case, covered in
[append-and-repair.md](append-and-repair.md).

## Watch out

* The token is not stored. A token given to `OpenAsync` cancels the open and nothing after it. Each
  later call takes its own.
* A scan is single-use, cancelled or not. Build another one to start again.
* Cancelling does not dispose the file, the cursor or the writer. `await using` still does that.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- cancel-work
```
