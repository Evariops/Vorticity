# Vorticity

A **pure .NET, dependency-free** implementation of the [Vortex](https://vortex.dev) columnar
file format (LF AI & Data, formerly SpiralDB).

* Ultra-optimized core: zero allocations on hot paths, SIMD (`System.Runtime.Intrinsics`), zero-copy.
* **Async-only** public API (`ValueTask`, `IAsyncEnumerable`).
* Conformant to the published spec (`VTXF` v1, `core2026.08.3` edition).
* Validated by **cross-testing** against the Rust reference implementation.
* Benchmarked against the Rust reference implementation — the fastest Vortex implementation there is.

**What parity means here.** Files written by this library are read by Vortex Rust, and every value
in them reads back equal — that is the parity claimed and the cross-check is what proves it. It is
not byte parity: the same data is encoded differently on the two sides by design, and a file from
one is not expected to match the other byte for byte.

## Documentation

| Document | Contents |
|---|---|
| [docs/01-scope.md](docs/01-scope.md) | Functional scope, phasing, explicit non-goals |
| [docs/02-format.md](docs/02-format.md) | Condensed binary spec: what to implement, byte by byte |
| [docs/03-architecture.md](docs/03-architecture.md) | .NET architecture, public API, performance invariants |
| [docs/04-conformance.md](docs/04-conformance.md) | Test strategy (xunit v3) and Rust cross-testing |
| [docs/05-benchmarks.md](docs/05-benchmarks.md) | Benchmark protocol against the Rust implementation |
| [docs/06-row-encoding.md](docs/06-row-encoding.md) | Byte-sortable row encoding: full condensed spec |
| [docs/07-dotnet-mapping.md](docs/07-dotnet-mapping.md) | DType → .NET type contract, and where naive mappings lose data |
| [docs/08-semantics.md](docs/08-semantics.md) | Pruning algebra, predicate semantics, unknown components, untrusted hints, resource caps |
| [docs/09-contracts.md](docs/09-contracts.md) | Thread-safety, parallelism, versioning, threat model, observability, licensing |
| [docs/10-indexes.md](docs/10-indexes.md) | Skipping and locating indexes (Bloom, postings, sorted runs), out of tree, append-friendly runs, Rust-readable |
| [docs/11-write-strategy.md](docs/11-write-strategy.md) | Write strategy: the fused block pipeline — `ColumnWriter`, exact block statistics, formula verdicts, one-pass encoding, SIMD kernel by kernel, the read contract, append, no sampling |
| [docs/12-index-reads.md](docs/12-index-reads.md) | Index reads: the key cursor (seek / next / prev, rank, distinct), probes without rows (`Any`, `Count`, `Min`, `Max`), key-ordered delivery, `StartsWith` / `Contains` / `Like`, and the consumer surface — no change to a byte on disk |
| [docs/90-registry.md](docs/90-registry.md) | Full registry of encodings / layouts / dtypes and their status |
| [docs/99-sources.md](docs/99-sources.md) | Primary sources and how to re-verify them |

## Status

**Reading is complete for the 1.0 scope; writing produces files the Rust reference reads back.**

Nothing here has been published yet, so nothing carries a version number. The first release will be
a `0.1.0` cut by CI.

| assembly | what it is | state |
|---|---|---|
| `Vorticity` | the format: open, layout tree, decoders, scan, filter, indexes, writer | the 1.0 scope, complete for reading |
| `Vorticity.Dataset` | a versioned dataset over an object store: commit objects, a prolly tree, the store seam an S3 library implements | experimental, and the format is this repository's own |
| `Vorticity.RowEncoding` | the byte-sortable row encoding | experimental: upstream reserves the right to change the layout between releases |

Measured on 2026-09-20; every command is one line and re-runs on a clone.

| | |
|---|---|
| Tests | **6 636** passing, 1 skipped, over the two test projects — `dotnet test -c Release` |
| Conformance corpus | **856 of 856** in-scope files read back value for value against the Rust sidecars: 2 547 199 rows and 5 720 491 values and validity bits compared — the conformance project, in the same `dotnet test` |
| **Cross-check** | **854** files written by Vorticity and **read by Vortex Rust**, 2 538 751 rows compared scalar by scalar against the reference's own file, and no file disagrees — `bench/crosscheck.sh`. Two corpus files are out, and both times it is the reference that cannot read: `types/no_dtype_segment` has no schema for the comparison example to open with, and `experimental_patched_array_editions_off` carries a `vortex.patched` belonging to no pinned edition |
| Throughput | **47 of 57** scan axes faster than the Rust reference — `dotnet run -c Release --project bench/Vorticity.Benchmarks -- --throughput --check` |
| Native AOT | `vxdump` publishes with no trim or AOT warnings and reads the corpus |

Implemented: the file open path, the layout tree, every array encoding of the 1.0 scope, typed
column access, scans with projection and row ranges, filter pushdown, zone-map pruning, random
access by row index, exact block statistics on write, the fused single-pass writer, skipping and
locating indexes, and the key cursor. Parser fuzzing runs in CI on every pull request. See
[docs/90-registry.md](docs/90-registry.md) for the component-by-component state.

## Performance

[bench/README.md](bench/README.md) is the one page on what to run, what it costs and what each
number means. Three commands cover most of it: `-- --throughput --check` for the scan against Rust,
`-- --throughput --write --check` for the writer, and `bench/gate.sh` for everything that gates a
commit.
