# Component registry and implementation status

Scope reference: edition **`core2026.08.3`** (minimum library version 0.85.0), which cumulatively
contains every component of the earlier frozen core editions. Reading a file written by a modern
default writer requires this set; the read-forever floor is `core2025.05.0` (Vortex 0.36.0).

Phase column refers to [01-scope.md](01-scope.md) §3.

## Array encodings (34)

### Canonical and structural — Phase 1

| ID | Role | Notes |
|---|---|---|
| `vortex.null` | all-null array | trivial |
| `vortex.bool` | bit-packed booleans | metadata carries a bit offset < 8 |
| `vortex.primitive` | fixed-width buffer | canonical for `Primitive`; empty metadata |
| `vortex.varbinview` | 16-byte views | canonical for `Utf8`/`Binary`; views buffer aligned to 16 |
| `vortex.varbin` | offsets + bytes | Arrow-compatible form |
| `vortex.struct` | one child per field | canonical for `Struct` |
| `vortex.listview` | offsets + sizes | canonical for `List` |
| `vortex.list` | Arrow-compatible list | |
| `vortex.fixed_size_list` | fixed-size list | |
| `vortex.decimal` | canonical decimal | |
| `vortex.ext` | extension wrapper | carries `vortex.date` / `time` / `timestamp` / `uuid` |
| `vortex.chunked` | concatenation | pervasive — needed before anything works end to end |
| `vortex.constant` | single repeated value | pervasive after compression |
| `vortex.masked` | validity applied to a child | added in `core2025.10.0` |

### Compressed integer — Phase 1 (decode) / Phase 3 (encode)

| ID | Role | Notes |
|---|---|---|
| `fastlanes.for` | frame of reference | precedes bit-packing to avoid patches |
| `fastlanes.bitpacked` | SIMD bit-packing | **1024-element transposed blocks**; optional patches; the single most important kernel. Unpack is vectorized — `Vector512`/`256`/`128` with the scalar loop as fallback and oracle ([05-benchmarks.md](05-benchmarks.md) §1b) |
| `fastlanes.rle` | SIMD run-length | added in `core2025.10.0` |
| `vortex.zigzag` | zigzag | removes negatives before bit-packing |
| `vortex.runend` | run-end encoding | Arrow-compatible |
| `vortex.dict` | dictionary | codes + values; any dtype |
| `vortex.sparse` | fill value + patches | reuses `PatchesMetadata` |
| `vortex.sequence` | fixed-interval runs | added in `core2025.06.0` |
| `vortex.bytebool` | one byte per boolean | |

### Float, string, temporal — Phase 2

| ID | Role | Notes |
|---|---|---|
| `vortex.alp` | Adaptive Lossless Floating Point | encoded ints are i32/i64; validate `exp_e`/`exp_f` |
| `vortex.alprd` | ALP for real doubles | |
| `vortex.fsst` | Fast Static Symbol Table | 255 symbols, code 255 = escape. **Three-buffer shape only** — see below |
| `vortex.onpair` | string fragmentation | added in `core2026.08.1`; competes with FSST at write time |
| `vortex.datetimeparts` | decomposed timestamps | days / seconds / subseconds |
| `vortex.decimal_byte_parts` | decomposed decimals | readers must require `lower_part_count == 0` — wide decimals belong to a future `_v2` ID |
| `vortex.zstd` | Zstd-compressed array | `core2025.06.0`; decoded with the in-box `ZstandardDecoder` |

### The one shape we decline

`vortex.fsst` has two serialized shapes. The three-buffer one — `[symbols, symbol_lengths, codes]`
with `uncompressed_lengths` and `codes_offsets` as children — is what every writer since the
encoding stabilized emits, and it is what all 55 corpus files use. The two-buffer one is upstream's
`deserialize_legacy`, and it keeps the codes as a nested `vortex.varbin` **array**: offsets plus a
byte heap, read as a child.

That shape is incompatible with this library's arena, not merely unimplemented. Every child is
canonicalized on the way in, and a canonical `VarBinView` inlines values of 12 bytes or fewer into
the views themselves — so the contiguous code stream the decode needs no longer exists by the time
the child is available. Reconstructing it would mean reading another encoding's serialized form
directly out of the blob, past the arena.

It is therefore refused by name, with a message that says which shape was found, rather than
implemented against no test. Revisit if a file in the read-forever range (Vortex 0.36.0 onward)
turns out to use it; the corpus generator can produce one on demand.

### Deferred to 1.1

| ID | Reason |
|---|---|
| `vortex.pco` | Pcodec: a full integer/float codec; pure algorithm, no BCL blocker, but a sizeable body of work. Writer opt-in upstream (`pco` feature) |
| `vortex.zstd_buffers` | Draft `zstd2026.02.0` edition, no read-forever guarantee; trivial once `vortex.zstd` exists |
| `vortex.map` | `core2026.08.2`, very recent |
| `vortex.variant` | `core2026.08.3`, very recent |
| `vortex.parquet.variant` | `core2026.08.3`, very recent |

### Outside every edition

Three distinct cases, and conflating them is a mistake the corpus catches:

* **Genuinely in-memory only.** `Slice`, `Filter`, `Interleave`, `Shared`, `ScalarFn` and friends
  have no wire ID at all. Nothing to implement.
* **Has a wire ID, in no edition, gated by an upstream flag.** `vortex.patched` has a serializer
  and is registered when `use_experimental_patches()` is set; `fastlanes.delta` likewise has a real
  ID. Neither belongs to any edition, so an edition-enforcing writer cannot emit them — but a
  writer with enforcement disabled can, and our conformance corpus contains such files (1 patched,
  4 delta). Treat them as low priority, **not** as impossible.
* **Experimental layouts.** The `vortex.list` layout is gated by `use_experimental_list_layout()`
  upstream and is documented there as expected to change; files using it may become unreadable by
  future Vortex versions. Out of scope, and the corpus has one.

Note that a patched ALP or BitPacked array written *normally* is read as a `Patched` node wrapping
a patch-free array, with the patches carried in the encoding's own metadata — that path needs no
`vortex.patched` decoder.

## Layouts (6) — Phase 1

| ID | Role | Priority |
|---|---|---|
| `vortex.flat` | terminal, one serialized array | required |
| `vortex.chunked` | row-wise partition | required |
| `vortex.struct` | one child per field | required |
| `vortex.zoned` | zone map for pruning | required (the default writer always emits it) |
| `vortex.stats` | legacy zone map | required for older files |
| `vortex.dict` | dictionary shared across a child layout | required |

## What an unknown id gets told

An id this library does not decode raises `VortexUnsupportedException` naming the id and which kind
of component it was — an array, a layout or an extension dtype. Nothing more, and the registry is
why: there is no id in this document that has no reader, so an id that reaches a throw site is one
from a future edition, about which this library has nothing useful to say.

There used to be a table of notes for ids we knew of but did not decode. Every entry left it when
its component gained a reader: `fastlanes.delta` and `vortex.patched`, then the `vortex.list`
layout, then `vortex.map` and `vortex.zstd_buffers`, and last `vortex.variant` and
`vortex.parquet.variant`. None of those sentences was ever a reason not to read the component, which
is why the entries went rather than the wording being softened. A file carrying a *shredded* variant
child is still refused, by the decoder itself and with a message naming the child — which is a
better place for that sentence than a table of ids.

## Extension dtypes (4) — Phase 1

`vortex.date`, `vortex.time`, `vortex.timestamp` (all `core2025.05.0`), `vortex.uuid`
(`core2026.08.3`). Each maps to a storage dtype plus metadata; unit and timezone live in that
metadata.

## Zone-map aggregates (6) — Phase 2

`vortex.min`, `vortex.max`, `vortex.bounded_min`, `vortex.bounded_max`, `vortex.nan_count`,
`vortex.null_count`. An unrecognized aggregate disables the affected pruning; it must never fail
the read.

`sum` is deliberately absent from the aggregate allowlist: file-level sums use a fixed legacy
field in `ArrayStats`, not a serialized aggregate ID.

## Random-access (`take`) strategy per encoding

F5 is Vortex's headline claim over Parquet, and it is only real if `take` traverses encodings
rather than canonicalizing whole zones. 1.0 specializes the three that dominate real files and
documents the fallback honestly everywhere else.

| Encoding | 1.0 strategy | built |
|---|---|---|
| `vortex.dict` | take on codes, values untouched | **yes** |
| `vortex.runend` | binary search in `ends` (validity of monotonicity per [08-semantics.md](08-semantics.md) §5 class II) | **yes** |
| `fastlanes.bitpacked` | O(1) positional access via the inverse transposition | **yes** |
| `fastlanes.for` | offset add over the child's strategy | **yes** |
| `vortex.zigzag`, `vortex.constant`, `vortex.sequence` | pointwise, trivial | **yes** |
| `vortex.alp` | pointwise over the integers underneath | **yes** — see below |
| Patches (shared) | merge against the selection — implemented once, used by BitPacked and ALP | **yes** |
| `vortex.fsst` | seek each row's codes through `codes_offsets` | **yes** — 0.99× a full scan to 0.27× |
| `vortex.onpair` | the same, same children | **yes** — 0.83× a full scan to 0.34× |
| `vortex.alprd` | take on both children, recombined per selected row | **yes** — 2.94× the reference to 0.42× |
| everything else | **fallback** | |

The mechanism is a selection pushed DOWN rather than a gather pulled up. `ArrayDecoder` grows one
virtual method, `DecodeSelected`, whose default is exactly what the scan used to do afterwards —
decode the node whole, then gather — so an encoding with nothing to say about takes says nothing and
behaves as before. The selection reaches it through the layout tree in the same coordinate space as
the row range beside it, which is what makes the plumbing small: `vortex.chunked` is the only layout
that re-partitions rows, so it is the only one that re-bases the selection, and struct, zoned and
stats pass it on by doing nothing at all. `vortex.dict` the LAYOUT keeps the selection for its codes
child and drops it for its shared values, which is the same strategy one level up.

**Three rows of this table were wrong.** `vortex.alp` was grouped with FSST and OnPair under "decode
the zone, then index"; it does not belong there, because it is one output per input with the
exceptions carried as patches — the same shape as `fastlanes.for` — and specializing it was worth
more than the bit-packing was.

**And the reason given for keeping FSST and OnPair in the fallback was itself wrong.** It read: they
are variable-length, so row n cannot be found without walking rows 0..n-1. That argument is sound in
general and **does not apply to these two**, because both carry a per-row index into the compressed
stream:

* `vortex.fsst` children are `[uncompressed_lengths, codes_offsets, validity?]`, and
  `codes_offsets[i]..[i+1]` bounds row i's codes exactly;
* `vortex.onpair` children are `[dict_offsets, codes, codes_offsets, uncompressed_lengths,
  validity?]` — the same shape.

Both decoders' own headers say so in as many words ("`codes_offsets` bounds each row's codes"); what
they also say is that the reference does not *use* it that way, decompressing the whole stream in
one pass and cutting the result with `uncompressed_lengths`. For a full scan that is the right
choice. For a take it is not, and the two were classified from the reference's decode strategy
rather than from what the format makes possible. They are a **defect to fix**, worth 830 µs of the
1070 µs a scattered take costs — the largest single performance item left in the library.

The
density threshold the bit-packed row prescribed ("decode per 1024-block once the hit density exceeds
a threshold") is deliberately **not** implemented: per-element is better than the old behaviour at
every density below "all of it", and at "all of it" the scan skips the pushdown entirely, so the
crossover is a measurement nobody has needed to make.

### What it bought, measured

`TakeBenchmarks`, `containers/zoned_many_zones_nulls` — 65 536 rows in 64 splits of 1024, taking 64
rows one from each split:

| | before | after |
|---|---|---|
| take 64 scattered rows, all 5 columns | 1309 µs (**1.02×** a full scan) | 1070 µs (0.82×) |
| the same, string column projected away | — | **242 µs (0.19×)** |

The second row is the attribution, and it is why the first one looks unimpressive: the remaining
830 µs is the `utf8` column. Over the four columns the specializations cover, a scattered take went
from ~1.0× a full scan to 0.19× — and the fallback is now the whole of the residual rather than
being hidden inside a number that averaged it with everything else, which is what made it worth
looking at again.

**Both are specialized now.** With `vortex.onpair` decoding selectively too, that scattered take is
**452 µs, 0.34× a full scan**, and the `strs` residual is 206 µs rather than 825 — 2.4× on the axis
and 4× on the column. The full scan did not move.

**That residual was `vortex.onpair` alone, and this section used to say "onpair and fsst".** The
correction came from a measurement that did not move: a `DecodeSelected` for `vortex.fsst` changed
that take by no time and *zero bytes of allocation*, because FSST is not in the file. The pair had
one member here all along, which also meant no axis in the suite could measure an FSST take — one
exists now, over `encodings/fsst`, where the same change is worth 3.7×:

| `encodings/fsst`, 4096 rows, one utf8 column | take 8 rows | full scan | ratio |
|---|---|---|---|
| before `FsstDecoder.DecodeSelected` | 143.2 µs | 144.1 µs | **0.99×** |
| after | **38.8 µs** | 142.3 µs | **0.27×** |

The full-scan column is the guard: seeking row n through `codes_offsets` must not slow the dense
path, which decompresses the whole stream in one pass deliberately. It did not move.

The fallback is correct, just not fast. Documenting which encodings take it — and measuring how much
of the bill it is — is what keeps F5 an engineering claim rather than a slogan.

**And most of that bill was not the fallback's.** "Decode the node whole, then gather" is one decode
per CALL, and `FlatLayoutReader` served a selection one batch at a time without the retained-chunk
cache its batch path uses — so a take of n rows spread over a chunk decoded that chunk n times.
Counted on a 1M-row `vortex.zstd` column: 64 wanted rows, 64 calls, a million rows and ~881 zstd
frames each, 427 ms against the 7 ms a full scan of that same node costs. Routing the fallback
through the cache (PERF-AUDIT-v2 R23) took that column from **65.20× the reference to 1.09**,
`zstd_nullable` from 40.51 to 0.67 and `zstd_buffers` from 6.82 to 0.15, with no change to any
encoding in the table above: `ArrayDecoder.SelectsWithoutFullDecode` says which rows of it specialize,
and only the ones that do not are rerouted. So the rows still marked **fallback** now cost one decode
per scan rather than one per wanted row, and the table's "worth specializing" column should be read
against that much smaller bill.

Correctness is a corpus-wide differential: `TakeSpecializationTests` takes a scattered, awkward set
of indices from every in-scope file (both ends, both sides of the 1024-element block boundary, a
prime stride between) and asserts every value equals what a full scan put at that index. 565 files,
5173 values. A specialization is an optimization with a correctness obligation, and the only honest
oracle for it is the path it replaced.

**Where this actually stands, measured.** `ScanBuilder.Take` is implemented and delivers the half
of F5 that dominates on object storage: the splits an index list never touches are skipped before a
single segment is registered, so scattered rows read the splits they live in and nothing else,
which `TakeTests.ScatteredRowsReadFarFewerSegmentsThanTheWholeFile` measures rather than assumes.
What is **not** implemented is the column of the table above: inside a split that is read, the whole
split is decoded and the wanted rows are gathered out of it, for every encoding.

`TakeBenchmarks` says what that costs, on `containers/zoned_many_zones_nulls` — 65 536 rows in 64
splits of 1024:

| | time | against a full scan |
|---|---|---|
| full scan, all 65 536 rows | 1284 µs | 1.00 |
| take 64 rows, one from each of 64 splits | 1309 µs | **1.02×** |
| take 64 rows, all from one split | 70.5 µs | 0.05× |

So the two halves are visible separately, and the second one is as bad as it could be: **taking
0.1% of the rows costs 102% of reading all of them** when they are scattered, while taking the same
number from one split costs 5%. The I/O half works; the decode half does not exist. That is the
number the table above is worth building against.

## Compression scheme → emitted wire ID

The writer's schemes are named after algorithms; editions constrain IDs. Prioritizing write
kernels requires the mapping, not the marketing names:

| Scheme (BtrBlocks) | Emitted ID | In default core edition? |
|---|---|---|
| FoR | `fastlanes.for` | yes |
| BitPacking | `fastlanes.bitpacked` | yes |
| ZigZag | `vortex.zigzag` | yes |
| IntDict / FloatDict / StringDict / BinaryDict | `vortex.dict` | yes |
| RunEnd | `vortex.runend` | yes |
| IntRLE / FloatRLE | `fastlanes.rle` | yes (`core2025.10.0`) |
| Sequence | `vortex.sequence` | yes (`core2025.06.0`) |
| Sparse / NullDominatedSparse | `vortex.sparse` | yes |
| ALP / ALPrd | `vortex.alp` / `vortex.alprd` | yes |
| FSST | `vortex.fsst` | yes |
| OnPair | `vortex.onpair` | yes (`core2026.08.1`) |
| VarBin | `vortex.varbin` | yes |
| Decimal | `vortex.decimal` / `vortex.decimal_byte_parts` | yes |
| Temporal | `vortex.datetimeparts` | yes |
| Delta | `fastlanes.delta` | **no** — in no core edition, never emittable by a default writer |
| Pco | `vortex.pco` | yes, but writer opt-in upstream |
| Zstd | `vortex.zstd` | yes — **written** since the corpus showed a 33.8× file it alone could fix |

## Writing: what the compressor does today

F10 describes a BtrBlocks-style sampling compressor. What exists is the rule-based chooser
underneath it, measuring two properties in one pass each:

| Property | Scheme | Notes |
|---|---|---|
| Few runs | `vortex.runend` | the constant case falls out as the single-run degenerate, so `vortex.constant` is not separately emitted |
| Narrow range | `fastlanes.for` or `vortex.zigzag`, over `fastlanes.bitpacked` | the width minimizes packed bytes plus the cost of the values that do not fit, which ride along as patches |
| Few distinct values | `vortex.dict` | codes are non-nullable; a null row is a code pointing at a null dictionary entry |
| Text a symbol table cannot capture | `vortex.zstd` | compared against `min(plain, fsst)`, and only for varbin columns of at least 64 kB |
| otherwise | canonical | |

**Zstd is compared, not used as a fallback**, and the distinction was measured rather than
reasoned. Pricing it only after every cheaper scheme declined left `distributions/huge_string_r16`
untouched at 33.8× the reference, because FSST *wins* there — 1.1 MB down to 139 kB — so zstd was
never reached, while zstd takes the same bytes to 118. A scheme that wins is not a scheme that wins
by enough. What keeps that affordable is the 64 kB gate, not the ordering: below it the most zstd
can save cannot repay a whole compression pass, which is the mistake the profiling session caught
FSST symbol training making on every chunk.

Only bit-packing needed a new kernel — `FastLanes.PackBlock`, the inverse of the unpacker — and it is
verified the only way a packer honestly can be: the Rust cross-check reads every bit-packed file we
write, which makes our transposition byte-compatible with the reference's unpacker rather than
merely self-consistent. The values child of run-end and dict is the original column gathered to its
representative rows, so a dictionary of strings shares the data buffers it came from and copies only
16-byte views.

**Where the size actually stands**, measured over the whole corpus against the ≤105% target of
[05-benchmarks.md](05-benchmarks.md) §3:

| | ratio to the reference |
|---|---|
| Whole corpus | **0.822×** — smaller than the reference, against a ≤105% target. Was 0.862× before `vortex.zstd`, 1.95× before FSST, 1.54× before nested columns, 1.15× before the schemes were priced in bytes, 1.11× before ALP, 1.044× before patched bit-packing, 1.008× before the codes cascade, 0.988× before `vortex.sequence`, 0.894× before the offsets went through the compressor |
| `distributions/high_cardinality_i64_r8193` (dense integers) | **0.97×** |
| `distributions/short_runs_i32_r8193` | **0.87×** |
| `types/i64_nonnull_r8192` | **0.99×** (was 3.31×) |
| `containers/zoned_many_zones_nulls` (five columns, one of them high-cardinality text) | 1.16× (was 3.61×) |

Measured by `WrittenSizeTests` on every run rather than by hand, with the worst offenders ranked
by BYTES LOST — a 40× ratio on a 300-byte file moves nothing.

**FSST is written**, which is where that 0.4× came from. It is tried last, only on VarBinView, and
only when runs, frame-of-reference and dictionaries have all declined — the case the reference
hands to FSST and we used to write canonically. Two things about it are worth carrying forward:

* The symbol table must be **ordered by length, 2…8 then 1**. The reference validates it
  (`validate_symbol_lengths`) and refuses the array; our own reader does not, so no round trip
  through our decoder could see the violation. The Rust cross-check rejected 35 of 774 files until
  the table was reordered.
* Deciding that FSST pays *means compressing the column* — there is no cheap estimate — so the plan
  is built during `Choose` and the bytes are kept rather than produced twice.

**Compression now reaches nested columns.** It used to be applied to the top of a column and
nowhere else, on the stated grounds that "applying a scheme inside a struct or a list would change
shapes the reader derives top-down". That was wrong twice over: a `vortex.dict` node in an
elements position produces the same dtype as the primitive it replaces — the corpus's own
`list(utf8)` files are written exactly that way by the reference — and the FlatBuffers constraint
it worried about does not arise, because children are written before the parent table opens. A
list's elements, a fixed-size list's elements, a struct's fields, an extension's storage and a
dictionary's or run-end's values child are all compressed now.

That paragraph used to end "validity bitmaps and a list's offsets and sizes are not: they are index
machinery, and they are small". The first half was true and the second was not. A list of 8193 rows
carries 8193 offsets and 8193 sizes — 64 kB between them, often more than the elements — and they
are the most compressible data in the file: offsets are monotone by construction, and a list of
fixed-width rows has offsets that are an exact arithmetic progression and sizes that are constant,
which is to say both are `vortex.sequence` and cost nothing at all. The same held for a
`vortex.varbin`'s offsets, which the reference bit-packs. They all go through the compressor now,
worth 0.894× → 0.862×. **Validity bitmaps still do not**: those really are one bit per row, and
they are the one child no scheme here applies to.

**Every scheme is now priced in bytes rather than by a ratio.** Three rules had been written as
fractions and never measured against the fixed cost they stood for:

* Frame of reference asked for "half the element width or better", which refused a 40-bit column of
  `i64` timestamps — a 37% saving over 8192 rows. It now compares the packed size (block padding
  included, since FastLanes rounds to 1024 and a 100-row column still pays for a whole block) plus
  a node's overhead against the canonical size. Dates and timestamps left the offender list.
* A dictionary asked for at most one distinct value per four rows. `types/binary_nonnull_r8193` has
  1153 distinct values in 8193 rows — about one in 3.5 per chunk — so the ratio refused it by a
  hair while the byte arithmetic says it wins by 100 kB, which is exactly what the reference does
  with it. The abandonment guard survives in a form that still bounds the work: every row needs at
  least a one-byte code and every entry at least its minimum width, so a dictionary that has
  already exceeded the column cannot recover.
* A binary column is written `vortex.varbin` when that is smaller than `vortex.varbinview`, which
  is almost always: four-byte offsets beat sixteen-byte views by twelve bytes a row, and inlining
  can save at most the twelve bytes the value would have taken in the heap. It costs a copy at
  write time — the view form can hand its existing buffers straight over — for a saving every
  reader keeps. **This also fixed FSST's baseline**: comparing against the view form made FSST look
  like a win on incompressible binary, where plain varbin is a quarter of the size.

**ALP is written**, for `f32` and `f64` both, with the exceptions carried as patches. Its encoded
child goes through the compressor like any other column, which is where the saving lands: ALP turns
decimals into small integers and frame-of-reference plus bit-packing turns small integers into few
bits. One rule in it is worth carrying forward — a value is encoded only when decoding the integer
reproduces its **exact bit pattern**, compared with `BitConverter.DoubleToInt64Bits` and never with
`==`, because `-0.0 == 0.0` and NaN payloads are invisible to `==`. An encoder that used `==` would
silently change values, and no equality-based test could see it.

**What is left, in order of bytes.** The whole-corpus figure is now below the reference's, so what
follows is a list of individual files we are worse on, not a deficit:

* `distributions/huge_string_r16` at 33.8× — one string over a mebibyte, which the reference
  crushes with `vortex.onpair`. That encoding is **absent from the default write target**
  `core2025.05.0` and first appears in `core2026.08.1`, so we cannot emit it by default at all.
  Same for `repeated_prefix_utf8_r8193` at 1.63×.
* `types/binary_*` at 1.59× and `types/list_*` at about 1.55× — and the diagnosis is the same for
  both, and is not about encodings. The reference shares ONE dictionary across the whole column
  using the `vortex.dict` **layout**; we dictionary-encode per chunk, so the 21 kB of distinct
  values in `types/binary_nonnull_r8193` is written twice. Our chunks are 4097 and 4096 rows
  because the writer emits one chunk per `WriteAsync` and inherits whatever batching the caller
  uses. [01-scope.md](01-scope.md) §3 lists "coalesce toward ~1 MiB" as a Phase 3 layout strategy
  and it is not built; that, or writing the `vortex.dict` layout, is what closes these.

So an edition caveat belongs on the whole measurement: the corpus was written at
`core2026.08.3` and we write at `core2025.05.0`, and §3's target says "the same data, edition and
configuration". Most of the remaining 0.8% is an edition difference rather than an implementation
gap.

**`vortex.sequence` is written**, and the story of why it was not is worth more than the encoding.
docs/90 listed it under "not in the default target" for as long as that target was believed to be
`core2025.05.0`. It arrived in `core2025.06.0`, and once edition targeting was actually implemented
the default became `core2026.08.3` — so what had been filed as an edition difference was an
implementation gap the whole time. Three of the files we were worst on were sequences and nothing
else: `types/fsl_i32_3_*` at 23×, `types/date_ms_nonnull_r8193` at 18×, `types/struct_field_names`
at 5.2×, and the `monotone i64` column of `containers/zoned_many_zones*`.

It is tried FIRST, before runs and distinct values, because where it applies nothing can beat it:
`A[i] = base + i * multiplier` lives entirely in the metadata, so a `vortex.primitive` node and its
whole buffer become one node and about thirty bytes. It is the one scheme with no overhead to weigh,
because it replaces a node rather than wrapping one. Nulls disqualify a column outright — the
encoding has neither children nor buffers, so there is nowhere for a validity bitmap to live.

Worth 0.988× → 0.894×.

**Cascading is done for the codes**, which is where the rest lived. A dictionary's codes and a
run-end's ends are the one part of those encodings that costs per ROW rather than per distinct
value, and they are the most bit-packable data in a file by construction: non-negative, dense, and
bounded by the entry count. A column with 1153 distinct values carries u16 codes and needs eleven
bits. They now go through the compressor like any other column instead of being written raw, which
is legal because the reader derives the codes child's dtype from `codes_ptype` and then decodes it
like any other child. The recursion terminates on the arithmetic rather than on a depth counter:
every scheme has to beat the level above it by its own overhead, so the sizes strictly decrease.

Worth 1.008× → 0.988× on its own.

**Bit-packing is patched, and chooses its own transform.** `types/i64_nonnull_r8192` is `0`,
`i64::MIN`, `i64::MAX` and then eight thousand values alternating either side of zero: three rows
in 8192 made the frame-of-reference span the whole 64 bits, and a scheme built for dense integers
declined to touch the densest column in the corpus. Two separate things were missing, and neither
works without the other:

* **Patches.** The width stops being a property of the data and becomes a MINIMUM over a cost
  function — packed bytes plus what the exceptions cost — which is what `best_bit_width` computes
  upstream. `fastlanes.bitpacked` has always been able to express them and our decoder has always
  read them.
* **The transform.** Patches over a frame of reference anchored at `i64::MIN` make *half* that
  column an exception. What the reference does is `vortex.zigzag` instead — magnitude rather than
  position decides the width — and then 17 bits with two patches. That is read off the corpus
  sidecar's own array tree, not guessed.

Both are priced and the cheaper wins; frame still wins outright on the columns it was already
winning, because a column of timestamps has a tiny span and an enormous magnitude.

Two things this cost, which are the interesting half:

* **A null row's packed value is zero, and that is not the same statement as "its value is zero".**
  Put a raw zero through a frame of reference and it encodes as `-reference`, 64 bits wide, so
  every null in the column prices as an exception and the scheme is refused outright. It cost
  37 kB on `containers/zoned_many_zones_nulls`, and no value test in the suite could see it: the
  file round-tripped perfectly at every stage. It was just bigger. `BitPackPlanTests` pins it now.
* **Scheme selection was an ORDERING and had to become a comparison.** Bit-packing was tried before
  dictionaries and returned the moment it beat canonical — harmless while it declined often, and a
  regression the moment patches let it apply to columns a dictionary was handling better. Both are
  priced in bytes, so the dictionary's abandonment budget is now what the bit-packing costs rather
  than what the plain column costs: it decides and gives up against the real competition.

## Writing: edition targeting

`VortexWriteOptions.TargetEdition` names the frozen edition every component in the file must belong
to, and `spec/editions/core*.toml` is transcribed into `EditionRegistry` as the table that decides
it. Editions are cumulative within a family, so the table stores *which edition introduced each id*
and membership is one integer comparison.

**The candidate scheme list is derived from the target, before anything is measured** — not
filtered after the fact. This is what upstream does (`vortex-file/src/writer.rs` calls
`retain_allowed_encodings` on the BtrBlocks builder with the edition's allowed array IDs), and it
is the only arrangement that works: otherwise the compressor elects a scheme on suitable data and
the write then fails at serialization because the target does not contain its ID. Failing the write
is the right last-resort assertion; it must never be the nominal path.

The per-kind allowlist (array / layout / dtype / aggregate) then only fires on a bug, and it
**fails the write** rather than producing a file the target's readers cannot open. Array and layout
IDs are checked in `EncodingDictionary.Intern`, which is the one place every ID in the file passes
through exactly once — a new serializer cannot forget to ask. Extension dtypes are checked against
the schema at `Create`, before the first batch. Aggregates are checked in `ZoneMapWriter`, where
silently dropping a zone map would quietly change the pruning the caller asked for.

### The default, and why it is not the read-forever floor

This section used to say "Default target: `core2025.05.0`, which maximizes the set of readers that
can consume our output". **That was false, and nothing checked it.** The writer emits `vortex.zoned`
on every file whose chunking allows a zone map, and `vortex.zoned` together with all six of its
aggregates first appears in `core2026.08.0`; `vortex.uuid` first appears in `core2026.08.3`. A
Vortex 0.36.0 reader — the version the floor exists for — would have met an unknown layout ID.

So the default is **`core2026.08.3`**, the newest frozen edition, which is also what the reference
writer defaults to. Lower targets are honoured rather than approximated:

| Target | What changes |
|---|---|
| `core2026.08.0`…`.3` | nothing; this is what the writer emits naturally |
| below `core2026.08.0` | the zone map is **omitted**. `vortex.stats` is not written in its place: no release of Vortex has ever emitted one — 0.86.1 cannot — so it would be an untestable format path, and pruning is an optimization whose absence costs correctness nothing |
| below `core2025.10.0` | a List or FixedSizeList column **fails the write**: their canonical forms here are `vortex.listview` and `vortex.fixed_size_list`, both introduced by that edition. `vortex.list` is the Arrow-compatible form that edition does carry, and writing it is the work that would lift this |
| below `core2026.08.3` | a `vortex.uuid` column fails the write |

Every failure names the ID *and* the edition that introduced it, which is what turns "this does not
work" into "raise your target to this". `EditionTargetTests` asserts the property on the written
FILE — reading its encoding dictionaries back — rather than at the call sites, because a writer that
checks itself and then emits something else would pass any test written the other way round.
