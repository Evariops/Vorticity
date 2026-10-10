# Dataset maintenance

Compact, vacuum and verify a dataset, and see what each operation costs in store requests.

```csharp
await using CountingObjectStore store = new CountingObjectStore(new MemoryObjectStore(), ownsInner: true);
await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions
{
    ClusteringKey = [Reading.ColumnNames.Day],
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
plan: work True, style Leveled, clustered True, lag 2, objects by level [10], bytes by level [128862]
  job: level 0 to 1, LevelZeroCeiling, 10 objects, 50000 rows, 128862 bytes, target 131072
  cost: 0 requests (0 get, 0 head, 0 put, 0 delete, 0 list), 0 dependent steps, 0 bytes read, 0 written
```

Appending is cheap, but it leaves a dataset in poor shape: many small objects, and every old version
still holding on to its own. The three operations on this page put it back in order. None of them
runs by itself. They are the caller's background job, and the dataset only reports how far behind
it is. Like everything in `Vorticity.Dataset`, they are experimental (see [datasets.md](datasets.md)).

## Compaction

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
compacted: version 12, Applied, level 0 to 1, 10 objects in and 1 out, 128862 bytes in and 79036 out, 50000 rows
  cost: 22 requests (10 get, 10 head, 2 put, 0 delete, 0 list), 22 dependent steps, 128862 bytes read, 79429 written
now: 1 objects, lag 0; again: nothing to do
```

Ten objects became one, and 128 862 bytes became 79 036. The same 50 000 rows take a third less
space, because the column encoders do more with 50 000 rows at once than with 5 000. Small appends
cost size, and compaction wins it back.

`CompactAsync` runs one job and returns `null` when none is due. A caller that wants the dataset
back within its bounds calls it in a loop, so no single call rewrites gigabytes. With a clustering
key the style is `Leveled`: the levels above 0 hold objects with disjoint keys, so a lookup by key
touches at most one object per level. Without a key it is `Tiered`, which rewrites each row less
often but bounds lookups less. `CompactionOptions` sets the level 0 ceiling, the fan-out between
levels, the size of a level 1 object and the cap on an output object. `Outcome` says whether the
commit applied, or whether a concurrent writer had already done the work.

Level 0 goes to the first level large enough to hold it. The ten appends here, 126 KiB, fit in level
1, which holds ten objects of 128 KiB, while a load of gigabytes skips the levels it would only
overflow and is written once. A dataset that takes many small commits therefore pays for each one a
merge into a level a few times its size, never a merge into the levels that hold most of its rows.
An output object is at most `MaxObjectBytes`, 4 MiB by default.

When a level above 0 grows past its size, it gives up one object, which is merged into the next
level with the objects there whose keys it overlaps. By default it picks the largest object, the one
that pushes the level furthest over its size. `CompactionOptions.Pick = CompactionPick.RoundRobin`
takes instead the object after the one the level's previous job took, wrapping around at the end,
so that rewrites sweep every key in turn as LevelDB does. On ten million rows and 30 000 small
commits it wrote 6 % more than the largest-first default, which is why leveled datasets do not use
it. Tiered datasets do, because their job concatenates a run of one level's objects, and finding the
run after the pointer costs a page where finding the longest run means reading every leaf.

Compaction writes only the live rows. Rows a delete marked in an input object disappear with it, and
the outputs carry no marks. An object whose marks reach half a delete's limits (a sixteenth of its
rows or half a kilobyte of positions) is rewritten alone in its level by a job whose `Trigger` is
`Marks`. Those jobs come after the level limits and before the fragments, the most marked object
first. A compaction that read an object before a delete marked rows in it finds the object's entry
changed when it commits, and ends `Abandoned` rather than bringing those rows back.

## Verify

```csharp
DatasetVerification verified = await dataset.VerifyAsync();
DatasetVerification since = await dataset.VerifyAsync(since: checkedAt);
```

```
verify: holds True, 1 objects, 1 commits, 1 pages, 0 fragments, 0 unhashed, 0 problems; cost: 9 requests (6 get, 3 head, 0 put, 0 delete, 0 list), 9 dependent steps, 145609 bytes read, 0 written
verify since 12: holds True, 1 objects, 1 pages; cost: 12 requests (9 get, 3 head, 0 put, 0 delete, 0 list), 12 dependent steps, 28178 bytes read, 0 written
```

`VerifyAsync` checks the version's tree pages, objects and index fragments against what their
references promise, and reports every problem instead of throwing. `Problems` is a list of
sentences, empty when `Holds` is true. Each object is hashed and compared with the hash its entry
records. `Unhashed` counts the imported objects whose entry records no hash, and for those only the
length is checked. `since` skips what an earlier, trusted version shares with this one: after one
more append it read 28 178 bytes, where a full verification read 145 609.

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
vacuum today, dry run: 0 would go, 0 too young, 13 versions retained, window 7.00:00:00; cost: 30 requests (13 get, 13 head, 0 put, 0 delete, 4 list), 30 dependent steps, 12649 bytes read, 0 written
vacuum in eight days, dry run: 21 would go, 1 retained, 2 pages read; cost: 29 requests (2 get, 23 head, 0 put, 0 delete, 4 list), 29 dependent steps, 672 bytes read, 0 written
vacuum in eight days: 21 deleted (11 commit objects), latest version 13; cost: 31 requests (2 get, 23 head, 0 put, 2 delete, 4 list), 31 dependent steps, 672 bytes read, 0 written
and the data: 55000 rows, 2 objects
```

Today nothing goes, because every version is younger than the window. The window is seven days
unless `DatasetOptions.RetentionWindow` set another when the dataset was created, and
`RetainedVersions` keeps a number of versions whatever their age. Eight days later only the latest
version is kept and 21 objects go: the ten data objects compaction replaced, and eleven of the
twelve superseded commit objects. The twelfth still holds pages the latest version reads.

`VacuumOptions.TimeProvider` is the clock vacuum measures ages against. That is how the sample
jumps past the window, and how you can test your own retention. It must agree with the store's
clock, since the ages it compares are the store's timestamps. Deletes go out in batches, two
requests for twenty-one keys here. The fourth listing covers the leases a compaction loop may have
left behind (see below), and since each lease's key says when it ended, none needs a head request.

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
10 more appends: 12 objects, lag 3
stopped: 1 compactions, 2 objects, lag 0
```

Each turn refreshes the handle (one head request), plans, and runs the job that is due. When nothing
is due it sleeps for `Idle`, a minute by default, and asks again. `BytesPerSecond` paces the loop:
after a job it waits until what the job wrote fits the rate, which leaves the store's bandwidth to
the writers. The task ends with `OperationCanceledException` once the token is cancelled, or with
the exception a job raised, which the host catches before starting the loop again.

A loop runs the same jobs as `CompactAsync`, so it is safe against every writer and every other
loop. What several loops on one dataset can waste is a job that two of them run at once, which one
commit then abandons. Loops that know about one another say so with `Loops` and `Loop`: each takes
the due job ranked at its index, and no two ranked jobs read or write the same level. Loops that
cannot coordinate set `UseLeases` instead. Before a job, a loop creates `leases/<level>/<end>` for each
of its levels with put-if-absent, holds them until the end of the current `LeaseSpan` (a minute by
default), and moves to the next job when another loop holds one. A loop remembers the leases it
created, so it can run the next job on the same levels within the span without asking the store
again. Nothing releases a lease, so a store that refuses deletes still works, and vacuum deletes the
leases that ended a window ago.

## In the writer's commit

A dataset with a single writer and no spare process can let the writer keep level 0 in check.
`DatasetOptions.InlineCompactionBytes` makes a commit that pushes level 0 past its ceiling merge it
into the next level before returning, as long as the job reads no more than that many bytes. The
merge's latency lands on that one commit, and larger jobs are left to another driver. The commit
stands whatever happens to the merge, and a failed or cancelled merge leaves level 0 to the next
commit.

## What each costs

Measured above with `CountingObjectStore`, on a dataset of one object after compaction:

| | requests | what they are |
|---|---|---|
| `PlanCompactionAsync` | 0 | the header the handle holds |
| `CompactAsync` | 22 | every input object read, one object written, one commit |
| `VerifyAsync` | 9 | every object hashed, every page read |
| `VerifyAsync(since)` | 12 | what the earlier version does not share |
| `VacuumAsync` | 29 to 31 | a listing, a head per commit object, the retained trees, the leases' listing, the deletes in batches |

`DependentSteps` is the number that sets latency: the round trips that had to wait for the previous
one. On a store where a request costs 30 ms, it is what a job takes. See
[object-store.md](object-store.md).

## Watch out

* Compaction and vacuum are separate. Compaction leaves the replaced objects in place for the
  versions that still name them, and vacuum removes them once those versions are past the window.
* A reader that outlives the window loses its objects. Vacuum works from the store's latest version,
  not from yours, so a handle still on an old version may find an object missing.
  `ObjectNotFoundException` then tells it to refresh.
* Compaction commits like any writer, so a concurrent writer can win the race. Check `Outcome`.
  Vacuum deletes only what no retained version references, and deletes nothing until every retained
  version has been walked.
* A writer that dies mid-commit on `FileObjectStore` leaves an incomplete commit. Every open and
  every commit then throws `TornCommitException`, which names the version.
  `VortexDataset.RemoveTornCommitAsync(store)` removes it once no writer holds it, and the dataset
  reads as the version before.
* None of this changes the data. After all three operations, the same rows read back.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- dataset-maintenance
```
