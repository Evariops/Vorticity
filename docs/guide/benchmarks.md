# Benchmarks

What this library costs against the Rust implementation, on scenarios a caller
would recognise. **This page is generated. Do not edit it** — every figure comes from
`dotnet run -c Release --project bench/Vorticity.Benchmarks -- --report --markdown`,
and a hand-written number here would be a number nothing re-measures.

## What is measured

Eight scenarios, at a million rows and at ten million, on a table of four columns: a
monotone `i64`, an `f64`, a short `utf8` and a nullable `bool`. **Each side runs in its
own process**, once per run, and the run is timed from outside — so what you see is
what a command costs, including starting a runtime, opening the file and exiting.

Peak resident memory and processor time are each side's own `getrusage`, and the wall
clock is the parent's. Both sides render the same rows, and the harness fails rather
than print a ratio between two different answers.

The reference does the **same work**, which took care: its scan has one entry point
that counts rows off an encoded array's metadata and one that materialises every
column. Only the second is comparable to a reader that has no other representation,
and it is the one measured here.

5 runs of each scenario, each in its own process, the median reported with the lowest and highest beside it; one discarded run before them.
Ratio above 1.00x means this library took less wall time.
machine: Apple M4 Pro (Arm64), 14 processors, macOS 26.7.0
runtime: .NET 11.0.0-rc.1.26425.128, reference: Vortex 0.86.1
commit: 3f3d27f
date: 2026-09-20 21:08 UTC

## 1,000,000 rows, 3,060,049 bytes

| scenario | what it does | ours, ms | Vortex Rust, ms | ratio | ours, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 50 (47-53) | 14 (14-19) | 0.29x | 47 MiB | 9 MiB |
| `scan` | read every column of every row | 95 (88-96) | 25 (25-28) | 0.27x | 74 MiB | 20 MiB |
| `project` | read one column of four | 77 (76-78) | 15 (14-17) | 0.19x | 54 MiB | 11 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 112 (110-114) | 18 (17-19) | 0.16x | 58 MiB | 15 MiB |
| `filter-wide` | read the rows of a band holding about half | 117 (116-121) | 23 (21-27) | 0.20x | 74 MiB | 21 MiB |
| `take` | take a thousand rows spread across the file | 170 (165-176) | 23 (21-24) | 0.13x | 56 MiB | 16 MiB |
| `write` | read the file and encode it back out | 223 (220-230) | 209 (206-211) | 0.94x | 84 MiB | 50 MiB |
| `append` | append a tenth of the rows to a copy of the file | 219 (211-220) | not asked | n/a | 85 MiB | not asked |

## 10,000,000 rows, 30,536,900 bytes

| scenario | what it does | ours, ms | Vortex Rust, ms | ratio | ours, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 53 (49-59) | 13 (13-15) | 0.25x | 47 MiB | 9 MiB |
| `scan` | read every column of every row | 121 (119-132) | 110 (104-111) | 0.91x | 98 MiB | 36 MiB |
| `project` | read one column of four | 80 (78-85) | 16 (16-19) | 0.20x | 55 MiB | 12 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 128 (123-130) | 19 (18-19) | 0.15x | 59 MiB | 19 MiB |
| `filter-wide` | read the rows of a band holding about half | 148 (146-157) | 64 (63-67) | 0.43x | 87 MiB | 36 MiB |
| `take` | take a thousand rows spread across the file | 724 (721-727) | 88 (86-90) | 0.12x | 85 MiB | 36 MiB |
| `write` | read the file and encode it back out | 638 (570-650) | refused | n/a | 118 MiB | refused |
| `append` | append a tenth of the rows to a copy of the file | 293 (289-298) | not asked | n/a | 91 MiB | not asked |

## Reading it

**Start with the floor.** The `open` row is a process that opens the file and reads
no rows: 50 ms for us against 14 ms. That difference is a managed runtime
starting, and it is the same whatever the file holds. Subtract it from every other row
to see what the work cost — and remember that a long-running process pays it once,
while this table pays it on every line.

**Where we stand.** Of 13 compared scenarios, the closest is `write`
at 1,000,000 rows (0.94x) and the furthest is `take` at 10,000,000 rows
(0.12x). A ratio above 1.00x would mean we took less wall time.

**Memory.** Our worst peak here is 98 MiB against 50 MiB. A managed heap and
its runtime are most of that difference at these sizes.

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

* **Steady state.** Every row includes a cold start: the runtime, the first tier of the
  just-in-time compiler, and a page cache warmed only by the discarded run before it. The
  per-encoding ratios in `bench/README.md` measure the other thing — the same code after
  warm-up, in one process — and they read very differently. Both are true.
* **Threading.** Both sides are single-threaded here, which is what makes a ratio a ratio.
* **Your data.** One table of four columns, written by us, is not every file. A column the
  compressor likes less, or a filter a zone map cannot prune, moves these numbers more
  than any implementation detail does.
* **Your machine.** These figures belong to the one named above.
