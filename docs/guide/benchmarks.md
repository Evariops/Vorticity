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
commit: e776f7b
date: 2026-09-20 21:45 UTC

## 1,000,000 rows, 3,060,049 bytes

| scenario | what it does | ours, ms | Vortex Rust, ms | ratio | ours, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 52 (49-54) | 13 (13-15) | 0.25x | 47 MiB | 9 MiB |
| `scan` | read every column of every row | 91 (88-92) | 26 (25-30) | 0.28x | 73 MiB | 19 MiB |
| `project` | read one column of four | 78 (78-91) | 16 (15-18) | 0.20x | 54 MiB | 11 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 114 (110-122) | 18 (16-22) | 0.16x | 58 MiB | 15 MiB |
| `filter-wide` | read the rows of a band holding about half | 121 (113-121) | 23 (22-28) | 0.19x | 73 MiB | 21 MiB |
| `take` | take a thousand rows spread across the file | 92 (89-102) | 24 (23-26) | 0.26x | 56 MiB | 16 MiB |
| `write` | read the file and encode it back out | 226 (218-231) | 212 (210-213) | 0.93x | 84 MiB | 50 MiB |
| `append` | append a tenth of the rows to a copy of the file | 222 (219-227) | not asked | n/a | 85 MiB | not asked |

## 10,000,000 rows, 30,536,900 bytes

| scenario | what it does | ours, ms | Vortex Rust, ms | ratio | ours, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 53 (47-67) | 15 (13-17) | 0.27x | 47 MiB | 9 MiB |
| `scan` | read every column of every row | 120 (118-127) | 113 (112-119) | 0.94x | 98 MiB | 39 MiB |
| `project` | read one column of four | 83 (80-91) | 18 (15-19) | 0.22x | 55 MiB | 12 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 116 (113-124) | 21 (20-24) | 0.18x | 59 MiB | 19 MiB |
| `filter-wide` | read the rows of a band holding about half | 157 (148-161) | 67 (63-71) | 0.42x | 86 MiB | 36 MiB |
| `take` | take a thousand rows spread across the file | 121 (113-126) | 87 (79-91) | 0.72x | 78 MiB | 40 MiB |
| `write` | read the file and encode it back out | 616 (600-646) | refused | n/a | 118 MiB | refused |
| `append` | append a tenth of the rows to a copy of the file | 304 (298-311) | not asked | n/a | 91 MiB | not asked |

## Reading it

**Start with the floor.** The `open` row is a process that opens the file and reads
no rows: 52 ms for us against 13 ms. That difference is a managed runtime
starting, and it is the same whatever the file holds. Subtract it from every other row
to see what the work cost — and remember that a long-running process pays it once,
while this table pays it on every line.

**Where we stand.** Of 13 compared scenarios, the closest is `scan`
at 10,000,000 rows (0.94x) and the furthest is `filter-narrow` at 1,000,000 rows
(0.16x). A ratio above 1.00x would mean we took less wall time.

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
