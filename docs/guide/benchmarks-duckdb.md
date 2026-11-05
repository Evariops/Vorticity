# Benchmarks against DuckDB

Our group bys against DuckDB's, on the same files. DuckDB reads them through its Vortex extension,
and again from a table of its own loaded from them beforehand. Both sides answer the same query, and
the answers are compared. [benchmarks.md](benchmarks.md) compares Vorticity with Vortex's Rust
implementation, and this page keeps its own reference so that neither comparison blurs the other.

Vortex™ is a trademark of LF Projects, LLC. DuckDB is a trademark of the DuckDB Foundation.
Vorticity is an independent implementation, not affiliated with or endorsed by the Vortex project,
LF Projects, LLC, DuckDB or the DuckDB Foundation.

How the comparison is run:

* Our side is the bench's runner (`bench/Vorticity.Benchmarks.Runner`) compiled with Native AOT, one
  process per block of runs. DuckDB's side is DuckDB 1.5.2 (`8a5851971f`) with its `vortex`
  extension (`6ea8bd7`), one process per file, kept open for the whole session, at the same number
  of threads (`SET threads`).
* Each query is timed on two bases. Warm pits our runner, with its file kept mapped the way a session
  keeps its 64 most recent files by default, against DuckDB's own table, loaded from the file before
  anything is timed. Cold pits our runner mapping the file anew at every run against DuckDB's Vortex
  reader, which reads the file at every query. A table already in memory against a file mapped anew
  would compare page faults rather than engines.
* Both sides do the same work. Each query is a group by whose result is aggregated once more:
  DuckDB's outer query sums every column the group by computes, and the runner sums the same columns
  as it reads the groups. Those sums are the answer, and every row of the tables below gave the same
  one on both sides, to a part in 10⁹ (a float sum depends on its order).
* The files are the bench's own, written by its generators, canonical, at edition `core2025.10`,
  which is the one the extension reads (it does not know `vortex.zoned`). Both sides read the same
  file, read once before timing so its pages are in the cache. There are 40 million rows with keys
  drawn at random from 10³, 10⁶ and 10⁷ values, and the same number of keys spread by a stride, which
  no span bounds and every table has to hash. There is also db-benchmark's group by at 10⁷ rows (the
  benchmark H2O.ai started and DuckDB Labs has maintained since 2023), with data drawn by the laws of
  its `groupby-datagen.R` from the bench's own random stream rather than R's. The queries run are
  those our native aggregates cover: q1 to q5, q7 and q10. q6 (a median), q8 (the two largest of each
  group) and q9 (a correlation) are not covered and not run.
* Runs alternate in pairs. A block of DuckDB runs and a process of ours take turns, DuckDB then us,
  us then DuckDB (ABBA), so any drift of the machine weighs on both. A block is 10 runs with the
  first 3 dropped, or 5 runs with 2 dropped once a query passes 100 ms, and a side's figure is the
  median of its runs. A pair's speedup is the ratio of its two medians, and a query's speedup is the
  median over its pairs: 3 pairs, then more while the 95 % interval of that median is wider than
  ±5 %, up to 8. Fixed canary runs before and after each query, plus the machine's busiest process,
  swap and power state, decide whether a query runs again.
* DuckDB's time is the latency from its JSON profile, to the nanosecond, for a query under 20 ms, and
  its timer's past that, which counts whole milliseconds (the profile cost 2 % on q10).
* Two sessions run one after the other, and a figure is published when they agree within 10 %,
  with a third session deciding otherwise. The tables give the mean of the two that agreed, the wider
  of their 95 % intervals, and their pairs.
* Speedup is DuckDB's time over ours. Above 1.00x, Vorticity took less.

These figures inform and gate nothing. Changes to the group by are judged against the engine's own
earlier runs ([05-benchmarks.md](../design/05-benchmarks.md)).

## One thread

Warm: our file kept mapped, DuckDB's own table.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 30.8 | 115 | 3.68x | 3.51–3.90 | 6 |
| count and sum by 10⁶ keys | 40M | 111.9 | 688 | 6.15x | 6.02–6.29 | 6 |
| count and sum by 10⁷ keys | 40M | 542.6 | 1 296 | 2.39x | 2.37–2.41 | 6 |
| count, least and largest by 10⁶ keys | 40M | 269.0 | 826 | 3.07x | 3.06–3.10 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 278.6 | 718 | 2.58x | 2.52–2.73 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 589.1 | 1 325 | 2.25x | 2.23–2.27 | 6 |
| db q1: sum v1 by id1 | 10M | 35.4 | 73.0 | 2.06x | 2.06–2.07 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 75.9 | 138 | 1.82x | 1.79–1.83 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 101.8 | 176 | 1.73x | 1.72–1.75 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 23.2 | 42.0 | 1.81x | 1.81–1.86 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 46.9 | 117 | 2.49x | 2.47–2.51 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 86.0 | 174 | 2.03x | 2.02–2.05 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 562.7 | 797 | 1.42x | 1.41–1.44 | 6 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 49.6 | 242 | 4.87x | 4.82–4.93 | 6 |
| count and sum by 10⁶ keys | 40M | 138.0 | 787 | 5.70x | 5.38–5.83 | 6 |
| count and sum by 10⁷ keys | 40M | 564.0 | 1 384 | 2.45x | 2.36–2.48 | 6 |
| count, least and largest by 10⁶ keys | 40M | 296.2 | 907 | 3.06x | 3.04–3.22 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 318.3 | 873 | 2.74x | 2.67–2.79 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 620.9 | 1 428 | 2.30x | 2.28–2.31 | 6 |
| db q1: sum v1 by id1 | 10M | 37.5 | 135 | 3.59x | 3.55–3.66 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 78.0 | 259 | 3.32x | 3.25–3.34 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 106.5 | 192 | 1.80x | 1.78–1.83 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 30.6 | 64.5 | 2.11x | 2.06–2.13 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 53.3 | 123 | 2.31x | 2.30–2.34 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 89.7 | 192 | 2.12x | 2.09–2.14 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 571.9 | 895 | 1.56x | 1.55–1.59 | 6 |

## Fourteen threads

Warm: our file kept mapped, DuckDB's own table.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 3.6 | 13.0 | 3.66x | 3.46–3.85 | 9 |
| count and sum by 10⁶ keys | 40M | 57.5 | 143 | 2.50x | 2.35–2.52 | 6 |
| count and sum by 10⁷ keys | 40M | 62.9 | 174 | 2.78x | 2.73–2.88 | 6 |
| count, least and largest by 10⁶ keys | 40M | 82.3 | 167 | 2.03x | 1.99–2.07 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 75.4 | 146 | 1.92x | 1.89–1.97 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 102.1 | 178 | 1.73x | 1.73–1.75 | 6 |
| db q1: sum v1 by id1 | 10M | 4.8 | 10.8 | 2.25x | 2.18–2.30 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 11.1 | 16.6 | 1.50x | 1.46–1.52 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 33.0 | 45.0 | 1.36x | 1.35–1.38 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 2.9 | 5.0 | 1.73x | 1.68–1.80 | 9 |
| db q5: sum v1:v3 by id6 | 10M | 16.6 | 44.0 | 2.66x | 2.54–2.69 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 21.1 | 47.5 | 2.26x | 2.22–2.36 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 84.7 | 99.5 | 1.17x | 1.16–1.20 | 6 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 12.1 | 50.0 | 4.10x | 3.27–4.64 | 16 |
| count and sum by 10⁶ keys | 40M | 63.3 | 180 | 2.80x | 2.69–3.00 | 6 |
| count and sum by 10⁷ keys | 40M | 67.5 | 223 | 3.27x | 3.25–3.42 | 6 |
| count, least and largest by 10⁶ keys | 40M | 88.0 | 197 | 2.22x | 2.09–2.29 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 83.4 | 181 | 2.17x | 2.12–2.27 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 109.6 | 220 | 1.99x | 1.95–2.12 | 9 |
| db q1: sum v1 by id1 | 10M | 6.2 | 20.5 | 3.33x | 3.06–3.42 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 13.3 | 34.0 | 2.56x | 2.46–2.65 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 35.6 | 58.0 | 1.63x | 1.59–1.65 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 6.0 | 17.9 | 2.96x | 2.76–3.05 | 9 |
| db q5: sum v1:v3 by id6 | 10M | 19.3 | 54.5 | 2.82x | 2.71–2.96 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 24.0 | 54.0 | 2.25x | 2.16–2.49 | 11 |
| db q10: sum v3, count by id1:id6 | 10M | 92.2 | 127 | 1.38x | 1.34–1.40 | 6 |

## Reading the results

Vorticity leads every query on both bases, on one thread and on fourteen.

On one thread the lead runs from 1.42x to 6.15x over DuckDB's own table, and from 1.56x to 5.70x
over its Vortex reader. The smallest lead is db-benchmark's q10, with six keys of which three are
text, and a group for nearly every one of its 10⁷ rows.

On fourteen threads the lead runs from 1.17x to 3.66x over DuckDB's own table, and from 1.38x to
4.10x over its reader. Integer keys of 10³ to 10⁷ values lead by 2.03x to 3.66x warm, hashed keys by
1.73x and 1.92x, and db-benchmark's q1 to q7 by 1.36x to 2.66x warm and 1.63x to 3.33x cold.

q10 on fourteen threads is the narrowest margin, 1.17x warm (interval 1.16x to 1.20x) and 1.38x
cold. Its six keys are held as one 64-byte tuple of their values, which the lanes hand to a shared
table of groups split into parts. DuckDB packs the same keys into 38 bytes before grouping them, and
writes rows that are nearly all unique straight to its partitions. Narrower tuples are the open lead
for this query.

The figures were measured on 2026-11-05 on an Apple M4 Pro (14 cores: 10 performance, 4 efficiency)
under macOS, at commit `6b7b122c`, held awake by `caffeinate -dimsu`: with nobody at the machine,
macOS runs these processes at 2 to 3 GHz instead of 4.3. Two sessions ran back to back, from 03:19 to
03:30 and from 03:30 to 03:40, and agreed on every row but two cold ones on fourteen threads, the
count and sum by 10³ keys and q7, which a third session at 03:40 settled. The machine was on mains
power: the canaries strayed up to 17.6 %, 6.7 % and 8.6 % from their first values, three rows ran
again, and another process held queries back 6 s in the second session.

## Run it again

```
bench/duckdb.sh --set all --threads 1,14
bench/duckdb.sh --set all --threads 1,14
bench/duckdb.sh --publish bench/.runs/duckdb-<first> bench/.runs/duckdb-<second>
```

This needs `duckdb` on the `PATH` (or `DUCKDB` pointing at it) with its extension installed
(`INSTALL vortex`), the same way `cargo` is needed for the comparison with Rust. It is a
workstation's dependency, never CI's. Each session first writes any file it lacks through
`bench/Vorticity.Benchmarks.Queries --fixture`, keeps the runner of the commit it measures
(`--runner`, `HEAD` by default), and stores every run under `bench/.runs/duckdb-<date>/`. `--publish`
builds the tables above from two sessions, or three when two do not agree. `--set hc` or `--set db`
runs one half, `--rows` and `--db-rows` change the sizes, and `--only db-q4,total-k7` keeps only the
rows named by the runner's scenario. A session took 10 to 17 minutes here.
