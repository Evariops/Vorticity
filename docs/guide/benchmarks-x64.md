# Benchmarks on x64

This is [the benchmark page](benchmarks.md) on an x64 machine: an AMD Ryzen 9 7950X (Zen 4, 16
cores and 32 threads, AVX-512) under Windows 11, against the same Vortex Rust 0.86.1 built the same
way for that machine. The instruments, files, rows and rules are the same as on that page, section
for section. What changes is the processor, the operating system and the file system under the page
cache. [05-benchmarks.md](../design/05-benchmarks.md) explains what is compared and how each
instrument measures, and [bench/README.md](../../bench/README.md) how to run them.

Vortex™ is a trademark of LF Projects, LLC. Vorticity is an independent implementation,
not affiliated with or endorsed by the Vortex project or LF Projects, LLC.

Rust's ratios on this page belong to this machine. Its `vxbench` is built here, while the ceilings
that `--ratio-check` and `--throughput --check` enforce are set on the arm64 machine against its own
build, so the in-process axes, the takes and the writes on this page are measured but held to no
ceiling. The per-encoding corpus is the generator's current one, with 61 files where the arm64 page
has 57: `pco_f32`, `pco_i16`, `pco_u32` and `pco_nullable` appear on this page only. The kernel
tables' speedups are computed from the Mean column of their run.

The ground rules:

* Vorticity runs as a Native AOT binary built for the machine's instruction set, with the
  workstation garbage collector, where a section says so, and on the JIT otherwise. Vortex Rust
  0.86.1 is built the way upstream builds its own benchmarks (mimalloc, `-C target-cpu=native`, one
  codegen unit, no LTO), through [tools/vxbench-rs](../../tools/vxbench-rs). `target-cpu=native`
  gives its compiler this machine's instruction set, AVX-512 included, just as .NET's JIT has it.
  Both sides map the file and read it where it lies, decode every value they return to its plain
  form (a constant column kept as one value on both), and must return the same rows or the run
  fails.
* Rust gets its faster setting. On one core, every Rust figure is the faster of its two ways of
  splitting a scan (its default, and one split per chunk), measured file by file. On all cores it
  keeps its default, as on [the benchmark page](benchmarks.md).
* Speedup is Rust's time over Vorticity's. Above 1.00x, Vorticity took less.
* Throughput is the plain size of the rows returned divided by the time, as on [the benchmark
  page](benchmarks.md).
* All cores means the 32 hardware threads, simultaneous multithreading included, where the arm64
  page has 14 cores of two kinds.
* The page cache is warm for every run, and every call maps its file anew, on both sides, as on
  [the benchmark page](benchmarks.md).

Each section is regenerated on an x64 machine by `dotnet run -c Release --project
bench/Vorticity.Benchmarks --` with the arguments below, which rewrite that section of this page and
leave the others alone. Publish the Native AOT runner that `--report` times first (`dotnet publish -c
Release bench/Vorticity.Benchmarks.Runner`), since an older runner times older code.

| section | arguments |
|---|---|
| [Reading and writing a table](#reading-and-writing-a-table-process-against-process), [decoding, per encoding](#decoding-per-encoding) | `--report --markdown --out docs/guide/benchmarks-x64.md` |
| [In one process](#in-one-process-after-warm-up) | `--ratio-check --out docs/guide/benchmarks-x64.md` |
| [Taking rows](#taking-rows-per-encoding), [writing](#writing-per-encoding), per encoding | `--throughput --take --out docs/guide/benchmarks-x64.md`, and `--write` |
| [Encodings, column by column](#encodings-column-by-column), [what the advice picks](#what-the-advice-picks) | `--tradeoffs --out docs/guide/benchmarks-x64.md`, and `--advise`, under `DOTNET_TieredCompilation=0` |
| [Kernels](#kernels-against-their-baselines) | `--out docs/guide/benchmarks-x64.md`, or a class name before it |

<!-- results: scenarios -->
## Reading and writing a table, process against process

* **The data.** A table of four columns: `monotone`, an increasing `i64` that the filters and
  the projection use; `value`, an `f64`; `label`, one of five short strings; `flag`, a `bool`
  with nulls. 2^20 rows, and ten times as many.
* **Two files of the same rows.** Each writer chooses its own encodings, and what a read costs
  depends on them, so the rows are written twice: by Vorticity's writer (*Vorticity's file*)
  and by Rust's (*Vortex Rust's file*). Both readers read both files; each size lists what the
  two writers chose.
* **One core, or all 32.** On one core, Rust's single-threaded runtime and our scans at one
  lane. On all of them, a Tokio worker per processor for Rust; for Vorticity, a lane per
  processor for a scan and as many threads for the writer, the degree a session's
  `MaxDegreeOfParallelism` gives both.
* **The same work on both sides.** Both map the file and read it where it lies, Rust through
  `open_buffer` over a `memmap2` mapping; both decode every value they return to its plain form,
  a constant column kept as one value on both; a write hands its bytes to a sink that keeps none,
  on both. On one core Rust is timed under each of its two ways of splitting a scan, its default
  and one split per chunk, and its figure is the faster; on all cores it keeps its default.
* **A figure** is the time a fresh process takes for its action, from opening the file to its
  last row, by its own clock: 5 runs a side, the sides taking turns after one discarded run, the
  median with the lowest and highest beside it. Both sides must return the same rows or the
  run fails.
* **Throughput** is the plain size of the rows returned over the time: 25.5 bytes a row here.
* **Allocated** is what Vorticity allocates on its managed heap for the action: the median of 6
  calls in a process that has made 2 before them, the cost of a call once the pools are filled.
  On all cores a call now and then allocates several times the median. Rust's allocator
  reports no such figure. **Peak** is each process's peak resident memory.

### 1,048,576 rows

| column | Vorticity's, 2,448,644 bytes | Vortex Rust's, 7,903,964 bytes |
|---|---|---|
| `monotone` | 16 chunks of `vortex.sequence` | 8 chunks of `vortex.sequence` |
| `value` | 16 chunks of `vortex.zstd` | 8 chunks of `vortex.alprd` |
| `label` | 16 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 2 chunks of `fastlanes.bitpacked` |
| `flag` | 16 chunks of `vortex.bool` | one chunk of `vortex.bool` |

#### One core

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.6) | 1.3 (1.1-1.4) | 2.29x | — | — | 5.4 KiB | 12 / 9 MiB |
| `open` | Vortex Rust's | 0.5 (0.5-0.6) | 1.1 (0.9-1.3) | 1.93x | — | — | 5.0 KiB | 12 / 9 MiB |
| `scan` | Vorticity's | 6.8 (6.6-7.1) | 17.3 (16.8-21.5) | 2.55x | 3.9 | 1.5 | 15.0 KiB | 15 / 15 MiB |
| `scan` | Vortex Rust's | 4.1 (4.0-5.0) | 5.2 (5.0-5.4) | 1.29x | 6.6 | 5.1 | 11.4 KiB | 23 / 22 MiB |
| `project` | Vorticity's | 1.0 (0.9-1.0) | 1.7 (1.6-1.9) | 1.81x | 8.8 | 4.9 | 17.2 KiB | 12 / 11 MiB |
| `project` | Vortex Rust's | 1.0 (1.0-1.4) | 1.8 (1.7-2.0) | 1.89x | 8.6 | 4.6 | 13.5 KiB | 13 / 11 MiB |
| `filter-narrow` | Vorticity's | 1.9 (1.9-2.1) | 2.6 (2.4-2.7) | 1.36x | 0.1 | 0.1 | 41.7 KiB | 15 / 13 MiB |
| `filter-narrow` | Vortex Rust's | 2.0 (2.0-2.2) | 2.4 (2.2-3.0) | 1.18x | 0.1 | 0.1 | 21.5 KiB | 16 / 13 MiB |
| `filter-wide` | Vorticity's | 4.4 (4.1-4.9) | 9.9 (9.6-10.4) | 2.24x | 3.0 | 1.4 | 24.1 KiB | 15 / 15 MiB |
| `filter-wide` | Vortex Rust's | 3.2 (3.0-3.7) | 4.4 (4.0-5.5) | 1.38x | 4.2 | 3.1 | 21.5 KiB | 20 / 17 MiB |
| `take` | Vorticity's | 6.1 (6.0-7.0) | 8.7 (8.6-8.8) | 1.42x | — | — | 38.7 KiB | 14 / 14 MiB |
| `take` | Vortex Rust's | 2.1 (2.0-2.2) | 3.3 (3.0-3.4) | 1.55x | — | — | 35.1 KiB | 15 / 16 MiB |
| `write` | Vorticity's | 31.4 (31.2-31.8) | 76.4 (75.6-96.0) | 2.43x | 0.8 | 0.3 | 31.1 KiB | 20 / 75 MiB |
| `write` | Vortex Rust's | 29.2 (28.8-36.9) | 75.8 (63.3-109.1) | 2.60x | 0.9 | 0.4 | 27.5 KiB | 29 / 80 MiB |
| `append` | Vorticity's | 11.6 (10.6-13.3) | not asked | n/a | — | — | 2.4 MiB | 21 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.5-0.6) | 2.8 (2.7-3.2) | 5.16x | — | — | 5.4 KiB | 12 / 10 MiB |
| `open` | Vortex Rust's | 0.5 (0.5-0.7) | 3.0 (2.7-3.2) | 5.52x | — | — | 5.0 KiB | 11 / 10 MiB |
| `scan` | Vorticity's | 4.0 (3.9-5.7) | 7.3 (6.9-9.8) | 1.80x | 6.6 | 3.7 | 37.0 KiB | 47 / 40 MiB |
| `scan` | Vortex Rust's | 4.5 (4.3-4.5) | 5.8 (5.5-5.9) | 1.29x | 5.9 | 4.6 | 30.2 KiB | 63 / 40 MiB |
| `project` | Vorticity's | 1.8 (1.7-4.2) | 3.7 (3.1-3.9) | 2.02x | 4.6 | 2.3 | 34.3 KiB | 21 / 16 MiB |
| `project` | Vortex Rust's | 1.8 (1.6-1.9) | 3.7 (3.2-4.1) | 2.09x | 4.7 | 2.2 | 22.1 KiB | 21 / 16 MiB |
| `filter-narrow` | Vorticity's | 1.7 (1.7-1.7) | 4.4 (4.1-4.5) | 2.65x | 0.2 | 0.1 | 48.3 KiB | 14 / 15 MiB |
| `filter-narrow` | Vortex Rust's | 1.6 (1.6-2.0) | 4.2 (4.0-4.4) | 2.57x | 0.2 | 0.1 | 25.0 KiB | 14 / 15 MiB |
| `filter-wide` | Vorticity's | 3.5 (3.2-3.9) | 7.5 (6.7-8.7) | 2.13x | 3.8 | 1.8 | 38.0 KiB | 30 / 31 MiB |
| `filter-wide` | Vortex Rust's | 3.4 (3.3-3.5) | 5.4 (5.1-6.0) | 1.59x | 4.0 | 2.5 | 30.1 KiB | 37 / 33 MiB |
| `take` | Vorticity's | 5.0 (4.3-5.8) | 6.5 (5.3-6.9) | 1.30x | — | — | 88.5 KiB | 26 / 22 MiB |
| `take` | Vortex Rust's | 2.5 (2.4-2.9) | 4.3 (4.0-5.5) | 1.73x | — | — | 88.0 KiB | 18 / 19 MiB |
| `write` | Vorticity's | 14.7 (14.4-14.8) | 23.3 (23.1-30.8) | 1.59x | 1.8 | 1.1 | 2.5 MiB | 59 / 110 MiB |
| `write` | Vortex Rust's | 15.6 (14.6-17.9) | 23.8 (22.3-29.5) | 1.52x | 1.7 | 1.1 | 2.5 MiB | 74 / 117 MiB |
| `append` | Vorticity's | 11.0 (10.7-11.6) | not asked | n/a | — | — | 2.4 MiB | 22 / — MiB |

### 10,485,760 rows

| column | Vorticity's, 24,464,260 bytes | Vortex Rust's, 78,986,804 bytes |
|---|---|---|
| `monotone` | 160 chunks of `vortex.sequence` | 80 chunks of `vortex.sequence` |
| `value` | 160 chunks of `vortex.zstd` | 80 chunks of `vortex.alprd` |
| `label` | 160 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 20 chunks of `fastlanes.bitpacked` |
| `flag` | 160 chunks of `vortex.bool` | 3 chunks of `vortex.bool` |

#### One core

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.6) | 1.1 (1.0-1.4) | 1.96x | — | — | 21.1 KiB | 11 / 9 MiB |
| `open` | Vortex Rust's | 0.6 (0.6-0.8) | 1.3 (1.1-1.4) | 1.99x | — | — | 9.5 KiB | 12 / 9 MiB |
| `scan` | Vorticity's | 59.1 (57.1-80.5) | 159.0 (151.8-212.6) | 2.69x | 4.5 | 1.7 | 74.6 KiB | 35 / 35 MiB |
| `scan` | Vortex Rust's | 33.1 (32.2-34.8) | 37.6 (37.0-45.6) | 1.14x | 8.1 | 7.1 | 28.7 KiB | 89 / 89 MiB |
| `project` | Vorticity's | 1.8 (1.8-2.0) | 3.0 (2.9-3.4) | 1.63x | 45.4 | 27.9 | 76.9 KiB | 13 / 12 MiB |
| `project` | Vortex Rust's | 2.0 (1.7-2.3) | 2.7 (2.5-3.5) | 1.37x | 42.5 | 31.0 | 30.9 KiB | 13 / 11 MiB |
| `filter-narrow` | Vorticity's | 2.7 (2.5-3.0) | 4.9 (4.7-5.8) | 1.78x | 1.0 | 0.5 | 107.4 KiB | 17 / 16 MiB |
| `filter-narrow` | Vortex Rust's | 2.9 (2.6-3.2) | 3.6 (3.4-3.8) | 1.21x | 0.9 | 0.7 | 62.5 KiB | 18 / 18 MiB |
| `filter-wide` | Vorticity's | 30.0 (28.9-36.0) | 78.9 (77.6-89.3) | 2.63x | 4.5 | 1.7 | 107.4 KiB | 24 / 25 MiB |
| `filter-wide` | Vortex Rust's | 19.4 (19.1-21.8) | 22.4 (21.0-30.3) | 1.15x | 6.9 | 6.0 | 62.5 KiB | 54 / 53 MiB |
| `take` | Vorticity's | 25.5 (25.4-26.3) | 69.8 (69.7-77.2) | 2.73x | — | — | 98.3 KiB | 30 / 37 MiB |
| `take` | Vortex Rust's | 5.6 (5.5-8.5) | 8.3 (8.0-8.9) | 1.47x | — | — | 52.4 KiB | 25 / 27 MiB |
| `write` | Vorticity's | 347.8 (280.3-398.4) | 705.1 (691.5-812.1) | 2.03x | 0.8 | 0.4 | 95.8 KiB | 42 / 377 MiB |
| `write` | Vortex Rust's | 267.9 (266.5-288.1) | 585.0 (572.0-652.1) | 2.18x | 1.0 | 0.5 | 49.9 KiB | 98 / 456 MiB |
| `append` | Vorticity's | 52.8 (51.2-69.3) | not asked | n/a | — | — | 3.3 MiB | 28 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.6) | 3.3 (3.2-3.5) | 5.90x | — | — | 21.1 KiB | 12 / 10 MiB |
| `open` | Vortex Rust's | 0.6 (0.5-0.6) | 3.2 (3.0-3.4) | 5.82x | — | — | 9.5 KiB | 12 / 10 MiB |
| `scan` | Vorticity's | 16.5 (15.3-21.8) | 28.0 (24.5-39.6) | 1.70x | 16.1 | 9.5 | 148.0 KiB | 102 / 101 MiB |
| `scan` | Vortex Rust's | 27.3 (26.8-28.4) | 20.4 (18.0-23.8) | 0.75x | 9.8 | 13.1 | 78.6 KiB | 261 / 126 MiB |
| `project` | Vorticity's | 2.9 (2.9-3.0) | 4.2 (4.2-4.5) | 1.46x | 29.0 | 19.9 | 124.9 KiB | 30 / 36 MiB |
| `project` | Vortex Rust's | 3.3 (3.2-3.9) | 4.0 (3.9-5.3) | 1.19x | 25.1 | 21.1 | 69.2 KiB | 46 / 28 MiB |
| `filter-narrow` | Vorticity's | 2.4 (2.1-2.6) | 6.1 (5.7-7.0) | 2.54x | 1.1 | 0.4 | 109.7 KiB | 18 / 20 MiB |
| `filter-narrow` | Vortex Rust's | 2.6 (2.6-2.9) | 5.0 (4.9-5.6) | 1.91x | 1.0 | 0.5 | 65.5 KiB | 19 / 21 MiB |
| `filter-wide` | Vorticity's | 14.3 (9.8-19.3) | 21.1 (17.2-28.8) | 1.48x | 9.3 | 6.3 | 181.9 KiB | 92 / 78 MiB |
| `filter-wide` | Vortex Rust's | 17.6 (17.5-20.4) | 13.0 (12.3-16.6) | 0.74x | 7.6 | 10.3 | 620.9 KiB | 229 / 89 MiB |
| `take` | Vorticity's | 11.5 (10.9-12.2) | 15.7 (15.3-16.1) | 1.37x | — | — | 260.6 KiB | 62 / 56 MiB |
| `take` | Vortex Rust's | 5.0 (4.6-5.5) | 7.2 (7.2-7.9) | 1.46x | — | — | 150.1 KiB | 29 / 32 MiB |
| `write` | Vorticity's | 86.8 (86.3-88.0) | 155.4 (143.1-176.4) | 1.79x | 3.1 | 1.7 | 2.7 MiB | 116 / 465 MiB |
| `write` | Vortex Rust's | 98.3 (97.1-101.1) | 149.8 (145.8-157.7) | 1.52x | 2.7 | 1.8 | 2.7 MiB | 279 / 528 MiB |
| `append` | Vorticity's | 47.7 (46.8-54.7) | not asked | n/a | — | — | 2.5 MiB | 61 / — MiB |

`open`: open the file and read no rows. `scan`: read every column of every row. `project`: read one column of four. `filter-narrow`: read the rows of a band holding about one in a hundred. `filter-wide`: read the rows of a band holding about half. `take`: take a thousand rows spread across the file. `write`: read the file and encode it back out. `append`: append a tenth of the rows to a copy of the file.

**On one core.** Vorticity took less time than Rust on 28 of 28 compared rows.
The highest speedup is `take` at 10,485,760 rows on Vorticity's file (2.73x), the lowest
`scan` at 10,485,760 rows on Vortex Rust's file (1.14x).

**On all 32 cores.** Vorticity took less time than Rust on 26 of 28 compared rows.
The highest speedup is `open` at 10,485,760 rows on Vorticity's file (5.90x), the lowest
`filter-wide` at 10,485,760 rows on Vortex Rust's file (0.74x).

**The process start** is not in the figures: 46 ms for Vorticity's native binary and 32 ms for Rust's,
most of it the operating system starting a binary, and 113 ms for the managed runtime on the JIT.

**Rust's splits.** On one core, one split per chunk was the faster of its two on 7 of 14 rows on Vorticity's file and 13 of 14 rows on Vortex Rust's file;
the two differ only where a chunk holds more than 100,000 rows.

**Not asked of Rust**: `append`, which its harness has no entry point for; those figures
are Vorticity's alone.

### A first call on the JIT

The same scenarios in the framework-dependent build, under `dotnet`, on one core: what a
process that is not compiled ahead of time pays the first time, the code compiling as it runs.
Milliseconds, the median of the same runs.

| scenario | 1,048,576 rows, Vorticity's file | 1,048,576 rows, Vortex Rust's file | 10,485,760 rows, Vorticity's file | 10,485,760 rows, Vortex Rust's file |
|---|---:|---:|---:|---:|
| `open` | 19.6 | 19.8 | 19.5 | 21.7 |
| `scan` | 114.7 | 102.2 | 220.3 | 139.3 |
| `project` | 70.3 | 70.7 | 76.5 | 81.0 |
| `filter-narrow` | 156.4 | 137.3 | 153.8 | 149.2 |
| `filter-wide` | 131.4 | 128.4 | 198.8 | 164.3 |
| `take` | 124.3 | 99.6 | 186.9 | 105.4 |
| `write` | 323.4 | 319.5 | 947.1 | 869.1 |
| `append` | 359.7 | — | 431.3 | — |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1, rustc 1.98.1 (48a229cea 2026-09-01); commit 716b74b6, 2026-10-09 17:35 UTC.*
<!-- /results: scenarios -->

<!-- results: decoding -->
## Decoding, per encoding

One file per encoding, and per notable shape of one, written by Rust's writer
(`bench/gen-throughput.sh`): a million rows each, `table_wide` fifty columns of fifty thousand.
Each side scans the file, every value decoded to its plain form, 12 times in a process of its own on
one core; a figure is the median of the last 10 scans, and the median of 3 processes: what a
decoder costs once warm. Each scan opens the file and maps it anew, on both sides, and reads it
where it lies: a column stored in its plain form is a view of the mapping on both sides, nothing
decoded and nothing copied, and its figure, past what memory can move, measures the walk of the
layout. Throughput is over the plain size, as above.

| encoding | rows | Vorticity, ns/row | Vortex Rust, ns/row | speedup | Vorticity, GB/s | Vortex Rust, GB/s |
|---|---:|---:|---:|---:|---:|---:|
| `alp` | 1,000,000 | 2.28 | 2.57 | 1.13x | 3.5 | 3.1 |
| `alp_no_patches` | 1,000,000 | 1.39 | 1.78 | 1.28x | 5.7 | 4.5 |
| `alp_patched_no_chunk_offsets` | 1,000,000 | 1.46 | 1.87 | 1.28x | 5.5 | 4.3 |
| `alprd` | 1,000,000 | 1.44 | 1.49 | 1.03x | 5.5 | 5.4 |
| `bool` | 1,000,000 | 0.17 | 0.18 | 1.05x | 0.7 | 0.7 |
| `bool_bit_offset3` | 1,000,000 | 0.18 | 0.17 | 0.93x | 0.7 | 0.8 |
| `bool_bit_offset7` | 1,000,000 | 0.17 | 0.17 | 0.99x | 0.7 | 0.7 |
| `bool_bit_offset_straddle` | 1,000,000 | 0.17 | 0.17 | 1.03x | 0.7 | 0.7 |
| `bytebool` | 1,000,000 | 0.32 | 0.34 | 1.08x | 0.4 | 0.4 |
| `chunked` | 1,000,000 | 0.19 | 0.72 | 3.73x | 41.7 | 11.2 |
| `chunked_bool` | 1,000,000 | 0.20 | 0.22 | 1.09x | 0.6 | 0.6 |
| `chunked_decimal` | 1,000,000 | 0.23 | 0.63 | 2.73x | 34.9 | 12.8 |
| `chunked_empty_chunks` | 1,000,000 | 0.20 | 0.52 | 2.68x | 41.0 | 15.3 |
| `chunked_mixed_validity` | 1,000,000 | 0.67 | 0.71 | 1.06x | 12.1 | 11.4 |
| `chunked_one_chunk` | 1,000,000 | 0.19 | 0.24 | 1.22x | 41.1 | 33.7 |
| `chunked_varbinview` | 1,000,000 | 4.35 | 10.8 | 2.49x | 3.1 | 1.2 |
| `constant` | 1,000,000 | 0.16 | 0.19 | 1.20x | 51.4 | 43.0 |
| `datetimeparts` | 1,000,000 | 2.82 | 3.19 | 1.13x | 2.8 | 2.5 |
| `decimal` | 1,000,000 | 0.19 | 0.18 | 0.97x | 42.9 | 44.3 |
| `decimal_byte_parts` | 1,000,000 | 0.22 | 0.18 | 0.81x | 36.0 | 44.3 |
| `dict` | 1,000,000 | 0.98 | 1.09 | 1.12x | 6.2 | 5.5 |
| `dict_nullable_codes` | 1,000,000 | 1.07 | 1.95 | 1.81x | 5.2 | 2.8 |
| `dict_nullable_values_nonnull_codes` | 1,000,000 | 0.94 | 1.02 | 1.09x | 6.1 | 5.6 |
| `dict_u64_codes` | 1,000,000 | 1.68 | 1.64 | 0.98x | 3.6 | 3.7 |
| `dict_u8_codes` | 1,000,000 | 0.57 | 0.64 | 1.12x | 13.9 | 12.4 |
| `ext` | 1,000,000 | 0.18 | 0.18 | 0.99x | 44.0 | 44.6 |
| `fastlanes_bitpacked` | 1,000,000 | 0.45 | 0.41 | 0.93x | 8.9 | 9.7 |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,000,000 | 0.45 | 0.41 | 0.91x | 8.9 | 9.9 |
| `fastlanes_delta` | 1,000,000 | 2.15 | 1.90 | 0.88x | 3.7 | 4.2 |
| `fastlanes_for` | 1,000,000 | 1.38 | 1.38 | 1.00x | 5.8 | 5.8 |
| `fastlanes_rle` | 1,000,000 | 0.73 | 0.79 | 1.08x | 5.5 | 5.1 |
| `fixed_size_list` | 1,000,000 | 0.19 | 0.18 | 0.98x | 64.3 | 65.4 |
| `fsst` | 1,000,000 | 8.12 | 10.2 | 1.25x | 6.9 | 5.5 |
| `list` | 1,000,000 | 0.81 | 1.30 | 1.60x | 12.3 | 7.7 |
| `listview` | 1,000,000 | 2.55 | 3.04 | 1.19x | 3.9 | 3.3 |
| `map` | 1,000,000 | 5.42 | 11.0 | 2.02x | 3.7 | 1.8 |
| `masked` | 1,000,000 | 0.18 | 0.18 | 1.04x | 23.3 | 22.5 |
| `masked_all_invalid` | 1,000,000 | 0.21 | 0.18 | 0.90x | 19.4 | 21.7 |
| `masked_all_valid` | 1,000,000 | 0.20 | 0.18 | 0.89x | 19.5 | 22.0 |
| `null` | 1,000,000 | 0.14 | 0.16 | 1.18x | — | — |
| `onpair` | 1,000,000 | 3.95 | 6.79 | 1.72x | 5.5 | 3.2 |
| `parquet_variant` | 1,000,000 | 3.75 | 15.2 | 4.05x | 3.2 | 0.8 |
| `pco` | 1,000,000 | 0.42 | 1.19 | 2.86x | 19.2 | 6.7 |
| `pco_f32` | 1,000,000 | 7.86 | 2.99 | 0.38x | 0.5 | 1.3 |
| `pco_i16` | 1,000,000 | 6.96 | 2.61 | 0.37x | 0.3 | 0.8 |
| `pco_nullable` | 1,000,000 | 8.71 | 4.28 | 0.49x | 0.9 | 1.9 |
| `pco_u32` | 1,000,000 | 13.1 | 4.24 | 0.32x | 0.3 | 0.9 |
| `primitive` | 1,000,000 | 0.18 | 0.22 | 1.21x | 43.7 | 36.2 |
| `runend` | 1,000,000 | 0.27 | 0.30 | 1.13x | 15.0 | 13.2 |
| `sequence` | 1,000,000 | 0.20 | 0.23 | 1.17x | 40.2 | 34.5 |
| `sparse` | 1,000,000 | 0.24 | 0.24 | 1.00x | 16.4 | 16.4 |
| `struct` | 1,000,000 | 2.65 | 7.75 | 2.92x | 5.6 | 1.9 |
| `table_mixed` | 1,000,000 | 12.0 | 23.6 | 1.97x | 5.8 | 3.0 |
| `table_wide` | 50,000 | 5.19 | 4.78 | 0.92x | 77.1 | 83.7 |
| `varbin` | 1,000,000 | 3.65 | 14.7 | 4.04x | 4.4 | 1.1 |
| `varbinview` | 1,000,000 | 4.00 | 11.0 | 2.74x | 3.4 | 1.2 |
| `variant` | 1,000,000 | 0.14 | 0.17 | 1.21x | 113.1 | 93.3 |
| `zigzag` | 1,000,000 | 0.77 | 0.97 | 1.26x | 5.2 | 4.1 |
| `zstd` | 1,000,000 | 4.43 | 9.61 | 2.17x | 7.2 | 3.3 |
| `zstd_buffers` | 1,000,000 | 0.42 | 0.46 | 1.09x | 18.9 | 17.4 |
| `zstd_nullable` | 1,000,000 | 1.99 | 4.16 | 2.09x | 4.1 | 2.0 |

Rust is timed under each of its two ways of splitting a scan, three processes each, and its
figure is the faster: its default cuts a chunk of more than 100,000 rows into splits of 100,000 and
decodes the whole chunk again for each, which on these files of one chunk of a million rows is ten
times; one split per chunk decodes it once, but builds a chunk made of smaller arrays in one
piece. One split per chunk was the faster on 49 of 61 files, its default on `bytebool`, `chunked`, `chunked_decimal`, `chunked_empty_chunks`, `chunked_mixed_validity`, `chunked_one_chunk`, `constant`, `datetimeparts`, `dict_u64_codes`, `fastlanes_delta`, `fsst`, `parquet_variant`.

Vorticity decoded 43 of 61 files in less time than Rust; the median speedup is 1.12x. At 1.00x or below: `pco_u32` 0.32x, `pco_i16` 0.37x, `pco_f32` 0.38x, `pco_nullable` 0.49x, `decimal_byte_parts` 0.81x, `fastlanes_delta` 0.88x, `masked_all_valid` 0.89x, `masked_all_invalid` 0.90x, `fastlanes_bitpacked_patched_no_chunk_offsets` 0.91x, `table_wide` 0.92x, `fastlanes_bitpacked` 0.93x, `bool_bit_offset3` 0.93x, `decimal` 0.97x, `dict_u64_codes` 0.98x, `fixed_size_list` 0.98x, `ext` 0.99x, `bool_bit_offset7` 0.99x, `sparse` 1.00x.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1, rustc 1.98.1 (48a229cea 2026-09-01); commit 716b74b6, 2026-10-09 17:35 UTC.*
<!-- /results: decoding -->

<!-- results: in-process -->
## In one process, after warm-up

Both readers called in turn in one process, Rust's through a C ABI, against one clock. A round times
both, and its speedup is Rust's time over Vorticity's: at least 21 rounds after a 2-second warm-up,
until the 95 % interval of their median is within 5 % of it. Our side runs on the JIT, warmed. Each
call opens its file and maps it anew, on both sides, and asks both the same question. Each axis is
one the `--ratio-check` gate holds to a ceiling; what each one reads and asks is in
[05-benchmarks.md](../design/05-benchmarks.md) §3.

| axis | Vorticity, µs | Vortex Rust, µs | speedup | 95 % interval |
|---|---:|---:|---:|---|
| full scan | 927.6 | 3,312.2 | 3.51x | 3.38 to 3.61 |
| projected scan, 1 of 5 columns | 252.1 | 486.9 | 1.96x | 1.91 to 1.98 |
| open to first batch | 220.7 | 2,868.4 | 13.0x | 12.7 to 13.1 |
| open, footer only | 133.4 | 212.8 | 1.59x | 1.49 to 1.62 |
| read and write back | 5,966.6 | 19,356.3 | 3.24x | 3.14 to 3.31 |
| filtered scan, 1% band | 259.2 | 679.5 | 2.70x | 2.59 to 2.78 |
| filtered scan, half the rows | 331.5 | 1,096.9 | 3.28x | 3.25 to 3.39 |
| scattered take, 64 of 64 splits | 670.0 | 2,622.4 | 3.91x | 3.87 to 3.97 |
| key order, sorted column, 1% band | 267.4 | 361.7 | 1.36x | 1.35 to 1.37 |
| key order, uncorrelated, 64 rows | 369.2 | 491.6 | 1.38x | 1.29 to 1.41 |
| count, exact cover, 1% band | 276.7 | 386.1 | 1.53x | 1.44 to 1.59 |
| filtered scan, string equality, fsst | 645.9 | 831.2 | 1.28x | 1.27 to 1.28 |
| filtered scan, string prefix, fsst | 1,084.8 | 1,195.2 | 1.10x | 1.10 to 1.11 |
| filtered scan, string equality, dict | 189.5 | 312.5 | 1.66x | 1.58 to 1.66 |
| filtered scan, string prefix, dict | 201.3 | 406.8 | 2.02x | 2.00 to 2.04 |
| filtered scan, band, runend | 197.6 | 294.5 | 1.49x | 1.42 to 1.51 |
| filtered scan, band, bitpacked | 234.1 | 268.7 | 1.15x | 1.14 to 1.16 |
| full scan, 1M table | 25,271.0 | 41,306.8 | 1.62x | 1.58 to 1.75 |
| projected scan, 1 of 50 columns | 176.9 | 229.7 | 1.31x | 1.29 to 1.32 |

Vorticity took less time on 19 of 19 axes.
Rust is timed under the faster of its two splits, axis by axis: one split per chunk on 13 of 19, its default on full scan; open, footer only; key order, uncorrelated, 64 rows; count, exact cover, 1% band; filtered scan, string equality, fsst; filtered scan, band, bitpacked.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary 7527e2b5d861; commit 716b74b6, 2026-10-09 17:36 UTC.*
<!-- /results: in-process -->

<!-- results: take -->
## Taking rows, per encoding

The files of the per-encoding corpus again, 64 rows spread evenly over each, one every 15,625: what a
decoder costs to reach a few rows rather than all of them.
Each call opens the file and maps it anew, on both sides.
Both implementations in one process, Rust's through a C ABI, taking turns against one clock:
at least 9 rounds after a 1-second warm-up per file, our side on the JIT, warmed.

| encoding | Vorticity, µs | Vortex Rust, µs | speedup |
|---|---:|---:|---:|
| `alp` | 856 | 904 | 1.01x |
| `alp_no_patches` | 245 | 276 | 1.11x |
| `alp_patched_no_chunk_offsets` | 277 | 310 | 1.11x |
| `alprd` | 301 | 324 | 1.10x |
| `bool` | 183 | 206 | 1.12x |
| `bool_bit_offset3` | 185 | 205 | 1.12x |
| `bool_bit_offset7` | 191 | 212 | 1.10x |
| `bool_bit_offset_straddle` | 180 | 206 | 1.14x |
| `bytebool` | 224 | 432 | 1.91x |
| `chunked` | 258 | 294 | 1.15x |
| `chunked_bool` | 186 | 228 | 1.21x |
| `chunked_decimal` | 247 | 285 | 1.16x |
| `chunked_empty_chunks` | 248 | 286 | 1.15x |
| `chunked_mixed_validity` | 268 | 321 | 1.19x |
| `chunked_one_chunk` | 247 | 273 | 1.11x |
| `chunked_varbinview` | 408 | 13,592 | 34.0x |
| `constant` | 124 | 174 | 1.42x |
| `datetimeparts` | 422 | 462 | 1.10x |
| `decimal` | 245 | 268 | 1.10x |
| `decimal_byte_parts` | 248 | 277 | 1.14x |
| `dict` | 233 | 262 | 1.12x |
| `dict_nullable_codes` | 261 | 306 | 1.16x |
| `dict_nullable_values_nonnull_codes` | 232 | 259 | 1.11x |
| `dict_u64_codes` | 251 | 281 | 1.11x |
| `dict_u8_codes` | 319 | 340 | 1.07x |
| `ext` | 253 | 273 | 1.10x |
| `fastlanes_bitpacked` | 221 | 243 | 1.11x |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 239 | 266 | 1.12x |
| `fastlanes_delta` | 476 | 3,301 | 6.50x |
| `fastlanes_for` | 361 | 400 | 1.09x |
| `fastlanes_rle` | 953 | 1,016 | 1.03x |
| `fixed_size_list` | 259 | 299 | 1.17x |
| `fsst` | 423 | 455 | 1.09x |
| `list` | 253 | 1,197 | 4.75x |
| `listview` | 5,571 | 7,306 | 1.19x |
| `map` | 9,785 | 17,961 | 1.69x |
| `masked` | 201 | 230 | 1.15x |
| `masked_all_invalid` | 200 | 226 | 1.12x |
| `masked_all_valid` | 375 | 401 | 1.08x |
| `null` | 180 | 249 | 1.35x |
| `onpair` | 403 | 437 | 1.08x |
| `parquet_variant` | 465 | 502 | 1.05x |
| `pco` | 314 | 1,218 | 3.81x |
| `pco_f32` | 7,958 | 3,702 | 0.43x |
| `pco_i16` | 6,190 | 2,692 | 0.43x |
| `pco_nullable` | 6,446 | 348 | 0.05x |
| `pco_u32` | 11,965 | 4,353 | 0.36x |
| `primitive` | 361 | 384 | 1.08x |
| `runend` | 217 | 226 | 1.11x |
| `sequence` | 169 | 231 | 1.37x |
| `sparse` | 267 | 277 | 1.13x |
| `struct` | 5,918 | 15,734 | 2.52x |
| `table_mixed` | 19,179 | 32,506 | 1.76x |
| `table_wide` | 438 | 483 | 1.24x |
| `varbin` | 440 | 12,960 | 29.6x |
| `varbinview` | 7,504 | 14,748 | 2.04x |
| `variant` | 126 | 190 | 1.49x |
| `zigzag` | 364 | 395 | 1.11x |
| `zstd` | 701 | 10,564 | 15.5x |
| `zstd_buffers` | 514 | 555 | 1.13x |
| `zstd_nullable` | 521 | 4,496 | 7.60x |

Vorticity took less time on 57 of 61 files; the median speedup is 1.12x. At 1.00x or below: `pco_nullable` 0.05x, `pco_u32` 0.36x, `pco_f32` 0.43x, `pco_i16` 0.43x.
Rust is timed under the faster of its two splits, file by file: one split per chunk on 59 of 61 files, its default on `fastlanes_delta`, `table_wide`.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary 7527e2b5d861; commit 716b74b6, 2026-10-09 17:37 UTC.*
<!-- /results: take -->

<!-- results: write -->
## Writing, per encoding

The files of the per-encoding corpus again, each read and written back out to a sink that
keeps nothing, on both sides, both writers given the rows as our reader delivers them, decoded and a
constant kept as one value: what a writer costs, the read included on both sides.
Each call opens the file and maps it anew, on both sides.
Both implementations in one process, Rust's through a C ABI, taking turns against one clock:
at least 9 rounds after a 1-second warm-up per file, our side on the JIT, warmed.
A writer can be fast by compressing less, so each one's bytes are beside its time.

| encoding | Vorticity, µs | Vortex Rust, µs | speedup | Vorticity, bytes | Vortex Rust, bytes |
|---|---:|---:|---:|---:|---:|
| `alp` | 8,314 | 33,734 | 3.97x | 2,740,820 | 2,976,924 |
| `alp_no_patches` | 6,988 | 24,835 | 3.72x | 1,955,668 | 1,956,060 |
| `alp_patched_no_chunk_offsets` | 6,125 | 25,662 | 4.15x | 2,155,956 | 2,141,772 |
| `alprd` | 8,436 | 27,115 | 3.24x | 6,508,892 | 6,509,316 |
| `bool` | 302 | 858 | 2.90x | 127,108 | 127,108 |
| `bool_bit_offset3` | 513 | 1,482 | 2.89x | 127,108 | 127,108 |
| `bool_bit_offset7` | 341 | 1,009 | 2.99x | 127,108 | 127,108 |
| `bool_bit_offset_straddle` | 350 | 1,034 | 2.97x | 127,108 | 127,108 |
| `bytebool` | 507 | 1,095 | 2.17x | 127,108 | 127,108 |
| `chunked` | 3,608 | 14,436 | 4.11x | 890,228 | 890,736 |
| `chunked_bool` | 379 | 1,053 | 2.85x | 127,236 | 127,248 |
| `chunked_decimal` | 5,560 | 18,287 | 3.35x | 1,822,388 | 1,825,056 |
| `chunked_empty_chunks` | 3,258 | 14,480 | 4.55x | 316,532 | 317,080 |
| `chunked_mixed_validity` | 4,128 | 17,561 | 4.26x | 1,431,516 | 1,374,016 |
| `chunked_one_chunk` | 3,240 | 14,569 | 4.35x | 5,044 | 4,640 |
| `chunked_varbinview` | 27,200 | 37,866 | 1.39x | 643,644 | 628,628 |
| `constant` | 239 | 4,271 | 18.0x | 4,212 | 3,300 |
| `datetimeparts` | 7,164 | 30,800 | 4.39x | 3,382,604 | 3,383,628 |
| `decimal` | 3,762 | 16,841 | 4.56x | 3,372 | 5,116 |
| `decimal_byte_parts` | 3,796 | 16,140 | 4.29x | 3,372 | 5,116 |
| `dict` | 9,259 | 21,212 | 2.24x | 383,892 | 378,288 |
| `dict_nullable_codes` | 18,159 | 20,804 | 1.29x | 384,628 | 378,772 |
| `dict_nullable_values_nonnull_codes` | 14,036 | 19,706 | 1.42x | 384,628 | 378,764 |
| `dict_u64_codes` | 10,514 | 23,074 | 2.18x | 383,892 | 378,288 |
| `dict_u8_codes` | 11,166 | 22,472 | 1.94x | 38,740 | 1,004,160 |
| `ext` | 3,153 | 24,750 | 7.90x | 5,548 | 4,988 |
| `fastlanes_bitpacked` | 1,467 | 4,569 | 3.12x | 1,254,340 | 1,253,668 |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,490 | 5,240 | 3.52x | 1,270,452 | 1,272,740 |
| `fastlanes_delta` | 3,719 | 15,674 | 3.98x | 4,908 | 4,532 |
| `fastlanes_for` | 3,574 | 7,170 | 2.03x | 881,364 | 879,732 |
| `fastlanes_rle` | 1,781 | 8,660 | 4.82x | 3,964 | 3,812 |
| `fixed_size_list` | 4,256 | 26,980 | 6.38x | 4,268 | 4,724 |
| `fsst` | 50,186 | 312,191 | 6.05x | 933,668 | 8,574,500 |
| `list` | 8,889 | 28,170 | 3.06x | 4,782,492 | 5,122,712 |
| `listview` | 11,679 | 38,569 | 3.33x | 4,745,468 | 4,502,136 |
| `map` | 23,149 | 75,151 | 3.29x | 4,426,852 | 4,811,136 |
| `masked` | 3,366 | 19,212 | 6.13x | 2,380,060 | 2,381,156 |
| `masked_all_invalid` | 1,554 | 2,319 | 1.51x | 2,636 | 2,700 |
| `masked_all_valid` | 1,664 | 10,577 | 6.43x | 3,484 | 3,324 |
| `null` | 154 | 586 | 3.82x | 2,052 | 2,164 |
| `onpair` | 27,960 | 33,828 | 1.20x | 60,588 | 1,005,840 |
| `pco` | 1,195 | 14,063 | 12.0x | 4,908 | 4,508 |
| `pco_f32` | 9,774 | 21,230 | 2.19x | 2,130,020 | 2,130,108 |
| `pco_i16` | 6,927 | 8,154 | 1.18x | 1,878,788 | 1,878,460 |
| `pco_nullable` | 14,502 | 35,847 | 2.47x | 2,876,548 | 2,874,276 |
| `pco_u32` | 18,600 | 9,780 | 0.52x | 1,931,748 | 2,879,908 |
| `primitive` | 3,057 | 13,484 | 4.24x | 5,356 | 4,572 |
| `runend` | 813 | 9,510 | 11.7x | 4,028 | 3,500 |
| `sequence` | 1,076 | 14,202 | 13.3x | 4,908 | 4,516 |
| `sparse` | 823 | 4,224 | 5.07x | 131,172 | 3,884 |
| `struct` | 44,892 | 119,019 | 2.54x | 638,412 | 5,274,480 |
| `table_mixed` | 85,274 | 415,839 | 4.84x | 7,867,284 | 14,856,384 |
| `table_wide` | 11,150 | 40,954 | 3.52x | 5,163,108 | 5,168,320 |
| `varbin` | 55,308 | 263,222 | 4.75x | 637,012 | 5,066,652 |
| `varbinview` | 24,162 | 35,058 | 1.45x | 643,516 | 628,504 |
| `variant` | 193 | 1,058 | 5.50x | 2,220 | 52,520 |
| `zigzag` | 2,576 | 11,809 | 4.69x | 2,531,652 | 2,380,300 |
| `zstd` | 36,620 | 41,824 | 1.18x | 786,948 | 754,336 |
| `zstd_buffers` | 1,863 | 4,876 | 2.59x | 630,732 | 629,028 |
| `zstd_nullable` | 5,415 | 21,568 | 3.96x | 756,500 | 628,740 |

Vorticity took less time on 59 of 60 files; the median speedup is 3.52x. At 1.00x or below: `pco_u32` 0.52x.
Rust is timed under the faster of its two splits, file by file: one split per chunk on 45 of 60 files, its default on `alp`, `alprd`, `chunked_decimal`, `chunked_empty_chunks`, `chunked_one_chunk`, `decimal`, `dict_nullable_values_nonnull_codes`, `fixed_size_list`, `fsst`, `list`, `pco_f32`, `runend`, `sparse`, `table_wide`, `zigzag`.
Vorticity's bytes were within 5 % of Rust's or fewer on 48 of 60 files; more than 5 % above them on `sparse` (33.8 times Rust's), `constant` (1.28 times), `zstd_nullable` (1.20 times), `primitive` (1.17 times), `runend` (1.15 times), `ext` (1.11 times), `pco` (1.09 times), `chunked_one_chunk` (1.09 times), `sequence` (1.09 times), `fastlanes_delta` (1.08 times), `zigzag` (1.06 times), `listview` (1.05 times).
Rust's writer declines `parquet_variant`, which is left out.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary 7527e2b5d861; commit 716b74b6, 2026-10-09 17:40 UTC.*
<!-- /results: write -->

<!-- results: tradeoffs -->
## Encodings, column by column

One column of 10,000,000 rows per file, written under each compression profile and each hint that
applies to it, then opened and read. A **scan** decodes every value to its plain form and reads it
once; a **take** reads 1,000 rows spread over the file. Each figure is the median of 3 passes after
1 warm-up, the file in the page cache and mapped anew by each pass, so that a larger file pays for its
pages on every read; a write is net of generating its rows. **Crosses at** is the
storage throughput at which a configuration and `Auto` read the column whole in the same time,
counting its bytes at that throughput and then its scan: below it the smaller file reads faster
end to end, above it the faster decode does. What the figures mean for a choice is in
[choose-encodings.md](choose-encodings.md).

**Worth knowing** is picked by rule, against `Auto`, among the configurations that write something
else: the smallest, when it saves 5 % of the bytes; of those a fifth faster to scan, the one that
overtakes `Auto` on the slowest storage, when that storage is no faster than the page cache, 10
GB/s; and the fastest take, when it halves `Auto`'s.

### Integers

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| a sequence | Sequence x77 | 0.00 | 13 | 1.8 | 0.26 |  |
| sorted runs of 1 000 | RunEnd x77 | 0.01 | 14 | 2.7 | 0.36 | Smallest: RunEnd x76, Pco x1, 0.01 B/value, scan 3.0 ms, take 0.74 ms; reads faster below 96 MB/s |
| timestamps (ms, increasing, jittered) | BitPacked x77 | 3.38 | 70 | 12.9 | 1.72 | Smallest: Pco x77, 1.35 B/value, scan 112.6 ms, take 118.47 ms; reads faster below 204 MB/s |
| random in 0..999 | BitPacked x77 | 1.25 | 22 | 5.5 | 1.54 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 55 | 3.3 | 1.14 |  |
| 100 003 distinct, repeating | BitPacked x77 | 4.63 | 38 | 12.9 | 1.51 | Smallest: Pco x77, 0.06 B/value, scan 72.5 ms, take 69.30 ms; reads faster below 767 MB/s; Auto, 16 MiB chunks: Dictionary x5, 2.36 B/value, scan 9.5 ms, take 1.32 ms; reads faster at any throughput |
| uniform 64-bit | Canonical x77 | 8.00 | 43 | 27.0 | 2.14 |  |
| random in 0..999, 10 % null | BitPacked x82 | 1.38 | 25 | 6.3 | 1.35 | Smallest: Pco x82, 1.26 B/value, scan 45.3 ms, take 44.57 ms; reads faster below 32 MB/s |

<details><summary>a sequence: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Sequence x77 | 38,844 | 0.00 | 13 | 1.8 | 0.26 |  |
| Fastest | Sequence x77 | 38,844 | 0.00 | 22 | 1.9 | 0.28 | reads slower at any throughput |
| Smallest | Sequence x77 | 38,844 | 0.00 | 15 | 1.8 | 0.27 | same |
| None | Canonical x77 | 80,043,724 | 8.00 | 51 | 23.2 | 1.68 | reads slower at any throughput |
| hint Dictionary | Sequence x77 | 38,844 | 0.00 | 121 | 1.8 | 0.28 | same |
| hint BitPacked | Sequence x77 | 38,844 | 0.00 | 15 | 1.8 | 0.28 | same |
| hint RunEnd | Sequence x77 | 38,844 | 0.00 | 13 | 2.1 | 0.37 | reads slower at any throughput |
| hint Zstd | Sequence x77 | 38,844 | 0.00 | 11 | 1.8 | 0.27 | same |

</details>

<details><summary>sorted runs of 1 000: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x77 | 88,004 | 0.01 | 14 | 2.7 | 0.36 |  |
| Fastest | RunEnd x77 | 88,004 | 0.01 | 10 | 2.6 | 0.38 | same |
| Smallest | RunEnd x76, Pco x1 | 53,748 | 0.01 | 110 | 3.0 | 0.74 | reads faster below 96 MB/s |
| None | Canonical x77 | 80,043,716 | 8.00 | 83 | 21.3 | 1.80 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 1,449,892 | 0.14 | 17 | 6.2 | 1.53 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 10,034,460 | 1.00 | 21 | 5.7 | 1.13 | reads slower at any throughput |
| hint RunEnd | RunEnd x77 | 88,004 | 0.01 | 10 | 2.6 | 0.36 | same |
| hint Zstd | Zstd x77 | 136,628 | 0.01 | 12 | 3.0 | 1.15 | reads slower at any throughput |

</details>

<details><summary>timestamps (ms, increasing, jittered): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 33,795,172 | 3.38 | 70 | 12.9 | 1.72 |  |
| Fastest | BitPacked x77 | 33,795,172 | 3.38 | 30 | 11.5 | 1.42 | reads faster at any throughput |
| Smallest | Pco x77 | 13,472,444 | 1.35 | 453 | 112.6 | 118.47 | reads faster below 204 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 43 | 19.3 | 1.57 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 33,795,172 | 3.38 | 156 | 11.2 | 1.45 | reads faster at any throughput |
| hint BitPacked | BitPacked x77 | 33,795,172 | 3.38 | 29 | 11.3 | 1.39 | reads faster at any throughput |
| hint RunEnd | BitPacked x77 | 33,795,172 | 3.38 | 49 | 11.1 | 1.41 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 20,700,796 | 2.07 | 182 | 80.8 | 35.05 | reads faster below 193 MB/s |

</details>

<details><summary>random in 0..999: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 12,544,284 | 1.25 | 22 | 5.5 | 1.54 |  |
| Fastest | BitPacked x77 | 12,544,284 | 1.25 | 32 | 6.9 | 1.67 | reads slower at any throughput |
| Smallest | BitPacked x76, Pco x1 | 12,543,924 | 1.25 | 344 | 5.1 | 1.34 | reads faster at any throughput |
| None | Canonical x77 | 80,043,716 | 8.00 | 43 | 21.5 | 1.82 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,652,724 | 1.27 | 70 | 6.5 | 1.25 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 12,544,284 | 1.25 | 23 | 4.9 | 1.15 | reads faster at any throughput |
| hint RunEnd | BitPacked x77 | 12,544,284 | 1.25 | 23 | 7.0 | 1.64 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 20,141,172 | 2.01 | 186 | 78.1 | 31.66 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,748 | 0.51 | 55 | 3.3 | 1.14 |  |
| Fastest | Dictionary x77 | 5,063,748 | 0.51 | 66 | 3.1 | 1.09 | reads faster at any throughput |
| Smallest | Dictionary x76, Pco x1 | 5,063,588 | 0.51 | 614 | 3.4 | 1.37 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 55 | 19.7 | 1.47 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,063,748 | 0.51 | 54 | 3.1 | 1.08 | reads faster at any throughput |
| hint BitPacked | Dictionary x77 | 5,063,748 | 0.51 | 127 | 3.1 | 1.07 | reads faster at any throughput |
| hint RunEnd | Dictionary x77 | 5,063,748 | 0.51 | 105 | 3.2 | 1.08 | same |
| hint Zstd | Zstd x77 | 10,539,396 | 1.05 | 139 | 51.4 | 24.40 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,932 | 0.50 | 84 | 3.1 | 1.05 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 46,295,652 | 4.63 | 38 | 12.9 | 1.51 |  |
| Fastest | BitPacked x77 | 46,295,652 | 4.63 | 64 | 16.5 | 1.96 | reads slower at any throughput |
| Smallest | Pco x77 | 580,924 | 0.06 | 423 | 72.5 | 69.30 | reads faster below 767 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 42 | 19.6 | 1.56 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 46,295,652 | 4.63 | 123 | 12.7 | 1.46 | same |
| hint BitPacked | BitPacked x77 | 46,295,652 | 4.63 | 37 | 13.2 | 1.55 | same |
| hint RunEnd | BitPacked x77 | 46,295,652 | 4.63 | 86 | 13.7 | 1.56 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 51,815,868 | 5.18 | 128 | 62.1 | 42.11 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 23,603,548 | 2.36 | 138 | 9.5 | 1.32 | reads faster at any throughput |

</details>

<details><summary>uniform 64-bit: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x77 | 80,043,732 | 8.00 | 43 | 27.0 | 2.14 |  |
| Fastest | Canonical x77 | 80,043,732 | 8.00 | 43 | 20.0 | 1.62 | reads faster at any throughput |
| Smallest | Canonical x77 | 80,043,732 | 8.00 | 350 | 20.1 | 1.70 | reads faster at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 43 | 23.3 | 1.64 | reads faster at any throughput |
| hint Dictionary | Canonical x77 | 80,043,732 | 8.00 | 152 | 20.0 | 1.58 | reads faster at any throughput |
| hint BitPacked | Canonical x77 | 80,043,732 | 8.00 | 117 | 19.9 | 1.68 | reads faster at any throughput |
| hint RunEnd | Canonical x77 | 80,043,732 | 8.00 | 114 | 24.1 | 2.27 | reads faster at any throughput |
| hint Zstd | Canonical x77 | 80,043,732 | 8.00 | 132 | 18.5 | 1.52 | reads faster at any throughput |

</details>

<details><summary>random in 0..999, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x82 | 13,800,460 | 1.38 | 25 | 6.3 | 1.35 |  |
| Fastest | BitPacked x82 | 13,800,460 | 1.38 | 23 | 7.6 | 1.97 | reads slower at any throughput |
| Smallest | Pco x82 | 12,567,268 | 1.26 | 311 | 45.3 | 44.57 | reads faster below 32 MB/s |
| None | Canonical x82 | 81,294,668 | 8.13 | 42 | 20.6 | 2.10 | reads slower at any throughput |
| hint Dictionary | Dictionary x82 | 12,676,396 | 1.27 | 89 | 10.0 | 1.52 | reads faster below 307 MB/s |
| hint BitPacked | BitPacked x82 | 13,800,460 | 1.38 | 27 | 6.3 | 1.34 | same |
| hint RunEnd | BitPacked x82 | 13,800,460 | 1.38 | 56 | 9.5 | 2.06 | reads slower at any throughput |
| hint Zstd | Zstd x82 | 19,283,940 | 1.93 | 169 | 65.0 | 30.96 | reads slower at any throughput |

</details>

### Floating point

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| prices (2 decimals) | Alp x77 | 2.50 | 47 | 10.5 | 1.33 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 47 | 3.1 | 1.08 |  |
| 1 000 distinct prices | Dictionary x77 | 1.28 | 90 | 9.8 | 1.84 | hint Alp: Alp x77, 1.28 B/value, scan 7.5 ms, take 1.31 ms; reads faster at any throughput |
| 100 003 distinct, repeating | Zstd x77 | 1.69 | 161 | 48.8 | 23.24 | Auto, 16 MiB chunks: Dictionary x5, 2.21 B/value, scan 11.0 ms, take 3.70 ms; reads faster above 138 MB/s; None: Canonical x77, 8.00 B/value, scan 19.8 ms, take 1.56 ms; reads faster above 2,178 MB/s |
| uniform in [0, 1) | AlpRd x77 | 6.91 | 60 | 22.9 | 2.46 |  |

<details><summary>prices (2 decimals): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Alp x77 | 25,049,732 | 2.50 | 47 | 10.5 | 1.33 |  |
| Fastest | Alp x77 | 25,049,732 | 2.50 | 50 | 9.6 | 1.20 | reads faster at any throughput |
| Smallest | Alp x77 | 25,048,868 | 2.50 | 880 | 9.9 | 1.38 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 43 | 19.3 | 1.59 | reads slower at any throughput |
| hint Dictionary | Alp x77 | 25,049,732 | 2.50 | 177 | 9.7 | 1.21 | reads faster at any throughput |
| hint Alp | Alp x77 | 25,049,732 | 2.50 | 48 | 9.7 | 1.23 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 41,240,260 | 4.12 | 295 | 66.1 | 36.34 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,748 | 0.51 | 47 | 3.1 | 1.08 |  |
| Fastest | Dictionary x77 | 5,063,748 | 0.51 | 41 | 3.1 | 1.07 | same |
| Smallest | Dictionary x76, Pco x1 | 5,063,588 | 0.51 | 560 | 3.4 | 1.33 | reads slower at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 27 | 20.2 | 1.65 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,063,748 | 0.51 | 42 | 3.5 | 1.19 | reads slower at any throughput |
| hint Alp | Dictionary x77 | 5,063,748 | 0.51 | 100 | 3.1 | 1.08 | same |
| hint Zstd | Zstd x77 | 10,545,668 | 1.05 | 139 | 47.7 | 22.21 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,932 | 0.50 | 66 | 3.0 | 1.03 | same |

</details>

<details><summary>1 000 distinct prices: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 12,756,260 | 1.28 | 90 | 9.8 | 1.84 |  |
| Fastest | Dictionary x77 | 12,756,260 | 1.28 | 75 | 7.8 | 1.42 | reads faster at any throughput |
| Smallest | Dictionary x77 | 12,751,356 | 1.28 | 499 | 6.8 | 1.61 | reads faster at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 46 | 23.2 | 1.73 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,756,260 | 1.28 | 68 | 6.3 | 1.28 | reads faster at any throughput |
| hint Alp | Alp x77 | 12,756,260 | 1.28 | 121 | 7.5 | 1.31 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 26,073,412 | 2.61 | 213 | 61.7 | 31.41 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 12,545,732 | 1.25 | 100 | 7.0 | 1.22 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x77 | 16,909,892 | 1.69 | 161 | 48.8 | 23.24 |  |
| Fastest | Zstd x77 | 16,909,892 | 1.69 | 174 | 47.9 | 22.40 | same |
| Smallest | Zstd x77 | 16,909,892 | 1.69 | 296 | 61.3 | 28.88 | reads slower at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 43 | 19.8 | 1.56 | reads faster above 2,178 MB/s |
| hint Dictionary | Zstd x77 | 16,909,892 | 1.69 | 260 | 63.0 | 28.51 | reads slower at any throughput |
| hint Alp | Zstd x77 | 16,909,892 | 1.69 | 284 | 47.8 | 22.96 | same |
| hint Zstd | Zstd x77 | 16,909,892 | 1.69 | 163 | 48.3 | 24.00 | same |
| Auto, 16 MiB chunks | Dictionary x5 | 22,129,988 | 2.21 | 132 | 11.0 | 3.70 | reads faster above 138 MB/s |

</details>

<details><summary>uniform in [0, 1): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | AlpRd x77 | 69,054,380 | 6.91 | 60 | 22.9 | 2.46 |  |
| Fastest | AlpRd x77 | 69,054,380 | 6.91 | 82 | 24.1 | 3.54 | reads slower at any throughput |
| Smallest | Pco x77 | 67,609,668 | 6.76 | 548 | 118.0 | 117.47 | reads faster below 15 MB/s |
| None | Canonical x77 | 80,043,732 | 8.00 | 45 | 18.9 | 1.57 | reads faster above 2,761 MB/s |
| hint Dictionary | AlpRd x77 | 69,054,380 | 6.91 | 288 | 21.2 | 2.62 | reads faster at any throughput |
| hint Alp | AlpRd x77 | 69,054,380 | 6.91 | 235 | 23.3 | 3.00 | same |
| hint Zstd | AlpRd x77 | 69,054,380 | 6.91 | 269 | 21.8 | 2.56 | same |

</details>

### Text

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| 16 cities, random order | Dictionary x175 | 0.51 | 124 | 7.7 | 1.60 |  |
| 10 000 distinct ids | Dictionary x306 | 2.54 | 1032 | 41.2 | 29.59 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 14.8 ms, take 3.78 ms; reads faster at any throughput |
| 10 000 distinct ids, 10 % null | Dictionary x306 | 2.45 | 995 | 54.5 | 36.86 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 13.5 ms, take 3.38 ms; reads faster at any throughput |
| unique UUIDs | Zstd x611 | 20.61 | 1792 | 309.4 | 301.45 | hint Fsst: Fsst x611, 22.44 B/value, scan 175.1 ms, take 13.05 ms; reads faster above 136 MB/s |
| log lines (~100 B) | Zstd x1221 | 13.71 | 1468 | 406.8 | 321.21 | hint Fsst: Fsst x1221, 22.01 B/value, scan 177.4 ms, take 13.64 ms; reads faster above 362 MB/s |

<details><summary>16 cities, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x175 | 5,117,284 | 0.51 | 124 | 7.7 | 1.60 |  |
| Fastest | Dictionary x175 | 5,117,284 | 0.51 | 141 | 6.1 | 1.39 | reads faster at any throughput |
| Smallest | Dictionary x175 | 5,117,284 | 0.51 | 323 | 6.4 | 1.37 | reads faster at any throughput |
| None | Canonical x175 | 96,746,756 | 9.67 | 183 | 46.6 | 20.78 | reads slower at any throughput |
| hint Dictionary | Dictionary x175 | 5,117,284 | 0.51 | 122 | 6.2 | 1.42 | reads faster at any throughput |
| hint Fsst | Fsst x175 | 41,142,948 | 4.11 | 315 | 42.9 | 8.95 | reads slower at any throughput |
| hint Zstd | Zstd x175 | 17,427,100 | 1.74 | 287 | 97.8 | 61.74 | reads slower at any throughput |

</details>

<details><summary>10 000 distinct ids: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 25,433,788 | 2.54 | 1032 | 41.2 | 29.59 |  |
| Fastest | Dictionary x306 | 25,433,788 | 2.54 | 1009 | 58.0 | 32.81 | reads slower at any throughput |
| Smallest | Dictionary x305, Zstd x1 | 23,452,500 | 2.35 | 2062 | 138.8 | 134.32 | reads faster below 20 MB/s |
| None | Canonical x306 | 140,098,172 | 14.01 | 225 | 55.6 | 40.79 | reads slower at any throughput |
| hint Dictionary | Dictionary x306 | 25,433,788 | 2.54 | 713 | 38.1 | 28.72 | reads faster at any throughput |
| hint Fsst | Fsst x306 | 62,863,260 | 6.29 | 461 | 47.5 | 8.41 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 27,097,628 | 2.71 | 430 | 92.2 | 63.15 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 18,014,012 | 1.80 | 446 | 14.8 | 3.78 | reads faster at any throughput |

</details>

<details><summary>10 000 distinct ids, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 24,539,284 | 2.45 | 995 | 54.5 | 36.86 |  |
| Fastest | Dictionary x306 | 24,539,284 | 2.45 | 944 | 40.5 | 34.87 | reads faster at any throughput |
| Smallest | Dictionary x305, Zstd x1 | 22,854,628 | 2.29 | 1984 | 148.6 | 139.29 | reads faster below 18 MB/s |
| None | Canonical x306 | 151,883,732 | 15.19 | 293 | 69.6 | 39.43 | reads slower at any throughput |
| hint Dictionary | Dictionary x306 | 24,539,284 | 2.45 | 817 | 39.9 | 28.75 | reads faster at any throughput |
| hint Fsst | Fsst x306 | 64,408,284 | 6.44 | 533 | 60.9 | 11.45 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 25,595,636 | 2.56 | 400 | 99.0 | 63.07 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 17,982,604 | 1.80 | 504 | 13.5 | 3.38 | reads faster at any throughput |

</details>

<details><summary>unique UUIDs: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x611 | 206,058,588 | 20.61 | 1792 | 309.4 | 301.45 |  |
| Fastest | Zstd x611 | 206,058,588 | 20.61 | 1702 | 302.5 | 306.99 | same |
| Smallest | Zstd x611 | 206,058,588 | 20.61 | 3473 | 299.3 | 307.14 | same |
| None | Canonical x611 | 360,155,516 | 36.02 | 281 | 109.3 | 96.06 | reads faster above 770 MB/s |
| hint Dictionary | Zstd x610, Fsst x1 | 206,059,580 | 20.61 | 6414 | 301.3 | 323.16 | same |
| hint Fsst | Fsst x611 | 224,384,604 | 22.44 | 1798 | 175.1 | 13.05 | reads faster above 136 MB/s |
| hint Zstd | Zstd x611 | 206,058,588 | 20.61 | 1761 | 305.3 | 320.92 | same |

</details>

<details><summary>log lines (~100 B): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x1221 | 137,107,188 | 13.71 | 1468 | 406.8 | 321.21 |  |
| Fastest | Zstd x1221 | 137,107,188 | 13.71 | 1450 | 393.2 | 317.69 | same |
| Smallest | Zstd x1221 | 137,107,188 | 13.71 | 2932 | 387.2 | 335.15 | same |
| None | Canonical x1221 | 725,468,444 | 72.55 | 790 | 206.6 | 158.59 | reads faster above 2,939 MB/s |
| hint Dictionary | Zstd x1221 | 137,107,188 | 13.71 | 6070 | 404.3 | 347.13 | same |
| hint Fsst | Fsst x1221 | 220,058,684 | 22.01 | 1838 | 177.4 | 13.64 | reads faster above 362 MB/s |
| hint Zstd | Zstd x1221 | 137,107,188 | 13.71 | 1398 | 401.5 | 320.92 | same |

</details>

### Booleans

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| half true | Canonical x10 | 0.13 | 10 | 0.4 | 0.46 |  |
| 1 % true | RunEnd x10 | 0.05 | 5 | 2.5 | 0.49 | None: Canonical x10, 0.13 B/value, scan 0.6 ms, take 0.66 ms; reads faster above 390 MB/s |

<details><summary>half true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x10 | 1,262,556 | 0.13 | 10 | 0.4 | 0.46 |  |
| Fastest | Canonical x10 | 1,262,556 | 0.13 | 0 | 0.5 | 0.46 | same |
| Smallest | Canonical x10 | 1,262,556 | 0.13 | 0 | 0.5 | 0.47 | same |
| None | Canonical x10 | 1,262,556 | 0.13 | 0 | 0.5 | 0.46 | same |
| hint RunEnd | Canonical x10 | 1,262,556 | 0.13 | 7 | 0.4 | 0.46 | same |

</details>

<details><summary>1 % true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x10 | 548,092 | 0.05 | 5 | 2.5 | 0.49 |  |
| Fastest | RunEnd x10 | 548,092 | 0.05 | 5 | 2.5 | 0.50 | same |
| Smallest | RunEnd x10 | 164,692 | 0.02 | 27 | 4.0 | 3.67 | reads faster below 246 MB/s |
| None | Canonical x10 | 1,262,556 | 0.13 | 13 | 0.6 | 0.66 | reads faster above 390 MB/s |
| hint RunEnd | RunEnd x10 | 548,092 | 0.05 | 6 | 2.4 | 0.49 | same |

</details>

### The profiles side by side

`Fastest` wrote what `Auto` wrote on 20 of 20 columns. `Smallest` wrote something else on *i64, sorted runs of 1 000*, *i64, timestamps (ms, increasing, jittered)*, *i64, random in 0..999*, *i64, 16 distinct, random order*, *i64, 100 003 distinct, repeating*, *i64, random in 0..999, 10 % null*, *f64, 16 distinct, random order*, *f64, uniform in [0, 1)*, *utf8, 10 000 distinct ids*, *utf8, 10 000 distinct ids, 10 % null*, and took up to 18.8 times `Auto`'s write, 880 ms against 47 ms on *f64, prices (2 decimals)*, since it tries every scheme on every chunk.
`None`, the plain form, made the largest file on 20 of 20 columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit a3dfc8ab, 2026-10-09 16:49 UTC.*
<!-- /results: tradeoffs -->

<!-- results: advice -->
## What the advice picks

`VortexSession.AdviseAsync` on the same columns of 10,000,000 rows, under five goals: whole scans with the
bytes read at 2 GB/s, 100 MB/s and 10 GB/s, a row in a thousand read by row, and the bytes alone.
A cell is what the advice takes where it departs from the writer's own choice, empty where it
does not; a column it never departs on is left out.

| column | scans at 2 GB/s | scans at 100 MB/s | scans at 10 GB/s | a row in 1 000 read by row | size |
|---|---|---|---|---|---|
| i64, sorted runs of 1 000 |  |  | `Zstd` |  |  |
| i64, timestamps (ms, increasing, jittered) |  | `Pco` |  | `Zstd` |  |
| i64, 100 003 distinct, repeating | 16 MiB chunks | `Pco` | 16 MiB chunks |  | `Dictionary`, 16 MiB chunks |
| i64, random in 0..999, 10 % null |  | `Dictionary` |  | `Dictionary` | `Dictionary` |
| f64, 100 003 distinct, repeating | 16 MiB chunks |  | 16 MiB chunks |  | `Dictionary`, 16 MiB chunks |
| f64, uniform in [0, 1) |  |  | `Canonical` |  |  |
| utf8, 10 000 distinct ids | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `OnPair` | 16 MiB chunks |
| utf8, 10 000 distinct ids, 10 % null | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `Zstd` | 16 MiB chunks |
| utf8, unique UUIDs | `Canonical` |  | `Canonical` | `Fsst` |  |
| utf8, log lines (~100 B) | `OnPair` |  | `Canonical` | `OnPair` |  |
| bool, 1 % true | `Canonical` |  | `Canonical` |  |  |

It keeps the writer's choice under every goal on the 9 other columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit a3dfc8ab, 2026-10-09 16:52 UTC.*
<!-- /results: advice -->

## Kernels, against their baselines

Each hot loop of the library is measured against a baseline, either the plain loop it replaces or
the floor of the work it does. BenchmarkDotNet times both in one process on one clock, after
checking that they give the same result. The Speedup column is the mean time of the baseline (the
row whose `MannWhitney(5%)` cell reads `Baseline`) over the row's own, so above 1.00x the row took
less. These figures come from the fast profile and show a direction. To quote a small difference,
run the reference profile on that class with `--full` before its name.

<!-- results: kernel:FastLanesKernelBenchmarks -->
### `FastLanesKernelBenchmarks`

| Method       | BitWidth | Mean       | Error     | StdDev    | Speedup | MannWhitney(5%) | ns/row | GB/s  | Allocated | Alloc Ratio |
|------------- |--------- |-----------:|----------:|----------:|--------:|---------------- |-------:|------:|----------:|------------:|
| &#39;i64 scalar&#39; | 17       |  95.737 μs | 1.3069 μs | 0.2023 μs |   1.00x | Baseline        |   1.46 |  5.48 |         - |          NA |
| &#39;i64 vector&#39; | 17       |   8.919 μs | 1.2853 μs | 0.1989 μs |   10.7x | Faster          |   0.14 | 58.78 |         - |          NA |
| &#39;i32 scalar&#39; | 17       | 107.554 μs | 0.4447 μs | 0.1155 μs |   0.89x | Same            |   1.64 |  2.44 |         - |          NA |
| &#39;i64 pack&#39;   | 17       |  11.620 μs | 0.2981 μs | 0.0461 μs |   8.24x | Faster          |   0.18 | 45.12 |         - |          NA |
| &#39;i32 pack&#39;   | 17       |  12.505 μs | 0.4404 μs | 0.0681 μs |   7.66x | Faster          |   0.19 | 20.96 |         - |          NA |
| &#39;i32 vector&#39; | 17       |   4.634 μs | 0.1085 μs | 0.0168 μs |   20.7x | Faster          |   0.07 | 56.58 |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.1, the fast profile; commit b5104f2 with uncommitted changes, 2026-09-27 03:15 UTC.*
<!-- /results: kernel:FastLanesKernelBenchmarks -->

<!-- results: kernel:RowEncodingBenchmarks -->
### `RowEncodingBenchmarks`

| Method                                   | Entry                | Mean       | Error      | StdDev    | ns/row | GB/s | Allocated |
|----------------------------------------- |--------------------- |-----------:|-----------:|----------:|-------:|-----:|----------:|
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nical [33]** |  **62.348 μs** |  **2.2289 μs** | **0.3449 μs** |  **15.22** | **2.82** |      **48 B** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nical [33] |  67.385 μs |  0.1931 μs | 0.0299 μs |  16.45 | 2.61 |      48 B |
| &#39;encode, then sort rows by key&#39;          | conta(...)nical [33] | 356.329 μs | 11.1380 μs | 2.8925 μs |  86.99 | 0.49 |      48 B |
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nulls [33]** |         **NA** |         **NA** |        **NA** |      **-** |    **-** |        **NA** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nulls [33] |         NA |         NA |        NA |      - |    - |        NA |
| &#39;encode, then sort rows by key&#39;          | conta(...)nulls [33] |         NA |         NA |        NA |      - |    - |        NA |
| **&#39;encode a batch to row keys&#39;**             | **encod(...)r1025 [31]** |   **9.784 μs** |  **0.0574 μs** | **0.0089 μs** |   **9.55** | **0.94** |      **48 B** |
| &#39;encode a batch to row keys, descending&#39; | encod(...)r1025 [31] |   9.722 μs |  0.0700 μs | 0.0108 μs |   9.48 | 0.95 |      48 B |
| &#39;encode, then sort rows by key&#39;          | encod(...)r1025 [31] |  50.392 μs |  7.0184 μs | 1.0861 μs |  49.16 | 0.18 |      48 B |

Benchmarks with issues:
  RowEncodingBenchmarks.'encode a batch to row keys': fast(MinIterationTime=50ms, Toolchain=InProcessEmitToolchain, IterationCount=5, IterationTime=100ms, MaxWarmupIterationCount=30, MinWarmupIterationCount=4) [Entry=conta(...)nulls [33]]
  RowEncodingBenchmarks.'encode a batch to row keys, descending': fast(MinIterationTime=50ms, Toolchain=InProcessEmitToolchain, IterationCount=5, IterationTime=100ms, MaxWarmupIterationCount=30, MinWarmupIterationCount=4) [Entry=conta(...)nulls [33]]
  RowEncodingBenchmarks.'encode, then sort rows by key': fast(MinIterationTime=50ms, Toolchain=InProcessEmitToolchain, IterationCount=5, IterationTime=100ms, MaxWarmupIterationCount=30, MinWarmupIterationCount=4) [Entry=conta(...)nulls [33]]

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.1, the fast profile; commit b5104f2 with uncommitted changes, 2026-09-27 03:15 UTC.*
<!-- /results: kernel:RowEncodingBenchmarks -->

<!-- results: kernel:CompressorBenchmarks -->
### `CompressorBenchmarks`

| Method                       | Column | Mean          | Error       | StdDev     | Speedup | MannWhitney(5%) | Allocated | Alloc Ratio |
|----------------------------- |------- |--------------:|------------:|-----------:|--------:|---------------- |----------:|------------:|
| &#39;Choose, the whole decision&#39; | i64    | 52,503.854 ns | 230.5989 ns | 35.6854 ns |   1.00x | Baseline        |         - |          NA |
| &#39;candidate: sequence&#39;        | i64    |     10.728 ns |   0.1120 ns |  0.0291 ns |  4,894x | Faster          |         - |          NA |
| &#39;candidate: bit packing&#39;     | i64    |  4,950.600 ns | 148.8102 ns | 23.0285 ns |   10.6x | Faster          |         - |          NA |
| &#39;candidate: FSST&#39;            | i64    |      1.846 ns |   0.0144 ns |  0.0038 ns | 28,448x | Faster          |         - |          NA |
| &#39;candidate: zstd&#39;            | i64    |  7,546.505 ns | 194.1759 ns | 30.0489 ns |   6.96x | Faster          |         - |          NA |
| &#39;candidate: ALP&#39;             | i64    |      2.599 ns |   0.0136 ns |  0.0035 ns | 20,204x | Faster          |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit a3dfc8ab, 2026-10-09 16:52 UTC.*
<!-- /results: kernel:CompressorBenchmarks -->

<!-- results: kernel:LanesBenchmarks -->
### `LanesBenchmarks`

| Method                 | Lanes | Mean       | Error     | StdDev   | Speedup | MannWhitney(5%) | Completed Work Items | Lock Contentions | Allocated | Alloc Ratio |
|----------------------- |------ |-----------:|----------:|---------:|--------:|---------------- |---------------------:|-----------------:|----------:|------------:|
| &#39;ours, n lanes&#39;        | 4     |   622.1 μs |  46.33 μs |  7.17 μs |   1.00x | Baseline        |              97.4875 |                - |   46910 B |        1.00 |
| &#39;reference, n workers&#39; | 4     | 1,311.8 μs | 184.25 μs | 47.85 μs |   0.47x | Slower          |                    - |                - |         - |        0.00 |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit 716b74b6, 2026-10-09 17:41 UTC.*
<!-- /results: kernel:LanesBenchmarks -->

## What this does not measure

* Nothing here runs on a cold cache. The page cache is warm for every run, on both sides, while
  upstream's own benchmarks flush it before each query.
* Cores are not pinned. Windows pins no process, and two hardware threads share each core's
  execution units, so both sides see all 32. Upstream measures on 94 pinned cores of a single kind.
* The operating system differs between the two pages. The file system, the page cache and the
  thread scheduler are Windows' here and macOS' on [the benchmark page](benchmarks.md), so a
  difference between the two pages comes from the whole machine as much as from the processor.
* Your data and your machine will differ. This is a handful of tables on one machine, and a column
  the compressor likes less, or a filter the zone maps cannot prune, moves these numbers more than
  either implementation does.
