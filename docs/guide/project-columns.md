# Project columns

Read three columns of fifty and pay for three.

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
await foreach (RecordBatch batch in file.Scan().Project(["c", "e"]).ExecuteAsync())
{
    using (batch)
    {
        Console.WriteLine($"{batch.FieldCount} columns: {DTypeFormatter.Format(batch.Schema)}");
    }
}
```

```
2 columns: struct{c: f64, e: f64}
```

The batch carries the projected schema, so `Column(0)` is the first column you asked for and not the
first column of the file.

## What it saves

On a six-column file of 7 261 481 bytes, 500 000 rows each:

| asked for | segments to read | bytes to read |
|---|---|---|
| everything | 12 | 7 247 592 |
| one column | 2 | 1 211 480 |
| two columns | 4 | 2 415 376 |

Measured end to end, the same scan asked the file for 441 311 847 bytes with every column and
73 823 015 with one: six columns' worth, then one. The saving is linear in the columns dropped
because each column's data lives in its own segments — a projection does not read them at all,
rather than reading and discarding.

`ExplainAsync()` is where those figures come from, and it costs no read of the data:

```csharp
ScanPlan whole = await file.Scan().ExplainAsync();
ScanPlan projected = await file.Scan().Project(["c"]).ExplainAsync();
Console.WriteLine($"{whole.SegmentsToRead} segments, {whole.BytesToRead} bytes");
Console.WriteLine($"{projected.SegmentsToRead} segments, {projected.BytesToRead} bytes");
```

## By index

```csharp
file.Scan().ProjectFields([2, 4])
```

The same thing with the field indexes of the file's schema, for a caller that already resolved them
with `IndexOfField`. It skips the name lookup and the UTF-8 comparison.

## Watch out

* **The order you ask for is not the order you get.** `Project(["e", "c"])` delivers `c, e`: the
  projection is a set of fields, and the batch keeps the file's own order. Look columns up by name,
  or by the index of the *projected* schema.
* A name that is not in the schema throws `ArgumentException` when the scan starts, naming the path
  it could not find.
* Nested fields are reached with a dotted path in the same call. The projection is over leaves, so
  asking for one field of a struct reads that field's segments and not its siblings'.
* A projection and a filter compose: the filter may read a column the projection leaves out, and
  that column's segments are read for the filter and not delivered.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- project-columns
```
