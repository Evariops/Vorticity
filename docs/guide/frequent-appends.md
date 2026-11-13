# Frequent appends

Run a dataset that receives small appends every few seconds, and keep its merges, its commits and its readers ahead of them.

A dataset was designed for appends of tens of megabytes every half minute. It takes appends of a few rows every two seconds too, but every append then pays fixed costs that dwarf its rows: an object, an entry in the tree, a commit object. This page gives the numbers that size such a deployment, and the settings that keep it healthy. The figures were measured on objects of metric records at 20 ms a request, the latency of an object store in the same region.

## What an append costs, whatever its rows

| Fixed cost | Measured | Where it comes from |
|---|---|---|
| A data object | 22 to 28 KB at least | its footer and its indexes, which grow with the number of columns |
| A commit object | 20 KB while level 0 is small, up to 300 KB once it holds thousands of objects | the leaves of the tree the commit rewrites, and the pages its header carries |
| A commit object at rest | one per commit, for as long as the retention window | a version replaced stays readable that long |

An append every two seconds is 1,800 commits an hour and as many commit objects at rest under a window of an hour. No setting of the merges changes that number: only the cadence does.

## The commit chain is shared

Every commit of a dataset goes through one chain of versions: the appends of every writer and the jobs of every driver. At 20 ms a request it applies at most about 20 commits a second. Up to 8 a second, no append fails and a commit takes about a quarter of a second. At 16, the first appends fail with `ObjectStoreException` after `MaxAttempts`.

- Count what a dataset commits: the flushes of its writers, plus about one merge for every seven appends.
- Keep it well under one commit every six request latencies, 8 a second at 20 ms.
- Prefer one process that commits to a dataset. As soon as two handles commit, each commit of each loses its first attempt, and half the commit objects sent are refused.

## Where the merges run

A partition holds at most `MergeFanout − 1` objects of each size class while its merges keep up. Someone has to run them.

- With one writer per dataset, let it merge: `DatasetOptions.InlineCompactionBytes` bounds what a commit merges before it returns, and its appends and merges then follow one another on one handle, with no race between them.
- With several writers, run one compaction loop per dataset in a process of its own (`RunCompactionAsync`), and leave inline compaction off on the writers.
- Give the loop's handle a page cache that holds the leaves of level 0 it merges (`DatasetOptions.PageCacheBytes`, 32 MiB by default). With 8 MiB, a consolidation of 3,305 small objects could not commit beside two appends a second.

One loop retires about 27 small objects a second from a level 0 of 3,305 objects, where one writer adds one every two seconds. A partition that falls behind is caught up in few jobs: a merge takes its whole size class when it fits the object size and the jobs' memory budget, and a job's commit retries for as long as the job took before it gives the job up.

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
- Watch the lag: `CompactionPlan.Partitions` gives it by partition, without opening an object. Past a few hundred small objects in a partition, its readers pay for it.

## Watch out

- A commit loses its race against the others and tries again: an append fails only after `MaxAttempts`, 8 by default. A compaction's commit tries for as long as its job took, half the retention window at most, then reports its job `Abandoned`, which the next plan takes up again.
- The retention window must exceed twice the longest job, and the longest reader. Under a window of an hour, a job of more than half an hour is refused at its commit.
- The fixed costs of an append do not depend on its rows. Flushing every thirty seconds rather than every two divides the commits, the objects, the merges and the commit objects at rest by fifteen.
