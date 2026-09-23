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
appended data/4960f1bc51154336b9c12abaa641c39b.vortex: version 2, 50000 rows
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
  data/4960f1bc51154336b9c12abaa641c39b.vortex: level 0, rows 0 to 50000, 82371 bytes
  data/8f1d43cdfb9d4d4ab7a10cc02baf54cb.vortex: level 0, rows 50000 to 100000, 82371 bytes
  imports/days-100-119.vortex: level 0, rows 100000 to 120000, 37828 bytes
```

`ImportAsync` takes a key in the store, not a path on disk: putting the bytes there is yours to
do, and the file's schema must be the dataset's. `ObjectsAsync()` lists the objects of the version,
reading the tree and never an object.

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
```

The plan's first pruning step is the objects' summaries: the two objects whose days end before 100
are not opened, and their 13 blocks are pruned without a read. `MayMatch` answers from the
summaries the version's header already carries, so it reads nothing, and `false` is a proof.
Positions are the dataset's: a batch's `StartRow`, `Rows(…)` and a key cursor's `Row` count the
version's rows, object after object, in the order a scan delivers them. A key cursor over the
clustering key merges the objects' cursors; its `KeyCountAsync` is the number of entries under the
current key, here the thousand rows of day 75. See [keys-in-order.md](keys-in-order.md).

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
replaced data/4960f1bc51154336b9c12abaa641c39b.vortex by the rows with a temperature: version 6, Applied, 129000 rows
removed the import: version 7, Applied, 109000 rows, 3 objects
removing it again: Abandoned, version 7
```

`ReplaceAsync(removed, added)` swaps objects in one commit: a reader sees the old ones or the new
ones, never both and never neither. `RemoveAsync(objects)` is the same with nothing added. Both
take `DataObject`s from `ObjectsAsync()`. The removed objects stay in the store, readable by the
versions that name them, until vacuum deletes them. Removing an object the version no longer
holds is `Abandoned`: no version is created, the result names the latest one, and the objects a
replace would have added are left for vacuum.

## Watch out

* **Experimental, and this repository's own format.** Pin the package version, and do not expect
  another Vortex implementation to read the tree.
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
