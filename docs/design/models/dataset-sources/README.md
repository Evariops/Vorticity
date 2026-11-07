# Sources, and each entry counted once

`DatasetSources.tla` models the offsets a dataset keeps per source
([sources and offsets](../../13-dataset.md#84-sources-and-offsets)), as `DatasetCommitter.AttemptAsync`
and `SourceTable` judge them. A version records the offset up to which a source's entries are counted.
A writer takes a range `(from, to]` from where its source says it stands, and commits the rows of those
entries declaring the range. Every attempt judges the range against the version it lands on: it
applies when the offset is the range's start, is counted already when the offset is past its end, and
conflicts otherwise. A verdict other than applying stands only on a version the store said was the
latest, since a version the writer held may have been passed since.

Two writers take entries of one source, holding versions that may be long passed, and a put may land
without its writer learning it: the writer, started again, takes the same range in a new commit of new
rows, which is the replay the offsets exist for.

## What it does not claim

One source, three entries, ranges of any length. Sources a commit forgets, and commits that declare
several sources at once, are left out. The trust path that builds on a held version is modelled as a
choice of the attempt, not by clocks, which [the lifecycle model](../dataset-lifecycle/README.md) owns.

## Checked properties

| property | claim |
|---|---|
| `ExactlyOnce` | in the latest version, every entry up to the offset is counted by exactly one row, and none past it |
| `NoSpuriousConflict` | a writer whose range starts at the latest offset is never told its range conflicts |

## Configurations

| configuration | purpose |
|---|---|
| `DatasetSources.cfg` | two writers, three entries, versions held from long ago, replays after a put that landed unknown |
| `BrokenJudgeOnce.cfg` | the range judged once, on the version the writer prepared on. Must violate `ExactlyOnce` |
| `BrokenVerdictOnHeld.cfg` | a verdict other than applying taken as final on a version held. Must violate `NoSpuriousConflict` |
| `ReachabilityReplay.cfg` | a writer is told its range was counted already. Must violate `NoReplay` |

## Correspondence

Modelled at commit 3da99dca.

| action | code |
|---|---|
| `Take` | the caller's range, declared as `CommitOptions.Consumed` |
| `Attempt` | the version an attempt builds on: the one held, outright while trusted, or the latest listed (`DatasetCommitter.cs:206-236`) |
| `Put` | the verdict of `SourceTable.Judge` (`:238-273`), asked again on the latest when it adds nothing on a version held (`:248-253`), then the creation (`:404-421`) |

## TLC evidence

Verdicts from TLC 1.7.4 (`tla2tools.jar`, SHA-256
`936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`), the same from TLC 1.8.0 (build
2026.11.06), on OpenJDK 21.0.13, macOS arm64, 2026-11-07. The traces are TLC 1.8.0's.

| configuration | result | distinct states | depth |
|---|---|---:|---:|
| `DatasetSources.cfg` | no error | 5 233 | 15 |
| `BrokenJudgeOnce.cfg` | `ExactlyOnce` violated | 137 | 7 |
| `BrokenVerdictOnHeld.cfg` | `NoSpuriousConflict` violated | 308 | 7 |
| `ReachabilityReplay.cfg` | `NoReplay` violated | 134 | 6 |

- `BrokenJudgeOnce.cfg`: `w1` takes entry 1 and commits it. `w2`, holding the first version, takes the
  same entry, judged applicable there, then builds on the latest version, where `w1` counted it. Judged
  once, the range applies again, and entry 1 is counted twice. Judged on the version each attempt lands
  on, it is counted already.
- `BrokenVerdictOnHeld.cfg`: `w1` takes entry 1 and commits it. `w2`, holding the first version, takes
  entry 2 from where its source stands, after entry 1, and builds on the version it holds, whose offset
  is 0: a gap, so a conflict. Asked again on the latest version, whose offset is 1, the range applies.
- `ReachabilityReplay.cfg`: `w1`'s put lands without its writer learning it, and `w1`, started again,
  finds its range counted: the replay a lost answer calls for counts nothing twice.
