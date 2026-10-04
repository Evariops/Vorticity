# Benchmarks

How Vorticity is measured against Vortex's Rust implementation, and its kernels against their
baselines: what is compared (§1), what a caller sees (§2), the ratios the gates hold in one
process (§3), each decoder (§4), allocations (§5), and the instruments (§6). The figures are on one
page, [the benchmark page](../guide/benchmarks.md), each section written by the instrument that
measures it; [bench/README.md](../../bench/README.md) says how to run each one.

## 1. The comparison

* **Against Rust.** The reference is Vortex's own implementation, the one the format was designed
  around. A .NET Parquet library would measure a difference of format, not of implementation.
* **The same work.** Both sides read the same bytes and must return the same rows, or the run fails.
  Every call opens its file and maps it anew, on both sides, and reads it where it lies: Rust through
  `open_buffer` over a `memmap2` mapping, ours through a session that keeps no mapping once a file is
  closed. Both decode every value they return to its plain form, except a constant column, which both
  keep as one value and a length: our reader's form, and upstream's `Columnar`. A write hands its
  bytes to a sink that counts them and keeps none, on both sides. A count is a count on both sides,
  and a read the reference has no index for is asked of it as the rows it would have to find.
  Neither side keeps a segment cache.
* **Rust's figure is its faster setting.** By default Rust cuts a chunk of more than 100 000 rows
  into splits of 100 000, and its reader decodes the whole chunk again for every split: on the
  per-encoding corpus, files of one chunk of a million rows, that is ten decodes of each, and the
  string-heavy files took ten times as long. Asked for one split per chunk it decodes each once, but
  builds a chunk made of smaller arrays in one piece, three to six times slower on those. Neither
  wins everywhere, so on one core every Rust figure is the faster of the two, measured on the spot,
  file by file and axis by axis; on all cores Rust keeps its default, whose splits spread its work.
* **Each side built for speed.** Vorticity as a Native AOT binary for the machine's instruction
  set. Vortex 0.86.1 built as upstream builds its own benchmarks: mimalloc, `-C target-cpu=native`,
  one codegen unit, no LTO, upstream's `release_debug` profile and the `RUSTFLAGS` of its benchmark
  workflows.
* **The same threads.** Every scenario runs on one core — Rust's single-threaded runtime, our scans
  at one lane — and on all of them: a Tokio worker per processor for Rust, a lane per processor for
  our scans and as many threads for our writer.
* **A speedup on every page**: Rust's time over Vorticity's, so that above 1.00× Vorticity took
  less, and in a kernel's table its baseline's time over its own. The gates hold the inverse,
  Vorticity's time over Rust's, under a ceiling (§3); their consoles print that ratio.
* **The targets**: within 2× of Rust's time on a full scan and 1.5× on a decoder, per instruction
  set, and files no larger than 105 % of Rust's. The benchmark page says where each time stands;
  `WrittenSizeTests` holds the bytes the writer gives the whole conformance corpus under a ceiling
  below the size target.
* **One machine per figure.** Each section of the benchmark page names the machine it was measured
  on: an Apple M4 Pro, arm64 and the `Vector128` paths, for [the benchmark page](../guide/benchmarks.md),
  and an AMD Ryzen 9 7950X, x64 with AVX-512, for [its twin](../guide/benchmarks-x64.md). CI builds
  and tests on x64 and arm64 but runs no benchmark.

## 2. What a caller sees

Seven actions a program performs on a file, each timed in a fresh process from opening the file to
its last row: the median of five runs, the page cache warm. The data is one table of 2^20 rows and
one of ten times that, four columns: `monotone`, an increasing `i64`; `value`, an `f64`; `label`,
one of five short strings; `flag`, a `bool` with nulls. In their plain form, Arrow's layout, that is
25.5 bytes a row.

**Two files of the same rows.** A Vortex file can store a column in many encodings, each writer
picks its own, and what a read costs depends on the pick. So the rows are written twice, once by
each implementation's writer, and both readers read both files; the benchmark page lists what each
writer chose at each size. Our writer stores `value` as zstd frames where Rust's chooses ALP-RD: a
much smaller file, slower to scan and to take from. Which of the two `Auto` should pick is the
writer's decision ([11-write-strategy.md](11-write-strategy.md) §3.4);
[the encodings, column by column](../guide/benchmarks.md#encodings-column-by-column) measure the
trade.

The actions: `open` the file and read no rows; `scan` every column of every row; `project` one
column of four; `filter-narrow`, the rows of a band of `monotone` holding about one in a hundred;
`filter-wide`, a band holding about half; `take` a thousand rows spread across the file; `write`,
read the file and write it back out. `append`, a tenth of the rows appended to a copy of the file,
is ours alone: Rust's harness has no entry point for it.

**GB/s** is the plain size of the rows returned over the time. **Allocated** is what Vorticity
allocates on its managed heap for one call, once warm: the median of six calls in a process that has
made two before them. **Peak** is each process's peak resident memory.

[Reading and writing a table](../guide/benchmarks.md#reading-and-writing-a-table-process-against-process)
has the figures, on one core and on all of them, and what a first call costs on the JIT. Reading
them:

* **A take from our file inflates a zstd frame per row.** Every row taken inflates the frame that
  holds its `value`, 64 KiB for one value, and the inflating is nearly all of the take.
* **On all cores** a take keeps three splits a lane decoded ahead of its consumer, so the lanes
  that finish first do not wait for the slowest; our writer summarizes its columns and compresses
  its zstd frames on every core, and chooses and writes the encodings on one.
* **Allocations on all cores**: buffers rented from the shared array pool on one thread and returned
  on another miss its per-thread caches, so a call now and then allocates several times its median.
  It is a known defect, not a cost of the work.
* **Native AOT, and the JIT.** The tables are the native build's. On the JIT, a fresh process
  compiles the code the first time it runs it, which the page's last table measures; a process that
  stays up pays that once, and then reads the speedups of §3 and §4. Starting a process is not in the
  figures, and the page gives it apart.

## 3. In one process, after warm-up

`--ratio-check` calls both implementations in one process, Rust's through a C ABI, in turn and
against one clock: at least 21 rounds, until the 95 % interval of the per-round ratios is within
5 % of their median. Our side runs on the JIT, warmed. Each axis is a gate: its ratio, our time over
Rust's, must stay under a ceiling. The page reads the same rounds the other way up, the median of
the per-round speedups and its interval. The first eight axes read a corpus file Rust's writer made,
`containers/zoned_many_zones_nulls`: 65 536 rows, five columns, 64 zones. The key-order, string and
band axes read files of 65 536 rows our writer makes for them; the last two, generated tables of a
million rows and six columns, and of fifty columns.
[In one process](../guide/benchmarks.md#in-one-process-after-warm-up) has the figures.

* **`open to first batch` is a latency.** Both sides decode their first batch. Rust's first batch
  costs most of what its whole scan does on this file, 1.4 ms of 1.6, where ours costs
  a fifth of ours. The speedup is on the time to the first row, and nothing more.
* **Where the reference has no counterpart, it answers the same question its own way.** A
  key-ordered read of a band is held against Rust's filtered scan of that band, which returns the
  same rows in file order; an exact count from the index, against Rust's count of the band with no
  column decoded. The speedup then prices the index, which is what a caller would weigh.
* The gate has seven more axes, which the page leaves out: two time Rust's scan without decoding,
  which asks another question; one holds the uncorrelated key-order read to a take of 64 rows whose
  positions the reference is given, the floor that read cannot go under; and four read one file as
  each writer wrote it. [bench/README.md](../../bench/README.md) describes them.

## 4. Decoders, per encoding

One file per encoding, and per notable shape of one: 57 files of a million rows
(`bench/gen-throughput.sh`), written by Rust's writer. Each side scans each file 12 times in a
process of its own, on one core, every value decoded to its plain form; a figure is the median of the
last 10 scans and of 3 processes, Native AOT against Rust's `vxbench` binary: what a decoder costs
once warm. [Decoding, per encoding](../guide/benchmarks.md#decoding-per-encoding) has all 57.

* **Past what memory can move is not decoding.** A column stored in its plain form is handed out as
  a view of the mapped file on both sides, nothing copied: the figure measures the walk of the
  layout, and the speedup compares two walks.
* **The widest gaps are UTF-8 validation.** In `varbin`, `varbinview`, `struct` and the tables, the
  stored arrays are already in their plain form, and the time is the check that every string is
  UTF-8, which both sides make: a sampled profile of Rust's scan of `varbinview` spends it in
  `from_utf8` and `validate_view`, under `VarBinViewData::try_new`.
* **Each scan opens the file again and maps it anew**, on both sides.

Take and write per encoding are measured in one process, on the JIT after warm-up, each file held to
a ceiling (`--throughput --take`, `--throughput --write`):
[taking rows](../guide/benchmarks.md#taking-rows-per-encoding) and
[writing](../guide/benchmarks.md#writing-per-encoding), per encoding. Rust's writer declines
`parquet_variant`.

### How the hot paths are built

* **FastLanes: the lane is the vector lane, and each packed word is loaded once.** A bit-packed
  block is 1 024 values transposed so that a row of the block, and each packed word of all its
  lanes, is 128 contiguous bytes whatever the element type: whole-vector loads and stores, no
  gather, no scatter. The kernel walks a block a packed word at a time, keeping the word in
  registers for every row inside it; what a step does depends on the width alone and is laid out
  once per width, so no branch looks at the data. The scalar loop stays as fallback and oracle
  (`FastLanesKernelBenchmarks` on the benchmark page).
* **FSST and OnPair: one wide store per symbol.** A symbol of one to eight bytes is written as one
  unaligned 8-byte store, and the output advances by its real length; an OnPair token of up to 16
  bytes, as one `Vector128` store. An exact copy only where the slack runs out at the end of a
  buffer. Checking that every code names a symbol and stays inside the destination costs next to
  nothing against the bare loop.
* **Zstd: a frame per block, and only the frames a take needs.** Our writer compresses a zstd column
  a block of 8 192 rows to a frame, and our reader inflates only the frames that hold the rows it is
  asked for.
* **A take reads by the run, and decodes ahead.** Consecutive zones holding a take's rows are read
  as one batch, up to sixteen rows a batch on several lanes, and a column whose encoding decodes
  ranges without selecting decodes the clusters of rows it is asked for rather than the span between
  them. On several lanes a take keeps three splits a lane in flight, and its degree bounds how many
  decode at once.
* **Zone maps prune before any decode.** The file keeps the minimum, maximum and null count of every
  zone of 8 192 rows. A filter skips the zones it proves empty and hands out the zones it proves full
  without testing their rows: the narrow filter of §2 decodes about a hundredth of the rows.
* **Views, not copies.** On a mapped file, a column stored in its plain form is a view of the mapped
  bytes; a test checks it by comparing pointers.
* **Nothing allocated per batch.** A scan's arenas and pools are its own, its `RecordBatch` is bound
  again rather than rebuilt, and its column views are `ref struct`s the compiler keeps inside the
  loop body (§5).
* **Parallel when asked, and thread-safe where shared.** Parallelism is given, never taken: a
  session's `MaxDegreeOfParallelism`, 1 unless set, or a scan's or a writer's own
  `DegreeOfParallelism`. A scan decodes its splits side by side, each on a context and arenas of its
  own, nothing shared, and delivers its batches in row order. A writer summarizes its columns and
  compresses a column's zstd frames on its threads, and writes the same bytes whatever the degree. A
  `VortexSession` and a `VortexFile` are thread-safe and meant to be shared, several scans of one
  file at once included; a scan, a writer and a batch belong to one consumer at a time
  ([09-contracts.md](09-contracts.md) §1–2, [threads.md](../guide/threads.md)).

## 5. Allocations

Held by ratchets in the test suite, counted by the runtime in a process doing nothing else, so
exact: a test fails above its ceiling, and a ceiling only comes down. Each test prints what it
measured beside its ceiling.

* **Per batch**: nothing on the calling thread, in steady state, on every scan
  (`ScanAllocationTests`). Counted on every thread of a scan at degree 2, a few hundred bytes a batch
  for its tasks, under a ceiling of 1 000.
* **Per operation**, on the 65 536-row corpus file of §3, the open included (`PathAllocationTests`):
  the footer alone, the first batch, a full scan, a projection, a take of 64 rows from 64 splits, a
  filter with and without its zone maps, and a full scan of one encoding's 4 096-row file, each
  under a ceiling in bytes. A full scan costs what its first batch costs.
* **Per write** (`WriteAllocationTests`): each corpus file written back out, a writer on four threads
  against one, and a column of a wide schema, each under a ceiling.

The benchmark page gives what each of §2's actions allocates once warm, beside its time.

## 6. How it is measured

Every instrument but the ratchets runs as `dotnet run -c Release --project
bench/Vorticity.Benchmarks -- <arguments>`, and, given `--out docs/guide/benchmarks.md`, writes
its sections of the benchmark page and leaves the others:

| instrument | what it gives | arguments |
|---|---|---|
| the report | §2, and §4's decode, each side in its own process | `--report --markdown` |
| the ratio gate | §3, both sides in one process | `--ratio-check` |
| the per-encoding gates | §4's take and write, in one process | `--throughput`, with `--take` or `--write`; `--check` holds each file to its ceiling |
| the trade-offs | every profile and hint on twenty column shapes, and what the advice picks on them | `--tradeoffs`, and `--advise` |
| BenchmarkDotNet | one kernel against its baseline, on one clock | a class name: `fastlanes` |
| the ratchets | §5 | `dotnet test Vorticity.slnx -c Release` |

Rust is driven through [tools/vxbench-rs](../../tools/vxbench-rs), built against the same
`vortex = "=0.86.1"` pin the corpus was written with: a C ABI for the in-process gates, a binary for
the report. `--ffi-check` checks that both readers return the same rows and the same checksum of
every decoded value before a ratio is trusted, and the gates refuse to run under a Rust build other
than the one their ceilings were set under. [bench/README.md](../../bench/README.md) has every
command, what it costs, and how a ceiling moves.

Not measured:

* **A cold page cache.** Every run reads a warm one; upstream's benchmarks flush it.
* **Pinned cores.** macOS pins no process, and an Apple M-series processor has cores of two kinds.
* **A remote store's latency**: every file is local.
* **Other data.** TPC-H and ClickBench are gigabytes that do not belong in a repository; what makes
  the comparison honest is both sides reading the same bytes, whatever they are.
* **The row encoding against Rust's**: the shim cannot hand `vortex-row` a batch, so
  `RowEncodingBenchmarks` compares our builds only.
* **CI** runs no benchmark: the gates are commands run before a push, `bench/gate.sh` among them.
