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
commit: 9a72334
date: 2026-09-22 12:38 UTC

## 1,048,576 rows, 3,191,324 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 0.2 (0.2-0.2) | 12.4 (12.3-13.3) | 0.5 (0.4-0.7) | 2.82x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 5.0 (4.7-5.3) | 59.7 (57.7-60.7) | 10.7 (10.4-11.0) | 2.11x | 52 MiB | 21 MiB |
| `project` | read one column of four | 0.8 (0.8-0.9) | 42.9 (41.3-43.2) | 0.9 (0.8-0.9) | 1.04x | 18 MiB | 11 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 1.6 (1.5-1.8) | 88.7 (81.9-93.3) | 1.9 (1.8-2.1) | 1.22x | 16 MiB | 15 MiB |
| `filter-wide` | read the rows of a band holding about half | 3.2 (3.0-3.4) | 75.6 (71.9-96.9) | 6.2 (6.1-6.8) | 1.93x | 31 MiB | 17 MiB |
| `take` | take a thousand rows spread across the file | 2.2 (2.1-2.3) | 55.5 (53.1-60.1) | 8.4 (7.9-8.7) | 3.78x | 15 MiB | 16 MiB |
| `write` | read the file and encode it back out | 42.6 (42.0-43.2) | 189.7 (186.1-195.1) | 199.5 (196.5-205.0) | 4.68x | 58 MiB | 56 MiB |
| `append` | append a tenth of the rows to a copy of the file | 11.4 (10.8-12.1) | 180.5 (174.9-184.3) | not asked | n/a | 43 MiB | not asked |

## 10,485,760 rows, 31,886,396 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 0.2 (0.2-0.3) | 13.6 (11.9-14.5) | 0.5 (0.5-0.5) | 2.30x | 10 MiB | 9 MiB |
| `scan` | read every column of every row | 26.5 (25.9-28.9) | 90.3 (86.4-95.1) | 95.7 (95.1-97.0) | 3.61x | 78 MiB | 42 MiB |
| `project` | read one column of four | 2.3 (2.0-2.4) | 44.7 (44.1-46.3) | 1.8 (1.7-2.0) | 0.77x | 18 MiB | 12 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 3.0 (3.0-4.0) | 79.2 (76.9-82.3) | 3.4 (3.3-3.5) | 1.12x | 12 MiB | 19 MiB |
| `filter-wide` | read the rows of a band holding about half | 19.1 (18.9-19.4) | 106.7 (104.3-161.5) | 50.7 (50.5-51.9) | 2.65x | 64 MiB | 38 MiB |
| `take` | take a thousand rows spread across the file | 17.5 (17.1-20.5) | 79.5 (78.4-85.0) | 70.6 (70.5-72.8) | 4.04x | 37 MiB | 37 MiB |
| `write` | read the file and encode it back out | 383.5 (377.3-387.3) | 686.9 (647.2-755.6) | 1956.9 (1947.7-1980.9) | 5.10x | 92 MiB | 221 MiB |
| `append` | append a tenth of the rows to a copy of the file | 60.6 (59.1-60.7) | 279.5 (272.3-285.1) | not asked | n/a | 67 MiB | not asked |

## Reading it

**What the table leaves out.** Starting the process, up to the point where it begins
its action, costs 16 ms for the native build and 19 ms for the reference on this machine, most of it
the operating system starting any binary at all.
The JIT build's start is 41 ms, the managed runtime coming up; what its
column then shows is the action compiling its own code as it runs, which is why an
`open` that takes the native build a fraction of a millisecond takes it tens.

**Where we stand.** Of 14 compared scenarios, the best is `write`
at 10,485,760 rows (5.10x) and the worst is `project` at 10,485,760 rows
(0.77x). A ratio above 1.00x means the native build's action took less time
than the reference's.

**Memory.** Our worst peak here is 92 MiB against 221 MiB. We decode a chunk whole and
hand each batch a window of it, where the reference decodes a split of at most a hundred
thousand rows at a time; on a file whose chunks hold half a million rows, that is the
difference, and it is the file's chunking rather than its size that sets it.

**Where there is nothing to compare against.** `append`: the reference shim
exposes no such entry point, so the figure is ours alone and is not a ratio. Those
cells read `not asked`, which is not the same claim as `refused`.

## What this does not measure

* **Steady state.** Every row includes a cold start: the process, a page cache warmed only
  by the discarded run before it, and in the JIT column the runtime and the first tier of
  the just-in-time compiler. The per-encoding ratios in `bench/README.md` measure the other
  thing — the same code after warm-up, in one process — and they are the place to look for
  what a decoder costs.
* **Threading.** Both sides are single-threaded here, which is what makes a ratio a ratio.
* **Your data.** One table of four columns, written by us, is not every file. A column the
  compressor likes less, or a filter a zone map cannot prune, moves these numbers more
  than any implementation detail does.
* **Your machine.** These figures belong to the one named above.
