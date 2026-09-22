# Benchmarks

What this library costs against the Rust implementation, on scenarios a caller
would recognise. **This page is generated. Do not edit it** — every figure comes from
`dotnet run -c Release --project bench/Vorticity.Benchmarks -- --report --markdown`,
and a hand-written number here would be a number nothing re-measures.

## What is measured

Eight scenarios, at a million rows and at ten million, on a table of four columns: a
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
commit: f6b04a1
date: 2026-09-22 11:40 UTC

## 1,000,000 rows, 3,060,049 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 16 (15-18) | 53 (51-54) | 14 (14-16) | 0.87x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 22 (21-22) | 103 (95-118) | 27 (26-27) | 1.23x | 50 MiB | 20 MiB |
| `project` | read one column of four | 17 (15-18) | 83 (81-84) | 16 (15-18) | 0.95x | 18 MiB | 11 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 17 (16-18) | 124 (118-176) | 18 (17-20) | 1.05x | 16 MiB | 15 MiB |
| `filter-wide` | read the rows of a band holding about half | 23 (22-26) | 131 (126-140) | 25 (24-27) | 1.09x | 32 MiB | 21 MiB |
| `take` | take a thousand rows spread across the file | 19 (19-22) | 100 (99-111) | 25 (25-26) | 1.29x | 15 MiB | 16 MiB |
| `write` | read the file and encode it back out | 60 (60-64) | 245 (239-255) | 216 (202-222) | 3.60x | 56 MiB | 52 MiB |
| `append` | append a tenth of the rows to a copy of the file | 28 (27-34) | 248 (236-258) | not asked | n/a | 37 MiB | not asked |

## 10,000,000 rows, 30,536,900 bytes

| scenario | what it does | ours AOT, ms | ours JIT, ms | Vortex Rust, ms | ratio | ours AOT, peak | Rust, peak |
|---|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 17 (17-23) | 52 (50-56) | 14 (13-16) | 0.81x | 9 MiB | 9 MiB |
| `scan` | read every column of every row | 43 (42-45) | 130 (127-136) | 111 (101-116) | 2.59x | 75 MiB | 39 MiB |
| `project` | read one column of four | 22 (21-22) | 84 (83-86) | 16 (15-17) | 0.72x | 18 MiB | 12 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 19 (19-20) | 119 (115-123) | 20 (19-21) | 1.07x | 12 MiB | 19 MiB |
| `filter-wide` | read the rows of a band holding about half | 37 (37-39) | 158 (149-165) | 71 (68-74) | 1.93x | 62 MiB | 36 MiB |
| `take` | take a thousand rows spread across the file | 36 (35-40) | 125 (119-131) | 84 (77-92) | 2.31x | 37 MiB | 36 MiB |
| `write` | read the file and encode it back out | 390 (388-393) | 699 (691-726) | refused | n/a | 88 MiB | refused |
| `append` | append a tenth of the rows to a copy of the file | 80 (79-92) | 329 (325-340) | not asked | n/a | 64 MiB | not asked |

## Reading it

**Start with the floor.** The `open` row is a process that opens the file and reads
no rows: 16 ms for the native build against 14 ms. Both are a native
binary starting, and a process that does nothing at all costs about as much on this
machine. Subtract it from every other row to see what the work cost.
The JIT column's floor is 53 ms: the managed runtime starting and compiling
the code an open touches, whatever the file holds. Every row of that column carries it,
plus the compilation of whatever else the scenario touches, and a long-running process
pays it once.

**Where we stand.** Of 13 compared scenarios, the best is `write`
at 1,000,000 rows (3.60x) and the worst is `project` at 10,000,000 rows
(0.72x). A ratio above 1.00x means the native build took less wall time
than the reference.

**Memory.** Our worst peak here is 75 MiB against 52 MiB. We decode a chunk whole and
hand each batch a window of it, where the reference decodes a split of at most a hundred
thousand rows at a time; on a file whose chunks hold half a million rows, that is the
difference, and it is the file's chunking rather than its size that sets it.

**Where the reference refused.** No figure for it on `write` at 10,000,000 rows.
It said: *Other error: append_to_builder for Zstd requires a variable-binary builder*.
The harness records the refusal rather than dropping the row: a table that shows only
what worked is not a comparison. Where the reason is a `vortex.zstd` array over a
numeric column, the shape is one the reference itself builds — `Zstd::from_primitive`
is public API and its own conformance corpus ships a `vortex.zstd` over an `i64?`. It
reads such a file everywhere; what it cannot do is append one to a builder, its Zstd
array replacing the canonicalize-then-append fallback with a path that takes
variable-binary builders only. So the gap is in re-encoding, not in reading, and it is
upstream rather than in the bytes we wrote.

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
