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
| `open` | Vorticity's | 0.6 (0.5-0.6) | 1.0 (1.0-1.2) | 1.81x | — | — | 2.9 KiB | 11 / 8 MiB |
| `open` | Vortex Rust's | 0.5 (0.5-0.6) | 1.0 (1.0-1.0) | 1.84x | — | — | 3.7 KiB | 11 / 8 MiB |
| `scan` | Vorticity's | 6.8 (6.7-7.4) | 20.5 (20.3-20.6) | 3.01x | 3.9 | 1.3 | 19.5 KiB | 14 / 44 MiB |
| `scan` | Vortex Rust's | 3.6 (3.5-3.6) | 8.2 (8.1-8.9) | 2.30x | 7.4 | 3.2 | 15.7 KiB | 21 / 50 MiB |
| `project` | Vorticity's | 0.9 (0.9-1.0) | 2.4 (2.4-2.4) | 2.61x | 9.2 | 3.5 | 13.0 KiB | 12 / 17 MiB |
| `project` | Vortex Rust's | 1.1 (1.0-1.1) | 2.4 (2.3-2.5) | 2.23x | 7.8 | 3.5 | 10.9 KiB | 13 / 17 MiB |
| `filter-narrow` | Vorticity's | 1.9 (1.9-2.0) | 2.3 (2.3-2.6) | 1.20x | 0.1 | 0.1 | 47.9 KiB | 15 / 12 MiB |
| `filter-narrow` | Vortex Rust's | 2.0 (1.8-2.1) | 2.0 (1.9-2.2) | 1.01x | 0.1 | 0.1 | 27.1 KiB | 17 / 12 MiB |
| `filter-wide` | Vorticity's | 4.4 (4.1-4.5) | 11.2 (11.2-11.4) | 2.58x | 3.1 | 1.2 | 29.3 KiB | 15 / 28 MiB |
| `filter-wide` | Vortex Rust's | 2.9 (2.9-3.2) | 5.2 (5.1-5.2) | 1.76x | 4.6 | 2.6 | 27.1 KiB | 21 / 31 MiB |
| `take` | Vorticity's | 6.1 (5.8-6.3) | 8.4 (8.3-8.6) | 1.37x | — | — | 43.3 KiB | 13 / 13 MiB |
| `take` | Vortex Rust's | 2.1 (2.0-2.1) | 2.9 (2.9-2.9) | 1.41x | — | — | 39.5 KiB | 15 / 15 MiB |
| `write` | Vorticity's | 30.7 (30.4-31.1) | 71.8 (71.7-73.5) | 2.34x | 0.9 | 0.4 | 37.2 KiB | 20 / 74 MiB |
| `write` | Vortex Rust's | 27.9 (27.5-28.4) | 60.7 (59.9-60.9) | 2.18x | 1.0 | 0.4 | 33.3 KiB | 27 / 82 MiB |
| `append` | Vorticity's | 10.9 (10.6-11.2) | not asked | n/a | — | — | 880.0 KiB | 21 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.5-0.6) | 2.6 (2.5-2.7) | 4.67x | — | — | 2.9 KiB | 11 / 9 MiB |
| `open` | Vortex Rust's | 0.6 (0.5-0.6) | 2.6 (2.5-2.8) | 4.44x | — | — | 3.7 KiB | 11 / 9 MiB |
| `scan` | Vorticity's | 3.8 (3.7-3.9) | 6.2 (6.1-6.5) | 1.63x | 7.0 | 4.3 | 36.5 KiB | 46 / 48 MiB |
| `scan` | Vortex Rust's | 4.0 (3.8-4.1) | 6.3 (6.3-6.8) | 1.57x | 6.7 | 4.2 | 26.2 KiB | 59 / 53 MiB |
| `project` | Vorticity's | 1.7 (1.6-1.9) | 3.5 (3.5-3.6) | 2.09x | 5.0 | 2.4 | 22.1 KiB | 20 / 19 MiB |
| `project` | Vortex Rust's | 1.7 (1.7-1.9) | 3.6 (3.4-3.6) | 2.14x | 5.0 | 2.3 | 16.0 KiB | 20 / 19 MiB |
| `filter-narrow` | Vorticity's | 1.9 (1.7-2.0) | 4.3 (4.0-4.6) | 2.30x | 0.1 | 0.1 | 67.0 KiB | 14 / 14 MiB |
| `filter-narrow` | Vortex Rust's | 1.7 (1.6-1.8) | 4.0 (3.9-4.0) | 2.30x | 0.2 | 0.1 | 30.5 KiB | 15 / 14 MiB |
| `filter-wide` | Vorticity's | 3.1 (3.1-3.5) | 6.3 (5.3-7.1) | 2.02x | 4.3 | 2.1 | 36.9 KiB | 31 / 32 MiB |
| `filter-wide` | Vortex Rust's | 3.0 (2.9-3.2) | 5.1 (4.8-5.3) | 1.69x | 4.4 | 2.6 | 32.0 KiB | 39 / 35 MiB |
| `take` | Vorticity's | 3.8 (3.6-4.1) | 4.5 (4.4-4.8) | 1.17x | — | — | 417.0 KiB | 26 / 23 MiB |
| `take` | Vortex Rust's | 2.4 (2.3-2.6) | 4.3 (4.1-4.4) | 1.75x | — | — | 81.2 KiB | 22 / 18 MiB |
| `write` | Vorticity's | 13.0 (12.8-13.2) | 19.9 (19.6-20.6) | 1.54x | 2.1 | 1.3 | 2.1 MiB | 61 / 111 MiB |
| `write` | Vortex Rust's | 13.5 (13.3-13.7) | 20.5 (20.1-21.4) | 1.52x | 2.0 | 1.3 | 1.8 MiB | 73 / 119 MiB |
| `append` | Vorticity's | 10.3 (10.3-10.5) | not asked | n/a | — | — | 1.4 MiB | 23 / — MiB |

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
| `open` | Vorticity's | 0.6 (0.6-0.6) | 1.1 (1.1-1.1) | 1.95x | — | — | 2.9 KiB | 11 / 8 MiB |
| `open` | Vortex Rust's | 0.6 (0.6-0.6) | 1.0 (1.0-1.1) | 1.88x | — | — | 3.7 KiB | 11 / 8 MiB |
| `scan` | Vorticity's | 57.2 (56.7-57.9) | 183.1 (181.7-184.0) | 3.20x | 4.7 | 1.5 | 89.3 KiB | 14 / 291 MiB |
| `scan` | Vortex Rust's | 22.0 (21.9-22.5) | 71.8 (71.6-71.8) | 3.26x | 12.1 | 3.7 | 33.1 KiB | 23 / 343 MiB |
| `project` | Vorticity's | 2.0 (2.0-2.4) | 11.4 (11.3-11.5) | 5.72x | 42.1 | 7.4 | 50.7 KiB | 12 / 74 MiB |
| `project` | Vortex Rust's | 2.4 (2.4-2.8) | 11.5 (11.4-11.6) | 4.70x | 34.3 | 7.3 | 21.3 KiB | 12 / 74 MiB |
| `filter-narrow` | Vorticity's | 2.4 (2.4-2.5) | 4.7 (4.6-4.9) | 1.95x | 1.1 | 0.6 | 122.7 KiB | 16 / 16 MiB |
| `filter-narrow` | Vortex Rust's | 2.6 (2.4-2.6) | 2.9 (2.9-3.2) | 1.13x | 1.0 | 0.9 | 68.0 KiB | 20 / 16 MiB |
| `filter-wide` | Vorticity's | 29.6 (29.5-29.8) | 97.9 (97.2-99.7) | 3.31x | 4.5 | 1.4 | 122.7 KiB | 15 / 184 MiB |
| `filter-wide` | Vortex Rust's | 14.4 (14.1-14.7) | 38.7 (38.4-40.3) | 2.68x | 9.3 | 3.4 | 68.0 KiB | 24 / 210 MiB |
| `take` | Vorticity's | 23.2 (23.2-23.3) | 68.2 (67.9-68.7) | 2.94x | — | — | 113.2 KiB | 13 / 37 MiB |
| `take` | Vortex Rust's | 5.2 (5.1-5.3) | 7.2 (7.2-7.5) | 1.39x | — | — | 61.1 KiB | 21 / 27 MiB |
| `write` | Vorticity's | 271.2 (270.5-276.1) | 646.7 (645.0-649.0) | 2.38x | 1.0 | 0.4 | 112.0 KiB | 22 / 381 MiB |
| `write` | Vortex Rust's | 235.2 (234.9-236.2) | 523.6 (523.2-526.3) | 2.23x | 1.1 | 0.5 | 55.8 KiB | 31 / 454 MiB |
| `append` | Vorticity's | 47.3 (46.9-48.2) | not asked | n/a | — | — | 2.0 MiB | 27 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.5-0.6) | 2.6 (2.6-2.8) | 4.70x | — | — | 2.9 KiB | 11 / 9 MiB |
| `open` | Vortex Rust's | 0.6 (0.5-0.6) | 2.5 (2.4-2.6) | 4.52x | — | — | 3.7 KiB | 11 / 9 MiB |
| `scan` | Vorticity's | 16.2 (16.0-17.0) | 24.5 (24.3-24.6) | 1.51x | 16.4 | 10.9 | 145.4 KiB | 66 / 301 MiB |
| `scan` | Vortex Rust's | 19.8 (19.1-21.8) | 31.9 (31.6-34.5) | 1.62x | 13.5 | 8.4 | 49.2 KiB | 69 / 351 MiB |
| `project` | Vorticity's | 3.3 (2.9-3.7) | 7.6 (7.4-7.8) | 2.31x | 25.5 | 11.0 | 68.2 KiB | 29 / 82 MiB |
| `project` | Vortex Rust's | 4.0 (3.4-5.0) | 7.4 (7.4-7.5) | 1.86x | 21.0 | 11.3 | 48.4 KiB | 45 / 80 MiB |
| `filter-narrow` | Vorticity's | 2.4 (2.1-2.5) | 5.6 (5.5-5.7) | 2.29x | 1.1 | 0.5 | 125.1 KiB | 17 / 20 MiB |
| `filter-narrow` | Vortex Rust's | 2.5 (2.4-2.5) | 5.0 (4.8-5.4) | 2.05x | 1.1 | 0.5 | 69.6 KiB | 20 / 21 MiB |
| `filter-wide` | Vorticity's | 10.9 (10.5-12.8) | 16.8 (16.3-17.2) | 1.54x | 12.2 | 8.0 | 190.4 KiB | 85 / 188 MiB |
| `filter-wide` | Vortex Rust's | 16.0 (15.8-17.9) | 19.8 (19.4-20.5) | 1.24x | 8.3 | 6.7 | 1.6 MiB | 225 / 222 MiB |
| `take` | Vorticity's | 9.4 (9.2-10.0) | 11.4 (10.9-11.8) | 1.22x | — | — | 228.1 KiB | 58 / 65 MiB |
| `take` | Vortex Rust's | 4.3 (4.0-5.0) | 7.3 (7.1-7.4) | 1.69x | — | — | 151.7 KiB | 29 / 31 MiB |
| `write` | Vorticity's | 77.0 (76.0-79.3) | 120.2 (119.3-130.5) | 1.56x | 3.5 | 2.2 | 2.3 MiB | 84 / 480 MiB |
| `write` | Vortex Rust's | 70.1 (69.7-70.4) | 129.0 (127.6-133.7) | 1.84x | 3.8 | 2.1 | 2.0 MiB | 87 / 539 MiB |
| `append` | Vorticity's | 43.0 (42.9-43.4) | not asked | n/a | — | — | 2.4 MiB | 59 / — MiB |

`open`: open the file and read no rows. `scan`: read every column of every row. `project`: read one column of four. `filter-narrow`: read the rows of a band holding about one in a hundred. `filter-wide`: read the rows of a band holding about half. `take`: take a thousand rows spread across the file. `write`: read the file and encode it back out. `append`: append a tenth of the rows to a copy of the file.

**On one core.** Vorticity took less time than Rust on 28 of 28 compared rows.
The highest speedup is `project` at 10,485,760 rows on Vorticity's file (5.72x), the lowest
`filter-narrow` at 1,048,576 rows on Vortex Rust's file (1.01x).

**On all 32 cores.** Vorticity took less time than Rust on 28 of 28 compared rows.
The highest speedup is `open` at 10,485,760 rows on Vorticity's file (4.70x), the lowest
`take` at 1,048,576 rows on Vorticity's file (1.17x).

**The process start** is not in the figures: 38 ms for Vorticity's native binary and 24 ms for Rust's,
most of it the operating system starting a binary, and 97 ms for the managed runtime on the JIT.

**Rust's splits.** On one core, one split per chunk was the faster of its two on 6 of 14 rows on Vorticity's file and 12 of 14 rows on Vortex Rust's file;
the two differ only where a chunk holds more than 100,000 rows.

**Not asked of Rust**: `append`, which its harness has no entry point for; those figures
are Vorticity's alone.

### A first call on the JIT

The same scenarios in the framework-dependent build, under `dotnet`, on one core: what a
process that is not compiled ahead of time pays the first time, the code compiling as it runs.
Milliseconds, the median of the same runs.

| scenario | 1,048,576 rows, Vorticity's file | 1,048,576 rows, Vortex Rust's file | 10,485,760 rows, Vorticity's file | 10,485,760 rows, Vortex Rust's file |
|---|---:|---:|---:|---:|
| `open` | 20.4 | 20.4 | 20.3 | 20.4 |
| `scan` | 124.3 | 115.3 | 236.7 | 144.8 |
| `project` | 72.6 | 81.6 | 76.6 | 83.0 |
| `filter-narrow` | 167.9 | 147.7 | 160.6 | 154.8 |
| `filter-wide` | 148.1 | 145.1 | 207.9 | 169.1 |
| `take` | 134.4 | 109.5 | 188.1 | 116.5 |
| `write` | 352.9 | 347.7 | 901.6 | 824.8 |
| `append` | 353.6 | — | 433.2 | — |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; Vortex 0.90.0, rustc 1.98.1 (48a229cea 2026-09-01); commit acd910ae, 2026-11-12 10:14 UTC.*
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
| `alp` | 1,000,000 | 0.88 | 2.56 | 2.92x | 9.1 | 3.1 |
| `alp_no_patches` | 1,000,000 | 0.59 | 1.85 | 3.16x | 13.6 | 4.3 |
| `alp_patched_no_chunk_offsets` | 1,000,000 | 0.60 | 1.87 | 3.09x | 13.3 | 4.3 |
| `alprd` | 1,000,000 | 0.80 | 1.62 | 2.04x | 10.1 | 4.9 |
| `bool` | 1,000,000 | 0.14 | 0.18 | 1.28x | 0.9 | 0.7 |
| `bool_bit_offset3` | 1,000,000 | 0.15 | 0.19 | 1.28x | 0.9 | 0.7 |
| `bool_bit_offset7` | 1,000,000 | 0.15 | 0.18 | 1.27x | 0.9 | 0.7 |
| `bool_bit_offset_straddle` | 1,000,000 | 0.15 | 0.19 | 1.27x | 0.9 | 0.7 |
| `bytebool` | 1,000,000 | 0.17 | 0.33 | 1.90x | 0.7 | 0.4 |
| `chunked` | 1,000,000 | 0.64 | 2.18 | 3.39x | 12.4 | 3.7 |
| `chunked_bool` | 1,000,000 | 0.15 | 0.20 | 1.33x | 0.8 | 0.6 |
| `chunked_decimal` | 1,000,000 | 0.63 | 2.01 | 3.18x | 12.7 | 4.0 |
| `chunked_empty_chunks` | 1,000,000 | 0.63 | 2.14 | 3.42x | 12.8 | 3.7 |
| `chunked_mixed_validity` | 1,000,000 | 0.68 | 2.01 | 2.96x | 11.9 | 4.0 |
| `chunked_one_chunk` | 1,000,000 | 0.62 | 2.06 | 3.31x | 12.8 | 3.9 |
| `chunked_varbinview` | 1,000,000 | 3.40 | 8.55 | 2.51x | 4.0 | 1.6 |
| `constant` | 1,000,000 | 0.14 | 0.16 | 1.17x | 57.6 | 49.4 |
| `datetimeparts` | 1,000,000 | 1.12 | 2.68 | 2.38x | 7.1 | 3.0 |
| `decimal` | 1,000,000 | 0.62 | 2.02 | 3.26x | 12.9 | 4.0 |
| `decimal_byte_parts` | 1,000,000 | 0.62 | 2.04 | 3.27x | 12.8 | 3.9 |
| `dict` | 1,000,000 | 0.85 | 1.38 | 1.62x | 7.1 | 4.4 |
| `dict_nullable_codes` | 1,000,000 | 1.13 | 1.85 | 1.64x | 4.9 | 3.0 |
| `dict_nullable_values_nonnull_codes` | 1,000,000 | 0.97 | 1.15 | 1.18x | 5.9 | 5.0 |
| `dict_u64_codes` | 1,000,000 | 1.14 | 2.04 | 1.79x | 5.3 | 2.9 |
| `dict_u8_codes` | 1,000,000 | 0.74 | 0.95 | 1.29x | 10.9 | 8.4 |
| `ext` | 1,000,000 | 0.62 | 2.09 | 3.39x | 12.9 | 3.8 |
| `fastlanes_bitpacked` | 1,000,000 | 0.27 | 0.41 | 1.54x | 14.8 | 9.7 |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,000,000 | 0.28 | 0.43 | 1.56x | 14.3 | 9.2 |
| `fastlanes_delta` | 1,000,000 | 0.69 | 1.87 | 2.70x | 11.5 | 4.3 |
| `fastlanes_for` | 1,000,000 | 0.59 | 1.44 | 2.45x | 13.6 | 5.6 |
| `fastlanes_rle` | 1,000,000 | 0.48 | 0.78 | 1.62x | 8.3 | 5.1 |
| `fixed_size_list` | 1,000,000 | 0.92 | 3.02 | 3.28x | 13.1 | 4.0 |
| `fsst` | 1,000,000 | 10.7 | 14.2 | 1.33x | 5.2 | 3.9 |
| `list` | 1,000,000 | 1.28 | 3.21 | 2.51x | 7.8 | 3.1 |
| `listview` | 1,000,000 | 2.36 | 4.90 | 2.07x | 4.2 | 2.0 |
| `map` | 1,000,000 | 4.49 | 12.1 | 2.70x | 4.5 | 1.7 |
| `masked` | 1,000,000 | 0.37 | 1.08 | 2.91x | 11.1 | 3.8 |
| `masked_all_invalid` | 1,000,000 | 0.37 | 1.08 | 2.91x | 10.8 | 3.7 |
| `masked_all_valid` | 1,000,000 | 0.37 | 1.06 | 2.88x | 10.8 | 3.8 |
| `null` | 1,000,000 | 0.13 | 0.16 | 1.20x | — | — |
| `onpair` | 1,000,000 | 5.37 | 9.39 | 1.75x | 4.0 | 2.3 |
| `parquet_variant` | 1,000,000 | 2.60 | 16.1 | 6.20x | 4.6 | 0.7 |
| `pco` | 1,000,000 | 0.48 | 1.29 | 2.68x | 16.6 | 6.2 |
| `pco_f32` | 1,000,000 | 2.31 | 3.07 | 1.33x | 1.7 | 1.3 |
| `pco_i16` | 1,000,000 | 1.84 | 2.64 | 1.43x | 1.1 | 0.8 |
| `pco_nullable` | 1,000,000 | 2.89 | 4.47 | 1.55x | 2.8 | 1.8 |
| `pco_u32` | 1,000,000 | 2.83 | 4.29 | 1.51x | 1.4 | 0.9 |
| `primitive` | 1,000,000 | 0.62 | 2.03 | 3.28x | 12.9 | 3.9 |
| `runend` | 1,000,000 | 0.20 | 0.26 | 1.28x | 19.9 | 15.5 |
| `sequence` | 1,000,000 | 0.25 | 0.28 | 1.12x | 31.8 | 28.3 |
| `sparse` | 1,000,000 | 0.22 | 0.27 | 1.22x | 18.1 | 14.9 |
| `struct` | 1,000,000 | 1.82 | 6.49 | 3.56x | 8.2 | 2.3 |
| `table_mixed` | 1,000,000 | 13.4 | 29.7 | 2.21x | 5.2 | 2.4 |
| `table_wide` | 50,000 | 36.2 | 105.0 | 2.90x | 11.0 | 3.8 |
| `varbin` | 1,000,000 | 2.07 | 8.68 | 4.18x | 7.7 | 1.8 |
| `varbinview` | 1,000,000 | 3.18 | 7.09 | 2.23x | 4.2 | 1.9 |
| `variant` | 1,000,000 | 0.14 | 0.17 | 1.19x | 111.9 | 94.1 |
| `zigzag` | 1,000,000 | 0.36 | 1.01 | 2.82x | 11.2 | 4.0 |
| `zstd` | 1,000,000 | 6.54 | 11.0 | 1.68x | 4.9 | 2.9 |
| `zstd_buffers` | 1,000,000 | 0.31 | 0.52 | 1.69x | 25.8 | 15.3 |
| `zstd_nullable` | 1,000,000 | 1.90 | 3.85 | 2.03x | 4.3 | 2.1 |

Rust is timed under each of its two ways of splitting a scan, three processes each, and its
figure is the faster: its default cuts a chunk of more than 100,000 rows into splits of 100,000 and
decodes the whole chunk again for each, which on these files of one chunk of a million rows is ten
times; one split per chunk decodes it once, but builds a chunk made of smaller arrays in one
piece. One split per chunk was the faster on 54 of 61 files, its default on `alprd`, `chunked`, `chunked_empty_chunks`, `chunked_mixed_validity`, `chunked_one_chunk`, `masked_all_invalid`, `masked_all_valid`.

Vorticity decoded 61 of 61 files in less time than Rust; the median speedup is 2.07x.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; Vortex 0.90.0, rustc 1.98.1 (48a229cea 2026-09-01); commit acd910ae, 2026-11-12 10:14 UTC.*
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
| full scan | 836.5 | 2,227.3 | 2.66x | 2.65 to 2.68 |
| projected scan, 1 of 5 columns | 238.6 | 448.3 | 1.88x | 1.87 to 1.89 |
| open to first batch | 215.3 | 2,166.7 | 10.1x | 9.94 to 10.3 |
| open, footer only | 120.0 | 193.4 | 1.62x | 1.61 to 1.64 |
| read and write back | 5,222.2 | 15,615.7 | 2.98x | 2.94 to 3.12 |
| filtered scan, 1% band | 198.7 | 583.8 | 2.94x | 2.92 to 2.98 |
| filtered scan, half the rows | 339.6 | 926.1 | 2.73x | 2.71 to 2.76 |
| scattered take, 64 of 64 splits | 657.5 | 1,833.2 | 2.79x | 2.77 to 2.80 |
| key order, sorted column, 1% band | 199.3 | 292.4 | 1.48x | 1.46 to 1.51 |
| key order, uncorrelated, 64 rows | 351.9 | 403.5 | 1.30x | 1.23 to 1.36 |
| count, exact cover, 1% band | 275.8 | 349.6 | 1.39x | 1.31 to 1.44 |
| filtered scan, string equality, fsst | 399.2 | 651.6 | 1.61x | 1.58 to 1.72 |
| filtered scan, string prefix, fsst | 924.1 | 1,195.3 | 1.29x | 1.29 to 1.30 |
| filtered scan, string equality, dict | 193.5 | 286.6 | 1.49x | 1.48 to 1.50 |
| filtered scan, string prefix, dict | 211.7 | 403.7 | 1.91x | 1.88 to 1.92 |
| filtered scan, band, runend | 191.2 | 261.9 | 1.37x | 1.36 to 1.38 |
| filtered scan, band, bitpacked | 200.2 | 255.7 | 1.28x | 1.27 to 1.29 |
| full scan, 1M table | 15,670.9 | 34,887.6 | 2.22x | 2.11 to 2.26 |
| projected scan, 1 of 50 columns | 279.4 | 313.9 | 1.13x | 1.12 to 1.14 |

Vorticity took less time on 19 of 19 axes.
Rust is timed under the faster of its two splits, axis by axis: one split per chunk on 10 of 19, its default on full scan; read and write back; filtered scan, half the rows; scattered take, 64 of 64 splits; key order, sorted column, 1% band; count, exact cover, 1% band; filtered scan, string equality, fsst; filtered scan, string prefix, fsst; filtered scan, string prefix, dict.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; Vortex 0.90.0 through the C ABI of `tools/vxbench-rs`, binary 2ab72ea31074; commit acd910ae, 2026-11-12 09:43 UTC.*
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
| `alp` | 729 | 569 | 0.78x |
| `alp_no_patches` | 237 | 258 | 1.09x |
| `alp_patched_no_chunk_offsets` | 248 | 277 | 1.11x |
| `alprd` | 270 | 285 | 1.06x |
| `bool` | 126 | 184 | 1.46x |
| `bool_bit_offset3` | 127 | 187 | 1.46x |
| `bool_bit_offset7` | 127 | 185 | 1.46x |
| `bool_bit_offset_straddle` | 130 | 187 | 1.44x |
| `bytebool` | 145 | 324 | 2.26x |
| `chunked` | 254 | 283 | 1.12x |
| `chunked_bool` | 136 | 209 | 1.54x |
| `chunked_decimal` | 245 | 275 | 1.13x |
| `chunked_empty_chunks` | 244 | 273 | 1.12x |
| `chunked_mixed_validity` | 261 | 312 | 1.20x |
| `chunked_one_chunk` | 241 | 265 | 1.10x |
| `chunked_varbinview` | 380 | 8,510 | 23.0x |
| `constant` | 121 | 168 | 1.40x |
| `datetimeparts` | 393 | 419 | 1.07x |
| `decimal` | 237 | 256 | 1.08x |
| `decimal_byte_parts` | 249 | 263 | 1.06x |
| `dict` | 229 | 252 | 1.10x |
| `dict_nullable_codes` | 248 | 284 | 1.14x |
| `dict_nullable_values_nonnull_codes` | 175 | 233 | 1.33x |
| `dict_u64_codes` | 242 | 264 | 1.10x |
| `dict_u8_codes` | 157 | 229 | 1.48x |
| `ext` | 245 | 261 | 1.07x |
| `fastlanes_bitpacked` | 152 | 216 | 1.41x |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 159 | 227 | 1.44x |
| `fastlanes_delta` | 410 | 2,464 | 6.01x |
| `fastlanes_for` | 238 | 263 | 1.10x |
| `fastlanes_rle` | 873 | 924 | 1.06x |
| `fixed_size_list` | 278 | 304 | 1.15x |
| `fsst` | 396 | 412 | 1.05x |
| `list` | 291 | 331 | 1.14x |
| `listview` | 3,725 | 4,126 | 1.11x |
| `map` | 8,206 | 12,057 | 1.45x |
| `masked` | 190 | 212 | 1.10x |
| `masked_all_invalid` | 195 | 211 | 1.09x |
| `masked_all_valid` | 252 | 274 | 1.08x |
| `null` | 118 | 163 | 1.38x |
| `onpair` | 385 | 412 | 1.07x |
| `parquet_variant` | 456 | 2,269 | 4.87x |
| `pco` | 229 | 1,213 | 5.34x |
| `pco_f32` | 403 | 3,035 | 7.45x |
| `pco_i16` | 355 | 2,624 | 7.49x |
| `pco_nullable` | 198 | 242 | 1.22x |
| `pco_u32` | 418 | 4,200 | 10.2x |
| `primitive` | 237 | 257 | 1.08x |
| `runend` | 133 | 199 | 1.50x |
| `sequence` | 119 | 168 | 1.42x |
| `sparse` | 136 | 199 | 1.47x |
| `struct` | 3,881 | 6,864 | 1.77x |
| `table_mixed` | 1,036 | 22,142 | 21.7x |
| `table_wide` | 362 | 432 | 1.20x |
| `varbin` | 368 | 4,515 | 12.4x |
| `varbinview` | 5,520 | 8,538 | 1.55x |
| `variant` | 124 | 176 | 1.41x |
| `zigzag` | 229 | 249 | 1.09x |
| `zstd` | 562 | 8,544 | 15.2x |
| `zstd_buffers` | 237 | 462 | 1.94x |
| `zstd_nullable` | 335 | 3,621 | 10.9x |

Vorticity took less time on 60 of 61 files; the median speedup is 1.33x. At 1.00x or below: `alp` 0.78x.
Rust is timed under the faster of its two splits, file by file: one split per chunk on 60 of 61 files, its default on `fastlanes_delta`.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; Vortex 0.90.0 through the C ABI of `tools/vxbench-rs`, binary 2ab72ea31074; commit acd910ae, 2026-11-12 09:44 UTC.*
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
| `alp` | 5,280 | 33,134 | 6.15x | 2,742,012 | 2,976,956 |
| `alp_no_patches` | 4,109 | 23,699 | 5.74x | 1,956,844 | 1,956,148 |
| `alp_patched_no_chunk_offsets` | 3,909 | 24,317 | 6.15x | 2,157,012 | 2,141,828 |
| `alprd` | 5,527 | 23,125 | 4.18x | 6,509,948 | 6,509,348 |
| `bool` | 216 | 823 | 3.82x | 127,108 | 127,148 |
| `bool_bit_offset3` | 237 | 978 | 4.13x | 127,108 | 127,148 |
| `bool_bit_offset7` | 234 | 962 | 4.18x | 127,108 | 127,148 |
| `bool_bit_offset_straddle` | 237 | 978 | 4.13x | 127,108 | 127,148 |
| `bytebool` | 235 | 959 | 4.10x | 127,108 | 127,148 |
| `chunked` | 1,899 | 13,547 | 7.11x | 890,596 | 890,736 |
| `chunked_bool` | 219 | 832 | 3.83x | 127,236 | 127,288 |
| `chunked_decimal` | 3,263 | 16,145 | 4.94x | 1,822,988 | 1,825,080 |
| `chunked_empty_chunks` | 1,658 | 13,304 | 7.94x | 317,044 | 317,080 |
| `chunked_mixed_validity` | 2,509 | 17,323 | 6.84x | 1,431,604 | 1,374,080 |
| `chunked_one_chunk` | 1,461 | 12,770 | 8.79x | 5,604 | 4,648 |
| `chunked_varbinview` | 19,974 | 33,391 | 1.64x | 643,644 | 628,652 |
| `constant` | 179 | 1,995 | 11.0x | 4,212 | 3,300 |
| `datetimeparts` | 3,654 | 24,882 | 6.83x | 3,382,604 | 3,383,676 |
| `decimal` | 2,138 | 15,450 | 7.28x | 4,324 | 5,132 |
| `decimal_byte_parts` | 2,162 | 15,385 | 7.12x | 4,324 | 5,132 |
| `dict` | 7,440 | 16,429 | 2.23x | 383,892 | 378,312 |
| `dict_nullable_codes` | 12,761 | 24,089 | 1.89x | 384,628 | 378,788 |
| `dict_nullable_values_nonnull_codes` | 11,685 | 22,381 | 1.92x | 384,628 | 378,780 |
| `dict_u64_codes` | 7,913 | 17,655 | 2.28x | 383,892 | 378,312 |
| `dict_u8_codes` | 10,396 | 13,928 | 1.36x | 38,740 | 1,004,216 |
| `ext` | 1,572 | 20,794 | 13.2x | 6,380 | 5,004 |
| `fastlanes_bitpacked` | 1,021 | 3,133 | 3.08x | 1,254,340 | 1,253,640 |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,078 | 5,293 | 4.91x | 1,270,452 | 1,272,860 |
| `fastlanes_delta` | 2,125 | 14,005 | 6.55x | 5,484 | 4,540 |
| `fastlanes_for` | 2,189 | 6,246 | 2.82x | 881,364 | 879,732 |
| `fastlanes_rle` | 1,343 | 5,153 | 3.84x | 4,548 | 3,820 |
| `fixed_size_list` | 2,007 | 27,078 | 13.5x | 5,604 | 4,724 |
| `fsst` | 47,457 | 230,768 | 4.86x | 933,668 | 9,566,228 |
| `list` | 6,107 | 22,730 | 3.70x | 4,782,492 | 5,173,896 |
| `listview` | 7,808 | 29,715 | 3.80x | 4,745,468 | 5,173,896 |
| `map` | 15,236 | 64,854 | 4.28x | 4,426,852 | 4,812,872 |
| `masked` | 1,944 | 19,715 | 10.2x | 2,380,060 | 2,381,180 |
| `masked_all_invalid` | 720 | 2,152 | 3.07x | 2,636 | 2,640 |
| `masked_all_valid` | 855 | 11,122 | 12.9x | 3,764 | 3,332 |
| `null` | 162 | 616 | 3.82x | 2,092 | 2,104 |
| `onpair` | 26,095 | 36,821 | 1.42x | 60,588 | 1,005,848 |
| `pco` | 1,089 | 12,406 | 11.7x | 5,476 | 4,516 |
| `pco_f32` | 4,227 | 21,721 | 5.10x | 2,131,076 | 2,130,156 |
| `pco_i16` | 2,717 | 5,838 | 2.15x | 1,878,788 | 1,878,460 |
| `pco_nullable` | 9,851 | 37,183 | 3.74x | 2,877,604 | 2,874,348 |
| `pco_u32` | 6,557 | 9,770 | 1.49x | 1,931,748 | 2,879,956 |
| `primitive` | 1,516 | 12,067 | 7.97x | 5,988 | 4,580 |
| `runend` | 729 | 6,855 | 9.40x | 4,620 | 3,508 |
| `sequence` | 907 | 11,237 | 12.3x | 5,484 | 4,524 |
| `sparse` | 716 | 2,716 | 3.78x | 131,172 | 3,876 |
| `struct` | 41,398 | 100,368 | 2.43x | 639,580 | 5,274,872 |
| `table_mixed` | 64,664 | 278,446 | 4.29x | 7,876,700 | 15,563,136 |
| `table_wide` | 6,396 | 36,468 | 5.62x | 5,163,244 | 5,168,720 |
| `varbin` | 49,998 | 120,211 | 2.40x | 637,012 | 5,356,244 |
| `varbinview` | 19,594 | 31,696 | 1.61x | 643,516 | 628,528 |
| `variant` | 186 | 859 | 4.60x | 2,220 | 53,392 |
| `zigzag` | 1,638 | 11,583 | 7.04x | 2,531,652 | 2,380,300 |
| `zstd` | 31,532 | 44,431 | 1.42x | 786,948 | 754,384 |
| `zstd_buffers` | 1,627 | 4,097 | 2.54x | 630,732 | 629,092 |
| `zstd_nullable` | 3,233 | 14,667 | 4.52x | 756,500 | 628,780 |

Vorticity took less time on 60 of 60 files; the median speedup is 4.23x.
Rust is timed under the faster of its two splits, file by file: one split per chunk on 53 of 60 files, its default on `alp`, `alp_no_patches`, `chunked`, `chunked_empty_chunks`, `chunked_one_chunk`, `masked_all_valid`, `table_wide`.
Vorticity's bytes were within 5 % of Rust's or fewer on 46 of 60 files; more than 5 % above them on `sparse` (33.8 times Rust's), `runend` (1.32 times), `primitive` (1.31 times), `constant` (1.28 times), `ext` (1.27 times), `pco` (1.21 times), `sequence` (1.21 times), `fastlanes_delta` (1.21 times), `chunked_one_chunk` (1.21 times), `zstd_nullable` (1.20 times), `fastlanes_rle` (1.19 times), `fixed_size_list` (1.19 times), `masked_all_valid` (1.13 times), `zigzag` (1.06 times).
Rust's writer declines `parquet_variant`, which is left out.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; Vortex 0.90.0 through the C ABI of `tools/vxbench-rs`, binary 2ab72ea31074; commit acd910ae, 2026-11-12 09:47 UTC.*
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
| a sequence | Sequence x77 | 0.00 | 9 | 1.3 | 0.26 |  |
| sorted runs of 1 000 | RunEnd x77 | 0.01 | 10 | 1.5 | 0.38 | Smallest: RunEnd x76, Pco x1, 0.01 B/value, scan 1.9 ms, take 0.73 ms; reads faster below 91 MB/s |
| timestamps (ms, increasing, jittered) | BitPacked x77 | 3.38 | 31 | 5.0 | 2.03 | Smallest: Pco x77, 1.35 B/value, scan 23.4 ms, take 20.74 ms; reads faster below 1,107 MB/s |
| random in 0..999 | BitPacked x77 | 1.25 | 21 | 3.0 | 1.03 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 21 | 2.2 | 0.68 |  |
| 100 003 distinct, repeating | BitPacked x77 | 4.63 | 35 | 5.5 | 1.46 | Smallest: Pco x77, 0.06 B/value, scan 17.3 ms, take 15.02 ms; reads faster below 3,892 MB/s |
| uniform 64-bit | Canonical x77 | 8.00 | 41 | 6.2 | 1.52 |  |
| random in 0..999, 10 % null | BitPacked x82 | 1.38 | 26 | 3.6 | 1.17 | Smallest: Pco x82, 1.26 B/value, scan 11.7 ms, take 8.79 ms; reads faster below 152 MB/s |

<details><summary>a sequence: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Sequence x77 | 44,380 | 0.00 | 9 | 1.3 | 0.26 |  |
| Fastest | Sequence x77 | 44,380 | 0.00 | 9 | 1.3 | 0.27 | same |
| Smallest | Sequence x77 | 44,380 | 0.00 | 9 | 1.4 | 0.26 | same |
| None | Canonical x77 | 80,043,724 | 8.00 | 43 | 6.4 | 1.53 | reads slower at any throughput |
| hint Dictionary | Sequence x77 | 44,380 | 0.00 | 105 | 1.3 | 0.26 | same |
| hint BitPacked | Sequence x77 | 44,380 | 0.00 | 9 | 1.3 | 0.26 | same |
| hint RunEnd | Sequence x77 | 44,380 | 0.00 | 9 | 1.4 | 0.29 | same |
| hint Zstd | Sequence x77 | 44,380 | 0.00 | 9 | 1.3 | 0.26 | same |

</details>

<details><summary>sorted runs of 1 000: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x77 | 88,004 | 0.01 | 10 | 1.5 | 0.38 |  |
| Fastest | RunEnd x77 | 88,004 | 0.01 | 11 | 1.5 | 0.39 | same |
| Smallest | RunEnd x76, Pco x1 | 53,748 | 0.01 | 90 | 1.9 | 0.73 | reads faster below 91 MB/s |
| None | Canonical x77 | 80,043,716 | 8.00 | 42 | 6.4 | 1.44 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 1,449,892 | 0.14 | 16 | 3.7 | 0.92 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 10,034,460 | 1.00 | 19 | 3.4 | 0.93 | reads slower at any throughput |
| hint RunEnd | RunEnd x77 | 88,004 | 0.01 | 11 | 1.5 | 0.37 | same |
| hint Zstd | Zstd x77 | 136,628 | 0.01 | 12 | 2.3 | 1.16 | reads slower at any throughput |

</details>

<details><summary>timestamps (ms, increasing, jittered): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 33,795,172 | 3.38 | 31 | 5.0 | 2.03 |  |
| Fastest | BitPacked x77 | 33,795,172 | 3.38 | 31 | 5.0 | 2.06 | same |
| Smallest | Pco x77 | 13,472,444 | 1.35 | 404 | 23.4 | 20.74 | reads faster below 1,107 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 41 | 6.5 | 1.61 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 33,795,172 | 3.38 | 128 | 5.0 | 2.05 | same |
| hint BitPacked | BitPacked x77 | 33,795,172 | 3.38 | 31 | 5.0 | 2.05 | same |
| hint RunEnd | BitPacked x77 | 33,795,172 | 3.38 | 38 | 5.2 | 2.19 | same |
| hint Zstd | Zstd x77 | 20,700,796 | 2.07 | 157 | 75.9 | 33.43 | reads faster below 185 MB/s |

</details>

<details><summary>random in 0..999: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 12,544,284 | 1.25 | 21 | 3.0 | 1.03 |  |
| Fastest | BitPacked x77 | 12,544,284 | 1.25 | 21 | 2.9 | 1.02 | same |
| Smallest | BitPacked x76, Pco x1 | 12,543,924 | 1.25 | 293 | 2.9 | 1.03 | same |
| None | Canonical x77 | 80,043,716 | 8.00 | 41 | 6.6 | 1.68 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,652,724 | 1.27 | 50 | 4.3 | 1.18 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 12,544,284 | 1.25 | 21 | 2.9 | 1.03 | same |
| hint RunEnd | BitPacked x77 | 12,544,284 | 1.25 | 22 | 3.0 | 1.03 | same |
| hint Zstd | Zstd x77 | 20,141,172 | 2.01 | 180 | 59.1 | 28.82 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,748 | 0.51 | 21 | 2.2 | 0.68 |  |
| Fastest | Dictionary x77 | 5,063,748 | 0.51 | 21 | 2.2 | 0.68 | same |
| Smallest | Dictionary x76, Pco x1 | 5,063,588 | 0.51 | 477 | 2.3 | 0.73 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 41 | 6.7 | 1.57 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,063,748 | 0.51 | 21 | 2.2 | 0.67 | same |
| hint BitPacked | Dictionary x77 | 5,063,748 | 0.51 | 42 | 2.2 | 0.67 | same |
| hint RunEnd | Dictionary x77 | 5,063,748 | 0.51 | 34 | 2.2 | 0.67 | same |
| hint Zstd | Zstd x77 | 10,539,396 | 1.05 | 135 | 44.6 | 20.34 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,932 | 0.50 | 28 | 1.9 | 0.54 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 46,295,652 | 4.63 | 35 | 5.5 | 1.46 |  |
| Fastest | BitPacked x77 | 46,295,652 | 4.63 | 35 | 5.4 | 1.41 | same |
| Smallest | Pco x77 | 580,924 | 0.06 | 342 | 17.3 | 15.02 | reads faster below 3,892 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 40 | 6.2 | 1.43 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 46,295,652 | 4.63 | 122 | 5.3 | 1.40 | same |
| hint BitPacked | BitPacked x77 | 46,295,652 | 4.63 | 34 | 5.3 | 1.39 | same |
| hint RunEnd | BitPacked x77 | 46,295,652 | 4.63 | 46 | 5.3 | 1.63 | same |
| hint Zstd | Zstd x77 | 51,815,868 | 5.18 | 128 | 49.5 | 39.31 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 23,603,548 | 2.36 | 89 | 5.9 | 2.11 | reads faster below 59,264 MB/s |

</details>

<details><summary>uniform 64-bit: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x77 | 80,043,732 | 8.00 | 41 | 6.2 | 1.52 |  |
| Fastest | Canonical x77 | 80,043,732 | 8.00 | 40 | 6.2 | 1.45 | same |
| Smallest | Canonical x77 | 80,043,732 | 8.00 | 308 | 6.3 | 1.44 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 38 | 6.5 | 1.58 | same |
| hint Dictionary | Canonical x77 | 80,043,732 | 8.00 | 152 | 6.5 | 1.49 | same |
| hint BitPacked | Canonical x77 | 80,043,732 | 8.00 | 96 | 6.4 | 1.46 | same |
| hint RunEnd | Canonical x77 | 80,043,732 | 8.00 | 95 | 6.7 | 1.58 | reads slower at any throughput |
| hint Zstd | Canonical x77 | 80,043,732 | 8.00 | 104 | 6.3 | 1.55 | same |

</details>

<details><summary>random in 0..999, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x82 | 13,800,460 | 1.38 | 26 | 3.6 | 1.17 |  |
| Fastest | BitPacked x82 | 13,800,460 | 1.38 | 27 | 3.6 | 1.18 | same |
| Smallest | Pco x82 | 12,567,268 | 1.26 | 291 | 11.7 | 8.79 | reads faster below 152 MB/s |
| None | Canonical x82 | 81,294,668 | 8.13 | 43 | 7.0 | 1.87 | reads slower at any throughput |
| hint Dictionary | Dictionary x82 | 12,676,396 | 1.27 | 67 | 7.4 | 1.25 | reads faster below 296 MB/s |
| hint BitPacked | BitPacked x82 | 13,800,460 | 1.38 | 26 | 3.6 | 1.17 | same |
| hint RunEnd | BitPacked x82 | 13,800,460 | 1.38 | 27 | 3.6 | 1.16 | same |
| hint Zstd | Zstd x82 | 19,283,940 | 1.93 | 168 | 56.4 | 26.62 | reads slower at any throughput |

</details>

### Floating point

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| prices (2 decimals) | Alp x77 | 2.51 | 40 | 5.3 | 1.89 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 24 | 2.2 | 0.72 |  |
| 1 000 distinct prices | Dictionary x77 | 1.28 | 52 | 4.4 | 1.20 |  |
| 100 003 distinct, repeating | Zstd x77 | 1.69 | 154 | 44.9 | 20.83 | Auto, 16 MiB chunks: Dictionary x5, 2.21 B/value, scan 8.1 ms, take 4.15 ms; reads faster above 142 MB/s; None: Canonical x77, 8.01 B/value, scan 6.5 ms, take 1.60 ms; reads faster above 1,644 MB/s |
| uniform in [0, 1) | AlpRd x77 | 6.91 | 54 | 9.1 | 2.35 | None: Canonical x77, 8.01 B/value, scan 6.2 ms, take 1.46 ms; reads faster above 3,713 MB/s |

<details><summary>prices (2 decimals): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Alp x77 | 25,059,572 | 2.51 | 40 | 5.3 | 1.89 |  |
| Fastest | Alp x77 | 25,059,572 | 2.51 | 41 | 5.4 | 1.94 | same |
| Smallest | Alp x77 | 25,058,708 | 2.51 | 805 | 5.5 | 2.12 | same |
| None | Canonical x77 | 80,053,572 | 8.01 | 44 | 6.3 | 1.41 | reads slower at any throughput |
| hint Dictionary | Alp x77 | 25,059,572 | 2.51 | 158 | 5.4 | 1.93 | same |
| hint Alp | Alp x77 | 25,059,572 | 2.51 | 40 | 5.3 | 1.90 | same |
| hint Zstd | Zstd x77 | 41,250,100 | 4.13 | 283 | 56.8 | 35.99 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,073,588 | 0.51 | 24 | 2.2 | 0.72 |  |
| Fastest | Dictionary x77 | 5,073,588 | 0.51 | 24 | 2.2 | 0.69 | same |
| Smallest | Dictionary x76, Pco x1 | 5,073,428 | 0.51 | 494 | 2.2 | 0.73 | same |
| None | Canonical x77 | 80,053,572 | 8.01 | 44 | 6.2 | 1.48 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,073,588 | 0.51 | 23 | 2.2 | 0.69 | same |
| hint Alp | Dictionary x77 | 5,073,588 | 0.51 | 69 | 2.2 | 0.70 | same |
| hint Zstd | Zstd x77 | 10,555,508 | 1.06 | 138 | 44.7 | 20.66 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,042,772 | 0.50 | 36 | 1.9 | 0.54 | reads faster at any throughput |

</details>

<details><summary>1 000 distinct prices: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 12,766,100 | 1.28 | 52 | 4.4 | 1.20 |  |
| Fastest | Dictionary x77 | 12,766,100 | 1.28 | 53 | 4.5 | 1.26 | same |
| Smallest | Dictionary x77 | 12,761,196 | 1.28 | 438 | 4.5 | 1.29 | same |
| None | Canonical x77 | 80,053,572 | 8.01 | 44 | 6.5 | 1.46 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,766,100 | 1.28 | 53 | 4.4 | 1.20 | same |
| hint Alp | Alp x77 | 12,766,100 | 1.28 | 47 | 5.6 | 1.21 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 26,083,252 | 2.61 | 212 | 46.9 | 23.79 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 12,555,572 | 1.26 | 52 | 4.2 | 1.03 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x77 | 16,919,732 | 1.69 | 154 | 44.9 | 20.83 |  |
| Fastest | Zstd x77 | 16,919,732 | 1.69 | 152 | 45.0 | 20.73 | same |
| Smallest | Zstd x77 | 16,919,732 | 1.69 | 274 | 45.1 | 20.67 | same |
| None | Canonical x77 | 80,053,572 | 8.01 | 42 | 6.5 | 1.60 | reads faster above 1,644 MB/s |
| hint Dictionary | Zstd x77 | 16,919,732 | 1.69 | 252 | 45.1 | 20.84 | same |
| hint Alp | Zstd x77 | 16,919,732 | 1.69 | 222 | 45.1 | 20.75 | same |
| hint Zstd | Zstd x77 | 16,919,732 | 1.69 | 152 | 45.2 | 20.85 | same |
| Auto, 16 MiB chunks | Dictionary x5 | 22,139,828 | 2.21 | 88 | 8.1 | 4.15 | reads faster above 142 MB/s |

</details>

<details><summary>uniform in [0, 1): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | AlpRd x77 | 69,064,220 | 6.91 | 54 | 9.1 | 2.35 |  |
| Fastest | AlpRd x77 | 69,064,220 | 6.91 | 54 | 9.4 | 2.47 | same |
| Smallest | Pco x77 | 67,619,508 | 6.76 | 498 | 24.9 | 31.63 | reads faster below 92 MB/s |
| None | Canonical x77 | 80,053,572 | 8.01 | 43 | 6.2 | 1.46 | reads faster above 3,713 MB/s |
| hint Dictionary | AlpRd x77 | 69,064,220 | 6.91 | 237 | 9.8 | 2.74 | reads slower at any throughput |
| hint Alp | AlpRd x77 | 69,064,220 | 6.91 | 188 | 9.5 | 2.57 | same |
| hint Zstd | AlpRd x77 | 69,064,220 | 6.91 | 240 | 9.8 | 2.39 | reads slower at any throughput |

</details>

### Text

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| 16 cities, random order | Dictionary x175 | 0.51 | 113 | 11.1 | 1.12 |  |
| 10 000 distinct ids | Dictionary x306 | 2.54 | 495 | 57.6 | 26.51 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 35.1 ms, take 2.80 ms; reads faster at any throughput; None: Canonical x306, 14.01 B/value, scan 47.7 ms, take 2.71 ms; reads faster above 11,615 MB/s |
| 10 000 distinct ids, 10 % null | Dictionary x306 | 2.45 | 515 | 63.6 | 27.23 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 38.7 ms, take 2.86 ms; reads faster at any throughput |
| unique UUIDs | Zstd x611 | 20.61 | 1540 | 284.1 | 294.68 | hint Fsst: Fsst x611, 22.44 B/value, scan 157.1 ms, take 12.65 ms; reads faster above 144 MB/s; None: Canonical x611, 36.02 B/value, scan 68.8 ms, take 4.30 ms; reads faster above 716 MB/s |
| log lines (~100 B) | Zstd x1221 | 13.71 | 1334 | 409.0 | 278.55 | hint Fsst: Fsst x1221, 22.01 B/value, scan 201.2 ms, take 21.22 ms; reads faster above 399 MB/s; None: Canonical x1221, 72.55 B/value, scan 144.5 ms, take 8.56 ms; reads faster above 2,224 MB/s |

<details><summary>16 cities, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x175 | 5,117,284 | 0.51 | 113 | 11.1 | 1.12 |  |
| Fastest | Dictionary x175 | 5,117,284 | 0.51 | 113 | 11.1 | 1.13 | same |
| Smallest | Dictionary x175 | 5,117,284 | 0.51 | 308 | 11.3 | 1.42 | same |
| None | Canonical x175 | 96,746,756 | 9.67 | 152 | 34.9 | 3.17 | reads slower at any throughput |
| hint Dictionary | Dictionary x175 | 5,117,284 | 0.51 | 113 | 11.2 | 1.14 | same |
| hint Fsst | Fsst x175 | 39,908,604 | 3.99 | 361 | 33.6 | 4.82 | reads slower at any throughput |
| hint Zstd | Zstd x175 | 17,427,100 | 1.74 | 243 | 85.1 | 52.83 | reads slower at any throughput |

</details>

<details><summary>10 000 distinct ids: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 25,433,788 | 2.54 | 495 | 57.6 | 26.51 |  |
| Fastest | Dictionary x306 | 25,433,788 | 2.54 | 497 | 57.6 | 26.36 | same |
| Smallest | Dictionary x305, Zstd x1 | 23,452,500 | 2.35 | 1318 | 76.5 | 42.91 | reads faster below 105 MB/s |
| None | Canonical x306 | 140,098,172 | 14.01 | 213 | 47.7 | 2.71 | reads faster above 11,615 MB/s |
| hint Dictionary | Dictionary x306 | 25,433,788 | 2.54 | 500 | 57.6 | 26.58 | same |
| hint Fsst | Fsst x306 | 62,863,260 | 6.29 | 424 | 60.0 | 7.12 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 27,097,628 | 2.71 | 385 | 101.1 | 56.91 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 18,014,012 | 1.80 | 398 | 35.1 | 2.80 | reads faster at any throughput |

</details>

<details><summary>10 000 distinct ids, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 24,539,284 | 2.45 | 515 | 63.6 | 27.23 |  |
| Fastest | Dictionary x306 | 24,539,284 | 2.45 | 517 | 63.7 | 27.26 | same |
| Smallest | Dictionary x305, Zstd x1 | 22,854,628 | 2.29 | 1317 | 82.3 | 43.06 | reads faster below 90 MB/s |
| None | Canonical x306 | 151,883,732 | 15.19 | 282 | 62.9 | 4.51 | reads slower at any throughput |
| hint Dictionary | Dictionary x306 | 24,539,284 | 2.45 | 519 | 63.3 | 26.92 | same |
| hint Fsst | Fsst x306 | 63,725,748 | 6.37 | 507 | 70.7 | 8.24 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 25,595,636 | 2.56 | 404 | 115.8 | 52.67 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 17,982,604 | 1.80 | 456 | 38.7 | 2.86 | reads faster at any throughput |

</details>

<details><summary>unique UUIDs: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x611 | 206,058,588 | 20.61 | 1540 | 284.1 | 294.68 |  |
| Fastest | Zstd x611 | 206,058,588 | 20.61 | 1538 | 283.7 | 294.38 | same |
| Smallest | Zstd x611 | 206,058,588 | 20.61 | 2980 | 283.2 | 293.40 | same |
| None | Canonical x611 | 360,155,516 | 36.02 | 199 | 68.8 | 4.30 | reads faster above 716 MB/s |
| hint Dictionary | Zstd x610, Fsst x1 | 206,059,580 | 20.61 | 5238 | 283.5 | 294.22 | same |
| hint Fsst | Fsst x611 | 224,384,604 | 22.44 | 1444 | 157.1 | 12.65 | reads faster above 144 MB/s |
| hint Zstd | Zstd x611 | 206,058,588 | 20.61 | 1545 | 284.5 | 294.59 | same |

</details>

<details><summary>log lines (~100 B): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x1221 | 137,107,188 | 13.71 | 1334 | 409.0 | 278.55 |  |
| Fastest | Zstd x1221 | 137,107,188 | 13.71 | 1333 | 409.1 | 278.98 | same |
| Smallest | Zstd x1221 | 137,107,188 | 13.71 | 1650 | 409.3 | 278.55 | same |
| None | Canonical x1221 | 725,468,444 | 72.55 | 536 | 144.5 | 8.56 | reads faster above 2,224 MB/s |
| hint Dictionary | Zstd x1221 | 137,107,188 | 13.71 | 3723 | 408.5 | 278.10 | same |
| hint Fsst | Fsst x1221 | 220,058,684 | 22.01 | 1578 | 201.2 | 21.22 | reads faster above 399 MB/s |
| hint Zstd | Zstd x1221 | 137,107,188 | 13.71 | 1340 | 407.9 | 278.32 | same |

</details>

### Booleans

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| half true | Canonical x10 | 0.13 | 4 | 0.2 | 0.28 |  |
| 1 % true | RunEnd x10 | 0.05 | 5 | 2.3 | 0.38 | None: Canonical x10, 0.13 B/value, scan 0.2 ms, take 0.27 ms; reads faster above 344 MB/s |

<details><summary>half true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.2 | 0.28 |  |
| Fastest | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.2 | 0.28 | same |
| Smallest | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.2 | 0.28 | same |
| None | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.3 | 0.28 | same |
| hint RunEnd | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.3 | 0.28 | same |

</details>

<details><summary>1 % true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x10 | 548,092 | 0.05 | 5 | 2.3 | 0.38 |  |
| Fastest | RunEnd x10 | 548,092 | 0.05 | 5 | 2.4 | 0.38 | same |
| Smallest | RunEnd x10 | 164,692 | 0.02 | 14 | 2.7 | 0.80 | reads faster below 931 MB/s |
| None | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.2 | 0.27 | reads faster above 344 MB/s |
| hint RunEnd | RunEnd x10 | 548,092 | 0.05 | 5 | 2.3 | 0.40 | same |

</details>

### The profiles side by side

`Fastest` wrote what `Auto` wrote on 20 of 20 columns. `Smallest` wrote something else on *i64, sorted runs of 1 000*, *i64, timestamps (ms, increasing, jittered)*, *i64, random in 0..999*, *i64, 16 distinct, random order*, *i64, 100 003 distinct, repeating*, *i64, random in 0..999, 10 % null*, *f64, 16 distinct, random order*, *f64, uniform in [0, 1)*, *utf8, 10 000 distinct ids*, *utf8, 10 000 distinct ids, 10 % null*, and took up to 22.4 times `Auto`'s write, 477 ms against 21 ms on *i64, 16 distinct, random order*, since it tries every scheme on every chunk.
`None`, the plain form, made the largest file on 20 of 20 columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; commit acd910ae, 2026-11-12 09:53 UTC.*
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

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; commit acd910ae, 2026-11-12 09:54 UTC.*
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
| &#39;i64 scalar&#39; | 17       |  95.171 μs | 1.5881 μs | 0.4124 μs |   1.00x | Baseline        |   1.45 |  5.51 |         - |          NA |
| &#39;i64 vector&#39; | 17       |   8.954 μs | 0.0778 μs | 0.0202 μs |   10.6x | Faster          |   0.14 | 58.55 |         - |          NA |
| &#39;i32 scalar&#39; | 17       | 106.541 μs | 2.4226 μs | 0.6291 μs |   0.89x | Same            |   1.63 |  2.46 |         - |          NA |
| &#39;i64 pack&#39;   | 17       |  10.256 μs | 0.0745 μs | 0.0194 μs |   9.28x | Faster          |   0.16 | 51.12 |         - |          NA |
| &#39;i32 pack&#39;   | 17       |  12.217 μs | 0.1926 μs | 0.0298 μs |   7.79x | Faster          |   0.19 | 21.46 |         - |          NA |
| &#39;i32 vector&#39; | 17       |   4.748 μs | 0.0260 μs | 0.0068 μs |   20.0x | Faster          |   0.07 | 55.22 |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit acd910ae, 2026-11-12 09:55 UTC.*
<!-- /results: kernel:FastLanesKernelBenchmarks -->

<!-- results: kernel:RowEncodingBenchmarks -->
### `RowEncodingBenchmarks`

| Method                                   | Entry                | Mean       | Error     | StdDev    | ns/row | GB/s | Allocated |
|----------------------------------------- |--------------------- |-----------:|----------:|----------:|-------:|-----:|----------:|
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nical [33]** |  **53.292 μs** | **1.4589 μs** | **0.3789 μs** |  **13.01** | **3.30** |         **-** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nical [33] |  55.320 μs | 1.0190 μs | 0.1577 μs |  13.51 | 3.18 |         - |
| &#39;encode, then sort rows by key&#39;          | conta(...)nical [33] | 119.064 μs | 0.8505 μs | 0.1316 μs |  29.07 | 1.48 |      48 B |
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nulls [33]** |  **16.836 μs** | **0.1044 μs** | **0.0271 μs** |  **16.44** | **4.01** |         **-** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nulls [33] |  18.283 μs | 0.1796 μs | 0.0278 μs |  17.85 | 3.70 |         - |
| &#39;encode, then sort rows by key&#39;          | conta(...)nulls [33] |  28.258 μs | 0.1224 μs | 0.0318 μs |  27.60 | 2.39 |      48 B |
| **&#39;encode a batch to row keys&#39;**             | **encod(...)r1025 [31]** |   **9.852 μs** | **0.0476 μs** | **0.0124 μs** |   **9.61** | **0.94** |         **-** |
| &#39;encode a batch to row keys, descending&#39; | encod(...)r1025 [31] |   9.830 μs | 0.0795 μs | 0.0206 μs |   9.59 | 0.94 |         - |
| &#39;encode, then sort rows by key&#39;          | encod(...)r1025 [31] |  25.563 μs | 0.2335 μs | 0.0606 μs |  24.94 | 0.36 |      48 B |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit acd910ae, 2026-11-12 09:55 UTC.*
<!-- /results: kernel:RowEncodingBenchmarks -->

<!-- results: kernel:CompressorBenchmarks -->
### `CompressorBenchmarks`

| Method                       | Column | Mean          | Error      | StdDev     | Speedup | MannWhitney(5%) | Allocated | Alloc Ratio |
|----------------------------- |------- |--------------:|-----------:|-----------:|--------:|---------------- |----------:|------------:|
| &#39;Choose, the whole decision&#39; | i64    | 30,478.544 ns | 49.7930 ns | 12.9311 ns |   1.00x | Baseline        |         - |          NA |
| &#39;candidate: sequence&#39;        | i64    |     15.608 ns |  0.2379 ns |  0.0368 ns |  1,953x | Faster          |         - |          NA |
| &#39;candidate: bit packing&#39;     | i64    |  4,233.383 ns | 32.2588 ns |  8.3775 ns |   7.20x | Faster          |         - |          NA |
| &#39;candidate: FSST&#39;            | i64    |      1.854 ns |  0.0181 ns |  0.0047 ns | 16,435x | Faster          |         - |          NA |
| &#39;candidate: zstd&#39;            | i64    |  7,537.581 ns | 63.3091 ns | 16.4412 ns |   4.04x | Faster          |         - |          NA |
| &#39;candidate: ALP&#39;             | i64    |      2.605 ns |  0.0290 ns |  0.0075 ns | 11,699x | Faster          |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit acd910ae, 2026-11-12 09:56 UTC.*
<!-- /results: kernel:CompressorBenchmarks -->

<!-- results: kernel:LanesBenchmarks -->
### `LanesBenchmarks`

| Method                 | Lanes | Mean       | Error    | StdDev   | Speedup | MannWhitney(5%) | Completed Work Items | Lock Contentions | Allocated | Alloc Ratio |
|----------------------- |------ |-----------:|---------:|---------:|--------:|---------------- |---------------------:|-----------------:|----------:|------------:|
| &#39;ours, n lanes&#39;        | 4     |   551.3 μs | 69.63 μs | 10.78 μs |   1.00x | Baseline        |             105.6989 |                - |   51072 B |        1.00 |
| &#39;reference, n workers&#39; | 4     | 1,115.3 μs | 24.81 μs |  6.44 μs |   0.49x | Slower          |                    - |                - |         - |        0.00 |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26300; .NET 11.0.0-rc.3.26522.148; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit acd910ae, 2026-11-12 09:56 UTC.*
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
