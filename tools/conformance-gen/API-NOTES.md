# Vortex 0.86.1 Rust API — reconnaissance notes

Everything here was verified by compiling and running against `vortex = "=0.86.1"` on
rustc 1.98.1 (macOS aarch64). Two runnable artefacts back these notes:

| what | how to run |
| --- | --- |
| `examples/api_experiments.rs` — proves every write-control claim in §3 | `cargo run --release -j 6 --example api_experiments` |
| `src/` — the corpus generator these notes were written for (see README.md) | `cargo run --release -j 6 -- --list` |

§6 records how each of the reconnaissance pass's open questions turned out once the generator was
actually built. `src/main.rs` was the throwaway probe when these notes were written; it is now the
generator's CLI.

Source paths below are relative to the crates.io checkout under the cargo registry,
`$CARGO_HOME/registry/src/index.crates.io-<hash>/`.

Where I could not find something, it says **NOT FOUND** and what I tried. Do not read past that
into an assumption.

---

## 0. Dependencies and the runtime — no tokio needed

`Cargo.toml` now carries exactly two Vortex deps:

```toml
vortex = "=0.86.1"
vortex-btrblocks = "=0.86.1"   # only to name individual compression schemes (see §3.2)
```

**No async runtime crate was added, and none is needed.** `vortex-io` ships its own runtimes and
is re-exported as `vortex::io`. On native targets without the `tokio` feature,
`Handle::find()` returns `None` (`vortex-io-0.86.1/src/runtime/platform/native.rs:12`), so
`VortexSession::default()` has **no runtime handle** and the first write panics with
*"Runtime handle not configured in Vortex session"*. You must install one:

```rust
use vortex::VortexSessionDefault;
use vortex::io::runtime::single::block_on;       // vortex-io-0.86.1/src/runtime/single.rs:197
use vortex::io::session::RuntimeSessionExt;      // vortex-io-0.86.1/src/session.rs:64
use vortex::session::VortexSession;

block_on(|handle| async move {
    let session = VortexSession::default().with_handle(handle);
    // ... write / read here ...
    Ok::<_, anyhow::Error>(())
})?;
```

`single::block_on` builds a `SingleThreadRuntime` (an `async_executor::LocalExecutor`) and drives
everything on the calling thread. Alternatives, all in `vortex-io-0.86.1/src/runtime/`:

- `runtime::current::CurrentThreadRuntime` — smol-backed, can add a worker pool
  (`new_pool()`), `BlockingRuntime::block_on`.
- `RuntimeSessionExt::with_tokio()` — only exists behind `vortex/tokio`, which we do **not**
  enable. The `lib.rs` doc example uses `#[tokio::test]`; that is not the only way and not the
  cheapest.

The `vortex` facade's default features are `["files", "wasm-bindgen", "zstd"]`
(`vortex-0.86.1/Cargo.toml:46`), so `vortex::file` and the zstd schemes are available out of the box.

**Byte determinism**: three consecutive runs of `cargo run --release` produced the identical
SHA-256 for `probe.vortex`. The writer deliberately pre-populates the array context so segment
bytes do not vary run to run (`vortex-file-0.86.1/src/writer.rs:391`, `NOTE(os)`). Good news for a
golden corpus.

---

## 1. Building an array of each logical DType

### 1.1 The uniform path: `builder_with_capacity_in` + `append_scalar`

Source: `vortex-array-0.86.1/src/builders/mod.rs:396`. One function covers every dtype, so the
corpus generator does not need per-type constructors.

```rust
use vortex::array::builders::builder_with_capacity_in;
use vortex::array::memory::BufferAllocatorRef;
use vortex::dtype::DType;
use vortex::error::VortexResult;
use vortex::scalar::Scalar;
use vortex::array::ArrayRef;

fn build_column(dtype: &DType, rows: usize, f: impl Fn(usize) -> Scalar) -> VortexResult<ArrayRef> {
    let mut builder = builder_with_capacity_in(dtype, rows, BufferAllocatorRef::static_ref());
    for i in 0..rows {
        builder.append_scalar(&f(i))?;
    }
    Ok(builder.finish())
}
```

`ArrayBuilder` also has `append_null()`, `append_nulls(n)`, `append_zeros(n)`,
`append_defaults(n)` (`.../builders/mod.rs:120-165`). `append_nulls` **asserts** the builder dtype
is nullable — it panics, it does not error. Nulls via `append_scalar(&Scalar::null(dtype))` work
the same way and are what `src/main.rs` uses.

`builder_with_capacity` (no `_in`) is deprecated in 0.86.1 — use the `_in` form.

### 1.2 dtype construction, verified

All of these are exercised in `src/main.rs::build_probe_array` and the resulting file reads back
correctly (see `== values ==` in the program output).

| logical type | `DType` value | scalar |
| --- | --- | --- |
| Null | `DType::Null` | `Scalar::null(DType::Null)` |
| Bool | `DType::Bool(nullability)` | `Scalar::bool(v, n)` |
| every PType incl. **F16** | `DType::Primitive(PType::F16, n)` | `Scalar::primitive(f16::from_f32(x), n)` |
| Decimal | `DType::Decimal(DecimalDType::try_new(10, 2)?, n)` | `Scalar::decimal(DecimalValue::I64(v), ty, n)` |
| Utf8 | `DType::Utf8(n)` | `Scalar::utf8("s", n)` |
| Binary | `DType::Binary(n)` | `Scalar::binary(vec![…], n)` |
| Struct | `StructArray::try_from_iter([(name, array), …])` | — |
| List | `DType::List(Arc<DType>, n)` | `Scalar::list(elem_dtype, vec![…], n)` |
| FixedSizeList | `DType::FixedSizeList(Arc<DType>, size, n)` | `Scalar::fixed_size_list(elem, vec![…], n)` |
| `vortex.date` | `DType::Extension(Date::new(TimeUnit::Days, n).erased())` | `Scalar::extension::<Date>(TimeUnit::Days, Scalar::primitive(19_000i32, n))` |
| `vortex.time` | `DType::Extension(Time::new(TimeUnit::Microseconds, n).erased())` | `Scalar::extension::<Time>(TimeUnit::Microseconds, Scalar::primitive(43_200_000_000i64, n))` |
| `vortex.timestamp` | `DType::Extension(Timestamp::new(TimeUnit::Milliseconds, n).erased())` | `Scalar::extension::<Timestamp>(ext.metadata().clone(), Scalar::primitive(1_700_000_000_000i64, n))` |

`PType` is the full set `U8 U16 U32 U64 I8 I16 I32 I64 F16 F32 F64`
(`vortex-array-0.86.1/src/dtype/ptype.rs:37`). `f16` is `vortex::dtype::half::f16`
(re-exported at `.../dtype/mod.rs:189`) — no direct `half` dependency needed.

**Extension dtype constraints found in source and worth respecting:**

- `Date` accepts only `TimeUnit::Days` (storage `i32`) and `TimeUnit::Milliseconds` (storage
  `i64`). Anything else errors: `.../extension/datetime/date.rs:30`.
- `Timestamp` has `new`, `new_with_tz(unit, tz, nullability)`, and
  `new_with_options(TimestampOptions, nullability)` (`.../datetime/timestamp.rs:33`, `:38`, `:54`). Only the
  no-timezone form is exercised here.
- `TimeUnit` = `Nanoseconds Microseconds Milliseconds Seconds Days`
  (`.../datetime/unit.rs:17`).

**Nullable extension columns:** build the storage dtype nullable
(`Timestamp::new(unit, Nullability::Nullable)`) and use
`Scalar::null(DType::Extension(...))` for the null rows. Verified.

### 1.3 What does NOT work through the generic builder

Read straight off `.../builders/mod.rs:462-465`:

- `DType::Union(..)` → `todo!("TODO(connor)[Union]: unimplemented")`
- `DType::Variant(_)` → `unimplemented!()`

`DType::Map` **does** have a builder (`MapBuilder<u64, u64>`), but I did not exercise it.
For Union/Variant the array constructors (`UnionArray`, `VariantArray`) exist in
`vortex-array-0.86.1/src/arrays/`, so a corpus entry is probably reachable by building the array
directly rather than through a builder — **I did not verify this**.

### 1.4 Canonical encodings you get, and how to get the other ones

The builder path always produces the *canonical* encoding for the dtype. Notably Utf8/Binary
canonicalize to **`vortex.varbinview`**, not `vortex.varbin`
(`.../builders/mod.rs:424-433`), and List canonicalizes to `ListViewBuilder<u64,u64>` →
`vortex.listview`. To emit `vortex.varbin` / `vortex.list` deliberately, construct
`VarBinArray` / `ListArray` directly from `vortex::array::arrays` and write them verbatim (§3.5).

---

## 2. Writing a `.vortex` file to disk

`lib.rs`'s `ByteBufferMut` example is only one sink. `VortexWrite` is implemented for `Vec<u8>`,
`ByteBufferMut`, `std::io::Cursor<T>` (`vortex-io-0.86.1/src/write.rs:28-72`), and — the one you
want — `vortex_io::std_file::FileWrite` (`vortex-io-0.86.1/src/std_file/write.rs:25`).

```rust
use vortex::array::stream::ArrayStreamExt;   // to_array_stream
use vortex::file::WriteOptionsSessionExt;
use vortex::io::session::RuntimeSessionExt;
use vortex::io::std_file::FileWrite;

async fn write_vortex_file(
    session: &VortexSession,
    path: &std::path::Path,
    array: ArrayRef,
) -> VortexResult<u64> {
    let sink = FileWrite::create(path, session.handle()).await?;
    let summary = session
        .write_options()                       // VortexWriteOptions
        .write(sink, array.to_array_stream())  // vortex-file-0.86.1/src/writer.rs:223
        .await?;
    Ok(summary.size())
}
```

`WriteSummary` (`.../writer.rs:708`) gives `footer()`, `size()`, `row_count()`, and
`compressed_column_sizes()` — useful for corpus metadata sidecars without reopening the file.

### 2.1 Streams of chunks

`ArrayRef::to_array_stream()` yields **one stream item per chunk** when the array is a
`ChunkedArray`, and a single item otherwise (`vortex-array-0.86.1/src/iter.rs:97`,
`.../stream/mod.rs:36`). Verified: experiment `12-chunked-stream` writes a 3-chunk
`ChunkedArray` and the file's layout ids come back as `{vortex.chunked, vortex.flat}`.

There is also a push API for incremental writing:
`VortexWriteOptions::writer(sink, dtype) -> Writer`, then `push(chunk).await` / `finish().await`
(`.../writer.rs:366`), and blocking variants via `VortexWriteOptions::blocking(&runtime)`
(`.../writer.rs:208`) which take plain `std::io::Write` + `ArrayIterator`.

### 2.2 Other write knobs on `VortexWriteOptions`

| method | effect | source |
| --- | --- | --- |
| `.exclude_dtype()` | omits the DType flatbuffer; readers must supply it via `with_dtype` | `writer.rs:152` |
| `.with_file_statistics(vec![])` | drops the file-level statistics flatbuffer entirely | `writer.rs:160` |
| `.with_metadata_segment(key, bytes)` | user metadata segment; ≤16 segments, keys ≤64 bytes | `writer.rs:167`, `footer/mod.rs:44` |
| `.disable_editions()` | turns off all edition enforcement | `writer.rs:134` |
| `.with_strategy(Arc<dyn LayoutStrategy>)` | replaces the whole layout pipeline | `writer.rs:123` |

---

## 3. Controlling the write

### 3.0 The one gotcha that governs everything else

`VortexWriteOptions::write_internal` (`vortex-file-0.86.1/src/writer.rs:236-260`) builds the
default strategy **only when you did not call `with_strategy`**, and when it does, it applies
`BtrBlocksCompressorBuilder::default().retain_allowed_encodings(&allowed_array_encodings)`
derived from the enabled editions. **The moment you pass your own strategy you lose that
automatic edition filtering of the compressor** — the final serialization context still rejects
disallowed ids, so you get a write *error* rather than a silently wrong file. Reapply the filter
yourself (§3.4, experiment 09).

Second governing fact, from `vortex-compressor-0.86.1/src/compressor/mod.rs:34`: the
compressor "compresses with the best scheme **and verifies the result is smaller**". Restricting
the scheme set therefore makes an encoding *possible*, never *guaranteed*. Confirmed
experimentally: with only `FoRScheme` enabled and 64 rows, nothing was encoded; the same
configuration on 4096 rows produced `fastlanes.for` + `fastlanes.bitpacked`. **If the corpus
needs an encoding to be present with certainty, use §3.5, not a compressor allowlist.**

### 3.1 Completely uncompressed

```rust
use vortex::compressor::BtrBlocksCompressorBuilder;
use vortex::file::WriteStrategyBuilder;

session.write_options().with_strategy(
    WriteStrategyBuilder::default()
        .with_btrblocks_builder(BtrBlocksCompressorBuilder::empty())  // no schemes at all
        .build(),
)
```

Result (`02-uncompressed`, 4096 rows): `{vortex.bool, vortex.constant, vortex.primitive,
vortex.struct, vortex.varbinview}` — 101 548 bytes vs 9 900 for the default. The residual
`vortex.constant`/`vortex.bool` come from zone-map stats tables and validity, not the data.
Combine with §3.3 for a truly canonical file: experiment `02b-uncompressed-no-zonemaps` yields
exactly `{vortex.primitive, vortex.varbinview}` and no `vortex.zoned` layout.

### 3.2 Encoding allowlist

Two ways, both verified.

**(a) By array encoding id — no extra dependency.** `retain_allowed_encodings` keeps only schemes
*all* of whose `produced_encodings()` are in the set (`vortex-btrblocks-0.86.1/src/builder.rs:207`):

```rust
use vortex::array::ArrayId;
use vortex::utils::aliases::hash_set::HashSet;

let allowed: HashSet<ArrayId> = ["fastlanes.for", "fastlanes.bitpacked"]
    .into_iter().map(ArrayId::new).collect();

WriteStrategyBuilder::default()
    .with_btrblocks_builder(
        BtrBlocksCompressorBuilder::default().retain_allowed_encodings(&allowed),
    )
    .build()
```

Result (`03a`): the int column becomes `fastlanes.for` + `fastlanes.bitpacked`, the string column
stays `vortex.varbinview`.

Mind the "*all* produced encodings" rule when composing an allowlist: a scheme is dropped whole if
any one of its `produced_encodings()` is missing from the set, and some schemes declare secondary
encodings. `BitPackingScheme` declares `fastlanes.bitpacked` plus `vortex.patched`, the latter only
when `VORTEX_EXPERIMENTAL_PATCHED_ARRAY=1` (`.../schemes/integer/bitpacking.rs:43-49`). Experiment
`10-allowlist-bitpacking-with-patches` adds the patch encodings to the set and produces output
identical to `03a`, which is consistent with patches being off by default — I did **not** verify the
drop behaviour with an experiment, only read it off `builder.rs:207-212`.

**(b) By scheme.** `SchemeId` is opaque outside `vortex-compressor`
(`vortex-compressor-0.86.1/src/scheme/mod.rs:47`: `pub(super) name`), and the `vortex` facade
re-exports `Scheme`/`SchemeId` but **not** `vortex_btrblocks::schemes`. So naming a scheme needs a
direct `vortex-btrblocks = "=0.86.1"` dependency (added):

```rust
use vortex_btrblocks::SchemeExt;
use vortex_btrblocks::schemes::integer::{FoRScheme, IntDictScheme};

BtrBlocksCompressorBuilder::empty().with_new_scheme(&FoRScheme)          // additive
BtrBlocksCompressorBuilder::default().exclude_schemes([IntDictScheme.id()])  // subtractive
```

The full scheme list is `vortex_btrblocks::ALL_SCHEMES` (`.../builder.rs:25`): FoR, ZigZag,
BitPacking, Sparse, IntDict, RunEnd, Sequence, IntRLE, Delta, ALP, ALPRD, FloatDict,
NullDominatedSparse, FloatRLE, StringDict, FSST, OnPair, BinaryDict, VarBin, Decimal, Temporal.
`with_compact()` adds Zstd (string/binary) and Pco (int/float); `only_cuda_compatible()` is a
preset.

### 3.3 Zone maps and file statistics on/off

Zone maps are **not** a flag. `WriteStrategyBuilder::build()` hard-wires a `ZonedStrategy` into
the pipeline (`vortex-file-0.86.1/src/strategy.rs:231`). To turn them off you assemble the
pipeline yourself and leave `ZonedStrategy` out:

```rust
use vortex::layout::LayoutStrategy;
use vortex::layout::layouts::chunked::writer::ChunkedLayoutStrategy;
use vortex::layout::layouts::collect::CollectStrategy;
use vortex::layout::layouts::compressed::CompressingStrategy;
use vortex::layout::layouts::flat::writer::FlatLayoutStrategy;
use vortex::layout::layouts::table::TableStrategy;

fn no_zonemap_strategy() -> Arc<dyn LayoutStrategy> {
    let flat: Arc<dyn LayoutStrategy> = Arc::new(FlatLayoutStrategy::default());
    let chunked = ChunkedLayoutStrategy::new(Arc::clone(&flat));
    let compressing =
        CompressingStrategy::new(chunked, BtrBlocksCompressorBuilder::default().build());
    let validity = CollectStrategy::new(Arc::clone(&flat));
    Arc::new(TableStrategy::new(Arc::new(validity), Arc::new(compressing)))
}
```

Verified (`05-no-zonemaps`): layout ids drop from
`{vortex.dict, vortex.flat, vortex.struct, vortex.zoned}` to `{vortex.flat, vortex.struct}`.
File-level statistics are separate and *are* a flag: `.with_file_statistics(vec![])`.

Zone map granularity when they are on: `ZonedLayoutOptions { block_size, aggregate_fns,
concurrency }` (`vortex-layout-0.86.1/src/layouts/zoned/writer.rs:53`). Through
`WriteStrategyBuilder` the block size is tied to `with_row_block_size`; to set `aggregate_fns`
you must build `ZonedStrategy::new(...)` yourself.

### 3.4 Target edition

Editions are session state, not write options. `EnabledEditions` holds at most one edition per
family and enabling replaces the previous one
(`vortex-edition-0.86.1/src/session.rs:56-60`, `enable` at `:74`), so this downgrades the default `core2026.08.3`:

```rust
use vortex::editions::{CORE_2025_05_0, CORE_2026_08_1, ComponentKind, EditionSessionExt};

let session = VortexSession::default().with_handle(handle);
session.enable_edition(CORE_2026_08_1)?;   // errors if the edition is not registered
```

Constants available from `vortex::editions`: `CORE_2025_05_0`, `CORE_2025_06_0`,
`CORE_2025_10_0`, `CORE_2026_08_0`, `CORE_2026_08_1`, `CORE_2026_08_2`, `CORE_2026_08_3`,
`PREVIEW_2026_08_0`. Inspect what an edition permits with
`session.enabled_component_ids(ComponentKind::{Array,Layout,DType,Aggregate})`.

Measured component counts:

| edition | arrays | layouts | dtypes | aggregates |
| --- | --- | --- | --- | --- |
| `core2025.05.0` | 23 | 5 | 4 | **0** |
| `core2026.08.1` | 31 | 6 | 4 | 6 |
| `core2026.08.3` | 34 | 6 | 5 | 6 |

**`core2026.08.1` writes fine with the default strategy.** No work needed.

**`core2025.05.0` cannot use the default strategy at all.** Two hard failures, both observed:

```
default strategy   FAILED: Other error: Aggregate vortex.max not permitted by ctx
no-zonemap strategy FAILED: Other error: Serialized array ID vortex.sequence not permitted by ctx
```

Cause: the `vortex.zoned` layout and its aggregates only join the family at `core2026.08.0`
(`vortex-edition-0.86.1/src/declarations/core/v2026_08.rs:29-34`). The legacy `vortex.stats`
layout *is* a member of `core2025.05.0`, but **the 0.86.1 writer never emits it** — `LegacyStats`
appears only in reader code and reader tests (grep over `vortex-layout-0.86.1/src`). So:

> **A `core2025.05.0` file written by 0.86.1 cannot contain zone maps.**

The recipe that works (experiment `09`, produces a valid 8 168-byte file):

```rust
session.enable_edition(CORE_2025_05_0)?;

// Reapply the edition's encoding filter, since `with_strategy` opts out of the automatic one.
let arrays = session.arrays();                       // vortex::array::session::ArraySessionExt
let allowed: HashSet<ArrayId> = session
    .enabled_component_ids(ComponentKind::Array)
    .iter()
    .filter_map(|serialized_id| arrays.registry().get(serialized_id))
    .map(|plugin| plugin.id())
    .collect();

session.write_options()
    .with_file_statistics(vec![])
    .with_strategy(/* no_zonemap_strategy but with retain_allowed_encodings(&allowed) */)
```

(That id-resolution dance mirrors `vortex-file-0.86.1/src/writer.rs:404-408` exactly: enabled
edition ids are *serialized* ids, `retain_allowed_encodings` wants *plugin* ids.)

**The edition is not recorded in the file.** It is purely a write-time policy; what lands in the
bytes is the array/layout spec lists. There is no `Footer::edition()` — **NOT FOUND**, and I read
all of `vortex-file-0.86.1/src/footer/`. Record the edition in your own sidecar.

### 3.5 Forcing an exact encoding, deterministically

The only reliable way. Build the array in the encoding you want, then use a strategy that neither
repartitions nor canonicalizes nor compresses. (The default pipeline's coalescing step sets
`canonicalize: true` — `vortex-file-0.86.1/src/strategy.rs:203` — which would undo your work.)

```rust
fn verbatim_strategy() -> Arc<dyn LayoutStrategy> {
    let flat: Arc<dyn LayoutStrategy> = Arc::new(FlatLayoutStrategy::default());
    Arc::new(ChunkedLayoutStrategy::new(flat))
}

let codes  = PrimitiveArray::new((0..64u32).map(|i| i % 4).collect::<Buffer<u32>>(), Validity::NonNullable);
let values = VarBinViewArray::from_iter_str(["a", "bb", "ccc", "dddd"]);
let dict   = DictArray::try_new(codes.into_array(), values.into_array())?.into_array();

session.write_options().with_file_statistics(vec![]).with_strategy(verbatim_strategy())
```

Result (`06-forced-dict`): array ids are exactly `{vortex.dict, vortex.primitive,
vortex.varbinview}` and layout ids exactly `{vortex.flat}`. The same array through the *default*
strategy (`06b`) comes back with array ids `{vortex.bool, vortex.constant, vortex.fsst,
vortex.primitive, vortex.struct}` and layout ids `{vortex.dict, vortex.flat, vortex.zoned}` — the
array-level dict was canonicalized away and the writer re-derived its own dictionary, as a *dict
layout* this time. Nothing about the input encoding survived. This contrast is the whole argument
for using `verbatim_strategy` in the corpus generator.

Note `verbatim_strategy` writes a non-struct root directly. For a struct root wrap it in
`TableStrategy::new(validity, leaf)`, and use `TableStrategy::with_field_writers` /
`WriteStrategyBuilder::with_field_writer(field_path, strategy)`
(`vortex-file-0.86.1/src/strategy.rs:117`) to force a different encoding per column in one file.

### 3.6 Chunk / row-block size

```rust
WriteStrategyBuilder::default()
    .with_row_block_size(1024)                 // default 8192; strategy.rs:88
    .with_data_block_target_bytes(None)        // default Some(1 MiB); strategy.rs:97
    .build()
```

Verified (`04-row-block-1024`, 4096 rows): `vortex.chunked` appears in the layout ids where the
default single 8192-row block produced none.

`with_data_block_target_bytes(None)` is **required**, not decorative: experiment
`04b-row-block-1024-with-default-coalescing` sets only `with_row_block_size(1024)` and produces
no `vortex.chunked` at all (9 900 bytes, byte-identical in shape to the default) — the 1 MiB
coalescing target merges the four 1024-row blocks straight back into one.

### 3.7 Environment-variable format switches (corpus-relevant variants)

Found by grepping `env::var(` across the vortex crates. Each is a `LazyLock`, so it is read once
per process — set it before the first Vortex call, and use a separate process per variant.

| var | effect | source |
| --- | --- | --- |
| `FLAT_LAYOUT_INLINE_ARRAY_NODE=1` | stores the array flatbuffer inline in flat-layout metadata instead of in the segment | `vortex-layout-0.86.1/src/layouts/flat/mod.rs:37` |
| `VORTEX_EXPERIMENTAL_PATCHED_ARRAY=1` | enables the `vortex.patched` encoding | `vortex-array-0.86.1/src/arrays/patched/mod.rs:117` |
| `VORTEX_EXPERIMENTAL_LIST_LAYOUT=1` | enables `ListLayoutStrategy` (also `WriteStrategyBuilder::with_list_layout()`) | `vortex-layout-0.86.1/src/layouts/table.rs:43` |
| `VORTEX_MAX_LAYOUT_TABLES`, `VORTEX_MAX_LAYOUT_DEPTH` | flatbuffer verifier limits on read | `vortex-layout-0.86.1/src/flatbuffers.rs:33` |

I did not run any of these.

---

## 4. Reading a file back

```rust
use vortex::file::OpenOptionsSessionExt;

let file = session.open_options().open_path(path).await?;   // vortex-file-0.86.1/src/open.rs:229
file.dtype();      // &DType
file.row_count();  // u64
file.footer();     // &Footer
```

`open_path` needs the session handle (it opens a `FileReadAt` on it). Other entry points:
`open_buffer(bytes)` (synchronous, in-memory, `open.rs:243`), `open(Arc<dyn VortexReadAt>)`,
`open_read(reader)`. `VortexOpenOptions::with_dtype(dtype)` is **required** for files written with
`exclude_dtype()`; `with_footer`, `with_file_size`, `with_segment_cache` reduce open-time IO.

### 4.1 Per-column values

```rust
use vortex::array::VortexSessionExecute;
use vortex::array::arrays::StructArray;
use vortex::array::arrays::struct_::StructArrayExt;   // names(), iter_unmasked_fields()

let array = file.scan()?.into_array_stream()?.read_all().await?;
let mut ctx = session.create_execution_ctx();
let strct = array.execute::<StructArray>(&mut ctx)?;

for (name, field) in strct.names().iter().zip(strct.iter_unmasked_fields()) {
    for i in 0..field.len() {
        let scalar = field.execute_scalar(i, &mut ctx)?;   // array/erased.rs:276
        // scalar.to_string() for a human form, scalar.value() -> Option<&ScalarValue> for raw
    }
}
```

`execute_scalar` returns `Scalar::null(dtype)` for invalid rows automatically. For expected-value
sidecars you probably want the **raw** value rather than `Display`, because `Display` renders
extension types as civil dates (`2022-01-08`, `12:00:00`, `2023-11-14T22:13:20Z`) and decimals as
`decimal64(125, precision=10, scale=2)`:

- `Scalar::value() -> Option<&ScalarValue>` (`scalar/scalar_impl.rs:177`) — the untyped value.
- Typed views via `as_bool/as_primitive/as_decimal/as_utf8/as_binary/as_list/as_extension`
  (`scalar/downcast.rs`).
- `scalar.as_extension().to_storage_scalar()`
  (`scalar/typed_view/extension/mod.rs:77`) — the underlying `i32`/`i64` for date/time/timestamp.
  This is the one the C# side will compare against.

`ScanBuilder` (from `file.scan()?`) also takes `.with_projection(expr)` and `.with_filter(expr)`
using `vortex::array::expr::{select, get_item, gt, lit, root}`; expressions must be
`.optimize_recursive(file.dtype())?.bind(file.dtype())?` first (see `vortex-0.86.1/src/lib.rs`
module docs).

### 4.2 Layout encoding ids actually in the file

```rust
let mut ids = BTreeSet::new();
for layout in file.footer().layout().depth_first_traversal() {   // vortex-layout-0.86.1/src/layout.rs:471
    ids.insert(layout?.encoding_id().to_string());
}
```

`probe.vortex` → `{vortex.flat, vortex.struct, vortex.zoned}`.

### 4.3 Array encoding ids actually in the file — the coverage gate

**Do not use the footer's array-spec list for this.** `Footer` interns *every id the enabled
editions permit*, not the ids used: `new_array_context` pre-populates the context with
`session.enabled_component_ids(ComponentKind::Array)` for byte determinism
(`vortex-file-0.86.1/src/writer.rs:387-417`). Measured: `probe.vortex` declares **34** array ids —
exactly the count of `core2026.08.3`'s array members — while only 15 are actually used.

The honest answer walks the serialized array trees. Each `vortex.flat` leaf carries the segment id
and the `ReadContext` that maps the on-wire `u16` back to an id, and `SerializedArray` exposes the
tree **without decoding it** (`vortex-array-0.86.1/src/serde.rs:460`, `:473`, `:480`):

```rust
use vortex::array::serde::SerializedArray;
use vortex::layout::layouts::flat::Flat;

let source = file.segment_source();
for layout in file.footer().layout().depth_first_traversal() {
    let layout = layout?;
    let Some(flat) = layout.as_opt::<Flat>() else { continue };
    let ctx = flat.array_ctx().clone();                    // layouts/flat/mod.rs:160
    let serialized = match flat.array_tree() {             // Some(..) iff FLAT_LAYOUT_INLINE_ARRAY_NODE
        Some(tree) => SerializedArray::from_array_tree(tree.clone())?,
        None => SerializedArray::try_from(source.request(flat.segment_id()).await?)?,
    };
    // recurse: serialized.encoding_id() -> u16, ctx.resolve(u16) -> Id,
    //          serialized.nchildren() / serialized.child(i)
}
```

`src/main.rs::array_encoding_ids` and `examples/api_experiments.rs::array_ids` are the working
implementations. This covers zone-map and dictionary sub-layouts too, because they are flat leaves
in the same tree.

`probe.vortex` → `{vortex.bool, vortex.constant, vortex.datetimeparts, vortex.decimal,
vortex.decimal_byte_parts, vortex.ext, vortex.fixed_size_list, vortex.fsst, vortex.list,
vortex.masked, vortex.null, vortex.primitive, vortex.sequence, vortex.struct, vortex.varbin}`.

Caveat: this enumerates only ids reachable from flat leaves. Non-flat layouts that hold their own
segments (there are none in the files produced here) would need `layout.segment_ids()` handled
separately.

---

## 5. Tree / debug printers

**Layout tree — per file, no IO:**

```rust
println!("{}", file.footer().layout().display_tree());              // layout.rs:495
println!("{}", file.footer().layout().display_tree_verbose(true));  // + metadata size + row counts
```

Verbose output for `probe.vortex` (excerpt):

```
vortex.struct, dtype: {null=null, bool=bool?, …}, children: 14, rows: 16
├── null: vortex.zoned, dtype: null, children: 2, metadata: 25 bytes, rows: 16
│   ├── data: vortex.flat, dtype: null, rows: 16, segment: 0
│   └── zones: vortex.flat, dtype: {vortex.null_count()=u64?}, rows: 1, segment: 14
…
```

There is also `layout.display_tree_with_segments(segment_source).await?`
(`layout.rs:505`) which fetches each segment to print per-buffer byte sizes — the IO cost is
documented as significant, but for a corpus generator it is exactly the right level of detail.

**Array tree — per physical array:**

```rust
println!("{}", array.tree_display());          // vortex-array-0.86.1/src/display/tree_display.rs
println!("{}", array.display_as(DisplayOptions::MetadataOnly));  // display/mod.rs
```

`tree_display()` shows encoding, nbytes, stats, metadata and per-buffer sizes; a builder form
`array.tree_display_builder().with(EncodingSummaryExtractor).with(NbytesExtractor)` picks
extractors. Verified output for a stored dict array:

```
root: vortex.dict(utf8, len=64) nbytes=320 B (100.00%)
  metadata: all_values_referenced: false
  codes: vortex.primitive(u32, len=64) nbytes=256 B (80.00%)
    metadata: ptype: u32
    buffer: values host 256 B (align=4) (100.00%)
  values: vortex.varbinview(utf8, len=4) nbytes=64 B (20.00%)
    metadata: 
    buffer: views host 64 B (align=16) (100.00%)
```

To get that array you decode a flat leaf's segment:
`SerializedArray::decode(&dtype, row_count, flat.array_ctx(), &session)` (`serde.rs:357`).
`vortex::array::expr::Expression::display_tree()` exists too, for scan expressions.

A `.table_display()` for tabular value dumps sits behind the `vortex/pretty` feature
(`vortex-array/table-display`) — not enabled here, not tried.

---

## 6. Open questions — resolved by the corpus generator

The reconnaissance pass left seven questions open. All seven were settled while building
`src/` (the generator); the answers are reproduced here so this document stays the single place
to look, and each is also recorded in `manifest.json`'s `skipped` list where it is a corpus gap.

1. **Writing a `vortex.stats` (legacy zone map) layout — NOT POSSIBLE.** Confirmed: no writer path
   constructs it, and `ZonedMetadata::metadata` *panics outright* on the legacy schema
   (`vortex-layout-0.86.1/src/layouts/zoned/mod.rs:108`,
   `"Cannot serialize legacy stats schema as vortex.zoned"`). The corpus therefore covers 5 of the
   6 claimed layouts; a `vortex.stats` file must come from an older Vortex release.

2. **`DType::Union` — NOT POSSIBLE, and correctly so.** `vortex.union` is a member of no core
   edition (grep the 34 array ids in `spec/editions/core2026.08.3.toml`), so a conformant writer
   cannot emit one whatever the builder does.
   **`DType::Variant` — POSSIBLE.** `VariantArray::try_new(core_storage, None)` over
   `ConstantArray::new(Scalar::variant(..), len)` writes and reads back fine; the id lands in the
   file as `vortex.variant`. The generic builder's `unimplemented!()` is only about the *builder*.

3. **`DType::Map` — POSSIBLE through the generic builder.** `builder_with_capacity_in` dispatches
   `DType::Map` to `MapBuilder<u64, u64>` and `Scalar::try_map` builds the values. The corpus has
   `map(utf8, i64?)` across the whole row-count sweep, and `vortex.map` appears in the bytes.

4. **Recovering the target edition from a written file — still NOT POSSIBLE.** Re-confirmed
   against `vortex-file-0.86.1/src/footer/`. The corpus records it per file in `manifest.json`'s
   `target_edition`, which is the sidecar the note anticipated.

5. **`vortex.uuid` — POSSIBLE.**
   `ExtDType::<Uuid>::try_new(UuidMetadata::default(), FixedSizeList(u8, 16, n))` builds the dtype
   and `Scalar::extension_ref` the values. No dependency on the `uuid` crate is needed as long as
   the metadata's version field stays `None`.

6. **Timezone-carrying timestamps — POSSIBLE and exercised.**
   `Timestamp::new_with_tz(TimeUnit::Nanoseconds, Some("Europe/Paris".into()), n)` writes, and the
   tz survives in the extension metadata (the corpus dumps `metadata_hex` per file).
   One trap the signatures do not show: **extension storage magnitudes are validated per type**. A
   `vortex.time` value must stay inside one day *in its own unit*, so a timestamp-sized number of
   microseconds panics with `"Invalid time scalar: adding duration to time overflowed"`. Use
   `ExtDTypeRef::metadata_opt::<AnyTemporal>()` to branch on the actual unit.

7. **§4.3's array-encoding enumeration.** Still true for every file this generator produces, now
   including the `FLAT_LAYOUT_INLINE_ARRAY_NODE` variant. The correct leaf reader is
   `SerializedArray::from_flatbuffer_and_segment(tree, segment)`, **not** `from_array_tree(tree)`:
   the latter returns a buffer-less tree, which is fine for walking ids and fails the moment
   anything is decoded (`"buffer indices 0..1 out of range for 0 buffers"`).

8. **The `retain_allowed_encodings` all-or-nothing rule — NOW DEMONSTRATED.** Experiment 10 was
   inconclusive because patches were off. With `VORTEX_EXPERIMENTAL_PATCHED_ARRAY=1`,
   `BitPackingScheme` really does declare `vortex.patched`, that id is in no core edition, and the
   whole scheme is dropped: `containers/experimental_patched_array` writes its bit-packable column
   completely uncompressed (21 352 bytes, array ids `{vortex.constant, vortex.primitive,
   vortex.struct}`), while the same data with `disable_editions()` gives 11 928 bytes and
   `{fastlanes.bitpacked, vortex.patched, ...}`. The rule at `builder.rs:207-212` is real.

9. **The env-var switches — all three run.** Each needs its own process (they are `LazyLock`s), so
   the generator re-execs itself. `VORTEX_EXPERIMENTAL_LIST_LAYOUT=1` additionally needs
   `disable_editions()`: the `vortex.list` *layout* id belongs to no core edition either.

### Two writer limitations found while building the corpus

* **A nullable top-level struct cannot be written at all.** `write_internal` always calls
  `accumulate_stats`, whose `FileStatsAccumulator::new` panics on one before looking at the
  requested statistics (`vortex-layout-0.86.1/src/layouts/file_stats.rs:461`).
  `with_file_statistics(vec![])` does not help. Nullable structs below the root are fine.
* **Segment- and buffer-level compression are reserved but never written.**
  `PostscriptSegment::write_flatbuffer` hard-codes `_compression: None`
  (`footer/postscript.rs:251`) and `FileLayout` hard-codes `compression_specs: None`
  (`footer/file_layout.rs:75`); no public API sets either. So "a file with Zstd-compressed
  segments" and "a file with an LZ4-compressed buffer" are unreachable from 0.86.1 — the corpus
  ships `vortex.zstd` *arrays* inside ordinary segments instead, and says so.
* **The postscript cannot approach its 65527-byte ceiling.** It carries four segment locators plus
  at most 16 metadata entries with 64-byte keys (`footer/mod.rs:44,51`). The largest this writer
  produces is **1 912 bytes** (`containers/postscript_max_metadata`), two orders of magnitude
  short.

## Appendix — recorded output of `cargo run --release --example api_experiments`

```
-- 01-default
   9900 bytes, 4096 rows
     layout ids: {"vortex.dict", "vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.fsst", "vortex.sequence", "vortex.struct"}
-- 02-uncompressed
   101548 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"vortex.bool", "vortex.constant", "vortex.primitive", "vortex.struct", "vortex.varbinview"}
-- 02b-uncompressed-no-zonemaps
   100256 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct"}
     array  ids: {"vortex.primitive", "vortex.varbinview"}
-- 03a-allowlist-for-bitpacked
   73980 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.struct", "vortex.varbinview"}
-- 03b-only-for-scheme
   73980 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.struct", "vortex.varbinview"}
-- 03c-no-dict-schemes
   10676 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.onpair", "vortex.primitive", "vortex.sequence", "vortex.struct"}
-- 04-row-block-1024
   11492 bytes, 4096 rows
     layout ids: {"vortex.chunked", "vortex.dict", "vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.fsst", "vortex.sequence", "vortex.struct"}
-- 04b-row-block-1024-with-default-coalescing
   9900 bytes, 4096 rows
     layout ids: {"vortex.dict", "vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.fsst", "vortex.sequence", "vortex.struct"}
-- 05-no-zonemaps
   8520 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.constant", "vortex.dict", "vortex.fsst", "vortex.sequence"}
-- 06-forced-dict
   1948 bytes, 64 rows
     layout ids: {"vortex.flat"}
     array  ids: {"vortex.dict", "vortex.primitive", "vortex.varbinview"}
-- 06b-forced-dict-default-strategy
   3008 bytes, 64 rows
     layout ids: {"vortex.dict", "vortex.flat", "vortex.zoned"}
     array  ids: {"vortex.bool", "vortex.constant", "vortex.fsst", "vortex.primitive", "vortex.struct"}
-- edition core2025.05.0: 23 array ids, 5 layout ids, 4 dtype ids, 0 aggregates
   default strategy FAILED: Other error: Aggregate vortex.max not permitted by ctx
   no-zonemap strategy FAILED: Other error: Serialized array ID vortex.sequence not permitted by ctx
-- edition core2026.08.1: 31 array ids, 6 layout ids, 4 dtype ids, 6 aggregates
   default strategy: 9804 bytes, 4096 rows
     layout ids: {"vortex.dict", "vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.fsst", "vortex.sequence", "vortex.struct"}
   no-zonemap strategy: 8416 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.constant", "vortex.dict", "vortex.fsst", "vortex.sequence"}
-- edition core2026.08.3: 34 array ids, 6 layout ids, 5 dtype ids, 6 aggregates
   default strategy: 9900 bytes, 4096 rows
     layout ids: {"vortex.dict", "vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.fsst", "vortex.sequence", "vortex.struct"}
   no-zonemap strategy: 8520 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.constant", "vortex.dict", "vortex.fsst", "vortex.sequence"}
-- 09-edition-2025-05-0-working
   8168 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.constant", "vortex.dict", "vortex.fsst", "vortex.primitive"}
-- 10-allowlist-bitpacking-with-patches
   73980 bytes, 4096 rows
     layout ids: {"vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.struct", "vortex.varbinview"}
-- 08-editions-disabled
   10092 bytes, 4096 rows
     layout ids: {"vortex.dict", "vortex.flat", "vortex.struct", "vortex.zoned"}
     array  ids: {"fastlanes.bitpacked", "fastlanes.for", "vortex.bool", "vortex.constant", "vortex.fsst", "vortex.sequence", "vortex.struct"}
-- 12-chunked-stream
   4288 bytes, 300 rows
     layout ids: {"vortex.chunked", "vortex.flat"}
     array  ids: {"vortex.primitive"}
-- 11-array-tree-display (06-forced-dict.vortex, first flat leaf)
root: vortex.dict(utf8, len=64) nbytes=320 B (100.00%)
  metadata: all_values_referenced: false
  codes: vortex.primitive(u32, len=64) nbytes=256 B (80.00%)
    metadata: ptype: u32
    buffer: values host 256 B (align=4) (100.00%)
  values: vortex.varbinview(utf8, len=4) nbytes=64 B (20.00%)
    metadata: 
    buffer: views host 64 B (align=16) (100.00%)

```
