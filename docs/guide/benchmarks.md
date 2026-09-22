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
commit: 6a43c16
date: 2026-09-22 15:46 UTC

## 1,048,576 rows, 3,191,324 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 0.2 (0.2-0.2) | 12.2 (11.8-13.8) | 0.5 (0.4-0.5) | 3.03x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 3.6 (3.5-4.0) | 55.6 (54.9-56.5) | 10.4 (10.2-11.2) | 2.87x | 24 MiB | 19 MiB |
| `project` | read one column of four | 0.5 (0.5-0.6) | 40.6 (39.4-44.1) | 0.8 (0.8-0.8) | 1.61x | 12 MiB | 11 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 1.3 (1.3-1.6) | 78.0 (76.3-78.1) | 1.9 (1.8-2.0) | 1.43x | 12 MiB | 15 MiB |
| `filter-wide` | read the rows of a band holding about half | 2.8 (2.7-3.0) | 70.3 (68.4-72.3) | 6.4 (6.1-6.5) | 2.29x | 22 MiB | 17 MiB |
| `take` | take a thousand rows spread across the file | 2.4 (2.2-2.4) | 51.9 (51.5-53.5) | 8.5 (8.4-9.4) | 3.57x | 15 MiB | 16 MiB |
| `write` | read the file and encode it back out | 38.8 (38.1-39.5) | 182.6 (181.1-191.0) | 193.3 (192.0-197.0) | 4.98x | 30 MiB | 49 MiB |
| `append` | append a tenth of the rows to a copy of the file | 10.3 (9.6-11.0) | 179.4 (175.1-181.0) | not asked | n/a | 27 MiB | not asked |

## 10,485,760 rows, 31,886,396 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 0.2 (0.2-0.7) | 12.1 (11.6-13.1) | 0.5 (0.4-0.6) | 2.73x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 24.5 (24.1-25.5) | 87.8 (85.9-98.1) | 100.6 (94.5-115.2) | 4.11x | 50 MiB | 42 MiB |
| `project` | read one column of four | 1.8 (1.7-3.8) | 43.1 (42.7-44.9) | 1.7 (1.6-1.7) | 0.94x | 12 MiB | 12 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 3.0 (3.0-3.2) | 76.2 (74.3-76.4) | 3.3 (3.2-3.7) | 1.09x | 12 MiB | 19 MiB |
| `filter-wide` | read the rows of a band holding about half | 18.8 (17.5-20.0) | 101.7 (100.1-102.5) | 50.1 (49.8-52.5) | 2.67x | 36 MiB | 39 MiB |
| `take` | take a thousand rows spread across the file | 17.3 (17.1-17.6) | 77.5 (77.0-78.8) | 69.4 (69.1-70.0) | 4.02x | 37 MiB | 36 MiB |
| `write` | read the file and encode it back out | 369.7 (367.4-372.0) | 698.8 (681.3-704.2) | 1897.5 (1896.0-1903.3) | 5.13x | 65 MiB | 215 MiB |
| `append` | append a tenth of the rows to a copy of the file | 57.6 (55.8-63.1) | 261.6 (261.3-262.2) | not asked | n/a | 38 MiB | not asked |

## Reading it

**What the table leaves out.** Starting the process, up to the point where it begins
its action, costs 16 ms for the native build and 14 ms for the reference on this machine, most of it
the operating system starting any binary at all.
The JIT build's start is 41 ms, the managed runtime coming up; what its
column then shows is the action compiling its own code as it runs, which is why an
`open` that takes the native build a fraction of a millisecond takes it tens.

**Where we stand.** Of 14 compared scenarios, the best is `write`
at 10,485,760 rows (5.13x) and the worst is `project` at 10,485,760 rows
(0.94x). A ratio above 1.00x means the native build's action took less time
than the reference's.

**Memory.** On the full scan at 10,485,760 rows our peak is 50 MiB against 42 MiB. We decode a chunk in windows of
131,072 rows where its encoding can decode a range of its rows, with a
dictionary's values decoded once for every window of its chunk, and whole where it cannot,
a compressed blob among them, or where its decode is only views onto the segment; the
reference decodes a split of at most a hundred thousand rows at a time. Every column of this
file is read in windows or as views, so the difference lies in what each reader holds at
once, its windows, the dictionaries' values and the segments read ahead, rather than in a
chunk held whole.
The reference's own worst is `write` at 10,485,760 rows, 215 MiB against our 65.

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
