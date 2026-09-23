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
`ScanBuilder.DefaultDegreeOfParallelism`, and its writer summarizes its columns and compresses
a column's zstd frames on as many threads, `VortexWriteOptions.DegreeOfParallelism`, but
chooses and writes the encodings on one, where the reference compresses its chunks on every
core. It also runs on one core as the framework-dependent build under `dotnet`, the **JIT**
column, in which the action compiles its own code as it goes: what the first call costs in a
fresh `dotnet` process.

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
commit: c6d55ff
date: 2026-09-23 23:00 UTC

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
| `open` | Vorticity | 0.2 (0.2-0.3) | 12.4 (11.9-13.8) | 0.6 (0.6-0.9) | 0.29x | 9 MiB | 12 MiB |
| `open` | Vortex Rust | 0.2 (0.1-0.2) | 11.9 (11.5-13.5) | 0.6 (0.6-0.6) | 0.28x | 9 MiB | 12 MiB |
| `scan` | Vorticity | 7.6 (7.2-8.4) | 61.7 (59.6-63.1) | 8.8 (8.7-10.2) | 0.86x | 15 MiB | 18 MiB |
| `scan` | Vortex Rust | 2.3 (2.2-2.7) | 65.1 (59.7-68.7) | 3.4 (3.3-3.5) | 0.67x | 23 MiB | 24 MiB |
| `project` | Vorticity | 0.4 (0.4-0.5) | 43.8 (42.7-46.7) | 1.2 (1.1-1.2) | 0.34x | 11 MiB | 16 MiB |
| `project` | Vortex Rust | 0.4 (0.4-0.5) | 43.9 (42.3-47.3) | 0.9 (0.9-1.0) | 0.43x | 11 MiB | 13 MiB |
| `filter-narrow` | Vorticity | 1.1 (1.0-1.2) | 81.7 (81.5-85.1) | 1.3 (1.2-1.4) | 0.85x | 13 MiB | 15 MiB |
| `filter-narrow` | Vortex Rust | 0.8 (0.8-0.9) | 84.4 (80.4-84.8) | 1.3 (1.3-1.4) | 0.59x | 15 MiB | 16 MiB |
| `filter-wide` | Vorticity | 3.9 (3.8-6.9) | 73.5 (71.4-74.4) | 5.3 (5.0-5.7) | 0.75x | 14 MiB | 18 MiB |
| `filter-wide` | Vortex Rust | 1.5 (1.4-1.8) | 75.7 (74.0-80.5) | 2.5 (2.4-2.6) | 0.58x | 20 MiB | 21 MiB |
| `take` | Vorticity | 7.4 (7.3-7.5) | 71.7 (70.0-75.1) | 6.6 (6.5-6.8) | 1.12x | 13 MiB | 17 MiB |
| `take` | Vortex Rust | 1.2 (1.2-1.3) | 64.9 (63.4-67.9) | 2.1 (2.1-2.2) | 0.58x | 18 MiB | 22 MiB |
| `write` | Vorticity | 31.5 (30.9-34.5) | 180.5 (178.8-184.3) | 58.0 (57.1-61.1) | 0.54x | 19 MiB | 83 MiB |
| `write` | Vortex Rust | 27.0 (26.4-27.4) | 184.6 (182.7-186.0) | 52.9 (52.1-55.7) | 0.51x | 28 MiB | 75 MiB |
| `append` | Vorticity | 10.0 (9.9-10.4) | 183.3 (179.4-188.1) | not asked | n/a | 18 MiB | not asked |

### All 14 cores

| scenario | file written by | ours AOT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | Vorticity | 0.2 (0.2-0.2) | 0.8 (0.8-0.9) | 0.22x | 9 MiB | 12 MiB |
| `open` | Vortex Rust | 0.2 (0.2-0.2) | 0.8 (0.7-0.9) | 0.23x | 9 MiB | 12 MiB |
| `scan` | Vorticity | 1.9 (1.8-2.2) | 4.0 (3.9-4.2) | 0.47x | 35 MiB | 49 MiB |
| `scan` | Vortex Rust | 1.6 (1.5-1.7) | 4.0 (3.6-4.1) | 0.41x | 62 MiB | 55 MiB |
| `project` | Vorticity | 0.6 (0.6-0.7) | 1.7 (1.6-1.8) | 0.37x | 16 MiB | 23 MiB |
| `project` | Vortex Rust | 0.6 (0.6-0.7) | 1.7 (1.5-2.1) | 0.39x | 18 MiB | 22 MiB |
| `filter-narrow` | Vorticity | 0.6 (0.6-0.6) | 1.9 (1.8-2.1) | 0.30x | 12 MiB | 20 MiB |
| `filter-narrow` | Vortex Rust | 0.5 (0.5-0.8) | 1.9 (1.9-3.0) | 0.27x | 12 MiB | 20 MiB |
| `filter-wide` | Vorticity | 1.3 (1.2-1.4) | 3.1 (2.8-3.5) | 0.42x | 27 MiB | 38 MiB |
| `filter-wide` | Vortex Rust | 1.2 (1.1-1.7) | 3.2 (3.1-3.4) | 0.37x | 37 MiB | 41 MiB |
| `take` | Vorticity | 1.7 (1.6-1.7) | 2.4 (2.3-2.4) | 0.70x | 15 MiB | 28 MiB |
| `take` | Vortex Rust | 1.1 (0.9-1.3) | 2.0 (1.9-2.3) | 0.54x | 19 MiB | 26 MiB |
| `write` | Vorticity | 11.8 (11.5-12.4) | 16.5 (16.1-17.4) | 0.71x | 52 MiB | 121 MiB |
| `write` | Vortex Rust | 12.8 (11.7-15.2) | 16.7 (15.4-19.2) | 0.77x | 71 MiB | 128 MiB |
| `append` | Vorticity | 9.5 (9.3-9.6) | not asked | n/a | 18 MiB | not asked |

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
| `open` | Vorticity | 0.2 (0.2-0.2) | 12.7 (12.0-13.5) | 0.6 (0.6-0.8) | 0.27x | 9 MiB | 12 MiB |
| `open` | Vortex Rust | 0.2 (0.2-0.2) | 12.1 (11.8-12.8) | 0.6 (0.6-0.6) | 0.27x | 10 MiB | 12 MiB |
| `scan` | Vorticity | 67.6 (67.3-68.2) | 127.1 (125.3-130.9) | 77.6 (77.1-79.9) | 0.87x | 36 MiB | 29 MiB |
| `scan` | Vortex Rust | 16.9 (16.6-17.0) | 82.3 (81.1-86.1) | 22.4 (21.8-23.3) | 0.75x | 89 MiB | 57 MiB |
| `project` | Vorticity | 1.5 (1.4-1.5) | 47.5 (45.5-51.3) | 4.0 (3.8-4.1) | 0.37x | 13 MiB | 22 MiB |
| `project` | Vortex Rust | 1.2 (1.2-3.0) | 46.5 (45.3-47.4) | 2.0 (1.9-2.1) | 0.64x | 11 MiB | 14 MiB |
| `filter-narrow` | Vorticity | 1.5 (1.4-1.5) | 77.6 (77.3-78.8) | 3.4 (3.2-3.5) | 0.43x | 14 MiB | 24 MiB |
| `filter-narrow` | Vortex Rust | 1.0 (0.9-1.1) | 80.6 (80.1-83.1) | 3.1 (3.0-3.5) | 0.32x | 16 MiB | 29 MiB |
| `filter-wide` | Vorticity | 34.5 (34.3-35.2) | 109.5 (109.1-116.4) | 43.1 (42.2-47.2) | 0.80x | 24 MiB | 34 MiB |
| `filter-wide` | Vortex Rust | 9.2 (8.9-9.8) | 90.5 (89.8-99.2) | 14.9 (14.6-15.6) | 0.62x | 53 MiB | 53 MiB |
| `take` | Vorticity | 52.4 (52.2-52.7) | 125.7 (124.7-131.1) | 57.3 (57.0-58.1) | 0.92x | 34 MiB | 30 MiB |
| `take` | Vortex Rust | 5.5 (5.4-5.9) | 83.5 (80.8-85.7) | 11.0 (10.8-12.5) | 0.50x | 35 MiB | 52 MiB |
| `write` | Vorticity | 292.0 (289.5-293.4) | 571.3 (566.5-621.7) | 559.6 (557.7-570.5) | 0.52x | 41 MiB | 428 MiB |
| `write` | Vortex Rust | 238.9 (237.7-240.9) | 512.7 (504.5-519.5) | 496.8 (489.9-514.4) | 0.48x | 97 MiB | 377 MiB |
| `append` | Vorticity | 87.5 (87.4-87.7) | 298.7 (294.3-302.6) | not asked | n/a | 23 MiB | not asked |

### All 14 cores

| scenario | file written by | ours AOT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | Vorticity | 0.2 (0.2-0.2) | 0.8 (0.8-0.8) | 0.23x | 9 MiB | 13 MiB |
| `open` | Vortex Rust | 0.2 (0.2-0.2) | 0.8 (0.8-1.0) | 0.25x | 10 MiB | 13 MiB |
| `scan` | Vorticity | 8.3 (8.0-8.4) | 12.0 (11.5-13.2) | 0.70x | 62 MiB | 77 MiB |
| `scan` | Vortex Rust | 5.2 (4.9-6.6) | 9.6 (8.7-10.4) | 0.54x | 161 MiB | 89 MiB |
| `project` | Vorticity | 1.2 (1.1-2.6) | 4.3 (3.9-4.4) | 0.28x | 20 MiB | 46 MiB |
| `project` | Vortex Rust | 1.1 (1.0-2.3) | 2.2 (2.1-2.4) | 0.49x | 25 MiB | 24 MiB |
| `filter-narrow` | Vorticity | 1.2 (1.1-1.3) | 4.0 (3.7-4.1) | 0.31x | 15 MiB | 28 MiB |
| `filter-narrow` | Vortex Rust | 1.1 (0.9-1.2) | 3.9 (3.5-4.7) | 0.27x | 17 MiB | 34 MiB |
| `filter-wide` | Vorticity | 5.3 (5.1-6.4) | 8.9 (8.5-9.9) | 0.60x | 52 MiB | 67 MiB |
| `filter-wide` | Vortex Rust | 4.2 (4.0-4.4) | 8.3 (7.1-8.7) | 0.50x | 124 MiB | 94 MiB |
| `take` | Vorticity | 8.9 (8.4-9.4) | 8.7 (8.2-15.0) | 1.03x | 36 MiB | 57 MiB |
| `take` | Vortex Rust | 3.1 (2.8-3.3) | 6.4 (6.1-7.9) | 0.48x | 36 MiB | 78 MiB |
| `write` | Vorticity | 85.4 (83.8-86.2) | 98.0 (95.8-120.5) | 0.87x | 74 MiB | 515 MiB |
| `write` | Vortex Rust | 85.6 (84.4-89.5) | 97.9 (97.8-104.8) | 0.87x | 173 MiB | 532 MiB |
| `append` | Vorticity | 81.7 (81.1-84.7) | not asked | n/a | 51 MiB | not asked |

`open`: open the file and read no rows. `scan`: read every column of every row. `project`: read one column of four. `filter-narrow`: read the rows of a band holding about one in a hundred. `filter-wide`: read the rows of a band holding about half. `take`: take a thousand rows spread across the file. `write`: read the file and encode it back out. `append`: append a tenth of the rows to a copy of the file.

## Reading it

**What the table leaves out.** Starting the process, up to the point where it begins
its action, costs 17 ms for the native build and 17 ms for the reference on this machine, most of it
the operating system starting any binary at all.
The JIT build's start is 42 ms, the managed runtime coming up; what its
column then shows is the action compiling its own code as it runs, which is why an
`open` that takes the native build a fraction of a millisecond takes it tens.

**On one core.** Of 28 compared rows, the native build took less time than
the reference on 27. The best is `open` at 10,485,760 rows on Vortex Rust's file (0.27x),
the worst `take` at 1,048,576 rows on Vorticity's file (1.12x).

**On all 14 cores.** Of 28 compared rows, the native build took less time than
the reference on 27. The best is `open` at 1,048,576 rows on Vorticity's file (0.22x),
the worst `take` at 10,485,760 rows on Vorticity's file (1.03x).

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
