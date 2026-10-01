# Dataset maintenance

Compact, vacuum and verify a dataset, and what each costs in requests.

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

Appending is cheap and leaves a dataset in poor shape: many small objects, and every old version
still holding its own. The three operations here put it back in order. None of them runs by
itself: they are the caller's background job, and the dataset only reports how far behind it is.
Like everything in `Vorticity.Dataset`, they are experimental: see [datasets.md](datasets.md).

## Compaction

Level 0 is where appends land, and it holds eight objects before compaction has work to do. `Lag`
is how far past that ceiling it is: here ten objects, a lag of 2, and every lookup by key touches
the two extra objects until compaction catches up. Ask before doing: `PlanCompactionAsync` reads
the top page of each level, which the version's header carries or the handle already holds, and
descends from it only to the objects a job would take: no request at all here, and two over 125 000
objects. `HasWork`
is the question; `Job` is the one job it would run: the levels it moves between, what triggered
it, the objects, rows and bytes it would read, and the size of the objects it would write.

```csharp
CompactionResult? compacted = await dataset.CompactAsync();
```

```
compacted: version 12, Applied, level 0 to 1, 10 objects in and 1 out, 128862 bytes in and 79036 out, 50000 rows
  cost: 22 requests (10 get, 10 head, 2 put, 0 delete, 0 list), 22 dependent steps, 128862 bytes read, 79429 written
now: 1 objects, lag 0; again: nothing to do
```

**Ten objects became one, and 128 862 bytes became 79 036**: the same 50 000 rows in two thirds of
the space, because a column encoder does more with 50 000 rows than with 5 000 at a time. Small
appends cost size, and compaction is what gets it back.

`CompactAsync` runs one job and returns `null` when none is due, so a caller that wants the
dataset back within its bounds loops until it does, rather than having one call rewrite
gigabytes. With a clustering key the style is `Leveled`: the levels above 0 hold key-disjoint
objects, so a lookup by key touches at most one object per level. Without one it is `Tiered`,
which rewrites each row less often and bounds lookups less. `CompactionOptions` moves the level 0
ceiling, the fan-out between levels, the size of a level 1 object and the cap on an output object.
`Outcome` says whether the commit applied, or found that a concurrent writer had already done it.

Level 0 goes to the first level that holds it: the ten appends here, 126 KiB, fit level 1, which
holds ten objects of 128 KiB; a load of gigabytes goes past the levels it would only overflow and
is written once. A dataset that takes many small commits then pays for each a merge into a level a
few times its size, never one into the levels that hold the bulk of its rows. An output object is
at most `MaxObjectBytes`, 4 MiB by default.

A level above 0 over its size gives up one object, merged into the level above with the objects
there its keys meet: by default the largest, the one holding the level over its size the most.
`CompactionOptions.Pick = CompactionPick.RoundRobin` takes instead the object past the one the
level's last job took, and the first past the end, so that the rewrites go over every key in turn,
as LevelDB does; each level of a header records where its last job stopped. On ten million rows
and 30 000 small commits it wrote 6 % more than the largest first, which is why a leveled dataset
does not default to it. A tiered dataset does: its job concatenates a run of one level's objects
that nothing else sits between, and the run past the pointer is found by descending the trees, one
page over 100 000 objects, where the longest run is found only by reading every leaf, 71 pages. A
dataset ordered by arrival keeps each level's objects together, and the two take the same runs.

Compaction writes the live rows only: the rows a delete marked in an input object end with it, and
its outputs carry no marks. An object whose marks reach half a delete's bounds, a sixteenth of its
rows or half a kilobyte of positions, is rewritten alone in its level by a job whose `Trigger` is
`Marks`: after the bounds of the levels and before the fragments, the most marked object first. A
compaction that read an object before a delete marked rows in it
finds the object's entry changed at its commit, and is `Abandoned` rather than bringing those rows
back.

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
references promise, and names every problem instead of throwing: `Problems` is a list of
sentences, empty when `Holds` is true. An object is hashed against the hash its entry records;
`Unhashed` counts the imported objects whose entry records none, whose length is checked instead.
`since` skips what an earlier, trusted version shares with this one: after one more append, it
read 28 830 bytes where a full verification read 146 049.

## Vacuum

Old versions keep their objects readable. Vacuum deletes what no version inside the retention
window references:

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

Today nothing goes: every version is younger than the window, seven days unless
`DatasetOptions.RetentionWindow` said otherwise when the dataset was created, and
`RetainedVersions` keeps a number of versions whatever their age. Eight days later only the latest
version is kept, and 21 objects go: the ten data objects compaction replaced, and eleven of the
twelve superseded commit objects, the twelfth still holding pages the latest version reads. `VacuumOptions.TimeProvider` is the clock vacuum measures ages against, which is
how the sample crosses the window and how you test your own retention; it must agree with the
store's clock, because the ages it compares are the store's timestamps. The deletes go out in
batches, two requests for twenty-one keys here. The fourth listing is of the leases a compaction
loop may have left (below); each lease's key says when it ended, so none costs a head request.

**Run a dry run first.** `Deleted` lists what would go, commit objects first, `Young` the
unreferenced objects kept because they may belong to a writer still in flight, and `Retained` the
versions kept. On a store under a retention lock, `Locked` lists what is past the window but still
under the store's lock or a legal hold, and `NextUnlock` says when the first of it may go: the next
vacuum after that date takes it.

## In the background

A loop that compacts until it is cancelled, which any host runs: a hosted service, a worker, a
console. The library starts no thread of its own.

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

Each turn refreshes the handle, one head request, plans, and runs the job due for it; when nothing
is due it sleeps for `Idle`, a minute by default, and asks again. `BytesPerSecond` paces it: after a
job it waits until what the job wrote fits the rate, so that compaction leaves the store's bandwidth
to the writers. The task ends with `OperationCanceledException` once the token is cancelled, or with
the exception a job raised, which a host catches before running the loop again.

A job is the one `CompactAsync` runs, so a loop is safe against every writer and every other loop;
what several loops on one dataset can waste is a job two of them run, which a commit then abandons.
Loops that know one another say so with `Loops` and `Loop`: each takes the due job ranked at its
index, and no two ranked jobs read or write one level. Loops that cannot count one another set
`Leases`: before a job a loop creates `leases/<level>/<end>` for each of its levels by
put-if-absent, holds them until the end of the current `LeaseSpan`, a minute by default, and moves
to the next job when another loop holds one. The loop remembers the leases it created, so it runs
the next job on its levels within the same span without asking the store again; a loop that leased
some of a job's levels before finding another holding the rest keeps those for the span all the
same. Nothing releases a lease, so a store that refuses deletes still works; vacuum deletes the
leases that ended a window ago.

## In the writer's commit

A dataset with one writer and no process to spare can have the writer hold level 0 itself:
`DatasetOptions.InlineCompactionBytes` makes a commit that takes level 0 past its ceiling merge it
into the level above before it returns, when the job reads no more than that many bytes. The merge's
latency lands in that one commit, and a larger job is left to another driver. The commit stands
whatever befalls the merge, which, failed or cancelled, leaves level 0 for the next commit.

## What each costs

Measured above with `CountingObjectStore`, on a dataset of one object after compaction:

| | requests | what they are |
|---|---|---|
| `PlanCompactionAsync` | 0 | the header the handle holds |
| `CompactAsync` | 22 | every input object read, one object written, one commit |
| `VerifyAsync` | 9 | every object hashed, every page read |
| `VerifyAsync(since)` | 12 | what the earlier version does not share |
| `VacuumAsync` | 29 to 31 | a listing, a head per commit object, the retained trees, the leases' listing, the deletes in batches |

`DependentSteps` is the number that decides latency: the round trips that waited for the one
before. On a store where a request costs 30 ms, it is what a job takes. See
[object-store.md](object-store.md).

## Watch out

* **Compaction and vacuum are separate.** Compaction leaves the replaced objects in place for the
  versions that still name them; vacuum removes them once those versions are past the window.
* **A reader that outlives the window loses its objects.** Vacuum marks from the store's latest
  version, not from yours: a handle still on an old version then finds an object missing, and
  `ObjectNotFoundException` says to refresh.
* Compaction commits like any writer, so a concurrent writer can win the race; look at `Outcome`.
  Vacuum deletes only what no retained version references, and deletes nothing until every
  retained version is marked.
* **A writer that dies mid-commit on `FileObjectStore` leaves a commit that is not whole.** Every
  open and every commit then throws `TornCommitException`, which names the version;
  `VortexDataset.RemoveTornCommitAsync(store)` removes it once no writer holds it, and the dataset
  reads as the version before.
* Nothing here changes the data: after all three, the same rows read back.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- dataset-maintenance
```
