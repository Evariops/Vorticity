# Datasets

Create a versioned dataset over an object store, import, append, commit.

This is `Vorticity.Dataset`, a separate package. It is **experimental, and the format is this
repository's own** — not something upstream defines. A single file needs none of it.

A dataset is a set of Vortex files in an object store, plus a tree of commits that says which files
belong to which version. Every change is a new version; nothing is overwritten.

## Creating one

```csharp
await using IObjectStore store = new FileObjectStore("/data/readings");
await using VortexDataset dataset = await VortexDataset.CreateAsync(store, schema, new DatasetOptions
{
    ClusteringKey = ["city"],
    SummaryColumns = ["city"],
});
```

`ClusteringKey` is the column the objects are ordered by, and it is the decision that matters most:
it is what lets a scan skip whole objects, the same way ordering rows lets a scan skip blocks. The
summary columns are what each object records about itself for that skipping.

`OpenAsync` takes the same store and reads the latest version.

## Appending

```csharp
ulong version = await dataset.AppendAsync(batches);   // an IAsyncEnumerable<RecordBatch>
```

```
created at version 1, 0 rows, clustered by city
appended: version 2, 60000 rows, 1 objects, depth 1, lag 0
```

One call, one commit, one new version. The batches become one or more objects, and the commit that
names them is what makes them visible.

## Importing a file you already have

```csharp
await store.PutIfAbsentAsync("imported.vortex", bytes, cancellationToken);
ulong version = await dataset.ImportAsync("imported.vortex");
```

`ImportAsync` takes an **object key in the store**, not a path on disk: the bytes must already be
there, and the dataset adopts them without rewriting. That is how a file written by something else
joins a dataset for the cost of a commit.

```
imported: version 3, 90000 rows, 2 objects
  object data/eb4dde42ebe440dd8d267a447d00cb9e.vortex: 60000 rows, 252460 bytes
  object imported.vortex: 30000 rows, 70185 bytes
```

`ObjectsAsync()` lists them, with the rows and bytes of each.

## Reading

The scan vocabulary is the file's, over every object at once:

```csharp
await foreach (RecordBatch batch in dataset.Scan().Where(paris).ExecuteAsync())
```

`Select`, `Where`, `Rows(from, to)`, `InKeyOrder`, `WithIndexes`, `WithSummaries`, `WithMetrics`,
then `ExecuteAsync`, `CountAsync`, `AnyAsync`, `MinAsync`, `MaxAsync`, `ExplainAsync`, or
`ObjectsAsync` for the objects a query would touch rather than the rows.

`DatasetScanMetrics` says what the skipping saved:

```
scanned 90000 rows: 2 objects considered, 2 opened, 0 skipped
city = Paris: 18000 rows, 0 objects skipped, 0 subtrees skipped
```

Nothing was skipped here, and the reason is the same as for a filter on a file: every object holds
every city, because the rows were appended in a rotation rather than clustered by city. An object
can only be skipped when its summaries put it outside the predicate — which is what
`ClusteringKey` is for.

`dataset.RankAsync(literal)` answers where a key falls among the clustering keys without reading
rows, and `dataset.Rows(from, to)` takes a range across the whole dataset.

## Versions

| | |
|---|---|
| `dataset.Version` | the version this handle reads |
| `RefreshAsync()` | move to the latest, if another writer has committed |
| `ApplyAsync(operations)` | one commit made of explicit operations |
| `ObjectCount`, `RowCount`, `Depth`, `Lag` | the shape of the current version |

A commit is atomic and optimistic: it writes a new commit object under a key that must not already
exist, so two writers racing produce one winner and one retry — `DatasetOptions.MaxAttempts`
bounds the retries.

## Watch out

* **Experimental, and this repository's own format.** A dataset written here is not something other
  Vortex implementations read. The files inside it are ordinary Vortex files; the tree over them is
  not.
* The store must be consistent on `PutIfAbsent`. See [object-store.md](object-store.md).
* Every version keeps its objects alive until a vacuum removes them — see
  [dataset-maintenance.md](dataset-maintenance.md).

## Run it

```
dotnet run --project samples/Vorticity.Samples -- datasets
```
