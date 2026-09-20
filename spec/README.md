# Vendored upstream schemas

Verbatim copies of the files that define the Vortex wire format, taken from the
[upstream repository](https://github.com/vortex-data/vortex). They are the authoritative
source for every tag number, field order and wire type in this implementation: transcribe from
these files, never from documentation prose and never from memory.

They are Apache-2.0 licensed and remain the copyright of the Vortex contributors — see
[NOTICE](../NOTICE).

Pinned to tag **`0.86.1`**, matching the vortex pin in
[`tools/conformance-gen/Cargo.toml`](../tools/conformance-gen/Cargo.toml). Moving one means moving
both.

| Vendored path | Upstream path at `0.86.1` | Upstream path on `develop` |
|---|---|---|
| `flatbuffers/dtype.fbs` | `vortex-flatbuffers/flatbuffers/vortex-dtype/dtype.fbs` | `vortex-array/flatbuffers/vortex-dtype/dtype.fbs` |
| `flatbuffers/array.fbs` | `vortex-flatbuffers/flatbuffers/vortex-array/array.fbs` | `vortex-array/flatbuffers/vortex-array/array.fbs` |
| `flatbuffers/layout.fbs` | `vortex-flatbuffers/flatbuffers/vortex-layout/layout.fbs` | `vortex-layout/flatbuffers/vortex-layout/layout.fbs` |
| `flatbuffers/footer.fbs` | `vortex-flatbuffers/flatbuffers/vortex-file/footer.fbs` | `vortex-file/flatbuffers/vortex-file/footer.fbs` |
| `proto/dtype.proto` | `vortex-proto/proto/dtype.proto` | `vortex-array/proto/dtype.proto` |
| `proto/scalar.proto` | `vortex-proto/proto/scalar.proto` | `vortex-array/proto/scalar.proto` |
| `proto/expr.proto` | `vortex-proto/proto/expr.proto` | `vortex-array/proto/expr.proto` |

Upstream relocated these after 0.86.1. The **contents are byte-identical across the move**
(verified 2026-09-12 by diffing both refs), so this is a path change and not a format change.
`refresh.sh` tries the pinned layout and falls back to the newer one, which is what lets
`./refresh.sh develop` still work as a drift check.

## Not verbatim, but derived

| File | What it is |
|---|---|
| [`METADATA.md`](METADATA.md) | The per-encoding metadata messages, **transcribed**. They are not `.proto` files upstream — they are `prost` derive structs declared next to each encoding — so there is nothing to copy verbatim. Each message records the upstream Rust path it came from. The tag numbers are the contract. |
| [`editions/`](editions/) | The seven frozen `core` edition manifests, verbatim TOML. These are the authoritative scope list: which array / layout / dtype / aggregate ids are legal in which edition, and the minimum upstream library version that can read each. The writer derives its candidate scheme list from these *before* sampling ([docs/design/90-registry.md](../docs/design/90-registry.md)). |

Transcribing `METADATA.md` also surfaced two errors in our own design docs, both since corrected
at source; the corrections are recorded at the end of that file.

## Refreshing

```sh
./refresh.sh                 # the pinned tag - reproduces exactly what is checked in
./refresh.sh develop         # upstream tip, as a drift check
git diff -- spec/            # a non-empty diff is upstream format drift: read it before merging
```

A change here is a format change. It must be reviewed against
[docs/design/90-registry.md](../docs/design/90-registry.md) and the conformance corpus regenerated
([docs/design/04-conformance.md](../docs/design/04-conformance.md) §3), not merged on the grounds that the build
still passes.

Last refreshed: 2026-09-12, from tag `0.86.1` (commit `d1fe2dc46d`).
`METADATA.md` and `editions/` were transcribed/vendored the same day, from the same ref.
