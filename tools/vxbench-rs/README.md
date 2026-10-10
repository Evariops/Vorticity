# vxbench-rs

Vortex's Rust reader behind a C ABI, so that the comparison described in
[docs/design/05-benchmarks.md](../../docs/design/05-benchmarks.md) can measure both implementations
in one process, on the same bytes, with the same clock and the same page-cache state. Two runs of two
binaries can differ by more than the thing being measured, which is how a 1.4× ratio becomes
unreadable.

## Two shapes, one body of code

The crate builds a cdylib and a binary from the same functions.

The cdylib is what BenchmarkDotNet loads, for the per-axis ratios. The binary is what the published
report runs, because the figures it reports (wall time, peak resident memory, processor time) belong
to a whole process and cannot be measured from inside a loop that shares one. Both call the same entry
points, so the two ways of measuring Rust cannot drift apart.

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

Each prints `rows=<n>` and exits 0, or prints a reason on standard error and exits 1. `filter` wants
an `i64` field, because the predicate's literal is one, and an `i32` column is refused rather than
coerced. `--threads <n>` or `--threads all` runs any of them on a multi-threaded runtime of that many
workers, or of one per processor, and without it they run on the single-threaded runtime.
`--repeat <n>` runs the scenario n times in the process and prints `round=<i> rows=<n>
work_us=<time>` for each before the last line, as our Native AOT runner does. The first round is the
cold one, and the others are what a process that stays up pays, which is how the report's
per-encoding table times a decoder once warm. `rewrite` is not a timing axis. It writes the same rows
the way the reference's writer makes them, so the read scenarios can run on a file of its own.

## Why this is not `vortex-ffi`

`vortex-ffi` would be the obvious cdylib, but it is not used, for two reasons:

* it is `publish = false` upstream, so it would mean a second git dependency
* it is a general-purpose C API with its own object model, whose per-call overhead would land inside
  the measurement

This shim is built against the same crates.io pin the corpus was generated with
(`vortex = "=0.86.1"`), exposes one entry point per benchmark axis, and returns a row count. There are
no handles, no allocation across the boundary and no object lifetime to manage, so there is nothing in
the interface to measure except the scan. The empty-call floor is 2.5 ns.

## Fairness, item by item

* It is built the way upstream's benchmarks are built: mimalloc as the global allocator, which
  upstream's benchmarks all run with and its README recommends, `-C target-cpu=native -C
  force-frame-pointers=yes` (`.cargo/config.toml`), one codegen unit, no LTO and full debug
  information (`Cargo.toml`), which is upstream's `release_debug` profile. A reference built otherwise
  is not the one upstream measures. The .NET side is built for the machine too, since the Native AOT
  runner has `IlcInstructionSet=native`.
* Both sides use one core, or all of them. Without a thread count every call runs on
  `vortex::io::runtime::single`, all the work on the calling thread, against our reader at one lane.
  `vxbench_set_threads(n)`, or `--threads`, runs them on a multi-threaded Tokio runtime of `n` workers
  under `with_tokio`, which is how upstream's benchmarks use every core, against our reader at `n`
  lanes. The count is the same on both sides, since a ratio between a reader held to one core and one
  free to use them all measures a threading model.
* Each split is decoded on its own task, through `ScanBuilder::map`, as upstream's Arrow conversion
  does. If the splits were decoded in the loop that drains the stream instead, they would be read on
  every core and decoded on one.
* Every value is decoded to the form our reader delivers. Each entry point that reads values takes
  every split to plain form with `execute::<RecursiveCanonical>`, because `execute::<Canonical>` stops
  at the first canonical kind, a struct among them, which on a table comes before a single column has
  been decoded. A constant column is kept as one value and a length, through upstream's own
  `Columnar`, because our reader keeps it that way and only expands it when its values are asked for,
  whereas `RecursiveCanonical` alone would have expanded it, a cost on this side only. `--ffi-check`
  asserts that both readers return the same rows and the same checksum of every decoded value, in
  file order, on seven corpus files, and the per-encoding gates check the rows of every file before
  timing it.
* The file is mapped anew at every call, on both sides, with `open_buffer` over a `memmap2` mapping,
  which reads the file where it lies, as our reader reads every file it opens from a path. The bench
  opens ours through a session that keeps no mapping once a file is closed
  (`MappedFileCacheCount = 0`). Upstream's `open_path` would instead read every segment it needs into
  buffers of its own. Measured against our mapping, a column stored in plain form then cost this side
  a copy and ours a view of pages nothing touched, and the decoding tables printed that I/O as
  decoding, up to twelve times on `table_wide`. Neither side keeps a segment cache.
* On one core, the reference gets the faster of its two ways to split. Upstream's default cuts a
  chunk of more than 100 000 rows into splits of 100 000, and its flat reader decodes the whole chunk
  again for each one, so on the per-encoding corpus (files of one chunk of a million rows) every
  string is validated ten times. One split per chunk (`vxbench_set_split`, `--split per-chunk`)
  decodes a chunk once but builds a chunk of smaller arrays in one piece, three to six times slower on
  the `chunked_` files. Neither wins everywhere, so on one core the harnesses time both and keep the
  faster, file by file. On all cores the default stays, since its splits are how the reference spreads
  a chunk over the cores.
* The writer is given the rows as our reader hands them to our writer, in plain form again. Given the
  file's own encodings, the reference's writer would re-encode from them, which is not the work our
  side does, and would refuse a numeric column stored as zstd, which it cannot append to a builder.
* The writer's bytes are counted and dropped by a `DiscardSink` of this crate, as the .NET side's
  `DiscardSink` drops ours. `vxbench_write_bytes` says how many bytes a write produced, so the page
  can print each writer's size next to its time.
* Each side is asked the same question. A count is answered by `vxbench_count_filtered`, a projection
  of no column, as our `CountAsync` produces no batch, and a key-ordered read of a band by the rows of
  that band, `vxbench_scan_filtered`, since the reference has no key order and no index.
* The session is built once. `VortexSession::default()` registers every edition and initializes the
  Arrow and Parquet-variant integrations, which takes about 90 µs that nothing on the .NET side pays
  per open, since `EncodingRegistry` is static. Built per call, it would read as Rust being slow to
  open a file.
* Panics are caught at the boundary, since unwinding across a C ABI is undefined behaviour. Every
  entry point returns a count, or a negative status.

## Building and running

CI does not build it, since it is a five-minute Rust build. The target CPU is the build machine's, so
the library is built on the machine that measures.

```sh
cd tools/vxbench-rs && cargo build --release

# Both readers must agree on what they read before any ratio means anything:
dotnet run -c Release --project bench/Vorticity.Benchmarks -- --ffi-check

# Then the ratios:
dotnet run -c Release --project bench/Vorticity.Benchmarks -- --ratio-check
```

`VORTICITY_VXBENCH` points the loader at a prebuilt library. Otherwise the resolver walks up from the
benchmark assembly to `tools/vxbench-rs/target/release/`.

The in-process gates record the fingerprint of the build their references were set under, and refuse
to gate under another one. Keep the previous `libvxbench.dylib` when you rebuild, and carry the
references over with `--rebase-from` (see [bench/README.md](../../bench/README.md)).

The comparison benchmarks fail loudly when the library is missing, naming this command. Skipping
quietly would produce a run that reports no ratio and looks exactly like one that reported a good one.
