# Datasets

Keep a table that grows as a versioned set of Vortex files in an object store, and read it with
the same scans as a file.

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
appended data/7f4f12630bb346098a7d785a6bed85bb.vortex: version 2, 50000 rows
```

This is `Vorticity.Dataset`, a separate package, and it is **experimental**: the assembly is
marked `[Experimental("VX0001")]`, so the compiler reports VX0001, as an error, wherever your code
uses one of its types, until you opt in. Opt in once for the project, with
`<NoWarn>$(NoWarn);VX0001</NoWarn>`, or mark your own assembly or type `[Experimental]`, which
makes its uses of experimental code legal and passes the warning on to its callers; the samples do
the latter. The diagnostic says what the attribute means: the dataset's commit format and API may
change between releases. The files inside a dataset are ordinary Vortex files; the tree of commits
over them is this repository's own format, which no other Vortex implementation reads. A single
file needs none of this.

## What a dataset is

A set of immutable Vortex files, the **data objects**, and one commit object per version that says
which objects the version holds. Every change is a commit, and every commit is a new version;
nothing is overwritten. A commit is a conditional creation in the store, so two writers need no
lock: the one that loses the race re-applies its change on top of the winner's version, up to
`DatasetOptions.MaxAttempts` times.

`ClusteringKey` is the decision that matters most. The objects are ordered by the smallest key
each holds, every object this handle writes carries a sorted-runs index on the key, and
compaction keeps the levels above 0 free of overlap on it. That is what lets a scan skip whole
objects, the way ordering rows lets a scan skip blocks; `SummaryColumns` chooses which other
columns each object summarises for the same purpose, the first 32 by default.

## Adding rows

`StartObject()` returns an `ObjectDraft`: a key in the store and a `VortexFileWriter` with the
dataset's schema, whose rows you write as you would to any file. `AppendAsync(draft)` completes the
writer, puts the object in the store and commits it; disposing a draft that was not committed
abandons it, and nothing reaches the store. `AppendAsync(batches)` does the same with an
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
  data/7f4f12630bb346098a7d785a6bed85bb.vortex: level 0, rows 0 to 50000, 82457 bytes
  data/6fd130088886482683ebdd6485d5530d.vortex: level 0, rows 50000 to 100000, 82457 bytes
  imports/days-100-119.vortex: level 0, rows 100000 to 120000, 37836 bytes
```

`ImportAsync` takes a key in the store, not a path on disk: putting the bytes there is yours to
do, and the file's columns must read as the dataset's: the same ones, or those of an earlier schema
of the dataset ([Changing the schema](#changing-the-schema)). `ObjectsAsync()` lists the objects of
the version, reading the tree and never an object.

## Reading

`dataset.Scan<Reading>()` is the `Scan<Reading>` of a file, over every object of the version, and
`dataset.Scan(columns)` is the tool scan. Everything in the guide's read pages applies:

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

The plan's first pruning step is the objects' summaries: the two objects whose days end before 100
are not opened, and their 13 blocks are pruned without a read. `MayMatch` answers from the
summaries the version's header already carries, so it reads nothing, and `false` is a proof.
Positions are the dataset's: a batch's `StartRow`, `Rows(…)` and a key cursor's `Row` count the
version's rows, object after object, in the order a scan delivers them. A key cursor over the
clustering key merges the objects' cursors; its `KeyCountAsync` is the number of entries under the
current key, here the thousand rows of day 75. It walks both ways, as a file's does: `SeekLastAsync`,
`PrevAsync`, `PrevKeyAsync`, `AtOrBefore` and `Before`, and a step against the direction of the
last one seeks every object again at the current entry. Walking down, an object of a level above 0
opens only once the walk could reach its keys, and level 0's all open at the seek, since nothing
bounds them from above. See [keys-in-order.md](keys-in-order.md).

## Versions

```
after another append: the handle reads version 5, 130000 rows; a scan built before it counts 120000; another handle reads version 4 until it refreshes to 5
```

A handle is pinned to one version: it moves only when it commits or when you call `RefreshAsync`.
A scan is pinned to the version its handle held when the scan was built, so a commit in the middle
of a query does not change its answer. `Version`, `RowCount`, `ObjectCount` and `Lag` describe the
version the handle holds; `Lag` is how many objects level 0 holds above its ceiling of eight,
which compaction brings back to 0 ([dataset-maintenance.md](dataset-maintenance.md)).

## Removing and replacing

```csharp
await using (ObjectDraft rewritten = dataset.StartObject())
{
    await rewritten.Writer.WriteAsync(dataset.Scan<Reading>()
        .Rows(RowRange.FromLength(first.FirstRow, first.Rows))
        .Where(r => r.Celsius.IsNotNull)
        .ToRecordsAsync());
    ReplaceResult replaced = await dataset.ReplaceAsync([first], [rewritten]);
}
```

```
replaced data/7f4f12630bb346098a7d785a6bed85bb.vortex by the rows with a temperature: version 6, Applied, 129000 rows
removed the import: version 7, Applied, 109000 rows, 3 objects
removing it again: Abandoned, version 7
```

`ReplaceAsync(removed, added)` swaps objects in one commit: a reader sees the old ones or the new
ones, never both and never neither. `RemoveAsync(objects)` is the same with nothing added. Both
take `DataObject`s from `ObjectsAsync()`. The removed objects stay in the store, readable by the
versions that name them, until vacuum deletes them. Removing an object the version no longer
holds is `Abandoned`: no version is created, the result names the latest one, and the objects a
replace would have added are left for vacuum.

## Deleting and updating rows

```csharp
RowChangeResult deleted = await dataset.DeleteAsync<Reading>(r => r.Day < 10);

RowChangeResult updated = await dataset.UpdateAsync<Reading>(
    r => r.City == "Nice" & r.Day >= 60,
    r => r with { Celsius = r.Celsius + 1.0 });
```

```
deleted days 0 to 9: version 8, 9800 rows, 1 object(s) rewritten into 1, 99200 rows left
warmed Nice from day 60: version 9, 6251 rows changed, 2 object(s) in, 3 out
```

`DeleteAsync` removes the rows its filter is true for, in one commit. A row the filter is false or
unknown for, a null compared, stays. Each object that holds a matching row is rewritten without it,
in its own level and, without a clustering key, at its own place in the order; one left with no row
is removed whole, and one whose summaries refute the filter is not opened. `UpdateAsync` reads the
matching rows as records, passes each through the lambda, and writes the results into new objects
of level 0, as an append would, since a change may move a row's key: an update is a delete and an
insert, applied together. Its record must have a member for every column. `DeleteAsync(VortexExpr)`
is the delete for a caller without a record type.

It is copy on write: reading pays nothing and every statistic stays exact, and the price is the
rewrite of every object a row is taken from, however few rows that is. A delete of one row in a
256 MiB object rewrites the object. `BytesIn` and `BytesOut` say what a change rewrote.

A reader sees the change whole or not at all. Rows appended by another writer while the change is
worked out are not touched; if another writer rewrites an object the change read, a compaction for
instance, the change is worked out again on the version that won, and `Attempts` counts how many
times.

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

`EvolveSchemaAsync` gives the dataset new columns in one commit that rewrites no object. A column is
added, nullable, under a name no column ever had; dropped, unless the clustering key holds it;
renamed, the new name mapped to the old one; its numbers widened within their kind, `int` to `long`,
`uint` to `ulong`, `float` to `double`; or made nullable. Anything else would lose a value already
written, and is refused before anything is committed, with the column and the reason.

The objects written before read as the new schema: a column they lack as nulls, a renamed one
through the name it had, a widened one converted batch by batch. A filter is pushed down to such an
object in its own terms, and one over a column it lacks is settled without reading it: `Humidity >
50.0` skips every earlier object, `Humidity.IsNull` takes them whole. Compaction writes each object
it rewrites in the current schema, so the conversion lasts until an object's level is next
compacted. A dropped or renamed name is never used again, so an object written with it cannot lend
its values to another column.

## Watch out

* **Experimental, and this repository's own format.** Pin the package version, and do not expect
  another Vortex implementation to read the tree.
* **A delete or an update rewrites whole objects.** Batch the rows you change into few calls: two
  deletes of one row each in one object rewrite it twice.
* **Two writers changing the schema at once do not both succeed**: the second is refused with
  `InvalidOperationException` and changes the schema the dataset has once it has refreshed.
* **The store must create objects atomically and list them consistently**: see
  [object-store.md](object-store.md). A store that cannot promise `PutIfAbsent` cannot host a
  dataset safely.
* Every version keeps its objects alive until a vacuum removes them, and a handle that outlives the
  retention window loses its objects to vacuum:
  [dataset-maintenance.md](dataset-maintenance.md).
* An object is created before the commit that names it, so a crash between the two leaves an
  object no version references, which vacuum collects once it is older than the window.
* `DatasetOptions.Session` is the session whose pool, cache and parallelism the dataset's scans
  use, and `DatasetOptions.TimeProvider` the clock a commit reads its creation time from.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- datasets
```
