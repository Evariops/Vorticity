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
* **Notably faster than the Rust reference implementation**, process against process on the same
  machine: about 1.2x to 3x on a whole table, and a median of about 3x to 5x per encoding
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

The library is consumed from source: reference the projects, as
[the samples](samples/Vorticity.Samples/Vorticity.Samples.csproj) do; the generator is an
analyzer reference:

```xml
<ProjectReference Include="src/Vorticity/Vorticity.csproj" />
<ProjectReference Include="src/Vorticity.Generators/Vorticity.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

## Documentation

| Document | Contents |
|---|---|
| [docs/guide/](docs/guide/README.md) | **Using the library**: pages named by what you are trying to do, each one a program the samples project compiles and runs |
| [docs/guide/benchmarks.md](docs/guide/benchmarks.md) | Every figure the bench publishes: what this costs against the Rust implementation, and each kernel against the loop it replaced |
| [docs/guide/benchmarks-x64.md](docs/guide/benchmarks-x64.md) | The same figures from an x64 machine, a Zen 4 processor under Windows |
| [docs/design/](docs/design/README.md) | **Why it is shaped this way**: the design documents, from the scope to the byte layout to the public API |

## Performance

Vorticity is measured against Vortex's Rust implementation on the same files and the same
machine, built with Native AOT, on one core and on all of them: a table read and written, every
encoding decoded, taken from and written, and each hot loop against the one it replaced. The
speedup is Rust's time over Vorticity's: above 1.0x, Vorticity is faster.

A table of four columns and 1,048,576 rows, both sides reading the file Vorticity wrote, on an
Apple M4 Pro:

| scenario | speedup, one core | speedup, all 14 cores |
|---|---:|---:|
| read every column | 1.2x | 2.2x |
| read one column of four | 2.9x | 2.7x |
| filter, 1 % of the rows | 1.3x | 3.2x |
| filter, half the rows | 1.4x | 2.2x |
| take 1,000 scattered rows | 0.98x | 1.4x |
| write the table back out | 2.1x | 1.6x |

Per encoding, on one core, Vorticity decodes 56 of 57 files faster than Rust (median speedup 3.2x;
run-end is the exception, at 0.72x), takes rows faster from 57 of 57 (median 4.6x) and writes 56 of
56 faster (median 4.6x). Both readers warmed up in one process, Rust is faster on 2 of 19 cases: key
order over an uncorrelated column (0.91x) and a prefix filter on FSST strings (0.94x).

A scan allocates next to nothing per batch. Every figure is on one page, [the benchmark page](docs/guide/benchmarks.md),
each section with the machine and the commit it was measured on, and again from an x64 machine on
[its twin](docs/guide/benchmarks-x64.md);
[docs/design/05-benchmarks.md](docs/design/05-benchmarks.md) says what is compared and how, and
[bench/README.md](bench/README.md) how to run each instrument: `bench/gate.sh` runs everything that
gates a commit.
