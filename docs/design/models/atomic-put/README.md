# The store contract

`AtomicPut.tla` writes down what a dataset asks of its store
([the store abstraction](../../13-dataset.md#11-the-store-abstraction)): a key holds nothing or one
whole object, a creation succeeds only on a free key, and an object leaves its key only by a removal
that names it, which only vacuum makes. Each object is named by the put that wrote it, since that is
what a put's answer is about.

It is the module the other models are checked against. `ConditionalPut` and `FilePut` instantiate it
over their own state and check that what they do is one of its steps, and the dataset's models take
the store through it.

## What it does not claim

It says nothing of time, listings, reads of ranges or tokens. A listing that lags, which the dataset
tolerates, belongs to the lifecycle model.

## Checked properties

| property | claim |
|---|---|
| `TypeOK` | each key holds nothing or one put's object |
| `NeverReplaced` | an object is never replaced by another in one step: its key goes back to free first |

## Configurations

| configuration | purpose |
|---|---|
| `AtomicPut.cfg` | two keys, two puts, removals allowed: the contract is consistent |

## TLC evidence

TLC 1.7.4 (`tla2tools.jar`, SHA-256 `936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88`)
on OpenJDK 21.0.13, macOS arm64, 2026-11-07: `AtomicPut.cfg` passes, 9 distinct states, depth 3.
