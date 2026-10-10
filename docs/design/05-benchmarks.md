# Benchmarks

How Vorticity is measured against Vortex's Rust implementation, and how its kernels are measured
against their baselines. This page covers [what is compared](#1-the-comparison), [what a caller
sees](#2-what-a-caller-sees), [the ratios the gates hold in one
process](#3-in-one-process-after-warm-up), [each decoder](#4-decoders-per-encoding),
[allocations](#5-allocations) and [the instruments](#6-how-it-is-measured). The figures are all on
[the benchmark page](../guide/benchmarks.md), each section written by the instrument that measures
it, and [bench/README.md](../../bench/README.md) explains how to run each one.

## 1. The comparison

* The reference is Rust. Vortex's own implementation is the one the format was designed around, and
  a .NET Parquet library would measure a difference of format, not of implementation.
* Both sides do the same work. They read the same bytes and must return the same rows, or the run
  fails. Every call opens its file and maps it anew, on both sides, and reads it where it lies: Rust
  through `open_buffer` over a `memmap2` mapping, ours through a session that keeps no mapping once a
  file is closed. Both decode every value they return to its plain form, except a constant column,
  which both keep as one value and a length (our reader's form, and upstream's `Columnar`). A write
  hands its bytes to a sink that counts them and keeps none, on both sides. A count is a count on
  both sides, and a read the reference has no index for is asked of it as the rows it would have to
  find. Neither side keeps a segment cache.
* Rust's figure is its faster setting. By default Rust cuts a chunk of more than 100 000 rows into
  splits of 100 000, and its reader decodes the whole chunk again for every split. On the
  per-encoding corpus, files of one chunk of a million rows, that means ten decodes of each, and the
  string-heavy files took ten times as long. Asked for one split per chunk, it decodes each chunk
  once but builds a chunk made of smaller arrays in one piece, three to six times slower on those
  files. Neither setting wins everywhere, so on one core every Rust figure is the faster of the two,
  measured on the spot, file by file and axis by axis. On all cores Rust keeps its default, whose
  splits spread its work.
* Each side is built for speed. Vorticity runs as a Native AOT binary for the machine's instruction
  set. Vortex 0.86.1 is built the way upstream builds its own benchmarks: mimalloc,
  `-C target-cpu=native`, one codegen unit, no LTO, upstream's `release_debug` profile and the
  `RUSTFLAGS` of its benchmark workflows.
* Both sides use the same threads. Every scenario runs on one core (Rust's single-threaded runtime,
  our scans at one lane) and on all of them (a Tokio worker per processor for Rust, a lane per
  processor for our scans and as many threads for our writer).
* Every page shows a speedup: Rust's time over Vorticity's, so above 1.00× Vorticity took less, and
  in a kernel's table, its baseline's time over its own. The gates hold the inverse, Vorticity's time
  over Rust's, under a ceiling ([in one process](#3-in-one-process-after-warm-up)), and their consoles
  print that ratio.
* The targets are within 2× of Rust's time on a full scan and within 1.5× on a decoder, per
  instruction set, and files no larger than 105 % of Rust's. The benchmark page shows where each time
  stands, and `WrittenSizeTests` holds the bytes the writer produces for the whole conformance corpus
  under a ceiling below the size target.
* Each figure comes from one machine. Each section of the benchmark page names the machine it was
  measured on: an Apple M4 Pro, arm64 with the `Vector128` paths, for [the benchmark
  page](../guide/benchmarks.md), and an AMD Ryzen 9 7950X, x64 with AVX-512, for [its
  twin](../guide/benchmarks-x64.md). CI builds and tests on x64 and arm64 but runs no benchmark.

## 2. What a caller sees

Seven actions a program performs on a file are each timed in a fresh process, from opening the file
to its last row: the median of five runs, with the page cache warm. The data is one table of 2^20
rows and one of ten times that, with four columns: `monotone`, an increasing `i64`, `value`, an
`f64`, `label`, one of five short strings, and `flag`, a `bool` with nulls. In their plain form
(Arrow's layout) that is 25.5 bytes per row.

The same rows are written as two files. A Vortex file can store a column in many encodings, each
writer picks its own, and what a read costs depends on the pick. So the rows are written twice, once
by each implementation's writer, and both readers read both files. The benchmark page lists what each
writer chose at each size. Our writer stores `value` as zstd frames where Rust's chooses ALP-RD,
which gives a much smaller file that is slower to scan and to take from. Which of the two `Auto`
should pick is the writer's decision ([choosing
encodings](11-write-strategy.md#34-choose-exact-verdicts-then-bounded-trials)), and [the encodings,
column by column](../guide/benchmarks.md#encodings-column-by-column) measure the trade-off.

The actions are: `open` the file and read no rows, `scan` every column of every row, `project` one
column of four, `filter-narrow` (the rows of a band of `monotone` holding about one in a hundred),
`filter-wide` (a band holding about half), `take` a thousand rows spread across the file, and
`write`, which reads the file and writes it back out. `append`, a tenth of the rows appended to a
copy of the file, is ours alone, because Rust's harness has no entry point for it.

GB/s is the plain size of the rows returned divided by the time. Allocated is what Vorticity
allocates on its managed heap for one call, once warm: the median of six calls in a process that has
made two before them. Peak is each process's peak resident memory.

[Reading and writing a table](../guide/benchmarks.md#reading-and-writing-a-table-process-against-process)
has the figures, on one core and on all of them, and what a first call costs on the JIT. A few
points help reading them:

* A take from our file inflates a zstd frame per row. Every row taken inflates the frame that holds
  its `value`, 64 KiB for one value, and the inflating is nearly all of the take.
* On all cores, a take keeps three splits per lane decoded ahead of its consumer, so the lanes that
  finish first do not wait for the slowest. Our writer summarizes its columns and compresses its
  zstd frames on every core, and chooses and writes the encodings on one.
* On all cores, buffers rented from the shared array pool on one thread and returned on another miss
  the pool's per-thread caches, so a call now and then allocates several times its median. This is a
  known defect, not a cost of the work.
* The tables come from the native build. On the JIT, a fresh process compiles code the first time it
  runs it, which the page's last table measures. A process that stays up pays that once, and then
  sees the speedups of [in one process](#3-in-one-process-after-warm-up) and
  [decoders](#4-decoders-per-encoding). Process start-up is not in the figures, and the page gives it
  separately.

## 3. In one process, after warm-up

`--ratio-check` calls both implementations in one process, Rust's through a C ABI, in turn and
against one clock: at least 21 rounds, until the 95 % interval of the per-round ratios is within 5 %
of their median. Our side runs on the JIT, warmed up. Each axis is a gate, whose ratio (our time over
Rust's) must stay under a ceiling. The page reads the same rounds the other way up, as the median of
the per-round speedups and its interval. The first eight axes read a corpus file Rust's writer made,
`containers/zoned_many_zones_nulls`: 65 536 rows, five columns, 64 zones. The key-order, string and
band axes read files of 65 536 rows that our writer makes for them, and the last two read generated
tables of a million rows with six columns, and of fifty columns.
[In one process](../guide/benchmarks.md#in-one-process-after-warm-up) has the figures.

* `open to first batch` is a latency. Both sides decode their first batch. Rust's first batch costs
  most of what its whole scan does on this file, 1.4 ms of 1.6, where ours costs a fifth of our full
  scan. The speedup is about the time to the first row, and nothing more.
* Where the reference has no counterpart, it answers the same question its own way. A key-ordered
  read of a band is compared with Rust's filtered scan of that band, which returns the same rows in
  file order, and an exact count from the index with Rust's count of the band with no column decoded.
  The speedup then prices the index, which is what a caller would weigh.
* The gate has seven more axes that the page leaves out. Two time Rust's scan without decoding, which
  asks a different question. One holds the uncorrelated key-order read against a take of 64 rows
  whose positions the reference is given, the floor that read cannot go under. Four read one file as
  each writer wrote it. [bench/README.md](../../bench/README.md) describes them.

## 4. Decoders, per encoding

There is one file per encoding, and per notable shape of one: 61 files of a million rows
(`bench/gen-throughput.sh`), written by Rust's writer. The arm64 page was measured on the earlier set
of 57, and the four `pco` shapes added since (`pco_f32`, `pco_i16`, `pco_u32`, `pco_nullable`) are on
the x64 page only. Each side scans each file 12 times in a process of its own, on one core, every
value decoded to its plain form. A figure is the median of the last 10 scans and of 3 processes,
Native AOT against Rust's `vxbench` binary, which is what a decoder costs once warm. [Decoding, per
encoding](../guide/benchmarks.md#decoding-per-encoding) has every file.

* A column stored in plain form is not decoding at all. It is handed out as a view of the mapped file
  on both sides, with nothing copied, so the figure measures the walk of the layout and the speedup
  compares two walks.
* The widest gaps are UTF-8 validation. In `varbin`, `varbinview`, `struct` and the tables, the
  stored arrays are already in plain form, and the time goes to checking that every string is UTF-8,
  which both sides do. A sampled profile of Rust's scan of `varbinview` spends it in `from_utf8` and
  `validate_view`, under `VarBinViewData::try_new`.
* Each scan opens the file again and maps it anew, on both sides.

Take and write per encoding are measured in one process, on the JIT after warm-up, with each file
held to a ceiling (`--throughput --take`, `--throughput --write`). See [taking
rows](../guide/benchmarks.md#taking-rows-per-encoding) and
[writing](../guide/benchmarks.md#writing-per-encoding), per encoding. Rust's writer declines
`parquet_variant`.

### How the hot paths are built

* In FastLanes, the lane is the vector lane, and each packed word is loaded once. A bit-packed block
  is 1 024 values transposed so that a row of the block, and each packed word across all its lanes,
  is 128 contiguous bytes whatever the element type: whole-vector loads and stores, no gather, no
  scatter. The kernel walks a block one packed word at a time, keeping the word in registers for
  every row inside it. What a step does depends only on the bit width and is laid out once per width,
  so no branch looks at the data. The scalar loop stays as fallback and oracle
  (`FastLanesKernelBenchmarks` on the benchmark page).
* FSST and OnPair write one wide store per symbol. A symbol of one to eight bytes is written as one
  unaligned 8-byte store and the output advances by its real length, and an OnPair token of up to 16
  bytes as one `Vector128` store. An exact copy is only needed where the slack runs out at the end of
  a buffer. Checking that every code names a symbol and stays inside the destination costs next to
  nothing compared with the bare loop.
* Zstd uses a frame per block, and a take only inflates the frames it needs. Our writer compresses a
  zstd column one block of 8 192 rows per frame, and our reader inflates only the frames that hold
  the rows it is asked for.
* A take reads by runs and decodes ahead. Consecutive zones holding a take's rows are read as one
  batch, up to sixteen rows per batch on several lanes, and a column whose encoding can decode ranges
  without a selection decodes the clusters of rows asked for rather than the span between them. On
  several lanes, a take keeps three splits per lane in flight, and its degree bounds how many decode
  at once.
* Zone maps prune before any decode. The file keeps the minimum, maximum and null count of every zone
  of 8 192 rows. A filter skips the zones it proves empty and hands out the zones it proves full
  without testing their rows, so the narrow filter of [what a caller sees](#2-what-a-caller-sees)
  decodes about a hundredth of the rows.
* On a mapped file, a column stored in plain form is a view of the mapped bytes rather than a copy,
  and a test checks it by comparing pointers.
* Nothing is allocated per batch. A scan's arenas and pools are its own, its `RecordBatch` is bound
  again rather than rebuilt, and its column views are `ref struct`s the compiler keeps inside the
  loop body ([allocations](#5-allocations)).
* Parallelism is opt-in, and shared objects are thread-safe. Parallelism is given, never taken: a
  session's `MaxDegreeOfParallelism` (1 unless set), or a scan's or a writer's own
  `DegreeOfParallelism`. A scan decodes its splits side by side, each on a context and arenas of its
  own with nothing shared, and delivers its batches in row order. A writer summarizes its columns and
  compresses a column's zstd frames on its threads, and writes the same bytes whatever the degree. A
  `VortexSession` and a `VortexFile` are thread-safe and meant to be shared, including several scans
  of one file at once, while a scan, a writer and a batch belong to one consumer at a time (see
  [concurrency](09-contracts.md#1-concurrency-and-thread-safety),
  [parallelism](09-contracts.md#2-parallelism) and [threads.md](../guide/threads.md)).

## 5. Allocations

Allocations are held by ratchets in the test suite, counted by the runtime in a process doing nothing
else, so the counts are exact. A test fails above its ceiling, and a ceiling only ever comes down.
Each test prints what it measured next to its ceiling.

* Per batch: nothing on the calling thread, in steady state, on every scan (`ScanAllocationTests`).
  Counted on every thread of a scan at degree 2, a few hundred bytes per batch for its tasks, under a
  ceiling of 1 000.
* Per operation, on the 65 536-row corpus file used [in one
  process](#3-in-one-process-after-warm-up), the open included (`PathAllocationTests`): the footer
  alone, the first batch, a full scan, a projection, a take of 64 rows from 64 splits, a filter with
  and without its zone maps, and a full scan of one encoding's 4 096-row file, each under a ceiling in
  bytes. A full scan costs what its first batch costs.
* Per write (`WriteAllocationTests`): each corpus file written back out, a writer on four threads
  against one, and a column of a wide schema, each under a ceiling.

The benchmark page gives what each of the actions in [what a caller sees](#2-what-a-caller-sees)
allocates once warm, next to its time.

## 6. How it is measured

Every instrument except the ratchets runs as `dotnet run -c Release --project
bench/Vorticity.Benchmarks -- <arguments>`, and, given `--out docs/guide/benchmarks.md`, rewrites its
own sections of the benchmark page and leaves the others alone:

| instrument | what it gives | arguments |
|---|---|---|
| the report | [what a caller sees](#2-what-a-caller-sees) and the per-encoding decode, each side in its own process | `--report --markdown` |
| the ratio gate | [in one process](#3-in-one-process-after-warm-up), both sides in one process | `--ratio-check` |
| the per-encoding gates | the per-encoding take and write, in one process | `--throughput`, with `--take` or `--write`, and `--check` to hold each file to its ceiling |
| the trade-offs | every profile and hint on twenty column shapes, and what the advice picks on them | `--tradeoffs`, and `--advise` |
| BenchmarkDotNet | one kernel against its baseline, on one clock | a class name, such as `fastlanes` |
| the ratchets | [allocations](#5-allocations) | `dotnet test Vorticity.slnx -c Release` |

Rust is driven through [tools/vxbench-rs](../../tools/vxbench-rs), built against the same
`vortex = "=0.86.1"` pin the corpus was written with: a C ABI for the in-process gates, and a binary
for the report. `--ffi-check` checks that both readers return the same rows and the same checksum of
every decoded value before a ratio is trusted, and the gates refuse to run under a Rust build other
than the one their ceilings were set with. [bench/README.md](../../bench/README.md) has every
command, what it costs, and how a ceiling moves.

What is not measured:

* A cold page cache. Every run reads a warm one, while upstream's benchmarks flush it.
* Pinned cores. macOS pins no process, and an Apple M-series processor has two kinds of cores.
* A remote store's latency, since every file is local.
* Other datasets. TPC-H and ClickBench are gigabytes that do not belong in a repository, and what
  makes the comparison honest is both sides reading the same bytes, whatever they are.
* The row encoding against Rust's. The shim cannot hand `vortex-row` a batch, so
  `RowEncodingBenchmarks` only compares our own builds.
* Anything in CI. CI runs no benchmark, and the gates are commands run before a push, `bench/gate.sh`
  among them.
