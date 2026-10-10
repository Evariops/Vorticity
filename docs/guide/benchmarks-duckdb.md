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
| count and sum by 10³ keys | 40M | 31.1 | 119 | 3.81x | 3.60–3.83 | 6 |
| count and sum by 10⁶ keys | 40M | 114.0 | 717 | 6.28x | 5.95–6.53 | 9 |
| count and sum by 10⁷ keys | 40M | 554.3 | 1 328 | 2.40x | 2.39–2.41 | 6 |
| count, least and largest by 10⁶ keys | 40M | 274.8 | 866 | 3.15x | 3.10–3.18 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 254.7 | 741 | 2.90x | 2.75–3.04 | 9 |
| count and sum by 10⁷ hashed keys | 40M | 679.5 | 1 332 | 1.96x | 1.95–1.98 | 6 |
| db q1: sum v1 by id1 | 10M | 39.3 | 73.5 | 1.87x | 1.85–1.87 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 83.3 | 138 | 1.66x | 1.64–1.66 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 102.5 | 177 | 1.73x | 1.59–1.73 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 23.3 | 43.5 | 1.86x | 1.80–1.89 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 47.4 | 118 | 2.48x | 2.45–2.51 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 86.0 | 176 | 2.04x | 2.02–2.06 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 586.4 | 814 | 1.39x | 1.38–1.40 | 6 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 50.6 | 246 | 4.84x | 4.72–4.96 | 6 |
| count and sum by 10⁶ keys | 40M | 148.0 | 812 | 5.54x | 5.12–5.71 | 10 |
| count and sum by 10⁷ keys | 40M | 580.1 | 1 422 | 2.44x | 2.42–2.45 | 6 |
| count, least and largest by 10⁶ keys | 40M | 304.1 | 943 | 3.10x | 3.01–3.15 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 294.6 | 889 | 3.00x | 2.93–3.06 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 718.2 | 1 451 | 2.02x | 1.99–2.03 | 6 |
| db q1: sum v1 by id1 | 10M | 41.3 | 136 | 3.30x | 3.24–3.37 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 86.2 | 262 | 3.05x | 2.80–3.08 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 108.6 | 195 | 1.79x | 1.75–1.84 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 31.5 | 65.0 | 2.08x | 2.02–2.11 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 54.8 | 124 | 2.26x | 2.19–2.31 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 90.3 | 199 | 2.20x | 2.09–2.33 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 596.2 | 917 | 1.53x | 1.52–1.58 | 6 |

## Fourteen threads

Warm: our file kept mapped, DuckDB's own table.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 3.6 | 13.2 | 3.66x | 3.47–3.77 | 9 |
| count and sum by 10⁶ keys | 40M | 61.9 | 146 | 2.36x | 2.10–2.48 | 11 |
| count and sum by 10⁷ keys | 40M | 65.3 | 179 | 2.73x | 2.65–2.88 | 6 |
| count, least and largest by 10⁶ keys | 40M | 84.8 | 171 | 2.04x | 1.95–2.16 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 76.6 | 144 | 1.88x | 1.84–1.91 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 102.3 | 181 | 1.76x | 1.74–1.80 | 6 |
| db q1: sum v1 by id1 | 10M | 5.2 | 11.1 | 2.09x | 2.03–2.16 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 12.4 | 16.6 | 1.35x | 1.26–1.37 | 9 |
| db q3: sum v1, mean v3 by id3 | 10M | 35.4 | 45.5 | 1.29x | 1.24–1.30 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 3.0 | 5.2 | 1.72x | 1.67–1.80 | 10 |
| db q5: sum v1:v3 by id6 | 10M | 17.2 | 44.5 | 2.60x | 2.50–2.65 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 22.9 | 48.0 | 2.11x | 1.95–2.13 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 97.4 | 103 | 1.05x | 1.02–1.07 | 6 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 12.0 | 51.5 | 4.29x | 3.39–4.75 | 16 |
| count and sum by 10⁶ keys | 40M | 66.4 | 187 | 2.81x | 2.75–2.89 | 6 |
| count and sum by 10⁷ keys | 40M | 71.4 | 231 | 3.19x | 2.99–3.33 | 10 |
| count, least and largest by 10⁶ keys | 40M | 90.6 | 201 | 2.24x | 2.11–2.31 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 84.2 | 189 | 2.23x | 2.09–2.26 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 111.3 | 229 | 2.04x | 1.94–2.14 | 10 |
| db q1: sum v1 by id1 | 10M | 6.7 | 21.0 | 3.15x | 3.04–3.31 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 14.4 | 34.5 | 2.39x | 2.30–2.48 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 38.1 | 58.5 | 1.55x | 1.48–1.58 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 6.1 | 17.6 | 2.90x | 2.82–2.97 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 19.5 | 55.0 | 2.82x | 2.70–2.86 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 26.0 | 60.2 | 2.28x | 2.00–2.42 | 14 |
| db q10: sum v3, count by id1:id6 | 10M | 101.8 | 128 | 1.26x | 1.21–1.29 | 6 |

## Where Vorticity leads, and where it trails

* **On one thread it leads every query on both bases**: 1.39x to 6.28x over DuckDB's own table, 1.53x
  to 5.54x over its reader. db-benchmark's q10, six keys of which three are texts and a group for
  nearly every one of 10⁷ rows, leads by the least: 1.39x and 1.53x.
* **On fourteen threads it leads every query on both bases**: 1.05x to 3.66x over DuckDB's own table
  and 1.26x to 4.29x over its reader. Integer keys of 10³ to 10⁷ values lead by 2.04x to 3.66x warm,
  hashed ones by 1.76x and 1.88x, and db-benchmark's q1 to q7 by 1.29x to 2.60x warm, 1.55x to 3.15x
  cold.
* **db-benchmark's q10 leads by the least on fourteen threads**: 1.05x over DuckDB's own table, its
  interval 1.02x to 1.07x, and 1.26x over its reader. Its six keys are held as one tuple of their
  values, which the lanes hand to a shared table of groups by parts; DuckDB writes rows nearly all
  unique straight to its partitions. It trailed at 0.51x on 2026-10-09, as 10⁷ hashed keys did at
  0.96x (1.76x now): the memory of the groups is now kept from one query to the next rather than made
  anew, and each part of the shared table is held whole rather than split as it grows.

Measured on 2026-10-10 on an Apple M4 Pro (14 cores: 10 performance, 4 efficiency) under macOS, at
commit `7445c2a5`: two sessions one after the other, from 03:45 to 03:56 and from 03:56 to 04:08, which
agreed within 10 % on every row. The machine was near rest: the canaries strayed up to 7.6 % and 8.0 %
from their first values, another process held a query back 24 s and 6 s in all, and two rows of the
second session ran again.

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
