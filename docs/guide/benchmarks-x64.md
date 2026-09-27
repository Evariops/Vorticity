# Benchmarks on x64

[The benchmark page](benchmarks.md) measured again on an x64 machine: an AMD Ryzen 9 7950X (Zen 4,
16 cores and 32 threads, AVX-512) under Windows 11, against the same Vortex Rust 0.86.1 built the
same way for that machine. The instruments, the files, the rows and the rules are that page's own,
section for section; what changes is the processor, the operating system and the file system under
the page cache. [05-benchmarks.md](../design/05-benchmarks.md) says what is compared and how each
instrument measures; [bench/README.md](../../bench/README.md) how to run them.

Vortex™ is a trademark of LF Projects, LLC. Vorticity is an independent implementation,
not affiliated with or endorsed by the Vortex project or LF Projects, LLC.

* **The two sides.** Vorticity as a Native AOT binary built for the machine's instruction set,
  with the workstation garbage collector, where a section says so, and on the JIT otherwise. Vortex
  Rust 0.86.1 built as upstream builds its own benchmarks (mimalloc, `-C target-cpu=native`, one
  codegen unit, no LTO), through [tools/vxbench-rs](../../tools/vxbench-rs): `target-cpu=native`
  gives its compiler this machine's instruction set, AVX-512 included, as .NET's JIT has it. Both
  decode every value they return to its plain form, and must return the same rows or the run fails.
* **Ratio** is Vorticity's time over Rust's: under 1.00x, Vorticity took less.
* **Throughput** is the plain size of the rows returned over the time, as on [the benchmark
  page](benchmarks.md).
* **All cores** are the 32 hardware threads, simultaneous multithreading included, where the arm64
  page's are 14 cores of two kinds.
* **The page cache is warm** for every run, on both sides, and **a file opened again keeps its
  mapping** on Vorticity's side, as on [the benchmark page](benchmarks.md).

Each section is regenerated on an x64 machine by `dotnet run -c Release --project
bench/Vorticity.Benchmarks --` and the arguments below, which rewrite that section of this page and
leave the others. The Native AOT runner that `--report` times is published first (`dotnet publish -c
Release bench/Vorticity.Benchmarks.Runner`): an older one times older code.

| section | arguments |
|---|---|
| [Reading and writing a table](#reading-and-writing-a-table-process-against-process), [decoding, per encoding](#decoding-per-encoding) | `--report --markdown --out docs/guide/benchmarks-x64.md` |
| [In one process](#in-one-process-after-warm-up) | `--ratio-check --out docs/guide/benchmarks-x64.md` |
| [Taking rows](#taking-rows-per-encoding), [writing](#writing-per-encoding), per encoding | `--throughput --take --out docs/guide/benchmarks-x64.md`, and `--write` |
| [Encodings, column by column](#encodings-column-by-column), [what the advice picks](#what-the-advice-picks) | `--tradeoffs --out docs/guide/benchmarks-x64.md`, and `--advise`, under `DOTNET_TieredCompilation=0` |
| [Kernels](#kernels-against-what-they-replaced) | `--out docs/guide/benchmarks-x64.md`, or a class name before it |

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

| column | Vorticity's, 2,448,636 bytes | Vortex Rust's, 7,903,964 bytes |
|---|---|---|
| `monotone` | 16 chunks of `vortex.sequence` | 8 chunks of `vortex.sequence` |
| `value` | 16 chunks of `vortex.zstd` | 8 chunks of `vortex.alprd` |
| `label` | 16 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 2 chunks of `fastlanes.bitpacked` |
| `flag` | 16 chunks of `vortex.bool` | one chunk of `vortex.bool` |

#### One core

| scenario | file | Vorticity, ms | Vortex Rust, ms | ratio | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.7) | 1.2 (1.1-1.2) | 0.51x | — | — | 5.3 KiB | 11 / 9 MiB |
| `open` | Vortex Rust's | 0.6 (0.6-0.6) | 1.2 (1.1-1.2) | 0.52x | — | — | 5.0 KiB | 11 / 9 MiB |
| `scan` | Vorticity's | 8.2 (8.0-8.9) | 16.6 (16.3-24.9) | 0.49x | 3.3 | 1.6 | 14.5 KiB | 16 / 16 MiB |
| `scan` | Vortex Rust's | 3.9 (3.8-4.0) | 4.7 (4.7-4.9) | 0.82x | 6.9 | 5.7 | 10.9 KiB | 25 / 21 MiB |
| `project` | Vorticity's | 0.9 (0.9-1.0) | 2.0 (1.9-2.0) | 0.48x | 8.8 | 4.2 | 16.8 KiB | 12 / 13 MiB |
| `project` | Vortex Rust's | 1.0 (1.0-1.1) | 1.7 (1.6-1.8) | 0.58x | 8.5 | 5.0 | 13.1 KiB | 13 / 11 MiB |
| `filter-narrow` | Vorticity's | 1.9 (1.9-2.0) | 2.5 (2.4-2.5) | 0.79x | 0.1 | 0.1 | 41.4 KiB | 15 / 13 MiB |
| `filter-narrow` | Vortex Rust's | 1.8 (1.8-1.9) | 2.4 (2.4-2.6) | 0.76x | 0.1 | 0.1 | 21.2 KiB | 17 / 14 MiB |
| `filter-wide` | Vorticity's | 4.7 (4.7-4.8) | 9.6 (9.6-9.8) | 0.49x | 2.8 | 1.4 | 23.8 KiB | 15 / 15 MiB |
| `filter-wide` | Vortex Rust's | 2.9 (2.9-3.2) | 3.8 (3.7-3.8) | 0.78x | 4.6 | 3.5 | 21.2 KiB | 21 / 18 MiB |
| `take` | Vorticity's | 8.0 (7.9-8.0) | 8.5 (8.4-8.8) | 0.93x | — | — | 38.2 KiB | 15 / 14 MiB |
| `take` | Vortex Rust's | 2.0 (2.0-2.1) | 3.6 (3.5-3.7) | 0.56x | — | — | 34.6 KiB | 17 / 19 MiB |
| `write` | Vorticity's | 35.4 (35.1-36.3) | 76.7 (76.3-84.0) | 0.46x | 0.8 | 0.3 | 30.7 KiB | 21 / 77 MiB |
| `write` | Vortex Rust's | 31.2 (30.8-31.4) | 63.7 (63.0-67.2) | 0.49x | 0.9 | 0.4 | 27.1 KiB | 30 / 78 MiB |
| `append` | Vorticity's | 11.1 (10.8-11.2) | not asked | n/a | — | — | 224.3 KiB | 20 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | ratio | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.6) | 2.8 (2.7-2.9) | 0.21x | — | — | 5.3 KiB | 11 / 10 MiB |
| `open` | Vortex Rust's | 0.6 (0.6-0.6) | 2.7 (2.6-2.9) | 0.22x | — | — | 5.0 KiB | 11 / 10 MiB |
| `scan` | Vorticity's | 4.2 (3.7-4.8) | 6.2 (5.7-7.2) | 0.68x | 6.3 | 4.3 | 40.7 KiB | 48 / 49 MiB |
| `scan` | Vortex Rust's | 3.6 (3.5-3.7) | 5.6 (5.5-5.8) | 0.64x | 7.5 | 4.8 | 27.5 KiB | 64 / 52 MiB |
| `project` | Vorticity's | 1.6 (1.6-1.7) | 3.8 (3.6-3.9) | 0.43x | 5.1 | 2.2 | 33.6 KiB | 21 / 21 MiB |
| `project` | Vortex Rust's | 1.7 (1.6-1.8) | 3.6 (3.5-3.8) | 0.48x | 4.8 | 2.3 | 22.5 KiB | 20 / 19 MiB |
| `filter-narrow` | Vorticity's | 1.7 (1.7-1.9) | 4.4 (4.3-4.5) | 0.40x | 0.2 | 0.1 | 43.5 KiB | 13 / 16 MiB |
| `filter-narrow` | Vortex Rust's | 1.7 (1.6-1.8) | 4.7 (4.3-5.3) | 0.35x | 0.2 | 0.1 | 29.0 KiB | 14 / 19 MiB |
| `filter-wide` | Vorticity's | 3.4 (3.0-3.8) | 5.4 (5.2-5.4) | 0.63x | 3.9 | 2.5 | 37.2 KiB | 31 / 35 MiB |
| `filter-wide` | Vortex Rust's | 3.1 (2.9-3.2) | 5.3 (5.2-5.6) | 0.57x | 4.4 | 2.5 | 28.8 KiB | 39 / 36 MiB |
| `take` | Vorticity's | 4.1 (4.0-4.3) | 4.9 (4.7-5.1) | 0.84x | — | — | 630.9 KiB | 27 / 25 MiB |
| `take` | Vortex Rust's | 2.3 (2.2-2.3) | 4.7 (4.7-5.4) | 0.48x | — | — | 281.3 KiB | 19 / 22 MiB |
| `write` | Vorticity's | 13.5 (12.5-13.7) | 22.4 (21.6-23.2) | 0.60x | 2.0 | 1.2 | 2.6 MiB | 58 / 120 MiB |
| `write` | Vortex Rust's | 15.1 (14.6-15.3) | 23.6 (22.7-24.1) | 0.64x | 1.8 | 1.1 | 1.4 MiB | 74 / 136 MiB |
| `append` | Vorticity's | 10.7 (10.4-10.9) | not asked | n/a | — | — | 221.4 KiB | 21 / — MiB |

### 10,485,760 rows

| column | Vorticity's, 24,464,252 bytes | Vortex Rust's, 78,986,804 bytes |
|---|---|---|
| `monotone` | 160 chunks of `vortex.sequence` | 80 chunks of `vortex.sequence` |
| `value` | 160 chunks of `vortex.zstd` | 80 chunks of `vortex.alprd` |
| `label` | 160 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 20 chunks of `fastlanes.bitpacked` |
| `flag` | 160 chunks of `vortex.bool` | 3 chunks of `vortex.bool` |

#### One core

| scenario | file | Vorticity, ms | Vortex Rust, ms | ratio | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.6) | 1.2 (1.2-1.2) | 0.48x | — | — | 21.1 KiB | 11 / 9 MiB |
| `open` | Vortex Rust's | 0.6 (0.6-0.9) | 1.2 (1.1-1.2) | 0.52x | — | — | 9.5 KiB | 11 / 9 MiB |
| `scan` | Vorticity's | 70.5 (70.3-71.2) | 147.3 (146.6-151.2) | 0.48x | 3.8 | 1.8 | 74.2 KiB | 36 / 35 MiB |
| `scan` | Vortex Rust's | 25.8 (25.5-26.1) | 29.4 (29.3-30.4) | 0.88x | 10.3 | 9.1 | 28.3 KiB | 90 / 84 MiB |
| `project` | Vorticity's | 1.8 (1.8-2.0) | 5.6 (5.5-5.7) | 0.32x | 46.3 | 14.9 | 76.5 KiB | 13 / 31 MiB |
| `project` | Vortex Rust's | 1.7 (1.7-1.7) | 2.9 (2.8-2.9) | 0.60x | 48.6 | 29.2 | 30.5 KiB | 13 / 11 MiB |
| `filter-narrow` | Vorticity's | 2.7 (2.5-2.9) | 5.8 (5.6-5.9) | 0.46x | 1.0 | 0.5 | 107.0 KiB | 16 / 21 MiB |
| `filter-narrow` | Vortex Rust's | 2.3 (2.2-2.4) | 4.7 (4.7-5.1) | 0.48x | 1.2 | 0.6 | 62.1 KiB | 19 / 24 MiB |
| `filter-wide` | Vorticity's | 36.2 (36.1-36.7) | 77.5 (77.3-78.0) | 0.47x | 3.7 | 1.7 | 107.0 KiB | 26 / 31 MiB |
| `filter-wide` | Vortex Rust's | 14.2 (14.1-15.3) | 19.3 (19.1-19.4) | 0.74x | 9.4 | 6.9 | 62.1 KiB | 54 / 61 MiB |
| `take` | Vorticity's | 54.6 (54.4-55.0) | 66.8 (66.6-67.5) | 0.82x | — | — | 97.9 KiB | 33 / 35 MiB |
| `take` | Vortex Rust's | 5.6 (5.4-5.7) | 18.7 (18.6-19.3) | 0.30x | — | — | 52.0 KiB | 27 / 81 MiB |
| `write` | Vorticity's | 315.5 (314.2-316.3) | 689.7 (685.4-700.1) | 0.46x | 0.8 | 0.4 | 95.4 KiB | 43 / 452 MiB |
| `write` | Vortex Rust's | 271.6 (270.8-275.5) | 560.5 (556.4-568.3) | 0.48x | 1.0 | 0.5 | 49.5 KiB | 99 / 451 MiB |
| `append` | Vorticity's | 56.4 (56.1-56.7) | not asked | n/a | — | — | 1.4 MiB | 25 / — MiB |

#### All 32 cores

| scenario | file | Vorticity, ms | Vortex Rust, ms | ratio | Vorticity, GB/s | Vortex Rust, GB/s | Vorticity, allocated | peak, Vorticity / Rust |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `open` | Vorticity's | 0.6 (0.6-0.7) | 2.8 (2.7-2.9) | 0.22x | — | — | 21.1 KiB | 11 / 10 MiB |
| `open` | Vortex Rust's | 0.6 (0.6-0.6) | 2.6 (2.5-2.7) | 0.22x | — | — | 9.5 KiB | 11 / 10 MiB |
| `scan` | Vorticity's | 12.4 (12.0-12.8) | 16.8 (16.5-17.8) | 0.74x | 21.5 | 15.9 | 142.6 KiB | 103 / 115 MiB |
| `scan` | Vortex Rust's | 18.0 (17.8-19.0) | 18.3 (16.6-19.6) | 0.98x | 14.8 | 14.6 | 86.3 KiB | 262 / 130 MiB |
| `project` | Vorticity's | 2.9 (2.8-4.0) | 6.9 (6.5-7.1) | 0.42x | 28.6 | 12.1 | 124.8 KiB | 30 / 53 MiB |
| `project` | Vortex Rust's | 3.0 (2.8-3.1) | 5.0 (4.6-5.2) | 0.60x | 28.3 | 16.9 | 75.7 KiB | 45 / 38 MiB |
| `filter-narrow` | Vorticity's | 2.2 (2.2-2.3) | 7.2 (7.0-7.4) | 0.30x | 1.2 | 0.4 | 110.0 KiB | 17 / 26 MiB |
| `filter-narrow` | Vortex Rust's | 2.2 (2.2-2.5) | 6.5 (6.3-7.1) | 0.34x | 1.2 | 0.4 | 89.0 KiB | 19 / 29 MiB |
| `filter-wide` | Vorticity's | 8.4 (7.9-8.6) | 13.7 (13.2-14.1) | 0.61x | 15.9 | 9.7 | 183.6 KiB | 92 / 109 MiB |
| `filter-wide` | Vortex Rust's | 12.6 (12.2-13.5) | 13.5 (12.5-14.3) | 0.94x | 10.6 | 9.9 | 1.4 MiB | 226 / 91 MiB |
| `take` | Vorticity's | 11.5 (11.3-12.2) | 12.1 (11.8-12.5) | 0.94x | — | — | 276.3 KiB | 61 / 66 MiB |
| `take` | Vortex Rust's | 4.2 (3.6-4.5) | 14.0 (13.6-14.5) | 0.30x | — | — | 583.2 KiB | 30 / 96 MiB |
| `write` | Vorticity's | 107.6 (104.5-113.8) | 132.9 (130.1-150.2) | 0.81x | 2.5 | 2.0 | 2.7 MiB | 116 / 648 MiB |
| `write` | Vortex Rust's | 106.2 (99.8-110.1) | 131.5 (129.3-133.0) | 0.81x | 2.5 | 2.0 | 2.7 MiB | 278 / 676 MiB |
| `append` | Vorticity's | 52.4 (51.4-52.5) | not asked | n/a | — | — | 3.2 MiB | 58 / — MiB |

`open`: open the file and read no rows. `scan`: read every column of every row. `project`: read one column of four. `filter-narrow`: read the rows of a band holding about one in a hundred. `filter-wide`: read the rows of a band holding about half. `take`: take a thousand rows spread across the file. `write`: read the file and encode it back out. `append`: append a tenth of the rows to a copy of the file.

**On one core.** Vorticity took less time than Rust on 28 of 28 compared rows.
The lowest ratio is `take` at 10,485,760 rows on Vortex Rust's file (0.30x), the highest
`take` at 1,048,576 rows on Vorticity's file (0.93x).

**On all 32 cores.** Vorticity took less time than Rust on 28 of 28 compared rows.
The lowest ratio is `open` at 1,048,576 rows on Vorticity's file (0.21x), the highest
`scan` at 10,485,760 rows on Vortex Rust's file (0.98x).

**The process start** is not in the figures: 42 ms for Vorticity's native binary and 28 ms for Rust's,
most of it the operating system starting a binary, and 92 ms for the managed runtime on the JIT.

**Not asked of Rust**: `append`, which its harness has no entry point for; those figures
are Vorticity's alone.

### A first call on the JIT

The same scenarios in the framework-dependent build, under `dotnet`, on one core: what a
process that is not compiled ahead of time pays the first time, the code compiling as it runs.
Milliseconds, the median of the same runs.

| scenario | 1,048,576 rows, Vorticity's file | 1,048,576 rows, Vortex Rust's file | 10,485,760 rows, Vorticity's file | 10,485,760 rows, Vortex Rust's file |
|---|---:|---:|---:|---:|
| `open` | 23.6 | 24.3 | 23.8 | 24.2 |
| `scan` | 102.3 | 105.7 | 172.9 | 134.8 |
| `project` | 72.9 | 75.0 | 74.1 | 75.6 |
| `filter-narrow` | 143.6 | 138.9 | 140.9 | 146.0 |
| `filter-wide` | 123.9 | 129.1 | 159.9 | 149.1 |
| `take` | 116.3 | 102.4 | 171.3 | 110.9 |
| `write` | 275.5 | 281.5 | 684.9 | 647.8 |
| `append` | 284.2 | — | 375.4 | — |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1, rustc 1.98.1 (48a229cea 2026-09-01); commit b5104f2 with uncommitted changes, 2026-09-27 02:49 UTC.*
<!-- /results: scenarios -->

<!-- results: decoding -->
## Decoding, per encoding

One file per encoding, and per notable shape of one, written by Rust's writer
(`bench/gen-throughput.sh`): a million rows each, `table_wide` fifty columns of fifty thousand.
Each side scans the file, every value decoded to its plain form, 12 times in a process of its own on
one core; a figure is the median of the last 10 scans, and the median of 3 processes: what a
decoder costs once warm. Throughput is over the plain size, as above; a figure past what memory
can move is a column handed out as views of the mapped file, nothing decoded and nothing
copied, and measures the walk of the layout.

| encoding | rows | Vorticity, ns/row | Vortex Rust, ns/row | ratio | Vorticity, GB/s | Vortex Rust, GB/s |
|---|---:|---:|---:|---:|---:|---:|
| `alp` | 1,000,000 | 0.28 | 1.00 | 0.28x | 28.8 | 8.0 |
| `alp_no_patches` | 1,000,000 | 0.16 | 0.72 | 0.22x | 49.2 | 11.1 |
| `alp_patched_no_chunk_offsets` | 1,000,000 | 0.18 | 0.78 | 0.23x | 44.3 | 10.3 |
| `alprd` | 1,000,000 | 0.41 | 0.87 | 0.46x | 19.8 | 9.2 |
| `bool` | 1,000,000 | 0.03 | 0.17 | 0.19x | 4.0 | 0.8 |
| `bool_bit_offset3` | 1,000,000 | 0.03 | 0.17 | 0.20x | 3.7 | 0.8 |
| `bool_bit_offset7` | 1,000,000 | 0.03 | 0.17 | 0.20x | 3.8 | 0.7 |
| `bool_bit_offset_straddle` | 1,000,000 | 0.04 | 0.17 | 0.20x | 3.6 | 0.7 |
| `bytebool` | 1,000,000 | 0.05 | 0.21 | 0.23x | 2.6 | 0.6 |
| `chunked` | 1,000,000 | 0.04 | 0.66 | 0.06x | 207.8 | 12.1 |
| `chunked_bool` | 1,000,000 | 0.05 | 0.22 | 0.21x | 2.7 | 0.6 |
| `chunked_decimal` | 1,000,000 | 0.04 | 0.63 | 0.06x | 213.3 | 12.7 |
| `chunked_empty_chunks` | 1,000,000 | 0.04 | 0.63 | 0.06x | 207.8 | 12.6 |
| `chunked_mixed_validity` | 1,000,000 | 0.30 | 0.56 | 0.54x | 26.8 | 14.6 |
| `chunked_one_chunk` | 1,000,000 | 0.04 | 0.58 | 0.07x | 210.5 | 13.7 |
| `chunked_varbinview` | 1,000,000 | 1.38 | 67.1 | 0.02x | 9.8 | 0.2 |
| `constant` | 1,000,000 | 0.03 | 0.22 | 0.15x | 250.0 | 36.4 |
| `datetimeparts` | 1,000,000 | 0.26 | 1.38 | 0.19x | 31.1 | 5.8 |
| `decimal` | 1,000,000 | 0.04 | 0.56 | 0.06x | 228.6 | 14.2 |
| `decimal_byte_parts` | 1,000,000 | 0.03 | 0.62 | 0.05x | 235.3 | 12.9 |
| `dict` | 1,000,000 | 0.29 | 0.66 | 0.44x | 20.6 | 9.1 |
| `dict_nullable_codes` | 1,000,000 | 0.64 | 1.74 | 0.37x | 8.6 | 3.2 |
| `dict_nullable_values_nonnull_codes` | 1,000,000 | 0.52 | 0.79 | 0.65x | 11.1 | 7.2 |
| `dict_u64_codes` | 1,000,000 | 0.43 | 0.79 | 0.54x | 14.0 | 7.6 |
| `dict_u8_codes` | 1,000,000 | 0.29 | 0.56 | 0.52x | 27.6 | 14.4 |
| `ext` | 1,000,000 | 0.04 | 0.57 | 0.06x | 222.2 | 13.9 |
| `fastlanes_bitpacked` | 1,000,000 | 0.11 | 0.24 | 0.47x | 36.2 | 16.9 |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,000,000 | 0.12 | 0.27 | 0.45x | 33.1 | 14.9 |
| `fastlanes_delta` | 1,000,000 | 0.26 | 0.95 | 0.28x | 30.4 | 8.4 |
| `fastlanes_for` | 1,000,000 | 0.18 | 0.58 | 0.32x | 43.2 | 13.7 |
| `fastlanes_rle` | 1,000,000 | 0.27 | 0.55 | 0.49x | 14.8 | 7.3 |
| `fixed_size_list` | 1,000,000 | 0.04 | 0.81 | 0.04x | 342.9 | 14.9 |
| `fsst` | 1,000,000 | 5.95 | 9.11 | 0.65x | 9.4 | 6.1 |
| `list` | 1,000,000 | 0.10 | 1.36 | 0.07x | 103.1 | 7.4 |
| `listview` | 1,000,000 | 0.16 | 7.78 | 0.02x | 62.1 | 1.3 |
| `map` | 1,000,000 | 0.41 | 57.5 | 0.01x | 48.8 | 0.3 |
| `masked` | 1,000,000 | 0.04 | 0.39 | 0.10x | 110.0 | 10.6 |
| `masked_all_invalid` | 1,000,000 | 0.04 | 0.39 | 0.10x | 105.3 | 10.2 |
| `masked_all_valid` | 1,000,000 | 0.04 | 0.39 | 0.10x | 103.9 | 10.3 |
| `null` | 1,000,000 | 0.03 | 0.16 | 0.20x | — | — |
| `onpair` | 1,000,000 | 2.64 | 6.40 | 0.41x | 8.2 | 3.4 |
| `parquet_variant` | 1,000,000 | 1.38 | 15.2 | 0.09x | 8.7 | 0.8 |
| `pco` | 1,000,000 | 0.38 | 1.74 | 0.22x | 20.9 | 4.6 |
| `primitive` | 1,000,000 | 0.03 | 0.59 | 0.05x | 250.0 | 13.6 |
| `runend` | 1,000,000 | 0.14 | 0.28 | 0.51x | 27.8 | 14.3 |
| `sequence` | 1,000,000 | 0.10 | 0.23 | 0.43x | 81.2 | 35.1 |
| `sparse` | 1,000,000 | 0.08 | 0.28 | 0.30x | 47.3 | 14.2 |
| `struct` | 1,000,000 | 0.19 | 61.3 | 0.00x | 76.7 | 0.2 |
| `table_mixed` | 1,000,000 | 2.21 | 133.3 | 0.02x | 31.7 | 0.5 |
| `table_wide` | 50,000 | 0.86 | 30.6 | 0.03x | 465.1 | 13.1 |
| `varbin` | 1,000,000 | 0.92 | 76.1 | 0.01x | 17.2 | 0.2 |
| `varbinview` | 1,000,000 | 0.85 | 67.9 | 0.01x | 15.9 | 0.2 |
| `variant` | 1,000,000 | 0.04 | 0.17 | 0.21x | 450.7 | 96.4 |
| `zigzag` | 1,000,000 | 0.10 | 0.45 | 0.22x | 39.4 | 8.8 |
| `zstd` | 1,000,000 | 4.00 | 9.78 | 0.41x | 8.0 | 3.3 |
| `zstd_buffers` | 1,000,000 | 0.43 | 3.10 | 0.14x | 18.7 | 2.6 |
| `zstd_nullable` | 1,000,000 | 1.88 | 4.43 | 0.42x | 4.3 | 1.8 |

Vorticity decoded 57 of 57 files in less time than Rust; the median ratio is 0.20x.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1, rustc 1.98.1 (48a229cea 2026-09-01); commit b5104f2 with uncommitted changes, 2026-09-27 02:49 UTC.*
<!-- /results: decoding -->

<!-- results: in-process -->
## In one process, after warm-up

Both readers called in turn in one process, Rust's through a C ABI, against one clock: at least
21 rounds after a 2-second warm-up, until the 95 % interval of the per-round ratios is within
5 % of their median. Our side runs on the JIT, warmed. Each axis is a question the `--ratio-check` gate
holds to a ceiling; what each one reads and asks is in
[05-benchmarks.md](../design/05-benchmarks.md) §3.

| axis | Vorticity, µs | Vortex Rust, µs | ratio | 95 % interval |
|---|---:|---:|---:|---|
| full scan | 553.4 | 3,012.1 | 0.18x | 0.180 to 0.186 |
| projected scan, 1 of 5 columns | 98.1 | 502.9 | 0.19x | 0.188 to 0.198 |
| open to first batch | 60.4 | 1,943.8 | 0.03x | 0.029 to 0.032 |
| open, footer only | 24.1 | 166.7 | 0.14x | 0.143 to 0.148 |
| read and write back | 6,146.7 | 19,180.1 | 0.33x | 0.318 to 0.348 |
| filtered scan, 1% band | 80.6 | 765.3 | 0.11x | 0.100 to 0.108 |
| filtered scan, half the rows | 164.4 | 1,231.2 | 0.13x | 0.129 to 0.135 |
| scattered take, 64 of 64 splits | 404.5 | 2,754.9 | 0.15x | 0.143 to 0.148 |
| key order, sorted column, 1% band | 110.8 | 335.3 | 0.31x | 0.298 to 0.319 |
| key order, uncorrelated, 64 rows | 272.7 | 423.8 | 0.65x | 0.614 to 0.676 |
| count, exact cover, 1% band | 176.3 | 448.0 | 0.38x | 0.365 to 0.399 |
| filtered scan, string equality, fsst | 253.9 | 546.1 | 0.47x | 0.431 to 0.475 |
| filtered scan, string prefix, fsst | 950.5 | 986.6 | 0.97x | 0.948 to 0.976 |
| filtered scan, string equality, dict | 82.6 | 279.1 | 0.29x | 0.291 to 0.297 |
| filtered scan, string prefix, dict | 103.2 | 395.8 | 0.26x | 0.255 to 0.270 |
| filtered scan, band, runend | 89.6 | 250.0 | 0.36x | 0.354 to 0.362 |
| filtered scan, band, bitpacked | 88.1 | 254.2 | 0.35x | 0.339 to 0.356 |
| full scan, 1M table | 1,811.7 | 144,088.6 | 0.01x | 0.012 to 0.013 |
| projected scan, 1 of 50 columns | 35.6 | 1,597.9 | 0.02x | 0.020 to 0.022 |

Vorticity took less time on 19 of 19 axes.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary 85ad1773783d; commit b5104f2 with uncommitted changes, 2026-09-27 02:50 UTC.*
<!-- /results: in-process -->

<!-- results: take -->
## Taking rows, per encoding

The files of the per-encoding corpus again, 64 rows spread evenly over each, one every 15,625: what a
decoder costs to reach a few rows rather than all of them.
Both implementations in one process, Rust's through a C ABI, taking turns against one clock:
at least 9 rounds after a 1-second warm-up per file, our side on the JIT, warmed.

| encoding | Vorticity, µs | Vortex Rust, µs | ratio |
|---|---:|---:|---:|
| `alp` | 118 | 1,166 | 0.10x |
| `alp_no_patches` | 54 | 619 | 0.08x |
| `alp_patched_no_chunk_offsets` | 66 | 645 | 0.10x |
| `alprd` | 66 | 577 | 0.12x |
| `bool` | 34 | 180 | 0.18x |
| `bool_bit_offset3` | 37 | 183 | 0.20x |
| `bool_bit_offset7` | 37 | 180 | 0.20x |
| `bool_bit_offset_straddle` | 36 | 181 | 0.20x |
| `bytebool` | 49 | 230 | 0.21x |
| `chunked` | 49 | 659 | 0.07x |
| `chunked_bool` | 48 | 254 | 0.19x |
| `chunked_decimal` | 51 | 670 | 0.07x |
| `chunked_empty_chunks` | 52 | 685 | 0.08x |
| `chunked_mixed_validity` | 320 | 611 | 0.51x |
| `chunked_one_chunk` | 49 | 684 | 0.07x |
| `chunked_varbinview` | 1,463 | 70,100 | 0.02x |
| `constant` | 38 | 170 | 0.23x |
| `datetimeparts` | 104 | 2,589 | 0.04x |
| `decimal` | 49 | 637 | 0.07x |
| `decimal_byte_parts` | 62 | 677 | 0.09x |
| `dict` | 96 | 556 | 0.17x |
| `dict_nullable_codes` | 110 | 603 | 0.19x |
| `dict_nullable_values_nonnull_codes` | 68 | 278 | 0.24x |
| `dict_u64_codes` | 92 | 843 | 0.11x |
| `dict_u8_codes` | 84 | 317 | 0.27x |
| `ext` | 48 | 623 | 0.08x |
| `fastlanes_bitpacked` | 58 | 270 | 0.22x |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 75 | 309 | 0.24x |
| `fastlanes_delta` | 123 | 1,653 | 0.07x |
| `fastlanes_for` | 54 | 626 | 0.08x |
| `fastlanes_rle` | 271 | 574 | 0.47x |
| `fixed_size_list` | 53 | 923 | 0.06x |
| `fsst` | 114 | 1,617 | 0.07x |
| `list` | 120 | 813 | 0.15x |
| `listview` | 273 | 7,801 | 0.03x |
| `map` | 685 | 67,752 | 0.01x |
| `masked` | 42 | 400 | 0.10x |
| `masked_all_invalid` | 43 | 403 | 0.10x |
| `masked_all_valid` | 44 | 405 | 0.11x |
| `null` | 35 | 162 | 0.21x |
| `onpair` | 92 | 1,013 | 0.09x |
| `parquet_variant` | 92 | 1,060 | 0.09x |
| `pco` | 301 | 1,770 | 0.17x |
| `primitive` | 43 | 629 | 0.07x |
| `runend` | 86 | 263 | 0.32x |
| `sequence` | 44 | 178 | 0.25x |
| `sparse` | 59 | 232 | 0.24x |
| `struct` | 383 | 67,279 | 0.01x |
| `table_mixed` | 2,136 | 145,032 | 0.01x |
| `table_wide` | 72 | 1,745 | 0.04x |
| `varbin` | 113 | 80,352 | 0.00x |
| `varbinview` | 790 | 69,788 | 0.01x |
| `variant` | 51 | 186 | 0.27x |
| `zigzag` | 52 | 418 | 0.12x |
| `zstd` | 971 | 10,378 | 0.09x |
| `zstd_buffers` | 437 | 3,114 | 0.14x |
| `zstd_nullable` | 1,354 | 4,956 | 0.27x |

Vorticity took less time on 57 of 57 files; the median ratio is 0.10x.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary 85ad1773783d; commit b5104f2 with uncommitted changes, 2026-09-27 02:51 UTC.*
<!-- /results: take -->

<!-- results: write -->
## Writing, per encoding

The files of the per-encoding corpus again, each read and written back out to a sink that
keeps nothing, both writers given the rows decoded: what a writer costs, the read included on both sides.
Both implementations in one process, Rust's through a C ABI, taking turns against one clock:
at least 9 rounds after a 1-second warm-up per file, our side on the JIT, warmed.

| encoding | Vorticity, µs | Vortex Rust, µs | ratio |
|---|---:|---:|---:|
| `alp` | 4,376 | 29,992 | 0.14x |
| `alp_no_patches` | 4,507 | 21,499 | 0.21x |
| `alp_patched_no_chunk_offsets` | 4,344 | 23,611 | 0.18x |
| `alprd` | 5,827 | 27,627 | 0.21x |
| `bool` | 175 | 1,254 | 0.14x |
| `bool_bit_offset3` | 185 | 1,125 | 0.16x |
| `bool_bit_offset7` | 185 | 1,129 | 0.16x |
| `bool_bit_offset_straddle` | 182 | 1,123 | 0.16x |
| `bytebool` | 155 | 1,017 | 0.15x |
| `chunked` | 1,272 | 12,044 | 0.10x |
| `chunked_bool` | 142 | 1,030 | 0.13x |
| `chunked_decimal` | 1,678 | 14,409 | 0.12x |
| `chunked_empty_chunks` | 998 | 11,910 | 0.08x |
| `chunked_mixed_validity` | 2,429 | 15,504 | 0.15x |
| `chunked_one_chunk` | 810 | 11,503 | 0.07x |
| `chunked_varbinview` | 22,478 | 95,018 | 0.24x |
| `constant` | 83 | 3,999 | 0.02x |
| `datetimeparts` | 2,512 | 26,036 | 0.10x |
| `decimal` | 1,670 | 13,829 | 0.12x |
| `decimal_byte_parts` | 1,667 | 13,842 | 0.12x |
| `dict` | 7,695 | 19,118 | 0.40x |
| `dict_nullable_codes` | 13,022 | 17,857 | 0.73x |
| `dict_nullable_values_nonnull_codes` | 12,264 | 18,026 | 0.68x |
| `dict_u64_codes` | 7,769 | 19,699 | 0.39x |
| `dict_u8_codes` | 16,361 | 22,009 | 0.74x |
| `ext` | 831 | 23,329 | 0.03x |
| `fastlanes_bitpacked` | 1,027 | 4,400 | 0.23x |
| `fastlanes_bitpacked_patched_no_chunk_offsets` | 1,066 | 5,175 | 0.20x |
| `fastlanes_delta` | 1,404 | 12,543 | 0.11x |
| `fastlanes_for` | 1,992 | 8,495 | 0.24x |
| `fastlanes_rle` | 1,618 | 9,311 | 0.15x |
| `fixed_size_list` | 978 | 31,694 | 0.03x |
| `fsst` | 60,769 | 369,274 | 0.16x |
| `list` | 5,084 | 22,175 | 0.23x |
| `listview` | 5,362 | 39,500 | 0.13x |
| `map` | 18,546 | 127,398 | 0.14x |
| `masked` | 1,831 | 18,767 | 0.10x |
| `masked_all_invalid` | 338 | 1,578 | 0.21x |
| `masked_all_valid` | 500 | 10,452 | 0.05x |
| `null` | 55 | 654 | 0.08x |
| `onpair` | 25,316 | 31,907 | 0.80x |
| `pco` | 1,034 | 12,691 | 0.08x |
| `primitive` | 783 | 10,828 | 0.07x |
| `runend` | 685 | 9,681 | 0.07x |
| `sequence` | 802 | 11,464 | 0.07x |
| `sparse` | 669 | 4,489 | 0.15x |
| `struct` | 37,295 | 166,588 | 0.22x |
| `table_mixed` | 64,331 | 503,438 | 0.13x |
| `table_wide` | 4,143 | 30,958 | 0.13x |
| `varbin` | 46,876 | 317,659 | 0.15x |
| `varbinview` | 20,919 | 95,327 | 0.22x |
| `variant` | 98 | 2,805 | 0.04x |
| `zigzag` | 1,401 | 11,610 | 0.11x |
| `zstd` | 41,519 | 57,769 | 0.79x |
| `zstd_buffers` | 2,004 | 8,348 | 0.24x |
| `zstd_nullable` | 4,019 | 21,427 | 0.21x |

Vorticity took less time on 56 of 56 files; the median ratio is 0.15x.
Rust's writer declines `parquet_variant`, which is left out.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; Vortex 0.86.1 through the C ABI of `tools/vxbench-rs`, binary 85ad1773783d; commit b5104f2 with uncommitted changes, 2026-09-27 02:53 UTC.*
<!-- /results: write -->

<!-- results: tradeoffs -->
## Encodings, column by column

One column of 10,000,000 rows per file, written under each compression profile and each hint that
applies to it, then opened and read. A **scan** decodes every value to its plain form and reads it
once; a **take** reads 1,000 rows spread over the file. Each figure is the median of 3 passes after
1 warm-up, the file in the page cache; a write is net of generating its rows. **Crosses at** is the
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
| a sequence | Sequence x77 | 0.00 | 26 | 2.8 | 1.19 |  |
| sorted runs of 1 000 | RunEnd x77 | 0.01 | 55 | 2.3 | 1.15 | None: Canonical x77, 8.00 B/value, scan 1.4 ms, take 0.29 ms; reads faster above 88,339 MB/s |
| timestamps (ms, increasing, jittered) | BitPacked x77 | 3.38 | 85 | 6.6 | 0.60 | Smallest: Zstd x77, 2.07 B/value, scan 74.5 ms, take 60.30 ms; reads faster below 193 MB/s; None: Canonical x77, 8.00 B/value, scan 1.3 ms, take 0.25 ms; reads faster above 8,691 MB/s |
| random in 0..999 | BitPacked x77 | 1.25 | 0 | 2.4 | 0.30 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 38 | 2.3 | 0.49 | None: Canonical x77, 8.00 B/value, scan 1.4 ms, take 0.24 ms; reads faster above 79,377 MB/s |
| 100 003 distinct, repeating | BitPacked x77 | 4.63 | 32 | 2.6 | 0.26 | Auto, 16 MiB chunks: Dictionary x5, 2.36 B/value, scan 6.7 ms, take 0.47 ms; reads faster below 5,585 MB/s |
| uniform 64-bit | Canonical x77 | 8.00 | 31 | 1.4 | 0.22 |  |
| random in 0..999, 10 % null | BitPacked x82 | 1.38 | 15 | 3.6 | 0.31 | hint Dictionary: Dictionary x82, 1.27 B/value, scan 6.3 ms, take 0.53 ms; reads faster below 417 MB/s |

<details><summary>a sequence: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Sequence x77 | 38,836 | 0.00 | 26 | 2.8 | 1.19 |  |
| Fastest | Sequence x77 | 38,836 | 0.00 | 27 | 2.7 | 1.22 | same |
| Smallest | Sequence x77 | 38,836 | 0.00 | 29 | 2.8 | 1.48 | same |
| None | Canonical x77 | 80,043,716 | 8.00 | 51 | 2.8 | 1.42 | reads slower at any throughput |
| hint Dictionary | Sequence x77 | 38,836 | 0.00 | 195 | 1.7 | 0.43 | reads faster at any throughput |
| hint BitPacked | Sequence x77 | 38,836 | 0.00 | 5 | 1.7 | 0.42 | reads faster at any throughput |
| hint RunEnd | Sequence x77 | 38,836 | 0.00 | 4 | 1.7 | 0.41 | reads faster at any throughput |
| hint Zstd | Sequence x77 | 38,836 | 0.00 | 4 | 1.7 | 0.43 | reads faster at any throughput |

</details>

<details><summary>sorted runs of 1 000: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x77 | 87,996 | 0.01 | 55 | 2.3 | 1.15 |  |
| Fastest | RunEnd x77 | 87,996 | 0.01 | 67 | 2.3 | 1.14 | same |
| Smallest | RunEnd x76, Zstd x1 | 87,964 | 0.01 | 90 | 3.6 | 1.79 | reads slower at any throughput |
| None | Canonical x77 | 80,043,708 | 8.00 | 43 | 1.4 | 0.29 | reads faster above 88,339 MB/s |
| hint Dictionary | Dictionary x77 | 1,449,884 | 0.14 | 34 | 5.8 | 1.65 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 10,034,452 | 1.00 | 75 | 4.8 | 0.44 | reads slower at any throughput |
| hint RunEnd | RunEnd x77 | 87,996 | 0.01 | 23 | 2.4 | 0.65 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 136,620 | 0.01 | 13 | 6.3 | 4.86 | reads slower at any throughput |

</details>

<details><summary>timestamps (ms, increasing, jittered): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 33,795,164 | 3.38 | 85 | 6.6 | 0.60 |  |
| Fastest | BitPacked x77 | 33,795,164 | 3.38 | 16 | 3.6 | 0.29 | reads faster at any throughput |
| Smallest | Zstd x77 | 20,700,788 | 2.07 | 187 | 74.5 | 60.30 | reads faster below 193 MB/s |
| None | Canonical x77 | 80,043,716 | 8.00 | 28 | 1.3 | 0.25 | reads faster above 8,691 MB/s |
| hint Dictionary | BitPacked x77 | 33,795,164 | 3.38 | 93 | 3.6 | 0.26 | reads faster at any throughput |
| hint BitPacked | BitPacked x77 | 33,795,164 | 3.38 | 14 | 3.6 | 0.26 | reads faster at any throughput |
| hint RunEnd | BitPacked x77 | 33,795,164 | 3.38 | 26 | 3.6 | 0.28 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 20,700,788 | 2.07 | 150 | 74.1 | 60.66 | reads faster below 194 MB/s |

</details>

<details><summary>random in 0..999: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 12,544,276 | 1.25 | 0 | 2.4 | 0.30 |  |
| Fastest | BitPacked x77 | 12,544,276 | 1.25 | 0 | 2.4 | 0.25 | same |
| Smallest | BitPacked x77 | 12,544,276 | 1.25 | 151 | 2.4 | 0.25 | same |
| None | Canonical x77 | 80,043,708 | 8.00 | 5 | 1.3 | 0.23 | reads faster above 57,820 MB/s |
| hint Dictionary | Dictionary x77 | 12,652,716 | 1.27 | 28 | 5.7 | 0.56 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 12,544,276 | 1.25 | 0 | 2.7 | 0.41 | reads slower at any throughput |
| hint RunEnd | BitPacked x77 | 12,544,276 | 1.25 | 0 | 2.5 | 0.32 | same |
| hint Zstd | Zstd x77 | 20,141,164 | 2.01 | 143 | 78.7 | 64.11 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,740 | 0.51 | 38 | 2.3 | 0.49 |  |
| Fastest | Dictionary x77 | 5,063,740 | 0.51 | 37 | 2.0 | 0.30 | reads faster at any throughput |
| Smallest | Dictionary x77 | 5,063,740 | 0.51 | 169 | 2.0 | 0.31 | reads faster at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 31 | 1.4 | 0.24 | reads faster above 79,377 MB/s |
| hint Dictionary | Dictionary x77 | 5,063,740 | 0.51 | 37 | 2.0 | 0.34 | reads faster at any throughput |
| hint BitPacked | Dictionary x77 | 5,063,740 | 0.51 | 102 | 2.0 | 0.32 | reads faster at any throughput |
| hint RunEnd | Dictionary x77 | 5,063,740 | 0.51 | 93 | 2.0 | 0.31 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 10,539,388 | 1.05 | 125 | 61.4 | 49.82 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,924 | 0.50 | 51 | 2.9 | 0.50 | reads slower at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 46,295,644 | 4.63 | 32 | 2.6 | 0.26 |  |
| Fastest | BitPacked x77 | 46,295,644 | 4.63 | 37 | 2.6 | 0.29 | same |
| Smallest | BitPacked x77 | 46,295,644 | 4.63 | 162 | 2.6 | 0.29 | same |
| None | Canonical x77 | 80,043,716 | 8.00 | 40 | 1.3 | 0.22 | reads faster above 25,357 MB/s |
| hint Dictionary | BitPacked x77 | 46,295,644 | 4.63 | 105 | 2.7 | 0.34 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 46,295,644 | 4.63 | 33 | 2.6 | 0.32 | same |
| hint RunEnd | BitPacked x77 | 46,295,644 | 4.63 | 58 | 2.6 | 0.29 | same |
| hint Zstd | Zstd x77 | 51,815,860 | 5.18 | 132 | 60.3 | 49.04 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 23,603,540 | 2.36 | 117 | 6.7 | 0.47 | reads faster below 5,585 MB/s |

</details>

<details><summary>uniform 64-bit: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x77 | 80,043,724 | 8.00 | 31 | 1.4 | 0.22 |  |
| Fastest | Canonical x77 | 80,043,724 | 8.00 | 30 | 1.3 | 0.24 | reads faster at any throughput |
| Smallest | Canonical x77 | 80,043,724 | 8.00 | 95 | 1.4 | 0.28 | same |
| None | Canonical x77 | 80,043,724 | 8.00 | 30 | 1.3 | 0.22 | reads faster at any throughput |
| hint Dictionary | Canonical x77 | 80,043,724 | 8.00 | 130 | 1.3 | 0.24 | same |
| hint BitPacked | Canonical x77 | 80,043,724 | 8.00 | 97 | 1.3 | 0.22 | reads faster at any throughput |
| hint RunEnd | Canonical x77 | 80,043,724 | 8.00 | 101 | 1.5 | 0.27 | same |
| hint Zstd | Canonical x77 | 80,043,724 | 8.00 | 105 | 1.3 | 0.27 | reads faster at any throughput |

</details>

<details><summary>random in 0..999, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x82 | 13,800,452 | 1.38 | 15 | 3.6 | 0.31 |  |
| Fastest | BitPacked x82 | 13,800,452 | 1.38 | 15 | 3.5 | 0.30 | same |
| Smallest | BitPacked x82 | 13,800,452 | 1.38 | 164 | 3.5 | 0.31 | same |
| None | Canonical x82 | 81,294,660 | 8.13 | 34 | 1.4 | 0.33 | reads faster above 31,597 MB/s |
| hint Dictionary | Dictionary x82 | 12,676,388 | 1.27 | 68 | 6.3 | 0.53 | reads faster below 417 MB/s |
| hint BitPacked | BitPacked x82 | 13,800,452 | 1.38 | 14 | 3.5 | 0.32 | same |
| hint RunEnd | BitPacked x82 | 13,800,452 | 1.38 | 15 | 3.5 | 0.30 | same |
| hint Zstd | Zstd x82 | 19,283,932 | 1.93 | 156 | 74.0 | 58.66 | reads slower at any throughput |

</details>

### Floating point

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| prices (2 decimals) | Alp x77 | 2.50 | 59 | 6.9 | 0.55 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 30 | 2.1 | 0.34 |  |
| 1 000 distinct prices | Dictionary x77 | 1.28 | 68 | 3.8 | 0.42 |  |
| 100 003 distinct, repeating | Zstd x77 | 1.69 | 185 | 79.0 | 71.68 | Auto, 16 MiB chunks: Dictionary x5, 2.21 B/value, scan 7.4 ms, take 3.33 ms; reads faster above 73 MB/s; None: Canonical x77, 8.00 B/value, scan 1.3 ms, take 0.23 ms; reads faster above 812 MB/s |
| uniform in [0, 1) | AlpRd x77 | 6.91 | 45 | 5.6 | 0.71 | None: Canonical x77, 8.00 B/value, scan 1.4 ms, take 0.27 ms; reads faster above 2,658 MB/s |

<details><summary>prices (2 decimals): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Alp x77 | 25,049,724 | 2.50 | 59 | 6.9 | 0.55 |  |
| Fastest | Alp x77 | 25,049,724 | 2.50 | 40 | 4.6 | 0.34 | reads faster at any throughput |
| Smallest | Alp x77 | 25,049,724 | 2.50 | 356 | 4.5 | 0.30 | reads faster at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 40 | 1.6 | 0.33 | reads faster above 10,473 MB/s |
| hint Dictionary | Alp x77 | 25,049,724 | 2.50 | 143 | 4.6 | 0.34 | reads faster at any throughput |
| hint Alp | Alp x77 | 25,049,724 | 2.50 | 39 | 4.6 | 0.35 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 41,240,252 | 4.12 | 286 | 77.1 | 62.95 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,740 | 0.51 | 30 | 2.1 | 0.34 |  |
| Fastest | Dictionary x77 | 5,063,740 | 0.51 | 30 | 2.0 | 0.32 | same |
| Smallest | Dictionary x77 | 5,063,740 | 0.51 | 168 | 2.0 | 0.32 | same |
| None | Canonical x77 | 80,043,724 | 8.00 | 32 | 1.3 | 0.24 | reads faster above 99,549 MB/s |
| hint Dictionary | Dictionary x77 | 5,063,740 | 0.51 | 28 | 2.0 | 0.32 | same |
| hint Alp | Dictionary x77 | 5,063,740 | 0.51 | 100 | 2.0 | 0.32 | same |
| hint Zstd | Zstd x77 | 10,545,660 | 1.05 | 127 | 61.5 | 49.92 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,924 | 0.50 | 43 | 2.0 | 0.27 | same |

</details>

<details><summary>1 000 distinct prices: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 12,756,252 | 1.28 | 68 | 3.8 | 0.42 |  |
| Fastest | Dictionary x77 | 12,756,252 | 1.28 | 87 | 5.1 | 0.68 | reads slower at any throughput |
| Smallest | Dictionary x77 | 12,756,252 | 1.28 | 334 | 5.3 | 0.65 | reads slower at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 46 | 1.6 | 0.34 | reads faster above 30,690 MB/s |
| hint Dictionary | Dictionary x77 | 12,756,252 | 1.28 | 99 | 5.4 | 0.67 | reads slower at any throughput |
| hint Alp | Alp x77 | 12,756,252 | 1.28 | 161 | 6.7 | 0.62 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 26,073,404 | 2.61 | 293 | 103.3 | 82.83 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 12,545,724 | 1.25 | 110 | 5.4 | 0.62 | reads faster below 130 MB/s |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x77 | 16,909,884 | 1.69 | 185 | 79.0 | 71.68 |  |
| Fastest | Zstd x77 | 16,909,884 | 1.69 | 203 | 87.1 | 62.72 | reads slower at any throughput |
| Smallest | Zstd x77 | 16,909,884 | 1.69 | 216 | 61.9 | 52.47 | reads faster at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 27 | 1.3 | 0.23 | reads faster above 812 MB/s |
| hint Dictionary | Zstd x77 | 16,909,884 | 1.69 | 247 | 77.3 | 50.25 | same |
| hint Alp | Zstd x77 | 16,909,884 | 1.69 | 224 | 62.2 | 50.60 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 16,909,884 | 1.69 | 148 | 61.8 | 50.31 | reads faster at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 22,129,980 | 2.21 | 98 | 7.4 | 3.33 | reads faster above 73 MB/s |

</details>

<details><summary>uniform in [0, 1): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | AlpRd x77 | 69,054,372 | 6.91 | 45 | 5.6 | 0.71 |  |
| Fastest | AlpRd x77 | 69,054,372 | 6.91 | 48 | 4.7 | 0.51 | reads faster at any throughput |
| Smallest | AlpRd x77 | 69,054,372 | 6.91 | 169 | 4.7 | 0.49 | reads faster at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 32 | 1.4 | 0.27 | reads faster above 2,658 MB/s |
| hint Dictionary | AlpRd x77 | 69,054,372 | 6.91 | 212 | 4.7 | 0.54 | reads faster at any throughput |
| hint Alp | AlpRd x77 | 69,054,372 | 6.91 | 175 | 4.7 | 0.59 | reads faster at any throughput |
| hint Zstd | AlpRd x77 | 69,054,372 | 6.91 | 231 | 4.7 | 0.51 | reads faster at any throughput |

</details>

### Text

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| 16 cities, random order | Dictionary x175 | 0.51 | 210 | 7.4 | 0.76 |  |
| 10 000 distinct ids | Dictionary x306 | 2.54 | 622 | 35.5 | 29.36 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 8.7 ms, take 2.43 ms; reads faster at any throughput; hint Fsst: Fsst x306, 9.06 B/value, scan 32.1 ms, take 1.05 ms; reads faster above 19,417 MB/s |
| 10 000 distinct ids, 10 % null | Dictionary x306 | 2.45 | 642 | 41.6 | 33.35 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 14.5 ms, take 3.70 ms; reads faster at any throughput; hint Fsst: Fsst x306, 8.80 B/value, scan 38.0 ms, take 0.98 ms; reads faster above 17,261 MB/s |
| unique UUIDs | Zstd x611 | 20.61 | 1520 | 293.3 | 290.51 | hint Fsst: Fsst x611, 24.91 B/value, scan 148.5 ms, take 2.13 ms; reads faster above 297 MB/s |
| log lines (~100 B) | Zstd x1221 | 13.71 | 1406 | 394.9 | 321.43 | hint Fsst: Fsst x1221, 23.96 B/value, scan 161.6 ms, take 3.69 ms; reads faster above 440 MB/s |

<details><summary>16 cities, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x175 | 5,117,276 | 0.51 | 210 | 7.4 | 0.76 |  |
| Fastest | Dictionary x175 | 5,117,276 | 0.51 | 201 | 8.0 | 0.95 | reads slower at any throughput |
| Smallest | Dictionary x175 | 5,117,276 | 0.51 | 357 | 4.9 | 0.54 | reads faster at any throughput |
| None | Canonical x175 | 96,746,748 | 9.67 | 159 | 22.8 | 2.78 | reads slower at any throughput |
| hint Dictionary | Dictionary x175 | 5,117,276 | 0.51 | 204 | 4.8 | 0.54 | reads faster at any throughput |
| hint Fsst | Fsst x175 | 64,458,972 | 6.45 | 226 | 25.9 | 0.72 | reads slower at any throughput |
| hint Zstd | Zstd x175 | 17,427,092 | 1.74 | 233 | 91.6 | 64.44 | reads slower at any throughput |

</details>

<details><summary>10 000 distinct ids: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 25,433,780 | 2.54 | 622 | 35.5 | 29.36 |  |
| Fastest | Dictionary x306 | 25,433,780 | 2.54 | 611 | 34.8 | 29.27 | same |
| Smallest | Dictionary x305, Zstd x1 | 25,427,444 | 2.54 | 1311 | 35.6 | 29.93 | same |
| None | Canonical x306 | 140,098,164 | 14.01 | 368 | 18.6 | 4.34 | reads faster above 6,808 MB/s |
| hint Dictionary | Dictionary x306 | 25,433,780 | 2.54 | 951 | 46.1 | 38.67 | reads slower at any throughput |
| hint Fsst | Fsst x306 | 90,615,708 | 9.06 | 374 | 32.1 | 1.05 | reads faster above 19,417 MB/s |
| hint Zstd | Zstd x306 | 27,097,620 | 2.71 | 368 | 90.7 | 68.49 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 18,014,004 | 1.80 | 398 | 8.7 | 2.43 | reads faster at any throughput |

</details>

<details><summary>10 000 distinct ids, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 24,539,276 | 2.45 | 642 | 41.6 | 33.35 |  |
| Fastest | Dictionary x306 | 24,539,276 | 2.45 | 655 | 41.8 | 33.52 | same |
| Smallest | Dictionary x305, Zstd x1 | 24,533,580 | 2.45 | 1384 | 41.8 | 33.98 | same |
| None | Canonical x306 | 151,883,724 | 15.19 | 312 | 26.1 | 3.88 | reads faster above 8,195 MB/s |
| hint Dictionary | Dictionary x306 | 24,539,276 | 2.45 | 646 | 42.1 | 33.53 | same |
| hint Fsst | Fsst x306 | 87,950,764 | 8.80 | 470 | 38.0 | 0.98 | reads faster above 17,261 MB/s |
| hint Zstd | Zstd x306 | 25,595,628 | 2.56 | 391 | 113.5 | 62.19 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 17,982,596 | 1.80 | 706 | 14.5 | 3.70 | reads faster at any throughput |

</details>

<details><summary>unique UUIDs: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x611 | 206,058,580 | 20.61 | 1520 | 293.3 | 290.51 |  |
| Fastest | Zstd x611 | 206,058,580 | 20.61 | 1509 | 293.2 | 288.92 | same |
| Smallest | Zstd x611 | 206,058,580 | 20.61 | 2820 | 290.7 | 288.95 | same |
| None | Canonical x611 | 360,155,508 | 36.02 | 196 | 21.0 | 7.27 | reads faster above 566 MB/s |
| hint Dictionary | Zstd x611 | 206,058,580 | 20.61 | 2884 | 289.7 | 288.12 | same |
| hint Fsst | Fsst x611 | 249,114,908 | 24.91 | 1497 | 148.5 | 2.13 | reads faster above 297 MB/s |
| hint Zstd | Zstd x611 | 206,058,580 | 20.61 | 1473 | 290.8 | 289.80 | same |

</details>

<details><summary>log lines (~100 B): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x1221 | 137,107,180 | 13.71 | 1406 | 394.9 | 321.43 |  |
| Fastest | Zstd x1221 | 137,107,180 | 13.71 | 1400 | 394.4 | 321.13 | same |
| Smallest | Zstd x1221 | 137,107,180 | 13.71 | 3392 | 400.1 | 321.98 | same |
| None | Canonical x1221 | 725,468,436 | 72.55 | 756 | 34.1 | 12.23 | reads faster above 1,631 MB/s |
| hint Dictionary | Zstd x1221 | 137,107,180 | 13.71 | 2815 | 391.9 | 320.55 | same |
| hint Fsst | Fsst x1221 | 239,634,612 | 23.96 | 1920 | 161.6 | 3.69 | reads faster above 440 MB/s |
| hint Zstd | Zstd x1221 | 137,107,180 | 13.71 | 1399 | 393.9 | 320.61 | same |

</details>

### Booleans

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| half true | Canonical x10 | 0.13 | 0 | 0.1 | 0.20 |  |
| 1 % true | RunEnd x10 | 0.05 | 1 | 2.6 | 1.95 | None: Canonical x10, 0.13 B/value, scan 0.1 ms, take 0.17 ms; reads faster above 289 MB/s |

<details><summary>half true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.20 |  |
| Fastest | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.18 | same |
| Smallest | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.21 | reads slower at any throughput |
| None | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.31 | reads slower at any throughput |
| hint RunEnd | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.17 | reads faster at any throughput |

</details>

<details><summary>1 % true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x10 | 548,084 | 0.05 | 1 | 2.6 | 1.95 |  |
| Fastest | RunEnd x10 | 548,084 | 0.05 | 0 | 2.5 | 1.95 | same |
| Smallest | RunEnd x10 | 548,084 | 0.05 | 0 | 2.4 | 1.87 | same |
| None | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.17 | reads faster above 289 MB/s |
| hint RunEnd | RunEnd x10 | 548,084 | 0.05 | 0 | 2.5 | 1.88 | same |

</details>

### The profiles side by side

`Fastest` wrote what `Auto` wrote on 20 of 20 columns. `Smallest` wrote something else on *i64, sorted runs of 1 000*, *i64, timestamps (ms, increasing, jittered)*, *utf8, 10 000 distinct ids*, *utf8, 10 000 distinct ids, 10 % null*, and took up to 10.8 times `Auto`'s write, 164 ms against 15 ms on *i64, random in 0..999, 10 % null*, since it tries every scheme on every chunk.
`None`, the plain form, made the largest file on 20 of 20 columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit b5104f2 with uncommitted changes, 2026-09-27 02:59 UTC.*
<!-- /results: tradeoffs -->

<!-- results: advice -->
## What the advice picks

`VortexSession.AdviseAsync` on the same columns of 10,000,000 rows, under five goals: whole scans with the
bytes read at 2 GB/s, 100 MB/s and 10 GB/s, a row in a thousand read by row, and the bytes alone.
A cell is what the advice takes where it departs from the writer's own choice, empty where it
does not; a column it never departs on is left out.

| column | scans at 2 GB/s | scans at 100 MB/s | scans at 10 GB/s | a row in 1 000 read by row | size |
|---|---|---|---|---|---|
| i64, timestamps (ms, increasing, jittered) |  | `Zstd` |  | `Zstd` |  |
| i64, 100 003 distinct, repeating | 16 MiB chunks | 16 MiB chunks |  |  | 16 MiB chunks |
| i64, random in 0..999, 10 % null |  | `Dictionary` |  | `Dictionary` | `Dictionary` |
| f64, 100 003 distinct, repeating | 16 MiB chunks | 16 MiB chunks | `Canonical` |  |  |
| f64, uniform in [0, 1) |  |  | `Canonical` |  |  |
| utf8, 10 000 distinct ids | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `Zstd` | 16 MiB chunks |
| utf8, 10 000 distinct ids, 10 % null | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `Zstd` | 16 MiB chunks |
| utf8, unique UUIDs | `Canonical` |  | `Canonical` | `Fsst` |  |
| utf8, log lines (~100 B) | `Fsst` |  | `Canonical` | `Fsst` |  |
| bool, 1 % true | `Canonical` |  | `Canonical` | `Canonical` |  |

It keeps the writer's choice under every goal on the 10 other columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit b5104f2 with uncommitted changes, 2026-09-27 03:01 UTC.*
<!-- /results: advice -->

## Kernels, against what they replaced

Each hot loop of the library against the loop it replaced, or against the floor of the work it
does, measured by BenchmarkDotNet in one process on one clock, both checked to give the same result
before any is timed. A **Ratio** column is the row's time over its baseline's, the row in bold.
These are the fast profile's figures, which tell a direction: `--full` before a class's name runs
the reference profile on that class, the one to quote a small difference from.

<!-- results: kernel:FastLanesKernelBenchmarks -->
### `FastLanesKernelBenchmarks`

| Method       | BitWidth | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | ns/row | GB/s  | Allocated | Alloc Ratio |
|------------- |--------- |-----------:|----------:|----------:|------:|---------------- |-------:|------:|----------:|------------:|
| &#39;i64 scalar&#39; | 17       |  95.737 μs | 1.3069 μs | 0.2023 μs |  1.00 | Baseline        |   1.46 |  5.48 |         - |          NA |
| &#39;i64 vector&#39; | 17       |   8.919 μs | 1.2853 μs | 0.1989 μs |  0.09 | Faster          |   0.14 | 58.78 |         - |          NA |
| &#39;i32 scalar&#39; | 17       | 107.554 μs | 0.4447 μs | 0.1155 μs |  1.12 | Same            |   1.64 |  2.44 |         - |          NA |
| &#39;i64 pack&#39;   | 17       |  11.620 μs | 0.2981 μs | 0.0461 μs |  0.12 | Faster          |   0.18 | 45.12 |         - |          NA |
| &#39;i32 pack&#39;   | 17       |  12.505 μs | 0.4404 μs | 0.0681 μs |  0.13 | Faster          |   0.19 | 20.96 |         - |          NA |
| &#39;i32 vector&#39; | 17       |   4.634 μs | 0.1085 μs | 0.0168 μs |  0.05 | Faster          |   0.07 | 56.58 |         - |          NA |

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

| Method                       | Column | Mean          | Error         | StdDev      | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|----------------------------- |------- |--------------:|--------------:|------------:|------:|---------------- |----------:|------------:|
| &#39;Choose, the whole decision&#39; | i64    | 57,255.034 ns | 1,758.2423 ns | 456.6099 ns | 1.000 | Baseline        |      72 B |        1.00 |
| &#39;candidate: sequence&#39;        | i64    |     13.330 ns |     3.0557 ns |   0.4729 ns | 0.000 | Faster          |         - |        0.00 |
| &#39;candidate: bit packing&#39;     | i64    | 21,888.618 ns | 3,518.0519 ns | 913.6269 ns | 0.382 | Faster          |         - |        0.00 |
| &#39;candidate: FSST&#39;            | i64    |      2.234 ns |     1.1546 ns |   0.2998 ns | 0.000 | Faster          |         - |        0.00 |
| &#39;candidate: zstd&#39;            | i64    | 26,766.801 ns | 2,571.6941 ns | 667.8608 ns | 0.468 | Faster          |      72 B |        1.00 |
| &#39;candidate: ALP&#39;             | i64    |      2.614 ns |     0.0307 ns |   0.0047 ns | 0.000 | Faster          |         - |        0.00 |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.1, the fast profile; commit b5104f2 with uncommitted changes, 2026-09-27 03:15 UTC.*
<!-- /results: kernel:CompressorBenchmarks -->

<!-- results: kernel:LanesBenchmarks -->
### `LanesBenchmarks`

| Method                 | Lanes | Mean       | Error    | StdDev  | Ratio | MannWhitney(5%) | RatioSD | Completed Work Items | Lock Contentions | Allocated | Alloc Ratio |
|----------------------- |------ |-----------:|---------:|--------:|------:|---------------- |--------:|---------------------:|-----------------:|----------:|------------:|
| &#39;ours, n lanes&#39;        | 4     |   398.4 μs | 22.11 μs | 5.74 μs |  1.00 | Baseline        |    0.00 |              95.9531 |                - |   46473 B |        1.00 |
| &#39;reference, n workers&#39; | 4     | 1,629.8 μs | 16.32 μs | 2.53 μs |  4.09 | Slower          |    0.05 |                    - |                - |         - |        0.00 |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.1, the fast profile; commit b5104f2 with uncommitted changes, 2026-09-27 03:15 UTC.*
<!-- /results: kernel:LanesBenchmarks -->

## What this does not measure

* **A cold cache.** The page cache is warm for every run, on both sides; upstream's own benchmarks
  flush it before each query.
* **Pinned cores.** Windows pins no process, and two hardware threads share each core's execution
  units; both sides see all 32. Upstream measures on 94 pinned cores of one kind.
* **Another operating system on this processor.** The file system, the page cache and the thread
  scheduler are Windows' here, and macOS' on [the benchmark page](benchmarks.md): a difference
  between the two pages is the machine's as much as the processor's.
* **Your data and your machine.** A handful of tables and one machine: a column the compressor likes
  less, or a filter the zone maps cannot prune, moves these numbers more than either implementation
  does.
