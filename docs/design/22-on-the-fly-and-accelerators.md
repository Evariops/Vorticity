# Questions on the fly, and the accelerators beside them

How a dataset answers any question, over any columns, by computing it from its rows with the group-by
engine, and what it keeps beside the rows so that the questions the rows cannot answer in time are
still answered: per-column structures only, a single optional cluster column, and answers learned
after a question has asked them. This document replaces the time-window design of
[20-time-window-aggregates.md](20-time-window-aggregates.md), whose composite synopsis tables are
removed.

Figures marked *computed* come from the cost model of section 2 and the reference table of section
1.2. Figures marked *measured* come from the bench and from the comparison with DuckDB
([benchmarks-duckdb.md](../guide/benchmarks-duckdb.md)).

## 1. The need

### 1.1 What a question may be

- Filters on any columns: `=`, `IN`, ranges, prefixes, null, `AND`, `OR`, `NOT`.
- Groups by any columns, combined as the question wants, and by buckets of time.
- Aggregates: count, sum, mean, minimum, maximum, distinct count, quantiles.
- Over any window of time, on any column, including one added to the schema later.

No question is known in advance, and neither is which columns a question crosses. No structure may
privilege a column, and none may be composite: no sort key of several columns, no table of fixed
column crossings, no declared groupings. Those were the faults of the design this one replaces: its
composite tables answered only questions on the prefixes of their order, and weighed 4.7 to 8.8 % of
the data, 47 to 88 TB per petabyte *(measured)*.

### 1.2 The reference table

The bench's request table at the design rate, which the figures below are computed on.

| quantity | value |
|---|---|
| rows | 5 × 10⁴ a second; 1.8 × 10⁸ an hour; 4.32 × 10⁹ a day; 1.3 × 10¹¹ in 30 days; 1.7 × 10¹² in 13 months |
| bytes a row, compressed | about 40: 173 GB a day, 68 TB in 13 months |
| services | 600, Zipf-distributed of exponent 0.8: the largest holds 7.4 % of the rows, the median 0.077 % |
| emitters (service, version, host) | about 1.2 × 10⁴ |
| distinct values a day | `Status` 60, `Method` 8, `Region` 5, `Service` 600, `Version` 1 500, `Endpoint` 3 × 10⁴, `Host` 10⁴ |

`RequestId` is an identifier, one value a row. `DurationNs` and `Weight` are measures.

## 2. The cost model

A question is read in dependent steps, each issuing `Q` requests in parallel, in waves of the
requests in flight:

> **time ≈ Σ over the steps of ⌈Q / in flight⌉ × latency + bytes / bandwidth + rows / engine rate**

| parameter | value |
|---|---|
| latency of a request, S3 profile | 30 ms (median); 5 ms on a local profile |
| requests in flight | 128 by default, an option of the scan |
| bandwidth from the store, cold | 1.25 GB/s (10 Gb/s) |
| bandwidth from the local cache, warm | 3 GB/s |
| engine rate, a filtered group by on a key of few or a moderate number of values | 5 × 10⁹ rows a second a node *(model, from the measured rates below)* |

The group-by engine of [16-queries.md](16-queries.md) groups 40 × 10⁶ rows by 10³ keys in 31.1 ms on
one core and 3.6 ms on fourteen, 1.1 × 10¹⁰ rows a second, and by 10⁶ or 10⁷ keys in 60 and 61 ms on
fourteen *(measured)*. It leads DuckDB on every group by compared, by 1.2 to 6 times. The processor is
not what bounds a question on a dataset: the bytes read are.

### 2.1 What a second covers on the fly

For a question that reads two columns of a table clustered on its emitter, about 1.2 bytes a row,
cold, on one node *(computed)*:

| scope | an hour | a day | 30 days | 13 months |
|---|---|---|---|---|
| the whole table | ~0.2 s | ~4 s | ~2 min | ~27 min |
| the largest service, 7.4 % | ~40 ms | ~0.3 s | ~9 s | ~2 min |
| a median service, 0.077 % | ~30 ms | ~35 ms | ~0.13 s | ~1.4 s |

A second on the fly covers about five hours of the whole table, three days of the largest service and
a year of a median one. The structures beside the rows exist for the two cases past that frontier:
the whole table over more than a few hours, and the largest values of the cluster column over more
than a few days.

## 3. The layout of the rows

### 3.1 The order of arrival

Without a cluster column, a dataset never reorders rows by their values. Its merges put the appended
objects end to end in the order of time rather than interleaving their rows, so that an application
that appends its rows in groups, a source or a tenant at a time, keeps blocks homogeneous on what
defines its groups.

### 3.2 The cluster column

A dataset may declare one cluster column (`DatasetOptions.ClusterColumn`), any column, or none.

- From level 0's exit on, the rows of one value of the column follow each other, in the order of
  time. The fresh merges and the seal merge sorted runs as a stream, in bounded memory. The current
  hour, in level 0, keeps the order of arrival.
- The column is fixed per period: the dataset's header records it with the period it starts from, and
  a change takes effect at the next period. Every object records the column its rows follow.
- The library recommends a column from the questions it has answered: the one whose clustering would
  have saved the most bytes read ([section 9.4](#94-the-recommendation-of-the-cluster-column)). It
  never applies one by itself.

The cluster column is not a composite key. No read goes through a prefix of it: every column keeps
its own structures, and a column that does not depend on the cluster column is never served worse than
in the order of arrival. What clustering does is make the column, and every column that depends on it,
nearly constant in each block, which the structures of section 5 measure column by column. Its price
falls on reads of rows by a window of time with no filter, on old objects, which meet more ranges; an
aggregation over such a window settles its blocks instead.

o11y-line, for instance, declares its emitter as the cluster column, written as `service/version/host`
so that the emitters of a service follow each other. Every resource attribute is then constant in a
block, and only the attributes of an event vary: route, status, method, duration.

### 3.3 Blocks

A block is the run of rows of one cluster value and one hour, cut at 65 536 rows. In objects older
than 30 days, the blocks of one cluster value and one day merge into one, without reading a row.
Blocks are row ranges the block statistics describe (section 5.3), independent of the format's zones
and chunks: a block a question settles has its rows taken out of the scan's selection, and a zone
partly settled is decoded for its other rows only.

### 3.4 What clustering changes

| what | in the order of arrival | clustered by emitter |
|---|---|---|
| rows a question naming a median service reads | all | 0.077 % |
| bytes a row of `Service`, `Version` and `Host` together | 4.5 | ~0, constant in a block |
| bytes a row of `Endpoint` | 2 | ~0.75: at most 50 values in a service, 6 bits |
| bytes a row of `Status` | 0.5 | ~0.4: one value holds 86 % of the rows |
| bytes a row of the instant | 1 | ~2: the gaps between two rows of an emitter are wider |

The clustered figures are estimates, which a size test checks before any accelerator relies on them.

## 4. The dataset's read path

The engine is fed by the dataset's scan, which holds to six rules.

1. **One read a segment**, whatever the number of lanes: every lane of a question shares the reads of
   every object's segments, those of objects cut into parts included, under one read-ahead reservation
   of the question's. The fallback of the former synopsis answer read 25.8 MB up to four lanes,
   164.8 MB at eight, and hung at sixteen *(measured)*; this read path is tested at 1, 8 and 32 lanes.
2. **Filter columns first.** The zone maps drop pages; the filtered columns of the pages left are read,
   cheapest filter first (the column's bytes times its selectivity, which the column sheets estimate);
   the other columns are read only for the pages where rows are left.
3. **Ranges joined.** The pages kept of one object and one column that lie side by side are read in
   one request, up to a cap, and the next part of each lane is read ahead.
4. **Newest first.** A question over a window reads its objects from the newest to the oldest, and may
   return partial results with the share of the window they cover. A cancellation stops the reads.
5. **A local cache of data pages**, beside the cache of structures, shared between processes, with its
   own quota. A region already verified in a neighbour process's cache is kept in memory rather than
   read and verified again.
6. **Parts for fan-out.** A question can be cut into independent parts, an object and a range of its
   blocks each, whose partial results merge to the same bits: an application with several nodes spreads
   a heavy question over them.

## 5. The accelerators, all per column

### 5.1 The column sheet

For each column of each object, in the object's leaf entry: rows, nulls, least and greatest value, a
distinct-count sketch of 1.5 KB, and the 128 most frequent values with their counts (Space-Saving,
about 3 KB). Beside them, a presence filter of 9 bits per distinct value (binary fuse, 0.4 % false
positives) lies in a region of kind `vorticity.column.sheet.v1`. A sheet weighs 4.5 KB plus 1.125 bytes
per distinct value of the object: 79 KB a day for the reference table, 31 MB in 13 months *(computed)*.
It drops an object whose value is absent without reading it, and the sheets of a tree's nodes merge
as the summaries do. Writing one costs about 6 ns a column a row.

### 5.2 The counts of a column by bucket of time

For the questions on one column, the time and the measures, past the frontier of section 2.1. Each
column has entries (value, bucket) holding the rows, and the sum, least and greatest of each measure,
sorted by value then time, in chunks of 16 KB with a directory (`vorticity.column.counts.v1`).

- **Grains.** The hour for 30 days, then the day; the month in derived files outside the data objects,
  summed from the days with no row read, and recomputed when a day of the month changes. A grain
  becomes the next by adding entries. The minute is not kept: recent windows are computed on the fly.
- **Sketches.** Quantile sketches (1 KB, 1 % relative error) for the columns of at most 1 000 values a
  bucket, on the measures; distinct-count sketches of the high-cardinality columns attached to the
  columns of at most 100 values.
- **Budget.** An absolute budget per column and object; past it the most frequent values are kept,
  with an exact `others` entry.
- **Size.** Hourly counts 0.93 GB over 30 days, daily 0.67 GB, monthly 0.03 GB, quantile sketches
  0.73 GB, distinct sketches 0.27 GB: 2.6 GB in 13 months *(computed)*. Writing costs about 8 ns a
  column a row, sketches included.

### 5.3 Block statistics, and settling blocks

For each block of section 3.3 and each column (`vorticity.block.stats.v1`):

- the block's rows and range of time;
- the values present and their rows, as long as they fit in little room (at most 256 values);
  otherwise the least, the greatest and the distinct count;
- for each number column, the count, sum, least and greatest.

The engine already settles a block from its zone maps when its key maps to a single group and every
aggregate is a count, a least or a greatest ([16-queries.md](16-queries.md#93-statistics-and-zone-maps)),
but never on a dataset's objects, whose files still count the rows a delete removed, and never for a
sum, which zone maps do not hold. With block statistics, any combination of columns composes at the
question, block by block, with nothing computed for a pair of columns:

| in the block | what the statistics give |
|---|---|
| no column of the question varies | its counts and aggregates exactly, the block's own |
| one column of the question varies | exact counts, from that column's values |
| two columns vary | exact bounds, max(0, n(x) + n(y) − n) ≤ n(x, y) ≤ min(n(x), n(y)); the exact answer reads those two columns in that block |

A block no delete touches settles as a file's does; a block a delete touches is decoded, or settled
from the negative statistics the delete wrote (section 6). For the reference table clustered by
emitter: 3.5 × 10⁵ blocks a day, about 200 bytes each, 70 MB a day and 2.1 GB over 30 days, then 3 MB
a day merged by (cluster value, day), 1.1 GB: 3.2 GB in 13 months *(computed)*. Writing costs about
15 ns a row. Aggregates per value for the columns of at most 16 values a block are left out of the
first version, and weighed again against the size test.

### 5.4 Where each value lives

For each column of each object (`vorticity.column.where.v1`): the values, sorted, each with the ranges
of blocks it appears in, or `everywhere`. The threshold is on the blocks a value actually touches,
measured when the object is written, never on a count of rows assumed spread at random. A filter on a
value of the cluster column, or of a column that depends on it, reads that value's blocks only; the
same structure answers ranges and prefixes. About 0.5 MB a day, 0.2 GB in 13 months *(computed)*,
5 ns a row to write.

### 5.5 Identifiers' postings

Unchanged ([10-indexes.md](10-indexes.md)): for a column whose every value is a needle, the hash of a
value gives the pages holding it. They weigh about 4.2 bytes a row and stay on the store; a lookup
reads about 64 KB of them per object.

### 5.6 Partials learned after the fact

No crossing of columns exists until a question asks it. When a question reads rows or block
statistics, it computes at the same time, for each object it reads, the partial result of its family:
the same columns, with its equality filters on columns of few values in the object turned into groups
(10⁴ groups at most), so that the partial answers every other value of those filters too
(`vorticity.partials.v1`).

- A partial is keyed by the object, the version of its deletes and the family. An object never changes,
  so a partial stays exact as long as the object lives.
- Objects replaced without removing rows, by a fresh merge or a seal, have the sum of their partials:
  the commit already says which objects an output replaces.
- While its family is asked, a partial is computed when a new object is written, so that monitors and
  sliding windows stay exact and cheap. A family not asked for a while stops being kept up.
- Partials live in the local cache, and may be written beside the monthly files on the store, under a
  budget of 2 GiB per dataset by default; the one that saves the least per byte goes first.

### 5.7 The caches

On the local disk, shared between processes: the data pages; the structures; the partials; and, for a
filter evaluated on rows, the pages that held at least one row it kept (at most 66 KB for a day's
object), which a later question with the same filter reads alone.

## 6. Deletes, exclusions, late rows, window edges

- A delete writes, beside its marks, the counts of each column and the statistics of the blocks it
  touches, negated. A question subtracts them; the next compaction rewrites the object without the rows.
- An exclusion, every row but those of an identifier's value, finds those rows by the postings and
  subtracts their contribution. A distinct count is corrected by the counts of the counted column, a
  value leaving a group only when its last row there is excluded.
- Late rows add their own entries, which merges add up.
- A window edge that cuts a block or a bucket is exact by default: that block's rows are read. With
  `ScanOptions.AlignWindow` the edge moves to the grain kept at that age, and the scan reports the
  window applied.

## 7. Estimates, then the exact answer

Rather than a stored sample, the engine reads a fraction of the blocks, drawn at random in each object
and in each cluster value: 0.1 %, 1 %, then 10 %, then the whole. Each tier returns estimates with
their margin, a stratified cluster sample, which narrow until the exact answer. "5xx by endpoint, every
service, 30 days" at 1 % reads 1.5 GB in about 1.2 s cold, within about ±5 % for an average endpoint
*(computed)*. A scan option says whether the caller wants the exact answer only, accepts estimates, or
sets a cost threshold, one second by default; the cost of the exact answer is announced before it is
read.

## 8. The planner

1. Time and the column sheets drop objects.
2. Where each value lives narrows the read to the blocks of the filtered values.
3. For each object, the cheapest of: a learned partial; the counts by bucket of time, for a question on
   one column; the block statistics, for the blocks they settle; reading the question's columns on the
   blocks left.
4. The cost follows the formula of section 2. Under the threshold the question runs and answers
   exactly; over it, the estimates come first, then the exact answer from the newest data to the oldest.
5. The reading leaves its partials.

Each object's choice is explained: the path taken, and its requests and bytes computed.

### 8.1 What each shape costs

At the design rate, S3 profile, cold, one node, on the reference table clustered by emitter
*(computed)*.

| question | on the fly, no accelerator | with the accelerators | asked again |
|---|---|---|---|
| top 100 endpoints over 13 months | ~17 min (1.3 TB) | ~50 ms, monthly counts | ~11 ms |
| one endpoint per day over 13 months | ~0.9 s for a median service, ~80 s for the largest | ~0.13 s, daily counts | ~4 ms |
| errors per hour over 30 days | ~45 s (52 GB) | ~35 ms, hourly counts | ~1 ms |
| 5xx per service per hour, 30 days | ~45 s | ~0.3 s, block statistics | ~20 ms |
| a service's routes under 5xx per hour, 30 h | ~40 ms, median service; ~0.45 s, the largest | the same | ~10 ms |
| per endpoint for version X, 30 days | ~0.1 s, median service; ~5.5 s, the largest | ~50 ms, block statistics | ~10 ms |
| a host's errors per hour, 30 days | ~40 ms | the same | ~5 ms |
| distinct hosts per region, 30 days | seconds: blocks at the emitters' boundaries | ~0.1 s, exact, block statistics | ~10 ms |
| the rows of a request | — | 0.1 to 0.2 s, postings | ~5 ms |
| the 99th percentile per service, last 15 minutes | ~0.2 s | the same | ~10 ms |
| the 99th percentile per service over 30 days | ~7 min (520 GB) | ~0.1 s, hourly sketches | ~5 ms |
| 5xx per endpoint, every service, 30 days | ~2 min (150 GB); estimate at 1 % in ~1.2 s | none | ~50 ms, learned partial |
| per endpoint where `Weight` > 0.37, 30 days | ~8 min (620 GB); estimate in seconds | none | ~50 ms for the same threshold |

### 8.2 What stays costly the first time

Crossing two columns that vary from row to row, or filtering a raw measure, over days, without
narrowing to a cluster value: about two to eight minutes for 30 days on one node. No per-column
structure holds such an answer; only the rows or a crossing kept from an earlier question do, and
ClickHouse reads the same bytes unless a projection was declared. Such a question gets its estimate in
about a second, its exact answer newest first (the last day in about 4 s), can be spread over nodes,
ten of which read its 30 days in about 12 s, and is learned for the next time.

## 9. Writing and sizes

### 9.1 What writing costs

| work | ns a row |
|---|---|
| clustering at level 0's exit, sorted merges | ~40 |
| block statistics | ~15 |
| where each value lives | ~5 |
| counts by bucket of time, seven columns | ~56 |
| quantile and distinct sketches | ~67 |
| column sheets, seven columns | ~42 |
| **total** | **~225**, plus ~30 for each learned family kept up |

At the design rate that is 0.011 of a core *(computed)*.

### 9.2 Sizes

| structure | a recent day | a day past 30 days | 13 months |
|---|---|---|---|
| column sheets | 0.08 MB | 0.08 MB | 0.03 GB |
| counts by bucket of time, sketches | 51 MB | 2.7 MB | 2.6 GB |
| block statistics | 70 MB | 3 MB | 3.2 GB |
| where each value lives | 0.5 MB | 0.5 MB | 0.2 GB |
| learned partials | what is asked | what is asked | 2 GiB budget |
| **total** | **~120 MB** | **~6 MB** | **~8 GB** |

The design this one replaces kept 7.7 GB a day of synopsis tables for this table, about 3 TB in 13
months *(measured)*. For a petabyte of data, fifteen such tables kept 13 months, the structures weigh
about 120 GB on the store, the working set of the local cache about 60 GB, and the sheets and
directories under 1 GB of memory *(computed)*.

### 9.3 Index kinds

| kind | where | what |
|---|---|---|
| `vorticity.column.sheet.v1` | an object's index region | the presence filters of the column sheets |
| `vorticity.column.counts.v1` | an object's index region; monthly derived files | counts by bucket of time and their sketches |
| `vorticity.block.stats.v1` | an object's index region | blocks and their per-column statistics |
| `vorticity.column.where.v1` | an object's index region | where each value lives |
| `vorticity.partials.v1` | the local cache; derived files | learned partials |

Readers skip a kind they do not know, as they do now. The kinds of the former synopsis tables are no
longer written nor read. The leaf entry loses the former synopsis table of contents and gains the
column sheets, and the commit format's version rises: a dataset written before is refused with a
message that says so.

### 9.4 The recommendation of the cluster column

The planner counts each shape of question in bounded counters, which a process pours into the dataset
now and then rather than committing per question, and which several processes add up. From them the
library recommends the column whose clustering would have saved the most bytes read, through a
diagnostic call and telemetry. The application declares the column; the library never does.

## 10. Decisions, and the alternatives weighed

| subject | decision | alternatives weighed |
|---|---|---|
| the core | the group-by engine on the rows, every question | precomputed synopses as the main path, which answer only what they were built for |
| layout | one optional cluster column, declared by the application | a composite sort key, which privileges its prefix; a sort key chosen per object, whose cost per question no one can predict |
| crossings of columns | learned after a question asks them, per object and family | tables of fixed crossings, built before any question, which miss the next crossing asked and grow with every column added |
| structures beside the rows | per column only, with absolute budgets | a budget as a share of the data, wrong both ways: 47 to 88 TB per petabyte, and nothing for a small dataset |
| approximate answers | a fraction of the blocks read on the fly | a stored sample, 4.4 GB for 13 months of the reference table, only a fixed fraction |
| recent windows | computed on the fly | counts per minute, 1.27 GB a day for the reference table |
| the cluster column's name | `ClusterColumn` | words that say "sort" or "key", which suggest a composite |
| blocks | a cluster value and an hour, 65 536 rows at most; merged by day past 30 days | blocks aligned on the format's zones, which straddle cluster values and hours |
| tenants | none in the library | partition columns, retired with the joins of small partitions |
