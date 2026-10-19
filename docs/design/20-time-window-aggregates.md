# Ad hoc aggregates over long time windows

An application that appends events to a dataset (requests, log lines, metric points) at tens of
thousands of rows a second asks them questions nobody wrote in advance: how many rows, under any
filter on any columns, grouped by any of them and by slices of time, and which values come first.
This document is the record of the design that lets a dataset answer such questions over any window,
from a minute to several years, on an object store, without reading the rows, and with the answer a
scan of the rows would give, to the bit.

It was written as a proposal on 2026-10-18 against `main` at `485e796b`, and built from it. Its idea
fits in one paragraph. A data object covers a period of time. Inside it, rows fall into time slices,
and inside each slice they are sorted by the columns whose values repeat, then by their instant. Each
of those columns then becomes a short list of runs, and the length of a run is the number of rows that
hold that combination of values: the rows count themselves, and nothing is stored twice to count them.
Next to them, in the same object and invisible to every other reader, a synopsis keeps what the runs
cannot give cheaply: counts and states per slice, per hour and per object, and counts per value. A
question reads the synopses of the objects in its window, a small part of each, and reads rows only
where they cannot answer. The local disk keeps that part, never the rest. Rows reach their period
through a short life the dataset bounds: fresh objects that merge as they age, a level for late rows,
and one seal per period, which writes the period in one pass.

The pieces it built are described now where their components are ([what was built, and
where](#3-what-was-built-and-where-it-is-described)). This document keeps what those descriptions do
not: the need they answer, the examples they are judged on, the cases where a question still costs
more than its answer, the decisions taken and the alternatives weighed. Variant columns, whose paths
take the same primitives, have their own record ([21-variant-columns.md](21-variant-columns.md)).

Each claim says where it comes from:

| tag | meaning |
|---|---|
| (code) | read in the code, linked to its lines |
| (measured) | measured, by the bench or test it names |
| (estimate) | arithmetic from the volumes of [the need](#1-the-need) |
| (hypothesis) | a design bet, with what would refute it |
| (source) | an external source, listed with its date in [sources](#9-sources) |

## 1. The need

### 1.1 The rows

The rows are events. Each has an instant and other columns, shown here on a table of HTTP requests
that the rest of this document reuses:

```csharp
[VortexRecord]
public partial record struct Request(
    DateTime At,
    string Service,
    string Endpoint,
    string Method,
    short Status,
    string Region,
    string Version,
    string Host,
    string RequestId,
    long DurationNs,
    double Weight);
```

| column | distinct values in a day | distinct values in a minute |
|---|---|---|
| `At` | | |
| `Status` | 60 | 5 |
| `Method` | 8 | 8 |
| `Region` | 5 | 5 |
| `Service` | 600 | 600 |
| `Version` | 1 500 | 1 500 |
| `Endpoint` | 3 × 10⁴ | 10⁴ |
| `Host` | 10⁴ | 10⁴ |
| `RequestId` | 4.3 × 10⁹ | 3 × 10⁶ |
| `DurationNs`, `Weight` | numbers | |

Two facts shape the design. Any column may be what a page is about, so nothing privileges a column:
a page about one host asks the same questions as a page about one service. And columns are added at
run time by schema evolution, when a user declares a property, so a column the design must serve may
not exist when the dataset is created.

### 1.2 The volumes

| | a second | a minute | an hour | a day | 30 days | 13 months |
|---|---|---|---|---|---|---|
| rows | 5 × 10⁴ | 3 × 10⁶ | 1.8 × 10⁸ | 4.3 × 10⁹ | 1.3 × 10¹¹ | 1.7 × 10¹² |
| bytes, at 40 a row once compressed | 2 MB | 120 MB | 7.2 GB | 173 GB | 5.2 TB | 68 TB |

Several writers append batches of a few seconds each, mostly in time order, with a few late rows.
Retention deletes rows, and may differ per row: debug events kept a week, errors kept thirteen months.
Rows are also deleted on request, rarely.

Tables of metric points are larger still, about 2 × 10⁵ rows a second of 6 to 20 bytes. They take
the same primitives for their counts, and the rows of one series lie in one run, in time order,
which a later design for questions per series (a counter's rate, a gauge's last value) can build on.
This document does not cover those questions.

### 1.3 The questions

Nobody knows them in advance. A page or an explorer composes each one from what a user clicks, and
they share one shape:

```csharp
from r in requests.Scan<Request>()
where r.At >= from && r.At < to
   && /* any predicate over any columns: ==, !=, In, ranges, == null, &&, ||, ! */
group r by (Minute: r.At.Truncate(CalendarUnit.Minute), r.Service, r.Status) into g   // any columns, any number
orderby g.Count() descending
select (g.Key.Minute, g.Key.Service, g.Key.Status, g.Count())
```

Most of them are counts, counts per slice of time (minutes, hours, days), and the top 100 values of a
column under some filter, with an exact "others". Fewer ask for the weighted count (the sum of
`Weight`), for the sum, average, extremes or quantiles of a measure, or for a distinct count. Some
fetch rows: the newest rows under a filter, every row of one identifier, a text search. A page sends
8 to 15 questions at once, and the busiest deployments see hundreds a second across query nodes that
each answer any question.

### 1.4 The targets

"Cold" means nothing on the local disk, and "warm" means the synopsis parts of the window's objects
are there. Requests to the store run in parallel.

| class | 30 days | 13 months |
|---|---|---|
| a question the runs and synopses answer | under 1 s cold, under 100 ms warm | under 2 s cold, under 300 ms warm |
| the same, excluding one rare value | under 1.5 s cold | under 3 s cold |
| a selective question, through the indexes | first rows under 500 ms, all under 2 s | progressive |
| a broad question nothing summarizes | seconds, progressive | progressive |

A question the runs and synopses answer is also held to one dependent round trip per object cold and
none warm, tens of megabytes read whatever its window, memory bounded by the groups it delivers, and
no allocation per batch. At the load of the busiest deployments, its requests also stay within what
the store serves: Amazon S3 serves 5 500 reads a second per prefix, and OVHcloud 900 per bucket
([19-object-storage.md, Cloudflare R2, OVHcloud and Scaleway](19-object-storage.md#74-cloudflare-r2-ovhcloud-and-scaleway)).

### 1.5 Why reading rows cannot meet them

A count per service and per hour over 30 days reads two columns of 1.3 × 10¹¹ rows. At a byte and a
half a row for the instant and a dictionary code, that is 195 GB to fetch (estimate). Grouping by a
dictionary key alone costs about 4 ns a row, and by a composite key such as the hour and the service
about 32 ns (measured, [16-queries.md](16-queries.md#13-how-it-is-tested)): some 4 000 lane-seconds,
close to five minutes on fourteen lanes, even with every byte already in memory. The indexes do not
help, since every block holds every busy service. The answer has 600 × 720 rows.

### 1.6 What any answer without rows has to keep

A question over a set of columns needs at least the joint distribution of those columns over its
window: how many rows hold each combination of their values. A structure built without knowing the
questions must therefore keep the joint distribution of every column it means to serve, all of them
together. Its size is the number of distinct combinations the rows hold, which the data decides, not
the design. Columns whose values repeat combine into far fewer combinations than there are rows, and
identifiers do not. The design keeps every combination of the columns whose values repeat, at the
finest grain of time, as the order of the rows themselves, and leaves the rest to rows and indexes.

## 2. Terms

| term | what it is | not to be confused with |
|---|---|---|
| period | the range of time a sealed data object covers, a calendar unit the dataset weighs from its volume | the retention window of a dataset |
| fresh level | a level of objects younger than their period's seal, key-disjoint, which grow by merging adjacent objects | level 0, whose objects overlap |
| late level | the level of rows older than the fresh object they would overlap, or than their period's seal, whose objects overlap under a ceiling | a partition |
| seal | the job that writes a period's object in one pass, once the period's end and the lateness horizon have passed | sealing a file, which checks its order |
| chunk directory | per column, where its chunks lie in the object and the rows each holds, so that the chunks covering a slice are found by its rows | the slice table, which gives a slice's rows |
| identifier | a column the application declares as one whose values questions look up, which gets hashed postings outside the index budget | a synopsis column |
| erasure delay | how long a deleted row may stay in the bytes of the store, declared by the application | the retention window |
| time slice | the rows of an object whose instant falls in a range of time: a contiguous range of rows | the slices of a sorted run, in key cursors |
| sorted slice | a time slice whose rows are sorted by its synopsis columns, then by their instant | a sorted object, in key order |
| synopsis column | a column, or a shredded path of a variant column, whose values repeat, by which slices are sorted | a measure column |
| measure column | a numeric column, or a numeric path of a variant column, whose values rarely repeat, whose states the synopsis keeps | a dimension, in other tools |
| run | a range of consecutive rows of a sorted slice holding the same value of a synopsis column, and so the same values of every synopsis column before it | a sorted run, the index kind |
| synopsis part | the synopsis columns and the synopsis tables, written together near the end of the object | the data's footer |
| run statistics | per slice and per combination of synopsis columns, the count and the states of the measure columns | zone maps, which are per block of 8 192 rows |
| coarse table | counts and states per combination of synopsis columns, per bucket coarser than the slice (an hour, the object) | a rollup kept outside the object |
| value counts | for one column and one bucket, how many rows hold each of its values, with states | a dictionary, which lists values without counts |
| state | the mergeable form of an aggregate: a count, an exact sum, an extreme and its presence, a sketch | the answer delivered, an average or a quantile |
| edge | the part of a question's window that cuts a time slice | |
| needle | a question about one rare value, such as every row of one `RequestId` | |
| lateness horizon | how late a row may arrive after its instant, declared by the application | the retention window |
| partition column | a leading clustering-key column of few values, a tenant, a source or a retention, whose rows a dataset of periods keeps and expires apart, every object holding one of its values | the partitions of a group by |
| synopsis cache | the synopsis parts of objects, and what leads to them, kept on the machine's disk beyond the life of a process | the session's `SegmentCache` and a handle's page cache, both in memory |
| round trip | a request whose answer the next request waits for, 10 to 100 ms on an object store | a network round trip inside a region |

## 3. What was built, and where it is described

Eight rules carry the design.

1. Time is the only axis between slices. An object covers a period, and its slices follow each other
   in time.
2. Inside a slice, the rows count themselves. They are sorted by the columns whose values repeat, so
   that each of those columns becomes runs whose lengths are counts. What the runs cannot give cheaply
   is kept beside them, within a budget.
3. Everything an object knows is in the object. Its rows, its indexes and its synopsis are written,
   compacted, expired and versioned together.
4. Nothing knows the questions. The order, the runs and the synopsis are decided by the data alone,
   and serve any predicate and any grouping over the columns they hold.
5. No size or threshold is guessed. Periods, tables and the cache's policy come from cost models whose
   constants a bench measures, and from a byte budget the user sets. The slice's target is the one
   exception: a model of its edges against its tables was measured and lost to a fixed minute of the
   example, since a slice is also the rows the job sorts together ([decision 3](#7-decisions)).
6. A question reads synopses first and rows last, and its answer is the same bits whichever it reads.
7. The local disk keeps synopsis parts, never the rest of an object.
8. Rows reach their period through a life the dataset bounds: fresh objects that merge as they age, a
   level for late rows, and a seal per period, and every level keeps the read bounds a dataset had.

| primitive | what it gives | where it is described |
|---|---|---|
| objects that cover periods | a window of 30 days holds tens of objects, not a million | [13-dataset.md, the invariant](13-dataset.md#52-the-invariant), [15-compaction.md, a dataset of periods](15-compaction.md#a-dataset-of-periods), [13-dataset.md, the commit protocol](13-dataset.md#8-the-commit-protocol) for the length of a period |
| what every dataset gains | objects that grow with keys in order too, a cap of 64 MiB, exact float sums past 69 billion values, mark limits as shares of an object, a bound on a put's length | [13-dataset.md, the invariant](13-dataset.md#52-the-invariant), [16-queries.md, sums](16-queries.md#51-sums-exact-and-the-same-bits-whatever-the-cut), [13-dataset.md, deleting and updating rows](13-dataset.md#12-deleting-and-updating-rows), [19-object-storage.md, the objects of a dataset](19-object-storage.md#112-the-objects-of-a-dataset) |
| sorted time slices | counts of every combination of the repeating columns, per slice, stored as the rows' own order | [11-write-strategy.md, sorted time slices](11-write-strategy.md#9-sorted-time-slices-and-shredded-variants), [10-indexes.md, the time slices of a file](10-indexes.md#44-vorticityslicesv1-the-time-slices-of-a-file), [12-index-reads.md, sources of order](12-index-reads.md#2-sources-of-order) |
| the synopsis | run statistics, coarse tables and value counts, chosen within a budget | [10-indexes.md, the synopsis part](10-indexes.md#48-vorticitysynopsiscolumnsv1-the-synopsis-part) and [its tables](10-indexes.md#49-vorticitysynopsistablev1-the-synopsis-tables) |
| states | exact sums over any window, distributions, distinct sketches, the same bits by every path | [16-queries.md, distributions and quantiles](16-queries.md#56-distributions-and-quantiles), [states kept past a scan](16-queries.md#57-states-kept-past-a-scan), [10-indexes.md, the kinds of persisted states](10-indexes.md#56-the-kinds-of-persisted-states) |
| reading without the data | one round trip per object cold for whole periods, two for a shorter window, no footer | [12-index-reads.md, the rows of a time slice](12-index-reads.md#51-the-rows-of-a-time-slice-without-the-footer), [13-dataset.md, what a node carries](13-dataset.md#42-what-a-node-carries) for the table of contents in the leaf entry |
| answering a question | synopses first, rows last, exact edges, exclusions, deletes corrected, newest first | [12-index-reads.md, aggregations from a dataset's synopsis](12-index-reads.md#45-aggregations-from-a-datasets-synopsis), [16-queries.md, statistics and zone maps](16-queries.md#93-statistics-and-zone-maps) |
| needles | an identifier found in three dependent reads per object cold, two warm | [10-indexes.md, hashed postings](10-indexes.md#57-vorticitypostingshashedv1-value-to-blocks-through-the-hash), [the policy](10-indexes.md#6-the-policy) |
| the synopsis cache | warm questions make no request to the store | [19-object-storage.md, the synopsis cache](19-object-storage.md#105-the-synopsis-cache-on-the-local-disk), [09-contracts.md](09-contracts.md) |
| retention and late rows | expiry reads nothing, late rows rewrite nothing large | [13-dataset.md, deleting and updating rows](13-dataset.md#12-deleting-and-updating-rows) for expiry and partition columns, [13-dataset.md, the commit protocol](13-dataset.md#8-the-commit-protocol) for late rows |
| columns known only at run time | the same answers for columns added later | [16-queries.md, the tool path](16-queries.md#11-the-tool-path), [10-indexes.md, the policy](10-indexes.md#6-the-policy) |

The guide shows each of them from the caller's side ([datasets.md](../guide/datasets.md),
[aggregates.md](../guide/aggregates.md), [indexes.md](../guide/indexes.md)).

## 4. The examples

Thirty periods of a day, nothing on the local disk, 30 to 50 ms a round trip. Every figure of this
table is an estimate, written before anything was built.

| question | read from | entries | bytes | round trips, cold | warm |
|---|---|---|---|---|---|
| a count per service and per 12 hours, 30 days | the value counts of `Service`, by hour | 4.3 × 10⁵ | about 2 MB | 1 | under 20 ms |
| services under `Method == "GET"`, 30 days | the coarse tables by period, down to `Service` | 3 × 10⁵ | about 2 MB | 1 | under 20 ms |
| any predicate over `Status`, `Method`, `Region` and `Service`, grouped by two of them, per hour, 30 days | the coarse tables by hour, down to `Service` | 3.6 × 10⁶ | about 15 MB | 1 | about 30 ms |
| one service per minute over the last hour, under any predicate on `Status`, `Method` and `Region` | the runs of 60 slices, inside that service's ranges | about 3 × 10³ | under 1 MB | 1 | under 5 ms |
| the errors of one service, by endpoint, per day, 30 days | the coarse tables by period, down to `Endpoint`, through their postings on `Service` | 6 × 10⁴ | under 1 MB | 1 to 2 | under 10 ms |
| the top 10 endpoints and "others", 13 months | the value counts of `Endpoint`, by period | 1.2 × 10⁷ | about 60 MB | 1 | about 50 ms |
| one endpoint per day, 13 months | the value counts of `Endpoint` by period, sorted by value, through their fence pages | 395 | under 1 MB | 1 to 2 | under 10 ms |
| one host per minute, the last day | the value counts of `Host` per minute, sorted by value, through their fence pages | 1 440 | under 100 KB | 1 to 2 | under 5 ms |
| every row but those of one request, per service and per 12 hours, 30 days | the value counts of `Service`, minus the needle's rows, each attributed to its run by its position | 4.3 × 10⁵ and a few rows | about 3 MB | 3 to 4 | one round trip |
| a count per service and per 12 hours, 30 days, the window unaligned | the value counts by hour, and the instants of its first slice | 4.3 × 10⁵ and 3 × 10⁶ instants | about 15 MB | 2 | one round trip |
| the same count per service, without any of this | rows | 1.3 × 10¹¹ | about 195 GB | thousands | minutes |

The same questions run on generated requests, at a 2 500th of the example's rate, 24 periods of an
hour, behind a store that counts and waits 30 ms a round trip (measured, `vortex-queries --micro
questions`). A count per service and per 12 hours makes 24 requests in two dependent round trips cold
and none warm, 3.6 ms, against 120 requests in 120 round trips and 3.9 s through the rows. With tables
kept up to the data's own bytes, the shape of the example's budget at that rate, the services under
`GET`, the errors of one service by endpoint, the count per endpoint and one endpoint per day answer
the same way, cold in 68 to 72 ms. Leaving one request out costs 337 requests in six round trips cold,
the identifier's index read in every object, and 25 requests in one round trip warm. One host per
minute reads its rows: the value counts sorted by value and their fence pages are not built, so no
table is deep enough. A window that cuts slices reads the rows of the slices it cuts, two hours at
that scale, where a slice is the whole hour.

## 5. The worst cases

### 5.1 How a question's cost is counted

A question's time is the sum of four terms, each of which a counting store and the metrics count
rather than time:

| term | what it counts |
|---|---|
| round trips | requests to the store that the next one waits for |
| synopsis bytes | the bytes of synopsis parts read |
| runs and table rows | the entries of synopsis tables walked |
| data rows | the values of data columns read |

The notation: `N_s` rows per slice, `R_k` runs of a slice's `k`-th synopsis column, `S` slices and `P`
objects in the window, `G` groups in the answer. On the example (estimates): `N_s` = 3 × 10⁶,
`R_4` = 3 × 10³, `R_6` = 4 × 10⁴ and `R_7` = 5 × 10⁵ per minute, `S` = 43 200 over 30 days and
5.7 × 10⁵ over thirteen months, `P` = 30 and 395. A table row costs about 12 ns to fold, as a data row
costs a few nanoseconds to group (measured, `vortex-queries --micro synopsistables` and
[16-queries.md](16-queries.md#13-how-it-is-tested)).

### 5.2 What is bounded

- A question over the columns of the synopsis part, on a window aligned on slices, reads no data row,
  makes one round trip per object cold when its window covers whole periods and two when it is
  shorter, none warm, and walks at most the rows of the coarsest table that holds its columns per
  bucket.
- An edge reads the rows of at most one slice at each end, found through the chunk directories.
- A needle on a declared identifier makes three dependent reads per object cold and two warm, then
  reads the rows it finds.
- No question reads a column it does not touch, or a value twice.
- A selective question reads no more than over the same rows in objects of 4 MiB, by the rule that
  sizes chunks from the footer's cap. A question that scans reads no more than the rows of the columns
  it touches, which is what it would cost without any of this.

The answer's own size, `G` groups, is a floor every design shares. The cases below cost more than
their answer.

### 5.3 Time

| case | what it costs | on the example | what the design does | what remains |
|---|---|---|---|---|
| bucket boundaries that cut slices: a width that is not a multiple of the slice's, or a time zone whose offset is not | one slice of rows per boundary cut, `B · N_s` | 500 points over 30 days at 86.4 minutes: 1.5 × 10⁹ rows, seconds | the plan states the boundaries it cuts | a caller that insists on its width pays `B · N_s` |
| an unaligned window | the rows of one slice at the start, the end falling in level 0 or the fresh levels, and one round trip more, even warm, since the instant lies with the data | 3 × 10⁶ rows, about 13.5 MB, 10 ms and a round trip | aligned windows read none, and the chunk directories spare the footer | a round trip warm |
| a slice past its target, from a burst | its edge reads more rows | ten times the target: 3 × 10⁷ rows, about 0.1 s | the writer splits it into finer calendar units | none |
| a grain finer than the slice | the rows of the slices concerned | per-second counts over an hour: 1.8 × 10⁸ rows | slices sized by volume, so the finest grain grows only where rows are few | rows |

### 5.4 Depth

| case | what it costs | on the example | what the design does | what remains |
|---|---|---|---|---|
| a deep question over a long window, with no coarse table at its depth | the runs of its depth in every slice, `Σ R_d` | down to `Endpoint` over thirteen months: 5.7 × 10⁵ × 4 × 10⁴ = 2.3 × 10¹⁰ runs, minutes | coarse tables by period down to the deep columns, when the budget allows: 395 × 2 × 10⁶ = 8 × 10⁸ entries, seconds | progressive. The coarsest table lives in one object, so the period's length bounds it |
| untouched shallow columns: a question touches a deep column but not some of those before it | the runs of its depth, not the combinations of the columns it touches: their product with the values the untouched columns take inside each combination | `Version` and `Endpoint` together, without `Status`, `Method` and `Region`: about 15 times their own combinations | value counts serve single columns. No table over a subset of columns ([decision 5](#7-decisions)) | ×10 to ×100 over the ideal, on structures already ×100 to ×1 000 smaller than the rows |
| a non-selective predicate on a deep column, under a shallow grouping | the rows of the deep column's table, the predicate evaluated once per table row | `Endpoint` matching a pattern half of them match, by `Service`, 30 days by hour: 720 × 5 × 10⁵ = 3.6 × 10⁸, a second or two | the complement, when the predicate is the negation of a selective one | the rest |
| a selective predicate on a deep column without postings | every table row of that column, or its rows | `Host == "h-17"`: 5 × 10⁵ runs a minute | value counts that tell how many rows the value holds, where the budget keeps them | rows, where the budget keeps none |
| many facets: a dozen repeating columns or more, as a table of spans holds | the deepest facets' runs, of one or two rows, and their rows spread over thousands of ranges a minute | `route` after a dozen shallower facets: runs close to its rows | value counts and coarse tables by bucket for deep columns, which the budget admits by their benefit. Measured on spans with a dozen facets, the route's value counts answer a count per route from 22 tables an hour (`vortex-queries --micro facets`) | the deepest facets' rows, for questions finer than their tables |
| a measure deeper than the run statistics | the measure's values for the touched runs | the average duration per `(Service, Endpoint)` per minute when run statistics stop at `Service`: up to every row of the window, 300 GB over 30 days | coarse tables carry the states at their depth, and the budget keeps states wherever it keeps counts, or the plan says it cannot | the measure's rows, at grains finer than the coarse tables |
| a column whose number of values moves between slices | the slices whose own counts would order it otherwise pay a few runs more | a column with few values some minutes and many others | one order per object, measured on its rows | a few runs more in those slices |

### 5.5 Columns nothing summarizes

| case | what it costs | on the example | what the design does | what remains |
|---|---|---|---|---|
| a threshold on a measure | the values of the runs whose minimum and maximum straddle it | `DurationNs > 10⁹`, where fast and slow requests mix in every run: every row, 1.3 × 10¹¹ over 30 days | a threshold on the distribution's grid is answered from the sketches | a threshold off the grid reads rows |
| an identifier the application did not declare | its column, over the window | `RequestId` over 30 days: about 2 TB, hours (estimate) | declared, it gets hashed postings in every object written after | the objects written before, until a compaction rewrites them |
| a column whose values rarely repeat, under a non-selective predicate: the top 20 raw URLs, an exact distinct count of `RequestId`, a text search on a frequent word | the column, over the window | the URL column over 30 days: about 1.3 TB, hours | progressive, newest first, and a distinct sketch per slice when the budget keeps one | inherent: nothing that ignores the questions answers them without the column |
| a needle that is not rare | the rows holding it | in proportion to them | nothing: the answer is that large | output-bound |
| a predicate between two columns, or a function of a column the synopsis does not hold | that column's rows | | rows | rows |
| a path of a variant column that an object left in its residual | the residual of the rows whose shape holds it | a key a few rows of one source hold | the synopsis counts it as a measure where its budget admits, shapes keep its rows together, and postings find them ([21-variant-columns.md](21-variant-columns.md#5-the-worst-cases)) | a parse per row that holds it, and its rows for any question that groups or filters by it |

### 5.6 Scale

| case | what it costs | on the example | what the design does | what remains |
|---|---|---|---|---|
| many objects | one round trip per object, in parallel | 395 objects over thirteen months: two to four rounds of parallel requests, 0.1 to 0.2 s cold | periods sized by volume, objects read many at once | none |
| many groups, or a top N whose ranking needs every group | `G` hash lookups, `G log N` to rank, memory for `G` | the top 100 of `(Endpoint, Host)` over 30 days: about 10⁶ groups, tens of milliseconds | the governor and the spill ([16-queries.md](16-queries.md#95-memory)) | output-bound |
| fresh rows | the rows of level 0, and the objects of the fresh levels | the last hour: level 0's appends and about 20 fresh objects, measured on periods of a day with fan-out 30 (`vortex-queries --micro lifecycle`). A path of a variant column costs 60 to 120 ms of one core more, since appends leave variants unshredded | fresh merges by f, and a memo of level 0's parsed paths | the objects f leaves, and a lagging loop, which `PlanCompactionAsync` reports |
| deletes | the marked rows' contributions, computed once per object and vector | in proportion to the marks | tables corrected by the marked rows, and a purge per erasure delay | bounded by the purge policy |
| rows newest first under a broad filter | the last slices of the newest objects | 200 rows: no request from a fresh tail its open holds whole, 2 dependent requests and 68 KB when the page ends in a sealed period, the segment of instants its slices lie in and then the rows (measured, at the test's scale) | slices bound each slice in time, objects open as the read reaches their newest instant, and a slice's instants come from the segments the slices before it read | the open of an object the page reaches, a round trip when its footer is not held |
| a lookup by key, a rank, a page in key order inside a sorted slice | the slice's instant | about 13.5 MB a slice (estimate) | the index of an identifier the key holds | a slice's instants per seek |
| requests a second at the busiest load | the reads each question sends | 9.7 reads a question without the cache, 5.7 with every region held, on the bench's mix: 569 to 972 questions a second under S3's limit, 93 to 159 under OVHcloud's (measured, `vortex-queries --micro cachetrace`) | the cache, and data objects named by a fresh uid that S3 can partition | the data's reads, and a cold load past the store's limit |
| long jobs | a seal of a day is minutes of work at the example's rate | 173 GB read and written | late rows routed away from a period that seals, and a lifecycle rule for the uploads an abort missed | a failure restarts the seal |
| a window larger than the cache | cold round trips for what the cache does not hold | thirteen months of synopsis parts, about 300 GB | an eviction policy that a sweep does not empty, chosen by replaying traces | none |
| hundreds of questions a second | their processor time | | each question is cancellable and governed, and a progressive one yields as it goes | sizing |

### 5.7 What remains

Two families remain, and they belong to the problem rather than to this design:

- The first is the questions whose answer is large: many groups, a fine grain over a long window, a
  needle held by many rows. No design answers them in less than their answer's size.
- The second is the questions that put a non-selective predicate on a column whose values rarely
  repeat: identifiers, raw URLs, free text, arbitrary thresholds on measures. A structure that does
  not know the questions cannot summarize those columns, so the question reads the column, newest
  first, progressively.

Three overheads belong to this design, and each has its lever. Cut bucket boundaries cost a slice of
rows each, and aligned widths remove them. Untouched shallow columns multiply the table rows walked,
and value counts serve single columns. A deep question over a long window walks a coarse table per
period, and longer periods shorten it.

The order and the life of a row add costs, accepted with it: a row is rewritten 4.7 times on periods
of a day under late rows and four writers, 2.7 times when rows come in order (measured, `vortex-queries
--micro lifecycle`), and a slice of rows is read per unaligned window.

## 6. How it is held

| promise | gate |
|---|---|
| runs count exactly | at every version of the interleaving fuzzer, every count the tables give, minus the marks, equals the count of the live rows, and every coarse table equals the merge of its slices' |
| the same bits by every path | the query matrix with synopses on and off, at degrees 1 to the machine's, over objects of one slice, several slices and several periods, before and after compaction, questions drawn at random, under deletes and exclusions, and on variant paths (`SynopsisAnswerTests`) |
| slices stay sorted | sealing refuses an object whose slices are out of order or unsorted, and a fuzzer of appends, late rows, fresh merges and seals seals every object it makes |
| one round trip per object cold for whole periods, two for a shorter window, none warm | ratchets on a counting store with latency (`ADayOfHoursCostsTheRoundTripsOfAnHour`, the cache's tests) |
| sums hold any window | a sum past 2^36 values gives the bits of its exact total rounded, whatever the order of its merges, and throws only past its capacity (`IndexedSumTests`) |
| the life of a row | the rewrites per row and the objects per window on the bench of appends with late rows, several writers and deletes (`vortex-queries --micro lifecycle`), and no seal abandoned by a late row in the fuzzer |
| deletes at the scale of a period | a hundred scattered rows in a period's object rewrite nothing, and a purge runs once per erasure delay |
| large objects stream on every store | a put of 200 GB planned under each store profile's rules, R2 and Scaleway included, by the S3 emulator of the tests (`UploadTests`) |
| needles | the round trips per object, cold and warm, and the bytes of each identifier's index, as ratchets |
| requests a second | the reads a question sends, with and without the cache, against each store's limit (`vortex-queries --micro cachetrace`) |
| the synopsis stays within its budget | the bytes of every table against the budget, and the verdicts recorded in the write report |
| no allocation per batch | the allocation ratchets, extended to the new kernels |
| Rust reads every object | objects of sorted slices with synopses in the cross-check (`bench/crosscheck.sh`) |

## 7. Decisions

| | question | decided | the other way |
|---|---|---|---|
| 1 | where the counts of combinations live | in the rows' own order, sorted inside each slice. An object is 4 % smaller than its rows in time order, 10 % smaller than a leveled object with its sorted run (measured) | in a synopsis table beside rows kept in time order ([alternatives](#8-alternatives-weighed)) |
| 2 | the order of the synopsis columns | one per object, measured on its rows by the runs each adds, and recorded in its slice table ([sorted time slices](11-write-strategy.md#9-sorted-time-slices-and-shredded-variants)) | measured per slice, which breaks the sorted merges of coarse tables, or per dataset |
| 3 | the slice's target | a minute of the example's rate, 3 × 10⁶ rows, cut at the coarsest calendar unit that holds no more. A slice is also the rows the job sorts together, so a narrower one breaks the runs of the synopsis columns | a target per object from the rows a cut hour's edge reads against the table rows it folds, a table row worth 2.3 edge rows (measured, 39.4 bytes against 16.5): on six hours of the questions bench it cut hours into minutes, whose sorted columns took 16 % more bytes, and questions read from rows took up to three times the requests and 3.8 times the bytes (measured) |
| 4 | the depth of the synopsis part | every synopsis column. A reader takes the regions it needs, not the whole part | where the budget stops |
| 5 | tables over subsets of columns | none beyond the prefixes of the order and each column's value counts | admitted by their benefit per byte, as every other table |
| 6 | edges | exact by default, their rows stated by the plan. `ScanOptions.AlignWindow` moves each end to the nearest bound the tables answer at, a slice's, an hour's or the object's, no further than its rows, and the metrics return the window applied, the same whether states or rows answer: a window that cuts its first and last hours, 8 requests in 6 round trips warm, aligned none (measured) | moved to slice boundaries by default, which changes what a question means without its asking |
| 7 | the synopsis part's table of contents | in the object's leaf entry, about 1.1 KB for an hour, so one round trip per object for whole periods and two for a shorter window | read from the object's index directory, one round trip more |
| 8 | deletes | tables corrected by the marked rows' contributions, computed at read | deltas written beside the vector, or the runs of every marked bucket |
| 9 | the lateness horizon | declared by the application, checked by the dataset's counters, on the data's time partition by partition: a partition that receives nothing more keeps its last period unsealed, exact at a few requests more | chosen by the dataset from the lateness it measures, or a clock, which would seal a lagging partition too early |
| 10 | rows later than the horizon | the late level beside their sealed period, sealed again when the late level is crowded, counted, or refused on request. On periods of an hour with a tail an hour late, every period is sealed about three times (measured) | a partition of their own, which loses their retention, or a rewrite of their period at once |
| 11 | retention per row | partition columns in one dataset | one dataset per retention, and a scan over several datasets |
| 12 | the budget | the indexes' share of the data's bytes, 10 % by default, with no floor | a number of bytes per object |
| 13 | the weight of each question in the benefit of a table | all alike, since none is known | weighed by the questions the dataset's counters have seen |
| 14 | format | commit format 4 for the style, partition columns, the table of contents and the footer's length, refused by older libraries | fields older libraries skip |
| 15 | the life of a row | a compaction style by periods: 4.7 rewrites per row on periods of a day with fan-out 30, 20 objects for the last hour (measured) | concatenations of time-adjacent objects, or the leveled style, which rewrites a row 2.2 times with keys in order but 10.2 times under late rows and four writers, and keeps no slices to answer from (measured) |
| 16 | writing large objects | a bound on a put's length and the spool, the store choosing by its profile and the object's size. Every rewrite announces what it reads times 1.03 (measured at worst 1.024), and starts again under twice the bound when its content runs past it | the spool alone, or the bound alone |
| 17 | the size of a chunk in a period | from the footer's cap. Chunks do not end at slices | 16 MB of the widest column, or chunks ended around each slice, which took five times the requests of a whole read and 2.4 times the bytes of a cut hour's rows (measured) |
| 18 | reading part of a flat segment | no, while chunks stay at a megabyte of values: what it saves of one segment, at most 10 ms, never pays the round trip it adds, 30 to 50 ms | now, for every column |
| 19 | needles | identifiers declared by the application, with hashed postings from value to block outside the index budget (0.14 to 0.40 times the bytes of a sorted run, a probe reading 44 KB instead of up to 1.2 MB) | Bloom filter trees, a sorted run inside or outside the budget |
| 20 | physical erasure | a delay the application declares, one purge per object and delay | a purge triggered by the vector's bytes |
| 21 | the float sum's state | 32 bytes where the source proves its rows within 2^36, and a wide state of 40 bytes with its high parts in a fifth word elsewhere and for persisted states | 40 bytes for every sum, which every group by of floats would pay |
| 22 | the size of objects for every dataset | 64 MiB at the deep levels | 4 MiB, or 256 MiB once the churn bench says so |
| 23 | the grid of distributions | log-linear, read off the double's bits, at 1 % | a logarithm per value, or a decimal grid exact at every threshold of three significant digits, which needed eight times the buckets |
| 24 | the order of the fresh levels' rows | sorted slices, as in periods: they already serve the newest rows first through their last slices | time order with a synopsis beside them, which would add a write and save the page no request |
| 25 | the placement of a period's chunks | interleaved by chunk | a range of part numbers per column while streaming, which encryption's nonces, the memory of a part per column in flight and R2's parts of one size forbid |
| 26 | a page's questions | answered one by one: a shared walk saves at most 0.7 ms of a warm page of 2.3 ms, and the reads are already shared by the cache (measured) | answered together, walking each object's tables once |

## 8. Alternatives weighed

| alternative | what it offers | why it was not chosen |
|---|---|---|
| a synopsis table of every combination beside rows kept in time order | rows keep their time order, so edges read only the rows of the edge's own seconds, and the writer never sorts | the finest counts are stored twice, and kept exact under deletes only by deltas. Sorting the slice stores them once, as the order, and makes the data smaller |
| the instant first and the synopsis columns after it across a whole period, sorted by columns first and time last | runs across the whole period, so coarse counts come free too | a window of a few minutes of that period would touch every run of it. Slices first keep time local, and coarse tables cost a small fraction of the rows |
| re-sorting old periods into coarser slices | fewer runs for long windows | a question about a few minutes of an old period would read a whole coarse slice. Slices keep their grain for life, and coarse tables serve long windows |
| coarse tables across objects, in trees of their own, in the same commits | a combination counted once over any window, whatever the number of objects | a second lifecycle beside the objects. Everything an object knows stays in it, and longer periods bring the same gain inside each object |
| count tables per column and per pair of columns | small, and every single column served | a question over three columns is served by none, and the pairs to keep depend on questions nobody knows |
| aggregates declared for given questions: a filtered count, a chosen set of columns | the cheapest answer to those questions | the questions are not known in advance, and each new one would need a declaration |
| a star-tree per object, as Apache Pinot builds one per segment (source) | any combination of filters over its dimensions, with a bounded leaf | its star nodes multiply its size. Sorted slices are its spine on an order the data chooses, stored as the rows themselves, and value counts stand in for its star nodes on single columns |
| granules of a fixed number of rows, with a sparse index and skipping indexes per granule | pruning for selective filters | a filter that keeps most rows still reads every row |
| fragments with a result cache per fragment, the practice one large log engine reports, about 80 % of fragment queries answered from it (source) | repeated questions without reading | a new question, or a cold one, reads its rows |
| bitmaps of rows per value | any boolean filter by bit operations, a count by population count | a frequent value costs a bit per row, and a group by still reads a code per row. In a sorted slice the same filter is a range of runs |
| a fixed reduction threshold and a fixed cap on distinct values | simple | the right threshold moves with the resolution, the states kept, the store and the window, and a cap of 10⁴ values would refuse `Endpoint` |
| small objects, 4 MiB | cheap rewrites | a million objects per 30 days, a million probes per needle, and opens that add a third to the requests of a scan. With marks, a delete no longer needs objects small |
| concatenating time-adjacent objects into periods | slices copied, nothing re-encoded | a copy of bytes needs chunks at their final size, is impossible in an encrypted dataset, where an epoch's key derives from its object ([18-encryption.md, Frames and nonces](18-encryption.md#33-frames-and-nonces)), and OVHcloud lists no `UploadPartCopy`. The copy on the service was built and removed for files ([19-object-storage.md, Appends](19-object-storage.md#114-appends)). A seal writes the period once, in one pass |
| a partition of their own for rows later than the horizon | periods left untouched | the rows lose their retention, and the partition's small objects never grow |
| chunks of 16 MB of the widest column | a footer of a few megabytes | every selective read fetches sixteen times what it fetches today, where the footer's cap needs about three times at 20 columns |
| Bloom filters in tree pages, or a global secondary index, for needles | needles without a probe per object | a union of filters saturates past a few hundred objects, and a global index is rewritten by every compaction ([13-dataset.md](13-dataset.md#66-order-beyond-the-lookup)). With periods, a needle probes tens of objects |
| Bloom filter trees for identifiers | an index inside the budget | no upper node survives 4 × 10⁹ values in an object, so a probe reads a whole level of filters, about 5 GB a day |
| a float sum's state of 32 bytes with a wide part kept aside | the state's size unchanged | the ops that update a state receive no group number, so every slot, the merge of lanes, the core and the spill would have to pass it |
| an eviction policy taken from the literature | no bench | the comparisons disagree once sizes count. Replayed on the bench's traces, S3-FIFO served 4 to 7 points more of the bytes than SIEVE below half the regions' bytes, and Window TinyLFU fell behind past a fifth ([bench/README.md](../../bench/README.md)) |
| an order of the synopsis columns weighed by the runs questions walk | fewer table rows for the questions asked most | the rows move from one question to another: on the generator, services under a method fold 2.6 times fewer, errors by region and method 62 times more, with the same requests (measured) |

## 9. Sources

Read on 2026-10-18. Older sources are marked as such, and a vendor's account is a lead, not a proof.

- D. Lemire, O. Kaser, *Reordering columns for smaller indexes*, Information Sciences, 2011 (older).
  Sorting columns by increasing cardinality minimizes the number of runs in many cases, and is
  asymptotically optimal on uniform tables among recursive orders. Finding the best order is NP-hard,
  and the right order can halve the runs or better. <https://arxiv.org/abs/0909.1346>
- G. Cormode, M. Garofalakis, P. J. Haas, C. Jermaine, *Synopses for massive data: samples,
  histograms, wavelets, sketches*, Foundations and Trends in Databases, 2012 (older): the term, and
  the families of synopses.
- J. Gray et al., *Data cube*, ICDE 1996, and V. Harinarayan, A. Rajaraman, J. D. Ullman,
  *Implementing data cubes efficiently*, SIGMOD 1996 (older, foundational): the lattice of group bys,
  and the greedy choice of views by benefit per byte.
- Apache Pinot documentation, *Star-tree index*, release 1.2. A vendor's documentation:
  `dimensionsSplitOrder`, `maxLeafRecords` at 10 000 by default, a default tree over the dimensions of
  at most 10 000 values. <https://docs.pinot.apache.org/release-1.2.0/basics/indexing/star-tree-index>
- Datadog engineering, *Husky: query architecture*, 2025-10-01. A vendor's account: long questions cut
  into time steps, fragments answering from a result cache kept in memory and on disk (about 80 % of
  fragment queries), a cache of blob ranges (about 70 % of reads), and no pre-aggregation described.
  <https://www.datadoghq.com/blog/engineering/husky-query-architecture/>
- Apache Iceberg, *Configuration*, the table properties, read on 2026-10-18:
  `write.target-file-size-bytes` is 536870912 (512 MB) by default.
  <https://iceberg.apache.org/docs/latest/configuration/>
- Delta Lake, *Optimizations*, read on 2026-10-18: auto compaction "will use 128 MB as the target
  file size" by default. <https://docs.delta.io/latest/optimizations-oss.html>
- J. Yang, Y. Zhang, Z. Qiu, Y. Yue, R. Vinayak, *FIFO queues are all you need for cache eviction*
  (S3-FIFO), SOSP 2023. <https://junchengyang.com/publication/sosp23-s3fifo.pdf>
- Y. Zhang, J. Yang, Y. Yue, Y. Vigfusson, K. V. Rashmi, *SIEVE is simpler than LRU*, NSDI 2024.
  <https://junchengyang.com/publication/nsdi24-SIEVE.pdf>
- G. Einziger, R. Friedman, B. Manes, *TinyLFU: a highly efficient cache admission policy*, ACM
  Transactions on Storage, 2017 (older). <https://arxiv.org/abs/1512.00727v2>
- libCacheSim, discussion 20: Window TinyLFU beats S3-FIFO when object sizes are counted, and loses
  when they are not. <https://github.com/1a1a11a/libCacheSim/discussions/20>
- C. Masson, J. E. Rim, H. K. Lee, *DDSketch: a fast and fully-mergeable quantile sketch with
  relative-error guarantees*, VLDB 2019 (older): the grid and its exact merge.
- O. Ertl, *UltraLogLog: a practical and more space-efficient alternative to HyperLogLog for
  approximate distinct counting*, VLDB 2024.
