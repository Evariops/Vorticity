# Frequent appends

Run a dataset that receives small appends every few seconds, and keep its merges, its commits and its readers ahead of them.

A dataset was designed for appends of tens of megabytes every half minute. It takes appends of a few rows every two seconds too, but every append then pays fixed costs that dwarf its rows: an object, an entry in the tree, a commit object. This page gives the numbers that size such a deployment, and the settings that keep it healthy. The figures were measured on objects of metric records at 20 ms a request, the latency of an object store in the same region.

## What an append costs, whatever its rows

| Fixed cost | Measured | Where it comes from |
|---|---|---|
| A data object | 22 to 28 KB at least | its footer and its indexes, which grow with the number of columns |
| A commit object | 25 KB while level 0 is small, 120 KB with a few weeks of consolidated partitions, up to 260 KB once level 0 holds thousands of objects | the leaves of the tree the commit rewrites, and the pages above them |
| A commit object at rest | one per commit, for as long as the retention window | a version replaced stays readable that long |

An append every two seconds is 1,800 commits an hour and as many commit objects at rest under a window of an hour. No setting of the merges changes that number: only the cadence does.

## The commit chain is shared

Every commit of a dataset goes through one chain of versions: the appends of every writer and the jobs of every driver. At 20 ms a request it applies at most about 20 commits a second. Up to 8 a second, no append fails and a commit takes about a quarter of a second. At 16, the first appends fail with `ObjectStoreException` after `MaxAttempts`.

- Count what a dataset commits: the flushes of its writers, plus about one merge for every seven appends, far fewer when a writer merges its own recent objects as it flushes.
- Keep it well under one commit every six request latencies, 8 a second at 20 ms.
- Prefer one process that commits to a dataset. As soon as two handles commit, each commit of each loses its first attempt, and half the commit objects sent are refused.

## Where the merges run

A partition holds at most `MergeFanout − 1` objects of each size class while its merges keep up. Someone has to run them.

- With one writer per dataset, let it merge: `DatasetOptions.InlineCompactionBytes` bounds what a commit merges before it returns, and its appends and merges then follow one another on one handle, with no race between them.
- With several writers, run one compaction loop per dataset in a process of its own (`RunCompactionAsync`), and leave inline compaction off on the writers.
- Give the loop's handle a page cache that holds the leaves of level 0 it merges (`DatasetOptions.PageCacheBytes`, 32 MiB by default). With 8 MiB, a consolidation of 3,305 small objects could not commit beside two appends a second.

One loop retires about 27 small objects a second from a level 0 of 3,305 objects, where one writer adds one every two seconds. A partition that falls behind is caught up in few jobs: a merge takes its whole size class when it fits the object size and the jobs' memory budget, and a job's commit retries for as long as the job took before it gives the job up.

## Replace your own recent objects

A writer that flushes every few seconds adds one small object per flush, and the merges then read and rewrite them all. The writer can do those merges itself, in the commit of its flush and from the rows it still holds in memory. Nothing is read from the store, no commit is added, and every flush still commits, so readers see each one as soon as before.

The writer keeps its recent flushes as runs. A run is the objects one of its commits added, with the rows they hold. At each flush, the new object takes the rows of the runs before it, from the latest back, while each holds fewer than twice the rows gathered so far, and the commit replaces their objects. The result of a replacement names the objects its commit added (`ReplaceResult.Added`), and so does the result of an append that counts its source ranges (`SourceCommit.Added`), which is how a run knows its objects.

```csharp
const int MostRows = 8_192;

// The writer's runs, oldest first: the objects one commit added, and the rows they hold.
List<(IReadOnlyList<DataObject> Objects, Metric[] Rows)> runs = [];

async Task FlushAsync(Metric[] fresh, SourceRange consumed)
{
    // The new object takes the runs before it, from the latest back,
    // while each holds fewer than twice the rows gathered so far.
    int kept = runs.Count;
    int gathered = fresh.Length;
    while (kept > 0 && runs[kept - 1].Rows.Length < 2 * gathered)
    {
        gathered += runs[--kept].Rows.Length;
    }

    Metric[] rows = [.. runs.Skip(kept).SelectMany(run => run.Rows), .. fresh];
    DataObject[] absorbed = [.. runs.Skip(kept).SelectMany(run => run.Objects)];
    ReplaceResult result = await ReplaceAsync(rows, absorbed, consumed);
    if (result.Outcome == OperationOutcome.Abandoned)
    {
        // A merge or a delete took one of those objects. Their rows are in the dataset:
        // forget the runs, and send the fresh rows alone.
        runs.RemoveRange(kept, runs.Count - kept);
        rows = fresh;
        result = await ReplaceAsync(rows, [], consumed);
    }

    if (result.Outcome != OperationOutcome.Applied)
    {
        // AlreadyThere: another writer counted these entries first, and holds their rows.
        return;
    }

    runs.RemoveRange(kept, runs.Count - kept);
    runs.Add((result.Added, rows));

    // A run that reached the rows the writer agrees to hold is let go: an ordinary object from now on.
    if (runs[0].Rows.Length >= MostRows)
    {
        runs.RemoveAt(0);
    }
}

async Task<ReplaceResult> ReplaceAsync(Metric[] rows, IReadOnlyList<DataObject> removed, SourceRange consumed)
{
    await using ObjectDraft draft = dataset.StartObject();
    await draft.Writer.WriteAsync<Metric>(rows);
    return await dataset.ReplaceObjectsAsync(removed, [draft], [consumed]);
}
```

Runs double in size from the latest to the oldest. A writer that made `n` flushes holds `log2(n)` runs at most, and has written each row `log2(n) / 2 + 1` times on average: 10 runs and 6 times over 1,024 flushes. Writing every recent row again at each flush would keep a single object, but the rows written would grow with the square of the flushes: each row 101 times over the same 1,024 flushes in the measure. A run that reaches `MostRows` is let go: its objects stay in the dataset as ordinary objects, which the merges take in their turn, and the writer holds twice `MostRows` rows at most.

The table compares a writer that appends with one that folds its runs, on the same flushes of metric records, with a fan-out of 8, the merges run inline and runs let go at about 8,000 rows. It covers 1,024 flushes of 1 and of 10 records, 128 flushes of 100 records and 120 flushes of 1,000 records. An object of one record weighs about 23 KB, and a thousand records add about 85 KB to it. Each cell reads appended → folded.

| Records a flush | Requests a flush | KB sent and read a flush | Merges in 100 flushes | Times the writer sends a record |
|---:|---|---|---|---|
| 1 | 3.4 → 2.0 | 82 → 41 | 14 → 0 | 1 → 6.0 |
| 10 | 3.4 → 2.0 | 102 → 49 | 14 → 0 | 1 → 6.0 |
| 100 | 3.7 → 2.0 | 138 → 64 | 14 → 0 | 1 → 4.5 |
| 1,000 | 5.5 → 2.5 | 263 → 211 | 13 → 2 | 1 → 2.5 |

Two requests a flush is the least a flush of one partition costs: one put of its object and one of its commit. The merges left at 1,000 records a flush are those of the runs let go.

- The runs are objects of level 0 like any other, and they count toward their size class. A class that reaches `MergeFanout` objects is merged whole, the runs in it included, and the next replacement that names them comes back `Abandoned`. One writer with a fan-out of 8 never filled a class in these measures.
- Several writers on one dataset fill a class together. With four writers at one record a flush, 396 of 1,024 replacements were abandoned and folding gained nothing over appending. A fan-out of 4 lets one writer's runs fill a class: 143 of 1,024 replacements were abandoned, for 30 % fewer bytes than appending all the same.
- Keep fewer runs in those cases, by taking a run while it holds fewer than 8 or 64 times the rows gathered, instead of twice. Fewer objects then stay in level 0, and each row is written more often. Four writers at 64 times had no replacement abandoned. They made 8.0 requests a flush where appending made 9.8, and sent and read 91 KB where appending did 134 KB, each record written 48 times.
- A larger factor also suits flushes of a few records, whose rows cost less than the entry each run keeps in every commit object. At one record a flush, a factor of 4 sent 12 % fewer bytes than a factor of 2. At 100 records a flush, it sent 5 % more.
- A replacement comes back `Abandoned` too when a delete or an update changed a row of the objects it names, a row the run would otherwise bring back.
- A writer that stops leaves its runs, which are ordinary objects that the merges take.
- In a dataset without a sort key, rows follow the order of the commits, so the rows of a run move after those that other writers appended in the meantime.

## Scale out by dataset first

| What grows | How to spread it | Coordination |
|---|---|---|
| The datasets | one driver per dataset, in as many processes as you like | none |
| The partitions of one dataset | `RunCompactionAsync` with `CompactionSchedule.Loops` and `Loop` | each process knows its index |
| One partition | a finer partition, in the layout | none |

Each dataset has its own commit chain, so datasets scale out linearly. Four loops on one dataset drained 40 partitions 2.1 to 2.4 times faster than one, since they share its chain. A dataset whose rows land in one partition at a time gives a second loop nothing to take.

The grain of the partition sets the largest job: its duration, its memory, and what a failure loses. A job must finish within half the retention window, or its commit is refused.

## Memory

A job holds what the session's jobs' budget gives it (`VortexSessionOptions.JobMemoryBudget`). It prices an open input at 128 KiB at least, plus three times what its batch weighs decoded, and its writer at the rows the job writes at most. A merge of 3,305 small objects reserves about 500 MB. A partition whose level 0 does not fit the budget at once is merged by size class first, a pass at a time. Give a maintenance process a budget a few times the largest partition's level 0 in open inputs.

## Readers

A merge never takes away what a reader holds: a reader holds a version, and every object it names stays until the vacuum, a retention window after it was replaced. What costs a reader is a level 0 that fell behind.

- A question opens the small objects of level 0 it reads side by side, as many at once as the session reads at once (`VortexSessionOptions.MaxConcurrentReads`, 128 by default). On 1,712 small objects at 20 ms a request, a count of one service takes about 0.4 s.
- An object a question cannot rule out costs it one request and all its bytes: 41.6 MB for that count, at every question, until the merges catch up.
- A handle keeps the objects it opened (`DatasetOptions.MaxOpenObjectBytes`, 64 MiB by default), about 81 KB for a small object, so 830 of them by default. A question that reads more of them than that finds none open the next time.
- Keep readers' handles open and refresh them rather than open a new one per question. A commit carries only the pages it wrote, so a handle opened cold reads, one request per level, the pages of the tree its question passes through that the last commit did not write. An open handle keeps them from one version to the next and reads them once.
- Watch the lag: `CompactionPlan.Partitions` gives it by partition, without opening an object. Past a few hundred small objects in a partition, its readers pay for it.

## Watch out

- A commit loses its race against the others and tries again: an append fails only after `MaxAttempts`, 8 by default. A compaction's commit tries for as long as its job took, half the retention window at most, then reports its job `Abandoned`, which the next plan takes up again.
- The retention window must exceed twice the longest job, and the longest reader. Under a window of an hour, a job of more than half an hour is refused at its commit.
- The fixed costs of an append do not depend on its rows. Flushing every thirty seconds rather than every two divides the commits, the objects, the merges and the commit objects at rest by fifteen.
