# The design documents

Why the format and this implementation are shaped as they are, written for someone implementing
either. If you are *using* the library, [the guide](../guide/README.md) is the other directory and
the one to start from.

The numbers are the reading order, not a filing scheme: 01 says what is in scope, 02 says what the
bytes are, and the rest build on those two.

| Document | Contents |
|---|---|
| [01-scope.md](01-scope.md) | Functional scope, phasing, explicit non-goals |
| [02-format.md](02-format.md) | Condensed binary spec: what to implement, byte by byte |
| [03-architecture.md](03-architecture.md) | .NET architecture, public API, performance invariants |
| [04-conformance.md](04-conformance.md) | Test strategy (xunit v3) and Rust cross-testing |
| [05-benchmarks.md](05-benchmarks.md) | Benchmark protocol against the Rust implementation |
| [06-row-encoding.md](06-row-encoding.md) | Byte-sortable row encoding: full condensed spec |
| [07-dotnet-mapping.md](07-dotnet-mapping.md) | DType → .NET type contract, and where naive mappings lose data |
| [08-semantics.md](08-semantics.md) | Pruning algebra, predicate semantics, unknown components, untrusted hints, resource caps |
| [09-contracts.md](09-contracts.md) | Thread-safety, parallelism, versioning, threat model, observability, licensing |
| [10-indexes.md](10-indexes.md) | Skipping and locating indexes (Bloom, postings, sorted runs), out of tree, append-friendly runs, Rust-readable |
| [11-write-strategy.md](11-write-strategy.md) | Write strategy: the fused block pipeline — `ColumnWriter`, exact block statistics, formula verdicts, one-pass encoding, SIMD kernel by kernel, the read contract, append, no sampling |
| [12-index-reads.md](12-index-reads.md) | Index reads: the key cursor (seek / next / prev, rank, distinct), probes without rows (`Any`, `Count`, `Min`, `Max`), key-ordered delivery, `StartsWith` / `Contains` / `Like`, and the consumer surface — no change to a byte on disk |
| [13-dataset.md](13-dataset.md) | The versioned dataset over an object store: commit objects, the prolly tree, compaction and vacuum |
| [14-public-api.md](14-public-api.md) | Every public type a caller can name, the shape of the calls and the cost of each shape: the surface, the symbolic scan, the read and write cases |
| [90-registry.md](90-registry.md) | Full registry of encodings / layouts / dtypes and their status |
| [99-sources.md](99-sources.md) | Primary sources and how to re-verify them |

The upstream material these are derived from is vendored in [spec/](../../spec/), which records what
was taken and from which revision.
