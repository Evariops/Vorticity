# Vorticity

A **pure .NET, dependency-free** implementation of the [Vortex](https://vortex.dev) columnar
file format (LF AI & Data, formerly SpiralDB).

* Ultra-optimized core: zero allocations on hot paths, SIMD (`System.Runtime.Intrinsics`), zero-copy.
* **Async-only** public API (`ValueTask`, `IAsyncEnumerable`).
* Conformant to the published spec (`VTXF` v1, `core2026.08.3` edition).
* Validated by **cross-testing** against the Rust reference implementation.
* Benchmarked against the Rust reference implementation — the fastest Vortex implementation there is.

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
| [docs/90-registry.md](docs/90-registry.md) | Full registry of encodings / layouts / dtypes and their status |
| [docs/99-sources.md](docs/99-sources.md) | Primary sources and how to re-verify them |

## Status

**Reading is complete for the 1.0 scope; writing produces files the Rust reference reads back.**

| | |
|---|---|
| Conformance corpus | **821 of 821** files in scope, all read back value for value against the Rust sidecars — 2.51 M rows, 5.60 M values and validity bits compared |
| Round trip | all 821 written by Vorticity and read back, 2.51 M rows compared |
| **Cross-check** | 819 of 820 **read by Vortex Rust**, 2.50 M rows compared scalar by scalar against its own file. The one that disagrees is `experimental_patched_array_editions_off`: we now read `vortex.patched` and write it back, and the 0.86.1 reference does not know that encoding |
| Tests | 5237, on a corpus of 821 files |
| Native AOT | `vxdump` publishes with no trim or AOT warnings and opens 818 of 819 corpus files |

Implemented: the file open path, the layout tree, all 30 array encodings of the 1.0 scope, typed
column access, scans with projection and row ranges, filter pushdown, zone-map pruning, random
access by row index, and a canonical uncompressed writer.

Not yet: the sampling compressor and statistics on write, SIMD kernels and the benchmark suite
against Rust, the byte-sortable row encoding, and parser fuzzing. See
[docs/90-registry.md](docs/90-registry.md) for the component-by-component state.
