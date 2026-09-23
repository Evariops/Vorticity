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
Ratio is the reference's time over our Native AOT build's: above 1.00x, we took less.
machine: Apple M4 Pro (Arm64), 14 processors, macOS 26.7.0
runtime: .NET 11.0.0-rc.1.26425.128, as Native AOT for this instruction set and on the JIT; reference: Vortex 0.86.1, upstream's benchmark build (mimalloc, target-cpu=native, codegen-units=1, no LTO), rustc 1.98.1 (48a229cea 2026-09-01) (Homebrew)
commit: 592e053
date: 2026-09-23 18:20 UTC

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
| `open` | Vorticity | 0.2 (0.2-0.3) | 15.4 (13.3-15.8) | 0.8 (0.7-0.8) | 3.48x | 10 MiB | 12 MiB |
| `open` | Vortex Rust | 0.2 (0.1-0.2) | 11.8 (11.4-12.1) | 0.5 (0.5-0.6) | 3.32x | 10 MiB | 12 MiB |
| `scan` | Vorticity | 7.4 (7.1-7.7) | 66.1 (61.5-70.9) | 9.0 (8.9-9.1) | 1.22x | 15 MiB | 18 MiB |
| `scan` | Vortex Rust | 2.1 (2.1-2.2) | 58.5 (57.2-62.2) | 3.1 (3.0-3.1) | 1.43x | 23 MiB | 24 MiB |
| `project` | Vorticity | 0.4 (0.4-0.5) | 41.8 (41.2-42.5) | 1.2 (1.1-1.3) | 2.76x | 11 MiB | 16 MiB |
| `project` | Vortex Rust | 0.5 (0.4-0.5) | 41.2 (40.8-45.5) | 0.9 (0.9-1.1) | 2.06x | 11 MiB | 13 MiB |
| `filter-narrow` | Vorticity | 1.0 (0.9-1.1) | 81.5 (80.6-85.1) | 1.3 (1.3-1.4) | 1.33x | 13 MiB | 15 MiB |
| `filter-narrow` | Vortex Rust | 0.8 (0.8-1.0) | 81.4 (78.7-88.5) | 1.4 (1.3-4.1) | 1.76x | 15 MiB | 16 MiB |
| `filter-wide` | Vorticity | 3.9 (3.8-3.9) | 71.9 (71.2-74.0) | 5.0 (4.9-5.1) | 1.31x | 16 MiB | 18 MiB |
| `filter-wide` | Vortex Rust | 1.6 (1.5-1.7) | 77.1 (74.8-79.1) | 2.4 (2.4-2.7) | 1.52x | 21 MiB | 21 MiB |
| `take` | Vorticity | 6.8 (6.8-7.0) | 64.1 (63.1-67.0) | 6.4 (6.3-7.4) | 0.93x | 14 MiB | 17 MiB |
| `take` | Vortex Rust | 1.2 (1.1-1.3) | 62.5 (61.8-65.1) | 2.0 (2.0-2.2) | 1.67x | 18 MiB | 22 MiB |
| `write` | Vorticity | 31.0 (30.8-31.5) | 174.6 (173.6-193.5) | 57.4 (57.0-60.3) | 1.85x | 19 MiB | 83 MiB |
| `write` | Vortex Rust | 26.6 (26.0-30.0) | 184.8 (179.0-195.1) | 54.6 (52.1-55.7) | 2.06x | 28 MiB | 77 MiB |
| `append` | Vorticity | 8.5 (8.3-8.5) | 176.6 (174.9-180.7) | not asked | n/a | 18 MiB | not asked |

### All 14 cores

| scenario | file written by | ours AOT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | Vorticity | 0.2 (0.1-0.3) | 0.7 (0.6-0.8) | 4.18x | 10 MiB | 12 MiB |
| `open` | Vortex Rust | 0.3 (0.2-0.3) | 0.9 (0.8-1.0) | 3.20x | 10 MiB | 12 MiB |
| `scan` | Vorticity | 1.7 (1.7-2.1) | 3.7 (3.6-3.9) | 2.14x | 36 MiB | 49 MiB |
| `scan` | Vortex Rust | 2.1 (1.7-2.1) | 4.0 (3.6-4.5) | 1.95x | 62 MiB | 53 MiB |
| `project` | Vorticity | 0.6 (0.6-0.7) | 1.7 (1.5-1.9) | 2.66x | 16 MiB | 23 MiB |
| `project` | Vortex Rust | 0.6 (0.6-0.7) | 1.6 (1.6-1.7) | 2.49x | 19 MiB | 22 MiB |
| `filter-narrow` | Vorticity | 1.1 (1.0-1.1) | 1.7 (1.7-1.9) | 1.64x | 14 MiB | 21 MiB |
| `filter-narrow` | Vortex Rust | 0.8 (0.8-0.9) | 1.9 (1.9-2.2) | 2.33x | 16 MiB | 21 MiB |
| `filter-wide` | Vorticity | 3.3 (3.1-3.4) | 2.9 (2.8-2.9) | 0.88x | 19 MiB | 38 MiB |
| `filter-wide` | Vortex Rust | 2.2 (2.1-2.4) | 3.0 (3.0-3.5) | 1.34x | 24 MiB | 41 MiB |
| `take` | Vorticity | 4.7 (4.6-5.0) | 2.5 (2.4-2.7) | 0.53x | 16 MiB | 25 MiB |
| `take` | Vortex Rust | 1.0 (1.0-1.1) | 2.0 (1.9-2.3) | 1.89x | 19 MiB | 26 MiB |
| `write` | Vorticity | 26.1 (25.2-27.1) | 16.3 (16.0-16.5) | 0.63x | 48 MiB | 120 MiB |
| `write` | Vortex Rust | 26.7 (26.1-27.5) | 17.1 (16.1-18.0) | 0.64x | 67 MiB | 125 MiB |
| `append` | Vorticity | 7.5 (7.2-7.7) | not asked | n/a | 18 MiB | not asked |

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
| `open` | Vorticity | 0.2 (0.2-0.3) | 13.4 (12.7-14.3) | 0.7 (0.7-0.7) | 2.80x | 10 MiB | 12 MiB |
| `open` | Vortex Rust | 0.2 (0.2-0.2) | 12.8 (12.0-13.4) | 0.6 (0.6-0.7) | 3.23x | 10 MiB | 12 MiB |
| `scan` | Vorticity | 67.8 (67.0-68.0) | 130.3 (124.8-131.6) | 78.0 (76.3-84.5) | 1.15x | 36 MiB | 29 MiB |
| `scan` | Vortex Rust | 17.7 (17.1-18.3) | 85.2 (84.4-86.2) | 22.7 (22.1-25.9) | 1.28x | 89 MiB | 57 MiB |
| `project` | Vorticity | 1.4 (1.3-1.5) | 45.9 (44.1-48.9) | 3.8 (3.5-4.0) | 2.61x | 13 MiB | 22 MiB |
| `project` | Vortex Rust | 1.3 (1.2-5.4) | 46.1 (43.4-51.2) | 2.2 (1.9-4.6) | 1.70x | 11 MiB | 14 MiB |
| `filter-narrow` | Vorticity | 1.4 (1.3-1.5) | 78.7 (78.1-80.3) | 3.1 (3.1-3.4) | 2.29x | 14 MiB | 24 MiB |
| `filter-narrow` | Vortex Rust | 0.8 (0.7-1.0) | 82.3 (80.0-84.0) | 3.0 (3.0-3.1) | 3.88x | 12 MiB | 29 MiB |
| `filter-wide` | Vorticity | 34.9 (34.6-35.0) | 110.6 (108.6-111.3) | 43.0 (42.0-43.7) | 1.23x | 26 MiB | 34 MiB |
| `filter-wide` | Vortex Rust | 9.9 (9.7-10.4) | 96.5 (92.6-99.2) | 14.9 (14.6-22.3) | 1.51x | 54 MiB | 53 MiB |
| `take` | Vorticity | 64.3 (64.0-66.3) | 130.1 (129.0-130.6) | 56.5 (56.0-56.8) | 0.88x | 35 MiB | 29 MiB |
| `take` | Vortex Rust | 5.5 (5.4-9.4) | 83.0 (81.3-84.0) | 12.2 (11.2-15.0) | 2.23x | 35 MiB | 52 MiB |
| `write` | Vorticity | 299.4 (290.7-304.8) | 556.5 (526.5-606.5) | 575.1 (554.6-594.4) | 1.92x | 41 MiB | 427 MiB |
| `write` | Vortex Rust | 243.3 (240.6-248.0) | 510.5 (506.2-517.5) | 509.2 (495.9-527.6) | 2.09x | 97 MiB | 419 MiB |
| `append` | Vorticity | 87.0 (85.6-87.4) | 300.1 (297.3-306.4) | not asked | n/a | 23 MiB | not asked |

### All 14 cores

| scenario | file written by | ours AOT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | Vorticity | 0.2 (0.1-0.4) | 0.8 (0.7-0.8) | 4.86x | 10 MiB | 13 MiB |
| `open` | Vortex Rust | 0.2 (0.2-0.3) | 0.8 (0.6-0.9) | 3.86x | 10 MiB | 12 MiB |
| `scan` | Vorticity | 8.8 (8.7-9.2) | 11.8 (11.7-12.5) | 1.33x | 63 MiB | 77 MiB |
| `scan` | Vortex Rust | 5.7 (5.1-14.4) | 9.1 (8.7-9.7) | 1.60x | 161 MiB | 98 MiB |
| `project` | Vorticity | 1.2 (1.1-2.3) | 3.9 (3.8-4.4) | 3.37x | 20 MiB | 45 MiB |
| `project` | Vortex Rust | 1.1 (1.0-1.2) | 2.2 (2.1-3.8) | 1.95x | 25 MiB | 24 MiB |
| `filter-narrow` | Vorticity | 1.2 (1.2-1.3) | 4.0 (3.6-4.1) | 3.28x | 16 MiB | 29 MiB |
| `filter-narrow` | Vortex Rust | 0.8 (0.8-1.8) | 3.7 (3.2-6.4) | 4.52x | 14 MiB | 34 MiB |
| `filter-wide` | Vorticity | 24.2 (23.8-26.9) | 9.1 (8.9-9.6) | 0.38x | 30 MiB | 70 MiB |
| `filter-wide` | Vortex Rust | 13.0 (12.5-16.6) | 8.5 (7.4-10.7) | 0.65x | 57 MiB | 93 MiB |
| `take` | Vorticity | 28.6 (27.9-29.0) | 9.0 (8.6-9.6) | 0.31x | 38 MiB | 56 MiB |
| `take` | Vortex Rust | 3.1 (3.0-3.2) | 6.4 (6.1-6.8) | 2.08x | 36 MiB | 79 MiB |
| `write` | Vorticity | 232.1 (229.0-233.1) | 97.7 (95.7-111.7) | 0.42x | 70 MiB | 514 MiB |
| `write` | Vortex Rust | 228.2 (226.6-231.7) | 97.6 (96.8-97.8) | 0.43x | 169 MiB | 525 MiB |
| `append` | Vorticity | 80.2 (79.6-85.1) | not asked | n/a | 51 MiB | not asked |

`open`: open the file and read no rows. `scan`: read every column of every row. `project`: read one column of four. `filter-narrow`: read the rows of a band holding about one in a hundred. `filter-wide`: read the rows of a band holding about half. `take`: take a thousand rows spread across the file. `write`: read the file and encode it back out. `append`: append a tenth of the rows to a copy of the file.

## Reading it

**What the table leaves out.** Starting the process, up to the point where it begins
its action, costs 10 ms for the native build and 8 ms for the reference on this machine, most of it
the operating system starting any binary at all.
The JIT build's start is 41 ms, the managed runtime coming up; what its
column then shows is the action compiling its own code as it runs, which is why an
`open` that takes the native build a fraction of a millisecond takes it tens.

**On one core.** Of 28 compared rows, the native build took less time than
the reference on 26. The best is `filter-narrow` at 10,485,760 rows on Vortex Rust's file (3.88x),
the worst `take` at 10,485,760 rows on Vorticity's file (0.88x).

**On all 14 cores.** Of 28 compared rows, the native build took less time than
the reference on 19. The best is `open` at 10,485,760 rows on Vorticity's file (4.86x),
the worst `take` at 10,485,760 rows on Vorticity's file (0.31x).

A ratio is the reference's time over the native build's: above 1.00x, we took less.

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
