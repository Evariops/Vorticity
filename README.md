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

Initial specification. No code written yet.
