# Component registry

Every component of the format — array encodings, layouts, extension dtypes, zone-map aggregates —
with the edition that introduced it, whether this library reads it and writes it, and how a take is
served. The newest frozen edition is `core2026.08.3`, read by Vortex Rust from 0.85.0; the
read-forever floor is `core2025.05.0`, Vortex 0.36.0. The edition records are vendored in
[spec/editions](../../spec/editions).

## 1. Array encodings

37 ids have a decoder (`src/Vorticity/Arrays/Decoders`). **Take** says how a take of a few rows is
served: *selective* decoders implement `DecodeSelected` and decode only the rows asked for, or only
the frames or chunks that hold them; the others decode the chunk once per scan and gather.

| id | role | edition | written | take |
|---|---|---|---|---|
| `vortex.null` | all-null | `core2025.05.0` | yes | gather |
| `vortex.bool` | bit-packed booleans, a bit offset under 8 | `core2025.05.0` | yes | gather |
| `vortex.primitive` | a fixed-width buffer; the plain form of numbers | `core2025.05.0` | yes | gather |
| `vortex.decimal` | the plain form of decimals | `core2025.05.0` | yes | gather |
| `vortex.varbinview` | 16-byte views, Arrow's string view; the plain form of text | `core2025.05.0` | yes | gather |
| `vortex.varbin` | offsets and bytes, Arrow's string | `core2025.05.0` | yes, when smaller than views | selective |
| `vortex.struct` | a child per field | `core2025.05.0` | yes | per field |
| `vortex.list` | Arrow's list, offsets | `core2025.05.0` | no: lists are written as list views | gather |
| `vortex.listview` | offsets and sizes; the plain form of lists | `core2025.10.0` | yes | gather |
| `vortex.fixed_size_list` | a fixed-size list | `core2025.10.0` | yes | gather |
| `vortex.map` | a list view of key-value structs under the map dtype | `core2026.08.2` | yes | gather |
| `vortex.ext` | an extension's storage | `core2025.05.0` | yes | its storage's |
| `vortex.chunked` | a concatenation | `core2025.05.0` | no: chunks are a layout here | per chunk |
| `vortex.constant` | one value repeated | `core2025.05.0` | no: integers as a sequence, others as one run | selective |
| `vortex.masked` | a validity applied to a child | `core2025.10.0` | no | gather |
| `fastlanes.bitpacked` | bit-packing in 1 024-value transposed blocks, with patches | `core2025.05.0` | yes | selective, through the inverse transposition |
| `fastlanes.for` | frame of reference over bit-packing | `core2025.05.0` | yes | selective |
| `fastlanes.rle` | run-length in FastLanes blocks | `core2025.10.0` | no | gather |
| `fastlanes.delta` | per-lane deltas | none: written only with edition enforcement off | no | gather |
| `vortex.zigzag` | signed integers made non-negative | `core2025.05.0` | yes | selective |
| `vortex.sequence` | `base + i × step` | `core2025.06.0` | yes | selective |
| `vortex.runend` | run ends and values | `core2025.05.0` | yes | selective, a binary search in the ends |
| `vortex.dict` | codes and values | `core2025.05.0` | yes | selective, on the codes |
| `vortex.sparse` | a fill value and patches | `core2025.05.0` | no | selective |
| `vortex.bytebool` | a byte per boolean | `core2025.05.0` | no | gather |
| `vortex.alp` | adaptive lossless floating point: decimals scaled to integers, with patches | `core2025.05.0` | yes | selective |
| `vortex.alprd` | ALP for real doubles: high bits into a small dictionary | `core2025.05.0` | yes | selective |
| `vortex.fsst` | a static symbol table, 255 codes and an escape | `core2025.05.0` | yes | selective, through each row's code offsets |
| `vortex.onpair` | a pair-merging dictionary of tokens | `core2026.08.1` | no | selective |
| `vortex.datetimeparts` | days, seconds and subseconds apart | `core2025.05.0` | no | selective |
| `vortex.decimal_byte_parts` | decimals split by bytes; the lower part must be empty | `core2025.05.0` | no | gather |
| `vortex.zstd` | zstd frames | `core2025.06.0` | yes, a frame per block | selective, by frame |
| `vortex.pco` | Pcodec | `core2025.06.0` | no | gather |
| `vortex.zstd_buffers` | each buffer of another array compressed apart | draft `zstd2026.02.0` | no | gather |
| `vortex.variant` | a variant over its storage | `core2026.08.3` | no: written as `vortex.parquet.variant` | selective |
| `vortex.parquet.variant` | the Parquet variant binary format | `core2026.08.3` | yes | gather |
| `vortex.patched` | patches over a patch-free child | none: written only with edition enforcement off | no | gather |

A patched ALP or bit-packed array written normally carries its patches in its own metadata and needs
no `vortex.patched` node. In-memory arrays with no wire id (slices, filters, shared arrays) have
nothing to read.

**Refused by name**: the legacy two-buffer `vortex.fsst`, which keeps its codes as a nested
`vortex.varbin` array that the arena canonicalizes on the way in, inlining short codes into views, so
the contiguous code stream the decode needs no longer exists; every writer since the encoding
stabilized emits the three-buffer form, and the corpus generator can produce the other if a file in
the read-forever range ever needs it. And a shredded variant child, refused by its decoder with a
message naming it.

**A selection is pushed down, not gathered up.** A decoder that has nothing to say about a take
decodes the node once and gathers; one with `DecodeSelected` receives the selection through the
layout tree, in the coordinates of the row range beside it: `vortex.chunked` is the one layout that
re-partitions rows, so the one that rebases the selection, and a dictionary layout keeps the
selection for its codes and drops it for its shared values. Patches merge against the selection once,
for every encoding that carries them. A decoder that falls back decodes its chunk once per scan, never
once per row taken. Correctness is a corpus-wide differential: a scattered set of indices from every
file, both ends and both sides of a 1 024-value block, equals what a full scan put there.

## 2. Layouts

| id | role | edition | written |
|---|---|---|---|
| `vortex.flat` | one serialized array | `core2025.05.0` | yes |
| `vortex.chunked` | a row-wise partition | `core2025.05.0` | yes |
| `vortex.struct` | a child per field | `core2025.05.0` | yes |
| `vortex.zoned` | a zone map for pruning | `core2026.08.0` | yes, a zone per block |
| `vortex.stats` | the zone map's predecessor | `core2025.05.0` | no: no Vortex release ever emitted one |
| `vortex.dict` | a dictionary shared across a child layout | `core2025.05.0` | no ([11-write-strategy.md](11-write-strategy.md) §7) |
| `vortex.list` | elements, offsets and validity apart | none: experimental upstream | no |

## 3. Extension dtypes and aggregates

`vortex.date`, `vortex.time` and `vortex.timestamp` arrived in `core2025.05.0`, `vortex.uuid` in
`core2026.08.3`; each is a storage dtype plus metadata, which carries the unit and the time zone
([07-dotnet-mapping.md](07-dotnet-mapping.md)).

The zone-map aggregates — `vortex.min`, `vortex.max`, `vortex.bounded_min`, `vortex.bounded_max`,
`vortex.nan_count`, `vortex.null_count` — arrived with `vortex.zoned` in `core2026.08.0`. An unknown
aggregate disables the pruning it would have given and never fails the read. A file-level sum lives
in a fixed field of `ArrayStats`, not in an aggregate.

## 4. What an unknown id gets told

An id this library does not decode raises `VortexUnsupportedException` naming the id and its kind,
when a read needs it ([08-semantics.md](08-semantics.md) §4). Every id any Rust release can write has a
reader, so an id that reaches that throw comes from a future edition.

## 5. The writer's schemes and their ids

The writer's schemes are named after algorithms, and editions constrain ids:

| scheme | writes |
|---|---|
| progression, constant integers | `vortex.sequence` |
| runs, other constants | `vortex.runend` |
| bit-packing, in the raw, zigzag or frame-of-reference domain | `fastlanes.bitpacked`, under `vortex.zigzag` or `fastlanes.for` |
| dictionary | `vortex.dict` |
| ALP, ALP-RD | `vortex.alp`, `vortex.alprd` |
| FSST | `vortex.fsst` |
| zstd | `vortex.zstd` |
| the plain form | `vortex.primitive`, `vortex.bool`, `vortex.decimal`, and for text `vortex.varbin` or `vortex.varbinview`, whichever is smaller |

A compression reaches every child: a list's elements, offsets and sizes, a struct's fields, an
extension's storage, a dictionary's or a run's values. Validity bitmaps are one bit a row and stay
plain. How the chooser prices each is [11-write-strategy.md](11-write-strategy.md) §3.4. Over the 856
files of the conformance corpus, the writer's files are smaller than the reference's, held under a
ceiling by `WrittenSizeTests`, which prints the ratio and the files worst by bytes lost.

## 6. Edition targeting

`VortexWriteOptions.TargetEdition` names the frozen edition every component of a file must belong
to. The edition records are transcribed into `EditionRegistry`, which stores which edition introduced
each id, so that membership is one comparison.

**The candidate schemes are derived from the target before anything is priced**, as upstream's writer
does: otherwise the chooser would elect a scheme on suitable data and the write would fail at
serialization. The allowlist at serialization then fires only on a bug, and **fails the write** rather
than produce a file its target's readers cannot open: array and layout ids are checked where every id
of the file is interned, extension dtypes against the schema when the writer is created, aggregates
where the zone map is written. Every failure names the id and the edition that introduced it.

The default is **`core2026.08.3`**, the newest frozen edition, which the reference writer also
defaults to, and the first with `vortex.uuid`. Lower targets are honoured, not approximated:

| target | what changes |
|---|---|
| `core2026.08.0` and later | nothing: this is what the writer emits |
| below `core2026.08.0` | the zone map is **omitted**, since `vortex.zoned` and its aggregates arrived then; `vortex.stats` is not written instead, since no release ever emitted it and pruning's absence costs no correctness |
| below `core2025.10.0` | a list or fixed-size list column **fails the write**: its forms here, `vortex.listview` and `vortex.fixed_size_list`, arrived then |
| below `core2026.08.2`, `core2026.08.3` | a map, a variant or a uuid column fails the write |
| below `core2025.06.0` | progressions and zstd are not candidates |

`EditionTargetTests` asserts this on the written file, reading its encoding tables back, rather than at
the call sites: a writer that checked itself and then emitted something else would pass a test
written the other way round.
