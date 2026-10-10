# Vorticity

Vorticity is a pure .NET implementation of the [Vortex](https://vortex.dev) columnar file format,
with no third-party dependency. It reads and writes Vortex files faster than Vortex's own Rust
implementation, and its group bys run faster than DuckDB's.

## Performance

Each figure below is a speedup: the other side's time divided by Vorticity's, so anything above
1.00x means Vorticity is faster. Both sides run as separate processes on the same machine, an Apple
M4 Pro, are compiled ahead of time, and do the same work.

Against Vortex's Rust implementation, Vorticity wins every scenario, often by a factor of two. On a
table of four columns and a million rows:

| scenario | speedup, one core | speedup, 14 cores |
|---|---:|---:|
| read every column | 1.99x | 2.48x |
| read one column of four | 2.31x | 2.51x |
| filter, 1 % of the rows | 1.45x | 3.01x |
| filter, half the rows | 2.08x | 2.37x |
| take 1,000 scattered rows | 1.64x | 1.11x |
| write the table back out | 2.37x | 1.35x |

Against DuckDB, Vorticity's group bys win all 13 queries we measured, on the same files and with the
same number of threads:

| DuckDB reading | speedup, one thread | speedup, 14 threads |
|---|---:|---:|
| its own table, loaded beforehand | 1.43x to 6.15x | 1.18x to 3.50x |
| the file, through its Vortex extension | 1.58x to 5.65x | 1.39x to 4.47x |

The way these numbers are produced is described [below](#how-performance-is-measured), and every
figure is on the pages [against Rust](docs/guide/benchmarks.md),
[against DuckDB](docs/guide/benchmarks-duckdb.md) and [on x64](docs/guide/benchmarks-x64.md).

## Features

* Columns are declared by a `[VortexRecord]` type, and filters and aggregates are plain C# lambdas
  that the scan pushes down into the file. A filter that cannot be pushed down does not compile.
* The core is built for throughput. It allocates next to nothing per batch on the hot paths, uses
  SIMD through `System.Runtime.Intrinsics`, and never copies data before decoding it: a mapped file
  is decoded where it lies, and an uncompressed column is a view of its bytes.
* The API is async only. Every read and every write is awaited (`ValueTask`, `await foreach`),
  whatever the source, so there is no synchronous path to pick by mistake.
* Files follow the published spec, `VTXF` version 1 up to the `core2026.08.3` edition, and every
  file Vorticity writes is read back by the Rust reference in CI.

Beyond the base class library, the core references only `System.IO.Hashing`, a first-party package
used for the write path's hashes and for Bloom filters, and `Vorticity.Zstd`, this repository's
managed Zstandard, which handles `vortex.zstd`.

Parity with Rust means that Rust reads every file Vorticity writes and every value reads back equal.
It does not mean byte parity. The two writers choose their encodings differently by design, so the
same rows produce different bytes on each side.

The project is developed with the help of LLMs, and its API may still change in breaking ways
before v1.0.

## A first look

A record describes the columns of a file. It is a schema rather than a row: each member is a column,
in declaration order and under its own name.

```csharp
using Vorticity;

[VortexRecord]
public partial record struct Reading(int Day, double? Celsius, string City);
```

Writing records out and completing the file:

```csharp
await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>("readings.vortex"))
{
    await writer.WriteAsync<Reading>(readings);          // Reading[]: copied into the builder, column by column
    await writer.CompleteAsync();                        // disposed without it, the file is deleted
}
```

Reading the columns back, each batch is borrowed: its columns are `ref struct`s that stay valid until
the next batch, and the loop allocates next to nothing per batch.

```csharp
await using VortexFile file = await VortexFile.OpenAsync("readings.vortex");

long hot = 0;
await foreach (var (_, celsius, _) in file.Scan<Reading>().Where(r => r.Day >= 900 && r.City == "Paris"))
{
    ReadOnlySpan<double> values = celsius.Values;        // the decoded buffer, no copy
    for (int i = 0; i < values.Length; i++)
    {
        if (celsius.IsValid(i) && values[i] > 30.0) hot++;
    }
}
```

The lambda given to `Where` runs once, when the scan is built, and what it records becomes the plan.
The file statistics, the zone maps and the indexes then skip whatever they can before a block is
decoded. Expressions such as `r.Day >= "900"` or `r.Day % 2 == 0` do not compile.

The same question, and a few more, can be asked without a loop. The answer comes from the statistics
when they settle it, from the encoded blocks otherwise, and never from a materialised batch.

```csharp
long hotDays = await file.Scan<Reading>()
    .Where(r => r.Day >= 900 && r.City == "Paris" && r.Celsius > 30.0)
    .CountAsync();

double? mean = await file.Scan<Reading>().AverageAsync(r => r.Celsius);

Spread spread = await file.Scan<Reading>()
    .Where(r => r.Day >= 900)
    .AggAsync<Spread>(a => (a.Min(r => r.Celsius), a.Max(r => r.Celsius), a.CountDistinct(r => r.City)));

[VortexRecord]
public partial record struct Spread(double? Min, double? Max, long Cities);   // several answers go into a record
```

A group by is a query too, and its result is a scan of a record, read in batches like a file:

```csharp
await foreach (var (city, readings, mean) in file.Scan<Reading>()
    .GroupBy(r => r.City)
    .Select(g => (g.Key, g.Count(), g.Average(r => r.Celsius)))
    .As<CityMean>())
{
    ReadOnlySpan<long> counts = readings.Values;          // one value per city, no copy
}

[VortexRecord]
public partial record struct CityMean(string City, long Readings, double? Mean);
```

When rows are really what you need, ask for them and pay for them: `ToRecordsAsync()` yields one
`Reading` per row, and LINQ takes over from there. A file whose schema is unknown at compile time is
read by column name, `file.Scan("Day", "City").Where($"Day >= {min}")`, with each hole of the filter
passed as a typed value. That is the surface `vxdump` is built on.

## Requirements

The library targets `net11.0`, and `global.json` pins the SDK to a .NET 11 release candidate with
`allowPrerelease`. Until .NET 11 is generally available you need that SDK installed: a machine with
only .NET 10 is refused by `global.json` before anything builds. That SDK is all `dotnet build` and
`dotnet test` need. Comparing against the Rust reference (the cross-check, the ratio gates of the
bench and the corpus generator) also requires a Rust toolchain.

The library ships on nuget.org for projects that target `net11.0`. The generator is a build-time
dependency, and `PrivateAssets` keeps it out of whatever your project packs:

```xml
<PackageReference Include="Vorticity" Version="0.5.1" />
<PackageReference Include="Vorticity.Generators" Version="0.5.1" PrivateAssets="all" />
```

`Vorticity.Dataset` and `Vorticity.RowEncoding` are both experimental. They are referenced the same
way, at the same version as the core.

## Documentation

| document | contents |
|---|---|
| [docs/guide/](docs/guide/README.md) | how to use the library: pages named after what you want to do, each backed by a program the samples project compiles and runs |
| [docs/guide/benchmarks.md](docs/guide/benchmarks.md) | every figure the bench publishes: Vorticity against the Rust implementation, and each kernel against its baseline |
| [docs/guide/benchmarks-x64.md](docs/guide/benchmarks-x64.md) | the same page measured on an x64 machine, a Zen 4 processor under Windows, against a Rust built there |
| [docs/guide/benchmarks-duckdb.md](docs/guide/benchmarks-duckdb.md) | our group bys against DuckDB's on the same files, through its Vortex reader and from its own table, on one thread and on fourteen |
| [docs/design/](docs/design/README.md) | why the library is shaped this way: scope, byte layout, engine, public API |

## How performance is measured

Vorticity is measured against Vortex's Rust implementation on the same files and the same machine,
both built with Native AOT, on one core and on all of them. The bench covers a table read and
written, every encoding decoded, taken from and written, and each hot loop against its baseline.
Both sides do the same work: each maps the file and reads it where it lies, decodes every value it
returns, and hands what it writes to a sink that keeps nothing. On one core, Rust is timed with the
faster of its two ways of splitting a scan. The speedup is Rust's time over Vorticity's, so above
1.00x Vorticity is faster. The table at the top of this page reads the file Vorticity writes.

Per encoding, Vorticity is well ahead on nested, text and compressed integer columns (`struct`
6.92x, `varbin` 6.08x, `map` 3.28x, `pco` 3.06x). It is level with Rust or behind by at most a tenth
on 10 files: plain booleans (0.90x), `onpair` (0.95x), `decimal` (0.96x), masked columns (0.96x to
1.00x), patched bit-packed integers (0.98x), `alprd` and `sequence` (1.00x). Its files are at most
5 % larger than Rust's, or smaller, on 44 of 56 encodings. With both readers warmed up in one
process, Rust is faster on 1 case out of 19, a prefix filter on FSST strings (0.93x).

Every figure is on [the benchmark page](docs/guide/benchmarks.md), each section with the machine
and the commit it was measured on, and [its x64 twin](docs/guide/benchmarks-x64.md) holds the same
sections measured on x64. [docs/design/05-benchmarks.md](docs/design/05-benchmarks.md) explains what
is compared and how, and [bench/README.md](bench/README.md) how to run each instrument.
`bench/gate.sh` runs everything that gates a change before it is pushed.
