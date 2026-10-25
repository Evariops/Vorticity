# Datasets

A dataset keeps a growing table as a versioned set of Vortex files in an object store. You read it
with the same scans as a single file.

```csharp
await using IObjectStore store = new FileObjectStore(Demo.Path("dataset"));
await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions
{
    ClusteringKey = [Reading.ColumnNames.Day],
});

await using (ObjectDraft draft = dataset.StartObject())
{
    await draft.Writer.WriteAsync<Reading>(Days(0, 50));
    ulong version = await dataset.AppendAsync(draft);
}
```

```
created: version 1, 0 rows, clustered by Day
appended data/3ee81023c25245108e5b2cfcf0978769.vortex: version 2, 50000 rows
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

`ClusteringKey` is the decision that matters most. Objects are ordered by the smallest key each
holds, every object this handle writes carries a sorted-runs index on the key, and compaction keeps
the levels above 0 free of overlap on it. That lets a scan skip whole objects, the same way ordered
rows let a scan skip blocks. The key goes on columns that hold no null, such as `Day` here, and
`CreateAsync` refuses a nullable one with an `ArgumentException`. Every null is the same key, so the
objects of a level would overlap on it, and a read that reaches the nulls would open each of them. A
row whose key is unknown takes a value of its own instead, the smallest or the largest its type has.
`SummaryColumns` picks which other columns each object summarises for the same purpose, the first 32
by default.

`Clustering` decides how compaction groups the rows into objects. Its `Value` is the key's first
column, `ClusteringValue.Column`, or a function of it that never decreases: `Truncate(CalendarUnit.Day)`
for the start of an instant's day, `Bucket(width)` for an instant's or a number's bucket. Above level 0
no object holds rows of two values, so the rows of a day, or of whatever the value is, lie in objects
of their own. Its `ObjectBytes` is the size an object aims at, past which a job starts another without
splitting a key. Either works alone. The library knows nothing of what a value means: to keep each
tenant's rows apart, lead the key with the tenant's column and cluster by `Column`.

```csharp
await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
{
    ClusteringKey = [nameof(Request.At)],
    Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Day) },
    Identifiers = [nameof(Request.RequestId)],
});
```

Its `BlockValue` does the same inside an object, for the key's second column. A job's objects keep,
for each column, statistics over runs of rows called blocks, which a question can answer from without
reading the rows (see [Reading](#reading)); a block ends where the key's first column changes, and with a
block value, where that value of the second column changes too. A function of the second column then
holds one group in each block: with a key `[Service, At]` and a `BlockValue` of
`Truncate(CalendarUnit.Hour)`, a question by service and hour answers from the blocks. As with `Value`,
the library only compares the function's values: the hour is the application's choice.

The clustering is fixed when the dataset is created, every commit carries it, and every writer
follows it. A question bounded on the key's first column then reads whole objects but at its ends, an
expiry by such a bound drops whole objects, and a long range can be asked a value at a time, as
[Reading](#reading) shows.

Which column to lead the key with is the application's choice, and the questions it asks tell. A handle
keeps count: `RecommendClustering()` ranks the columns its questions' filters read by the bytes a
clustering by each would have spared the objects it read as rows, and the telemetry counts the same
(`vortex.dataset.clustering.avoidable_bytes`). It is advice for the next dataset, never applied.

```csharp
foreach (ClusteringAdvice advice in dataset.RecommendClustering())
{
    Console.WriteLine($"{advice.Column}: {advice.BytesAvoided:N0} bytes over {advice.Objects} objects");
}
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
  data/3ee81023c25245108e5b2cfcf0978769.vortex: level 0, rows 0 to 50000, 80137 bytes
  data/73531b788ee34599aa8fc9e7e167e372.vortex: level 0, rows 50000 to 100000, 80137 bytes
  imports/days-100-119.vortex: level 0, rows 100000 to 120000, 37836 bytes
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
ScanPlan plan = await dataset.Scan<Reading>().Where(r => r.Day >= 100).ExplainAsync();
```

```
Day >= 100: 20000 rows; plan: 3 of 16 blocks, object summaries pruned 13; zone map pruned 0
may Day be 500? False; may Day be 75? True
the first batch of day 110 starts at dataset row 108192
key cursor: seek 75 found True, key 75 at row 75000; 1000 entries
walking down: last key 119 at row 119999; the key before it 118, its last entry at row 118999
```

Pruning starts with the object summaries. The two objects whose days all fall before 100 are never
opened, and their 13 blocks are pruned without a read. `MayMatch` answers from the summaries the
handle already holds (the version's header and the pages it kept from earlier versions), so it reads
nothing, and `false` is a proof.

An object a job writes, of a mebibyte or more, also keeps a sheet of each column: its distinct
values, its most frequent ones, and, when its values repeat, a filter of them. A question that asks a
column for a value, `r.Route == "/checkout"` or a set of them, reads the sheets of such an object
before the object, and passes over an object whose filter lacks every value asked: one request a few
kilobytes long instead of the object's reads. A smaller object is read in the request its sheets
would take, and keeps none.

Such an object also keeps, for each column whose values repeat, where each value lives: the column's
values, each with the ranges of rows of the blocks it appears in. A question that asks those columns for
values reads these indexes in place of the sheets, passes over an object that lacks the values, and
reads only the ranges of the others where the values live. That holds on any column, not only the key's:
a filter on an endpoint, which only its service's rows hold, reads that service's rows. On thirty
sources of 30 000 rows, each with endpoints whose names interleave with every other's, so that no zone
map leaves rows out, a filter on one endpoint read 1.3 MB in 8 requests, where the same filter written
as a prefix, which no index answers, read 14.4 MB in 131. A value that lives in more than half an
object's blocks is read with the whole object.

A value that is rare but scattered, which no index places, is read in two passes when the sheets say
so: the filter's columns first, then the other columns only for the rows it kept, from the chunks that
hold them. A code held by thirty rows of 100 000 scattered over the objects, under bodies of 400 bytes,
read 4.8 MB in 36 requests that way, where one pass over every body read 30.3 MB in 198.

Row positions are the dataset's. A batch's `StartRow`, `Rows(…)` and a key cursor's `Row` count the
version's rows object after object, in the order a scan delivers them. A key cursor over the
clustering key merges the cursors of the objects, and its `CountAtKeyAsync` is the number of entries
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
column of the clustering key, or a function of it, a group by under a take streams: the dataset then
delivers its rows in key order, one object after the other, and the take stops the read early. Without
a take, the objects are read side by side. See [aggregates.md](aggregates.md).

An object a job writes, of a mebibyte or more, is cut into slices of the key that its table of
contents lists, and a question under a range of the key's first column reads only what the range
reaches: the objects whose keys it may hold, and in each the slices and blocks whose keys it holds.
Every aggregation reads its rows, as above. Three days of a request every five seconds, clustered by
day, by service over the first two, then over the last quarter of an hour of the second (the
`time-windows` sample):

```
51840 requests over three days in 3 objects
two days by service: api 6944, auth 7000, billing 6867, cart 6870, search 6879; 34560 rows read
the last quarter of an hour of the second day: 180 requests; blocks decoded 1, pruned 2
```

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
one, and `ToInt64()` converts every kind of value.

Some questions are answered from counts rather than rows. An object a job writes, of a mebibyte or
more, keeps for each column whose values repeat its counts: the rows of each value, each number
column's exact sum, least and greatest, and for a column of at most 1 000 values a quantile sketch of
each number column. A question on one column and the numbers reads an object's counts in one request
instead of its rows when its other keys are values the object holds constant and its filter asks that
column for values or is true for every row of the object, as its summaries prove. Under a clustering by
hour each object holds one hour, so a question by service and hour is such a question, and only the
objects at the window's ends, which hold rows outside it, are read as rows:

```csharp
var hourly = dataset.Scan<Request>()
    .Where(r => r.At >= from && r.At < to)
    .GroupBy(r => (r.Service, r.At.Truncate(CalendarUnit.Hour)))
    .Select(g => (g.Key.Service, g.Key.Item2, g.Count(), g.Sum(r => r.Bytes), g.Quantile(r => r.Bytes, 0.99)));
```

It answers what the rows would, to the bit. The objects still in their appends keep no counts and are
read as rows. `ScanOptions.UseSynopses = false` reads every object's rows, and `ExplainAsync` says
object by object which it reads (`ScanPlan.States`).

A filter that leaves one value of a declared identifier out, `r.RequestId != id`, keeps the counts: an
object's counts answer, less those of the rows that hold the value, which the identifier's postings find
without reading the object's other rows. An object that does not hold the value reads its counts and
the postings alone. That holds for counts, sums, means and distinct values, when the identifier column
holds no null; a minimum, a maximum or a quantile reads the rows.

An object whose counts do not answer may still be answered block by block. Such an object keeps, for
each column, its statistics over the object's blocks, the runs of rows of one value of the key's first
column: each block's values and their rows, and each number's count, sum and extremes. A block whose
columns of the question hold one value each, its filter proven over its rows, gives its counts and
measures; a block where one column varies gives the count of each of its values, under a question of
counts alone. The other blocks are read as rows. Under a key that leads with the service, errors by
service read the statistics of the service and the status, `r.Status >= 500` judged value by value:
on four services of 150 000 rows, 86.8 KB where the rows took 480.7 KB. By the hour as well, a block
answers when its instants fall in one hour, which takes blocks short in time: here each held some fifty
minutes, and the question read 2.7 MB where the rows took 3.4 MB. With a `BlockValue` of the hour, its
blocks end at each hour, and it read 438 KB where the rows took 3.4 MB.
`ScanOptions.AlignWindow` widens the window to the bounds of the objects its ends fall in, so that
they are answered from their counts too: the answer is then the exact answer of the window applied,
which `ScanMetrics.Window` gives. The other synopses are designed in
[22-on-the-fly-and-synopses.md](../design/22-on-the-fly-and-synopses.md).

An object read as rows leaves what it gave the question, so that the question asked again reads
nothing of it. What it keeps is the question's family: the same keys and aggregates, each filter that
asks a column for a value or a few made a key of its own. Counts by route where `r.Status == 404`
learn the counts by route and status, and the same question under `r.Status == 500` answers from
them. The handle keeps these partials, and so does the session's `SynopsisCache` when it has one, for
the handles and processes after. A question whose aggregates a state answers whole learns them: counts,
sums, means, extremes, quantiles and approximate distinct counts. A value of the key's first column is
not made a key where an object holds it with others, since learning would read every value's rows.
A window on the key's first column that moves, `r.At >= now - 15 minutes`, keeps the partials of the
objects it holds whole, and reads the one its edge cuts. The handle keeps up the families its questions
asked in the last hour: each object it appends learns their partials as it is written, which costs the
append some 1.5 ms an object and family, so that a monitor asking the same question after each append
reads none of the objects appended since it first asked.

An aggregation whose filter is evaluated on an object's rows, a range no sheet or index answers, also
notes where in the object the filter kept rows; asked again, from the handle or one opened later on the
same `SynopsisCache`, it reads those parts of the object alone.

Over a long range, an object's counts still cost a request an object. `RollUpAsync` sums the counts of
runs of about `DatasetOptions.RollupFanout` consecutive objects (64 by default) into rollups, then as
many rollups into one of the rank above, with no row read, and `RunCompactionAsync` calls it once no
job is due. A question whose every object under a rollup would answer from its counts reads the
rollup instead. On thirty days of 2 000 routes, an object a day and rollups of four, a count of every
route reads 4 requests and 261 698 bytes, where the objects' counts take 30 requests and 1 930 456
bytes. A commit that adds or takes away an object takes away the rollups over it, which the next call
sums again.

A page that shows a long window can show its newest part first. `AskByValuesAsync` cuts a range of the
key's first column where the clustering value changes, asks your question over each part, several
parts at once, and delivers each part's answer, the last part first by default, once it and every part
before it are in:

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
none. Under a clustering by day, a bound on the instant covers every day before the one it falls in,
so a row outlives the bound by a day at most. To drop rows exactly, delete them: `DeleteAsync` opens
the objects that straddle the bound. The sample keeps three days of debug lines, an hour an object,
and drops the first two:

```
debug lines expired before 10-03: 2880 then 960, 1920 dropped; info lines untouched: 1440
```

A row that comes late, after rows of later keys, is appended as any other, into level 0, and the job
that takes it down rewrites the objects of the level below that its key reaches. On 48 hours of
requests at 20 a second from four writers, clustered by hour, one row in fifty a few seconds late and
one in ten thousand up to an hour late, a row is rewritten 6.5 times on its way down, against 2.8 when
none comes late (`vortex-queries --micro lifecycle`). A writer that can hold its rows until the late
ones are in, and append them in key order, pays the second figure.

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
find its rows, of its chunks, its pages, its indexes and its variants' paths, never its data. A read
with no open of an object, such as a lookup of an identifier in a large one, needs nothing else before
its data; a read that opens the object reads its last 64 KiB from the store first. An object never
changes, so an entry never goes stale.

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
version the handle holds. `Lag` is how many objects level 0 holds beyond its ceiling of eight, and
compaction brings it back to 0 ([dataset-maintenance.md](dataset-maintenance.md)).

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
* drop a column, unless the clustering key uses it
* rename a column, mapping the new name to the old one
* widen a number within its kind: `int` to `long`, `uint` to `ulong`, `float` to `double`
* make a column nullable, unless the clustering key uses it

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
* A dataset written in commit format 4 is refused by earlier releases, which would otherwise misread
  it. A dataset takes format 4 once it declares a clustering, identifiers, shredded paths
  or an erasure delay, once it holds rollups, or once one of its objects has a footer past the first
  64 KiB an open reads.
  Upgrade every process that writes or reads such a dataset before any of them uses one of those.
* A dataset an earlier release wrote by periods opens and reads as before, but compaction plans
  nothing for it: its late level's objects overlap, and the rows of its other levels are in no key
  order, which a merge on the key would take them to be. Append its rows to a clustered dataset to
  compact them.
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

The dataset clustered by day, the window asked a day at a time, the cache and the retentions are
the `time-windows` sample, and the variant columns the `variants` sample:

```
dotnet run -c Release --project samples/Vorticity.Samples -- time-windows variants
```
