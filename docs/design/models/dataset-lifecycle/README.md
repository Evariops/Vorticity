# The retention window, vacuum and the clocks

`DatasetLifecycle.tla` models what a dataset's safety owes to clocks ([the latest version in one
request](../../13-dataset.md#83-the-latest-version-in-one-request) and [the
lifecycle](../../13-dataset.md#10-lifecycle)). Three mechanisms count time. A handle that knew its
version less than half a retention window ago builds on it, or asks for the versions after it, the
first one missing ending the run, rather than list the commits. A commit refuses to name objects whose
first put began half the window ago or more. Vacuum keeps the latest version and those superseded less
than a window ago, by the store's date of the next commit, and deletes what nothing marked once it is a
window old, by its own clock against the store's dates.

Time is a count of ticks of the store's clock, which dates every object. A writer compares two
readings of its own clock, so an offset from the store's cancels out. What does not is a reading that
falls behind an earlier one, a clock stepped back or a pause between a reading and the request it
decides, up to `WriterDrift` ticks. The vacuum's clock is off the store's by up to `VacuumSkew` ticks
either way, at each pass, and a listing may miss a version created less than `ListingLag` ticks ago.

Three writers each upload an object and commit it after any delay, lose races to one another and move
past the keys they lost. In the deep configurations they also refresh whenever they like. Vacuum runs
one or two passes, each a mark and a sweep, between which the writers act.

## What it does not claim

- Each version names its own commit object and the first one, whose pages no commit rewrites. Vacuum
  keeps the first and deletes the later ones it does not retain, which leaves gaps below the window,
  the case the trust span must survive. Pages shared otherwise keep more commit objects and leave fewer
  gaps.
- A writer puts one object. A job of many objects is a writer whose first object is its oldest, which
  is what the commit measures from.
- A commit is one step from its reading of the clock to its put. A pause between them is a reading that
  fell behind, which `WriterDrift` stands for.
- The window is four ticks, the trust span and the guard two. Versions kept by number
  (`RetainedVersions`) and objects under a store's lock are left out, since both only keep more.

## Checked properties

| property | claim |
|---|---|
| `LatestReadable` | the latest version names only objects and commit objects the store holds |
| `NoFork` | no commit creates a key below the latest version, or a key vacuum had deleted |
| `RetainedReadable` | a version superseded less than the window ago, less the vacuum's skew, names only what the store holds |

## The bound

With the trust span and the guard at half the window, the configurations pass while the vacuum's skew,
the writer's drift and the listing's lag add up to half the window at most, and fail one tick past it.
In ticks, a lag under `ListingLag` ticks is at most `ListingLag - 1` of them. These runs explored the
bound with TLC 1.8.0 on four workers, three writers, a window of four ticks, eight ticks of time and two
passes. They are not configurations of the manifest.

| vacuum skew | writer drift | listing lag under | sum | verdict | distinct states |
|---:|---:|---:|---:|---|---:|
| 1 | 1 | 0 | 2 | no error | 4 278 231 |
| 2 | 0 | 0 | 2 | no error | 3 326 985 |
| 0 | 2 | 0 | 2 | no error | 4 077 835 |
| 1 | 0 | 2 | 2 | no error | 3 229 706 |
| 0 | 0 | 3 | 2 | no error | 2 424 368 |
| 2 | 1 | 0 | 3 | `LatestReadable` violated | 4 471 |
| 1 | 2 | 0 | 3 | `LatestReadable` violated | 9 019 |
| 3 | 0 | 0 | 3 | `LatestReadable` violated | 2 031 |
| 0 | 3 | 0 | 3 | `LatestReadable` violated | 15 675 |
| 1 | 1 | 2 | 3 | `LatestReadable` violated | 10 366 |
| 0 | 0 | 4 | 3 | `LatestReadable` violated | 15 750 |

Each half has its own bound. With a trust span of one tick, a skew of one and a drift of two never
fork (7 179 894 distinct states), where a trust span of two forks: the trust span holds while it,
the skew and the drift add up to the window at most. The guard stays at half the window there, and
`LatestReadable` still fails, so the guard's bound is the same sum with the guard in the trust span's
place. A listing that lags costs the trust span only through a refresh, which can take a passed version
for the latest and trust it: without refreshes, a lag under four ticks never forks, and with them a lag
under four ticks does (`DeepBrokenStaleRefresh.cfg`).

## Configurations

| configuration | purpose |
|---|---|
| `DatasetLifecycle.cfg` | three writers, the vacuum's clock a tick off either way and a writer's a tick behind, two passes |
| `DatasetLifecycleLag.cfg` | a listing that may miss a version created less than two ticks ago, with the vacuum's clock a tick off |
| `DeepRefreshes.cfg` | the first, with writers that refresh, which takes minutes |
| `DeepRefreshesLag.cfg` | writers that refresh over a listing that may miss a version created less than three ticks ago |
| `BrokenNoGuard.cfg` | the commit as the code had it, without the guard. Must violate `LatestReadable` |
| `BrokenTrustSpan.cfg` | a trust span of three ticks of four. Must violate `NoFork` |
| `BrokenVacuumSkew.cfg` | the vacuum's clock three ticks ahead. Must violate `LatestReadable` |
| `BrokenWriterDrift.cfg` | a writer's clock three ticks behind its earlier reading. Must violate `NoFork` |
| `BrokenListingLag.cfg` | a listing that may miss a version created less than four ticks ago. Must violate `LatestReadable` |
| `DeepBrokenStaleRefresh.cfg` | a refresh over that listing trusts a passed version. Must violate `NoFork` |
| `ReachabilityCommitDeleted.cfg` | vacuum deletes a commit object below the window. Must violate `NoCommitDeleted` |
| `ReachabilityRefusal.cfg` | the guard turns a writer away. Must violate `NoRefusal` |
| `ReachabilityCommitAcrossVacuum.cfg` | vacuum sweeps between a writer's put and its commit, which goes through. Must violate `NoCommitAcrossVacuum` |

## Correspondence

Modelled at commit 36983254.

| action | code |
|---|---|
| `Upload` | an object started, the handle's clock read first (`VortexDataset.StartObjectUnder`, `VortexDataset.cs:903`), then put and dated by the store |
| `Commit` | one attempt of `DatasetCommitter.AttemptAsync`: the reading and the held version built on outright while trusted (`DatasetCommitter.cs:212-221`), `LatestAsync` otherwise (`:466-489`), the guard on the operations that apply (`:408-409`, `RequireRecentPuts` at `:437-450`), the creation, and the search past a key another writer took (`:422-423`) |
| `After`, `NewestFrom` | `AfterAsync` (`:506-525`) and `NewestFromAsync` (`:547-579`) |
| `Listed` | `NewestVersionAsync` (`:605-611`), the first key a listing of `commit/` yields |
| `Refresh` | `VortexDataset.RefreshAsync` (`VortexDataset.cs:579-591`) |
| `Mark` | `DatasetVacuum.SweepAsync`: its clock read once (`DatasetVacuum.cs:140`), the latest listed (`:142`), the commits after it left alone (`:158`), the versions retained by the date of the next commit (`:165-174`), then the marking |
| `Sweep` | the deletes of what nothing marked once the window has passed by the clock read at the start (`:371-380`) |

## TLC evidence

Verdicts from TLC 1.7.4 (`tla2tools.jar`, SHA-256
`936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`), the same from TLC 1.8.0 (build
2026.11.06), on OpenJDK 21.0.13, macOS arm64, 2026-11-07, one worker each. The traces are TLC 1.8.0's.

| configuration | result | distinct states | depth | time |
|---|---|---:|---:|---:|
| `DatasetLifecycle.cfg` | no error | 4 278 231 | 21 | 80 s |
| `DatasetLifecycleLag.cfg` | no error | 1 994 120 | 20 | 34 s |
| `DeepRefreshes.cfg` | no error | 8 302 721 | 18 | 332 s |
| `DeepRefreshesLag.cfg` | no error | 12 245 761 | 19 | 330 s |
| `BrokenNoGuard.cfg` | `LatestReadable` violated | 709 | 9 | 1 s |
| `BrokenTrustSpan.cfg` | `NoFork` violated | 423 879 | 13 | 5 s |
| `BrokenVacuumSkew.cfg` | `LatestReadable` violated | 722 | 6 | 1 s |
| `BrokenWriterDrift.cfg` | `NoFork` violated | 900 510 | 14 | 13 s |
| `BrokenListingLag.cfg` | `LatestReadable` violated | 6 591 | 9 | 1 s |
| `DeepBrokenStaleRefresh.cfg` | `NoFork` violated | 7 511 428 | 15 | 107 s |
| `ReachabilityCommitDeleted.cfg` | `NoCommitDeleted` violated | 85 241 | 11 | 2 s |
| `ReachabilityRefusal.cfg` | `NoRefusal` violated | 145 | 5 | 1 s |
| `ReachabilityCommitAcrossVacuum.cfg` | `NoCommitAcrossVacuum` violated | 120 | 5 | 1 s |

- `BrokenNoGuard.cfg`: `w1` puts its object at tick 0 and waits. At tick 4 vacuum marks from version 1,
  which does not name the object, and deletes it, a window old. `w1` then commits version 2, which
  names it. With the guard, `w1` refuses from tick 2. The same sequence in the code, before the guard,
  published a version whose scan threw `ObjectNotFoundException`, and
  `AVacuumBetweenAPutAndItsCommitLeavesTheLatestVersionReadable` now holds it to the guard.
- `BrokenTrustSpan.cfg`: `w1` creates version 2 at tick 0. `w2`, holding version 1, loses key 2, reads
  version 2 and creates version 3, at tick 0 too. At tick 3 vacuum, its clock a tick ahead, keeps
  versions 1 and 3 and deletes version 2, superseded a window ago by its clock. `w3` still holds version
  1, learned at tick 0, and its clock reads tick 2: inside a trust span of three ticks, it builds on
  version 1 and creates key 2 again, below version 3.
- `BrokenVacuumSkew.cfg`: `w1` puts its object at tick 0. At tick 1 vacuum, its clock three ticks ahead,
  reads tick 4 and deletes the object, a window old by its clock. `w1` commits at tick 1, well inside
  the guard, and version 2 names a deleted object.
- `BrokenWriterDrift.cfg`: versions 2 and 3 are created at tick 0, as above. At tick 4 vacuum deletes
  version 2. `w3`, holding version 1 since tick 0, reads its clock three ticks behind, tick 1, trusts
  version 1 and creates key 2 again.
- `BrokenListingLag.cfg`: `w1` puts its object at tick 0 and commits version 2 at tick 1. At tick 4
  vacuum's listing, which may miss what was created less than four ticks ago, shows version 1 as the
  latest. Vacuum marks from it and deletes the object, a window old, which version 2 names.
- `DeepBrokenStaleRefresh.cfg`: versions 2 and 3 are created at tick 0. At tick 3 `w3` refreshes past
  its trust span, so it lists, and the listing, which may miss what was created less than four ticks
  ago, shows version 1 as the latest. At tick 4 vacuum keeps versions 1 and 3 and deletes version 2.
  `w3` trusts version 1, learned a tick ago, builds on it and creates key 2 again.
- `ReachabilityCommitDeleted.cfg`: versions 2 and 3 are created at tick 0, and at tick 3 vacuum, its
  clock a tick ahead, keeps versions 1 and 3 and deletes version 2: the passing configurations do meet
  gaps below the window.
- `ReachabilityRefusal.cfg`: `w1` puts its object at tick 0 and commits at tick 2, half the window
  later, and the guard turns it away.
- `ReachabilityCommitAcrossVacuum.cfg`: vacuum marks, `w1` puts its object, vacuum sweeps, and `w1`'s
  commit goes through: the guard lets a commit across a vacuum when its objects are young.
