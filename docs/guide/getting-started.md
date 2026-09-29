# Getting started

Declare a record, write a hundred thousand rows, read them back, take a mean and count a filter.
One file of code, a few minutes, and the only page that shows both directions.

## Reference the library

The library ships on nuget.org. Reference its packages from a project that targets .NET 11; the
generator is a build-time dependency, which `PrivateAssets` keeps out of whatever your project
packs:

```xml
<ItemGroup>
  <PackageReference Include="Vorticity" Version="0.1.0" />
  <PackageReference Include="Vorticity.Generators" Version="0.1.0" PrivateAssets="all" />
</ItemGroup>
```

`Vorticity` is the whole format: opening, scanning, filtering, aggregating, indexes and the
writer, with `System.IO.Hashing` as its only dependency. `Vorticity.Generators` runs at build time
only: it turns a `[VortexRecord]` type into the code that reads and writes it, and ships the
analyzers. The two other packages are separate subjects, both experimental, and referenced the
same way at the same version as the core: `Vorticity.Dataset` for a versioned dataset over an
object store ([datasets.md](datasets.md)), `Vorticity.RowEncoding` for byte-sortable keys
([row-keys.md](row-keys.md)).

Everything that touches bytes is asynchronous: a file is opened with `await`, a scan is consumed
with `await foreach`, a writer is fed with `WriteAsync`. There is no synchronous path to look for.

## Declare a record

```csharp
[VortexRecord]
public partial record struct Reading(int Day, double? Celsius, string City);
```

A record is a schema, not a row: its members are the columns, in declaration order, under their
own names. `double?` makes `Celsius` a nullable column; `int` and `string` are not nullable. The
type must be `partial`, because the generator adds the `IVortexRecord<Reading>` implementation and
the members the examples below use (`r.Celsius`, the deconstruction of a batch).
[records.md](records.md) says what else a record can hold.

## Write a file

```csharp
string[] cities = ["Paris", "Lyon", "Marseille", "Toulouse"];

Reading[] readings = new Reading[100_000];
for (int i = 0; i < readings.Length; i++)
{
    double? celsius = i % 50 == 0 ? null : 10.0 + i % 400 / 10.0;
    readings[i] = new Reading(i / 1_000, celsius, cities[i / 7 % cities.Length]);
}

await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
{
    await writer.WriteAsync<Reading>(readings);
    WriteReport report = await writer.CompleteAsync();
    Console.WriteLine($"wrote {report.RowCount} rows in {report.Bytes.Total} bytes");
}
```

`CompleteAsync` is what makes the bytes a file: it writes the last block, the statistics, the zone
maps and the footer. A writer disposed without it deletes what it wrote.

## Read it back

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
Console.WriteLine($"{file.Schema}, {file.RowCount} rows");

long batches = 0;
long rows = 0;
long nulls = 0;
await foreach (var (day, celsius, _) in file.Scan<Reading>())
{
    batches++;
    rows += day.Length;
    nulls += celsius.NullCount;
}

double? mean = await file.Scan<Reading>().AvgAsync(r => r.Celsius);
long hot = await file.Scan<Reading>().Where(r => r.Celsius > 45.0 && r.City == "Paris").CountAsync();
```

It prints:

```
wrote 100000 rows in 157980 bytes
struct{Day: i32, Celsius: f64?, City: utf8}, 100000 rows
3 batches, 100000 rows, 2000 without a temperature
mean 30.00 degrees
3039 readings above 45 degrees in Paris
```

## What happened

* **The rows became columns.** About 1.85 MB of values became a file of 157 980 bytes, because the
  writer chose an encoding per column and per chunk instead of storing what it was handed.
* **The scan came back in batches, not rows**: 3 of them, one per chunk the writer made of the
  file's 8 192-row blocks. `day`, `celsius` and `city` are `Column<T>` values over the decoded
  batch. They are borrowed: valid inside the loop body, and the compiler refuses to let one outlive
  it ([scan-a-table.md](scan-a-table.md)).
* **`AvgAsync` ran inside the scan.** No batch reached the caller; the nulls were skipped for you.
  An aggregate is an operator, not a loop you write ([aggregates.md](aggregates.md)).
* **The filter is not a delegate.** The lambda given to `Where` runs once, when the scan is built,
  over a symbolic record: `r.Celsius` is a `Sym<double?>`, and `>` records a predicate instead of
  comparing. A breakpoint inside the lambda sees `Sym<double?>`, not values, and hits once. What
  compiles is exactly what the scan can push down ([filter-rows.md](filter-rows.md)).

## Next

* [open-a-file.md](open-a-file.md): the ways in, and what an open reads.
* [scan-a-table.md](scan-a-table.md): the loop above, and who owns what.
* [filter-rows.md](filter-rows.md): how not to read the rows you do not want.
* [write-a-file.md](write-a-file.md): the writer, column by column.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- getting-started
```
