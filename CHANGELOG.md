# Changelog

Nothing has been released yet, so nothing carries a version number. This entry describes what the
first release will contain; the release itself will date it and give it one.

## Unreleased

### `Vorticity`

The Vortex columnar file format in pure .NET, with `System.IO.Hashing` as its one runtime
dependency. The public API is async throughout, and the hot paths allocate nothing per batch.

**Reading** is complete for the 1.0 scope: opening a file, the layout tree, all 37 array encodings
that have a decoder and all 7 layouts, typed column access, scans with projection and row ranges,
filter pushdown, zone-map pruning, random access by row index with the selection pushed down into
the encoding rather than gathered afterwards, aggregates and group by on the encoded form, skipping
and locating indexes, and a key cursor with seek, step, rank and distinct.

**Writing** produces files Vortex Rust reads back: a fused single-pass writer, exact block
statistics, a rule-based scheme chooser, append to an existing file, and recovery from a torn tail.
Edition targeting is enforced on the way out, so a file cannot claim a component its target does not
contain.

**The default write edition is `core2026.08.3`** (`VortexEditions.Default`), which Vortex Rust reads
from 0.85.0. It is the first edition that carries `vortex.uuid`, so a `Guid` column writes with the
defaults, and it equals `VortexEditions.Newest` in this release.

#### The public surface

About sixty types a caller names on purpose, in two namespaces: `Vorticity`, and `Vorticity.IO`
for the segment seam. Everything else in the assembly is internal.

* **Session.** `VortexSession` owns the memory pool (`AlignedMemoryPool`, 64-byte aligned), one
  `SegmentCache` for every file it opens, the bound on reads in flight (`MaxConcurrentReads`), the
  degree of parallelism (`MaxDegreeOfParallelism`, 1 unless set), the index cache, the files it
  keeps mapped once closed (`MappedFileCacheCount`, 64 unless set) and the registry of extension
  dtypes (`IVortexExtension<TSelf>`). Its options are frozen by `Create`;
  `VortexSession.Default` is immutable and is what `VortexFile.OpenAsync(path)` uses. It opens
  files, creates writers and opens appends.
* **Schema and records.** `VortexSchema`, `VortexField` and `VortexType` describe a file's columns
  as values; a schema is a collection expression of `(name, type)` pairs. A `[VortexRecord]` type
  implements `IVortexRecord<TSelf>`, its schema and how its rows are read from columns and written
  to builders, shaped by `[VortexColumn]` and `[VortexIgnore]`. It binds to a file by member name,
  exactly and then case-insensitively; the record is the projection.
* **Symbolic scan and columns.** `VortexFile.Scan<TRecord>()` returns a `Scan<TRecord>`, composed
  with `Where` (a lambda over `Probe<TRecord>`, whose members are `Sym<T>`: comparisons to a
  literal of the column's type or to another column, `In`, `Between`, nullity, `StartsWith`,
  `Contains`, `Like`, list membership, joined into a `Predicate`), `Rows`, `OrderBy` and
  `With(ScanOptions)`. `await foreach` yields a borrowed `Columns<TRecord>` per batch; a
  `Column<T>` exposes contiguous 64-byte aligned `Values`, typed indexers, `ValidityWords`,
  `NullCount`, and its encoded form (`DictionaryView<T>`, `RunEndView<T>`, `AsConstant`) beside a
  `Selection`. The other sinks: `ToBatchesAsync`, `ToRecordsAsync`, `CountAsync`, `AnyAsync`,
  `MinAsync`, `MaxAsync`, and `ExplainAsync` and `Statistics` for the plan before and the numbers
  after. `VortexFile.MayMatch<TRecord>` answers from the file statistics.
* **Aggregates.** `SumAsync`, `AvgAsync`, `CountDistinctAsync`; `AggAsync`, up to eight answers in
  one pass; `GroupBy` on one to four keys, then `AggAsync` into an `Aggregation<TResult>`; and
  `AggregateAsync` with a caller's `IAggregator<T, TState>` or `IEncodedAggregator<T, TState>`. They
  run block by block on the encoded form, one state per chunk merged at the end; an integer sum is
  exact in 128 bits.
* **Key cursor.** `Scan<TRecord>.Keys(r => r.Column)` returns a `KeyCursorBuilder<TKey>`
  (`Distinct`, `ExplainAsync`, `OpenAsync`), and a `KeyCursor<TKey>` seeks, steps by row or by key,
  ranks and counts, with keys of the column's own type.
* **Tool path.** `VortexFile.Scan(params ReadOnlySpan<string> columns)` returns a `Scan` for a file
  whose schema is not known when the program is compiled: `Where` takes a `VortexExpr`
  (`VortexExpr.Parse`, `&`, `|`, `!`) or an interpolated string whose holes are typed
  (`FilterHandler`), and `await foreach` yields a `BatchView` whose columns are read by name or
  index. `RecordBatch`, the owned batch, is shared by both paths.
* **Writer and builders.** `VortexSession.CreateWriter`, over a path, for a record type or into a
  `PipeWriter`, and `AppendAsync` return a `VortexFileWriter`. Its one reusable `ColumnsBuilder`,
  also seen as `ColumnsBuilder<TRecord>`, holds `ColumnBuilder<T>` columns filled in place
  (`GetSpan` and `Advance`), appended to one value or one bitmap at a time, with lists and nested
  records. `WriteAsync` takes the builder, a span or a stream of records, the columns of a scan, a
  `BatchView` or a `RecordBatch`; `FlushAsync` seals whole blocks into a chunk; `CompleteAsync`
  returns a `WriteReport`, which says what each chunk of each column was written as;
  `Abandon` gives the file up. `VortexFileIndexer.AppendIndexesAsync` adds indexes to a file
  already written, and `BuildFragmentAsync` builds them into an `IndexFragment` a reader passes at
  open, leaving the file untouched.
* **Options, plans, reports, diagnostics.** `VortexOpenOptions`, `VortexWriteOptions` and
  `ScanOptions` are records with `init` properties, beside `CompressionProfile`, `EncodingHint`,
  `VortexTornTailPolicy` and `IndexPolicy` (`Bloom`, `NgramBloom`, `Postings`, `SortedRuns`,
  `ForKey` with an `IKeyEncoder`, `required`, `WithBudgetPerMille`). `ScanPlan`, `PruningStep`,
  `CountPlan`, `OrderPlan`, `ScanStatistics`, `WriteReport`, `VortexFileStatistics`,
  `FieldStatistics`, `VortexMetadata`, and the inspection records `VortexComponent`,
  `VortexSegment` and `VortexLayout`. `VortexDiagnostics` names the `Meter` and the
  `ActivitySource`, both `Vorticity`. The exceptions are `VortexException` and its three
  subclasses, `VortexFormatException`, `VortexUnsupportedException` and `VortexSchemaException`.
* **I/O seam.** `ISegmentSource` (a length, one range, or several ranges read together),
  `SegmentRange` and `SegmentLease`, with three sources: `FileSegmentSource` through
  `RandomAccess`, `MemoryMappedSegmentSource` and `MemorySegmentSource`. A writer's sink is a
  `PipeWriter`.
* **Satellites.** `Vorticity.Generators` ships the generator and eight analyzers: VX1001 to
  VX1004 are warnings (a borrowed span kept past its batch, an owned batch never disposed, a filter
  hole of the wrong type, rows by range and by index on one scan), VX1005 to VX1008 errors (a member
  with no mapping, a type that is not `partial`, a type that cannot be a record, a member the reader
  cannot fill). `vxdump` is written against the public surface alone.

#### What left the surface

* The arenas and the engine: `DTypeArena`, `DType`, `PType`, `CanonicalArena`, `CanonicalNode`,
  `VortexBuffer`, `Validity`, `AlignedBufferPool`, and every decoder, layout reader, metadata
  struct, scalar, FlatBuffers and Protobuf type, registry and `Int256` are internal.
* `RecordBatch`'s constructors and engine accessors, `VortexColumn` and the nine typed column views.
* `ScanBuilder` and `VortexFileScanExtensions`, `Projection`, `ScanRequest`, `ScanContext`, `ScanMetrics`,
  `CountingSegmentSource`, and the static `ScanBuilder.DefaultDegreeOfParallelism`.
* `Expr`, the `VortexExpr` node classes, `FilterLiteral`, `ComparisonOp`, `StringMatchOp`,
  `ExprKind`; `VortexExpr` stays as the parse result of the tool path.
* `ColumnSummaries`, `SummaryPruner`, `ZoneMap`, `FileStatistics`, `FileStatisticsView`.
* The untyped key cursor: `file.Keys(path)` and `file.Keys(paths)`, `KeyCursor`,
  `KeyCursorBuilder`, `KeyCursor.Compare`, `KeyBytes`.
* The old seam: `SegmentSpec`, `SegmentOwner`, `SegmentRequestSet`, `SegmentReadOptions`,
  `SegmentCoalescer`, `RandomAccessSegmentSource` and the segment owners; `ISegmentSink` and
  `StreamSegmentSink`.
* `VortexReadOptions`, `WriteProfile`, `VortexEncodingHint`, `WritePolicy`, `CompositeKeyPolicy`,
  `IndexPolicyKind`, folded into the new options; `EditionRegistry`, now `VortexEditions`;
  `VortexFileFormat`, `IndexDirectory` and its entries.

#### Behaviour a caller of the earlier API will notice

* **Async only.** A scan is consumed with `await foreach`, whatever the source: there is no
  synchronous enumeration, and `ISegmentSource` has no synchronous read. A writer is fed with
  `WriteAsync`; filling a builder, which is memory work, is the one synchronous step.
* **A batch is borrowed.** `await foreach` yields columns that are `ref struct`s, valid until the
  next `MoveNextAsync`, which the compiler enforces; there is nothing to dispose. An owned batch is
  asked for: `ToBatchesAsync()` and `ToOwned()` copy each batch once into buffers from the session's
  pool, which the caller disposes.
* **Disposing a writer abandons it.** Without `CompleteAsync`, a created file is deleted, an append
  is truncated back to the length it had, and a caller's `PipeWriter` is completed with an error.
  Disposal used to complete the file.
* **No index by default.** `VortexWriteOptions.Indexes` is `IndexPolicy.None`; it was `Auto`. An
  index marked `required` that cannot be built makes `CompleteAsync` throw, and the file is not
  completed; an index the budget abandons leaves no bytes in the file.
* **Text columns prune by default.** Their zones carry 16-byte bounds (`StringBoundBytes`); the
  default was none.
* **Nullability is in the type.** `Column<double?>` and `Column<string?>` are nullable columns; a
  non-nullable record member over a nullable column is refused at binding with
  `VortexSchemaException`. `string` and `string?` columns share their accessors: a UTF-8 span
  indexer, empty at a null, `GetString`, `null` at a null, and `GetLength`.
* **A literal of the wrong type is refused.** On the typed path it does not compile; on the tool
  path `Where` throws `VortexSchemaException`. It used to match nothing, silently.
* **Rows by index come in their blocks.** `Rows(indices)`, which was `Take`, delivers each block
  that holds a row asked for, with a `Selection` of those rows located from `StartRow`;
  `ToRecordsAsync` yields only the rows asked for.
* **A scan is single-use.** Every composition call returns the same builder, and a second sink
  throws.
* **Answers are typed.** `MinAsync`, `MaxAsync` and a cursor's keys are of the column's type, not a
  `FilterLiteral`.
* **Parallelism is the session's.** `MaxDegreeOfParallelism` replaces the process-wide static, and
  `ScanOptions.DegreeOfParallelism` overrides it for one scan; reads in flight are bounded per
  session by `MaxConcurrentReads`.
* **`VortexUnsupportedException.Kind` is a `ComponentKind`**, which gains `Compression`,
  `Encryption`, `Index` and `Feature`.

**Not in this release**, each by decision rather than omission:

* No sampling compressor. The writer measures the whole block in one fused pass instead, which is
  what makes a sample unnecessary rather than unaffordable.
* The same rows written in different batch sizes produce a valid file of a different size. The
  determinism promised is *the same batches give the same bytes*, not *the same rows give the same
  bytes*; `docs/design/11-write-strategy.md` states the limit and its cost.
* No encryption. The format reserves a slot for it and this library writes it empty.
* No S3 client. `Vorticity.Dataset` defines the store seam; an implementation is a caller's.

### `Vorticity.Generators`

The `[VortexRecord]` source generator and the analyzers VX1001 to VX1008, build-time only. The
attributes and `IVortexRecord<TSelf>` live in the core, so a project can write a record by hand
without the generator; it loses the extension members, and nothing else.

### `Vorticity.Dataset` — experimental

A versioned dataset over an object store: immutable data and commit objects, a prolly tree of
leaves, compaction, vacuum, and the seam an object-store client implements. **The format is this
repository's own** — no other implementation reads it — and decisions about it are still open. Treat
it as subject to change; the assembly is `[Experimental("VX0001")]`.

A dataset is scanned with the same `Scan<TRecord>` and `Scan` as a file, pinned to the version its
handle read. `IObjectStore` reads a range with the object's token in the same answer, creates an
object from a `PipeReader` of known length so that it streams, deletes a batch of keys, and lists
through an `IAsyncEnumerable<string>` that asks for its pages as it goes. Its exceptions derive from
`VortexException`, and the maintenance that reads the clock takes a `TimeProvider`.

### `Vorticity.RowEncoding` — experimental

The byte-sortable row encoding: columns to row keys whose `memcmp` order is tuple order, byte for
byte with the reference. **Upstream marks this format experimental and reserves the right to change
its byte layout between releases**, which is why it is a package of its own rather than part of the
core, and why the assembly is `[Experimental("VX0002")]`.

It encodes the columns of a typed batch or a `BatchView`, and one key given as a record whose members
are the key columns (`EncodeKey<TKey>`). `RowKeyEncoder` is the `IKeyEncoder` a composite key index
names in `IndexPolicy.ForKey`.
