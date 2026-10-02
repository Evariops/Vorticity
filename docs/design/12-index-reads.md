# Index reads: cursors, answers without rows, and key order

What a caller can ask of the read path beyond a scan: a cursor that seeks and steps in key order,
counts and extremes computed without materializing rows, and rows delivered in key order. Every
structure these read is one [10-indexes.md](10-indexes.md) defines or one the file statistics
already carry: **nothing on disk changes** for any of it.

Three more constraints, in order:

1. **Memory is bounded by a batch, never by the file.** An operation that would need a whole column
   in memory — a sort, a distinct table over an unindexed column — is **refused**, naming the index
   kind that would serve it, never emulated.
2. **Every answer equals a full materialization's**: a cursor walk equals a sort of the column, a
   count equals the length of the scan's output, with indexes on or off.
3. **No allocation per step or per batch** in steady state
   ([03-architecture.md](03-architecture.md) §4).

## 1. Two models over one payload

**The set model** is the scan: a predicate becomes a block mask, the mask a row selection, the
selection batches in file order ([11-write-strategy.md](11-write-strategy.md) §6). It answers *which
rows*, and it is the right engine for a filter: each touched split is read once and only live blocks
are decoded.

**The order model** is a cursor: position at a key, step to the neighbour. It answers *what is
next*, which no set can. A streaming writer produces sorted runs, never a globally sorted column, so
key order exists inside each run; a cursor over the union is a **k-way merge** over the runs the
key range reaches, the merging iterator every log-structured store has. It is a reader, not a
format.

| model | entry point | answers |
|---|---|---|
| set | `file.Scan<TRecord>()` | which rows match; how many; whether any; the smallest and largest |
| order | `Scan<TRecord>().Keys(r => r.Column)` | what is at, after or before a key; the first, the last, the i-th; the next distinct key; how many share a key |
| set driven by order | `Scan<TRecord>().OrderBy(r => r.Column)` | the matching rows, in key order |

Neither model mutates. Insertion into a key range, deletion and update belong to a storage engine
that owns a memtable and tombstones; the format carries no tombstone, and this library provides the
immutable half: bulk load, append, and every read below.

## 2. Sources of order

A cursor needs a **key source**: the entries of a column in key order, with a row per entry, able to
locate a key in `O(log n)`. The cheapest one the file offers is used, and the builder's
`ExplainAsync` returns a `KeyPlan` naming it and why each other was rejected.

| source | exists when | entries | seek |
|---|---|---|---|
| **sorted column** | the file statistics say the column ascends, and it has a zone map | `(value, row)` per non-null row, in row order, which *is* key order | the zone bounds in memory, then one zone decoded and bisected |
| **sorted runs** | the column carries `vorticity.sorted.runs.v1` | `(key, row)` per non-null row, per run | the runs' bounds exclude runs, then a binary search in each survivor, through its fences |
| **postings** | `vorticity.postings.blocks.v1` | the distinct keys of each run; no rows | the same, on keys only |
| **dictionary** | `vorticity.dict.probe.v1` | the values of each dictionary chunk, sorted at open; no rows | in memory |
| none | an unsorted, unindexed column | — | **refused**: `VortexUnsupportedException` naming `IndexPolicy.SortedRuns` |

**Cheapest first**: a sorted column costs one zone-map read and one zone decode, so it beats sorted
runs whenever both exist. **Rows when rows are wanted**: a cursor opened without `Distinct()` takes
only the first two, because a postings or dictionary source cannot say where a key's rows are, and
answering with a block's first row would make `Row` a lie.

The sorted-column source serves the files this writer produces, whose statistics carry the order
flags in the reference's semantics; the reference writer drops those flags from its file statistics.
An `Inexact` zone bound still brackets correctly, since the true minimum is at or above a stated one
and the true maximum at or below ([08-semantics.md](08-semantics.md) §1). A lying `is_sorted` gives a
wrong order, never a memory fault ([08-semantics.md](08-semantics.md) §5).

**Nulls are in no source**: no index inserts a null, and a sorted column's null rows carry no key. A
cursor never visits one; `IS NULL` is the scan's question, answered from the zone maps' null counts.

## 3. The cursor

`KeyCursor<TKey>` (`Keys/KeyCursor.Typed.cs`), `TKey` inferred from the record member, so a key of the
wrong type does not compile. Positioning is asynchronous because it may read a run segment or a zone
not yet loaded; inside a loaded run a step completes synchronously. A cursor is not thread-safe; the
runs it reads are cached on the file and shared by every cursor and scan of it.

### 3.1 Positioning

The entries of a source are totally ordered by `(key, row)`, and `SeekAsync(k, op)` positions the
cursor on that order:

| `op` | positions on | is |
|---|---|---|
| `Exact` | the first entry whose key is `k` | a point lookup; invalid when `k` is absent |
| `AtOrAfter` | the first entry with key `≥ k` | a lower bound: the start of a range |
| `After` | the first entry with key `> k` | an upper bound: the successor |
| `AtOrBefore` | the last entry with key `≤ k` | the end of a closed range, walking backwards |
| `Before` | the last entry with key `< k` | the predecessor |

`SeekFirstAsync` and `SeekLastAsync` are the minimum and the maximum. `Exact` on a key with duplicates
lands on its lowest row, `AtOrBefore` on its highest. A range `[a, b)` is `AtOrAfter(a)`, then
`NextAsync` while `Key < b`. A null seek key throws, since no entry has one.

### 3.2 Stepping and direction

`NextAsync` and `PrevAsync` move one entry in `(key, row)` order and return false past the ends.
`NextKeyAsync` moves to the first entry of the next **distinct** key by seeking `After(Key)` rather
than stepping through the duplicates: a loose index scan, one seek per group, so a `GROUP BY` over a
key with a million rows costs one seek per value. `PrevKeyAsync` is `Before(Key)`.

A cursor has a direction. Stepping the other way re-seeks at the current entry, `O(r log n)` for `r`
runs, because a heap of run positions does not run backwards; that is what every merging iterator
charges, and it is documented rather than optimized. On a sorted column, one contiguous run, a flip
costs nothing.

### 3.3 Keys and rows

`Key` is the entry's key as the member's .NET type ([07-dotnet-mapping.md](07-dotnet-mapping.md)), so
it goes straight back into a seek, a filter or a comparison. `Row` is the entry's row in the file, the
coordinate a scan's `Rows` accepts: that is how a cursor's answer becomes rows, and what §5 does for
the consumer. `HasRows` is false for a `Distinct()` cursor over a keys-only source, where `Row` throws.
A step that reads no key allocates nothing.

### 3.4 Key order per dtype

A cursor is an ordinal structure and needs a **total** order; a filter follows IEEE 754, where NaN is
unordered ([08-semantics.md](08-semantics.md) §2). The two are kept apart by stating the total order
once, for the writer of sorted runs and for this reader:

| dtype | key order | notes |
|---|---|---|
| integers | numeric | the run's key column is the column's own type |
| floats | negative NaN, −∞, negatives, −0.0, +0.0, positives, +∞, positive NaN: the row encoding's total order ([06-row-encoding.md](06-row-encoding.md) §3) | −0.0 and +0.0 are two keys. A range from a filter maps onto the slice with its IEEE meaning, `x > v` never reaching a NaN: the two orders meet by construction of the bounds, never by a comparison |
| text, binary | bytewise, which is code-point order for UTF-8 | |
| extensions | their storage's order | a timestamp orders as its integer |
| bool | not a key source | the dictionary or the postings answer a two-value domain |
| composite `(a, b, …)` | bytewise over the row encoding of the tuple | §3.6 |

Within one key, entries are in **row order**, which makes a walk deterministic, a `Distinct()`
cursor's row the key's first occurrence, and the reverse walk the exact reverse. The source owns its
comparator: runs are walked in the total order, a sorted column in the IEEE order its flag was
computed in, where −0.0 and +0.0 are one key and a column holding a NaN is not sorted at all.

### 3.5 Rank, select, and the count of a key

`RankAsync(k)` is the number of entries with a key `< k`; `SeekRankAsync(i)` positions on the entry of
rank `i`; `KeyCountAsync()` is the number of entries sharing the current key. All three are exact on
the two sources with rows, and refused on the others. A sorted array is its own counter: rank is the
sum over runs of each run's lower bound, `O(r log n)`; select is a binary search over positions with a
rank computed at each probe, `O(r log² n)`.

### 3.6 Composite keys

A sorted run over the row encoding of `(a, b, …)` ([10-indexes.md](10-indexes.md) §5.4) orders
tuples bytewise, and the encoding of `(a)` is a byte prefix of the encoding of `(a, b)`, so a prefix
on the leading columns is a range. The public cursor walks one column: a composite key's runs serve
the engine's own key-ordered reads, which a dataset's clustering key drives
([13-dataset.md](13-dataset.md) §6.6), and the row-encoding package's `RowEncoder.EncodeKey` gives
one tuple's bytes.

## 4. Answers without rows

Four terminals on a scan, each computed without a batch and bounded by one split of memory, whatever
the file's size. Each honours the scan's filter, row range, and pruning and index switches exactly as
its enumeration would: a terminal is the scan with a different output, never a different scan.

### 4.1 `AnyAsync`

Whether the scan would return a row. Cheapest proof first, stopping at the first that decides: an
empty block mask (no data read); an exact source covering the predicate, with an entry in its slice;
a block the zone maps prove full; otherwise the live splits in order, stopping at the first row that
passes. It is also the public form of a point probe: `Where(x == v).AnyAsync()` has the whole pruning
chain in front of it.

### 4.2 `CountAsync`

The number of rows the scan would return, exactly, taking per block the cheapest of three tiers:

| tier | proof | cost |
|---|---|---|
| **exact cover** | a comparison, `IN`, `StartsWith`, or a conjunction of those on one column with a sorted source: the sum of the slices' lengths | `O(r log n)`, no data read |
| **full-block proof** | the zone maps prove every non-null row of the block matches, and its null count is known | the zone maps, already read |
| **count-only decode** | the block decoded, the filter evaluated, its true rows counted | one decode, no gather, no batch |

The full-block proof is the dual of pruning, with the asymmetry reversed: it proves only when the
statistics point the safe way. For `x ≥ v` a block matches whole when its stated minimum is `≥ v`,
and an `Inexact` minimum still proves it; `x = v` only when the minimum and maximum are both `v` and
both `Exact`. A zone's bounds exclude NaN, so a float block's NaN count is subtracted or the block
decoded. Predicates combine as three counts per range — true, false and unknown, any two deciding
the third — so that `NOT` stays exact and two predicates on one nullable column share their unknowns,
its nulls. What that algebra cannot see falls to the third tier.

**A wrong proof is a wrong count**, which is what makes it testable: every count is compared with
the materialized scan with each tier forced off in turn.

### 4.3 `MinAsync` and `MaxAsync`

The smallest and largest non-null value of a column among the scan's rows, in the filter's IEEE
order, so a NaN is never an extreme, as in the statistics. In order: the file statistic, when the
scan is the whole file and the statistic `Exact`; the zone maps, where an `Exact` bound answers a
split and an `Inexact` one is a candidate, decoded best bound first, usually once; otherwise a decode
of the live blocks with a running extreme, one split at a time.

### 4.4 Distinct keys

`Keys(r => r.Column).Distinct()` opens a cursor that steps by `NextKey`: every distinct non-null value,
in key order, once. It admits the keys-only sources, so a `GROUP BY status` over a billion rows reads
the postings keys of each run and never a data segment. A column with no source is refused naming
`IndexPolicy.Postings`, the cheapest structure that would serve, rather than filled into a hash set
bounded by the column. Distinct is a property of the cursor, not a terminal returning a list, because
it is an ordered walk with an early exit, and a list would buffer it.

## 5. Rows in key order

`OrderBy(r => r.Column, descending)` makes the scan deliver its rows in the key order of a column
instead of file order. It composes with the projection and the filter, and excludes `Rows`.

**How.** The scan is driven by a cursor instead of the split planner. The key range is the
intersection of the slices of the filter's top-level conjuncts on the key, so an `OR` or an `IN` on
the key narrows to its exact slices. The cursor walks a **window** of entries, a batch's worth; the
entries whose block the mask pruned are skipped, since the filter's other conjuncts prune as in any
scan; the window's rows are taken as a selection, the filter evaluated on them, and the batch
permuted into the window's key order. Then the next window. Memory is one window and one split's
decode, never the result.

**What it delivers.** Batches in key order, ties in row order, the descending order the exact
reverse. Rows whose key is null come **last in both directions**, read after the walk as a filtered
scan the zone maps' null counts prune. A consumer that stops early is a `LIMIT`: the windows past it
are never read. A degree of parallelism applies within a window, whose splits are independent;
windows stay in order.

**What it costs**, which a caller should read before using it. A window of `W` entries touches as many
splits as its rows are scattered over. On a sorted column the rows of consecutive keys are
consecutive, so the walk is a plain scan. On sorted runs over a column **uncorrelated with file
order**, a window can touch a split per row, and a scattered take costs what it costs: 64 rows of an
uncorrelated column read in key order take longer than Rust's reader takes for 64 rows scattered the
same way whose positions it is given, a take over 64 splits (the `against a take` axis of
`--ratio-check`, a gate the benchmark page leaves out), and less than Rust's filtered scan of the
same band, which is what a reader without the index has to do (the key-order axes of
[05-benchmarks.md](05-benchmarks.md) §3, measured on
[the benchmark page](../guide/benchmarks.md#in-one-process-after-warm-up)). So it is the tool for a
selective range, a top-k or a merge, and not for a whole uncorrelated column, where sorting the
scan's output costs less. The plan's `OrderPlan` states the source, the runs the range reaches and
the entries it admits before the read; the scan's statistics count the windows and the splits they
touched after it.

The dataset's key-ordered reads and its compaction merge are built on this, one call per object
([13-dataset.md](13-dataset.md) §5.3, §6.6).

## 6. Predicates on text and lists

Four predicates beyond comparisons, `IN` and nullity, each with its pruning:

| predicate | true when | null | pruning |
|---|---|---|---|
| `StartsWith(p)` | the value's bytes begin with `p` | unknown | as a range `[p, succ(p))`, where `succ` increments the last byte after dropping trailing `0xFF`s: the zone bounds and a sorted run answer it, a count by exact cover |
| `Contains(p)` | `p` occurs in the value | unknown | the trigram filter and postings when `p` has three bytes or more ([10-indexes.md](10-indexes.md) §4.2, §5.3) |
| `Like(pattern)` | SQL `LIKE`: `%` any run, `_` any one character of a utf8 column and one byte of a binary one, an escape quoting either | unknown | a leading literal as `StartsWith`, every literal run of three bytes or more through the trigram structures; always re-evaluated on survivors |
| `ListContains(list, v)` | an element of the row's list equals `v`, under the comparison kernels' equality | unknown on a null list or under a null `v`; a null element matches nothing | the list's null counts; the Bloom filter over the elements ([10-indexes.md](10-indexes.md) §4.1) |

Matching is bytewise, but for `_` over utf8, and case-sensitive; `NOT StartsWith` claims nothing
from a zone map, which is the safe answer. On the typed path these are members of `Sym<string>` and
of a list member's symbol; on the tool path, expressions of `VortexExpr`.

## 7. What is not on the surface, on purpose

A key-range type (the filter says it), a `Probe(column, value)` (`AnyAsync` is it), a distinct
terminal returning a list (the cursor walks it), a key-ordered take (the window inside `OrderBy` is
the only reorder), a sort (constraint 1), and a comparator on the cursor (keys arrive typed and in
order). Each would be a second way to say something one member already says, or a way to buffer a
file.

## 8. How it is tested

- A cursor's walk, forwards and backwards, with and without `Distinct()`, equals a sort of the
  materialized column, on every source, with windows and lanes.
- `rank(select(i)) ≤ i < rank(select(i)) + KeyCount(select(i))` over every entry.
- Every count and extreme equals the materialized scan's, with pruning, indexes and each count tier
  forced off in turn.
- A key-ordered scan equals a sort of the filtered scan's rows, nulls last, in both directions.
- A cursor's step allocates nothing (`ScanAllocationTests`).
