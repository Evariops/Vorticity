# Index reads: cursors, answers without rows, and key order

What a caller can ask of the read path beyond a scan: a cursor that seeks and steps in key order,
counts and extremes computed without materializing rows, and rows delivered in key order. Every
structure these read is one that [10-indexes.md](10-indexes.md) defines, or one the file statistics
already carry, so nothing on disk changes for any of it.

Three more constraints apply, in order:

1. Memory is bounded by a batch, never by the file. An operation that would need a whole column in
   memory (a sort, a distinct table over an unindexed column) is refused, naming the index kind that
   would serve it, and never emulated.
2. Every answer equals what a full materialization would give. A cursor walk equals a sort of the
   column, and a count equals the length of the scan's output, with indexes on or off.
3. No allocation per step or per batch in steady state (see [performance
   invariants](03-architecture.md#4-performance-invariants)).

## 1. Two models over one payload

The set model is the scan: a predicate becomes a block mask, the mask a row selection, and the
selection batches in file order (see [reading what the writer
produced](11-write-strategy.md#6-reading-what-the-writer-produced)). It answers which rows match, and
it is the right engine for a filter, since each touched split is read once and only live blocks are
decoded.

The order model is a cursor: position at a key, then step to the neighbour. It answers what comes
next, which no set can. A streaming writer produces sorted runs, never a globally sorted column, so
key order exists inside each run, and a cursor over their union is a k-way merge over the runs the
key range reaches, the merging iterator every log-structured store has. It is a reader, not a format.

| model | entry point | answers |
|---|---|---|
| set | `file.Scan<TRecord>()` | which rows match, how many, whether any, the smallest and largest |
| order | `Scan<TRecord>().Keys(r => r.Column)` | what is at, after or before a key, the first, the last, the i-th, the next distinct key, how many share a key |
| set driven by order | `Scan<TRecord>().OrderBy(r => r.Column)` | the matching rows, in key order |

Neither model changes a file. Inserting into a key range, deleting and updating belong to a storage
layer that owns a memtable and tombstones, and the file format carries no tombstone. This library
provides the immutable half (bulk load, append, and every read below), and its dataset builds row
deletes and updates on top of whole objects ([deleting and updating
rows](13-dataset.md#12-deleting-and-updating-rows)).

## 2. Sources of order

A cursor needs a key source: the entries of a column in key order, with a row per entry, able to
locate a key in `O(log n)`. The cheapest source the file offers is used, and the builder's
`ExplainAsync` returns a `KeyPlan` naming it and saying why each other one was rejected.

| source | exists when | entries | seek |
|---|---|---|---|
| sorted column | the file statistics say the column ascends, and it has a zone map | `(value, row)` per non-null row, in row order, which is key order | the zone bounds in memory, then one zone decoded and bisected |
| sorted runs | the column carries `vorticity.sorted.runs.v1` | `(key, row)` per non-null row, per run | the runs' bounds exclude runs, then a binary search in each survivor, through its fences |
| postings | `vorticity.postings.blocks.v1` | the distinct keys of each run, without rows | the same, on keys only |
| dictionary | `vorticity.dict.probe.v1` | the values of each dictionary chunk, sorted at open, without rows | in memory |
| none | an unsorted, unindexed column | none | refused with a `VortexUnsupportedException` naming `IndexPolicy.SortedRuns` |

The cheapest source wins: a sorted column costs one zone-map read and one zone decode, so it beats
sorted runs whenever both exist. A cursor opened without `Distinct()` only takes the first two
sources, because a postings or dictionary source cannot say where a key's rows are, and answering
with a block's first row would make `Row` a lie.

The sorted-column source serves the files this writer produces, whose statistics carry the order
flags in the reference's semantics. The reference writer drops those flags from its file statistics.
An `Inexact` zone bound still brackets correctly, since the true minimum is at or above a stated one
and the true maximum at or below (see [statistic
precision](08-semantics.md#1-statistic-precision-what-inexact-licenses)). A lying `is_sorted` gives a
wrong order, never a memory fault (see [untrusted
hints](08-semantics.md#5-untrusted-hints-a-three-class-policy)).

No source holds nulls. No index inserts a null, and a sorted column's null rows carry no key, so a
cursor never visits one. `IS NULL` is the scan's question, answered from the zone maps' null counts.

## 3. The cursor

`KeyCursor<TKey>` (`Keys/KeyCursor.Typed.cs`) infers `TKey` from the record member, so a key of the
wrong type does not compile. Positioning is asynchronous because it may read a run segment or a zone
not loaded yet, while a step inside a loaded run completes synchronously. A cursor is not
thread-safe, but the runs it reads are cached on the file and shared by every cursor and scan of it.

### 3.1 Positioning

The entries of a source are totally ordered by `(key, row)`, and `SeekAsync(k, op)` positions the
cursor in that order:

| `op` | positions on | is |
|---|---|---|
| `Exact` | the first entry whose key is `k` | a point lookup, invalid when `k` is absent |
| `AtOrAfter` | the first entry with key `≥ k` | a lower bound, the start of a range |
| `After` | the first entry with key `> k` | an upper bound, the successor |
| `AtOrBefore` | the last entry with key `≤ k` | the end of a closed range, walking backwards |
| `Before` | the last entry with key `< k` | the predecessor |

`SeekFirstAsync` and `SeekLastAsync` are the minimum and the maximum. `Exact` on a key with duplicates
lands on its lowest row, and `AtOrBefore` on its highest. A range `[a, b)` is `AtOrAfter(a)`, then
`NextAsync` while `Key < b`. A null seek key throws, since no entry has one.

### 3.2 Stepping and direction

`NextAsync` and `PrevAsync` move one entry in `(key, row)` order and return false past the ends.
`NextKeyAsync` moves to the first entry of the next distinct key by seeking `After(Key)` rather than
stepping through the duplicates. That is a loose index scan, one seek per group, so a `GROUP BY` over
a key with a million rows costs one seek per value. `PrevKeyAsync` is `Before(Key)`.

A cursor has a direction. Stepping the other way seeks again at the current entry, which costs
`O(r log n)` for `r` runs, because a heap of run positions does not run backwards. Every merging
iterator charges that, and it is documented rather than optimized. On a sorted column, which is one
contiguous run, a change of direction costs nothing.

### 3.3 Keys and rows

`Key` is the entry's key as the member's .NET type ([07-dotnet-mapping.md](07-dotnet-mapping.md)), so
it goes straight back into a seek, a filter or a comparison. `Row` is the entry's row in the file, the
coordinate a scan's `Rows` accepts. That is how a cursor's answer becomes rows, and what [rows in key
order](#5-rows-in-key-order) does for the consumer. `HasRows` is false for a `Distinct()` cursor over
a keys-only source, where `Row` throws. A step that reads no key allocates nothing.

### 3.4 Key order per dtype

A cursor is an ordinal structure and needs a total order, whereas a filter follows IEEE 754, where
NaN is unordered (see [NaN](08-semantics.md#2-nan)). The two are kept apart by stating the total
order once, for the writer of sorted runs and for this reader:

| dtype | key order | notes |
|---|---|---|
| integers | numeric | the run's key column has the column's own type |
| floats | negative NaN, −∞, negatives, −0.0, +0.0, positives, +∞, positive NaN, the row encoding's total order (see [per-type encodings](06-row-encoding.md#3-per-type-encodings)) | −0.0 and +0.0 are two keys. A range from a filter maps onto the slice with its IEEE meaning, so `x > v` never reaches a NaN. The two orders meet by the way the bounds are built, never by a comparison |
| text, binary | bytewise, which is code-point order for UTF-8 | |
| extensions | their storage's order | a timestamp orders as its integer |
| bool | not a key source | the dictionary or the postings answer a two-value domain |
| composite `(a, b, …)` | bytewise over the row encoding of the tuple | see [composite keys](#36-composite-keys) |

Within one key, entries are in row order, which makes a walk deterministic, makes a `Distinct()`
cursor's row the key's first occurrence, and makes the reverse walk the exact reverse. The source
owns its comparator. Runs are walked in the total order, and a sorted column in the IEEE order its
flag was computed in, where −0.0 and +0.0 are one key and a column holding a NaN is not sorted at
all.

### 3.5 Rank, select, and the count of a key

`RankAsync(k)` is the number of entries with a key `< k`, `SeekRankAsync(i)` positions on the entry
of rank `i`, and `CountAtKeyAsync()` is the number of entries that share the current key. All three
are exact on the two sources with rows, and refused on the others. A sorted array is its own
counter: rank is the sum over runs of each run's lower bound, in `O(r log n)`, and select is a binary
search over positions with a rank computed at each probe, in `O(r log² n)`.

### 3.6 Composite keys

A sorted run over the row encoding of `(a, b, …)` (see [composite
keys](10-indexes.md#54-composite-keys-through-the-row-encoding)) orders tuples bytewise, and the
encoding of `(a)` is a byte prefix of the encoding of `(a, b)`, so a prefix on the leading columns is
a range. The public cursor walks one column. A composite key's runs serve the engine's own key-ordered
reads, which a dataset's clustering key drives (see [order beyond the
lookup](13-dataset.md#66-order-beyond-the-lookup)), and the row-encoding package's
`RowEncoder.EncodeKey` gives one tuple's bytes.

## 4. Answers without rows

Four terminal operations on a scan are each computed without a batch and bounded by one split of
memory, whatever the file's size. Each honours the scan's filter, row range, and pruning and index
switches exactly as its enumeration would. A terminal is the same scan with a different output,
never a different scan.

### 4.1 `AnyAsync`

`AnyAsync` says whether the scan would return a row, trying the cheapest proof first and stopping at
the first one that decides: an empty block mask (no data read), an exact source covering the
predicate with an entry in its slice, a block the zone maps prove full, and otherwise the live splits
in order, stopping at the first row that passes. It is also the public form of a point probe:
`Where(x == v).AnyAsync()` has the whole pruning chain in front of it.

### 4.2 `CountAsync`

`CountAsync` returns the exact number of rows the scan would return, taking the cheapest of three
tiers per block:

| tier | proof | cost |
|---|---|---|
| exact cover | a comparison, `IN`, `StartsWith`, or a conjunction of those on one column with a sorted source: the sum of the slices' lengths | `O(r log n)`, no data read |
| full-block proof | the zone maps prove every non-null row of the block matches, and its null count is known | the zone maps, already read |
| count-only decode | the block is decoded, the filter evaluated, and its true rows counted | one decode, no gather, no batch |

The full-block proof is the dual of pruning, with the asymmetry reversed: it only proves when the
statistics point the safe way. For `x ≥ v`, a block matches whole when its stated minimum is `≥ v`,
and an `Inexact` minimum still proves it. For `x = v`, only when the minimum and maximum are both `v`
and both `Exact`. A zone's bounds exclude NaN, so a float block's NaN count is subtracted or the block
is decoded. Predicates combine as three counts per range (true, false and unknown, any two deciding
the third), so that `NOT` stays exact and two predicates on one nullable column share their unknowns,
which are its nulls. Whatever that algebra cannot see falls to the third tier.

A wrong proof gives a wrong count, which is what makes it testable: every count is compared with the
materialized scan with each tier forced off in turn.

### 4.3 `MinAsync` and `MaxAsync`

These return the smallest and largest non-null value of a column among the scan's rows, in the
filter's IEEE order, so a NaN is never an extreme, as in the statistics. They try, in order, the file
statistic when the scan covers the whole file and the statistic is `Exact`, then the zone maps, where
an `Exact` bound answers a split and an `Inexact` one is a candidate decoded best bound first (usually
just once), and otherwise a decode of the live blocks with a running extreme, one split at a time.

### 4.4 Distinct keys

`Keys(r => r.Column).Distinct()` opens a cursor that steps by `NextKey`, visiting every distinct
non-null value once, in key order. It accepts the keys-only sources, so a `GROUP BY status` over a
billion rows reads the postings keys of each run and never a data segment. A column with no source is
refused, naming `IndexPolicy.Postings`, the cheapest structure that would serve it, rather than
filled into a hash set bounded by the column. Distinct is a property of the cursor, not a terminal
returning a list, because it is an ordered walk with an early exit, and a list would buffer it.

## 5. Rows in key order

`OrderBy(r => r.Column)` and `OrderByDescending(r => r.Column)` make the scan deliver its rows in the
key order of a column instead of file order. They compose with the projection and the filter, and
exclude `Rows`.

The scan is then driven by a cursor instead of the split planner. The key range is the intersection
of the slices of the filter's top-level conjuncts on the key, so an `OR` or an `IN` on the key
narrows to its exact slices. The cursor walks a window of entries, a batch's worth at a time. Entries
whose block the mask pruned are skipped, since the filter's other conjuncts prune as in any scan, the
window's rows are taken as a selection, the filter is evaluated on them, and the batch is permuted
into the window's key order. Then comes the next window. Memory is one window plus one split's
decode, never the result.

What it delivers is batches in key order, ties in row order, with the descending order the exact
reverse. Rows whose key is null come last in both directions, read after the walk as a filtered scan
that the zone maps' null counts prune. A consumer that stops early acts as a `LIMIT`, and the windows
past it are never read. A degree of parallelism applies within a window, whose splits are
independent, while the windows stay in order.

What it costs should be read before using it. A window of `W` entries touches as many splits as its
rows are scattered over. On a sorted column, the rows of consecutive keys are consecutive, so the
walk is a plain scan. On sorted runs over a column uncorrelated with file order, a window can touch
one split per row, and a scattered take costs what it costs. Reading 64 rows of an uncorrelated
column in key order takes longer than Rust's reader takes for 64 rows scattered the same way whose
positions it is given, a take over 64 splits (the `against a take` axis of `--ratio-check`, a gate
the benchmark page leaves out). It takes less than Rust's filtered scan of the same band, which is
what a reader without the index has to do (the key-order axes [in one
process](05-benchmarks.md#3-in-one-process-after-warm-up), measured on [the benchmark
page](../guide/benchmarks.md#in-one-process-after-warm-up)). So it is the tool for a selective range,
a top-k or a merge, not for a whole uncorrelated column, where sorting the scan's output costs less.
The plan's `OrderPlan` states the source, the runs the range reaches and the entries it admits
before the read, and the scan's metrics count the windows and the splits they touched afterwards.

The dataset's key-ordered reads and its compaction merge are built on this, one call per object (see
[what a compaction does](13-dataset.md#53-what-a-compaction-does) and [order beyond the
lookup](13-dataset.md#66-order-beyond-the-lookup)).

## 6. Predicates on text and lists

Four predicates go beyond comparisons, `IN` and nullity, each with its own pruning:

| predicate | true when | null | pruning |
|---|---|---|---|
| `StartsWith(p)` | the value's bytes begin with `p` | unknown | as a range `[p, succ(p))`, where `succ` increments the last byte after dropping trailing `0xFF`s. The zone bounds and a sorted run answer it, and a count by exact cover |
| `Contains(p)` | `p` occurs in the value | unknown | the trigram filter and postings when `p` has three bytes or more (see [trigram filters](10-indexes.md#42-vorticitybloomngram3v1-trigrams-for-text-predicates) and [trigram postings](10-indexes.md#53-vorticitypostingsngram3v1-trigrams-to-blocks)) |
| `Like(pattern)` | SQL `LIKE`: `%` matches any run, `_` any one character of a utf8 column or one byte of a binary one, and an escape quotes either | unknown | a leading literal as `StartsWith`, and every literal run of three bytes or more through the trigram structures. Always evaluated again on the survivors |
| `ListContains(list, v)` | an element of the row's list equals `v`, under the comparison kernels' equality | unknown on a null list or under a null `v`. A null element matches nothing | the list's null counts, and the Bloom filter over the elements (see [the Bloom filter](10-indexes.md#41-vorticitybloomsbbfv1-a-split-block-bloom-filter-per-block)) |

Matching is bytewise, except for `_` over utf8, and case-sensitive. `NOT StartsWith` claims nothing
from a zone map, which is the safe answer. On the typed path these are members of `Sym<string>` and
of a list member's symbol, and on the tool path, expressions of `VortexExpr`.

## 7. What is not in the public API, on purpose

A key-range type (the filter already says it), a `Probe(column, value)` (`AnyAsync` is it), a
distinct terminal returning a list (the cursor walks it), a key-ordered take (the window inside
`OrderBy` is the only reordering), a sort (ruled out by the first constraint), and a comparator on the
cursor (keys arrive typed and in order). Each would be a second way to say something one member
already says, or a way to buffer a whole file.

## 8. How it is tested

- A cursor's walk, forwards and backwards, with and without `Distinct()`, equals a sort of the
  materialized column, on every source, with windows and lanes.
- `rank(select(i)) ≤ i < rank(select(i)) + KeyCount(select(i))` holds over every entry.
- Every count and extreme equals the materialized scan's, with pruning, indexes and each count tier
  forced off in turn.
- A key-ordered scan equals a sort of the filtered scan's rows, nulls last, in both directions.
- A cursor's step allocates nothing (`ScanAllocationTests`).
