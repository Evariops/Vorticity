# Vorticity

A **pure .NET, dependency-free** implementation of the [Vortex](https://vortex.dev) columnar
file format (LF AI & Data, formerly SpiralDB).

* **A typed surface.** A `[VortexRecord]` declares the columns; filters and aggregates are C#
  lambdas the scan pushes down to the file, and what cannot be pushed down does not compile.
* Ultra-optimized core: near-zero allocations per batch on hot paths, SIMD (`System.Runtime.Intrinsics`),
  no copy before decoding: a mapped file is decoded where it lies, and an uncompressed column is a
  view of its bytes.
* **Async-only** public API: every read and every write is awaited (`ValueTask`, `await foreach`),
  whatever the source; there is no synchronous path to choose.
* Conformant to the published spec (`VTXF` v1, `core2026.08.3` edition).
* Validated by **cross-testing** against the Rust reference implementation.
* **Faster than the Rust reference implementation on most of what is measured**, process against
  process on the same machine, both sides doing the same work: a speedup of 1.15x to 2.10x on one
  core and 1.42x to 2.81x on all cores on a whole table, slower on a take of scattered rows on one
  core; per encoding, a median of 1.16x when decoding and 3.64x when writing
  ([Performance](#performance)).

No third-party package: the base class library, plus `System.IO.Hashing`, first party, for the
hashes of the write path and the Bloom filters.

**What parity means here.** Files written by this library are read by Vortex Rust, and every value
in them reads back equal — that is the parity claimed and the cross-check is what proves it. It is
not byte parity: the same data is encoded differently on the two sides by design, and a file from
one is not expected to match the other byte for byte.

**Status.** This project was developed with the help of LLMs. Until v1.0, it may be subject to
breaking changes.

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

double? mean = await file.Scan<Reading>().AvgAsync(r => r.Celsius);

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
| [docs/guide/benchmarks.md](docs/guide/benchmarks.md) | Every figure the bench publishes: what this costs against the Rust implementation, and each kernel against the loop it replaced |
| [docs/guide/benchmarks-x64.md](docs/guide/benchmarks-x64.md) | The same sections from an x64 machine, a Zen 4 processor under Windows; its comparisons with Rust are withdrawn until measured again |
| [docs/design/](docs/design/README.md) | **Why it is shaped this way**: the design documents, from the scope to the byte layout to the public API |

## Performance

Vorticity is measured against Vortex's Rust implementation on the same files and the same
machine, built with Native AOT, on one core and on all of them: a table read and written, every
encoding decoded, taken from and written, and each hot loop against the one it replaced. Both sides
do the same work: each maps the file and reads it where it lies, decodes every value it returns,
and hands what it writes to a sink that keeps nothing; on one core, Rust is timed under the faster
of its two ways of splitting a scan. The speedup is Rust's time over Vorticity's: above 1.00x,
Vorticity is faster.

A table of four columns and 1,048,576 rows, both sides reading the file Vorticity wrote, on an
Apple M4 Pro:

| scenario | speedup, one core | speedup, all 14 cores |
|---|---:|---:|
| read every column | 1.17x | 1.73x |
| read one column of four | 2.10x | 2.06x |
| filter, 1 % of the rows | 1.15x | 2.81x |
| filter, half the rows | 1.32x | 1.73x |
| take 1,000 scattered rows | 0.88x | 1.42x |
| write the table back out | 1.97x | 1.43x |

Per encoding, on one core, Vorticity decodes 40 of 57 files faster than Rust, a median speedup of
1.16x: well ahead on text, nested and compressed integer columns (`struct` 6.22x, `varbin` 5.18x,
`pco` 2.90x, `map` 2.42x), behind on run-end (0.76x) and on a column chunked inside one array
(0.38x). It takes rows faster from 23 of 57 (median 0.89x), and writes 56 of 56 faster (median
3.64x), its file no more than 5 % larger than Rust's, or smaller, on 44 of them. Both readers warmed
up in one process, Rust is faster on 2 of 19 cases: a prefix filter on FSST strings (0.87x) and a
band over bit-packed integers (0.96x).

**Corrected on 2026-10-02.** The figures published before that date came from a harness that
charged Rust for work Vorticity did not do, and favoured Vorticity: a median speedup of about 3x
per encoding when decoding and 4.5x when taking rows, where the same files now give 1.16x and 0.89x.
[The benchmark page](docs/guide/benchmarks.md) says what changed.

A scan allocates next to nothing per batch. Every figure is on one page, [the benchmark page](docs/guide/benchmarks.md),
each section with the machine and the commit it was measured on; [its x64
twin](docs/guide/benchmarks-x64.md) awaits an x64 machine to measure its comparisons again.
[docs/design/05-benchmarks.md](docs/design/05-benchmarks.md) says what is compared and how, and
[bench/README.md](bench/README.md) how to run each instrument: `bench/gate.sh` runs everything that
gates a commit.
