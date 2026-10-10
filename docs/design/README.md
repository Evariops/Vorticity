# The design documents

Why the format and this implementation are shaped the way they are. If you want to use the library,
start with [the guide](../guide/README.md). These documents are the depth behind it, and the code is
the reference for every name they mention.

The numbers give a reading order: 01 says what is in scope, 02 what the bytes are, and the rest
build on those two.

| document | what it covers |
|---|---|
| [01-scope.md](01-scope.md) | what Vorticity reads, writes and answers, what it leaves out, and the guarantees it holds |
| [02-format.md](02-format.md) | the Vortex file format, condensed: the tail, the footer, dtypes, arrays, layouts |
| [03-architecture.md](03-architecture.md) | the engine: constraints, arenas, the I/O interface, performance invariants, parser safety |
| [04-conformance.md](04-conformance.md) | how correctness is anchored in the Rust reference: the corpus, the cross-check, fuzzing |
| [05-benchmarks.md](05-benchmarks.md) | how performance is measured against the Rust implementation (the figures are on [the benchmark page](../guide/benchmarks.md)) |
| [06-row-encoding.md](06-row-encoding.md) | the byte-sortable row encoding |
| [07-dotnet-mapping.md](07-dotnet-mapping.md) | which .NET type each dtype becomes, and where a naive mapping loses data |
| [08-semantics.md](08-semantics.md) | pruning, predicates, NaN and nulls, unknown components, untrusted hints, resource caps |
| [09-contracts.md](09-contracts.md) | thread safety, parallelism, versioning, threat model, observability, licensing |
| [10-indexes.md](10-indexes.md) | the skipping and locating indexes: kinds, bytes, policy, and Rust readability |
| [11-write-strategy.md](11-write-strategy.md) | the writer's single pass per block, how it chooses encodings, appends, and the encoding advisor |
| [12-index-reads.md](12-index-reads.md) | the key cursor, answers without rows, and rows in key order |
| [13-dataset.md](13-dataset.md) | the versioned dataset over an object store |
| [14-public-api.md](14-public-api.md) | the rules the public API follows, and where each part lives |
| [15-compaction.md](15-compaction.md) | compaction inline, on demand or in the background, planned from the top of the tree, and on a store that cannot delete |
| [16-queries.md](16-queries.md) | projections, group by and aggregates in the shape of LINQ, run as a flow of batches: the first answer as soon as it is final, memory only for what is open, sums identical whatever the split, results that are scans themselves |
| [17-object-storage.md](17-object-storage.md) | a design to build: files and datasets read and written straight on Amazon S3, Cloudflare R2, OVHcloud, Scaleway, Azure Blob Storage and Google Cloud Storage, with clients written here over the platform's HTTP client, the same API, and every operation's requests counted |
| [90-registry.md](90-registry.md) | every encoding, layout and dtype: its edition, whether it is read and written, how a take is served |
| [99-sources.md](99-sources.md) | the primary sources, and how to verify them again |

The upstream material these documents derive from is vendored in [spec/](../../spec/), which records
what was taken and from which revision.
