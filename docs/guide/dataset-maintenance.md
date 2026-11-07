# Dataset maintenance

Compact, vacuum, verify and index a dataset, and see what each operation costs in store requests.

```csharp
await using CountingObjectStore store = new CountingObjectStore(new MemoryObjectStore(), ownsInner: true);
await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions
{
    Layout = new DatasetLayout { SortKey = [LayoutValue.Column(Reading.ColumnNames.Day)] },
});

const int Appends = 10;
for (int i = 0; i < Appends; i++)
{
    await using ObjectDraft draft = dataset.StartObject();
    await draft.Writer.WriteAsync<Reading>(Rows(i * 5_000, 5_000));
    await dataset.AppendAsync(draft);
}

store.Reset();
CompactionPlan plan = await dataset.PlanCompactionAsync();
```

```
after 10 appends: version 11, 50000 rows, 10 objects, lag 2
plan: work True, style Leveled, keyed True, lag 2, objects by level [10], bytes by level [137031]
  job: level 0 to 1, LevelZeroCeiling, 10 objects, 50000 rows, 137031 bytes, target 131072
  cost: 0 requests (0 read, 0 head, 0 put, 0 delete, 0 list), 0 dependent steps, 0 bytes read, 0 written
```

Appending is cheap, but it leaves a dataset in poor shape: many small objects, and every old version
still holding on to its own. The operations on this page put it back in order, and the last one gives
it indexes it was written without. None of them runs by itself. They are the caller's background job, and the dataset only reports how far behind
it is. Like everything in `Vorticity.Dataset`, they are experimental (see [datasets.md](datasets.md)).

## Compaction

This dataset has a sort key and no partition, so its jobs keep levels of objects disjoint by key. A
partitioned dataset merges objects of one size class within each partition and consolidates a
partition once it receives no more rows, as [dataset-layout.md](dataset-layout.md) shows; the calls
on this page run both.

Appends land in level 0, which can hold eight objects before compaction has work to do. `Lag` is how
far past that ceiling it is. Here ten objects give a lag of 2, and every lookup by key touches the
two extra objects until compaction catches up.

You can ask before doing anything. `PlanCompactionAsync` reads the top page of each level, which the
version's header carries or the handle already holds, and only descends to the objects a job would
take. Here that costs no request at all, and two requests over 125 000 objects. `HasWork` answers
the question, and `Job` describes the one job it would run: the levels it moves between, what
triggered it, the objects, rows and bytes it would read, and the size of the objects it would write.

```csharp
CompactionResult? compacted = await dataset.CompactAsync();
```

```
compacted: version 12, Applied, level 0 to 1, 10 objects in and 3 out, 137031 bytes in and 90644 out, 50000 rows
  cost: 14 requests (10 read, 0 head, 4 put, 0 delete, 0 list), 14 dependent steps, 137031 bytes read, 91453 written
now: 3 objects, lag 0; again: nothing to do
```

Ten objects became three, and 137 031 bytes became 90 644. The same 50 000 rows take a third less
space, because the column encoders do more with 50 000 rows at once than with 5 000. Small appends
cost size, and compaction wins it back.

`CompactAsync` runs one job and returns `null` when none is due. A caller that wants the dataset
back within its bounds calls it in a loop, so no single call rewrites gigabytes. With a sort key and
no partition the style is `Leveled`: the levels above 0 hold objects with disjoint keys, so a lookup
by key touches at most one object per level. Otherwise it is `Tiered`: objects of one size class
merge within their partition, which rewrites each row less often, and a calm partition is
consolidated into objects disjoint by key. `CompactionOptions` sets the level 0 ceiling, the fan-out
between levels and the size of a level 1 object. `Outcome` says whether the commit applied, or
whether a concurrent writer had already done the work.

Level 0 goes to the first level large enough to hold it. The ten appends here, 134 KiB, fit in level
1, which holds ten objects of 128 KiB, while a load of gigabytes skips the levels it would only
overflow and is written once. A dataset that takes many small commits therefore pays for each one a
merge into a level a few times its size, never a merge into the levels that hold most of its rows.
An output object is at most the layout's `ObjectBytes`, 1 GiB by default, reached at level 5 and
above with the default target of level 1. A job sizes its objects as it writes them, from the
bytes its columns will encode to; until a column has written a chunk it guesses them, so objects of
columns that compress well, as here, come out under their target.

When a level above 0 grows past its size, it gives up one object, which is merged into the next
level with the objects there whose keys it overlaps. By default it picks the largest object, the one
that pushes the level furthest over its size. `CompactionOptions.Pick = CompactionPick.RoundRobin`
takes instead the object after the one the level's previous job took, wrapping around at the end,
so that rewrites sweep every key in turn as LevelDB does. On ten million rows and 30 000 small
commits it wrote 6 % more than the largest-first default, which is why leveled datasets do not use
it. Tiered datasets do, because their job concatenates a run of one level's objects, and finding the
run after the pointer costs a page where finding the longest run means reading every leaf.

When nothing in the next level overlaps the object that goes up, the job moves it there whole: its
entry changes level, and no row is read or written. A merge of it alone would have written the same
rows into one object of the same bounds. `CompactionResult.ObjectsMoved` says how many objects a job
moved, and those count in neither `BytesIn` nor `BytesOut`. An object keeps being rewritten when it
holds marks or fragments to fold, or a schema older than the dataset's, since a rewrite is what
brings it to the current one. Keys that arrive in order, as when time leads the sort key, never
overlap, so every row is rewritten once, by the merge that takes it out of level 0: on a stream of a
thousand appends at the tail, compaction wrote half the bytes it wrote without the moves.

Compaction writes only the live rows. Rows a delete marked in an input object disappear with it, and
the outputs carry no marks. An object whose marks reach half a delete's limits (a sixteenth of its
rows, or half of its vector's bound, a thousandth of its bytes and at least a kilobyte) is
rewritten alone in its level by a job whose `Trigger` is `Marks`. Those jobs come after the level
limits and before the fragments, the most marked object first.

An object a job writes, of a mebibyte or more, also keeps what aggregations answer from without its
rows: the counts of each column whose values repeat and the statistics of its blocks, computed as the
rows go out, with no pass of its own (see
[How an aggregation is answered](datasets.md#how-an-aggregation-is-answered)). Nothing a question
reads is kept for the next: what answers a question is what the writes left. A vacuum deletes the
partials an earlier version learned of questions and kept under `learned/`, which nothing reads now.

When the application owes the erasure of deleted rows within a delay, it declares the delay when it
creates the dataset:

```csharp
await using VortexDataset events = await VortexDataset.CreateAsync(store, Event.Schema, new DatasetOptions
{
    Layout = new DatasetLayout { SortKey = [LayoutValue.Column("At")] },
    ErasureDelay = TimeSpan.FromDays(30),
});
```

An object is then rewritten without its deleted rows once its oldest mark is fifteen days old, by a
job whose `Trigger` is `Erasure`, and its vector's size alone no longer triggers anything. The old
object stays readable by the versions the retention keeps, and a vacuum deletes it after them: the
rows are gone from the store within the delay plus the retention. A dataset on a locked store refuses
a delay. A compaction that read an object before a delete marked rows in it finds the object's entry
changed when it commits, and ends `Abandoned` rather than bringing those rows back.

## Verify

```csharp
DatasetVerification verified = await dataset.VerifyAsync();
DatasetVerification since = await dataset.VerifyAsync(since: checkedAt);
```

```
verify: holds True, 3 objects, 1 commits, 1 pages, 0 fragments, 0 unhashed, 0 problems; cost: 17 requests (13 read, 4 head, 0 put, 0 delete, 0 list), 17 dependent steps, 274130 bytes read, 0 written
verify since 12: holds True, 1 objects, 1 pages; cost: 15 requests (13 read, 2 head, 0 put, 0 delete, 0 list), 15 dependent steps, 33153 bytes read, 0 written
```

`VerifyAsync` checks the version's tree pages, objects and index fragments against what their
references promise, and reports every problem instead of throwing. `Problems` is a list of
sentences, empty when `Holds` is true. Each object is hashed and compared with the hash its entry
records. `Unhashed` counts the imported objects whose entry records no hash, and for those only the
length is checked. An object a delete marked has its counts of the deleted rows recounted from the
rows its vector names, and every rollup's object is checked to be in the store at its length. `since`
skips what an earlier, trusted version shares with this one: after one more append it read 33 153
bytes, where a full verification read 274 130.

## Vacuum

Old versions keep their objects readable. Vacuum deletes what no version inside the retention window
still references:

```csharp
VacuumResult today = await dataset.VacuumAsync(new VacuumOptions { DryRun = true });

VacuumOptions later = new VacuumOptions { TimeProvider = new Later(TimeSpan.FromDays(8)), DryRun = true };
VacuumResult dry = await dataset.VacuumAsync(later);
VacuumResult done = await dataset.VacuumAsync(later with { DryRun = false });
```

```
vacuum today, dry run: 0 would go, 0 too young, 13 versions retained, window 7.00:00:00; cost: 18 requests (13 read, 0 head, 0 put, 0 delete, 5 list), 18 dependent steps, 42070 bytes read, 0 written
vacuum in eight days, dry run: 21 would go, 1 retained, 2 pages read; cost: 7 requests (2 read, 0 head, 0 put, 0 delete, 5 list), 7 dependent steps, 1592 bytes read, 0 written
vacuum in eight days: 21 deleted (11 commit objects), latest version 13; cost: 9 requests (2 read, 0 head, 0 put, 2 delete, 5 list), 9 dependent steps, 1592 bytes read, 0 written
and the data: 55000 rows, 4 objects
```

Today nothing goes, because every version is younger than the window. The window is seven days
unless `DatasetOptions.RetentionWindow` set another when the dataset was created, and
`RetainedVersions` keeps a number of versions whatever their age. Eight days later only the latest
version is kept and 21 objects go: the ten data objects compaction replaced, and eleven of the
twelve superseded commit objects. The twelfth still holds pages the latest version reads. A commit
object that holds a delete's vector of positions, or its counts of the rows it marked, is kept the
same way while a retained version names them, and the rollups a retained version names stay with it.

`VacuumOptions.TimeProvider` is the clock vacuum measures ages against. That is how the sample
jumps past the window, and how you can test your own retention, on a store no writer commits to
meanwhile, since a clock that far ahead takes the objects of a writer in flight. In production it
must agree with the store's clock, since the ages it compares are the store's timestamps. Deletes go
out in batches, two requests for twenty-one keys here, and the listings say when each object was
written, so none needs a head request.

Run a dry run first. `Deleted` lists what would go, commit objects first. `Young` lists the
unreferenced objects kept because they may belong to a writer still in flight, and `Retained` the
versions kept. On a store under a retention lock, `Locked` lists what is past the window but still
under the store's lock or a legal hold, and `NextUnlock` says when the first of it may go, so the
next vacuum after that date takes it.

## In the background

The library starts no thread of its own. Compaction in the background is a loop that runs until it
is cancelled, hosted wherever you like: a hosted service, a worker, a console.

```csharp
using CancellationTokenSource stop = new CancellationTokenSource();
Task loop = dataset.RunCompactionAsync(
    new CompactionSchedule { Idle = TimeSpan.FromMilliseconds(10), Progress = new Counting(() => Interlocked.Increment(ref ran)) },
    stop.Token);
```

```
10 more appends: 14 objects, lag 3
stopped: 1 compactions, 13 objects, lag 0
```

Each turn refreshes the handle (one round trip), plans, and runs the job that is due. When nothing
is due, the first loop (`Loop` 0) sums the counts of the deepest level's new runs of objects into
rollups (`RollUpAsync`, see [Rollups](datasets.md#how-an-aggregation-is-answered)), reading no row,
then sleeps for `Idle`, a minute by default, and asks again. `BytesPerSecond` paces the loop:
after a job it waits until what the job wrote fits the rate, which leaves the store's bandwidth to
the writers. The task ends with `OperationCanceledException` once the token is cancelled, or with
the exception a job raised, which the host catches before starting the loop again.

A loop runs the same jobs as `CompactAsync`, so it is safe against every writer and every other
loop. What several loops on one dataset can waste is a job that two of them run at once, which one
commit then abandons. When several processes write one dataset, give its compaction to a process of
its own that runs the dataset's only loop, and leave inline compaction off in the writers
(`InlineCompactionBytes` is 0 by default). Two compactions then never run at once on the dataset.
Loops that know about one another can share a dataset all the same, and say so with `Loops` and
`Loop`: each takes the due job ranked at its index, and no two ranked jobs read or write the same
level.

To follow compaction from a dashboard, listen to the meter `Vorticity` (see
[observability.md](observability.md)). `vortex.dataset.compaction.bytes_written` over the bytes the
writers append is the write amplification, `vortex.dataset.compactions` with the outcome `abandoned`
counts the jobs two compactions ran at once, and `vortex.dataset.commit_attempts` past the applied
`vortex.dataset.commits` counts the rebases. A dataset's own lag is `VortexDataset.Lag`, which a host
exports for the datasets it watches, and each compaction is an activity, `vortex.dataset.compaction`,
tagged with its levels, its bytes and its outcome.

## In the writer's commit

A dataset with a single writer and no spare process can let the writer keep level 0 in check.
`DatasetOptions.InlineCompactionBytes` makes a commit that pushes level 0 past its ceiling merge it
into the next level before returning, as long as the job reads no more than that many bytes. The
merge's latency lands on that one commit, and larger jobs are left to another driver. The commit
stands whatever happens to the merge, and a failed or cancelled merge leaves level 0 to the next
commit.

## Indexing after the fact

A filter you find you need after months of data does not need the data rewritten.

```csharp
IndexingResult indexed = await dataset.IndexAsync(IndexPolicy.None.Bloom(Reading.ColumnNames.Celsius));
long none = await dataset.Scan<Reading>().Where(r => r.Celsius == 25.15).CountAsync();
```

```
indexed: 13 objects, 0 left, 2 walks, 2 commits, 53758 fragment bytes, version 26; cost: 90 requests (86 read, 2 head, 2 put, 0 delete, 0 list), 90 dependent steps, 2018162 bytes read, 60139 written
a temperature no row holds: 0 rows; cost: 26 requests (26 read, 0 head, 0 put, 0 delete, 0 list), 26 dependent steps, 799498 bytes read, 0 written
the same without the indexes and the objects' counts: 0 rows; cost: 35 requests (35 read, 0 head, 0 put, 0 delete, 0 list), 35 dependent steps, 1327150 bytes read, 0 written
```

The first commit gives the index to the dataset, not to a handle. From that version on, every write
and every compaction builds it into the objects it writes, whatever its own options and its budget
say, so a compaction loop in another process, which never heard of the filter, does not drop it.
Then each object written before takes an index fragment, kept in a commit, a batch of objects per
commit (`IndexingOptions.ObjectsPerCommit`, 64 by default). The second walk found nothing more to
index and ended the call. A temperature inside every object's bounds is then refused by each object's
filter, which comes with the object's open, before its column is read: a handle that holds none of
the objects reads 799 KB in 26 requests, where without the filters, and without the counts the
objects keep, which would answer it too, it reads 1.3 MB in 35. An object smaller than the tail an
open reads, 64 KiB, is read whole either way.

The indexing runs beside the writers and the compaction loop, and every version it commits answers as
the dataset did before. A compaction that started before its first commit may still write an object
without the index, and the next `IndexAsync` finds it. Two indexings of two columns at once keep both. An
object written by another writer carries no identity to bind a fragment to, and `ObjectsLeft` counts
it until a compaction rewrites it with the index. The indexes are on one column each: a composite key
is refused, since its encoder lives in a handle's options.

## Expiry

An expiry drops old data without reading it: every object a filter covers whole goes in one commit.

```csharp
DateTimeOffset cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
RowChangeResult expired = await dataset.ExpireAsync<Request>(r => r.At < cutoff, ct);
```

It is decided from the objects' summaries in the dataset's tree, with no data object read, and
`expired.Rows` says how many rows went. An object that holds rows on both sides of the bound stays
whole, so a row may outlive the bound. Under a partition every object holds rows of one partition
value, so a bound on the partition's column covers whole objects up to the value it falls in: under a
partition by day, a row outlives the bound by a day at most. To drop rows exactly, delete them
(`DeleteAsync`), which opens the objects that straddle the bound and marks their rows. A filter on a
text covers no object, since an object's summaries bound numbers and times, not texts. Expiry, like a
delete, leaves the bytes to vacuum.

## What each costs

Measured above with `CountingObjectStore`, on a dataset of one object after compaction:

| | requests | what they are |
|---|---|---|
| `PlanCompactionAsync` | 0 | the header the handle holds |
| `CompactAsync` | 14 | every input object read, three objects written, one commit |
| `CompactAsync`, a move | 1 to 2 | the object's tail, to know its schema, unless the handle holds it open, and one commit |
| `VerifyAsync` | 17 | every object hashed, every page read |
| `VerifyAsync(since)` | 15 | what the earlier version does not share |
| `VacuumAsync` | 7 to 18 | the listings, the commit object of each retained version and the trees they name, the deletes in batches |
| `IndexAsync` | 90, for thirteen objects | per walk a refresh, then per object its tail and its index directory, its columns read for a fragment, and one commit per batch |

`DependentSteps` is the number that sets latency: the round trips that had to wait for the previous
one. On a store where a request costs 30 ms, it is what a job takes. See
[object-store.md](object-store.md).

## Watch out

* Compaction and vacuum are separate. Compaction leaves the replaced objects in place for the
  versions that still name them, and vacuum removes them once those versions are past the window.
* A reader that outlives the window loses its objects. Vacuum works from the store's latest version,
  not from yours, so a handle still on an old version may find an object missing.
  `ObjectNotFoundException` then tells it to refresh.
* A writer has half the window to commit. Vacuum deletes an object no version names once it is a
  window old, so a commit refuses to name objects whose first put began half the window ago or more,
  and throws `DatasetIntegrityException` with nothing committed. Write those rows again. A window
  shorter than twice your longest append, rewrite or compaction job refuses that job every time.
* The other half of the window is for the clocks and the store's listing. The vacuum's clock may run
  ahead of the store's, a writer's clock may fall behind its own earlier reading, stepped back or
  paused mid-commit, and the listing may lag, by half the window at most together. With the default
  seven days that is three and a half days, far past what synchronised clocks drift. With a window of
  minutes, it is what you must hold your clocks to.
* Compaction commits like any writer, so a concurrent writer can win the race. Check `Outcome`.
  Vacuum deletes only what no retained version references, and deletes nothing until every retained
  version has been walked.
* A writer that dies mid-commit on `FileObjectStore` leaves no commit: the store shows a commit only
  once it is whole. On a file system that can neither rename exclusively nor link files, where the
  store writes a commit in place, it leaves an incomplete one. Every open and every commit then throws
  `TornCommitException`, which names the version. `VortexDataset.RemoveTornCommitAsync(store)` removes
  it, and the dataset reads as the version before. Run it while no writer commits, since a commit
  still being written can look torn there.
* A compaction that dies leaves what it wrote, which no version names, and vacuum deletes it like any
  other object. An upload in parts it had started is aborted when the job fails, and one the abort
  could not reach, because the process died, stays in the bucket as parts no object shows. Give the
  bucket a lifecycle rule that aborts incomplete uploads after a day. A consolidation of a dataset
  partitioned by day writes a day's objects, up to a gibibyte each, each in one upload, which makes
  the rule worth its line.
* None of this changes the data. After all three operations, the same rows read back.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- dataset-maintenance
```
