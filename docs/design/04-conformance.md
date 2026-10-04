# Testing and conformance

A format implementation tested only against itself proves nothing: a round trip through its own
writer and reader is self-consistent and can be uniformly wrong. So every claim about bytes is
anchored in the Rust reference, in both directions. Tests use xUnit v3 on Microsoft.Testing.Platform.

## 1. Four layers

| layer | question | needs Rust |
|---|---|---|
| unit and property tests | does this kernel, parser or operator do what the spec says? | no |
| golden corpus | do we read what Rust wrote, value for value? | when the corpus is generated |
| cross-check | does Rust read what we write, value for value? | yes, in CI |
| fuzzing | does malformed input ever escape a clean failure? | no |

## 2. Unit and property tests

- The FlatBuffers and Protobuf readers on hand-built bytes: a missing vtable field reads its
  default, an empty vector, an absent optional table, and a truncated or over-long input fails with
  `VortexFormatException` and nothing else.
- Every SIMD kernel against a scalar reference over random inputs; in CI, again with hardware
  intrinsics disabled, every test class that reaches a scalar fallback and the conformance corpus.
- The FastLanes transposition: `untranspose(transpose(i)) == i` for every `i` of a block, as the
  reference asserts.
- Every operator against the full materialization it replaces: a projection, a filter, a take, a
  count or an aggregate equals the same operation applied to every decoded row.
- The allocation ratchets of [05-benchmarks.md](05-benchmarks.md) §5.

## 3. The golden corpus

`tools/conformance-gen`, a Rust crate pinned to `vortex = "=0.86.1"`, writes the corpus under
`tests/Vorticity.Conformance/corpus`: 876 files, deterministic from a fixed seed, across the seven
core editions from `core2025.05.0` to `core2026.08.3`. The matrix is deliberate:

- every dtype × nullability × row counts of 0, 1, 1 023, 1 024, 1 025, 8 191, 8 192 and 8 193,
  because the block edges at 1 024 (FastLanes) and 8 192 (the default block) are where bugs live;
- every encoding forced on its own by an explicit compressor configuration, so that each is
  exercised in isolation rather than whichever the sampling happens to pick;
- the default configuration on realistic distributions, which is the production path;
- edge cases: all-null and constant columns, a single-value dictionary, strings over a mebibyte,
  deep nesting, a root that is not a struct, a file with no dtype segment, user metadata, and the
  layouts a default writer never emits (an inlined array tree, a dictionary layout).

Beside each file, a **sidecar of expected values** produced by the Rust reader (`SIDECAR.md` gives
its format): the dtype, the layout tree, the file statistics, the zone maps and every value, with
floats compared by their bits, so a wrong NaN payload or a `-0.0` read as `+0.0` fails. The tests
therefore need no Rust toolchain. A `manifest.json` records the Vortex version, the edition and the
encodings of every file, and its SHA-256.

**The coverage gate**: the union of the corpus's array and layout ids must cover every component
this library claims to read, or the build fails. What no Rust writer can produce is forged by
patching bytes, in `forged/`: the legacy `vortex.stats` layout, and the unknown ids of §6.

## 4. The cross-check: Rust reads what we write

Golden files prove that we read what Rust wrote. They do not prove that Rust reads what we write,
and a writer tested only by its own reader reads back its own mistakes. So a CI job has this
library write the whole corpus out again, and the reference read every file and compare it scalar
by scalar with the source (`bench/crosscheck.sh` locally, `verify_written` in the generator). The
writes rotate the index policies, append in pieces, rewrite files as a dataset's compaction does,
and chunk some columns apart, so that every structure this writer can emit meets a strict Rust
reader.

The benchmark shim adds an in-process check ([05-benchmarks.md](05-benchmarks.md) §6): both readers
return the same rows and the same checksum of every decoded value on the same files.

## 5. Fuzzing

- **Target**: open and full scan, over mutated corpus files.
- **Invariant**: only `VortexFormatException` or `VortexUnsupportedException` escapes. An access
  violation, an index out of range, an out-of-memory, a hang or a silently wrong value is a bug.
- **Structure-aware mutation**: a naive bit flip on an offset-based format dies at the first bounds
  check. The mutator (`tests/Vorticity.Fuzz`) rewrites words in the tail where the offsets live,
  truncates, writes plausible Protobuf tags and long varints, and puts extreme values in the
  lengths and counts that memory safety depends on ([08-semantics.md](08-semantics.md) §5).
- **Caps are invariants**: a file declaring a `2²⁵⁵` alignment or a segment that expands to 100 GiB
  must fail cleanly and fast.
- **Where it runs**: a smoke campaign in every test run (`FuzzSmokeTests`), and 100 000 mutations
  with a fixed seed in CI, so a finding reproduces from the log.

## 6. Negative and forward-compatibility tests

Two fixtures carry an encoding or a layout id that exists in no edition
(`forged/negative/unknown_*_id.vortex`):

- reading a **projected** column that uses one fails with `VortexUnsupportedException` naming the
  id and its kind;
- scanning **without** projecting it succeeds. This is what locks in lazy resolution
  ([08-semantics.md](08-semantics.md) §4): without it, an open path "simplified" into an eager
  failure would pass.

On the pruning side, filter tests run with pruning and indexes on and off and must return the same
rows, which is what enforces that pruning never removes a matching row.

## 7. The row encoding

The row encoder's contract is one property, byte order equals tuple order, and a format match
([06-row-encoding.md](06-row-encoding.md)):

- **The order property, randomized**: random tuples over a random schema and random sort options;
  sorting by `memcmp` of the encoded rows must give exactly the permutation of a tuple sort.
- **Adversarial values**: `-0.0` and `+0.0`, NaN bit patterns, `i64::MIN`, empty against null
  against one-byte strings, strings of 31, 32, 33 and 64 bytes around the block size, prefix pairs,
  and nulls under nested structs with garbage in their children.
- **Byte for byte against Rust**: `tools/row-vectors` encodes generated tables with the reference
  `vortex-row` and records the bytes in `tests/Vorticity.Conformance/row-vectors`; the tests
  compare every one. `vortex-row` is not published on crates.io, so that crate takes the Vortex
  repository at tag `0.86.1` as a git dependency, the only one in the repository, and its output is
  committed. Being order-compatible is not enough: two encoders can each be consistent and still
  disagree, which would break any comparison across languages.

## 8. Read forever

Files written under the oldest edition must stay readable, and the corpus holds files written under
each edition from `core2025.05.0` on: the files of the older editions are what catches a reader
that hard-codes the encodings of the newest edition. They are written by the pinned 0.86.1 generator restricted to
each edition, which is the same component set as the 0.36.0 floor with a modern writer; files from
a 0.36.0 writer itself would need a second, older generator.

A CI job re-vendors the upstream schemas and reports any drift; a change there is a format change,
reviewed against [90-registry.md](90-registry.md), and the corpus is regenerated with the generator
pinned to the new version.
