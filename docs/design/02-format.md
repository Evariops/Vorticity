# The format, condensed

The Vortex file format as this library reads and writes it, condensed from the primary sources of
[99-sources.md](99-sources.md); the schemas themselves are vendored in [spec/](../../spec/).
**Everything is little-endian.**

## 1. File structure

```
┌────────────────────────────┐
│     Magic bytes 'VTXF'     │  4 bytes
├────────────────────────────┤
│          Segments          │  serialized arrays + statistics, writer-chosen order,
│                            │  inter-segment padding to satisfy alignment
├────────────────────────────┤
│     Metadata segments      │  optional, up to 16, opaque, UTF-8 keys
├────────────────────────────┤
│      DType flatbuffer      │  optional (may be supplied out of band)
├────────────────────────────┤
│      Layout flatbuffer     │  required — root layout tree
├────────────────────────────┤
│    Statistics flatbuffer   │  optional — per-field, file-level statistics
├────────────────────────────┤
│      Footer flatbuffer     │  required — dictionaries (segments, encodings, ...)
├────────────────────────────┤
│         Postscript         │  ≤ 65527 bytes
├────────────────────────────┤
│     End of File (8 bytes)  │  u16 version, u16 postscript length, 'VTXF'
└────────────────────────────┘
```

Frozen constants (these never change):

| Constant | Value |
|---|---|
| `MAGIC_BYTES` | `VTXF` (0x56 0x54 0x58 0x46) |
| `EOF_SIZE` | 8 |
| `MAX_POSTSCRIPT_SIZE` | 65527 (`u16::MAX - 8`) |
| `VERSION` | 1 |
| `V1_FOOTER_FBS_SIZE` | 32 |
| Extension | `.vortex` |

**Open sequence (2 round trips worst case, 1 best case):**

1. Read the 64 KiB tail (or the whole file if smaller). By construction this always covers the
   postscript (`MAX_POSTSCRIPT_SIZE` + `EOF_SIZE` ≤ 64 KiB). A local file, whose reads are copies
   and cost no round trip, reads its last 8 KiB first; one whose postscript reaches past them reads
   the 64 KiB next.
2. Validate magic and version from the last 8 bytes, read the postscript length.
3. Parse the postscript → locate dtype / layout / statistics / footer / metadata.
4. If any of those segments falls outside the window already read, issue one targeted second read.

This is exactly why the postscript exists: without it, locating a large DType would cost three
round trips.

## 2. Postscript (`footer.fbs`)

```fbs
table Postscript {
    dtype:      PostscriptSegment;              // optional
    layout:     PostscriptSegment;              // required
    statistics: PostscriptSegment;              // optional
    footer:     PostscriptSegment;              // required
    metadata:   [PostscriptMetadata];           // ≤ 16, keys unique, non-empty, ≤ 64 UTF-8 bytes
}
table PostscriptMetadata { key: string (required); segment: PostscriptSegment (required); }
table PostscriptSegment {
    offset: uint64; length: uint32; alignment_exponent: uint8;
    _compression: CompressionSpec; _encryption: EncryptionSpec;   // inline, not an indirection
}
```

Compression and encryption are **inline** in the postscript (rather than indices into the footer)
so a reader does not need the footer before it can decrypt.

User metadata values are **not** loaded by default: each locator is resolved on demand.

## 3. Footer (`footer.fbs`)

```fbs
table Footer {
    array_specs:       [ArraySpec];        // dictionary of array encoding IDs,  u16 index
    layout_specs:      [LayoutSpec];       // dictionary of layout IDs,          u16 index
    segment_specs:     [SegmentSpec];      // segment map,                       u32 index
    compression_specs: [CompressionSpec];  // ≤ 8
    encryption_specs:  [EncryptionSpec];
}
table ArraySpec  { id: string (required); }   // e.g. "fastlanes.bitpacked"
table LayoutSpec { id: string (required); }   // e.g. "vortex.chunked"

struct SegmentSpec {          // FlatBuffers struct = 16 bytes inline, no vtable
    offset: uint64;           //  0..8   absolute offset from start of file
    length: uint32;           //  8..12
    alignment_exponent: uint8;// 12..13  alignment = 1 << exponent
    _compression: uint8;      // 13..14  reserved (index into compression_specs)
    _encryption: uint16;      // 14..16  reserved
}
enum CompressionScheme : uint8 { None = 0, LZ4 = 1, ZLib = 2, ZStd = 3 }
table FileStatistics { field_stats: [ArrayStats]; }  // one entry per root field
```

`segment_specs` is a vector of fixed-size structs, so it is read as a reinterpreted
`ReadOnlySpan<SegmentSpec>` with no traversal and no allocation, which matters on wide files; so is
`Buffer` (8 bytes) in `array.fbs`.

## 4. DType (`dtype.fbs` + `dtype.proto`)

The root DType is a FlatBuffer; the Protobuf variant is used in scalars and statistics. Both
define the same union, with identical and **stable** tags:

| Tag | Type | Fields |
|---|---|---|
| 1 | `Null` | — |
| 2 | `Bool` | nullable |
| 3 | `Primitive` | ptype, nullable |
| 4 | `Decimal` | precision (u8), scale (i8), nullable |
| 5 | `Utf8` | nullable |
| 6 | `Binary` | nullable |
| 7 | `Struct_` | names[], dtypes[], nullable |
| 8 | `List` | element_type, nullable |
| 9 | `Extension` | id, storage_dtype, metadata[] |
| 10 | `FixedSizeList` | element_type, size (u32), nullable — *placed after Extension for backward compatibility* |
| 11 | `Variant` | nullable |
| 12 | `Union` | names[], dtypes[], type_ids[], nullable |
| 13 | `Map` | key_type, value_type, keys_sorted, nullable |

`PType`: `U8=0, U16, U32, U64, I8, I16, I32, I64, F16, F32, F64=10`.

Two parsers therefore share one model, and a property test holds them equivalent: a generated
DType serialized both ways parses back to the same value (`DTypeEquivalenceTests`).

A file's root DType is **not required to be a Struct**: a file may hold a bare `Float64` or `Bool`,
and a reader must not assume a tabular schema ([07-dotnet-mapping.md](07-dotnet-mapping.md) §5).

Core-edition extension dtypes: `vortex.date`, `vortex.time`, `vortex.timestamp`, `vortex.uuid`.

## 5. Serialized arrays

### 5.1 Blob format

```
[padding] [buffer 0] [padding] [buffer 1] ... [padding] [Array flatbuffer] [u32 flatbuffer length]
```

Reading: read the last 4 bytes → `fb_length`; the FlatBuffer occupies
`[len-4-fb_length, len-4)`; everything before it is the buffer region. Each buffer's offset is
reconstructed by accumulating `padding + length` from 0.

The FlatBuffer is 8-byte aligned. Each buffer carries its own required alignment (up to 16 for
`varbinview` views), and the written padding guarantees that a memory-mapped segment is directly
usable — **this is what makes zero-copy possible**.

### 5.2 Schema (`array.fbs`)

```fbs
table Array { root: ArrayNode; buffers: [Buffer]; }

struct Buffer {               // 8 bytes inline
    padding: uint16;          // padding written immediately BEFORE this buffer
    alignment_exponent: uint8;
    compression: uint8;       // enum Compression { None = 0, LZ4 = 1 }
    length: uint32;
}

table ArrayNode {
    encoding: uint16;         // index into Footer.array_specs
    metadata: [ubyte];        // opaque: encoding-specific Protobuf message
    children: [ArrayNode];
    buffers: [uint16];        // indices into Array.buffers
    stats: ArrayStats;
}

table ArrayStats {
    min: [ubyte]; min_precision: Precision;   // Protobuf-serialized ScalarValue
    max: [ubyte]; max_precision: Precision;
    sum: [ubyte];
    is_sorted: bool = null; is_strict_sorted: bool = null; is_constant: bool = null;
    null_count: uint64 = null; uncompressed_size_in_bytes: uint64 = null; nan_count: uint64 = null;
}
enum Precision : uint8 { Inexact = 0, Exact = 1 }
```

**Key point:** an `ArrayNode` does **not** carry its DType. The type is inherited or derived from
the parent (the root DType comes from the file). Decoding is therefore a type-guided top-down
traversal, and every decoder must know how to compute its children's DTypes.

### 5.3 Per-encoding metadata

Each encoding serializes its metadata as **Protobuf** (`prost`) with stable tags. There is no
single `.proto` file upstream: messages are declared beside each encoding. A few examples:

```proto
// vortex.dict
message DictMetadata { uint32 values_len = 1; PType codes_ptype = 2;
                       optional bool is_nullable_codes = 3; optional bool all_values_referenced = 4; }
// vortex.bool                 (bit offset, < 8)
message BoolMetadata { uint32 offset = 1; }
// vortex.varbin
message VarBinMetadata { PType offsets_ptype = 1; }
// vortex.runend
message RunEndMetadata { PType ends_ptype = 1; uint64 num_runs = 2; uint64 offset = 3; }
// vortex.sparse
message SparseMetadata { required PatchesMetadata patches = 1; }
// fastlanes.bitpacked
message BitPackedMetadata { uint32 bit_width = 1; uint32 offset = 2;     // offset < 1024
                            optional PatchesMetadata patches = 3; }
// vortex.alp
message ALPMetadata { uint32 exp_e = 1; uint32 exp_f = 2; optional PatchesMetadata patches = 3; }
// vortex.fsst
message FSSTMetadata { PType uncompressed_lengths_ptype = 1; PType codes_offsets_ptype = 2; }
// shared
message PatchesMetadata { uint64 len = 1; uint64 offset = 2; PType indices_ptype = 3;
                          optional uint64 chunk_offsets_len = 4; optional PType chunk_offsets_ptype = 5;
                          optional uint64 offset_within_chunk = 6; }
```

`vortex.primitive`, `vortex.varbinview`, `vortex.struct`, `vortex.chunked`, `vortex.null`,
`vortex.masked`, `vortex.fixed_size_list`, `vortex.ext`, `vortex.bytebool`, `vortex.zigzag` and
`fastlanes.for`: empty metadata.

Two traps in that neighbourhood, in opposite directions. `vortex.constant` **is** empty — but its
scalar is not absent, it lives in **buffer 0** as a Protobuf `ScalarValue`, so a decoder that reads
the metadata and stops finds nothing. `fastlanes.for` is **not** empty: its metadata is a bare
`ScalarValue` holding the frame-of-reference value, with zero buffers and one child. And
`vortex.zoned`'s layout metadata is a version byte followed by a protobuf, not a bare message.

The complete transcription of every metadata message, with the upstream source path of each, is
[spec/METADATA.md](../../spec/METADATA.md). Its tag numbers are the contract.

#### Unknown Protobuf fields: skip, never reject

The Protobuf runtime **skips fields with unknown numbers**, dispatching on the wire type (varint,
fixed32, fixed64, length-delimited; groups, wire types 3 and 4, are rejected, as proto3 never emits
them).

This protects the read-forever promise. Upstream may add an *optional* field to a metadata message
without it being a reader-visible evolution: old readers ignore it and the semantics are unchanged,
so upstream need not mint a new id. A parser that rejected unknown fields would fail on legal files,
and a corpus pinned to today's version would not notice until its next regeneration.

The distinction to keep sharp:

* **Reject** = a value outside the contract's domain (a forbidden ptype, `lower_part_count != 0`,
  an out-of-range exponent).
* **Tolerate** = a field number the wire format does not recognize.

**An encoding id is a frozen contract.** A reader-visible evolution gets a **new id**
(`vortex.foo` → `vortex.foo_v2`), never a silent extension. A decoder therefore rejects a payload
outside the contract of the id it read, even when it could decode the successor's: `vortex.pco`
refuses an 8-bit dtype, which belongs to `vortex.pco.v2`.

### 5.4 Validity (nullability)

Validity is not a mandatory bitmap. Four states: `NonNullable`, `AllValid`, `AllInvalid`, or a
**boolean array** (itself possibly encoded: constant, dict, runend…). `true` = valid. The validity
array's DType is always non-nullable `Bool`.

## 6. Layouts (`layout.fbs`)

```fbs
table Layout {
    encoding: uint16;     // index into Footer.layout_specs
    row_count: uint64;    // rows represented — the basis for pruning
    metadata: [ubyte];    // opaque, layout-specific
    children: [Layout];
    segments: [uint32];   // indices into Footer.segment_specs
}
```

| ID | Role | Children | Segments | Metadata |
|---|---|---|---|---|
| `vortex.flat` | one serialized array | 0 | exactly 1 | `optional bytes array_encoding_tree` — when present the array FlatBuffer is inlined in the layout and the segment holds only buffers |
| `vortex.chunked` | row-wise partition | ≥ 1 | 0 | empty (chunk offsets derive from children `row_count`s, whose sum must equal the parent's) |
| `vortex.struct` | one child per field (+ validity) | n fields | 0 | empty |
| `vortex.dict` | dictionary shared with a child | codes + values | — | `PType codes_ptype = 1; optional bool is_nullable_codes = 2; optional bool all_values_referenced = 3;` |
| `vortex.zoned` | statistics zone map for pruning | data + stats table | — | `[u8 version = 1] ++ proto { uint32 zone_len = 1; repeated AggregateSpec aggregate_specs = 2; }` |
| `vortex.stats` | legacy ancestor of `zoned` | same | — | must stay readable (read by the same machinery as `zoned`) |

Aggregates allowed in zone maps (core edition): `vortex.min`, `vortex.max`, `vortex.bounded_min`,
`vortex.bounded_max`, `vortex.nan_count`, `vortex.null_count`.
An unknown aggregate must **not** invalidate the file: it disables the affected pruning only.

## 7. What the Rust writer produces

What a default Rust writer emits is what most files contain. Its pipeline is **split structs →
repartition into 8 192-row blocks → zone maps → dictionary where useful → coalesce toward ~1 MiB →
BtrBlocks compression → flat leaves**.

Compression schemes enabled by default, in this order (order breaks ties):
FoR, ZigZag, BitPacking, Sparse, IntDict, RunEnd, Sequence, IntRLE, Delta; ALP, ALPrd, FloatDict,
NullDominatedSparse, FloatRLE; StringDict, FSST, OnPair; BinaryDict, VarBin; Decimal; Temporal.

Two caveats:

* **Zstd and Pco are not enabled by default** (`with_compact()`, the `zstd` and `pco` features), so
  ordinary files need neither. This library reads both, every type, mode and delta pco has, and
  writes both: zstd costs it no dependency, since `net11.0` ships `ZstandardDecoder` in
  `System.IO.Compression`, and pco is ported, its encoder tried by the size-first profile as
  `with_compact()` tries it.
* **`fastlanes.delta` belongs to no edition.** The Delta scheme is in the list, but the edition
  allowlist forbids its id, so it appears only in files written with enforcement turned off.

## 8. Algorithmic details that are easy to get wrong

* **FastLanes**: 1024-element **transposed** blocks. Bit-packing operates in that order, not
  logical order — which is what allows unpacking without cross-lane shuffles, and any access by
  logical index must go through the inverse transposition. There are **two** permutations, not one,
  and they are easy to conflate: the bit-packing index
  `FL_ORDER[row / 8] * 16 + (row % 8) * 128 + lane` (lane over `1024 / bitwidth(T)` lanes), and the
  element transposition `lane * 64 + FL_ORDER[order] * 8 + row` (lane always `idx % 16`). Only
  `FL_ORDER = [0,4,2,6,1,5,3,7]` is its own inverse; the transposition itself is not, so
  `untranspose` is the inverse mapping and not a second `transpose`. Both derivations, their
  inverses, and the known-value table to test against are in
  [spec/REFERENCE.md](../../spec/REFERENCE.md).
* **FSST**: 255-entry symbol table; code 255 is the escape code. Symbol buffers are padded to that
  fixed size.
* **ALP**: the encoded integer is `i32` or `i64`; `exp_e`/`exp_f` must be validated in range for
  the target float type. Patches in a modern ALP are read as a `Patched` array wrapping a
  patch-free ALP.
* **VarBinView**: 16-byte views (Arrow StringView compatible), `views` buffer aligned to 16.
* **Patches**: one shared structure (indices, values and optional chunk offsets) that BitPacked,
  ALP and Sparse all use, implemented once here.
