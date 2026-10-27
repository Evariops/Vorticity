# Laying out a dataset

How a dataset orders and groups its rows decides what each question reads. Declare it when you create the dataset:
a partition, a sort key, and how much of the key each object indexes.

```csharp
DatasetOptions options = new DatasetOptions
{
    Layout = new DatasetLayout
    {
        Partition = LayoutValue.Column(nameof(ServiceCall.At)).Truncate(CalendarUnit.Day),
        SortKey = [LayoutValue.Column(nameof(ServiceCall.Service)), LayoutValue.Column(nameof(ServiceCall.At))],
        IndexedPrefix = 1,
    },
};
await using VortexDataset dataset = await VortexDataset.CreateAsync(store, ServiceCall.Schema, options);
```

```
created: partition At.Truncate(Day), key Service, At, indexed prefix 1, Tiered
appended 768000 calls in 192 appends; 62 Tier jobs rewrote 2048000 rows, 1 Consolidation jobs rewrote 384000 rows; level 0: 3 objects, level 1: 1 objects
  2026-10-02T00:00:00Z: 3 objects of its size classes, 2353708 bytes, not calm yet
two hours later: 1 Consolidation jobs rewrote 384000 rows; level 1: 2 objects
plan: rows, the 4.3 % of its rows its key's index keeps, 215933 bytes in 6 requests
  cost: 6 requests, 215933 bytes, 30 ms cold
svc-07 on the second day: 9566 calls; 9 reads, 298771 bytes of 4835040
  counted: 9566 calls; 4 reads, 76929 bytes
advice: Status (14187063 bytes spared); index the first 1
```

The sample appends two days of calls to forty services, 4 000 calls a quarter of an hour, then asks for one service
over one day. Of the 4.8 MB the dataset holds, the question reads its day's object alone, and of that object the
granules where the service's rows lie: 299 KB for the rows, 77 KB for their count. The layout is the dataset's, carried by every commit: a handle opened later
follows it whatever its own options say. Datasets are experimental (VX0001, see [datasets.md](datasets.md)).

## The partition

`Partition` is a value worked out on every row, and no object ever holds rows of two values of it, fresh objects
included. It is a column, `LayoutValue.Column`, or a function of one: `Truncate(CalendarUnit.Day)` for the start of an
instant's day, `Bucket(width)` for an instant's, an integer's or a float's bucket. The library compares the values and
never interprets them: a day, a tenant or a range of identifiers are all the same to it.

The partition carries three things:

* the lifecycle: an expiry by the partition's column drops whole objects without reading them
  (`ExpireAsync`, [dataset-maintenance.md](dataset-maintenance.md#expiry));
* the coarse pruning: a filter on the partition's column passes over the other partitions' objects, unopened;
* the scope of the background work: merges and consolidations stay within a partition.

It does not order the rows: the sort key does. With no partition, the dataset is one partition.

## The sort key, and its indexed prefix

`SortKey` is a sequence of one to sixteen components, and every object a writer adds is sorted by it, appended
objects included: an append splits its rows by partition, sorts each part in memory and writes one sorted object per
partition. A component is a column, a non-decreasing function of a column (`Truncate`, `Bucket`), a field of a struct
column, or a path of a variant column read in a class, `LayoutValue.Path("Attributes", "http.route",
VariantClass.String)`. Nulls sort after every value, and rows of equal keys keep the order they came in. With no key,
the rows keep the order they arrive in.

Each object indexes the first key of each of its granules, the runs of 8 192 rows its columns' statistics and reads
are cut in. `IndexedPrefix` says how many of the key's components that index keeps: those whose values group rows
(a service, a host), not those that vary inside a granule (the instant), which the granules' zone maps bound anyway.
By default it keeps every component. A question whose filter bounds the indexed prefix reads the index first, then
the granules it keeps alone.

A shape that suits event data: the day as the partition, then the entity, a coarse grain of time, the columns of few
values, and the instant:

```csharp
Partition = LayoutValue.Column("At").Truncate(CalendarUnit.Day),
SortKey =
[
    LayoutValue.Column("Service"),
    LayoutValue.Column("Host"),
    LayoutValue.Column("At").Truncate(CalendarUnit.Hour),
    LayoutValue.Column("Status"),
    LayoutValue.Path("Attributes", "http.route", VariantClass.String),
    LayoutValue.Column("At"),
],
IndexedPrefix = 3,
```

Within an hour of a host the rows run by status, then by route: those columns become runs, and every question that
filters or groups them reads tens of times fewer bytes for them, the engine folding a run as one weighted row. This is
not precomputation; it is the order of the rows.

## How the objects are organized

Appends land as small sorted objects. Within each partition, objects of one size class merge: when a partition holds
`MergeFanout` objects of a class, 8 by default, a job merges them into one object of the class above. A row is
rewritten once a class, and a partition of a day stays within a few dozen objects while it receives rows.

When a partition has received no row for `CalmAfter`, an hour by default, its next job **consolidates** it: it
rewrites the partition into objects disjoint by key, each up to `ObjectBytes`, 1 GiB by default, cutting no key. An
entity's rows then lie in one object of the partition. `CalmAfter = null` consolidates only when you call
`ConsolidateAsync`. The clock is `DatasetOptions.TimeProvider`'s; the sample sets it to the time of its data, so its
two days become calm as their rows stop coming, though it writes them in seconds.

Rows that come late for a consolidated partition land as small objects of that partition and wait in its size
classes; they are merged into the consolidated objects they overlap once they weigh `1 / LateShare` of them, an eighth
by default, which bounds what a late row costs at some nine writes.

A dataset with a sort key and no partition is one partition that never becomes calm. Its jobs keep levels of objects
disjoint by key instead (`LayoutOrganization.Leveled`), the organization its declaration calls for unless
`Organization` says otherwise. The jobs run as described in [dataset-maintenance.md](dataset-maintenance.md):
`CompactAsync` one at a time, or `RunCompactionAsync` in the background.

## What a question reads

A question walks the dataset's tree of objects, passing over the partitions its filter excludes and the objects whose
key range it refutes, all without a read. Of an object it reads, it reads the open's tail first, the last 64 KiB, which
holds the object's directories and, unless the object is large, its key's index. When the filter bounds the indexed
prefix, the index says which granules may hold a row, and the question reads those granules of the columns it needs,
each a range of the column's chunk, or the chunk whole where the granules hold most of it or where a request costs
more than the bytes it would spare at the store's profile.

`ExplainAsync(analyze: true)` opens the objects it would read as rows, reads their key's index, and prices the part of
each the index keeps; `ExplainAsync()` reads nothing and prices what the handle already knows. The sample's plan says
it before the read, and a new handle's cold read then shows what the store saw.

Counts are answered the same way: a count of one service over twelve hours reads the instant's granules where the
service's rows lie, not the instant's column.

## Which layout

`RecommendLayoutAsync()` says what the questions a handle asked call for: the columns and variant keys their filters
read, with the bytes an order by each would have spared the objects they read as rows, in the order a key would take
them, the fewest distinct values a partition first, and where the indexed prefix would stop. The sample reads the
failed calls themselves three times, rows the objects' counts do not answer: the advice leads with `Status`. It is
advice for the next dataset, never applied; the partition stays your choice, the library knowing nothing of which
values close.

## What it costs

* Sorting an append costs some 50 to 100 ns a row on one core, which an append pays before it writes.
* Each tier merge rewrites its rows once, and a consolidation rewrites the partition once more. In the sample, appends
  of 4 000 rows, the merges rewrote each row 2.7 times on its way through the size classes, and the consolidations
  once: 3.7 rewrites a row besides its append. Larger appends start higher in the classes and pay fewer merges.
* An append that touches more than `DatasetOptions.MaxPartitionsPerAppend` partitions, 100 by default, is refused
  before anything is written.

## Watch out

* The partition, the sort key, its prefix and the organization are fixed when the dataset is created, and every
  commit carries them. `ObjectBytes`, `MergeFanout`, `CalmAfter` and `LateShare` are policy, which a later commit may
  change.
* A partition of very few rows a day makes many small objects; a partition no clock ever calms is never
  consolidated. `PlanCompactionAsync()` reports each partition's state (`PartitionCompaction`), and flags one that
  never calms (`NeverCalm`) or holds too little for its objects (`TooFine`).
* A key component on a nullable column is allowed: its nulls sort last, and the index bounds them as any value.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- dataset-layout
```
