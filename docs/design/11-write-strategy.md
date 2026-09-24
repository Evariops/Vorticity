# The writer: one pass per block

How a file is written: rows arrive in batches of any size, and each value is summarized once,
encoded once, and written once, with every pruning structure exact and every index built by the same
pass. Four constraints shape it, in this order: **throughput on one thread**, with the rest of the
session's threads used where the work splits cleanly; **pruning structures that are exact**, never
sampled; **indexes as outputs of the same pass**, across appends too
([10-indexes.md](10-indexes.md)); **no allocation per row, and no chunk-sized scratch per column**.

## 1. The principle

A value is **read twice and copied never** between its arrival and its emission. The statistics
pass reads it on the batch where the caller put it; the encoder of the scheme that won reads the
same rows again, from cache, while the chunk is still warm. One pass for everything is not possible
without guessing: every wire form but the dictionary carries a parameter known only once the chunk
is whole — the bit width, the frame of reference, ALP's exponents — and a guess from the previous
chunk would have to be checked and, when wrong, redone. The dictionary is the exception: it assigns
its codes in the first pass, and its encoder never reads the values again.

Everything that must be exact — minimum, maximum, null count, the Bloom insertions, the keys behind
an index — is computed in the first pass over every row. Everything the chooser needs — runs,
order, progressions, bit-width histograms, distinct counts, byte lengths — is computed in the same
pass. The chooser then decides by **exact formulas** for every scheme whose cost is a function of
those statistics, and runs a **trial** only for the four whose cost is not (ALP, ALP-RD, FSST and
zstd), with the best cost in hand as its abort bound. The winner encodes into outputs sized exactly,
which become the segment's buffers.

## 2. Blocks and chunks

A **block** is 8 192 rows counted from row 0 of the file, whatever the caller's batching
(`VortexWriteOptions.BlockRows`). It is the zone of the zone map, upstream's row block, eight
FastLanes blocks of 1 024, and the unit of an index. A batch that straddles blocks produces
**partials** per column and block that merge exactly, so a block's statistics are final when its
last row arrives, whichever batch brings it.

A **chunk** is a run of whole blocks, written as one segment per column. It stays a multiple of the
block length, because a chunk edge inside a block would put a zone edge inside a segment, where a
pruned zone saves no I/O. Its size weighs four costs: a read pays a fixed cost per chunk, which stops
mattering between 65 536 and 131 072 rows of an eight-byte column; a dictionary pays only when a
chunk holds many more rows than distinct values; a filtered or indexed read fetches the whole chunk
of every column it keeps; and an encoding that rides on local ranges loses when a chunk spans
several. So by default a chunk holds as many whole blocks as keep its **widest column within a
megabyte** of values, between one block and 128, with all its columns within 64 MiB.
`ChunkTargetBytes` fixes a byte target instead, and `ColumnChunkTargetBytes` gives one column
chunks of its own, spanning whole chunks of the file: a dictionary paid once for more rows, while
the other columns keep what a selective read fetches (§8).

A batch that is a multiple of the block length and fills a chunk is encoded where it lies; any other
batch waits in the builder's buffers for the rows that complete its blocks. `FlushAsync` seals the
whole blocks into a chunk; `CompleteAsync` writes the rest as the tail. A caller who writes in
multiples of `BlockRows` gets a file whose every chunk ends on a block, which appends without
rewriting anything (§3.8).

## 3. The pipeline, stage by stage

### 3.1 One column writer per leaf

The writer keeps the segment list, a small shared scratch, and a tree of column writers mirroring
the schema, one per physical leaf: a struct field, a list's elements, an extension's storage. A
column writer holds the open block's partials, one 56-byte summary per closed block until the zone
map is written, its plan memory (§3.4), its index builders, and while it runs the chunk's distinct
table. A nested column's blocks are defined by its parent's rows: block `i` of a list's elements
covers the elements of the parent's rows of block `i`, so a filter over the elements answers "does
any element of a row in this block match".

### 3.2 The statistics pass

Per column and block, one loop, resolved on the physical type before it starts and walking the
validity as bitmap words, never a row at a time. It keeps:

| what | exact | for |
|---|---|---|
| minimum and maximum; NaN skipped, `-0.0` and `+0.0` told apart by their bits | yes | the zone map, frame of reference, progressions, constants |
| null count | yes | the zone map, all-null chunks, every cost formula |
| whether the values ascend, strictly or not | yes | the file statistics, progressions, run-end |
| a progression's step, and whether it broke | yes | `vortex.sequence` |
| the count of runs | yes | run-end |
| bit-width histograms, raw and zigzag | yes | bit-packing with patches, kept only where such a packing is held |
| distinct values, through the distinct table while it runs | yes | the dictionary |
| text and binary byte totals; bounded minimum and maximum | yes, the bounds as bounds | varbin against views, the trials' ceilings, the zones of a text column |

It runs on the batch's own memory, while the decode that produced a rewrite's batch is still in
cache, with no copy before it.

### 3.3 The distinct table

One open-addressing table per column, **running over a chunk**, maps a value to its code, assigned
in first-seen order, and writes each row's code as it arrives. Equality is on bytes, never on the
hash alone, and the table **owns** its keys: a block straddles batches, and the previous batch's
memory is recycled the moment the next one is decoded, so a string is copied once, the first time it
is seen, into a heap that is, in code order, the dictionary's values child.

Probing every row of a column whose values are all distinct costs a few nanoseconds a row for
nothing, so the table runs **only on a chunk whose remembered plan is a dictionary that held**. A
column's first chunk, and a chunk whose memory sends it back to full pricing, count their distinct
values in the chooser's own walk, which gives up early on a column where a dictionary loses; the
table then runs for the next chunk if the dictionary won.

### 3.4 Choose: exact verdicts, then bounded trials

In order, per column chunk:

1. **Degenerate cases**, from the statistics alone: an all-null chunk; an integer progression, a
   constant included, as `vortex.sequence`, about thirty bytes whatever the rows; a constant of
   another type as one run.
2. **Exact candidates**, each priced by a formula that is what its encoder will write: the plain
   form (for text, `vortex.varbin` or views, whichever is smaller), run-end (which wins outright
   inside a quarter as many runs as rows), bit-packing in the raw, zigzag or frame-of-reference
   domain with the cheapest width and its patches, and the dictionary, priced by its own layer at
   the codes' width plus its values child.
3. **Trials**, only when no exact verdict won, each carrying its encoded result so that a winner is
   never encoded twice: for floats, ALP, then ALP-RD against a zstd frame; for text, a zstd frame
   priced first, then FSST, which must save a tenth on the plain form and not lose to the frame by
   more than a tenth. Zstd is tried only on chunks of at least 16 KiB.

`CompressionProfile` changes the arithmetic, not the pass: `Auto` weighs bytes and decode speed as
above; `Smallest` prices every candidate by its bytes alone and tries every trial under the best
exact plan; `Fastest` writes the cheapest encodings and no index; `None` writes every column plain.

**Plan memory.** A column keeps its last plan, the bytes the formulas predicted for it and the bytes
its encoder produced. The next chunk re-prices only the remembered plan, from its own statistics,
as long as the last chunk's bytes stayed within 5 % of the prediction; otherwise it prices
everything again. A progression and a run count are checked before memory, since they are
arithmetic on the summary: a plan that held never costs a progression its 32 bytes. What memory
saves is the walks and the trials, never a candidate the statistics already decide. An FSST symbol
table is still trained per chunk (sharing one costs 4.3 % of the output); what is remembered is
the decision.

**Hints.** `VortexWriteOptions.Hints` pins a column's scheme by path (`EncodingHint`: `Canonical`,
`RunEnd`, `Dictionary`, `BitPacked`, `Fsst`, `Alp`, `AlpRd`, `Sequence`, `Zstd`): plan memory with an
infinite tolerance, priced on every chunk and written when it applies, under every profile; only a
progression, which costs nothing per row, is written before it. A chunk the scheme cannot describe
is priced in full, and the next chunk is offered the hint again. A hint on a path the schema does
not have throws at `CreateWriter`.

**The cascade has context.** A child never enters the chooser blind: a dictionary's codes are dense
in `[0, entries)` and packed at their width; run-end ends are strictly increasing; offsets and sizes
are monotone or bounded, and a list of fixed-width rows has offsets that are an exact progression
and sizes that are constant, so both cost nothing. A dictionary's or a run's values are a column
like any other, chosen in full on a small input.

### 3.5 Encode once

The winner reads the chunk's rows block by block, from the batch or the retained batches, and
writes outputs sized exactly: patches found inside the pack, frame of reference and zigzag applied
on load, a dictionary's codes packed from the codes the first pass wrote, ALP's exceptions as
patches. A value is ALP-encoded only when decoding the integer gives back its **exact bit pattern**,
compared by bits and never with `==`, since `-0.0 == 0.0` and NaN payloads are invisible to `==`.
An FSST symbol table is ordered by symbol length, two to eight bytes then one, as the reference
requires.

A zstd column is compressed as **one frame per block**, so a take inflates only the frames that hold
the rows it wants, and the frames of a large column are compressed on the writer's threads (§3.7):
each into a room of its own, closed up in order, so the bytes are the same on any number of
threads.

### 3.6 Emit

A chunk goes out when enough whole blocks are pending, and a Bloom filter's runs as their tree nodes
close, between chunks. When the file completes: the key runs, fence pages and upper filter levels;
then the zone maps, from the closed blocks' summaries; the file statistics; the index directory;
the file's identity ([13-dataset.md](13-dataset.md) §7); the footer and the postscript.

**The file statistics** are one `ArrayStats` per top-level column: the exact minimum and maximum of
a numeric column, the null count, and whether the column ascends, in the reference's semantics — a
null below every value, equal neighbours allowed by `is_sorted` and refused by `is_strict_sorted`, a
NaN claiming nothing. The Rust cross-check holds every one against its own recomputation. A text
column's zones carry **bounded** minimum and maximum, 16 bytes by default (`StringBoundBytes`), so
that a text filter prunes.

`CompleteAsync` returns a **`WriteReport`**: the file's bytes by kind, the chunk sizes, per column
the scheme of every chunk and how often plan memory held, and per index what was built, its bytes,
or why it was abandoned.

### 3.7 Memory, allocation and threads

| what | owner | size | lifetime |
|---|---|---|---|
| the open block's partials and 56 bytes per closed block | the column | small | the file |
| the distinct table, its key heap and its codes | the column | at most a small multiple of the column's share of the pending chunk | the chunk; only while it runs |
| index builders and spilled chunk runs | the column | pooled pages, then a scratch file | the file |
| encoder outputs | the writer's arena | the segment's exact size | until the segment is written |
| per row | — | **nothing** | — |

A schema of a thousand columns costs a thousand small states, 1 892 bytes each, not a thousand
scratches ([05-benchmarks.md](05-benchmarks.md) §5).

**Threads.** At a degree above one (`VortexWriteOptions.DegreeOfParallelism`, or the session's), a
batch of a block or more is summarized column by column side by side: each column's statistics, and
apart from them a text column's bounds and the distinct values a dictionary is priced from, each
into state of its own. A column's zstd frames are compressed side by side too. Choosing and writing
the encodings stays on the calling thread, and the file is the same bytes at any degree. A write
that builds indexes summarizes on one thread.

### 3.8 Appending to a file

`VortexSession.AppendAsync(path)` returns a writer that continues a file this writer's shape can
extend — a struct of columns, each a chunked layout of flat segments, chunked alike — and refuses any
other with the advice to rewrite it. The old segments stay where they are; new blocks are numbered from the old
row count, so their boundaries are the ones a single write would have made; new segments, runs,
zone maps, statistics, directory, footer and postscript are appended, and the old postscript
becomes dead bytes. Three rules make it exact:

- **A last chunk that is not whole blocks is re-opened.** A zone map allows only its last zone to be
  short, and the last block's filter and runs were built on a short block. So the last chunk is
  read, dropped from the layout, and written again as the append's first rows, with the runs that
  covered it: one chunk, whatever the file's size. Every block before it becomes a summary taken
  from the old zone map, so the zone map is written again over the whole file without reading them.
- **Plan memory is seeded from the last chunk's encodings**, one segment read per column, so the
  appended rows keep the file's encodings unless their statistics say otherwise.
- **The index policy and budget are read from the directory**, so an append needs no options; the
  old runs are kept and merged past K ([10-indexes.md](10-indexes.md) §7).

An append is not atomic. It never truncates while it writes, so a torn append leaves the previous
version intact before garbage: the open finds it by walking back to the last end-of-file record,
reports the tear in `VortexFile.TornTail`, and reads the previous version, unless
`VortexOpenOptions.TornTail` says `Refuse`; `VortexFileRepair.RepairAsync` truncates the file to
it. An append, and an indexing pass, refuse a torn file until it is repaired. `Abandon()` on an
append truncates the file back to the length it had.

## 4. Kernels

`Vector128` everywhere, wider vectors where the hardware has them, and a scalar twin the suite runs
with intrinsics disabled ([03-architecture.md](03-architecture.md) §1). In the statistics pass:
minimum and maximum per lane, with validity folded in as a mask; null counts by population count
per 64-bit word; run boundaries by comparing a vector with itself shifted by one; order and
progressions by lane-wise subtraction against a broadcast step, exact because the progression's
endpoint is checked to fit the type first; bit widths by lane leading-zero counts where the
instruction set has them; string runs by one 16-byte comparison per pair of views. In the encoders:
frame of reference and zigzag applied on load, patch detection by a comparison mask per 1 024-block,
the FastLanes pack, and ALP's multiply, round and convert with the exception mask.

Some kernels were priced before being written and were not, because the saving was not there: an
eight-lane Bloom probe saves 0.2 µs of a 7 ms filtered scan, since a scan probes about 140 times per
literal; a vectorized zone-map pass has no register's worth of work in front of the decode that
feeds it; and a Swiss-table group probe would save at most five per cent of a dictionary's write.
The one that was written is the merge-join of an `IN` of many keys against a sorted run's keys,
which took a thousand-key `IN` from 117 to 51 ms.

## 5. What it guarantees, and what holds it

1. **Nothing a contract rests on is sampled.** Minimum, maximum, null count, Bloom insertions and
   the keys behind an index cover every row. ALP's exponent search and FSST's symbol training
   sample, and neither is a statistic anything prunes by.
2. **Equality is on bytes.** A table hit is confirmed on the key's bytes; hashes only route.
3. **An index is whole or absent**, and the report says which ([10-indexes.md](10-indexes.md) §6).
4. **Merges are exact or conservative.** Statistics merge exactly; a filter node is sized from the
   exact union of its blocks; a bounded string minimum or maximum is marked as a bound.
5. **Pruning never removes a row a full scan returns** ([08-semantics.md](08-semantics.md) §1).

| what | how it is held |
|---|---|
| bytes | `WrittenSizeTests` rewrites the whole corpus: 0.650× the reference's bytes, held by a ratchet |
| Rust reads it | the cross-check, appended files and chunked-apart columns among them |
| statistics | property tests against a naive per-row computation, at block and batch edges, for every dtype and validity shape |
| formulas | the formula's cost equals the encoder's bytes, for every scheme on the corpus |
| plan memory | a column whose data changes shape re-prices within a chunk; a stable column never |
| append | a file written once and the same rows appended in two, three and eleven pieces answer the same, zone maps and indexes included |
| threads | the file is byte for byte the same at every degree |
| allocations and dispatch | ratchets per written file and per column; no per-row dispatch in `Writing/` beyond the calls named in `PerRowDispatchTests` |

## 6. Reading what the writer produced

The writer's structures are only worth what the scan does with them. Every pruning structure
implements one operation: **refine a mask of live blocks** for the scan's predicate, a bit per block.
The scan builds the mask once per query and runs the structures **cheapest first**: the file
statistics, the zone maps (minimum and maximum, null counts, the bounded string bounds), the Bloom
filters from the root down, the postings and sorted runs, and last the dictionary probe, the one
structure that reads the column's own bytes. It stops as soon as the mask is empty. Then, per split:

- a split whose blocks are all dead is skipped before its read: the I/O saving;
- a split with dead blocks becomes a **row selection** over its live blocks, which the decoders
  serve through `DecodeSelected`, so only live blocks are decoded: the decode saving;
- an `Exact` index covering the whole predicate turns its rows into that selection, and the
  predicate is not evaluated again; under `OR`, `NOT` or a partial cover it only narrows, and the
  filter runs on the survivors.

Two more promises close the contract:

- **One lease per segment per scan.** The live splits register their segments in one request, each
  distinct segment is read once in as few requests as coalescing allows, and every block of it
  decodes from that lease. `ScanStatistics.Requests` is the plan's distinct segments, and
  `BytesRequested` its bytes, within the coalescing gap.
- **A selection instead of a compaction.** A filtered batch is compacted to its survivors by
  default; with `ScanOptions.Compact = false` each live block is delivered whole with the
  `Selection` of the rows that passed, and nothing is copied. Either way `StartRow` is the block's
  first row in the file.

**Granularity, honestly.** I/O is per segment, and a segment is a chunk of several blocks: a block
pruned inside a live chunk saves decode, not bytes read. That is why chunks stay whole blocks, and
why a file meant for selective reads wants smaller chunks.

The index directory is read lazily, on the first filter that could use it, and usually lies inside
the tail the open already read; the runs are read on the first query that needs them and cached on
the file, shared by every scan. `ExplainAsync` returns the plan without executing it — blocks, blocks
pruned by each structure with what consulting it cost, segments and bytes to read — and the scan's
`Statistics` report the same quantities once it has run, counted by the same sink.

## 7. Determinism, and its limit

The file is a pure function of the **sequence of batches**: no seed, no sample, plan memory a
function of the chunks before. The one exception is the file's sixteen-byte identity, random unless
`VortexWriteOptions.Identity` pins it.

It is **not** a function of the rows alone. Feed the same rows in another batching and the file is
valid, reads back value for value, and is a different size: rewriting an 8 193-row binary column at
forced batch counts costs 65 980 bytes in 2 chunks, 92 212 in 3, 146 076 in 5 and 169 860 in 9,
about 26 kB per extra chunk, because each chunk builds and writes its own dictionary of the same
distinct values. The reference shares one values child across a column's chunks through the
`vortex.dict` layout, which this writer does not emit. `BatchingSizeTests` pins those numbers, as a
ratchet on a cost known to be bad. A caller who wants the smallest file writes the largest batches
it can, or gives the column chunks of its own.

## 8. Choosing encodings ahead: the advisor

The writer optimizes one thing per chunk, bytes, under rules that keep decoding fast. The
measurements of [choose-encodings.md](../guide/choose-encodings.md) show where that is not what a
caller wants: text that does not repeat goes to zstd, which a scan decodes three times slower than
FSST and a take seventy times slower; a column whose values repeat across the file more than within
a chunk cannot take a dictionary, which larger chunks would give it; and which is right turns on the
storage under the file — below 200 to 360 MB/s the smaller file reads faster end to end — and on
whether the file is scanned or read by row. None of that is the writer's to know; all of it is the
caller's, or can be measured on the caller's data.

`VortexSession.AdviseAsync` measures it (`Advice/`): it takes a sample of the data, writes it under
each choice that applies, reads it back, and ranks the choices by the cost of the reads the caller
declares in an `EncodingGoal` — a storage throughput, lookups per scan, or size. It returns per column
the numbers, the recommendation and why, and `ToWriteOptions` turns them into hints and chunk
targets that the writer then applies exactly, as any options.

- **The cost of a candidate**, per value: to minimize read time, `scan + bytes ÷ throughput`, plus
  `lookupsPerScan × (lookup + chunkBytes ÷ throughput) ÷ rows`; to minimize size, its bytes, the scan
  breaking a tie. `scan` and `lookup` are timed in memory, on the machine the advisor runs on, so one
  sample prices any throughput; each time is the least of several passes, and under the JIT the
  advisor first waits until the compiler has settled.
- **The sample**: eight windows of contiguous rows spread over the data, 1 048 576 rows in all by
  default. Contiguous, because a chunk's encoding depends on the rows that are together; spread,
  because data drifts.
- **The candidates**: per kind, the plain form, `Auto`, and the hints that apply, at the writer's
  chunk size, and at 4 MiB and 16 MiB chunks when the sample says the chunk decides.
- **The choice**: the cheapest candidate, unless `Auto` is within 5 % of it, since advice should not
  move with a measurement's noise; a larger chunk replaces the writer's own only when it costs the
  column 5 % less.

The advice is a measurement, run once for a kind of data rather than for every file: a
million-row sample costs a fraction of a second for a column of numbers and a few seconds for text.
Held outside CI to the twenty column shapes of `--tradeoffs`, it names the choice the guide's
tables measured as best on every one. Taking the advice by default would change what a write
produces and its determinism, and is not done.

## 9. Deliberately not done

| approach | why not |
|---|---|
| pricing from a sample, as BtrBlocks does | it prices what exact formulas price for free, and costs determinism; plan memory takes most of what it would save on drifting text |
| emitting a chunk at every batch | a chunk edge inside a block saves no I/O on a pruned zone; a caller who wants no transit writes in multiples of `BlockRows` |
| choosing and writing encodings on several threads | the summaries and zstd frames are parallel; the choice and emission stay ordered on one thread, which keeps the bytes independent of the degree |
| rewriting without decoding, passing encoded arrays through | a separate feature: an order of magnitude on rewrites and compactions, nothing on first writes |
| buffering a column for global decisions, or a global sorted index | it breaks streaming and bounds memory by the file; sorted runs per chunk, merged, give the same answers ([10-indexes.md](10-indexes.md) §5.2) |
| one FSST symbol table shared across chunks | measured at 4.3 % more bytes |
| the `vortex.dict` layout, one values child per column | what §7's limit would need; it moves the bytes of every dictionary column, and is weighed on the size ratchet and the cross-check together |

The reference writer this is measured against is `vortex-file`'s strategy and `vortex-btrblocks`'
schemes at 0.86.1; the kernels follow FastLanes (Afroozeh and Boncz, VLDB 2023) and ALP (Afroozeh,
Kuiper and Boncz, SIGMOD 2024).
