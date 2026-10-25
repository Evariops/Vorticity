# Questions on the fly, and the synopses beside them

How a dataset answers any question, over any columns, by computing it from its rows with the group-by
engine, and the synopses it keeps beside the rows so that the questions the rows cannot answer in time
are still answered: a fast path of per-column structures only, objects cut by a value and a size the
application chooses, and answers learned after a question has asked them. This document replaces the
time-window design that came before it: its composite synopsis tables give way to synopses per column.

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
| requests in flight | 128 by default, an option of the scan, drawn from the process's budget (section 9.2) |
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

Measured since (section 3.4), `Status` and `Endpoint` take 1.9 bytes a row clustered, not 1.2: the times
the bytes bound are some 1.6 times longer, and the frontiers below shorter by as much.

A second on the fly covers about five hours of the whole table, three days of the largest service and
a year of a median one. The synopses exist for the two cases past that frontier: the whole table over
more than a few hours, and the largest values of the key's first column over more than a few days.

## 3. The layout of the rows

### 3.1 The order of arrival

Without a clustering key, a dataset never reorders rows by their values. Its merges put the appended
objects end to end in the order of time rather than interleaving their rows, so that an application
that appends its rows in groups, a source or a tenant at a time, keeps blocks homogeneous on what
defines its groups.

### 3.2 The clustering

A dataset may declare how its objects are cut (`DatasetOptions.Clustering`): by a value of its key's
first column, the column itself or a function of it that never decreases, by a size an object aims at,
or by both ([clustering](15-compaction.md#clustering-by-a-value-and-a-size)). Nothing in it knows a
time: an hour or a day is a truncation the application picks, as a tenant is a column it leads its key
with.

- From level 0's exit on, the rows follow the key: those of one value of its first column together, in
  the order of the key's later columns. Level 0 keeps the order of arrival.
- The clustering is fixed when the dataset is created, and every header carries it, so that every
  writer cuts alike.
- The library recommends a key's leading column from the questions it has answered: the one whose
  clustering would have saved the most bytes read ([section
  10.4](#104-the-recommendation-of-a-leading-column)). It never applies one by itself.

The clustering is no composite index. No synopsis goes through a prefix of the key: every column keeps
its own synopses, and a column that does not depend on the key's first column is never served worse
than in the order of arrival. What clustering does is make that column, and every column that depends
on it, nearly constant in each block, which the synopses of section 5 measure column by column. Its
price falls on reads of rows by a window of time with no filter, on old objects, which meet more
ranges; an aggregation over such a window settles its blocks instead.

o11y-line, for instance, leads its key with its emitter, written as `service/version/host` so that the
emitters of a service follow each other, then its instant, and cuts its objects by size. Every
resource attribute is then constant in a block, and only the attributes of an event vary: route,
status, method, duration.

### 3.3 Blocks

A block is the run of rows of one value of the key's first column in an object, cut at 65 536 rows. A
value's run ends its block only once the block holds 4 096 rows: a first column whose runs are shorter,
such as an identifier, gives blocks of several values rather than one block per row. An application may
declare a block value, a function of the key's second column (`ClusteringOptions.BlockValue`): a block
then ends where either value changes, past the same 4 096 rows, so that the function holds one group in
each block whose runs are long enough. An object keeps its blocks for its whole life. Blocks are row
ranges the block statistics describe (section 5.3), independent of the format's zones and chunks: a
question settles some blocks of an object from their statistics and reads the rows of the others, each
run of them a part of its scan.

### 3.4 What clustering changes

| what | in the order of arrival | clustered by emitter |
|---|---|---|
| rows a question naming a median service reads | all | 0.077 % |
| bytes a row of `Service`, `Version` and `Host` together | 4.36 | 0.03, constant in a run |
| bytes a row of `Endpoint` | 1.85 | 1.22: at most 50 values in a service |
| bytes a row of `Status` | 0.65 | 0.65: one value holds 86 % of the rows |
| bytes a row of the instant | 3.68 | 3.88: the gaps between two rows of an emitter are wider |
| bytes a row of the request's id | 0.60 | 4.24: the ids counted up lose their order |
| bytes a row, every column | 23.9 | 20.4 |

Measured with `vortex-queries --micro layout`: the reference generator's requests, 20 minutes at 5 000 a
second (6 × 10⁶ rows, some 300 rows an emitter in an object), written as they arrive and clustered by
`service/version/host` then the instant, each compacted to its last level. The estimates this table held
before were within a factor of two of these, but for the instant in the order of arrival, estimated at
1 byte a row: the 2 % of rows that arrive a few seconds late, and the few an hour late, widen every
block's deltas. Clustering takes 15 % off a row on this table, more on the columns that depend on the
emitter, and costs the id its order. At the design's rate an emitter's runs are thirty times longer, which
should lower the clustered figures further.

### 3.5 Each column in segments of its own

A dataset's object chunks each column apart, in segments of about 1 MiB encoded, and ends the chunks of
every column together at each slice and at least every 2²² rows, where lanes cut the object. A
question reading two columns of 30 days of the reference table then asks for about 150 000 segments of
a megabyte *(computed)*, where segments laid out chunk by chunk, every column of 32 000 rows side by
side, meant either about 8 × 10⁶ requests of a few dozen kilobytes or every column's bytes read to
bridge them. A file written alone keeps its columns chunked together, the default a local read prefers
([11-write-strategy.md](11-write-strategy.md#9-key-slices-and-shredded-variants)).

## 4. The dataset's read path

The engine is fed by the dataset's scan, which holds to six rules.

1. **One read a segment**, whatever the number of lanes: every lane of a question shares the reads of
   every object's segments, those of objects cut into parts included, under one read-ahead reservation
   of the question's. The fallback of the former synopsis answer read 25.8 MB up to four lanes,
   164.8 MB at eight, and hung at sixteen *(measured)*; this read path is tested at 1, 8 and 32 lanes.
2. **Filter columns first.** The zone maps drop pages; the filtered columns of the pages left are read,
   cheapest filter first (the column's bytes times its selectivity, which the column sheets estimate);
   the other columns are read only for the pages where rows are left.
3. **Ranges joined.** The segments of one object that lie close are read in one request, across a gap
   of 256 KiB at most and up to 8 MiB, and the next part of each lane is read ahead. Under the cost model
   of section 9, a request more costs about 290 KB of transfer (30 ms among 128 in flight, at
   1.25 GB/s): a gap wider than that costs more to bridge than to ask apart. A column's chunks of about
   a mebibyte already carry some 3.5 times what their request costs, and lie among the other columns'
   chunks, so a read of a few columns asks for each chunk apart rather than bridging the others'.
4. **Newest first.** A question over a window reads its objects from the newest to the oldest, and may
   return partial results with the share of the window they cover. A cancellation stops the reads.
5. **A local cache of data pages**, beside the cache of synopses, shared between processes, with its
   own quota. A region already verified in a neighbour process's cache is kept in memory rather than
   read and verified again.
6. **Parts for fan-out.** A question can be cut into independent parts, an object and a range of its
   blocks each, whose partial results merge to the same bits: an application with several nodes spreads
   a heavy question over them. A part's partial is its groups' states, those a synopsis keeps, which the
   merge folds as a synopsis's ([16-queries.md](16-queries.md#58-parts-of-a-question-merged)).

## 5. The synopses, all per column

The fast path: structures that answer without reading rows, so that a question over months of a large
table reads megabytes rather than terabytes. Each describes one column; none crosses two, and a
crossing a question asks is learned after it (section 5.6). An object keeps its synopses for its whole
life, at its own grain, the clustering value it holds; the coarser grains are rollups, which sum runs
of objects outside them, so that no data object is ever rewritten to change a grain.

### 5.1 The column sheet

For each column of each object, in a region of kind `vorticity.column.sheet.v1` that the object's table
of contents names: rows, nulls, a distinct-count sketch of 1.5 KB, and the 128 most frequent values with
their counts, about 3 KB, beside a presence filter of 9 bits per distinct value (binary fuse, 0.4 % false
positives). The least and greatest value stay in the leaf entry's summaries, which the tree's nodes merge.
A sheet weighs 4.5 KB plus 1.125 bytes per distinct value of the object: 79 KB a day for the reference
table, 31 MB in 13 months *(computed)*. In a region rather than in the leaf entry, it costs a walk of the
tree nothing: seventeen columns of 4.5 KB would have made an entry of 76 KB, read by every walk and
written again by every commit, and a reader fetches the sheets of all its columns in one request with no
open of the object ([10-indexes.md](10-indexes.md#48-vorticitycolumnsheetv1-what-a-column-holds)).

A column keeps frequent values and a filter only when its values repeat, its valid rows four times its
distinct values or more: an identifier, an instant or a measure keeps its sketch alone, which is what
held the reference table's sheets to seven columns. A sheet drops an object whose value is absent
without reading it. An object under a mebibyte keeps none: a question reads it in the request its sheets
would take, and its sheets, some 13 KB for an object of 42 KB, would only add to that read. Writing one
costs 5.6 to 5.9 ns a column a row *(measured)*.

### 5.2 The counts of a column, and their rollups

For the questions on one column and the measures, past the frontier of section 2.1. Each column of an
object has an entry a value holding the rows, and the sum, least and greatest of each measure, sorted
by value, in chunks of 16 KB with a directory (`vorticity.column.counts.v1`); the
entries' sketches lie in a region of their own right after them (`vorticity.column.sketches.v1`), which
a question of counts, sums and extremes does not read: on the design's requests, Service's counts take
136 KB and their sketches 2.4 MB *(measured)*.

- **Grains.** The object, for its whole life: the clustering value it holds, an hour or a day as the
  application cuts it. Coarser grains are rollups beside the levels (`RollUpAsync`): a rollup sums the
  counts of about 64 consecutive objects of the deepest level, cut where the clustering value changes,
  and keeps them by bucket of clustering value as well when its run spans several; a rollup of the rank
  above sums 64 of them, with no row read. A commit that changes an object takes away the rollups over
  it, which are summed again. A day at the design rate is an object of about 173 GB (section 9), which
  summing its synopses into coarser grains must not rewrite. A grain becomes the next by adding
  entries. The minute is not kept: recent windows are computed on the fly.
- **Sketches.** Quantile sketches at 1 % of relative error, the engine's grid, for the columns of at
  most 1 000 values, on every measure an entry carries; distinct-count sketches of the wide columns, text
  columns of more than 1 000 values that repeat, attached to the columns of at most 100 values, a
  HyperLogLog of 2^11 registers each, the sheets', about 2.3 % of error, an identifier keeping none since
  its distinct values in an entry are the entry's rows. A quantile sketch weighs 0.5 to 2 KB on the
  design's requests *(measured)*, not the 1 KB first reckoned: a measure spanning 3.7 decades fills some
  790 buckets at 1 %, and a float spanning both signs and many binades more.
- **Measures.** Every top-level integer and float column of more than 64 values, its values, exact
  sum, least and greatest in the engine's state words; a number column of 64 values or fewer, a status
  or a code, is counted as a column and carried by none. A column whose values hardly repeat, distinct
  values past a quarter of its rows, keeps no counts: an identifier, an instant, a measure.
- **Budget.** An absolute budget per column and object, 2^20 entries; past it the most frequent values
  are kept, with an exact `others` entry, and a value left out stays out.
- **Size.** An entry of an integer and a float measure takes 52 to 61 bytes, most of them the float's
  exact sum and extremes. The reference table's 1.0 × 10⁶ entries a day make about 60 MB of counts in
  objects of an hour *(measured on the design's requests, extrapolated)*, where this design first
  reckoned 51 MB for the counts and their sketches together; the sketches add to it. A rollup holds an
  entry a value over its run, and one a value and bucket where its run spans several clustering values:
  over thirty days of 2 000 routes, an object a day and rollups of four, a count of every route reads 4
  requests and 262 KB, against 30 requests and 1.9 MB of the objects' counts *(measured)*.
- **Writing.** 9.4 to 10.2 ns a counted column a row at 12 million rows an hour, 10.0 to 12.0 at 750 000,
  where each entry holds fewer rows *(measured, one thread)*: a row's float is split into its bins'
  parts once for the entries of every column it falls in. The quantile sketches add 12 to 18 ns a
  sketched column a row, a bucket of each measure reached a row: with four of the seven columns sketched,
  the design's requests cost 21 to 25 ns a counted column a row, 150 to 175 ns a row, where section 10.1
  reckoned 123 ns for the counts and their sketches.

### 5.3 Block statistics, and settling blocks

For each block of section 3.3 and each column, a region a column (`vorticity.block.stats.v1`), beside
the blocks' rows and range of the key (`vorticity.block.table.v1`):

- the block's rows and nulls;
- the values present, each by its number in the column's index of section 5.4, and their rows, as long
  as they fit in little room (at most 256 values); otherwise their count;
- for each number column the counts carry as a measure, the count, exact sum, least and greatest.

A column whose values hardly repeat and that is no measure, an identifier, keeps none
([10-indexes.md](10-indexes.md#412-vorticityblockstatsv1-a-column-over-the-blocks)).

The engine already settles a block from its zone maps when its key maps to a single group and every
aggregate is a count, a least or a greatest ([16-queries.md](16-queries.md#93-statistics-and-zone-maps)),
on a dataset's objects too, but for the blocks that hold a row a delete marked, which their files still
count, and never for a sum, which zone maps do not hold. With block statistics, any combination of columns composes at the
question, block by block, with nothing computed for a pair of columns:

| in the block | what the statistics give |
|---|---|
| no column of the question varies | its counts and aggregates exactly, the block's own |
| one column of the question varies | exact counts, from that column's values |
| two columns vary | exact bounds, max(0, n(x) + n(y) − n) ≤ n(x, y) ≤ min(n(x), n(y)); the exact answer reads those two columns in that block |

A dataset answers so the objects its counts do not (`BlockAnswer`): a block whose statistics prove the
filter true over its rows and whose keys each hold one group gives its rows and its measures' states; a
block where one column of the question varies, under a question of counts alone, gives the rows of each
of its values, the filter judged value by value on the summary each value makes; any other block is
read as rows, each run of them a part of the scan. A key that is a function of a column, a truncation,
holds one group over a block whose extremes it sends to one: an instant, an integer under an extension,
keeps its extremes over each block for it. Before it reads an object's statistics, the dataset counts
the columns of the question that may vary in its blocks, those of the keys and of a filter the object's
summaries do not prove, other than the key's first column and than a key's column the summaries prove
constant: past one under a question of counts, or past none under one of measures, every block would be
read as rows after its statistics, and the object is read as rows at once. Under a key led by the
service, errors by service read 86.8 KB through the blocks where the rows took 480.7 KB *(measured,
`DatasetBlockAnswerTests`, four services of 150 000 rows)*. The exact bounds of two varying columns are
not given yet; they belong with the estimates the planner chooses (section 8).

The truncation of an instant holds one group over a block only when the block is short in time: blocks
end where the key's first column changes and at 65 536 rows, and an hour means nothing here. Errors by
service and hour settle the blocks that fall in one hour; with blocks of some fifty minutes, the
measured question read 2.7 MB through them where the rows took 3.4 MB. For the reference table led by
its emitter, whose blocks span hours, they mostly would not. The application that asks such questions
declares a block value of its instant (section 3.3), the function it groups by, and the blocks end where
it changes: the same question then read 438 KB where the rows took 3.4 MB, and measures by service and
hour 752 KB where the rows took 5.9 MB *(measured, `DatasetBlockAnswerTests`)*. For the reference table,
a block value of the hour makes each block a run of one emitter's hour, some 14 700 rows: about
4.9 × 10⁵ blocks a day where they were 3.5 × 10⁵, and 180 MB of statistics a day where they were
129 *(computed)*.

A block no delete touches settles as a file's does; a block a delete touches is decoded, or settled
from the negative statistics the delete wrote (section 6). For the reference table clustered by
emitter: 3.5 × 10⁵ blocks a day, about 370 bytes each on the design's requests, where this design first
reckoned 200: a service's block lists its endpoints and hosts, 129 and 83 bytes, and the instant keeps
its extremes, 32. That is 129 MB a day kept in each object, 51 GB in 13 months *(369 bytes a block
measured, `--micro blocks`; extrapolated)*.
The rollups sum none of them: their buckets are the clustering values, not the key's first column.
Writing costs 12.2 ns a row, every column's statistics and their indexes together *(measured, one
thread)*. Aggregates per value for the columns of at most 16 values a block are left out of the first
version, and weighed again against the size test.

### 5.4 Where each value lives

For each column of each object whose blocks list values (`vorticity.column.where.v1`): the values,
sorted by their bytes, each with its number and the ranges of rows of the blocks it appears in, or
`everywhere`. The threshold is on the blocks a value actually touches, measured when the object is
written, never on a count of rows assumed spread at random: more than half the blocks, or more ranges
than a sixteenth of them. A filter on a value of the key's first column, or of a column that depends on
it, reads that value's blocks only: the equalities and sets a filter asks of such columns, through `AND`
and `OR`, read the indexes of an object in place of its sheets, in one request, and give the ranges of
rows its scan reads, each on its own. On thirty sources whose endpoints' names interleave, so that no
zone map leaves rows out, a filter on one endpoint read 1.3 MB in 8 requests, against 14.4 MB in 131 for
the same filter as a prefix, which no index answers *(measured, `DatasetWhereTests`)*. The same
structure would answer ranges and prefixes, which the planner takes up (section 8). The index holds the
column's values once an object, 6.2 bytes a value for the design's endpoints and hosts, their names
sharing their prefixes *(measured)*: some 270 KB an object of an hour of the reference table, whose
30 000 endpoints and 10 200 hosts it holds all, 6.6 MB a day, 2.6 GB in 13 months *(computed)*, where
this design first reckoned 0.5 MB a day. Its writing is in the 12.2 ns of section 5.3.

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
- Objects replaced without removing rows, by a merge, have the sum of their partials:
  the commit already says which objects an output replaces.
- While its family is asked, a partial is computed when a new object is written, so that monitors and
  sliding windows stay exact and cheap. A family not asked for a while stops being kept up.
- Partials live in the local cache, and may be written beside the rollups on the store, under a
  budget of 2 GiB per dataset by default; the one that saves the least per byte goes first.

### 5.7 The caches

On the local disk, shared between processes: the data pages; the synopses; the partials; and, for a
filter evaluated on rows, the pages that held at least one row it kept (at most 66 KB for a day's
object), which a later question with the same filter reads alone.

## 6. Deletes, exclusions, late rows, window edges

- A delete writes, beside its marks, the counts of the rows it takes of each column the object counts,
  out of line in its commit object, merged with an earlier delete's
  ([deleting rows](13-dataset.md#what-a-question-of-counts-does-with-marks)). A question of counts,
  sums, means or exact distinct values subtracts them from the object's counts; one of extremes,
  sketches or registers, and the blocks' statistics, read the marked object's rows. The next compaction
  rewrites the object without the rows.
- An exclusion, every row but those of an identifier's value, finds those rows by the postings and
  subtracts their contribution. A distinct count is corrected by the counts of the counted column, a
  value leaving a group only when its last row there is excluded.
- Late rows add their own entries, which merges add up.
- A window edge that cuts a block or a bucket is exact by default: that block's rows are read. With
  `ScanOptions.AlignWindow` the edge moves out to the bounds, on the key's first column, of the objects
  it falls in, and the scan reports the window applied.

## 7. Estimates, then the exact answer

Rather than a stored sample, the engine reads a fraction of the data: 0.1 %, 1 %, then 10 %, then the
whole. It draws ranges of consecutive blocks, at least 1 MiB of each column read, at random in each
object and in each value of the key's first column, a value's share of the ranges growing with its
rows; a value smaller than a range in an object is read whole when it is drawn. Each tier returns
estimates with their margin, a stratified cluster sample, which narrow until the exact answer. The unit
is a range rather than a block because a block holds about 15 KB of two columns: drawn one by one, 1 %
of 30 days would be some 10⁵ requests, about 50 s at 128 in flight and past the rate S3 sustains on a
prefix (section 9). "5xx by endpoint, every service, 30 days" at 1 % reads 1.5 GB in about 1 500
ranges, in about 1.6 s cold *(computed)*. A scan option says whether the caller wants the exact answer
only, accepts estimates, or sets a cost threshold, one second by default; the cost of the exact answer
is announced before it is read.

The estimates exist as `EstimateAsync` ([16-queries.md](16-queries.md#59-estimates-then-the-exact-answer)).
Each drawn range runs as a part of the question and gives its states, and the strata are runs of
neighbouring objects, which follow the clustering's values, with two ranges drawn in each at least. The
cost of the exact answer is announced by every tier, priced by the planner object by object (section 8)
with no read; under `EstimateOptions.ExactUnder`, a second by default, the exact answer comes at once.

## 8. The planner

1. Time and the column sheets drop objects.
2. Where each value lives narrows the read to the blocks of the filtered values.
3. For each object, the cheapest of: a learned partial; its counts, or a rollup's over it, for a question
   on one column; the block statistics, for the blocks they settle; reading the question's columns on the
   blocks left.
4. The cost follows the formula of section 2. Under the threshold the question runs and answers
   exactly; over it, the estimates come first, then the exact answer from the newest data to the oldest.
5. The reading leaves its partials.

Each object's choice is explained: the path taken, and its requests and bytes computed.

The first planner, built before the blocks, chooses between the counts and the rows. A question on one
column and the measures (its keys the column, functions of it, and columns or functions of them that an
object holds constant; its aggregates counts, sums, extremes, means, quantiles and distributions of the
measures the counts carry, and the column's distinct values; its filter values of the column by
equality or a set, and conjuncts an object's summaries prove true over its every row) is worked out
once. Nothing in it knows a time: a window on an instant is a conjunct like any, and a truncation of an
instant a function like any. Then each object a job wrote and no delete marked, or whose marked rows a
delete counted under a question that subtracts them (§6), whose summaries prove the other conjuncts and
keys, reads its column's counts, with their sketches for a quantile, in one
request: no sheet of it, the counts saying which values it holds; read ahead as many objects as the
session reads at once; kept parsed by the handle, so that the question asked again reads nothing. A
rollup whose objects would all answer is read in their place, the highest rank first, or bucket by
bucket where that costs less ([rollups](15-compaction.md#rollups)). Every other object is read as rows,
the objects side by side one part of the scan with its read-ahead. On the planner's dataset, four
objects of an hour and two appended after, a count by service reads 6 requests and 41.6 KB cold, none
warm, where its rows take
323 KB; a quantile by service reads 74.4 KB; questions drawn at random on tables drawn at random answer as
their rows do *(measured)*.

Past the counts, an object's blocks answer where its counts do not (section 5.3), when the formula says
they cost less than its rows. Each object a job writes says in its table of contents the bytes of each
of its columns, a few bytes an entry, which the writer counts as it writes them. The cost of a path is
its bytes and its requests, each worth the 293 KB of transfer its round trip would have carried: through
the blocks, the regions of the statistics and the indexes of the question's columns, those within
256 KiB of each other in one request; through the rows, the bytes of the question's columns, an open and a
request a mebibyte of chunk. The explanation of each object names the path taken, its requests and bytes,
and those of the rows it was weighed against: errors by service read 114 bytes in a request from an
object of 4 MiB, where its rows would take 33 KB in three *(measured, `DatasetBlockAnswerTests`)*.

An object read as rows is read in two passes when its sheets say its filter keeps so few rows that most
chunks of the widest column read would hold none: the columns of the filter first, the rows they keep
marked, then the other columns for those rows alone, which read only the chunks that hold them. With `k`
rows kept among `c` chunks of the widest column, about `min(k, c)` are read where every one was, at the
price of a round trip more; the dataset takes the two passes when `2k ≤ c`. `k` is the sheets' count of
the values an equality or a set asks: a frequent value's rows, none where the presence filter refutes
it, else the rows the sheet leaves to its other values shared out evenly; a conjunction the least of its
sides, a disjunction their sum. `c` is the chunks the object's contents count for each column. A rare
code scattered among thirty rows of 100 000, whose bodies of 400 bytes lie in chunks of 512 rows, read
4.8 MB in 36 requests in two passes, where the same filter as a range, which no sheet estimates, read
30.3 MB in 198 *(measured, `DatasetTwoPhaseTests`)*. Where the rows lie together, in a value's blocks, the
index of where each value lives (section 5.4) has already narrowed the read, and one pass reads them.

### 8.1 What each shape costs

At the design rate, S3 profile, cold, one node, on the reference table clustered by emitter
*(computed)*.

| question | on the fly, no synopsis | with the synopses | asked again |
|---|---|---|---|
| top 100 endpoints over 13 months | ~17 min (1.3 TB) | ~50 ms, the rollups' counts | ~11 ms |
| one endpoint per day over 13 months | ~0.9 s for a median service, ~80 s for the largest | ~50 ms, the rollups' counts by bucket | ~4 ms |
| errors per hour over 30 days | ~45 s (52 GB) | ~35 ms, the counts of objects of an hour | ~1 ms |
| 5xx per service per hour, 30 days | ~45 s | ~0.3 s, block statistics | ~20 ms |
| a service's routes under 5xx per hour, 30 h | ~40 ms, median service; ~0.45 s, the largest | the same | ~10 ms |
| per endpoint for version X, 30 days | ~0.1 s, median service; ~5.5 s, the largest | ~50 ms, block statistics | ~10 ms |
| a host's errors per hour, 30 days | ~40 ms | the same | ~5 ms |
| distinct hosts per region, 30 days | seconds: blocks at the emitters' boundaries | ~0.1 s, exact, block statistics | ~10 ms |
| the rows of a request | — | 0.1 to 0.2 s, postings | ~5 ms |
| the 99th percentile per service, last 15 minutes | ~0.2 s | the same | ~10 ms |
| the 99th percentile per service over 30 days | ~7 min (520 GB) | ~0.1 s, the objects' sketches | ~5 ms |
| 5xx per endpoint, every service, 30 days | ~2 min (150 GB); estimate at 1 % in ~1.6 s | none | ~50 ms, learned partial |
| per endpoint where `Weight` > 0.37, 30 days | ~8 min (620 GB); estimate in seconds | none | ~50 ms for the same threshold |

The coarse paths were computed for monthly files of counts by day and month, which rollups replaced:
their requests are bounded the same way, a header and the chunks of the values asked for each rollup a
question takes.

### 8.2 What stays costly the first time

Crossing two columns that vary from row to row, or filtering a raw measure, over days, without
narrowing to a value of the key's first column: about two to eight minutes for 30 days on one node. No
per-column synopsis holds such an answer; only the rows or a crossing kept from an earlier question do,
and ClickHouse reads the same bytes unless a projection was declared. Such a question gets its estimate
in one to two seconds, its exact answer newest first (the last day in about 4 s), can be spread over
nodes, ten of which read its 30 days in about 12 s, and is learned for the next time.

## 9. What the store sees

At the design rate, clustered by day, an object holds a day, the longest unit whose footer keeps a
selective read's chunk under 16 MB: one object of about 173 GB a day, about 395 over 13 months, and for
the current day at most eight objects of level 0 and the few its jobs left. A question's requests are
bounded by the objects its window touches, or by the rollups over them when theirs answer it, never by
its rows.

### 9.1 Reads

Cold, S3 profile, one node *(computed)*:

| question | path | requests | bytes | time |
|---|---|---|---|---|
| top 100 endpoints over 13 months | the rollups' counts | 20 to 40 | 10 to 25 MB | ~50 ms |
| one endpoint per day over 13 months | the rollups' counts by bucket | 20 to 40 | ~1 MB | ~50 ms |
| errors per hour over 30 days | the counts of objects of an hour | 30 to 50 | under 2 MB | ~35 ms |
| 5xx per service per hour, 30 days | block statistics | 50 to 80 | 0.2 to 0.4 GB | ~0.3 s |
| a service's routes under 5xx per hour, 30 h | the service's rows | 10 to 60 | 5 MB to 0.5 GB | 40 to 450 ms |
| the rows of a request, 30 days | postings | ~60 | ~2 MB | 0.1 to 0.2 s |
| 5xx per endpoint, every service, 30 days | rows | ~18 000 ranges of 8 MiB | 150 GB | ~2 min; ~12 s over ten nodes |
| the same, estimated at 1 % | ranges of at least 1 MiB | ~1 500 | 1.5 GB | ~1.6 s |
| the same, asked again | learned partials | ~50 | under 50 MB | ~50 ms |

Asked again warm, a question finds its synopses in the local cache and sends the store no request for
them.

### 9.2 Bursts, and a budget of requests

- One question with 128 requests in flight over small ranges, the 16 KB chunks of the counts, sends
  about 4 000 requests a second while it lasts, 128 every 30 ms. S3 sustains at least 5 500 reads a
  second on a prefix, and splits a prefix only under sustained load: a few such questions at once meet
  `503 SlowDown`.
- A process therefore holds one budget of requests, shared by its questions and its jobs: 256 in
  flight and 2 000 a second by default, both options. A question's requests in flight are drawn from
  it; a `SlowDown` halves the rate, which then grows back by steps.
- An object's key starts with its random identifier, which lets S3 split the dataset's prefix as its
  load grows.
- The rollups turn the 395 reads of a question by day over 13 months into about 14, a header and the
  chunks of the values asked for each of the 7 rollups of 64 days *(computed)*, and the estimates read
  ranges rather than blocks (section 7).

### 9.3 Writes

| work | bytes | requests |
|---|---|---|
| appends | the data, once | 3 to 5 PUT each: the object, the commit, the pages of the tree; how often is the application's choice |
| compaction: level 0's exit and the leveled merges | each row rewritten 2 to 4 times: 4 to 8 MB/s written, a little less read | under one PUT and one GET a second, in parts of 16 to 64 MB |
| synopses in the objects | about 187 MB a day, 0.1 % of the data | within the objects' parts |
| rollups | a few MB a column and a run of 64 objects | a PUT a rollup, once the objects of its run stand |
| listings | none: the commit tree is the catalog | none |

The composite tables this design replaces wrote 7.7 GB a day, 4.4 % of the data.

## 10. Writing and sizes

### 10.1 What writing costs

| work | ns a row |
|---|---|
| clustering at level 0's exit, sorted merges | ~40 |
| block statistics, and where each value lives | 12.2 *(measured)* |
| counts, seven columns | ~56 |
| quantile and distinct sketches | ~67 |
| column sheets, seven columns | ~42 |
| **total** | **~217**, plus ~30 for each learned family kept up |

At the design rate that is 0.011 of a core *(computed)*.

### 10.2 Sizes

| structure | in an object, a day | coarser grains, a day | 13 months |
|---|---|---|---|
| column sheets | 0.08 MB | — | 0.03 GB |
| counts, sketches | 51 MB | 2.7 MB | 21 GB |
| block statistics | 129 MB | — | 51 GB |
| where each value lives | 6.6 MB | — | 2.6 GB |
| learned partials | what is asked | what is asked | 2 GiB budget |
| **total** | **~187 MB** | **~3 MB** | **~75 GB** |

The synopses weigh 0.1 % of the data. The design this one replaces kept 7.7 GB a day of synopsis
tables for this table, 4.4 % of the data and about 3 TB in 13 months *(measured)*. For a petabyte of
data, fifteen such tables kept 13 months, the synopses weigh about 1.1 TB on the store, of which the
local cache's working set is about 60 GB, the last 30 days and the coarser grains; the sheets and
directories take under 1 GB of memory *(computed)*. The coarser grains were computed for monthly files of
days and months, which rollups replaced.

### 10.3 Index kinds

| kind | where | what |
|---|---|---|
| `vorticity.column.sheet.v1` | an object's region, which its table of contents names | the column sheets |
| `vorticity.column.counts.v1` | an object's region, which its table of contents names; a rollup's | the counts of each value, kept by bucket in a rollup whose run spans several clustering values |
| `vorticity.column.sketches.v1` | an object's region right after the counts, which its table of contents names; a rollup's | the counts' quantile sketches and distinct registers |
| `vorticity.block.table.v1` | an object's region right after the counts, which its table of contents names | its blocks, their rows and keys |
| `vorticity.block.stats.v1` | an object's region a column, right after the column's index of where its values live, which its table of contents names | each block's rows, nulls, values by their numbers and their rows, and a measure's states |
| `vorticity.column.where.v1` | an object's region a column, which its table of contents names | where each value lives: the values, their numbers and their ranges of rows |
| `vorticity.partials.v1` | the local cache; beside the rollups | learned partials |

Readers skip a kind they do not know, as they do now. The kinds of the former synopsis tables are no
longer written nor read. The leaf entry keeps its table of contents, at a second version that holds
them no more and names the regions of the column sheets, counts and sketches. An entry of the first
version is read through its object's directory, so a dataset written before stays readable and the
commit format's version does not rise.

### 10.4 The recommendation of a leading column

The planner counts each shape of question in bounded counters, which a process pours into the dataset
now and then rather than committing per question, and which several processes add up. From them the
library recommends the column to lead the key with, whose clustering would have saved the most bytes
read, through a diagnostic call and telemetry. The application declares its key and its clustering;
the library never does.

## 11. Decisions, and the alternatives weighed

| subject | decision | alternatives weighed |
|---|---|---|
| the core | the group-by engine on the rows, every question | precomputed synopses as the main path, which answer only what they were built for |
| the fast path | synopses, per column only, with absolute budgets | composite synopsis tables under a budget as a share of the data, wrong both ways: 47 to 88 TB per petabyte, and nothing for a small dataset |
| layout | a clustering the application declares: a value of its key's first column, of any type, a size, or both | one cluster column beside the key, which the first version of this design had; a composite sort key, which privileges its prefix; a sort key chosen per object, whose cost per question no one can predict |
| crossings of columns | learned after a question asks them, per object and family | tables of fixed crossings, built before any question, which miss the next crossing asked and grow with every column added |
| coarser grains | rollups beside the levels, which sum runs of objects' counts with no row read | monthly files of days and months, a calendar the library would have had to know; regraining inside the objects, which rewrites a day's 173 GB to merge its 120 MB of synopses |
| approximate answers | ranges of at least 1 MiB drawn on the fly, stratified by value of the key's first column | a stored sample, 4.4 GB for 13 months of the reference table, only a fixed fraction; blocks drawn one by one, some 10⁵ requests for 1 % of 30 days |
| requests to the store | one budget per process, shared by its questions and jobs | 128 in flight per question with no ceiling: about 4 000 requests a second from a single question |
| recent windows | computed on the fly | counts per minute, 1.27 GB a day for the reference table |
| the clustering's name | `ClusteringOptions`, a value and a size | a cluster column of its own, which the key's first column already is |
| blocks | a value of the key's first column in an object, 65 536 rows at most; their statistics summed into the rollups | blocks aligned on the format's zones, which straddle values |
| tenants | none in the library | partition columns, retired with the joins of small partitions |
