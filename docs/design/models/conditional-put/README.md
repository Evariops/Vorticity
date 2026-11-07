# A put-if-absent whose answers are lost

`ConditionalPut.tla` models the conditional put every HTTP store of `Vorticity.ObjectStorage` makes
([writes whose outcome is unknown](../../19-object-storage.md#64-timeouts-retries-and-writes-whose-outcome-is-unknown)),
as `src/Vorticity.ObjectStorage/Http/Upload.cs` writes it in `SettleAsync`, `LearnAsync` and
`RecheckAsync`. Every attempt carries the put's random id in the object's metadata. When an attempt's
answer is lost, a head says whose object the key holds. A refusal that follows a lost answer is
checked the same way, since the lost attempt may be the one that took the key.

The service applies a request whenever it likes: before its answer is lost, after, and after the head
that followed it, which the design allows. It may also drop a request without applying it. Two puts
race for one key, each with two attempts.

## What it does not claim

A whole put and the completion of an upload in parts are one attempt each: the parts themselves, and
an upload the service forgets, are left out. The put id is assumed unique, as 16 random bytes make it.
Nothing here models the dataset's reaction to an answer, which the dataset's models do.

## Checked properties

| property | claim |
|---|---|
| `TypeOK` | the variables keep their types |
| `Truthful` | an answer `created` means the key holds this put's object, `exists` another put's, and `failed` that this put's object will never land: no attempt of it is still held by the service |
| `EveryPutEnds` | under weak fairness of each put's own steps, every put answers |
| `StoreSafety` | the key, seen through [the store contract](../atomic-put/README.md), is created once and never replaced |

## Configurations

| configuration | purpose |
|---|---|
| `ConditionalPut.cfg` | the put once fixed: a budget that ends after lost answers says the outcome is unknown. Checks every property |
| `BrokenGiveUpAsFailed.cfg` | the put before the fix, which said "did not land" when the budget ended. Must violate `Truthful` |
| `BrokenNoRecheck.cfg` | a refusal after a lost answer taken as another put's, the danger the design names. Must violate `Truthful` |
| `ReachabilityLateApply.cfg` | an attempt lands after the head that found the key free. Must violate `NoLateApply` |

## Correspondence

Modelled at commit 3da99dca.

| action | code |
|---|---|
| `Send` | an attempt of `PutWholeAsync`, or the completion of an upload in parts |
| `Answer` | `SettleAsync`, the answer of the attempt (`Upload.cs:513-515`) |
| `Lost` | a `StoreAnswerLostException` caught by `SettleAsync` (`:497`) |
| `Head` | `LearnAsync` (`:521-556`), then `RetryAfterAsync` and the end of the budget (`:505-507`) |
| `Recheck` | `RecheckAsync` (`:563-581`) |
| `HeadFails` | a head that fails, which says the outcome is unknown (`:529-536`, `:570-577`) |

## TLC evidence

TLC 1.7.4 (`tla2tools.jar`, SHA-256 `936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`)
on OpenJDK 21.0.13, macOS arm64, 2026-11-07.

| configuration | result | distinct states | depth |
|---|---|---:|---:|
| `ConditionalPut.cfg` | no error, liveness and refinement included | 8 044 | 17 |
| `BrokenGiveUpAsFailed.cfg` | `Truthful` violated | 123 | 7 |
| `BrokenNoRecheck.cfg` | `Truthful` violated | 296 | 8 |
| `ReachabilityLateApply.cfg` | `NoLateApply` violated | 29 | 5 |

The trace of `BrokenGiveUpAsFailed.cfg` is the defect the fix removed: `Send`, `Lost`, `Head` (the key
free, the put sent again), `Send`, `Lost`, `Head` (the key still free, the budget spent), and the put
answers `failed` while both attempts are still held by the service, either of which may land. The
store now throws an `ObjectStoreException` whose `OutcomeUnknown` is true there, and
`UploadTests.APutWhoseLastAttemptMayStillLandSaysItsOutcomeIsUnknown` holds the code to it with the S3
emulator.
