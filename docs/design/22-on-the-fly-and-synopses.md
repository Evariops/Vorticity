# Questions on the fly, and the synopses beside them

How a dataset answers any question, over any columns, by computing it from its rows with the group-by
engine, and the synopses it keeps beside the rows so that the questions the rows cannot answer in time
are still answered: a fast path of per-column structures only, objects of one partition sorted by the
key the application declares, and answers learned after a question has asked them. This document replaces the
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

No question is known in advance, and neither is which columns a question crosses. No structure the
library builds may privilege a column or be composite: no table of fixed column crossings, no declared
groupings. The one composite structure is the sort key, which the application declares and which
orders the rows rather than precomputing an answer ([the sort
key](23-query-architecture.md#42-the-sort-key-and-its-indexed-prefix)). Composite tables were the faults
of the design this one replaces: they answered only questions on the prefixes of their order, and
weighed 4.7 to 8.8 % of the data, 47 to 88 TB per petabyte *(measured)*.

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

For a question that reads two columns of a table sorted by its emitter, about 1.2 bytes a row,
cold, on one node *(computed)*:

| scope | an hour | a day | 30 days | 13 months |
|---|---|---|---|---|
| the whole table | ~0.2 s | ~4 s | ~2 min | ~27 min |
| the largest service, 7.4 % | ~40 ms | ~0.3 s | ~9 s | ~2 min |
| a median service, 0.077 % | ~30 ms | ~35 ms | ~0.13 s | ~1.4 s |

Measured since, on the reference generator's requests (section 8.3), a question by the hour reads the
instant too, 3.9 bytes a row sorted by emitter: a count by status and hour reads 4.8 bytes a row, not 1.2. The
longest window a second reads exactly, cold, at the design's rate, an object an hour, requests of a
mebibyte *(computed from the measured bytes, `vortex-queries --micro columns`)*:

| question | the whole table | the largest service | a median service |
|---|---|---|---|
| a count by status and hour, 4.8 bytes a row | 1.1 h | 15 h | 46 days |
| 5xx by endpoint and hour, 6.3 bytes a row | 51 min | 12 h | 37 days |
| the 99th percentile of durations by hour, 7.5 bytes a row | 43 min | 10 h | 32 days |

In the order of arrival, every granule holds every service, so a filter on one spares no byte: 38
minutes to 1.2 hours, whatever the service.

A second on the fly covers about an hour of the whole table, half a day of the largest service and a
month of a median one. The synopses exist for the two cases past that frontier: the whole table over
more than an hour, and the largest values of the key's first column over more than half a day.

## 3. The layout of the rows

### 3.1 The order of arrival

Without a sort key, a dataset never reorders rows by their values. Its merges put the appended objects
end to end in the order of time rather than interleaving their rows, so that an application that
appends its rows in groups, a source or a tenant at a time, keeps granules homogeneous on what defines
its groups.

### 3.2 The layout

A dataset may declare a layout (`DatasetOptions.Layout`): a partition no object holds two values of, a
sort key every object's rows follow, appended objects included, and the prefix of the key each object
indexes, the first key of each granule ([the layout](23-query-architecture.md#4-laying-out-the-rows),
[the invariant](13-dataset.md#52-the-invariant)). Nothing in it knows a time: an hour or a day is a
truncation the application picks, as a tenant is a column it leads its key with.

- From the first write on, an object's rows follow the key: an append sorts each partition's rows in
  memory before it writes them, and a merge's outputs are sorted by construction.
- The layout is fixed when the dataset is created, and every header carries it, so that every writer
  sorts and cuts alike.
- The library recommends a key from the questions a handle has answered: the columns and variant keys
  their filters read, with the bytes an order by each would have saved the objects read as rows
  ([section 10.4](#104-the-recommendation-of-a-key)). It never applies one by itself.

The sort key is the one composite structure, and the application declares it. No synopsis goes through
a prefix of the key: every column keeps its own synopses, and a column that does not depend on the key's
first components is never served worse than in the order of arrival. What the order does is make those
components, and every column that depends on them, nearly constant in each granule, which the synopses
of section 5 measure column by column, and runs, which the engine folds as weighted rows.

o11y-line, for instance, partitions by day and leads its key with its emitter, `service`, `version`
and `host`, then the hour, the status and the route, then the instant. Every resource attribute is
then constant in a granule, and only the attributes of an event vary within an hour of an emitter.

### 3.3 Granules

A granule is a run of 8 192 rows of an object, the writer's block: the zone of its zone maps, the place
of its sparse index, the unit its statistics describe (section 5.3), and the unit a read takes a part
of a chunk by. Granules are row ranges, never cut where the key changes, so a granule may hold the end of
one value of the key and the start of the next. An object keeps its granules for its whole life: a
question settles some granules of an object from their statistics and reads the rows of the others,
each run of them a part of its scan.

### 3.4 What the order changes

| what | in the order of arrival | sorted by emitter |
|---|---|---|
| rows a question naming a median service reads | all | 0.077 % |
| bytes a row, every column | 23.90 | 20.45 |
| `Service`, `Version`, `Host` | 1.26, 1.28, 1.82 | 0.005, 0.004, 0.019 |
| `Emitter`, the three joined | 1.99 | 0.019 |
| `Region` | 0.38 | 0.013 |
| `Endpoint` | 1.85 | 1.24 |
| `Method`, `Status` | 0.24, 0.65 | 0.21, 0.65 |
| `At` | 3.68 | 3.88 |
| `RequestId` | 0.59 | 4.24 |
| `DurationNs`, `Weight` | 3.25, 6.91 | 3.25, 6.91 |
| an append of 6 250 rows, a row | 1 267 ns | 1 861 ns |

Measured with `vortex-queries --micro layout`: the reference generator's requests, 20 minutes at 5 000 a
second (6 × 10⁶ rows, some 300 rows an emitter in an object), written as they arrive and sorted by
`service/version/host` then the instant, each compacted to its last level. The 2 % of rows that arrive a
few seconds late, and the few an hour late, widen the instant's deltas in the order of arrival. The order
takes 14 % off a row on this table, nearly all of the columns that depend on the emitter, and costs the
id its order. The sort costs an append of 6 250 rows 594 ns a row, some 3.7 ms an append, a fixed cost
that larger appends spread ([sorted
objects](23-query-architecture.md#44-sorted-objects-from-the-first-write)). At the design's rate an
emitter's runs are thirty times longer, which should lower the sorted figures further.

### 3.5 Each column in segments of its own

A dataset's object chunks each column apart, in segments of about 1 MiB encoded, and ends the chunks of
every column together at least every 2²² rows, where lanes cut the object. A
question reading two columns of 30 days of the reference table then asks for about 150 000 segments of
a megabyte *(computed)*, where segments laid out chunk by chunk, every column of 32 000 rows side by
side, meant either about 8 × 10⁶ requests of a few dozen kilobytes or every column's bytes read to
bridge them. A file written alone keeps its columns chunked together, the default a local read prefers
([11-write-strategy.md](11-write-strategy.md#9-a-datasets-objects)).

## 4. The dataset's read path

The engine is fed by the dataset's scan, which holds to seven rules.

1. **One read a segment**, whatever the number of lanes: every lane of a question shares the reads of
   every object's segments, those of objects cut into parts included, under one read-ahead reservation
   of the question's. The fallback of the former synopsis answer read 25.8 MB up to four lanes,
   164.8 MB at eight, and hung at sixteen *(measured)*; this read path is tested at 1, 8 and 32 lanes.
2. **Filter columns first.** The zone maps drop granules; the filtered columns of the granules left are
   read, cheapest filter first (the column's bytes times its selectivity, which the column sheets
   estimate); the other columns are read only for the blocks of 1 024 rows where rows are left, when that
   spares what a second pass costs ([two passes](23-query-architecture.md#63-two-passes-and-late-materialization)).
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
6. **Parts for fan-out.** A question can be cut into independent parts, a range of the dataset's rows
   each, cut at its objects and inside a large one at its chunks, whose partial results merge to the
   same bits: an application with several nodes spreads
   a heavy question over them. A part's partial is its groups' states, those a synopsis keeps, which the
   merge folds as a synopsis's ([16-queries.md](16-queries.md#58-parts-of-a-question-merged)).
7. **Objects opened ahead, even on one lane.** An object read as rows costs an open, a request for its
   end, then its chunks: two round trips after one another. The objects that read their rows next are
   opened ahead, eight at most, and left open in the handle's cache for the read that comes to them; an
   object that learns its family's partial learns it while the one before it is served, as a scan
   decodes its next batch ahead of its consumer (`ScanOptions.Prefetch`). On one lane at the S3 profile,
   five of the client's questions, which learn from their objects, waited 2 to 14 round trips one after
   another where they waited 12 to 88, and answered in 0.26 to 2.1 s where they took 0.56 to 4.9 s; on 32 lanes,
   as before *(measured, `vortex-queries --micro paths --qp`)*.

## 5. The synopses, all per column

The fast path: structures that answer without reading rows, so that a question over months of a large
table reads megabytes rather than terabytes. Each describes one column; none crosses two, and a
crossing a question asks is learned after it (section 5.6). An object keeps its synopses for its whole
life, at its own grain, the partition it holds; the coarser grains are rollups, which sum runs
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

- **Grains.** The object, for its whole life: the partition it holds, an hour or a day as the
  application cuts it. Coarser grains are rollups beside the levels (`RollUpAsync`): a rollup sums the
  counts of about 64 consecutive objects of the deepest level, cut where the partition changes, and
  keeps them by bucket of partition as well when its run spans several; a rollup of the rank
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
  entry a value over its run, and one a value and bucket where its run spans several partitions: over
  thirty days of 2 000 routes, an object a day, a count of every route reads two rollups and the counts
  of the three objects no rollup covers yet, 7 requests and 324 KB, where the objects' counts take a
  request each *(measured)*.
- **Writing.** 9.4 to 10.2 ns a counted column a row at 12 million rows an hour, 10.0 to 12.0 at 750 000,
  where each entry holds fewer rows *(measured, one thread)*: a row's float is split into its bins'
  parts once for the entries of every column it falls in. The quantile sketches add 12 to 18 ns a
  sketched column a row, a bucket of each measure reached a row: with four of the seven columns sketched,
  the design's requests cost 21 to 25 ns a counted column a row, 150 to 175 ns a row, where section 10.1
  reckoned 123 ns for the counts and their sketches.

### 5.3 Granule statistics, and settling granules

For each granule of section 3.3 and each column, a region a column (`vorticity.granule.stats.v1`):

- the granule's rows and nulls;
- the values present, each by its number in the region's dictionary, and their rows, as long as they fit
  in little room (at most 256 values); otherwise their count;
- for each number column the counts carry as a measure, the count, exact sum, least and greatest; an
  instant or a date keeps its extremes, which say which group of a truncation of it a granule falls in.

A column whose values hardly repeat and that is no measure, an identifier, keeps none
([10-indexes.md](10-indexes.md#411-vorticitygranulestatsv1-a-column-over-the-granules)). For a measure a
question's plan reads, each granule also keeps its values in a log-linear sketch, apart, which a
quantile or a distribution merges over the granules it settles, within 5.9 % of any value
(`vorticity.granule.histograms.v1`).

The engine already settles a granule from its zone maps when its key maps to a single group and every
aggregate is a count, a least or a greatest ([16-queries.md](16-queries.md#93-statistics-and-zone-maps)),
on a dataset's objects too, but for the granules that hold a row a delete marked, which their files
still count, and never for a sum, which zone maps do not hold. With granule statistics, any combination
of columns composes at the question, granule by granule, with nothing computed for a pair of columns:

| in the granule | what the statistics give |
|---|---|
| no column of the question varies | its counts and aggregates exactly, the granule's own |
| one column of the question varies | exact counts, from that column's values |
| two columns vary | exact bounds, max(0, n(x) + n(y) − n) ≤ n(x, y) ≤ min(n(x), n(y)); the exact answer reads those two columns in that granule |

A dataset answers so the objects its counts do not (`GranuleAnswer`): a granule whose statistics prove
the filter true over its rows and whose keys each hold one group gives its rows and its measures'
states; a granule where one column of the question varies, under a question of counts alone, gives the
rows of each of its values, the filter judged value by value on the summary each value makes; any other
granule is read as rows, each run of them a part of the scan. A key that is a function of a column, a
truncation, holds one group over a granule whose extremes it sends to one. Before it reads an object's
statistics, the dataset counts the columns of the question that may vary in its granules, those of the
keys and of a filter the object's summaries do not prove, other than the key's first component and
than a key's column the summaries prove constant: past one under a question of counts, or past none
under one of measures, every granule would be read as rows after its statistics, and the object is read
as rows at once. Under a key led by the service, errors by service read 247 KB through the granules
where the rows take 898 KB, errors by service and hour 1.9 MB where they take 3.6 MB, and measures by
service and hour, the hour in the key after the service, 2.4 MB where they take 6.2 MB *(measured,
`DatasetBlockAnswerTests`)*. The exact bounds of two varying columns are not given yet; they belong
with the estimates the planner chooses (section 8).

A granule no delete touches settles as a file's does; a granule a delete touches is read as rows
(section 6). The statistics take 1 307.7 bytes a granule on the design's requests, sorted by emitter,
most of them the values listed by the columns of many values, the endpoints 677 and the hosts 423: on
the reference table, 5.3 × 10⁵ granules a day, about 690 MB a day kept in its objects *(measured,
`--micro granules`; extrapolated)*. The rollups sum none of them: their buckets are partitions. Writing
costs 19.1 ns a row, every column's statistics and locates together *(measured, one thread)*, where
this design reckoned 15.

### 5.4 Where each value lies

For each column of each object whose sheet numbers its values (`vorticity.locate.v1`): the values,
sorted by their bytes, each with the granules it lies in, or *everywhere*. The threshold is on the
granules a value actually touches, measured when the object is written, never on a count of rows assumed
spread at random: more than three granules in four. A filter on a value, a set of them or a prefix reads
that value's granules only, whatever column it is on: the equalities and sets a filter asks of such
columns, through `AND` and `OR`, read the locates of an object in place of its sheets, in one request,
and give the granules its scan reads. On thirty sources whose endpoints' names interleave, so that no
zone map leaves rows out, a filter on one endpoint read 788 KB in 18 requests, against 2.9 MB in 51 for
the same filter as a prefix, which no index answers *(measured, `DatasetWhereTests`)*. A consolidated
partition's index carries its objects' locates, so a question on a value over a window of days reads a
page of it a day. The locates take 570.6 bytes a granule on the design's requests *(measured,
`--micro granules`)*: some 300 MB a day of the reference table, where this design first reckoned
0.5 MB a day for ranges of blocks cut on the key. Their writing is in the 19.1 ns of section 5.3.

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

Built so far (`LearnedFamily`, `LearnedPartials`): the core forms the family of a question whose
aggregates a part's states answer whole, the plan's keys followed by a key for each conjunct that asks a
top-level column for a value or a few, and names it on the pass that asks states (`ScanSpec.Family`).
The dataset learns the partial of each object it would read as rows, one with no row marked whose added
keys widen no read: a value of the key's first column that the object holds with others is read as the
question reads it, since the family would read every value's rows. It runs the family over the object's
rows as a part (`AggregationPartials.ExportAsync`), with the synopses off, keeps the partial in the
handle (64 MiB) and the session's synopsis cache, under the object's identity and the family's, its
bytes checked by their XXH3-128, and serves it as a partial's states, the groups of the values asked
selected; a partial past 1 MiB, some 10⁴ groups, answers its question and is not kept, and the object
learns that family no more. The plan says `partial`, learned or to learn. Learning reads the object's
rows of the family's columns, which a question with a scattered column's value reads anyway: on four
services of 20 000 calls, counts and sums by route under one status read 542 KB the first time, as the
rows do, and nothing under another status, nor from a handle opened later on the same cache
*(measured, `DatasetLearnedPartialsTests`)*. Only the objects a job wrote learn, on a version that keeps
counts: letting appended objects learn too, and versions without counts, raised what a dozen ratchets of
the rows' path allocate warm two to five times, and made the test of 5.7 million groups learn partials
past its budget. Through a compaction, a job notes which outputs each input's rows went to, the merge's
runs each coming from one input; an output whose every input sent it all its rows, none of them marked,
holds theirs, all and no other, and the handle and the session's cache keep its inputs' identities. A
question that finds no partial of the output sums its inputs' (`LearnedFamily.MergeAsync`, the
family's plan over their states, to the bits their rows would give), found so in turn up to eight merges
deep, and keeps the sum as the output's: an object rewritten into the next level is answered with no row
read *(measured)*.

At several lanes, a question cuts an object it would read as rows between its lanes, where the chunks of
the columns it reads end together. Each lane's piece of an object that learns runs the family over its own
rows, on its lane, and serves that partial; the question's source (`PiecePartials`) keeps the pieces'
partials until they tile the object, then has the handle merge them in the background, to the bits the
object's rows would give, and keep the sum as the object's partial, which the handle's disposal waits for.
An object that learns is cut over all its rows, as one lane reads them all to learn it, not over the
granules its key's index keeps; a scan whose rows cut an object without the question's lanes, a window's
edge, learns nothing of it. Objects of blocks of a thousand rows, cut into ten pieces each at 32 lanes: a
family asked there alone is learned, asked again with another value it reads 43 KB where the rows read
1.1 MB, and each partial holds the groups and states of the one a lane learns of its object whole
*(measured, `DatasetLearnedPartialsTests`)*.

Each object makes its own family (`FamilyProof`): the conjuncts its summaries prove true over its every
row leave it, so that a window that moves keeps the partials of the objects it holds whole; an object a
conjunct on the key's first column alone neither proves nor refutes, an edge of such a window, is read as
the question reads it and learns nothing, since it would learn a partial each time the window moves. The
handle keeps up the families its questions asked in the last hour, sixteen at most: each object it
appends learns their partials as it is written, read back from where the append left it, so that a
monitor, the same question after each append, answers every object appended after it first asked from
its partial (*measured*: the plan says so of each, minute after minute). An appended object, which keeps
no table of contents, answers from such a partial and learns none from its rows. Keeping a family up
costs an appended object some 1.5 ms whatever its rows, the family's run and its partial's file: 75 ns
a row of a minute of 20 000 rows, 912 of one of 2 000, under 30 past some 50 000 *(measured)*. Not built
yet: the store beside the rollups, the families kept up across processes, which would ask the families
written down, and a budget by the savings each family makes.

### 5.7 The caches

On the local disk, shared between processes: the data pages; the synopses; the partials; and, for a
filter evaluated on rows, the pages that held at least one row it kept (at most 66 KB for a day's
object), which a later question with the same filter reads alone.

The filter's pages are built (`PredicatePages`): a scan whose batches stay whole, an aggregation's,
notes of each object it covers whole and reads by its own scan the batches where the filter kept a row,
as ranges of the object's rows, under the object's identity, the filter's canonical text and the
version's columns; the handle keeps them, and the session's synopsis cache, their bytes checked by their
hash. Pages that hold more than half the object's rows, or more than 4 096 ranges (64 KB), are not kept:
they would spare little. The filter asked again reads each range alone. On 100 000 events, bodies of
300 bytes in chunks of 512 rows, thirty rows of a rare code asked as a range, which no sheet estimates
and no index answers: 23.2 MB the first time, 5.3 MB after, 5.4 MB from a handle opened later on the
same cache *(measured, `DatasetPredicatePagesTests`)*. A scan that compacts its batches, whose rows no
longer lie where they were, notes none.

## 6. Deletes, exclusions, late rows, window edges

- A delete writes, beside its marks, the counts of the rows it takes of each column the object counts,
  out of line in its commit object, merged with an earlier delete's
  ([deleting rows](13-dataset.md#what-a-question-of-counts-does-with-marks)). A question of counts,
  sums, means or exact distinct values subtracts them from the object's counts; one of extremes,
  sketches or registers reads the marked object's rows. The blocks' statistics are not negated: the
  blocks that hold a marked row are read as rows, the others answered by their statistics. The next
  compaction rewrites the object without the rows.
- An exclusion, every row but those of an identifier's value, finds those rows by the postings and
  subtracts their contribution. A distinct count is corrected by the counts of the counted column, a
  value leaving a group only when its last row there is excluded. Done for one value of a declared
  identifier left out, `id != v` (`CountsAnswer.Exclusion`), under a question of counts, sums, means or
  exact distinct values, on an object no delete marked whose identifier the summaries or the type prove
  holds no null (a null id is left out too, which no count tells): the object's counts less the counts
  of the rows the postings find for `id == v` (`VortexDataset.LookupAsync`), of the counted column and
  the measures the question reads. Per object it costs its counts and the identifier's index entries
  and postings; an object that holds the value reads besides the chunks of the identifier, the counted
  column and those measures that hold its rows. On two objects of 80 000 calls, counts by service but
  one request's read 127 KB when no object holds it, and 611 KB when one does, one chunk a column
  there, where the rows take 791 KB *(measured, `DatasetExclusionTests`)*. A rollup's totals hold the
  value's rows, and are not read under an exclusion.
- Late rows add their own entries, which merges add up.
- A window edge that cuts a granule or a bucket is exact by default: that granule's rows are read, and
  the granules the window holds whole are answered by their statistics. Windows drawn to the second over
  keys and measures drawn too give the rows' answer (`DatasetBlockAnswerTests`). With
  `ScanOptions.AlignWindow` the edge moves out to the bounds, on the partition's column, of the objects
  it falls in, and the scan reports the window applied. An edge on a later component of the key does not
  move: each value of the first component has granules of its own bounds on it, so that no one window
  would be the one applied.

## 7. Estimates, then the exact answer

Rather than a stored sample, the engine reads a fraction of the data: 0.1 %, 1 %, then 10 %, then the
whole. It draws ranges of consecutive granules, at least 1 MiB of each column read, at random in each
object and in each value of the key's first column, a value's share of the ranges growing with its
rows; a value smaller than a range in an object is read whole when it is drawn. Each tier returns
estimates with their margin, a stratified cluster sample, which narrow until the exact answer. The unit
is a range rather than a granule because a granule holds about 15 KB of two columns: drawn one by one, 1 %
of 30 days would be some 10⁵ requests, about 50 s at 128 in flight and past the rate S3 sustains on a
prefix (section 9). "5xx by endpoint, every service, 30 days" at 1 % reads 1.5 GB in about 1 500
ranges, in about 1.6 s cold *(computed)*. A scan option says whether the caller wants the exact answer
only, accepts estimates, or sets a cost threshold, one second by default; the cost of the exact answer
is announced before it is read.

The estimates exist as `EstimateAsync` ([16-queries.md](16-queries.md#59-estimates-then-the-exact-answer)).
Each drawn range runs as a part of the question and gives its states, and the strata are runs of
neighbouring objects, which follow the partitions, with two ranges drawn in each at least. The
cost of the exact answer is announced by every tier, priced by the planner object by object (section 8)
with no read; under `EstimateOptions.ExactUnder`, a second by default, the exact answer comes at once.

## 8. The planner

1. Time and the column sheets drop objects.
2. The sort key's sparse index, and where each value lies, narrow the read to the granules of the
   filtered values.
3. For each object, the cheapest of: a learned partial; its counts, or a rollup's over it, for a question
   on one column; the granule statistics, for the granules they settle; reading the question's columns on
   the granules left.
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

The price a question announces before a read (`CostAsync`, and the threshold of the exact answer, section
7) follows each object down the path its explanation names, and counts what that path reads:

- **the rows**: the open, a request for the 64 KiB read from the object's end, or its footer's bytes when
  more; the bytes of the columns the question reads and filters by; a request a chunk. A column's chunks
  but its last lie where its writer flushed them; the last chunks of every column lie together at the end
  of the data, in the columns' order, and a read joins two of them across the last chunks between when
  those take 256 KiB at most, reading them too. In an object of a few mebibytes every column is one
  chunk, and a read of the first and fourth column reads the second and third.
- **the regions** of counts, statistics and indexes: their bytes, and the gaps a read joins between them.
- **the granules**: their statistics, and the rows of the granules they are not expected to answer. A
  granule that holds one value of each key and filtered column may answer; one that gathers short runs
  varies in the key's first component, and answers only a question of counts where one column varies.
  Each object says in its table of contents how many of its rows lie in granules that list a column's
  values, which its writer counts as it writes them; the rest is read as rows. A window on a column of the
  key, unproven by the object's summaries, cuts the granules at its bounds, which the price takes whole.
  An object none of whose granules would answer is read as rows, without its statistics.
- **the sort key's index**: the share of the object's rows its sparse index keeps under the filter,
  priced as granules of each column read, when the index is held, or read through the open an analysis
  makes anyway.
- **a learned partial**: nothing; the partials of the objects a compaction merged into it, nothing; else
  the rows of the family's columns.

A bound on another column than the key's, `Status >= 500`, varies in a granule like any column: its
extremes there span the statuses. On the battery of section 8.3 the price is a bound where an index of
values or the granules read less. On objects of a mebibyte, every question of `DatasetFormulaTests`
reads within a twentieth of its price, and on the layout guide's day sorted by service, a question on
one service read 299 KB for a price of 216 KB *(measured)*.

An object read as rows is read in two passes when its filter keeps so few rows that most granules of the
widest column read would hold none: the columns of the filter first, the rows they keep marked, then the
other columns for those rows alone, by their places, which reads the granules that hold them alone. The
first pass decides: it notes the blocks of 1 024 rows that hold a kept row, and the second pass reads by
places while those are fewer than half the object's, and while the kept rows lie farther apart in the
widest column than the store's profile joins two ranges across; past that, it reads the granules that
hold a kept row whole, or the first pass gives up and one pass reads the object. A first pass over a
large object reads a sample first, its first thirty-second, and gives up early when the sample says the
second pass would read most of the object. A rare code scattered among thirty rows of 100 000, whose
bodies of 400 bytes lie in chunks of 512 rows, read 4.4 MB in 52 requests in two passes, where the same
filter in one pass read 30.3 MB in 198 *(measured, `DatasetTwoPhaseTests`)*. Where the rows lie
together, in a value's granules, the sort key's index or where each value lies (section 5.4) has
already narrowed the read, and one pass reads them.

### 8.1 What each shape costs

At the design rate, S3 profile, cold, one node, on the reference table sorted by emitter
*(computed)*.

| question | on the fly, no synopsis | with the synopses | asked again |
|---|---|---|---|
| top 100 endpoints over 13 months | ~17 min (1.3 TB) | ~50 ms, the rollups' counts | ~11 ms |
| one endpoint per day over 13 months | ~0.9 s for a median service, ~80 s for the largest | ~50 ms, the rollups' counts by bucket | ~4 ms |
| errors per hour over 30 days | ~45 s (52 GB) | ~35 ms, the counts of objects of an hour | ~1 ms |
| 5xx per service per hour, 30 days | ~45 s | ~0.3 s, granule statistics | ~20 ms |
| a service's routes under 5xx per hour, 30 h | ~40 ms, median service; ~0.45 s, the largest | the same | ~10 ms |
| per endpoint for version X, 30 days | ~0.1 s, median service; ~5.5 s, the largest | ~50 ms, granule statistics | ~10 ms |
| a host's errors per hour, 30 days | ~40 ms | the same | ~5 ms |
| distinct hosts per region, 30 days | seconds: granules at the emitters' boundaries | ~0.1 s, exact, granule statistics | ~10 ms |
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

### 8.3 Any column, in either order

The battery "any column" asks, of the reference generator's requests, for each of seven columns a
count by value and hour, then a filter on its most frequent value and on its rarest grouped by each of
the six others: 91 questions, of the same rows in the order they arrive (partitioned by hour, sorted by
the instant, an object an hour) and sorted by service (a key on the service, the hour and the instant,
its first two indexed, objects of the same size). Each question gives the rows' answer, its plan's path,
its price and what a cold handle reads; 4 hours at 50 rows a second, 720 000 rows, 29.6 MB in arrival
order and 23.0 MB sorted by service *(measured, `vortex-queries --micro columns`)*:

| column | its 13 questions, arrival order | sorted by service |
|---|---|---|
| `Service` | 12.4 MB | 5.8 MB |
| `Endpoint` | 18.2 MB | 11.4 MB |
| `Method` | 6.7 MB | 9.6 MB |
| `Status` | 6.2 MB | 8.9 MB |
| `Region` | 5.1 MB | 6.4 MB |
| `Version` | 4.2 MB | 4.7 MB |
| `Host` | 7.5 MB | 6.1 MB |
| all 91, through the plan | 60.2 MB | 52.8 MB |
| all 91, through the rows | 176.9 MB | 79.6 MB |

Every answer is the rows', and no question reads more than 1.89 times its price, 1.08 sorted by
service. Sorted by service, every question but the counts by hour reads less: 56 to 80 % less on the
service and the columns that depend on it, the version, the host and the endpoint, and 15 to 40 % less
on the method, the status and the region, which every service holds. The counts by value and hour read
more: in arrival order the objects' counts answer them, an object an hour, where sorted by service an
object holds every hour and they read its rows, 3.3 to 4.4 MB each. A first ask learns its family's
partial, which then answers the rare value's questions after the frequent one's; asked again, any of
these questions reads nothing.

## 9. What the store sees

At the design rate, partitioned by day, a consolidated day holds some 160 objects of 1 GiB, about 173 GB
a day, and the current day its size classes, a few dozen objects. A question's requests are bounded by
the objects its window touches, and within them by the granules the sort key's index keeps, or by the
rollups over them when theirs answer it, never by its rows.

### 9.1 Reads

Cold, S3 profile, one node *(computed)*:

| question | path | requests | bytes | time |
|---|---|---|---|---|
| top 100 endpoints over 13 months | the rollups' counts | 20 to 40 | 10 to 25 MB | ~50 ms |
| one endpoint per day over 13 months | the rollups' counts by bucket | 20 to 40 | ~1 MB | ~50 ms |
| errors per hour over 30 days | the counts of objects of an hour | 30 to 50 | under 2 MB | ~35 ms |
| 5xx per service per hour, 30 days | granule statistics | 50 to 80 | 0.2 to 0.4 GB | ~0.3 s |
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
- The rollups turn the reads of a question by day over 13 months, a request an object, into about 14
  for each rank of rollups it takes, a header and the chunks of the values asked *(computed)*, and the
  estimates read ranges rather than granules (section 7).

### 9.3 Writes

| work | bytes | requests |
|---|---|---|
| appends | the data, once | 3 to 5 PUT each: the object, the commit, the pages of the tree; how often is the application's choice |
| compaction: the size classes and the consolidation | each row rewritten 2 to 3 times: 4 to 6 MB/s written, a little less read | under one PUT and one GET a second, in parts of 16 to 64 MB |
| synopses in the objects | about 1.1 GB a day, 0.6 % of the data, most of it the granule statistics (section 10.2) | within the objects' parts |
| rollups | a few MB a column and a run of 64 objects | a PUT a rollup, once the objects of its run stand |
| listings | none: the commit tree is the catalog | none |

The composite tables this design replaces wrote 7.7 GB a day, 4.4 % of the data.

## 10. Writing and sizes

### 10.1 What writing costs

| work | ns a row |
|---|---|
| sorting each append, sorted merges | 50 to 100 *(measured, `RowEncodingBenchmarks`)* |
| granule statistics and locates | 19.1 *(measured)* |
| counts, seven columns | ~56 |
| quantile and distinct sketches | ~67 |
| column sheets, seven columns | ~42 |
| **total** | **~250 to 300**, plus 2 to 3 µs a row for each learned family kept up |

At the design rate that is 0.013 to 0.015 of a core *(computed)*.

### 10.2 Sizes

The synopses grow two ways. The sheets, and the sketches the counts keep of the columns of few values,
cost each object about the same whatever its rows: they follow the objects a day. The counts, the
granule statistics and the locates follow the values and the granules, which the rows set. A day of the
reference table sorted by emitter, in some 160 objects of 1 GiB *(computed from the units measured
below)*:

| structure | a day | a unit, measured | the unit first reckoned |
|---|---|---|---|
| column sheets | ~6 MB | a sheet 3.4 to 6.7 KB, 0.72 to 0.92 of its formula | 4.5 KB and 1.125 bytes a distinct value |
| counts | ~70 MB | an entry 67 to 69 bytes | 52 to 61 bytes |
| counts' sketches | ~50 MB | a sketch 0.3 to 0.45 KB at the bench's scale | 0.5 to 2 KB a quantile sketch, 1.5 KB a distinct one |
| granule statistics | ~690 MB | 1 307.7 bytes a granule, 5.3 × 10⁵ granules a day | 200 bytes |
| locates | ~300 MB | 570.6 bytes a granule | |
| learned partials | what is asked | | |
| **total** | **~1.1 GB, 0.6 % of the data** | | |

The units are those of `vortex-queries --micro columns` and `--micro granules`, the reference requests
at 50 and 200 rows a second and at the design's emitters, each sheet weighed against its formula over the
object's own distinct values *(measured)*. The granule statistics weigh most: a granule lists the values
of the columns of many values it holds, the endpoints 677 bytes and the hosts 423, where the model
reckoned 200 bytes a granule; listing fewer values a granule, or none past a share of its rows, would
bring them toward it, and is weighed against the questions they answer.

For a petabyte of data, fifteen such tables kept 13 months, the synopses weigh about 6 TB on the store,
of which the local cache's working set is the last 30 days and the rollups; the sheets and directories
take under 1 GB of memory *(computed)*. The design this one replaces kept 7.7 GB a day of synopsis tables
for this table, 4.4 % of the data *(measured)*.

### 10.3 Index kinds

| kind | where | what |
|---|---|---|
| `vorticity.column.sheet.v1` | an object's region, which its table of contents names | the column sheets |
| `vorticity.column.counts.v1` | an object's region, which its table of contents names; a rollup's | the counts of each value, kept by bucket in a rollup whose run spans several partitions |
| `vorticity.column.sketches.v1` | an object's region right after the counts, which its table of contents names; a rollup's | the counts' quantile sketches and distinct registers |
| `vorticity.granule.stats.v1` | an object's region a column, which its table of contents names | each granule's rows, nulls, values by their numbers and their rows, and a measure's states |
| `vorticity.granule.histograms.v1` | an object's region a measure, which its table of contents names | each granule's values of the measure in a log-linear sketch |
| `vorticity.locate.v1` | an object's region a column, which its table of contents names; the partition's index | where each value lies: the values and their granules |
| `vorticity.partials.v1` | the local cache; beside the rollups | learned partials |

Readers skip a kind they do not know, as they do now. The kinds of the former synopsis tables are no
longer written nor read. The leaf entry keeps its table of contents, at a second version that holds
them no more and names the regions of the column sheets, counts and sketches; a table of contents of
another version is not read, and a dataset is read in commit format 5 alone ([sources and
offsets](13-dataset.md#84-sources-and-offsets)).

### 10.4 The recommendation of a key

The planner counts each shape of question in bounded counters. From them the library recommends the
key its questions call for, the values whose order would have saved the most bytes read, through a
diagnostic call and telemetry. The application declares its layout; the library never does.

Built in a handle (`VortexDataset.RecommendLayoutAsync`, `LayoutAdvice`): each object read as rows
whole under a filter, its rows not narrowed by an index, credits each top-level column and each variant
key's read the filter reads but the layout key's first column with its bytes of the columns read, by the
cost formula, times the share of its rows the filter left out, which an order by the value would have
left in other granules; a filter on several values credits each the whole, an upper bound. Sixty-four
values at most are counted, and the telemetry counts the same bytes by value
(`vortex.dataset.layout.avoidable_bytes`). The advice orders the values credited as a key would take
them: the fewest distinct values a partition first, by the column sheets of the version's objects, each
partition's registers merged, with the bytes a row would take of each in runs at its place; and it says
where the sparse index would stop, past the last component whose values still group a granule's rows
within a partition. Not built: the counters poured into the dataset and added up across processes; each
handle counts its own questions, and a variant key's read has no sheet to count its values.

## 11. Decisions, and the alternatives weighed

| subject | decision | alternatives weighed |
|---|---|---|
| the core | the group-by engine on the rows, every question | precomputed synopses as the main path, which answer only what they were built for |
| the fast path | synopses, per column only, with absolute budgets | composite synopsis tables under a budget as a share of the data, wrong both ways: 47 to 88 TB per petabyte, and nothing for a small dataset |
| layout | a partition, a sort key of up to sixteen components and its indexed prefix, which the application declares: columns, struct fields, variant keys or functions of them | one cluster column beside the key, which the first version of this design had; a clustering value of the key's first column with blocks cut on it, the version before this one; a sort key chosen per object, whose cost per question no one can predict |
| crossings of columns | learned after a question asks them, per object and family | tables of fixed crossings, built before any question, which miss the next crossing asked and grow with every column added |
| coarser grains | rollups beside the levels, which sum runs of objects' counts with no row read | monthly files of days and months, a calendar the library would have had to know; regraining inside the objects, which rewrites a day's 173 GB to merge its 120 MB of synopses |
| approximate answers | ranges of at least 1 MiB drawn on the fly, stratified by value of the key's first column | a stored sample, 4.4 GB for 13 months of the reference table, only a fixed fraction; blocks drawn one by one, some 10⁵ requests for 1 % of 30 days |
| requests to the store | one budget per process, shared by its questions and jobs | 128 in flight per question with no ceiling: about 4 000 requests a second from a single question |
| recent windows | computed on the fly | counts per minute, 1.27 GB a day for the reference table |
| granules | 8 192 rows, the writer's block: the zone of the zone maps, the place of the sparse index, the unit of the statistics and of a read of part of a chunk | blocks cut where the key's prefix changed, 4 096 to 65 536 rows, which held one group each but kept a second geometry beside the zones |
| tenants | none in the library: an application partitions or sorts by its tenant's column | a tenant notion of the library's own |
