# Sources and how to re-verify them

This specification was derived from primary sources on 2026-09-11, against Vortex **0.86.1**
(published the same day; previous release 0.85.0, 2026-08-21).

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

Raw doc sources are fetchable directly, which avoids paraphrase:
`https://docs.vortex.dev/_sources/specs/<page>.md.txt`

## Files in the reference repository that define the wire format

These are the paths on `develop`. **At tag `0.86.1`, which is what we pin, the schemas live
under `vortex-flatbuffers/` and `vortex-proto/` instead** — upstream relocated them afterwards,
with byte-identical contents. All of them are vendored in [spec/](../spec/), which records both
layouts; read them from there rather than fetching.

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
                                                     — NOT on crates.io (`publish = false`): read it
                                                       from the repo at the pinned tag, and note that
                                                       tools/row-vectors takes it as a git dependency
vortex/editions/zstd/zstd2026.02.0.toml              Draft zstd edition (vortex.zstd_buffers)
vortex-ffi/cinclude/vortex.h                         C API used for cross-testing and benchmarking
                                                     — note: it exposes no row-encoding entry points
```

Per-encoding metadata messages are **not** in `.proto` files: they are `prost` derive structs
declared next to each encoding (`encodings/*/src/**.rs`, `vortex-array/src/arrays/**/vtable/mod.rs`).
Transcribing them is a per-encoding task; the tag numbers are the contract. **That transcription
is done** and lives in [spec/METADATA.md](../spec/METADATA.md), with the upstream source path
recorded per message. It also records two errors it found in these docs.

The whole reference tree is unpacked locally as a side effect of building
`tools/conformance-gen` — see [spec/REFERENCE.md](../spec/REFERENCE.md) for the paths. Read the
reference there rather than fetching it.

## Local toolchain assumptions

Recorded so a future reader can tell what was actually verified rather than assumed:

* Target framework: **`net11.0`**, SDK `11.0.100-rc.1.26425.128` installed locally (alongside
  8.0.x, 9.0.x and 10.0.x, which the project does not target).
* Zstd availability was checked by inspecting the reference assemblies, not the documentation:
  `Microsoft.NETCore.App.Ref/11.0.0-rc.1.26425.128/ref/net11.0/System.IO.Compression.dll` exposes
  six Zstandard types, where the 10.0 ref assembly exposes none.
* Rust toolchain present (`cargo`, `rustc`) for the conformance generator and the FFI harness.

## Semantics resolved by reading the reference

Facts the prose specs do not state, established from source and cited where they are used:

| Fact | Source | Used in |
|---|---|---|
| `Inexact` is a *conservative bound* (max is an upper bound on the true max), not an unqualified approximation — so range pruning stays legal | `vortex-array/src/expr/stats/precision.rs` (doc comment on `Precision`) | [08-semantics.md](08-semantics.md) §1 |
| `min`/`max` are computed with `skip_nans()`; NaN is tracked separately in `nan_count` | `vortex-array/src/stats/expr.rs` | [08-semantics.md](08-semantics.md) §2 |
| The writer filters the compressor's candidate schemes by the edition allowlist **before** sampling | `vortex-file/src/writer.rs` → `retain_allowed_encodings` | [90-registry.md](90-registry.md) |
| Row encoding propagates the parent's `RowSortField` unchanged to nested children; inversion is applied once, at the leaf | `vortex-row/src/codec.rs` → `encode_struct`, `field_encode` | [06-row-encoding.md](06-row-encoding.md) §3 |
| The child null sentinel under a null parent is chosen by the *child's* dtype with the *inherited* field, and fixed-width children are zero-filled even when descending | `vortex-row/src/codec.rs` → `child_canonical_null_byte` | [06-row-encoding.md](06-row-encoding.md) §3 |

Each of these would have been a plausible guess in either direction, and each guessed wrong
produces silently incorrect results rather than a test failure. They are recorded here so a future
change to them is detected as drift.

## Re-verification procedure

```sh
git clone --depth 1 https://github.com/vortex-data/vortex.git
# scope list for a given edition:
cat vortex/editions/core/core2026.08.3.toml
# every metadata message and its tags:
grep -rn --include='*.rs' -B2 'pub struct .*Metadata {' vortex-array/src vortex-layout/src encodings
# frozen constants:
sed -n '130,165p' vortex-file/src/lib.rs
```

Drift to watch for, in decreasing order of impact:

1. **A new frozen `core` edition.** Adds component IDs the default writer may emit. Our reader
   must be extended or it will reject newly written files. This is the one that breaks users.
2. **A new `_v2` ID on an existing encoding.** Means the old contract we implemented is now
   narrower than the in-memory representation. Our decoder must keep enforcing the old contract.
3. **A change of default in `strategy.rs` or `builder.rs`.** Does not break correctness but
   changes which encodings dominate real files, and therefore which kernels deserve optimization.
4. **The file format version leaving 1.** Would be a major event; nothing suggests it is coming.
5. **Row encoding byte-layout changes.** Explicitly allowed by its own spec, which is marked
   experimental. This is the one place where upstream has reserved the right to break us, so the
   golden vectors are pinned and checked on every corpus regeneration.

The scheduled corpus regeneration described in [04-conformance.md](04-conformance.md) is what
turns this from a manual review into an automated alarm.
