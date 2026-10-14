# Vorticity

[![NuGet](https://img.shields.io/nuget/v/Vorticity.svg)](https://www.nuget.org/packages/Vorticity)
[![CI](https://github.com/Evariops/Vorticity/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/Evariops/Vorticity/actions/workflows/build.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

Vorticity reads, writes and queries columnar files in .NET. It speaks [Vortex](https://vortex.dev)
and [Apache Parquet](https://parquet.apache.org), works on a local disk or straight against S3, Azure
Blob Storage and Google Cloud Storage, and is written in C# from end to end, with no native library
and no third-party dependency.

It is also built for speed: it reads and writes Vortex faster than Vortex's own Rust implementation,
its group bys beat DuckDB's on every query we measured, and the managed Zstandard it relies on
decompresses faster than the native libzstd. The figures are [at the end of this page](#performance).

## Quick start

The packages target .NET 11. The generator runs at build time only, and `PrivateAssets` keeps it out
of whatever your project packs:

```xml
<PackageReference Include="Vorticity" Version="0.6.0" />
<PackageReference Include="Vorticity.Generators" Version="0.6.0" PrivateAssets="all" />
```

A record declares the columns of a file. It is a schema rather than a row: each member is a column,
in declaration order and under its own name.

```csharp
using Vorticity;

[VortexRecord]
public partial record struct Reading(int Day, double? Celsius, string City);
```

Write the records, then ask the file a question:

```csharp
await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>("readings.vortex"))
{
    await writer.WriteAsync<Reading>(readings);   // a Reading[], copied in column by column
    await writer.CompleteAsync();                 // disposed without it, the file is deleted
}

await using VortexFile file = await VortexFile.OpenAsync("readings.vortex");

long hotDays = await file.Scan<Reading>()
    .Where(r => r.Day >= 900 && r.City == "Paris" && r.Celsius > 30.0)
    .CountAsync();
```

The lambda given to `Where` runs once, when the scan is built, and what it records becomes the plan.
The file's statistics, zone maps and indexes then skip whatever they can before a block is decoded,
and the count comes from the statistics when they settle it, from the encoded blocks otherwise, never
from materialised rows. A filter the scan cannot push down, such as `r.Day % 2 == 0`, does not
compile.

A group by is a query too, written as a method chain or in LINQ's query syntax:

```csharp
var hottest =
    from r in file.Scan<Reading>()
    where r.Day >= 900
    group r by r.City into g
    orderby g.Average(x => x.Celsius) descending
    select (g.Key, g.Count(), g.Average(x => x.Celsius));

await foreach (CityHeat city in hottest.Take(3).As<CityHeat>().ToRecordsAsync())
{
    Console.WriteLine($"{city.City}: {city.Mean:F1} °C over {city.Readings} readings");
}

[VortexRecord]
public partial record struct CityHeat(string City, long Readings, double? Mean);
```

When the values themselves are what you need, a scan lends each batch's columns as spans over the
decoded buffers, with no copy and next to no allocation per batch:

```csharp
long hot = 0;
await foreach (var (_, celsius, _) in file.Scan<Reading>().Where(r => r.City == "Paris"))
{
    ReadOnlySpan<double> values = celsius.Values;   // the decoded buffer, valid until the next batch
    for (int i = 0; i < values.Length; i++)
    {
        if (celsius.IsValid(i) && values[i] > 30.0) hot++;
    }
}
```

`ToRecordsAsync()` yields one record per row when rows are what you need, and a file whose schema is
only known at run time is read by column name, as in `file.Scan("Day", "City").Where($"Day >= {min}")`.

## Parquet

A Parquet file goes through the same engine, so the same records, filters, aggregates and group bys
work on it, and one scan converts it to Vortex:

```csharp
using Vorticity.Parquet;

#pragma warning disable VX0003   // the Parquet API is experimental until its surface settles

await using ParquetFile parquet = await ParquetFile.OpenAsync("readings.parquet");
double? lyon = await parquet.Scan<Reading>().Where(r => r.City == "Lyon").AverageAsync(r => r.Celsius);

await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>("readings.vortex");
await writer.WriteAsync(parquet.Scan<Reading>());
await writer.CompleteAsync();
```

`VortexSession.Default.CreateParquetWriter<Reading>(path)` writes Parquet the same way. Row group
statistics, the page index, Bloom filters and dictionaries prune a scan before a page is read.

## Object storage

With `Vorticity.ObjectStorage`, a location opens and writes like a path:

```csharp
using Vorticity.ObjectStorage;

await using VortexSession session = VortexSession.Create(o => o.ObjectStores.UseEnvironment());

await using VortexFile file = await session.OpenAsync("s3://lake/readings/2026-10.vortex");
double? mean = await file.Scan<Reading>().Where(r => r.Day >= 900).AverageAsync(r => r.Celsius);
```

`UseEnvironment` serves every `s3://`, `az://` and `gs://` location from the environment, which it
reads the way each cloud's own SDKs do, Kubernetes workload identities included.
`AWS_ENDPOINT_URL_S3` points `s3://` at R2, OVHcloud, Scaleway or any other service that speaks the
S3 API. Opening a file costs a single request on Amazon S3, a scan coalesces each batch's reads into
ranges of up to 16 MiB issued together, and a large write uploads its parts in parallel as they fill.
Parquet files open from a local path for now. [object-storage.md](docs/guide/object-storage.md)
covers stores, credentials, retries and what each operation costs.

## Building from source

`global.json` pins a .NET 11 release candidate SDK, so until .NET 11 ships, a machine with only .NET 10
is refused before anything builds. That SDK is all `dotnet build` and `dotnet test` need. A Rust
toolchain is needed only to compare against the Rust reference: the cross-check, the bench's ratio
gates and the corpus generator. [CONTRIBUTING.md](CONTRIBUTING.md) says what to run before a change
is done.

## Documentation

| where | what you find |
|---|---|
| [docs/guide/](docs/guide/README.md) | how to use the library, one page per task, each backed by a program the samples project compiles and runs |
| [docs/design/](docs/design/README.md) | why the library is shaped this way: scope, byte layout, engine, Parquet, object storage, encryption, public API |
| [bench/README.md](bench/README.md) | how to run the bench, and the gates a change has to pass |

## Performance

Where Vorticity is compared with another implementation, the figure is a speedup: the other side's
time divided by Vorticity's, so anything above 1.00x means Vorticity took less time. The machines
are an Apple M4 Pro (arm64, 14 cores, macOS) and an AMD Ryzen 9 7950X (x64, 16 cores and 32
threads, Windows 11). The figures against Rust were measured on 2026-10-14 on the M4 Pro and on
2026-10-12 on the Ryzen, those for Parquet on 2026-10-12 and those against DuckDB on 2026-10-14, and
[how it is measured](#how-it-is-measured) says what both sides do.

### Against Vortex's Rust implementation

Vortex Rust 0.86.1 and Vorticity, each in its own process, read and write a table of four columns
and a million rows. On the file Vorticity writes:

| scenario | M4 Pro, 1 core | M4 Pro, 14 cores | 7950X, 1 core | 7950X, 32 threads |
|---|---:|---:|---:|---:|
| read every column | 2.22x | 2.35x | 2.91x | 1.63x |
| read one column of four | 2.89x | 2.59x | 2.74x | 2.08x |
| filter, 1 % of the rows | 1.38x | 2.72x | 1.27x | 2.40x |
| filter, half the rows | 2.14x | 2.05x | 2.69x | 1.77x |
| take 1,000 scattered rows | 1.73x | 1.06x | 1.47x | 1.16x |
| write the table back out | 2.32x | 1.94x | 2.40x | 1.71x |

Rust keeps the lead in a few places on the M4 Pro: a prefix filter over FSST strings (0.94x) and 10
of the 61 encodings decoded alone, from 0.86x to 1.00x, mostly columns stored plain or masked. The
Pcodec files decode in less time there, from 1.07x to 3.59x. On the Ryzen, Vorticity decodes all 61
encodings in less time, Pcodec files included.

### Against DuckDB

DuckDB 1.5.2 answers the same group bys on the same files with the same number of threads, from a
table of its own loaded beforehand and through its Vortex extension. Vorticity leads all 13 queries
on both, from 40 million rows grouped by 10³ to 10⁷ keys to db-benchmark's group by on 10 million
rows:

| DuckDB reading | one thread | 14 threads |
|---|---:|---:|
| its own table, loaded beforehand | 1.43x to 6.14x | 1.18x to 3.61x |
| the file, through its Vortex extension | 1.57x to 5.81x | 1.38x to 4.44x |

The narrowest lead is db-benchmark's q10 on 14 threads, six keys and nearly a group per row, at
1.18x. These figures come from the M4 Pro.

### Parquet

Parquet reads through the same engine as Vortex: a page decompresses straight into the buffer that
becomes its column, plain values and dictionaries are read as views rather than copied, and a scan
of the taxi trips below, 3 million rows, allocates 82 KiB. The table times a scan of every column of
three real files on the Ryzen 9 7950X, against the same rows rewritten as Vortex:

| scan of every column, in ms | Parquet, 1 thread | Parquet, 32 threads | Vortex, 32 threads |
|---|---:|---:|---:|
| New York's taxi trips, 3.1M rows of 19 columns, GZIP | 158 | 37.7 | 22.2 |
| ClickBench's hits, 1M rows of 105 columns, Snappy | 174 | 74.3 | 67.5 |
| a nested file, 2M rows of 22 columns, Snappy | 119 | 76.3 | 62.0 |

On one thread the codec weighs most: inflating the taxi trips' GZIP pages takes 121 ms of the 158,
even though Vorticity decodes GZIP with its own DEFLATE decoder, 19 % faster than .NET's
`GZipStream`.

### Zstandard and LZ4

The codecs come from [ManagedCompressions](https://github.com/Evariops/ManagedCompressions), which
writes the exact bytes of libzstd 1.5.7 and lz4 1.10.0. Against the native libraries, on
silesia.tar in one call, at the levels in use:

| managed against native | Apple M4 Pro | Ryzen 9 7950X |
|---|---:|---:|
| Zstandard, decompression | 1.30x to 1.49x | 1.01x to 1.11x |
| Zstandard, compression | 0.94x to 1.03x | 0.92x to 1.09x |
| LZ4, decompression | 1.03x to 1.04x | 0.99x to 1.08x |
| LZ4, compression | 0.85x to 0.96x | 0.88x to 0.95x |

On x64, native libzstd decodes with hand-written assembly that arm64 does not have, which is why the
managed decoder's lead is smaller there.

### How it is measured

Against Rust, both sides are compiled ahead of time, run on the same machine and do the same work.
Each opens the file anew at every call, decodes every value it returns to its plain form and must
return the same rows, and a write hands its bytes to a sink that keeps none. On the M4 Pro both read
the file mapped, where it lies. Under Windows, Rust maps the file while Vorticity copies the segments
it reads, as a session that keeps no mapping does there. On both machines both sides also read
every value they return, the same bytes, as the Parquet figures read theirs on the caller's thread.
Rust is built the way upstream builds its own benchmarks, and on one core its figure is the faster
of its two ways of splitting a scan.
Against DuckDB, the two sides take turns in pairs and must give the same answer to every query. The
page cache is warm, cores are not pinned, and your data and your machine will differ.

Every figure is on [the arm64 page](docs/guide/benchmarks.md), [the x64
page](docs/guide/benchmarks-x64.md), [the DuckDB page](docs/guide/benchmarks-duckdb.md) and [the
Parquet page](docs/guide/benchmarks-parquet-x64.md), each with its machine, commit and date.
[05-benchmarks.md](docs/design/05-benchmarks.md) explains what is compared and how, and
`bench/gate.sh` runs everything that gates a change before it is pushed.

## Status and license

Vorticity is developed with the help of LLMs, and its API may still change in breaking ways before
v1.0. It is licensed under Apache-2.0, as [LICENSE](LICENSE) and [NOTICE](NOTICE) say.

Vortex™ is a trademark of LF Projects, LLC, DuckDB is a trademark of the DuckDB Foundation, and
Apache Parquet is a trademark of The Apache Software Foundation. Vorticity is an independent
implementation, not affiliated with or endorsed by any of them.
