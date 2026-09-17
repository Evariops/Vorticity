# Write strategy: the fused block pipeline

A Vorticity specification, written 2026-09-15 and revised the same day after a review of its
first iteration (§10 lists what the review changed). It starts from the measurements in
[WRITE-ARCHITECTURE.md](../WRITE-ARCHITECTURE.md) (§1, kept verbatim) and specifies the writer
that replaces today's trial encoder: one fused, typed pass per block of 8 192 rows that computes
everything that must be exact and everything the chooser needs; verdicts by exact formula; one
encoding pass into output written once; zones equal to blocks; index builders fed by the same
pass; no sampling; no extra parallelism; scratch owned by the writer, not by the column. It also
specifies what the reader does with what the writer produced (§6), because a pruning structure is
only as good as the scan that consults it, and the surface a caller sees (§7), because a writer
that needs to be configured to be fast is not fast.

Four constraints shape it, in this order: **maximum throughput on one thread**; **100 % reliable
pruning structures** (min/max, Bloom filters, and the indexes of [10-indexes.md](10-indexes.md));
**indexes as first-class outputs of the same pass**, including across successive appends;
**no per-row allocation and no chunk-sized scratch**.

---

## 1. The starting point, in one table

Measured 2026-09-15, a million rows, `--throughput --write`, read subtracted (write-only ratio),
profile shares on the main thread (WRITE-ARCHITECTURE.md §1).

| file | ours (ms) | Rust (ms) | ratio | where the time goes |
|---|---|---|---|---|
| `variant` | 19,0 | 2,1 | 9,3 | 62 % discovering a constant row by row; 18 % transit copies |
| `fastlanes_bitpacked` | 21,5 | 5,6 | 3,9 | `Dictionary` 34 % (cannot win); zero-fill 17 %; exception count 7 %; zone map 9 % |
| `masked_all_invalid` | 3,8 | 1,1 | 3,4 | 56 % discovering all-null bit by bit; 18 % zone map; 9 % copying values nobody reads |
| `alp_no_patches` | 40,1 | 16,4 | 2,5 | `Dictionary` 63 % (cannot win) |
| `onpair`, `zstd` | 58,9 / 70,0 | 25,6 / 37,1 | 2,3 / 1,9 | dictionary hashing 37-47 %; zero-fill 16 % |
| `dict_nullable_codes` | 31,3 | 14,6 | 2,2 | `Equal`/`Hash` 31 %; zero-fill 18 %; codes re-chosen from scratch 13 % |

Four structural causes (WRITE-ARCHITECTURE.md §3): no shared statistics; pricing by full trial;
no degenerate-case short-circuit; generic per-row passes with no SIMD. Plus zero-filled and
re-copied outputs, and zone maps coupled to chunk shape.

What a writer decides, per column chunk, in order: **chunk shape** (rows, bytes), **zone
granularity**, **encoding** (and its cascade), **output assembly** (buffers → blob → segment).
Every approach below touches one of the four.

---

## 2. The target in one page

**One principle.** A value is **iterated twice** between its arrival and its emission, and copied
never: once by the fused statistics pass, on the batch where the caller put it, and once by the
encoder of the scheme that won, which reads the same rows again **from cache** — a block is
64 KiB and a chunk about a mebibyte, so the second read costs its work, not its loads (re-reading
a chunk from L2 is under 0,1 ns per value against 1 to 3 ns of work per value). On the blocks
where a frame of reference is in contention there is a third iteration, a sweep over the block
while it is still in L1 (§3.2.3). One scheme never reads the values a second time: the dictionary
assigns its codes in the first pass, as today's single probe loop does, and its encoder packs the
codes (§3.2.2). A single pass for the others is not possible without guessing: every other wire
form carries a parameter known only once the chunk is whole — the bit width, the frame of
reference, ALP's exponent pair — and a guess taken from the previous chunk would have to be
verified and, when wrong, re-encoded, for a gain bounded by the loads the second iteration
spends, a few percent of the column. Everything that must be exact — min, max, null count, the
Bloom filter, the distinct set behind an index — is computed in the first iteration, over every
row, never sampled. Everything the chooser needs — run boundaries, sortedness, bit-width
histograms, distinct count, byte lengths — is computed in the same iteration. The chooser decides by **exact formulas** for every scheme whose
cost is a function of those statistics, and runs a trial only for the three encoders whose cost
is not (FSST, zstd, ALP), under the best cost already in hand as an abort ceiling, **once per
column** and again only when the actual output size stops matching the prediction. The winner
encodes in one pass into arena-owned output that is written once.

**What it replaces.** Today's passes over a `u32` chunk, and where each goes:

| today (WRITE-ARCHITECTURE.md §2.3) | in the target |
|---|---|
| transit `CopyFrom` + `CanonicalConcat` | the stats pass runs on the batch where it lies; the encoder concatenates into its output |
| `SequencePlan` walk | sorted flag + constant delta from the fused pass |
| `TryRuns` (¼ to 1 pass, `Equal` per row) | run-boundary bitmap from a vector compare |
| `BitPackPlan.Minimum`, histogram, `Collect` count (3 passes) | min/max + raw and zigzag histograms in the fused pass; the FoR histogram in a conditional L1 sweep; patches gathered inside the pack |
| `Dictionary` (1 pass, never abandons on low cardinality) | one running distinct table per chunk in the fused pass, codes assigned as rows arrive (today's loop, moved); exact size formula; the table runs only while a consumer is live (§3.2.2) |
| `PlainBinarySize` | byte-length sum in the fused pass |
| `Pack` + 2 zero-fills + `ToArray` + blob copy | pack into an uninitialised arena buffer that *is* the segment |
| `ZoneStatistics.Compute` (2 passes after encoding) | the block statistics *are* the zone map |
| `Choose` again on codes, ALP ints, offsets | width known from the parent; direct pack |

**What it guarantees.**

- **Bytes**: identical to today's output for every scheme, because the formulas *are* the sizes
  the encoders produce and frame of reference keeps its exact patched pricing (§3.2.3). The only
  byte changes are the zone map's shape (one zone per 8 192-row block, the reference's shape) and,
  if so decided, the wire form of a constant (§10).
- **Determinism**: a pure function of the sequence of batches. No seed, no sample. Plan memory
  makes a chunk's encoding depend on the chunks before it, which is still a pure function of the
  input; the file's chunking already depended on the caller's batching. *One exception since
  step 20: the sixteen bytes of the file's identity ([13-dataset.md](13-dataset.md) §7) are random
  unless `VortexWriteOptions.Identity` pins them, and with them pinned the file is again a pure
  function of its batches.*
- **Pruning structures**: exact by construction (§5.1); an index is written whole or not at all;
  an append extends them without touching what was written (§3.8).
- **Memory**: one block-sized scratch per **writer**, reused for every column of every block;
  per column only a small state; every cap is a writer-level budget; zero allocations per row.

---

## 3. The pipeline, stage by stage

### 3.0 Structure: one `ColumnWriter` per leaf column

`VortexFileWriter` keeps the segment list, the shared scratch (§3.7) and a **tree of
`ColumnWriter`s** mirroring the schema, one per physical leaf (a struct field, a list's elements
child, an extension's storage). Each `ColumnWriter` is a state machine fed by every `WriteAsync`:

| state | contents | size |
|---|---|---|
| open block | mergeable partials for the block in progress (§3.2), the previous value for run and delta continuity | a few hundred bytes, plus one copied value for strings |
| closed blocks | one `BlockStats` per closed block, compact, until the zone map is written | ~200 B per block |
| plan memory | last plan, its predicted cost, the actual bytes it produced (§3.4.3) | small |
| index builders | the builders the policy started, their partials and runs (§3.3, [10-indexes.md](10-indexes.md) §7) | per policy, capped by the writer's budget |
| distinct table, key heap, codes buffer | the chunk's running table (§3.2.2), the distinct values it owns, one code per row of the chunk | a small multiple of the column's share of the pending chunk; capped by the writer's budget |

Nothing else is global. An append (§3.8) is a `ColumnWriter` seeded from an existing file; a
nested column is a `ColumnWriter` whose blocks are defined by its parent's rows (§3.2.4).

### 3.1 Ingest: blocks, not batches

A **block** is 8 192 rows counted from row 0 of the file, independently of how the caller
batches. It is the zone length, upstream's `row_block_size`, and eight FastLanes blocks of 1 024.
A batch of 8 131 rows straddles two blocks; the fused pass therefore produces **partials** per
(column, block) that are mergeable: min, max, counts, histograms and hash buffers merge exactly;
the run-boundary count and the delta bounds merge given the previous value, which the open block
keeps (copied, for strings, since the previous batch's arena will be recycled). A block's
statistics are final when its last row has been seen; its Bloom filter and its zone row are
produced at that moment (§3.3).

The pass runs on the batch's own arena, while the decode that produced it is still in cache. No
transit copy precedes it. A batch that is a multiple of 8 192 rows and meets the byte threshold is
emitted as its own chunk; any other batch is retained (the compact `CopyFrom` of W-31) until
enough rows are pending, and **chunks stay multiples of 8 192 rows**: a chunk edge inside a block
would put a zone edge inside a segment, where a pruned zone saves no I/O (§6.2). The transit
this costs is at most 12 ms per million rows on string columns and nothing on fixed-width ones
(WRITE-ARCHITECTURE.md §1.2); the way to avoid it is to batch in multiples of
`VortexFileWriter.PreferredBatchRows` (8 192), which §7 makes a public constant.

### 3.2 The fused pass: `BlockStats`

Per column and per block, one loop resolved on the physical type before it starts (the form of
R7, W-9 and W-33: a generic method instantiated per `PType`, spans cast once, the validity walked
as bitmap words, never `IsValid` per row). It fills a `BlockStats` struct:

| field | exact? | used by |
|---|---|---|
| `min`, `max` (NaN skipped for floats; raw bits compared for `-0.0`/`+0.0`) | exact | zone map, FoR bound, sequence check, constant check |
| `null_count` | exact | zone map, all-null check, every cost formula |
| `sorted`, `strict_sorted` | exact | sequence check, run-end bound |
| `delta_min`, `delta_max` (integers, consecutive valid rows) | exact | sequence: `sorted && delta_min == delta_max` |
| `run_boundaries` bitmap (1 bit per row, 1 KiB) and its popcount | exact | run-end cost and encode |
| `bits_raw[65]`, `bits_zigzag[65]` (integers) | exact | bit-packing cost with patches in the raw and zigzag domains |
| `distinct_count`, from the block stamps of the running **distinct table** (§3.2.2), while a consumer keeps it running | exact | dictionary cost, postings index, Bloom sizing |
| `entry_bytes` (strings: bytes of the distinct values, while the table runs), `total_bytes`, `inline_count` | exact | dictionary and varbin cost, FSST/zstd ceilings |
| hash buffer (64 KiB, writer scratch) | — | Bloom build at block close, distinct table |
| `str_min`, `str_max` bounded prefixes (strings, policy) | exact bound | zone map |

**Cost budget**: under 3 ns per value scalar and under 1 ns vectorised for fixed-width types
(§4); for strings the hash and the table probe dominate at ~4 ns per short value while the table
runs, under 1 ns once it is off (§3.2.2).

#### 3.2.1 Hashing

Fixed-width values are their own key: the table bucket comes from a multiply-xorshift mixer of the
value (the `Mix` of today's `RowComparer`, `ColumnCompressor.cs:885`); when the column carries a
Bloom filter the filter's hash is XxHash3-64 of the little-endian bytes and doubles as the table
key. Strings always hash their bytes with XxHash3-64: it is the hash upstream's Bloom filter uses,
the fastest 64-bit hash on short inputs (dedicated paths under 16, 128 and 240 bytes, a vectorised
stripe loop above, about 2 ns for a 10-byte string), and a byte comparison confirms every table
match anyway (§3.2.2). The implementation is the first-party .NET one,
`System.IO.Hashing.XxHash3` (`dotnet/runtime`, MIT), taken as the package: it is not part of the
shared framework, and [03-architecture.md](03-architecture.md) §1 admits this one first-party
dependency (decided 2026-09-15, [10-indexes.md](10-indexes.md) §11). The Parquet-compatible
xxHash64 exists only as the Bloom filter's explicit variant. A hash is computed **once per value
per pass** and written to the block's hash buffer, so the Bloom build at block close and the
table never rehash.

#### 3.2.2 The distinct table: running per chunk, keys owned, codes as rows arrive

Per column, **one** open-addressing table in the Swiss-table shape, **running over the chunk**: it
opens with the chunk's first block and closes with the chunk. It maps a key to its **code**,
assigned in first-seen order, which is what today's single probe loop does
(`ColumnCompressor.cs:616-622`: `code = distinct++`, `codes[row] = code`), so the dictionary's
codes and its values order are byte-identical to today's — and the dictionary is the one scheme
whose encoder never reads the values again (§3.5).

- A **control byte array** (one 7-bit tag per slot, load never above one half) and a **slot
  array**. A slot holds **the key itself** for fixed-width types (inline at its storage width) or,
  for strings, the 64-bit hash, an offset and a length into the column's **key heap**, where the
  value's bytes are copied the first time the value is seen; every slot also holds the code
  (`u32`) and a **block stamp** (`u16`). Sixteen bytes per slot up to 8-byte keys, twenty-four for
  strings and 16-byte decimals.
- Probe: hash → group of sixteen control bytes → vector compare against the tag → candidate
  slots → key comparison (one compare for fixed width; length, stored hash, then **bytes** for
  strings). Equality is on bytes, never on the hash alone: the table is exact. It never references
  a row of the batch, because a block straddles batches and the previous batch's arena is
  recycled the moment the next one is decoded; copying costs one copy per *distinct* value, and
  the key heap, in code order, *is* the data buffer of the dictionary's values child.
- The **codes buffer**: one code per row of the chunk, written by the probe, at the type's own
  width for `w ≤ 2` (a `u8` has at most 256 distinct values, a `u16` 65 536) and `u32` otherwise —
  never wider than the value it replaces, so never more than the chunk's own bytes.
- The **block stamp** gives the per-block distinct count without a per-block table: a row whose
  key is found in a slot stamped with an earlier block counts as a new distinct of this block and
  restamps the slot; a new key counts too. The restamp is also the postings event: a key restamped
  in block `b` occurs in `b` ([10-indexes.md](10-indexes.md) §6.1).
- Capacity: 1 024 slots at open, doubled as entries grow, up to a cap from the writer's budget;
  growth re-inserts from the stored hash (strings) or the key (fixed width), never from bytes. A
  low-cardinality column stays within a few kibibytes; a chunk that would exceed the cap refuses
  its dictionary candidate and its locating index, and keeps a per-block table (cleared at block
  close) for the Bloom sizing.

**The table runs only while it has a consumer.** Probing every row of a column whose values are
all distinct costs 2 to 5 ns per row for nothing. Its consumers are the **dictionary candidate**
of the chunk, the **Bloom sizing** (the block distinct count) and the **postings and sorted-run
builders**; a block is probed when at least one is live. The dictionary candidate is live on
every chunk priced in full (a column's first chunk, and every chunk plan memory sends back to full
pricing, §3.4.3) and on every chunk whose remembered plan is a dictionary; it is dead on a chunk
whose remembered plan is another scheme within tolerance. A full re-pricing that finds the table
off prices every scheme but the dictionary and turns the table on for the next chunk: one chunk
of lag, on a column whose data changed character. Index consumers are live by policy: under
`Auto` every cheap builder starts at block 0 and the table runs until the last of them has
abandoned ([10-indexes.md](10-indexes.md) §5.5); under `None` there are none. So a
high-cardinality string column with no index probes its first chunk and never again; a dictionary
column probes every row **once** and encodes without a second probe.

Per-chunk memory is a small multiple of the column's share of the pending chunk — the table at
most three times its bytes, the key heap and the codes buffer at most once each — and the chunk
threshold (`DataBlockTargetBytes`, 1 MiB) counts the whole batch, so the sum over a schema of any
width stays a few times the chunk's bytes.

#### 3.2.3 The second sweep: frame of reference, exactly

The histogram of `v − min` cannot be computed before `min` is known, so the fused pass cannot
price frame of reference with patches. It prices it with the **upper bound**
`packed(bits(max − min), rows)`: exact when there are no patches, and only ever higher than the
truth. When that bound already beats every other candidate, the encoder computes the exact width
and patches inside its own pass. When another candidate beats the bound by less than a margin
(`for_margin`, default 10 %), the writer runs a **second sweep over the block while it is still in
L1**: subtract `min`, leading-zero count, histogram — about 0,3 ns per value, on the contested
blocks only — and the chooser then compares exact costs. Bytes are therefore identical to
today's exact patched FoR pricing, at a fraction of its four passes.

#### 3.2.4 Per dtype, and nested columns

| kind | what the pass does |
|---|---|
| integers | everything in the table above; the zigzag histogram only for signed types |
| floats | min/max/NaN-aware, null count, sorted, run boundaries by raw bits, distinct table by raw bits; no histogram (ALP produces the integers whose histogram is fused into its encode, §3.5) |
| decimals | as integers at their storage width |
| bool | null count, run boundaries by the word trick of W-33, popcount of set bits (constant check); no table |
| utf8, binary | null count, run boundaries by **view** compare (16 bytes: equal views prove equal values; when views differ and lengths are equal the bytes are compared, so that two equal out-of-line strings at different offsets — the common case after compaction — do not split a run), distinct table, `total_bytes`, `entry_bytes`, `inline_count`, bounded `str_min`/`str_max` when asked |
| struct | one `ColumnWriter` per field; the struct contributes its null count only |
| list, fixed-size list | the elements child is a `ColumnWriter` whose **blocks are the parent's**: block `i` of the child covers the elements of parent rows `[8192·i, 8192·(i+1))`, found through the offsets, so that a zone or an index over the elements answers "does any element of a row in this block match". Element-side row counts are not block-aligned and never need to be |
| extension | the storage column, under the extension's name |
| variant | the shredded columns as struct fields; the core storage as binary |
| null | nothing |

### 3.3 Merging: zones, chunks, resolutions, file

- **Zone map**: a zone is a block; `min`, `max`, `null_count` (and the bounded string prefixes when
  present) go to `ZoneMapWriter` as they are. The zone map no longer depends on the chunk shape:
  the five corpus files that lose theirs today (WRITE-ARCHITECTURE.md §3.7) get it back, and an
  appended file keeps it.
- **Chunk statistics**: the merge of the chunk's block partials — the same struct, exact — is what
  the chooser reads. Run boundaries across a block edge count once; histograms add; the distinct
  count and `entry_bytes` are the running table's, on the chunks where it ran (§3.2.2).
- **Bloom filters are built at block close**, from the block's hash buffer, each sized from the
  block's **exact** distinct count, the block stamps of §3.2.2 (`n_blocks` = smallest power of two giving the target `fpp`).
  Coarser resolutions are **generations**: a filter over `k` consecutive blocks is built when the
  `k`-th closes, from the `k` retained hash buffers (`k × 64 KiB`, `k ≤ 16` by default), sized from
  the sum of the block distinct counts, which over-sizes it by at most the overlap and never
  under-sizes it. The file-level filter is the coarsest generation (`k` = the whole file when the
  policy asks; sized by the sum of block distincts, capped). Filters of different sizes are never
  merged by OR; a probe consults every generation that overlaps the live blocks.
- **Index runs**: a postings or sorted-key builder closes a **run** per chunk (the running table's
  entries, sorted, with the block ids their restamps recorded or the row positions the codes
  buffer scatters by), written as index segments at
  `CompleteAsync`. Runs are immutable and keyed by block range; an append adds runs; a rewrite
  compacts them ([10-indexes.md](10-indexes.md) §4.2).

### 3.4 Choose: exact verdicts, then bounded trials

#### 3.4.1 Order

1. **Degenerate cases** from the chunk statistics: `null_count == rows` → all-null;
   `min == max && null_count == 0` → constant; integers `sorted && delta_min == delta_max` and
   no null → sequence. No candidate runs.
2. **Exact-cost candidates**, each priced by formula (§3.4.2), the cheapest kept as `best`:
   canonical (varbin or varbinview for strings, whichever is smaller), run-end, bit-packing in the
   raw domain (`min ≥ 0`), zigzag then bit-packing (signed), frame of reference then bit-packing
   (bound, then the second sweep when contested), dictionary.
3. **Trial candidates**, only when the statistics leave them a chance and with `best` as the
   abort ceiling: ALP for floats; FSST and zstd for strings (`total_bytes ≥ 16 KiB` for zstd, as
   today). Each carries its encoded result, so a winner is never encoded twice.
4. **Plan memory** (§3.4.3) short-circuits 2 and 3 on a column whose last plan still predicts
   its output.

#### 3.4.2 Exact cost formulas

`rows` = the chunk's row count, `nulls` = `null_count`, `w` = element width in bytes,
`bits(x)` = 64 − lzcnt(x), `packed(b, n)` = FastLanes bytes for `n` values at `b` bits (whole
1 024-blocks), `patch(e)` = 256 + e × (w + bits(rows) / 8).

| scheme | cost | exact? |
|---|---|---|
| canonical primitive | `rows × w` + validity bytes | exact |
| canonical strings | `min(rows × 16 + data, (rows + 1) × offsetWidth + total_bytes)` | exact |
| run-end | `runs × (bits(rows) / 8 + w)` + 256, `runs` = boundaries + 1; declined outright when `runs > rows / 4` | exact bound (ends and values are cascaded, only smaller) |
| bit-packing, raw | `min over b of packed(b, rows) + patch(Σ bits_raw[b+1..])` | exact |
| zigzag + bit-packing | the same over `bits_zigzag` | exact |
| FoR + bit-packing | `packed(bits(max − min), rows)` + 256 as a bound; the same formula over the second sweep's histogram when contested | exact where it matters |
| dictionary | `packed(bits(entries − 1), rows)` + the values child priced by its own `Choose` (§3.4.4) + 512, with `entries` and the entries' bytes read from the running table; short-circuited by today's lower bound `rows + entries × minEntry ≥ best` before the child is priced; not priced on a chunk whose table was off (§3.2.2) | exact, with no further pass |
| sequence | ~32 bytes | exact |
| constant, all-null | ~32 bytes | exact |
| FSST, zstd, ALP | trial, aborted past `best` | measured, not estimated |

Every number above is what the encoder will write, so the chooser's verdict and the file's bytes
agree by construction; that is what makes `WrittenSizeTests` a test of the formulas.

#### 3.4.3 Plan memory, kept honest by the bytes it produced

Per column the writer keeps the last plan, **the cost the formulas predicted for it, and the
bytes the encoder actually produced**. On the next chunk it recomputes the prediction from the
new statistics (free: the formulas are arithmetic on `BlockStats`) and **reuses the plan without
re-pricing the alternatives** as long as the previous chunk's actual bytes were within
`plan_tolerance` (default 5 %) of their prediction; a trial scheme's prediction is the ratio it
achieved last time applied to `total_bytes`. Outside the tolerance the column re-prices in full,
and the distinct table follows the same signal: it runs on the chunks where the dictionary is
priced or chosen (§3.2.2).

**What memory skips is what costs — the walks and the trials — never a candidate the statistics
have already answered.** A progression (§3.4.1) and a run count the pass took are arithmetic on
`BlockStats`; they are compared before the remembered plan is re-priced, or a bit-packing that
held to the byte on the one chunk with a jump in it is re-priced on every progression that
follows, holds again, and is written at 295 KB a chunk where 32 bytes are exact. Measured on the
1M-row `chunked` file: 1 902 356 bytes against 890 004, and +70 % on the clock that the bytes,
not the time, explained.

**A dictionary is held to its own layer.** Its prediction is codes at width plus entries; the
children then take schemes of their own (§3.4.4), and the buffers they append are a fraction of
the layer — on `dict_u8_codes` 363 bytes against 66 738, chunk after chunk. Measured by the
subtree, the prediction never held, the distinct table was never expected to serve (§3.2.2), and
every chunk walked for the dictionary the table had already built. The encoder reports the layer
as built — the same formula the plan was priced by — and that is what the tolerance compares.
Measured: `dict` 15,7 → 11,3 ms per million rows, `table_mixed` 151 → 77.

**The chunk after a plan holds opens on rows ingested before it held.** The emission carries the
tail of the batch it cut, and that tail is already the next chunk's first block when the plan is
remembered — probed into no table, counted into no histogram. The tables see it again at the
carry (§3.2.2); the width histograms are counted at the same moment, so the block is whole and the
chunk that opens on it reads its widths instead of walking with 31 counted blocks behind it that
served nobody. Measured on `fastlanes_bitpacked`: one chunk in four counted for nothing, +13 % on
the axis; counted at the carry, and with the raw count split over two alternating histograms so a
run of equal widths is not one chain of dependent increments, −5 % under the writer before the
pass existed.
This replaces bucketed signatures and periodic re-pricing with a control that costs nothing and
measures the one thing that matters. An FSST symbol table is still trained per chunk (sharing one
was measured at 4,3 % of output size, `bench/PLAN.md:62`); what is remembered is the decision,
not the table. A caller may pin a plan per column (`EncodingHint`), which is the same mechanism
with the tolerance set to infinity, and an appended file seeds the memory from its last chunk
(§3.8).

#### 3.4.4 Cascade with context

A child never re-enters `Choose` blind:

| child | what the parent knows | what happens |
|---|---|---|
| dictionary codes | dense in `[0, entries)`, non-negative | width `bits(entries − 1)`, packed directly; run-end considered only if the parent's `runs ≤ rows / 4` (the codes have exactly the parent's boundaries) |
| ALP integers | produced by the encode, histogram fused into it | FoR is a no-op when `min = 0`; pack at the width the histogram gives |
| run-end ends | strictly increasing, bounded by `rows` | delta then pack, or pack |
| varbin offsets, list offsets and sizes | monotone or bounded | sequence when constant length (from `delta_min == delta_max` of the sizes), else pack |
| dictionary values, run-end values | a column like any other | full `Choose`, on a small input |

### 3.5 Encode: one pass, output written once

The winner reads the chunk's rows — from the batch or from the list of retained batches, block by
block, **never from a concatenation** — and writes into buffers obtained from
`CanonicalArena.AllocateUninitialized`, sized exactly, that become the segment's buffers without a
further copy. `PendingBuffer` carries a view; the blob is assembled in one pass; nothing is zeroed
that is fully overwritten.

| scheme | the encode pass |
|---|---|
| bit-packing (raw) | per 1 024-block: exceptions found by a `v ≥ 2^b` mask and appended to the patch list inline; `PackBlock` on the block; no separate count or collect pass, no clear of the destination |
| zigzag, FoR | the transform is applied on load, in the same loop (`(v << 1) ^ (v >> 63)`, `v − min`); FoR's width and patches come from the second sweep's histogram when it ran, or from a histogram fused into this first sweep of the block otherwise, packing from the block still in L1 |
| dictionary | no lookup, no hash, no second read of the values: the chunk's codes buffer (§3.2.2) is packed by FastLanes at `bits(entries − 1)`; the values child is the entries in code order — the key heap and its offsets for strings, the slots' keys gathered by code for fixed width, one pass over at most twice the entries |
| run-end | ends from the boundary bitmap by trailing-zero walks; values gathered at the boundaries |
| ALP | vectorised encode (§4) with exceptions inline and the integers' histogram fused; then FoR/pack of the integers |
| FSST | as today (`FsstPlan` carries its result); the input gather is one pass into a rented buffer |
| zstd | as today; the value stream is gathered once |
| canonical strings as varbin | heap and offsets written once, into their final position |
| validity | bitmaps concatenated by word shifts into their final buffer |

### 3.6 Emit: chunks, zones, segments, indexes, report

- A chunk is emitted when at least `RowBlockSize` rows and `DataBlockTargetBytes` bytes are
  pending, as whole blocks (today's rule; the remainder is carried); a batch that already meets
  both and is a multiple of 8 192 rows goes out where it lies.
- The zone map (from the closed blocks' statistics), the file statistics and the index runs are
  written at `CompleteAsync`, in that order, before the footer, as today.
- `CompleteAsync` returns a **`WriteReport`** (§7.3): per column the encodings chosen and how often
  plan memory was reused, per index what was built, its size, and what was abandoned and why.

**As delivered (step 11a).** "As today" was not true of the file statistics: the writer wrote no
statistics segment at all. It does now, after the zone maps and before the footer: one
`ArrayStats` per top-level field, the merge of the column's closed blocks — `min` / `max` in
`Exact` for the numeric domains the block summaries hold (a string column has no bound here, the
bounded prefixes of §3.2 being a policy), `null_count`, and `is_sorted` / `is_strict_sorted`
whenever the pass tracked the column's order, absent otherwise. The order is tracked without
touching the bounds loop: the seam of every range with the row before it, a progression's step,
and for the rest a second, vectorised walk over the range that runs only while the column can
still be sorted — a witness is sticky — and rides on the pairs `ViewRuns` already reads for a
string column. The semantics are the reference's (`aggregate_fn/fns/is_sorted`): a null below
every value, equal neighbours allowed by `is_sorted` and refused by `is_strict_sorted`, a NaN
claiming nothing; the Rust cross-check holds every exact statistic written against the
reference's recomputation over the canonical column. `VortexWriteOptions.FileStatistics` turns
the segment off. A few dozen bytes per field; about two kilobytes of allocation per file.

### 3.7 Memory and allocation model

| what | owner | size | lifetime |
|---|---|---|---|
| block scratch: hash buffers up to 16 × 64 KiB for the coarsest active Bloom generation, boundary bitmap 1 KiB, two histograms, FoR bias buffer 64 KiB, a per-block table for the Bloom sizing of a chunk whose running table was refused | **writer** | ≈ 1,1 MiB with a 16-block generation active, ≈ 130 KiB otherwise | allocated once, reused for every column of every block |
| open block partial, closed `BlockStats`, plan memory | column | a few hundred bytes plus ~200 B per closed block | the file |
| running distinct table, key heap, codes buffer (§3.2.2) | column, from the writer's budget | at most three times, once and once the column's share of the pending chunk; summed over the schema, a few times the chunk's bytes | until the chunk is encoded; the table is off on chunks without a consumer |
| index partials and runs | column, from the writer's budget (`IndexBudgetBytes`, default 64 MiB per file) | until `CompleteAsync` |
| encoder outputs | arena | exact size of the segment | released when the segment is written |
| per row | — | **nothing** | — |

A schema of a thousand columns costs a thousand small states and one scratch, not a thousand
scratches. `WriteAllocationTests` gets one row per stage of §8, and `PerRowDispatchTests` goes to
zero for `Writing/` rather than being raised.

### 3.8 Appending to an existing file

*Amended by [13-dataset.md](13-dataset.md) §6.1 and §12: this in-place append stays as the
single-file mode beside the dataset. It keeps at most K = 4 runs per index entry and merges them
past that, mints a new identity at every postscript (13 §7), and the reader falls back to
`previous_eof` on a torn tail instead of failing.*

`VortexFileWriter.Append(path)` opens the file, reads its footer, and continues it: the old
segments stay where they are and keep their ids; new blocks are numbered from the old row count
(blocks are counted from row 0, so their boundaries are the same they would have been in one
write); new segments, the zone map, new index runs, a new directory, footer and postscript are
appended; the old postscript becomes dead bytes. Three rules make it exact:

- **The last chunk is re-opened when it is not block-aligned.** A zone map allows only its last
  zone to be short, and the last block's Bloom filter and index run were built on a short block.
  If the old row count is not a multiple of 8 192, the last chunk is decoded (one chunk), its rows
  become the first pending batch, its segments are dropped from the layout, and the runs covering
  its blocks are replaced. Cost: one chunk, whatever the file's size.
- **Plan memory is seeded from the last chunk's encoding tree**, read from the footer, so that the
  appended data keeps the file's encodings unless its statistics say otherwise.
- **The index policy is read from the directory**, so an append needs no options
  ([10-indexes.md](10-indexes.md) §7.1).

An append is not atomic on its own: the caller is single-writer and flushes; a torn append leaves
the file's tail invalid. The directory records the previous end of file, and `vxdump --repair`
truncates a torn file to the last valid postscript found by scanning back for the EOF marker.

*Since step 25 (13 §12), a torn file opens without repair.* The open finds that same postscript,
reads the version it ends, and reports the tear in `VortexFile.TornTail`.
`VortexOpenOptions.TornTail = Refuse` keeps the old failure. An append, an indexing pass or a
sidecar refuses a torn file until it is repaired.
Object stores that cannot append (S3) take the sidecar path of [10-indexes.md](10-indexes.md) §8
or a rewrite.

**As delivered (step 17).** `VortexFileWriter.AppendAsync(path, options?)` continues a file of this
writer's shape — a struct of columns, each a chunked layout of flat segments, zoned or not, chunked
alike — and refuses any other with `VortexUnsupportedException` ("rewrite it instead"). The old
segment specs and array-encoding indices are taken over as they are; the block length is the zone
map's; the index policy and the index budget come from the directory when `options` is null (the
budget is a directory field since this step, written only when it is not the default). When the
old row count is not a whole number of blocks, the last chunk is read, copied out, dropped from the
layout and written again as the first rows. Every block before the boundary becomes a summary taken
from the old zone map — rows, null count, bounds when they are exact — so the zone map is written
again over the whole file without reading those blocks; a column whose old part had no zone map
gets none. The file statistics combine the old ones (bounds, which cover every old row, and the
order flags) with the new blocks': the order across the seam is the two halves' own when a chunk was
re-opened, and a comparison of the old maximum with the new minimum otherwise, a null after an old
value unsorting the file. The index builders start at the boundary block and row; the old entries'
runs that end by the boundary are listed again, merged into the new entry of the same kind, column
and options (Bloom counts laid block by block, or run by run), and the dictionary probe is
recomputed from the chunks' schemes. Plan memory is seeded from the last chunk's encoding tree (one
segment read per column, `PlanSeed`): each column and struct field whose root is an encoding a
scheme writes starts with that scheme as a memory that held, with the distinct table live under a
dictionary and the width histograms under a bit-packing without a frame. A canonical chunk seeds
nothing, so a short last chunk does not hold a large append canonical. A test reads every chunk of
the corpus rewritten by this writer back through the seed and compares it with the `WriteReport`.
The repair is
`VortexFileRepair.RepairAsync` (and `vxdump --repair`): it walks back from the end for an EOF record
whose prefix opens as a file, and truncates to it. `vxdump --indexes` prints the directory and
`--explain "expr"` the plan and the count of a filtered scan. The acceptance test writes the same
rows once and in two, three and eleven pieces, cuts on and off a block, and compares values, zone
maps, file statistics and index answers; the Rust cross-check reads 22 appended files among its 854.

---

## 4. SIMD, kernel by kernel

Convention, unchanged from the read path: `Vector128` is the baseline (NEON on Apple silicon,
SSE on x86) and every kernel keeps a scalar path the suite runs under
`DOTNET_EnableHWIntrinsic=0`; `Vector256` and `Vector512` paths exist only behind
`IsHardwareAccelerated` (x86 AVX2, AVX-512). Availability below was checked against the
`System.Runtime.Intrinsics` reference assembly of the .NET 11 SDK this repository pins
(`11.0.100-rc.1`): `Vector128.Min/Max`, `IsNaN`, `Round`, `ConvertToInt64`, `ConvertToDouble`,
`ExtractMostSignificantBits`, `ShiftRightArithmetic`, `Multiply` exist; **`LeadingZeroCount`,
`PopCount` and `CompressStore` do not exist on `Vector128`** — lane lzcnt is `AdvSimd` (8-, 16-
and 32-bit lanes only) and `Avx512CD` (32- and 64-bit lanes); and `Vector128.Multiply<ulong>` is
software-emulated on NEON, which has no 64-bit lane multiply.

### 4.1 The fused pass and the second sweep

| kernel | vector form | availability | gain | scalar fallback |
|---|---|---|---|---|
| min / max, fixed width | `Vector128.Min/Max` per lane, horizontal reduce per block; nullable: bitmap byte → lane mask, `ConditionalSelect` with the neutral element | portable | ×4 to ×8 over a scalar loop | one compare per value |
| floats with NaN | `IsNaN(v)` mask folded into the select | portable | same | same |
| null count | `PopCount` per 64-bit word of the bitmap | scalar `BitOperations` (one per cycle) | no SIMD needed; the gain is leaving `IsValid` per row | — |
| run boundaries | `Equals(v, v_shifted_by_one)` then `ExtractMostSignificantBits`, stored to the boundary bitmap, popcount for the count | portable | ×8 to ×16 | compare per value |
| sorted / strict | `LessThanOrEqual(v, v_next)` all-true; `delta_min/max` by subtract then Min/Max | portable | ×4 | — |
| bit-width histogram, raw and zigzag | lane lzcnt (`AdvSimd.LeadingZeroCount` for 32-bit lanes; `Avx512CD` for 64-bit; otherwise the float-exponent trick: `ConvertToDouble` and read the exponent field) into a byte buffer, then scalar increments | platform paths | ×2: the increments stay scalar; scalar cost is already ~0,5 ns/value | `BitOperations.LeadingZeroCount` per value |
| second sweep (FoR) | subtract the broadcast `min`, lane lzcnt, byte buffer, scalar increments — on an L1-resident block | as above | the sweep is ~0,3 ns/value | — |
| zigzag domain | `(v << 1) ^ ShiftRightArithmetic(v, 63)` per lane before the lzcnt | portable | folded into the above | — |
| string view compare (runs) | one `Vector128<byte>` equality per pair of 16-byte views; a byte compare only when views differ at equal length | portable | ×8 on the common case | `SequenceEqual` |
| bounded `str_min`/`str_max` | prefix compare of 16 bytes: equality mask, first differing byte by `ExtractMostSignificantBits` and tzcnt | portable (what `SequenceCompareTo` does) | ×2 | `SequenceCompareTo` |
| hashing, fixed width | mixer on 32-bit lanes with `Multiply<uint>`; for 64-bit lanes an xor-shift mixer (no NEON 64-bit multiply) or scalar `Crc32.ComputeCrc32` (`Arm.Crc32`, `Sse42`: one per cycle) | portable / platform | ×2 to ×4 | `Mix` |
| hashing, strings (XxHash3-64) | short inputs take scalar dedicated paths (≤ 16, ≤ 128, ≤ 240 bytes: a handful of multiplies, no loop); above 240 bytes the stripe loop is vectorised (`Vector128` in the dotnet/runtime implementation) | portable | ×2 to ×3 over xxHash64 on short strings, which is the choice itself; a multi-buffer variant (four strings in lanes) is future work behind a measurement | — |
| distinct table probe | Swiss-table group compare: `Vector128<byte>` equality of sixteen control bytes with the tag, `ExtractMostSignificantBits` → candidate slots; for strings the stored hash is compared before the bytes; the code is stored to the codes buffer in the same step | portable | one dependent load per probe instead of a chain walk: ~×2 on the probe, and one probe per row for the whole write | linear probe on bytes |
| Bloom build at block close | for each hash of the buffer: the eight Parquet salts as a `Vector256<uint>` (or two `Vector128<uint>`), `bits = 1 << ((h × salt) >> 27)` per lane, OR into the 256-bit block | portable on 128 bits, one instruction per step on AVX2 | ~3 ns per value per generation against eight scalar bit sets | scalar loop |

### 4.2 Encoding

| kernel | vector form | availability | gain |
|---|---|---|---|
| FoR subtract, zigzag | applied on the load feeding `PackBlock` | portable | folded into the pack |
| patch detection | `GreaterThanOrEqual(v, 2^b)` mask per 1 024-block; indices extracted by tzcnt walks (rare by construction) | portable | ×8 on detection; extraction scalar |
| `PackBlock` | already vectorised (`FastLanes.cs:357`) | — | — |
| ALP encode | multiply by `10^e`, `Vector128.Round` (or the magic-number add), `ConvertToInt64`, back-conversion and bit compare → exception mask; the integers' lzcnt for their histogram in the same loop | portable | ×2 to ×3, the paper's own design |
| dictionary codes | no lookups at encode: the codes buffer is packed by `PackBlock` at one to four bytes per row; the first pass's tag compare is vectorised (§4.1) | — | — |
| run-end | boundaries by tzcnt over the bitmap (the W-33 form); values gathered scalar | — | — |
| varbin gather, inline views | a 12-byte inline value is one 16-byte load/store | portable | ×2 |
| validity concat | word shifts | scalar | — |
| postings and sorted runs (index) | delta then FastLanes pack of `u32` block ids or row positions | already vectorised | — |

### 4.3 Read side of the same structures

The pruning and probing kernels are the mirror of the above and share code: Bloom probe (eight
lanes, AND-compare), zone map min/max evaluation over many zones at once, postings unpack
(FastLanes), Swiss-table group compare and sorted merge-join for `IN (...)` against a run's keys.
Writing the SIMD kernels once, for both sides, is part of the design.

### 4.4 What SIMD does not fix

Short-string hashing, table probing (a dependent load), FSST and zstd (scalar by nature; our FSST
already beats the reference's), and every per-chunk fixed cost. On string-heavy files the wins are
the Bloom, the group probe and the gather; on integer and float files the whole fused pass and
ALP. Order of magnitude on a `u32` block: the fused pass at ~2 to 3 ns per value scalar and ~0,5 to
1 vectorised, which is 20 to 25 % of the write after §3, more with ALP.

---

## 5. Guarantees, and how they are tested

### 5.1 Reliability rules

1. **Contract statistics are never sampled.** Min, max, null count, Bloom insertions, distinct
   sets behind an index: every row, every time. Sampling exists nowhere in this design.
2. **Equality is on bytes.** A table hit is confirmed by comparing the key's bytes, which the table
   owns; hashes only route.
3. **An index is whole or absent.** A builder that hits its budget abandons the column's index,
   writes no entry, and the `WriteReport` says so; a partial index would be a silent false negative.
4. **Merges are exact or conservative.** Statistics merge exactly; a Bloom generation is built from
   its own hashes and sized from a sum that cannot under-size it; a bounded string min/max is a
   bound, marked as such; an appended file's runs cover disjoint block ranges.
5. **Pruning never removes a row a full scan returns** ([08-semantics.md](08-semantics.md) §1),
   tested with pruning and indexes on and off on the same queries, on written and on appended
   files, and with forged lying payloads.

### 5.2 Tests and gates

| what | how |
|---|---|
| bytes | `WrittenSizeTests` at the byte; the only expected moves are the zone map shape and, if decided, the constant's wire form |
| read back by Rust | `bench/crosscheck.sh`, unchanged, plus appended files |
| statistics | property tests: `BlockStats` against a naive per-row implementation on generated data, all dtypes, all validity shapes, block edges and batch edges included |
| formulas | for every scheme, the formula's cost equals the encoder's output size on the corpus; FoR's second sweep equals today's `BitPackPlan` pricing |
| dictionary | codes and values in first-seen order, byte-identical with today's `ColumnPlan.Dictionary` on the corpus; the table off on a stable non-dictionary column (probes per chunk at zero), back on within one chunk when the data changes character |
| plan memory | a column whose data changes character re-prices within one chunk; a stable column re-prices never (`table_mixed` as the witness) |
| Bloom | false-positive rate under 1,5 % at `fpp` 1 %; bit-for-bit equal to the Rust `BloomPartial` of 0.86.1 on the same keys; the Parquet-compatible variant bit-for-bit equal to a Parquet SBBF |
| indexes | equivalence on and off; forged fixture read by a strict Rust session; runs merged across an append probe the same as one write |
| append | write-then-append equals one write in rows returned, zone map content and index answers; a torn append is repaired by `vxdump --repair` and reads as the pre-append file |
| allocations | `WriteAllocationTests` per stage; zero per row on the 1 M-row files; a 1 000-column schema within one scratch |
| dispatch | `PerRowDispatchTests` at zero for `Writing/` |
| throughput | `--throughput --write --check`, the references lowered behind each stage; the five-configuration probe of WRITE-ARCHITECTURE.md §1.2 in `bench/` |
| SIMD | every kernel run under `DOTNET_EnableHWIntrinsic=0` in the suite, results identical |

### 5.3 Targets

Measured on the corpus, single thread, M4 Pro, against the Rust numbers of §1; **projections from
the measured shares, to be replaced by measurements stage by stage**:

| file | today | target | Rust |
|---|---|---|---|
| `fastlanes_bitpacked` | 21,5 ms | ≤ 5 ms | 5,6 |
| `alp_no_patches` | 40,1 | ≤ 12 | 16,4 |
| `masked_all_invalid` | 3,8 | ≤ 0,5 | 1,1 |
| `variant` | 19,0 | ≤ 1 (with the reader's constant form) | 2,1 |
| `dict_nullable_codes` | 31,3 | ≤ 15 | 14,6 |
| `zstd`, `onpair` | 70,0 / 58,9 | ≤ 40 / ≤ 30 | 37,1 / 25,6 |
| write axis median | 0,92 | < 0,6 | — |
| above ×2 | 11 encodings | none | — |
| `Auto` indexes on (`table_mixed`) | — | ≤ +10 % over the same write without | — |

---

## 6. Reading what the writer produced

The writer's structures are only worth what the scan does with them. Today the scan asks one
question per split, `ZonePruner.MayMatch(RowRange)`, before the split's single coalesced read
(`BatchAsyncEnumerable.cs:388`), and a split is a chunk sub-range capped by `MaxBatchRows`. With
zones per block and indexes at block granularity, the answer is finer than the question.

### 6.1 The contract: block masks, then selection

Every pruning structure implements one operation: **refine a mask of live blocks** for the scan's
predicate. The scan builds the mask once per query (a bit per block: 123 bits for a million rows,
15 KiB for a billion), runs the pruners **cheapest first** — zone map (min/max, null count, already
loaded), then Bloom generations from the coarsest to the finest (each only over blocks still live,
each hash of a literal computed once per query), then postings (one key lookup per literal, a
merge-join for a sorted `IN`), then exact indexes — and **stops as soon as the mask is empty**.
Then, per split:

- a split whose blocks are all dead is skipped before its read: the I/O saving;
- a split with dead blocks becomes a **row selection** covering its live blocks, fed to the row
  selection machinery `Take` already has, so `DecodeSelected` decodes the live blocks only: the
  decode saving;
- an `Exact` index that covers the whole predicate turns its rows into that selection and the
  predicate is not re-evaluated; under `OR`, `NOT`, or partial cover it only narrows, and
  `FilterEvaluator` runs on the survivors, as today.

Nulls are never inserted in an index, so `x = v` and `x IN (...)` are safe; `NOT` makes no claim;
`IS NULL` is the zone map's null count. The combination across `AND` is per block, which is
weaker than the truth and therefore safe, exactly `ZonePruner`'s rule.

A key-ordered scan ([12-index-reads.md](12-index-reads.md) §6) replaces the split walk by a
**window driver**: a key source walks a batch's worth of entries inside the slices the filter's
conjuncts on the key allow, the mask skips the entries of dead blocks, and the survivors are the
row selection above — registered, read and executed per split the window touches, then permuted
into key order. The mask is built the same way and consulted per entry instead of per split.

### 6.2 Granularity, honestly

I/O is per segment, and a segment is a chunk of 16 to 32 blocks in the default shape. A block
pruned inside a live segment saves decode, not bytes read. This is why chunks stay block-aligned
(§3.1), why the default `DataBlockTargetBytes` is a trade between pruning and request size, and
why the zone map's `zone_len` stays 8 192 whatever the chunk shape.

### 6.3 What the scan needs first

- **The expression model**: `ExprKind` has no `LIKE`, `StartsWith` or `Contains`. The n-gram
  structures of [10-indexes.md](10-indexes.md) bind to nothing until they exist in `VortexExpr`
  and `FilterEvaluator` (the BCL's vectorised `IndexOf` does the evaluation).
- **Lazy, cached loading**: the directory and the runs are read on the first `Where`, only for
  the blocks the mask still holds live, and cached on the `VortexFile`, which is shared between
  scans and therefore thread-safe ([09-contracts.md](09-contracts.md) §1).
  `VortexOpenOptions.PreloadIndexes` exists for object stores where a lazy read is a round trip.
- **File-level pruning**: `VortexFile.MayMatch(expr)` answers from the footer's file statistics
  and the file-level Bloom generation without reading a data segment; an engine over many files
  calls it before opening a scan.

### 6.4 Explain

`ScanBuilder.Explain()` returns the plan without executing it: splits in the file, blocks pruned
by each structure, rows selected by exact indexes, bytes to read against the file's size.
`ScanMetrics` reports the same after execution. Nothing of the kind exists today (`Diagnostics/`
holds exceptions and limits), and without it nobody can tell whether an index earns its bytes.

**As delivered (step 8d).** `ScanBuilder.ExplainAsync()` returns a `ScanPlan`: it is the same
planning the scan does before its first batch — the split plan, the mask refined by every
structure (the zone maps are read for that, as the scan reads them), the live splits registered
into one request set so segments are distinct and bytes counted once — and nothing is decoded.
Each `PruningStep` carries **what the structure pruned and what consulting it cost** (segments and
bytes), the two numbers this section asks for; the plan's totals are the live splits' data plus
that cost, and `FileMayMatch` is §6.3's answer. `WithMetrics(ScanMetrics)` hands the scan a sink
the caller owns; the pruning pass, the flat reader and the enumerator add to it what they ask,
materialize and produce, so that plan and measurement are one quantity: on the corpus's zoned file
the sink's requests equal the segment source's, exactly.

---

## 7. The surface a caller sees

### 7.1 Writing

- `VortexWriteOptions` keeps `Compress`, `TargetEdition`, `RowBlockSize`, `DataBlockTargetBytes`,
  and gains `Indexes` (a `WritePolicy`: per column path an `IndexPolicy`, default **`Auto`**) and
  `Profile` (`Default`, `Fastest` = no indexes, no bounded string stats, nothing beyond the zone
  map).
- **`Auto` is the default** because a Vortex file without pruning structures is what nobody wants
  and the writer already pays the pass. `Auto` starts every cheap builder and **abandons** those
  the statistics disqualify or the budget refuses: a Bloom on a sorted column (min/max already
  prunes), postings past the budget, a filter whose projected bytes exceed the column's share of
  `IndexBudgetBytes`. The decision is when to abandon, never when to start, which is what makes
  block 0 indexed like every other. Its cost is measured before it becomes the default (§5.3's
  last row).
- `VortexFileWriter.PreferredBatchRows` (8 192): a caller that batches in multiples of it pays no
  transit at all.
- `VortexFileWriter.Append(path, options?)`: §3.8; the policy comes from the file when omitted.
- `EncodingHint` per column, for callers who know.

### 7.2 Reading

- `Scan().Where(expr)` uses every structure the file carries with no further call;
  `WithIndexes(false)` and `WithPruning(false)` exist for debugging and for the equivalence tests.
- `VortexFile.MayMatch(expr)`, `VortexFile.Indexes` (an enumeration for tooling), `Explain()`,
  `ScanMetrics`.
- The order-shaped operations — `file.Keys(path)` cursors (seek, next, prev, rank, distinct), the
  terminals `AnyAsync`, `CountAsync`, `MinAsync`, `MaxAsync`, `InKeyOrder`, and `StartsWith` /
  `Contains` / `Like` in the expression model — are [12-index-reads.md](12-index-reads.md) §8,
  which also adds `MustMatch` and a count-only path beside the contract of §6.1.

### 7.3 Observability

`WriteReport` from `CompleteAsync`: per column, the encodings chosen per chunk and the plan-memory
hit rate; per index, built or abandoned with the reason, bytes, generations and runs; the file's
bytes by kind (data, zone maps, indexes). `vxdump --indexes` prints the directory, `--explain
<expr>` the plan, `--repair` truncates a torn append.

---

## 8. Staging

Each step is a measurable commit with the byte outcome stated; the order is chosen so that every
step is bytes-identical except where noted.

1. **`ColumnWriter` and `BlockStats` at ingest, consumed by the zone map.** Kills
   `ZoneStatistics`; zone maps return on the five files that lost them. *Bytes move on the zone map
   segment only* (one zone per block).
2. **Candidates read `BlockStats`.** `Minimum`, the histogram pass, the `Collect` count,
   `PlainBinarySize` and the run scan are deleted one by one; the dictionary's probe loop moves
   into the fused pass as the running table with its codes buffer (§3.2.2); the second sweep
   replaces the exact FoR pricing; bytes identical.
3. **Exact verdicts and degenerate cases first.** Bytes identical except the constant's wire form,
   which is decided here (§10).
4. **Output discipline.** Uninitialised outputs, views instead of `ToArray`, no clears; bytes
   identical.
5. **Encode fusion.** Patches inline, codes packed from the codes buffer, no `Concat`, cascade
   with context; bytes identical.
6. **Plan memory by cost feedback.** Bytes identical on stable columns; the tolerance measured on
   `table_mixed`.
7. **SIMD kernels**, one per commit, each with its scalar twin and its before → after.
8. **The read contract**: block masks, selection, `MayMatch`, `Explain`; then `LIKE` in the
   expression model.
9. **Index builders** ([10-indexes.md](10-indexes.md)) on the pass, `Auto` measured then defaulted,
   `WriteReport`.
10. **Append**, with its repair tool and its tests.

---

## 9. Deliberately not in the target

| approach | why not |
|---|---|
| sample-based pricing (BtrBlocks; Z6) | it prices what the exact formulas already price for free, and it costs determinism. It could still shorten the FSST/zstd/ALP trials on text that drifts every chunk; plan memory by cost feedback takes most of that gain without a seed. Reopen only on the size ratchet's evidence, for those three encoders, as a two-stage variant |
| emitting a chunk at every large batch (W-32 as first written) | a chunk edge inside a block puts a zone edge inside a segment, where a pruned zone saves no I/O; the transit it would save is at most 12 ms per million rows. Kept only for batches that are multiples of 8 192 rows, which `PreferredBatchRows` tells callers to send |
| parallelism inside the writer | excluded by the constraint; the block pipeline is the shape that parallelises later (blocks and columns are independent, emission stays ordered) without undoing anything |
| zero-decode rewrite (pass encoded arrays through) | a reader-side change (`RecordBatch` is canonical by construction; Z1a/Z2 of PERF-AUDIT-v2); an order of magnitude on rewrites, nothing on first writes. Worth doing, separately |
| whole-column buffering for global decisions, or a globally sorted index | breaks the streaming property and bounds memory by the file; sorted **runs** per chunk give the same answers in flux ([10-indexes.md](10-indexes.md) §6.2) |
| sharing an FSST symbol table across chunks | 4,3 % of output size, measured |
| sampling the bit-width histogram | one exact pass costs less than a wrong width |

---

## 10. Open questions, and what the review changed

Open:

- The wire form of a constant chunk: `vortex.constant` (smaller, the reference's choice, moves
  bytes) or today's one-run run-end (byte-identical). Decided at stage 3 with the ratchet.
- `plan_tolerance` (5 %) and `for_margin` (10 %): measured on the corpus and on `table_mixed`.
- Whether bounded string min/max are on by default (Rust writes them; ~2 to 5 ns per value).
- The default `IndexBudgetBytes` and `Auto`'s per-column share of it.

Decided (2026-09-15): XxHash3-64 comes from the `System.IO.Hashing` package, the one first-party
dependency [03-architecture.md](03-architecture.md) §1 admits; see [10-indexes.md](10-indexes.md)
§11.

Changed by the review of the first iteration (2026-09-15): the distinct table now owns its keys
(it referenced rows of recycled arenas); frame of reference keeps exact patched pricing through a
conditional second sweep (the first iteration's bound could change bytes); the scratch is the
writer's, not the column's (a thousand columns would have cost 200 MiB); plan memory is checked
by the bytes it produced rather than by bucketed signatures and a fixed interval; Bloom filters
are built at block close from the hash buffer and coarser resolutions are generations, which
also settles the file-level filter; a sorted index is runs per chunk, never a global sort;
string runs compare bytes when views differ at equal length; nested columns have parent-row
blocks; W-32 is withdrawn except for block-multiple batches; the read contract (§6), the append
(§3.8) and the surface (§7) exist at all.

Changed by the second review (2026-09-15), on the question of the two passes: §2 says what the
two are (a second read from cache, never a copy) and why one pass is not possible without
guessing; the dictionary is the exception and assigns its codes in the first pass, through one
running table per chunk with block stamps and a codes buffer, so its encoder never probes (the
second iteration probed every row twice); the table runs only while a consumer is live, so a
high-cardinality column without an index is probed on its first chunk only.

---

## 11. Sources

Measurements and profiles: [WRITE-ARCHITECTURE.md](../WRITE-ARCHITECTURE.md) (2026-09-15).
Read path: `src/Vorticity/Scan/{BatchAsyncEnumerable.cs,SplitPlan.cs,FilteredBatches.cs}`,
`src/Vorticity/Compute/{ZonePruner.cs,FilterEvaluator.cs}`, `src/Vorticity/Expressions/`.
Reference writer at 0.86.1: `vortex-file/src/strategy.rs`, `vortex-layout/src/layouts/{repartition.rs,
compressed.rs,dict/writer.rs,zoned/writer.rs}`, `vortex-compressor/src/compressor/{cascade.rs,
select.rs,sample.rs}`, `vortex-compressor/src/stats/`, `vortex-btrblocks/src/schemes/`,
`vortex-fastlanes/src/bitpacking/array/bitpack_compress.rs` (histogram, best width, patches).
Upstream: [#7939](https://github.com/vortex-data/vortex/issues/7939) multi-resolution zone maps,
[#7707](https://github.com/vortex-data/vortex/issues/7707) stats as aggregate functions.
Kernels: FastLanes (Afroozeh & Boncz, VLDB 2023); ALP (Afroozeh, Kuiper, Boncz, SIGMOD 2024);
split-block Bloom filters (`parquet-format` `BloomFilter.md`); XXH3 (`Cyan4973/xxHash`
`doc/xxhash_spec.md`, and `System.IO.Hashing.XxHash3` in `dotnet/runtime`); Swiss tables
(`hashbrown`); log-structured runs (O'Neil et al., 1996).
Intrinsics: `System.Runtime.Intrinsics` reference assembly, .NET 11.0.100-rc.1.
