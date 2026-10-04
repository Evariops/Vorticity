# Vorticity

A **pure .NET, dependency-free** implementation of the [Vortex](https://vortex.dev) columnar
file format (LF AI & Data).

## Performance

**Faster than Vortex's Rust implementation on most of what is measured**, process against process
on the same machine, both sides doing the same work. A table of four columns and 1,048,576 rows,
Native AOT on an Apple M4 Pro, Rust's time over Vorticity's:

| scenario | one core | all 14 cores |
|---|---:|---:|
| read every column | 1.98x | 2.31x |
| read one column of four | 2.23x | 2.24x |
| filter, 1 % of the rows | 1.35x | 2.89x |
| filter, half the rows | 2.05x | 2.31x |
| take 1,000 scattered rows | 1.59x | 1.17x |
| write the table back out | 2.39x | 1.37x |

Per encoding, on one core: decoding faster on 50 of 57 files (median 1.27x), taking rows faster
from all 57 (median 1.14x), writing faster on all 56 (median 3.82x). A scan allocates next to
nothing per batch. [How it is measured](#how-performance-is-measured), and every figure on [the
benchmark page](docs/guide/benchmarks.md).

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

(double? min, double? max, long cities) = await file.Scan<Reading>()
    .Where(r => r.Day >= 900)
    .AggAsync(a => (a.Min(r => r.Celsius), a.Max(r => r.Celsius), a.CountDistinct(r => r.City)));
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
| [docs/guide/benchmarks-x64.md](docs/guide/benchmarks-x64.md) | The sections that compare Vorticity with itself, from an x64 machine, a Zen 4 processor under Windows |
| [docs/design/](docs/design/README.md) | **Why it is shaped this way**: the design documents, from the scope to the byte layout to the public API |

## How performance is measured

Vorticity is measured against Vortex's Rust implementation on the same files and the same
machine, built with Native AOT, on one core and on all of them: a table read and written, every
encoding decoded, taken from and written, and each hot loop against its baseline. Both sides
do the same work: each maps the file and reads it where it lies, decodes every value it returns,
and hands what it writes to a sink that keeps nothing; on one core, Rust is timed under the faster
of its two ways of splitting a scan. The speedup is Rust's time over Vorticity's: above 1.00x,
Vorticity is faster. The table above is read from the file Vorticity writes.

Per encoding, Vorticity is well ahead on text, nested and compressed integer columns (`varbin`
5.70x, `struct` 5.10x, `map` 3.11x, `pco` 2.96x), and level or behind on 7 files by a fifth at
most: booleans that start at a bit offset (0.80x and 0.99x), masked columns (0.93x to 0.99x),
`onpair` (0.97x) and `decimal` (1.00x). Its file is no more than 5 % larger than Rust's, or
smaller, on 45 of 56. Both readers warmed up in one process, Rust is faster on 2 of 19 cases: a
prefix filter on FSST strings (0.93x) and a band over bit-packed integers (0.98x).

Every figure is on [the benchmark page](docs/guide/benchmarks.md), each section with the machine
and the commit it was measured on, and [its x64 twin](docs/guide/benchmarks-x64.md) holds the
sections measured on x64. [docs/design/05-benchmarks.md](docs/design/05-benchmarks.md) says what is
compared and how, and [bench/README.md](bench/README.md) how to run each instrument: `bench/gate.sh`
runs everything that gates a commit.
