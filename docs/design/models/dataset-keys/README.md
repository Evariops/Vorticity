# Data keys across rekeys

`DatasetKeys.tla` models the data keys of an encrypted dataset
([rotation, rekeying and shredding](../../18-encryption.md#44-rotation-rekeying-and-shredding)). Each
version is sealed under a data key, numbered in the order rekeys draw them. A writer seals its object
under the key of the newest commit it has read, and reads the newest one when it refreshes and when it
commits, since a commit reads the version it lands on before it puts its own. A writer may be in flight
while a rekey lands, or idle since before it. A rekeyer publishes versions sealed under a new key that
add nothing.

The property is the one a full rekey relies on: every object a version adds is sealed under that
version's key, so that once every object sealed before the rekey is rewritten and a vacuum has passed,
no version names an object under the replaced key, and that key can be retired.

## What it does not claim

The model counts keys and objects, not bytes, salts or frames, which the sealed format's own tests hold
(tampering, known answers, and `NoTwoSealedObjectsShareASaltAndNoneAfterARekeyTheReplacedKey`). Rebases
of the operations, compactions and vacuum are the other models'. A rekeyer is a writer of its own, and a
writer's own rekey, whose commit takes up the new key at once, is the same race seen from one handle.

## Checked properties

| property | claim |
|---|---|
| `SealedUnderItsVersion` | every object a version adds is sealed under that version's key |

## Configurations

| configuration | purpose |
|---|---|
| `DatasetKeys.cfg` | three writers and two rekeys, the refusal on |
| `BrokenNoRefusal.cfg` | the commit as the code had it, with no look at an object's key. Must violate `SealedUnderItsVersion` |
| `ReachabilityRefusal.cfg` | a commit is refused. Must violate `NoRefusal` |
| `ReachabilityWriteAfterRekey.cfg` | an object lands after a rekey, under the new key. Must violate `NoWriteAfterRekey` |

## Correspondence

Modelled at commit ca4cfb4a, which adds the refusal.

| action | code |
|---|---|
| `Refresh` | a read of a newer commit object, whose key the store takes up (`SealedObjectStore.Remember`, `Adopt`) |
| `Write` | a data object sealed under the current key (`SealedObjectStore.PutAsync`), the generation of keys noted when it is begun (`VortexDataset.StartObjectUnder`) |
| `Commit` | `DatasetCommitter.AttemptAsync`: the version landed on read, then `RequireCurrentKey`, then the creation, sealed under the current key |
| `Rekey` | `VortexDataset.RekeyAsync` |

## TLC evidence

TLC 1.7.4 (`tla2tools.jar`, SHA-256 `936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`),
the same from TLC 1.8.0 (build 2026.11.06), OpenJDK 21.0.13, macOS arm64, 2026-11-07. The traces are TLC
1.8.0's.

| configuration | result | distinct states | depth |
|---|---|---:|---:|
| `DatasetKeys.cfg` | no error | 4 118 | 12 |
| `BrokenNoRefusal.cfg` | `SealedUnderItsVersion` violated | 9 | 4 |
| `ReachabilityRefusal.cfg` | `NoRefusal` violated | 32 | 4 |
| `ReachabilityWriteAfterRekey.cfg` | `NoWriteAfterRekey` violated | 85 | 5 |

- `BrokenNoRefusal.cfg`: a rekey lands, `w1`, which read the dataset before it, seals its object under
  the first key, and its commit, reading the rekey, adds the object to a version of the second. The code
  did this before the refusal: `AWriterInFlightDuringARekeyWritesItsRowsAgainUnderTheNewKey` and
  `AHandleIdleSinceBeforeARekeyLandsNothingUnderTheReplacedKey` failed on it.
- `ReachabilityRefusal.cfg`: the same order, the refusal on: `w1`'s commit is refused.
- `ReachabilityWriteAfterRekey.cfg`: a rekey lands, `w1` refreshes, seals under the new key, and its
  commit goes through.
