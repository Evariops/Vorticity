# Vorticity

Vorticity is a **high-performance**, pure .NET implementation of the [Vortex](https://vortex.dev)
columnar file format, with no dependencies: it reads and writes Vortex files faster than Vortex's
own Rust implementation, and its group bys run faster than DuckDB's.

## Performance

Each speedup below is the other side's time divided by Vorticity's, so anything above 1.00x means
Vorticity is faster. Both sides run as separate processes on the same machine, an Apple M4 Pro with
Native AOT, and do the same work.

Against Vortex's own Rust implementation, Vorticity is faster in every scenario, and often twice as
fast. On a table of four columns and a million rows:

| scenario | speedup, one core | speedup, 14 cores |
|---|---:|---:|
| read every column | 1.99x | 2.48x |
| read one column of four | 2.31x | 2.51x |
| filter, 1 % of the rows | 1.45x | 3.01x |
| filter, half the rows | 2.08x | 2.37x |
| take 1,000 scattered rows | 1.64x | 1.11x |
| write the table back out | 2.37x | 1.35x |

Against DuckDB, its group bys win all 13 queries we measured, on the same files and with the same
number of threads:

| DuckDB reading | speedup, one thread | speedup, 14 threads |
|---|---:|---:|
| its own table, loaded beforehand | 1.43x to 6.15x | 1.18x to 3.50x |
| the file, through its Vortex extension | 1.58x to 5.65x | 1.39x to 4.47x |

You can read [how it is measured](#how-performance-is-measured) below, and find every figure on the
pages [against Rust](docs/guide/benchmarks.md), [against DuckDB](docs/guide/benchmarks-duckdb.md)
and [on x64](docs/guide/benchmarks-x64.md).

## Features

* **A typed surface.** A `[VortexRecord]` declares the columns; filters and aggregates are C#
  lambdas the scan pushes down to the file, and what cannot be pushed down does not compile.
* Ultra-optimized core: near-zero allocations per batch on hot paths, SIMD (`System.Runtime.Intrinsics`),
  no copy before decoding: a mapped file is decoded where it lies, and an uncompressed column is a
  view of its bytes.
* **Async-only** public API: every read and every write is awaited (`ValueTask`, `await foreach`),
  whatever the source; there is no synchronous path to choose.
* Conformant to the published spec (`VTXF` v1, `core2026.08.3` edition).
* Validated by **cross-testing** against the Rust reference implementation.

No third-party package: the base class library, plus `System.IO.Hashing`, first party, for the
hashes of the write path and the Bloom filters, and `Vorticity.Zstd`, this repository's managed
Zstandard, for `vortex.zstd`.

**What parity means here.** Files written by this library are read by Vortex Rust, and every value
in them reads back equal — that is the parity claimed and the cross-check is what proves it. It is
not byte parity: the same data is encoded differently on the two sides by design, and a file from
one is not expected to match the other byte for byte.

**Status.** This project is developed with the help of LLMs. Its API may change in breaking ways
before v1.0.

## A first look

A record is a schema, not a row: its members are the file's columns, in order, under their names.

```csharp
using Vorticity;

[VortexRecord]
public partial record struct Reading(int Day, double? Celsius, string City);
```

Writing records out, and completing the file:

```csharp
await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>("readings.vortex"))
{
    await writer.WriteAsync<Reading>(readings);          // Reading[]: copied into the builder, column by column
    await writer.CompleteAsync();                        // disposed without it, the file is deleted
}
```

Reading columns back. Each batch is borrowed: its columns are `ref struct`s, valid until the next
batch, and the loop allocates next to nothing per batch.

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

The lambda given to `Where` runs once, when the scan is built, and what it records is the plan: the
file statistics, the zone maps and the indexes skip what they can before a block is decoded.
`r.Day >= "900"` and `r.Day % 2 == 0` do not compile.

The same question, and a few more, without a loop: answered from the statistics when they settle
it, from the encoded blocks otherwise, and never by materialising a batch.

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

A group by is a query too, and its result a scan of a record, read as batches like a file's:

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

Rows, when rows are what you need, are a sink you ask for and pay for: `ToRecordsAsync()` yields
one `Reading` per row, and LINQ starts after it. A file whose schema is not known when the program
is compiled is read by name, `file.Scan("Day", "City").Where($"Day >= {min}")`, each hole of the
filter typed, which is the surface `vxdump` is written on.

## Requirements

The library targets `net11.0`, and `global.json` pins the SDK to a **.NET 11 release candidate**
with `allowPrerelease`. Until .NET 11 reaches general availability you need that SDK installed —
an otherwise current machine on .NET 10 will be refused by `global.json` before anything builds.
That SDK is all `dotnet build` and `dotnet test` need. Everything that compares against the Rust
reference — the cross-check, the ratio axes of the bench, and the corpus generator — wants a Rust
toolchain on top.

The library ships on nuget.org, for a project that targets `net11.0`; the generator is a
build-time dependency, which `PrivateAssets` keeps out of whatever your project packs:

```xml
<PackageReference Include="Vorticity" Version="0.1.0" />
<PackageReference Include="Vorticity.Generators" Version="0.1.0" PrivateAssets="all" />
```

`Vorticity.Dataset` and `Vorticity.RowEncoding`, both experimental, are referenced the same way,
at the same version as the core.

## Documentation

| Document | Contents |
|---|---|
| [docs/guide/](docs/guide/README.md) | **Using the library**: pages named by what you are trying to do, each one a program the samples project compiles and runs |
| [docs/guide/benchmarks.md](docs/guide/benchmarks.md) | Every figure the bench publishes: what this costs against the Rust implementation, and each kernel against its baseline |
| [docs/guide/benchmarks-x64.md](docs/guide/benchmarks-x64.md) | The same page from an x64 machine, a Zen 4 processor under Windows, against a Rust built there |
| [docs/guide/benchmarks-duckdb.md](docs/guide/benchmarks-duckdb.md) | Our group bys against DuckDB's on the same files, through its Vortex reader and from its own table, query by query, on one thread and on fourteen |
| [docs/design/](docs/design/README.md) | **Why it is shaped this way**: the design documents, from the scope to the byte layout to the public API |

## How performance is measured

Vorticity is measured against Vortex's Rust implementation on the same files and the same
machine, built with Native AOT, on one core and on all of them: a table read and written, every
encoding decoded, taken from and written, and each hot loop against its baseline. Both sides
do the same work: each maps the file and reads it where it lies, decodes every value it returns,
and hands what it writes to a sink that keeps nothing; on one core, Rust is timed under the faster
of its two ways of splitting a scan. The speedup is Rust's time over Vorticity's: above 1.00x,
Vorticity is faster. The table above is read from the file Vorticity writes.

Per encoding, Vorticity is well ahead on nested, text and compressed integer columns (`struct`
6.92x, `varbin` 6.08x, `map` 3.28x, `pco` 3.06x), and level or behind on 10 files by a tenth at
most: plain booleans (0.90x), `onpair` (0.95x), `decimal` (0.96x), masked columns (0.96x to
1.00x), patched bit-packed integers (0.98x), `alprd` and `sequence` (1.00x). Its file is no more
than 5 % larger than Rust's, or smaller, on 44 of 56. Both readers warmed up in one process, Rust
is faster on 1 of 19 cases: a prefix filter on FSST strings (0.93x).

Every figure is on [the benchmark page](docs/guide/benchmarks.md), each section with the machine
and the commit it was measured on, and [its x64 twin](docs/guide/benchmarks-x64.md) holds the
same sections measured on x64. [docs/design/05-benchmarks.md](docs/design/05-benchmarks.md) says what is
compared and how, and [bench/README.md](bench/README.md) how to run each instrument: `bench/gate.sh`
runs everything that gates a commit.
