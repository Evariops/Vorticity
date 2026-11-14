# A tail of operations ahead of level 0

A dataset commit writes the pages of its path through the tree, a leaf of 64 to 256 KiB and the pages above it, whatever it changed. An append of one small object adds an entry of about 2.5 KB and writes 25 to 260 KB of commit object to do it. This document proposes that a commit write its operations instead, and that the tree take them in now and then, in a commit that pays for all of them at once.

It was written as a proposal on 2026-11-14 against `main` at `73db801f`. Nothing in it is built, and nothing in it is scheduled: sections 1 to 3 say what a commit writes today, what smaller pages would change, and when the tail is worth its price. It changes the commit format from 5 to 6, in two releases: one that reads format 6 and writes 5, then one that writes 6.

## 1. What a commit writes today

Measured on real rows, 13,234 metric records cut into datasets in memory, a mean over 256 appends of 4 to 10 rows each:

| A commit of one append | KB |
|---|---|
| a healthy dataset without history, whose writer merges level 0 in its own commits | 24.6 |
| a healthy dataset of 40 consolidated partitions, whose writer merges level 0 in its own commits | 24.4 |
| the same 40 partitions, level 0 left to grow to 296 objects | 119 |
| a dataset whose level 0 holds 3,300 objects behind its merges | 257 |

What a commit changes is one entry, 2.5 KB. What it writes is the leaf that entry falls in, rewritten whole, and every page above that leaf. A healthy dataset keeps some ten objects in level 0, one leaf, and its commit is that leaf: 24 KB whatever its history, since an append leaves the levels above alone. A dataset whose merges fell behind pays for the pages of a tree that grew.

The design bounds what a read costs by the tree's depth. It bounds what a commit writes by the same depth, which is the right bound for reads and the wrong one for writes: the bytes do not follow the change.

## 2. Smaller pages, measured and turned down

The size of a page is a setting every header carries, 128 KiB by default, between 64 and 256. A dataset created with another needs no new format: every writer follows the header. A commit rewrites one page per level, so smaller pages make lighter commits and a deeper tree. On the same real rows:

| KB a commit, by the size pages aim for | 128 KiB | 32 KiB | 16 KiB | 8 KiB |
|---|---|---|---|---|
| healthy, without history | 24.6 | 23.7 | 18.6 | 16.6 |
| healthy, 40 consolidated partitions | 24.4 | | 16.5 | |
| 40 partitions, 296 objects in level 0 | 119 | 57.2 | 42.7 | 40.0 |
| 3,300 objects behind | 257 | 101 | 72.3 | 66.2 |

A healthy dataset gains a quarter to a third at 16 KiB, a dataset behind its merges a factor of 3. The reads of a handle opened cold pay for it, and the reads of a warm handle do not change:

| Requests of a cold read | 128 KiB | 16 KiB |
|---|---|---|
| a walk of every entry, 3,300 objects behind: a plan of compaction, a listing | 90 | 1,021 |
| a listing of the objects, 40 partitions and 296 objects in level 0 | 8 | 101 |
| a listing of the objects, healthy with 40 partitions | 1 | 21 |
| a count of the oldest rows, healthy with 40 partitions | 1 | 3 |
| a count of the newest rows, healthy with 40 partitions | 5 | 7 |
| a commit, 3,300 objects behind | 3 | 7 |

Smaller pages are turned down as a default. Where a dataset is healthy they take a quarter off a commit that already weighs 24 KB. Where it fell behind they take two thirds off, and multiply by eleven the requests of the walk its planner makes to catch up. The pages a walk reads lie in as many commit objects as wrote them, so no ranged read gathers them.

## 3. When the tail is worth building

Modelled on the library's committer (section 9), the tail takes a commit to about 10 KB where a dataset is healthy and 14 KB where it fell behind, with the walks of today's pages and one round trip more for a cold open.

- For a healthy dataset that is 14 KB less a commit: 25 MB an hour at a commit every two seconds, kept for a retention window.
- For a dataset behind its merges it is 240 KB less a commit, and a job's commit of a few KB where it writes 150 to 540 KB.

Its price is a commit format, two releases, and a second place where the state of level 0 lives. It is worth that price for a dataset that commits several times a second, or whose level 0 cannot be kept merged. For the others, the levers before it cost less: a writer that merges its own objects, a commit less often, and an entry that names its schema by its hash (section 13), which lightens commits and reads alike.

## 4. The proposal in one paragraph

A commit that changes level 0 writes its operations as a run: a page of the leaf format, sorted by tree key, each entry a put or a removal. It names the runs before it where they lie. A reader sees level 0 as its tree with the runs applied, the latest run winning on a key, and every reader keeps walking a tree: the pages the runs touch are rewritten as they are read, in the handle's memory, and the others pass through. Runs merge geometrically, a new one taking the smaller ones before it, so a version names a few of them and an operation is written about twice. Once the runs written since the tree was last folded have cost what a fold costs, the commit folds: it applies them all to the tree and writes the pages, as a commit of format 5 does.

## 5. Terms

| term | meaning |
|---|---|
| operation | a change to level 0: a put of an entry under its tree key, or a removal of a key |
| run | a page of operations sorted by tree key, written by one commit in its pages region |
| tail | the runs a version names for level 0, oldest first |
| fold | the commit that applies the tail to the tree, writes the pages, and leaves an empty tail |
| rent | what the runs written since the last fold cost, in bytes |
| price | what the last fold wrote, in bytes, 128 KiB before the first |

## 6. What a version holds

The header of format 6 adds three fields to the record of level 0, and to no other level.

| field | content |
|---|---|
| `Tail` (8) | the runs, oldest first, each a page reference of 36 bytes |
| `Rent` (9) | the bytes of the runs written since the last fold |
| `Price` (10) | the bytes the last fold wrote, 0 before the first |

A run is a page of the leaf format (`TreePage.WriteLeaf`), in the pages region of the commit that wrote it, sealed as a page is on a sealed dataset. Its entries are sorted by tree key. Each holds a key, a row count and a value whose first byte says what the operation is.

| operation | value | rows |
|---|---|---|
| put | `1`, then the entry's bytes, as a leaf holds them | the entry's live rows |
| removal | `2`, then the bytes, the deleted rows and the tally of the entry taken away | the rows taken away |

A removal carries what it takes away so that a reader corrects the sums of the pages above the key without reading the entry. Rollups, partition indexes and every level above 0 keep no tail: a commit writes their pages as today.

Two things follow, and both are to be settled before this is built.

- What an ordinary append writes of the trees of rollups and of partition indexes has not been measured: the rows the measures use declare no identifier and keep no rollup. A dataset that has both may write more than section 1 says.
- A delete that marks rows in an object of a level above 0 writes that level's pages as today. A tail on every level is one field more in each level's record, and is to be weighed for a dataset whose commits are mostly marks.

A header of format 5 reads as a header of format 6 with an empty tail.

## 7. Reading a level with a tail

A handle opens a version, reads its runs, and puts in front of its page source one that applies them. Asked for a page, that source finds the operations whose keys fall in the page's key range and, when there are some, gives a rewritten page.

- A leaf gains the puts in its range, loses the keys removed, and takes a put's entry where the key was already there.
- An internal page's entry for such a child adds the rows the puts bring and subtracts those the removals take away. It folds the puts' summaries into its bounds, which can only widen them, widens its first and last keys to a put past them, and corrects its tally, the size classes included.
- The child's reference is replaced by one that names the rewritten child, a reference that only this source resolves and that no stored page can have, so that a reader descending meets the rewritten pages and nothing else, and no cache mistakes one for the other.

A page whose range holds no operation is the stored page, read and kept as it is. The ranges come from the parent: a child covers the keys from its first key to the next child's first key, the first child also every key before it and the last every key after it.

Every walk in the library reads a level through `DatasetTree` and a page source: lookups, scans, the key cursor, counts, the planner, the checks of a commit's operations. None of them sees a tail. What they read is the tree that folding the tail would write, entry for entry. Its pages are not byte for byte those the fold would write, since a rewritten leaf keeps the stored leaf's boundaries.

### An example

Level 0 holds three leaves: `A` with keys 10 to 19, `B` with 20 to 29, `C` with 30 to 39. The tail holds a run of four operations: put 24, put 41, remove 33, put 18 replacing the entry already there.

```
root           [A: 10, rows 1000] [B: 20, rows 1000] [C: 30, rows 1000]
read as        [A': 10, rows 1000] [B': 20, rows 1004] [C': 30, rows 1010 - rows of 33]
A' = A with 18's entry replaced, rows unchanged when 18's rows did not change
B' = B with 24 inserted
C' = C without 33, with 41 appended past its last key
```

A question about keys 10 to 15 reads the root and `A'`, a rewrite of `A` that changes nothing in its range. A question about key 41 reads `C'`. A question that rules out `B` by its bounds still rules it out unless a put in `B`'s range matches, since the put's summary widened `B'`'s bounds.

### What it costs a reader

- A cold open reads the header, then the runs in one round trip side by side: a version names a handful of them, five at most in the measures below.
- A rewritten page costs the work of merging its operations, once for each version that has them: a handle keeps the rewritten pages it read, under the run that wrote them.
- A refresh reads the new runs, which are the latest commit's, in its object, read already.

## 8. Writing a run

A commit whose operations touch level 0 only, the appends of a writer and the replacements of its own objects, writes its operations as a run. The run takes the runs before it, latest first, while each holds fewer than twice the operations gathered so far, and the runs it took are named no more.

```
commit   1  2  3  4  5  6  7  8
runs     1  2  2  4  4  4  4  8      operations per run, latest last
         .  .  1  .  1  2  2  .
         .  .  .  .  .  .  1  .
```

This is a binary counter: after `n` commits a version names at most `log2(n)` runs, and an operation is written about `log2(n)/2 + 1` times before the next fold. Within a run, a removal of a key a put of the same run added takes both away, so an object a writer replaces soon after writing it, as the runs of its own recent objects do, never reaches the tree.

A commit that changes another level as well, a consolidation that takes objects out of level 0 into level 1, writes level 1's pages as today and its removals from level 0 as a run.

## 9. Folding

A commit folds when the rent, with its own run, reaches the price. It applies the whole tail and its own operations to level 0's tree, through the same code a commit of format 5 runs, writes the pages, records an empty tail, a rent of 0 and a price of the bytes it wrote.

The rule is the one of renting against buying: the tail costs a little at each commit, a fold costs once, and folding when the rent paid reaches the price spends at most twice what the best period would. Measured over 1,024 commits, it stays within 15 % of the best fixed period, with no constant chosen.

The table below is a model, on loads heavier than the datasets of section 1. Its entries are synthetic, 2,325 bytes each as a real small object's entry weighs, committed through the library's committer: the tree and every fold are real commits, and a run is weighed by the serialized size of its operations. Nothing consolidates in these loads, so their trees keep every object, and a commit of today weighs 84 to 106 KB there where a healthy dataset's weighs 24.

| KB per commit, the fold amortized | starts empty | 3,300 objects behind | 5,000 objects, appends past them |
|---|---|---|---|
| today | 84.1 | 106.2 | 86.9 |
| runs, folded every 64 commits | 8.1 | 12.0 | 8.2 |
| runs, folded when the rent reaches the price | 9.7 | 13.5 | 9.9 |
| the whole tail copied into each header, folded past 32 KiB | 21.1 | 26.0 | 21.2 |

A commit that changes the layout's key, a rekey, or repacks commit objects, folds first. A commit that changes the schema does not: the entries in the tail are entries like those of the tree.

## 10. Concurrent commits

Operations are applied again on the version that won, as today (13-dataset.md, section 8.2).

- A commit that writes a run reads the winner's header, which names the winner's runs, applies its operations again on the winner's tree with its tail, and writes its run again. It reads no page of the tree but the leaves its checks need, and the runs it absorbs: the latest of them lie in the winner's object, which the read that opened it holds.
- A fold that loses reads the winner's header and applies the winner's tail and its own operations again. The pages it wrote for the leaves no new operation touches stay right, so it reads only the leaves the winner's run changed.
- A replacement whose input is gone or changed in the tree with the tail applied abandons, as today.

## 11. Datasets without a key

An object's tree key is then its partition and its first row's position, and an append puts its object past every row of the dataset. A removal takes its rows away at its place. Row positions are those of the tree with the tail applied in key order, which the rewritten pages' row sums give: a walk by row range counts them as it walks today.

## 12. Lifecycle

- Vacuum marks a run as it marks a page: a commit object that holds a run a retained version names is kept. A run dies a retention window after the fold that took it in.
- Verification reads each run where it lies, checks its hash, and checks that applying the tail gives a tree whose sums match the header's.
- A repack copies the runs a version names as it copies its pages.
- Two versions are equal when their trees with their tails applied are: the root hash of a version with a tail says nothing until it folds, and a diff applies both tails.

## 13. The table of contents names its schema by its hash

The table of contents of an object's entry holds the object's schema, 1,056 of its 2,148 bytes on the rows measured, and every object of a dataset whose schema never changed holds the same one. A table of format 6 holds the XXH3-64 of the schema's bytes instead, when the schema is the dataset's at the version that writes it. A reader compares it with the hash of its version's schema: equal, it takes the dataset's schema, different, it reads the object as one of an earlier schema, opening it to learn its schema as for a file imported. An entry goes from about 2.5 KB to about 1.5 KB, which every run, every leaf and every plan carries.

Dropping the table of an object small enough to be read whole in one request was measured and turned down: several paths exist only through the table, among them indexing after the fact, rollups and identifier lookups, and the reads of small objects would lose them.

## 14. Moving from format 5 to 6

1. A release reads format 6 and tables that hold a schema's hash, and writes format 5. A header of format 5 reads as one of format 6 with an empty tail.
2. The next release writes format 6. A binary of a release before the first refuses a commit of format 6, saying which release wrote it, as it refuses every format it does not know.

A dataset that a binary of the second release committed to is format 6 from then on.

## 15. What it costs

- A second place where level 0's state lives. Every reader is shielded from it by the page source that applies the tail, which is the one place a mistake would be.
- A cold open of a version reads its runs, one round trip more.
- A rewritten page costs the merge of its operations, once a version per handle.
- The root hash of a version with a tail no longer identifies its content alone.

## 16. How it is tested

- The oracle is the fold. Every question asked of a version with a tail must give what it gives on the same version folded, under the interleaving fuzzer, at each step.
- The commit bytes ratchet (`CommitBytesBudgetTests`) takes the bounds of section 9.
- The read budgets take one round trip more for a cold open of a version with runs, and none for a warm one.
- A version of format 5 reads as it did, its requests and allocations unchanged.

## 17. Alternatives weighed

| question | chosen | turned down, and why |
|---|---|---|
| where the operations live | runs in the commit objects that wrote them, named by the header | copied whole into each header: quadratic in a period, each operation written 7 times |
| when to fold | when the rent reaches the price | every N commits: a constant to choose, which the measures show depends on the load |
| how readers see the tail | a page source that rewrites the pages the tail touches | a view each reader merges: some thirty places, each a chance to get it wrong. A tree built in memory at open: a cold reader would read every leaf the tail touches |
| which levels take a tail | level 0 | every level: the commits that change the others are jobs, rare, and pay their pages once |
| smaller pages instead | not by default | measured in section 2: a quarter to a third off a healthy dataset's commit, for 11 to 21 times the requests of a cold walk |
| no commit object for a small append | none for now | an append as one object that carries both its rows and its commit is the next lever once a commit weighs a few KB |
