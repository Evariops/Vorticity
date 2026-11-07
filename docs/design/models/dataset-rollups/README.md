# Rollups against the commits that change their objects

`DatasetRollups.tla` models the rollups a dataset keeps over its deepest level
([rollups](../../15-compaction.md#rollups)). A level holds objects under tree keys, each with the rows
it counts. A rollup covers a range of tree keys and holds the total of its objects and a fingerprint of
what it summed. Writers put an object under a free key, take one away, or mark rows in one, which puts
its entry again with fewer rows. A builder reads the latest version, sums a range, and commits the
rollup later, once other commits may have landed. Every commit applies to the latest version, as a
rebase does.

Two rules keep a rollup true, and each is a switch a mutation turns off:

- a rollup is put only over what its range holds as it was summed, by its fingerprint
  (`CheckFingerprint`), and dropped otherwise;
- a commit that puts or removes an entry, marks included, takes away every rollup whose range holds its
  key (`TakeAway`), which a later call sums again.

## What it does not claim

Rollups of rank 1, summed from objects. The ranks above, summed from the rollups below them by the same
fingerprint, are left out, and so are a schema change, which takes every rollup away, and the tree's
pages. The writers' keys are apart, which the commit model's rebase covers otherwise. The index of a
consolidated partition is not modelled: it names the objects it covers by uid, and an object a delete
rewrites takes a new uid and leaves its coverage, which `PartitionIndexTests` holds.

## Checked properties

| property | claim |
|---|---|
| `RollupsTrue` | every rollup of every version holds the total of the objects under its range, and their fingerprint |

## Configurations

| configuration | purpose |
|---|---|
| `DatasetRollups.cfg` | three keys, two writers changing their keys twice, two builders of a rollup each |
| `BrokenNoTakeAway.cfg` | a change that leaves the rollups over its key. Must violate `RollupsTrue` |
| `BrokenNoFingerprint.cfg` | a rollup put whatever its range holds now. Must violate `RollupsTrue` |
| `ReachabilityDrop.cfg` | a rollup summed before a change is dropped at its commit. Must violate `NoDrop` |
| `ReachabilityTakenAway.cfg` | a change takes a rollup away. Must violate `NoTakenAway` |

## Correspondence

Modelled at commit 960f9776.

| action | code |
|---|---|
| `Sum` | `DatasetRollups.RollUpAsync`: a run of objects summed, its fingerprint taken |
| `PutRollup` | `ApplyAsync`'s `PutRollup` (`DatasetCommitter.cs:1282-1305`): `SummedAsync` (`:1597`) hashes the range again, the rollup dropped when it differs |
| `Change` | an operation that puts or removes an entry, then `InvalidateAsync` (`:1325`), which takes away every rollup that covers a key the batch changed |

## TLC evidence

TLC 1.7.4 (`tla2tools.jar`, SHA-256 `936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`),
the same from TLC 1.8.0 (build 2026.11.06), OpenJDK 21.0.13, macOS arm64, 2026-11-07.

| configuration | result | distinct states | depth |
|---|---|---:|---:|
| `DatasetRollups.cfg` | no error | 80 141 | 9 |
| `BrokenNoTakeAway.cfg` | `RollupsTrue` violated | 46 | 4 |
| `BrokenNoFingerprint.cfg` | `RollupsTrue` violated | 44 | 4 |
| `ReachabilityDrop.cfg` | `NoDrop` violated | 531 | 4 |
| `ReachabilityTakenAway.cfg` | `NoTakenAway` violated | 547 | 4 |

- `BrokenNoTakeAway.cfg`: `b1` sums key 1, two rows, and puts its rollup. The writer of key 1 then
  changes its object, and the rollup, left in place, no longer holds its total.
- `BrokenNoFingerprint.cfg`: `b1` sums key 1, the writer changes it, then `b1` puts the rollup it
  summed before: stale from the start.
