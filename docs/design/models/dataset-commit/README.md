# The commit, and the rebase of every operation

`DatasetCommit.tla` models how a dataset's writers commit
([the commit protocol](../../13-dataset.md#8-the-commit-protocol)), as `DatasetCommitter.AttemptAsync`
and `ApplyAsync` do it. A writer prepares its operation on the version it read, then commits it: it
reads the latest version, applies the operation there again, and creates the next version's key, which
only one writer can create. A writer that loses reads the winner and applies its operation again, up to
`MaxAttempts` times. A batch in which nothing applied publishes nothing. A put may land and its writer
die before it learns it.

Three kinds of actors run beside one another over three levels.

- Appenders add one row each, in an object of their own in level 0, under a key they pick.
- Deleters mark the rows of one key in every object that holds a live one, as one replacement whose
  inputs are the entries they read, and plan again when it is abandoned. An object whose every row
  left is of that key goes out whole, unmarked, as the code takes it: the [runs of the
  code](../dataset-commit-trace/README.md) showed it, which a model that only marked rejected.
- Compactors empty level 0 into a level above, with the objects of that level it overlaps, or push an
  object of a level into the next, moving it whole when nothing there overlaps it and it holds no marks.

Above level 0, a level's objects must not share a key. The rebase rules are the code's:

| operation | rebased onto the version that won |
|---|---|
| an append | already there when level 0 holds an entry of its uid, added otherwise (`AddObject`) |
| a replacement, a compaction's or a delete's | abandoned when an input is missing or its entry is not the one read, marks included, or when the part of its destination it fills holds an object it did not read (`ReplaceObjects`, `Expected`, `Range`) |

## What it does not claim

Index fragments, schema changes, the index policy, rollups and partition indexes are left out, and so
are the trees' pages, which the fuzzer and the tree's oracles hold. So are a delete that rewrites an
object once its marks would pass its share (`DatasetOptions.MarkedShare`), a purge of marks, and a
level 0 object moved whole rather than merged: jobs of the code this model does not plan, which the
runs of the code turn off. An object holds its rows' keys as a
range, a key is an integer, and an object written by a merge holds the live rows of its inputs. Sources
and their offsets are a model of their own. The store is the contract of
[the atomic put](../atomic-put/README.md): a creation succeeds only on a free key, and answers `Exists`
only when another writer created it, which the stores of this repository are each checked to hold.

## Checked properties

| property | claim |
|---|---|
| `NoDuplicate` | no row is read twice, in any version |
| `NoLostRow` | a row whose append its writer was told landed, and that no version deleted, is in the latest version |
| `NoResurrection` | a row a version deleted comes back in no later version |
| `KeyDisjoint` | above level 0, no two objects of a level share a key |

## Configurations

| configuration | purpose |
|---|---|
| `DatasetCommit.cfg` | two appenders and a compactor running two jobs, a seeded object in level 1, writers that may die once their put landed |
| `DatasetCommitDeletes.cfg` | two appenders, a deleter and a compactor running two jobs, so that a job merges an object of two keys a delete marks, writers that do not die |
| `DatasetCommitRace.cfg` | an appender and two compactors, one job each, over a seeded level 1 |
| `DatasetCommitAll.cfg` | two appenders, a deleter and a compactor running two jobs |
| `BrokenUidInKey.cfg` | a tree key without the object's uid. Must violate `NoLostRow` |
| `BrokenNoRewalk.cfg` | no second look at the destination's range at commit. Must violate `KeyDisjoint` |
| `BrokenNoExpected.cfg` | an input checked for its presence alone, not for its entry. Must violate `NoResurrection` |
| `BrokenPhantomExists.cfg` | a store that answers `Exists` for a creation its own put made. Must violate `NoDuplicate` |
| `ReachabilityCrossLevel.cfg` | two jobs from two levels into one level in flight together. Must violate `NoCrossLevelRace` |
| `ReachabilityMarkUnderJob.cfg` | a delete marks rows in an object a job read before them. Must violate `NoMarkUnderJob` |
| `ReachabilityAbandon.cfg` | an operation is abandoned on the version it lands on. Must violate `NoAbandon` |

## Correspondence

Modelled at commit 36983254.

| action | code |
|---|---|
| `PlanAppend` | an append's object, its key ending with its uid (`DatasetOperation.AddObject`) |
| `PlanDelete` | `DatasetRowChanges.RunAsync`: the entries read and re-marked as one `ReplaceObjects` with `Expected`, an object whose rows left it takes all going whole (`DatasetRowChanges.cs:161-165`, `:305-308`) |
| `PlanCompaction` | `CompactionPlan`: level 0 emptied into a level with what it overlaps, or an object pushed up, moved whole when it can be |
| `Read` | the latest version an attempt builds on (`DatasetCommitter.cs:207-237`) |
| `Put` | `ApplyAsync` on that version (`:928-1125`), then the conditional creation (`:405-423`), a lost race sending the writer to read again |
| `Apply` | the rules of `AddObject` (`:962-975`) and `ReplaceObjects` (`:1021-1090`), `ForeignAsync` for the destination's range (`:1461-1488`) |

## TLC evidence

Verdicts from TLC 1.7.4 (`tla2tools.jar`, SHA-256
`936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`), the same from TLC 1.8.0 (build
2026.11.06), on OpenJDK 21.0.13, macOS arm64, 2026-11-07. The traces are TLC 1.8.0's, which names the
actor of each step.

| configuration | result | distinct states | depth | time |
|---|---|---:|---:|---:|
| `DatasetCommit.cfg` | no error | 28 714 | 19 | 1 s |
| `DatasetCommitDeletes.cfg` | no error | 437 403 | 26 | 10 s |
| `DatasetCommitRace.cfg` | no error | 4 755 | 15 | 1 s |
| `DatasetCommitAll.cfg` | no error | 2 218 128 | 26 | 38 s |
| `BrokenUidInKey.cfg` | `NoLostRow` violated | 18 | 7 | |
| `BrokenNoRewalk.cfg` | `KeyDisjoint` violated | 912 | 10 | |
| `BrokenNoExpected.cfg` | `NoResurrection` violated | 133 142 | 16 | 3 s |
| `BrokenPhantomExists.cfg` | `NoDuplicate` violated | 117 | 9 | |
| `ReachabilityCrossLevel.cfg` | `NoCrossLevelRace` violated | 95 | 6 | |
| `ReachabilityMarkUnderJob.cfg` | `NoMarkUnderJob` violated | 198 737 | 14 | 3 s |
| `ReachabilityAbandon.cfg` | `NoAbandon` violated | 453 | 7 | |

With two compactors as well, `DatasetCommitAll.cfg` passed ten gigabytes and fifteen minutes before it
was stopped, so it keeps one.

The traces of the mutations:

- `BrokenUidInKey.cfg`: `PlanAppend("a1")`, `Read("a1")`, `Put("a1")`, then the same for `a2` under the
  same key: without the uid, `a2`'s entry takes `a1`'s tree key, and `a1`'s row, acknowledged, is gone.
  It is the case the interleaving fuzzer once found, and why a key ends with its uid.
- `BrokenNoRewalk.cfg`: `a1` appends a row of key 2. `PlanCompaction("c1")` plans to empty level 0 into
  level 2, `PlanCompaction("c2")` to push level 1's object, also of key 2, into level 2, both on that
  version. `c1` commits, then `c2`: the two jobs share no input, so neither is abandoned for a missing
  one, and level 2 holds two objects of key 2. The second look at the destination's range at commit is
  what the code does since the race was found.
- `BrokenNoExpected.cfg`: `a1` appends a row of key 1 and `a2` one of key 2, and `c1`'s first job merges
  level 0 with level 1's seed into one object of both keys. `PlanDelete("d1")` plans to mark the row of
  key 1 in it, then `PlanCompaction("c1")` plans its second job on that object, unmarked. The delete
  commits, then the job, whose input is still there: it carries on the entry it read, without the
  mark, and the deleted row is back. It takes two appenders and two jobs since a delete marks only an
  object of two keys, and takes an object of one key out whole.
- `BrokenPhantomExists.cfg`: `a1`'s append lands, and the store answers `Exists` as if another writer had
  taken the version. Before `a1` reads again, `c1` empties level 0 into a level above. `a1` applies its
  append to that version, finds no entry of its uid in level 0, and adds its object a second time.

The last trace is a contract the dataset relies on rather than a defect of its own: a put that answers
`Exists` must have written nothing (`PutOutcome.Exists`), since an append is idempotent only while its
object is still in level 0. The stores of this repository hold to it: the
[conditional put](../conditional-put/README.md) settles a lost answer by its put id, and the
[file store](../file-put/README.md) no longer answers `Exists` for its own claim. The interleaving
fuzzer's own crash, which retries a commit that landed at once, finds the object where it left it.
