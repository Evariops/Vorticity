# Ad hoc aggregates over long time windows

An application that appends events to a dataset (requests, log lines, metric points) at tens of
thousands of rows a second asks them questions nobody wrote in advance: how many rows, under any
filter on any columns, grouped by any of them and by slices of time, and which values come first.
This document proposes the primitives that let a dataset answer such questions over any window, from
a minute to several years, in well under a second on an object store, without reading the rows, and
with the answer a scan of the rows would give, to the bit.

It is a proposal written on 2026-10-18 against `main` at `485e796b`, and nothing in it is built. Its
idea fits in one paragraph. A data object covers a period of time whose length follows from the
volume. Inside it, rows fall into time slices, also sized by the volume, and inside each slice they
are sorted by the columns whose values repeat, from the column with the fewest distinct values to the
one with the most, then by their instant. Each of those columns then becomes a short list of runs, and
the length of a run is the number of rows that hold that combination of values: the rows count
themselves, and nothing is stored twice to count them. Next to them, in the same object and invisible
to every other reader, a synopsis keeps what the runs cannot give cheaply: where each slice lies, the
states of the numeric columns per run, counts per coarser span of time, and counts per value. A
question reads the runs and the synopses of the objects in its window, a small part of each, and
reads rows only where neither can answer. The local disk keeps that part, never the rest. Rows reach
their period through a short life the dataset bounds: fresh objects that merge as they age, a level
for late rows, and one seal per period, which writes the period in one pass. Each row is rewritten
three or four times on its way, the price of the order.

[The worst cases](#14-the-worst-cases) lists every case where a question still costs more than its
answer, with its cost on the example and what remains of it once the design has done what it can.

The design leans on changes that serve every dataset, not only this one: exact float sums past 69
billion values, mark limits and object sizes made for objects of gigabytes, a bound on a put's
length, and an index for identifiers. They come first in [the order of work](#15-order-of-work).

Each claim says where it comes from:

| tag | meaning |
|---|---|
| (code) | read in the code at `485e796b`, linked to its lines |
| (measured) | measured earlier and recorded in the design document the link names |
| (estimate) | arithmetic from the volumes of [the need](#1-the-need), to be replaced by a measure |
| (hypothesis) | a design bet, with what would refute it |
| (source) | an external source, listed with its date in [sources](#20-sources) |

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
| period | the range of time a sealed data object covers, whose length follows from the volume | the retention window of a dataset |
| fresh level | a level of objects younger than their period's seal, key-disjoint, which grow by merging adjacent objects | level 0, whose objects overlap |
| late level | the level of rows older than the fresh object they would overlap, or than their period's seal, whose objects overlap under a ceiling | a partition |
| seal | the job that writes a period's object in one pass, once the period's end and the lateness horizon have passed | sealing a file, which checks its order |
| slice directory | per column, where its chunks lie in the object and the rows each holds, so that the chunks covering a slice are found by its rows | the slice table, which gives a slice's rows |
| identifier | a column the application declares as one whose values questions look up, which gets a sorted run outside the index budget | a synopsis column |
| erasure delay | how long a deleted row may stay in the bytes of the store, declared by the application | the retention window |
| time slice | the rows of an object whose instant falls in `[t, t + w)`, `w` a calendar unit in UTC: a contiguous range of rows | the slices of a sorted run, in key cursors |
| sorted slice | a time slice whose rows are sorted by its synopsis columns, then by their instant | a sorted object, in key order |
| synopsis column | a column, or a shredded path of a variant column, whose values repeat, by which slices are sorted | a measure column |
| measure column | a numeric column, or a numeric path of a variant column, whose values rarely repeat, whose states the synopsis keeps | a dimension, in other tools |
| run | a range of consecutive rows of a sorted slice holding the same value of a synopsis column, and so the same values of every synopsis column before it | a sorted run, the index kind |
| depth of a question | the rank, in a slice's order, of the deepest synopsis column the question touches | |
| synopsis part | the synopsis columns down to a depth the budget sets, and the synopsis tables, written together near the end of the object | the data's footer |
| run statistics | per run, the states of the measure columns | zone maps, which are per block of 8 192 rows |
| coarse table | counts and states per combination of synopsis columns, per bucket coarser than the slice (an hour, the period) | a rollup kept outside the object |
| value counts | for one column and one bucket, how many rows hold each of its values, with states | a dictionary, which lists values without counts |
| state | the mergeable form of an aggregate: a count, an exact sum, an extreme and its presence, a sketch | the answer delivered, an average or a quantile |
| edge | the part of a question's window that cuts a time slice | |
| needle | a question about one rare value, such as every row of one `RequestId` | |
| lateness horizon | how late a row may arrive after its instant, declared by the application | the retention window |
| partition column | a leading clustering-key column of few values, declared so that no compaction job spans two of its values | the partitions of a group by |
| synopsis cache | the synopsis parts of objects, and what leads to them, kept on the machine's disk beyond the life of a process | the session's `SegmentCache` and a handle's page cache, both in memory |
| round trip | a request whose answer the next request waits for, 10 to 100 ms on an object store | a network round trip inside a region |

## 3. What a dataset does today, and where it stops

| today | where it stops |
|---|---|
| A clustering key that leads with time keeps each level in time order, and the summaries in the tree skip every object outside a window without opening it ([13-dataset.md](13-dataset.md#42-what-a-node-carries)) | finding a window's objects is solved. Answering without their rows is the problem |
| An object written into a level is capped at 4 MiB by default (code: [CompactionPlan.cs:85](../../src/Vorticity.Dataset/CompactionPlan.cs#L85)), and the dataset holds a whole object in memory, up to 1 GiB, before its put (code: [ObjectSegmentSink.cs:29](../../src/Vorticity.Dataset/ObjectSegmentSink.cs#L29)) | at 4 MiB, 30 days are 1.24 × 10⁶ objects of two seconds each (estimate): a million objects to visit per question, and a million filters to probe per needle |
| Keys that arrive in order move up the levels without being rewritten (measured, [15-compaction.md](15-compaction.md#1-what-compaction-buys-and-what-it-costs)) | a move takes an object of any size, and none of the four triggers merges small neighbours (code: [CompactionPolicy.cs:313-316](../../src/Vorticity.Dataset/CompactionPolicy.cs#L313-L316), [CompactionPlan.cs:53-72](../../src/Vorticity.Dataset/CompactionPlan.cs#L53-L72)), so objects never grow past the size they left level 0 with. At a thousand rows a second, that is objects of a megabyte, 1.3 million a year (estimate) |
| A delete marks rows rather than rewrite an object, up to limits ([13-dataset.md](13-dataset.md#12-deleting-and-updating-rows)) | the limits were sized for objects of 4 MiB. A vector past 1 KiB makes a delete rewrite the object, and half that makes a purge rewrite it alone (code: [DatasetOptions.cs:155](../../src/Vorticity.Dataset/DatasetOptions.cs#L155)). A hundred scattered rows would rewrite an object of a day whole (estimate) |
| A put of unknown length grows its parts as it goes ([19-object-storage.md](19-object-storage.md#111-a-file-written-to-a-location)) | R2 wants parts of one size and refuses such a put past about 156 GiB, and Scaleway's growing parts stop near 100 GiB |
| A flat segment is read whole (code: [FlatLayoutReader.cs:48](../../src/Vorticity/Vortex/Layouts/FlatLayoutReader.cs#L48)), and a chunk targets a megabyte of its widest column ([blocks-and-chunks.md](../guide/blocks-and-chunks.md)) | the chunk is both the unit the footer counts and the least a selective read fetches. An open reads a fixed 64 KiB tail (code: [ObjectStoreSource.cs:33](../../src/Vorticity/IO/ObjectStoreSource.cs#L33)), and a footer past it costs a second read |
| A float sum is exact and the same bits whatever the cut ([16-queries.md](16-queries.md#51-sums-exact-and-the-same-bits-whatever-the-cut)) | its 32-byte state takes 2^36 values, about 69 billion, and throws past them, when lanes merge too. A sum of weights over 16 days of the example threw, until the wide state of [the states](#75-states) |
| Bloom filter trees and sorted runs find values ([10-indexes.md](10-indexes.md#3-the-directory-and-the-runs)) | over 4 × 10⁹ values in an object, a tree keeps no upper node, `Auto` gives it up, and `Auto` never builds a sorted run ([10-indexes.md, The policy](10-indexes.md#6-the-policy)) |
| "Level 0 is not sorted, and nothing ever sorts it", and the writer never has to learn to sort ([13-dataset.md](13-dataset.md#53-what-a-compaction-does)) | rows lie in arrival or time order, so a column whose values repeat is stored once per row, and counting it reads every row |
| Zone maps hold a minimum, a maximum and a null count per block, and counts, extremes and some group bys are answered from them ([12-index-reads.md](12-index-reads.md#4-answers-without-rows), [16-queries.md](16-queries.md#93-statistics-and-zone-maps)) | nothing counts values or combinations, so a count under a filter that keeps most rows reads them all |
| An open reads the tail, then every footer segment that lies before it (code: [VortexFile.cs:413](../../src/Vorticity/Vortex/File/VortexFile.cs#L413)) | nothing an object holds can be read without the data's footer, which grows with the object |
| A delete removes an object whose live rows all match without reading its data ([13-dataset.md](13-dataset.md#12-deleting-and-updating-rows)) | it still opens and counts every object its summaries do not refute (code: [DatasetRowChanges.cs:128-133](../../src/Vorticity.Dataset/DatasetRowChanges.cs#L128-L133), [251-256](../../src/Vorticity.Dataset/DatasetRowChanges.cs#L251-L256)) |
| A composite clustering key merges on its tuple | a job's footprint is the hull of its sources' bounds on the leading key column alone, "the holes between them included" (code: [LevelRange.cs:49-75](../../src/Vorticity.Dataset/LevelRange.cs#L49-L75)). Under `(Retention, At)` a job takes every object of its destination level that shares a retention value, and under `(At)` a row an hour late makes a job rewrite the last hour of the level |
| Pages, open objects, index regions and segments are kept in memory (code: [DatasetOptions.cs:65-79](../../src/Vorticity.Dataset/DatasetOptions.cs#L65-L79), [VortexReadOptions.cs:21](../../src/Vorticity/Scan/VortexReadOptions.cs#L21), [SegmentCache.cs:18](../../src/Vorticity/Session/SegmentCache.cs#L18)) | a restart, or another process, reads everything from the store again |
| Approximate distinct counts, quantiles and aggregates on the tool path are left for later ([16-queries.md](16-queries.md#12-not-in-it-on-purpose)) | no quantile, and no group by over a column known only at run time |

## 4. The approach at a glance

Seven rules carry the design.

1. Time is the only axis between slices. An object covers a period, its slices follow each other in
   time, and how long each one is follows from the volume.
2. Inside a slice, the rows count themselves. They are sorted by the columns whose values repeat, from
   the fewest values to the most, so that each of those columns becomes runs whose lengths are counts.
   What the runs cannot give cheaply is kept beside them, within a budget.
3. Everything an object knows is in the object. Its rows, its indexes and its synopsis are written,
   compacted, expired and versioned together.
4. Nothing knows the questions. The order, the runs and the synopsis are decided by the data alone,
   and serve any predicate and any grouping over the columns they hold, whatever their number.
5. No size or threshold is guessed. Periods, slices, the synopsis's depth, tables and resolutions come
   from cost models whose constants a targeted bench measures, and from a byte budget the user sets.
6. A question reads runs and synopses first and rows last, and its answer is the same bits whichever
   it reads.
7. The local disk keeps synopsis parts, never the rest of an object.
8. Rows reach their period through a life the dataset bounds: fresh objects that merge as they age, a
   level for late rows, and a seal per period. Each row is rewritten three or four times, and every
   level keeps the read bounds a dataset has today.

| primitive | what it gives | what it touches | what it writes to the store |
|---|---|---|---|
| [objects that cover periods sized by volume](#5-objects-that-cover-periods-sized-by-volume) | a window of 30 days holds tens of objects, not a million, each row rewritten three or four times | the dataset's sink and puts, a compaction style by periods, mark limits | larger objects whose chunks end at slices, the style, the lateness horizon and the erasure delay in the header, the footer's length in the leaf entry |
| [what every dataset gains](#55-what-every-dataset-gains) | objects that grow with keys in order too, and a cap of 64 MiB, then 256 MiB | compaction | fewer, larger objects |
| [sorted time slices](#6-sorted-time-slices) | counts of every combination of the repeating columns, per slice, stored as the rows' own order | the writer, compaction, the dataset's order | rows in a new order, the same Vortex files |
| [the synopsis](#7-the-synopsis) | what runs cannot give cheaply: measures per run, coarser spans of time, single columns | the writer, compaction | index regions that a Rust reader never sees |
| [states](#75-states) | exact sums over any window, distributions, distinct sketches, the same bits by every path | the engine | states in index regions, each a versioned kind |
| [reading the synopsis part without the data](#8-reading-the-synopsis-part-without-the-data) | one round trip per object cold for whole periods, two for a shorter window, no footer, no rows | the open, the dataset | the synopsis part's table of contents in the leaf entry, a slice directory |
| [answering a question](#9-answering-a-question) | runs and synopses first, rows last, exact edges, exclusions, newest first | the planner, the engine | nothing |
| [needles](#95-needles) | an identifier found in three dependent reads per object cold, two warm | the index policy | a sorted run per declared identifier, outside the index budget |
| [deletes](#10-deletes) | counts that stay exact under deletion vectors, and tables corrected by the marked rows, without any delta | the planner | nothing |
| [the synopsis cache](#11-the-synopsis-cache) | warm questions make no request to the store | the session, object storage | nothing |
| [retention and late rows](#12-retention-and-late-rows) | expiry reads nothing, late rows rewrite nothing large | the dataset, compaction | partition columns |
| [columns known only at run time](#13-columns-known-only-at-run-time) | the same answers for columns added later | the tool path, policies | nothing |

## 5. Objects that cover periods sized by volume

### The problem today

An object written into a level is capped at 4 MiB by default (code:
[CompactionPlan.cs:85](../../src/Vorticity.Dataset/CompactionPlan.cs#L85)). The design gives the cap
one reason, that it is what a delete rewrites ([13-dataset.md, The invariant](13-dataset.md#52-the-invariant)),
and deletes have marked rows rather than rewritten objects since then. With keys that arrive in order
the cap is not even reached. A move takes an object up whatever its size (code:
[CompactionPolicy.cs:313-316](../../src/Vorticity.Dataset/CompactionPolicy.cs#L313-L316)), and none of
the four triggers (level 0's ceiling, a level's size, fragments, marks) merges small neighbours (code:
[CompactionPlan.cs:53-72](../../src/Vorticity.Dataset/CompactionPlan.cs#L53-L72)), so objects keep the
size they left level 0 with. When the move was built, requiring the destination's target size was
tried and moved nothing below level 3, which is why the condition became "no larger than the target".
At 4 MiB, 30 days of the workload are 1.24 × 10⁶ objects of two seconds each (estimate): a question
would visit a million objects, and each would summarize two seconds, where the same combinations
repeat all day.

Larger objects meet mechanisms sized for 4 MiB:

- The dataset's sink holds a whole object in memory before its put, up to 1 GiB (code:
  [ObjectSegmentSink.cs:29](../../src/Vorticity.Dataset/ObjectSegmentSink.cs#L29)).
- A delete rewrites an object rather than mark it once its vector would pass 1 KiB, and a purge
  rewrites it alone at half that (code: [DatasetOptions.cs:155](../../src/Vorticity.Dataset/DatasetOptions.cs#L155)).
  A row deleted alone is a run of 4 to 9 bytes, so a hundred scattered rows would rewrite an object of
  a day whole, and a request to erase one person's rows, held by every period, would rewrite the whole
  retention (estimate).
- A put of unknown length grows its parts, but R2 wants parts of one size and refuses one past about
  156 GiB, and Scaleway's growing parts stop near 100 GiB ([19-object-storage.md, A file written to a
  location](19-object-storage.md#111-a-file-written-to-a-location)). A period of a day, 161 GiB, would
  not stream there.
- A flat segment is read whole (code: [FlatLayoutReader.cs:48](../../src/Vorticity/Vortex/Layouts/FlatLayoutReader.cs#L48)),
  so the chunk is both the unit the footer counts and the least a selective read fetches. An open
  reads a fixed tail of 64 KiB (code: [ObjectStoreSource.cs:33](../../src/Vorticity/IO/ObjectStoreSource.cs#L33)),
  which a larger footer passes, at the cost of a second dependent read.

### 5.1 A compaction style by periods

A dataset that declares its instant and its lateness horizon is compacted by periods rather than by
the leveled style. The instant is the first column of a clustering key that leads with a timestamp,
stored in UTC. The horizon, how late rows may come, is the application's to declare, since it is a
property of its sources, and the dataset reports the lateness it actually sees. The style, the
horizon and the erasure delay are set at creation and kept in the header, under commit format 4:

```csharp
await using VortexDataset requests = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
{
    ClusteringKey = [Request.ColumnNames.At],
    Periods = new PeriodOptions                      // new
    {
        Lateness = TimeSpan.FromHours(1),            // how late rows may come, a property of the sources
    },
    ErasureDelay = TimeSpan.FromDays(30),            // how long a deleted row may stay in the store, for every dataset
    Identifiers = [Request.ColumnNames.RequestId],   // new: columns whose values questions look up
});
```

The style keeps four tiers:

| tier | what it holds | its objects | the order of its rows | written by | invariant |
|---|---|---|---|---|---|
| level 0 | the appends | as appended | arrival | the append | they overlap, at most 8 |
| fresh levels | the open period, and the closed ones until their seal | at the first level, a level 0 exit (24 s and 48 MB on the example), then f times more per level | sorted slices, partial at an object's two ends | level 0's exit, then the merge of f adjacent objects | key-disjoint within each level |
| late level | rows older than the fresh object they would overlap, and rows of sealed periods | small, merged among themselves past a ceiling | sorted slices | level 0's exit | they overlap, under a ceiling like level 0's |
| periods | one object per partition and period | the size the period's length gives | whole slices, and the synopsis part | the seal | key-disjoint |

1. Level 0's exit routes each row. A row past the last fresh object of its partition goes to the first
   fresh level, any other to the late level. A late row therefore rewrites neither a fresh object nor
   a period, and a job's footprint is no longer the hull of its sources' bounds on the leading key
   column (code: [LevelRange.cs:49-75](../../src/Vorticity.Dataset/LevelRange.cs#L49-L75)).
2. When a fresh level holds f adjacent objects of one partition and one period, they merge into the
   next level, under the order fixed per object ([the order inside a slice](#62-the-order-inside-a-slice)).
   No fresh object spans two periods. f comes from the cost model: the objects a question over the
   recent hours reads, against the rewrites per row.
3. Once a period's end and the lateness horizon have passed, a job seals it. It reads the period's
   fresh objects and late rows slice by slice, writes the period's object in one pass ([writing the
   object of a period](#52-writing-the-object-of-a-period)), and commits one `ReplaceObjects`.
   Meanwhile level 0's exits route that period's rows to the late level, so no late row abandons a
   seal.
4. Late rows of a sealed period stay in the late level, read beside it, while their read cost stays
   below a seal's. Past that, the period is sealed again. Its rows keep their partition, and so their
   retention ([late rows](#123-late-rows)).
5. Level 0 and the late level overlap under their ceilings, and every other level is key-disjoint. A
   lookup by key, a rank or a delete opens at most level 0, the late level and one object per
   key-disjoint level, the bound a dataset has today ([13-dataset.md, Order beyond the
   lookup](13-dataset.md#66-order-beyond-the-lookup)). A merge takes adjacent objects of one level and
   writes their union, which keeps the level key-disjoint, where a tiered compaction under a clustering
   key would break it.

What it costs, with f = 30 on the example (estimate):

- A row is rewritten three or four times: at level 0's exit, at one or two fresh merges (from 24
  seconds to 12 minutes, then to 6 hours), and at the seal. With keys in order, a row is rewritten once
  today ([15-compaction.md, What compaction buys](15-compaction.md#1-what-compaction-buys-and-what-it-costs)).
  Compaction writes 0.5 to 0.7 TB a day for 173 GB appended. A late row is rewritten twice.
- A question over the last 15 minutes reads at most thirty objects of the first fresh level and one
  or two of the second. Over the last hour, about thirty and four or five. Over 30 days, 29 periods
  and a few dozen fresh objects.

### 5.2 Writing the object of a period

1. The dataset's sink streams the object as it is written. Past the part threshold it starts a
   multipart upload and sends each part as it fills, its parts in flight reserved through the memory
   governor, as a file written to a location already does
   ([19-object-storage.md](19-object-storage.md#111-a-file-written-to-a-location)). The 1 GiB cap goes.
2. A put takes a bound on its length, beside the exact length or nothing it takes today (code:
   [ObjectStore.cs:78-89](../../src/Vorticity/IO/ObjectStore.cs#L78-L89)), and the store chooses its
   path by its profile and the object's size. A compaction and a seal always know a bound, the sum of
   their inputs plus the synopsis budget, and take the stream. The bound changes the interface every
   store implements.

   | what the writer knows of the length | the path |
   |---|---|
   | the exact length | as today: one put under the part threshold of 32 MiB, parts sized by the length past it |
   | a bound | streamed, the parts sized by the bound as the profile allows: of free sizes on S3, OVHcloud, Cloud Storage and Azure, of one size with a margin on R2. Content past what those parts reach goes again through the spool |
   | nothing | streamed with parts that double, as today, up to what the profile reaches that way: about 156 GiB on R2, 100 GiB on Scaleway, 1 TiB on S3. Past it, the spool when one is configured from the start, and a refusal otherwise |

   The spool (`StoreTransportOptions.SpoolDirectory`) costs local disk the size of the object, and an
   upload that waits for the end of the write.
3. Chunks are sized from the footer's cap rather than a fixed size. The writer writes one flat layout
   per chunk and per column, under a chunked and a zoned layout (code:
   [VortexFileWriter.Append.cs:863-894](../../src/Vorticity/Vortex/Writing/VortexFileWriter.Append.cs#L863-L894)),
   so a footer holds about chunks × columns nodes. Keeping it at half the 10⁶ tables a traversal may
   visit ([limits.md](../guide/limits.md#the-fixed-caps)) gives `rows per chunk ≥ 2 × rows × columns / 10⁶`.
   For a period of a day with 20 columns that is 1.7 × 10⁵ rows, about 2.8 MB of the widest column,
   where the default is a megabyte: a selective read fetches three times what it fetches in an object
   of 4 MiB (estimate). With 50 columns, about 7 MB. A variant column is not sized with the others: its
   chunks are long and its own, about 10⁶ rows, so that the encoding trees of its shredded paths stay
   few ([21-variant-columns.md](21-variant-columns.md#53-what-an-object-shreds)).
4. Chunks stay whole blocks of 8 192 rows, as a file's zone maps and every one of its indexes count
   them (code: the writer's block arithmetic in `VortexFileWriter.cs`). A chunk that ended inside a
   block would split that block's statistics and index entries across two chunks. The slice table
   records each slice's first row and row count instead, so a slice's rows are a range, and the slice
   directory names the chunks that overlap it: a read of one slice reads at most one chunk partly
   outside it at each end. A run that crosses a slice boundary is cut at the slice's first row ([what
   a slice is](#61-what-a-slice-is)).
5. The synopsis part carries a slice directory: a region per column listing its chunks, with their
   offsets, lengths and rows, about 250 KB a column for a day, and the encodings their trees name. The
   chunks covering a slice are found by its rows, and a read of the instant alone fetches the
   instant's region alone. Rows read in a period's object, for an edge, a needle or rows to show, are
   found through it, never through the footer ([index reads](12-index-reads.md#51-the-rows-of-a-time-slice-without-the-footer)). The footer, 20 to 40 MB for a day at
   that chunk size (estimate), is read only by a whole open: compaction, and Rust readers.
6. The leaf entry records the footer's length, so that a whole open reads the tail and the footer in
   one request at any size, under commit format 4.
7. The synopsis part is written last, before the footer, so the writer holds it until the end of the
   object: 0.7 to 0.85 GB a day without distributions, several gigabytes with them ([sizes, on the
   example](#76-sizes-on-the-example)). It spools to the local scratch, through the governor.

Two later steps would take the remaining costs away, each a hypothesis to measure before it is taken
up:

- Reading part of a flat segment, for every column. Given its encoding tree, a take fetches only what
  its rows need: the FastLanes blocks of 1 024 values they lie in, the codes of a dictionary then its
  values, the ends of a run-end array then its values, the offsets of a text then its bytes. The
  registry already counts those takes as selective ([90-registry.md](90-registry.md)), and
  [21-variant-columns.md](21-variant-columns.md#59-reading-one-path-without-the-others) builds the read
  for one path of a variant column. It costs a dependent read when the tree is not known beforehand,
  and a sealed object's frames of 64 KiB bound its grain ([18-encryption.md, Frames and
  nonces](18-encryption.md#33-frames-and-nonces)). It would leave the chunk's size to the footer alone.
- Laying each column's chunks together while streaming. S3, Azure's block lists and Cloud Storage's
  XML uploads assemble an object in the order of its part numbers, whatever the order the parts were
  sent in, and the numbers need not follow each other. A range of part numbers per column, or per
  group of narrow columns, lays an object out column by column, so a scan of one column over a period
  reads a few large ranges rather than one per chunk: for a column of a byte a row over 30 days, about
  2 000 reads of 64 MB instead of 3 × 10⁵ (estimate). R2's parts of one size, parts of at least 5 MiB,
  the 10 000 parts, and sealed frames whose nonce is their plaintext offset bound it.

### 5.3 Marks at the scale of a period

1. The cap on a vector comes from its cost, not from a fixed kilobyte. Out of line, a vector is read
   once per handle beside the object's open, where a rewrite costs the object's bytes: for an object of
   173 GB, a vector of several megabytes stays far cheaper than a rewrite. The cap becomes a function
   of the object's size, whose constant a bench measures. `MarkedShare`, a share of the rows, holds at
   any scale.
2. The physical erasure of deleted rows has its own delay, which the application declares, since it is
   a property of its obligations. The planner purges an object whose marks are older than that delay,
   once per delay, with every deletion of the window. The vector alone no longer triggers a purge.
3. The cost stays, and is stated. When every window of erasure touches every period, every window
   rewrites the whole retention. An application that keeps a token in its rows, and the mapping from
   token to person outside the dataset, makes physical erasure rare.
4. The synopsis stays usable under marks: a question corrects counts and sums by the marked rows' own
   contributions ([deletes](#10-deletes)).

### 5.4 The length of a period

A period's length is the longest calendar unit that holds four constraints:

- The store must take the object on its streamed path ([writing the object of a
  period](#52-writing-the-object-of-a-period)).
- The footer must stay under its cap for the chunk size the same section's rule gives.
- A purge rewrites the period, so its cost grows with the length.
- Expiry is aligned down to a period's boundary, so a row may outlive its retention by up to a period,
  and a period stays below a share of its partition's retention ([expiry without opening
  objects](#122-expiry-without-opening-objects)).

Among the units that hold them, the cost model takes the one that minimizes what a window costs. Each
object of a window costs a question a fixed price: the round trip of its synopsis part, its open when
rows are read, a probe per needle. Each rewrite costs the object's size: a seal again for late rows, a
purge. The length minimizes the dataset's measured rate of questions times that fixed price times the
objects per window, plus its measured rate of rewrites times the size. A longer period also merges
more slices into its coarse tables, which [the worst cases](#14-the-worst-cases) weigh.

### 5.5 What every dataset gains

Two changes serve every dataset, sorted by slices or not.

1. A trigger merges adjacent objects of a level while they lie under the level's target, so that
   objects grow with keys in order as they do with keys at random. Without it, raising the cap changes
   nothing for a dataset sorted by time.
2. The cap rises in two steps. At 64 MiB for the deep levels, the footer of 20 columns still fits the
   tail of 64 KiB, so an open stays one request (estimate, to check), and objects are sixteen times
   fewer. Its prerequisites are the marks' limits as shares ([marks at the scale of a
   period](#53-marks-at-the-scale-of-a-period)) and the sink's buffer reserved through the governor. At
   256 MiB and beyond, once the leaf entry records the footer's length and the sink streams, objects
   are sixty-four times fewer than today. The growth by ten from one level to the next stays: the upper
   levels, rewritten often, keep small objects, and the deep ones, which hold nearly every byte, take
   the large ones.

What a size changes on the example, a table of 40 bytes a row and 20 columns at 5 × 10⁴ rows a second
(estimates):

| | 4 MiB | 64 MiB | 256 MiB | 1 GiB | a period of a day |
|---|---|---|---|---|---|
| objects per TiB | 262 144 | 16 384 | 4 096 | 1 024 | 6 |
| objects over 30 days | 1.24 × 10⁶ | 7.8 × 10⁴ | 1.9 × 10⁴ | 4.8 × 10³ | 30 |
| requests of a pruned question over 30 days, two per object | 2.5 × 10⁶ | 1.6 × 10⁵ | 3.9 × 10⁴ | 9.7 × 10³ | 60 |
| its time at 5 500 reads a second on one prefix | 7.5 min | 28 s | 7 s | 1.8 s | one wave |
| opens added to a scan of 2 columns | +30 % | +2 % | +0.5 % | under 0.2 % | none |
| requests of an open | 1 | 1 | 2, unless the leaf entry gives the footer's length | 2, likewise | none, through the table of contents |
| leaf entries for 68 TB | 1.6 × 10⁷ | 1.0 × 10⁶ | 2.5 × 10⁵ | 6.3 × 10⁴ | 395 |
| a leveled job at random keys, one object and the ten below | 44 MiB | 0.7 GiB | 2.8 GiB | 11 GiB | none |

The bytes a scan reads do not depend on the size: a read takes chunks, and a chunk keeps its default
size while the footer stays under its cap, up to about 65 GB of object with 20 columns. What the size
moves is the price of each object a question touches.

The target should be a cost model's output rather than a number. Per byte stored and per unit of
time, an object of size `S` costs `a·o / S + κ·S`: `o` is the fixed cost of an object touched (its
requests, its open, its entry), `a` how often questions touch one, and `κ·S` the work lost per byte
that grows with the size, mostly the compaction jobs a concurrent delete abandons. The minimum is at
`S* = √(a·o / κ)`. For appends in time order, with late rows routed and marks capped as shares, `κ` is
nearly zero and only hard limits bound the size, which is what periods are. For random keys with
churn, `κ` is positive and the optimum finite, probably tens to a few hundred MiB while a conflict
abandons a job (estimate). It would rise if a compaction carried onto its outputs the marks a delete
made while it ran, rather than abandon itself (hypothesis).

The churn bench decides the default, at caps of 4, 16, 64 and 256 MiB: the compaction bytes per byte
appended, the abandoned jobs and their bytes, the p99 of a commit, and the requests of its reads on a
counting store. On a locked store it also counts the bytes abandoned jobs leave until the lock's end.
Iceberg targets 512 MB per data file by default, and Delta's auto compaction 128 MB (source).

### Example

At 5 × 10⁴ rows a second, if the cost model lands on a day, a period is 173 GB, 30 days are 30
objects and thirteen months 395, and each row is rewritten three or four times on its way (estimate).
A smaller dataset, at a hundredth of the volume, lands on a period of weeks or months by the same
rule, within the share of its retention.

### Done when

- An object of several hundred gigabytes is streamed with bounded memory under every store profile,
  R2 and Scaleway included, and a whole open of it is one request (ratchets on memory, requests and
  bytes).
- On a bench of appends in order with 1 % of rows an hour late, the rewrites per row and the objects a
  question reads hold the figures of [a compaction style by periods](#51-a-compaction-style-by-periods),
  and no seal is abandoned by a late row (ratchets).
- A hundred scattered rows deleted in a period's object rewrite nothing, and a purge runs once per
  erasure delay.
- A selective question reads no more bytes than over the same rows in objects of 4 MiB, on a counting
  store (a ratchet).
- A lookup by key, a rank and a delete open no more objects than the bound of the style allows.
- The churn bench's verdict on the default size is recorded in the bench's README.

## 6. Sorted time slices

### 6.1 What a slice is

A time slice is the rows of an object whose instant falls in `[t, t + w)`, where `w` is a calendar unit
in UTC: a second, a minute, an hour, a day, a month. Slices follow each other in time. A period's
object holds whole slices, and a fresh object holds whole slices except at its two ends, whose
partial slices a later merge completes. The slice table records, per slice, its bounds, its first row
and its row count, which cut every run at a slice boundary, and the slice directory says which chunks
hold each slice's rows ([writing the object of a period](#52-writing-the-object-of-a-period)). The
table is an entry of the file's index directory, `vorticity.slices.v1`, on the instant column, which
a writer that sorts its rows slice by slice declares before it completes the file.

The width follows from the volume, per object. A slice is the finest grain of time a question reads
without reading rows, and the most rows an edge of a window reads ([edges](#93-edges)). The cost model
picks the calendar unit whose slices hold about a target number of rows, the point where a larger
slice would make edges cost more than the runs it saves. At 5 × 10⁴ rows a second a minute holds
3 × 10⁶ rows, and a dataset a hundred times smaller reaches the same rows per slice with slices of an
hour or more. A burst that puts many times the target into one unit is split into finer units, so
that every slice stays near its target.

### 6.2 The order inside a slice

Inside a slice, rows are sorted by the object's synopsis columns, then by their instant. The synopsis
columns are the columns whose values repeat, ordered by their number of distinct values over the
object's rows, fewest first, and their values sort by their dictionary codes, assigned in the order
of the values. One order serves every slice of an object, which the merges of its coarse tables and of
its partial slices need, and the slice table records it: a slice whose own counts would order its
columns otherwise pays a few runs more. A column whose values do not repeat, an identifier, is never
a synopsis column, and comes after them with every other column, carried along by the order.

On the example, a slice of a minute is sorted by `Status` (5 values), `Region` (5), `Method` (8),
`Service` (600), `Version` (1 500), `Endpoint` (10⁴), `Host` (10⁴), then `At`.

The order minimizes the runs stored, which is what ordering by ascending numbers of distinct values
aims at (source). A question pays something else: the runs of its depth, and one range of rows per
distinct prefix before the column it filters. On the example, a service's rows lie in about fifty
ranges a minute, eight times fewer blocks than in time order (estimate). That locality fades with
depth. A table of spans holds a dozen facets or more, and there a column of 600 values after six
shallower ones lies in thousands of ranges a minute, while the deepest facets have runs of one or two
rows: their questions rest on value counts and coarse tables, not on runs ([depth](#144-depth)).
Whether weighing the runs that questions walk, uniformly over the subsets of columns, orders better
than the runs stored is a measure on real mixes (hypothesis).

A dataset whose rows carry attributes whose keys are data holds them in variant columns, shredded per
object ([21-variant-columns.md](21-variant-columns.md)). The shredded paths whose values repeat are
synopsis columns like the dataset's own, and so is each row's shape, the set of paths it holds, which
keeps every path's rows together ([shapes](21-variant-columns.md#55-shapes)). Such paths are sparse,
null outside the shapes that hold them, so the order is measured by the runs each column adds, which
for dense columns comes back to the ascending number of distinct values ([the order with variant
columns](21-variant-columns.md#62-the-order-inside-a-slice)).

As built, the first pass's counts choose the synopsis columns, those that repeat, and the object's
first slice orders them: greedily, each place to the column that adds the fewest runs after those
before it, on a sample of the slice. Independent columns keep the order of their numbers of distinct
values; a column nested in another comes right after it, as a route after its service, which on a
mix of a service of ten, its three routes each and a method of twenty stores 640 runs where the
order by distinct values stores 810 (`vortex-queries --micro runsorder`). The order is then the
object's for every slice, and the slice table records it.

### 6.3 What the order gives

The `k`-th synopsis column of a slice is a sequence of runs, one per distinct combination of the first
`k` columns, and a run of it is a run of every column before it. Run-end encoding (`vortex.runend`,
read and written since `core2025.05.0`, [90-registry.md](90-registry.md)) stores each run once, its
value and its end. Sorting columns by ascending numbers of distinct values is what minimizes the runs
in many cases (source).

| synopsis column | runs in a minute, on the example | rows per run |
|---|---|---|
| 1, `Status` | 5 | 6 × 10⁵ |
| 2, `Region` | about 20 | 1.5 × 10⁵ |
| 3, `Method` | about 45 | 7 × 10⁴ |
| 4, `Service` | about 3 × 10³ | 10³ |
| 5, `Version` | about 5 × 10³ | 600 |
| 6, `Endpoint` | about 4 × 10⁴ | 75 |
| 7, `Host` | about 5 × 10⁵, each endpoint served by about seventeen hosts | 6 |

All figures in this table are estimates. Five things follow.

1. The length of a run is a count. The number of rows holding a combination of the first `k` columns
   is the length of its run in column `k`, so the counts of every combination, at the slice's grain,
   are stored once, as the order of the rows, and never a second time. No run spans two slices: one
   that crosses a boundary is cut at the slice's first row.
2. A predicate on the first column is a range of rows, and a predicate on the `k`-th column is a range
   inside each run of the column before it. Rows a filter on a shallow column excludes are skipped
   whole.
3. Counts stay exact under deletes. A run's count is its length minus the marked positions inside it,
   which a binary search in the deletion vector finds ([deletes](#10-deletes)).
4. The instant ascends inside each run of the last synopsis column, so rows in time order are a merge
   of a slice's runs.
5. The data shrinks, less than first estimated. On a million requests of the example's table, drawn
   column by column independently and sorted in one slice (`vortex-queries --micro slicebytes`), the
   three shallowest columns fall from about 0.39 bytes a row each to 0.01, `Service` from 1.35 to 0.69
   (0.46 under the size-first profile) and `Version` from 1.62 to 1.29 (0.81). `Host` and `Endpoint`,
   deep in the order, keep runs of a row or two, and about their weight, 2 to 3 bytes a row each. In
   all the seven synopsis columns go from 10.1 bytes a row to 7.7 under the default profile, and from
   9.6 to 5.9 under the size-first one, not under a byte as estimated: the estimate assumed runs that
   columns drawn independently do not make, and real columns, which follow each other (an endpoint
   belongs to one service), make more of them. The instant gives some of it back: 0.75 bytes a row
   more under the default profile, whose frame of reference spans the slice rather than a few
   seconds, and 2.3 more under the size-first one, which loses pco's delta. The file weighs 40.35
   bytes a row against 42.03 in time order, 4 % less, and 36.94 against 38.36 under the size-first
   profile, 3.7 % less. An object of a dataset of levels also carries a run on its key, and against it
   the sorted object is 10 % and 5.5 % lighter, about 18 GB less a day of rows on the example. A
   ratchet holds these bytes (`SortedSliceBytesTests`).

### 6.4 How a writer sorts a slice

The writer gathers a slice's rows, sorts the slice by its keys alone, then gathers every column
through the resulting permutation, one column at a time. The keys are the synopsis columns'
dictionary codes and the instant, about 50 MB for a slice of 3 × 10⁶ rows (estimate), and a radix sort
over the codes takes one pass per column. Memory holds one slice's keys and one column at a time,
reserved through the governor, and a slice past its reservation is sorted in runs and merged, as the
engine already sorts a result that outgrows its budget ([16-queries.md](16-queries.md#the-sort-in-runs)).
The gather reads a column at random through the permutation, so the slice's whole column lies in
memory or in a mapped file of the local scratch: 0.3 to 0.9 GB for log bodies of 100 to 300 bytes,
1.8 GB for a variant column of twenty attributes decoded (estimate). A machine that ingests and
compacts holds that beside its appends. The pass that gathers the columns also builds the run
statistics and value counts ([how the synopsis is built](#74-how-the-synopsis-is-built-and-merged)).

A slice is sorted when its rows leave level 0, merged when a fresh merge completes it, and written for
the last time by the seal ([a compaction style by periods](#51-a-compaction-style-by-periods)). Parts
sorted under one order merge without a sort, and parts whose orders differ are sorted again, which the
radix makes cheap beside the gather. Appends to level 0 stay as they come, a few seconds each.

Every job that writes a slice shreds the dataset's variant columns again, decoding each value once
and writing it to its path's typed column or to the residual, and builds their path maps: level 0's
exit, each fresh merge and the seal. Appends never shred, so ingestion pays nothing for it, where
writers that shred in the append report a third more time per row ([writing, and what it
costs](21-variant-columns.md#57-writing-and-what-it-costs)).

### 6.5 What the order costs, and what changes in the dataset

Rows inside a slice are no longer in time order, and the dataset's order changes with that.

| what | today | with sorted slices |
|---|---|---|
| an object's order | the clustering key's, through the rows or a sorted run on the key ([13-dataset.md](13-dataset.md#53-what-a-compaction-does)) | the clustering key's slice by slice: slices follow each other in time, and the slice table replaces the sorted run on a time key |
| rows in time order (`OrderBy`, newest first) | read in order | slice by slice: a merge of the selected runs, each in time order, bounded by one slice. Under a broad filter on a column the synopsis does not hold, the newest rows lie anywhere in the slice, at worst a chunk per row and per column ([newest first](#96-newest-first)) |
| the key cursor on time | a seek and a step in the key source | a seek to the slice through the slice table, then inside it, where rows are not in key order: the slice's instant is read and ranked, about 13.5 MB a slice on the example (estimate). A rank adds the rows of the slices before it |
| a lookup by the whole key, an upsert | a seek in the key's source | a seek to the slice, then the slice's instant read, or the index of an identifier the key holds |
| a group by that streams on time | groups final as the key moves ([16-queries.md](16-queries.md#23-a-group-by-that-streams)) | buckets of at least a slice are final at the slice's end. Finer buckets group one slice at a time |
| sealing an object | a key cursor must open on the key | slices must follow each other and each must be sorted as its table says, or the object is refused |
| compaction | a k-way merge on the key, and a move without a rewrite for keys in order | slice by slice, under the style by periods: each row rewritten three or four times ([a compaction style by periods](#51-a-compaction-style-by-periods)) |
| other readers | the instant is sorted | the instant is sorted per slice, and their zone maps on it still prune by slice. The file is a plain Vortex file in another order |

Questions see two costs. The edges of their windows read the instant of a whole slice
([edges](#93-edges)), bounded by the slice's target. And rows wanted in time order under a filter the
runs do not select lie anywhere in their slice ([newest first](#96-newest-first)).

### 6.6 Race conditions

A slice is written by the job that writes its object, and the commit rules of every job are
unchanged. Two things are new. A seal must never be abandoned by a late row, so while a period seals,
level 0's exits route its rows to the late level. And a fresh merge or a seal takes inputs a delete may
mark while it runs: it is abandoned as a compaction is today ([13-dataset.md, Compaction folds the
marks](13-dataset.md#compaction-folds-the-marks)). On jobs of minutes over objects of gigabytes,
carrying onto the outputs the marks a delete made during the job, rather than abandoning it, is worth
a measure (hypothesis).

### 6.7 Done when

- Every slice the writer seals is sorted as its table says, and an object whose slices are out of order
  is refused at sealing.
- Rows in time order, the key cursor, `OrderBy` and the streaming group by give the answers they give
  today, on objects of sorted slices.
- A slice sort holds its reservation, and a slice past it sorts in runs.
- A run that crosses a slice boundary is cut there, and slices whose last and next first rows hold the
  same values still count each slice's rows exactly.
- The bytes of an object of sorted slices against the same rows in time order, under the default and
  the size-first profiles, recorded on the write bench.
- The costs of a lookup by key, a rank and a page of rows in time order inside a sorted slice,
  recorded.

## 7. The synopsis

### 7.1 What it holds

The runs give, for free, the counts of every combination of the synopsis columns at the slice's
grain. The synopsis keeps, beside them, what the runs cannot give cheaply:

| part | what it holds | where it lies |
|---|---|---|
| the slice table | per slice: its bounds, first row, row count, and the object's order of its synopsis columns | an index region |
| the slice directory | per column, its chunks: their offsets, lengths and rows | an index region per column |
| the synopsis columns of the synopsis part | the data's own synopsis columns, from the first down to a depth the budget sets, written as long column chunks so that they lie together | data segments, which the index directory names |
| run statistics | per run of each column the budget keeps them for, the states of every measure column | index regions |
| coarse tables | per bucket coarser than the slice (an hour, the period) and per depth the budget keeps: the bucket, the first `k` synopsis columns, a count and states | index regions |
| value counts | per column and bucket, each value, its count and states, for columns whose runs are long to walk alone: the deep synopsis columns, and the paths of variant columns an object left in its residual, counted from the parsed rows ([21-variant-columns.md](21-variant-columns.md#61-what-each-path-gets)) | index regions |
| postings | value to blocks, for deep synopsis columns ([10-indexes.md](10-indexes.md#51-vorticitypostingsblocksv1-value-to-blocks)) | index regions |

Everything in the synopsis but the synopsis columns stores keys or states a second time, and is
admitted only when it saves more than its bytes cost ([what the synopsis keeps](#73-what-the-synopsis-keeps)).

### 7.2 The synopsis part

The synopsis columns down to the depth the budget reaches, and the synopsis tables, are written last
and together, so that they form one contiguous range of bytes before the object's footer: the
synopsis part. The synopsis columns past that depth are still sorted, and still run-end encoded where
their runs are long, but lie with the rest of the data. A question whose depth stays inside the
synopsis part reads that part only. A deeper one also reads the runs of the deeper columns from the
data. On the example the synopsis part would most likely stop at `Endpoint`, since `Host` holds runs of
six rows: its runs then lie with the data, and its value counts and postings in the synopsis part.
The writer holds the synopsis part until the end of the object, and spools it to the local scratch
through the governor ([writing the object of a period](#52-writing-the-object-of-a-period)).

As built, a job tells the writer the object's synopsis columns, the columns its slices are sorted by,
and the writer holds each of their chunks in a scratch store, in memory up to its budget and in its
sealed file past it, then writes them together after the rest of the data. The index directory names
each chunk's segment with its checksum, and the part's extent
([the synopsis part](10-indexes.md#48-vorticitysynopsiscolumnsv1-the-synopsis-part)). The layout still
references the chunks, so a Rust reader reads the object as before (`SynopsisPartTests`, and the
cross-check of sorted slices). The depth the budget sets, and the tables written after the columns,
come with the synopsis's tables.

### 7.3 What the synopsis keeps

Every table is a candidate, each at each resolution: the depth of the synopsis part, run statistics
per column, coarse tables per depth, value counts per column, postings per column, and distributions.
A candidate is worth its bytes when answering from it costs less than answering from the next
candidate that exists, or from runs or rows when none does. The writer knows both sides of that
comparison. It measures each candidate's rows exactly, since it builds them, and the bytes a question
reads per row of a table and per row of data come from the files it writes. The costs of reading a
byte from each kind of store, of walking a run or a table row and of grouping a data row come from a
targeted bench. Among the candidates worth their bytes, the budget, a share of the object's bytes as
for [the index budget](10-indexes.md#6-the-policy), keeps those that save the most per byte stored, a
greedy choice in the manner of the classic selection of views (source).

The benefit of a table is computed over every question it can answer, weighed alike, since the
questions are unknown. Nothing is declared per column or per question. Every verdict is recorded in the
index directory with its numbers, as `IndexPolicy.Auto` records why it gave an index up.

A table's order is part of the candidate. Value counts sorted by bucket then value serve the top
values of each bucket. Sorted by value then bucket, with fence pages like those of long index runs
([10-indexes.md, The directory and the runs](10-indexes.md#3-the-directory-and-the-runs)), they serve
one value over a long window in one ranged read: one host per minute over a day reads its 1 440
entries, not the host's runs. A deep column may keep both orders, each admitted by its own benefit per
byte.

As built ([the synopsis tables](10-indexes.md#49-vorticitysynopsistablev1-the-synopsis-tables)), the
questions are the grains the candidate tables hold, and a question's cost is its source's bytes at a
price per byte plus its rows at a price per row grouped, a row of data or of a table alike. The price of
a row is measured: 12 ns on one lane over three keys, a count and three sums, reading and decoding
included (`vortex-queries --micro synopsistables`). The price of a byte is provisional, a tenth of a
gigabyte a second, an object store's one stream as the encoding advice takes it, until the bench of
questions measures each store's and the local cache's. The budget is the indexes' share of the data's
bytes, a tenth by default, with no floor. The verdicts go to the write report, which `Auto`'s reasons
go to, rather than to the index directory. The choice drops the run statistics of a deep column whose
runs are a few rows long, which cost more to read than the columns, and a table that holds the same rows
as one kept. A table holds about 1.9 KB even for one row, the nodes of its twenty-odd columns of states,
so a small object keeps few. The value counts sorted by value then bucket, the postings and the
distributions are not candidates yet.

A seal weighs the length of the next periods from the period it wrote, the bytes of a row bounding its
widest column's. Those are the bytes of its data alone: an object's tables and indexes belong to no
column, and counted in, they made a small period's rows look wide enough to shorten the periods to a
minute.

### 7.4 How the synopsis is built and merged

1. The sort gives the runs ([how a writer sorts a slice](#64-how-a-writer-sorts-a-slice)).
2. The pass that gathers the columns through the permutation sees each measure value in run order, and
   folds it into the states of its run, for every column whose run statistics are kept.
3. The value counts of every column come from its dictionary codes, cheaply, before the sort, and they
   give the order of the synopsis columns.
4. A coarse table is the merge of the runs of its bucket's slices: the slices of an object share its
   order, so runs come sorted alike, and the merge is a sorted merge that adds the states of equal
   keys, without a hash table.
5. At a fresh merge and at the seal, slices are merged ([how a writer sorts a
   slice](#64-how-a-writer-sorts-a-slice)), the slice tables concatenate, and the coarse tables and
   value counts merge as sorted lists, under the order of the object written.

The cost at write time, at 5 × 10⁴ rows a second (estimate): a radix pass per synopsis column and a
gather per column, so a few nanoseconds per row and per column, a few milliseconds of one lane per
second of rows.

As built, a job counts each slice as it writes it, from the codes the slice's sort gave each row. Down
to the first variant's shapes in the object's order, a combination's rows are one run and a group starts
where a code changes. Past them, the rows of a combination may lie apart, and pairs of a group and a
code number the groups. The measures are folded once per row, into the deepest groups, and each
shallower depth merges their states. Depth 0, every row of a slice, is kept when there are measures. At
the object's end, the deepest run statistics of every slice are coded across the object and sorted by
hour and keys with a counting sort per column, which gives every depth's coarse table per hour, each
merged from the one below it. The value counts of each key after the first, the first's being the coarse
table of depth 1, and the coarse tables per object, built only when the object spans more than an hour,
merge the deepest table per hour. A fresh merge and a seal sort their inputs' rows into new slices and
count them afresh, so a period's tables are those one write of its rows gives, slice for slice and row
for row (`SynopsisTableTests`). Merging the inputs' tables as sorted lists instead pays once a merge
keeps their sorted slices without sorting them again, and comes with it.

On a million rows in twenty slices, sorted by three synopsis columns of 5, 5 and 600 values, with three
measures, a slice's sort takes 58 ns a row, counting it 38 ns, and building the object's ten tables
31 ns (`vortex-queries --micro synopsistables`). The 600 values make runs of three and a half rows at
the third depth, the worst case of this mix: the tables' 305 000 rows are a third of the data's.

### 7.5 States

| aggregate | state | merge | final |
|---|---|---|---|
| `Count()` | a signed 64-bit count, or a run's length | addition | the count |
| `Sum` of integers or decimals | the exact sum, in the width the engine already chooses | addition | the sum in the column's type, as LINQ gives it, which throws when the total does not fit. A widened sum, asked by `AsInt128()` on the column (`g.Sum(r => r.DurationNs.AsInt128())`), delivers it whole |
| `Sum` of floats, `Average` | the indexed sum of [16-queries.md](16-queries.md#51-sums-exact-and-the-same-bits-whatever-the-cut): its three bins, their high parts and its top, and a count | bin by bin, each high part with its bin | the sum rounded once, the average |
| `Min`, `Max` | the extreme, whether there is one | the smaller or the larger | the extreme |
| `Distribution(x => e, relativeError)`, new | counts per bucket of a log-linear grid, each power of two split into `2^k` buckets, a value's bucket the top bits of its double, its exponent and `k` bits of its mantissa, never `Math.Log` ([distributions](16-queries.md#56-distributions-and-quantiles)) | addition per bucket | quantiles, a histogram |
| `ApproximateCountDistinct(x => e)`, new | HyperLogLog registers over an XXH3-64 of fixed seed, an exact set while small, sparse past that, dense last. UltraLogLog takes fewer bytes for the same error, but merges by OR-ing the hash prefixes its registers encode rather than by their maximum | the larger of each register | Ertl's improved estimator, no bias table |

Every merge is commutative, associative and exact within a state's capacity, so the order in which
objects, slices, runs and rows are merged never changes a bit. The states are integers and vectors of
integers, the same on every machine, and the one floating-point step of a distribution, finding a
value's bucket, reads bits instead of calling a platform's logarithm. A numeric column whose values
repeat, such as `Weight`, is a synopsis column, and its sum under any grouping is the sum of value
times count, exactly.

The engine's float sum took 2^36 values, about 69 billion, and threw past them, when two lanes merged
too, so a sum of weights over 16 days of the example threw. A wide state now takes the values past
that: an indexed sum whose bins are settled every 2^35 values and when two sums merge, each bin's
multiples of 2^56 moved into a high part of its own, 18 bits a bin in a word that also counts the
epochs settled, which leaves the bin's long within 2^55 of zero (code:
[WideIndexedSum.cs](../../src/Vorticity/Aggregation/WideIndexedSum.cs)). A settling moves value within
a bin, never between two, so it changes no bit of the answer, whenever it happens, and a high part
moves up with its bin when the top moves. A wide sum takes about 3.5 × 10¹³ values, 1,023 epochs and
one short of its end.

The wide state costs 40 bytes a group instead of 32, and 80 instead of 64 for a variance. A bench of
the grouped float sum measured that price on every group (vortex-queries --micro floatsum, 10⁷ rows,
2026-10-18): 3.5 % at a hundred groups, 9 % at 10⁵ and 15 % at 10⁶, beyond the noise. So the engine
keeps the 32-byte state wherever a source proves its rows within 2^36, as a file, a Parquet file and a
dataset do, and takes the wide one where it does not, as a query's result read again, and for every
state a dataset persists or merges past a scan. The integer sums choose their width the same way. The
wide state folds without the narrow one's grouped fast path, 1.6 to 1.7 times its time on that bench,
which only a source past 69 billion rows pays. Persisted, a bin is two `Int64` columns, its long and
its high part, the second almost always zero and compressed to nothing.

An integer sum keeps the column's type because LINQ does, and `Sum(DurationNs)` passes a `long` over 30
days of the example once the mean duration passes 71 ms (estimate): hence the widened sum, asked by
name.

A distribution is the synopsis's heaviest state: an entry of a thousand latencies, log-normal around
50 ms, occupies about 395 buckets of the grid (`vortex-queries --micro sketches`, 2026-10-19). A
threshold is answered exactly only on the grid's bounds, fractions of powers of two, and the thresholds
people use, 250 ms, 500 ms or a second, almost never are. A decimal grid, of buckets
`[d · 10^e, (d + 1) · 10^e)` for `d` from 100 to 999, would answer every threshold of three
significant digits exactly, at 0.5 %. Weighed on the same entries it occupied 695 buckets and took 13.2
ns a value, where the bits take 1.6 ns and DDSketch's logarithm 2.4 ns, so the bits stay.

Every state is a kind of the index directory with a version, test vectors across platforms as the
Bloom filter has against Rust ([10-indexes.md](10-indexes.md#41-vorticitybloomsbbfv1-a-split-block-bloom-filter-per-block)),
and a rule for objects written under an earlier version. The hash of a distinct sketch is part of its
kind, never the runtime's, which changes from one process to the next.

### 7.6 Sizes, on the example

Per period of a day (estimate):

| part | entries | bytes |
|---|---|---|
| the synopsis columns down to `Endpoint`, per minute | 7 × 10⁷ runs | 140 to 280 MB |
| run statistics of `DurationNs`, down to `Service`, per minute | 4.3 × 10⁶ | about 90 MB |
| coarse tables down to `Endpoint`, per hour, with the states of `DurationNs` | 1.2 × 10⁷ | about 300 MB |
| coarse tables down to `Endpoint`, per period | 2 × 10⁶ | about 50 MB |
| value counts of `Host`, per minute, and of the other columns per hour | 1.5 × 10⁷ | about 120 MB |
| distributions of `DurationNs`, beside the states of the run statistics and coarse tables | 1.8 × 10⁷ | 2 to 5 GB |

Without distributions that is 0.7 to 0.85 GB, under 0.5 % of the period's 173 GB, against 17 to 30 GB
that the sorted synopsis columns take off the data itself. With them it is about 3 to 6 GB, 2 to
3.5 % of the period, which the budget weighs like any table. An object of sorted slices with its
synopsis should come out smaller than the same rows in time order without one, which the write bench
checks.

As measured on requests at 50 000 a second with seven synopsis columns (`vortex-queries --micro
slicebytes`), the sorted slices and their synopsis columns weigh 2.4 % less than the rows in time
order under the default profile, about the same under the size-first one, and 8 % less than an
object of a dataset of levels with its key's run. The tables come on top, as many as the choice keeps
under the budget: 0.93 bytes a row on a million rows in one slice, 2.3 % of the data, and 2.44 bytes
a row on four million in 79 slices, 6 %, where a coarse table per hour down to the service, 7.5 MB,
saves 428 ms of a question. With them the object can outweigh the rows in time order, by choice: the
tables trade bytes for the time of questions, and the budget, not an estimate, bounds what they take.
A table takes about 1.5 to 1.9 KB even for one row, a node for each of its twenty-odd columns of
states. The distributions are not candidates yet.

## 8. Reading the synopsis part without the data

### The problem today

An open reads the file's last 64 KiB, then every dtype, layout, statistics and footer segment that
lies before them, in one more read (code: [VortexFile.cs:413](../../src/Vorticity/Vortex/File/VortexFile.cs#L413)).
For a period of 173 GB the footer runs to megabytes, and a question that needs only the synopsis part
would fetch it for every object.

### What changes

1. A synopsis-only open reads the synopsis part, and only the columns and tables of it a question
   needs, in ranged reads coalesced as every read is. It never reads the data's footer, since the
   index directory names the segments of the synopsis columns itself.
2. The dataset's leaf entry carries the synopsis part's table of contents: each column's and table's
   kind, resolution and regions, and the region of the slice directory, a few hundred bytes. A
   question whose window covers whole periods then reads what it needs in one round trip, for every
   object of its window at once. An object with variant columns may hold hundreds of paths, so its
   table of contents names its path directory and the paths its rows hold most, and a question on
   another path reads the directory first, one round trip more, cold only ([finding a path's
   synopsis](21-variant-columns.md#64-finding-a-paths-synopsis)).
3. A window shorter than its period, an hour of a day, cannot take whole regions: the synopsis columns
   of a day weigh 140 to 280 MB (estimate). It reads the slice directory first, then the ranges of the
   slices it covers, in two round trips cold and none warm.
4. The leaf entry also records the footer's length, so that a whole open, which compaction and rows
   read without the slice directory need, reads the tail and the footer in one request at any size
   ([writing the object of a period](#52-writing-the-object-of-a-period)).

```
round trip 1   the columns and tables a question needs, for each object of its window       in parallel, from the tables of contents in their entries
               (a window shorter than its period: the slice directory first, then its slices' ranges)
```

As built, the writer of a sliced object completes it with its table of contents, which the seal puts
in the object's entry ([the dataset's entries](13-dataset.md#4-the-tree)). It names the object's
schema, so that a table's rows decode at a type rebuilt from it and the contents hold no table's
dtype, the encodings its blobs name, its slice table, each column's chunk directory, the synopsis
part's extent, each table's region and each synopsis column's chunks. The dataset reads the tables of
an object from it in one request set, and a slice through a reader that opens with no read at all.
On two sealed hours of calls behind a 20 ms store, the tables of both objects read in one dependent
round trip and one request an object, 27 of the 32 KB of their parts, and a slice in two
(`SynopsisContentsTests`). The entry of such an hour weighs 1.3 KB, its table of contents 1.1 KB. Every
synopsis column stays in the part, since a reader takes the regions it needs, not the part whole. The
paths of a variant column join the contents with the synopsis of paths.

### Done when

On a counting store with injected latency, a question the runs and synopses answer makes one
dependent round trip per object cold when its window covers whole periods, two when it is shorter,
whatever the object's size, and reads none of its data segments past the synopsis part, nor its
footer (ratchets, as `ReadBudgetTests` and `DatasetBudgetTests` are).

## 9. Answering a question

### 9.1 The plan

Nothing changes in how a question is written. For each object of its window, which the tree's
summaries already find, the planner:

1. finds each variant path the question names in the object's path directory, which says whether the
   object shredded it, counts it, or does not hold it ([resolving a path](21-variant-columns.md#71-resolving-a-path-object-by-object))
2. cuts the window into the object's whole coarse buckets, its whole slices, and its edges
3. answers each whole coarse bucket from the coarse table of the question's depth, or from the value
   counts when the question touches one column
4. answers each whole slice by walking its runs ([walking the runs](#92-walking-the-runs))
5. reads rows where neither answers: a column the synopsis does not hold, a measure past the run
   statistics, a time grain finer than the slice, a variant path left in the residual and counted by
   no table that fits the question, whose rows only the shapes holding it are parsed for
6. reads the instant of each edge slice ([edges](#93-edges))

The value counts also tell the planner exactly how many rows each value holds, which lets it compare
walking deep runs with reading a value's rows through its postings. A question on one value over a
long window reads that value's range of value counts sorted by value ([what the synopsis
keeps](#73-what-the-synopsis-keeps)), and a filter on one host joined with other columns reads that
host's blocks rather than every run of `Host`. A bucket that holds marked rows is corrected by their
contributions ([deletes](#10-deletes)).

Every part yields states, merged into the answer across objects, so a top N, its exact "others", an
average or a quantile come out whole. Every row of a run holds the run's values, nulls included, so a
predicate over synopsis columns, under three-valued logic, passes a run exactly when it would pass
each of its rows. The answer is the bits a scan of the rows gives.

### 9.2 Walking the runs

For one slice and a question of depth `d`:

1. Read the run-end columns the question touches, and the `d`-th, which it touches by definition.
2. Evaluate each predicate once per distinct value of its column, on the dictionary, which gives a set
   of codes. A predicate on the first column becomes a range of rows.
3. Walk the runs of the `d`-th column in order. The runs of the shallower columns the question touches
   nest in them, so one pointer per column follows along. For each run, look the codes up in the
   predicates' sets, form the group's key from the touched columns' values, add the run's length, minus
   the marked rows inside it, to the group's count, and its run statistics to the group's states.
4. Skip whole every range a predicate on a shallow column excludes.

The cost is the runs of the `d`-th column, plus those of the shallower columns touched, never a row.
No run spans two slices: one that crosses a boundary is cut at the slice's first row. The engine already groups a
composite key whose parts are run-end encoded this way, one lookup per range
([16-queries.md](16-queries.md#91-keys-block-by-block)).

### 9.3 Edges

The runs and tables count rows per slice, a minute for instance. Take a question about the last 30 days
asked at 14:17:30. Its window starts 30 days ago at 14:17:30, in the middle of the minute 14:17. The
runs of that minute count all its rows, not those after 14:17:30, and inside a sorted slice the rows
after 14:17:30 are spread through the slice, since rows there are sorted by their synopsis columns.
Two answers are exact:

- read the instant of every row of that one slice, keep those after 14:17:30, and add them to the runs
  they belong to: one slice of rows, 3 × 10⁶ at the example's rate, about 10 ms of one lane
  (estimate)
- or move the window's start to 14:17:00 or 14:18:00, and say so with the answer

The same holds at the window's end, which usually falls in level 0 or the fresh levels, whose rows a
question reads anyway: in practice the start pays. A dashboard that draws points per minute or per
hour aligns its window on them anyway, and then reads no edge. The instant lies with the data, so an
unaligned question reads the slice's chunks of the instant, found through the slice directory rather
than the footer: about 13.5 MB at 36 bits a row in nanoseconds, in one round trip more, warm as well,
since the cache keeps no data (estimate). The plan states the rows its edges will read, and the
default is one of the [decisions to take](#18-decisions-to-take).

### 9.4 Excluding a rare value

Under three-valued logic, `r.RequestId != id` is unknown where `RequestId` is null, so it drops every
row that has none ([08-semantics.md](08-semantics.md#3-three-valued-logic)). "Every row except those
of this request" is `r.RequestId != id || r.RequestId == null`. A product that offers to exclude a
value almost always means the second form, and on a column that is mostly null the two differ by most
of the table.

The second form is the complement of `r.RequestId == id`, which is rare. For a count, a sum or a
distribution, the planner answers it as the whole minus the part: the whole from the runs and
synopses, the part from the rows a needle finds ([needles](#95-needles)), each attributed to its run
without reading the row, since a position names its slice and, through the run ends, its
combination. An extreme stands unless an excluded row holds it, which a comparison tells. A distinct
count has no subtraction, and is read as rows.

### 9.5 Needles

A needle is a question about one rare value: every row of one `RequestId` over 30 days. `RequestId`
has a value per row, so it is no synopsis column, and only an index finds it. Two of today's
structures fail at this scale. A Bloom filter tree filters the union of sixteen children per node, and
a node past what a filter can hold is built without one, the descent starting below it
([10-indexes.md, The directory and the runs](10-indexes.md#3-the-directory-and-the-runs)): over
4.3 × 10⁹ values a day no upper node is left, and a probe reads a whole level of filters, about 5 GB
per object of a day at 1 % (estimate). `Auto` gives such a filter up anyway, and never builds the
sorted run that would answer ([10-indexes.md, The policy](10-indexes.md#6-the-policy)).

So the application declares the columns whose values questions look up, its identifiers
([a compaction style by periods](#51-a-compaction-style-by-periods)). Indexing one costs 10 to 25 % of
an object, a physical choice like the clustering key, and not an aggregate tied to a question.

1. Each identifier gets a sorted run in every object, outside the index budget as the clustering key's
   run already is ([10-indexes.md, The policy](10-indexes.md#6-the-policy)), its bytes reported by the
   `WriteReport`. On the example that is about 14 % of an object for a trace identifier whose rows come
   ten to a value, and 20 to 25 % for a span identifier of 8 bytes unique to its row, whose positions
   take 33 bits past 2³² rows (estimate).
2. A probe reads the run's root from the table of contents, its fence pages from the synopsis cache,
   about 2 MB per object of a day (estimate), then one segment of the run, about 650 KB for 65 536
   entries, then the rows it finds, through the slice directory. That is three dependent round trips
   cold and two warm, for every object of the window at once.
3. A hashed index from value to block is measured next (hypothesis). Its (value, block) pairs spread
   over pages by hash, each page holding fingerprints of 24 bits, sorted and coded in Elias-Fano at
   about 12 bits an entry, and a block number of 19 bits, its table of pages cached. That is about 3.9
   bytes a pair, 9 to 10 % of an object per identifier instead of 14 to 25 %, a page of about 64 KB
   read per probe instead of 650 KB, and about one false block in a thousand probes (estimate). It
   replaces the sorted run if a bench holds those figures. The index design left exactly this case
   open, row lookups on long incompressible keys ([10-indexes.md, Considered and not
   built](10-indexes.md#55-considered-and-not-built)).
4. A needle is delivered newest first, as every progressive question is
   ([newest first](#96-newest-first)), and the application stops once it holds what it looks for. A
   trace lives in a few seconds, which Vorticity cannot assume of a needle in general.

As built, every object a dataset writes, appends and jobs alike, carries a required sorted run on each
identifier, spared by the budget, and Auto tries no Bloom filter on it. A random identifier of 32
hexadecimal digits takes 19.3 bytes a row in its run, about 43 % of an hour of such rows, since the
rows here are little else (`IdentifierRunTests`). Positions already take 64 bits past 2³² rows. The
table of contents names the object's index directory, and a lookup reads, with no open of the
object, that directory beside every column's chunk directory, then the run's segments that may hold
the value, then the chunks of the blocks the run leaves: three dependent round trips cold for the
object that holds the value, two for one whose run rules it out, and none warm once the session's
cache holds them. The rows of a block are all returned, and the caller keeps those the filter holds
for. The planner does not route needles to this read yet, and the fence pages are not kept apart
from the session's cache.

Over 1.24 × 10⁶ objects of 4 MiB, a needle costs 1.24 × 10⁶ probes. Over periods of a day it costs
30. A needle in a path of a variant column, a user's identifier among the attributes, takes the same
road through the indexes of that path and its type class, which Vorticity keeps since the format gives
a path none ([what the format gives a
path](21-variant-columns.md#510-what-the-format-gives-a-path-and-what-vorticity-adds)).

### 9.6 Newest first

A question's objects and slices are read newest first, and each one's part of the answer is final for
its range of time, so the answer can be delivered as it grows, with the part of the window it covers.
The newest rows under a filter come from the newest slice: the filter selects runs, and their rows,
each run in time order, merge newest first until the question has enough.

Under a broad filter on a column the synopsis does not hold, the newest rows of a sorted slice lie
anywhere in it. Taking the 200 newest is then a scattered take: at worst a chunk per row and per
column, hundreds of megabytes with chunks of a few megabytes, where time order reads the last chunk or
two (estimate). Level 0, in time order, serves the live tail and the newest seconds, and reading part
of a flat segment ([writing the object of a period](#52-writing-the-object-of-a-period)) would bring
the rest down to the rows' own bytes. Whether the fresh levels should keep time order with a synopsis
beside them is one of the [decisions to take](#18-decisions-to-take).

### 9.7 What the plan and the metrics say

`ExplainAsync` returns, per object, what it reads (coarse tables, runs at which depth, rows and why),
and the rows of its edges. Once the question has run, its metrics count the table rows, runs, edge
rows and data rows it read, and the requests it made.

### 9.8 Examples

Thirty periods of a day, nothing on the local disk, 30 to 50 ms a round trip. Every figure is an
estimate.

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

The bytes are those of the columns read: the synopsis is columnar, so a count reads no distribution.
Reading the host's runs instead of its value counts would read the `Host` column of the day, 7.2 × 10⁸
runs and 1.4 to 3.2 GB, since a run-end array is located through its ends (estimate).

### 9.9 Done when

- Every question of the test matrix gives the same bits with runs and synopses on and off, over edges,
  deletes, objects without a synopsis and every kind of predicate. `VortexReadOptions` gets the switch
  that pruning and indexes already have.
- The round trips and bytes of each example are ratchets on a counting store with latency.
- The complement gives the bits of the rows' answer on a column that is mostly null.
- One value over a long window reads its range of value counts, not its column's runs (a ratchet on
  bytes).

### 9.10 A page's questions together

A page sends 8 to 15 questions over one window, which walk the same runs and tables. An API that takes
them together could walk each object's runs once and feed every grouping, which divides the walking.
The reads are already shared, by the single read per region of the synopsis cache ([the synopsis
cache](#11-the-synopsis-cache)) (hypothesis, of low priority).

## 10. Deletes

A delete marks rows, as today. Counts stay exact without anything more: a run's count is its length
minus the marked positions inside it, so a question walking runs needs only the deletion vector it
already reads with the object.

What the runs do not carry is corrected rather than set aside: the run statistics of a run, and the
coarse tables and value counts of a bucket, that hold marked rows. The vector is bounded and its
positions sorted, so a walk of the positions against the run ends of each synopsis column gives each
marked row's combination, and a take gives its measures. The counts and sums of the bucket are then
corrected by subtraction. An extreme changes only if a marked row holds it, which a comparison tells.
A float sum is corrected the same way unless a marked row holds the magnitude that sets its top bin. A
distinct sketch has no subtraction, and its bucket falls back to the runs of its slices. The
correction is computed once per object and vector, and kept in memory with the object's handle. No
delta is written. A purge or a compaction rewrites the object, and its synopsis is exact again ([marks
at the scale of a period](#53-marks-at-the-scale-of-a-period)).

Without the correction, an erasure scattered over every period would push every question over 13
months from the tables by period down to the tables by hour, then to the runs of every marked bucket.

Retention never takes this path. It removes whole objects ([retention and late
rows](#12-retention-and-late-rows)), whose synopses leave with them.

The fuzzer that drives deletes, updates and compactions checks, at every version, that every count
walked through runs equals the count of the live rows, and that every coarse table it uses, corrected
by the marked rows, equals the merge of its slices' live runs.

## 11. The synopsis cache

### What it keeps

The local disk is small next to the store, so it keeps only what a question reads without the rest of
the data: the synopsis parts of objects, the slice directories in them, through which rows are found
without the footer, and the roots of their indexes (the upper nodes of Bloom filter trees, the fence
pages of sorted runs), which needles probe. It never keeps the rest of the data, nor the data's
footer. A thirteen-month window of the example holds about 300 GB of synopsis
parts (estimate), so the cache holds what questions use most: the coarse tables by period, the value
counts, the shallow synopsis columns, a small part of the whole.

### What changes

```csharp
VortexSession session = VortexSession.Create(o =>
    o.SynopsisCache = new SynopsisCache("/var/cache/vorticity", capacityBytes: 200L << 30));

// Optional: fetch ahead the synopsis parts a window's questions will read.
await requests.WarmAsync(r => r.At >= DateTime.UtcNow.AddDays(-30));
```

1. No invalidation. An entry's key is the object's identity, its uid and the store's token, which the
   dataset knows from the object's entry before any read, and the region's place in the object. An
   object never changes, so an entry stays valid until it is evicted.
2. Integrity. Index regions already carry an XXH3-64 ([10-indexes.md](10-indexes.md#3-the-directory-and-the-runs)),
   and the index directory records one for each segment of the synopsis columns it names. Each is
   checked on every read from the disk, and a region that fails is dropped and read again from the
   store.
3. Sealed stays sealed. The cache sits under the decorator that seals an encrypted dataset, so it
   holds what the store holds.
4. Persistence. An entry is a file in directories sharded by its key's hash, written under a temporary
   name and then renamed. A walk of the directory rebuilds the index at start. One process owns a
   directory, through a lock of the operating system.
5. One read per region. Questions that miss the same region at the same moment wait for one read.
6. Eviction. The policy weighs frequency as well as recency, resists a question that scans thirteen
   months once, counts bytes rather than entries, ages its counts, and keeps a few bytes per entry.
   S3-FIFO and SIEVE are recent FIFO-based policies, simpler than LRU and run in production, and Window
   TinyLFU is the older admission design of the Caffeine library. The published comparisons disagree
   once entries are weighed by their size (source), so the default is chosen by replaying the access
   traces a counting store records on the bench's questions, and the replay stays in the bench.
7. What it tells, through the meter that already exists: hits, misses, bytes from the disk and from the
   store, evictions, refused admissions, corrupt regions and bytes held.
8. What it saves the store. A cold question over 30 days makes 30 to 60 requests, and at hundreds of
   questions a second the store's limits come near: 5 500 reads a second per prefix on S3, 900 per
   bucket on OVHcloud ([the targets](#14-the-targets)). The hit rate the bench measures says whether a
   deployment stays under them. S3 partitions a prefix under sustained load, which the fresh uid that
   names every data object allows ([13-dataset.md, Objects and keys](13-dataset.md#3-objects-and-keys)).

### Done when

- Warm, the aligned examples make no request to the store (a ratchet).
- The bench records the requests a second its load makes, and the hit rate that keeps them under each
  store's limit.
- A region torn or flipped at every byte is caught, dropped and read again.
- A restart keeps the cache, and two processes on one directory do not corrupt it.
- Nothing past the synopsis part ever enters it (a test over every question of the matrix).
- The trace replay is part of the bench, and its verdict is recorded in the bench's README.

## 12. Retention and late rows

### 12.1 Partition columns

A job's footprint is judged on the leading key column alone (code:
[LevelRange.cs:49-75](../../src/Vorticity.Dataset/LevelRange.cs#L49-L75) and
[107](../../src/Vorticity.Dataset/LevelRange.cs#L107)). Under a key `(Retention, At)`, every object of one
retention value overlaps every other object of that value, so a job takes the whole of that value's
part of its destination level, and an append that mixes values mixes them in its objects, so that no
object can ever be expired whole.

`DatasetOptions.PartitionColumns`, fixed at creation, says how many leading clustering-key columns are
partition columns:

1. An append splits its rows into one object per partition value. Partition columns have few values by
   declaration, and an append past a bound the dataset states is refused.
2. No job takes objects of two partitions, and overlap is judged on the whole key, from bounds built
   out of the per-column summaries: the tuple of the key columns' minimums and the tuple of their
   maximums bound every key of the object, exactly on the columns that are constant in it, as
   partition columns are.
3. Each partition grows its own periods of sorted slices, and a period holds one retention: what dies
   together is written together.

### 12.2 Expiry without opening objects

A delete opens and counts every object its summaries do not refute (code:
[DatasetRowChanges.cs:251-256](../../src/Vorticity.Dataset/DatasetRowChanges.cs#L251-L256)). The
summary pruner also learns to say "true for every live row": an entry whose bounds lie wholly inside
the filter's range, with a null count proven zero on the columns it names, is removed without an open.
Bounds stay valid under marks, which only loosen them outwards, and the clustering key holds no null.
`ExpireAsync` builds on it:

```csharp
DateTime applied = await requests.ExpireAsync(r => r.Retention == "debug", before: now - TimeSpan.FromDays(7));
```

It removes, in one commit, every object of the partition whose period ends before the cutoff, and
their synopses with them, reading none of them. The cutoff is aligned down to a period boundary, so
that no object straddles it, and the call returns the cutoff it applied. A row may thus outlive its
retention by up to a period, which is why a period stays below a share of its partition's retention
([the length of a period](#54-the-length-of-a-period)). A filter on anything but partition columns is
a delete, not an expiry.

### 12.3 Late rows

A row within the lateness horizon lands in the fresh levels, or in the late level when it is older
than the fresh object it would overlap ([a compaction style by periods](#51-a-compaction-style-by-periods)).
The late level's small objects merge among themselves, so a late row never rewrites a large object,
and a job's footprint is no longer the hull of its sources on the leading key column. Today a row an
hour late among the rows of the last minute makes a job rewrite every destination object of the last
hour, 7.2 GB at the example's rate (estimate).

A row later than the horizon belongs to a sealed period. It stays in the late level, in its own
partition and so under its own retention, and questions read it beside its period until a seal again
pays for itself. The application may also refuse such rows. The dataset counts them either way.

### 12.4 Done when

- Interleaved appends over three partition values rewrite each row as often as appends in order do.
- Rows an hour late rewrite no period, and rows later than the horizon keep their partition's
  retention.
- Expiring a partition's old periods reads no data object (a ratchet on requests).

## 13. Columns known only at run time

Attributes whose keys are data, which appear with every new source, are not columns of the schema at
all: they are paths of a variant column, shredded per object, and need no change of schema
([21-variant-columns.md](21-variant-columns.md)). What follows is for columns the schema does declare
after the dataset's creation.

1. Every column a writer meets is measured for the order, those that schema evolution adds included,
   from the next slice it sorts. An object written before a column existed holds it as null, one run
   per slice.
2. Aggregates on the tool path. `Scan("Service", "At").Where(text)` exists, and a group by over it does
   not ([16-queries.md](16-queries.md#12-not-in-it-on-purpose)). Its API is the one the queries design
   announces, over the same engine, and the planner serves both paths alike.
3. An index policy for every column. `IndexAsync` refuses a policy that names no column (code:
   [DatasetIndexing.cs:113-116](../../src/Vorticity.Dataset/DatasetIndexing.cs#L113-L116)).
   `IndexPolicy.Auto`, committed as the dataset's policy, would apply to every column a writer meets,
   later ones included, and to every path of a variant column that an object shreds, one entry per
   type class the path holds ([what the format gives a path](21-variant-columns.md#510-what-the-format-gives-a-path-and-what-vorticity-adds)).
   Identifiers are the exception: the application declares them, since their index costs 10 to 25 % of
   an object ([needles](#95-needles)).

## 14. The worst cases

### 14.1 How a question's cost is counted

A question's time is the sum of four terms, each of which a counting store and the metrics count
rather than time:

| term | what it counts |
|---|---|
| round trips | requests to the store that the next one waits for |
| synopsis bytes | the bytes of synopsis parts read |
| runs and table rows | the entries of run-end columns, run statistics and synopsis tables walked |
| data rows | the values of data columns read |

The notation: `N_s` rows per slice, `R_k` runs of a slice's `k`-th synopsis column, `S` slices and `P`
objects in the window, `G` groups in the answer. On the example (estimates): `N_s` = 3 × 10⁶,
`R_4` = 3 × 10³, `R_6` = 4 × 10⁴ and `R_7` = 5 × 10⁵ per minute, `S` = 43 200 over 30 days and
5.7 × 10⁵ over thirteen months, `P` = 30 and 395. A run or a table row costs a few nanoseconds to walk,
as a data row does to group (measured per row, [16-queries.md](16-queries.md#13-how-it-is-tested)).

### 14.2 What is bounded

- A question over the columns of the synopsis part, on a window aligned on slices, reads no data row,
  makes one round trip per object cold when its window covers whole periods and two when it is
  shorter, none warm, and walks at most the runs of its depth per slice, or the rows of a coarse table
  per bucket.
- An edge reads the instant of at most one slice at each end, found through the slice directory,
  whatever the volume, since slices are sized by it.
- A needle on a declared identifier makes three dependent reads per object cold and two warm, then
  reads the rows it finds.
- No question reads a column it does not touch, or a value twice.
- A selective question reads no more than over the same rows in objects of 4 MiB, by the rule that
  sizes chunks from the footer's cap, and less once part of a flat segment can be read ([writing the
  object of a period](#52-writing-the-object-of-a-period)). A question that scans reads no more than
  the rows of the columns it touches, which is what it would cost without any of this.

The answer's own size, `G` groups, is a floor every design shares. The cases below cost more than
their answer.

### 14.3 Time

| case | what it costs | on the example | what the design does | what remains |
|---|---|---|---|---|
| bucket boundaries that cut slices: a width that is not a multiple of the slice's, or a time zone whose offset is not | one slice of rows per boundary cut, `B · N_s` | 500 points over 30 days at 86.4 minutes: 1.5 × 10⁹ rows, seconds | the plan states the boundaries it cuts, and the dataset offers the widths that cut none, 86 or 87 minutes here | a caller that insists on its width pays `B · N_s` |
| an unaligned window | the instants of one slice at the start, the end falling in level 0 or the fresh levels, and one round trip more, even warm, since the instant lies with the data | 3 × 10⁶ instants, about 13.5 MB, 10 ms and a round trip | aligned windows read none, and the slice directory spares the footer | a round trip warm |
| a slice past its target, from a burst | its edge reads more rows | ten times the target: 3 × 10⁷ rows, about 0.1 s | the writer splits it into finer calendar units | none |
| a grain finer than the slice | the rows of the slices concerned | per-second counts over an hour: 1.8 × 10⁸ rows | slices sized by volume, so the finest grain grows only where rows are few | rows |

### 14.4 Depth

| case | what it costs | on the example | what the design does | what remains |
|---|---|---|---|---|
| a deep question over a long window, with no coarse table at its depth | the runs of its depth in every slice, `Σ R_d` | down to `Endpoint` over thirteen months: 5.7 × 10⁵ × 4 × 10⁴ = 2.3 × 10¹⁰ runs, minutes | coarse tables by period down to the deep columns, when the budget allows: 395 × 2 × 10⁶ = 8 × 10⁸ entries, seconds. Longer periods merge more: a week per object would give about 1.7 × 10⁸ | progressive. The coarsest table lives in one object, so the period's length bounds it |
| untouched shallow columns: a question touches a deep column but not some of those before it | the runs of its depth, not the combinations of the columns it touches: their product with the values the untouched columns take inside each combination | `Version` and `Endpoint` together, without `Status`, `Method` and `Region`: about 15 times their own combinations | nothing by default. A table over a subset of columns, admitted by its benefit per byte like any table, would remove it | ×10 to ×100 over the ideal, on structures already ×100 to ×1 000 smaller than the rows |
| a non-selective predicate on a deep column, under a shallow grouping | the runs of the deep column, the predicate evaluated once per distinct value and looked up per run | `Endpoint` matching a pattern half of them match, by `Service`, 30 days by hour: 720 × 5 × 10⁵ = 3.6 × 10⁸, a second or two. By period: 30 × 2 × 10⁶ = 6 × 10⁷ | the complement, when the predicate is the negation of a selective one | the rest |
| a selective predicate on a deep column without postings | every run of that column, or its rows | `Host == "h-17"`: 5 × 10⁵ runs a minute | postings on deep columns, and value counts that tell how many rows the value holds | rows, where the budget keeps neither |
| many facets: a dozen repeating columns or more, as a table of spans holds | the deepest facets' runs, of one or two rows, and their rows spread over thousands of ranges a minute | `route` after a dozen shallower facets: runs close to its rows | value counts and coarse tables by bucket for deep columns, which the budget admits by their benefit | the deepest facets' rows, for questions finer than their tables |
| a measure deeper than the run statistics | the measure's values for the touched runs | the average duration per `(Service, Endpoint)` per minute when run statistics stop at `Service`: up to every row of the window, 300 GB over 30 days | coarse tables carry the states at their depth, and the budget keeps states wherever it keeps counts, or the plan says it cannot | the measure's rows, at grains finer than the coarse tables |
| a column whose number of values moves between slices | the slices whose own counts would order it otherwise pay a few runs more | a column with few values some minutes and many others | one order per object, measured on its rows | a few runs more in those slices |

### 14.5 Columns nothing summarizes

| case | what it costs | on the example | what the design does | what remains |
|---|---|---|---|---|
| a threshold on a measure | the values of the runs whose minimum and maximum straddle it | `DurationNs > 10⁹`, where fast and slow requests mix in every run: every row, 1.3 × 10¹¹ over 30 days | a threshold on the distribution's grid is answered exactly from the sketches, and a decimal grid would answer every threshold of three significant digits (hypothesis) | a threshold off the grid reads rows |
| an identifier the application did not declare | its column, over the window | `RequestId` over 30 days: about 2 TB, hours (estimate) | declared, it gets a sorted run in every object written after | the objects written before, until a compaction rewrites them |
| a column whose values rarely repeat, under a non-selective predicate: the top 20 raw URLs, an exact distinct count of `RequestId`, a text search on a frequent word | the column, over the window | the URL column over 30 days: about 1.3 TB, hours | progressive, newest first, and a distinct sketch per slice when the budget keeps one | inherent: nothing that ignores the questions answers them without the column |
| a needle that is not rare | the rows holding it | in proportion to them | nothing: the answer is that large | output-bound |
| a predicate between two columns, or a function of a column the synopsis does not hold | that column's rows | | evaluated per run when both columns are synopsis columns | rows otherwise |
| a path of a variant column that an object left in its residual | the residual of the rows whose shape holds it | a key a few rows of one source hold | the synopsis counts it where its budget admits, shapes keep its rows together, and postings find them ([21-variant-columns.md](21-variant-columns.md#8-the-worst-cases)) | a parse per row that holds it, for questions finer than its tables |

### 14.6 Scale

| case | what it costs | on the example | what the design does | what remains |
|---|---|---|---|---|
| many objects | one round trip per object, in parallel | 395 objects over thirteen months: two to four rounds of parallel requests, 0.1 to 0.2 s cold | periods sized by volume | none |
| many groups, or a top N whose ranking needs every group | `G` hash lookups, `G log N` to rank, memory for `G` | the top 100 of `(Endpoint, Host)` over 30 days: about 10⁶ groups, tens of milliseconds | the governor and the spill ([16-queries.md](16-queries.md#95-memory)) | output-bound |
| fresh rows | the rows of level 0, and the objects of the fresh levels | the last hour: level 0's eight appends of three seconds, 1.2 × 10⁶ rows, and about 35 fresh objects. A path of a variant column costs 60 to 120 ms of one core more, since appends leave variants unshredded ([the worst cases of variants](21-variant-columns.md#8-the-worst-cases)) | fresh merges by f, and a memo of level 0's parsed paths | the objects f leaves, and a lagging loop, which `PlanCompactionAsync` reports |
| deletes | the marked rows' contributions, computed once per object and vector, and the runs of the buckets a distinct count needs | in proportion to the marks | tables corrected by the marked rows, and a purge per erasure delay | bounded by the purge policy |
| rows newest first under a broad filter, past level 0 | a scattered take, at worst a chunk per row and per column | 200 rows: hundreds of megabytes with chunks of a few megabytes (estimate) | level 0 in time order serves the live tail | the take, until part of a flat segment can be read |
| a lookup by key, a rank, a page in key order inside a sorted slice | the slice's instant, or a merge of its last column's runs | about 13.5 MB a slice (estimate) | the index of an identifier the key holds | a slice's instants per seek |
| requests a second at the busiest load | 30 to 60 per cold question over 30 days | at 100 cold questions a second, 3 000 requests a second, past OVHcloud's 900 per bucket | the cache, and data objects named by a fresh uid that S3 can partition | a cold load past the store's limit |
| long jobs | a seal of a day is minutes of work | 173 GB read and written | late rows routed away from a period that seals, and a lifecycle rule for the uploads an abort missed | a failure restarts the seal |
| a window larger than the cache | cold round trips for what the cache does not hold | thirteen months of synopsis parts, about 300 GB | a policy that resists scans keeps what questions use most | none |
| hundreds of questions a second | their processor time | | each question is cancellable and governed, and a progressive one yields as it goes | sizing |

### 14.7 What remains

Two families remain, and they belong to the problem rather than to this design:

- The first is the questions whose answer is large: many groups, a fine grain over a long window, a
  needle held by many rows. No design answers them in less than their answer's size.
- The second is the questions that put a non-selective predicate on a column whose values rarely
  repeat: identifiers, raw URLs, free text, arbitrary thresholds on measures. A structure that does
  not know the questions cannot summarize those columns, so the question reads the column, newest
  first, progressively.

Three overheads belong to this design, and each has its lever. Cut bucket boundaries cost a slice of
rows each, and aligned widths remove them. Untouched shallow columns multiply the runs walked, and a
table over a subset of columns, admitted by its benefit, removes them. A deep question over a long
window walks a coarse table per period, and longer periods shorten it.

The order and the life of a row add three more costs, accepted with it: three or four rewrites per row,
a slice of instants per unaligned window, and scattered takes for rows wanted in time order under a
broad filter past level 0, which reading part of a flat segment would bring down to the rows' own
bytes.

## 15. Order of work

| phase | primitives | delivers |
|---|---|---|
| 0 | the engine's float sum ([states](#75-states)), [marks at the scale of a period](#53-marks-at-the-scale-of-a-period), [what every dataset gains](#55-what-every-dataset-gains), a put's bound ([writing the object of a period](#52-writing-the-object-of-a-period)) | float sums past 2^36 values, mark limits as shares and the erasure delay, the trigger that merges small neighbours and the cap at 64 MiB, a put's bound with the store's choice of path, on every dataset |
| 1 | [a compaction style by periods](#51-a-compaction-style-by-periods), [writing the object of a period](#52-writing-the-object-of-a-period), [expiry without opening objects](#122-expiry-without-opening-objects) | fresh levels, the late level, seals, chunks sized from the footer's cap and ending at slices, the slice directory, the footer's length in the leaf entry, expiry that reads nothing |
| 2 | [states](#75-states) | exact merges of counts, sums and extremes, distributions, distinct sketches, each a versioned kind |
| 3 | [sorted time slices](#6-sorted-time-slices) | slices sorted by the writer and by compaction under one order per object, the slice table, the dataset's slice order, the bench of bytes against rows in time order |
| 4 | [the synopsis](#7-the-synopsis), [reading the synopsis part without the data](#8-reading-the-synopsis-part-without-the-data) | run statistics, coarse tables, value counts sorted by value with fence pages, postings, the synopsis part, its table of contents |
| 5 | [answering a question](#9-answering-a-question), [deletes](#10-deletes) | the walk of runs, edges, exclusions, newest first, tables corrected by the marked rows |
| 6 | [the synopsis cache](#11-the-synopsis-cache) | warm questions without the store |
| 7 | [needles](#95-needles) | declared identifiers with a sorted run outside the index budget, then the hashed index if a bench holds it |
| 8 | [partition columns](#121-partition-columns), [late rows](#123-late-rows), [columns known only at run time](#13-columns-known-only-at-run-time) | retention per row, rows later than the horizon, the tool path |

Each phase ends with its documentation and its ratchets, and each primitive is judged on a targeted
bench of what it changes, not on a full run. Phase 0 serves every dataset and comes first: the sum's
limit already throws on scans of rows, and phase 1 needs the marks, the put's bound and the trigger.

## 16. How it is held

| promise | gate |
|---|---|
| runs count exactly | at every version of the interleaving fuzzer, every count walked through runs, minus the marks, equals the count of the live rows, and every coarse table equals the merge of its slices' runs |
| the same bits by every path | the query matrix with runs and synopses on and off, at degrees 1 to the machine's, over objects of one slice, several slices and several periods, before and after compaction |
| slices stay sorted | sealing refuses an object whose slices are out of order or unsorted, and a fuzzer of appends, late rows, fresh merges and seals seals every object it makes |
| one round trip per object cold for whole periods, two for a shorter window, none warm | ratchets on a counting store with latency |
| sums hold any window | a sum past 2^36 values gives the bits of its exact total rounded, whatever the order of its merges, and throws only past its capacity. A source that proves its rows within 2^36 keeps the 32-byte state, and the grouped float sum's bench reads the same times |
| the life of a row | the rewrites per row and the objects per question on the bench of appends in order with late rows, as ratchets, and no seal abandoned by a late row in the fuzzer |
| deletes at the scale of a period | a hundred scattered rows in a period's object rewrite nothing, and a purge runs once per erasure delay |
| selective reads | the bytes of a needle's rows and of an edge, no more than in objects of 4 MiB, on a counting store |
| large objects stream on every store | a put of 200 GB under each store profile's limits, R2 and Scaleway included, against a counting store |
| needles | the round trips per object, cold and warm, and the bytes of each identifier's index, as ratchets |
| requests a second | the bench's load against each store's limit, with the cache's hit rate |
| the synopsis stays within its budget | the bytes of every part against the budget, and the verdicts recorded |
| the writer's added cost, and the bytes saved | ratchets on the write bench, the share of shredding included, and an append's throughput unchanged by it ([21-variant-columns.md](21-variant-columns.md#11-how-it-is-held)) |
| large objects in bounded memory | the governor's peak while a period is written and a slice sorted |
| expiry and late rows | requests and bytes rewritten, as ratchets |
| no allocation per batch | the allocation ratchets, extended to the walk of runs and the new kernels |
| Rust reads every object | objects of sorted slices with synopses in the cross-check |

## 17. Documentation to change, and what to clean up

| primitive | documents |
|---|---|
| the style by periods, objects sized by volume, retention, late rows | [13-dataset.md](13-dataset.md) (levels and their price, the style by periods, the late level, partition columns, expiry, the footer's length in the leaf entry, the mark limits and the erasure delay, the alternatives table, commit format 4), [15-compaction.md](15-compaction.md) (fresh merges, seals, footprints, the trigger for small neighbours, the cap), [19-object-storage.md](19-object-storage.md) (a dataset's object streamed, the put's bound and the store's choice of path), `dataset-maintenance.md`, a guide page on time-ordered datasets with its sample |
| the engine's float sum | [16-queries.md](16-queries.md) (the high parts and the capacity, the widened integer sum), `aggregates.md` |
| needles | [10-indexes.md](10-indexes.md) (identifiers, their sorted runs outside the budget, the hashed index if taken), the guide's `indexes.md` |
| sorted slices | [11-write-strategy.md](11-write-strategy.md) (the writer sorts a slice), [12-index-reads.md](12-index-reads.md) (the slice order as a source of order for the key cursor), [13-dataset.md](13-dataset.md) (an object's order, sealing, "nothing ever sorts" no longer true), [16-queries.md](16-queries.md) (the group by that streams, at slice grain) |
| states and the synopsis | [10-indexes.md](10-indexes.md) (the synopsis's kinds in the index directory, its budget), [16-queries.md](16-queries.md) (states, distributions, the approximate aggregates leave "Not in it, on purpose"), a guide page on the synopsis with its sample, `aggregates.md` |
| reading and answering | [12-index-reads.md](12-index-reads.md) (answers from runs and synopses), [16-queries.md](16-queries.md) (the plan, the walk of runs), [01-scope.md](01-scope.md), `queries.md`, `observability.md` |
| the synopsis cache | [19-object-storage.md](19-object-storage.md), [09-contracts.md](09-contracts.md), a guide page with its sample |
| columns known at run time | [16-queries.md](16-queries.md) (the tool path), [10-indexes.md](10-indexes.md) (the dataset's `Auto`), `untyped-files.md` |
| every primitive | [README.md](README.md), `PublicSurface.txt`, the XML documentation, `bench/README.md`, `tests/.impact/map.txt` regenerated |

What to clean up as primitives land: the rows of "Not in it, on purpose" in the queries design that
they deliver, the sentence of the dataset design that says nothing ever sorts level 0's rows once
slices are sorted on its exit, the limit of 2^36 values in the sum's documentation and in
`aggregates.md`, the sentence that caps objects at 4 MiB, and this document, whose sections move into
the documents they extend until only its decisions and alternatives remain.

## 18. Decisions to take

| | question | proposed | the other way |
|---|---|---|---|
| 1 | where the counts of combinations live | in the rows' own order, sorted inside each slice | in a synopsis table beside rows kept in time order ([alternatives](#19-alternatives-weighed)) |
| 2 | the order of the synopsis columns | one per object, measured on its rows and recorded in its slice table, decided on 2026-10-18 ([the order with variant columns](21-variant-columns.md#62-the-order-inside-a-slice)) | measured per slice, which breaks the sorted merges of coarse tables and of partial slices, or per dataset |
| 3 | the slice's target | from the cost model of edges against runs | a fixed number of rows |
| 4 | the depth of the synopsis part | where the budget stops | every synopsis column |
| 5 | tables over subsets of columns, against the overhead of untouched shallow columns | none by default | admitted by their benefit per byte, as every other table |
| 6 | edges | exact by default, their rows stated by the plan | moved to slice boundaries by default, the window applied returned with the answer |
| 7 | the synopsis part's table of contents | in the object's leaf entry with the slice directory's region, so one round trip per object for whole periods and two for a shorter window | read from the object's index directory, one round trip more |
| 8 | deletes | tables corrected by the marked rows' contributions, computed at read | deltas written beside the vector, or the runs of every marked bucket |
| 9 | the lateness horizon | declared by the application, checked by the dataset's counters | chosen by the dataset from the lateness it measures |
| 10 | rows later than the horizon | the late level beside their sealed period, sealed again when it pays | a partition of their own, which loses their retention, refused, or a rewrite of their period at once |
| 11 | retention per row | partition columns in one dataset | one dataset per retention, and a scan over several datasets |
| 12 | the budget | a share of the object's bytes, as for indexes | a number of bytes per object |
| 13 | the weight of each question in the benefit of a table | all alike, since none is known | weighed by the questions the dataset's counters have seen |
| 14 | format | commit format 4 for the style, partition columns, the table of contents and the footer's length, refused by older libraries | fields older libraries skip |
| 15 | the life of a row | a compaction style by periods, three or four rewrites per row, decided on 2026-10-18 | concatenations of time-adjacent objects, or the leveled style with a larger cap |
| 16 | writing large objects | a bound on a put's length and the spool, the store choosing by its profile and the object's size, decided on 2026-10-18 | the spool alone, or the bound alone |
| 17 | the size of a chunk in a period | from the footer's cap, chunks ending at slices | 16 MB of the widest column |
| 18 | reading part of a flat segment | later, after a bench on needles, edges and rows to show | now, for every column |
| 19 | needles | identifiers declared by the application, decided on 2026-10-18, with a sorted run outside the index budget, then a hashed index from value to block if a bench holds it | Bloom filter trees, or a sorted run inside the budget |
| 20 | physical erasure | a delay the application declares, decided on 2026-10-18, one purge per object and delay | a purge triggered by the vector's bytes |
| 21 | the float sum's state | 32 bytes where the source proves its rows within 2^36, and a wide state of 40 bytes with its high parts in a fifth word elsewhere and for persisted states, decided on 2026-10-18 after the bench measured 40 bytes on every group at 3.5 % to 15 % | 40 bytes for every sum, which every group by of floats would pay |
| 22 | the size of objects for every dataset | 64 MiB at the deep levels, then 256 MiB once the leaf entry records the footer's length, the default set by the churn bench | 4 MiB |
| 23 | the grid of distributions | DDSketch at 1 % | a decimal log-linear grid, exact at every threshold of three significant digits, with eight times the buckets |
| 24 | the order of the fresh levels' rows | sorted slices, as in periods | time order with a synopsis beside them, which keeps newest first and the key cursor cheap where they matter most (hypothesis) |
| 25 | the placement of a period's chunks | interleaved by chunk, as today | a range of part numbers per column while streaming (hypothesis) |
| 26 | a page's questions | answered one by one | answered together, walking runs once (hypothesis) |

## 19. Alternatives weighed

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
| small objects, 4 MiB | cheap rewrites | a million objects per 30 days, a million probes per needle, and opens that add a third to the requests of a scan. With marks, a delete no longer needs objects small ([what every dataset gains](#55-what-every-dataset-gains)) |
| concatenating time-adjacent objects into periods | slices copied, nothing re-encoded | a copy of bytes needs chunks at their final size, is impossible in an encrypted dataset, where an epoch's key derives from its object ([18-encryption.md, Frames and nonces](18-encryption.md#33-frames-and-nonces)), and OVHcloud lists no `UploadPartCopy`. The copy on the service was built and removed for files ([19-object-storage.md, Appends](19-object-storage.md#114-appends)). A seal writes the period once, in one pass |
| a partition of their own for rows later than the horizon | periods left untouched | the rows lose their retention, and the partition's small objects never grow |
| chunks of 16 MB of the widest column | a footer of a few megabytes | every selective read fetches sixteen times what it fetches today, where the footer's cap needs about three times at 20 columns |
| Bloom filters in tree pages, or a global secondary index, for needles | needles without a probe per object | a union of filters saturates past a few hundred objects, and a global index is rewritten by every compaction ([13-dataset.md](13-dataset.md#66-order-beyond-the-lookup)). With periods, a needle probes tens of objects |
| Bloom filter trees for identifiers | an index inside the budget | no upper node survives 4 × 10⁹ values in an object, so a probe reads a whole level of filters, about 5 GB a day |
| a float sum's state of 32 bytes with a wide part kept aside | the state's size unchanged | the ops that update a state receive no group number, so every slot, the merge of lanes, the core and the spill would have to pass it |
| an eviction policy taken from the literature | no bench | the comparisons disagree once sizes count, so the traces decide |

## 20. Sources

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
