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
commit: 6e7cb0c
date: 2026-09-20 18:48 UTC

## 1,000,000 rows, 3,060,049 bytes

| scenario | what it does | ours, ms | Vortex Rust, ms | ratio | ours, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 49 (47-51) | 14 (13-16) | 0.28x | 47 MiB | 9 MiB |
| `scan` | read every column of every row | 91 (90-93) | 25 (24-26) | 0.28x | 73 MiB | 20 MiB |
| `project` | read one column of four | 78 (75-83) | 16 (16-17) | 0.21x | 54 MiB | 11 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 114 (108-119) | 17 (16-19) | 0.15x | 58 MiB | 15 MiB |
| `filter-wide` | read the rows of a band holding about half | 121 (114-130) | 23 (21-26) | 0.19x | 74 MiB | 21 MiB |
| `take` | take a thousand rows spread across the file | 173 (170-174) | 24 (23-25) | 0.14x | 56 MiB | 16 MiB |
| `write` | read the file and encode it back out | 225 (222-227) | 209 (208-211) | 0.93x | 84 MiB | 47 MiB |
| `append` | append a tenth of the rows to a copy of the file | 224 (216-228) | refused | n/a | 85 MiB | refused |

## 10,000,000 rows, 30,536,900 bytes

| scenario | what it does | ours, ms | Vortex Rust, ms | ratio | ours, peak | Rust, peak |
|---|---|---|---|---|---|---|
| `open` | open the file and read no rows | 51 (51-63) | 13 (13-15) | 0.25x | 47 MiB | 9 MiB |
| `scan` | read every column of every row | 120 (118-130) | 110 (108-112) | 0.92x | 98 MiB | 39 MiB |
| `project` | read one column of four | 84 (78-99) | 15 (14-16) | 0.18x | 55 MiB | 12 MiB |
| `filter-narrow` | read the rows of a band holding about one in a hundred | 123 (120-133) | 19 (19-19) | 0.16x | 59 MiB | 19 MiB |
| `filter-wide` | read the rows of a band holding about half | 149 (145-157) | 68 (65-73) | 0.46x | 87 MiB | 36 MiB |
| `take` | take a thousand rows spread across the file | 727 (721-727) | 89 (86-89) | 0.12x | 84 MiB | 36 MiB |
| `write` | read the file and encode it back out | 634 (622-643) | refused | n/a | 118 MiB | refused |
| `append` | append a tenth of the rows to a copy of the file | 298 (291-308) | refused | n/a | 91 MiB | refused |

## Reading it

**Start with the floor.** The `open` row is a process that opens the file and reads
no rows: 49 ms for us against 14 ms. That difference is a managed runtime
starting, and it is the same whatever the file holds. Subtract it from every other row
to see what the work cost — and remember that a long-running process pays it once,
while this table pays it on every line.

**Where we stand.** Of 13 compared scenarios, the closest is `write`
at 1,000,000 rows (0.93x) and the furthest is `take` at 10,000,000 rows
(0.12x). A ratio above 1.00x would mean we took less wall time.

**Memory.** Our worst peak here is 98 MiB against 47 MiB. A managed heap and
its runtime are most of that difference at these sizes.

**Where the reference refused.** No figure for it on `write` at 10,000,000 rows.
The harness records the refusal rather than dropping the row: a table that shows only
what worked is not a comparison.

**Where there is nothing to compare against.** `append`: the reference shim
exposes no such entry point, so the figure is ours alone and is not a ratio.

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
