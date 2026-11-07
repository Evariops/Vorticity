# The file store's creation of a commit

`FilePut.tla` models how `FileObjectStore` creates a key, what the reads that meet the creation see,
and what `VortexDataset.RemoveTornCommitAsync` does beside it, for one key, two puts, one reader and
the repair. Four protocols are compared.

- `claim` is the store at 3da99dca, for every key. `open` with `O_CREAT|O_EXCL` claims the key, and
  the exclusive `flock` that stands for `FileShare.None` follows as a second system call: .NET emulates
  file sharing on Unix that way, and its own source says the lock is "not atomic with file opening".
  The content is written in place. A failure closes the file, which drops the lock, then deletes the
  key by its path. A lock refused after the claim leaves the claimed file, and the store, finding the
  key's file there, answers that the key exists.
- `whole` is the store since the fix, for a commit's key. The commit is written to a file beside the
  key that no other process names, then published in one system call that the kernel refuses when the
  key names a file: an exclusive rename (`renamex_np` with `RENAME_EXCL` on macOS, `renameat2` with
  `RENAME_NOREPLACE` on Linux), a link where the file system has no such rename, and on Windows a move
  that does not replace.
- `claimfix` is the store since the fix for every other key, and for a commit on a file system that
  can neither rename exclusively nor link: the claim, with a failed put's file deleted while its lock
  still keeps the others out, and a lock refused right after the claim failing the put rather than
  answering that the key exists.
- `move` is the tempting fix that is not one: `File.Move(temp, key, overwrite: false)`, which .NET runs
  on Unix as `lstat` of the key followed by `rename(2)`, two steps.

A reader opens the key, takes its shared lock as a second step, and reads the file it opened, whatever
the key names by then. Refused the lock, it opens again, once. The repair lists the newest commit,
reads it the same way and, finding it not whole, deletes the key by its path. A put may die at any
step: the kernel drops its locks and its files stay.

## What it does not claim

The model has one key that two puts race for and a reader reads before it is complete, which is what
a commit's key is. A data object's key, or a rollup's, is unique to its put, and nothing reads it
before a commit names it: neither race exists there, and `claimfix` holds for it with no repair in
the picture, the repair looking at commits only. Durability across a power loss, which
`FileObjectStore.Durable` and the file system decide, is outside the model, and so are the store's
held files and tokens. A commit claimed in place, on a file system that can neither rename
exclusively nor link, is safe only while the repair does not run beside the writers, which
`BrokenFallbackWithRepair.cfg` records.

## Checked properties

| property | claim |
|---|---|
| `CreatedStays` | a put that answered `created` leaves its whole object under the key, since nothing in the model removes the newest commit on purpose |
| `ExistsMeansAnother` | a put answers `exists` only when another put's file was under the key |
| `TornOnlyWhenAbandoned` | a read that finds the key's file not whole finds one whose writer died or gave up, never a commit being written: the claim the code made in `VortexDataset.cs:601-603` |
| `StoreSafety` | the key, seen through [the store contract](../atomic-put/README.md), holds an object only once it is whole, and keeps it |

## Configurations

| configuration | purpose |
|---|---|
| `FilePut.cfg` | `whole`, with locks, the repair and crashes: every property holds |
| `FilePutWithoutLocks.cfg` | `whole` on a file system without `flock`, or under `DOTNET_SYSTEM_IO_DISABLEFILELOCKING`: every property holds, since one step needs no lock |
| `FilePutFallback.cfg` | `claimfix`, without the repair: no answer lies |
| `BrokenFallbackWithRepair.cfg` | `claimfix` with the repair. Must violate `CreatedStays`: the limit of a commit claimed in place |
| `BrokenClaimCleanup.cfg` | `claim` with the repair. Must violate `CreatedStays` |
| `BrokenClaimExists.cfg` | `claim` with a reader. Must violate `ExistsMeansAnother` |
| `BrokenClaimTorn.cfg` | `claim` with a reader, locks working. Must violate `TornOnlyWhenAbandoned` |
| `BrokenClaimWithoutLocks.cfg` | `claim` without `flock`, with the repair. Must violate `CreatedStays` |
| `BrokenMove.cfg` | `move`. Must violate `CreatedStays` |
| `ReachabilityLockGap.cfg` | `claim`: a reader gets between a put's open and its lock. Must violate `NoLockGap` |
| `ReachabilityRacingPublish.cfg` | `whole`: two puts written whole race to publish into the free key. Must violate `NoRacingPublish` |

## Correspondence

`claim` is modelled at commit 3da99dca, the others at the commit that brought the fix.

| action | code |
|---|---|
| `ClaimOpen`, `ClaimLock` | `File.OpenHandle(key, CreateNew, FileShare.None)` in `PutAsync`, which .NET runs as `open` then `flock` (`SafeFileHandle.Unix.cs`, `Init`), and since the fix the refused lock told apart by `HeldByAPut` |
| `Size`, `Finish`, `Fail` | `RandomAccess.SetLength`, the writes, and a failure among them (`WriteAsync`) |
| `Release`, `Unlock`, `Cleanup` | the `using` that closes the file, and `TryDelete` by the key's path, after the close at 3da99dca and before it since the fix |
| `TempOpen`, `Discard`, `Show` | the file beside the key, its deletion on failure, and `Publish` by `RenameExclusive` or `Link` |
| `MoveCheck`, `MoveRename` | `File.Move(temp, key, overwrite: false)` on Unix (`FileSystem.Unix.cs`, `MoveFile`) |
| `ROpen`, `RLock`, `RRead` | `OpenToReadAsync`, its retries while a put holds the file, and the read |
| `XList` to `XDelete` | `RemoveTornCommitAsync`: `NewestVersionAsync`, `IsWholeAsync`, then `DeleteAsync` of the key (`VortexDataset.cs:606-616`) |

## TLC evidence

TLC 1.7.4 (`tla2tools.jar`, SHA-256 `936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`)
on OpenJDK 21.0.13, macOS arm64, 2026-11-07.

| configuration | result | distinct states | depth |
|---|---|---:|---:|
| `FilePut.cfg` | no error, refinement included | 1 060 | 16 |
| `FilePutWithoutLocks.cfg` | no error, refinement included | 840 | 14 |
| `FilePutFallback.cfg` | no error | 930 | 16 |
| `BrokenFallbackWithRepair.cfg` | `CreatedStays` violated | 1 726 | 11 |
| `BrokenClaimCleanup.cfg` | `CreatedStays` violated | 1 478 | 11 |
| `BrokenClaimExists.cfg` | `ExistsMeansAnother` violated | 34 | 5 |
| `BrokenClaimTorn.cfg` | `TornOnlyWhenAbandoned` violated | 35 | 5 |
| `BrokenClaimWithoutLocks.cfg` | `CreatedStays` violated | 592 | 9 |
| `BrokenMove.cfg` | `CreatedStays` violated | 150 | 11 |
| `ReachabilityLockGap.cfg` | `NoLockGap` violated | 7 | 3 |
| `ReachabilityRacingPublish.cfg` | `NoRacingPublish` violated | 219 | 7 |

The traces of the store before the fix:

- `BrokenClaimCleanup.cfg`: `ClaimOpen` (put `a` claims the key, its file empty), the repair's `XList`,
  `XOpen`, `XLock`, `XRead` (it reads the empty file before `a` locks it, and judges it torn),
  `ClaimLock`, `Size`, `Finish`, `Release` (`a` answers `created`), then `XDelete`: the repair deletes
  the key, and the commit `a` was told it created is gone. No put failed for it, the repair only ran
  while a commit was being written.
- `BrokenClaimExists.cfg`: `ClaimOpen`, `ROpen`, `RLock` (a reader takes its shared lock on `a`'s empty
  file), then `ClaimLock`: `a`'s lock is refused, and it answers that the key exists, its own empty
  claim under it. A dataset then rebases onto that file, reads it torn, and stops until someone runs
  `RemoveTornCommitAsync`.
- `BrokenClaimTorn.cfg`: `ClaimOpen`, `ROpen`, `RLock`, `RRead`: a refresh reads the commit `a` is about
  to write as torn, locks working.
- `BrokenClaimWithoutLocks.cfg`: without locks, the repair reads the commit `a` is writing, judges it
  torn, and deletes it once `a` has answered `created`.

The trace of `BrokenMove.cfg` is why the fix calls an exclusive rename or a link itself: both puts find
the key free in `MoveCheck`, then both rename, and the second rename replaces the commit the first put
was told it created. The trace of `BrokenFallbackWithRepair.cfg` is the one of
`BrokenClaimCleanup.cfg`: a commit claimed in place keeps the gap between the claim and the lock, so
the repair must not run beside writers on a file system that can neither rename exclusively nor link.

## What the fix costs, and what holds the code to the model

A commit shown whole costs a rename more than a claim. On an M4 Pro under macOS, whose endpoint agent
inspects every file operation, a creation of 4 KiB took about 285 µs shown by an exclusive rename,
180 µs claimed in place and 530 µs shown by a link and an unlink, measured over 2 000 creations a mode
in four alternations. A data object, claimed in place as before, costs what it did, so a commit of
the dataset costs one rename more, about a tenth of a millisecond on that machine.

`FileObjectStorePutTests` holds the code to the model, for each way of showing a commit: a commit
being written is not under its key, a put that fails leaves nothing under or beside its key, one put
of many creates the key, and the repair leaves a commit being written alone. Against the code at
3da99dca, three of them fail, the last after waiting ten seconds for the lock of the commit being
written.
