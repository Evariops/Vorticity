# The writer: one pass per block

How a file is written. Rows arrive in batches of any size, and each value is summarized once, encoded
once and written once, with every pruning structure exact and every index built by the same pass.
Four constraints shape the writer, in this order:

1. throughput on one thread, with the rest of the session's threads used where the work splits
   cleanly
2. pruning structures that are exact, never sampled
3. indexes produced by the same pass, across appends too ([10-indexes.md](10-indexes.md))
4. no allocation per row, and no chunk-sized scratch buffer per column

## 1. The principle

Between its arrival and its emission, a value is read twice and never copied. The statistics pass
reads it in the batch where the caller put it, and the encoder of the winning scheme reads the same
rows again, from the CPU cache, while the chunk is still warm. A single pass for everything is not
possible without guessing: every wire form except the dictionary carries a parameter that is only
known once the chunk is complete (the bit width, the frame of reference, ALP's exponents), and a
guess from the previous chunk would have to be checked and, when wrong, redone. The dictionary is
the exception, because it assigns its codes in the first pass and its encoder never reads the values
again.

Everything that must be exact (minimum, maximum, null count, the Bloom insertions, the keys behind an
index) is computed in the first pass over every row. Everything the chooser needs (runs, order,
progressions, bit-width histograms, distinct counts, byte lengths) is computed in the same pass. The
chooser then decides by exact formulas for every scheme whose cost is a function of those
statistics, and only runs a trial for the schemes whose cost is not: ALP, ALP-RD, FSST, OnPair and
zstd, plus pco under the `Smallest` profile, each with the best cost so far as its abort bound. The
winner encodes into outputs sized exactly, which become the segment's buffers.

## 2. Blocks and chunks

A block is 8 192 rows counted from row 0 of the file, whatever the caller's batching
(`VortexWriteOptions.BlockRows`). It is the zone of the zone map, upstream's row block, eight
FastLanes blocks of 1 024, and the unit of an index. A batch that straddles blocks produces partial
summaries per column and block that merge exactly, so a block's statistics are final when its last
row arrives, whichever batch brings it.

A chunk is a run of whole blocks, written as one segment per column. It stays a multiple of the
block length, because a chunk edge inside a block would put a zone edge inside a segment, where a
pruned zone saves no I/O. Its size balances four costs. A read pays a fixed cost per chunk, which
stops mattering somewhere between 65 536 and 131 072 rows of an eight-byte column. A dictionary only
pays off when a chunk holds many more rows than distinct values. A filtered or indexed read fetches
the whole chunk of every column it keeps. And an encoding that relies on local ranges loses when a
chunk spans several. So by default a chunk holds as many whole blocks as keep its widest column
within a megabyte of values, between one block and 128, with all its columns within 64 MiB.
`ChunkTargetBytes` sets a byte target instead, and `ColumnChunkTargetBytes` gives one column chunks
of its own, spanning whole chunks of the file, which pays for a dictionary once over more rows while
the other columns keep what a selective read fetches (see [the advisor](#8-choosing-encodings-ahead-the-advisor)).

A batch that is a multiple of the block length and fills a chunk is encoded where it lies. Any other
batch waits in the builder's buffers for the rows that complete its blocks. `FlushAsync` seals the
whole blocks into a chunk, and `CompleteAsync` writes the rest as the tail. A caller who writes in
multiples of `BlockRows` gets a file in which every chunk ends on a block, which can be appended to
without rewriting anything (see [appending to a file](#38-appending-to-a-file)).

## 3. The pipeline, stage by stage

### 3.1 One column writer per leaf

The writer keeps the segment list, a small shared scratch area, and a tree of column writers that
mirrors the schema, one per physical leaf: a struct field, a list's elements, an extension's storage.
A column writer holds the open block's partial summaries, one 56-byte summary per closed block until
the zone map is written, its plan memory (see [choosing](#34-choose-exact-verdicts-then-bounded-trials)),
its index builders, and, while it runs, the chunk's distinct table. A nested column's blocks are
defined by its parent's rows. Block `i` of a list's elements covers the elements of the parent's rows
in block `i`, so a filter over the elements answers "does any element of a row in this block match".

A chunk of a list column writes the elements its rows name, and no other. A batch's rows are a view of its list's elements, and a chunk cut from it shares them whole, so the writer narrows each chunk before it writes it. Rows that lie one after the other in their elements keep them as a view, narrowed to the extent from the first to the last. Rows that lie apart are gathered: their elements are copied in the order of the rows. The writer tells the two apart in one pass, by comparing the elements the rows name with the extent they span. Rows lie apart when they were laid out in another order than their lists, as an append sorted by a key leaves them, or as a merge takes them a few at a time from several objects. Before this rule, such a chunk wrote the elements of its whole batch. In `DatasetCompositeCompactionTests`, 150 000 rows of short histograms appended in no order of their key made an object of 86.8 MB where the same rows in order made one of 5.7 MB, and twelve objects of 1.05 MB merged into one of 8.7 to 10 MB. The first now weighs what the rows in order do, within a hundredth, and the merge writes less than it reads.

### 3.2 The statistics pass

Per column and block there is one loop, resolved on the physical type before it starts, that walks
the validity as bitmap words, never a row at a time. It keeps:

| what | exact | for |
|---|---|---|
| minimum and maximum, NaN skipped, `-0.0` and `+0.0` told apart by their bits | yes | the zone map, frame of reference, progressions, constants |
| null count | yes | the zone map, all-null chunks, every cost formula |
| whether the values ascend, strictly or not | yes | the file statistics, progressions, run-end |
| a progression's step, and whether it broke | yes | `vortex.sequence` |
| the count of runs | yes | run-end |
| bit-width histograms, raw and zigzag | yes | bit-packing with patches, kept only where such a packing is held |
| distinct values, through the distinct table while it runs | yes | the dictionary |
| text and binary byte totals, bounded minimum and maximum | yes, the bounds as bounds | varbin against views, the trials' ceilings, the zones of a text column |

It runs on the batch's own memory, with no copy before it, and when the batch comes from decoding a
file being rewritten, while that decode is still in the CPU cache.

### 3.3 The distinct table

There is one open-addressing table per column, running over a chunk, that maps a value to its code,
assigned in first-seen order, and writes each row's code as it arrives. Equality is checked on bytes,
never on the hash alone, and the table owns its keys. A block straddles batches, and the previous
batch's memory is recycled the moment the next one is decoded, so a string is copied once, the first
time it is seen, into a heap that is, in code order, the dictionary's values child.

While a table of four- or eight-byte values holds 32 values at most, it codes eight rows at a time
by comparing them with each of its values at once, a row equal to none going through the table: a
column drawing few values in no order would otherwise miss the branch on its slot's compare most
rows, 6.5 ns a row for two values of eight bytes against 0.25 this way.

Probing every row of a column whose values are all distinct costs a few nanoseconds per row for
nothing, so the table only runs on a chunk whose remembered plan is a dictionary that held. A
column's first chunk, and a chunk whose plan memory sends it back to full pricing, count their
distinct values in the chooser's own walk, which gives up early on a column where a dictionary loses.
The table then runs for the next chunk if the dictionary won.

### 3.4 Choose: exact verdicts, then bounded trials

Per column chunk, in order:

1. Degenerate cases are decided from the statistics alone. A chunk of a single value, or of nulls
   only, of any dtype (a struct's and a list's included, whose validity would otherwise be a bitmap
   of zeros) is written as `vortex.constant`, a scalar and a length. An integer progression is
   written as `vortex.sequence`, about thirty bytes whatever the row count. The constant comes first,
   integers included, because a sequence of step zero decodes to all its rows, whereas a constant
   stays a single value through a scan.
2. A chunk with nulls on nine rows in ten or more is written as `vortex.sparse` with a null fill: the
   valid rows' positions and values, and no validity, priced the way a frame of reference packs them
   against the runs the nulls make. It comes before run-end's rule below, which would otherwise take
   every such chunk as runs, and before a decimal's parts. A decimal whose values fit in 64 bits is
   then written as `vortex.decimal_byte_parts` over the integers, which the integer schemes price in
   turn.

   The share of nulls is a price, measured. A sparse chunk takes fewer bytes than the dense one the
   chooser picks from half its rows null, but it decodes slower until 98 rows in a hundred are null:
   0.15 ns a row and 3.2 ns a valid row for integers and doubles, 0.45 ns and 5.7 ns for text,
   against 0.23 ns a row for bit-packed integers (`vortex-queries --micro sparsecost`, nulls at
   random places). Nine nulls in ten is where each byte sparse saves costs about a third of a
   nanosecond of decode: 0.32 ns for integers at 90 % nulls, and at 85 % 0.35 for integers, 0.33 for
   doubles and 0.55 for text. A choice per chunk at that price would make about the same choices on
   these shapes, and it would need the dense plan's bytes and decode time when sparse is weighed,
   which the chooser only has once it has priced every other candidate.
3. Exact candidates are each priced by a formula equal to what their encoder will write: the plain
   form (for text, `vortex.varbin` or views, whichever is smaller), run-end (which wins outright
   below a quarter as many runs as rows), bit-packing in the raw, zigzag or frame-of-reference domain
   at the cheapest width with its patches, a timestamp's instants split into days, seconds and
   subseconds (`vortex.datetimeparts`), priced by each part's packed width and taken when it comes a
   tenth under the best so far, and the dictionary, priced by its own layer, its codes bit-packed at the
   width that indexes its values as their own cascade writes them at worst, plus its values child: eight
   status codes, five in a hundred past the width the others pack at, take 3 bits a row as a dictionary
   where the bit-packing and its patches take 6.7, a choice its codes priced a byte a row lost. A 256-row sample bounds the timestamp split from below, so instants at full
   resolution are refused without the pass that splits them.
4. Trials only run when no exact verdict won, and each carries its encoded result so a winner is
   never encoded twice. For floats, ALP is tried, then ALP-RD against a zstd frame. For text, a zstd
   frame is priced first, then FSST, which must save a tenth on the plain form and not lose to the
   frame by more than a tenth, then OnPair, which must also come a tenth under FSST. OnPair trains a
   dictionary of up to 4 096 tokens on a random sixth of the chunk's bytes, and its codes on that
   sample decide before the pass over every row. Zstd is only tried on chunks of at least 16 KiB.

`CompressionProfile` changes the arithmetic, not the pass. `Auto` weighs bytes and decode speed as
above. `Smallest` prices every candidate by its bytes alone and tries every trial under the bytes the
best exact plan writes, pco among them for numbers of 16 bits and more, on the column and on every
array a scheme makes of it, the way the reference's compact compressor cascades. `Fastest` writes the
cheapest encodings and no index, and `None` writes every column plain.

Under `Smallest`, a numeric chunk is compressed the way pco does it at its default level. The latents
are split into chunks of up to 2^18 and pages of 8 192, as Vortex's compact compressor pages them, an
integer's common divisor is found by pco's own test on pco's own sample (`IntMult`), the consecutive
delta order is the one a sample prices best, and equal-count bins are merged by pco's bit-cost
dynamic program and entropy-coded by its four-way tANS. What upstream's automatic choice also tries
and this one does not is the lookback delta and the float modes, whose values ALP and ALP-RD already
handle. The bytes match upstream's own, framing aside, on every shape measured (event times, a random
walk, multiples of a hundred, skewed counts, full doubles), and the plan is priced with the framing
its pages bring, a buffer and a count each, since a column that compresses to little has as much
there as in the pages. Before the pass that bins and writes a chunk, the sample that chose its delta
prices it, and a chunk a quarter over the bytes it must beat is refused unwritten. The trial costs
what pco costs: a partition of the latents into bins rather than a sort, and a count in their place
where their range is narrower than their number, which is what deltas leave.

A column also keeps a plan memory: its last plan, the bytes the formulas predicted for it and the
bytes its encoder produced. As long as the last chunk's bytes stayed within 5 % of the prediction,
the next chunk only prices the remembered plan again, from its own statistics. Otherwise it prices
everything again. A progression and a run count are checked before the memory, since they are
arithmetic on the summary, so a plan that held never costs a progression its 32 bytes. What memory
saves is the walks and the trials, never a candidate the statistics already decide. An FSST symbol
table is still trained per chunk (sharing one costs 4.3 % more output), and only the decision is
remembered.

`VortexWriteOptions.Hints` pins a column's scheme by path (`EncodingHint`: `Canonical`, `RunEnd`,
`Dictionary`, `BitPacked`, `Fsst`, `Alp`, `AlpRd`, `Sequence`, `Zstd`, `DecimalByteParts`,
`Constant`, `DateTimeParts`, `Sparse`, `OnPair`, `Pco`). A hint acts as plan memory with an infinite
tolerance, priced on every chunk and written whenever it applies, under every profile. Only a
constant and a progression, which cost nothing per row, are written before it. `Sequence` and
`Constant` pin nothing the writer would not do anyway, and simply name what a report says a chunk was
written as. A chunk the scheme cannot describe is priced in full, and the next chunk is offered the
hint again. A hint on a path the schema does not have throws at `CreateWriter`.

The cascade has context. A child never enters the chooser blind: a dictionary's codes are dense in
`[0, entries)` and packed at their width, run-end ends are strictly increasing, offsets and sizes are
monotone or bounded, and a list of fixed-width rows has offsets that form an exact progression and
sizes that are constant, so both cost nothing. A dictionary's or a run's values are a column like any
other, chosen in full on a small input. A timestamp's storage knows its unit, which is what lets it
price its split, and a part that is the same for every value (the subseconds of instants rounded to
the second) is handed on as a constant rather than discovered by a walk.

### 3.5 Encode once

The winner reads the chunk's rows block by block, from the batch or the retained batches, and writes
outputs sized exactly: patches found inside the pack, frame of reference and zigzag applied on load,
a dictionary's codes packed from the codes the first pass wrote, and ALP's exceptions as patches. A
value is only ALP-encoded when decoding the integer gives back its exact bit pattern, compared by
bits and never with `==`, since `-0.0 == 0.0` and NaN payloads are invisible to `==`. An FSST symbol
table is ordered by symbol length, two to eight bytes then one, as the reference requires.

A zstd column is compressed as one frame per block, so a take only inflates the frames that hold the
rows it wants. The frames of a large column are compressed on the writer's threads (see
[threads](#37-memory-allocation-and-threads)), each into its own buffer and then closed up in order,
so the bytes are the same on any number of threads.

### 3.6 Emit

A chunk goes out when enough whole blocks are pending, and a Bloom filter's runs go out as their tree
nodes close, between chunks. When the file completes, the writer emits the key runs, fence pages and
upper filter levels, then the zone maps from the closed blocks' summaries, the file statistics, the
index directory, the file's identity (see [identity and
integrity](13-dataset.md#7-identity-and-integrity)), and finally the footer and the postscript.

A chunk whose array declares no buffer, a sequence for instance, also has its array tree inlined in
its flat layout's metadata, so that a scan reads it from no segment at all. The blob stays whole in
the segment, as the reference's writer leaves it, for a reader that ignores the field. Rust readers
before 0.84 refuse a flat layout that carries any metadata, so a file that targets an edition before
`core2026.08.0` inlines none.

The file statistics are one `ArrayStats` per top-level column: the exact minimum and maximum of a
numeric column, the null count, and whether the column ascends, in the reference's semantics (a null
below every value, equal neighbours allowed by `is_sorted` and refused by `is_strict_sorted`, a NaN
claiming nothing). They carry no sum. The Rust cross-check compares every one with its own
recomputation. A text column's zones carry bounded minimum and maximum values, 16 bytes by default
(`StringBoundBytes`), so that a text filter can prune.

`CompleteAsync` returns a `WriteReport`: the file's bytes by kind, the chunk sizes, per column the
scheme of every chunk and how often plan memory held, and per index what was built and its bytes, or
why it was abandoned.

### 3.7 Memory, allocation and threads

| what | owner | size | lifetime |
|---|---|---|---|
| the open block's partial summaries, and 56 bytes per closed block | the column | small | the file |
| the distinct table, its key heap and its codes | the column | at most a small multiple of the column's share of the pending chunk | the chunk, and only while it runs |
| index builders and spilled chunk runs | the column | pooled pages, then a scratch file | the file |
| encoder outputs | the writer's arena | the segment's exact size | until the segment is written |
| per row | none | nothing | none |

A schema of a thousand columns costs a thousand small states of 1 892 bytes each, not a thousand
scratch buffers ([allocations](05-benchmarks.md#5-allocations)).

At a degree above one (`VortexWriteOptions.DegreeOfParallelism`, or the session's), a batch of a
block or more is summarized column by column, side by side: each column's statistics, and separately
a text column's bounds and the distinct values a dictionary is priced from, each into its own state.
Each column's chunk then leaves for an emission of its own, a unit of the session's core that a free
lane compresses while the blocks after the chunk go through, against a copy of the file's encoding
ids, and lands in column order, each column's new ids made the file's in the order it met them, so
the file is the same bytes at any degree. The chunk's rows stay where they wait in transit, the
emission reading them through records of its own, and the writer lets go of the transit once every
emission of the chunk is done, its next rows going into another; rows the caller lent, which its call
may reuse once it returns, are copied for the emission. A column's chunk lands as the column's next
chunk leaves, or at a flush or the completion; a writer that builds indexes lands its chunk before
the chunk closes, its columns compressed at once on the lanes, since its indexes are judged against
their columns' bytes as the chunk closes and the payloads it closed go out after the verdict. A column
with a chunk target of its own gathers its rows on its thread as well, and its chunks, once due, are
each emitted the same way (see [a dataset's objects](#9-a-datasets-objects)). A column whose last
trial priced zstd on enough of its values compresses its frames as items of its emission. On one lane,
an emission compresses its chunk as it leaves, and the chunk lands where it does on many. A write that
builds indexes summarizes on one thread.

A writer that stages its blocks sends several blocks through at once on its line (`VortexFileWriter.FlightLine`), up to four ahead of the one whose landing its size is read at: a dataset job's whose object is aimed at many blocks, from its first block, and any writer of a table of several columns cut into chunks by their bytes, from its fifth, so that one of a few blocks makes none of the line's state. Such a writer takes its caller's rows a block at a time at any degree: a call below a block is copied into the block it falls in, a call of a block or more is cut into blocks where it lies, each held there until its block is assembled, and the call returns once its blocks landed. Its chunks are cut block by block, as a merge's writer cuts them, under the columns that gather too: a block whose widest column holds more than a gathered chunk's bytes ends its chunk, where blocks went into chunks of eight. The bytes are those of one call for calls of a block or more, at any degree. Records written through the writer's builder go the same way, the builder's whole blocks taken a chunk's worth at least at a time. At completion, the chunk of the whole blocks ends before the last rows short of a block, which make the last chunk alone, so that a scan cuts every chunk before it at the zones; an append keeps them in its last chunk, which it reads and writes again when the file ends inside a block. Each block is a flight. Its prologue runs in the order of the blocks: it decides whether the line takes the block, reads the measures the columns' counts share and prepares the block's census into what the flight owns. Its items run on the session's lanes: each column's statistics, bounds and distinct table, each column's index builders, each variant column's read of its keys and each group of them, the census. An item runs once every state it touches is done with the block before, a column's statistics, its distinct table, its builders, a group of keys, so the columns quick to take a block run ahead of the slowest, where every block once waited for the slowest item of the block before. Its epilogue runs in order and puts its rows in transit; it runs once every item of its block and of the blocks before ran, so it moves every state past its block, those its block did not touch too: a state moved only as an item that touched it ended, and the take of a shredded column's rows along its chain, broken for three blocks and taken again at a chunk's start, waited for blocks that never touched it, and the writer hung. The end of a chunk of the file is a barrier: a column's distinct table must see no row of the next chunk before its chunk takes it, so the blocks of one chunk overlap, and the next chunk's first block waits for the chunk's columns to leave for their emissions, each with its table and its statistics, rather than for their compression. The counts of a column attach the wide columns' distinct values of a block as they start on the next one, once every wide column took it: an attachment raises registers to their maximum, which no later block lowers, and a column found no longer wide is dropped whole at the end of the object whenever the counts learn of it. A block the line does not take, an object's first, one that would start a chunk past the rows in transit or cross one, goes through alone as every block did before. A block that makes exactly a whole chunk ends it on the line, as a block that completes one does: gone through alone, the 6 328 blocks of a consolidation of metric records, each a chunk, were ingested one at a time on the job's flow, 7.45 s of its 9.6 s of writing at degree 14, and the job ran in 11.1 s against 9.2 on the line, the same bytes.

### 3.8 Appending to a file

`VortexSession.OpenWriterAsync(path)` returns a writer that continues a file whose shape this writer can
extend (a struct of columns, each a chunked layout of flat segments, all chunked alike), and refuses
any other file with the advice to rewrite it. The old segments stay where they are. New blocks are
numbered from the old row count, so their boundaries are the ones a single write would have made.
New segments, runs, zone maps, statistics, directory, footer and postscript are appended, and the old
postscript becomes dead bytes. Three rules make it exact:

- A last chunk that is not made of whole blocks is reopened. A zone map only allows its last zone to
  be short, and the last block's filter and runs were built on a short block. So the last chunk is
  read, dropped from the layout, and written again as the append's first rows, together with the runs
  that covered it. That is one chunk, whatever the file's size. Every block before it becomes a
  summary taken from the old zone map, so the zone map is written again over the whole file without
  reading those blocks.
- Plan memory is seeded from the last chunk's encodings, at the cost of one segment read per column,
  so the appended rows keep the file's encodings unless their statistics say otherwise.
- The index policy and budget are read from the directory, so an append needs no options. The old
  runs are kept, and merged once there are more than four (see [indexing after the
  fact](10-indexes.md#7-building-appending-and-indexing-after-the-fact)).

An append is not atomic. It never truncates while it writes, so a torn append leaves the previous
version intact, followed by garbage. The open finds that version by walking back to the last
end-of-file record, reports the tear in `VortexFile.TornTail`, and reads the previous version, unless
`VortexOpenOptions.TornTail` says `Refuse`. `VortexFileRepair.RepairAsync` truncates the file back
to it. An append, and an indexing pass, refuse a torn file until it is repaired. `Abandon()` on an
append truncates the file back to the length it had.

## 4. Kernels

`Vector128` is used everywhere, wider vectors where the hardware has them, plus a scalar twin whose
tests run again with intrinsics disabled (see [the founding
constraints](03-architecture.md#1-founding-constraints)).

The statistics pass vectorizes:

- minimum and maximum per lane, with the validity folded in as a mask
- null counts, by population count per 64-bit word
- run boundaries, by comparing a vector with itself shifted by one
- order and progressions, by lane-wise subtraction against a broadcast step, exact because the
  progression's endpoint is first checked to fit the type
- bit widths, by lane leading-zero counts where the instruction set has them
- string runs, by one 16-byte comparison per pair of views

The encoders apply frame of reference and zigzag on load, detect patches with a comparison mask per
1 024-value block, run the FastLanes pack, and do ALP's multiply, round and convert with the
exception mask.

Some kernels were priced before being written and then not written, because the saving was not
there. An eight-lane Bloom probe would save 0.2 µs of a 7 ms filtered scan, since a scan probes
about 140 times per literal. A vectorized zone-map pass has less than a register's worth of work
ahead of the decode that feeds it. A Swiss-table group probe would save at most five per cent of a
dictionary's write. The one that was written is the merge-join of an `IN` of many keys against a
sorted run's keys, which took a thousand-key `IN` from 117 to 51 ms.

## 5. What it guarantees, and what holds it

1. Nothing a contract rests on is sampled. Minimum, maximum, null count, Bloom insertions and the
   keys behind an index cover every row. ALP's exponent search and FSST's symbol training do sample,
   but neither is a statistic anything prunes by.
2. Equality is on bytes. A table hit is confirmed on the key's bytes, and hashes only route.
3. An index is whole or absent, and the report says which (see [the policy](10-indexes.md#6-the-policy)).
4. Merges are exact or conservative. Statistics merge exactly, a filter node is sized from the exact
   union of its blocks, and a bounded string minimum or maximum is marked as a bound. A block's bounds
   are the same whether its rows come as views or as a constant: a merge's block of one run keeps the
   constant its input decoded, one of several runs concatenates their views, and where the runs fall
   depends on how the inputs' batches were cut. A constant block once had no bounds, and its zone map
   none: one consolidation wrote a different zone map on one lane than on four.
5. Pruning never removes a row a full scan returns (see [statistic
   precision](08-semantics.md#1-statistic-precision-what-inexact-licenses)).

| what | how it is held |
|---|---|
| bytes | `WrittenSizeTests` rewrites the whole corpus and holds its bytes against the reference's under a ratchet |
| Rust reads it | the cross-check, appended files and chunked-apart columns among them |
| statistics | property tests against a naive per-row computation, at block and batch edges, for every dtype and validity shape |
| formulas | the formula's cost equals the encoder's bytes, for every scheme on the corpus |
| plan memory | a column whose data changes shape is priced again within a chunk, and a stable column never is |
| append | a file written once and the same rows appended in two, three and eleven pieces give the same answers, zone maps and indexes included |
| threads | the file is byte for byte the same at every degree |
| allocations and dispatch | ratchets per written file and per column, and no per-row dispatch in `Writing/` beyond the calls named in `PerRowDispatchTests` |

## 6. Reading what the writer produced

The writer's structures are only worth what the scan does with them. Every pruning structure
implements one operation: refine a mask of live blocks, one bit per block, for the scan's predicate.
The scan builds the mask once per query and runs the structures cheapest first: the file statistics,
the zone maps (minimum and maximum, null counts, the bounded string bounds), the Bloom filters from
the root down, the postings and sorted runs, and last the dictionary probe, the one structure that
reads the column's own bytes. It stops as soon as the mask is empty. Then, per split:

- a split whose blocks are all dead is skipped before it is read, which saves the I/O
- a split with some dead blocks becomes a row selection over its live blocks, which the decoders
  serve through `DecodeSelected`, so only live blocks are decoded, which saves the decode
- an `Exact` index covering the whole predicate turns its rows into that selection, and the
  predicate is not evaluated again. Under `OR`, `NOT` or a partial cover, it only narrows, and the
  filter runs on the survivors

Two more promises complete the contract:

- A scan takes one lease per segment. The live splits register their segments in one request, each
  distinct segment is read once in as few requests as coalescing allows, and every block in it
  decodes from that lease. `ScanMetrics.Requests` is the plan's distinct segments, and
  `BytesRequested` their bytes, within the coalescing gap.
- A selection can replace a compaction. A filtered batch is compacted to its surviving rows by
  default. With `ScanOptions.Compact = false`, each live block is delivered whole with the
  `Selection` of the rows that passed, and nothing is copied. Either way, `StartRow` is the block's
  first row in the file.

The granularity has to be stated honestly. I/O is per segment, and a segment is a chunk of several
blocks, so a block pruned inside a live chunk saves decode, not bytes read. That is why chunks stay
whole blocks, and why a file meant for selective reads wants smaller chunks.

The index directory is read lazily, on the first filter that could use it, and usually lies inside
the tail the open already read. The runs are read on the first query that needs them and cached on
the file, shared by every scan. `ExplainAsync` returns the plan without executing it (blocks, blocks
pruned by each structure with what consulting it cost, segments and bytes to read), and the scan's
`Metrics` report the same quantities once it has run, counted by the same sink.

## 7. Determinism, and its limit

The file is a pure function of the sequence of batches: no seed, no sample, and plan memory is a
function of the chunks before. The one exception is the file's sixteen-byte identity, which is random
unless `VortexWriteOptions.Identity` pins it.

The file is not a function of the rows alone. Feed the same rows with a different batching and the
file is valid and reads back value for value, but its size changes. Rewriting an 8 193-row binary
column at forced batch counts costs 65 980 bytes in 2 chunks, 92 212 in 3, 146 076 in 5 and 169 860
in 9, about 26 kB per extra chunk, because each chunk builds and writes its own dictionary of the same
distinct values. The reference shares one values child across a column's chunks through the
`vortex.dict` layout, which this writer emits only for a column a dataset's writer declares an
[object dictionary](23-query-architecture.md#54-ordered-object-dictionaries) of. `BatchingSizeTests`
pins those numbers as a ratchet on a cost known to be bad. A caller who wants the smallest file writes
the largest batches it can, or gives the column chunks of its own.

## 8. Choosing encodings ahead: the advisor

The writer optimizes one thing per chunk, bytes, under rules that keep decoding fast. The
measurements in [the encodings, column by
column](../guide/benchmarks.md#encodings-column-by-column) show where that is not what a caller
wants. Text that does not repeat goes to zstd, which a scan decodes several times slower than FSST,
and a take far slower. A column whose values repeat across the file more than within a chunk cannot
take a dictionary, which larger chunks would give it. And the right choice depends on the storage
under the file (below a few hundred MB/s the smaller file reads faster end to end) and on whether the
file is scanned or read by row. None of that is the writer's to know. All of it is the caller's, or
can be measured on the caller's data ([choose-encodings.md](../guide/choose-encodings.md)).

`VortexSession.AdviseAsync` measures it (`Advice/`). It takes a sample of the data, writes it under
each choice that applies, reads it back, and ranks the choices by the cost of the reads the caller
declares in an `EncodingGoal`: a storage throughput, lookups per scan, or size. It returns, per
column, the numbers, the recommendation and the reason, and `ToWriteOptions` turns them into hints and
chunk targets that the writer then applies exactly, like any other options.

- The cost of a candidate is computed per value. To minimize read time, it is
  `scan + bytes ÷ throughput`, plus `lookupsPerScan × (lookup + chunkBytes ÷ throughput) ÷ rows`. To
  minimize size, it is the candidate's bytes, with the scan breaking a tie. `scan` and `lookup` are
  timed in memory, on the machine the advisor runs on, so one sample prices any throughput. Each time
  is the best of several passes, and under the JIT the advisor first waits until the compiler has
  settled.
- The sample is eight windows of contiguous rows spread over the data, 1 048 576 rows in all by
  default. Contiguous, because a chunk's encoding depends on the rows that sit together, and spread,
  because data drifts.
- The candidates, per kind, are the plain form, `Auto` and the hints that apply, at the writer's
  chunk size, and also at 4 MiB and 16 MiB chunks when the sample says the chunk size decides.
- The choice is the cheapest candidate, unless `Auto` is within 5 % of it, since advice should not
  move with measurement noise. A larger chunk only replaces the writer's own when it costs the column
  5 % less.

The advice is a measurement, run once for a kind of data rather than for every file. A million-row
sample costs a fraction of a second for a column of numbers and a few seconds for text. Checked
outside CI against the twenty column shapes of `--tradeoffs`, it names the choice the guide's tables
measured as best on every one. Taking the advice by default would change what a write produces and
its determinism, so it is not done.

## 9. A dataset's objects

A dataset's writer hands the file writer its rows in the order of the dataset's sort key: an append
sorts each partition's rows in memory first, and a job merges its inputs in key order
([the layout](23-query-architecture.md#44-sorted-objects-from-the-first-write)). The file writer cuts no
object by its key: its granules are its blocks, 8 192 rows by default, and the sparse index the object
keeps names the first key of each ([the sparse
index](10-indexes.md#44-vorticitykeyindexv1-the-first-key-of-each-granule)).

A dataset's object chunks each column on its own. A column gathers its rows across the object's chunks
until they would encode to about 1 MiB (`DatasetOptions.ChunkBytes`, internal) at the rate its last
chunk compressed to, the first guessed at a quarter, and holds at most sixteen times that in canonical
bytes, so that a question reading a few columns fetches segments of about a megabyte rather than every
column's bytes between them, and a question reading a few granules of a column the ranges of those
granules in its chunks ([reading a granule, not a
chunk](23-query-architecture.md#53-reading-a-granule-not-a-chunk)). The chunks of every column end
together at least every 2²² rows (`GroupRows`): where a lane's range of the object starts, so that no
chunk is read by two lanes. A file written alone keeps its columns chunked together, which the decision
of 2026-10-11 left as the default for a file read locally.

A column's chunk that is due leaves its gathering for an emission of its own (`VortexFileWriter.ColumnEmission`), a unit of the session's core that a free lane compresses while the blocks after the chunk go through, where the blocks once waited for the chunk's compression at the end of the file's chunk. It takes with it what the column's ingest knew of its rows (`ColumnWriter.Freeze`): the summary of its blocks, the sum of their width histograms, its distinct table and what that held at its last block. The column starts its next chunk in another table, and its emission reads none of the column's state the blocks after it change. The emissions of a column that is not shredded follow one another, each offered as the one before ends, since the plan a chunk is chosen under is the one its column's chunk before left. Those of different columns, and those of a shredded column, which keeps no memory of its plans, go at once. A column whose last trial priced zstd on enough of its values compresses its frames as items of its emission, on the session's lanes, and the item that ends last writes the column, its trial taking the frames. A chunk lands in the file when its column's chunk after next is due, or at a flush, the end of a group or the completion: two of a column's chunks are in emission at most, and what the emission decided for the column's ingest, whether its table runs and its widths are counted on the chunks after it, takes effect there, a point of the rows' own order. So the file is the same bytes at any degree, and the work the ingest does too. A file's first chunks, met before the file holds an encoding id, are written on the writer's flow, which interns the file's first ids.

Measured on the client's spans, two hours at 500 rows a second over a store 5 ms away, at 16 lanes, cold
(`vortex-queries --micro paths --qp`): an hour's object went from about 2 000 segments to 102, 555 KB
each on average; QP-1 read 33.9 MB in 75 requests in 123 ms, against 177.7 MB in 232 in 280 ms; QP-5
504 MB in 545 requests in 470 ms, against 1 061 MB in 2 688 in 1 291 ms. A needle took a segment a
column then, about a megabyte, where it took the small chunk of every column in one; it now reads the
granules that hold its row, through the marks of their chunks.

A read of a few granules slices a dictionary's codes but reads its values whole, as any row may name
any of them, where FSST's bytes lie between its granules' marks. So an object, which its readers read
granule by granule, writes a text chunk as FSST rather than a dictionary when the dictionary's values
weigh more than one granule's share of it, so that a read of one granule reads more than twice its
share, and FSST writes the chunk in no more than the dictionary does and one granule's share of it,
an eighth at most. The values are priced as FSST would write them, at its ratio
to the plain column. A file written alone keeps the dictionary. A trace identifier of 32 hexadecimal
digits, one span in seven sharing its trace with a span a few rows before, read 2.07 MB of an object of
3.40 MB for one trace's spans as a dictionary, and 494 KB of 3.63 MB as FSST.

## 10. Shredded variants

A job of a schema with a variant column reads its rows twice, once to count the paths of each
partition's rows and choose what its variant columns shred, once to write them; another job reads them
once. A variant column is written whole by an append, and shredded by the job that rewrites it, so that
ingestion pays nothing for shredding, unless the dataset asks its appends to shred the paths it declares
(`DatasetOptions.ShredOnAppend`). Until that job runs, a query node that reads the variants of an object
of level 0 shreds a copy of them in memory, once per object and under the session's budget, and keeps it
until the object leaves level 0, so every question after the first reads typed columns there too.

An append still counts the paths of its rows, on another lane than its writer's and without shredding them, for its
path directory, so that a question on a path refutes the objects and granules of level 0 that lack it, and the paths
of a window are read from directories (`DatasetOptions.PathDirectoryOnAppend`, on by default): about 0.37 µs of a
processor a row of log records.

The job's first pass counts each top-level key, its class and its bytes (`PathCensus`). Every path whose
dominant class the object holds 2 048 values of or more has its typed column, a stream of its own, dense,
or sparse where the compressor finds nine nulls in ten (`vortex.sparse`); a rarer path stays in the
residual, where the path directory says which granules hold it. When more paths qualify than a chunk's
encoding tree may hold, those held most are kept. No saving in bytes decides: a sparse stream of `n`
values takes about `6 n` bytes, its positions and its values, where the residual took about `3.6 n` for
integers, and a question on the path then reads its values alone, where it parsed the residual of every
row that holds it. A shredded path adds 154 to 217 bytes to each chunk and 4 to 10 tables to the chunk's
encoding tree (`vortex-queries --micro shredcost`). A path the dataset declares shredded
(`DatasetOptions.ShreddedPaths`) is a column of every object whatever share of its rows holds it.

The keys chosen are written in the reference's form, the `vortex.variant` encoding with a typed tree,
integers of every width in one 64-bit column, each key encoded the same way in every chunk, and a chunk
shreds only the keys its rows hold. The validity of a shredded field goes through the compressor as any
column does, as runs where it has few of them, as a bitmap otherwise. A path map in each chunk's metadata
(`vorticity.variant.paths.v1`) says which keys it holds and where. On a million rows of six to eight
attributes, counting and choosing cost 256 ns a row, and the shredded write 310 ns a row more than the
whole one (`vortex-queries --micro shredwrite`).

A chunk's write holds its values and one typed column. The shredder takes each key's values at the rows
that hold it, and nothing at the others, and the blob writer lays a typed column out over the chunk's
rows only when it comes to it, in an arena emptied before the next. Laid out all at once, the columns
of a chunk of a million rows of log attributes cost a slot a row a key, sixteen bytes of view for a
text: a consolidation of 72 million such rows held 9.9 GB at its writer's peak, and allocated 1 145
bytes a row; written a column at a time, 3.1 GB and 56 bytes, its write 8 % shorter
(`vortex-queries --micro consolidation`).

A shredded chunk written on more than one lane writes its leaves apart, whether it is emitted by a task of its own, the chunk's columns compress side by side, or the writer's flow writes it: once its rows are shredded, each leaf of its blob, the rows' metadata, a key's values or its typed column, is an item of a piece of the core, laid out and compressed in a workspace and against an encoding dictionary of its own, on the session's lanes beside the others. Side by side, the columns' items take the shredded columns' rows, and a second pass writes the leaves of them all. The writer's flow awaits the piece, then grafts the blob from the leaves in the order the blob comes to them: each leaf's tree written again node by node in the blob's builder, its buffers past the blob's, its ids the file's in the order the blob meets them. The blob is the bytes of the blob written in one go, whatever order the leaves were written in. The rows' metadata, which every variant node of the tree names, is compressed once a validity encoding and its buffers copied where it is named again. A chunk emitted by a task of its own has its rows taken block by block on the writer's line before it is due: each block's rows of the column taken by an item of its own, after the block before's, at their place in the column's chunk, into a context the chunk keeps, so that the chunk's leaves start from rows already taken. A chunk a block left out of the line's order, one written alone at a place that does not follow the rows taken, or one cut across, is taken whole where it is compressed, as before; the rows taken in parts are the rows taken whole, to the byte. A chunk's leaves wrote one after another before, hundreds of keys of a log's attributes, close to a core of serial work a variant column.

A body is a variant too: a line of text keeps its text, which the trigram and token indexes search, and
an object is shredded as attributes are, and shown again from its columns and residual, keys in order.

## 11. Deliberately not done

| approach | why not |
|---|---|
| pricing from a sample, as BtrBlocks does | it prices what exact formulas price for free, and costs determinism. Plan memory takes most of what it would save on drifting text |
| `fastlanes.rle` | its per-row indices need `fastlanes.delta`, which no edition carries, to come under run-end's ends. Without it, run-end is smaller at every run length |
| a sparse fill other than null | a dominant value that is the chunk's minimum is already a bit-packing of width zero with patches, and one that is not makes runs. Finding it would take a frequency count on every chunk |
| emitting a chunk at every batch | a chunk edge inside a block saves no I/O on a pruned zone. A caller who wants no buffering writes in multiples of `BlockRows` |
| rewriting without decoding, passing encoded arrays through | a separate feature, worth an order of magnitude on rewrites and compactions and nothing on first writes |
| buffering a column for global decisions, or a global sorted index | it breaks streaming and bounds memory by the file. Sorted runs per chunk, merged, give the same answers (see [sorted runs](10-indexes.md#52-vorticitysortedrunsv1-value-to-rows-exact)) |
| one FSST symbol table shared across chunks | measured at 4.3 % more bytes |
| the `vortex.dict` layout, one values child per column, for any file | what the [determinism limit](#7-determinism-and-its-limit) would need. It changes the bytes of every dictionary column, and has to be weighed on the size ratchet and the cross-check together; a dataset's writers emit it for the columns whose values they know ahead |

The reference writer this is measured against is `vortex-file`'s strategy and `vortex-btrblocks`'
schemes at 0.86.1. The kernels follow FastLanes (Afroozeh and Boncz, VLDB 2023) and ALP (Afroozeh,
Kuiper and Boncz, SIGMOD 2024).
