# Indexes

What `Auto` builds, what it costs, and when to ask for more by name.

An index here is a **skipping** index: it does not find rows, it proves a block cannot hold them, so
the scan never reads that block. What [statistics-and-pruning.md](statistics-and-pruning.md)
describes is the free version of the same idea — bounds every file carries. An index is what you
add when bounds cannot decide.

## What the default does

`VortexWriteOptions.Indexes` is `WritePolicy.Auto`, and `Auto` is allowed to decline. On a file of
five city names and a temperature:

```
cities, Auto: 396025 bytes, 101 of them indexes
  city vorticity.dict.probe.v1: Built
  city vorticity.bloom.sbbf.v1: Abandoned -- its first 4 blocks hold the same 5 values, so its
    block filters prune nothing and the pass that would fill their root costs more than a default
    write may spend
  celsius vorticity.dict.probe.v1: Built
  celsius vorticity.bloom.sbbf.v1: Abandoned -- 2048 bytes of filters against 65536 raw bytes of
    column, over its share
```

Read the report. `WriteReport.Indexes` is the only place that says what was built, what was given up
and why, and the reason is a sentence, not a code.

## Asking for one

A Bloom filter is for the column you look a value up in: an identifier, with no order, no repeats,
and nothing to compress. On a log of 200 000 session identifiers:

```csharp
VortexWriteOptions options = new VortexWriteOptions
{
    Indexes = WritePolicy.None.For("session", IndexPolicy.Bloom()),
    IndexBudgetPerMille = 500,
};
```

`WritePolicy` is a default plus overrides by column: `For(column, policy)` sets one,
`WithDefault(policy)` sets the rest, `ForKey([a, b], policy)` covers a composite key. `IndexPolicy`
offers `Auto`, `None`, `Bloom(...)`, `NgramBloom(...)`, `Postings`, `NgramPostings(...)` and
`SortedRuns`, and `AsRequired()` marks one you want built.

## What it costs and what it buys

| | bytes |
|---|---|
| no index | 1 387 788 |
| a Bloom at the default false-positive rate | 1 921 129 |
| a Bloom at one false positive in a million | refused: the filters alone would be 1 572 940 |

The filter is 533 008 bytes against 1 363 772 bytes of data — a third of the file. That is the real
trade, and it is why the default budget of 100 per mille turns this one down until you raise
`IndexBudgetPerMille`.

What it buys, on `session = <a value>`:

| | rounds of reading | bytes |
|---|---|---|
| with the index, a value that exists | 6 | 1 823 167 |
| with the index, a value that does not | 5 | 459 511 |
| without it, either way | 27 | 32 815 051 |

Eighteen times less for a hit, seventy for a miss. A skipping index earns most when the answer is
no.

## Reading what a file carries

```csharp
foreach (VortexIndexInfo index in await file.ReadIndexesAsync())
{
    Console.WriteLine($"{index.Column} {index.Kind}: {index.Runs} runs over {index.Blocks} blocks " +
        $"of {index.BlockLength} rows, {index.ListedBytes} bytes listed, layout {index.Layout}");
}
```

```
session vorticity.bloom.sbbf.v1: 1 runs over 25 blocks of 8192 rows, 124 bytes listed, layout FilterTree
```

`ListedBytes` is what the directory lists, not what the index weighs: a `FilterTree` holds the rest
below those regions. `file.Indexes` is the same list without the read, and is `null` until the
directory has been read.

`VerifyIndexesAsync()` checks that the indexes still describe these bytes — `Holds`, plus what was
`Held`, what is `Torn` and what is `Bare`. An index that no longer matches the file is not used.

## Indexing a file you already wrote

```csharp
await VortexFileIndexer.AppendIndexesAsync(path, WritePolicy.None.For("session", IndexPolicy.Bloom()), options);
```

It reads the file, builds the indexes and appends them, leaving the data untouched: 1 387 788 bytes
became 1 922 597, with one index listed. `BuildFragmentAsync` does the same but hands you the bytes
instead of writing them, for an index kept beside the file and passed back at open through
`VortexReadOptions.IndexFragments`.

## Watch out

* **An index that is abandoned is not free.** On this file, asking for a Bloom that the budget then
  refuses leaves 262 368 bytes in it and **no index listed** — the filters are written before the
  budget is checked, and the abandoned ones stay. Check `report.Indexes` and rewrite without the
  policy if the bytes matter. The same happens through `AppendIndexesAsync`: +263 788 bytes, nothing
  listed.
* **`AsRequired()` does not make the write fail.** A required policy that cannot be built is still
  abandoned, with its reason in the report; nothing throws.
* `WithIndexes(false)` on a scan ignores them, which is the way to measure what one is worth.
* A dictionary probe is built for a column the writer dictionary-encoded, and abandoned for one it
  did not — the index follows the encoding, not your request.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- indexes
```
