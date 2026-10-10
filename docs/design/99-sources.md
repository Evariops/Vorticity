# Sources and how to verify them again

This specification is derived from primary sources, against Vortex 0.86.1.

## Primary sources

| Source | Used for |
|---|---|
| <https://docs.vortex.dev/specs/file-format> | File structure, postscript, footer, EOF marker |
| <https://docs.vortex.dev/specs/editions> | Edition system, component registry, compatibility rules |
| <https://docs.vortex.dev/specs/dtype-format>, `.../scalar-format` | DType and Scalar schemas |
| <https://docs.vortex.dev/concepts/layouts>, `.../arrays`, `.../scanning` | Layout and array models, scan semantics |
| <https://docs.vortex.dev/developer-guide/internals/serialization> | Blob format, alignment, zero-copy design |
| <https://docs.vortex.dev/specs/row-encoding> | Byte-sortable row encoding (see [06-row-encoding.md](06-row-encoding.md)) |
| <https://github.com/vortex-data/vortex> | Everything below |

The raw doc sources can be fetched directly, which avoids paraphrase:
`https://docs.vortex.dev/_sources/specs/<page>.md.txt`

## Files in the reference repository that define the wire format

These are the paths on `develop`. At tag `0.86.1`, which is the one pinned here, the schemas live
under `vortex-flatbuffers/` and `vortex-proto/` instead, since upstream relocated them afterwards with
byte-identical contents. All of them are vendored in [spec/](../../spec/), which records both layouts,
so read them from there rather than fetching them.

```
vortex-array/flatbuffers/vortex-dtype/dtype.fbs      DType union
vortex-array/flatbuffers/vortex-array/array.fbs      Array, ArrayNode, Buffer, ArrayStats
vortex-layout/flatbuffers/vortex-layout/layout.fbs   Layout tree
vortex-file/flatbuffers/vortex-file/footer.fbs       Postscript, Footer, SegmentSpec
vortex-ipc/flatbuffers/vortex-serde/message.fbs      IPC (out of scope)
vortex-array/proto/dtype.proto                       DType (protobuf form)
vortex-array/proto/scalar.proto                      Scalar, ScalarValue
vortex-array/proto/expr.proto                        Expressions
vortex-file/src/lib.rs                               VERSION, MAGIC_BYTES, EOF_SIZE, MAX_POSTSCRIPT_SIZE
vortex-array/src/serde.rs                            Array blob layout, padding, alignment
vortex-array/src/metadata.rs                         Metadata serialization (prost)
vortex-file/src/strategy.rs                          Default write pipeline (8192 rows, 1 MiB blocks)
vortex-btrblocks/src/builder.rs                      Default scheme list and their ordering
vortex/editions/core/*.toml                          Frozen edition records (the authoritative scope list)
vortex-row/src/                                      Byte-sortable row encoder (codec, encode, size, options)
                                                     NOT on crates.io (`publish = false`): read it
                                                     from the repo at the pinned tag, and note that
                                                     tools/row-vectors takes it as a git dependency
vortex/editions/zstd/zstd2026.02.0.toml              Draft zstd edition (vortex.zstd_buffers)
vortex-ffi/cinclude/vortex.h                         C API used for cross-testing and benchmarking
                                                     (it exposes no row-encoding entry points)
```

Per-encoding metadata messages are not in `.proto` files. They are `prost` derive structs declared
next to each encoding (`encodings/*/src/**.rs`, `vortex-array/src/arrays/**/vtable/mod.rs`). Their
transcription is in [spec/METADATA.md](../../spec/METADATA.md), with the upstream source path of each
message, and the tag numbers are the contract.

Building `tools/conformance-gen` unpacks the whole reference tree at the pinned version into Cargo's
registry. [spec/REFERENCE.md](../../spec/REFERENCE.md) gives the paths, and the reference is best read
there rather than fetched.

## Semantics resolved by reading the reference

These facts are not stated by the prose specs. They were established from the source and are cited
where they are used:

| Fact | Source | Used in |
|---|---|---|
| `Inexact` is a conservative bound (max is an upper bound on the true max), not a loose approximation, so range pruning stays legal | `vortex-array/src/expr/stats/precision.rs` (doc comment on `Precision`) | [statistic precision](08-semantics.md#1-statistic-precision-what-inexact-licenses) |
| `min` and `max` are computed with `skip_nans()`, and NaN is tracked separately in `nan_count` | `vortex-array/src/stats/expr.rs` | [NaN](08-semantics.md#2-nan) |
| The writer filters the compressor's candidate schemes by the edition allowlist before sampling | `vortex-file/src/writer.rs`, `retain_allowed_encodings` | [90-registry.md](90-registry.md) |
| Row encoding passes the parent's `RowSortField` unchanged to nested children, and inversion is applied once, at the leaf | `vortex-row/src/codec.rs`, `encode_struct` and `field_encode` | [per-type encodings](06-row-encoding.md#3-per-type-encodings) |
| `Buffer.compression` and `SegmentSpec._compression` are declared but unimplemented: 0.86.1 writes `None`, never reads either field, and depends on no lz4 crate, and `footer.fbs` calls the segment field "reserved for future use" | `vortex-array/src/serde.rs`, `vortex-flatbuffers/flatbuffers/**/*.fbs` (the only four files in the tree that mention lz4 are the two schemas and their generated code) | [buffer-level LZ4](08-semantics.md#7-buffer-level-lz4) |
| The child null sentinel under a null parent is chosen by the child's dtype with the inherited field, and fixed-width children are zero-filled even when descending | `vortex-row/src/codec.rs`, `child_canonical_null_byte` | [per-type encodings](06-row-encoding.md#3-per-type-encodings) |

Each of these would have been a plausible guess in either direction, and each one guessed wrong
produces silently incorrect results rather than a test failure. They are recorded here so that a
future change to any of them is detected as drift.

## Verification procedure

```sh
git clone --depth 1 https://github.com/vortex-data/vortex.git
# scope list for a given edition:
cat vortex/editions/core/core2026.08.3.toml
# every metadata message and its tags:
grep -rn --include='*.rs' -B2 'pub struct .*Metadata {' vortex-array/src vortex-layout/src encodings
# frozen constants:
sed -n '130,165p' vortex-file/src/lib.rs
```

The drift to watch for, in decreasing order of impact:

1. A new frozen `core` edition adds component ids the default writer may emit. The reader must be
   extended, or it will reject newly written files. This is the one that breaks users.
2. A new `_v2` id on an existing encoding means the old contract implemented here is now narrower than
   the in-memory representation. The decoder must keep enforcing the old contract.
3. A change of default in `strategy.rs` or `builder.rs` does not break correctness, but it changes
   which encodings dominate real files, and therefore which kernels deserve optimization.
4. The file format version leaving 1 would be a major event, and nothing suggests it is coming.
5. Row encoding byte-layout changes are explicitly allowed by its own spec, which is marked
   experimental. This is the one place where upstream has reserved the right to break us, so the
   golden vectors are pinned and checked on every corpus regeneration.

A CI job vendors the upstream schemas again and reports any drift, and the corpus is regenerated with
the generator pinned to the new version (see [read forever](04-conformance.md#8-read-forever)).
