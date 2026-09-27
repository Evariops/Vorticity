# Vorticity

A **pure .NET, dependency-free** implementation of the [Vortex](https://vortex.dev) columnar
file format (LF AI & Data, formerly SpiralDB).

* **A typed surface.** A `[VortexRecord]` declares the columns; filters and aggregates are C#
  lambdas the scan pushes down to the file, and what cannot be pushed down does not compile.
* Ultra-optimized core: zero allocations per batch on hot paths, SIMD (`System.Runtime.Intrinsics`),
  zero-copy from the file to the span you read.
* **Async-only** public API: every read and every write is awaited (`ValueTask`, `await foreach`),
  whatever the source; there is no synchronous path to choose.
* Conformant to the published spec (`VTXF` v1, `core2026.08.3` edition).
* Validated by **cross-testing** against the Rust reference implementation.
* Benchmarked against the Rust reference implementation, process against process.

No third-party package: the base class library, plus `System.IO.Hashing`, first party, for the
hashes of the write path and the Bloom filters.

**What parity means here.** Files written by this library are read by Vortex Rust, and every value
in them reads back equal — that is the parity claimed and the cross-check is what proves it. It is
not byte parity: the same data is encoded differently on the two sides by design, and a file from
one is not expected to match the other byte for byte.

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
batch, and the loop allocates nothing per batch.

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

Nothing is published yet. Until it is, reference the projects, as
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
| [CHANGELOG.md](CHANGELOG.md) | What the first release will contain, and what changed on the way |

## Status

**Reading is complete for the 1.0 scope; writing produces files the Rust reference reads back.**

Nothing here has been published yet, so nothing carries a version number. The first release will be
a `0.1.0` cut by CI.

| package | what it is | state |
|---|---|---|
| `Vorticity` | the format and its public surface: the session, files, the typed scan and its columns, aggregates, the key cursor, the tool path, the writer and its builders, the I/O seam | the 1.0 scope, complete for reading |
| `Vorticity.Generators` | the `[VortexRecord]` source generator and the analyzers VX1001 to VX1008 | build-time only; a record can also be written by hand, since its interface is in the core |
| `Vorticity.Dataset` | a versioned dataset over an object store: commit objects, a prolly tree, the store seam an S3 library implements | experimental, `[Experimental("VX0001")]`, and the format is this repository's own |
| `Vorticity.RowEncoding` | the byte-sortable row encoding | experimental, `[Experimental("VX0002")]`: upstream reserves the right to change the layout between releases |
| `vxdump` | the inspection tool: schema, layout, encodings, segments, statistics, indexes | written against the public surface only, published ahead of time |

What checks it, each one a command that re-runs on a clone:

| check | what it compares | command |
|---|---|---|
| Tests | the unit and property tests, and the conformance corpus: 856 committed files read back value for value against the Rust sidecars | `dotnet test Vorticity.slnx -c Release` |
| **Cross-check** | the corpus written by Vorticity and **read by Vortex Rust**, compared scalar by scalar against the reference's own file | `bench/crosscheck.sh` |
| Throughput | each encoding's decoder against the Rust reference, both in one process, on one clock | `dotnet run -c Release --project bench/Vorticity.Benchmarks -- --throughput --check` |
| Native AOT | `vxdump` publishes with no trim or AOT warning and reads the corpus | `dotnet publish tools/vxdump -c Release -r <rid>` |

Implemented: the file open path, the layout tree, every array encoding of the 1.0 scope, typed
column access through records, projection by record, filter pushdown from lambdas, zone-map
pruning, rows by index, aggregates and group by on the encoded form, owned batches, exact block
statistics on write, the fused single-pass writer, append and torn-tail recovery, skipping and
locating indexes, and the key cursor. Parser fuzzing runs in CI on every pull request. See
[docs/design/90-registry.md](docs/design/90-registry.md) for the component-by-component state.

## Performance

Vorticity is measured against Vortex's Rust implementation on the same files and the same
machine, built with Native AOT, on one core and on all of them: a table read and written, every
encoding decoded, taken from and written, and each hot loop against the one it replaced. A scan
allocates nothing per batch. Every figure is on one page, [the benchmark page](docs/guide/benchmarks.md),
each section with the machine and the commit it was measured on, and again from an x64 machine on
[its twin](docs/guide/benchmarks-x64.md);
[docs/design/05-benchmarks.md](docs/design/05-benchmarks.md) says what is compared and how, and
[bench/README.md](bench/README.md) how to run each instrument: `bench/gate.sh` runs everything that
gates a commit.
