# Getting started

Write a million rows, read them back, and take the mean of one column. Ten minutes, two files, no
concepts to learn first.

## Reference the library

Nothing is published to nuget.org yet, so there is no version to ask for. Until the first release,
reference the project:

```xml
<ProjectReference Include="path/to/Vorticity/src/Vorticity/Vorticity.csproj" />
```

`Vorticity` is the whole format: opening, decoding, scanning, filtering, indexes and the writer.
The two other assemblies are separate subjects — `Vorticity.Dataset` for a versioned dataset over
an object store, `Vorticity.RowEncoding` for the byte-sortable row encoding — and neither is
needed to read or write a file.

The library is async-only: everything that touches bytes returns a `ValueTask` or an
`IAsyncEnumerable`.

## Write a file

A file has a schema, and rows arrive in batches. A batch is built in an arena: you ask it for a
buffer per column, fill the buffer in place, and tell it what the buffer is.

```csharp
DTypeArena types = new DTypeArena();
DType schema = types.Struct(
    ["day", "celsius"],
    [types.Primitive(PType.I32, Nullability.NonNullable),
     types.Primitive(PType.F64, Nullability.NonNullable)],
    Nullability.NonNullable);

await using VortexFileWriter writer = VortexFileWriter.Create(path, schema);
CanonicalArena arena = new CanonicalArena();

VortexBuffer days = arena.Allocate(rows * sizeof(int), 8, out Span<byte> dayBytes);
VortexBuffer degrees = arena.Allocate(rows * sizeof(double), 8, out Span<byte> degreeBytes);
Span<int> day = MemoryMarshal.Cast<byte, int>(dayBytes);
Span<double> celsius = MemoryMarshal.Cast<byte, double>(degreeBytes);
for (int i = 0; i < rows; i++)
{
    day[i] = i / 1_000;
    celsius[i] = 10.0 + (i * 7919L % 4001) / 100.0;
}

int dayNode = arena.AddPrimitive(schema.GetField(0), rows, Validity.NonNullable, PType.I32, days);
int celsiusNode = arena.AddPrimitive(schema.GetField(1), rows, Validity.NonNullable, PType.F64, degrees);
int root = arena.AddStruct(schema, rows, Validity.NonNullable, [dayNode, celsiusNode]);

using RecordBatch batch = new RecordBatch(arena, root, 0);
await writer.WriteAsync(batch);
await writer.CompleteAsync();
```

`CompleteAsync` is what makes the file a file: it writes the statistics, the zone maps, any index
the writer decided to build, and the footer. A writer disposed without it leaves nothing usable
behind.

## Read it back

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
double total = 0;
long seen = 0;
await foreach (RecordBatch batch in file.Scan().Project(["celsius"]).ExecuteAsync())
{
    using (batch)
    {
        total += Sum(batch);
        seen += batch.RowCount;
    }
}

Console.WriteLine($"{seen} rows of {file.RowCount}, mean {total / seen:F2} degrees");

static double Sum(RecordBatch batch)
{
    ReadOnlySpan<double> values = batch.Column("celsius"u8).AsPrimitive<double>().Values;
    double sum = 0;
    foreach (double value in values)
    {
        sum += value;
    }

    return sum;
}
```

It prints:

```
1000000 rows of 1000000, mean 30.00 degrees
12000000 bytes of values became a file of 1523369
```

Three things happened worth naming. The scan came back in batches, not rows — 123 of them here, of
at most 8 192 rows. `Values` is the decoded column itself, a span over the batch's own memory, so
reading it copies nothing. And the twelve megabytes of values became a file of 1.5 MB, because the
writer chose an encoding per column rather than storing what you handed it.

## Next

* [open-a-file.md](open-a-file.md) — the three ways in, and what each costs.
* [scan-a-table.md](scan-a-table.md) — the loop above, in full, and who owns what.
* [write-a-file.md](write-a-file.md) — text columns, nulls, and what the writer decided.
* [filter-rows.md](filter-rows.md) — how to not read the rows you do not want.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- getting-started
```
