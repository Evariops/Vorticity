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
commit: e74780a
date: 2026-09-22 13:32 UTC

## 1,048,576 rows, 3,191,324 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 0.2 (0.1-0.2) | 12.9 (12.1-15.0) | 0.4 (0.4-0.5) | 2.56x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 3.8 (3.8-4.0) | 61.4 (55.7-71.7) | 10.8 (10.5-11.3) | 2.82x | 30 MiB | 21 MiB |
| `project` | read one column of four | 0.4 (0.4-0.5) | 41.4 (40.4-48.3) | 0.8 (0.7-0.8) | 1.70x | 11 MiB | 11 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 1.4 (1.3-1.7) | 81.2 (79.2-84.7) | 1.9 (1.8-2.0) | 1.33x | 13 MiB | 15 MiB |
| `filter-wide` | read the rows of a band holding about half | 2.7 (2.5-3.0) | 72.7 (71.8-81.2) | 6.6 (6.2-6.8) | 2.45x | 22 MiB | 17 MiB |
| `take` | take a thousand rows spread across the file | 2.6 (2.5-2.7) | 63.9 (61.4-65.5) | 8.4 (8.3-8.7) | 3.22x | 15 MiB | 17 MiB |
| `write` | read the file and encode it back out | 41.4 (40.3-42.5) | 207.2 (199.3-208.3) | 200.9 (200.6-226.2) | 4.86x | 36 MiB | 53 MiB |
| `append` | append a tenth of the rows to a copy of the file | 11.1 (10.8-11.9) | 190.4 (188.7-193.1) | not asked | n/a | 33 MiB | not asked |

## 10,485,760 rows, 31,886,396 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 0.3 (0.2-0.3) | 14.6 (13.7-15.3) | 0.6 (0.6-0.6) | 2.20x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 26.2 (25.4-27.9) | 96.5 (92.7-101.8) | 98.6 (97.4-101.1) | 3.76x | 57 MiB | 43 MiB |
| `project` | read one column of four | 2.2 (1.9-2.2) | 48.9 (47.9-51.9) | 2.1 (1.9-2.2) | 0.99x | 11 MiB | 12 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 3.4 (3.3-3.5) | 85.0 (83.2-86.3) | 3.6 (3.6-3.7) | 1.06x | 12 MiB | 19 MiB |
| `filter-wide` | read the rows of a band holding about half | 18.6 (18.3-20.1) | 110.5 (103.5-113.2) | 50.7 (50.1-50.9) | 2.73x | 43 MiB | 37 MiB |
| `take` | take a thousand rows spread across the file | 17.7 (17.6-18.3) | 80.2 (78.9-82.9) | 71.1 (70.7-72.2) | 4.01x | 37 MiB | 37 MiB |
| `write` | read the file and encode it back out | 375.8 (374.8-380.6) | 704.6 (641.0-709.0) | 1978.9 (1963.8-1992.3) | 5.27x | 71 MiB | 213 MiB |
| `append` | append a tenth of the rows to a copy of the file | 57.0 (56.3-60.9) | 272.9 (271.2-277.1) | not asked | n/a | 45 MiB | not asked |

## Reading it

**What the table leaves out.** Starting the process, up to the point where it begins
its action, costs 15 ms for the native build and 13 ms for the reference on this machine, most of it
the operating system starting any binary at all.
The JIT build's start is 42 ms, the managed runtime coming up; what its
column then shows is the action compiling its own code as it runs, which is why an
`open` that takes the native build a fraction of a millisecond takes it tens.

**Where we stand.** Of 14 compared scenarios, the best is `write`
at 10,485,760 rows (5.27x) and the worst is `project` at 10,485,760 rows
(0.99x). A ratio above 1.00x means the native build's action took less time
than the reference's.

**Memory.** On the full scan at 10,485,760 rows our peak is 57 MiB against 43 MiB. We decode a chunk in windows of
65 536 rows where its encoding can decode a range of its rows, and whole where it cannot,
a dictionary of many values or a compressed blob among them; the reference decodes a split
of at most a hundred thousand rows at a time. On a file whose chunks hold half a million
rows, the chunks held whole are the difference, and it is the file's encodings and
chunking rather than its size that set it.
The reference's own worst is `write` at 10,485,760 rows, 213 MiB against our 71.

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
