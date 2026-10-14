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
ceiling. The per-encoding corpus is the generator's current one, the same 61 files as on the arm64
page. The kernel tables' speedups are computed from the Mean column of their run.

The ground rules:

* Vorticity runs as a Native AOT binary built for the machine's instruction set, with the
  workstation garbage collector, where a section says so, and on the JIT otherwise. Vortex Rust
  0.86.1 is built the way upstream builds its own benchmarks (mimalloc, `-C target-cpu=native`, one
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

| column | Vorticity's, 2,449,820 bytes | Vortex Rust's, 7,903,964 bytes |
|---|---|---|
| `monotone` | 16 chunks of `vortex.sequence` | 8 chunks of `vortex.sequence` |
| `value` | 16 chunks of `vortex.zstd` | 8 chunks of `vortex.alprd` |
| `label` | 16 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 2 chunks of `fastlanes.bitpacked` |
| `flag` | 16 chunks of `vortex.bool` | one chunk of `vortex.bool` |

#### One core

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.5-0.6) | 1.0 (1.0-1.0) | 1.79x | — | — | 2.9 KiB | 11 / 9 MiB |
| `open` | Vortex Rust's | 0.6 (0.5-0.6) | 1.0 (0.9-1.1) | 1.81x | — | — | 3.6 KiB | 12 / 9 MiB |
| `scan` | Vorticity's | 7.1 (6.8-7.2) | 20.7 (20.5-21.5) | 2.91x | 3.8 | 1.3 | 19.8 KiB | 15 / 45 MiB |
| `scan` | Vortex Rust's | 3.8 (3.7-4.4) | 8.5 (8.3-8.7) | 2.24x | 7.1 | 3.1 | 16.1 KiB | 22 / 51 MiB |
| `project` | Vorticity's | 0.9 (0.9-0.9) | 2.4 (2.3-2.4) | 2.74x | 9.6 | 3.5 | 13.6 KiB | 13 / 18 MiB |
| `project` | Vortex Rust's | 1.0 (1.0-1.1) | 2.4 (2.2-2.6) | 2.38x | 8.3 | 3.5 | 11.4 KiB | 13 / 18 MiB |
| `filter-narrow` | Vorticity's | 1.8 (1.8-1.9) | 2.3 (2.3-2.4) | 1.27x | 0.1 | 0.1 | 46.5 KiB | 16 / 13 MiB |
| `filter-narrow` | Vortex Rust's | 1.9 (1.8-1.9) | 2.2 (2.2-2.3) | 1.21x | 0.1 | 0.1 | 26.2 KiB | 18 / 13 MiB |
| `filter-wide` | Vorticity's | 4.3 (4.2-4.3) | 11.4 (11.3-11.7) | 2.69x | 3.1 | 1.2 | 29.0 KiB | 15 / 29 MiB |
| `filter-wide` | Vortex Rust's | 3.1 (2.9-3.9) | 5.3 (5.2-5.5) | 1.72x | 4.3 | 2.5 | 26.2 KiB | 22 / 32 MiB |
| `take` | Vorticity's | 5.8 (5.7-6.0) | 8.5 (8.4-8.8) | 1.47x | — | — | 43.5 KiB | 14 / 14 MiB |
| `take` | Vortex Rust's | 2.1 (2.0-2.3) | 3.1 (3.1-3.3) | 1.49x | — | — | 39.8 KiB | 16 / 16 MiB |
| `write` | Vorticity's | 31.1 (30.8-31.2) | 74.8 (74.3-75.2) | 2.40x | 0.9 | 0.4 | 36.1 KiB | 20 / 75 MiB |
| `write` | Vortex Rust's | 27.9 (27.8-28.5) | 62.4 (62.2-63.7) | 2.23x | 1.0 | 0.4 | 32.4 KiB | 27 / 80 MiB |
| `append` | Vorticity's | 11.0 (10.7-11.3) | not asked | n/a | — | — | 1.3 MiB | 21 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.5 (0.5-0.6) | 2.5 (2.4-2.6) | 4.53x | — | — | 2.9 KiB | 11 / 10 MiB |
| `open` | Vortex Rust's | 0.6 (0.6-0.6) | 2.5 (2.4-2.6) | 4.35x | — | — | 3.6 KiB | 12 / 10 MiB |
| `scan` | Vorticity's | 3.9 (3.7-4.1) | 6.3 (6.2-6.7) | 1.63x | 6.9 | 4.2 | 46.9 KiB | 47 / 49 MiB |
| `scan` | Vortex Rust's | 3.8 (3.8-4.0) | 6.4 (6.3-6.6) | 1.66x | 7.0 | 4.2 | 27.4 KiB | 57 / 54 MiB |
| `project` | Vorticity's | 1.6 (1.6-1.7) | 3.3 (3.3-3.4) | 2.08x | 5.3 | 2.5 | 31.0 KiB | 21 / 19 MiB |
| `project` | Vortex Rust's | 1.6 (1.6-1.8) | 3.4 (3.3-3.4) | 2.06x | 5.1 | 2.5 | 24.4 KiB | 21 / 20 MiB |
| `filter-narrow` | Vorticity's | 1.7 (1.6-1.8) | 4.0 (3.9-4.1) | 2.40x | 0.2 | 0.1 | 48.6 KiB | 14 / 15 MiB |
| `filter-narrow` | Vortex Rust's | 1.7 (1.6-1.9) | 3.9 (3.7-4.0) | 2.26x | 0.2 | 0.1 | 31.0 KiB | 15 / 15 MiB |
| `filter-wide` | Vorticity's | 3.1 (3.1-3.4) | 5.6 (5.2-6.2) | 1.77x | 4.2 | 2.4 | 40.5 KiB | 31 / 33 MiB |
| `filter-wide` | Vortex Rust's | 3.0 (3.0-3.3) | 4.9 (4.7-5.2) | 1.64x | 4.5 | 2.7 | 32.9 KiB | 39 / 36 MiB |
| `take` | Vorticity's | 4.1 (4.0-4.4) | 4.8 (4.3-4.9) | 1.16x | — | — | 121.0 KiB | 28 / 24 MiB |
| `take` | Vortex Rust's | 2.6 (2.5-2.7) | 4.4 (4.3-4.9) | 1.67x | — | — | 100.2 KiB | 20 / 19 MiB |
| `write` | Vorticity's | 12.8 (12.4-13.2) | 21.9 (20.6-22.4) | 1.71x | 2.1 | 1.2 | 2.6 MiB | 60 / 121 MiB |
| `write` | Vortex Rust's | 13.1 (12.8-13.2) | 21.0 (20.1-29.7) | 1.60x | 2.0 | 1.3 | 2.3 MiB | 72 / 124 MiB |
| `append` | Vorticity's | 10.4 (10.3-10.6) | not asked | n/a | — | — | 2.4 MiB | 23 / — MiB |

### 10,485,760 rows

| column | Vorticity's, 24,475,804 bytes | Vortex Rust's, 78,986,804 bytes |
|---|---|---|
| `monotone` | 160 chunks of `vortex.sequence` | 80 chunks of `vortex.sequence` |
| `value` | 160 chunks of `vortex.zstd` | 80 chunks of `vortex.alprd` |
| `label` | 160 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 20 chunks of `fastlanes.bitpacked` |
| `flag` | 160 chunks of `vortex.bool` | 3 chunks of `vortex.bool` |

#### One core

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.6) | 1.1 (1.1-1.1) | 1.74x | — | — | 2.9 KiB | 12 / 9 MiB |
| `open` | Vortex Rust's | 0.6 (0.5-0.6) | 1.0 (1.0-1.1) | 1.77x | — | — | 3.6 KiB | 12 / 9 MiB |
| `scan` | Vorticity's | 59.1 (58.8-59.5) | 186.0 (184.4-188.7) | 3.15x | 4.5 | 1.4 | 89.6 KiB | 15 / 296 MiB |
| `scan` | Vortex Rust's | 24.7 (24.4-26.6) | 75.6 (75.2-76.6) | 3.06x | 10.8 | 3.5 | 33.5 KiB | 24 / 408 MiB |
| `project` | Vorticity's | 2.0 (2.0-2.0) | 11.5 (11.5-11.6) | 5.84x | 42.5 | 7.3 | 51.2 KiB | 13 / 75 MiB |
| `project` | Vortex Rust's | 2.4 (2.3-2.5) | 11.6 (11.5-12.3) | 4.84x | 34.9 | 7.2 | 21.8 KiB | 13 / 75 MiB |
| `filter-narrow` | Vorticity's | 2.5 (2.5-3.1) | 4.8 (4.8-4.9) | 1.91x | 1.0 | 0.6 | 122.3 KiB | 16 / 17 MiB |
| `filter-narrow` | Vortex Rust's | 2.6 (2.6-3.0) | 3.3 (3.2-3.3) | 1.26x | 1.0 | 0.8 | 67.2 KiB | 21 / 18 MiB |
| `filter-wide` | Vorticity's | 30.7 (30.3-32.4) | 98.9 (98.4-99.6) | 3.23x | 4.4 | 1.3 | 122.3 KiB | 15 / 185 MiB |
| `filter-wide` | Vortex Rust's | 15.4 (15.3-16.2) | 40.4 (40.1-42.4) | 2.61x | 8.6 | 3.3 | 67.2 KiB | 24 / 211 MiB |
| `take` | Vorticity's | 23.7 (23.2-23.8) | 69.0 (68.4-70.0) | 2.91x | — | — | 113.3 KiB | 13 / 38 MiB |
| `take` | Vortex Rust's | 5.4 (5.2-5.6) | 8.5 (8.3-9.2) | 1.59x | — | — | 61.2 KiB | 22 / 28 MiB |
| `write` | Vorticity's | 274.9 (272.2-277.7) | 670.3 (667.5-685.2) | 2.44x | 1.0 | 0.4 | 110.9 KiB | 21 / 378 MiB |
| `write` | Vortex Rust's | 239.0 (238.2-247.5) | 559.2 (552.3-568.3) | 2.34x | 1.1 | 0.5 | 54.8 KiB | 30 / 457 MiB |
| `append` | Vorticity's | 47.5 (47.0-48.1) | not asked | n/a | — | — | 3.2 MiB | 27 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | speedup | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.6) | 2.6 (2.5-2.8) | 4.52x | — | — | 2.9 KiB | 11 / 10 MiB |
| `open` | Vortex Rust's | 0.6 (0.6-0.6) | 2.6 (2.4-2.6) | 4.48x | — | — | 3.6 KiB | 11 / 10 MiB |
| `scan` | Vorticity's | 19.6 (19.0-20.1) | 24.8 (24.7-26.2) | 1.27x | 13.6 | 10.8 | 157.2 KiB | 88 / 300 MiB |
| `scan` | Vortex Rust's | 25.0 (24.8-25.2) | 33.2 (32.9-33.2) | 1.33x | 10.7 | 8.0 | 57.6 KiB | 120 / 354 MiB |
| `project` | Vorticity's | 3.1 (3.0-3.3) | 7.5 (7.3-7.6) | 2.44x | 27.5 | 11.3 | 88.2 KiB | 30 / 82 MiB |
| `project` | Vortex Rust's | 3.6 (3.5-4.1) | 7.4 (7.3-7.5) | 2.03x | 23.0 | 11.3 | 68.3 KiB | 46 / 83 MiB |
| `filter-narrow` | Vorticity's | 2.1 (2.0-2.3) | 5.9 (5.5-6.2) | 2.77x | 1.2 | 0.4 | 126.3 KiB | 18 / 21 MiB |
| `filter-narrow` | Vortex Rust's | 2.6 (2.6-2.9) | 5.0 (4.8-5.6) | 1.91x | 1.0 | 0.5 | 75.1 KiB | 21 / 22 MiB |
| `filter-wide` | Vorticity's | 12.0 (11.5-12.6) | 16.6 (16.4-17.0) | 1.39x | 11.2 | 8.0 | 191.2 KiB | 89 / 190 MiB |
| `filter-wide` | Vortex Rust's | 17.0 (16.3-17.7) | 20.2 (19.9-20.5) | 1.19x | 7.8 | 6.6 | 650.9 KiB | 213 / 219 MiB |
| `take` | Vorticity's | 10.2 (9.9-10.4) | 12.0 (11.6-13.7) | 1.17x | — | — | 274.7 KiB | 62 / 62 MiB |
| `take` | Vortex Rust's | 5.0 (4.0-5.2) | 7.5 (7.4-8.0) | 1.50x | — | — | 169.9 KiB | 29 / 33 MiB |
| `write` | Vorticity's | 75.5 (75.1-79.1) | 122.1 (119.6-124.5) | 1.62x | 3.5 | 2.2 | 3.8 MiB | 103 / 473 MiB |
| `write` | Vortex Rust's | 80.0 (78.4-80.1) | 128.8 (127.6-138.5) | 1.61x | 3.3 | 2.1 | 2.1 MiB | 134 / 554 MiB |
| `append` | Vorticity's | 43.7 (43.5-44.5) | not asked | n/a | — | — | 2.5 MiB | 60 / — MiB |

`open`: open the file and read no rows. `scan`: read every column of every row. `project`: read one column of four. `filter-narrow`: read the rows of a band holding about one in a hundred. `filter-wide`: read the rows of a band holding about half. `take`: take a thousand rows spread across the file. `write`: read the file and encode it back out. `append`: append a tenth of the rows to a copy of the file.

**On one core.** Vorticity took less time than Rust on 28 of 28 compared rows.
The highest speedup is `project` at 10,485,760 rows on Vorticity's file (5.84x), the lowest
`filter-narrow` at 1,048,576 rows on Vortex Rust's file (1.21x).

**On all 32 cores.** Vorticity took less time than Rust on 28 of 28 compared rows.
The highest speedup is `open` at 1,048,576 rows on Vorticity's file (4.53x), the lowest
`take` at 1,048,576 rows on Vorticity's file (1.16x).

**The process start** is not in the figures: 44 ms for Vorticity's native binary and 29 ms for Rust's,
most of it the operating system starting a binary, and 104 ms for the managed runtime on the JIT.

**Rust's splits.** On one core, one split per chunk was the faster of its two on 8 of 14 rows on Vorticity's file and 12 of 14 rows on Vortex Rust's file;
the two differ only where a chunk holds more than 100,000 rows.

**Not asked of Rust**: `append`, which its harness has no entry point for; those figures
are Vorticity's alone.

### A first call on the JIT

The same scenarios in the framework-dependent build, under `dotnet`, on one core: what a
process that is not compiled ahead of time pays the first time, the code compiling as it runs.
Milliseconds, the median of the same runs.

| scenario | 1,048,576 rows, Vorticity's file | 1,048,576 rows, Vortex Rust's file | 10,485,760 rows, Vorticity's file | 10,485,760 rows, Vortex Rust's file |
|---|---:|---:|---:|---:|
| `open` | 20.3 | 20.1 | 19.9 | 20.4 |
| `scan` | 120.6 | 110.8 | 235.5 | 143.9 |
| `project` | 69.4 | 77.4 | 73.1 | 80.0 |
| `filter-narrow` | 164.3 | 142.9 | 162.6 | 157.0 |
| `filter-wide` | 138.7 | 135.7 | 200.9 | 162.3 |
| `take` | 128.6 | 104.6 | 181.7 | 112.8 |
| `write` | 327.6 | 328.7 | 885.0 | 816.6 |
| `append` | 325.9 | — | 409.6 | — |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1, rustc 1.98.1 (48a229cea 2026-09-01); commit e813ac54, 2026-10-12 08:14 UTC.*
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
| `alp` | 1,000,000 | 0.97 | 2.76 | 2.85x | 8.3 | 2.9 |
| `alp_no_patches` | 1,000,000 | 0.60 | 1.93 | 3.20x | 13.3 | 4.1 |
| `alp_patched_no_chunk_offsets` | 1,000,000 | 0.62 | 1.91 | 3.08x | 12.9 | 4.2 |
| `alprd` | 1,000,000 | 0.81 | 1.66 | 2.05x | 9.9 | 4.8 |
| `bool` | 1,000,000 | 0.15 | 0.20 | 1.33x | 0.9 | 0.6 |
| `bool_bit_offset3` | 1,000,000 | 0.15 | 0.19 | 1.23x | 0.8 | 0.7 |
| `bool_bit_offset7` | 1,000,000 | 0.15 | 0.19 | 1.29x | 0.8 | 0.6 |
| `bool_bit_offset_straddle` | 1,000,000 | 0.15 | 0.19 | 1.26x | 0.8 | 0.7 |
| `bytebool` | 1,000,000 | 0.18 | 0.34 | 1.89x | 0.7 | 0.4 |
| `chunked` | 1,000,000 | 0.64 | 2.31 | 3.59x | 12.5 | 3.5 |
| `chunked_bool` | 1,000,000 | 0.17 | 0.21 | 1.30x | 0.8 | 0.6 |
| `chunked_decimal` | 1,000,000 | 0.63 | 2.22 | 3.49x | 12.6 | 3.6 |
| `chunked_empty_chunks` | 1,000,000 | 0.64 | 2.25 | 3.50x | 12.4 | 3.6 |
| `chunked_mixed_validity` | 1,000,000 | 0.69 | 2.11 | 3.06x | 11.7 | 3.8 |
| `chunked_one_chunk` | 1,000,000 | 0.66 | 2.20 | 3.33x | 12.1 | 3.6 |
| `chunked_varbinview` | 1,000,000 | 3.98 | 14.4 | 3.61x | 3.4 | 0.9 |
| `constant` | 1,000,000 | 0.14 | 0.17 | 1.22x | 58.6 | 48.2 |
| `datetimeparts` | 1,000,000 | 1.42 | 3.28 | 2.31x | 5.6 | 2.4 |
| `decimal` | 1,000,000 | 0.65 | 2.11 | 3.27x | 12.4 | 3.8 |
| `decimal_byte_parts` | 1,000,000 | 0.65 | 2.11 | 3.27x | 12.4 | 3.8 |
| `dict` | 1,000,000 | 1.02 | 1.41 | 1.39x | 5.9 | 4.2 |
| `dict_nullable_codes` | 1,000,000 | 1.33 | 2.39 | 1.80x | 4.2 | 2.3 |
| `dict_nullable_values_nonnull_codes` | 1,000,000 | 1.15 | 1.33 | 1.16x | 5.0 | 4.3 |
| `dict_u64_codes` | 1,000,000 | 1.34 | 2.27 | 1.69x | 4.5 | 2.6 |
| `dict_u8_codes` | 1,000,000 | 0.91 | 0.96 | 1.06x | 8.8 | 8.4 |
| `ext` | 1,000,000 | 0.65 | 2.10 | 3.24x | 12.3 | 3.8 |
| `fastlanes_bitpacked` | 1,000,000 | 0.28 | 0.44 | 1.55x | 14.2 | 9.1 |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,000,000 | 0.29 | 0.44 | 1.50x | 13.8 | 9.2 |
| `fastlanes_delta` | 1,000,000 | 0.71 | 1.94 | 2.73x | 11.3 | 4.1 |
| `fastlanes_for` | 1,000,000 | 0.62 | 1.51 | 2.45x | 13.0 | 5.3 |
| `fastlanes_rle` | 1,000,000 | 0.49 | 0.79 | 1.62x | 8.1 | 5.0 |
| `fixed_size_list` | 1,000,000 | 1.00 | 3.24 | 3.24x | 12.0 | 3.7 |
| `fsst` | 1,000,000 | 12.1 | 14.1 | 1.17x | 4.6 | 4.0 |
| `list` | 1,000,000 | 1.37 | 3.34 | 2.44x | 7.3 | 3.0 |
| `listview` | 1,000,000 | 2.52 | 6.22 | 2.47x | 4.0 | 1.6 |
| `map` | 1,000,000 | 5.36 | 16.7 | 3.12x | 3.7 | 1.2 |
| `masked` | 1,000,000 | 0.38 | 1.20 | 3.12x | 10.7 | 3.4 |
| `masked_all_invalid` | 1,000,000 | 0.38 | 1.13 | 2.94x | 10.4 | 3.6 |
| `masked_all_valid` | 1,000,000 | 0.39 | 1.12 | 2.90x | 10.4 | 3.6 |
| `null` | 1,000,000 | 0.14 | 0.16 | 1.20x | — | — |
| `onpair` | 1,000,000 | 6.31 | 9.38 | 1.49x | 3.4 | 2.3 |
| `parquet_variant` | 1,000,000 | 3.50 | 17.7 | 5.05x | 3.4 | 0.7 |
| `pco` | 1,000,000 | 0.50 | 1.33 | 2.68x | 16.1 | 6.0 |
| `pco_f32` | 1,000,000 | 2.49 | 3.10 | 1.24x | 1.6 | 1.3 |
| `pco_i16` | 1,000,000 | 2.37 | 2.65 | 1.12x | 0.8 | 0.8 |
| `pco_nullable` | 1,000,000 | 2.89 | 4.42 | 1.53x | 2.8 | 1.8 |
| `pco_u32` | 1,000,000 | 3.11 | 4.30 | 1.38x | 1.3 | 0.9 |
| `primitive` | 1,000,000 | 0.67 | 2.15 | 3.23x | 12.0 | 3.7 |
| `runend` | 1,000,000 | 0.20 | 0.27 | 1.34x | 19.6 | 14.7 |
| `sequence` | 1,000,000 | 0.25 | 0.29 | 1.13x | 31.6 | 27.9 |
| `sparse` | 1,000,000 | 0.22 | 0.28 | 1.24x | 17.8 | 14.4 |
| `struct` | 1,000,000 | 2.12 | 10.5 | 4.95x | 7.0 | 1.4 |
| `table_mixed` | 1,000,000 | 15.3 | 39.9 | 2.62x | 4.6 | 1.8 |
| `table_wide` | 50,000 | 40.8 | 107.5 | 2.64x | 9.8 | 3.7 |
| `varbin` | 1,000,000 | 2.58 | 17.0 | 6.57x | 6.2 | 0.9 |
| `varbinview` | 1,000,000 | 3.79 | 12.3 | 3.26x | 3.6 | 1.1 |
| `variant` | 1,000,000 | 0.14 | 0.17 | 1.21x | 114.7 | 94.7 |
| `zigzag` | 1,000,000 | 0.37 | 1.02 | 2.76x | 10.8 | 3.9 |
| `zstd` | 1,000,000 | 7.39 | 12.3 | 1.66x | 4.3 | 2.6 |
| `zstd_buffers` | 1,000,000 | 0.50 | 0.53 | 1.06x | 16.1 | 15.2 |
| `zstd_nullable` | 1,000,000 | 1.91 | 3.76 | 1.97x | 4.3 | 2.2 |

Rust is timed under each of its two ways of splitting a scan, three processes each, and its
figure is the faster: its default cuts a chunk of more than 100,000 rows into splits of 100,000 and
decodes the whole chunk again for each, which on these files of one chunk of a million rows is ten
times; one split per chunk decodes it once, but builds a chunk made of smaller arrays in one
piece. One split per chunk was the faster on 49 of 61 files, its default on `alprd`, `chunked`, `chunked_decimal`, `chunked_empty_chunks`, `chunked_mixed_validity`, `chunked_one_chunk`, `decimal`, `dict_u64_codes`, `fastlanes_for`, `fixed_size_list`, `masked_all_invalid`, `masked_all_valid`.

Vorticity decoded 61 of 61 files in less time than Rust; the median speedup is 2.31x.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1, rustc 1.98.1 (48a229cea 2026-09-01); commit 9e6e3edf, 2026-10-12 16:55 UTC.*
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
| full scan | 860.8 | 2,986.3 | 3.46x | 3.35 to 3.51 |
| projected scan, 1 of 5 columns | 260.4 | 516.4 | 1.98x | 1.97 to 2.01 |
| open to first batch | 220.0 | 2,886.4 | 13.1x | 12.9 to 13.4 |
| open, footer only | 125.0 | 204.3 | 1.64x | 1.60 to 1.64 |
| read and write back | 5,642.8 | 18,501.2 | 3.26x | 3.22 to 3.46 |
| filtered scan, 1% band | 204.1 | 630.3 | 3.09x | 3.05 to 3.12 |
| filtered scan, half the rows | 353.1 | 1,104.1 | 3.12x | 3.11 to 3.18 |
| scattered take, 64 of 64 splits | 654.4 | 2,615.1 | 4.00x | 3.96 to 4.08 |
| key order, sorted column, 1% band | 202.0 | 320.2 | 1.58x | 1.56 to 1.58 |
| key order, uncorrelated, 64 rows | 339.9 | 471.8 | 1.44x | 1.37 to 1.51 |
| count, exact cover, 1% band | 281.1 | 366.9 | 1.38x | 1.33 to 1.47 |
| filtered scan, string equality, fsst | 388.0 | 678.4 | 1.76x | 1.73 to 1.82 |
| filtered scan, string prefix, fsst | 954.9 | 1,183.5 | 1.23x | 1.23 to 1.25 |
| filtered scan, string equality, dict | 211.2 | 332.8 | 1.60x | 1.57 to 1.62 |
| filtered scan, string prefix, dict | 221.3 | 422.5 | 1.92x | 1.86 to 1.94 |
| filtered scan, band, runend | 193.0 | 286.3 | 1.48x | 1.44 to 1.49 |
| filtered scan, band, bitpacked | 195.8 | 269.5 | 1.37x | 1.34 to 1.39 |
| full scan, 1M table | 22,736.6 | 45,329.2 | 1.98x | 1.91 to 2.00 |
| projected scan, 1 of 50 columns | 284.7 | 330.2 | 1.16x | 1.13 to 1.17 |

Vorticity took less time on 19 of 19 axes.
Rust is timed under the faster of its two splits, axis by axis: one split per chunk on 12 of 19, its default on full scan; open, footer only; filtered scan, half the rows; filtered scan, string equality, fsst; filtered scan, string prefix, dict; filtered scan, band, runend; filtered scan, band, bitpacked.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary 1464d38db0f3; commit e813ac54, 2026-10-12 08:15 UTC.*
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
| `alp` | 750 | 746 | 1.00x |
| `alp_no_patches` | 246 | 272 | 1.11x |
| `alp_patched_no_chunk_offsets` | 256 | 280 | 1.09x |
| `alprd` | 273 | 301 | 1.10x |
| `bool` | 123 | 192 | 1.50x |
| `bool_bit_offset3` | 129 | 193 | 1.51x |
| `bool_bit_offset7` | 127 | 194 | 1.50x |
| `bool_bit_offset_straddle` | 131 | 197 | 1.52x |
| `bytebool` | 150 | 338 | 2.20x |
| `chunked` | 251 | 285 | 1.15x |
| `chunked_bool` | 133 | 208 | 1.58x |
| `chunked_decimal` | 257 | 288 | 1.14x |
| `chunked_empty_chunks` | 259 | 296 | 1.14x |
| `chunked_mixed_validity` | 277 | 326 | 1.18x |
| `chunked_one_chunk` | 260 | 286 | 1.11x |
| `chunked_varbinview` | 389 | 13,048 | 33.3x |
| `constant` | 122 | 176 | 1.43x |
| `datetimeparts` | 406 | 447 | 1.11x |
| `decimal` | 249 | 274 | 1.10x |
| `decimal_byte_parts` | 249 | 274 | 1.09x |
| `dict` | 236 | 260 | 1.11x |
| `dict_nullable_codes` | 262 | 302 | 1.18x |
| `dict_nullable_values_nonnull_codes` | 177 | 238 | 1.35x |
| `dict_u64_codes` | 250 | 273 | 1.09x |
| `dict_u8_codes` | 157 | 239 | 1.51x |
| `ext` | 243 | 269 | 1.11x |
| `fastlanes_bitpacked` | 154 | 225 | 1.46x |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 162 | 233 | 1.46x |
| `fastlanes_delta` | 423 | 2,583 | 6.03x |
| `fastlanes_for` | 249 | 279 | 1.12x |
| `fastlanes_rle` | 915 | 970 | 1.08x |
| `fixed_size_list` | 262 | 298 | 1.14x |
| `fsst` | 404 | 433 | 1.08x |
| `list` | 308 | 1,191 | 3.94x |
| `listview` | 4,586 | 5,411 | 1.18x |
| `map` | 8,563 | 15,536 | 1.79x |
| `masked` | 201 | 227 | 1.13x |
| `masked_all_invalid` | 201 | 226 | 1.13x |
| `masked_all_valid` | 263 | 288 | 1.10x |
| `null` | 129 | 176 | 1.39x |
| `onpair` | 401 | 427 | 1.07x |
| `parquet_variant` | 457 | 474 | 1.05x |
| `pco` | 222 | 1,202 | 5.45x |
| `pco_f32` | 428 | 3,028 | 7.07x |
| `pco_i16` | 448 | 2,624 | 5.90x |
| `pco_nullable` | 226 | 249 | 1.10x |
| `pco_u32` | 542 | 4,194 | 7.85x |
| `primitive` | 238 | 269 | 1.11x |
| `runend` | 135 | 206 | 1.52x |
| `sequence` | 124 | 180 | 1.44x |
| `sparse` | 138 | 208 | 1.52x |
| `struct` | 4,147 | 10,417 | 2.53x |
| `table_mixed` | 18,546 | 31,883 | 1.73x |
| `table_wide` | 370 | 482 | 1.29x |
| `varbin` | 357 | 11,800 | 32.8x |
| `varbinview` | 6,211 | 12,742 | 2.03x |
| `variant` | 123 | 179 | 1.46x |
| `zigzag` | 229 | 256 | 1.13x |
| `zstd` | 558 | 10,007 | 17.8x |
| `zstd_buffers` | 528 | 486 | 0.92x |
| `zstd_nullable` | 346 | 3,748 | 10.7x |

Vorticity took less time on 59 of 61 files; the median speedup is 1.29x. At 1.00x or below: `zstd_buffers` 0.92x, `alp` 1.00x.
Rust is timed under the faster of its two splits, file by file: one split per chunk on 61 of 61 files, its default on none.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary 317c6a6d1f2d; commit 9eea1796, 2026-10-12 07:44 UTC.*
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
| `alp` | 5,873 | 32,740 | 5.47x | 2,740,956 | 2,976,924 |
| `alp_no_patches` | 4,716 | 22,961 | 4.82x | 1,955,788 | 1,956,060 |
| `alp_patched_no_chunk_offsets` | 4,473 | 23,546 | 5.27x | 2,155,956 | 2,141,772 |
| `alprd` | 5,418 | 22,686 | 4.13x | 6,508,892 | 6,509,316 |
| `bool` | 240 | 829 | 3.43x | 127,108 | 127,108 |
| `bool_bit_offset3` | 287 | 994 | 3.47x | 127,108 | 127,108 |
| `bool_bit_offset7` | 283 | 988 | 3.49x | 127,108 | 127,108 |
| `bool_bit_offset_straddle` | 290 | 992 | 3.42x | 127,108 | 127,108 |
| `bytebool` | 258 | 978 | 3.83x | 127,108 | 127,108 |
| `chunked` | 2,034 | 13,846 | 6.78x | 890,596 | 890,736 |
| `chunked_bool` | 242 | 864 | 3.55x | 127,236 | 127,248 |
| `chunked_decimal` | 3,837 | 16,422 | 4.23x | 1,822,988 | 1,825,056 |
| `chunked_empty_chunks` | 1,676 | 13,053 | 7.69x | 317,044 | 317,080 |
| `chunked_mixed_validity` | 2,493 | 17,060 | 6.85x | 1,431,604 | 1,374,016 |
| `chunked_one_chunk` | 1,469 | 12,714 | 8.60x | 5,604 | 4,640 |
| `chunked_varbinview` | 20,223 | 36,196 | 1.77x | 643,644 | 628,628 |
| `constant` | 182 | 3,416 | 18.7x | 4,212 | 3,300 |
| `datetimeparts` | 4,261 | 28,829 | 6.87x | 3,382,604 | 3,383,628 |
| `decimal` | 2,143 | 14,806 | 6.97x | 4,324 | 5,116 |
| `decimal_byte_parts` | 2,197 | 15,592 | 7.03x | 4,324 | 5,116 |
| `dict` | 7,831 | 19,721 | 2.50x | 383,892 | 378,288 |
| `dict_nullable_codes` | 13,327 | 17,689 | 1.35x | 384,628 | 378,772 |
| `dict_nullable_values_nonnull_codes` | 12,146 | 17,749 | 1.47x | 384,628 | 378,764 |
| `dict_u64_codes` | 8,372 | 20,826 | 2.48x | 383,892 | 378,288 |
| `dict_u8_codes` | 10,708 | 22,047 | 2.06x | 38,740 | 1,004,160 |
| `ext` | 1,585 | 23,727 | 14.8x | 6,380 | 4,988 |
| `fastlanes_bitpacked` | 1,188 | 4,441 | 3.78x | 1,254,340 | 1,253,668 |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,173 | 5,169 | 4.36x | 1,270,452 | 1,272,740 |
| `fastlanes_delta` | 2,145 | 14,239 | 6.65x | 5,484 | 4,532 |
| `fastlanes_for` | 2,368 | 7,375 | 3.08x | 881,364 | 879,732 |
| `fastlanes_rle` | 1,373 | 8,771 | 6.24x | 4,548 | 3,812 |
| `fixed_size_list` | 2,029 | 27,158 | 13.2x | 5,604 | 4,724 |
| `fsst` | 47,351 | 297,183 | 6.28x | 933,668 | 8,574,500 |
| `list` | 6,463 | 24,336 | 3.80x | 4,782,492 | 5,122,712 |
| `listview` | 8,729 | 38,577 | 4.38x | 4,745,468 | 4,502,136 |
| `map` | 18,306 | 75,483 | 4.11x | 4,426,852 | 4,811,136 |
| `masked` | 2,274 | 19,112 | 8.52x | 2,380,060 | 2,381,156 |
| `masked_all_invalid` | 719 | 2,210 | 3.06x | 2,636 | 2,700 |
| `masked_all_valid` | 887 | 10,703 | 11.9x | 3,764 | 3,324 |
| `null` | 151 | 595 | 3.91x | 2,092 | 2,164 |
| `onpair` | 26,120 | 33,300 | 1.27x | 60,588 | 1,005,840 |
| `pco` | 1,092 | 12,530 | 11.9x | 5,476 | 4,508 |
| `pco_f32` | 9,664 | 20,899 | 2.17x | 2,130,020 | 2,130,108 |
| `pco_i16` | 7,272 | 8,499 | 1.16x | 1,878,788 | 1,878,460 |
| `pco_nullable` | 14,901 | 34,950 | 2.34x | 2,876,548 | 2,874,276 |
| `pco_u32` | 18,403 | 9,604 | 0.50x | 1,931,748 | 2,879,908 |
| `primitive` | 1,478 | 12,356 | 8.26x | 5,988 | 4,572 |
| `runend` | 752 | 9,493 | 12.6x | 4,620 | 3,500 |
| `sequence` | 894 | 11,063 | 12.3x | 5,484 | 4,516 |
| `sparse` | 774 | 4,250 | 5.43x | 131,172 | 3,884 |
| `struct` | 41,523 | 110,902 | 2.64x | 639,580 | 5,274,480 |
| `table_mixed` | 73,266 | 390,394 | 5.30x | 7,875,620 | 14,856,384 |
| `table_wide` | 6,451 | 35,780 | 5.50x | 5,163,244 | 5,168,320 |
| `varbin` | 49,696 | 253,809 | 5.11x | 637,012 | 5,066,652 |
| `varbinview` | 19,845 | 36,158 | 1.80x | 643,516 | 628,504 |
| `variant` | 196 | 1,043 | 5.38x | 2,220 | 52,520 |
| `zigzag` | 1,816 | 11,868 | 6.65x | 2,531,652 | 2,380,300 |
| `zstd` | 30,925 | 40,141 | 1.30x | 786,948 | 754,336 |
| `zstd_buffers` | 1,885 | 4,802 | 2.53x | 630,732 | 629,028 |
| `zstd_nullable` | 3,457 | 14,407 | 4.16x | 756,500 | 628,740 |

Vorticity took less time on 59 of 60 files; the median speedup is 4.30x. At 1.00x or below: `pco_u32` 0.50x.
Rust is timed under the faster of its two splits, file by file: one split per chunk on 47 of 60 files, its default on `alp`, `alp_patched_no_chunk_offsets`, `chunked`, `chunked_empty_chunks`, `chunked_one_chunk`, `datetimeparts`, `decimal_byte_parts`, `dict_u8_codes`, `fsst`, `list`, `runend`, `sparse`, `zigzag`.
Vorticity's bytes were within 5 % of Rust's or fewer on 45 of 60 files; more than 5 % above them on `sparse` (33.8 times Rust's), `runend` (1.32 times), `primitive` (1.31 times), `ext` (1.28 times), `constant` (1.28 times), `pco` (1.21 times), `sequence` (1.21 times), `fastlanes_delta` (1.21 times), `chunked_one_chunk` (1.21 times), `zstd_nullable` (1.20 times), `fastlanes_rle` (1.19 times), `fixed_size_list` (1.19 times), `masked_all_valid` (1.13 times), `zigzag` (1.06 times), `listview` (1.05 times).
Rust's writer declines `parquet_variant`, which is left out.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary 7527e2b5d861; commit 0c1aa9bf, 2026-10-11 22:25 UTC.*
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
| a sequence | Sequence x77 | 0.00 | 9 | 1.7 | 0.26 |  |
| sorted runs of 1 000 | RunEnd x77 | 0.01 | 10 | 1.9 | 0.41 | Smallest: RunEnd x76, Pco x1, 0.01 B/value, scan 2.2 ms, take 0.75 ms; reads faster below 101 MB/s |
| timestamps (ms, increasing, jittered) | BitPacked x77 | 3.38 | 31 | 5.8 | 2.35 | Smallest: Pco x77, 1.35 B/value, scan 103.6 ms, take 102.97 ms; reads faster below 208 MB/s |
| random in 0..999 | BitPacked x77 | 1.25 | 22 | 3.4 | 1.05 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 24 | 2.7 | 0.72 |  |
| 100 003 distinct, repeating | BitPacked x77 | 4.63 | 37 | 5.6 | 1.42 | Smallest: Pco x77, 0.06 B/value, scan 69.1 ms, take 68.97 ms; reads faster below 720 MB/s |
| uniform 64-bit | Canonical x77 | 8.00 | 44 | 6.8 | 1.52 |  |
| random in 0..999, 10 % null | BitPacked x82 | 1.38 | 27 | 4.1 | 1.25 | Smallest: Pco x82, 1.26 B/value, scan 43.4 ms, take 42.61 ms; reads faster below 31 MB/s |

<details><summary>a sequence: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Sequence x77 | 44,380 | 0.00 | 9 | 1.7 | 0.26 |  |
| Fastest | Sequence x77 | 44,380 | 0.00 | 9 | 1.7 | 0.25 | same |
| Smallest | Sequence x77 | 44,380 | 0.00 | 9 | 1.7 | 0.26 | same |
| None | Canonical x77 | 80,043,724 | 8.00 | 44 | 7.5 | 1.66 | reads slower at any throughput |
| hint Dictionary | Sequence x77 | 44,380 | 0.00 | 109 | 1.8 | 0.26 | same |
| hint BitPacked | Sequence x77 | 44,380 | 0.00 | 9 | 1.7 | 0.25 | same |
| hint RunEnd | Sequence x77 | 44,380 | 0.00 | 8 | 1.7 | 0.25 | same |
| hint Zstd | Sequence x77 | 44,380 | 0.00 | 8 | 1.7 | 0.25 | same |

</details>

<details><summary>sorted runs of 1 000: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x77 | 88,004 | 0.01 | 10 | 1.9 | 0.41 |  |
| Fastest | RunEnd x77 | 88,004 | 0.01 | 11 | 1.9 | 0.41 | same |
| Smallest | RunEnd x76, Pco x1 | 53,748 | 0.01 | 92 | 2.2 | 0.75 | reads faster below 101 MB/s |
| None | Canonical x77 | 80,043,716 | 8.00 | 44 | 7.0 | 1.66 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 1,449,892 | 0.14 | 17 | 4.2 | 0.92 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 10,034,460 | 1.00 | 20 | 3.8 | 0.95 | reads slower at any throughput |
| hint RunEnd | RunEnd x77 | 88,004 | 0.01 | 11 | 1.9 | 0.37 | same |
| hint Zstd | Zstd x77 | 136,628 | 0.01 | 12 | 2.6 | 1.14 | reads slower at any throughput |

</details>

<details><summary>timestamps (ms, increasing, jittered): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 33,795,172 | 3.38 | 31 | 5.8 | 2.35 |  |
| Fastest | BitPacked x77 | 33,795,172 | 3.38 | 31 | 6.0 | 2.43 | same |
| Smallest | Pco x77 | 13,472,444 | 1.35 | 421 | 103.6 | 102.97 | reads faster below 208 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 44 | 8.0 | 1.81 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 33,795,172 | 3.38 | 138 | 5.6 | 2.15 | same |
| hint BitPacked | BitPacked x77 | 33,795,172 | 3.38 | 31 | 5.8 | 2.25 | same |
| hint RunEnd | BitPacked x77 | 33,795,172 | 3.38 | 43 | 5.5 | 2.13 | same |
| hint Zstd | Zstd x77 | 20,700,796 | 2.07 | 159 | 76.1 | 33.37 | reads faster below 186 MB/s |

</details>

<details><summary>random in 0..999: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 12,544,284 | 1.25 | 22 | 3.4 | 1.05 |  |
| Fastest | BitPacked x77 | 12,544,284 | 1.25 | 23 | 3.5 | 1.29 | same |
| Smallest | BitPacked x76, Pco x1 | 12,543,924 | 1.25 | 297 | 3.4 | 1.19 | same |
| None | Canonical x77 | 80,043,716 | 8.00 | 42 | 7.2 | 1.62 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,652,724 | 1.27 | 53 | 4.9 | 1.27 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 12,544,284 | 1.25 | 23 | 3.3 | 1.02 | same |
| hint RunEnd | BitPacked x77 | 12,544,284 | 1.25 | 22 | 3.3 | 1.14 | same |
| hint Zstd | Zstd x77 | 20,141,172 | 2.01 | 181 | 60.5 | 29.44 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,748 | 0.51 | 24 | 2.7 | 0.72 |  |
| Fastest | Dictionary x77 | 5,063,748 | 0.51 | 24 | 2.6 | 0.68 | same |
| Smallest | Dictionary x76, Pco x1 | 5,063,588 | 0.51 | 503 | 2.9 | 0.98 | reads slower at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 43 | 6.8 | 1.54 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,063,748 | 0.51 | 24 | 2.6 | 0.70 | same |
| hint BitPacked | Dictionary x77 | 5,063,748 | 0.51 | 113 | 2.7 | 0.74 | same |
| hint RunEnd | Dictionary x77 | 5,063,748 | 0.51 | 105 | 2.6 | 0.67 | same |
| hint Zstd | Zstd x77 | 10,539,396 | 1.05 | 135 | 45.3 | 20.54 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,932 | 0.50 | 43 | 2.4 | 0.53 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 46,295,652 | 4.63 | 37 | 5.6 | 1.42 |  |
| Fastest | BitPacked x77 | 46,295,652 | 4.63 | 39 | 6.2 | 1.60 | reads slower at any throughput |
| Smallest | Pco x77 | 580,924 | 0.06 | 399 | 69.1 | 68.97 | reads faster below 720 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 44 | 6.5 | 1.46 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 46,295,652 | 4.63 | 124 | 6.1 | 1.49 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 46,295,652 | 4.63 | 37 | 6.8 | 1.60 | reads slower at any throughput |
| hint RunEnd | BitPacked x77 | 46,295,652 | 4.63 | 68 | 6.0 | 1.52 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 51,815,868 | 5.18 | 129 | 50.4 | 40.96 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 23,603,548 | 2.36 | 108 | 6.7 | 2.42 | reads faster below 20,504 MB/s |

</details>

<details><summary>uniform 64-bit: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x77 | 80,043,732 | 8.00 | 44 | 6.8 | 1.52 |  |
| Fastest | Canonical x77 | 80,043,732 | 8.00 | 43 | 7.1 | 1.55 | same |
| Smallest | Canonical x77 | 80,043,732 | 8.00 | 325 | 6.8 | 1.57 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 42 | 7.7 | 1.78 | reads slower at any throughput |
| hint Dictionary | Canonical x77 | 80,043,732 | 8.00 | 197 | 7.1 | 1.61 | same |
| hint BitPacked | Canonical x77 | 80,043,732 | 8.00 | 116 | 7.0 | 1.54 | same |
| hint RunEnd | Canonical x77 | 80,043,732 | 8.00 | 110 | 6.8 | 1.48 | same |
| hint Zstd | Canonical x77 | 80,043,732 | 8.00 | 120 | 8.3 | 1.65 | reads slower at any throughput |

</details>

<details><summary>random in 0..999, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x82 | 13,800,460 | 1.38 | 27 | 4.1 | 1.25 |  |
| Fastest | BitPacked x82 | 13,800,460 | 1.38 | 36 | 4.5 | 1.60 | reads slower at any throughput |
| Smallest | Pco x82 | 12,567,268 | 1.26 | 302 | 43.4 | 42.61 | reads faster below 31 MB/s |
| None | Canonical x82 | 81,294,668 | 8.13 | 45 | 7.0 | 1.73 | reads slower at any throughput |
| hint Dictionary | Dictionary x82 | 12,676,396 | 1.27 | 72 | 7.9 | 1.45 | reads faster below 297 MB/s |
| hint BitPacked | BitPacked x82 | 13,800,460 | 1.38 | 27 | 4.0 | 1.19 | same |
| hint RunEnd | BitPacked x82 | 13,800,460 | 1.38 | 30 | 4.0 | 1.19 | same |
| hint Zstd | Zstd x82 | 19,283,940 | 1.93 | 168 | 57.5 | 26.89 | reads slower at any throughput |

</details>

### Floating point

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| prices (2 decimals) | Alp x77 | 2.50 | 43 | 6.2 | 2.15 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 26 | 2.6 | 0.67 |  |
| 1 000 distinct prices | Dictionary x77 | 1.28 | 58 | 4.9 | 1.34 |  |
| 100 003 distinct, repeating | Zstd x77 | 1.69 | 153 | 45.7 | 21.03 | Auto, 16 MiB chunks: Dictionary x5, 2.21 B/value, scan 8.3 ms, take 3.98 ms; reads faster above 139 MB/s; None: Canonical x77, 8.00 B/value, scan 6.7 ms, take 1.68 ms; reads faster above 1,619 MB/s |
| uniform in [0, 1) | AlpRd x77 | 6.91 | 55 | 10.4 | 2.67 | None: Canonical x77, 8.00 B/value, scan 6.9 ms, take 1.51 ms; reads faster above 3,121 MB/s |

<details><summary>prices (2 decimals): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Alp x77 | 25,049,732 | 2.50 | 43 | 6.2 | 2.15 |  |
| Fastest | Alp x77 | 25,049,732 | 2.50 | 44 | 6.4 | 2.38 | same |
| Smallest | Alp x77 | 25,048,868 | 2.50 | 820 | 6.0 | 2.34 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 45 | 7.0 | 1.68 | reads slower at any throughput |
| hint Dictionary | Alp x77 | 25,049,732 | 2.50 | 171 | 6.1 | 2.35 | same |
| hint Alp | Alp x77 | 25,049,732 | 2.50 | 43 | 5.8 | 1.95 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 41,240,260 | 4.12 | 283 | 57.6 | 36.48 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,748 | 0.51 | 26 | 2.6 | 0.67 |  |
| Fastest | Dictionary x77 | 5,063,748 | 0.51 | 26 | 2.6 | 0.67 | same |
| Smallest | Dictionary x76, Pco x1 | 5,063,588 | 0.51 | 500 | 3.0 | 1.11 | reads slower at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 45 | 7.8 | 1.71 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,063,748 | 0.51 | 26 | 2.7 | 0.71 | same |
| hint Alp | Dictionary x77 | 5,063,748 | 0.51 | 113 | 2.6 | 0.68 | same |
| hint Zstd | Zstd x77 | 10,545,668 | 1.05 | 142 | 45.7 | 20.86 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,932 | 0.50 | 44 | 2.4 | 0.56 | reads faster at any throughput |

</details>

<details><summary>1 000 distinct prices: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 12,756,260 | 1.28 | 58 | 4.9 | 1.34 |  |
| Fastest | Dictionary x77 | 12,756,260 | 1.28 | 62 | 4.9 | 1.22 | same |
| Smallest | Dictionary x77 | 12,751,356 | 1.28 | 449 | 5.1 | 1.56 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 45 | 6.8 | 1.60 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,756,260 | 1.28 | 59 | 4.8 | 1.24 | same |
| hint Alp | Alp x77 | 12,756,260 | 1.28 | 118 | 6.0 | 1.27 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 26,073,412 | 2.61 | 211 | 48.4 | 24.60 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 12,545,732 | 1.25 | 73 | 4.5 | 1.03 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x77 | 16,909,892 | 1.69 | 153 | 45.7 | 21.03 |  |
| Fastest | Zstd x77 | 16,909,892 | 1.69 | 155 | 45.6 | 21.08 | same |
| Smallest | Zstd x77 | 16,909,892 | 1.69 | 296 | 45.6 | 21.51 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 47 | 6.7 | 1.68 | reads faster above 1,619 MB/s |
| hint Dictionary | Zstd x77 | 16,909,892 | 1.69 | 277 | 45.7 | 21.37 | same |
| hint Alp | Zstd x77 | 16,909,892 | 1.69 | 252 | 45.5 | 20.84 | same |
| hint Zstd | Zstd x77 | 16,909,892 | 1.69 | 155 | 45.7 | 20.95 | same |
| Auto, 16 MiB chunks | Dictionary x5 | 22,129,988 | 2.21 | 117 | 8.3 | 3.98 | reads faster above 139 MB/s |

</details>

<details><summary>uniform in [0, 1): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | AlpRd x77 | 69,054,380 | 6.91 | 55 | 10.4 | 2.67 |  |
| Fastest | AlpRd x77 | 69,054,380 | 6.91 | 58 | 10.0 | 2.55 | same |
| Smallest | Pco x77 | 67,609,668 | 6.76 | 514 | 105.7 | 115.91 | reads faster below 15 MB/s |
| None | Canonical x77 | 80,043,732 | 8.00 | 45 | 6.9 | 1.51 | reads faster above 3,121 MB/s |
| hint Dictionary | AlpRd x77 | 69,054,380 | 6.91 | 250 | 10.2 | 2.57 | same |
| hint Alp | AlpRd x77 | 69,054,380 | 6.91 | 220 | 10.5 | 2.84 | same |
| hint Zstd | AlpRd x77 | 69,054,380 | 6.91 | 267 | 9.6 | 2.41 | reads faster at any throughput |

</details>

### Text

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| 16 cities, random order | Dictionary x175 | 0.51 | 116 | 5.8 | 1.22 |  |
| 10 000 distinct ids | Dictionary x306 | 2.54 | 496 | 33.6 | 27.09 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 9.6 ms, take 3.04 ms; reads faster at any throughput |
| 10 000 distinct ids, 10 % null | Dictionary x306 | 2.45 | 529 | 36.6 | 27.84 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 10.8 ms, take 3.02 ms; reads faster at any throughput |
| unique UUIDs | Zstd x611 | 20.61 | 1559 | 265.6 | 300.97 | hint Fsst: Fsst x611, 22.44 B/value, scan 139.0 ms, take 12.69 ms; reads faster above 145 MB/s |
| log lines (~100 B) | Zstd x1221 | 13.71 | 1389 | 355.7 | 289.31 | hint Fsst: Fsst x1221, 22.01 B/value, scan 141.7 ms, take 20.16 ms; reads faster above 388 MB/s |

<details><summary>16 cities, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x175 | 5,117,284 | 0.51 | 116 | 5.8 | 1.22 |  |
| Fastest | Dictionary x175 | 5,117,284 | 0.51 | 116 | 5.8 | 1.14 | same |
| Smallest | Dictionary x175 | 5,117,284 | 0.51 | 303 | 5.9 | 1.24 | same |
| None | Canonical x175 | 96,746,756 | 9.67 | 164 | 30.7 | 19.50 | reads slower at any throughput |
| hint Dictionary | Dictionary x175 | 5,117,284 | 0.51 | 115 | 5.9 | 1.35 | same |
| hint Fsst | Fsst x175 | 41,142,948 | 4.11 | 278 | 30.4 | 4.91 | reads slower at any throughput |
| hint Zstd | Zstd x175 | 17,427,100 | 1.74 | 247 | 81.8 | 54.53 | reads slower at any throughput |

</details>

<details><summary>10 000 distinct ids: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 25,433,788 | 2.54 | 496 | 33.6 | 27.09 |  |
| Fastest | Dictionary x306 | 25,433,788 | 2.54 | 483 | 33.8 | 27.41 | same |
| Smallest | Dictionary x305, Zstd x1 | 23,452,500 | 2.35 | 1306 | 134.8 | 129.16 | reads faster below 20 MB/s |
| None | Canonical x306 | 140,098,172 | 14.01 | 201 | 26.7 | 38.29 | reads faster above 16,661 MB/s |
| hint Dictionary | Dictionary x306 | 25,433,788 | 2.54 | 480 | 32.9 | 27.53 | same |
| hint Fsst | Fsst x306 | 62,863,260 | 6.29 | 405 | 38.8 | 7.43 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 27,097,628 | 2.71 | 363 | 79.5 | 58.96 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 18,014,012 | 1.80 | 376 | 9.6 | 3.04 | reads faster at any throughput |

</details>

<details><summary>10 000 distinct ids, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 24,539,284 | 2.45 | 529 | 36.6 | 27.84 |  |
| Fastest | Dictionary x306 | 24,539,284 | 2.45 | 528 | 36.5 | 27.36 | same |
| Smallest | Dictionary x305, Zstd x1 | 22,854,628 | 2.29 | 1341 | 139.4 | 131.52 | reads faster below 16 MB/s |
| None | Canonical x306 | 151,883,732 | 15.19 | 291 | 39.8 | 35.92 | reads slower at any throughput |
| hint Dictionary | Dictionary x306 | 24,539,284 | 2.45 | 531 | 36.5 | 27.77 | same |
| hint Fsst | Fsst x306 | 64,408,284 | 6.44 | 481 | 46.8 | 8.11 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 25,595,636 | 2.56 | 404 | 92.7 | 53.11 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 17,982,604 | 1.80 | 457 | 10.8 | 3.02 | reads faster at any throughput |

</details>

<details><summary>unique UUIDs: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x611 | 206,058,588 | 20.61 | 1559 | 265.6 | 300.97 |  |
| Fastest | Zstd x611 | 206,058,588 | 20.61 | 1564 | 266.7 | 300.20 | same |
| Smallest | Zstd x611 | 206,058,588 | 20.61 | 3013 | 262.5 | 298.41 | same |
| None | Canonical x611 | 360,155,516 | 36.02 | 241 | 49.9 | 96.15 | reads faster above 714 MB/s |
| hint Dictionary | Zstd x610, Fsst x1 | 206,059,580 | 20.61 | 5737 | 266.4 | 303.06 | same |
| hint Fsst | Fsst x611 | 224,384,604 | 22.44 | 1418 | 139.0 | 12.69 | reads faster above 145 MB/s |
| hint Zstd | Zstd x611 | 206,058,588 | 20.61 | 1529 | 264.0 | 298.80 | same |

</details>

<details><summary>log lines (~100 B): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x1221 | 137,107,188 | 13.71 | 1389 | 355.7 | 289.31 |  |
| Fastest | Zstd x1221 | 137,107,188 | 13.71 | 1392 | 356.5 | 289.98 | same |
| Smallest | Zstd x1221 | 137,107,188 | 13.71 | 1702 | 359.0 | 290.72 | same |
| None | Canonical x1221 | 725,468,444 | 72.55 | 668 | 91.4 | 157.61 | reads faster above 2,226 MB/s |
| hint Dictionary | Zstd x1221 | 137,107,188 | 13.71 | 4519 | 352.9 | 288.14 | same |
| hint Fsst | Fsst x1221 | 220,058,684 | 22.01 | 1613 | 141.7 | 20.16 | reads faster above 388 MB/s |
| hint Zstd | Zstd x1221 | 137,107,188 | 13.71 | 1365 | 352.4 | 288.41 | same |

</details>

### Booleans

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| half true | Canonical x10 | 0.13 | 4 | 0.2 | 0.28 |  |
| 1 % true | RunEnd x10 | 0.05 | 6 | 2.4 | 0.38 | None: Canonical x10, 0.13 B/value, scan 0.2 ms, take 0.27 ms; reads faster above 338 MB/s |

<details><summary>half true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.2 | 0.28 |  |
| Fastest | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.3 | 0.26 | same |
| Smallest | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.3 | 0.37 | reads slower at any throughput |
| None | Canonical x10 | 1,262,556 | 0.13 | 3 | 0.2 | 0.26 | same |
| hint RunEnd | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.2 | 0.26 | same |

</details>

<details><summary>1 % true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x10 | 548,092 | 0.05 | 6 | 2.4 | 0.38 |  |
| Fastest | RunEnd x10 | 548,092 | 0.05 | 6 | 2.4 | 0.37 | same |
| Smallest | RunEnd x10 | 164,692 | 0.02 | 15 | 4.0 | 2.01 | reads faster below 234 MB/s |
| None | Canonical x10 | 1,262,556 | 0.13 | 4 | 0.2 | 0.27 | reads faster above 338 MB/s |
| hint RunEnd | RunEnd x10 | 548,092 | 0.05 | 5 | 2.4 | 0.38 | same |

</details>

### The profiles side by side

`Fastest` wrote what `Auto` wrote on 20 of 20 columns. `Smallest` wrote something else on *i64, sorted runs of 1 000*, *i64, timestamps (ms, increasing, jittered)*, *i64, random in 0..999*, *i64, 16 distinct, random order*, *i64, 100 003 distinct, repeating*, *i64, random in 0..999, 10 % null*, *f64, 16 distinct, random order*, *f64, uniform in [0, 1)*, *utf8, 10 000 distinct ids*, *utf8, 10 000 distinct ids, 10 % null*, and took up to 21.1 times `Auto`'s write, 503 ms against 24 ms on *i64, 16 distinct, random order*, since it tries every scheme on every chunk.
`None`, the plain form, made the largest file on 20 of 20 columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit 0c1aa9bf, 2026-10-11 22:31 UTC.*
<!-- /results: tradeoffs -->

<!-- results: advice -->
## What the advice picks

`VortexSession.AdviseAsync` on the same columns of 10,000,000 rows, under five goals: whole scans with the
bytes read at 2 GB/s, 100 MB/s and 10 GB/s, a row in a thousand read by row, and the bytes alone.
A cell is what the advice takes where it departs from the writer's own choice, empty where it
does not; a column it never departs on is left out.

| column | scans at 2 GB/s | scans at 100 MB/s | scans at 10 GB/s | a row in 1 000 read by row | size |
|---|---|---|---|---|---|
| i64, timestamps (ms, increasing, jittered) |  | `Pco` |  | `Zstd` |  |
| i64, 100 003 distinct, repeating | 16 MiB chunks | `Pco` |  |  | `Dictionary`, 16 MiB chunks |
| i64, random in 0..999, 10 % null |  | `Dictionary` |  | `Dictionary` | `Dictionary` |
| f64, 100 003 distinct, repeating | 16 MiB chunks |  | `Canonical` |  | `Dictionary`, 16 MiB chunks |
| f64, uniform in [0, 1) |  |  | `Canonical` |  |  |
| utf8, 10 000 distinct ids | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `OnPair` | 16 MiB chunks |
| utf8, 10 000 distinct ids, 10 % null | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `Zstd` | 16 MiB chunks |
| utf8, unique UUIDs | `Canonical` |  | `Canonical` | `Fsst` |  |
| utf8, log lines (~100 B) | `OnPair` |  | `Canonical` | `OnPair` |  |
| bool, 1 % true | `Canonical` |  | `Canonical` |  |  |

It keeps the writer's choice under every goal on the 10 other columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit 0c1aa9bf, 2026-10-11 22:33 UTC.*
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
| &#39;i64 scalar&#39; | 17       |  95.401 μs | 1.1425 μs | 0.2967 μs |   1.00x | Baseline        |   1.46 |  5.50 |         - |          NA |
| &#39;i64 vector&#39; | 17       |   8.764 μs | 0.1103 μs | 0.0171 μs |   10.9x | Faster          |   0.13 | 59.83 |         - |          NA |
| &#39;i32 scalar&#39; | 17       | 108.126 μs | 3.2475 μs | 0.8434 μs |   0.88x | Same            |   1.65 |  2.42 |         - |          NA |
| &#39;i64 pack&#39;   | 17       |  21.319 μs | 0.1756 μs | 0.0272 μs |   4.47x | Faster          |   0.33 | 24.59 |         - |          NA |
| &#39;i32 pack&#39;   | 17       |  12.092 μs | 0.3950 μs | 0.0611 μs |   7.89x | Faster          |   0.18 | 21.68 |         - |          NA |
| &#39;i32 vector&#39; | 17       |   4.614 μs | 0.0663 μs | 0.0103 μs |   20.7x | Faster          |   0.07 | 56.82 |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit 0c1aa9bf, 2026-10-11 22:42 UTC.*
<!-- /results: kernel:FastLanesKernelBenchmarks -->

<!-- results: kernel:RowEncodingBenchmarks -->
### `RowEncodingBenchmarks`

| Method                                   | Entry                | Mean       | Error      | StdDev     | ns/row | GB/s | Allocated |
|----------------------------------------- |--------------------- |-----------:|-----------:|-----------:|-------:|-----:|----------:|
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nical [33]** |  **57.099 μs** |  **1.1828 μs** |  **0.3072 μs** |  **13.94** | **3.08** |      **48 B** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nical [33] |  57.963 μs |  0.9317 μs |  0.1442 μs |  14.15 | 3.04 |      48 B |
| &#39;encode, then sort rows by key&#39;          | conta(...)nical [33] | 405.035 μs | 51.5773 μs | 13.3945 μs |  98.89 | 0.43 |      48 B |
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nulls [33]** |  **17.744 μs** |  **0.4188 μs** |  **0.1088 μs** |  **17.33** | **3.81** |      **48 B** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nulls [33] |  18.734 μs |  0.2592 μs |  0.0401 μs |  18.30 | 3.61 |      48 B |
| &#39;encode, then sort rows by key&#39;          | conta(...)nulls [33] |  56.571 μs |  0.6562 μs |  0.1704 μs |  55.24 | 1.19 |      48 B |
| **&#39;encode a batch to row keys&#39;**             | **encod(...)r1025 [31]** |   **9.849 μs** |  **0.0406 μs** |  **0.0063 μs** |   **9.61** | **0.94** |      **48 B** |
| &#39;encode a batch to row keys, descending&#39; | encod(...)r1025 [31] |  10.058 μs |  0.2516 μs |  0.0389 μs |   9.81 | 0.92 |      48 B |
| &#39;encode, then sort rows by key&#39;          | encod(...)r1025 [31] |  52.268 μs |  0.8557 μs |  0.2222 μs |  50.99 | 0.18 |      48 B |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit 0c1aa9bf, 2026-10-11 22:42 UTC.*
<!-- /results: kernel:RowEncodingBenchmarks -->

<!-- results: kernel:CompressorBenchmarks -->
### `CompressorBenchmarks`

| Method                       | Column | Mean          | Error       | StdDev      | Speedup | MannWhitney(5%) | Allocated | Alloc Ratio |
|----------------------------- |------- |--------------:|------------:|------------:|--------:|---------------- |----------:|------------:|
| &#39;Choose, the whole decision&#39; | i64    | 51,766.157 ns | 696.9923 ns | 181.0067 ns |   1.00x | Baseline        |         - |          NA |
| &#39;candidate: sequence&#39;        | i64    |     10.560 ns |   0.0864 ns |   0.0134 ns |  4,902x | Faster          |         - |          NA |
| &#39;candidate: bit packing&#39;     | i64    |  5,081.511 ns | 117.6022 ns |  30.5409 ns |   10.2x | Faster          |         - |          NA |
| &#39;candidate: FSST&#39;            | i64    |      1.843 ns |   0.0086 ns |   0.0013 ns | 28,087x | Faster          |         - |          NA |
| &#39;candidate: zstd&#39;            | i64    |  8,060.220 ns |  74.4823 ns |  11.5262 ns |   6.42x | Faster          |         - |          NA |
| &#39;candidate: ALP&#39;             | i64    |      2.588 ns |   0.0326 ns |   0.0051 ns | 20,001x | Faster          |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit 0c1aa9bf, 2026-10-11 22:42 UTC.*
<!-- /results: kernel:CompressorBenchmarks -->

<!-- results: kernel:LanesBenchmarks -->
### `LanesBenchmarks`

| Method                 | Lanes | Mean       | Error    | StdDev   | Speedup | MannWhitney(5%) | Completed Work Items | Lock Contentions | Allocated | Alloc Ratio |
|----------------------- |------ |-----------:|---------:|---------:|--------:|---------------- |---------------------:|-----------------:|----------:|------------:|
| &#39;ours, n lanes&#39;        | 4     |   588.1 μs | 89.91 μs | 13.91 μs |   1.00x | Baseline        |              99.2102 |                - |   52597 B |        1.00 |
| &#39;reference, n workers&#39; | 4     | 1,374.6 μs | 72.64 μs | 18.86 μs |   0.43x | Slower          |                    - |                - |         - |        0.00 |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit 9eea1796, 2026-10-12 07:45 UTC.*
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
