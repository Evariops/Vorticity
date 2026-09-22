# Read rows

Get records, one per row, when rows are what the code wants, and know what that costs.

```csharp
int shown = 0;
long kept = 0;
await foreach (Reading r in file.Scan<Reading>().Where(r => r.Day == 900).ToRecordsAsync(ct))
{
    if (shown++ < 3)
    {
        Console.WriteLine($"{r.City}: {r.Celsius?.ToString() ?? "null"}");
    }

    kept++;
}

List<Reading> hottest = await file.Scan<Reading>()
    .ToRecordsAsync(ct)
    .OrderByDescending(r => r.Celsius)
    .Take(10)
    .ToListAsync(ct);
```

```
Toulouse: null
Toulouse: 10.1
Toulouse: 10.2
1000 rows for day 900
hottest, sorted on the client: 49.9 in Lyon on day 0
```

`ToRecordsAsync` is a sink, like `await foreach` over the scan or `CountAsync`: it runs the scan and
turns each surviving row into a `Reading`, through the `ReadRows` the generator wrote for the record
(see [records.md](records.md)). The record is the projection, the `Where` is pushed down exactly as
in [filter-rows.md](filter-rows.md), and only what survives becomes a row.

## LINQ starts after the sink

`ToRecordsAsync` returns an `IAsyncEnumerable<Reading>`, so `System.Linq.AsyncEnumerable`, in the
shared framework, applies to it: `OrderByDescending`, `Take`, `ToListAsync` above. They run on the
client, over rows already materialised. Everything to the left of `ToRecordsAsync` is pushed into
the scan; nothing to the right of it is, and the code shows where the line falls.

## What it costs

Measured on the demonstration file, a million rows, warmed, the best of five passes:

| | time | allocated |
|---|---|---|
| every row as a `Reading` | 28 ms | 35 MiB |
| the same values as columns, `await foreach` | 10 ms | 111 KiB |
| the ten hottest, sorted on the client | 161 ms | 148 MiB |
| `MaxAsync(r => r.Celsius)` | 0.002 ms | 2 280 bytes, no request, no block decoded |
| `MaxAsync`, then `Where(r => r.Celsius >= max).ToRecordsAsync()` | 10 ms | 528 KiB, 2 500 rows |

A row costs a field copy, and an allocation per row for every `string`, list or nested class member:
here the 35 MiB are the million `City` strings. The columns cost neither, because a text column is
read as UTF-8 spans ([text-columns.md](text-columns.md)).

The client-side top ten reads the whole file and materialises a million rows to keep ten of them.
The pushed forms ask the file first: `MaxAsync` is answered from the file's statistics without a
read, and a filter on the answer materialises the 2 500 rows that hold it and nothing else. A key
that is sorted has a third form, `OrderBy` on it, which delivers the batches in its order so that
the first rows are the answer ([keys-in-order.md](keys-in-order.md)).

## Watch out

* **A row is a copy.** Changing a `Reading` changes nothing in the file, and holding a million of
  them holds a million copies.
* The scan behind `ToRecordsAsync` is single-use, like every scan: build another one to enumerate
  again.
* Cancellation reaches the sink at a batch boundary: the rows of the batch in hand are still
  yielded ([cancel-work.md](cancel-work.md)).
* With `ScanOptions.Compact = false`, or on a take by position, the scan delivers whole blocks and
  `ToRecordsAsync` yields only their selected rows, in order: `Rows(4, 900_000)` yields 2 records,
  and `Celsius > 49` under `Compact = false` yields 22 500, the coldest of them 49.1.

The sinks and what each one pays for are listed in §5.6 of
[14-public-api.md](../design/14-public-api.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- read-rows
```
