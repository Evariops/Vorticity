# Library contracts: concurrency, parallelism, versioning, threat model, observability, licensing

The promises made to a caller, as opposed to the promises made about bytes, written down once so
that each one is not deduced differently at each call site.

## 1. Concurrency and thread safety

| type | contract |
|---|---|
| `VortexSession` | thread-safe, and meant to be shared by every request of a process. Its options are frozen when `Create` returns, and `VortexSession.Default` is immutable. Disposing it while a file it opened is still open throws `InvalidOperationException` naming the file |
| `VortexFile` | thread-safe. Concurrent scans of one open file are expected, and its footer, layout tree, index directory and the runs it caches are immutable or published once |
| `ISegmentSource` | implementations must be thread-safe, and the built-in ones are |
| `Scan<TRecord>`, `Scan` | single use, one thread. Composition returns the same builder, so build it on one thread, then run one sink. A second sink throws `InvalidOperationException`. `ExplainAsync` may be called before the sink |
| `GroupedScan<TRecord, TKey>`, `Aggregation<T>`, `Aggregation`, `Projection<T>`, `Projection`, the `Scan<TRecord>` of a result | single use, one thread, like the scan they are built on. Enumerating one runs its query once, and a second sink throws `InvalidOperationException` |
| the scan's enumerator | one consumer, as the language requires |
| `Columns<TRecord>`, `Column<T>`, `BatchView` | bound to the loop body, enforced by the compiler. They are `ref struct`s valid until the next `MoveNextAsync` or `DisposeAsync`, and cannot be stored, captured or carried across an `await`. The one escape the compiler does not see, a span copied into a variable declared outside the loop, is caught by analyzer VX1001 |
| `RecordBatch` | owned by its consumer, and not thread-safe. It may be handed to another thread (through a channel, for instance) and disposed there, and its spans become invalid with it |
| `KeyCursorBuilder<TKey>`, `KeyCursor<TKey>` | not thread-safe, like the scan |
| `ColumnsBuilder`, `ColumnBuilder<T>` | one thread. The builder a writer hands out belongs to the writer and is reused after every `WriteAsync` |
| `VortexFileWriter` (an append included), `VortexFileIndexer` | one writer per file. An append is not atomic, and `VortexFileRepair` truncates a torn one |
| `ScanPlan`, `CountPlan`, `OrderPlan`, `KeyPlan`, `ScanMetrics`, `WriteReport` | immutable records |
| decoders and kernels | pure functions over borrowed memory, with no shared mutable state |

## 2. Parallelism

A ratio against Rust only means something at equal threading, and a library must not take over a
host's thread pool without consent: a server running 200 concurrent requests does not want each scan
fanning out. So:

- The default is sequential. `VortexSessionOptions.MaxDegreeOfParallelism` is 1 unless set, and
  `VortexSession.Default` is immutable, so a caller who configures nothing gets one thread.
- Consent is given once, on the session. Its degree is where every scan and every writer of the
  session starts, which suits a host that decides its threading at start-up rather than at a hundred
  call sites. Two sessions in one process keep two separate degrees.
- A single call may override it: `ScanOptions.DegreeOfParallelism` for one scan and
  `VortexWriteOptions.DegreeOfParallelism` for one file, where 0 takes the session's.
- A scan decodes its splits side by side, each on a context and arenas of its own with nothing
  shared, and still delivers its batches in row order. A take, whose splits are many and small, keeps
  up to three per lane decoded ahead of its consumer, so the lanes that finish first do not wait for
  the slowest, and its degree still bounds how many decode at once.
- A streaming query runs on that ordered stream of batches, with its decoding in parallel. A blocking
  aggregation keeps a state per group on each lane and merges the lanes' states one part of the key
  space at a time, as tasks it awaits, never a thread blocked on another. The design (see
  [parallelism and the merge](16-queries.md#94-parallelism-and-the-merge) and
  [memory](16-queries.md#95-memory)) folds each lane's rows into a cache of bounded capacity and holds
  every group once, in sub-tables of 256 parts. Its answers are the same bits at every degree, float
  sums included, and stay so at every cache capacity and every merge order.
- A dataset opens its objects ahead of the one being read, and reads them side by side.
- A writer summarizes its columns and compresses a column's zstd frames on its threads, and chooses
  and writes the encodings on the calling one. The file is the same bytes at any degree.
- I/O concurrency is separate and always on. The batch read of the I/O interface issues overlapping
  reads whatever the degree, because hiding latency is the point on an object store. It is bounded
  per session by `MaxConcurrentReads`, 16 by default, across every scan of every file. A mapped file
  and bytes in memory have no reads to bound.

[threads.md](../guide/threads.md) shows what each one costs and when parallelism pays off.

## 3. Versioning

The library follows semantic versioning, with each level spelled out, since a format library's
version has two axes: its API and the format it understands.

| change | level |
|---|---|
| reading a newly frozen upstream edition (more ids) | minor |
| an encoder for an encoding already decoded | minor |
| a case that threw `VortexFormatException` now throwing `VortexUnsupportedException` | minor |
| dropping or adding a target framework | major |
| changing the lifetime of borrowed spans | major |
| changing the default write edition | major, since it changes who can read the output |
| the row encoding's bytes following upstream | not applicable, see below |

The default write edition is `VortexEditions.Default`, and it is not the newest by definition. It is
the edition the most widely deployed Rust reader accepts, chosen per release and named in the release
notes next to the minimum Rust version that reads it (`VortexEditions.MinimumRustVersion`). Today it
is `core2026.08.3`, read by Vortex Rust 0.85.0 and later, the first edition with `vortex.uuid`,
without which a `Guid` column cannot be written.

The row encoding ships separately, in `Vorticity.RowEncoding`, versioned `0.x` and marked
experimental. Upstream reserves the right to change its bytes between releases, and binding it into
a stable core would force a choice between breaking this library's semver and diverging from Rust,
which would lose the byte-exactness that is the whole point of the feature.

Each release records the Vortex version its corpus was generated with and the highest edition it
reads. That pair, more than the version number, is what a user needs.

## 4. Threat model

It fits in two sentences, because the alternative is an unbounded promise:

1. Malformed input (bad offsets, truncation, out-of-range fields that memory depends on, exceeded
   caps) produces a clean `VortexFormatException` or `VortexUnsupportedException`, with no
   out-of-bounds access, no unbounded allocation and no hang. Fuzzing enforces it.
2. Well-formed but lying input (a valid structure over false statistics) may produce wrong results,
   as in every format with embedded statistics, Parquet included.
   `VortexOpenOptions.VerifyStatistics` trades throughput for validation when the producer is not
   trusted (see [untrusted hints](08-semantics.md#5-untrusted-hints-a-three-class-policy)). An index
   that lies costs pruning, never rows: its regions carry checksums, and a region that fails claims
   nothing (see [the directory and the runs](10-indexes.md#3-the-directory-and-the-runs)).

Not claimed: protection against resource exhaustion below the configured caps, or any
confidentiality property (the format's encryption slot is an empty reserved table).

## 5. Observability

There are three channels, each free when nobody listens.

The first is a meter and an activity source, both named `Vorticity` (`VortexDiagnostics`), which
OpenTelemetry and `dotnet-counters` read without an adapter. Each instrument is created on its own,
so a listener that enables one only pays for that one:

| instrument | diagnoses |
|---|---|
| `vortex.scan.rows` | rows delivered |
| `vortex.scan.requests`, `vortex.scan.bytes_requested` | what the scans asked their sources for, which shows read amplification against the plan's `BytesToRead` |
| `vortex.scan.blocks_decoded`, `vortex.scan.blocks_pruned` | whether pruning works at all |
| `vortex.cache.hits`, `vortex.cache.misses` | whether the session's segment cache does anything |
| `vortex.write.bytes` | bytes the writers handed to their sinks |

A scan is one activity, `vortex.scan.typed` or `vortex.scan.tool`, tagged when it ends with its rows,
batches, requests, bytes requested, and blocks decoded and pruned. A write is one activity,
`vortex.write`, tagged with its rows, its bytes and whether it completed, and marked as an error when
it is abandoned.

The second is an event source, `Vorticity`, whose polling counters say whether an index earns its
bytes: `segments-requested`, `bytes-requested`, `zones-pruned`, `zones-total`, `index-runs-read`,
`cursor-seeks`, `cursor-steps`, `count-blocks-proven`, `count-blocks-decoded`, `key-order-windows`
and `key-order-window-splits`.

The third works per query: a scan's `ExplainAsync` is its plan before it runs, and its `Metrics`
the same quantities measured afterwards ([observability.md](../guide/observability.md)).

## 6. Licensing and attribution

Vorticity is licensed under Apache-2.0 (`LICENSE` holds the canonical text).

Upstream material arrives under two licenses, which Vortex's `REUSE.toml` splits:

| upstream material | license | what this repository does with it |
|---|---|---|
| source tree, including the `.fbs` and `.proto` schemas and the format constants | Apache-2.0 | schemas vendored verbatim under `spec/`, constants transcribed into `docs/` |
| documentation at docs.vortex.dev and upstream `docs/` | CC BY 4.0 | condensed and adapted throughout `docs/` |

Two obligations apply, and both are met in `NOTICE`:

1. Apache-2.0, section 4(c): the vendored schemas keep their SPDX copyright and license headers, and
   `NOTICE` lists each vendored file against its upstream path.
2. CC BY 4.0: adapting the upstream documentation requires attribution, a link to the license, and
   an indication that changes were made, which citing the source alone does not provide.

`NOTICE` also records, per component, whether it was reimplemented from the specification or
translated from reference code. Only the second inherits the upstream license's attribution
requirements. Reimplementation from the specification is the rule, with the reference consulted for
semantics.

"Vortex" is a trademark of LF Projects, LLC. Vorticity is an independent implementation, not
affiliated with or endorsed by the Vortex project, and says so at the top of `NOTICE` and of the
benchmark page.
