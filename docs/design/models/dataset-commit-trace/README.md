# Runs of the code, judged by the model of the commit

The [model of the commit](../dataset-commit/README.md) proves its properties about itself. These
configurations check that the code does what it says: runs of the real dataset, each the versions it
created, are given to TLC, which accepts a run only if the model can create those versions in that
order. It is trace validation as Cirstea, Kuppe, Merz and Loillier describe it for TLA+, at the edge of
the store, where every effect of a commit shows.

## How a run is made

`DatasetScheduleTests` runs a scenario of the model on the real code: a dataset whose first version
holds one row of key 2 in level 1, appenders of one row each, a deleter of a key's rows, a compactor of
level 0 into a level above. A store holds each commit's put until a scheduler releases it, and a writer
that deletes or compacts waits before it refreshes and plans, so that the scheduler decides every order
in which plans and puts interleave. The test runs the scenario once for each order, 26, 20 and 835 of
them for its three scenarios, and checks the model's properties on every version of every run: no row
read twice, no acknowledged row lost, no deleted row brought back, no two objects of a level above 0
sharing a key.

Each run's versions are then read back from the store and written in the model's terms. An object is
named after the writer whose commit first named it, `<<"append", "a1">>` or `<<"job", "c1", 2>>`, its
rows after the appenders, read from the object itself, its marked rows from its deletion vector, and
its keys from its rows. `DatasetCommitTrace.tla` constrains the model's history to follow those
versions, and leaves the rest of the model's state, the writers' plans, reads, attempts and keys, for
TLC to search.

The scenarios turn off what the code does and the model does not plan: a delete that rewrites an object
rather than marks it (`MarkedShare` at 1), a purge of marks, a merge of neighbours, a level 0 object
moved whole. Everything else is the code's own.

## Checked properties

| property | claim |
|---|---|
| `NotFollowed` | the model has not yet created every version of the run. Violated, exit 12, when it has: the run is a behaviour of the model. Held, exit 0, when no behaviour creates the run |

## Configurations

| configuration | purpose |
|---|---|
| `RunAppends.cfg` | two appends and two jobs, the second merging an append with the seed. Must violate `NotFollowed` |
| `RunDelete.cfg` | a delete that takes the seed out whole, then an append and a job. Must violate `NotFollowed` |
| `RunMarks.cfg` | two appends merged with the seed by a job, then a delete that marks two of the three rows. Must violate `NotFollowed` |
| `RejectedResurrection.cfg` | `RunMarks` followed by a job that writes the marked rows back, made by hand. Must hold `NotFollowed`, exit 0 |

`DatasetCommitRuns.tla` holds the three runs the test recorded, out of the 53 distinct ones its three
scenarios produce, and the one made by hand.

## Running every run

```sh
dotnet build tests/Vorticity.Tests -c Release
VORTICITY_MODEL_TRACES=/tmp/runs tests/Vorticity.Tests/bin/Release/net11.0/Vorticity.Tests \
  -class Vorticity.Tests.Dataset.DatasetScheduleTests
JAVA=/path/to/java tools/models.sh /tmp/runs/appends /tmp/runs/delete /tmp/runs/marks
```

The test writes each distinct run of each scenario as a definition, a configuration per run, and the
exit code each must give, 12, into a directory per scenario, which `tools/models.sh` checks as it
checks the models.

## TLC evidence

TLC 1.7.4 (`tla2tools.jar`, SHA-256 `936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`),
OpenJDK 21.0.13, macOS arm64, 2026-11-07, the code at commit 36983254.

| configuration | result | distinct states | depth |
|---|---|---:|---:|
| `RunAppends.cfg` | `NotFollowed` violated | 285 | 13 |
| `RunDelete.cfg` | `NotFollowed` violated | 161 | 10 |
| `RunMarks.cfg` | `NotFollowed` violated | 1 654 | 13 |
| `RejectedResurrection.cfg` | no error | 2 674 | 26 |

Every one of the 53 distinct runs is accepted: 10 of the appends, 6 of the delete and 37 of the marks,
each in a second or two.

The validation bites, and found a gap in the model. The model of the commit, until this check, had a
delete mark every row it took. The code takes an object out of the version whole when a delete takes
every row left in it, its bounds proving it or not (`DatasetRowChanges.cs:161-165`, `:305-308`). Against
that model, 41 of the 43 runs with a delete were rejected, the first at the version where the seed, of
key 2 alone, left the tree rather than staying marked. The model now takes such an object out whole,
its configurations still pass, and its mutations still bite, with two appenders where a delete must
mark an object of two keys.
