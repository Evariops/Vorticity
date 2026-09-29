# Per-encoding metadata: the transcribed wire contract

`ArrayNode.metadata` and `Layout.metadata` are opaque `[ubyte]` at the FlatBuffers layer
([02-format.md](../docs/design/02-format.md) §5.2, §6). Their contents are **Protobuf messages defined in
Rust rather than in `.proto` files** — `prost` derive structs declared next to each encoding — so
there is nothing to vendor verbatim and the tag numbers had to be transcribed. That is what this
file is: the transcription, with the upstream source path recorded for every message so it can be
re-verified.

Transcribed 2026-09-12 from `vortex-data/vortex@develop`. **The tag numbers are the contract.**
An encoding id is frozen: a reader-visible change gets a new id, never a silent extension
([02-format.md](../docs/design/02-format.md) §5.3). Unknown *field numbers* are skipped; values outside a
contract's domain are rejected.

Enum `PType` is the one from [`proto/dtype.proto`](proto/dtype.proto):
`U8=0, U16, U32, U64, I8, I16, I32, I64, F16, F32, F64=10`.

Enum `DecimalType` (`vortex-array/src/dtype/decimal/types.rs`):
`I8=0, I16=1, I32=2, I64=3, I128=4, I256=5`.

---

## Shared

### `PatchesMetadata` — `vortex-array/src/patches.rs`

Reused by BitPacked, ALP, ALPrd and Sparse. Implement once.
`PATCH_CHUNK_SIZE = 1024` — one patch index offset is stored per chunk, which is what makes patch
lookup constant time.

```proto
message PatchesMetadata {
  uint64 len                        = 1;
  uint64 offset                     = 2;
  PType  indices_ptype              = 3;
  optional uint64 chunk_offsets_len = 4;
  optional PType  chunk_offsets_ptype = 5;
  optional uint64 offset_within_chunk = 6;
}
```

---

## Array encodings

### `vortex.bool` — `vortex-array/src/arrays/bool/vtable/mod.rs`
```proto
message BoolMetadata { uint32 offset = 1; }   // bit offset, must be < 8
```

### `vortex.decimal` — `vortex-array/src/arrays/decimal/vtable/mod.rs`
```proto
message DecimalMetadata { DecimalType values_type = 1; }
```

### `vortex.dict` — `vortex-array/src/arrays/dict/array.rs`
```proto
message DictMetadata {
  uint32 values_len                 = 1;
  PType  codes_ptype                = 2;
  optional bool is_nullable_codes   = 3;   // added after stabilisation
  optional bool all_values_referenced = 4; // absent/false = unknown (conservative)
}
```
`values_len` is class I ([08-semantics.md](../docs/design/08-semantics.md) §5): validate it against the
actual values length and every code against `[0, values_len)`.

### `vortex.list` — `vortex-array/src/arrays/list/vtable/mod.rs`
```proto
message ListMetadata { uint64 elements_len = 1; PType offset_ptype = 2; }
```

### `vortex.listview` — `vortex-array/src/arrays/listview/vtable/mod.rs`
```proto
message ListViewMetadata { uint64 elements_len = 1; PType offset_ptype = 2; PType size_ptype = 3; }
```

### `vortex.varbin` — `vortex-array/src/arrays/varbin/vtable/mod.rs`
```proto
message VarBinMetadata { PType offsets_ptype = 1; }
```

### `vortex.constant` — `vortex-array/src/arrays/constant/vtable/mod.rs`
**Metadata is EMPTY. The scalar lives in buffer 0.** `serialize` returns `Ok(Some(vec![]))` with
the comment *"HACK: Because the scalar is stored in the buffers, we do not need to serialize the
metadata at all"*, and `deserialize` requires `buffers.len() == 1` and reads the
Protobuf `ScalarValue` from `buffers[0]`, decoded against the node's inherited DType.

An earlier revision of this file said the scalar was in the metadata. It is not — corrected
2026-09-12 against the 0.86.1 source and confirmed against the corpus, where all 494 files
containing a `vortex.constant` node report `nbuffers:1, metadata_len:0`.

### `vortex.sequence` — `encodings/sequence/src/array.rs`
```proto
message SequenceMetadata { ScalarValue base = 1; ScalarValue multiplier = 2; }
```

### `vortex.sparse` — `encodings/sparse/src/lib.rs`
```proto
message SparseMetadata { PatchesMetadata patches = 1; }   // required
```

### `vortex.runend` — `encodings/runend/src/array.rs`
```proto
message RunEndMetadata { PType ends_ptype = 1; uint64 num_runs = 2; uint64 offset = 3; }
```

### `fastlanes.bitpacked` — `encodings/fastlanes/src/bitpacking/vtable/mod.rs`
```proto
message BitPackedMetadata {
  uint32 bit_width = 1;
  uint32 offset    = 2;                     // must be < 1024
  optional PatchesMetadata patches = 3;
}
```

### `fastlanes.rle` — `encodings/fastlanes/src/rle/vtable/mod.rs`
```proto
message RLEMetadata {
  uint64 values_len             = 1;
  uint64 indices_len            = 2;
  PType  indices_ptype          = 3;
  uint64 values_idx_offsets_len = 4;
  PType  values_idx_offsets_ptype = 5;
  uint64 offset                 = 6;   // default 0; must be < 1024 (RLEData::try_new)
}
```

### `fastlanes.delta` — `encodings/fastlanes/src/delta/vtable/mod.rs`
Belongs to **no core edition** — a default writer cannot emit it. Recorded for completeness.
```proto
message DeltaMetadata { uint64 deltas_len = 1; uint32 offset = 2; }   // offset < 1024
```

### `vortex.alp` — `encodings/alp/src/alp/array.rs`
```proto
message ALPMetadata { uint32 exp_e = 1; uint32 exp_f = 2; optional PatchesMetadata patches = 3; }
```

### `vortex.alprd` — `encodings/alp/src/alp_rd/array.rs`
```proto
message ALPRDMetadata {
  uint32 right_bit_width  = 1;
  uint32 dict_len         = 2;
  repeated uint32 dict    = 3;
  PType left_parts_ptype  = 4;
  PatchesMetadata patches = 5;   // prost `message` without `optional`: still Option<> in Rust
}
```

### `vortex.fsst` — `encodings/fsst/src/array.rs`
```proto
message FSSTMetadata { PType uncompressed_lengths_ptype = 1; PType codes_offsets_ptype = 2; }
```

### `vortex.onpair` — `encodings/onpair/src/array.rs`
Note the **gap at tag 2** — a removed field. Do not renumber.
```proto
message OnPairMetadata {
  PType  uncompressed_lengths_ptype = 1;
  // tag 2 unused
  uint32 dict_size            = 3;   // dict_offsets has length dict_size + 1
  uint64 codes_len            = 4;
  PType  dict_offsets_ptype   = 5;
  PType  codes_ptype          = 6;
  PType  codes_offsets_ptype  = 7;
}
```
Buffer 0 is `dict_bytes`, the read-padded dictionary blob. On-disk shape is FSST-like.

### `vortex.datetimeparts` — `encodings/datetime-parts/src/array.rs`
Validity lives in the days array.
```proto
message DateTimePartsMetadata {
  PType days_ptype = 1; PType seconds_ptype = 2; PType subseconds_ptype = 3;
}
```

### `vortex.decimal_byte_parts` — `encodings/decimal-byte-parts/src/decimal_byte_parts/mod.rs`
```proto
message DecimalBytesPartsMetadata { PType zeroth_child_ptype = 1; uint32 lower_part_count = 2; }
```
Readers must require `lower_part_count == 0`; wide decimals belong to a future `_v2` id
([90-registry.md](../docs/design/90-registry.md)).

### `vortex.zstd` — `encodings/zstd/src/lib.rs`
```proto
message ZstdFrameMetadata { uint64 uncompressed_size = 1; uint64 n_values = 2; }
message ZstdMetadata {
  uint32 dictionary_size = 1;                  // 0 when no dictionary
  repeated ZstdFrameMetadata frames = 2;
}
```
`uncompressed_size` is file-supplied: validate against the decompression cap
([08-semantics.md](../docs/design/08-semantics.md) §6) **before** allocating.

### `vortex.zstd_buffers` — `encodings/zstd/src/lib.rs` (draft `zstd2026.02.0`)
```proto
message ZstdBuffersMetadata {
  string inner_encoding_id            = 1;
  bytes  inner_metadata               = 2;
  repeated uint64 uncompressed_sizes  = 3;
  repeated uint32 buffer_alignments   = 4;   // each a power of two
  repeated DType  child_dtypes        = 5;
  repeated uint64 child_lens          = 6;
}
```

### `vortex.pco` — `encodings/pco/src/lib.rs`
```proto
message PcoPageInfo  { uint32 n_values = 1; }              // pco caps at 2^24 values per chunk
message PcoChunkInfo { repeated PcoPageInfo pages = 1; }
message PcoMetadata  { bytes header = 1; repeated PcoChunkInfo chunks = 2; }
```

### `vortex.variant` / `vortex.parquet.variant`
```proto
// vortex-array/src/arrays/variant/vtable/mod.rs
message VariantMetadataProto { optional DType shredded_dtype = 1; }
// encodings/parquet-variant/src/vtable.rs
message ParquetVariantMetadataProto {
  bool has_value = 1; optional DType typed_value_dtype = 2; bool value_nullable = 3;
}
```

### `fastlanes.for` — `encodings/fastlanes/src/for/vtable/mod.rs`
**Not empty, and not a message either.** The metadata is a bare Protobuf `ScalarValue` carrying the
frame-of-reference value: `serialize` returns `ScalarValue::to_proto_bytes(array.reference_scalar()
.value())` with the note *"we **only** serialize the optional scalar value (not including the
dtype)"*, and `deserialize` does `ScalarValue::from_proto_bytes(metadata, dtype, session)`.
Zero buffers, exactly one child.

An earlier revision of this file listed `fastlanes.for` under "Empty metadata". It is not — an
empty payload decodes to a null reference value, which upstream rejects. Corrected 2026-09-12;
75 corpus files are affected.

### Empty metadata
`vortex.null`, `vortex.primitive`, `vortex.varbinview`, `vortex.struct`, `vortex.chunked`,
`vortex.masked`, `vortex.fixed_size_list`, `vortex.ext`, `vortex.bytebool`, `vortex.zigzag`.
Their metadata field is absent or a zero-length message; everything they need is carried by
children, buffers, and the inherited DType.

**Two encodings that look like they belong on this list and do not:** `vortex.constant` (empty
metadata, but a scalar in buffer 0) and `fastlanes.for` (a bare `ScalarValue` in the metadata).
Both are documented above. Reading either as "empty and nothing else" produces a decoder that is
wrong on 569 of the 819 corpus files.

### `Patched` — in-memory only, never serialized
`vortex-array/src/arrays/patched/vtable/mod.rs` defines a `PatchedMetadata`
(`n_patches = 1`, `n_lanes = 2`, `offset = 3`), but `Patched` appears in **no edition**
([90-registry.md](../docs/design/90-registry.md)). A patched BitPacked or ALP array is *read as* a
`Patched` node wrapping a patch-free array; on the wire the patches live in the encoding's own
metadata. Do not implement a decoder keyed on a `Patched` id.

---

## Layouts

### `vortex.flat` — `vortex-layout/src/layouts/flat/mod.rs`
```proto
message FlatLayoutMetadata { optional bytes array_encoding_tree = 1; }
```
When present, the Array FlatBuffer is inlined here and the segment holds only buffers — a
different offset-reconstruction path, and one the default writer does not exercise
([04-conformance.md](../docs/design/04-conformance.md) §3 forces it in the corpus).

### `vortex.dict` — `vortex-layout/src/layouts/dict/mod.rs`
```proto
message DictLayoutMetadata {
  PType codes_ptype = 1;
  optional bool is_nullable_codes = 2;
  optional bool all_values_referenced = 3;
}
```

### `vortex.list` — `vortex-layout/src/layouts/list/mod.rs`
```proto
message ListLayoutMetadata { PType offsets_ptype = 1; }
```
Not a member of any core edition as of `core2026.08.3` — see [editions/](editions/).

### `vortex.zoned` — `vortex-layout/src/layouts/zoned/mod.rs`, `.../zoned/schema.rs`
**Not a bare message.** The metadata is `[u8 version] ++ ZonedMetadataProto`, with
`ZONED_METADATA_PROTO_VERSION = 1`. A missing version byte, a version other than 1, or an empty
protobuf tail is malformed — upstream rejects all three, and so must we.

```proto
message ZonedMetadataProto {
  uint32 zone_len = 1;
  repeated AggregateSpecProto aggregate_specs = 2;
}
message AggregateSpecProto { string id = 1; bytes options = 2; }   // zoned/schema.rs
```
An unknown aggregate `id` disables that aggregate's pruning and must never fail the read
([08-semantics.md](../docs/design/08-semantics.md) §4).

### `vortex.chunked`, `vortex.struct`
Empty metadata. Chunk offsets derive from children `row_count`s, whose sum must equal the parent's.
One caveat recorded in `layout.fbs` itself: `ChunkedLayout` historically used the first metadata
byte as a flag indicating whether the first child is the statistics table for the other chunks.
Treat a non-empty chunked metadata as that flag, not as an error.

### `vortex.stats` — legacy ancestor of `vortex.zoned`
Read by the same machinery. `LegacyStatsMetadata` carries `zone_len` plus a `ZoneMapSchema`
(a stat bitset) rather than an aggregate-spec list — it predates the aggregate registry.
It is the only zone-map layout present in editions `core2025.05.0` through `core2025.10.0`,
so it is what every file older than `core2026.08.0` uses. Not optional.

---

## Corrections this transcription forces on our own docs

Two of our documents state something the reference contradicts. Both were plausible and both are
wrong; they are recorded here and fixed at their source.

1. **Decimal precision goes to 76, not 38, and is backed by `i256`, not `i128`.**
   `vortex-array/src/dtype/decimal/mod.rs` sets `MAX_PRECISION = <i256>::MAX_PRECISION = 76` and
   `MAX_SCALE = 76`, and `DecimalType` has an `I256 = 5` case selected for precision 39–76.
   `DecimalDType::try_new` validates: `1 <= precision <= 76`; `scale <= 76`; and
   `scale <= precision` **only when `scale > 0`** — negative scale is legal and is not bounded
   below beyond `i8`. [07-dotnet-mapping.md](../docs/design/07-dotnet-mapping.md) §2 claimed precision 38 /
   `i128` and that "Decimal256 does not exist in the DType union". It does, and .NET has no
   `Int256`, so one has to be written. (The row encoder's own 19–38 → `i128` table stays correct:
   `vortex-row` genuinely does not support `Decimal256` — [06-row-encoding.md](../docs/design/06-row-encoding.md) §6.)

2. **`vortex.constant` metadata is empty and its scalar is in buffer 0**, and **`fastlanes.for`
   carries a bare `ScalarValue` in its metadata** rather than nothing. Both are documented in
   their own entries above. The first revision of THIS file got both wrong in opposite
   directions, which is a fair warning about how far a plausible reading of the vtable can be
   from what `serialize`/`deserialize` actually do. Read those two functions, not the struct.

3. **A Protobuf `ScalarValue` cannot be interpreted without its DType.** The wire form carries no
   type tag of its own, so `from_proto_bytes` takes the DType as a parameter. This is what makes a
   Decimal scalar readable: it arrives as `bytes_value` holding a
   little-endian two's-complement integer whose **length** selects i8/i16/i32/i64/i128/i256, and
   an Extension DType resolves through its storage type.

4. **Our `alignment_exponent` cap of 6 is stricter than upstream's, which is 16.**
   [08-semantics.md](../docs/design/08-semantics.md) §6 justifies 64 bytes as covering every legitimate
   alignment. Parsing the footers of all 819 corpus files gives an observed maximum of 4
   (16 bytes, the varbinview views buffer), so the cap is safe against real files — but it is a
   deliberate divergence, not an equivalence, and a file upstream considers legal can be rejected
   by us. The fix if it ever bites is one constant.
