# Library contracts: concurrency, parallelism, versioning, threat model, observability, licensing

The promises made to a caller, as opposed to the promises made about bytes, written once so that
each is not deduced differently at each call site.

## 1. Concurrency and thread safety

| type | contract |
|---|---|
| `VortexSession` | **Thread-safe**, meant to be shared by every request of a process; its options are frozen when `Create` returns, and `VortexSession.Default` is immutable. Disposing it while a file it opened is still open throws `InvalidOperationException` naming the file |
| `VortexFile` | **Thread-safe.** Concurrent scans of one open file are expected; its footer, layout tree, index directory and the runs it caches are immutable or published once |
| `ISegmentSource` | implementations **must** be thread-safe; the built-in ones are |
| `Scan<TRecord>`, `Scan` | **Single use, one thread.** Composition returns the same builder; build it on one thread, then run one sink. A second sink throws `InvalidOperationException`; `ExplainAsync` may be asked before the sink |
| the scan's enumerator | one consumer, as the language requires |
| `Columns<TRecord>`, `Column<T>`, `BatchView` | **Bound to the loop body**, enforced by the compiler: `ref struct`s valid until the next `MoveNextAsync` or `DisposeAsync`, which cannot be stored, captured or carried across an `await`. The one escape the compiler does not see, a span copied into a variable declared outside the loop, is analyzer VX1001's |
| `RecordBatch` | **Owned by its consumer**, not thread-safe: it may be handed to another thread, through a channel for instance, and disposed there; its spans die with it |
| `KeyCursorBuilder<TKey>`, `KeyCursor<TKey>` | not thread-safe, like the scan |
| `ColumnsBuilder`, `ColumnBuilder<T>` | **One thread.** The builder a writer hands out is its own, reused after every `WriteAsync` |
| `VortexFileWriter` (an append included), `VortexFileIndexer` | one writer per file; an append is not atomic, and `VortexFileRepair` truncates a torn one |
| `ScanPlan`, `CountPlan`, `OrderPlan`, `KeyPlan`, `ScanStatistics`, `WriteReport` | immutable records |
| decoders and kernels | pure functions over borrowed memory, no shared mutable state |

## 2. Parallelism

A ratio against Rust means something only at equal threading, and a library must not take a host's
thread pool without consent: a server running 200 concurrent requests does not want each scan
fanning out. So:

- **Sequential by default.** `VortexSessionOptions.MaxDegreeOfParallelism` is 1 unless set, and
  `VortexSession.Default` is immutable, so a caller who configures nothing gets one thread.
- **Consent is given on the session, once**: its degree is where every scan and every writer of the
  session starts, for a host that decides its threading at start-up rather than at a hundred call
  sites. Two sessions in one process keep two degrees.
- **The call may say otherwise**: `ScanOptions.DegreeOfParallelism` for one scan and
  `VortexWriteOptions.DegreeOfParallelism` for one file, 0 taking the session's.
- **A scan** decodes its splits side by side, each on a context and arenas of its own, nothing
  shared, and still delivers its batches in row order; an aggregate keeps a state per chunk and
  merges them at the end. **A writer** summarizes its columns and compresses a column's zstd frames
  on its threads, and chooses and writes the encodings on the calling one; the file is the same
  bytes at any degree.
- **I/O concurrency is separate and always on**: the batch read of the seam issues overlapping reads
  whatever the degree, because latency hiding is the point on an object store. It is bounded per
  session by `MaxConcurrentReads`, 16 by default, across every scan of every file. A mapped file and
  bytes in memory have no reads to bound.

[threads.md](../guide/threads.md) shows what each costs and when parallelism pays.

## 3. Versioning

Semantic versioning, with each level spelled out, since a format library's version has two axes:
its API and the format it understands.

| change | level |
|---|---|
| reading a newly frozen upstream edition (more ids) | minor |
| an encoder for an encoding already decoded | minor |
| a case that threw `VortexFormatException` now throwing `VortexUnsupportedException` | minor |
| dropping or adding a target framework | **major** |
| changing the lifetime of borrowed spans | **major** |
| changing the default write edition | **major**: it changes who can read the output |
| the row encoding's bytes following upstream | not applicable, below |

The default write edition is `VortexEditions.Default`, and it is not the newest by definition: it is
the edition the most deployed Rust reader accepts, chosen per release and named in the release
notes beside the minimum Rust version that reads it (`VortexEditions.MinimumRustVersion`). Today
it is `core2026.08.3`, read from Vortex Rust 0.85.0: the first edition with `vortex.uuid`, without
which a `Guid` column cannot be written.

**The row encoding ships apart**, in `Vorticity.RowEncoding`, versioned `0.x` and marked
experimental: upstream reserves the right to change its bytes between releases, and binding it into
a stable core would force a choice between breaking this library's semver and diverging from Rust,
which would lose the byte-exactness that is the feature's point.

Each release records the Vortex version its corpus was generated with and the highest edition it
reads: that pair, more than the version number, is what a user needs.

## 4. Threat model

Two sentences, because the alternative is an unbounded promise:

1. **Malformed input** — bad offsets, truncation, out-of-range fields memory depends on, exceeded
   caps — produces a clean `VortexFormatException` or `VortexUnsupportedException`: no out-of-bounds
   access, no unbounded allocation, no hang. Fuzzing enforces it.
2. **Well-formed but lying input** — valid structure over false statistics — may produce wrong
   results, as in every format with embedded statistics, Parquet included.
   `VortexOpenOptions.VerifyStatistics` trades throughput for validation when the producer is not
   trusted ([08-semantics.md](08-semantics.md) §5). An index that lies costs pruning, never rows:
   its regions carry checksums, and a failed one claims nothing
   ([10-indexes.md](10-indexes.md) §3).

Not claimed: protection against resource exhaustion below the configured caps, or any
confidentiality property (the format's encryption slot is an empty reserved table).

## 5. Observability

Three channels, each free when nobody listens.

**A meter and an activity source**, both named `Vorticity` (`VortexDiagnostics`), which
OpenTelemetry and `dotnet-counters` read without an adapter. Each instrument is created on its own,
so a listener enabling one pays for that one:

| instrument | diagnoses |
|---|---|
| `vortex.scan.rows` | rows delivered |
| `vortex.scan.requests`, `vortex.scan.bytes_requested` | what the scans asked their sources for: read amplification, against the plan's `BytesToRead` |
| `vortex.scan.blocks_decoded`, `vortex.scan.blocks_pruned` | whether pruning works at all |
| `vortex.cache.hits`, `vortex.cache.misses` | whether the session's segment cache does anything |
| `vortex.write.bytes` | bytes the writers handed their sinks |

A scan is one activity, `vortex.scan.typed` or `vortex.scan.tool`, tagged when it ends with its
rows, batches, requests, bytes requested and blocks decoded and pruned; a write is one activity,
`vortex.write`, tagged with its rows, bytes and whether it completed, and marked as an error when it
is abandoned.

**An event source**, `Vorticity`, whose polling counters say whether an index earns its bytes:
`segments-requested`, `bytes-requested`, `zones-pruned`, `zones-total`, `index-runs-read`,
`cursor-seeks`, `cursor-steps`, `count-blocks-proven`, `count-blocks-decoded`, `key-order-windows`
and `key-order-window-splits`.

**Per query**: a scan's `ExplainAsync` is its plan before it runs, and its `Statistics` the same
quantities measured after ([observability.md](../guide/observability.md)).

## 6. Licensing and attribution

Vorticity is licensed under Apache-2.0 (`LICENSE`, the canonical text).

Upstream material arrives under **two** licenses, which Vortex's `REUSE.toml` splits:

| upstream material | license | what this repository does with it |
|---|---|---|
| source tree, including the `.fbs` and `.proto` schemas and the format constants | Apache-2.0 | schemas vendored verbatim under `spec/`; constants transcribed into `docs/` |
| documentation at docs.vortex.dev and upstream `docs/` | **CC BY 4.0** | condensed and adapted throughout `docs/` |

Two obligations bind, and both are met in `NOTICE`:

1. **Apache-2.0 §4(c)**: the vendored schemas keep their SPDX copyright and license headers, and
   `NOTICE` lists each vendored file against its upstream path.
2. **CC BY 4.0**: adapting the upstream documentation requires attribution, a link to the license,
   and an **indication that changes were made**, which citing the source alone does not give.

`NOTICE` also records, per component, whether it was **reimplemented from the specification** or
**translated from reference code**; only the second inherits the upstream license's attribution
requirements. Reimplementation from the specification is the rule, with the reference consulted
for semantics.

"Vortex" is a trademark of LF Projects, LLC. Vorticity is an independent implementation, not
affiliated with or endorsed by the Vortex project, and says so at the top of `NOTICE` and of the
benchmark page.
