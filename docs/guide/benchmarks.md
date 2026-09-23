# Benchmarks

What this library costs against the Rust implementation, on scenarios a caller
would recognise. **This page is generated. Do not edit it** — every figure comes from
`dotnet run -c Release --project bench/Vorticity.Benchmarks -- --report --markdown`,
and a hand-written number here would be a number nothing re-measures.

## What is measured

Eight scenarios, at 2^20 rows and at ten times that, on a table of four columns: a
monotone `i64`, an `f64`, a short `utf8` and a nullable `bool`. **Each side runs in its
own process**, once per run, and **times the action from its own clock**, once the
process is up: opening the file, doing the work, rendering the rows. The figures are that
time. Starting the process is measured too, from the parent's clock, and reported apart:
it is a property of the build, not of the work.

Our side runs twice. **AOT** is the same code published as Native AOT: a native binary,
like the reference's, and the one the ratio is taken against. **JIT** is the
framework-dependent build under `dotnet`, in which the action compiles its own code as it
goes: what the first call costs in a fresh `dotnet` process, and what a long-running
process pays once.

Peak resident memory and processor time are each side's own `getrusage`, and the wall
clock is the parent's. Both sides render the same rows, and the harness fails rather
than print a ratio between two different answers.

The reference does the **same work**, which took care: its scan has one entry point
that counts rows off an encoded array's metadata and one that materialises every
column. Only the second is comparable to a reader that has no other representation,
and it is the one measured here.

5 runs of each scenario, each in its own process, the median reported with the lowest and highest beside it; one discarded run before them.
Figures are the action's time inside the process, from its own clock; the process start is reported apart.
Ratio is the reference's time over our Native AOT build's: above 1.00x, we took less.
machine: Apple M4 Pro (Arm64), 14 processors, macOS 26.7.0
runtime: .NET 11.0.0-rc.1.26425.128, as Native AOT and on the JIT; reference: Vortex 0.86.1, cargo release with lto
commit: da335f7
date: 2026-09-23 14:02 UTC

## 1,048,576 rows, 3,191,324 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 0.2 (0.1-0.3) | 12.7 (11.7-13.5) | 0.5 (0.5-0.6) | 3.12x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 2.9 (2.8-3.1) | 58.3 (57.2-60.2) | 10.2 (10.1-10.5) | 3.54x | 19 MiB | 21 MiB |
| `project` | read one column of four | 0.4 (0.4-0.5) | 42.3 (41.0-43.9) | 0.8 (0.7-1.1) | 2.04x | 11 MiB | 11 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 1.1 (1.0-1.2) | 80.3 (79.2-82.2) | 2.0 (1.6-2.1) | 1.88x | 12 MiB | 15 MiB |
| `filter-wide` | read the rows of a band holding about half | 2.1 (2.1-2.3) | 76.5 (75.8-82.1) | 5.7 (5.6-5.8) | 2.76x | 22 MiB | 17 MiB |
| `take` | take a thousand rows spread across the file | 2.2 (2.1-2.4) | 59.9 (57.8-62.7) | 7.7 (7.6-7.8) | 3.52x | 15 MiB | 16 MiB |
| `write` | read the file and encode it back out | 26.7 (26.3-27.3) | 189.2 (187.0-195.6) | 195.5 (193.9-202.1) | 7.34x | 28 MiB | 51 MiB |
| `append` | append a tenth of the rows to a copy of the file | 8.8 (8.7-10.0) | 187.7 (186.4-195.2) | not asked | n/a | 26 MiB | not asked |

## 10,485,760 rows, 31,886,396 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 0.2 (0.2-0.3) | 12.1 (11.6-12.9) | 0.6 (0.4-0.8) | 3.23x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 23.5 (22.8-31.0) | 90.6 (88.0-91.8) | 94.5 (93.9-96.8) | 4.02x | 45 MiB | 41 MiB |
| `project` | read one column of four | 1.2 (1.1-8.5) | 45.7 (42.4-48.0) | 2.0 (1.9-3.6) | 1.59x | 11 MiB | 12 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 1.3 (1.3-1.6) | 79.0 (76.4-83.8) | 3.2 (2.9-5.6) | 2.42x | 12 MiB | 19 MiB |
| `filter-wide` | read the rows of a band holding about half | 13.1 (12.8-14.1) | 91.7 (90.0-93.4) | 50.1 (50.0-50.7) | 3.84x | 36 MiB | 37 MiB |
| `take` | take a thousand rows spread across the file | 16.9 (16.6-23.7) | 85.6 (84.4-87.4) | 70.0 (69.3-71.5) | 4.13x | 35 MiB | 39 MiB |
| `write` | read the file and encode it back out | 250.6 (250.0-252.4) | 519.8 (516.7-531.6) | 1972.5 (1967.6-2489.2) | 7.87x | 57 MiB | 217 MiB |
| `append` | append a tenth of the rows to a copy of the file | 46.4 (45.7-47.7) | 295.4 (282.3-323.4) | not asked | n/a | 37 MiB | not asked |

## Reading it

**What the table leaves out.** Starting the process, up to the point where it begins
its action, costs 7 ms for the native build and 7 ms for the reference on this machine, most of it
the operating system starting any binary at all.
The JIT build's start is 34 ms, the managed runtime coming up; what its
column then shows is the action compiling its own code as it runs, which is why an
`open` that takes the native build a fraction of a millisecond takes it tens.

**Where we stand.** Of 14 compared scenarios, the best is `write`
at 10,485,760 rows (7.87x) and the worst is `project` at 10,485,760 rows
(1.59x). A ratio above 1.00x means the native build's action took less time
than the reference's.

**Memory.** On the full scan at 10,485,760 rows our peak is 45 MiB against 41 MiB. We decode a chunk in windows of
131,072 rows where its encoding can decode a range of its rows, with a
dictionary's values decoded once for every window of its chunk, and whole where it cannot,
a compressed blob among them, or where its decode is only views onto the segment; the
reference decodes a split of at most a hundred thousand rows at a time. Every column of this
file is read in windows or as views, so the difference lies in what each reader holds at
once, its windows, the dictionaries' values and the segments read ahead, rather than in a
chunk held whole.
The reference's own worst is `write` at 10,485,760 rows, 217 MiB against our 57.

**Where there is nothing to compare against.** `append`: the reference shim
exposes no such entry point, so the figure is ours alone and is not a ratio. Those
cells read `not asked`, which is not the same claim as `refused`.

## What this does not measure

* **Steady state.** Every figure is a first and only action in a fresh process: a page cache
  warmed only by the discarded run before it, thread pools starting, and in the JIT column
  the code compiling as it runs. The per-encoding ratios in `bench/README.md` measure the
  other thing — the same code after warm-up, in one process — and they are the place to
  look for what a decoder costs.
* **Threading.** Both sides are single-threaded here, which is what makes a ratio a ratio.
* **Your data.** One table of four columns, written by us, is not every file. A column the
  compressor likes less, or a filter a zone map cannot prune, moves these numbers more
  than any implementation detail does.
* **Your machine.** These figures belong to the one named above.
