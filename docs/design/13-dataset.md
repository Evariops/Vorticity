# Dataset: a log-structured tree of Vortex files

`Vorticity.Dataset`, experimental (`VX0001`), keeps a versioned table over an object store: rows
appended over time into plain Vortex files, compacted in the background, indexed progressively, and
read as one table at any version. [datasets.md](../guide/datasets.md) and
[dataset-maintenance.md](../guide/dataset-maintenance.md) show it in use.

**The requirement: no read-path cost proportional to any N.** Not the bytes of data, not the number
of data objects, commits, appends since the last compaction, blocks of an object or index fragments.
Every bound is a constant of the design — a fan-out, a page size, a level count — times a logarithm,
plus the size of the answer. The write path pays instead, once and amortized, in compaction, and the
price is stated (§5.4).

**What does not bend:** every data object is a plain Vortex file a strict Rust 0.86.1 reader opens,
with this library's structures invisible to it; the core takes no third-party dependency; an index is
a hint unless it is proven complete ([10-indexes.md](10-indexes.md) §7); no interleaving of readers,
writers, indexers, compactions and vacuums produces a wrong answer; and **the S3 client is not this
library's**: another library implements the store seam (§11), and this one never names S3 in code.

## 1. Where the N's hide, and the bound that replaces each

| N | the bound | mechanism |
|---|---|---|
| bytes of data | nothing on the read path | identity from bytes the open already reads; the content hash is the writer's, checked only offline (§7) |
| data objects | O(log N) pages read, at most 8 + L objects touched by a key lookup | a copy-on-write tree of objects with summaries per node (§4), leveled compaction on a clustering key (§5) |
| commits | one `List` finds the latest; a commit writes O(depth) pages in one object | newest-first keys (§8.3); path copying into one commit object (§4.3) |
| appends since compaction | at most K runs per index entry per object, at most 8 objects in level 0 | one rule inside a file and across files (§5.2, §6.1) |
| blocks of an object | `1 + 16 · log₁₆ blocks` filter reads, output-sensitive | the filter tree ([10-indexes.md](10-indexes.md) §3) |
| entries of a run | two fence pages to 10¹² entries | fence pages ([10-indexes.md](10-indexes.md) §3) |
| fragments | one ranged read each, inside the commit object that wrote it | §6.4 |
| distinct keys, on a Bloom filter | the structure is chosen by cardinality | §6.5 |

## 2. Principles

1. **Immutable objects, one commit object per version.** Nothing committed is rewritten; a reader
   holding a version's header holds a snapshot.
2. **Metadata is a tree, not a list.** The set of data objects is a copy-on-write tree whose nodes
   carry summaries: reading descends, committing copies a path, and neither is O(objects).
3. **Bounded fan-in everywhere**: K runs per index entry per object, 8 objects in level 0, L levels,
   one object per level for a clustering-key lookup. Compaction holds the bounds; a plan reports
   when it lags.
4. **Cost is output-sensitive or logarithmic**, never proportional to the dataset: a predicate that
   matches everything reads everything, which is the answer's own size.
5. **The write path pays**: merges when a file completes, filter trees, fences, compaction, each
   stated as a write amplification.
6. **Identity from bytes already read; integrity by checksums of what is read.** Pages are
   referenced by their XXH3-128, commit headers and index regions carry an XXH3-64, and nothing on
   the read path hashes data.
7. **Derived data folds back into the data**: a compaction that rewrites an object embeds its
   indexes, so external fragments are a transient layer between "written without an index" and
   "next rewritten".

## 3. Objects and keys

A dataset is a store prefix holding two kinds of immutable object:

| object | key | content |
|---|---|---|
| data object | `data/<uid>.vortex` | a plain Vortex file, its indexes embedded, its identity in its postscript (§7) |
| commit object | `commit/<inverted version>.vxc` | everything else one commit produced: a header, the tree pages it wrote, the index fragments it wrote, a table of both, a trailer |

**The commit object** opens by its head, where a Vortex file opens by its tail: magic `VXCOMMIT`, a
format version, the header's length, the header (Protobuf), then pages and fragments in the order
they were added, the table, and a 32-byte trailer — the table's offset and length, **the object's own
length**, an XXH3-64 over the header and the table, and magic `VXCT`. Recording the length turns any
truncation into a stated error rather than a shorter object. The **header** holds the version, its
parent, the schema, the clustering key, the write policy, the tree's chunking parameters and seed,
the compaction and retention settings, and per level its top page **inlined**, with the pages below
it while the header stays under 256 KiB. One ranged read of the first 256 KiB opens a commit.

**A page reference** is a fixed 36 bytes: the version that wrote the page, its offset relative to that
object's pages region, its length, and its XXH3-128. A commit references every page it did not change
where it already lies, in an older commit object, and checks the hash on read; fixed-width,
relative references are what let a header name offsets that depend on its own length, and what make
two identical trees identical page for page. There is no separate page object, no orphan page after
a crash, and one conditional creation per commit.

**`<inverted version>`** is `10²⁰ − 1 − version` in twenty digits, so the **newest commit sorts
first** and the first key of a listing of `commit/` is the latest version (§8.3). There is no mutable
object anywhere.

An existing Vortex file becomes a leaf by import, **without a copy**: the store grows by the commit
object alone. A process that lists `data/` and reads every file sees uncommitted and superseded
objects, as in every table format: the latest commit object is the only definition of the content.

## 4. The tree

### 4.1 A prolly tree per level

Each level (§5) is a **copy-on-write search tree over data objects**, ordered by the clustering key
when the dataset declares one and by first row position otherwise. It is a **prolly tree**, the
probabilistic B-tree of Noms and Dolt. Once a commit is a sorted batch of changes per level, a prolly
tree and a B+tree with path copying are one algorithm — load the touched leaves, merge the changes,
re-emit them, rebuild the nodes above up to the top — and only the rule that places page boundaries
differs. From the prolly rule alone follow history independence, deduplication across writers and
the strongest test oracle there is (§12).

**The boundary rule.** A boundary is decided per entry from the XXH3-64 of the entry's key alone,
seeded with sixteen random bytes the dataset draws once, so values never move a boundary and an
indexer that updates an object's descriptor rewrites exactly `depth` pages. It is normalized by
size: no boundary before 64 KiB of entries, a probability proportional to the room left after that,
`k · entryBytes / (max − bytes)` with `k = 2`, which puts the mean page at 128 KiB by construction,
and a forced boundary at 256 KiB, the page cap. After an edit, chunking continues until a new
boundary meets an old one, so **the tree is a pure function of the key set and the parameters**: the
same objects committed in any order give byte-identical pages and one root hash. The rule is behind a
seam, whose other implementation, a B+tree's fill factor, is measured on the same bench.

With entries of about 180 bytes the fan-out is about 680. The header inlines a level's top page and,
while it fits, the pages below, so a lookup reads **0 dependent pages** up to about 650 objects,
**1** up to about 400 000, **2** up to about 280 million.

### 4.2 What a node carries

A **leaf entry** describes one data object:

- **its key**: the row encoding of the object's smallest clustering key followed by its first row
  position, or the position alone without a clustering key, then the object's uid, which makes the key
  a function of the object rather than of the tree it was committed into (§8.2);
- its size, row count and identity: the uid in its postscript, the store's token when the store
  gives one, and the XXH3-128 the writer computed (§7);
- **summaries**: per summarized column its minimum, maximum and null count, taken from the object's
  own file statistics before its put, so they cost the append no request. The first 32 top-level
  columns are summarized by default (`DatasetOptions.SummaryColumns`), and the clustering key's
  always;
- its index descriptor: embedded in the object, or per entry the fragments that cover it (§6.4).

An **internal entry** holds a page reference to a child, the sum of its rows, and the **union** of its
children's summaries. The union is intersection-shaped, which is the one thing about it a reader must
get right: a column travels up only when every child carries it, and a bound only when every child
has one, since keeping a bound because another child was silent would prune a subtree that holds the
answer. One inexact child makes the union inexact.

**What the walk skips.** A subtree whose union refutes the predicate is skipped unread; an object whose
own summaries refute it is not opened; an object outside a row range is neither read nor counted, by
the row sums at every level. The question is asked by the core's own pruner, the one a file's
statistics use, so there is one implementation of [08-semantics.md](08-semantics.md) §1 and two
callers. Measured: a filter inside one of eight objects opens one object with the summaries and eight
without, for the same rows.

### 4.3 Commits are paths, written as one object

A commit is a sorted batch of changes per level: entries added, removed, or updated. Adding an object
re-chunks its leaf and the pages above it, O(depth) pages, laid out inside the commit object with the
header and the fragments; every page the commit did not change is referenced where it lies, and a page
whose new content equals one the writer has already read is referenced rather than rewritten, which
the prolly rule makes common after a rebase. One conditional creation commits it (§8), its cost does
not depend on the number of objects, and a crash leaves nothing to clean up but data objects.

## 5. Levels and compaction

### 5.1 Why levels are part of the read bound

Without a merge policy, the objects a lookup touches are the appends. The bound of §2 holds only if
compaction holds an invariant, so the invariant is specified, and a plan (`PlanCompactionAsync`)
reports every violation of it as a **lag** without opening a data object. A lag is reported, never
refused: compaction is the caller's background job, and **a library never stalls a writer**.

### 5.2 The invariant

- **Level 0** holds appended objects as they came, overlapping in key: at most **8**.
- **Levels 1 to L**, when the dataset declares a clustering key, hold objects with **disjoint key
  ranges** within a level, so a lookup by key touches at most one object per level: **8 + L** in all.
  This is leveled compaction as RocksDB does it, with Vortex files as the sorted tables. An object
  written into level `i` targets `256 MiB × F^(i−1)`, `F` = 10, capped at 4 GiB, and a level holds `F`
  of them; a level holding a single object is never over its size.
- Without a clustering key, levels are **size tiers**: a lookup touches every object the summaries do
  not refute, which is output-sensitive rather than bounded, and the plan says so.
- **Inside every object, at most K = 4 runs per index entry**, the same rule as a single file
  appended in place ([10-indexes.md](10-indexes.md) §3).

`CompactionOptions` carries the fan-out, the level-0 ceiling, the level-1 target, the object cap and
a cap on levels: under a cap, the top level has no size bound and only grows, since merging it into
itself would rewrite key-disjoint objects into the same objects.

### 5.3 What a compaction does

It reads `k` objects and writes one or more at the destination level's target: a k-way merge of the
inputs on the clustering key when there is one, a concatenation otherwise, and **the writer builds the
outputs' embedded indexes once**, dropping the inputs' fragments. Compaction is therefore also the
index compaction of everything it touches.

**Level 0 is not sorted, and nobody sorts it.** An append arrives in any order and the writer streams
with bounded memory ([11-write-strategy.md](11-write-strategy.md)). So the write policy **requires a
sorted run on the clustering key** in every object ([10-indexes.md](10-indexes.md) §6), and the
compaction reads each input **in key order through that run**
([12-index-reads.md](12-index-reads.md) §5): the merge's inputs are ordered, its outputs are sorted by
construction, and the writer never learns to sort. An append whose run the budget would refuse is
refused as an append.

The merge emits **windows, not rows**: at each step the input holding the smallest key emits every
row at or below the smallest key the others hold, a contiguous window of its current batch, so the
cost is `k` comparisons per window. Rows are compared by their row encoding, the order the tree, the
runs and the seeks already use. An output is sealed at the first key change past its target, **never
inside a key**, since two outputs sharing a boundary value would overlap; the destination inputs are
chosen against the union of the source range. Rows with a null key come last. A composite key merges
on its tuple; a composite key whose column holds nulls is refused, since its run holds no such tuple.
The rows written are checked against the inputs' count: a rewrite that loses rows is the one failure
it must not have. Measured on four interleaved level-0 objects: 42 808 bytes in, one sorted object of
16 596 out.

Triggers: level 0 above its ceiling, a level above its size, then an entry above K fragments, a
compaction of index bytes only (§6.4). When many processes append, one coordinator batching their
rows into one commit answers the contention; without it the protocol of §8 stays correct, only
slower.

### 5.4 The price

Leveled compaction with `F` = 10 rewrites each row about `F/2` times per level it crosses: about
**20 to 25×** the appended bytes over four levels. Tiered rewrites each row about once per level,
about **L×**. The default is leveled when a clustering key is declared and tiered otherwise. A dataset
that appends 1 TiB a day, leveled, writes about 25 TiB a day in compaction: the honest cost of the
one-object-per-level read bound, which every LSM store pays.

## 6. Indexes at scale

### 6.1 One run per entry per object

An index entry has one run per object, merged when the object completes, and an append in place keeps
it at K = 4 runs by merging past that ([10-indexes.md](10-indexes.md) §3). A lookup on a key
uncorrelated with row order therefore reads at most K fence pages per object, never one per chunk.

### 6.2 The filter tree and the fences

Bloom filters form a tree of fan-out 16 and long runs page their segment tables into fences
([10-indexes.md](10-indexes.md) §3): a point probe reads `1 + 16 · log₁₆ blocks` filters at most, a
lookup two fence pages before its payload, whatever the object's size.

### 6.3 Checksums

Every page, fragment and index region carries its checksum (§7), and a region that fails claims
nothing: a reader answers exactly without it, and `VerifyAsync` names it.

### 6.4 Fragments, in commit objects, transient

A **fragment** is one entry's runs over a block range of one object, in the container the core
builds for a file it cannot append to ([10-indexes.md](10-indexes.md) §7), with its own encoding table
and dtypes so that it decodes without the object's footer. An indexer works by object and block
range, and each batch is one commit whose **commit object carries the fragment's bytes**; the leaf
entry references it by `(version, offset, length, XXH3-64)`, one ranged read. Coverage may be partial:
a pruner uses the covered blocks, and a key source waits for complete coverage, which the entry
states, so an object can be indexed progressively without ever answering wrong.

A fragment is its content: two indexers of one range write one fragment, and a drop removes the
fragment of those bytes wherever it lies. Past K fragments an entry's fragments are **bundled** — the
containers copied as they are, one after another, behind a table — rather than merged, since a Bloom
tree's upper filters cannot be rebuilt from its leaves and a run's payloads hold offsets into their
own container. A data compaction that rewrites the object embeds the index and drops every fragment,
so at steady state the external layer holds only the indexes of objects nobody has rewritten.

### 6.5 Which structure for which column

| distinct keys | structure | point lookup |
|---|---|---|
| few, or dictionary-encoded | zone map and dictionary probe | one values child per chunk the zone map keeps |
| up to about 10⁵ per 16 blocks | Bloom filter tree | `1 + 16 · log₁₆ blocks` filters, output-sensitive |
| many, or a range or an order | one sorted run per object, with fences | two fence pages and two segments per object touched |
| the dataset's lookup key | the **clustering key** | one object per level, then its run: the only structure that bounds the objects |

`Auto` gives up a Bloom filter on a column whose first node passes a filter's capacity, where a sorted
run is the structure ([10-indexes.md](10-indexes.md) §6).

### 6.6 Order beyond the lookup

The trees are ordered by the clustering key, so the order of **that one key** is bounded everywhere:

| need | mechanism | cost |
|---|---|---|
| walk the objects by key | an in-order walk of a level's tree, the next pages prefetched eight ahead | O(log N + objects touched), about one dependent round trip per window of eight pages |
| rows in key order, `OrderBy` | a merge of each object's key-ordered read; inside a level above 0 the objects are key-disjoint | at most 8 + L objects open at once |
| rows by position, `Rows(a, b)` | the row sums of every node | O(log N) plus the objects the range touches |
| count and rank on the clustering key | objects whose summaries lie wholly inside the range counted unopened; the core's exact cover in the boundary objects | at most level 0's objects plus two per level opened |
| an order on another column | no tree helps | one cursor per object the summaries cannot refute |

**An object opens when it could hold the next row, not before.** Objects are offered in the order of a
lower bound on their keys — on the clustering key, upward, a leaf key is the object's exact minimum —
and one is opened only once that bound is at or below the smallest key an open input holds. So a
consumer that stops after `k` rows opens no object whose minimum lies past the k-th key: `LIMIT 10`
opens one object of four. Measured on the same rows: four cursors over four interleaved level-0
objects, one over the key-disjoint level a compaction made of them. **A float has no such bound**,
since a zone's minimum and maximum exclude NaN while the key order places NaNs at the ends; float
objects open before the first row, and an object whose first row lies below the bound it was ordered
by raises `VortexFormatException` rather than deliver a wrong order. Ties between objects follow the
dataset's order, so a descending read is the exact reverse of an ascending one.

**A key cursor walks both ways.** Walking down is the same merge with a max-heap. The objects of a
level above 0 are key-disjoint, so the minimum of the next object of the level is a strict upper
bound on an object's keys, and the object opens only once the walk passes below it; level 0's
objects, which nothing but their summaries bounds from above, open at the seek, at most eight and the
lag. A step against the walk's direction seeks every object again just past the current entry, whose
place in the order is its key and then its row in the dataset: an object before it in the dataset's
order lands at or before the key, one after it strictly before, and its own object steps. That is the
flip every merging iterator pays ([12-index-reads.md](12-index-reads.md) §3.2), and the walk down is
the walk up reversed, ties included.

Any other order costs, at best, one cursor per object the summaries cannot refute: `ORDER BY x LIMIT
k` prunes by the summaries, which is good in practice and output-sensitive, never bounded. A second
bounded order is a second copy of the data clustered by that key, a dataset of its own; a global
secondary index is excluded, since every compaction would rewrite it.

## 7. Identity and integrity

- **Every postscript this library writes mints a uid**: sixteen random bytes in the metadata entry
  `vorticity.identity`, written right after the footer, so the open's 64 KiB tail read covers it and
  reading it costs no request. It identifies a **version of the bytes**, not a file: an append in
  place mints a new one, and a fragment bound to the old uid is refused. It costs 72 to 96 bytes a
  file; `VortexWriteOptions.Identity` pins it for a reproducible write.
- **The store's token** — a version id or an entity tag, or on a file system the length and the
  modification time — is recorded when the store gives one and compared as an opaque string. It is the
  only binding for a foreign file without a uid; on a file system it is a heuristic, and the entry
  says so. `VortexOpenOptions.IndexFragmentsNeedIdentity` refuses a fragment bound so.
- **The content hash is the writer's.** The writer sees every byte it emits, so an XXH3-128 of the
  object costs nothing then and is recorded in the leaf entry. **No reader computes it**: `verify`
  does, offline.
- **Pages are addressed by their XXH3-128**, and a commit object carries an XXH3-64 over its header
  and table: integrity, deduplication and idempotent retries in one mechanism. A torn page or header
  makes its version unreadable, with the reason; a corrupt fragment or index region claims nothing; a
  corrupt data segment is a `VortexFormatException`. XXH3 guards against accident, not forgery.

## 8. The commit protocol

### 8.1 A commit is one conditional creation

A writer reads the latest commit `N`, uploads its data objects under fresh uid keys, in parallel,
then creates `commit/<inverted N+1>` — header, pages and fragments in one object — with the store's
**put-if-absent**, streamed from a pipe whose length is given up front, so the store chooses between
one request and a multipart upload. Put-if-absent linearizes commits: no lease, no lock, no external
service. Uncontended, a commit is **three dependent requests**: the listing, the read of `N`'s header,
the creation; the data objects cost the commit nothing more.

### 8.2 Rebase by re-applying operations

If `N+1` exists, the writer reads it and **re-applies its logical operations** to that version's trees
— add objects, add fragments, drop fragments, replace objects by a compaction's outputs — and never
merges two trees. Each operation carries its own answer to a moved ground:

| concurrent operations | result |
|---|---|
| append, append | both objects land in level 0 |
| append, compaction of level 0 | the compaction took a snapshot; the new object is not among its inputs and stays |
| indexer, indexer on the same range | the second finds the same fragment already there and writes nothing |
| indexer, compaction of that object | the object is gone; the fragment is dropped and never written |
| compaction, compaction on overlapping inputs | the second finds an input missing and abandons; its outputs are garbage |
| vacuum, anything | vacuum deletes only what no retained version references and what is older than the window (§10) |
| reader, anything | the reader holds a version; everything it references is immutable |

An object's key ends with its uid, which makes it unique and a function of the object: two appenders
that read the same version would otherwise compute one key, and the second would replace the first's
entry, which an interleaving fuzzer caught. Because a commit header carries the pages it wrote and the
pages above them it read, a rebase iteration is also three dependent requests while the tree fits the
header. A writer that loses too many times (`DatasetOptions.MaxAttempts`) is told to use a
coordinator, in the exception's own words.

### 8.3 The latest version in one request

Commit keys sort newest first, so the first key a listing of `commit/` yields is the whole discovery,
and the listing stops after one page. That needs a strongly consistent listing, which S3 has had since
2020 and which §11 requires of any store. A reader that wants to read its own writes is handed the
version by its writer. There is no hint object and no probe loop.

## 9. The read-path budget

### 9.1 Dependent round trips, not bytes

On an object store a request costs 10 to 100 ms whatever its size below a megabyte, so the budget is
**dependent round trips**. Cold, a point lookup on the clustering key through a sorted run:

| step | dependent requests | cached by |
|---|---|---|
| list the latest commit, read its header | 2 | version |
| descend a level's tree | 0 up to ~650 objects, 1 up to ~400 000, 2 up to ~280 million | page, forever: pages are immutable |
| open the object's tail | 1, in parallel across the ≤ 8 + L objects touched | object |
| its fences, then the two payload segments | 1 + 1 | run |
| the data segments | 1, coalesced | — |

About **6 to 8 cold and 2 to 3 warm**, for any number of rows and objects. Latency does not choose the
structure; it sets the page cap, the inlining, the one-object commit, the in-process cache of
immutable headers, pages, tails and fences, and the parallel requests. Hedged requests belong to the
store library.

### 9.2 Held by a counting store

- **One file, any size**: opening a file and answering a count through a sorted run in fence pages, a
  scan through a filter tree, a key seek and the roots costs the same **13 requests and 415 811 bytes**
  on a sparse 1.31 GiB file and a sparse 13.1 GiB one (`ReadBudgetTests`). A content hash on the read
  path would turn that into 1 157 against 11 525 requests, and the test would fail.
- **Any number of objects**: a cold clustering-key lookup costs **2, 2 and 3** requests over datasets
  of 1, 1 000 and 100 000 objects, and one object added costs **3, 3 and 4** dependent steps
  (`DatasetBudgetTests`).
- **The critical path**: with 20 ms of injected latency, the lookup waits for its round trips one after
  another, which no count of requests can show.

## 10. Lifecycle

- **Vacuum** (`VacuumAsync`, explicit, never automatic) marks everything the retained versions
  reference — a subtree already seen is not walked again, so the versions cost their distinct pages —
  then deletes the unmarked commit objects and data objects older than the **retention window**,
  seven days by default (`DatasetOptions.RetentionWindow`), keeping the latest version always. What
  ages is the moment a version was superseded, by the store's own clock. An unreferenced object
  younger than the window is a writer in flight and is kept: **a writer must commit within the
  window**. Only `commit/` and `data/` are swept; an imported file is its owner's. A reader that
  outlives the window gets an `ObjectNotFoundException` naming the version it was reading, never a
  partial answer. A dry run reports without deleting.
- **Repack**: a commit object kept alive by a few live pages among dead ones is reported as sparse, and
  a metadata-only commit copies those pages and fragments into itself, so the old object can go at the
  next vacuum.
- **Verify** (`VerifyAsync`, offline) checks every object's length, content hash, rows and identity;
  every fragment against its reference and with its object; every commit object whole; and it reads
  the pages from the store, not from a header's inlined copies, which is the only way to see a torn
  stored page a reader never reads. It reports every problem by name and throws none. Given an older
  version, it walks the two trees' difference only, O(changed pages).
- **Rebuilding an index** drops an object's fragments and attaches one over the whole object in the
  same commit, so no version holds the object with neither; rewriting the object, which a compaction
  does, embeds it instead.

## 11. The store abstraction

`IObjectStore`, in the dataset package, is what an S3 library implements:

| operation | used by |
|---|---|
| `GetRangeAsync(key, offset, length)` → bytes and the object's token, in one answer | every read; a data object's segment source plans its reads with the core's coalescer and issues them together |
| `HeadAsync(key)` → size, token, the store's creation time | opening; vacuum's ages |
| `PutIfAbsentAsync(key, pipe, length)` → created or exists | every write, streamed |
| `DeleteAsync(keys)` | vacuum, a batch at a time; an absent key is not an error |
| `ListAsync(prefix, startAfter)` | the latest version, one page (§8.3); vacuum |

A store must document an atomic put-if-absent, a strongly consistent listing, a token that changes
whenever the bytes under a key change, and ranged reads on objects of any size. Three shapes belong to
the seam: a put **streams** and creates nothing when its content ends early or runs long; a delete
takes a batch and reports nothing; a listing pages by itself as it is enumerated. Retries, hedging,
connection pools and credentials are the store library's and never this one's.

Three stores ship: `FileObjectStore`, whose put-if-absent is the kernel's create-new, and whose
content a crash may leave short, which the format detects; `MemoryObjectStore`, with injectable
latency, failures and crashes after a put; and `CountingObjectStore`, a decorator counting requests,
bytes and the **dependent steps** of the critical path. The contract is a test suite,
`ObjectStoreContractTests`, which an S3 library runs against its own store.

## 12. Alternatives weighed

| question | chosen | rejected, and why |
|---|---|---|
| metadata structure | a prolly tree per level | a flat manifest per version (O(N) per open and per commit); checkpoint and deltas (the checkpoint is still the list); a manifest list of manifests, as Iceberg (fine to 10⁶ files, not a bound); an external catalog (a service dependency) |
| prolly or B+tree | prolly | same lookup and commit costs; the prolly adds history independence, deduplication across writers, a one-hash equality of datasets, an incremental diff across lineages, and a rebuild-from-scratch oracle, for a chunker of about a hundred lines |
| the latest version | newest-first keys and one listing | a hint object and probes (O(lag), a mutable object); a pointer updated by compare-and-swap (one more mutable thing) |
| concurrency | put-if-absent, rebase by operations | leases and lock files; three-way merges of trees |
| data across objects | leveled on a clustering key, tiered without | no compaction (O(appends)); a global secondary index (rewritten at every compaction) |
| index placement | in the file, external fragments transient | in-file only (adding an index rewrites data); external only (the index no longer travels with the file) |
| binding | uid and store token; the writer's content hash for verify | a content hash on the read path (reads the data) |
| metadata objects | one commit object per version | an object per page or per pack (sequential reads, orphans after a crash, two creations per commit) |
| Iceberg or Delta as the table layer | — | their data formats are Parquet, ORC and Avro; a Vortex data file would make the table unreadable to every engine that reads them, which is the only reason to adopt them |

**Not in this design:** deleting or updating rows, which an LSM on a key makes natural and the design
leaves room for; a clustering key picked automatically, since it decides the write amplification and
is the user's to state; schema evolution, where the header records one schema and an object with
another is refused.

## 13. How it is tested

- **The counting matrix** of §9.2.
- **An interleaving fuzzer** over the in-memory store: readers, writers, indexers, compactions and
  vacuums in seeded schedules, commits prepared on a stale version and crashes after a put; every read
  must equal a scan with indexes and summaries off on the same version, and every version a vacuum
  retains must verify whole.
- **The rebase matrix**, every row of §8.2.
- **The invariant** of §5.2 after every step of a randomized append stream drained at random moments.
- **Progressive indexing**: at every intermediate commit, the answers equal those without indexes.
- **The tree's oracles**: incremental edits equal a rebuild from scratch, byte for byte, and the same
  operations in two orders give one root hash.
- **Order**: key-ordered reads across levels equal a sort of the rows, in both directions, with and
  without summaries; a key cursor's walk down is its walk up reversed, a step against its direction
  lands next to the entry before it, and its seeks below a key land where one sorted file's would.
- **Tampering**: an object replaced at equal size, a page, a root and a fragment torn at every byte, a
  fragment of another object: readers answer exactly, and verify names each.
- **Rust**: compaction outputs are among the files the cross-check hands to Rust 0.86.1.
