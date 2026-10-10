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
| count and sum by 10³ keys | 40M | 31.2 | 115 | 3.69x | 3.56–3.76 | 6 |
| count and sum by 10⁶ keys | 40M | 117.0 | 723 | 6.15x | 6.02–6.36 | 6 |
| count and sum by 10⁷ keys | 40M | 553.2 | 1 333 | 2.41x | 2.39–2.42 | 6 |
| count, least and largest by 10⁶ keys | 40M | 277.1 | 866 | 3.12x | 3.09–3.24 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 292.2 | 737 | 2.52x | 2.45–2.66 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 598.8 | 1 334 | 2.23x | 2.17–2.25 | 6 |
| db q1: sum v1 by id1 | 10M | 35.5 | 72.5 | 2.05x | 2.02–2.08 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 76.1 | 138 | 1.81x | 1.78–1.83 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 102.6 | 177 | 1.72x | 1.69–1.75 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 23.3 | 43.0 | 1.85x | 1.79–1.90 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 47.2 | 117 | 2.48x | 2.47–2.49 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 86.2 | 175 | 2.02x | 2.02–2.06 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 569.0 | 813 | 1.43x | 1.42–1.45 | 6 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 49.7 | 241 | 4.84x | 4.79–4.91 | 6 |
| count and sum by 10⁶ keys | 40M | 145.3 | 819 | 5.65x | 5.29–5.86 | 9 |
| count and sum by 10⁷ keys | 40M | 575.6 | 1 428 | 2.48x | 2.47–2.51 | 6 |
| count, least and largest by 10⁶ keys | 40M | 303.6 | 943 | 3.10x | 3.07–3.16 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 321.5 | 887 | 2.76x | 2.63–2.81 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 634.0 | 1 461 | 2.30x | 2.28–2.32 | 6 |
| db q1: sum v1 by id1 | 10M | 37.5 | 135 | 3.60x | 3.56–3.64 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 78.1 | 261 | 3.34x | 3.30–3.36 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 108.9 | 197 | 1.77x | 1.71–1.87 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 30.6 | 64.5 | 2.12x | 2.09–2.14 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 53.8 | 124 | 2.30x | 2.18–2.35 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 89.8 | 193 | 2.14x | 2.06–2.16 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 580.4 | 915 | 1.58x | 1.57–1.60 | 6 |

## Fourteen threads

Warm: our file kept mapped, DuckDB's own table.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 3.7 | 12.8 | 3.50x | 3.26–3.60 | 7 |
| count and sum by 10⁶ keys | 40M | 58.9 | 145 | 2.46x | 2.35–2.58 | 9 |
| count and sum by 10⁷ keys | 40M | 65.9 | 176 | 2.66x | 2.50–2.90 | 9 |
| count, least and largest by 10⁶ keys | 40M | 84.3 | 173 | 2.06x | 1.88–2.09 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 77.6 | 146 | 1.87x | 1.82–1.92 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 104.0 | 180 | 1.75x | 1.66–1.78 | 6 |
| db q1: sum v1 by id1 | 10M | 4.9 | 11.3 | 2.31x | 2.22–2.43 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 11.6 | 16.6 | 1.44x | 1.31–1.51 | 15 |
| db q3: sum v1, mean v3 by id3 | 10M | 33.6 | 46.0 | 1.35x | 1.32–1.39 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 3.0 | 5.1 | 1.66x | 1.59–1.75 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 17.2 | 44.0 | 2.56x | 2.40–2.60 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 21.7 | 48.5 | 2.21x | 2.13–2.24 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 87.2 | 103 | 1.18x | 1.15–1.20 | 6 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 11.9 | 53.0 | 4.47x | 4.26–5.41 | 11 |
| count and sum by 10⁶ keys | 40M | 64.9 | 184 | 2.79x | 2.68–2.92 | 6 |
| count and sum by 10⁷ keys | 40M | 71.4 | 227 | 3.16x | 2.82–3.35 | 16 |
| count, least and largest by 10⁶ keys | 40M | 89.8 | 202 | 2.24x | 2.18–2.34 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 83.8 | 184 | 2.19x | 2.14–2.26 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 110.8 | 226 | 2.03x | 1.95–2.18 | 15 |
| db q1: sum v1 by id1 | 10M | 6.1 | 21.0 | 3.44x | 3.26–3.49 | 9 |
| db q2: sum v1 by id1, id2 | 10M | 13.8 | 34.0 | 2.46x | 2.27–2.53 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 36.7 | 58.0 | 1.60x | 1.56–1.62 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 5.9 | 17.7 | 3.00x | 2.93–3.06 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 19.4 | 56.0 | 2.89x | 2.80–2.97 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 24.4 | 60.0 | 2.47x | 2.36–2.52 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 91.5 | 129 | 1.39x | 1.31–1.42 | 6 |

## Where Vorticity leads, and where it trails

* **On one thread it leads every query on both bases**: 1.43x to 6.15x over DuckDB's own table, 1.58x
  to 5.65x over its reader. db-benchmark's q10, six keys of which three are texts and a group for
  nearly every one of 10⁷ rows, leads by the least: 1.43x and 1.58x.
* **On fourteen threads it leads every query on both bases**: 1.18x to 3.50x over DuckDB's own table
  and 1.39x to 4.47x over its reader. Integer keys of 10³ to 10⁷ values lead by 2.06x to 3.50x warm,
  hashed ones by 1.75x and 1.87x, and db-benchmark's q1 to q7 by 1.35x to 2.56x warm, 1.60x to 3.44x
  cold.
* **db-benchmark's q10 leads by the least on fourteen threads**: 1.18x over DuckDB's own table, its
  interval 1.15x to 1.20x, and 1.39x over its reader. Its six keys are held as one tuple of their
  values, 64 bytes, which the lanes hand to a shared table of groups by parts; DuckDB packs the same
  keys into 38 bytes before it groups them, and writes rows nearly all unique straight to its
  partitions. It trailed at 0.51x on 2026-10-09, as 10⁷ hashed keys did at 0.96x (1.75x now): the
  memory of the groups is now kept from one query to the next rather than made anew, each part of the
  shared table is held whole rather than split as it grows, a key is hashed once on its way, and the
  keys are written into the result a part at a time.

Measured on 2026-10-10 on an Apple M4 Pro (14 cores: 10 performance, 4 efficiency) under macOS, at
commit `7734c472`: two sessions one after the other, from 05:45 to 05:57 and from 05:57 to 06:07, and a
third for the one row they did not agree on (10³ keys on fourteen threads, cold, which took 3.85x and
4.64x, then 4.31x). The machine was near rest: the canaries strayed up to 9.8 % and 10.3 % from their
first values, another process held a query back 8 s and 6 s in all, and one row of the first session
ran again.

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
