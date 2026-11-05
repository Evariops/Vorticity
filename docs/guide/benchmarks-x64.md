# Benchmarks on x64

This is [the benchmark page](benchmarks.md) on an x64 machine: an AMD Ryzen 9 7950X (Zen 4, 16
cores and 32 threads, AVX-512) under Windows 11, against the same Vortex Rust 0.90.0 built the same
way for that machine. The instruments, files, rows and rules are the same as on that page, section
for section. What changes is the processor, the operating system and the file system under the page
cache. [05-benchmarks.md](../design/05-benchmarks.md) explains what is compared and how each
instrument measures, and [bench/README.md](../../bench/README.md) how to run them.

Vortex™ is a trademark of LF Projects, LLC. Vorticity is an independent implementation,
not affiliated with or endorsed by the Vortex project or LF Projects, LLC.

Rust's ratios on this page belong to this machine. Its `vxbench` is built here, while the ceilings
that `--ratio-check` and `--throughput --check` enforce are set on the arm64 machine against its own
build, so the in-process axes, the takes and the writes on this page are measured but held to no
ceiling. The per-encoding corpus is the generator's current one, the same 61 files as on the arm64
page, written by Vortex 0.90.0. The kernel tables' speedups are computed from the Mean column of
their run.

The ground rules:

* Vorticity runs as a Native AOT binary built for the machine's instruction set, with the
  workstation garbage collector, where a section says so, and on the JIT otherwise. Vortex Rust
  0.90.0 is built the way upstream builds its own benchmarks (mimalloc, `-C target-cpu=native`, one
  codegen unit, no LTO), through [tools/vxbench-rs](../../tools/vxbench-rs). `target-cpu=native`
  gives its compiler this machine's instruction set, AVX-512 included, just as .NET's JIT has it.
  Both sides decode every value they return to its plain form (a constant column kept as one
  value on both), read every value they return, the same bytes on both, and must return the
  same rows or the run fails.
* Rust maps the file and reads it where it lies. Vorticity reads it as a session that keeps no
  mapping does on Windows: it copies the segments a scan reads, which costs less than mapping the
  file for one scan and dropping it, and maps the file only for a scan of many segments smaller
  than a page or for a large segment a decode touches little of. A column stored in its plain form
  is then a copy on our side and a view of the mapping on Rust's, and each side reads its values
  from there.
* Rust gets its faster setting. On one core, every Rust figure is the faster of its two ways of
  splitting a scan (its default, and one split per chunk), measured file by file. On all cores it
  keeps its default, as on [the benchmark page](benchmarks.md).
* Speedup is Rust's time over Vorticity's. Above 1.00x, Vorticity took less.
* Throughput is the plain size of the rows returned divided by the time, as on [the benchmark
  page](benchmarks.md).
* All cores means the 32 hardware threads, simultaneous multithreading included, where the arm64
  page has 14 cores of two kinds.
* The page cache is warm for every run, and every call opens its file anew, on both sides, as on
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
* **The same work on both sides.** Rust maps the file and reads it where it lies, through
  `open_buffer` over a `memmap2` mapping. Vorticity reads it as a session that keeps no mapping
  does on Windows: it copies the segments a scan reads, and maps the file only for many segments
  smaller than a page or for a large segment a decode touches little of. Both decode every value
  they return to its plain form, a constant column kept as one value on both, and read every
  value they return, the same bytes on both; a write hands its bytes to a sink that keeps none,
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

| column | Vorticity's, 2,450,932 bytes | Vortex Rust's, 7,904,060 bytes |
|---|---|---|
| `monotone` | 16 chunks of `vortex.sequence` | 8 chunks of `vortex.sequence` |
| `value` | 16 chunks of `vortex.zstd` | 8 chunks of `vortex.alprd` |
| `label` | 16 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 2 chunks of `fastlanes.bitpacked` |
| `flag` | 16 chunks of `vortex.bool` | one chunk of `vortex.bool` |

#### One core

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.6) | 1.0 (1.0-1.1) | 1.84x | — | — | 2.9 KiB | 11 / 8 MiB |
| `open` | Vortex Rust's | 0.5 (0.5-0.6) | 1.0 (1.0-1.0) | 1.82x | — | — | 3.7 KiB | 11 / 8 MiB |
| `scan` | Vorticity's | 6.8 (6.8-7.1) | 20.6 (20.5-20.7) | 3.01x | 3.9 | 1.3 | 19.9 KiB | 14 / 44 MiB |
| `scan` | Vortex Rust's | 3.6 (3.5-4.0) | 8.3 (8.2-8.9) | 2.31x | 7.4 | 3.2 | 16.2 KiB | 21 / 50 MiB |
| `project` | Vorticity's | 0.9 (0.9-0.9) | 2.4 (2.4-2.5) | 2.57x | 9.0 | 3.5 | 13.7 KiB | 12 / 17 MiB |
| `project` | Vortex Rust's | 1.0 (1.0-1.1) | 2.3 (2.2-2.4) | 2.18x | 8.1 | 3.7 | 11.5 KiB | 13 / 17 MiB |
| `filter-narrow` | Vorticity's | 2.1 (2.0-2.2) | 2.2 (2.2-2.4) | 1.06x | 0.1 | 0.1 | 47.6 KiB | 15 / 12 MiB |
| `filter-narrow` | Vortex Rust's | 1.8 (1.8-1.9) | 2.0 (2.0-2.1) | 1.08x | 0.1 | 0.1 | 27.2 KiB | 17 / 12 MiB |
| `filter-wide` | Vorticity's | 4.2 (4.2-4.5) | 11.4 (11.3-13.3) | 2.67x | 3.1 | 1.2 | 29.7 KiB | 15 / 28 MiB |
| `filter-wide` | Vortex Rust's | 2.8 (2.8-3.0) | 5.1 (5.0-5.3) | 1.82x | 4.7 | 2.6 | 27.0 KiB | 21 / 31 MiB |
| `take` | Vorticity's | 5.9 (5.8-5.9) | 8.5 (8.4-8.6) | 1.44x | — | — | 43.7 KiB | 13 / 13 MiB |
| `take` | Vortex Rust's | 2.0 (2.0-2.1) | 2.8 (2.8-3.0) | 1.38x | — | — | 40.0 KiB | 15 / 15 MiB |
| `write` | Vorticity's | 31.0 (31.0-31.3) | 73.1 (72.4-73.7) | 2.36x | 0.9 | 0.4 | 37.6 KiB | 20 / 74 MiB |
| `write` | Vortex Rust's | 27.7 (27.5-28.5) | 60.7 (60.2-61.2) | 2.19x | 1.0 | 0.4 | 33.8 KiB | 27 / 79 MiB |
| `append` | Vorticity's | 11.2 (11.0-11.3) | not asked | n/a | — | — | 879.0 KiB | 21 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.5-0.6) | 2.5 (2.5-2.7) | 4.46x | — | — | 2.9 KiB | 11 / 9 MiB |
| `open` | Vortex Rust's | 0.6 (0.5-0.7) | 2.7 (2.5-2.8) | 4.44x | — | — | 3.7 KiB | 11 / 9 MiB |
| `scan` | Vorticity's | 3.8 (3.8-4.1) | 6.1 (5.9-6.3) | 1.60x | 7.0 | 4.4 | 40.1 KiB | 46 / 48 MiB |
| `scan` | Vortex Rust's | 4.0 (3.7-4.0) | 6.3 (6.3-6.4) | 1.60x | 6.7 | 4.2 | 34.3 KiB | 59 / 52 MiB |
| `project` | Vorticity's | 1.7 (1.7-1.8) | 3.5 (3.4-3.5) | 1.99x | 4.8 | 2.4 | 31.5 KiB | 20 / 19 MiB |
| `project` | Vortex Rust's | 1.6 (1.6-1.8) | 3.5 (3.3-4.0) | 2.11x | 5.1 | 2.4 | 22.2 KiB | 20 / 19 MiB |
| `filter-narrow` | Vorticity's | 1.7 (1.7-1.8) | 4.0 (3.9-4.1) | 2.35x | 0.2 | 0.1 | 51.0 KiB | 14 / 14 MiB |
| `filter-narrow` | Vortex Rust's | 1.7 (1.7-1.8) | 3.9 (3.7-4.0) | 2.32x | 0.2 | 0.1 | 33.3 KiB | 15 / 14 MiB |
| `filter-wide` | Vorticity's | 3.1 (2.9-3.2) | 5.4 (5.2-5.5) | 1.73x | 4.3 | 2.5 | 40.3 KiB | 31 / 33 MiB |
| `filter-wide` | Vortex Rust's | 3.1 (2.9-3.4) | 5.0 (4.8-5.3) | 1.64x | 4.3 | 2.6 | 34.1 KiB | 39 / 36 MiB |
| `take` | Vorticity's | 4.3 (4.0-4.4) | 4.5 (4.2-4.6) | 1.04x | — | — | 130.0 KiB | 28 / 23 MiB |
| `take` | Vortex Rust's | 2.6 (2.3-2.7) | 4.2 (4.0-4.4) | 1.63x | — | — | 98.7 KiB | 22 / 18 MiB |
| `write` | Vorticity's | 13.0 (12.9-13.2) | 19.6 (19.3-20.1) | 1.51x | 2.1 | 1.4 | 2.2 MiB | 59 / 118 MiB |
| `write` | Vortex Rust's | 12.9 (12.7-13.0) | 20.6 (19.7-21.8) | 1.59x | 2.1 | 1.3 | 1.7 MiB | 71 / 118 MiB |
| `append` | Vorticity's | 10.8 (10.4-10.9) | not asked | n/a | — | — | 1.4 MiB | 23 / — MiB |

### 10,485,760 rows

| column | Vorticity's, 24,486,132 bytes | Vortex Rust's, 78,987,108 bytes |
|---|---|---|
| `monotone` | 160 chunks of `vortex.sequence` | 80 chunks of `vortex.sequence` |
| `value` | 160 chunks of `vortex.zstd` | 80 chunks of `vortex.alprd` |
| `label` | 160 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 20 chunks of `fastlanes.bitpacked` |
| `flag` | 160 chunks of `vortex.bool` | 3 chunks of `vortex.bool` |

#### One core

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.5-0.6) | 1.0 (1.0-1.1) | 1.87x | — | — | 2.9 KiB | 11 / 8 MiB |
| `open` | Vortex Rust's | 0.5 (0.5-0.6) | 1.0 (1.0-1.1) | 1.90x | — | — | 3.7 KiB | 11 / 8 MiB |
| `scan` | Vorticity's | 57.1 (56.9-57.8) | 182.5 (181.8-184.9) | 3.20x | 4.7 | 1.5 | 89.7 KiB | 14 / 291 MiB |
| `scan` | Vortex Rust's | 22.3 (21.8-22.7) | 72.8 (72.6-75.1) | 3.27x | 12.0 | 3.7 | 33.6 KiB | 23 / 343 MiB |
| `project` | Vorticity's | 2.0 (2.0-2.0) | 11.4 (11.3-11.6) | 5.71x | 41.9 | 7.3 | 51.4 KiB | 12 / 74 MiB |
| `project` | Vortex Rust's | 2.4 (2.4-2.5) | 11.5 (11.2-11.7) | 4.75x | 34.7 | 7.3 | 21.9 KiB | 13 / 74 MiB |
| `filter-narrow` | Vorticity's | 2.5 (2.4-2.9) | 4.6 (4.5-4.8) | 1.82x | 1.0 | 0.6 | 123.3 KiB | 16 / 16 MiB |
| `filter-narrow` | Vortex Rust's | 2.5 (2.4-2.5) | 2.9 (2.8-3.5) | 1.18x | 1.1 | 0.9 | 68.2 KiB | 20 / 16 MiB |
| `filter-wide` | Vorticity's | 29.6 (29.5-29.8) | 97.2 (96.8-97.9) | 3.29x | 4.5 | 1.4 | 123.1 KiB | 15 / 184 MiB |
| `filter-wide` | Vortex Rust's | 14.5 (14.2-14.6) | 39.4 (39.2-39.5) | 2.73x | 9.2 | 3.4 | 67.9 KiB | 24 / 210 MiB |
| `take` | Vorticity's | 23.3 (23.2-25.8) | 68.6 (68.1-76.2) | 2.94x | — | — | 113.6 KiB | 13 / 37 MiB |
| `take` | Vortex Rust's | 5.3 (5.2-5.3) | 7.6 (7.3-7.7) | 1.44x | — | — | 61.6 KiB | 21 / 27 MiB |
| `write` | Vorticity's | 273.6 (272.0-274.6) | 645.0 (641.8-651.7) | 2.36x | 1.0 | 0.4 | 112.4 KiB | 22 / 381 MiB |
| `write` | Vortex Rust's | 237.8 (236.1-241.3) | 524.8 (521.7-537.2) | 2.21x | 1.1 | 0.5 | 56.3 KiB | 30 / 454 MiB |
| `append` | Vorticity's | 47.8 (47.4-48.3) | not asked | n/a | — | — | 2.2 MiB | 27 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.5-0.6) | 2.6 (2.5-2.6) | 4.58x | — | — | 2.9 KiB | 11 / 9 MiB |
| `open` | Vortex Rust's | 0.6 (0.5-0.6) | 2.5 (2.4-2.6) | 4.46x | — | — | 3.7 KiB | 11 / 9 MiB |
| `scan` | Vorticity's | 16.7 (16.0-17.3) | 24.6 (24.3-26.9) | 1.47x | 15.9 | 10.8 | 137.7 KiB | 66 / 304 MiB |
| `scan` | Vortex Rust's | 19.8 (19.6-20.9) | 32.2 (31.7-32.6) | 1.63x | 13.5 | 8.3 | 48.9 KiB | 69 / 354 MiB |
| `project` | Vorticity's | 3.1 (3.0-3.1) | 7.5 (7.3-7.6) | 2.43x | 27.1 | 11.1 | 90.1 KiB | 29 / 81 MiB |
| `project` | Vortex Rust's | 3.7 (3.4-5.1) | 7.6 (7.2-8.7) | 2.04x | 22.5 | 11.0 | 69.4 KiB | 45 / 82 MiB |
| `filter-narrow` | Vorticity's | 2.3 (2.1-2.7) | 5.6 (5.4-6.0) | 2.42x | 1.1 | 0.5 | 128.1 KiB | 17 / 20 MiB |
| `filter-narrow` | Vortex Rust's | 2.5 (2.4-2.8) | 4.9 (4.9-5.3) | 1.97x | 1.1 | 0.5 | 71.2 KiB | 20 / 21 MiB |
| `filter-wide` | Vorticity's | 11.1 (11.0-11.6) | 16.4 (16.3-16.8) | 1.48x | 12.0 | 8.1 | 188.1 KiB | 87 / 190 MiB |
| `filter-wide` | Vortex Rust's | 16.6 (16.0-18.9) | 20.1 (19.7-20.6) | 1.21x | 8.0 | 6.6 | 1.7 MiB | 227 / 217 MiB |
| `take` | Vorticity's | 10.1 (9.7-10.5) | 11.2 (11.1-11.5) | 1.11x | — | — | 266.3 KiB | 61 / 65 MiB |
| `take` | Vortex Rust's | 4.3 (4.0-4.9) | 7.1 (7.0-7.4) | 1.66x | — | — | 183.1 KiB | 29 / 31 MiB |
| `write` | Vorticity's | 75.5 (74.5-76.1) | 124.9 (117.7-125.6) | 1.65x | 3.5 | 2.1 | 2.3 MiB | 81 / 469 MiB |
| `write` | Vortex Rust's | 79.2 (78.4-84.3) | 129.1 (128.4-134.2) | 1.63x | 3.4 | 2.1 | 2.0 MiB | 83 / 544 MiB |
| `append` | Vorticity's | 45.0 (44.5-45.3) | not asked | n/a | — | — | 2.3 MiB | 60 / — MiB |

`open`: open the file and read no rows. `scan`: read every column of every row. `project`: read one column of four. `filter-narrow`: read the rows of a band holding about one in a hundred. `filter-wide`: read the rows of a band holding about half. `take`: take a thousand rows spread across the file. `write`: read the file and encode it back out. `append`: append a tenth of the rows to a copy of the file.

**On one core.** Vorticity took less time than Rust on 28 of 28 compared rows.
The highest speedup is `project` at 10,485,760 rows on Vorticity's file (5.71x), the lowest
`filter-narrow` at 1,048,576 rows on Vorticity's file (1.06x).

**On all 32 cores.** Vorticity took less time than Rust on 28 of 28 compared rows.
The highest speedup is `open` at 10,485,760 rows on Vorticity's file (4.58x), the lowest
`take` at 1,048,576 rows on Vorticity's file (1.04x).

**The process start** is not in the figures: 38 ms for Vorticity's native binary and 24 ms for Rust's,
most of it the operating system starting a binary, and 98 ms for the managed runtime on the JIT.

**Rust's splits.** On one core, one split per chunk was the faster of its two on 8 of 14 rows on Vorticity's file and 11 of 14 rows on Vortex Rust's file;
the two differ only where a chunk holds more than 100,000 rows.

**Not asked of Rust**: `append`, which its harness has no entry point for; those figures
are Vorticity's alone.

### A first call on the JIT

The same scenarios in the framework-dependent build, under `dotnet`, on one core: what a
process that is not compiled ahead of time pays the first time, the code compiling as it runs.
Milliseconds, the median of the same runs.

| scenario | 1,048,576 rows, Vorticity's file | 1,048,576 rows, Vortex Rust's file | 10,485,760 rows, Vorticity's file | 10,485,760 rows, Vortex Rust's file |
|---|---:|---:|---:|---:|
| `open` | 20.6 | 20.4 | 20.4 | 20.5 |
| `scan` | 125.1 | 114.4 | 237.1 | 146.8 |
| `project` | 73.8 | 80.6 | 76.3 | 83.8 |
| `filter-narrow` | 169.3 | 147.6 | 160.8 | 156.0 |
| `filter-wide` | 149.7 | 144.4 | 207.6 | 171.3 |
| `take` | 135.1 | 108.5 | 186.6 | 116.7 |
| `write` | 350.6 | 342.8 | 948.5 | 823.0 |
| `append` | 352.9 | — | 430.5 | — |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; Vortex 0.90.0, rustc 1.98.1 (48a229cea 2026-09-01); commit 96d3ae37, 2026-11-05 07:52 UTC.*
<!-- /results: scenarios -->

<!-- results: decoding -->
## Decoding, per encoding

One file per encoding, and per notable shape of one, written by Rust's writer
(`bench/gen-throughput.sh`): a million rows each, `table_wide` fifty columns of fifty thousand.
Each side scans the file, every value decoded to its plain form, 12 times in a process of its own on
one core; a figure is the median of the last 10 scans, and the median of 3 processes: what a
decoder costs once warm, and every value each scan returns is read, the same bytes on both sides.
Each scan opens the file anew, on both sides. Rust maps it and reads it where it lies: a column
stored in its plain form is a view of the mapping, read where it lies. Vorticity copies the
segment of each file, as a session that keeps no mapping does on Windows, and reads the copy.
Throughput is over the plain size, as above.

| encoding | rows | Vorticity, ns/row | Vortex Rust, ns/row | speedup | Vorticity, GB/s | Vortex Rust, GB/s |
|---|---:|---:|---:|---:|---:|---:|
| `alp` | 1,000,000 | 0.89 | 2.59 | 2.91x | 9.0 | 3.1 |
| `alp_no_patches` | 1,000,000 | 0.58 | 1.91 | 3.28x | 13.7 | 4.2 |
| `alp_patched_no_chunk_offsets` | 1,000,000 | 0.60 | 1.87 | 3.11x | 13.3 | 4.3 |
| `alprd` | 1,000,000 | 0.80 | 1.62 | 2.02x | 9.9 | 4.9 |
| `bool` | 1,000,000 | 0.14 | 0.18 | 1.28x | 0.9 | 0.7 |
| `bool_bit_offset3` | 1,000,000 | 0.13 | 0.17 | 1.30x | 0.9 | 0.7 |
| `bool_bit_offset7` | 1,000,000 | 0.13 | 0.17 | 1.28x | 0.9 | 0.7 |
| `bool_bit_offset_straddle` | 1,000,000 | 0.13 | 0.17 | 1.31x | 0.9 | 0.7 |
| `bytebool` | 1,000,000 | 0.16 | 0.33 | 2.04x | 0.8 | 0.4 |
| `chunked` | 1,000,000 | 0.62 | 2.19 | 3.55x | 13.0 | 3.7 |
| `chunked_bool` | 1,000,000 | 0.14 | 0.19 | 1.34x | 0.9 | 0.7 |
| `chunked_decimal` | 1,000,000 | 0.62 | 1.96 | 3.15x | 12.9 | 4.1 |
| `chunked_empty_chunks` | 1,000,000 | 0.62 | 2.13 | 3.43x | 12.9 | 3.8 |
| `chunked_mixed_validity` | 1,000,000 | 0.66 | 2.02 | 3.05x | 12.2 | 4.0 |
| `chunked_one_chunk` | 1,000,000 | 0.63 | 2.07 | 3.28x | 12.7 | 3.9 |
| `chunked_varbinview` | 1,000,000 | 3.36 | 8.59 | 2.56x | 4.0 | 1.6 |
| `constant` | 1,000,000 | 0.13 | 0.15 | 1.17x | 63.2 | 53.9 |
| `datetimeparts` | 1,000,000 | 1.11 | 2.86 | 2.59x | 7.2 | 2.8 |
| `decimal` | 1,000,000 | 0.61 | 2.02 | 3.29x | 13.0 | 4.0 |
| `decimal_byte_parts` | 1,000,000 | 0.61 | 2.05 | 3.35x | 13.1 | 3.9 |
| `dict` | 1,000,000 | 0.83 | 1.41 | 1.69x | 7.2 | 4.3 |
| `dict_nullable_codes` | 1,000,000 | 1.13 | 1.88 | 1.65x | 4.9 | 3.0 |
| `dict_nullable_values_nonnull_codes` | 1,000,000 | 0.96 | 1.15 | 1.20x | 6.0 | 5.0 |
| `dict_u64_codes` | 1,000,000 | 1.12 | 2.06 | 1.84x | 5.4 | 2.9 |
| `dict_u8_codes` | 1,000,000 | 0.71 | 0.94 | 1.32x | 11.2 | 8.5 |
| `ext` | 1,000,000 | 0.62 | 2.05 | 3.31x | 12.9 | 3.9 |
| `fastlanes_bitpacked` | 1,000,000 | 0.25 | 0.41 | 1.64x | 15.8 | 9.6 |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,000,000 | 0.27 | 0.42 | 1.58x | 14.9 | 9.4 |
| `fastlanes_delta` | 1,000,000 | 0.70 | 2.06 | 2.93x | 11.4 | 3.9 |
| `fastlanes_for` | 1,000,000 | 0.59 | 1.50 | 2.54x | 13.6 | 5.3 |
| `fastlanes_rle` | 1,000,000 | 0.47 | 0.79 | 1.66x | 8.4 | 5.1 |
| `fixed_size_list` | 1,000,000 | 0.95 | 3.03 | 3.20x | 12.7 | 4.0 |
| `fsst` | 1,000,000 | 10.7 | 14.3 | 1.34x | 5.2 | 3.9 |
| `list` | 1,000,000 | 1.29 | 3.22 | 2.49x | 7.7 | 3.1 |
| `listview` | 1,000,000 | 2.38 | 5.12 | 2.15x | 4.2 | 2.0 |
| `map` | 1,000,000 | 4.59 | 12.4 | 2.71x | 4.4 | 1.6 |
| `masked` | 1,000,000 | 0.37 | 1.08 | 2.89x | 11.1 | 3.8 |
| `masked_all_invalid` | 1,000,000 | 0.36 | 1.08 | 2.96x | 11.0 | 3.7 |
| `masked_all_valid` | 1,000,000 | 0.36 | 1.08 | 3.00x | 11.1 | 3.7 |
| `null` | 1,000,000 | 0.12 | 0.15 | 1.21x | — | — |
| `onpair` | 1,000,000 | 5.39 | 9.44 | 1.75x | 4.0 | 2.3 |
| `parquet_variant` | 1,000,000 | 2.58 | 16.4 | 6.35x | 4.6 | 0.7 |
| `pco` | 1,000,000 | 0.47 | 1.28 | 2.73x | 17.1 | 6.3 |
| `pco_f32` | 1,000,000 | 2.30 | 3.06 | 1.33x | 1.7 | 1.3 |
| `pco_i16` | 1,000,000 | 1.83 | 2.64 | 1.44x | 1.1 | 0.8 |
| `pco_nullable` | 1,000,000 | 2.91 | 4.47 | 1.54x | 2.8 | 1.8 |
| `pco_u32` | 1,000,000 | 2.80 | 4.30 | 1.54x | 1.4 | 0.9 |
| `primitive` | 1,000,000 | 0.61 | 2.03 | 3.33x | 13.1 | 3.9 |
| `runend` | 1,000,000 | 0.19 | 0.25 | 1.31x | 21.2 | 16.2 |
| `sequence` | 1,000,000 | 0.24 | 0.27 | 1.13x | 33.3 | 29.4 |
| `sparse` | 1,000,000 | 0.21 | 0.26 | 1.26x | 19.1 | 15.1 |
| `struct` | 1,000,000 | 1.75 | 6.41 | 3.66x | 8.5 | 2.3 |
| `table_mixed` | 1,000,000 | 13.7 | 30.3 | 2.22x | 5.1 | 2.3 |
| `table_wide` | 50,000 | 38.3 | 101.8 | 2.66x | 10.4 | 3.9 |
| `varbin` | 1,000,000 | 2.06 | 8.65 | 4.20x | 7.7 | 1.8 |
| `varbinview` | 1,000,000 | 3.10 | 7.24 | 2.34x | 4.4 | 1.9 |
| `variant` | 1,000,000 | 0.13 | 0.16 | 1.19x | 121.7 | 101.9 |
| `zigzag` | 1,000,000 | 0.35 | 1.04 | 2.99x | 11.6 | 3.9 |
| `zstd` | 1,000,000 | 6.63 | 10.9 | 1.64x | 4.8 | 2.9 |
| `zstd_buffers` | 1,000,000 | 0.48 | 0.51 | 1.07x | 16.7 | 15.6 |
| `zstd_nullable` | 1,000,000 | 1.86 | 3.88 | 2.09x | 4.4 | 2.1 |

Rust is timed under each of its two ways of splitting a scan, three processes each, and its
figure is the faster: its default cuts a chunk of more than 100,000 rows into splits of 100,000 and
decodes the whole chunk again for each, which on these files of one chunk of a million rows is ten
times; one split per chunk decodes it once, but builds a chunk made of smaller arrays in one
piece. One split per chunk was the faster on 52 of 61 files, its default on `chunked`, `chunked_empty_chunks`, `chunked_mixed_validity`, `chunked_one_chunk`, `dict_u64_codes`, `fixed_size_list`, `masked`, `masked_all_invalid`, `table_wide`.

Vorticity decoded 61 of 61 files in less time than Rust; the median speedup is 2.15x.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; Vortex 0.90.0, rustc 1.98.1 (48a229cea 2026-09-01); commit 96d3ae37, 2026-11-05 07:52 UTC.*
<!-- /results: decoding -->

<!-- results: in-process -->
## In one process, after warm-up

Both readers called in turn in one process, Rust's through a C ABI, against one clock. A round times
both, and its speedup is Rust's time over Vorticity's: at least 21 rounds after a 2-second warm-up,
until the 95 % interval of their median is within 5 % of it. Our side runs on the JIT, warmed. Each
call opens its file anew, Rust mapping it and Vorticity reading it as a session that keeps no
mapping does on Windows, and asks both the same question. Each axis is
one the `--ratio-check` gate holds to a ceiling; what each one reads and asks is in
[05-benchmarks.md](../design/05-benchmarks.md) §3.

| axis | Vorticity, µs | Vortex Rust, µs | speedup | 95 % interval |
|---|---:|---:|---:|---|
| full scan | 837.6 | 2,215.5 | 2.65x | 2.64 to 2.67 |
| projected scan, 1 of 5 columns | 233.7 | 442.8 | 1.90x | 1.89 to 1.92 |
| open to first batch | 229.6 | 2,196.4 | 9.63x | 9.40 to 10.0 |
| open, footer only | 124.5 | 196.3 | 1.57x | 1.56 to 1.60 |
| read and write back | 5,242.2 | 15,504.4 | 2.93x | 2.90 to 3.14 |
| filtered scan, 1% band | 203.3 | 587.5 | 2.87x | 2.84 to 2.91 |
| filtered scan, half the rows | 347.1 | 918.6 | 2.67x | 2.61 to 2.70 |
| scattered take, 64 of 64 splits | 645.5 | 1,835.3 | 2.84x | 2.83 to 2.87 |
| key order, sorted column, 1% band | 203.0 | 298.4 | 1.46x | 1.44 to 1.47 |
| key order, uncorrelated, 64 rows | 315.3 | 406.8 | 1.26x | 1.19 to 1.31 |
| count, exact cover, 1% band | 286.4 | 354.0 | 1.28x | 1.25 to 1.37 |
| filtered scan, string equality, fsst | 399.0 | 654.9 | 1.66x | 1.59 to 1.70 |
| filtered scan, string prefix, fsst | 940.8 | 1,213.8 | 1.29x | 1.27 to 1.30 |
| filtered scan, string equality, dict | 193.4 | 288.1 | 1.49x | 1.48 to 1.50 |
| filtered scan, string prefix, dict | 219.8 | 417.6 | 1.89x | 1.88 to 1.90 |
| filtered scan, band, runend | 193.0 | 264.1 | 1.37x | 1.35 to 1.40 |
| filtered scan, band, bitpacked | 198.7 | 256.5 | 1.29x | 1.29 to 1.31 |
| full scan, 1M table | 15,784.9 | 35,344.1 | 2.20x | 2.14 to 2.23 |
| projected scan, 1 of 50 columns | 265.5 | 304.1 | 1.14x | 1.11 to 1.17 |

Vorticity took less time on 19 of 19 axes.
Rust is timed under the faster of its two splits, axis by axis: one split per chunk on 8 of 19, its default on the other 11, one split per chunk on open to first batch; read and write back; filtered scan, 1% band; scattered take, 64 of 64 splits; count, exact cover, 1% band; filtered scan, string equality, fsst; filtered scan, string equality, dict; full scan, 1M table.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; Vortex 0.90.0 through the C ABI of `tools/vxbench-rs`, binary 2ab72ea31074; commit 96d3ae37, 2026-11-05 07:53 UTC.*
<!-- /results: in-process -->

<!-- results: take -->
## Taking rows, per encoding

The files of the per-encoding corpus again, 64 rows spread evenly over each, one every 15,625: what a
decoder costs to reach a few rows rather than all of them.
Each call opens the file anew: Rust maps it, and Vorticity reads it as a session that keeps no
mapping does on Windows.
Both implementations in one process, Rust's through a C ABI, taking turns against one clock:
at least 9 rounds after a 1-second warm-up per file, our side on the JIT, warmed.

| encoding | Vorticity, µs | Vortex Rust, µs | speedup |
|---|---:|---:|---:|
| `alp` | 735 | 575 | 0.79x |
| `alp_no_patches` | 239 | 260 | 1.09x |
| `alp_patched_no_chunk_offsets` | 244 | 271 | 1.09x |
| `alprd` | 268 | 279 | 1.03x |
| `bool` | 116 | 175 | 1.49x |
| `bool_bit_offset3` | 120 | 177 | 1.47x |
| `bool_bit_offset7` | 118 | 177 | 1.49x |
| `bool_bit_offset_straddle` | 118 | 179 | 1.52x |
| `bytebool` | 133 | 315 | 2.36x |
| `chunked` | 233 | 265 | 1.15x |
| `chunked_bool` | 125 | 198 | 1.60x |
| `chunked_decimal` | 237 | 272 | 1.14x |
| `chunked_empty_chunks` | 239 | 268 | 1.11x |
| `chunked_mixed_validity` | 256 | 303 | 1.17x |
| `chunked_one_chunk` | 234 | 259 | 1.12x |
| `chunked_varbinview` | 398 | 9,109 | 23.7x |
| `constant` | 109 | 158 | 1.46x |
| `datetimeparts` | 380 | 409 | 1.07x |
| `decimal` | 230 | 248 | 1.07x |
| `decimal_byte_parts` | 240 | 255 | 1.06x |
| `dict` | 224 | 248 | 1.11x |
| `dict_nullable_codes` | 243 | 276 | 1.13x |
| `dict_nullable_values_nonnull_codes` | 166 | 224 | 1.36x |
| `dict_u64_codes` | 230 | 257 | 1.10x |
| `dict_u8_codes` | 140 | 210 | 1.47x |
| `ext` | 245 | 258 | 1.05x |
| `fastlanes_bitpacked` | 145 | 207 | 1.47x |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 152 | 219 | 1.45x |
| `fastlanes_delta` | 422 | 2,504 | 5.95x |
| `fastlanes_for` | 233 | 253 | 1.08x |
| `fastlanes_rle` | 903 | 930 | 1.03x |
| `fixed_size_list` | 240 | 285 | 1.18x |
| `fsst` | 396 | 424 | 1.06x |
| `list` | 288 | 328 | 1.15x |
| `listview` | 4,002 | 4,268 | 1.08x |
| `map` | 8,515 | 12,470 | 1.49x |
| `masked` | 186 | 204 | 1.11x |
| `masked_all_invalid` | 186 | 206 | 1.11x |
| `masked_all_valid` | 245 | 270 | 1.11x |
| `null` | 110 | 156 | 1.42x |
| `onpair` | 374 | 402 | 1.08x |
| `parquet_variant` | 451 | 2,213 | 4.91x |
| `pco` | 220 | 1,232 | 5.58x |
| `pco_f32` | 401 | 3,045 | 7.64x |
| `pco_i16` | 349 | 2,637 | 7.63x |
| `pco_nullable` | 206 | 235 | 1.14x |
| `pco_u32` | 409 | 4,241 | 10.3x |
| `primitive` | 224 | 244 | 1.09x |
| `runend` | 121 | 188 | 1.52x |
| `sequence` | 112 | 163 | 1.45x |
| `sparse` | 127 | 194 | 1.51x |
| `struct` | 4,197 | 7,314 | 1.73x |
| `table_mixed` | 1,063 | 23,244 | 21.7x |
| `table_wide` | 346 | 427 | 1.23x |
| `varbin` | 349 | 4,490 | 13.0x |
| `varbinview` | 5,814 | 8,992 | 1.54x |
| `variant` | 115 | 162 | 1.41x |
| `zigzag` | 223 | 241 | 1.10x |
| `zstd` | 519 | 8,779 | 16.8x |
| `zstd_buffers` | 410 | 469 | 1.13x |
| `zstd_nullable` | 337 | 3,603 | 10.8x |

Vorticity took less time on 60 of 61 files; the median speedup is 1.23x. At 1.00x or below: `alp` 0.79x.
Rust is timed under the faster of its two splits, file by file: one split per chunk on 60 of 61 files, its default on `fastlanes_delta`.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; Vortex 0.90.0 through the C ABI of `tools/vxbench-rs`, binary 2ab72ea31074; commit 96d3ae37, 2026-11-05 08:22 UTC.*
<!-- /results: take -->

<!-- results: write -->
## Writing, per encoding

The files of the per-encoding corpus again, each read and written back out to a sink that
keeps nothing, on both sides, both writers given the rows as our reader delivers them, decoded and a
constant kept as one value: what a writer costs, the read included on both sides.
Each call opens the file anew: Rust maps it, and Vorticity reads it as a session that keeps no
mapping does on Windows.
Both implementations in one process, Rust's through a C ABI, taking turns against one clock:
at least 9 rounds after a 1-second warm-up per file, our side on the JIT, warmed.
A writer can be fast by compressing less, so each one's bytes are beside its time.

| encoding | Vorticity, µs | Vortex Rust, µs | speedup | Vorticity, bytes | Vortex Rust, bytes |
|---|---:|---:|---:|---:|---:|
| `alp` | 5,600 | 32,811 | 5.88x | 2,742,012 | 2,976,956 |
| `alp_no_patches` | 4,177 | 24,113 | 5.73x | 1,956,844 | 1,956,148 |
| `alp_patched_no_chunk_offsets` | 3,934 | 24,289 | 6.36x | 2,157,012 | 2,141,828 |
| `alprd` | 5,476 | 23,103 | 4.18x | 6,509,948 | 6,509,348 |
| `bool` | 206 | 809 | 3.94x | 127,108 | 127,148 |
| `bool_bit_offset3` | 250 | 973 | 3.92x | 127,108 | 127,148 |
| `bool_bit_offset7` | 247 | 958 | 3.86x | 127,108 | 127,148 |
| `bool_bit_offset_straddle` | 251 | 968 | 3.87x | 127,108 | 127,148 |
| `bytebool` | 225 | 951 | 4.22x | 127,108 | 127,148 |
| `chunked` | 1,946 | 13,632 | 7.02x | 890,596 | 890,736 |
| `chunked_bool` | 210 | 829 | 3.94x | 127,236 | 127,288 |
| `chunked_decimal` | 3,282 | 16,260 | 5.11x | 1,822,988 | 1,825,080 |
| `chunked_empty_chunks` | 1,669 | 13,257 | 7.92x | 317,044 | 317,080 |
| `chunked_mixed_validity` | 2,580 | 17,832 | 6.89x | 1,431,604 | 1,374,080 |
| `chunked_one_chunk` | 1,451 | 12,799 | 8.76x | 5,604 | 4,648 |
| `chunked_varbinview` | 20,375 | 32,718 | 1.58x | 643,644 | 628,652 |
| `constant` | 181 | 1,995 | 11.0x | 4,212 | 3,300 |
| `datetimeparts` | 3,862 | 25,686 | 6.65x | 3,382,604 | 3,383,676 |
| `decimal` | 2,133 | 15,660 | 7.38x | 4,324 | 5,132 |
| `decimal_byte_parts` | 2,122 | 15,901 | 7.43x | 4,324 | 5,132 |
| `dict` | 7,668 | 16,511 | 2.15x | 383,892 | 378,312 |
| `dict_nullable_codes` | 13,044 | 24,143 | 1.86x | 384,628 | 378,788 |
| `dict_nullable_values_nonnull_codes` | 11,870 | 22,477 | 1.88x | 384,628 | 378,780 |
| `dict_u64_codes` | 8,087 | 17,563 | 2.24x | 383,892 | 378,312 |
| `dict_u8_codes` | 10,690 | 13,833 | 1.29x | 38,740 | 1,004,216 |
| `ext` | 1,567 | 21,096 | 13.4x | 6,380 | 5,004 |
| `fastlanes_bitpacked` | 1,104 | 3,192 | 2.90x | 1,254,340 | 1,253,640 |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,167 | 5,515 | 4.67x | 1,270,452 | 1,272,860 |
| `fastlanes_delta` | 2,119 | 14,060 | 6.59x | 5,484 | 4,540 |
| `fastlanes_for` | 2,187 | 6,281 | 2.85x | 881,364 | 879,732 |
| `fastlanes_rle` | 1,335 | 5,159 | 3.85x | 4,548 | 3,820 |
| `fixed_size_list` | 2,062 | 26,918 | 12.8x | 5,604 | 4,724 |
| `fsst` | 47,778 | 232,721 | 4.89x | 933,668 | 9,566,228 |
| `list` | 6,331 | 22,977 | 3.67x | 4,782,492 | 5,173,896 |
| `listview` | 8,003 | 29,916 | 3.80x | 4,745,468 | 5,173,896 |
| `map` | 16,029 | 66,040 | 4.15x | 4,426,852 | 4,812,872 |
| `masked` | 2,049 | 19,627 | 9.54x | 2,380,060 | 2,381,180 |
| `masked_all_invalid` | 708 | 2,155 | 3.03x | 2,636 | 2,640 |
| `masked_all_valid` | 848 | 10,869 | 12.7x | 3,764 | 3,332 |
| `null` | 146 | 592 | 4.11x | 2,092 | 2,104 |
| `onpair` | 26,223 | 37,213 | 1.41x | 60,588 | 1,005,848 |
| `pco` | 1,069 | 12,713 | 12.1x | 5,476 | 4,516 |
| `pco_f32` | 4,396 | 21,802 | 4.95x | 2,131,076 | 2,130,156 |
| `pco_i16` | 2,837 | 5,878 | 2.04x | 1,878,788 | 1,878,460 |
| `pco_nullable` | 10,254 | 36,994 | 3.61x | 2,877,604 | 2,874,348 |
| `pco_u32` | 6,883 | 9,910 | 1.44x | 1,931,748 | 2,879,956 |
| `primitive` | 1,488 | 12,398 | 8.35x | 5,988 | 4,580 |
| `runend` | 714 | 6,820 | 9.58x | 4,620 | 3,508 |
| `sequence` | 920 | 11,499 | 12.5x | 5,484 | 4,524 |
| `sparse` | 698 | 2,678 | 3.83x | 131,172 | 3,876 |
| `struct` | 40,922 | 101,473 | 2.47x | 639,580 | 5,274,872 |
| `table_mixed` | 65,381 | 279,589 | 4.27x | 7,876,700 | 15,563,136 |
| `table_wide` | 6,856 | 36,838 | 5.31x | 5,163,244 | 5,168,720 |
| `varbin` | 50,130 | 120,406 | 2.40x | 637,012 | 5,356,244 |
| `varbinview` | 19,767 | 31,859 | 1.60x | 643,516 | 628,528 |
| `variant` | 175 | 842 | 4.83x | 2,220 | 53,392 |
| `zigzag` | 1,729 | 11,764 | 7.04x | 2,531,652 | 2,380,300 |
| `zstd` | 31,432 | 44,194 | 1.41x | 786,948 | 754,384 |
| `zstd_buffers` | 1,774 | 4,100 | 2.31x | 630,732 | 629,092 |
| `zstd_nullable` | 3,318 | 14,749 | 4.42x | 756,500 | 628,780 |

Vorticity took less time on 60 of 60 files; the median speedup is 4.20x.
Rust is timed under the faster of its two splits, file by file: one split per chunk on 48 of 60 files, its default on `alp`, `alp_no_patches`, `alprd`, `chunked`, `chunked_empty_chunks`, `chunked_one_chunk`, `datetimeparts`, `decimal_byte_parts`, `pco_nullable`, `primitive`, `table_wide`, `zigzag`.
Vorticity's bytes were within 5 % of Rust's or fewer on 46 of 60 files; more than 5 % above them on `sparse` (33.8 times Rust's), `runend` (1.32 times), `primitive` (1.31 times), `constant` (1.28 times), `ext` (1.27 times), `pco` (1.21 times), `sequence` (1.21 times), `fastlanes_delta` (1.21 times), `chunked_one_chunk` (1.21 times), `zstd_nullable` (1.20 times), `fastlanes_rle` (1.19 times), `fixed_size_list` (1.19 times), `masked_all_valid` (1.13 times), `zigzag` (1.06 times).
Rust's writer declines `parquet_variant`, which is left out.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; Vortex 0.90.0 through the C ABI of `tools/vxbench-rs`, binary 2ab72ea31074; commit 96d3ae37, 2026-11-05 07:56 UTC.*
<!-- /results: write -->

<!-- results: tradeoffs -->
## Encodings, column by column

One column of 10,000,000 rows per file, written under each compression profile and each hint that
applies to it, then opened and read. A **scan** decodes every value to its plain form and reads it
once; a **take** reads 1,000 rows spread over the file. Each figure is the median of 3 passes after
1 warm-up, the file in the page cache and read anew by each pass, so that a larger file pays for its
bytes on every read; a write is net of generating its rows. **Crosses at** is the
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
| a sequence | Sequence x77 | 0.00 | 9 | 1.4 | 0.27 |  |
| sorted runs of 1 000 | RunEnd x77 | 0.01 | 10 | 1.5 | 0.41 | Smallest: RunEnd x76, Pco x1, 0.01 B/value, scan 1.9 ms, take 0.74 ms; reads faster below 102 MB/s |
| timestamps (ms, increasing, jittered) | BitPacked x77 | 3.38 | 32 | 5.4 | 2.36 | Smallest: Pco x77, 1.35 B/value, scan 23.3 ms, take 20.40 ms; reads faster below 1,138 MB/s |
| random in 0..999 | BitPacked x77 | 1.25 | 23 | 3.1 | 1.25 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 22 | 2.2 | 0.70 |  |
| 100 003 distinct, repeating | BitPacked x77 | 4.63 | 36 | 5.4 | 1.45 | Smallest: Pco x77, 0.06 B/value, scan 17.4 ms, take 15.14 ms; reads faster below 3,816 MB/s |
| uniform 64-bit | Canonical x77 | 8.00 | 40 | 6.3 | 1.56 |  |
| random in 0..999, 10 % null | BitPacked x82 | 1.38 | 28 | 3.7 | 1.23 | Smallest: Pco x82, 1.26 B/value, scan 12.3 ms, take 9.56 ms; reads faster below 143 MB/s |

<details><summary>a sequence: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Sequence x77 | 44,380 | 0.00 | 9 | 1.4 | 0.27 |  |
| Fastest | Sequence x77 | 44,380 | 0.00 | 9 | 1.3 | 0.26 | same |
| Smallest | Sequence x77 | 44,380 | 0.00 | 9 | 1.4 | 0.26 | same |
| None | Canonical x77 | 80,043,724 | 8.00 | 42 | 6.6 | 1.60 | reads slower at any throughput |
| hint Dictionary | Sequence x77 | 44,380 | 0.00 | 106 | 1.4 | 0.27 | same |
| hint BitPacked | Sequence x77 | 44,380 | 0.00 | 9 | 1.4 | 0.27 | same |
| hint RunEnd | Sequence x77 | 44,380 | 0.00 | 9 | 1.4 | 0.27 | same |
| hint Zstd | Sequence x77 | 44,380 | 0.00 | 9 | 1.3 | 0.27 | same |

</details>

<details><summary>sorted runs of 1 000: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x77 | 88,004 | 0.01 | 10 | 1.5 | 0.41 |  |
| Fastest | RunEnd x77 | 88,004 | 0.01 | 10 | 1.5 | 0.40 | same |
| Smallest | RunEnd x76, Pco x1 | 53,748 | 0.01 | 91 | 1.9 | 0.74 | reads faster below 102 MB/s |
| None | Canonical x77 | 80,043,716 | 8.00 | 43 | 6.7 | 1.65 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 1,449,892 | 0.14 | 17 | 3.8 | 0.91 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 10,034,460 | 1.00 | 19 | 3.4 | 1.00 | reads slower at any throughput |
| hint RunEnd | RunEnd x77 | 88,004 | 0.01 | 11 | 1.5 | 0.39 | same |
| hint Zstd | Zstd x77 | 136,628 | 0.01 | 12 | 2.3 | 1.14 | reads slower at any throughput |

</details>

<details><summary>timestamps (ms, increasing, jittered): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 33,795,172 | 3.38 | 32 | 5.4 | 2.36 |  |
| Fastest | BitPacked x77 | 33,795,172 | 3.38 | 32 | 5.5 | 2.34 | same |
| Smallest | Pco x77 | 13,472,444 | 1.35 | 407 | 23.3 | 20.40 | reads faster below 1,138 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 42 | 6.6 | 1.60 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 33,795,172 | 3.38 | 129 | 5.6 | 2.48 | same |
| hint BitPacked | BitPacked x77 | 33,795,172 | 3.38 | 32 | 5.3 | 2.13 | same |
| hint RunEnd | BitPacked x77 | 33,795,172 | 3.38 | 38 | 5.1 | 2.09 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 20,700,796 | 2.07 | 156 | 75.7 | 33.65 | reads faster below 186 MB/s |

</details>

<details><summary>random in 0..999: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 12,544,284 | 1.25 | 23 | 3.1 | 1.25 |  |
| Fastest | BitPacked x77 | 12,544,284 | 1.25 | 23 | 3.0 | 1.16 | same |
| Smallest | BitPacked x76, Pco x1 | 12,543,924 | 1.25 | 294 | 3.0 | 1.08 | same |
| None | Canonical x77 | 80,043,716 | 8.00 | 40 | 6.2 | 1.54 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,652,724 | 1.27 | 53 | 4.4 | 1.20 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 12,544,284 | 1.25 | 23 | 3.2 | 1.37 | same |
| hint RunEnd | BitPacked x77 | 12,544,284 | 1.25 | 23 | 3.0 | 1.13 | same |
| hint Zstd | Zstd x77 | 20,141,172 | 2.01 | 180 | 59.7 | 28.89 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,748 | 0.51 | 22 | 2.2 | 0.70 |  |
| Fastest | Dictionary x77 | 5,063,748 | 0.51 | 23 | 2.2 | 0.72 | same |
| Smallest | Dictionary x76, Pco x1 | 5,063,588 | 0.51 | 482 | 2.3 | 0.75 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 40 | 6.7 | 1.61 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,063,748 | 0.51 | 23 | 2.2 | 0.70 | same |
| hint BitPacked | Dictionary x77 | 5,063,748 | 0.51 | 45 | 2.2 | 0.72 | same |
| hint RunEnd | Dictionary x77 | 5,063,748 | 0.51 | 37 | 2.2 | 0.71 | same |
| hint Zstd | Zstd x77 | 10,539,396 | 1.05 | 135 | 44.7 | 20.39 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,932 | 0.50 | 29 | 2.0 | 0.55 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 46,295,652 | 4.63 | 36 | 5.4 | 1.45 |  |
| Fastest | BitPacked x77 | 46,295,652 | 4.63 | 36 | 6.2 | 1.66 | reads slower at any throughput |
| Smallest | Pco x77 | 580,924 | 0.06 | 371 | 17.4 | 15.14 | reads faster below 3,816 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 40 | 6.4 | 1.55 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 46,295,652 | 4.63 | 125 | 5.5 | 1.46 | same |
| hint BitPacked | BitPacked x77 | 46,295,652 | 4.63 | 36 | 5.4 | 1.46 | same |
| hint RunEnd | BitPacked x77 | 46,295,652 | 4.63 | 49 | 5.6 | 1.57 | same |
| hint Zstd | Zstd x77 | 51,815,868 | 5.18 | 128 | 50.0 | 39.24 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 23,603,548 | 2.36 | 89 | 5.9 | 2.08 | reads faster below 47,894 MB/s |

</details>

<details><summary>uniform 64-bit: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x77 | 80,043,732 | 8.00 | 40 | 6.3 | 1.56 |  |
| Fastest | Canonical x77 | 80,043,732 | 8.00 | 40 | 6.7 | 1.52 | reads slower at any throughput |
| Smallest | Canonical x77 | 80,043,732 | 8.00 | 312 | 7.0 | 1.71 | reads slower at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 40 | 6.9 | 1.66 | reads slower at any throughput |
| hint Dictionary | Canonical x77 | 80,043,732 | 8.00 | 155 | 7.4 | 1.91 | reads slower at any throughput |
| hint BitPacked | Canonical x77 | 80,043,732 | 8.00 | 98 | 6.7 | 1.52 | reads slower at any throughput |
| hint RunEnd | Canonical x77 | 80,043,732 | 8.00 | 94 | 6.7 | 1.59 | reads slower at any throughput |
| hint Zstd | Canonical x77 | 80,043,732 | 8.00 | 104 | 6.3 | 1.60 | same |

</details>

<details><summary>random in 0..999, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x82 | 13,800,460 | 1.38 | 28 | 3.7 | 1.23 |  |
| Fastest | BitPacked x82 | 13,800,460 | 1.38 | 27 | 3.8 | 1.36 | same |
| Smallest | Pco x82 | 12,567,268 | 1.26 | 293 | 12.3 | 9.56 | reads faster below 143 MB/s |
| None | Canonical x82 | 81,294,668 | 8.13 | 42 | 6.6 | 1.79 | reads slower at any throughput |
| hint Dictionary | Dictionary x82 | 12,676,396 | 1.27 | 69 | 7.4 | 1.31 | reads faster below 298 MB/s |
| hint BitPacked | BitPacked x82 | 13,800,460 | 1.38 | 27 | 3.7 | 1.24 | same |
| hint RunEnd | BitPacked x82 | 13,800,460 | 1.38 | 28 | 3.6 | 1.24 | same |
| hint Zstd | Zstd x82 | 19,283,940 | 1.93 | 167 | 56.6 | 26.56 | reads slower at any throughput |

</details>

### Floating point

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| prices (2 decimals) | Alp x77 | 2.51 | 42 | 5.6 | 2.03 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 25 | 2.3 | 0.76 |  |
| 1 000 distinct prices | Dictionary x77 | 1.28 | 55 | 4.4 | 1.24 |  |
| 100 003 distinct, repeating | Zstd x77 | 1.69 | 155 | 45.1 | 20.82 | Auto, 16 MiB chunks: Dictionary x5, 2.21 B/value, scan 7.9 ms, take 4.08 ms; reads faster above 140 MB/s; None: Canonical x77, 8.01 B/value, scan 6.8 ms, take 1.57 ms; reads faster above 1,646 MB/s |
| uniform in [0, 1) | AlpRd x77 | 6.91 | 56 | 9.6 | 2.55 | None: Canonical x77, 8.01 B/value, scan 6.4 ms, take 1.63 ms; reads faster above 3,471 MB/s |

<details><summary>prices (2 decimals): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Alp x77 | 25,059,572 | 2.51 | 42 | 5.6 | 2.03 |  |
| Fastest | Alp x77 | 25,059,572 | 2.51 | 42 | 5.4 | 1.95 | same |
| Smallest | Alp x77 | 25,058,708 | 2.51 | 810 | 5.4 | 1.96 | same |
| None | Canonical x77 | 80,053,572 | 8.01 | 42 | 6.3 | 1.59 | reads slower at any throughput |
| hint Dictionary | Alp x77 | 25,059,572 | 2.51 | 162 | 5.4 | 1.94 | same |
| hint Alp | Alp x77 | 25,059,572 | 2.51 | 41 | 5.4 | 1.96 | same |
| hint Zstd | Zstd x77 | 41,250,100 | 4.13 | 282 | 57.2 | 36.46 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,073,588 | 0.51 | 25 | 2.3 | 0.76 |  |
| Fastest | Dictionary x77 | 5,073,588 | 0.51 | 26 | 2.3 | 0.72 | same |
| Smallest | Dictionary x76, Pco x1 | 5,073,428 | 0.51 | 502 | 2.3 | 0.75 | same |
| None | Canonical x77 | 80,053,572 | 8.01 | 43 | 6.5 | 1.49 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,073,588 | 0.51 | 24 | 2.2 | 0.70 | same |
| hint Alp | Dictionary x77 | 5,073,588 | 0.51 | 89 | 2.2 | 0.70 | same |
| hint Zstd | Zstd x77 | 10,555,508 | 1.06 | 139 | 44.6 | 20.35 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,042,772 | 0.50 | 35 | 1.9 | 0.58 | reads faster at any throughput |

</details>

<details><summary>1 000 distinct prices: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 12,766,100 | 1.28 | 55 | 4.4 | 1.24 |  |
| Fastest | Dictionary x77 | 12,766,100 | 1.28 | 58 | 4.8 | 1.49 | reads slower at any throughput |
| Smallest | Dictionary x77 | 12,761,196 | 1.28 | 443 | 4.8 | 1.67 | reads slower at any throughput |
| None | Canonical x77 | 80,053,572 | 8.01 | 44 | 6.6 | 1.63 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,766,100 | 1.28 | 57 | 4.5 | 1.30 | same |
| hint Alp | Alp x77 | 12,766,100 | 1.28 | 50 | 5.7 | 1.34 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 26,083,252 | 2.61 | 211 | 47.5 | 24.03 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 12,555,572 | 1.26 | 58 | 4.2 | 1.09 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x77 | 16,919,732 | 1.69 | 155 | 45.1 | 20.82 |  |
| Fastest | Zstd x77 | 16,919,732 | 1.69 | 152 | 45.1 | 20.81 | same |
| Smallest | Zstd x77 | 16,919,732 | 1.69 | 288 | 45.7 | 21.18 | same |
| None | Canonical x77 | 80,053,572 | 8.01 | 41 | 6.8 | 1.57 | reads faster above 1,646 MB/s |
| hint Dictionary | Zstd x77 | 16,919,732 | 1.69 | 265 | 45.6 | 20.92 | same |
| hint Alp | Zstd x77 | 16,919,732 | 1.69 | 253 | 45.2 | 20.84 | same |
| hint Zstd | Zstd x77 | 16,919,732 | 1.69 | 153 | 45.7 | 20.86 | same |
| Auto, 16 MiB chunks | Dictionary x5 | 22,139,828 | 2.21 | 96 | 7.9 | 4.08 | reads faster above 140 MB/s |

</details>

<details><summary>uniform in [0, 1): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | AlpRd x77 | 69,064,220 | 6.91 | 56 | 9.6 | 2.55 |  |
| Fastest | AlpRd x77 | 69,064,220 | 6.91 | 57 | 9.6 | 2.51 | same |
| Smallest | Pco x77 | 67,619,508 | 6.76 | 539 | 24.9 | 31.21 | reads faster below 94 MB/s |
| None | Canonical x77 | 80,053,572 | 8.01 | 41 | 6.4 | 1.63 | reads faster above 3,471 MB/s |
| hint Dictionary | AlpRd x77 | 69,064,220 | 6.91 | 259 | 9.7 | 2.60 | same |
| hint Alp | AlpRd x77 | 69,064,220 | 6.91 | 229 | 9.5 | 2.54 | same |
| hint Zstd | AlpRd x77 | 69,064,220 | 6.91 | 260 | 9.5 | 2.36 | same |

</details>

### Text

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| 16 cities, random order | Dictionary x175 | 0.51 | 119 | 11.9 | 1.21 |  |
| 10 000 distinct ids | Dictionary x306 | 2.54 | 504 | 57.6 | 26.79 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 34.8 ms, take 2.78 ms; reads faster at any throughput |
| 10 000 distinct ids, 10 % null | Dictionary x306 | 2.45 | 516 | 64.7 | 27.35 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 39.5 ms, take 3.07 ms; reads faster at any throughput |
| unique UUIDs | Zstd x611 | 20.61 | 1534 | 286.1 | 294.40 | hint Fsst: Fsst x611, 22.44 B/value, scan 158.9 ms, take 12.51 ms; reads faster above 144 MB/s; None: Canonical x611, 36.02 B/value, scan 72.6 ms, take 4.49 ms; reads faster above 722 MB/s |
| log lines (~100 B) | Zstd x1221 | 13.71 | 1374 | 411.4 | 280.26 | hint Fsst: Fsst x1221, 22.01 B/value, scan 202.3 ms, take 20.13 ms; reads faster above 397 MB/s; None: Canonical x1221, 72.55 B/value, scan 146.6 ms, take 8.73 ms; reads faster above 2,221 MB/s |

<details><summary>16 cities, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x175 | 5,117,284 | 0.51 | 119 | 11.9 | 1.21 |  |
| Fastest | Dictionary x175 | 5,117,284 | 0.51 | 115 | 11.7 | 1.18 | same |
| Smallest | Dictionary x175 | 5,117,284 | 0.51 | 301 | 11.8 | 1.20 | same |
| None | Canonical x175 | 96,746,756 | 9.67 | 155 | 36.3 | 3.27 | reads slower at any throughput |
| hint Dictionary | Dictionary x175 | 5,117,284 | 0.51 | 115 | 11.9 | 1.16 | same |
| hint Fsst | Fsst x175 | 39,908,604 | 3.99 | 361 | 35.5 | 5.03 | reads slower at any throughput |
| hint Zstd | Zstd x175 | 17,427,100 | 1.74 | 243 | 87.0 | 54.14 | reads slower at any throughput |

</details>

<details><summary>10 000 distinct ids: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 25,433,788 | 2.54 | 504 | 57.6 | 26.79 |  |
| Fastest | Dictionary x306 | 25,433,788 | 2.54 | 501 | 57.2 | 26.35 | same |
| Smallest | Dictionary x305, Zstd x1 | 23,452,500 | 2.35 | 1322 | 77.0 | 43.65 | reads faster below 102 MB/s |
| None | Canonical x306 | 140,098,172 | 14.01 | 212 | 47.9 | 2.87 | reads faster above 11,819 MB/s |
| hint Dictionary | Dictionary x306 | 25,433,788 | 2.54 | 517 | 57.0 | 26.45 | same |
| hint Fsst | Fsst x306 | 62,863,260 | 6.29 | 425 | 61.4 | 7.64 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 27,097,628 | 2.71 | 379 | 102.2 | 57.97 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 18,014,012 | 1.80 | 413 | 34.8 | 2.78 | reads faster at any throughput |

</details>

<details><summary>10 000 distinct ids, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 24,539,284 | 2.45 | 516 | 64.7 | 27.35 |  |
| Fastest | Dictionary x306 | 24,539,284 | 2.45 | 524 | 63.8 | 27.24 | same |
| Smallest | Dictionary x305, Zstd x1 | 22,854,628 | 2.29 | 1329 | 82.2 | 42.58 | reads faster below 96 MB/s |
| None | Canonical x306 | 151,883,732 | 15.19 | 267 | 63.5 | 4.39 | reads slower at any throughput |
| hint Dictionary | Dictionary x306 | 24,539,284 | 2.45 | 523 | 64.8 | 27.28 | same |
| hint Fsst | Fsst x306 | 63,725,748 | 6.37 | 501 | 73.1 | 8.80 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 25,595,636 | 2.56 | 401 | 116.3 | 52.74 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 17,982,604 | 1.80 | 455 | 39.5 | 3.07 | reads faster at any throughput |

</details>

<details><summary>unique UUIDs: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x611 | 206,058,588 | 20.61 | 1534 | 286.1 | 294.40 |  |
| Fastest | Zstd x611 | 206,058,588 | 20.61 | 1531 | 284.3 | 294.53 | same |
| Smallest | Zstd x611 | 206,058,588 | 20.61 | 2983 | 286.6 | 296.61 | same |
| None | Canonical x611 | 360,155,516 | 36.02 | 196 | 72.6 | 4.49 | reads faster above 722 MB/s |
| hint Dictionary | Zstd x610, Fsst x1 | 206,059,580 | 20.61 | 5684 | 286.9 | 298.84 | same |
| hint Fsst | Fsst x611 | 224,384,604 | 22.44 | 1383 | 158.9 | 12.51 | reads faster above 144 MB/s |
| hint Zstd | Zstd x611 | 206,058,588 | 20.61 | 1524 | 285.8 | 297.21 | same |

</details>

<details><summary>log lines (~100 B): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x1221 | 137,107,188 | 13.71 | 1374 | 411.4 | 280.26 |  |
| Fastest | Zstd x1221 | 137,107,188 | 13.71 | 1374 | 410.9 | 280.14 | same |
| Smallest | Zstd x1221 | 137,107,188 | 13.71 | 1695 | 411.8 | 281.95 | same |
| None | Canonical x1221 | 725,468,444 | 72.55 | 572 | 146.6 | 8.73 | reads faster above 2,221 MB/s |
| hint Dictionary | Zstd x1221 | 137,107,188 | 13.71 | 4522 | 412.4 | 281.47 | same |
| hint Fsst | Fsst x1221 | 220,058,684 | 22.01 | 1608 | 202.3 | 20.13 | reads faster above 397 MB/s |
| hint Zstd | Zstd x1221 | 137,107,188 | 13.71 | 1371 | 411.3 | 279.44 | same |

</details>

### Booleans

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| half true | Canonical x10 | 0.13 | 4 | 0.2 | 0.28 |  |
| 1 % true | RunEnd x10 | 0.05 | 5 | 2.3 | 0.38 | None: Canonical x10, 0.13 B/value, scan 0.3 ms, take 0.28 ms; reads faster above 350 MB/s |

<details><summary>half true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.2 | 0.28 |  |
| Fastest | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.3 | 0.29 | reads slower at any throughput |
| Smallest | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.3 | 0.28 | reads slower at any throughput |
| None | Canonical x10 | 1,262,556 | 0.13 | 5 | 0.2 | 0.29 | same |
| hint RunEnd | Canonical x10 | 1,262,556 | 0.13 | 5 | 0.3 | 0.31 | reads slower at any throughput |

</details>

<details><summary>1 % true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x10 | 548,092 | 0.05 | 5 | 2.3 | 0.38 |  |
| Fastest | RunEnd x10 | 548,092 | 0.05 | 5 | 2.3 | 0.39 | same |
| Smallest | RunEnd x10 | 164,692 | 0.02 | 14 | 2.8 | 0.84 | reads faster below 847 MB/s |
| None | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.3 | 0.28 | reads faster above 350 MB/s |
| hint RunEnd | RunEnd x10 | 548,092 | 0.05 | 5 | 2.3 | 0.38 | same |

</details>

### The profiles side by side

`Fastest` wrote what `Auto` wrote on 20 of 20 columns. `Smallest` wrote something else on *i64, sorted runs of 1 000*, *i64, timestamps (ms, increasing, jittered)*, *i64, random in 0..999*, *i64, 16 distinct, random order*, *i64, 100 003 distinct, repeating*, *i64, random in 0..999, 10 % null*, *f64, 16 distinct, random order*, *f64, uniform in [0, 1)*, *utf8, 10 000 distinct ids*, *utf8, 10 000 distinct ids, 10 % null*, and took up to 21.6 times `Auto`'s write, 482 ms against 22 ms on *i64, 16 distinct, random order*, since it tries every scheme on every chunk.
`None`, the plain form, made the largest file on 20 of 20 columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; commit 96d3ae37, 2026-11-05 08:03 UTC.*
<!-- /results: tradeoffs -->

<!-- results: advice -->
## What the advice picks

`VortexSession.AdviseAsync` on the same columns of 10,000,000 rows, under five goals: whole scans with the
bytes read at 2 GB/s, 100 MB/s and 10 GB/s, a row in a thousand read by row, and the bytes alone.
A cell is what the advice takes where it departs from the writer's own choice, empty where it
does not; a column it never departs on is left out.

| column | scans at 2 GB/s | scans at 100 MB/s | scans at 10 GB/s | a row in 1 000 read by row | size |
|---|---|---|---|---|---|
| i64, timestamps (ms, increasing, jittered) |  | `Pco` |  | `Pco` |  |
| i64, 100 003 distinct, repeating | 16 MiB chunks | `Pco` |  | `Pco` | `Dictionary`, 16 MiB chunks |
| i64, random in 0..999, 10 % null |  | `Dictionary` |  | `Dictionary` | `Dictionary` |
| f64, 100 003 distinct, repeating | 16 MiB chunks |  | `Canonical` |  | `Dictionary`, 16 MiB chunks |
| f64, uniform in [0, 1) |  |  | `Canonical` |  |  |
| utf8, 10 000 distinct ids | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `OnPair` | 16 MiB chunks |
| utf8, 10 000 distinct ids, 10 % null | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `Zstd` | 16 MiB chunks |
| utf8, unique UUIDs | `Canonical` |  | `Canonical` | `Fsst` |  |
| utf8, log lines (~100 B) | `OnPair` |  | `Canonical` | `OnPair` |  |
| bool, 1 % true | `Canonical` |  | `Canonical` |  |  |

It keeps the writer's choice under every goal on the 10 other columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; commit 96d3ae37, 2026-11-05 08:05 UTC.*
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
| &#39;i64 scalar&#39; | 17       |  94.988 μs | 1.6443 μs | 0.2545 μs |   1.00x | Baseline        |   1.45 |  5.52 |         - |          NA |
| &#39;i64 vector&#39; | 17       |   8.968 μs | 0.0817 μs | 0.0212 μs |   10.6x | Faster          |   0.14 | 58.46 |         - |          NA |
| &#39;i32 scalar&#39; | 17       | 106.608 μs | 0.7793 μs | 0.1206 μs |   0.89x | Same            |   1.63 |  2.46 |         - |          NA |
| &#39;i64 pack&#39;   | 17       |  11.565 μs | 0.0152 μs | 0.0040 μs |   8.21x | Faster          |   0.18 | 45.34 |         - |          NA |
| &#39;i32 pack&#39;   | 17       |  12.351 μs | 0.1147 μs | 0.0298 μs |   7.69x | Faster          |   0.19 | 21.23 |         - |          NA |
| &#39;i32 vector&#39; | 17       |   4.574 μs | 0.0583 μs | 0.0090 μs |   20.8x | Faster          |   0.07 | 57.31 |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit 96d3ae37, 2026-11-05 08:06 UTC.*
<!-- /results: kernel:FastLanesKernelBenchmarks -->

<!-- results: kernel:RowEncodingBenchmarks -->
### `RowEncodingBenchmarks`

| Method                                   | Entry                | Mean       | Error     | StdDev    | ns/row | GB/s | Allocated |
|----------------------------------------- |--------------------- |-----------:|----------:|----------:|-------:|-----:|----------:|
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nical [33]** |  **54.023 μs** | **0.7831 μs** | **0.2034 μs** |  **13.19** | **3.26** |         **-** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nical [33] |  67.416 μs | 0.6464 μs | 0.1000 μs |  16.46 | 2.61 |         - |
| &#39;encode, then sort rows by key&#39;          | conta(...)nical [33] | 327.567 μs | 6.8176 μs | 1.7705 μs |  79.97 | 0.54 |      48 B |
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nulls [33]** |  **20.057 μs** | **0.5685 μs** | **0.1476 μs** |  **19.59** | **3.37** |         **-** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nulls [33] |  18.445 μs | 0.2022 μs | 0.0525 μs |  18.01 | 3.66 |         - |
| &#39;encode, then sort rows by key&#39;          | conta(...)nulls [33] |  55.228 μs | 0.4227 μs | 0.1098 μs |  53.93 | 1.22 |      48 B |
| **&#39;encode a batch to row keys&#39;**             | **encod(...)r1025 [31]** |   **9.856 μs** | **0.1106 μs** | **0.0171 μs** |   **9.62** | **0.94** |         **-** |
| &#39;encode a batch to row keys, descending&#39; | encod(...)r1025 [31] |   9.771 μs | 0.2216 μs | 0.0576 μs |   9.53 | 0.94 |         - |
| &#39;encode, then sort rows by key&#39;          | encod(...)r1025 [31] |  51.078 μs | 0.5780 μs | 0.1501 μs |  49.83 | 0.18 |      48 B |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit 96d3ae37, 2026-11-05 08:06 UTC.*
<!-- /results: kernel:RowEncodingBenchmarks -->

<!-- results: kernel:CompressorBenchmarks -->
### `CompressorBenchmarks`

| Method                       | Column | Mean          | Error       | StdDev      | Speedup | MannWhitney(5%) | Allocated | Alloc Ratio |
|----------------------------- |------- |--------------:|------------:|------------:|--------:|---------------- |----------:|------------:|
| &#39;Choose, the whole decision&#39; | i64    | 30,591.872 ns | 717.7775 ns | 186.4045 ns |   1.00x | Baseline        |         - |          NA |
| &#39;candidate: sequence&#39;        | i64    |     10.519 ns |   0.1473 ns |   0.0383 ns |  2,908x | Faster          |         - |          NA |
| &#39;candidate: bit packing&#39;     | i64    |  4,936.746 ns | 126.6209 ns |  32.8830 ns |   6.20x | Faster          |         - |          NA |
| &#39;candidate: FSST&#39;            | i64    |      1.842 ns |   0.0230 ns |   0.0060 ns | 16,612x | Faster          |         - |          NA |
| &#39;candidate: zstd&#39;            | i64    |  7,577.871 ns | 163.5998 ns |  25.3172 ns |   4.04x | Faster          |         - |          NA |
| &#39;candidate: ALP&#39;             | i64    |      2.590 ns |   0.0228 ns |   0.0059 ns | 11,810x | Faster          |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit 96d3ae37, 2026-11-05 08:06 UTC.*
<!-- /results: kernel:CompressorBenchmarks -->

<!-- results: kernel:LanesBenchmarks -->
### `LanesBenchmarks`

| Method                 | Lanes | Mean       | Error     | StdDev   | Speedup | MannWhitney(5%) | Completed Work Items | Lock Contentions | Allocated | Alloc Ratio |
|----------------------- |------ |-----------:|----------:|---------:|--------:|---------------- |---------------------:|-----------------:|----------:|------------:|
| &#39;ours, n lanes&#39;        | 4     |   575.8 μs |  15.56 μs |  4.04 μs |   1.00x | Baseline        |              99.7216 |           0.0057 |   52756 B |        1.00 |
| &#39;reference, n workers&#39; | 4     | 1,127.9 μs | 167.85 μs | 43.59 μs |   0.51x | Slower          |                    - |                - |         - |        0.00 |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit 96d3ae37, 2026-11-05 08:06 UTC.*
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
