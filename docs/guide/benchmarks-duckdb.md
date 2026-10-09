# Benchmarks against DuckDB

Our group bys against DuckDB's, on the same files: DuckDB reads them through its Vortex extension,
and again from a table of its own it loaded from them first. Both sides answer the same query, and
the answers are compared. [benchmarks.md](benchmarks.md) compares Vorticity with Vortex's Rust
implementation; this page has a reference of its own, so that neither blurs the other.

Vortex™ is a trademark of LF Projects, LLC. DuckDB is a trademark of the DuckDB Foundation.
Vorticity is an independent implementation, not affiliated with or endorsed by the Vortex project,
LF Projects, LLC, DuckDB or the DuckDB Foundation.

* **The two sides.** Ours: the bench's runner (`bench/Vorticity.Benchmarks.Runner`) compiled with
  Native AOT, a process a block of runs. DuckDB 1.5.2 (`8a5851971f`) and its `vortex` extension
  (`6ea8bd7`), a process a file kept open for the whole session, at the same number of threads
  (`SET threads`).
* **Two bases.** Each query is timed twice. Warm: our runner with its file kept mapped, as a session
  keeps its 64 most recent files by default, against DuckDB's own table, loaded from the file before
  anything is timed. Cold: our runner mapping the file anew at every run against DuckDB's Vortex
  reader, which reads the file at every query. A table already in memory against a file mapped anew
  would compare page faults rather than engines.
* **The same work on both sides.** Each query is a group by whose result is aggregated once more:
  DuckDB's outer query sums every column the group by computes, and the runner sums the same columns
  as it reads the groups. The two sums are the answer, and every row of the tables below gave the
  same one on both sides, to a part in 10⁹ (a float's sum depends on its order).
* **The files** are the bench's own, written by its generators, canonical, at edition `core2025.10`,
  the one the extension reads (it does not know `vortex.zoned`); both sides read the same file, read
  once before anything is timed so that its pages are in the cache. 40 million rows of keys at random
  of 10³, 10⁶ and 10⁷ values, and the same number of keys spread by a stride, which no span bounds and
  every table hashes. And db-benchmark's group by at 10⁷ rows, the benchmark H2O.ai started and DuckDB
  Labs has kept since 2023: its data drawn by the laws of its `groupby-datagen.R` from the bench's own
  stream, not R's, and the queries our native aggregates cover, q1 to q5, q7 and q10. q6 (a median),
  q8 (the two largest of each group) and q9 (a correlation) are not covered, and not run.
* **In turns, in pairs.** A block of DuckDB's runs and a process of ours alternate, DuckDB then us, us
  then DuckDB (ABBA), so that a drift of the machine weighs on both. A block is 10 runs of which the
  first 3 are dropped, or 5 and 2 once the query passes 100 ms; a side's figure is the median of its
  runs. A pair's speedup is the ratio of its two medians, a query's the median of its pairs': 3 pairs,
  then more while the 95 % interval of that median is wider than ±5 %, 8 at most. Fixed canary runs
  before and after each query, and the machine's busiest process, swap and power, decide whether it
  runs again.
* **DuckDB's time** is the latency its JSON profile gives, to the nanosecond, for a query under 20 ms,
  and its timer's past that, which counts whole milliseconds: the profile cost 2 % on q10.
* **Two sessions**, one after the other: a figure is published when they agree within 10 %, a third
  session deciding otherwise. The tables give the mean of the two that agreed, the wider of their 95 %
  intervals and their pairs.
* **Speedup** is DuckDB's time over ours: above 1.00x, Vorticity took less.

These figures inform; they gate nothing. Each change to the group by is judged against the
engine's own earlier runs ([05-benchmarks.md](../design/05-benchmarks.md)).

## One thread

Warm: our file kept mapped, DuckDB's own table.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 31.2 | 117 | 3.73x | 3.54–3.84 | 6 |
| count and sum by 10⁶ keys | 40M | 130.2 | 762 | 5.90x | 5.54–6.18 | 6 |
| count and sum by 10⁷ keys | 40M | 581.4 | 1 352 | 2.32x | 2.29–2.37 | 6 |
| count, least and largest by 10⁶ keys | 40M | 284.4 | 882 | 3.11x | 3.07–3.14 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 275.4 | 764 | 2.75x | 2.62–2.89 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 690.9 | 1 326 | 1.92x | 1.90–1.96 | 6 |
| db q1: sum v1 by id1 | 10M | 39.8 | 73.5 | 1.85x | 1.82–1.88 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 83.0 | 138 | 1.66x | 1.65–1.72 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 102.7 | 179 | 1.73x | 1.69–1.76 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 23.7 | 44.0 | 1.86x | 1.79–1.88 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 48.3 | 117 | 2.42x | 2.37–2.47 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 86.2 | 174 | 2.02x | 2.01–2.05 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 682.9 | 826 | 1.21x | 1.18–1.29 | 9 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 52.2 | 242 | 4.64x | 4.55–4.70 | 6 |
| count and sum by 10⁶ keys | 40M | 177.0 | 889 | 5.03x | 4.57–5.21 | 9 |
| count and sum by 10⁷ keys | 40M | 597.4 | 1 435 | 2.40x | 2.30–2.44 | 6 |
| count, least and largest by 10⁶ keys | 40M | 313.3 | 955 | 3.05x | 2.98–3.08 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 346.2 | 939 | 2.71x | 2.65–2.77 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 723.4 | 1 437 | 2.00x | 1.97–2.02 | 6 |
| db q1: sum v1 by id1 | 10M | 42.9 | 139 | 3.23x | 3.19–3.29 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 87.8 | 259 | 2.96x | 2.84–3.02 | 9 |
| db q3: sum v1, mean v3 by id3 | 10M | 108.6 | 202 | 1.86x | 1.73–1.90 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 31.7 | 63.5 | 2.02x | 1.95–2.05 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 55.0 | 124 | 2.25x | 2.21–2.28 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 90.0 | 192 | 2.14x | 2.05–2.22 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 694.5 | 914 | 1.32x | 1.28–1.41 | 12 |

## Fourteen threads

Warm: our file kept mapped, DuckDB's own table.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 3.6 | 13.2 | 3.60x | 3.54–3.68 | 6 |
| count and sum by 10⁶ keys | 40M | 65.2 | 145 | 2.23x | 2.15–2.41 | 9 |
| count and sum by 10⁷ keys | 40M | 73.0 | 178 | 2.47x | 2.34–2.56 | 9 |
| count, least and largest by 10⁶ keys | 40M | 97.5 | 175 | 1.79x | 1.67–1.91 | 9 |
| count and sum by 10⁶ hashed keys | 40M | 112.6 | 143 | 1.27x | 1.22–1.31 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 188.7 | 182 | 0.96x | 0.93–1.00 | 9 |
| db q1: sum v1 by id1 | 10M | 5.3 | 11.3 | 2.12x | 1.94–2.20 | 9 |
| db q2: sum v1 by id1, id2 | 10M | 13.4 | 16.9 | 1.25x | 1.16–1.28 | 9 |
| db q3: sum v1, mean v3 by id3 | 10M | 39.8 | 46.0 | 1.16x | 1.14–1.19 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 3.1 | 5.3 | 1.74x | 1.69–1.80 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 20.8 | 45.5 | 2.20x | 2.12–2.30 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 27.5 | 49.0 | 1.78x | 1.70–1.87 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 211.0 | 107 | 0.51x | 0.50–0.52 | 6 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 12.4 | 52.8 | 4.23x | 3.50–4.62 | 16 |
| count and sum by 10⁶ keys | 40M | 72.7 | 183 | 2.51x | 2.46–2.66 | 6 |
| count and sum by 10⁷ keys | 40M | 79.9 | 228 | 2.87x | 2.80–3.02 | 6 |
| count, least and largest by 10⁶ keys | 40M | 103.8 | 200 | 1.95x | 1.89–2.00 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 123.2 | 183 | 1.49x | 1.45–1.56 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 192.3 | 226 | 1.18x | 1.13–1.20 | 6 |
| db q1: sum v1 by id1 | 10M | 6.9 | 21.5 | 3.13x | 2.87–3.20 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 15.7 | 35.5 | 2.23x | 2.18–2.31 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 44.6 | 60.5 | 1.36x | 1.34–1.40 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 6.4 | 17.9 | 2.79x | 2.73–2.98 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 23.6 | 57.2 | 2.42x | 2.27–2.58 | 9 |
| db q7: max v1 − min v2 by id3 | 10M | 31.6 | 56.5 | 1.81x | 1.74–1.94 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 205.6 | 133 | 0.65x | 0.61–0.67 | 9 |

## Where Vorticity leads, and where it trails

* **On one thread it leads every query on both bases**: 1.21x to 5.90x over DuckDB's own table, 1.32x
  to 5.03x over its reader. db-benchmark's q10, six keys of which three are texts and a group for
  nearly every one of 10⁷ rows, leads by the least: 1.21x and 1.32x.
* **On fourteen threads it leads every query but two**: 1.16x to 3.60x over DuckDB's own table and
  1.18x to 4.23x over its reader. Integer keys of 10³ to 10⁷ values lead by 1.79x to 3.60x warm, and
  db-benchmark's q1 to q7 by 1.16x to 2.20x warm, 1.36x to 3.13x cold.
* **db-benchmark's q10 trails on fourteen threads**: 0.51x over DuckDB's own table, 0.65x over its
  reader. Its six keys are held as one tuple of their values, which the lanes hand to a shared table
  of groups by parts; DuckDB writes rows nearly all unique straight to its partitions.
* **10⁷ hashed keys are level with DuckDB's own table on fourteen threads**: 0.96x, its interval
  reaching 1.00x; 1.18x over its reader.

Measured on 2026-10-09 on an Apple M4 Pro (14 cores: 10 performance, 4 efficiency) under macOS, at
commit `41467eaf`: two sessions one after the other, from 21:24 to 21:35 and from 21:35 to 21:53, and a
third for the two rows they did not agree on (10⁶ keys and 10⁶ hashed keys on one thread, cold, which
took 4.95x and 2.70x there). The machine was not at rest: in the second session another process held
a query back 356 s in all, and its canaries strayed up to 30 % from their first values, which the
pairs share; the first strayed up to 16 %.

Until 2026-10-09 this page timed every DuckDB query, then every one of ours: a perturbation of the
machine weighed on one side unseen, and DuckDB's q10 on fourteen threads read 257 ms where it took 102
the same day. It also set our runner mapping its file anew against DuckDB's table already in memory.

## Run it again

```
bench/duckdb.sh --set all --threads 1,14
bench/duckdb.sh --set all --threads 1,14
bench/duckdb.sh --publish bench/.runs/duckdb-<first> bench/.runs/duckdb-<second>
```

`duckdb` on the `PATH` (or `DUCKDB` naming it) with its extension installed (`INSTALL vortex`), as
`cargo` is for the comparison with Rust: a workstation's dependency, never CI's. Each session writes
any file it lacks first, through `bench/Vorticity.Benchmarks.Queries --fixture`, keeps the runner of
the commit it measures (`--runner`, `HEAD` by default), and keeps every run under
`bench/.runs/duckdb-<date>/`; `--publish` builds the tables above from two sessions, or three when two
do not agree. `--set hc` or `--set db` runs one half, `--rows` and `--db-rows` change the sizes,
`--only db-q4,total-k7` keeps the rows named by the runner's scenario. A session took 11 to 17 minutes
here.
