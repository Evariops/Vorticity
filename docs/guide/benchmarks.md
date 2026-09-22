# Benchmarks

What this library costs against the Rust implementation, on scenarios a caller
would recognise. **This page is generated. Do not edit it** — every figure comes from
`dotnet run -c Release --project bench/Vorticity.Benchmarks -- --report --markdown`,
and a hand-written number here would be a number nothing re-measures.

## What is measured

Eight scenarios, at 2^20 rows and at ten times that, on a table of four columns: a
monotone `i64`, an `f64`, a short `utf8` and a nullable `bool`. **Each side runs in its
own process**, once per run, and the run is timed from outside — so what you see is
what a command costs, including starting, opening the file and exiting.

Our side runs twice. **AOT** is the same code published as Native AOT: a native binary,
like the reference's, and the one the ratio is taken against, because it is the only
pairing where both processes pay the same fixed costs. **JIT** is the framework-dependent
build under `dotnet`, which starts the runtime and compiles the code as it goes: what a
`dotnet run` costs, and what a long-running process pays once.

Peak resident memory and processor time are each side's own `getrusage`, and the wall
clock is the parent's. Both sides render the same rows, and the harness fails rather
than print a ratio between two different answers.

The reference does the **same work**, which took care: its scan has one entry point
that counts rows off an encoded array's metadata and one that materialises every
column. Only the second is comparable to a reader that has no other representation,
and it is the one measured here.

5 runs of each scenario, each in its own process, the median reported with the lowest and highest beside it; one discarded run before them.
Ratio is the reference's wall time over our Native AOT build's: above 1.00x, we took less.
machine: Apple M4 Pro (Arm64), 14 processors, macOS 26.7.0
runtime: .NET 11.0.0-rc.1.26425.128, as Native AOT and on the JIT; reference: Vortex 0.86.1, cargo release with lto
commit: 5a73688
date: 2026-09-22 11:51 UTC

## 1,048,576 rows, 3,191,324 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 18 (17-18) | 59 (56-62) | 16 (15-17) | 0.90x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 23 (21-24) | 99 (98-102) | 26 (26-28) | 1.14x | 52 MiB | 21 MiB |
| `project` | read one column of four | 19 (18-20) | 88 (85-90) | 16 (16-17) | 0.87x | 18 MiB | 11 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 19 (19-38) | 133 (130-142) | 19 (17-19) | 0.98x | 16 MiB | 15 MiB |
| `filter-wide` | read the rows of a band holding about half | 21 (20-21) | 126 (118-143) | 23 (22-24) | 1.12x | 31 MiB | 17 MiB |
| `take` | take a thousand rows spread across the file | 20 (19-20) | 102 (100-103) | 24 (24-25) | 1.21x | 15 MiB | 17 MiB |
| `write` | read the file and encode it back out | 60 (59-69) | 260 (255-287) | 229 (214-230) | 3.80x | 58 MiB | 53 MiB |
| `append` | append a tenth of the rows to a copy of the file | 31 (29-35) | 252 (246-256) | not asked | n/a | 43 MiB | not asked |

## 10,485,760 rows, 31,886,396 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 17 (17-21) | 58 (56-58) | 15 (15-16) | 0.91x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 46 (46-50) | 138 (135-150) | 125 (122-130) | 2.73x | 78 MiB | 45 MiB |
| `project` | read one column of four | 20 (19-27) | 92 (89-95) | 18 (17-19) | 0.89x | 18 MiB | 12 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 20 (19-22) | 126 (122-135) | 20 (20-21) | 1.04x | 12 MiB | 19 MiB |
| `filter-wide` | read the rows of a band holding about half | 40 (39-44) | 160 (156-164) | 81 (76-82) | 2.03x | 64 MiB | 41 MiB |
| `take` | take a thousand rows spread across the file | 38 (36-42) | 128 (126-134) | 100 (97-102) | 2.60x | 37 MiB | 37 MiB |
| `write` | read the file and encode it back out | 408 (399-415) | 750 (688-760) | 1967 (1960-1978) | 4.82x | 92 MiB | 216 MiB |
| `append` | append a tenth of the rows to a copy of the file | 81 (76-90) | 334 (323-339) | not asked | n/a | 67 MiB | not asked |

## Reading it

**Start with the floor.** The `open` row is a process that opens the file and reads
no rows: 18 ms for the native build against 16 ms. Both are a native
binary starting, and a process that does nothing at all costs about as much on this
machine. Subtract it from every other row to see what the work cost.
The JIT column's floor is 59 ms: the managed runtime starting and compiling
the code an open touches, whatever the file holds. Every row of that column carries it,
plus the compilation of whatever else the scenario touches, and a long-running process
pays it once.

**Where we stand.** Of 14 compared scenarios, the best is `write`
at 10,485,760 rows (4.82x) and the worst is `project` at 1,048,576 rows
(0.87x). A ratio above 1.00x means the native build took less wall time
than the reference.

**Memory.** Our worst peak here is 92 MiB against 216 MiB. We decode a chunk whole and
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
