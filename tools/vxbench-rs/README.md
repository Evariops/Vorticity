# vxbench-rs

Vortex's Rust reader behind a C ABI, so that the comparison of
[docs/design/05-benchmarks.md](../../docs/design/05-benchmarks.md) can measure **both
implementations in one process**, on the same bytes, with the same clock and the same page-cache
state. Two runs of two binaries can differ by more than the thing being measured, which is how a
1.4× ratio becomes unreadable.

## Two shapes, one body of code

The crate builds a **cdylib** and a **binary**, from the same functions.

The cdylib is what BenchmarkDotNet loads, for the per-axis ratios above. The binary is what the
published report runs, because the figures it reports — wall time, peak resident memory, processor
time — belong to a whole process and cannot be measured from inside a loop that shares one. Both
call the same entry points, so the two ways of measuring Rust cannot drift apart.

```
cargo build --release
target/release/vxbench scan <file.vortex>
target/release/vxbench project <file.vortex> <field>
target/release/vxbench filter <file.vortex> <i64 field> <lo> <width>
target/release/vxbench take <file.vortex> <count> <stride>
target/release/vxbench write <file.vortex>
target/release/vxbench open <file.vortex>
target/release/vxbench rewrite <file.vortex> <out.vortex>
```

Each prints `rows=<n>` and exits 0, or a reason on standard error and exits 1. `filter` wants an
`i64` field: the predicate's literal is one, and an `i32` column is refused rather than coerced.
`--threads <n>` or `--threads all` runs any of them on a multi-threaded runtime of that many
workers, or of one per processor; without it they run on the single-threaded runtime.
`--repeat <n>` runs the scenario n times in the process and prints `round=<i> rows=<n>
work_us=<time>` for each before the last line, as our Native AOT runner does: the first round is
the cold one, the others what a process that stays up pays, which is how the report's
per-encoding table times a decoder once warm. `rewrite` is not a timing axis: it writes the same
rows as the reference's writer makes them, for the read scenarios to run on a file of its own.

## Why this is not `vortex-ffi`

`vortex-ffi` would be the obvious cdylib. Two reasons it is not used:

* it is `publish = false` upstream, so it would mean a second git dependency;
* it is a general-purpose C API with its own object model, whose per-call overhead would land
  inside the measurement.

This shim is built against the same crates.io pin the corpus was generated with
(`vortex = "=0.86.1"`), exposes one entry point per benchmark axis, and returns a row count. No
handles, no allocation across the boundary, no object lifetime to manage — there is nothing in the
surface to measure except the scan. The empty-call floor is 2.5 ns.

## Fairness, item by item

* **Built as upstream's benchmarks are built.** mimalloc as the global allocator, which upstream's
  benchmarks all run with and its README recommends; `-C target-cpu=native -C
  force-frame-pointers=yes` (`.cargo/config.toml`); one codegen unit, no LTO, full debug
  information (`Cargo.toml`), upstream's `release_debug` profile. A reference built otherwise is
  not the one upstream measures. The .NET side is built for the machine too: the Native AOT runner
  has `IlcInstructionSet=native`.
* **One core, or all of them, on both sides.** Without a thread count every call runs on
  `vortex::io::runtime::single`, all the work on the calling thread, against our reader at one lane.
  `vxbench_set_threads(n)`, or `--threads`, runs them on a multi-threaded Tokio runtime of `n`
  workers under `with_tokio`, which is how upstream's benchmarks use every core, against our reader
  at `n` lanes. The count is the same on both sides; a ratio between a reader held to one core and
  one free to use them all measures a threading model.
* **Decoded where the work is split.** Each split is decoded on its own task, through
  `ScanBuilder::map`, as upstream's Arrow conversion does. Decoded in the loop that drains the stream
  instead, the splits would be read on every core and decoded on one.
* **Every value decoded, to the form our reader delivers.** Each entry point that reads values takes
  every split to `plain`: `execute::<RecursiveCanonical>`, because `execute::<Canonical>` stops at
  the first canonical kind, a struct among them, which on a table is before a single column has been
  decoded; and a constant column kept as one value and a length, through upstream's own `Columnar`,
  because our reader keeps it so and expands it only when its values are asked for.
  `RecursiveCanonical` alone expanded it, a cost on this side only. `--ffi-check` asserts that both
  readers return the same rows and the same checksum of every decoded value, in file order, on seven
  corpus files, and the per-encoding gates check the rows of every file before timing it.
* **The file is mapped, anew at every call, on both sides.** `open_buffer` over a `memmap2` mapping,
  which reads the file where it lies, as our reader reads every file it opens from a path; the bench
  opens ours through a session that keeps no mapping once a file is closed
  (`MappedFileCacheCount = 0`). Upstream's `open_path` reads every segment it needs into buffers of
  its own instead: measured against our mapping, a column stored in its plain form cost this side a
  copy and ours a view of pages nothing touched, and the decoding tables printed that I/O as
  decoding, up to twelve times on `table_wide`. Neither side keeps a segment cache.
* **On one core, the faster of the reference's two splits.** Upstream's default cuts a chunk of more
  than 100 000 rows into splits of 100 000, and its flat reader decodes the whole chunk again for
  each: on the per-encoding corpus, files of one chunk of a million rows, every string is validated
  ten times. One split per chunk (`vxbench_set_split`, `--split per-chunk`) decodes a chunk once but
  builds a chunk of smaller arrays in one piece, three to six times slower on the `chunked_` files.
  Neither wins everywhere, so on one core the harnesses time both and keep the faster, file by file;
  on all cores the default stays, its splits being how the reference spreads a chunk over the cores.
* **The writer is given the rows as our reader hands them to our writer**, `plain` again. Given the
  file's own encodings, the reference's writer re-encodes from them, which is not the work our side
  does, and refuses a numeric column stored as zstd, which it cannot append to a builder.
* **The writer's bytes are counted and dropped**, by a `DiscardSink` of this crate, as the .NET side's
  `DiscardSink` drops ours. It was a `Vec<u8>` that grew to hold the whole file. `vxbench_write_bytes`
  says how many bytes a write produced, so the page can print each writer's size beside its time.
* **Each side is asked the same question.** A count is answered by `vxbench_count_filtered`, a
  projection of no column, as our `CountAsync` produces no batch; a key-ordered read of a band by the
  rows of that band, `vxbench_scan_filtered`, the reference having no key order and no index.
* **The session is built once.** `VortexSession::default()` registers every edition and initializes
  the arrow and parquet-variant integrations: about 90 µs, which nothing on the .NET side pays per
  open, since `EncodingRegistry` is static. Built per call, it would read as Rust being slow to open
  a file.
* **Panics are caught** at the boundary: unwinding across a C ABI is undefined behaviour. Every
  entry point returns a count, or a negative status.

## Building and running

Not built by CI: it is a five-minute Rust build. The target CPU is the build machine's, so the
library is built on the machine that measures.

```sh
cd tools/vxbench-rs && cargo build --release

# Both readers must agree on what they read before any ratio means anything:
dotnet run -c Release --project bench/Vorticity.Benchmarks -- --ffi-check

# Then the ratios:
dotnet run -c Release --project bench/Vorticity.Benchmarks -- --ratio-check
```

`VORTICITY_VXBENCH` points the loader at a prebuilt library; otherwise the resolver walks up from
the benchmark assembly to `tools/vxbench-rs/target/release/`.

The in-process gates record the fingerprint of the build their references were set under, and refuse
to gate under another. Keep the previous `libvxbench.dylib` when you rebuild, and carry the references
over with `--rebase-from` (see `bench/README.md`).

The comparison benchmarks **fail loudly** when the library is missing, naming this command. Skipping
quietly would produce a run that reports no ratio and looks exactly like one that reported a good
one.
