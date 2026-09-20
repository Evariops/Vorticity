# Dataset maintenance

Compact, vacuum, verify, and what each costs.

Appending to a dataset is cheap and leaves it in a poor shape: many small objects, and every old
version still holding its own. The three operations here are how it is put back in order.

## Compaction

Ten appends of 5 000 rows:

```
after 10 appends: version 11, 50000 rows, 10 objects, depth 1, lag 2
  level 0: 10 entries, 50000 rows
```

`Lag` is how far past its ceiling level zero has drifted — it holds eight objects before compaction
has anything to do. Ask before doing:

```csharp
CompactionPlan plan = await dataset.PlanCompactionAsync();
```

```
the plan: work to do True, style Leveled, clustered True, lag 2, fragmented 0
  objects by level [10], bytes by level [305440]
  job: level 0 to 1, trigger LevelZeroCeiling, 10 inputs, 50000 rows, 305440 bytes, target 268435456
```

`HasWork` is the question; `Job` is the one job it would run, with the level it moves from and to,
what triggered it, and the bytes involved. Then:

```csharp
CompactionResult result = await dataset.CompactAsync();
```

```
compacted: version 12, outcome Applied, level 0 to 1, 10 objects in and 1 out,
           305440 bytes in and 92382 out, 50000 rows
now: 1 objects, depth 0, lag 0
```

**Ten objects became one, and 305 440 bytes became 92 382** — a third of the size for the same
50 000 rows. That is not compaction overhead being reclaimed: it is what a column encoder can do
with 50 000 rows that it cannot do with 5 000 at a time. Small appends cost size, and compaction is
what gets it back.

`CompactAsync` returns `null` when there is nothing to do, and `Outcome` says whether the commit was
applied or lost a race.

## Vacuum

Old versions keep their objects. Vacuum deletes what no retained version needs:

```csharp
VacuumResult dry = await dataset.VacuumAsync(new VacuumOptions { DryRun = true });
```

```
vacuum today: 0 would go, 0 too young, 12 versions retained, window 7.00:00:00
```

Nothing goes, because every version is inside the retention window of seven days. A week later, on
the same dataset:

```
vacuum in eight days, dry run: 21 would go, 0 too young, 1 retained, 1 pages read
vacuum in eight days: 21 deleted, latest version 12
```

Twenty-one objects, one version kept. `VacuumOptions.Clock` is the `TimeProvider` it asks, which is
how the figures above were measured and how you test your own retention. `RepackBelow` also asks it
to rewrite objects that have become mostly dead rows.

**Always `DryRun` first.** `Deleted`, `Young`, `Retained` and `Sparse` say exactly what would go.

## Verify

```csharp
DatasetVerification check = await dataset.VerifyAsync();
```

```
verify: holds True, 1 objects, 1 commits, 1 pages, 0 fragments, 0 unhashed, 0 problems
```

It walks the commit tree and checks every object against the hash the tree records for it.
`Problems` is a list of sentences, empty when `Holds` is true, and `Unhashed` counts what could not
be checked because nothing recorded a hash. `VerifyOptions.Since` limits it to versions after one
you already trust, which is what makes it affordable to run often.

## What each costs

| | in requests |
|---|---|
| `PlanCompactionAsync` | reads the commit tree only |
| `CompactAsync` | reads every input object, writes one, commits once |
| `VacuumAsync` | walks the retained versions' trees, then one delete per object |
| `VerifyAsync` | reads every object's head, and its pages when hashing |

A `CountingObjectStore` around your store prices any of them exactly — see
[object-store.md](object-store.md).

## Watch out

* Compaction and vacuum are **separate**: compaction leaves the old objects in place for the old
  versions, and vacuum is what removes them.
* Both are ordinary commits, so a concurrent writer can win the race and either can come back
  having done nothing. Look at `Outcome`.
* Nothing here changes the data: after all three, the same 50 000 rows read back.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- dataset-maintenance
```
