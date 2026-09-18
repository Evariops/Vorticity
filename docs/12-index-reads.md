# Index reads: cursors, probes and the consumer surface

A Vorticity specification, written 2026-09-15. [10-indexes.md](10-indexes.md) owns what an
index **is** and where it lives; [11-write-strategy.md](11-write-strategy.md) §6 owns the scan
contract that consults one: block masks, then a row selection. This document owns **what a consumer
can ask of the read path once indexes exist** — the operations, their exact semantics, their cost,
and the API that exposes them.

It starts from the inventory of operations a storage engine expects of an index (§1), finds that
the scan answers the *set-shaped* half of them and none of the *order-shaped* half, and adds the
missing half **without changing a byte on disk**: every structure below is a reader over the runs
10 defines and over statistics the file already carries. The one payload decision it forces on 10
— the key order of a sorted run, per dtype — is stated in §4.4 and listed as an amendment in §14.

Four constraints, in order:

1. **Nothing on disk changes.** A file written under 10 serves every operation here; a file
   written before 10, by anyone, serves those its statistics allow (§3).
2. **Memory is bounded by a batch, never by the file.** An operation that would need a column in
   memory — a sort, a distinct table over an unindexed column — is **refused**, naming the index
   kind that would serve it, never emulated. This is [11-write-strategy.md](11-write-strategy.md)
   §9's rule, read from the other side.
3. **Equivalence.** Every answer equals the one a full materialization gives: a cursor walk equals
   a sort of the materialized column, a count equals the length of the scan's output, with indexes
   on or off. [08-semantics.md](08-semantics.md) §1's invariant governs every proof a probe uses.
4. **No allocation per step or per batch** in steady state — [03-architecture.md](03-architecture.md)
   §4, invariant 1, extended to a cursor's step and a terminal's block.

---

## 1. The inventory, and what the read path answers today

Verified 2026-09-15 on the working tree of `perf/src-audit`. The read surface is `ScanBuilder` —
`Project`, `Rows`, `Where`, `Take`, `WithPruning`, `WithMaxBatchRows`, `WithDegreeOfParallelism`,
`ExecuteAsync` (`Scan/ScanBuilder.cs:59-279`) — and one terminal, an `IAsyncEnumerable<RecordBatch>`
in **file order**. `Take` says so in as many words: "batches come out in file order, and reordering
rows to match an arbitrary list would mean buffering the whole result" (`ScanBuilder.cs:197-199`;
`Scan/RowSelection.cs` sorts and deduplicates the indices when the scan is built). The expression
model is six comparison operators, `IN`, `IS [NOT] NULL`, `AND`, `OR`, `NOT`
(`Expressions/VortexExpr.cs:24-66`) over four comparison domains — signed, unsigned, float, bytes —
plus bool (`Expressions/FilterLiteral.cs:17-36`), evaluated by kernels that accept bool, primitive,
utf8 and binary columns and extensions over those, and refuse the rest
(`Compute/ComparisonKernels.cs:65-84`). Pruning answers one question,
`ZonePruner.MayMatch(RowRange)` (`Compute/ZonePruner.cs:62`), from per-zone `Min`, `Max`,
`IsExact` and `NullCount` (`Compute/ZoneBounds.cs:42-63`); the file statistics add `is_sorted`,
`is_strict_sorted`, `null_count` and `nan_count` per root field, with a precision flag on min and
max (`File/FileStatistics.cs:166-246`).

| primitive | today | with 10 and 11 §6 | after this document |
|---|---|---|---|
| point lookup `x = v` | scan, zone maps | `Exact` rows from `sorted.runs`, blocks from postings, Bloom and dictionary pruning; no re-evaluation under an `Exact` cover | unchanged, plus `AnyAsync` and `CountAsync` without rows (§5) |
| membership | scan, then look at a batch | `VortexFile.MayMatch(expr)`, file level, `Superset` | `AnyAsync`: exact, stops at the first proof (§5.1) |
| range scan, open or closed bounds, with or without an upper bound | scan, zone maps; rows in file order | the contiguous slice of each run; rows in file order | rows in **key order** through `InKeyOrder` (§6); entries through a cursor (§4) |
| prefix | not expressible on strings; expressible on a composite key through the row encoding | same | `StartsWith` in the expression model (§7), pruned as a range; `KeyBytes.StartsWith` on a cursor (§4.6) |
| min / max | the file statistic when present and `Exact`; otherwise a scan written by the caller | same | `MinAsync` / `MaxAsync` on a scan: statistics, then zone maps, then an ordered source, then a bounded scan (§5.3) |
| successor / predecessor | nothing | nothing | `SeekAsync(k, After)` / `SeekAsync(k, Before)` (§4.1) |
| full ordered scan | nothing; a global sort is rejected by 11 §9 | nothing | a cursor from `SeekFirst` to the end; `InKeyOrder` without a filter (§6), at the cost §10 states |
| skip scan / distinct | nothing | the keys of postings and dictionary runs exist and nothing reads them | `NextKeyAsync` on a cursor; `Keys(path).Distinct()` over a postings, dictionary or sorted source (§5.4) |
| rank / select | nothing | nothing | `RankAsync(k)`, `SeekRankAsync(i)`, `KeyCountAsync()` on `Exact` sources (§4.5) |
| count / aggregation without rows | count the scan's output | `Explain` reports the rows an exact index selected, as a diagnostic | `CountAsync`: exact cover, then full-block proofs, then a count-only decode (§5.2) |
| nearest neighbours, spatial, vector | out of scope: `tensor` and `spatial` are plugins outside `core` ([01-scope.md](01-scope.md)) | same | same; not in this document |
| insertion, deletion, update, upsert | none: a file is immutable, an append adds blocks | an append adds runs and never touches a key range | unchanged; a storage engine above this library owns mutation (§2) |
| bulk load | the writer | the writer's streaming builders (10 §7.2); post-hoc by append; sidecar | unchanged |

The reduction "everything is `seek(key, op)` then `next()` / `prev()`" is right, and it is exactly
what the read path lacks: the scan is a *set* engine, and 10 consults an index as a set. §2 says
why, and why the fix is a reader, not a format.

---

## 2. Two models, one payload

**The set model** is what the scan is. A predicate becomes a block mask, the mask a row selection,
the selection batches in file order (11 §6.1). It is the right engine for a filter: it reads each
touched split once, decodes only live blocks, and never reorders. It answers *which rows*, and
every index of 10 is consulted through it as a set of blocks or rows.

**The order model** is what a B-tree cursor is: position at a key, step to the neighbour, in key
order. It answers *what is next*, which no set can. 10 does not have it because of the writer: a
streaming writer produces **sorted runs per chunk** (10 §6.2), never a globally sorted column, so
a key order exists only inside each run, and the union of the runs is a set.

The payload, however, is enough. A `sorted.runs` run is an array of keys in key order with a row
position per entry and a min/max per run (10 §6.2, §4.2). A cursor over the union is a **k-way
merge** over the runs overlapping the key range — a heap of `r` run positions, `O(log r)` per
step, `O(r log n)` per seek — the merging iterator every log-structured store has
(`MergingIterator` in RocksDB, §15). It is a reader-side structure: no byte on disk changes, the
runs are the same arrays, the directory the same directory, and the forged fixtures of 10 §10 test
it before the writer produces a single real run.

So this document keeps **both**, each with its idiom, and every primitive of §1 lands on exactly
one of them:

| model | entry point | answers | delivered as |
|---|---|---|---|
| set | `file.Scan()` | which rows match; how many; whether any; the smallest and largest | batches in file order; `long`; `bool`; a `FilterLiteral` |
| order | `file.Keys(path)` | what is at, after, before, first, last, i-th; the next distinct key; how many share a key | a `KeyCursor` positioned on an entry |
| set driven by order | `file.Scan().InKeyOrder(path)` | the matching rows, in key order | batches in key order, one window at a time |

What neither model gets is **mutation**. Insertion into a key range, deletion, update and upsert
are the operations of a storage engine that owns a memtable, tombstones and compaction; 10's runs
are that engine's flushed tables, "a log-structured merge tree without the merge" (10 §6.2). This
library provides the immutable half — bulk load, append, and every read below — and a caller who
needs the other half builds it above the library, where deletion vectors and compaction belong.
Nothing in the Vortex format carries a tombstone, and this document does not invent one.

---

## 3. Sources of order

A cursor needs a **key source**: something that yields the entries of a column in key order, with
a row per entry, and locates a key in `O(log n)`. Four exist, and the cheapest one the file offers
is used; `Keys(path).Explain()` names it and says why the others were not chosen.

| source | exists when | entries | exactness | seek | serves |
|---|---|---|---|---|---|
| `SortedRuns` | the column carries `vorticity.sorted.runs.v1` (10 §6.2) | `(key, row)` per non-null row, per run | `Exact` | `O(r log n)`: the per-run min/max excludes runs, then a binary search in each survivor's key column — two reads with the blocked payload of 10 §4.2 | everything in §4 |
| `SortedColumn` | the file statistics say `is_sorted` (`FileStatistics.TryGetIsSorted`) and the column has a zone map | `(value, row)` per non-null row, in row order — which **is** key order | `Exact`, subject to the Class II hint rule of 08 §5: a lying `is_sorted` gives a wrong order, never a memory fault, and `VerifyStatistics` validates it | `O(log zones)` over zone `Min` / `Max` — `Inexact` bounds still bracket correctly, since the true min is at or above the stated one and the true max at or below (08 §1) — then one zone decoded and bisected; a zone with nulls is walked linearly rather than bisected, 8 192 values at most | everything in §4, on a file written by **anyone**, with no index at all |
| `Postings` | `vorticity.postings.blocks.v1` (10 §6.1) | the distinct keys of each run, sorted; no rows, only blocks | keys `Exact`; rows unavailable | `O(r log n)` | `Distinct`, `NextKeyAsync`, key-only walks |
| `Dictionary` | `vorticity.dict.probe.v1` (10 §5.3) | the values child of each dictionary-encoded chunk: distinct but **not sorted**, since the writer assigns codes in first-seen order and the key heap in code order *is* the values child (11 §3.2.2) | keys `Exact`; rows unavailable | the values child is sorted at open, `O(d log d)` for a chunk's `d` distinct values, bounded by the chunk and never by the file | `Distinct`, `NextKeyAsync` |
| none | an unsorted, unindexed column | — | — | — | **refused**: `VortexUnsupportedException("vorticity.sorted.runs.v1", "index")`, whose message names the policy (`IndexPolicy.SortedRuns`) and the post-hoc route (10 §8) |

Two rules decide the choice. **Cheapest first**: a `SortedColumn` costs one zone-map read and one
zone decode, so it beats `SortedRuns` whenever both exist — the same reason 10 §5.5's `Auto`
abandons a Bloom on a column the statistics show sorted: the statistics already answer, and so
does this source. **Rows when the caller wants rows**: a
cursor opened without `Distinct()` takes only the first two sources, because a `Postings` or
`Dictionary` source cannot say where a key's rows are, and answering with a block's first row
would make `Row` a lie.

Nulls are in no source. 10 §5.1 inserts no null in any index, a sorted column's null rows carry no
key, and the postings and dictionary sources hold values. A cursor never visits a null; `IS NULL`
is the scan's question, answered from the zone map's null count.

**Amendment (step 11a).** "A file written by anyone" overstated it. No file the reference writer
produces carries `is_sorted` in its file statistics: vortex-layout-0.86.1's file-level aggregation
(`layouts/file_stats.rs`) drops `IsSorted` and `IsStrictSorted` whatever the caller asks for, and
the conformance corpus has none. The `SortedColumn` source therefore serves the files **this**
writer produces, which since 11a carry a statistics segment with the two flags, tracked at ingest
in the reference's own semantics (a null below every value, equal neighbours allowed by the first
flag and refused by the second, a NaN claiming nothing) and held by the Rust cross-check against
the reference's recomputation over the canonical column — the recomputation over the encoded
column is not an oracle: some encoding kernels answer `true` for a column that is not sorted.

---

## 4. The cursor

```csharp
public sealed class KeyCursor : IAsyncDisposable
{
    public bool IsValid { get; }                       // positioned on an entry
    public bool HasRows { get; }                       // false for a Distinct() cursor over a key-only source
    public FilterLiteralKind KeyKind { get; }          // the column's comparison domain
    public FilterLiteral Key { get; }                  // the entry's key; copies a byte key (§4.3)
    public ReadOnlySpan<byte> KeyBytes { get; }        // a byte key, borrowed until the next positioning call
    public long Row { get; }                           // the entry's file row; throws when !HasRows
    public long? EntryCount { get; }                   // the source's entries, when known without a walk

    public ValueTask<bool> SeekAsync(FilterLiteral key, SeekOp op, CancellationToken ct = default);
    public ValueTask<bool> SeekFirstAsync(CancellationToken ct = default);
    public ValueTask<bool> SeekLastAsync(CancellationToken ct = default);
    public ValueTask<bool> NextAsync(CancellationToken ct = default);
    public ValueTask<bool> PrevAsync(CancellationToken ct = default);
    public ValueTask<bool> NextKeyAsync(CancellationToken ct = default);    // the next distinct key: a skip scan
    public ValueTask<bool> PrevKeyAsync(CancellationToken ct = default);
    public ValueTask<long> RankAsync(FilterLiteral key, CancellationToken ct = default);
    public ValueTask<bool> SeekRankAsync(long rank, CancellationToken ct = default);
    public ValueTask<long> KeyCountAsync(CancellationToken ct = default);   // entries sharing the current key

    public static int Compare(FilterLiteral left, FilterLiteral right);      // the key order of §4.4
}

public enum SeekOp : byte { Exact, AtOrAfter, After, AtOrBefore, Before }
```

Every positioning method is asynchronous because it may read a run segment or a zone the file has
not loaded yet; inside a loaded run a step completes synchronously and a `ValueTask` costs nothing
on that path (03 §1: the I/O surface is async-only, and a cursor is I/O). A cursor is not
thread-safe, like the builder that made it (09 §1); the runs it reads are cached on the
`VortexFile` and shared by every cursor and scan on that file (11 §6.3).

### 4.1 Positioning

`SeekAsync(k, op)` positions the cursor and returns whether an entry was found. The entries of a
source are totally ordered by `(key, row)` — §4.4 gives the key order per dtype — and the five
operators are defined on that order:

| `op` | positions on | is | when `k` is absent |
|---|---|---|---|
| `Exact` | the first entry with key `= k` | a point lookup | invalid |
| `AtOrAfter` | the first entry with key `≥ k` | `lower_bound`; the start of a range | the successor's first entry |
| `After` | the first entry with key `> k` | `upper_bound`; the **successor** | the same |
| `AtOrBefore` | the last entry with key `≤ k` | the end of a closed range, walking backwards | the predecessor's last entry |
| `Before` | the last entry with key `< k` | the **predecessor** | the same |

`SeekFirstAsync` and `SeekLastAsync` are `min` and `max`. `Exact` on a key with duplicates lands
on its **lowest row**, `AtOrBefore` on its highest. A range `[a, b)` is `AtOrAfter(a)` then `Next`
while `Key < b`; `(a, b]` is `After(a)` while `Key ≤ b`; a range with no upper bound walks until
`IsValid` is false. A seek with a literal of the wrong domain — a string on an integer column —
throws `ArgumentException` at the call, as a filter does at evaluation; a `Null` literal throws
too, since no entry has a null key.

Under the merge a seek is: keep the runs whose `[min, max]` (the run's `options`, 10 §4.2) can
hold the answer, position each by binary search, build the heap. `r` runs cost `r` binary
searches; a file written once has one run per chunk and a rewritten file few, which is the
log-structured price 10 §4.2 states and §10 does not hide.

### 4.2 Stepping and direction

`NextAsync` moves to the next entry in `(key, row)` order, `PrevAsync` to the previous one; both
return `false` past the ends and leave the cursor invalid. `NextKeyAsync` moves to the first entry
of the next **distinct** key — the loose index scan — by re-seeking `After(Key)` rather than
stepping through the duplicates, so a `GROUP BY` over a key with a million rows costs one seek per
group. `PrevKeyAsync` is `Before(Key)`: the last entry of the previous key.

A cursor has a direction. Stepping the other way after a step re-seeks at the current entry,
`O(r log n)`, because a min-heap of run positions does not run backwards; a consumer that
alternates directions pays a seek per reversal, which is what every merging iterator charges, and
it is documented rather than optimised.

### 4.3 Keys and rows

`Key` is a `FilterLiteral` in the column's comparison domain — the same union a filter's constants
use, so a key read from a cursor goes straight back into `SeekAsync`, into `Expr.Literal`, or into
a comparison with the kernels' own rules. For a fixed-width key it is a 16-byte struct and costs
nothing. For a **byte key** — utf8, binary, a composite key — `Key` copies the bytes, because a
`FilterLiteral` owns its array, and `KeyBytes` lends them: valid until the next positioning call,
the rule 07 §4 states for a batch's spans. A cursor's steady-state step allocates nothing when the
consumer reads `KeyBytes`, and `ScanAllocationTests` pins it (§11).

`Row` is the entry's file row, the coordinate `ScanBuilder.Take` and `Rows` accept, which is how a
cursor's answer becomes rows: collect a window of rows, take them; §6 does exactly that for the
consumer. `HasRows` is false for a `Distinct()` cursor served by a `Postings` or `Dictionary`
source, and `Row` throws `InvalidOperationException` there.

`EntryCount` is the number of entries in the source: the column's non-null rows for `SortedRuns`
(the sum of the runs' `entry_count`, a directory field this document adds to 10 §4.1 so that
`Explain` has it without reading a payload) and `RowCount − null_count` for a `SortedColumn` whose
null count is known; null when it is not known without a walk.

**As delivered (step 11b, the `SortedColumn` source).** The entries are the rows
`[null_count, RowCount)` — contiguous, because a sorted nullable column keeps its nulls at the
front — so the cursor is written against entry INDICES and the merge is degenerate: a step is an
addition, `rank` is `lower_bound`, `select` is the index itself, and `KeyCount` is the difference
of the two bounds. The heap of run positions this document describes is what `SortedRuns` will
need; the surface does not move when it slides underneath. A direction flip costs nothing on one
contiguous run, so §4.2's re-seek charge stays owed by the source that will owe it. A seek walks
the zone bounds in memory for the first zone that may hold the key, decodes that one zone and
bisects inside it; an `Inexact` bound only widens, so a zone it over-includes yields nothing and
the search moves on, which costs a decode and never an answer. The two orders of §4.4 are kept
apart by making the comparator a property of the SOURCE: `KeyCursor.Compare` is the total order,
and a sorted column is walked in the IEEE order its `is_sorted` was computed in, where `−0.0` and
`+0.0` are one key and no NaN can occur, a float column holding one not being `is_sorted` at all.
`Distinct()` is honoured by `NextKeyAsync` rather than by a source of its own until step 15.

### 4.4 Key order per dtype

A cursor is an ordinal structure and needs a **total** order; a filter is a predicate structure and
follows IEEE 754, where NaN is unordered (08 §2). 08 §2 forbids using one for the other, and this
section keeps them apart by stating the total order once, for the writer of 10 §6.2 and the reader
here:

| dtype | key order in a sorted run | notes |
|---|---|---|
| signed and unsigned integers | numeric | the run's key column is the column's own ptype |
| floats (f16, f32, f64) | the total order of [06-row-encoding.md](06-row-encoding.md) §3: negative NaN, −∞, negatives, −0.0, +0.0, positives, +∞, positive NaN | `−0.0` and `+0.0` are two keys, as they are two hash inputs in 10 §5.1. A **range from the scan** maps onto the slice with the IEEE meaning — `x > v` is `(v, +∞]` and never a NaN — which is the one place the two orders meet, and they meet by construction of the bounds, never by a comparison |
| decimals | numeric, at the column's scale | outside this iteration's kernels (`ComparisonKernels.cs:82`), so outside the cursor until the filter has them |
| utf8, binary | bytewise (`memcmp`), which is code-point order for UTF-8 | the comparator `RowKeys.Compare` already uses (`RowEncoding/RowKeys.cs:82`) |
| extension | the storage type's order | a timestamp orders as its integer |
| bool | not a key source | a two-value domain: the dictionary or the postings answer it; a cursor has nothing to walk |
| composite `(a, b, …)` | bytewise over the row encoding of the tuple (10 §6.5) | §4.6 |

Within one key, entries are in **row order**. That makes a walk deterministic, a `Distinct()`
cursor's `Row` the key's first occurrence, and the reverse walk the exact reverse.
`KeyCursor.Compare` exposes this order, so a consumer merging two cursors (§8.2) does not write a
comparator that disagrees with it.

### 4.5 Rank, select, and the count of a key

`RankAsync(k)` returns the number of entries with key `< k`. `SeekRankAsync(i)` positions on the
entry of rank `i`, zero-based, and returns false when `i ≥ EntryCount`. `KeyCountAsync()` returns
the number of entries sharing the current key, `rank(After(Key)) − rank(Key)`. All three are exact
on `SortedRuns` and `SortedColumn` sources and not offered on the others, which have no rows to
count.

None needs an augmented structure: a sorted array **is** its own counter. `rank(k)` is the sum
over the overlapping runs of each run's `lower_bound(k)`, `O(r log n)`. `select(i)` is a binary
search over the position in the largest run with a rank computed at each probe, `O(r log² n)`; a
faster selection (Frederickson–Johnson, §15) changes no API and waits for a measurement that asks
for it. Duplicates count: `rank(select(i)) ≤ i < rank(select(i)) + KeyCount(select(i))`, which §11
tests as an invariant.

### 4.6 Composite keys and prefixes

`file.Keys("country", "city")` opens a cursor over a `sorted.runs` index whose keys are the row
encoding of `(country, city)` (10 §6.5). Its keys are **bytes** and its order bytewise, and both
properties come from 06 §1: encoded bytes compare as tuples, and the encoding concatenates
per-column encodings, so the encoding of `(a)` is a byte prefix of the encoding of `(a, b)`. A
prefix query on the leading columns is therefore `AtOrAfter(prefix)` then `Next` while
`KeyBytes.StartsWith(prefix)`: exact, with no comparator of its own.

The seek key is produced by the row-encoding package: `RowEncoder.EncodeKey(ReadOnlySpan<FilterLiteral>
values, ReadOnlySpan<RowSortField> fields)`, a single-tuple overload this document adds to
`Vorticity.RowEncoding`, producing the same bytes the batch overloads produce
(`RowEncoding/RowEncoder.cs:67,106`). The core stays byte-keyed and depends on nothing — 09 §3
keeps the row encoding in its own `0.x` package — so a composite cursor in core sees opaque ordered
bytes and a row per entry, and the tuple's values come from the row, through a take. Where the
*writer* of a composite index gets its encoder is 10's question, and §13 lists it.

**As delivered (step 16).** `file.Keys("country", "city")` opens the sorted runs of an entry whose
key columns are exactly those, in that order (10 §6.5 as amended); only that source serves, and a
key without one is refused naming `WritePolicy.ForKey` and `VortexWriteOptions.KeyEncoder`.
`KeyKind` is bytes, `KeyFormat` reports what the writer's encoder said its bytes follow, and every
cursor operation — `Distinct()` included — works unchanged over the bytes. `RowEncoder.EncodeKey`
has the spec's overload, which infers `i64`, `u64`, `f64`, `utf8` or `bool` from the literals, and one
that takes the key columns' dtypes, since width and nullability shape the bytes; both build a
one-row column per value and call the batch encoder, which is how the test proves the seek key and
the index key are the same bytes. `RowKeyEncoder(params RowSortField[])` is the writer's encoder;
one field applies to every column of every key. A key column may be nested (`"person.address.city"`):
its entry carries the field-index path, and a row whose tuple crosses a null struct is no entry.

---

## 5. Probes: answers without rows

Four terminals on `ScanBuilder`, each computed without a `RecordBatch` and each bounded by one
batch of memory, whatever the file's size. Each honours `Where`, `Rows`, `Take`, `WithPruning` and
`WithIndexes` exactly as `ExecuteAsync` does: a terminal is the scan with a different output, never
a different scan.

### 5.1 `AnyAsync` — membership

`ValueTask<bool> AnyAsync(ct)`: whether the scan would return at least one row. Cheapest proof
first, stopping at the first that decides:

1. the block mask of 11 §6.1 is empty → false, no data read;
2. an `Exact` source covers the whole predicate → true iff some run has an entry in the
   predicate's slice, `O(r log n)`, no data read;
3. a full-block proof (§5.2) holds for a live block → true, no data read;
4. otherwise the first live split is decoded, the filter evaluated, and the scan stops at the
   first true row; the bad case reads every live split once and materializes none.

Without a filter it is `RowCount > 0`, intersected with `Rows` or `Take`. This is the membership
primitive of §1, exact where `VortexFile.MayMatch` is `Superset`; and it answers 10 §11's open
question — a public `VortexFile.Probe(column, value)` — with `Scan().Where(x = v).AnyAsync()`: the
same probe, with the whole pruning chain in front of it and one fewer type on the surface.

### 5.2 `CountAsync` — the count pushed into the structures

`ValueTask<long> CountAsync(ct)`: the number of rows the scan would return, exactly. Three tiers,
decided per block; a block takes the cheapest that applies:

| tier | proof | cost |
|---|---|---|
| **exact cover** | the predicate is a comparison, `IN`, `StartsWith`, or a conjunction of those on one column with an `Exact` source (`SortedRuns` or `SortedColumn`): the count is the sum of the slice lengths over the runs, `Σ (upper_bound − lower_bound)` | `O(r log n)`, no data read |
| **full-block proof** | the zone map proves every non-null row of the block matches (`MustMatch`, the dual of `MayMatch`) and the block's null count is known: `rows − null_count` | zone map only, already in memory |
| **count-only decode** | the block is decoded, the filter evaluated, `Trilean.CountTrue` taken — the first half of today's `ApplyFilter` (`Scan/BatchAsyncEnumerable.cs:510-512`) without the gather, the projection trim, or the `RecordBatch` | one decode, no copy |

`MustMatch` is new and it is the mirror of `ZonePruner.MayMatch`, with the asymmetry reversed: it
proves only when the statistics point the safe way. For `x ≥ v` a block matches entirely when its
stated `min ≥ v`, and an `Inexact` min still proves it, because the true minimum is at or above the
stated one (08 §1); `x < v` when `max < v`, likewise; `x = v` only when `min = max = v` and both
are `Exact`, since 08 §1 forbids the equality shortcut on inexact bounds. NaN rows never satisfy a
comparison (08 §2) and the zone's min and max exclude them, so a float block needs `nan_count = 0`,
or subtracts a known `nan_count`, or takes the third tier. `IN` is an `OR` of equalities; `AND` and
`OR` combine per block as the pruner does; `NOT` and `IS NOT NULL` push through De Morgan as
`ZonePruner` already does; `IS NULL` is `null_count` itself. A block whose null count is unknown
takes the third tier. Under `Rows` or `Take` the first tier walks its entries instead of
subtracting bounds, and the other two intersect with the selection.

The property that makes this testable is pruning's own: **a wrong proof is a wrong count**. §11
runs every count with each tier forced off in turn and asserts that the numbers agree.

**As delivered (step 10a).** `ZonePruner.MustMatch(RowRange)` and `TryCount(RowRange, out long)`
are two readings of one `RangeVerdict`: **three counts per range — true, false, unknown — each
decided or not**, any two deciding the third. A zone map says how many and never which, so
`NOT` is exact only with all three (it swaps true and false and keeps unknown), which is why the
verdict does not push a negation into the comparison the way `MayMatch` does. `AND` and `OR`
combine as the tautologies of three-valued logic over counts — a side that is true, false or
unknown everywhere decides the other side's contribution — plus the one rule that does the work
in practice: two predicates on the same column are unknown on the same rows, that column's
nulls, and a verdict remembers whose nulls its unknowns are, so `x ≥ a AND x < b` on a nullable
column is decided where the counts alone would not be. The leaves follow this section: an
`Inexact` bound proves an order and never an equality; a NaN row is false for an ordering or an
equality and true for `!=` (08 §2), so "every value satisfies `x > v`" leaves exactly
`nan_count` false rows and "every value equals `v`" leaves exactly `nan_count` true rows for
`x != v`; `nan_count` is read from the zone map; a zone the range covers in part contributes only
what was uniform over it. `StartsWith(p)` proves whole and empty through `[p, succ(p))`; `LIKE`
proves empty through its leading literal; `Contains` nothing but the empty pattern. What the
algebra does not see, on purpose, falls to the third tier: `x > v OR x IS NULL` on one column
(one side's unknowns are the other side's trues). Two things the dual found in the pruner are
fixed with it: `NOT (x > v)` is true on a NaN row where the pushed-down `x ≤ v` is false, so
`MayMatch` now keeps a zone that may hold a NaN under a negated ordering predicate; and a bound
is ordered against a constant exactly as the kernels order a value — an integer widened to
`double`, monotone and therefore sound for the bound — with "not comparable" told apart from
"equal", which a prune never needed and a proof does.

**As delivered (step 10b).** `ScanBuilder.CountAsync` and `AnyAsync` are one walk
(`TerminalScan`): the scan's own frame — `Rows`, or the span of a `Take` — the split plan under
the projection of the filter's columns alone, the mask and the zone-map pruner of the pruning pass
(`ZonePruningPlan.PlanAsync` returns both), then split by split, cheapest proof first: a split the
mask killed counts nothing and reads nothing; a split the maps decide (`ZonePruner.TryCount`)
counts from bounds already in memory; the rest are registered, read and decoded exactly as a
batch is (`SplitExecution`, the one place the take is pushed down, shared with the batch
enumerator), the filter evaluated and the trues counted, with no gather, no projection trim and no
`RecordBatch`, one split of memory at a time whatever the degree. Under a `Take` the maps say how
many rows match and never which, so only a whole answer serves: every row, hence every taken row,
or none. `AnyAsync` is the same walk stopped at the first split that counts. Without a filter both
are arithmetic and read nothing. The exact-cover tier waits for a source; `CountTiers`, internal,
is the switch §11 asks for, and the tests hold every count against the materialized scan with
pruning and the full-block proof each forced off in turn, a count at 0 B per block. Moving the
split's execution to one place found that the batch enumerator's pipelined path (a degree above
one) built its batch without the take and without the filter; it is fixed and tested with it.

### 5.3 `MinAsync` / `MaxAsync` — first and last

`ValueTask<FilterLiteral> MinAsync(string path, ct)` and `MaxAsync`: the smallest and largest
non-null value of `path` among the scan's rows, `FilterLiteral.Null` when there is none, in the
filter's IEEE order for floats — a NaN is never the min or the max, matching the `min` / `max`
statistics, which skip NaN (08 §2). Resolution:

1. no filter, no `Rows`, no `Take`, and the file statistic is present and `Exact` → the statistic
   (`FieldStatistics.Min`, `MinPrecision`);
2. no filter, and the zone map's bounds decide → the min of the `Exact` zone minima; an `Inexact`
   zone min is a **candidate**, and only the zones whose stated min could beat the best exact one
   are decoded — usually one;
3. an ordered source on `path` and a predicate that is a range on `path` → `SeekFirst` /
   `SeekLast` inside the range, `O(r log n)`;
4. otherwise a count-only decode over the live blocks with a running extreme: one pass, one batch
   of memory, and `Explain` says so.

### 5.4 `Distinct` — the keys without the rows

`file.Keys(path).Distinct().OpenAsync()` returns a cursor whose `Next` is `NextKey`: every distinct
non-null value of `path`, in key order, once. Its source is the cheapest key source of §3, which
now includes `Postings` and `Dictionary`: a `GROUP BY status` over a billion rows reads the
postings keys of each run — a few hundred bytes per chunk — and never a data segment. On a
`SortedColumn` it is a run-length walk of the zones; on `SortedRuns`, the merge with the
duplicates skipped by re-seek.

A column with no key source gets a refusal, not a hash set: the distinct set of an unindexed column
is bounded by the column, and constraint 2 forbids it. The refusal names `IndexPolicy.Postings` as
the cheapest structure that would serve, and the caller's alternative is `Scan().Project(path)` and
a table of their own, sized by their own judgment.

`Distinct` is a property of the cursor and not a terminal on the scan because it is an ordered walk
with an early exit, and a terminal returning a list would buffer it.

**As delivered (step 15).** A `Distinct()` cursor steps by `NextKey` and `PrevKey`, and a backward
landing (`PrevAsync`, `SeekLastAsync`, `AtOrBefore`, `Before`) is moved to its key's first entry,
so `Row` is always the key's first occurrence on a source with rows. The choice for a distinct walk
is `SortedColumn`, `Postings`, `Dictionary`, `SortedRuns`; for rows it stays `SortedColumn`,
`SortedRuns`, and the key-only sources are rejected by name ("only a Distinct() cursor takes
them"). A key-only source forced on a cursor without `Distinct()` is an
`InvalidOperationException`; a distinct walk with no source is refused naming
`IndexPolicy.Postings`. The two key-only sources are the sorted-runs merge with the run's ordinal
standing in for the row — keys are unique within a run, so `(key, ordinal)` is still a total order —
and `HasRows` false: `Row`, `RankAsync`, `SeekRankAsync` and `KeyCountAsync` throw
`InvalidOperationException`, and `EntryCount` is null, since a key in two chunks is two entries.
`Postings` reads only the keys array of each segment (the offsets and blocks are the pruner's), and
a walk over it reads no data segment — the test holds every request against the footer's segment
specs. `Dictionary` finds the column's flat chunks under its struct field (through zoned wrappers),
reads each chunk a `dict.probe` run claims, checks its root is `vortex.dict`, decodes the values
child alone, drops the nulls, sorts in the total order and deduplicates; the runs are held by the
source rather than the run cache, since the merge needs every head from its first seek. A claimed
chunk that is not a dictionary refuses the source, and an `Explain` that has already chosen a
cheaper source does not open the dictionaries just to reject them. Case-folded postings
(`ngram3` or `CaseInsensitive`) are refused: their keys are not the column's values.

**As delivered (step 10c, `MinAsync` / `MaxAsync`).** `TerminalScan.ExtremeAsync` takes §5.3's
resolutions in order, the third excepted until an ordered source exists. The file statistic
answers when the scan is the whole file and the statistic is `Exact`, with no read. The column's
zone map — read through the pruning plan on `IS NOT NULL(path)`, so its cost lands in the sink like
any structure's — answers for every split that is whole zones of it and whose rows all qualify (no
filter, or one `MustMatch` proves for the split): an `Exact` bound is the split's answer, an
`Inexact` one a candidate, and the candidates are decoded best-bound-first, stopping at the first
whose bound cannot beat the best, which is usually one decode. Everything else is decoded: the
filter evaluated, its true rows selected without a gather, the extreme found among them by
`Extremes` — which remembers the winning row and reads one `FilterLiteral` per split — in the
filter's order: IEEE for floats, NaN skipped by name and never an extreme, `−0.0 == 0.0`. Each
decode hands the readers a mask of its one split (`BlockMask.KeepOnly`) so that the restricted
decode of 11 §6.1 materializes the split, not the chunk. `Rows`, `Take` and a filter push every
split they touch to the decode unless the bounds decide it whole. The test of the `Inexact` bounds
found that `vortex.bounded_max(64)` — the reference's `{bound, unknown}` struct — had never been
read: string predicates pruned and proved from the minimum alone; `ZonePruningPlan` reads both
shapes now.

---

## 6. Key-ordered delivery from the scan

*Amended by [13-dataset.md](13-dataset.md) §6.6: across the objects of a dataset, key-ordered
delivery on the clustering key is a k-way merge of at most 8 + L cursors, bounded; on any other
column it is output-sensitive, one cursor per object the summaries cannot refute.*

`ScanBuilder.InKeyOrder(string path, bool descending = false)` makes the scan deliver its rows in
the key order of `path` instead of file order. It composes with `Project` and `Where` and is
mutually exclusive with `Rows` and `Take`, as those two are with each other
(`ScanBuilder.cs:121,217`).

**How it works.** The scan is driven by a cursor instead of the split planner. The cursor's range
comes from the filter: the tightest `[lo, hi]` over `path` among the top-level `AND` conjuncts —
comparisons, `IN` as its min and max, `StartsWith` as a range (§7) — the extraction the pruner
already performs; no `KeyRange` type exists on the surface, because `Where` already says it. The
cursor walks a **window** of entries — `WithMaxBatchRows` rows, the natural batch size by default —
collects their rows, skips the entries whose block the mask has pruned (the other conjuncts prune
as they do today), takes the survivors as a `RowSelection` through the push-down
`ExecuteWithTake` already implements (`BatchAsyncEnumerable.cs:446-486`), evaluates the filter on
the gathered rows, and **permutes the batch into the window's key order** with the gather
`CanonicalFilter.Apply` already performs for a filter (`BatchAsyncEnumerable.cs:518`), fed the
permutation instead of a selection. Then the next window. Memory is one window of rows plus one
split's decode, never more: the whole result is never buffered, which is the objection `Take`
raises against reordering (`ScanBuilder.cs:197-199`), met by bounding the reorder to a window.

**What it delivers.** Batches of at most the window, each in key order, consecutive batches in key
order, ties in row order (§4.4), `descending` the exact reverse. A `LIMIT` is the consumer stopping
the enumeration; the windows past it are never read. A window whose rows are all rejected by the
filter produces no batch, as `FilteredBatches` does today. `WithDegreeOfParallelism` applies
within a window — its splits are independent — and windows stay sequential, so batches are still
delivered in order (09 §2).

**What it costs**, and this is the paragraph a consumer must read before using it. A window of `W`
entries touches as many splits as its rows are scattered over. On a `SortedColumn` the rows of
consecutive keys are consecutive: a window is a contiguous read and `InKeyOrder` costs a plain
scan. On a `SortedRuns` index over a column **uncorrelated with file order**, a window of 8 192
entries can touch 8 192 splits, and a scattered take costs what [90-registry.md](90-registry.md)
measures — 64 rows over 64 splits of 1 024 at 0,34× a full scan of the file (`TakeBenchmarks`,
452 µs against a 1,3 ms scan), about 7 µs per scattered row on that file. So `InKeyOrder` is the
right tool for a **selective** range, a top-k, a merge-join probe, and the wrong tool for a whole
uncorrelated column, where a sort of the scan's output by the consumer costs less. `Explain()`
reports the source, the runs the range overlaps and the entry-count bound before execution;
`ScanMetrics` reports the splits each window touched after it; neither refuses, because the
operation is correct and the caller asked for it (§13 keeps a cap as an open question).

**As delivered (step 14).** `ScanBuilder.InKeyOrder(path, descending)` compiles the scan as
usual and hands it to `KeyOrderedBatches`, which opens the key source at the first
`MoveNextAsync` (a source-less column throws `VortexUnsupportedException` there, naming
`IndexPolicy.SortedRuns`). The range is `ExactCover.RangeAsync`: every entry, intersected with the
slices of each top-level `AND` conjunct that tests the key alone — so an `OR` or an `IN` on the key
narrows to its exact slices rather than to its min and max. The window is the batch cap; its rows
are sorted and deduplicated into one `RowSelection` the scan reuses (`RowSelection.Over`), each
touched split is found by `SplitPlan.SplitOf`, and the permutation is fed to `CanonicalFilter.Apply`,
whose gathers are positional — and skipped when it is the identity, which is every ascending
window of a sorted column with no row filtered out. With `WithDegreeOfParallelism(n)` the window's
splits are cut into `n` contiguous groups decoded on their own contexts, and the lanes' roots are
referenced (`CanonicalArena.ReferenceFrom`) into the scan's context for the concatenation; the lanes
are reset with the batch that borrows from them. Rows with a null key are delivered by no source
and so by no key-ordered scan. `ScanMetrics.Windows` and `WindowSplits` count the windows and the
splits they touched. A window allocates its `RecordBatch` when it reads its splits whole; a window
across a split boundary pays what the take push-down pays per partial split on that column's
encoding. `Explain` reports the source, the runs the range reaches and their entries since step 18
(`ScanPlan.Order`). Measured on 65 536 rows
in 64 splits (`--ratio-check`): a 1 % band of a sorted column in key order at **0,70×** the
reference's filtered scan; 64 rows of an uncorrelated column at **1,40×** the reference's take of
64 rows over 64 splits; the same band counted by the exact cover at **0,79×** the reference's scan.

**A second caller since 13's step 41b: compaction.** A dataset's k-way merge reads each of its
inputs through this, which is what 13 §5.3 asks for by name ("the permuted read `InKeyOrder` already
performs"). Two properties stated above become load-bearing there rather than advisory. The first is
that it drives **one** column: a dataset whose clustering key is composite has no permuted read, the
composite key source of §4.6 serving cursors only, and its compaction is refused rather than ordered
on the leading column. The second is that a row whose key is null is in no source and is delivered by
no key-ordered scan: for a merge that rewrites objects, that is not a filter but a silent row loss,
so a key column with nulls is refused up front and the rewritten row count is checked against the
inputs' at the end. Both are gaps of this section, not of the dataset's, and closing the first is
what would let a composite-keyed dataset compact.

**A third caller since 13's step 39d: the dataset's own `InKeyOrder`.** `DatasetScanBuilder.InKeyOrder`
runs this over each object and merges the results, so the contract above becomes the dataset's: it
drives one column, a row whose key is null is not delivered, and it excludes `Rows`. Two properties
travel up by construction. Ties come in row order within a file, and across objects in the dataset's
order, so a descending read is still the exact reverse of an ascending one. And a `Select` that leaves
the key out is honoured: the merge compares rows by the key, reads it on top of the selection and
drops it with `RecordBatch.Project` before a batch goes out.

**As delivered (2026-09-18, closing debts 1 and 2 of the plan): both gaps named above are closed.**
- **Null keys come last, in both directions.** A row whose key is null is delivered after every
  keyed row: in row order ascending, in reverse row order descending. No source holds it, so the
  scan reads those rows once the walk is done, in file order, as a filtered scan under
  `IsNull(key) AND filter`; the zone maps' null counts prune every block without one. Descending,
  the splits go last first and each batch is reversed. A split is at most one batch, so memory stays
  one batch.
- **Why last, in both directions.** A merge of files in key order encodes its keys with nulls last to
  match, which is not the row encoding's own default (nulls first). A non-null key encodes to the
  same bytes either way, so the merge's bounds are unchanged. Taking the encoding's default instead
  was the first run, and a test caught it: a null head went out before another object's largest keys.
- **Composite keys: `InKeyOrder(IReadOnlyList<string> paths)`.** It walks the composite run (§4.6,
  `WritePolicy.ForKey`), whose bytewise order is exactly what a merge across files compares. The
  filter prunes and filters but does not narrow the walk, since its conjuncts name columns and the
  walk is over tuples. `Explain` names the key `(a, b)`. A file with no run for the tuple is refused,
  naming `ForKey`.
- **What the composite path still does not deliver.** A row whose tuple holds a null is in no entry
  and is not delivered. The row encoding sorts it inside its leading column's group, not after every
  keyed row, so it cannot come last as a single column's null does without sorting every such row in
  memory. The dataset's compactor refuses such a tuple by name; nothing else moves it.
- The tests:
  - the oracle table of `SortedRunsCursorTests` gains the null tail, including a descending case
    with lanes and windows of 100;
  - `KeyCursorTests` holds nulls last in both directions;
  - `CompositeKeyTests` walks the tuple with a filter on a third column, in both directions, with
    lanes and windows.

---

## 7. The expression model gains three predicates

`ExprKind.StringMatch`, node `StringMatchExpr { FieldExpr Field; StringMatchOp Op; FilterLiteral
Pattern }`, operators `StartsWith`, `Contains`, `Like`, built by `Expr.StartsWith(field, literal)`,
`Expr.Contains(field, literal)` and `Expr.Like(field, pattern, escape = '\\')`. Bytes only: the
column must be utf8 or binary, or an extension over those, and the literal `Bytes`; a wrong domain
throws at construction, as the builder already does for a column-to-column comparison
(`VortexExpr.cs:404-421`). Case-sensitive and bytewise; case folding is not in this iteration
(§13), and 10 §5.2's `ILIKE` option bit on the n-gram Bloom waits for it.

| op | true when | null | evaluation | pruning |
|---|---|---|---|---|
| `StartsWith(p)` | the value's bytes begin with `p`; every value begins with the empty pattern | `unknown`, as a comparison (08 §3) | `MemoryExtensions.StartsWith`, vectorised in the BCL | **as a range**: `x ≥ p AND x < succ(p)`, where `succ(p)` is `p` with its last byte incremented after trailing `0xFF` bytes are dropped, and no upper bound when `p` is empty or all `0xFF`. Zone min/max prune it (the bounded string statistics of 11 §10), a `SortedRuns` slice answers it exactly, the Bloom makes no claim |
| `Contains(p)` | `p` occurs in the value; the empty pattern always | `unknown` | `MemoryExtensions.IndexOf`, vectorised | the n-gram Bloom (10 §5.2) and the n-gram postings (10 §6.4) when the pattern has three bytes or more; otherwise no claim |
| `Like(pattern)` | SQL `LIKE`: `%` any run, `_` any one **byte**, `escape` quotes either | `unknown` | a backtracking matcher over bytes, greedy `%`, linear on patterns without adjacent wildcards; `_` matches a byte, not a code point, and the documentation says so | the leading literal run, when the pattern does not begin with a wildcard, prunes as `StartsWith`; every literal run of three bytes or more prunes through the n-gram structures; the pattern is always re-evaluated on survivors (`Superset`) |

`FilterEvaluator` gains one case, `ComparisonKernels` one kernel family over `VarBinView` —
inline views of 12 bytes or fewer compared in place, out-of-line views through their buffer, the
shape the byte comparisons already have — and `ZonePruner` one rewrite, `StartsWith` to a range.
`NOT` pushes through as it does for comparisons: `NOT StartsWith` claims nothing from a zone map,
which is the safe answer. This is exactly the set 10 §6.6 names and nothing more; `EndsWith`,
regular expressions and case folding are §13.

*As delivered (step 28b): a fourth predicate, the one a Bloom filter over a list's elements
answers (10 §5.1).* `ExprKind.ListContains`, node `ListContainsExpr { FieldExpr Field;
FilterLiteral Value }`, built by `Expr.ListContains(list, value)`: the reference's
`vortex.list.contains` with a constant needle.

| true when | null | evaluation | pruning |
|---|---|---|---|
| an element of the row's list equals the value, under the comparison kernels' equality (IEEE for floats; a NaN matches nothing, the two zeros match each other) | a null list is `unknown`, and so is every row under a null value; a null element matches nothing and leaves the row `false`, as the reference gives the result the list's validity alone; an empty list is `false` | `ListKernels`: the elements the batch's valid rows name are compared once, over their window only, since a batch shares its chunk's elements whole; each row then looks for a match in its own range | the list's null count: a zone of null lists holds no row for the predicate nor for its negation. The list's zone map describes list values, so its bounds are never read. The Bloom filter over the elements proves absence per block, the file-level one per file. An equality on a list column claims nothing from that filter |

The column is a list or a fixed-size list of booleans, numbers or bytes, or an extension over
one. Any other column throws `NotSupportedException` when the filter first runs, as a comparison
does.

---

## 8. The surface a consumer sees

### 8.1 Listing

| where | member | answers |
|---|---|---|
| `VortexFile` | `MayMatch(VortexExpr)` (10 §5.4) | might this file hold a match: file statistics and the file-level Bloom, no scan |
| `VortexFile` | `Indexes` (10 §7) | what the file carries, for tooling |
| `VortexFile` | `Keys(string path)`, `Keys(params string[] paths)` — an extension method, as `Scan()` is, so that file-open does not depend on the read path | a `KeyCursorBuilder` |
| `KeyCursorBuilder` | `Distinct()` | keys once each; admits the key-only sources |
| `KeyCursorBuilder` | `WithSource(KeySourceKind)` | forces a source, for tests and for a caller who measured; refuses when it is absent |
| `KeyCursorBuilder` | `Explain()` → `KeyPlan` | the source chosen, its runs, `EntryCount`, and why the others were not chosen |
| `KeyCursorBuilder` | `OpenAsync(ct)` → `KeyCursor` | §4 |
| `ScanBuilder` | `Where`, `Project`, `Rows`, `Take`, `WithMaxBatchRows`, `WithPruning`, `WithDegreeOfParallelism`, `ExecuteAsync` | unchanged |
| `ScanBuilder` | `WithIndexes(bool)` (10 §6.6) | the index chain on or off: the equivalence switch |
| `ScanBuilder` | `InKeyOrder(string path, bool descending = false)` | §6 |
| `ScanBuilder` | `AnyAsync(ct)`, `CountAsync(ct)`, `MinAsync(path, ct)`, `MaxAsync(path, ct)` | §5 |
| `ScanBuilder` | `Explain()` → `ScanPlan` (11 §6.4) | splits, blocks pruned per structure, rows an exact index selected, the count tier each block would take, the order source and its runs |
| `ScanMetrics` (11 §6.4) | after execution | the same, measured; plus windows and splits per window under `InKeyOrder` |
| `Expr` | `StartsWith`, `Contains`, `Like` | §7 |
| `Vorticity.RowEncoding` | `RowEncoder.EncodeKey(values, fields)` | the seek key of a composite cursor (§4.6) |
| `EventSource` `Vorticity` (09 §5) | `index-runs-read`, `cursor-seeks`, `cursor-steps`, `count-blocks-proven`, `count-blocks-decoded` | whether an index earns its bytes, in production |

**As delivered (step 18).** `ScanBuilder.ExplainAsync` fills `RowsSelectedByIndex` (the exact
cover's rows when they fit a batch and the scan is not narrowed by `Rows` or `Take`), a `Count` plan
(`CountPlan`: whether an exact cover answers and its count, then the splits the mask prunes, the
zone maps prove and the decode is left with — the three tiers `CountAsync` takes, decided by the same
predicates) and, under `InKeyOrder`, an `Order` plan (`OrderPlan`: the source, its runs, the runs the
range reaches — found by selecting the two end keys of each slice and probing every run —, its
entries and the entries the range admits, which bounds the rows the windows gather). `ScanMetrics`
has `Windows` and `WindowSplits` since step 14. The `EventSource` is 09 §5's as-delivered note;
`vxdump --explain` prints the plan and the count tiers.

**As delivered (step 27): `VortexFile.Indexes`.**
- **What it returns.** A list of `VortexIndexInfo`, one per index the reader kept: kind, column (a
  dotted path, or a composite key's columns in parentheses), block length, runs, blocks covered,
  entries, the bytes of the regions the directory lists, and a `VortexIndexLayout` (`None`,
  `Listed`, `FencePages`, `FilterTree`). The layout says whether more lies below the listed bytes.
- **When.** It is `null` until the directory has been read — by a scan, by
  `ReadIndexesAsync`, or at the open under `VortexOpenOptions.PreloadIndexes` — and costs no
  request after.
- **Where it shows.** `vxdump --indexes` prints it.

Not on the surface, on purpose: a `KeyRange` type (`Where` says it, §6); a `Probe(column, value)`
(`AnyAsync` is it, §5.1); a `DistinctAsync` returning a list (§5.4); a key-ordered `Take` (the
window inside `InKeyOrder` is the only reorder, §6); a `Sort` (constraint 2). Each would be a
second way to say something one member already says, or a way to buffer a file.

### 8.2 Worked examples

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path, ct);
FieldExpr id = Expr.Field("id");
FieldExpr ts = Expr.Field("ts");
LiteralExpr fortyTwo = Expr.Literal(FilterLiteral.From(42L));

// Point lookup: rows, exact, the predicate not re-evaluated under an Exact cover.
await foreach (RecordBatch b in file.Scan().Where(Expr.Eq(id, fortyTwo)).ExecuteAsync()) { /* ... */ }

// Membership: no rows.
bool present = await file.Scan().Where(Expr.Eq(id, fortyTwo)).AnyAsync(ct);

// Count pushed into the structures: exact cover, then full-block proofs, then a count-only decode.
long since = await file.Scan().Where(Expr.Ge(ts, Expr.Literal(FilterLiteral.From(t0)))).CountAsync(ct);

// Successor and predecessor.
await using KeyCursor c = await file.Keys("ts").OpenAsync(ct);
if (await c.SeekAsync(FilterLiteral.From(t), SeekOp.After, ct))  { long row = c.Row; FilterLiteral next = c.Key; }
if (await c.SeekAsync(FilterLiteral.From(t), SeekOp.Before, ct)) { /* the predecessor */ }

// A range in key order with an early exit: the windows past the break are never read.
await foreach (RecordBatch b in file.Scan()
    .Project("id", "ts")
    .Where(Expr.And(Expr.Ge(ts, Expr.Literal(FilterLiteral.From(t0))),
                    Expr.Lt(ts, Expr.Literal(FilterLiteral.From(t1)))))
    .InKeyOrder("ts")
    .WithMaxBatchRows(1024)
    .ExecuteAsync())
{ /* ... */ if (enough) { break; } }

// A prefix on a composite key: bytes from the row-encoding package, a bytewise walk in core.
byte[] fr = RowEncoder.EncodeKey([FilterLiteral.From("FR")], [RowSortField.Ascending]);
await using KeyCursor k = await file.Keys("country", "city").OpenAsync(ct);
for (bool ok = await k.SeekAsync(FilterLiteral.From(fr), SeekOp.AtOrAfter, ct);
     ok && k.KeyBytes.StartsWith(fr);
     ok = await k.NextAsync(ct))
{ /* k.Row */ }

// Distinct values in order from the postings keys: no data segment read.
await using KeyCursor d = await file.Keys("status").Distinct().OpenAsync(ct);
for (bool ok = await d.SeekFirstAsync(ct); ok; ok = await d.NextAsync(ct)) { /* d.KeyBytes */ }

// Group counts from a row cursor: one seek per group, one rank difference per count.
await using KeyCursor g = await file.Keys("status").OpenAsync(ct);
for (bool ok = await g.SeekFirstAsync(ct); ok; ok = await g.NextKeyAsync(ct))
{
    Report(g.Key, await g.KeyCountAsync(ct));
}

// A merge join of two files on id: two cursors, advance the smaller.
await using KeyCursor l = await left.Keys("id").OpenAsync(ct);
await using KeyCursor r = await right.Keys("id").OpenAsync(ct);
bool lo = await l.SeekFirstAsync(ct), ro = await r.SeekFirstAsync(ct);
while (lo && ro)
{
    int cmp = KeyCursor.Compare(l.Key, r.Key);
    if (cmp < 0)      { lo = await l.NextKeyAsync(ct); }
    else if (cmp > 0) { ro = await r.NextKeyAsync(ct); }
    else              { /* join the two groups by Row */ lo = await l.NextKeyAsync(ct); ro = await r.NextKeyAsync(ct); }
}
```

### 8.3 Contracts

- **Thread-safety** (09 §1 gains three rows): `KeyCursorBuilder` and `KeyCursor` are not
  thread-safe, like `ScanBuilder`; the run cache on `VortexFile` is thread-safe, like the layout
  tree; `KeyPlan` and `ScanPlan` are immutable records.
- **Lifetime**: `KeyBytes` is valid until the next positioning call. A cursor over a
  `SortedColumn` holds a `ScanContext` for its zone decodes and returns it on `DisposeAsync`; a
  cursor over runs holds references into the run cache and releases them.
- **Cancellation**: honoured at every positioning call, before and after its read, never inside a
  binary search — the granularity 03 §1 fixes for the batch, applied to the step.
- **Errors**: a literal of the wrong domain is `ArgumentException`; no source is
  `VortexUnsupportedException(kind, "index")` naming the policy; a forged run is
  `VortexFormatException` for every Class I field (row positions below `RowCount`, segments inside
  the file, 10 §4.1) and an ignored entry for the rest (10 §4.1: an unusable entry never fails the
  file — a cursor opened on an ignored entry gets the next source, or the refusal).
- **Versioning** (09 §3): every addition is additive and minor. `SeekOp` and `KeySourceKind` may
  grow; a consumer switching over them keeps a default arm.
- **Rust**: none of this is visible to a Rust reader, because the file is unchanged (10 §3.1).
  `bench/crosscheck.sh` runs as it does.

---

## 9. Inside the reader

What the implementation adds, so that its shape is decided here rather than discovered:

- **`IKeySource`** (internal): `RunCount`, `Bounds(run) → (min, max, entryCount)`,
  `Locate(run, key, op) → position`, `Entry(run, position) → (key, row)`. Four implementations,
  one per source of §3. The composite source is the `SortedRuns` one with a bytewise comparator;
  the `Dictionary` one sorts each values child at open into a permutation it owns.
- **`MergeCursor`** (internal): a binary heap of `(run, position)` ordered by `(key, row)`, sized
  at open to the source's run count and reused across seeks, so a step allocates nothing. A seek
  filters runs by `Bounds`, positions the survivors, heapifies. `Next` pops, advances, pushes.
  A direction flip re-seeks.
- **The run cache** on `VortexFile` (11 §6.3): decoded key columns and postings, keyed by run
  segment, loaded on first use, shared, thread-safe, **detached** from any scan arena as
  `ZoneBounds` already is (`ZonePruningPlan.cs` header), because batch arenas reset at every
  boundary. A run's decoded keys can be a chunk's rows, so the cache has a budget:
  `VortexReadOptions.IndexCacheBytes`, default 64 MiB, LRU by run — a cap in the sense of 08 §6,
  and §13 lists its default as a guess. `VortexOpenOptions.PreloadIndexes` (11 §6.3) fills it at
  open for object stores.
- **`ZonePruner.MustMatch(RowRange)`** beside `MayMatch`, with the rules of §5.2, and a
  count-only execute path in `BatchAsyncEnumerable` that stops after `Trilean.CountTrue` and
  builds no `RecordBatch`.
- **A window driver** beside `SplitCursor` for `InKeyOrder`: per window a `RowSelection` built
  from the cursor's rows and the permutation that maps key order onto the gathered batch, applied
  with `CanonicalFilter.Apply`; the filter and the trim run as today on the permuted batch.
- **`StringMatchExpr`**, its kernel family and its pruner rewrite (§7).
- **`ScanPlan` and `KeyPlan`**, the outputs of `Explain`, and `ScanMetrics`, filled by the
  driver; the `EventSource` counters of §8.1.

Nothing in the list touches a decoder, a layout reader, the writer, or a byte of the format.

**As delivered (step 13, the `SortedRuns` source).** The source/merge split is folded into one
internal `KeySource` per source, because the two sources that deliver rows are too unlike for it
to pay: `SortedColumnWalker` is the contiguous walk of step 11b, and `SortedRunsSource` is the
merge. The merge has **one primitive**, "the first position of a run at or after `(key, row)`",
and everything is that primitive with a row of `long.MinValue`, `long.MaxValue` or a real row:
the five operators, the re-seek of a direction flip (forward past `(k, r)` is `(k, r + 1)`,
backward before it is `(k, r)`), `rank` as the sum over runs, `KeyCount`, and `select`, which
bisects each run in turn with the entry's rank computed against the others — `O(r² log² n)`,
waiting for a measurement to ask for better. A run whose `[min, max]` excludes the key costs no
read; inside one, only the segment the search lands in is decoded (keys, then rows relative to
the run's first row), copied out of the arena into a `RunSegment` and kept in the file's
`IndexRunCache`, an LRU bounded by `VortexReadOptions.IndexCacheBytes` (64 MiB by default, `0`
keeps nothing) with inserts that lose a race returning the first. The order is the total one of
§4.4, so the two zeros are two keys and the NaNs the two ends. A run that does not make sense —
a segment table that does not match its payload, bounds of the wrong width, more entries than its
blocks have rows — refuses the whole source, since a walk missing a run would be wrong rather
than slow; a payload that decodes to the wrong shape or names a row outside its run is a
`VortexFormatException`. `KeyCursorBuilder` takes the sorted column first and the runs second and
says why in `Explain`; `KeyCursor` keeps the argument rules and the lifetime and delegates the
walk.

**The exact cover** (§5.1, §5.2's first tier, §5.3's third resolution, and 10 §6.6) is
`Keys/ExactCover`: a predicate whose every leaf is a comparison, `IN` or `StartsWith` on one
column that an exact source serves becomes disjoint slices of rank space — `AND` and `OR` their
intersection and union — computed without reading a data segment. The IEEE meaning is built into
the bounds: `x = 0.0` runs from `−0.0` to `+0.0`, a range stops short of the NaNs, a NaN literal
selects nothing, and `!=` on a float declines. `CountAsync` is the sum of the slices, `AnyAsync`
its sign, `MinAsync` / `MaxAsync` of the covered column a seek to either end; under `Rows` or
`Take` the slices' rows are walked and intersected, up to a batch of them. A filtered scan whose
cover holds at most a batch's worth of rows reads them as a take and evaluates nothing — the
enumerator only trims the filter's columns — which is 10 §6.6's row selection; past that, the
block-mask chain of step 12c prunes as before.

---

## 10. Costs, honestly

`n` entries per run, `r` runs overlapping the key range, `W` the window, `z` zones. Measured
numbers come from [90-registry.md](90-registry.md); the rest are complexities to be measured at
each staging step (§12).

| operation | I/O | CPU | note |
|---|---|---|---|
| seek, `SortedRuns` | the key column of each overlapping run, once, then cached | `O(r log n)` | one run per chunk on a file written once; a rewrite compacts them (10 §4.2) |
| seek, `SortedColumn` | the zone map, once; one zone | `O(log z)` then `O(log 8 192)` | on any file with `is_sorted` |
| step | none inside a loaded run | `O(log r)` | zero allocation |
| direction flip | none | one seek | |
| `NextKey` | none | one seek | one per group, not one per row |
| rank, `KeyCount` | none once loaded | `O(r log n)` | |
| select | none once loaded | `O(r log² n)` | |
| `Any`, exact cover | none | `O(r log n)` | |
| `Count`, exact cover | none | `O(r log n)` | |
| `Count`, full-block proofs | the zone map | `O(blocks)` | |
| `Count`, decode | the live splits | one evaluation per live block, no gather, no copy | |
| `Distinct`, postings or dictionary | the keys of each run, a few hundred bytes per chunk | `O(r log n)` per key | never a data segment |
| `InKeyOrder`, sorted column | a contiguous read per window | a plain scan plus one permutation per window | |
| `InKeyOrder`, uncorrelated column | up to `W` splits per window | a scattered take: **0,34× a full scan for 64 rows over 64 splits of 1 024** (452 µs against 1,3 ms), about 7 µs per scattered row | selective ranges only |

*Since step 22 ([13-dataset.md](13-dataset.md) §6.1), `r` is at most 4 for a sorted-runs or
postings entry, whatever the number of chunks: one merged run, the last chunk's own, and what
appends added before the K rule merged them. A seek reads one segment per run it reaches.*

The two numbers that decide whether a consumer should use `InKeyOrder` — the splits a window
touches and the entries in the range — are exactly what `Explain` and `ScanMetrics` report.

---

## 11. Tests and acceptance

- **Seek semantics**, `KeyCursorSeekTests`: generated files per dtype of §4.4 and per source of §3
  (`SortedRuns` forged by `tools/conformance-gen` as 10 §10 does, `SortedColumn` written with
  `is_sorted`), keys with duplicates, absent keys, keys at both ends, the five operators, `Next`
  and `Prev`, a direction flip after each, `NextKey` and `PrevKey`; the oracle is an in-memory
  list of `(value, row)` sorted by §4.4.
- **Walk equivalence**, `KeyCursorEquivalenceTests`: a full walk equals `OrderBy(value).ThenBy(row)`
  of the materialized column, on every corpus file whose statistics say `is_sorted`, on the forged
  index fixtures, and on files written once and appended in two, three and eleven pieces (10 §10),
  which is where `r > 1`.
- **Rank and select**, `RankSelectTests`: `rank(select(i)) ≤ i < rank(select(i)) + KeyCount`;
  `select(rank(k))` has key `k` when `k` is present; `KeyCount` equals the materialized count.
- **Terminals**, `ScanTerminalTests`: `Any`, `Count`, `Min`, `Max` against the materialized
  result, with indexes on and off, pruning on and off, and each count tier forced off in turn
  through an internal switch; `Min` / `Max` on columns with NaN, `−0.0`, all nulls, and `Inexact`
  zone bounds.
- **Key order**, `KeyOrderTests`: `InKeyOrder` equals `OrderBy` of the filtered scan's output, at
  windows of 1, 7, 1 024 and the natural size, ascending and descending, with a filter on another
  column, with a projection; an early exit reads no segment past its window, counted the way
  `TakeTests.ScatteredRowsReadFarFewerSegmentsThanTheWholeFile` counts.
- **String predicates**, `StringMatchTests`: the evaluator against a naive reference over every
  corpus string column and generated adversarial patterns — empty, longer than the value, all
  `0xFF`, non-ASCII, escapes, `_` across a multi-byte code point — and the pruning equivalence with
  `WithPruning(false)` and `WithIndexes(false)`; `succ(p)` has its own unit table.
- **Lying runs**, the fuzzer: forged runs that are unsorted, keys outside their stated min/max,
  rows at or beyond `RowCount`, overlapping runs. Class I fields throw `VortexFormatException`
  before any read out of bounds; Class II lies produce a wrong order and never a fault, and
  `VerifyStatistics` catches them.
- **Allocation**, `ScanAllocationTests`: a row per addition — a cursor step reading `KeyBytes` at
  0 B, a count at 0 B per block, an `InKeyOrder` window at the filtered-batch figure plus its
  rented permutation.
- **Cost ratchets**: `TakeBenchmarks` gains two `InKeyOrder` axes, a sorted column and an
  uncorrelated one, with the full-scan figure as the guard that the window driver slows nothing
  else, the way the FSST row of [90-registry.md](90-registry.md) guards its dense path; a
  `CountBenchmarks` axis, exact cover against the scan; ceilings in `--throughput --check`
  ([05-benchmarks.md](05-benchmarks.md)).
- **Rust**: unchanged files, unchanged `bench/crosscheck.sh`.

---

## 12. Staging

After step 8 of 11 §8, the read contract. Every step is a measurable commit with its allocation row
and its gate; the first four need no index and run on today's corpus.

1. **String predicates** (§7): the expression model, the kernels, the pruner rewrite.
2. **`MustMatch` and the count-only path**, then `AnyAsync`, `CountAsync`, `MinAsync`, `MaxAsync`
   on zone maps and statistics alone.
3. **`SortedColumn` source and `KeyCursor`** with the merge over one source; `Compare`.
4. **`SortedRuns` source over forged runs**, before the writer builds real ones; `EntryCount`,
   rank, select, `KeyCount`; the run cache and its budget.
5. **`InKeyOrder`**: the window driver, the permutation, the two benchmark axes.
6. **`Postings` and `Dictionary` sources; `Distinct()`.**
7. **Composite keys**: `EncodeKey` in the row-encoding package; the bytewise cursor.
8. **`Explain`, `ScanMetrics`, the `EventSource` counters**; the rows 09 §1 and §5 gain.

---

## 13. Open questions

- **Where the composite index builder lives.** 10 §6.5 keys a composite index by the row encoding,
  and the row encoding is a separate `0.x` package the core must not reference (09 §3). Proposed:
  an `IKeyEncoder` slot on `VortexWriteOptions` that the `Vorticity.RowEncoding` package fills,
  so the core writes what it is handed and depends on nothing. This is an amendment to 10 §6.5,
  and the reader here needs none of it. **Decided and delivered at step 16** as proposed:
  `VortexWriteOptions.KeyEncoder`, `IKeyEncoder`, `RowKeyEncoder`.
- **Whether `InKeyOrder` should refuse above a splits-per-window ratio.** Recommended no: it is
  correct, the caller asked, `Explain` and `ScanMetrics` say what it cost. Reopen on the first
  report of a consumer who did not read §6.
- **Whether `Auto` builds `SortedRuns`.** 10 §5.5 starts every *cheap* builder, and a sort per
  chunk on every column is not cheap. Recommended: never under `Auto`; an explicit
  `IndexPolicy.SortedRuns` per column, and the refusal of §3 names it. 10's decision.
- **`Distinct` without a source.** A bounded hash set under a cap would serve small columns.
  Recommended no, on constraint 2: the cap would be a guess and the refusal is honest.
- **`EndsWith`, regular expressions, case folding.** Not now. `EndsWith` would want a reversed key
  column, a regular expression a different evaluator, and case folding a definition of "case" for
  UTF-8 that 10 §5.2's lower-cased trigrams also need.
- **The selection algorithm.** `O(r log² n)` is adequate until measured otherwise.
- **`IndexCacheBytes` at 64 MiB** is a guess, like every default in 10 §11, to be calibrated on
  `table_mixed` once real runs exist. **Calibrated at step 33, and it is not what it was blamed
  for.** On `table_mixed` rewritten with sorted runs, one process, best of five, planning an `IN`
  of a thousand keys:

  | column | index bytes | cache 0 | 1 MiB | 8 MiB | 64 MiB | 256 MiB |
  | --- | --- | --- | --- | --- | --- | --- |
  | `key`, a sequence, every literal present | 4 854 | 28,3 ms | 24,7 | 24,0 | 23,6 | 23,2 |
  | `measure`, random, every literal absent | 5 127 836 | 1,7 ms | 1,6 | 1,6 | 1,7 | 1,6 |

  From nothing to a quarter of a gigabyte the plan moves by 18 % on the worst shape and not at all
  on the other, and a file kept open does not get faster across rounds. **64 MiB stays**, not
  because it buys speed but because it bounds what one open file holds: a sorted run's segment for
  a million rows is 12 MB of keys and rows, so the default holds a handful and an unbounded cache
  would hold the file. Step 32 left ~39 ms of an `IN (1000)` to "the two rank lookups a literal
  makes in the key source (the run cache, `IndexCacheBytes`)"; that attribution was wrong. The cost
  was **per block, not per read**: `ProvesAbsent` asked the column whether each literal was absent
  from each block, and finding the literal's slot walked the filter's list — 123 blocks × 1 000
  literals × ~500 comparisons, 61 million of them, for a plan that took 28,2 ms of which the scan
  it guards is 0,11. The slots are now found once per expression and read per block; `bench/ab.sh`
  on `prune-in` (200 literals, in one process, 21 rounds) says **0,836** [0,823 ; 0,860] with
  `lookup-sorted-runs` unchanged at 1,004. What is left grows with the literals because each one
  descends the fences of every run it may be in, which is the design of §6.

Decided (2026-09-15): two models rather than one, with the scan as the only engine that returns
rows and the cursor as the only structure that returns order; no `KeyRange`, no `Probe`, no `Sort`
on the surface; the core byte-keyed for composite keys; nulls in no cursor; the float key order of
§4.4; refusal over emulation whenever an answer would need the column in memory.

---

## 14. Amendments to 10 and 11

Small, and listed so that neither document drifts from this one:

| document | section | amendment |
|---|---|---|
| 10 | §4.1 | `Run` gains `uint64 entry_count = 5`, the entries of a locating run, so that `EntryCount` and `Explain` cost no payload read |
| 10 | §6.2 | the key order per dtype of §4.4 here, entries ordered `(key, row)`, and the IEEE mapping of a range predicate onto the slice |
| 10 | §6.5 | the composite key encoder's home (§13, first question) |
| 10 | §11 | the `Probe` question is answered by §5.1: `AnyAsync` |
| 11 | §6.1 | `MustMatch` beside `MayMatch`; the count-only path; the window driver of §6 |
| 11 | §7.2 | the surface rows of §8.1 |
| 09 | §1, §5 | the thread-safety rows and the counters of §8.3 and §8.1 |

---

## 15. Sources

Cursor semantics, read on 2026-09-15: RocksDB `include/rocksdb/iterator.h` (`Seek`,
`SeekForPrev`, `SeekToFirst`, `SeekToLast`, `Next`, `Prev`, `Valid`) and
`table/merging_iterator.cc` (the heap of child iterators, the re-seek on a direction change);
SQLite `src/btree.c` (`sqlite3BtreeTableMoveto`, `sqlite3BtreeIndexMoveto`, `sqlite3BtreeNext`,
`sqlite3BtreePrevious`) and the `OP_SeekGE` / `OP_SeekGT` / `OP_SeekLE` / `OP_SeekLT` opcodes of
`src/vdbe.c`, which are the five operators of §4.1 minus `Exact`. Loose index scan: the MySQL
reference manual, "GROUP BY Optimization". Selection in sorted arrays: G. N. Frederickson and
D. B. Johnson, *Generalized selection and ranking: sorted matrices*, SIAM Journal on Computing
13(1), 1984. Log-structured runs: O'Neil, Cheng, Gawlick, O'Neil, *The log-structured merge-tree*,
1996, as cited in 10 §12. Vectorised byte search: `MemoryExtensions.IndexOf` and `StartsWith` over
`ReadOnlySpan<byte>` in `dotnet/runtime` (`SpanHelpers.Byte.cs`). Inexact statistics:
`vortex-array/src/expr/stats/precision.rs` at 0.86.1, as quoted in 08 §1.
