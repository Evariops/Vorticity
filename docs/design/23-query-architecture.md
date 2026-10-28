# The query architecture: fast by construction

This document describes how Vorticity datasets answer questions: how rows are laid out, what each data object carries,
how a question is planned and executed, and why. It was adopted on 2026-10-27 against `main` at `94d3f5ec` and built in
the phases of [section 13](#13-how-it-was-built), which also says where the code stops short of it. The documents that
describe each part ([13-dataset.md](13-dataset.md), [15-compaction.md](15-compaction.md), [16-queries.md](16-queries.md),
[22-on-the-fly-and-synopses.md](22-on-the-fly-and-synopses.md)) describe the same code in more detail. Names of types and
members are the API's.

The idea fits in one sentence: every question is made cheap by construction, through the order of the rows, the unit
the data is addressed by, and the form in which it is executed, and precomputed views come on top of that, never in its
place.

Figures marked *computed* come from the cost model of [section 2](#2-the-cost-model) applied to the reference table of
[22-on-the-fly-and-synopses.md](22-on-the-fly-and-synopses.md#12-the-reference-table). Figures marked *measured* come
from the benchmarks named next to them.

---

## 1. Goals

1. **Any property at the cost of its own bytes.** A column, or a path inside a variant column, is filtered, grouped and
   aggregated by reading that property and nothing else, pruned and located per granule, with no budget of columns and
   no cap on the number of paths.
2. **Fast by construction.** The layout, the per-granule structures and execution on encoded data carry the common
   shapes of question, with no index or view to declare. Automatic, exact views carry what no layout can, such as long
   windows over everything.
3. **Everything decoupled.** The partition from the sort key. The sort key from its indexed prefix. The granule from the
   chunk. Decoding from requests. The logical properties from their physical placement. Index analysis from reading.
   The plan from its execution and its cost. The engine from its sources. Storage from compute.
4. **The rules that already hold keep holding.** Every data object is a plain Vortex file that a strict Rust reader
   opens, with this library's structures invisible to it. The dataset depends on no service, only on a store interface.
   Answers are exact, and sums are identical whatever the split. The library carries no notion of time: the application
   declares functions (a truncation, a bucket), and the library applies them.

---

## 2. The cost model

A question runs in dependent steps. Each step issues requests in waves of the requests in flight, transfers bytes, and
folds values:

> **time ≈ Σ over the steps of ⌈requests / in flight⌉ × latency + bytes / bandwidth + values / engine rate**

| parameter | value |
|---|---|
| latency of a request, object store | 30 ms |
| requests in flight per node | 128 |
| bandwidth from the object store, per node | 1.25 GB/s |
| bandwidth from the local disk, per node | 3 GB/s |
| engine rate, a filtered aggregation, per node | 5 × 10⁹ values a second; a run counts as one value |
| network between nodes | 10 Gb/s |

The reference table holds 4.32 × 10⁹ rows and about 173 GB a day, about 40 bytes a row.

Every mechanism below attacks one term:

| term | what reduces it |
|---|---|
| bytes | the sort key turning columns into runs; reading a granule instead of a chunk; reading a property instead of a row |
| requests | larger objects where reads are broad; index objects per partition where lookups are narrow; metadata kept warm by its owner |
| dependent steps | each structure findable from the tree of commits without opening a data object |
| values | execution on runs, dictionary codes and constants; predicates evaluated on encoded values |
| nodes | distributed execution over immutable objects |

---

## 3. Units, one job each

| unit | its job | its size | who decides |
|---|---|---|---|
| **partition** | lifecycle (expiry of whole objects), coarse pruning, the scope of merges and per-partition structures | the value of an expression, such as a day | the application |
| **object** | immutability; the unit written and replaced | up to 1 GiB by default | the application, within its partition |
| **sort key** | locality and compression: the order of rows inside an object | components: columns, functions of columns, variant paths | the application, advised by the library |
| **indexed prefix** | what the sparse key index keeps | the first components of the sort key | the application |
| **granule** | pruning and addressing: the unit of every index, every piece of metadata and every offset | 8 192 rows, the format's zone | the format |
| **decode block** | decoding without the rest | 1 024 values, a FastLanes block | the encoding |
| **chunk** | the file format: one segment per column | about 1 MiB per column | the writer |
| **request** | the network: one byte range read at once | from a few KiB to 8 MiB | the reader, from the store's cost profile |
| **task** | parallelism: a part of a question, a range of the dataset's rows | four a node by default | the coordinator |
| **owner** | the warm metadata and the index analysis of an object | one node per object | rendezvous hashing of the object's identity |
| **property** | what a question reads: a column or a variant path | a stream of the object | the schema, and for a path, the objects that hold it |

---

## 4. Laying out the rows

### 4.1 The partition

A partition is the value of an expression the application declares over any column: the column itself, a truncation, a
bucket. No object holds rows of two partitions, fresh objects included. The library compares partition values and never
interprets them: a day, a tenant or a range of identifiers are all the same to it.

The partition carries the lifecycle (an expiry removes whole objects without reading them), the coarse pruning (a filter
on the partition's source column becomes a range of the tree of objects), and the scope of merges and of the structures
kept per partition. It does not carry the order of rows: that is the sort key's job.

### 4.2 The sort key and its indexed prefix

A sort key is a sequence of components. Each is a column, a non-decreasing function of a column (a truncation, a
bucket), or a path of a variant column read in a class. Nulls sort last within each component. The application declares
the key; the library applies functions it already knows and extracts paths it already shreds. A non-decreasing function
maps a range of its source to a range of its value, which is all pruning needs.

The **indexed prefix**, the first components of the key, is what the sparse key index keeps
([section 5.2](#52-the-sparse-key-index)). The components after it still order the rows, at no cost to the index.

The library builds no composite structure of its own: its indexes and metadata are per column or per path. The sort key
is the one composite structure, and the application declares it.

### 4.3 An idiom, and what it does to the columns

A useful shape for event data is a coarse grain as the partition, then the entity, a fine grain, the low-cardinality
columns, and the instant. For the reference table: partition `At.Truncate(Day)`, sort key `(Service, Version, Host,
At.Truncate(Hour), Status, Endpoint, At)`, indexed prefix `(Service, Version, Host, Hour)`. The hour and the day are the
application's choices; the library sees functions.

Within each hour of an emitter, about one granule of rows, the rows are ordered by status and then by endpoint. These
columns become runs, about 65 to a granule *(computed)*:

| column | bytes a row, ordered by emitter and instant *(measured)* | under the idiom *(computed)* |
|---|---:|---:|
| `Service`, `Version`, `Host` | 0.03 | ~0.003 |
| `Status` | 0.65 | ~0.002 |
| `Endpoint` | 1.24 | ~0.02 |
| `Region`, `Method` | 0.23 | ~0.005 |
| all columns | 20.45 | ~18.2 |

This is not precomputation; it is the order of the rows. Every question that filters or groups these columns reads
about 60 times fewer bytes for them, and the engine folds a run as a weighted row
([section 6.1](#61-runs-codes-and-constants)).

### 4.4 Sorted objects, from the first write

Every object a dataset writes holds one partition and is sorted by the sort key, appended objects included. An append
splits its rows by partition, sorts each buffer in memory, and writes one sorted object per partition and per buffer,
the way a database sorts an insert block. Encoding the key and sorting rows costs 51 to 99 ns a row on batches of
4 096 rows *(measured, `RowEncodingBenchmarks`)*, about 0.25 to 0.5 s for a buffer of 256 MiB on one core
*(computed)*: about 1 % of a core at the reference rate. Measured on whole appends, sorting costs 594 ns a row on
appends of 6 250 rows, some 3.7 ms an append, a fixed cost that larger appends spread: 49 ns a row on one append of
2 × 10⁶ rows *(measured, `vortex-queries --micro layout`; the second once, when the sort was built)*. An append that
touches more partitions than a bound (100 by default) is refused before anything is written.

Sorted objects make every other structure simpler: the sparse key index bounds a lookup in any object, fresh or not, and
no sorted run has to be built beside an unsorted object.

### 4.5 Organizing a partition

Within a partition, objects of similar size merge: when a partition holds `k` objects of one size class (`k` = 8 by
default), they merge into one object of the class above, up to the object top size. When the partition is calm (no
append for an hour by default, or on demand), a **consolidation** rewrites it into objects disjoint by key, in parallel
over key ranges taken from the objects' sparse indexes. An entity's rows then lie in one object per partition.

Rows that arrive late for a consolidated partition land as small objects of that partition and wait in its size classes;
they are merged into the consolidated objects they overlap once they weigh an eighth of them, which bounds a late row at
about nine writes. A partition that never becomes calm is reported rather than consolidated over and over.

| | key-disjoint levels | tiered merges and consolidation |
|---|---|---|
| writes a row, key led by the emitter, appends of 64 to 100 MB | ~20 to 25 *(computed)* | **2**, **3** once consolidated *(computed)* |
| objects of the reference table | — | ~220 in an active day, ~161 in a consolidated day |
| a lookup by key | at most one object a level | one object per consolidated partition, located by the tree of objects |

Key-disjoint levels remain an option, and the default for a dataset with a sort key and no partition, whose single
partition never becomes calm. On 48 hours of requests at 20 a second appended every 30 seconds, partitioned by hour, a
row is rewritten 2.92 times when the keys come in order, 1.94 times by the size classes and 0.98 times by the
consolidation, and 2.89 times when four writers append and a row in fifty comes late *(measured, `vortex-queries --micro
lifecycle`)*.

### 4.6 The declaration, and its defaults

```csharp
DatasetOptions options = new()
{
    Layout = new DatasetLayout
    {
        Partition = LayoutValue.Column("At").Truncate(CalendarUnit.Day),
        SortKey =
        [
            LayoutValue.Column("Service"),
            LayoutValue.Column("Version"),
            LayoutValue.Column("Host"),
            LayoutValue.Column("At").Truncate(CalendarUnit.Hour),
            LayoutValue.Column("Status"),
            LayoutValue.Path("Attributes", "http.route", VariantClass.String),
            LayoutValue.Column("At"),
        ],
        IndexedPrefix = 4,
    },
};
```

| setting | default | what it governs |
|---|---|---|
| `DatasetLayout.ObjectBytes` | 1 GiB | the largest object a merge or a consolidation writes |
| `DatasetLayout.MergeFanout` | 8 | objects of one size class merged at once |
| `DatasetLayout.CalmAfter` | 1 hour | when a partition is consolidated |
| `DatasetLayout.LateShare` | 8 | late rows wait until they weigh an eighth of what they overlap |
| `DatasetLayout.Organization` | tiered with a partition; key-disjoint levels with a sort key and no partition | how objects are organized |
| `DatasetOptions.AppendBufferBytes` | 256 MiB | the in-memory sort buffer of an append |
| `DatasetOptions.MaxPartitionsPerAppend` | 100 | the partitions one append may touch |

The partition, the sort key, its prefix and the organization are fixed when the dataset is created. The object size,
the fan-out, the calm and the share are policy, which a commit may change; the buffer and the partitions an append may
touch are each handle's.

---

## 5. The object: a Vortex file addressable to the granule

### 5.1 What every object holds

- the rows of one partition, sorted by the sort key;
- the sparse key index of its indexed prefix ([5.2](#52-the-sparse-key-index));
- a chunk directory with each chunk's encoding tree and the granule offsets of its variable-length buffers
  ([5.3](#53-reading-a-granule-not-a-chunk));
- ordered dictionaries for its dictionary-encoded columns, where they cost less than a dictionary per chunk
  ([5.4](#54-ordered-object-dictionaries));
- the format's zone maps, one zone a granule.

Everything beyond the format lives in the file's index directory, which a Rust reader ignores. The entry of the object
in the tree of commits names each structure, so a reader finds any of them without opening the object.

### 5.2 The sparse key index

For each granule, the indexed prefix of its first row, front-coded against the granule before it, in pages of 128
granules (`vorticity.key.index.v1`). A reader finds it through the object's entry, without opening the object, and reads
its pages in one request. A binary search finds the granules of a prefix. A filter on a
component that is not leading is judged on ranges of granules: when the bounds of a range share their first `j`
components, the component after them is bounded by theirs, and the filter is judged on that box; ranges the box refutes
are skipped, the others split and judged again.

It costs at most ~16 bytes a granule: ~52 KB for an object of 1 GiB, ~8 MB a day and ~3.3 GB over 13 months of the
reference table *(computed)*.

### 5.3 Reading a granule, not a chunk

A chunk holds about 1 MiB of a column. A selective read fetches only the bytes of the granules a question keeps, and
decodes only the rows it needs, a selection nearly every encoding decodes on its own.

The encodings say where those bytes are:

| encoding | the bytes of one granule of 8 192 rows | what is read once per chunk |
|---|---|---|
| bit-packing, frame of reference, zigzag, dictionary codes | `1 024 × w` bytes for a width of `w` bits (`128 × w` per block of 1 024) | the patches, which are small |
| ALP | those of its integers | its exceptions |
| run-end | the runs that overlap the granule | the run ends, about 2 bytes a run |
| sequence, constant | nothing | nothing |
| FSST, binary, string views | the bytes of the granule's rows | the granule's offsets, stored in the chunk directory |
| zstd | its frame (one frame a block) | the frame sizes, in the encoding tree |
| pco | its page of 8 192 values | the chunk's metadata |

The **encoding tree** of a chunk, the format's own description of its encodings and buffers, is stored in the object's
marks (`vorticity.marks.v1`), together with the start of each granule in the chunk's variable-length buffers, the only
offsets that cannot be computed; where each chunk lies is in the paged chunk directory. A reader plans the byte ranges of
the granules it keeps, joins ranges closer than a request is worth (256 KiB on an object store, a few KiB on a local
disk), reads a chunk whole past three quarters of its bytes, and decodes the ranges in place.

A granule of a column of width `w` then costs about `w` KiB instead of about 1 MiB; a row found by an identifier costs
about 0.3 to 0.4 MB for eleven columns *(computed)*, 0.3 to 0.75 MB in four or five dependent steps *(measured,
`DatasetGranuleReadsTests`)*. On an object store, where requests dominate below about 300 KB, the gain shows in memory
and on local disks: a cache holds 10 to 100 times more granules than chunks.

### 5.4 Ordered object dictionaries

A dictionary-encoded column of text, binary or integers keeps its values once per object while they fit in 65 535 values
and 1 MiB, through the format's dictionary layout, and **its values are sorted**: a smaller code means a smaller value.
Every writer of a dataset knows its values before it writes its codes: an append sorts its buffer, and a merge starts
from the sorted dictionaries of its inputs. The format does not fix the order of a dictionary's values, so a Rust reader
reads these files unchanged.

- A predicate is judged once per dictionary per question, not once per chunk.
- A range, a prefix or a set of strings becomes a range or a set of codes, judged on the packed codes.
- Minimum, maximum and order are read on the codes; a grouping keeps one table from code to group for the whole
  dictionary.
- A merge reads its inputs' dictionaries from their tables of contents, without opening them, and declares their union,
  sorted, to each object it writes. The writer codes a column that reaches it dictionary-encoded through a table from
  its codes to the union's, built once by merging two sorted lists, and any other by looking its values up.
- A locating index over a dictionary column stores codes, the dictionary itself being the sorted list of keys.

The writer prices an object dictionary against a dictionary per chunk with the exact formulas it already uses, since an
object may hold more distinct values than a chunk and need wider codes. The dictionary's values lie right before the
column's first chunk, so that a read of that chunk, the column's only one in an object of a few mebibytes, takes them in
the same request; the planner prices a column's reads with them.

### 5.5 Metadata per granule, and locating indexes

| structure | for | cost a day, reference table *(computed)* |
|---|---|---|
| zone map per column (minimum, maximum, nulls, NaN) | any comparison, any range | ~120 MB, 0.07 % |
| zone map and presence per variant path | a filter on a path; skipping granules a rare path is absent from | proportional to where the path is present |
| histogram of a measure per granule, on a log-linear grid (k = 3, ±5.9 %) | thresholds and quantiles per entity and fine grain | ~37 MB a measure |
| locating index: each value of a repeating column or path, to its granules | equality, sets, prefixes, ranges, without visiting every granule | ~60 MB, 0.03 % |
| identifier postings: the hash of each value, to the granules of the identifier's stream that hold it | finding one row among billions | ~3.4 bytes a row of a unique identifier, ~8 % |

Yield rules decide, per object, what is kept: a value present in more than three granules in four is marked everywhere
rather than listed, a column whose values make more than 256 pairs of a value and a granule a granule on average keeps no
locating index, and a granule lists its distinct values only when it holds 256 or fewer. Budgets apply to metadata,
never to data. Without identifier postings, all of it weighs about 0.17 % of the data.

### 5.6 Index objects per partition

Objects of 1 GiB mean about 161 objects for a consolidated day, and a lookup that consults one index per object pays one
request per object: about 4 830 requests for 30 days. So a consolidation also writes an **index object for the
partition**, which holds the locating indexes, the identifier postings, the presence and the histograms of the objects it
covers, arranged by value (or by hash) for what a question looks up, and by object and granule for the metadata. The
sparse key index and the zone maps stay in the objects: the tree of objects already narrows a lookup by key to one
object.

The index object names the objects it covers. An object rewritten later, by a purge of deleted rows, leaves that
coverage and carries its own index regions, like a late object, until the next consolidation rebuilds the index object.
A lookup over 30 days then reads 30 index pages instead of about 4 830, for about 7 more bits per identifier posting
*(computed)*.

---

## 6. Computing on encoded data

### 6.1 Runs, codes and constants

The engine executes on the encoded forms ([16-queries.md](16-queries.md#9-execution)): a constant once, a run as one
weighted row, a dictionary by its codes, a frame of reference with its base applied once. Under a sort key such as the
idiom of [section 4.3](#43-an-idiom-and-what-it-does-to-the-columns), most low-cardinality columns arrive as runs. A count
of server errors by endpoint over a day then folds a few million runs rather than 4.32 × 10⁹ rows, about 0.05 s of one
core *(computed)*. This is the design's largest saving of processor time, and it needs no precomputation: it comes from
the order of the rows meeting execution on encoded data. Each phase keeps it: a partial read of a run-end column yields
runs, and a filter hands ranges of rows, not one verdict a row, to what follows it.

### 6.2 Predicates evaluated in the encoded domain

A comparison with a constant, a range, or a set is answered by the encoding itself where it can be:

| encoding | how | decode, then compare *(measured decode)* | in the encoded domain *(computed)* |
|---|---|---:|---:|
| bit-packing | compares each value as it is unpacked, in registers, one 1 024-bit mask a block | ~0.48 ns a row | 0.035 to 0.21 *(measured, 5 to 45 bits)* |
| frame of reference | subtracts the reference from the constant, then as bit-packing; a constant outside the chunk's range settles every row | ~0.82 | ~0.15 |
| ALP | encodes the constant with the chunk's exponents, compares the integers, handles exceptions apart | ~0.8 to 1.2 | ~0.2 |
| sequence | intersects the progression with the range: two bounds a chunk | ~0.45 | constant |
| sparse | judges the fill once and the present values only | 0.15 + 3.2 per present value | 0.28 + ~7 per present value *(measured)* |
| run-end, dictionary, constant | once per run, per value, per chunk | — | already so |
| FSST | equality and prefixes on the compressed codes; containment by an automaton over the symbols | 14.7 to 17.6 for a containment | 10.4 to 11.5 when few rows hold the pattern *(measured)* |

The decode costs are measured on one core ([benchmarks-x64.md](../guide/benchmarks-x64.md), decoding per encoding:
0.14 ns a row for a constant, 0.20 for run-end, 0.28 for bit-packing, 0.62 for frame of reference, 0.60 to 0.97 for
ALP, 0.91 to 1.34 for a dictionary, 12.1 for FSST); the FastLanes unpacking kernel alone runs at 0.07 to 0.13 ns a
value. Each kernel answers exactly what decoding and comparing would, three-valued and row for row, and is checked so on
the corpus of its encoding. The measured figures are the best of short runs on one core with AVX-512: the bit-packing
kernel at 5 and 45 bits, a sparse column of two million integers at 0.1 % to 50 % present
(`vortex-queries --micro sparsejudge`), and a containment on a million URLs of 51 bytes, which costs about what
decompressing and searching does when a tenth of the rows hold the pattern or more.

### 6.3 Two passes, and late materialization

Masks per block of 1 024 rows tell the second pass of a filtered read which blocks still hold a row, and only those are
decoded, through the selective decoders the encodings already have. For scattered survivors at a selectivity of 10⁻⁴,
that is 9.7 % of the blocks of 1 024 against 56 % of the granules *(computed)*. A second pass runs only when it spares
what it costs: while the blocks kept lie farther apart than the profile's joined gap in the widest column it reads,
which the summaries estimate before the first pass and the first pass follows. Under an order and a limit, the column
of the order is read first, the positions kept, and the other columns read for those positions alone.

---

## 7. Variant properties as streams

Attributes whose keys are data live in variant columns ([21-variant-columns.md](21-variant-columns.md)). In this design,
every path of an object is a stream of its own, of one of four kinds:

| kind | for | form |
|---|---|---|
| dense | a path whose dominant class the object holds 2 048 values of or more | a typed column of the shredded variant |
| sparse | such a path in rows nine in ten of which lack it | the present values and their positions |
| rare | a path held fewer than 2 048 times | the binary residual, its granules in the path directory |
| keys that are data | an attribute bag whose keys are themselves values | hash buckets |

There is no budget of columns and no cap on paths; budgets apply to metadata alone. Each path has its presence, zone
maps and locating index per granule, under the yield rules of [section 5.5](#55-metadata-per-granule-and-locating-indexes).
Paths that questions filter and group on are promoted to typed streams with their indexes at the next merge, learned
from the questions asked rather than declared: a path a handle's questions name three times. A declared path may be a
component of the sort key. A filter on a sparse path costs its present values
([section 6.2](#62-predicates-evaluated-in-the-encoded-domain)), and a question on a path reads that path's streams and
nothing else.

---

## 8. Planning a question

### 8.1 The logical query and its units

Whatever API asks it, a question becomes a logical query: a source, a filter, the properties it reads, a shape (rows in
an order and a limit, an aggregation, a count, an existence, an extreme, a walk of keys) and its options. It says what
the question wants, never how. Its physical units nest: the version of the dataset, the runs of rollups, the partition,
the object, the range of granules.

### 8.2 Index analysis

Partitions are kept by the filter on their source; objects by the summaries in the tree; granules by the sparse key
index, the zone maps, presence, locating indexes and postings, consulted cheapest first. A structure is consulted only
when it costs less than reading what it could exclude. The sets it returns are combined: `AND` intersects, `OR` unites,
and `NOT` applies only where a set is exact. The filter is evaluated again on the rows, each structure being a safe
superset.

The conjunctions of a filter are then evaluated on the rows kept from the cheapest per row excluded to the dearest: the
price of judging its column's encoding, divided by the share of rows it excludes. That share comes first from the
structures, then from the read itself, as observed selectivity reorders the conjunctions from one chunk to the next.

### 8.3 Alternatives, and the choice

Each way of answering a unit is an alternative that says whether it **covers** the unit, what it **costs** under the
store's profile, and what it **returns**: granules, states or rows.

| alternative | unit | covers when |
|---|---|---|
| entry | version, object | a count or an extreme the tree's summaries answer |
| rollup | run of objects | its totals answer the question for the whole run |
| counts | object | the object's counts per value answer the aggregation |
| learned partial | object | a partial of the question's family is held |
| granule statistics | object | the statistics of its columns over its granules answer some granules; the rest are rows |
| zones | object | the zone maps settle some granules; the rest are rows |
| rows | granule ranges | always |

The choice is the exact optimum over the tree of units, since costs are additive: a unit takes the cheapest of the
alternatives that cover it whole, of those that cover part of it plus the best of the rest, and of the sum of its
children. Every alternative is exact and sums are reproducible, so the choice changes the cost of an answer and never a
bit of it. Choosing costs about 3 ms for 30 days of the reference table and about 45 ms for 13 months, before any data is
read *(computed)*.

### 8.4 The cost profile

Each store carries a profile: latency, requests in flight, bandwidth, the bytes a request is worth (latency × bandwidth
÷ requests in flight: ~293 KB on an object store, ~4.7 KB on a local disk), the gap across which two ranges are read as
one, and the largest request. Each cache tier has its own, and a region costs the tier it sits in. The processor has
prices per encoding class, for decoding, for judging a predicate and for folding, taken from the measured decode costs
of [section 6.2](#62-predicates-evaluated-in-the-encoded-domain): a single price would be wrong by a factor of 60 between
runs and compressed text. Prices are constants of the profile that an application may change; nothing is measured at
start-up.

### 8.5 Capabilities

The engine sees a source through capabilities, each absent when the source does not have it: **rows** of granule ranges,
with pushed predicates evaluated in the encoded domain; **statistics**; **keys**; **states** for a family of
aggregations; **tasks** with their affinity; **cost**. A file, a dataset and a stream implement what they can. This
replaces a source interface with many extension points that only the dataset overrode.

### 8.6 Explanation, and plan tests

An explanation says, per unit, the alternative chosen, its cost, and why each other one lost, from the tree's entries
alone; on request, it runs the index analysis and reports the exact granules. Tests hold the plan of each question to
its execution: the alternative per unit, the granules kept, the requests and bytes issued, deterministic and independent
of the operating system.

---

## 9. Distributed execution

Nodes are interchangeable: everything lives in the store, objects are immutable, and a node holds only caches.

- **Owners.** Each object has an owner, chosen by rendezvous hashing of its identity over the nodes present. The owner
  keeps the object's metadata in memory (sparse index, chunk directory, dictionaries, index pages) and performs its index
  analysis. An object's identity never changes, so ownership moves only when nodes come and go.
- **Tasks.** A question, named and the same on every node, is cut into parts, four a node by default, each a range of
  the dataset's rows sent to the owner of the object of its first row, whose local disk is likely warm. A part a node
  fails runs again on the object's next owner, twice at most by default.
- **Exchanges.** Partials stream to the coordinator, which merges them as they arrive. Partials merge to the bit,
  whatever the split.
- **Caches.** Owner memory for metadata, local disks for chunks and regions, the store for everything. A handle's
  `DatasetOptions.Moved` hook names each version that adds objects, and `ClusterExecutor.WarmAsync` has every node read
  the metadata of the objects it now owns before their first part.

The interface, the tasks and a local executor of several nodes live in the library; a transport over TCP lives in a
satellite package (`Vorticity.Cluster.Tcp`), so the library runs no service of its own.

---

## 10. Precomputation, in its place

Some questions read too much for any layout: everything over months. They are served by views the library keeps on its
own, exact under deletes, with no declaration:

- **counts** per column and per object, with the states of measures and sketches per value;
- **rollups** per partition and per runs of partitions, for top lists and series over months;
- **histograms** of measures per granule (section 5.5);
- **learned partials**: the partial of a question's family, learned the first time it is asked, kept on the store, shared
  between nodes, and served to the questions it subsumes.

A joint precomputation of several columns, a degraded history and derived datasets remain options for an application to
choose.

---

## 11. What questions cost

Partition by day, the idiom of [section 4.3](#43-an-idiom-and-what-it-does-to-the-columns), objects of 1 GiB, index
objects per consolidated partition. Cold: an object store at 128 requests in flight. Warm: the local disk. *(computed)*

| question | how | read | cold, one node | warm, one node | cold, ten nodes |
|---|---|---:|---:|---:|---:|
| an emitter's errors per hour, 30 days | sparse index, runs, one object per day | < 1 MB | ~0.1 s | ~ms | ~0.1 s |
| server errors per endpoint, all services, 30 days | runs of status and endpoint | ~2.9 GB, ~9 700 requests | ~4.6 s | ~1 s | ~0.5 s |
| rows where a user path equals a value, 30 days | one locating page per partition, then the user's granules | ~25 MB | ~0.3 s | ~50 ms | ~0.1 s |
| one route under errors, per host, 7 days | locating index, the run of errors | ~3 MB | ~0.15 s | ~15 ms | ~0.1 s |
| a rare path per service, 7 days | presence per partition, a sparse stream judged on its present values | ~0.76 GB | ~0.6 s | ~0.25 s | ~0.07 s |
| the rows of one request, 30 days | identifier postings per partition, then a granule per column | ~3 MB | ~0.15 s | ~ms | ~0.05 s |
| p99 per service and hour, 7 days, ±5.9 % | histograms per granule | ~0.26 GB | ~0.3 s | ~0.1 s | ~0.05 s |
| top 100 endpoints, 13 months | rollups | ~25 to 100 MB | ~0.1 s | ~10 ms | ~0.1 s |
| a count by a high-cardinality path, 1 day | the path's stream, a shuffle of partials | ~13 GB | ~10 s | ~4 s | ~1.3 s |

What the table says:

- one second, cold, on one node, holds for whatever reads less than about 1 GB after pruning in fewer than about 30 waves
  of requests; on ten nodes, up to about 10 GB; beyond that, the views of [section 10](#10-precomputation-in-its-place);
- any property costs what a column costs, and a lookup on any repeating value or identifier is located, not scanned;
- the price of objects of 1 GiB is in broad cold scans of narrow columns, which pay one request per object and column.
  Distributed execution spreads those requests over nodes.

Writing costs 3 writes a row once consolidated: about 520 GB written a day for 173 GB of data, about 6 MB/s on average
*(computed; 2.89 to 2.92 rewrites a row measured on the lifecycle bench, section 4.5)*.

---

## 12. Formats and compatibility

- Every data object stays a plain Vortex file that a strict Rust reader opens: ordered dictionaries use the format's
  dictionary layout, zone maps and encodings are the format's, and the sparse key index, the chunk directory, the
  per-granule metadata and the locating indexes live in the index directory, which Rust ignores
  ([10-indexes.md](10-indexes.md)).
- Index objects per partition are objects of the dataset's store, named by the dataset's commits.
- The dataset's commit format is version 5, the only one read. During the beta, a dataset written before it is refused
  with a message that says so: there is no compatibility with earlier dataset formats.

---

## 13. How it was built

Each phase started with its specification and landed as commits that each built; the suite, its three modes and the
cross-check with the Rust reader ran once every phase had landed.

| phase | content | state |
|---|---|---|
| 0 | the format break: earlier dataset formats and their compatibility removed, commit format version 5 | done |
| 1 | the encoded core: predicates in the encoded domain, ordered object dictionaries, encoding trees and marks, reading a granule by its bytes | done |
| 2 | the layout: partition, sort key, sorted appends, sparse key index, tiered merges and consolidation, index objects per partition, reads over the new layout | done |
| 3 | the planner: logical query, units, alternatives, cost profile, capabilities | done |
| 4 | the granule end to end: two passes per block, identifier lookups, postings located at the identifier's granules | done, two gaps below |
| 5 | metadata per granule and locating indexes, combined per granule | done, one gap below |
| 6 | variant properties as streams | done |
| 7 | distributed execution | done, exchanges by gathering alone |
| 8 | read tactics and automatic views | done; the optional views on demand |
| 9 | design documents, guide and benchmarks | this document among them |

Where the code stops short of this design, measured or known:

- **Bytes of a filtered read.** The planner prices the rows of a filter that the sparse key index does not narrow from
  their columns' whole bytes, and a second pass reads whole a chunk worth less than a request, as the profile prices it,
  where the model counts its granules: a row found by an identifier takes 0.3 to 0.75 MB where the model says 0.3 to 0.4.
- **Exact sets.** The granules a filter keeps whole come from the zone maps' proofs and the granule statistics alone; the
  locating indexes and the postings give the granules a row may lie in.
- **A filtered count** decodes its columns and counts, where a scan pushes its predicates to the encodings.
- **Sparse columns** cost about 7 ns a present value, against 3.2 in the model.
- **Object dictionaries** are one per object, never one per run of chunks, and leave floats to ALP.
- **Exchanges** gather partials on the coordinator; shuffles by hash buckets and broadcasts are specified, not built, and
  an idle node takes no part from another.
- **Top lists** under an order correlated with the key decode 8 blocks of about 30 for the top ten, against about 3 in
  the model.
- **Views** that an application chooses, a joint precomputation of several columns, a degraded history and derived
  datasets, are specified and built when an application asks for them.
- **The memory of jobs** is reserved from an estimate, its inputs' rows and open chunks and its writer's targets, not
  measured: a consolidation's range over a day of logs reserves 1.4 GB for about 1.5 held, over small inputs 1.65 for
  about 1.0 ([the memory of a job](15-compaction.md#the-memory-of-a-job)). The shared pool keeps up to 256 MiB a
  processor of idle blocks parked, which the process's peak counts until the budget refuses, a minute passes without a
  rent, or the machine's memory runs high.

## 14. Decisions, and the alternatives weighed

| # | decision | why |
|---|---|---|
| 1 | A composite sort key of columns, functions and variant paths, declared by the application, with an indexed prefix | the order of the rows is the largest lever on bytes and processor time; the library still builds no composite structure on its own |
| 2 | A partition decoupled from the key, an expression of any column | lifecycle by whole objects and locality by entity, together |
| 3 | Tiered merges within a partition and a consolidation once it is calm; key-disjoint levels as an option | 3 writes a row instead of ~20 to 25 under a key led by the entity, while a key lookup stays one object per consolidated partition |
| 4 | The granule of 8 192 rows as the one unit of pruning and addressing | it is the format's zone and eight FastLanes blocks; the earlier variable-length statistics blocks go |
| 5 | Objects addressable to the granule: encoding trees and offsets of variable-length buffers only; ordered object dictionaries, translated by merges | the encodings already know their geometry; a dictionary sorted once serves predicates, ranges, groupings and merges |
| 6 | Metadata per granule and locating indexes by default, under yield rules | locating a value without visiting every granule, with nothing to declare |
| 7 | Variant properties as streams, with no budget of columns | any property at the cost of its own bytes |
| 8 | Distributed execution in the library, its transport in a satellite package | storage and compute decoupled, with no service in the library |
| 9 | An explicit plan, capabilities and a cost profile before any new alternative | one place where costs are compared, and a stable boundary between engine and sources |
| 10 | Precomputation as automatic, exact views | the long windows no layout can serve, with nothing to declare |
| 11 | No compatibility with earlier dataset formats during the beta | the new layout is not a variant of the old one |
| 12 | Predicates evaluated in the encoded domain, at the core | filters on measures cost a fraction of decoding them |
| 13 | Index objects per consolidated partition | lookups over long windows cost one request per partition, not per object |
| — | Objects of 1 GiB at most by default | smaller merges and one write fewer a row; the requests this adds to lookups are absorbed by decision 13 |

Alternatives weighed and set aside:

| alternative | why not |
|---|---|
| granules of 1 024 rows | eight times the metadata, for reads that stay bound by requests on an object store; the block of 1 024 already serves as the unit of decoding |
| key-disjoint levels as the default for partitioned data | ~20 to 25 writes a row under a key led by the entity |
| objects of 8 GiB | fewer requests on long windows, but merges of several GiB and one more write a row; index objects per partition recover the requests |
| a delta encoding of instants outside the format's editions | Rust would no longer read the files |
| one compression table shared per object for FSST | measured at 4.3 % more bytes; it is the dictionary that gains from being shared |
| a sparse key index stored as dictionary codes | the dictionary would have to be read before the index; front coding already shares prefixes |
| all columns of a granule stored side by side | one request per looked-up row, but one request per granule for any scan of a column |
| a precomputed joint table of low-cardinality columns at the heart of the design | it answers the questions it was built for; the sort key gives the same runs to every question |

---

## 15. Terms

| term | meaning |
|---|---|
| partition | the value of a declared expression on each row; no object holds two |
| sort key | the components that order the rows of an object |
| indexed prefix | the first components of the sort key, kept by the sparse key index |
| granule | 8 192 consecutive rows of an object, the format's zone |
| sparse key index | per granule, the indexed prefix of its first row |
| encoding tree | the format's description of a chunk's encodings and buffers, kept in the chunk directory |
| ordered object dictionary | a dictionary with sorted values shared by an object's chunks |
| consolidation | rewriting a calm partition into objects disjoint by key |
| index object | the per-partition object that holds the lookups and metadata of a consolidated partition |
| run | consecutive rows with one value, encoded and executed as one |
| owner | the node that keeps an object's metadata warm and analyses its indexes |
