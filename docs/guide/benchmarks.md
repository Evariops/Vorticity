# Benchmarks

What this library costs against the Rust implementation, on scenarios a caller
would recognise, on one core and on all of them. **This page is generated. Do not edit
it** — every figure comes from
`dotnet run -c Release --project bench/Vorticity.Benchmarks -- --report --markdown`,
and a hand-written number here would be a number nothing re-measures.

Vortex™ is a trademark of LF Projects, LLC. Vorticity is an independent implementation,
not affiliated with or endorsed by the Vortex project or LF Projects, LLC.

## How the two sides are built and run

**Vortex Rust 0.86.1** is built and run as upstream's own benchmarks are: mimalloc as the
allocator, `-C target-cpu=native -C force-frame-pointers=yes`, one codegen unit, no LTO. On
one core it runs on its single-threaded runtime, all the work on the calling thread; on all
cores, on a multi-threaded Tokio runtime of one worker per processor, which is how upstream's
benchmarks use every core, with each split decoded on its own task, as upstream's Arrow
conversion does. Its scans decode every column to its plain form: the reference's scan
otherwise hands back arrays in their stored encodings, which is not the work a reader
with no other representation does. `tools/vxbench-rs` is the harness.

**Vorticity** runs as a Native AOT binary built for this machine's instruction set
(`IlcInstructionSet=native`), with the workstation garbage collector. On one core its
scans run at one lane, the library's default; on all cores at one lane per processor,
`ScanBuilder.DefaultDegreeOfParallelism`. **Its writer is single-threaded**: on all cores
only the read of the `write` scenario runs in parallel, where the reference compresses its
chunks on every core. It also runs on one core as the framework-dependent build under
`dotnet`, the **JIT** column, in which the action compiles its own code as it goes: what the
first call costs in a fresh `dotnet` process.

**Both sides read the same two files**: the one Vorticity's writer makes, and the one Vortex
Rust's writer makes from the same rows, four columns of 2^20 and ten times as many rows: a
monotone `i64`, an `f64`, a short `utf8` and a nullable `bool`. Each writer chooses its own
chunks and encodings, which each size lists first: on a given file, both readers decode the
same encodings, and from one file to the other, the encodings change what a read costs.

**Each side runs in its own process**, once per run, and **times the action from its own
clock** once the process is up: opening the file, doing the work, rendering the rows. The
sides take turns run after run, the first of them changing, so that a machine that drifts
drifts under both. Peak resident memory and processor time are each side's own `getrusage`.
Both sides render the same rows, and the harness fails rather than print a ratio between two
different answers. The page cache is warm: one run of each side is discarded first.

5 runs of each scenario, each in its own process, the median reported with the lowest and highest beside it; one discarded run before them.
Figures are the action's time inside the process, from its own clock; the process start is reported apart.
Ratio is our Native AOT build's time over the reference's: under 1.00x, we took less.
machine: Apple M4 Pro (Arm64), 14 processors, macOS 26.7.0
runtime: .NET 11.0.0-rc.1.26425.128, as Native AOT for this instruction set and on the JIT; reference: Vortex 0.86.1, upstream's benchmark build (mimalloc, target-cpu=native, codegen-units=1, no LTO), rustc 1.98.1 (48a229cea 2026-09-01) (Homebrew)
commit: df420cb
date: 2026-09-23 20:07 UTC

## 1,048,576 rows

| column | as Vorticity writes it, 2,448,636 bytes in all | as Vortex Rust writes it, 7,903,964 bytes in all |
|---|---|---|
| `monotone` | 16 chunks of `vortex.sequence` | 8 chunks of `vortex.sequence` |
| `value` | 16 chunks of `vortex.zstd` | 8 chunks of `vortex.alprd` |
| `label` | 16 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 2 chunks of `fastlanes.bitpacked` |
| `flag` | 16 chunks of `vortex.bool` | one chunk of `vortex.bool` |

### One core

| scenario | file written by | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | Vorticity | 0.2 (0.2-0.2) | 12.9 (12.4-14.0) | 0.6 (0.6-0.6) | 0.29x | 10 MiB | 12 MiB |
| `open` | Vortex Rust | 0.2 (0.2-0.2) | 11.5 (11.3-13.8) | 0.6 (0.6-0.6) | 0.30x | 10 MiB | 12 MiB |
| `scan` | Vorticity | 7.1 (7.1-7.2) | 61.1 (60.5-63.4) | 8.7 (8.6-8.9) | 0.82x | 15 MiB | 18 MiB |
| `scan` | Vortex Rust | 2.4 (2.3-2.5) | 61.6 (58.4-64.8) | 3.7 (3.4-4.3) | 0.66x | 23 MiB | 24 MiB |
| `project` | Vorticity | 0.4 (0.4-0.6) | 44.6 (44.2-45.0) | 1.1 (1.1-1.3) | 0.38x | 11 MiB | 16 MiB |
| `project` | Vortex Rust | 0.4 (0.4-0.5) | 43.9 (41.8-47.7) | 0.9 (0.9-0.9) | 0.46x | 11 MiB | 13 MiB |
| `filter-narrow` | Vorticity | 1.0 (0.9-1.0) | 80.1 (79.1-81.9) | 1.3 (1.2-1.4) | 0.78x | 13 MiB | 15 MiB |
| `filter-narrow` | Vortex Rust | 0.9 (0.8-0.9) | 79.1 (78.7-84.9) | 1.4 (1.3-1.5) | 0.60x | 15 MiB | 16 MiB |
| `filter-wide` | Vorticity | 4.1 (3.9-4.3) | 73.7 (72.4-76.2) | 5.1 (4.9-5.6) | 0.80x | 16 MiB | 18 MiB |
| `filter-wide` | Vortex Rust | 1.7 (1.6-1.8) | 74.9 (73.8-80.4) | 2.6 (2.4-2.6) | 0.65x | 21 MiB | 21 MiB |
| `take` | Vorticity | 7.1 (6.8-8.1) | 64.2 (63.4-68.5) | 6.6 (6.5-8.0) | 1.07x | 14 MiB | 17 MiB |
| `take` | Vortex Rust | 1.2 (1.2-1.3) | 62.5 (61.0-63.7) | 2.2 (2.1-2.2) | 0.58x | 18 MiB | 22 MiB |
| `write` | Vorticity | 33.1 (31.4-34.6) | 183.2 (177.1-183.9) | 58.9 (58.2-63.1) | 0.56x | 19 MiB | 83 MiB |
| `write` | Vortex Rust | 26.3 (26.0-26.6) | 180.3 (179.4-193.8) | 52.3 (51.4-54.1) | 0.50x | 28 MiB | 75 MiB |
| `append` | Vorticity | 9.7 (9.4-10.3) | 181.2 (177.6-184.4) | not asked | n/a | 18 MiB | not asked |

### All 14 cores

| scenario | file written by | ours AOT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | Vorticity | 0.2 (0.2-0.2) | 0.8 (0.6-0.8) | 0.25x | 10 MiB | 12 MiB |
| `open` | Vortex Rust | 0.2 (0.2-0.2) | 0.8 (0.7-1.0) | 0.27x | 10 MiB | 12 MiB |
| `scan` | Vorticity | 1.8 (1.8-1.9) | 4.0 (3.9-4.2) | 0.46x | 35 MiB | 49 MiB |
| `scan` | Vortex Rust | 1.6 (1.6-1.8) | 4.0 (3.5-4.1) | 0.40x | 62 MiB | 55 MiB |
| `project` | Vorticity | 0.7 (0.6-0.7) | 1.6 (1.5-1.9) | 0.40x | 16 MiB | 23 MiB |
| `project` | Vortex Rust | 0.6 (0.6-0.8) | 1.6 (1.6-1.8) | 0.40x | 18 MiB | 22 MiB |
| `filter-narrow` | Vorticity | 1.1 (1.0-1.3) | 2.0 (1.9-5.1) | 0.54x | 14 MiB | 20 MiB |
| `filter-narrow` | Vortex Rust | 0.9 (0.8-0.9) | 2.1 (1.9-2.2) | 0.42x | 16 MiB | 21 MiB |
| `filter-wide` | Vorticity | 3.4 (3.3-3.5) | 3.1 (3.0-3.4) | 1.09x | 18 MiB | 37 MiB |
| `filter-wide` | Vortex Rust | 2.2 (2.2-2.4) | 3.2 (3.2-3.3) | 0.69x | 24 MiB | 41 MiB |
| `take` | Vorticity | 4.8 (4.6-5.3) | 2.5 (2.4-2.6) | 1.90x | 16 MiB | 26 MiB |
| `take` | Vortex Rust | 1.1 (1.0-1.2) | 2.0 (1.7-2.1) | 0.55x | 19 MiB | 26 MiB |
| `write` | Vorticity | 25.9 (25.7-26.0) | 16.8 (16.0-17.1) | 1.54x | 48 MiB | 120 MiB |
| `write` | Vortex Rust | 25.8 (25.5-26.3) | 15.8 (15.3-16.7) | 1.63x | 67 MiB | 130 MiB |
| `append` | Vorticity | 9.2 (9.1-10.0) | not asked | n/a | 18 MiB | not asked |

## 10,485,760 rows

| column | as Vorticity writes it, 24,464,252 bytes in all | as Vortex Rust writes it, 78,986,804 bytes in all |
|---|---|---|
| `monotone` | 160 chunks of `vortex.sequence` | 80 chunks of `vortex.sequence` |
| `value` | 160 chunks of `vortex.zstd` | 80 chunks of `vortex.alprd` |
| `label` | 160 chunks of `vortex.dict` | a dictionary layout, its values in one chunk of `vortex.fsst` and its codes in 20 chunks of `fastlanes.bitpacked` |
| `flag` | 160 chunks of `vortex.bool` | 3 chunks of `vortex.bool` |

### One core

| scenario | file written by | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | Vorticity | 0.2 (0.2-0.3) | 12.2 (12.0-12.6) | 0.6 (0.6-0.7) | 0.28x | 10 MiB | 12 MiB |
| `open` | Vortex Rust | 0.2 (0.2-0.2) | 12.2 (11.5-13.3) | 0.7 (0.6-0.8) | 0.27x | 10 MiB | 12 MiB |
| `scan` | Vorticity | 67.3 (66.9-68.9) | 123.6 (122.3-125.0) | 77.3 (76.9-78.2) | 0.87x | 36 MiB | 29 MiB |
| `scan` | Vortex Rust | 16.9 (16.7-18.2) | 86.1 (80.3-87.8) | 23.6 (22.0-24.4) | 0.72x | 89 MiB | 57 MiB |
| `project` | Vorticity | 1.4 (1.3-1.5) | 42.8 (41.8-45.5) | 3.7 (3.6-3.8) | 0.39x | 13 MiB | 22 MiB |
| `project` | Vortex Rust | 1.2 (1.2-1.5) | 45.5 (43.0-48.4) | 2.0 (1.9-2.1) | 0.62x | 11 MiB | 14 MiB |
| `filter-narrow` | Vorticity | 1.4 (1.3-1.5) | 75.4 (73.8-76.0) | 3.2 (3.1-3.9) | 0.45x | 14 MiB | 24 MiB |
| `filter-narrow` | Vortex Rust | 0.8 (0.7-0.8) | 80.5 (78.5-83.1) | 3.2 (3.0-3.5) | 0.25x | 12 MiB | 29 MiB |
| `filter-wide` | Vorticity | 34.6 (34.6-35.2) | 109.1 (107.7-111.5) | 41.8 (41.6-42.0) | 0.83x | 26 MiB | 34 MiB |
| `filter-wide` | Vortex Rust | 10.2 (9.5-10.6) | 101.1 (94.9-106.7) | 16.1 (15.2-18.6) | 0.63x | 54 MiB | 53 MiB |
| `take` | Vorticity | 64.7 (64.2-65.1) | 130.3 (128.2-133.9) | 56.9 (56.8-58.4) | 1.14x | 35 MiB | 30 MiB |
| `take` | Vortex Rust | 5.5 (5.4-5.8) | 82.3 (80.7-87.8) | 11.9 (11.2-12.7) | 0.46x | 35 MiB | 52 MiB |
| `write` | Vorticity | 293.0 (289.2-302.5) | 567.6 (536.5-613.4) | 566.0 (557.0-613.1) | 0.52x | 41 MiB | 427 MiB |
| `write` | Vortex Rust | 240.3 (239.8-244.5) | 516.0 (506.2-520.2) | 509.2 (502.7-528.2) | 0.47x | 97 MiB | 377 MiB |
| `append` | Vorticity | 87.7 (85.0-90.8) | 297.5 (290.8-302.2) | not asked | n/a | 23 MiB | not asked |

### All 14 cores

| scenario | file written by | ours AOT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | Vorticity | 0.2 (0.2-0.2) | 0.8 (0.8-0.9) | 0.25x | 10 MiB | 13 MiB |
| `open` | Vortex Rust | 0.2 (0.2-0.2) | 0.8 (0.8-0.9) | 0.24x | 10 MiB | 12 MiB |
| `scan` | Vorticity | 8.5 (7.9-9.1) | 12.5 (11.7-15.2) | 0.68x | 61 MiB | 77 MiB |
| `scan` | Vortex Rust | 5.7 (5.3-6.4) | 9.5 (8.9-9.8) | 0.60x | 159 MiB | 96 MiB |
| `project` | Vorticity | 1.2 (1.1-1.2) | 4.1 (3.7-4.8) | 0.29x | 20 MiB | 46 MiB |
| `project` | Vortex Rust | 1.2 (1.1-5.1) | 2.4 (2.2-2.6) | 0.52x | 25 MiB | 24 MiB |
| `filter-narrow` | Vorticity | 1.4 (1.3-2.9) | 3.9 (3.7-4.2) | 0.35x | 16 MiB | 28 MiB |
| `filter-narrow` | Vortex Rust | 1.0 (0.9-2.0) | 4.0 (3.7-4.1) | 0.24x | 15 MiB | 34 MiB |
| `filter-wide` | Vorticity | 24.1 (24.0-29.4) | 9.0 (8.7-9.3) | 2.69x | 30 MiB | 70 MiB |
| `filter-wide` | Vortex Rust | 13.5 (12.7-16.8) | 9.3 (7.9-11.1) | 1.45x | 57 MiB | 95 MiB |
| `take` | Vorticity | 28.3 (28.0-29.5) | 8.8 (8.4-10.7) | 3.22x | 38 MiB | 57 MiB |
| `take` | Vortex Rust | 3.4 (3.3-3.4) | 7.1 (6.7-12.2) | 0.48x | 36 MiB | 78 MiB |
| `write` | Vorticity | 229.2 (227.6-230.6) | 104.9 (98.4-107.6) | 2.18x | 70 MiB | 522 MiB |
| `write` | Vortex Rust | 232.2 (227.6-235.1) | 102.0 (95.8-119.8) | 2.28x | 169 MiB | 522 MiB |
| `append` | Vorticity | 82.6 (79.9-84.1) | not asked | n/a | 51 MiB | not asked |

`open`: open the file and read no rows. `scan`: read every column of every row. `project`: read one column of four. `filter-narrow`: read the rows of a band holding about one in a hundred. `filter-wide`: read the rows of a band holding about half. `take`: take a thousand rows spread across the file. `write`: read the file and encode it back out. `append`: append a tenth of the rows to a copy of the file.

## Reading it

**What the table leaves out.** Starting the process, up to the point where it begins
its action, costs 17 ms for the native build and 16 ms for the reference on this machine, most of it
the operating system starting any binary at all.
The JIT build's start is 45 ms, the managed runtime coming up; what its
column then shows is the action compiling its own code as it runs, which is why an
`open` that takes the native build a fraction of a millisecond takes it tens.

**On one core.** Of 28 compared rows, the native build took less time than
the reference on 26. The best is `filter-narrow` at 10,485,760 rows on Vortex Rust's file (0.25x),
the worst `take` at 10,485,760 rows on Vorticity's file (1.14x).

**On all 14 cores.** Of 28 compared rows, the native build took less time than
the reference on 19. The best is `filter-narrow` at 10,485,760 rows on Vortex Rust's file (0.24x),
the worst `take` at 10,485,760 rows on Vorticity's file (3.22x).

A ratio is the native build's time over the reference's: under 1.00x, we took less.

**Where there is nothing to compare against.** `append`: the reference shim
exposes no such entry point, so the figure is ours alone and is not a ratio. Those
cells read `not asked`, which is not the same claim as `refused`.

## What this does not measure

* **Steady state.** Every figure is a first and only action in a fresh process: a page cache
  warmed only by the discarded run before it, thread pools starting, and in the JIT column
  the code compiling as it runs. The per-encoding ratios in `bench/README.md` measure the
  other thing — the same code after warm-up, in one process — and they are the place to
  look for what a decoder costs.
* **Pinned cores.** macOS pins no process to a set of cores, and this machine's cores are of
  two kinds; both sides see all of them and the same count. Upstream's own figures come
  from 94 pinned cores of one kind, and are not this machine's.
* **A cold cache.** Upstream flushes the page cache before each query and keeps the first,
  cold, run in its median; here the cache is warm for every run, on both sides.
* **Your data.** One table of four columns is not every file. A column the compressor likes
  less, or a filter a zone map cannot prune, moves these numbers more than any implementation
  detail does.
* **Your machine.** These figures belong to the one named above.
