# Indexes

Ask the writer for an index, read in the report whether it was built, and check that a filter uses
it.

```csharp
VortexWriteOptions options = new()
{
    Indexes = IndexPolicy.None
        .Bloom(Hit.ColumnNames.Session)
        .NgramBloom(Hit.ColumnNames.Path)
        .WithBudgetPerMille(300),
};
```

`Hit` is `[VortexRecord] public partial record struct Hit(string Session, string Path, int Status, int Score)`,
and the sample writes 400 000 of them: a twelve-character session identifier, a path, an HTTP status,
a score, none of them in order. The zone maps and the string bounds every file carries can prune
nothing on such columns; that is where an index earns its bytes. [statistics-and-pruning.md](statistics-and-pruning.md)
is the free kind of pruning.

## What the report says

```
Bloom on Session, NgramBloom on Path, budget 300 per mille: 5310908 bytes, 932416 of them indexes
  Session vorticity.bloom.sbbf.v1: Built, 820056 bytes
  Path vorticity.bloom.ngram3.v1: Built, 111448 bytes
```

`report.Indexes` has one entry per index the policy asked for: the column, the kind as the file
names it, `Built` or `Abandoned`, the reason when it was abandoned, and the bytes it takes. It is the
only place that says what was given up and why, and the reason is a sentence.

## What it buys

The sample asks each filter for its plan with and without the indexes (`ScanOptions.UseIndexes = false`):

| filter | rows | blocks read, with | bytes read, with | without |
|---|---|---|---|---|
| `Session == ` a value that exists | 1 | 1 of 49 | 1 163 616 | 49 blocks, 4 365 728 bytes |
| `Session == ` a value that does not | 0 | 0 of 49 | 803 596 | 49 blocks, 4 365 728 bytes |
| `Path.Contains("checkout")` | 10 | 10 of 49 | 3 681 912 | 49 blocks, 4 365 728 bytes |

A skipping index does not find rows; it proves that a block cannot hold them, so the block is never
read. The Bloom filters on `Session` cost 820 KB against 4.4 MB of data and cut the read almost four
times for a hit and five times for a miss; the filters are read whole, 804 KB of the 1.16 million
bytes. The n-gram filters are cheaper and serve `Contains` and `Like`. `plan.Pruning` names the
structure that pruned each block: `bloom filter pruned 48 reading 803596 bytes`.

A block an index keeps is read by its chunk: the writer seals whole blocks into chunks, four here,
and a column's chunk is one segment. The ten blocks `Contains` keeps fall in ten of the file's
thirteen chunks, which is why it reads most of the file. A file read mostly for a few rows at a time
is one to write with smaller chunks, `ChunkTargetBytes` ([blocks-and-chunks.md](blocks-and-chunks.md)).

## The kinds

| policy | index | serves |
|---|---|---|
| `Bloom(column, falsePositiveRate)` | a split-block Bloom filter per block | equality and `In` |
| `NgramBloom(column)` | a Bloom filter of trigrams per block | `Contains` and `Like` on a text column |
| `Postings(column)` | value to blocks, exact | equality and `In` on a column of few values |
| `SortedRuns(column)` | value to rows, sorted | equality, and the key cursor over an unsorted column |
| `ForKey(columns, kind, encoder)` | sorted runs over a tuple's row encoding | a composite key |

The locating kinds, measured on the same rows:

```
Postings on Status, SortedRuns on Score, budget 3000 per mille: 6232421 bytes, 1853905 of them indexes
  Status == 301: 16 rows; 16 of 49 blocks and 4287212 bytes with the indexes (… locating index pruned 33 reading 1184 bytes), 49 blocks and 4365728 bytes without
  Score == a value that exists: 1 rows; 1 of 49 blocks and 692068 bytes with the indexes (… locating index pruned 48 reading 333200 bytes), 49 blocks and 4365728 bytes without
  Score between 1000 and 1100: 38 rows; 36 of 49 blocks and 4365728 bytes with the indexes (… sorted runs pruned 13 reading 0 bytes), 49 blocks and 4365728 bytes without
  a key cursor on Score: the first key at or after 1000 is 1001, at row 280415; 382 rows hold a smaller one
```

Postings on four statuses take 1 184 bytes and find the sixteen rows of a rare one, in sixteen
blocks that spread over nearly every chunk. Sorted runs take 1.85 MB, two fifths of the data: they
list every row. They prune an equality, and they are what lets
`Scan<Hit>().Keys(r => r.Score)` open a cursor over a column that is not sorted; without them
`OpenAsync` throws `VortexUnsupportedException` naming the index to build
([keys-in-order.md](keys-in-order.md)). They prune a range filter too: `Between` kept the 36 blocks
that hold its 38 rows. The bytes did not drop, since every segment holds one of those blocks, and
the step reports 0 bytes because sorted runs are read through the index's own source and the
file's run cache, which the plan does not count.

An index can also be added to a file already written, in place:

```csharp
IReadOnlyList<IndexWriteReport> added = await VortexFileIndexer.AppendIndexesAsync(
    later, IndexPolicy.None.SortedRuns(Hit.ColumnNames.Score).WithBudgetPerMille(3_000), ct);
```

```
  added afterwards: vorticity.sorted.runs.v1 Built, 1850040 bytes; the file grew from 4378452 to 6234823 bytes; the cursor finds 1001 at row 280415
```

The data stays where it is; the file's tail is written again with the index regions after the last
chunk, and the file takes a new identity. The index is the one the write would have built, 1 850 040
bytes, and the file grew by 1 856 371: the directory and the tail written again are the difference.
`VortexFileIndexer.BuildFragmentAsync`
builds the same index into an `IndexFragment` instead and leaves the file untouched: a reader passes
it with `VortexOpenOptions.IndexFragments`, which is how an index reaches a file that cannot be
rewritten.

A composite key names its encoder, from the row-encoding package:

```csharp
Indexes = IndexPolicy.None
    .ForKey([Hit.ColumnNames.Status, Hit.ColumnNames.Score], IndexKind.SortedRuns, new RowKeyEncoder())
    .WithBudgetPerMille(3_000),
```

It was built, 1.40 MB. No scan of the typed surface reads it: `Status == 301 & Score >= 1000` read
49 of 49 blocks, and `Keys` takes one column. It is how a dataset's clustering key is written
([datasets.md](datasets.md)). Without an encoder the key is abandoned: *no key encoder: declare the
key with IndexPolicy.ForKey(columns, kind, encoder), e.g. with the RowKeyEncoder of
Vorticity.RowEncoding*. [row-keys.md](row-keys.md) is the encoding.

## Auto, and the budget

`IndexPolicy.Auto` tries the cheap indexes on every column and keeps them only where the statistics
and its share of the budget say they pay. On these rows:

```
IndexPolicy.Auto: 4378617 bytes, 101 of them indexes
  Session vorticity.bloom.sbbf.v1: Abandoned, 0 bytes -- Auto gave it up: 16384 bytes of filters against 229376 raw bytes of column, over its share of 20‰. An explicit Bloom policy overrides the share
  Status vorticity.dict.probe.v1: Built, 0 bytes
  Status vorticity.bloom.sbbf.v1: Abandoned, 0 bytes -- Auto gave it up: the first 16 blocks hold 4 distinct values, under the floor of 8 the policy asks before a filter pays
```

It built dictionary probes, which cost nothing since the dictionary is already in the file; a probe
answers for the chunk its dictionary covers, and with a 301 in all but one of the file's chunks it
pruned one block of 49 for `Status == 301`. It gave up the Bloom filter the session lookup needed.
Ask for that one by name.

`WithBudgetPerMille` bounds the bytes the indexes may take together, per thousand bytes of data, 100
by default. The same Bloom and n-gram policy under the default budget:

```
  Session vorticity.bloom.sbbf.v1: Abandoned, 0 bytes -- the file's indexes reached 296960 bytes against 1430532 bytes of data, over the budget of 100‰ (IndexPolicy.WithBudgetPerMille)
```

**An abandoned index leaves no bytes.** The file was 106 bytes larger than one without a policy, the
index directory. The budget drops only an index that has written nothing, so one whose filters are
already in the file is kept even past the budget.

## Required

```
a required index over its budget: VortexException: The vorticity.bloom.sbbf.v1 index on 'Session' is required and was not built: the required indexes take 3277420 bytes against 4367852 bytes of data, over the budget of 50‰; raise it with IndexPolicy.WithBudgetPerMille. The file is not completed.
  the file is left behind: False
```

`required: true` makes an index that cannot be built fail the write: `CompleteAsync` throws
`VortexException` naming the index and the reason, before the tail is written, and the writer's
disposal deletes the partial file. The budget is judged once the data passes 1 MiB.

## What a file carries

```
  listed: Session vorticity.bloom.sbbf.v1, 1 runs over 49 blocks of 8192 rows, 0 entries, 132 bytes listed, layout FilterTree
  listed: Path vorticity.bloom.ngram3.v1, 1 runs over 49 blocks of 8192 rows, 0 entries, 2180 bytes listed, layout FilterTree
  verification holds: True (held 2, torn 0, bare 0)
```

`file.GetIndexesAsync()` lists the index directory: kind, column, runs, blocks, entries and bytes.
`ListedBytes` is what the directory lists, not what the index weighs: a `FilterTree` holds the
filters below those regions. `file.VerifyIndexesAsync()` checks every listed region against its
checksum, reading each once; it is a tool's check, and a scan does not run it.

## Watch out

* **`AppendIndexesAsync` rewrites the tail even when every index is abandoned**, and the file takes
  a new identity all the same. A `required` index it cannot build throws `VortexException` before
  anything reaches the file.
* **A fragment built by `BuildFragmentAsync` records no hash of the file**, so `VerifyIndexesAsync`
  and `vxdump --verify` cannot tell that it still matches; it is bound to the file by its identity,
  or, for a file written by another library, by its length and last write time.
* **A name the schema does not have throws at `CreateWriter`**: *The index policy names 'Referrer',
  which is no column of the schema struct{…}.*
* **The composite keys of a file share one encoder**: `ForKey` refuses a second encoder of another
  format when the policy is built.
* **A skipping index earns most when the answer is no**, and nothing on a column whose zone maps
  already prune: a sorted column needs none.
* [10-indexes.md](../design/10-indexes.md) is the design: the kinds, the directory, the policy.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- indexes
```

The figures above come from that run.
