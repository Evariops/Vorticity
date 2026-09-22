# Library contracts: concurrency, versioning, threat model, observability, licensing

The promises made to a caller, as opposed to the promises made about bytes. Short by design — each
section exists so the answer is written down once rather than deduced differently at each call
site.

## 1. Concurrency and thread-safety

| Type | Contract |
|---|---|
| `VortexSession` | **Thread-safe**, and meant to be shared by every request of a process; its options are frozen when `Create` returns, and `VortexSession.Default` is immutable. Disposing it while a file it opened is still open throws `InvalidOperationException` naming the file |
| `VortexFile` | **Thread-safe.** Concurrent scans on one open file are supported and expected; the footer and layout tree are immutable after open |
| `ISegmentSource` | Implementations **must** be thread-safe; the built-in ones are |
| `Scan<TRecord>` / `Scan` | **Single-use, one thread.** Every composition call returns the same builder; build on one thread, then run one sink. A second sink throws `InvalidOperationException`. `ExplainAsync` may be asked before the sink |
| The scan's enumerator | Single consumer, as the language requires |
| `Columns<TRecord>` / `Column<T>` / `BatchView` | **Affine to the enumeration body**, enforced by the compiler: `ref struct`s valid until the next `MoveNextAsync` or `DisposeAsync`, which cannot be stored, captured or carried across an `await`. The one escape the compiler does not see, a span copied into a variable declared outside the loop, is analyzer VX1001's |
| `RecordBatch` | **Affine to its consumer.** Not thread-safe; it may be handed to another thread, a channel for instance, and disposed there. Its spans die with it |
| Decoders / kernels | Pure functions over borrowed memory; no shared mutable state |
| `KeyCursorBuilder<TKey>` / `KeyCursor<TKey>` | Not thread-safe, like the scan (docs/design/12-index-reads.md §8.3) |
| The run cache on `VortexFile` | **Thread-safe**, like the layout tree; a load happens outside its lock, and the first insert wins a race |
| `KeyPlan` / `ScanPlan` / `CountPlan` / `OrderPlan` / `ScanStatistics` / `WriteReport` | Immutable records |
| `ColumnsBuilder` / `ColumnBuilder<T>` | **One thread.** The builder a writer hands out is its own, reused after every `WriteAsync` |
| `VortexFileWriter` (including an append) / `VortexFileIndexer` | One writer per file; an append is not atomic, and `VortexFileRepair` truncates a torn one |

## 2. Parallelism

Unspecified parallelism makes the benchmark target meaningless — a ratio against Rust is only
interpretable at equal threading. The 1.0 model:

* **Sequential decode by default.** A library must not appropriate the host's thread pool without
  consent; a server running 200 concurrent requests does not want each scan fanning out.
* **Consent is given on the session, once**: `VortexSessionOptions.MaxDegreeOfParallelism` is the
  degree every scan of the session starts from, decoding and aggregating independent chunks
  concurrently, for a host that decides its threading at start-up rather than at each of a hundred
  call sites. Splits are already the unit of independence in the format. It is **1** unless set, and
  `VortexSession.Default` is immutable, so a caller who configures nothing sees the sequential
  default. There is no process-wide mutable static: two sessions in one process keep two degrees.
* **Per scan, the call wins**: `ScanOptions.DegreeOfParallelism` overrides the session's degree for
  one scan; 0 takes the session's.
* I/O concurrency is separate and always on: the batch `ReadAsync` of the seam issues overlapping
  reads regardless of decode parallelism, because latency hiding is the whole point on object
  storage. It is bounded per session by `VortexSessionOptions.MaxConcurrentReads`, 16 by default,
  across every scan of every file: a `SemaphoreSlim`, and no package. A mapped file and bytes in
  memory have no reads to bound.

Benchmark rule ([05-benchmarks.md](05-benchmarks.md)): both implementations pinned to the same
thread count, ratios reported at 1 thread **and** at N threads. The Rust harness's threading
configuration is pinned explicitly in the FFI setup, not left to its default.

## 3. Versioning

Semantic versioning, with the meaning of each level spelled out because a format library's
version has two axes (our API, and the format we understand):

| Change | Level |
|---|---|
| Supporting a newly frozen upstream edition (reading more IDs) | minor |
| Adding an encoder for an encoding we already decode | minor |
| Widening `VortexUnsupportedException` to a case that previously threw `VortexFormatException` | minor |
| Dropping or adding a target framework | **major** |
| Changing the lifetime contract of batch-borrowed spans | **major** |
| Changing default write edition | **major** — it changes who can read our output |
| Row encoding byte layout following upstream | **not applicable** — see below |

The default write edition is `VortexEditions.Default`, and it is not the newest edition by
definition: it is the edition the most deployed Rust reader accepts, chosen per release and named
in the release notes, beside the minimum Rust version that reads it
(`VortexEditions.MinimumRustVersion`). A writer that wants another passes
`VortexWriteOptions.TargetEdition`. The first release sets it to `core2026.08.3`, read by Vortex Rust
from 0.85.0: the first edition that carries `vortex.uuid`, without which a `Guid` column cannot be
written, and equal to `VortexEditions.Newest` today, which the next edition will change.

**Row encoding ships as a separate package**, `Vorticity.RowEncoding`, versioned `0.x`. The
reason is structural: upstream marks that format experimental and reserves the right to change its
byte layout between releases. Binding it into a stable `1.x` core would force a choice between
breaking our own semver to follow upstream, or diverging from Rust and losing the byte-exactness
that is the feature's entire point. A `0.x` package that tracks Vortex releases explicitly has
neither problem. The technical plan in [06-row-encoding.md](06-row-encoding.md) is unchanged,
including its parallelizability — only the packaging and the 1.0 acceptance criteria move.

Each release records the Vortex version its corpus was generated against and the highest edition
it can read. That pairing, not the version number, is what a user actually needs.

## 4. Threat model

Two sentences, because the alternative is an unbounded promise:

1. **Malformed input**: a file that violates the format — bad offsets, truncation, out-of-range
   Class I fields, exceeded caps — is guaranteed to produce a clean `VortexFormatException` or
   `VortexUnsupportedException`. No out-of-bounds access, no unbounded allocation, no hang. This is
   enforced by fuzzing.
2. **Well-formed but lying input**: a file whose structure is valid but whose Class II statistics
   are false may produce wrong results. This is inherent to every format with embedded statistics,
   Parquet included. `VerifyStatistics` trades throughput for validation when the producer is not
   trusted. See [08-semantics.md](08-semantics.md) §5.

What is explicitly *not* claimed: protection against a malicious producer targeting resource
exhaustion below the configured caps, or any confidentiality property (the format's encryption
slot is an empty reserved table).

## 5. Observability

An `EventSource` named `Vorticity`, with counters chosen so that the first "it's slow on my
bucket" report is diagnosable without a debugger:

| Counter | Diagnoses |
|---|---|
| `segments-requested` / `segments-read` | coalescing effectiveness (the ratio is the signal) |
| `bytes-read` vs `bytes-used` | read amplification — the object-storage failure mode |
| `cache-hits` / `cache-misses` | whether the segment cache is doing anything |
| `zones-pruned` / `zones-total` | whether pruning is working at all — the most common silent regression |
| `bytes-decompressed` | decode-bound vs I/O-bound |
| `open-latency` | the 1–2 round trip promise, in production |

Zero-cost when no listener is attached, which `EventSource` gives us for free.

docs/design/12-index-reads.md §8.1 adds the counters that say whether an index earns its bytes:

| Counter | Diagnoses |
|---|---|
| `index-runs-read` | index payload segments read — the price of consulting the runs |
| `cursor-seeks` / `cursor-steps` | a cursor that seeks per row where it should step, or the reverse |
| `count-blocks-proven` / `count-blocks-decoded` | whether counts resolve from the zone maps or pay a decode |
| `key-order-windows` / `key-order-window-splits` | what `InKeyOrder` costs: splits per window near 1 on a correlated key, near the window on an uncorrelated one |

**As delivered (step 18 of IMPL-PLAN).** `Diagnostics/VortexEventSource`, name `Vorticity`,
publishes as `IncrementingPollingCounter`s: `segments-requested`, `bytes-requested`,
`zones-pruned`, `zones-total`, and the six rows above. Every hook is one `IsEnabled` test; the
totals are `Interlocked` longs, so a step still allocates nothing. `segments-read`, `bytes-used`,
`cache-hits` / `cache-misses`, `bytes-decompressed` and `open-latency` are not published: they
belong to the I/O and codec layers these specs did not change, and stay this section's to-do. A
listener that asks for counters passes `EventCounterIntervalSec` as a whole number: the runtime
parses it in the current culture.

*Amended by [14-public-api.md](14-public-api.md) §7:* the public observability is a `Meter` and an
`ActivitySource`, both named `VortexDiagnostics.MeterName` / `ActivitySourceName`, `Vorticity`,
which OpenTelemetry and `dotnet-counters` read without an adapter. The meter's counters, each added
on its own so that a listener enabling one pays for that one:

| Instrument | Diagnoses |
|---|---|
| `vortex.scan.rows` | rows delivered |
| `vortex.scan.requests`, `vortex.scan.bytes_requested` | what the scans asked their sources for — read amplification, against the plan's `BytesToRead` |
| `vortex.scan.blocks_decoded`, `vortex.scan.blocks_pruned` | whether pruning works at all |
| `vortex.cache.hits`, `vortex.cache.misses` | whether the session's segment cache is doing anything |
| `vortex.write.bytes` | bytes the writers handed their sinks |

A scan is one activity, `vortex.scan.typed` or `vortex.scan.tool`, tagged when it ends with what it
did: `vortex.rows`, `vortex.batches`, `vortex.requests`, `vortex.bytes_requested`,
`vortex.blocks_decoded`, `vortex.blocks_pruned`. A write is one activity, `vortex.write`, tagged
`vortex.rows`, `vortex.bytes` and `vortex.completed`, with an error status when it is abandoned. The
figures of one query are `ScanStatistics`, read after the fact. The `EventSource` above stays, with
the index counters of 12 §8.1, which the meter does not repeat.

## 6. Licensing and attribution

Vorticity is licensed under Apache-2.0 (`LICENSE`, canonical text, no modifications).

Upstream material arrives under **two** licenses, and conflating them is the mistake to avoid.
Vortex's `REUSE.toml` splits them:

| Upstream material | License | What we do with it |
|---|---|---|
| Source tree, including `.fbs` / `.proto` schemas and format constants | Apache-2.0 | schemas vendored verbatim under `spec/`; constants transcribed into `docs/` |
| Documentation at docs.vortex.dev and upstream `docs/` | **CC BY 4.0** | condensed and adapted throughout our `docs/` |

Exactly **two obligations are binding**, and both live in `NOTICE`:

1. **Apache-2.0 §4(c)** — the vendored schemas keep their SPDX copyright and license headers, and
   `NOTICE` lists each vendored file against its upstream path. This is a duty to *preserve*
   existing notices, not to write new ones.
2. **CC BY 4.0** — adapting the upstream documentation requires attribution, a link to the
   license, and an **indication that changes were made**. The last part is the one most often
   missed: citing the source is not sufficient on its own.

Everything else in `NOTICE` is voluntary record-keeping, kept because it is useful to us rather
than because anyone requires it: the per-component distinction between **reimplemented from the
specification** and **translated from reference code** (only the second inherits the upstream
license's attribution requirements). The default working mode is reimplementation from the
specification, with the reference consulted for semantics; where translation is unavoidable it is
noted in the file header and added to `NOTICE`.

Two deliberate omissions, so they are not "fixed" later by someone assuming they were oversights:

* **No copyright line for Vorticity itself**, in `LICENSE` or `NOTICE`. Copyright arises
  automatically on creation; Apache-2.0 requires no such line, and §4(d) is explicit that a project
  without a `NOTICE` file need propagate nothing. The line in the license's appendix is a
  recommended template, not a clause.
* **No licensing section in `README.md`.** `LICENSE` and `NOTICE` carry it; restating it in the
  readme is ceremony that adds a maintenance surface without adding a right.

Separately: "Vortex" is a Linux Foundation (LF AI & Data) project. The non-affiliation disclaimer
at the top of `NOTICE` reduces the risk but does not remove it — before publishing under the name
`Vorticity` with public conformance claims, check the foundation's trademark guidelines. A
one-hour check now avoids a rename after release.

## 7. Build configuration notes

* `global.json` pins the SDK with an explicit roll-forward policy. The `net11.0` target currently
  resolves to an RC SDK; an unpinned build silently changing compiler version under a
  performance-sensitive library is not acceptable.
* `InvariantGlobalization` is an **application** property and has no effect on a class library.
  It belongs in `vxdump` and the benchmark host, not in `Vorticity.csproj`. The core avoids
  culture-sensitive APIs by construction instead — no `ToString()` without
  `CultureInfo.InvariantCulture`, no culture-sensitive comparisons.
* A module initializer asserts `BitConverter.IsLittleEndian`. The entire zero-copy design
  (`MemoryMarshal.Cast` over file buffers) assumes it; one line documents the assumption and fails
  loudly instead of returning byte-swapped data.
* A static assert verifies `sizeof(SegmentSpec) == 16` and `sizeof(BufferSpec) == 8`. Natural
  packing happens to be correct; the assert survives refactoring.
