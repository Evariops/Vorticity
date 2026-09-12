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
| `fastlanes.bitpacked` | SIMD bit-packing | **1024-element transposed blocks**; optional patches; the single most important kernel |
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

| Encoding | 1.0 strategy |
|---|---|
| `vortex.dict` | take on codes, values untouched — the child stays encoded |
| `vortex.runend` | binary search in `ends` (validity of monotonicity per [08-semantics.md](08-semantics.md) §5 class II) |
| `fastlanes.bitpacked` | O(1) positional access via the inverse transposition. For scattered takes, decode per 1024-block once the hit density within a block exceeds a threshold; per-element below it |
| `fastlanes.for` | offset add over the child's strategy |
| `vortex.zigzag`, `vortex.constant`, `vortex.sequence` | pointwise, trivial |
| Patches (shared) | binary search in patch indices — implemented once, used by BitPacked/ALP/Sparse |
| `vortex.fsst`, `vortex.onpair`, `vortex.alp`, `vortex.alprd` | **fallback**: decode the containing zone, then index |
| everything else | **fallback** |

The fallback is correct, just not fast. Documenting which encodings take it is what keeps F5 an
engineering claim rather than a slogan; the rest move to 1.1 on benchmark evidence.

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
| Zstd | `vortex.zstd` | yes, but writer opt-in upstream |

## Writing: edition targeting

**The candidate scheme list is derived from the target edition, before sampling** — not filtered
after the fact. This is what upstream does (`vortex-file/src/writer.rs` calls
`retain_allowed_encodings` on the BtrBlocks builder with the edition's allowed array IDs), and it
is the only arrangement that works: otherwise the sampler happily elects `Sequence` on suitable
data and the write then fails at serialization because the default target `core2025.05.0` does not
contain `vortex.sequence`. Failing the write is the right last-resort assertion; it must never be
the nominal path.

With that ordering, the writer's per-kind allowlist (array / layout / dtype / aggregate) becomes an
invariant that only fires on a bug, and it **fails the write** when a serializer produces an ID
outside it. Default
target: `core2025.05.0`, which maximizes the set of readers that can consume our output. Emitting
a component absent from the target edition is a bug, not a warning — including for aggregates,
where silently dropping a zone map would quietly change the pruning behavior the caller asked for.
