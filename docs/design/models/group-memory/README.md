# The memory of the lanes' merge

`GroupMemory.tla` models the memory of a group by whose lanes keep their own tables
([the lanes' spill](../../16-queries.md#the-lanes-spill)). Each lane folds its batches, a batch growing
its table by a unit of memory or not, after reserving the unit in the query's budget, and writes its
table to the scratch when the budget refuses it. A lane out of rows retires: once the budget shows room
for what the merge grows the shared table by, it merges its table into the one the retired lanes share,
the doubling first, then the copy, then its own table given back. Without room, or while another merge
runs, it writes its table to the scratch. A doubling the budget refuses still happens, since a merge
cannot stop halfway.

`ReserveAhead` is the fix of a race found on CI, where a distinct count peaked at 108 % of its budget:
the merge's room was seen and not reserved, and the lanes that kept folding meanwhile took it. With the
switch, the merge reserves its growth as a credit when it is decided, its arrays come out of it, and
what it did not use is given back.

## What it does not claim

Units, not bytes, and one doubling per merge. The core that lanes turn to under pressure, its parts and
their stacks, the turn each lane waits for, and the delivery part by part are left out: their waits run
on tasks across the engine, and a model of them needs their code, not the design's prose. Coyote 1.7.11, which
would have run the engine's own tests under its scheduler, cannot rewrite the .NET 11 assemblies: it
resolves no framework assembly of .NET 11, and given one, stops on a null reference.

## Checked properties

| property | claim |
|---|---|
| `WithinCeiling` | what the lanes' tables and the shared table hold never passes the budget |
| `Counted` | the budget counts at least what they hold |
| `EveryLaneEnds` | under weak fairness, every lane ends: no wait blocks for ever |

## Configurations

| configuration | purpose |
|---|---|
| `GroupMemory.cfg` | three lanes of three batches under a budget of four units, the merge reserving its growth |
| `BrokenNoReserveAhead.cfg` | the merge seeing its room without reserving it. Must violate `WithinCeiling` |
| `ReachabilityMergeBesideFolds.cfg` | a lane merges while another still folds. Must violate `NoMergeBesideFolds` |
| `ReachabilitySpill.cfg` | a lane the budget refuses writes its table to the scratch. Must violate `NoSpill` |

## Correspondence

Modelled at commit 1a8d2ac7, the fix.

| action | code |
|---|---|
| `Grow`, `Fold` | a lane's batch, the growth of its table reserved first (`ArrayShelf`), or written to the scratch as a run when refused |
| `Retire` | the merge into the retired table decided, `AggregationPartition.ReserveAhead` reserving its growth |
| `Double`, `Copy` | the merge, its arrays out of the credit, then `GiveBackAhead` and the lane's table given back |

## TLC evidence

TLC 1.7.4 (`tla2tools.jar`, SHA-256 `936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`),
the same from TLC 1.8.0 (build 2026.11.06), OpenJDK 21.0.13, macOS arm64, 2026-11-07. The trace is TLC
1.8.0's.

| configuration | result | distinct states | depth |
|---|---|---:|---:|
| `GroupMemory.cfg` | no error, `EveryLaneEnds` holds | 8 893 | 19 |
| `BrokenNoReserveAhead.cfg` | `WithinCeiling` violated | 130 | 7 |
| `ReachabilityMergeBesideFolds.cfg` | `NoMergeBesideFolds` violated | 90 | 5 |
| `ReachabilitySpill.cfg` | `NoSpill` violated | 85 | 5 |

- `BrokenNoReserveAhead.cfg`: `l1` grows its table to two units and runs out of rows. The budget, four
  units, shows room for the merge's two, and `l1` decides it. `l2` grows its table by one meanwhile,
  into the room the merge only saw. The merge's doubling then takes its two units: five held under a
  budget of four, the 108 % of CI in small.
