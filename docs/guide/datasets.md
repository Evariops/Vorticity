# Datasets

A dataset keeps a growing table as a versioned set of Vortex files in an object store. You read it
with the same scans as a single file.

```csharp
await using IObjectStore store = new FileObjectStore(Demo.Path("dataset"));
await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions
{
    Layout = new DatasetLayout { SortKey = [LayoutValue.Column(Reading.ColumnNames.Day)] },
});

await using (ObjectDraft draft = dataset.StartObject())
{
    await draft.Writer.WriteAsync<Reading>(Days(0, 50));
    ulong version = await dataset.AppendAsync(draft);
}
```

```
created: version 1, 0 rows, sorted by Day
appended: version 2, 50000 rows in 1 object
```

Datasets live in `Vorticity.Dataset`, a separate package, and it is experimental. The assembly is
marked `[Experimental("VX0001")]`, so the compiler reports VX0001 as an error wherever your code uses
one of its types, until you opt in. You can opt in once for the whole project with
`<NoWarn>$(NoWarn);VX0001</NoWarn>`, or mark your own assembly or type `[Experimental]`, which
allows its uses and passes the warning on to its callers. The samples do the latter.

The warning means what it says: the dataset's commit format and API may change between releases. A
release reads the formats of the releases before it, and an older release refuses a newer format
rather than misread it. Version 0.2.0 reads format 1 and refuses the format 2 written today. The
files inside a dataset are ordinary Vortex files, but the tree of commits over them is this
project's own format, and no other Vortex implementation reads it. A single file needs none of this.

## What a dataset is

A dataset is a set of immutable Vortex files, the data objects, plus one commit object per version
that lists the objects the version holds. Every change is a commit and every commit is a new
version, so nothing is ever overwritten. A commit is a conditional create in the store, which means
two writers need no lock. The one that loses the race applies its change again on top of the
winner's version, up to `DatasetOptions.MaxAttempts` times (8 by default).

### The layout

`DatasetOptions.Layout` is the decision that matters most, and [dataset-layout.md](dataset-layout.md) is about it.
Its `SortKey` orders the rows of every object, appended objects included, and each object indexes the first key of
each of its granules, the runs of 8 192 rows its reads are cut in: a question bounded on the key's first components
reads the granules that may hold its rows alone, the way ordered rows let a scan skip blocks. Its `Partition`, a
column or a function of one, `Truncate(CalendarUnit.Day)` or `Bucket(width)`, keeps the rows of each value in objects
of their own: a question bounded on it passes over the other partitions unopened, and an expiry by it drops whole
objects. Within a partition, objects of one size class merge, and a partition that receives no more rows is
consolidated into objects disjoint by key. The layout is fixed when the dataset is created, every commit carries it,
and every writer follows it.

```csharp
await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
{
    Layout = new DatasetLayout
    {
        Partition = LayoutValue.Column(nameof(Request.At)).Truncate(CalendarUnit.Day),
        SortKey = [LayoutValue.Column(nameof(Request.At))],
    },
    Identifiers = [nameof(Request.RequestId)],
});
```

How to choose, from the questions the application asks:

| the questions mostly | partition by | sort by |
|---|---|---|
| select one value of a column: a tenant, a service, a source | nothing, or that column when its values close | that column, then the instant |
| ask windows of time: the last hour, the last day | the unit the windows and the expiry fall on, a `Truncate` of the instant | the instant |
| ask a column's values within windows of time | the time unit, a `Truncate` of the instant | the column, then the instant, the index on the column alone (`IndexedPrefix = 1`) |

A filter on the sort key's first components reads the granules of their values alone; any other column is still
answered by its counts, its sheets and where its values live, at a cost the plan states. `SummaryColumns` picks which
columns each object summarises in its entry, the first 32 by default, so that a question passes over an object
without opening it.

Which values to order the rows by is the application's choice, and the questions it asks tell. A handle keeps count:
`RecommendLayoutAsync()` takes the columns and variant keys its questions' filters read, each with the bytes an order
by it would have spared the objects it read as rows, orders them as a key would take them, the fewest distinct values
a partition first, and says where the indexed prefix would stop. The telemetry counts the same bytes
(`vortex.dataset.layout.avoidable_bytes`). It is advice for the next dataset, never applied, and the partition stays
the application's choice.

```csharp
LayoutAdvice advice = await dataset.RecommendLayoutAsync();
foreach (ComponentAdvice component in advice.Components)
{
    Console.WriteLine($"{component.Path}: {component.DistinctPerPartition:N0} values a partition, {component.BytesAvoided:N0} bytes spared");
}

Console.WriteLine($"index the first {advice.IndexedPrefix} component(s)");
```

## Adding rows

`StartObject()` returns an `ObjectDraft`, which is a key in the store plus a `VortexFileWriter` with
the dataset's schema. You write rows to it as you would to any file. `AppendAsync(draft)` completes
the writer, puts the object in the store and commits it. Disposing a draft that was never committed
abandons it, and nothing reaches the store. `AppendAsync(batches)` does the same from an
`IAsyncEnumerable<RecordBatch>`, such as another scan's `ToBatchesAsync()`.

A file that is already in the store joins for the price of a commit, without being rewritten:

```csharp
await using (FileStream stream = System.IO.File.OpenRead(local))
{
    await store.PutIfAbsentAsync("imports/days-100-119.vortex", PipeReader.Create(stream), stream.Length, CancellationToken.None);
}

ulong imported = await dataset.ImportAsync("imports/days-100-119.vortex");
```

```
imported: version 4, 120000 rows, 3 objects, lag 0
  data/b6b4bdbd6ec244b4a134b5fa6e9472d2.vortex: level 0, rows 0 to 50000, 80510 bytes
  data/6f3adc98a2c24449aa225245f3c30c3a.vortex: level 0, rows 50000 to 100000, 80510 bytes
  imports/days-100-119.vortex: level 0, rows 100000 to 120000, 37948 bytes
```

`ImportAsync` takes a key in the store, not a path on disk, so putting the bytes there is up to you.
The file's columns must read as the dataset's, either the current ones or those of an earlier
schema of the dataset (see [Changing the schema](#changing-the-schema)). `ListObjectsAsync()` lists the
objects of the version from the commit tree, without opening any object.

## Reading

`dataset.Scan<Reading>()` is the same `Scan<Reading>` as on a file, over every object of the
version, and `dataset.Scan(columns)` is the tool scan. Everything in the guide's read pages applies:

```csharp
Scan<Reading> recent = dataset.Scan<Reading>().Where(r => r.Day >= 100);
ScanPlan plan = await dataset.Scan<Reading>().Where(r => r.Day >= 100).ExplainAsync(analyze: true);
```

```
Day >= 100: 20000 rows; plan: 3 of 16 blocks, object summaries pruned 13
may Day be 500? False; may Day be 75? True
the first batch of day 110 starts at dataset row 108192
key cursor: seek 75 found True, key 75 at row 75000; 1000 entries
walking down: last key 119 at row 119999; the key before it 118, its last entry at row 118999
```

### Positions, cursors and lanes

Row positions are the dataset's. A batch's `StartRow`, `Rows(…)` and a key cursor's `Row` count the
version's rows object after object, in the order a scan delivers them. A key cursor over the
sort key merges the cursors of the objects, and its `CountAtKeyAsync` is the number of entries
under the current key, here the thousand rows of day 75. It walks both ways like a file's cursor
(`SeekLastAsync`, `PreviousAsync`, `PreviousKeyAsync`, `AtOrBefore`, `Before`), and a step against the
direction of the previous one seeks every object again at the current entry. Walking down, an object
above level 0 opens only once the walk could reach its keys, while the level 0 objects all open at
the seek since nothing bounds them from above. See [keys-in-order.md](keys-in-order.md).

A scan reads the objects one after another in the tree's order. Once the consumer reads past the
first batch, the next object is opened and its first batch read and decoded while the current one
is still being read. At most two objects are open at a time, and a scan that stops at its first
batch opens only one. `ScanOptions.Prefetch` controls this, 1 by default, and 0 opens each object
only when the scan reaches it.

A group by at a degree above one reads the objects side by side, each one a range in the lanes'
queue, with large objects cut where the chunks of the columns it reads start together. On the first
component of the sort key, or a function of it, a group by under a take streams: the dataset then
delivers its rows in key order, one object after the other, and the take stops the read early. Without
a take, the objects are read side by side. See [aggregates.md](aggregates.md).

### How a question finds its rows

Before it reads an object, a question asks what the object says of itself, cheapest first:

| what the object keeps | what it spares | measured |
|---|---|---|
| summaries in its entry: each column's least, greatest and nulls | objects whose bounds refute the filter, never opened; `MayMatch` reads nothing | days 100 and on: two objects of three never opened, 13 blocks of 16 |
| the index of its partition, once consolidated: where each declared identifier's rows lie | a needle finds its object and its granules in a few dependent steps, opening no other | one request id among 600 000 rows: 5 or 6 dependent steps, under 1.1 MB |
| the first key of each granule, the sparse index of its sort key's prefix | a filter on the key's first components reads the granules of its values alone | one service of a day: 4.3 % of the object's rows, 299 KB read of 2.4 MB |
| a sheet of each column: distinct values, frequent ones, a filter of them | an object that lacks every value a filter asks, for a few kilobytes | |
| where each value lives: the granules it appears in | of the others, the granules of the values asked alone, on any column | one endpoint of thirty sources: 788 KB in 18 requests, against 2.9 MB in 51 |
| nothing for a rare, scattered value | read in two passes: the filter's columns, then the other columns for the rows kept | thirty rows of 100 000: 4.4 MB in 52 requests, against 30.3 MB in 198 |

Pruning starts with the object summaries. The two objects whose days all fall before 100 are never
opened, and their 13 blocks are pruned without a read. `MayMatch` answers from the summaries the
handle already holds (the version's header and the pages it kept from earlier versions), so it reads
nothing, and `false` is a proof.

An object reads its sort key's index from the last bytes of the object, which its open reads anyway, and a question
bounded on the key's first components then reads the granules that may hold its rows: of each column it needs, a range
of the column's chunk, or the chunk whole when those granules hold most of it or when a request at the store's profile
costs more than the bytes it would spare. The [layout sample](dataset-layout.md) asks for one service of a day sorted by
service: the plan, analyzed, says it will read the 4.3 % of the day's rows the index keeps, and a cold read takes
299 KB of the day's 2.4 MB, its count 77 KB.

An object a job writes, of a mebibyte or more, also keeps a sheet of each column: its distinct
values, its most frequent ones, and, when its values repeat, a filter of them. A question that asks a
column for a value, `r.Route == "/checkout"` or a set of them, reads the sheets of such an object
before the object, and passes over an object whose filter lacks every value asked: one request a few
kilobytes long instead of the object's reads. A smaller object is read in the request its sheets
would take, and keeps none.

Such an object also keeps, for each column whose values repeat, the granules each value lives in. A
question that asks those columns for values reads these indexes in place of the sheets, passes over an
object that lacks the values, and reads only the granules of the others where the values live. That
holds on any column, not only the key's: a filter on an endpoint, which only its service's rows hold,
reads that service's granules, where the same filter written as a prefix, which no index answers, reads
every row. A value that lives in more than three granules in four is read with the whole object. A
value that is rare but scattered, which no index places, is read in two passes when the sheets say so:
the filter's columns first, then the other columns only for the rows it kept, from the granules that
hold them.

Three days of a request every five seconds, partitioned by day and sorted by the instant, by service
over the first two days, then over the last quarter of an hour of the second (the `time-windows` sample):

```
51840 requests over three days in 3 objects
two days by service: api 6944, auth 7000, billing 6867, cart 6870, search 6879; 34560 rows read
the last quarter of an hour of the second day: 180 requests; blocks decoded 1, pruned 0
```

Its appends came in order, so each day's merged objects hold disjoint ranges of instants. The day's
consolidation merges such neighbours, each far under the object size, into one object: three days in
three objects of three granules each, the last quarter of an hour read from the granule that holds it.

A key of a variant column is asked as a column is, read strictly as an integer, a double, a text or a
boolean. An object that shreds the key reads its typed column, and one that leaves it in the residual
parses the residual of the chunks that hold it:

```csharp
var routes = dataset.Scan<LogRow>()
    .Where(r => r.Attributes["http.response.status_code"].AsInt64() >= 500)
    .GroupBy(r => r.Attributes["http.route"].AsString())
    .Select(g => (g.Key, g.Count()));
```

`AsDouble()` reads floats as well as doubles, the floats from the field's value of the rows that hold
one, and `ToInt64()` converts every kind of value. [variants.md](variants.md) says how a dataset keeps
a variant column's keys apart.

### How an aggregation is answered

An aggregation answers each object, or each rollup standing for the objects it covers, by the path
of least weight among those it can prove give the rows' answer, to the bit: the bytes and requests
each reads and what its values cost the processor, at the dataset's cost profile. `ExplainAsync` says
unit by unit which path it takes, what it weighs, and why each other path lost (`ScanPlan.Units`),
and `ScanOptions.UseSynopses = false` reads every object's rows.

| path | which objects | measured |
|---|---|---|
| the column's counts | a question on one column and the numbers, its other keys constant over the object, its filter asking that column for values or proven over every row | a count by service: a request an object, a few kilobytes |
| the counts less one value | a filter that leaves one value of a declared identifier out, `r.RequestId != id` | no object holding it: 127 KB where the rows take 791 KB |
| the counts less the deleted rows | an object a delete marked, under counts, sums, means or distinct values | 332 bytes where the rows take 540 KB |
| the granules' statistics | a question the counts do not answer, granule by granule, the granules that hold one group | errors by service: 247 KB where the rows take 898 KB |
| a rollup's totals | every object under a rollup, over a long range | a count by service: 17 requests where the objects' counts take 26 |
| the filter's pages | a filter evaluated on rows, asked before | 3.4 MB where the first ask read 23.2 MB |
| the rows | any other object | |

**Counts.** An object a job writes, of a mebibyte or more, keeps for each column whose values repeat
its counts: the rows of each value, each number column's exact sum, least and greatest, and for a
column of at most 1 000 values a quantile sketch of each number column. A question on one column and
the numbers reads an object's counts in one request instead of its rows when its other keys are values
the object holds constant and its filter asks that column for values or is true for every row of the
object, as its summaries prove. Under a partition by hour each object holds one hour, so a question by
service and hour is such a question, and only the objects at the window's ends, which hold rows outside
it, are read as rows. The objects still in their appends keep no counts and are read as rows.

```csharp
var hourly = dataset.Scan<Request>()
    .Where(r => r.At >= from && r.At < to)
    .GroupBy(r => (r.Service, r.At.Truncate(CalendarUnit.Hour)))
    .Select(g => (g.Key.Service, g.Key.Item2, g.Count(), g.Sum(r => r.Bytes), g.Quantile(r => r.Bytes, 0.99)));
```

A filter that leaves one value of a declared identifier out, `r.RequestId != id`, keeps the counts: an
object's counts answer, less those of the rows that hold the value, which the identifier's postings find
without reading the object's other rows. An object that does not hold the value reads its counts and
the postings alone. That holds for counts, sums, means and distinct values, when the identifier column
holds no null; a minimum, a maximum or a quantile reads the rows. Rows a delete marked are taken out the
same way (see [Deleting and updating rows](#deleting-and-updating-rows)).

**Granules.** An object whose counts do not answer may still be answered granule by granule. Such an
object keeps, for each column, its statistics over its granules: each granule's values and their rows,
and each number's count, sum and extremes. A granule whose columns of the question hold one value each,
its filter proven over its rows, gives its counts and measures; a granule where one column varies gives
the count of each of its values, under a question of counts alone. A distinct count, exact or
approximate, is such a question: a granule gives each group it holds the values it lists of the distinct
column, the same values its rows would give. The other granules are read as rows,
those a delete marked a row of among them. Under a key that leads with the service, errors by service
read the statistics of the service and the status, `r.Status >= 500` judged value by value: 247 KB where
the rows take 898 KB. By the hour as well, a granule answers when its instants fall in one hour, which a
key with the hour after the service, `At.Truncate(CalendarUnit.Hour)`, makes so: measures by service and
hour read 2.4 MB where the rows take 6.2 MB. A window whose edges cut granules reads those granules as
rows, and never more than the rows take. `ScanOptions.AlignWindow` widens the window to the bounds of the
objects its ends fall in, so that they are answered from their counts too: the answer is then the exact
answer of the window applied, which `ScanMetrics.Window` gives.

**Rollups.** Over a long range, an object's counts still cost a request an object. `RollUpAsync` sums
the counts of runs of about `DatasetOptions.RollupFanout` consecutive objects (64 by default) into
rollups, then as many rollups into one of the rank above, with no row read, and `RunCompactionAsync`
calls it once no job is due. A question whose every object under a rollup would answer from its counts
reads the rollup instead. On thirty days of 2 000 routes, an object a day, a count of every route reads
two rollups and the counts of the three objects no rollup covers yet: 7 requests and 324 254 bytes, where
the objects' counts take a request each. A commit that adds or takes away an object takes away the
rollups over it, which the next call sums again.

**Pages.** An aggregation whose filter is evaluated on an object's rows, a range no sheet or index
answers, also notes where in the object the filter kept rows; asked again, from the handle or one
opened later on the same `SynopsisCache`, it reads those parts of the object alone. The synopses are
designed in [22-on-the-fly-and-synopses.md](../design/22-on-the-fly-and-synopses.md).

### Long ranges, expiry and late rows

A page that shows a long window can show its newest part first. `AskByValuesAsync` cuts a range of the
partition's column a part a partition, asks your question over each part, several parts at once, and
delivers each part's answer, the last part first by default, once it and every part before it are in:

```csharp
await foreach (ValuePart<DateTimeOffset, List<ServiceTotals>> part in dataset.AskByValuesAsync(from, to, async (start, end, ct) =>
    await dataset.Scan<Request>().Where(r => r.At >= start && r.At < end)
        .GroupBy(r => r.Service).Select(g => (g.Key, g.Count(), g.Sum(r => r.Bytes)))
        .As<ServiceTotals>().ToRecordsAsync(ct).ToListAsync(ct)))
{
    Show(part.From, part.To, part.Answer, part.Covered);
}
```

```
part 10-02 to 10-03: 17280 requests
part 10-01 to 10-02: 17280 requests
```

Each part's answer is final for its values, so once a part arrives, the range from its start to the
range's end is answered, and `Covered` says which share of the range's values that is, 1 for the last
part. The newest part arrives as soon as its own objects are read, whatever the older parts still read,
and a consumer that stops, or cancels, cancels the questions asked ahead of it, which read no further.
Counts and sums over the parts add up, and so do minimums and maximums. A mean
over the whole range is the sum of the parts' sums over the sum of their counts. A value whose next
value no function tells, a text's or a float's, has no part to cut, and the call refuses it.

Rows kept for different lengths of time go in a dataset each, and so may rows of different tenants or
sources an application keeps apart: the library knows no tenant, and an application lays out its rows
as it chooses. Each dataset then expires on its own schedule, reading no data:

```csharp
DateTimeOffset week = DateTimeOffset.UtcNow.AddDays(-7);
DateTimeOffset quarter = DateTimeOffset.UtcNow.AddDays(-90);
await debug.ExpireAsync<Logged>(r => r.At < week);
await info.ExpireAsync<Logged>(r => r.At < quarter);
```

An expiry drops, in one commit, the objects its filter covers whole, by their summaries, and opens
none. Under a partition by day, a bound on the instant covers every day before the one it falls in,
so a row outlives the bound by a day at most. To drop rows exactly, delete them: `DeleteAsync` opens
the objects that straddle the bound. The sample keeps three days of debug lines, an hour an object,
and drops the first two:

```
debug lines expired before 10-03: 2880 then 960, 1920 dropped; info lines untouched: 1440
```

A row that comes late for a partition already consolidated is appended as any other, as a small object
of that partition. It waits in the partition's size classes, and is merged into the consolidated objects
it overlaps once the late rows weigh an eighth of them (`DatasetLayout.LateShare`), which bounds what a
late row costs at some nine writes. On 24 hours of requests at 20 a second from four writers, partitioned
by hour, a row in fifty a few seconds late and one in ten thousand up to an hour late, the size classes
rewrote a row 1.93 times and the consolidations 0.92 times (`vortex-queries --micro lifecycle`), and a
window of a day covers 40 objects.

### The local cache

The directories a question reads to find an object's rows can stay on the local disk, so that the next
question over the same objects asks the store nothing for them. Give the session a `SynopsisCache`, a
directory and the bytes it may hold:

```csharp
await using VortexSession session = VortexSession.Create(options =>
    options.SynopsisCache = new SynopsisCache("/var/cache/vorticity", capacityBytes: 20L << 30));
await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session });

// Optional: read the last month's directories before the first question needs them.
long warmed = await dataset.WarmAsync<Request>(r => r.At >= DateTimeOffset.UtcNow.AddDays(-30));
```

By default only what an object's table of contents declares enters the cache: the directories that
find its rows, of its chunks, its pages, its indexes and its variants' paths, never its data, along with
the filters' pages questions evaluated. A read with no open of an object, such as a
lookup of an identifier in a large one, needs nothing else before its data; a read that opens the
object reads its last 64 KiB from the store first. An object never changes, so an entry never goes
stale.

Given a quota of its own, the cache keeps the data a question reads of a plain dataset's objects too,
their footers included, so that the same question asked again, by this process or the next, reads no
data object from the store:

```csharp
options.SynopsisCache = new SynopsisCache("/var/cache/vorticity", capacityBytes: 20L << 30, dataCapacityBytes: 200L << 30);
```

The data keeps within its quota, the directories within theirs, so a sweep of a long window through
the data never pushes the directories out. Each data segment's file holds an XXH3-64 of its bytes,
which every read from the disk checks. An encrypted dataset's data never enters the cache, and a data
segment whose write would hold up the question, past the bytes waiting for their writes, is not kept.
Each region is checked against its checksum whenever it is read from the disk, and one that fails is
read again from the store. One process owns a directory at a time, and the entries outlive it: the
next session that opens the directory finds them. A second cache on the same directory, in the same
process or another, throws `InvalidOperationException`. Processes of one machine share a root instead:

```csharp
options.SynopsisCache = SynopsisCache.OpenShared("/var/cache/vorticity", capacityBytes: 64L * 1024 * 1024 * 1024);
```

Each takes a directory of its own under the root, `p0`, `p1` and on, with its own share of the disk,
and on a miss reads the region from the others' directories before it asks the store, without keeping
it again: a region the processes read lies once on the disk, unless two of them read it from the store
before either kept it ([several processes](../design/19-object-storage.md#106-several-processes-on-one-machine)).
A root is shared or owned, never both. Of an encrypted dataset, the cache keeps the
encrypted bytes the store holds, the frames around each region and the object's trailer, and decrypts
them at each read: no key and no plain byte reaches the disk, and a frame altered there is refused and
read again from the store. A frame is 64 KiB, so a small region costs the disk more than its own bytes.
Past its capacity, a region
read only once leaves before one read again and again, so a question over thirteen months passes
through without pushing out the directories of the last days. The cache reports what it does on the
library's meter, under `vortex.synopsis_cache.*`.

## Versions

```csharp
await using VortexDataset past = await VortexDataset.OpenAsync(store, version: 2);
```

```
after another append: the handle reads version 5, 130000 rows; a scan built before it counts 120000; another handle reads version 4 until it refreshes to 5
version 2 opened by its number: 50000 rows, 1 object
```

A handle is pinned to one version. It moves only when it commits or when you call `RefreshAsync`,
which takes a single round trip to the store when no commit or one came in since, and lands on a
version that was the latest at some moment during the call. A scan is pinned to the version its
handle held when the scan was built, so a commit in the middle of a query does not change the
answer. `Version`, `RowCount`, `ObjectCount` and `Lag` describe the
version the handle holds. `Lag` is how many objects level 0 holds beyond eight: under levels, what
compaction owes, which it brings back to 0; under tiers, where level 0 holds the size classes of every
partition still receiving rows, `PlanCompactionAsync()` says what each partition's merges owe
([dataset-maintenance.md](dataset-maintenance.md)).

`OpenAsync(store, version)` opens a version whose number you know, in one request, where opening the
latest takes a listing and a read. A writer can hand the number of the version it committed to its
readers, which then read exactly that version. Such a handle does not take its version for the
latest: its first refresh or commit lists the commits, and a commit from it applies its changes to
the latest version, as any commit does. A version superseded for longer than the retention window
may have lost its objects to vacuum, and then fails to open, or to read, with
`ObjectNotFoundException`, never with a part of its rows. So a version number handed around has to
be used well inside the window.

Most versions of a busy dataset change no row: a compaction, an index or a repack only moves them.
`LastDataChange` is the last version that changed rows, which an append, an import, a delete, an
update, a replacement of objects or a change of schema makes, and every other commit copies:

```csharp
ulong changed = dataset.LastDataChange;
await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 });
```

```
after a compaction: version 12, rows last changed at version 11, as before (11)
```

So a result worked out at version V still holds while `LastDataChange` is at most V, however many
compactions came since, and the version the handle reads is enough to tell. A version written by a
library that predates the field reads as its own version, which is all that can be said of it.

## Removing and replacing objects

```csharp
await using (ObjectDraft rewritten = dataset.StartObject())
{
    await rewritten.Writer.WriteAsync(dataset.Scan<Reading>()
        .Rows(RowRange.FromLength(first.FirstRow, first.Rows))
        .Where(r => r.Celsius.IsNotNull)
        .ToRecordsAsync());
    ReplaceResult replaced = await dataset.ReplaceObjectsAsync([first], [rewritten]);
}
```

```
replaced data/3ee81023c25245108e5b2cfcf0978769.vortex by the rows with a temperature: version 6, Applied, 129000 rows
removed the import: version 7, Applied, 109000 rows, 3 objects
removing it again: Abandoned, version 7
```

`ReplaceObjectsAsync(removed, added)` swaps objects in one commit. A reader sees either the old ones
or the new ones, never both and never neither. `RemoveObjectsAsync(objects)` is the same with nothing
added. Both take `DataObject`s from `ListObjectsAsync()`. The removed objects stay in the store, readable by the
versions that still name them, until vacuum deletes them. Removing an object the version no longer
holds returns `Abandoned`: no version is created, the result names the latest one, and any objects
a replace would have added are left for vacuum.

## Deleting and updating rows

```csharp
RowChangeResult deleted = await dataset.DeleteAsync<Reading>(r => r.Day < 10);

RowChangeResult updated = await dataset.UpdateAsync<Reading>(
    r => r.City == "Nice" & r.Day >= 60,
    r => r with { Celsius = r.Celsius + 1.0 });
```

```
deleted days 0 to 9: version 8, 9800 rows, 1 object(s) rewritten into 1, 0 marked, 99200 rows left
warmed Nice from day 60: version 9, 6251 rows changed, 2 object(s) in, 3 out
```

`DeleteAsync` removes the rows its filter is true for, in one commit. A row the filter is false or
unknown for (a comparison with a null, for instance) stays. `UpdateAsync` reads the matching rows as
records, passes each one through the lambda and writes the results into new level 0 objects, as an
append would, because a change may move a row's key. An update is a delete and an insert applied
together. Its record must have a member for every column. `DeleteAsync(VortexExpr)` is the delete
for a caller without a record type.

A delete usually marks rows instead of rewriting objects. Each object's entry in the dataset records
the positions of its deleted rows and every read steps over them, so deleting ten rows from a 4 MiB
object writes a few kilobytes of metadata and no data. Counts stay exact, and a scan of a marked
object reads the same bytes it would read from a rewritten one. The delete also counts the rows it
marks by the columns the object keeps counts of, so that a question of counts, sums, means or
distinct values still answers the object from its counts, less the marked rows'; a question of
minimums, maximums or quantiles reads its rows. A walk in key order pays a little
for the marks: a cursor over a level 0 object reads the keys of its deleted rows once to rank around
them, and a step that lands in a run of them skips past it.

An object is rewritten without the rows instead in two cases: when it is under a mebibyte, where a
rewrite is cheap and keeps the object exact, and when its marked rows would pass an eighth of its
rows or a kilobyte of positions. An object left with no rows is removed whole, and one whose
summaries refute the filter is not opened. In the result, `ObjectsMarked` counts the objects a change
marked, `ObjectsIn` and `ObjectsOut` those it rewrote, and `BytesIn` and `BytesOut` what the rewrites
read and wrote. `DataObject.DeletedRows` says how many marked rows an object holds. Compaction drops
them the next time it rewrites the object, either in a merge or on its own once the marks reach half
of those limits.

A reader sees the change whole or not at all. Rows appended by another writer while the change is
being worked out are not touched. If another writer rewrites an object the change read (a compaction,
for instance), the change is worked out again on the winning version, and `Attempts` says how many
times that took.

## Changing the schema

```csharp
[VortexRecord]
public partial record struct Observation(int Day, double? Celsius, string Town, double? Humidity);

ulong evolved = await dataset.EvolveSchemaAsync(Observation.Schema, new Dictionary<string, string> { ["Town"] = "City" });
await using (ObjectDraft draft = dataset.StartObject())
{
    await draft.Writer.WriteAsync<Observation>(Observations(140, 10));
    await dataset.AppendAsync(draft);
}

long humid = await dataset.Scan<Observation>().Where(r => r.Humidity > 50.0).CountAsync();
long unknown = await dataset.Scan<Observation>().Where(r => r.Humidity.IsNull).CountAsync();
```

```
evolved: version 10, columns Day, Celsius, Town, Humidity
  humidity above 50: 4900 rows; unknown, in every row written before: 99200 rows
  a row written before: Observation { Day = 10, Celsius = 10.1, Town = Nice, Humidity =  }
```

`EvolveSchemaAsync` gives the dataset a new schema in one commit, without rewriting any object. The
allowed changes are these:

* add a nullable column, under a name no column has ever had
* drop a column
* rename a column, mapping the new name to the old one
* widen a number within its kind: `int` to `long`, `uint` to `ulong`, `float` to `double`
* make a column nullable

A column the layout reads, which partitions and orders every object, keeps its name and its type.

Anything else could lose a value already written, and is refused before anything is committed, with
the column and the reason.

Objects written earlier read as the new schema. A column they lack reads as nulls, a renamed column
is read through its old name, and a widened one is converted batch by batch. A filter reaches such an
object in its own terms, and a filter over a column the object lacks is settled without reading it:
`Humidity > 50.0` skips every earlier object and `Humidity.IsNull` takes them whole. Compaction
writes every object it rewrites in the current schema, so the conversion only lasts until the
object's level is next compacted. A dropped or renamed name is never reused, so an old object can
never lend its values to an unrelated column.

## Watch out

* The dataset format is experimental and specific to this project. Pin the package version, and do
  not expect another Vortex implementation to read the commit tree.
* This release reads and writes commit format 5 alone. A dataset an earlier release wrote, in
  formats 1 to 4, is refused with a message that says so: read its rows with the release that wrote
  it and append them to a new dataset. A later release's format is refused too, so upgrade every
  process that writes or reads a dataset together.
* Marked rows keep their bytes until compaction. A delete that marks rows frees no space, since the
  object holds them (up to an eighth of its rows) until compaction rewrites it or a later delete
  passes that share. An object under a mebibyte is rewritten by every delete that touches it, so
  group the rows you change into few calls: two one-row deletes in a small object rewrite it twice.
* Two writers changing the schema at the same time do not both succeed. The second gets an
  `InvalidOperationException`, and once it has refreshed it sees the schema the first one set.
* Two processes creating the same dataset at the same time do not both succeed either. One creates
  it, and the other gets an `ObjectStoreException` saying the store already holds a dataset. Open it
  then, and check that its schema is the one you need.
* The store must create objects atomically and list them consistently. A dataset writes only to a
  store that declares `ObjectStoreCapabilities.CreatesConditionally`, and only reads from one that
  does not. See [object-store.md](object-store.md).
* Every version keeps its objects alive until a vacuum removes them, and a handle that outlives the
  retention window can lose its objects to vacuum. See
  [dataset-maintenance.md](dataset-maintenance.md).
* An object is created before the commit that names it, so a crash between the two leaves an object
  no version references. Vacuum collects it once it is older than the window.
* An object past 32 MiB goes to the store as it is written. A compaction tells the store how large it
  can grow, so the parts fit every service, but an append cannot, and an append past what a service
  reaches without that fails at its end: 168 GB on Cloudflare R2 and 106 GB on Scaleway (about a
  terabyte on Amazon S3). Append in smaller pieces, or give the store a spool directory
  (`StoreTransportOptions.SpoolDirectory`) there. The parts of a large object are in flight four at a
  time: an object of a day at a few thousand rows a second sends parts of about 180 MB on Scaleway,
  where objects of an hour keep the default parts of 16 MiB.
* `DatasetOptions.Session` is the session whose pool, cache and parallelism the dataset's scans use,
  and `DatasetOptions.TimeProvider` is the clock a commit reads its creation time from.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- datasets
```

The dataset partitioned by day, the window asked a day at a time, the cache and the retentions are
the `time-windows` sample, the variant columns the `variants` sample, and the layout the
`dataset-layout` sample:

```
dotnet run -c Release --project samples/Vorticity.Samples -- time-windows variants dataset-layout
```
