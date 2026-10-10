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
rows let a scan skip blocks. `SummaryColumns` picks which other columns each object summarises for
the same purpose, the first 32 by default.

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
  data/3ee81023c25245108e5b2cfcf0978769.vortex: level 0, rows 0 to 50000, 82457 bytes
  data/73531b788ee34599aa8fc9e7e167e372.vortex: level 0, rows 50000 to 100000, 82457 bytes
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

Row positions are the dataset's. A batch's `StartRow`, `Rows(…)` and a key cursor's `Row` count the
version's rows object after object, in the order a scan delivers them. A key cursor over the
clustering key merges the cursors of the objects, and its `CountAtKeyAsync` is the number of entries
under the current key, here the thousand rows of day 75. It walks both ways like a file's cursor
(`SeekLastAsync`, `PrevAsync`, `PrevKeyAsync`, `AtOrBefore`, `Before`), and a step against the
direction of the previous one seeks every object again at the current entry. Walking down, an object
above level 0 opens only once the walk could reach its keys, while the level 0 objects all open at
the seek since nothing bounds them from above. See [keys-in-order.md](keys-in-order.md).

A scan reads the objects one after another in the tree's order. Once the consumer reads past the
first batch, the next object is opened and its first batch read and decoded while the current one
is still being read. At most two objects are open at a time, and a scan that stops at its first
batch opens only one. `ScanOptions.Prefetch` controls this, 1 by default, and 0 opens each object
only when the scan reaches it.

A group by at a degree above one reads the objects side by side, each one a range in the lanes'
queue, with large objects cut between their chunks. On the first column of the clustering key, or a
function of it, a group by streams, because the dataset delivers its rows in key order. See
[aggregates.md](aggregates.md).

## Versions

```
after another append: the handle reads version 5, 130000 rows; a scan built before it counts 120000; another handle reads version 4 until it refreshes to 5
```

A handle is pinned to one version. It moves only when it commits or when you call `RefreshAsync`. A
scan is pinned to the version its handle held when the scan was built, so a commit in the middle of
a query does not change the answer. `Version`, `RowCount`, `ObjectCount` and `Lag` describe the
version the handle holds. `Lag` is how many objects level 0 holds beyond its ceiling of eight, and
compaction brings it back to 0 ([dataset-maintenance.md](dataset-maintenance.md)).

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
object reads the same bytes it would read from a rewritten one. A walk in key order pays a little
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
* make a column nullable

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
* Marked rows keep their bytes until compaction. A delete that marks rows frees no space, since the
  object holds them (up to an eighth of its rows) until compaction rewrites it or a later delete
  passes that share. An object under a mebibyte is rewritten by every delete that touches it, so
  group the rows you change into few calls: two one-row deletes in a small object rewrite it twice.
* Two writers changing the schema at the same time do not both succeed. The second gets an
  `InvalidOperationException`, and once it has refreshed it sees the schema the first one set.
* The store must create objects atomically and list them consistently. A store that cannot promise
  `PutIfAbsent` cannot host a dataset safely. See [object-store.md](object-store.md).
* Every version keeps its objects alive until a vacuum removes them, and a handle that outlives the
  retention window can lose its objects to vacuum. See
  [dataset-maintenance.md](dataset-maintenance.md).
* An object is created before the commit that names it, so a crash between the two leaves an object
  no version references. Vacuum collects it once it is older than the window.
* `DatasetOptions.Session` is the session whose pool, cache and parallelism the dataset's scans use,
  and `DatasetOptions.TimeProvider` is the clock a commit reads its creation time from.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- datasets
```
