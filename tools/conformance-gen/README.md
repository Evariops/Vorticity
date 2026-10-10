# conformance-gen

Generates the golden Vortex corpus for Vorticity's conformance tests, plus one expected-value sidecar
per file, so day-to-day .NET test runs need no Rust toolchain.

The problem it solves is stated in [docs/design/04-conformance.md](../../docs/design/04-conformance.md):
an implementation that only tests against itself proves nothing, because a round trip through its own
writer and reader is self-consistent and can be uniformly wrong. Everything must be anchored to files
produced by the Rust reference implementation.

## The pinned version, and why it is a pin

```toml
vortex = "=0.86.1"
```

The pin is exact on purpose. It is a conformance decision (see [the golden
corpus](../../docs/design/04-conformance.md#3-the-golden-corpus)), not a dependency choice: a version
bump is a corpus regeneration, and the version that produced a corpus is recorded in `manifest.json`
and in every file record.

0.86.1 is the release [docs/design/99-sources.md](../../docs/design/99-sources.md) derived the
specification against. It can target every frozen core edition up to `core2026.08.3`, so the corpus
reaches every component in scope. It needs rustc 1.95 or later (built here with 1.98.1).

## Running

```sh
cargo build --release -j 6          # clean, no warnings

cargo run --release -j 6 --                       # the whole corpus, ~5 s
cargo run --release -j 6 -- --prune               # ...and delete files no longer in the manifest
cargo run --release -j 6 -- --filter encodings/   # one dimension; --filter is repeatable
cargo run --release -j 6 -- --list                # the plan, writing nothing
cargo run --release -j 6 -- --verify              # hash the corpus again against manifest.json

# Derive the coverage union again from the written footers, sharing no code with
# the generator's own extraction path.
cargo run --release -j 6 --example verify_corpus -- ../../tests/Vorticity.Conformance/corpus

# Assert that the forged fixture set behaves the way its manifest claims.
cargo run --release -j 6 --example verify_forged -- ../../tests/Vorticity.Conformance/forged

# Does Vortex Rust read what Vorticity writes? Produce the files from .NET first,
# then hand them to the reference and compare scalar by scalar against its own corpus file.
VORTICITY_WRITE_CORPUS=/tmp/vxwritten dotnet test ../../Vorticity.slnx -c Release \
    --filter-method '*WritesTheCorpusOutForTheRustCrossCheck*'
# VORTEX_EXPERIMENTAL_PATCHED_ARRAY=1 because the verifier opens the REFERENCE file to compare
# against, and containers/experimental_patched_array_editions_off carries `vortex.patched`, which
# Vortex refuses without that switch. It says nothing about our output, which uses no such encoding.
VORTEX_EXPERIMENTAL_PATCHED_ARRAY=1 VORTEX_EXPERIMENTAL_LIST_LAYOUT=1 \
    cargo run --release -j 6 --example verify_written -- \
    ../../tests/Vorticity.Conformance/corpus /tmp/vxwritten
```

Output goes to `tests/Vorticity.Conformance/corpus` by default, and `--out <dir>` redirects it.
`--seed <u64>` overrides the master PRNG seed, which changes every random value in the corpus and is
therefore a corpus version bump, not a tuning knob.

## What the corpus is

876 `.vortex` files and 876 sidecars, about 128 MiB on disk (15.3 MiB of Vortex files, the rest
mostly sidecars, plus the manifest and the sidecar grammar), 2 571 779 rows in all. That fits the CI
artifact budget of about 200 MB, so nothing has been trimmed. A small forged-fixture directory sits
next to it and is not part of the corpus (see [below](#the-forged-fixture-set)).

```
tests/Vorticity.Conformance/
  corpus/
    manifest.json        per-file record, coverage gate, shape gate, caveats, list of gaps
    SIDECAR.md           the sidecar line grammar, so the artifact describes itself
    types/        509    the type matrix: dtype x nullability x row count
    encodings/    292    one file per array encoding x length, that encoding forced, plus shapes
    distributions/ 51    adversarial value distributions
    containers/    17    layout and container variations
    editions/       7    one file per frozen core edition
  forged/
    manifest.json        source file, source hash, and the exact byte patch for each fixture
    negative/       2    files no conformant writer can produce
    legacy/         1    a valid file in a layout 0.86.1 reads but cannot write
```

### The type matrix

Every logical dtype × nullability × `{0, 1, 1023, 1024, 1025, 8191, 8192, 8193}` rows: 1024 is the
FastLanes block, 8192 the default row block, and the ±1 neighbours are where bugs live. Files are
written with default write options, the production path. Nullability is independent of the value:
the per-dtype factory always produces a non-null value and the harness injects nulls on a fixed
pattern, so a nullable file and its non-nullable twin differ in exactly one dimension.
Representation extremes (`i64::MIN`, `u64::MAX`, `-0.0`, NaN, subnormals, the decimal precision
limits) sit at rows 0–2, so every file with three or more rows carries them.

All six decimal storage widths are present (`decimal(2,1)` → i8, `(4,2)` → i16, `(9,2)` → i32,
`(18,4)` → i64, `(38,10)` → i128, `(40,10)` → i256), matching
`DecimalType::smallest_decimal_value_type` (`dtype/decimal/types.rs:47`). The i256 values run past
`i128::MAX`, so the high limb is non-zero and a reader that quietly narrows is wrong rather than
lucky. Every Utf8 value class is present at every UTF-8 encoded width: the string filler cycles
ASCII, 2-byte, 3-byte, 4-byte (surrogate-pair) and mixed alphabets, one of which embeds a U+0000, and
every string hits its target byte length exactly, so byte length and character count differ and a
confusion between the two fails instead of agreeing.

There is also a deeply nested struct, a non-struct root, a struct root with a column per dtype, a file
written with `exclude_dtype()`, a file with user metadata segments, and `types/struct_field_names`, a
struct whose fields are named `""`, `zones`, `data`, `codes`, `values`, `élan`, `名前`, `😀`,
`has space`, `a.b`, `A` and `a`. The middle four are the names the layout tree uses for its own
children, so a reader that resolves layout children by name rather than by index breaks on it.

### The encoding matrix

There is one file per array encoding per length, with that encoding forced and verified by reading
the id back out of the written bytes. A compressor allowlist cannot deliver this:
`vortex-compressor` compresses with the best scheme and verifies the result is smaller, so restricting
the scheme set makes an encoding possible, never guaranteed. Every case builds the array already in
the target encoding and writes it through a `chunked(flat)` strategy that neither repartitions,
canonicalizes nor compresses.

The lengths are 4096 (the bare `encodings/<id>` name) plus `_r0`, `_r1`, `_r1023` and `_r1025`, the
degenerate and off-block cases where a decoder divides by a block count or indexes `ends[n - 1]`. Five
combinations are refused by the constructor itself and recorded as skips.

Beyond one file per encoding, the matrix carries the shapes the compressor never picks on its own,
because an encoding id says nothing about them:

| file | the shape it is the only witness to |
|---|---|
| `bool_bit_offset{3,7,_straddle}` | `vortex.bool` with a non-zero bit offset, the encoding's distinguishing feature, which is 0 in every other bool array and bool validity child |
| `dict_{u8,u64}_codes` | dict codes that are not `u16` |
| `dict_nullable_codes` | `is_nullable_codes = true`, an optional field whose absent, false and true states are three read paths |
| `dict_nullable_values_nonnull_codes` | a null that comes from the dictionary, not the code |
| `alp_no_patches` | `vortex.alp` with 1 child, so no patch slots at all |
| `alp_patched_no_chunk_offsets` | 3 children: patches whose `chunk_offsets_dtype()` is `None` |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | the 2-child bitpacked shape |
| `chunked_one_chunk`, `chunked_empty_chunks` | a chunked array of one chunk, and one with zero-row chunks first, in the middle and last |
| `masked_all_invalid`, `masked_all_valid` | a materialized column under an explicit all-zero or all-one validity child |

### Value distributions

The distribution is what selects the encoding, so these files are not just extra data: constant
values and long runs, low and high cardinality, sorted and reverse-sorted values, arithmetic
progressions, mostly-null and all-null columns, NaN payloads, ±Inf, −0.0 and subnormals, ALP- and
ALPrd-shaped floats, FSST-shaped prefixes, empty strings, VarBinView lengths 0, 4, 12, 13, 16 and 31, a
string over 1 MiB, `unicode_utf8` (2-, 3- and 4-byte sequences, a combining mark, an embedded U+0000),
and `over_bound_utf8`, where every value is longer than the 64-byte zone bound, which is what makes
`Precision::Inexact` and the zone map's `unknown: true` branch reachable at all.

### Container variations

Dict, zoned and chunked layouts, zone maps off, fully uncompressed, file statistics off, edition
enforcement off, the largest postscript this writer can emit, and three environment-switch variants
(`FLAT_LAYOUT_INLINE_ARRAY_NODE`, `VORTEX_EXPERIMENTAL_PATCHED_ARRAY`,
`VORTEX_EXPERIMENTAL_LIST_LAYOUT`), each produced by a child process, because every Vortex environment
switch is a `LazyLock` read once per process.

The containers also provide the zone-map substrate. `containers/zoned_many_zones` holds 64 zones of
1024 rows whose bounds are pairwise disjoint in all three columns: a strictly increasing i64, one
distinct i32 per zone, and strings from an alphabet disjoint per zone, so that `bounded_min/max(64)`
differ too. `containers/zoned_many_zones_nulls` adds columns whose per-zone `null_count` sweeps from 0
to 1024 (the last zone is entirely null) and whose `nan_count` sweeps from 0 to 63. Without these, no
predicate could eliminate a strict, non-trivial subset of zones anywhere in the corpus, and the
equivalence test with pruning on and off (see [the negative
tests](../../docs/design/04-conformance.md#6-negative-and-forward-compatibility-tests)) would pass on
a reader that prunes the wrong zone.

### Edition spread

There is one file per frozen core edition, from `core2025.05.0` to `core2026.08.3`, for the
read-forever test (see [read forever](../../docs/design/04-conformance.md#8-read-forever)). Each
file's table is shaped to pick what that edition adds: a sequence column for `core2025.06.0`,
fixed-size-list and list columns for `core2025.10.0`, the long-shared-prefix distribution for
`core2026.08.1`'s `vortex.onpair`, a map column for `core2026.08.2` and a uuid column for
`core2026.08.3`. The claim is asserted through `expected_array_ids`, so a miss fails the generator run
rather than passing silently. Columns are cumulative, which is the read-forever story stated as data.
Members of an edition that the default pipeline does not pick (`fastlanes.rle` loses to
`vortex.runend`, and so on) are named in each file's `notes` with the reason, and every one of them
has a forced `encodings/*` file.

## The forged fixture set

`tests/Vorticity.Conformance/forged/` is deliberately not part of the corpus. These files are not
output of the reference implementation, and a .NET test that globs the corpus must not read them as
golden data. The set has its own `manifest.json`, in which every fixture records the source corpus
file, that file's SHA-256 and the exact byte patch applied, so each one can be derived again and
audited rather than taken on trust.

`negative/unknown_encoding_id` is `containers/uncompressed_canonical` with the 16-byte id string
`vortex.primitive` in the footer's `array_specs` overwritten by `vortex.unknown01`, an id registered
nowhere. Equal length matters: the id is a length-prefixed flatbuffer string, an overwrite of equal
length leaves every offset valid, and Vortex checksums nothing. `negative/unknown_layout_id` does the
same for a layout id. They serve both negative tests (see [the negative
tests](../../docs/design/04-conformance.md#6-negative-and-forward-compatibility-tests)), and
`examples/verify_forged.rs` asserts the manifest's expectations against the Rust reader, for instance:

```
open:            ok, dtype {ints=i64, strs=utf8}
project strs:    ok, 4096 rows, {strs=utf8}
project ints:    rejected: Unknown encoding: vortex.unknown01
```

The second line is the one that matters: it locks in lazy component resolution, and a reader that
resolves every declared id up front fails it.

`legacy/stats_layout` is the exception, a perfectly valid file in the legacy `vortex.stats` layout,
which 0.86.1 can read but not write. It closes the one gap the coverage gate reports for the corpus.

## The sidecar

The sidecar is JSON Lines, one file per corpus file, produced by the Rust reader. The format is
`vortex-conformance-sidecar/2`, and its grammar ships inside the corpus as `SIDECAR.md`, so the
published CI artifact describes itself without a Rust toolchain. It must be lossless enough to catch a
wrong decode, which rules out every convenient shortcut:

| trap | what the sidecar does instead |
|---|---|
| `1.0` for a float | raw IEEE bits in hex (normative) and a decimal |
| `inf` or `-inf` in that decimal | `Infinity` or `-Infinity`, which both Rust and .NET parse, plus an explicit `special` field |
| a JSON number for `u64::MAX` | a decimal string, since JSON numbers are f64 in most parsers |
| a JSON string for text or binary | base64, since a JSON string cannot carry invalid UTF-8 |
| a byte length standing in for a character count | Utf8 carries `len` and `char_count` |
| "empty means null" | `null` is JSON null, and nothing else ever is |
| a `Display` string where a parsed value is meant | `dtype_json` on the dtype, layout and file-statistics lines alike |

The lines come in this order: `header`, `dtype`, `layout`, `metadata`, `file_stats`, one `zone_map`
per `vortex.zoned` layout with its zone table decoded, then the values in `rows` batches of 128, then
`null_counts` per field path.

What each part of a sidecar catches:

* `header.entry_id`, `path` and `sha256`: a sidecar names the `.vortex` file it describes and carries
  its hash. Paired by filename convention alone, a partially regenerated corpus would be undetectable
  from inside a sidecar, so a loader should check the hash before trusting anything else in the file.
* `layout.array_tree`: every `vortex.flat` node carries the array encoding tree serialized inside it
  (`{id, nchildren, nbuffers, metadata_len, metadata_b64, children}`, child order preserved). The
  manifest's flat per-file id set cannot see structure, since `map(listview(struct(...)))` and
  `listview(map(...))` produce the same set and the same values. It cannot see shape either (for
  BitPacked and ALP the patch shape is the child count), and it cannot attribute an encoding to a
  column, which for a 30-column struct meant 19 ids with no column attribution at all.
  `metadata_b64` is the raw metadata protobuf, which is where `vortex.bool`'s bit offset lives.
* The `metadata` line: user metadata segment payloads, base64, in stored order, with `""` for a
  segment that is present but empty. With the keys alone, a reader that returned the wrong bytes,
  truncated the 256-byte binary payload, or conflated `conformance.empty` with an absent key would pass
  every check in the corpus.
* `zone_map.aggregate_details`: per aggregate, a `precision`, `exact` for
  `vortex.min/max/null_count/nan_count` and `bound` for `vortex.bounded_min/bounded_max`. The zone map
  is the pruning input, so without a precision, a reader that treated `vortex.bounded_min(64)` as an
  exact minimum, and therefore pruned a zone that does contain matching rows, would compare equal
  against every `zone_map` line in the corpus.
* `zones.distinct_zones`: whether that map's zones actually differ from one another. A zone map whose
  zones carry identical bounds only proves it can be parsed.

Every sidecar is checked against the same file's footer: a decoded value set against the footer
statistic covering it (min, max, sum, `nan_count`, `null_count`), with zero disagreements. Where a
statistic is a bound rather than a value, the reader marks it `inexact` and the check honours that.

## Coverage gate

The rule (see [the golden corpus](../../docs/design/04-conformance.md#3-the-golden-corpus)) is that
the union of `array_specs` and `layout_specs` across the corpus must cover every component this
library claims to support. There are two readings of that, both computed and both reported, because
they differ:

* `array_specs` as written in the footer over-reports. The writer pre-populates the array context with
  every id the enabled editions permit, for byte determinism (`vortex-file-0.86.1/src/writer.rs:387`).
  Across this corpus its union is 40 ids, including `vortex.union` and `fastlanes.transposed_bool`,
  which no array anywhere in the corpus uses, so a gate reading `array_specs` would pass components
  with no file behind them.
* What is actually serialized is the answer. The generator walks each `vortex.flat` leaf's serialized
  array tree without decoding it, and resolves each node's interned `u16` through that leaf's own read
  context, which gives 37 ids. `manifest.json` records both per file, as `array_ids` and
  `declared_array_ids`, so the gap stays visible. The walk is the same one the sidecar records as
  `array_tree`, so there is one extraction path rather than two that can drift.

The result against [docs/design/90-registry.md](../../docs/design/90-registry.md):

| kind | in scope | covered | gap |
|---|---|---|---|
| array encodings | 35 | 35 | none |
| layouts | 6 | 5 | `vortex.stats`, covered by `forged/legacy/stats_layout` |
| extension dtypes | 4 | 4 | none |
| zone-map aggregates | 6 | 6 | none |

The 35 include `vortex.map`, `vortex.pco`, `vortex.variant`, `vortex.parquet.variant` and
`vortex.zstd_buffers`, which are gated like the other thirty.

Two ids appear that the registry lists as belonging to no edition: `fastlanes.delta` and
`vortex.patched`. Both are deliberate forward-compatibility fixtures, written with edition enforcement
off (see below). The check for unclaimed ids runs over layouts, aggregates and extension dtypes as
well as arrays. Over arrays alone, `vortex.list` would sit in the corpus as a layout the registry's
layout table does not list while the manifest declared full coverage. `vortex.list` is only reachable
under `VORTEX_EXPERIMENTAL_LIST_LAYOUT=1` and belongs to no edition, exactly as `vortex.patched` does
on the array side. It is listed in `manifest::EXPERIMENTAL_LAYOUTS` and named in its entry's `notes`,
so the report stays a signal rather than a permanent line of known noise.

## Shape gate

The coverage gate answers whether every claimed component is present somewhere. It would pass on a
corpus where every `vortex.bool` bit offset is 0, every dict has non-nullable `u16` codes, every ALP
array has patches with chunk offsets, every zone map has two zones with identical bounds, every Utf8
value is ASCII, and three of the six decimal storage widths never appear. None of those can be
expressed as a component id, so each gets a named check of its own, 17 in all, evaluated from
`manifest.json` and printed by the generator:

```
=== shapes ===
  [x] bool bit offset                        some vortex.bool array has a non-zero bit offset
  [x] dict codes u8 / u16 / u64              ...
  [x] dict nullable codes                    some vortex.dict has nullable codes
  [x] alp without patches                    some vortex.alp has 1 child (no patch slots)
  [x] alp patches, no chunk offsets          some vortex.alp has 3 children
  [x] alp patches with chunk offsets         some vortex.alp has 4 children
  [x] bitpacked patches, no chunk offsets    some fastlanes.bitpacked has 2 children
  [x] chunked array, one chunk               some vortex.chunked array wraps a single chunk
  [x] many distinct zones                    some file has >= 32 zones with pairwise distinct bounds
  [x] unicode utf8 / embedded NUL in utf8    ...
  [x] empty metadata segment                 some file has a present-but-zero-length segment
  [x] decimal i8 / i16 / i256 storage        ...

  SHAPE GATE: pass
```

A miss fails the run the same way a missing component does, unless the run was filtered. The per-file
inputs are recorded so the same assertions can be made from .NET: `array_node_shapes`
(`<id>/c<children>/b<buffers>`), `dict_nodes`, `max_bool_bit_offset`,
`zone_maps_with_distinct_zones`, `utf8_non_ascii`, `utf8_embedded_nul`, and `metadata_segments` with
per-key byte lengths.

## The gaps, and which kind each one is

The distinction that matters is between "the writer cannot emit it" and "the generator did not ask".
Only the first is a legitimate gap, and the second is a bug. Every entry below is of the first kind,
and each one is a `SkipRecord` in `manifest.json` with the source reference that establishes it.

| gap | why |
|---|---|
| `vortex.stats` layout | The vtable exists and readers handle it, but no writer path constructs it. `ZoneMapSchema::LegacyStats` is `pub(crate)`, `ZonedLayout::try_new` only accepts the `AggregateFns` variant, `vortex-layout/src/layouts/zoned/writer.rs` never mentions `LegacyStats`, and `ZonedMetadata::metadata` panics on the legacy schema (`zoned/mod.rs:108`). A real `vortex.stats` file would have to come from an older release, which would break the single-version pin, so the forged `legacy/stats_layout` fixture covers it instead |
| Zstd-compressed segments | The format reserves per-segment compression, but the 0.86.1 writer hard-codes it off: `PostscriptSegment::write_flatbuffer` passes `_compression: None` (`footer/postscript.rs:251`) and `FileLayout` passes `compression_specs: None` (`footer/file_layout.rs:75`). No public API sets either. `containers/zstd_arrays_in_segments` is the closest reachable variant, compressed at the array level, not the segment level |
| LZ4-compressed buffer | The same mechanism, and in addition no LZ4 codec is registered anywhere in 0.86.1, with no `lz4` dependency in any `vortex-*` crate |
| postscript near the 65527-byte ceiling | The writer's own ceiling is two orders of magnitude lower: four segment locators plus at most `MAX_METADATA_SEGMENTS` (16) entries with keys capped at `MAX_METADATA_KEY_BYTES` (64) (`footer/mod.rs:44,51`), about 1 KiB of key budget. `containers/postscript_max_metadata` is the largest postscript actually achievable |
| deliberately false min or max | This one has to be forged, and the forged set does not hold it yet: a zone's min and max live inside a compressed, encoded stats array, so inverting one means re-encoding rather than patching bytes |
| row-encoding golden vectors | Not produced here. `vortex-row` is not published on crates.io, so [tools/row-vectors](../row-vectors/README.md) takes it from the Vortex repository at tag `0.86.1` as a git dependency and writes the vectors (see [the row encoding](../../docs/design/04-conformance.md#7-the-row-encoding)). The `rows/*` skip and its caveat in `manifest.json` predate that crate |
| `vortex.chunked` layout with one chunk | `ChunkedLayoutStrategy::write_stream` collapses a single-child layout into that child, `if child_layouts.len() == 1 { return child }` (`layouts/chunked/writer.rs:86`). The equivalent array shape is in the corpus as `encodings/chunked_one_chunk` |
| `vortex.chunked` layout with a zero-row chunk | The file writer filters empty chunks out of the write stream before any layout strategy sees them, `.try_filter(\|chunk\| ready(!chunk.is_empty()))` (`vortex-file-0.86.1/src/writer.rs:270`), as a 100/0/100 stream confirms by producing a two-child layout. The equivalent array shape is in the corpus as `encodings/chunked_empty_chunks` |
| unknown encoding id | Not producible by any writer by definition: the per-kind allowlist rejects an unregistered id, and `disable_editions()` only widens it to ids the session has registered. It is produced by byte-patching instead, and shipped in the forged-fixture set |
| nullable top-level struct | Unwritable by any configuration. `write_internal` always calls `accumulate_stats`, which constructs a `FileStatsAccumulator` before looking at the requested statistics, and that constructor panics on a nullable top-level struct (`layouts/file_stats.rs:461`). `with_file_statistics(vec![])` does not help. Nullable structs are still covered as non-root columns |
| `DType::Union` | `vortex.union` is in no core edition, and the generic builder refuses it outright (`todo!("TODO(connor)[Union]: unimplemented")`, `builders/mod.rs:462`) |
| `preview2026.08.0` edition | Preview editions are not frozen, so a file written against one carries no read-forever guarantee and has no place in a corpus for interoperability regressions |
| Vortex 0.36.0 floor files | This crate is pinned to exactly 0.86.1. Floor files would need a separately pinned generator, and `editions/core2025.05.0` is as close as this one gets |

`fastlanes.delta` and `vortex.zstd_buffers` are not gaps, since they are generated. Neither belongs to
a core edition, but "no core edition contains it" is a weaker claim than "this release cannot write
one", and the corpus should not assert the stronger one. `encodings/fastlanes_delta` and
`encodings/zstd_buffers` write them with `disable_editions()`, and both round-trip through the Rust
reader to their exact input values. They are the fixtures the forward-compatibility tests need:
structurally valid files carrying an encoding a conformant reader may legitimately not know.

## Determinism

Two runs produce byte-identical output, `.vortex` files, sidecars, `manifest.json` and `SIDECAR.md`
alike. This was verified over every file by SHA-256, across separate processes and different output
paths, across `TZ=Asia/Tokyo` and `TZ=Pacific/Kiritimati`, `LC_ALL=tr_TR.UTF-8` and `LC_ALL=C`, and
under ten-way CPU contention. The 0.86.1 writer has no nondeterministic dimension.

Three things make that true, all recorded in `manifest.json`:

* Random values come from a hand-rolled SplitMix64 seeded with `splitmix64(master_seed ^
  fnv1a64(entry_id))`, so a single entry regenerates in isolation and matches. It is hand-rolled
  because a crates.io RNG may change its stream in a minor version.
* The writer pre-populates the array context rather than interning ids in the order serialization
  completes, which would otherwise vary from run to run (`vortex-file-0.86.1/src/writer.rs:387`,
  `NOTE(os)`). This is also why `array_specs` over-reports, since the two facts are the same fact.
* User metadata segments are sorted by key before being written (`footer/serializer.rs:116`), so the
  `HashMap` they are collected in does not leak its iteration order into the bytes.

## Caveats: reference behaviour that looks like a bug

`manifest.json` carries a `caveats` array. These are things Vortex 0.86.1 really does, and a .NET test
that "corrects" one will disagree with every real Vortex file. The most important one:

> The file-level `sum` statistic is not the IEEE sum on a float column containing an infinity. Vortex
> binds `Sum` with `NumericalAggregateOpts::skip_nans()` (`stats/expr.rs:53`), and on a column holding
> both +Inf and −Inf it records `0.0` (non-nullable) or `-inf` (nullable) while marking the value
> `exact`, where the true sum is NaN. 40 corpus files are affected. Float columns with no infinity
> agree with an independent Kahan sum to the bit (10 of 10).

The others: `inexact` precision means a bound, not a value (`distributions/huge_string_r16` truncates
`min` to 64 bytes of a 1.1 MB string), `vortex.dict` keys floats by bit pattern rather than by IEEE
equality, so a 15-value dictionary survives `0.0 == -0.0` and `NaN != NaN`, and `array_specs`
over-reports as described above.

## Code map

| file | contents |
|---|---|
| `src/main.rs` | CLI, the produce loop, child-process spawning, the coverage gate, `--verify` |
| `src/schema.rs` | the type matrix and the value distributions |
| `src/encodings.rs` | one forcing recipe per array encoding |
| `src/emit.rs` | write strategies, the corpus plan, produce-then-verify, the static skip list |
| `src/sidecar.rs` | reading back and dumping expected values |
| `src/manifest.rs` | the manifest, the claimed-component lists, the coverage summary, the caveats |
| `src/util.rs` | PRNG, base64, hex, SHA-256 |
| `src/forged.rs` | the forged-fixture set: byte patches no writer can produce, with their provenance |
| `examples/verify_corpus.rs` | independent derivation of the coverage, sharing no code with `emit` |
| `examples/verify_forged.rs` | asserts the forged manifest's expectations against the Rust reader |
| `API-NOTES.md` | the 0.86.1 API, as this generator uses it |
| `examples/api_experiments.rs` | the runnable proof behind `API-NOTES.md` |
