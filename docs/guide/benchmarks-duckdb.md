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
| count and sum by 10³ keys | 40M | 31.1 | 115 | 3.69x | 3.58–3.84 | 6 |
| count and sum by 10⁶ keys | 40M | 112.2 | 688 | 6.14x | 5.57–6.23 | 6 |
| count and sum by 10⁷ keys | 40M | 540.4 | 1 282 | 2.38x | 2.36–2.39 | 6 |
| count, least and largest by 10⁶ keys | 40M | 265.1 | 814 | 3.08x | 3.05–3.09 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 278.2 | 715 | 2.57x | 2.44–2.62 | 9 |
| count and sum by 10⁷ hashed keys | 40M | 584.9 | 1 297 | 2.22x | 2.21–2.23 | 6 |
| db q1: sum v1 by id1 | 10M | 35.3 | 73.0 | 2.07x | 2.05–2.08 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 75.6 | 138 | 1.82x | 1.80–1.83 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 101.6 | 176 | 1.73x | 1.71–1.76 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 23.1 | 42.0 | 1.82x | 1.77–1.82 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 46.9 | 116 | 2.47x | 2.45–2.50 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 85.4 | 174 | 2.04x | 2.02–2.10 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 553.2 | 788 | 1.43x | 1.41–1.44 | 6 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 49.4 | 246 | 4.98x | 4.80–5.09 | 6 |
| count and sum by 10⁶ keys | 40M | 134.5 | 782 | 5.81x | 5.68–5.83 | 6 |
| count and sum by 10⁷ keys | 40M | 559.2 | 1 368 | 2.44x | 2.43–2.46 | 6 |
| count, least and largest by 10⁶ keys | 40M | 290.5 | 894 | 3.08x | 2.85–3.10 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 310.3 | 858 | 2.77x | 2.74–2.85 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 615.1 | 1 410 | 2.29x | 2.28–2.33 | 6 |
| db q1: sum v1 by id1 | 10M | 37.1 | 134 | 3.63x | 3.48–3.69 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 79.6 | 260 | 3.28x | 3.24–3.33 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 105.7 | 192 | 1.81x | 1.80–1.82 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 30.0 | 63.5 | 2.12x | 2.10–2.17 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 52.7 | 122 | 2.32x | 2.30–2.34 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 88.9 | 189 | 2.12x | 2.11–2.14 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 557.5 | 876 | 1.57x | 1.55–1.58 | 6 |

## Fourteen threads

Warm: our file kept mapped, DuckDB's own table.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 3.6 | 12.8 | 3.61x | 3.54–3.65 | 6 |
| count and sum by 10⁶ keys | 40M | 60.0 | 143 | 2.38x | 2.32–2.52 | 6 |
| count and sum by 10⁷ keys | 40M | 60.9 | 174 | 2.85x | 2.70–2.86 | 6 |
| count, least and largest by 10⁶ keys | 40M | 81.5 | 166 | 2.04x | 1.97–2.08 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 74.7 | 143 | 1.92x | 1.89–1.94 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 101.1 | 175 | 1.73x | 1.72–1.74 | 6 |
| db q1: sum v1 by id1 | 10M | 4.8 | 10.7 | 2.25x | 2.18–2.31 | 6 |
| db q2: sum v1 by id1, id2 | 10M | 11.9 | 16.2 | 1.37x | 1.27–1.47 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 32.5 | 45.0 | 1.39x | 1.36–1.43 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 2.9 | 5.0 | 1.72x | 1.70–1.76 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 16.4 | 44.0 | 2.69x | 2.63–2.72 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 20.9 | 47.5 | 2.29x | 2.19–2.33 | 6 |
| db q10: sum v3, count by id1:id6 | 10M | 84.6 | 99.5 | 1.18x | 1.16–1.19 | 6 |

Cold: our file mapped anew at every run, DuckDB's Vortex reader.

| query | rows | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 11.7 | 51.8 | 4.44x | 3.51–5.48 | 16 |
| count and sum by 10⁶ keys | 40M | 64.0 | 179 | 2.80x | 2.60–2.96 | 6 |
| count and sum by 10⁷ keys | 40M | 67.3 | 221 | 3.31x | 3.08–3.40 | 9 |
| count, least and largest by 10⁶ keys | 40M | 86.9 | 196 | 2.22x | 2.19–2.33 | 6 |
| count and sum by 10⁶ hashed keys | 40M | 82.5 | 178 | 2.17x | 2.12–2.26 | 6 |
| count and sum by 10⁷ hashed keys | 40M | 108.1 | 219 | 2.02x | 1.94–2.23 | 6 |
| db q1: sum v1 by id1 | 10M | 6.0 | 21.0 | 3.45x | 3.33–3.59 | 9 |
| db q2: sum v1 by id1, id2 | 10M | 14.1 | 34.5 | 2.45x | 2.30–2.52 | 6 |
| db q3: sum v1, mean v3 by id3 | 10M | 35.4 | 57.5 | 1.61x | 1.58–1.64 | 6 |
| db q4: mean v1:v3 by id4 | 10M | 5.7 | 17.6 | 3.04x | 2.87–3.16 | 6 |
| db q5: sum v1:v3 by id6 | 10M | 18.7 | 55.0 | 2.90x | 2.81–3.00 | 6 |
| db q7: max v1 − min v2 by id3 | 10M | 23.4 | 60.0 | 2.55x | 2.39–2.62 | 9 |
| db q10: sum v3, count by id1:id6 | 10M | 88.5 | 122 | 1.38x | 1.33–1.40 | 6 |

## Reading the results

Vorticity leads every query on both bases, on one thread and on fourteen.

On one thread the lead runs from 1.43x to 6.14x over DuckDB's own table, and from 1.57x to 5.81x
over its Vortex reader. The smallest lead is db-benchmark's q10, with six keys of which three are
text, and a group for nearly every one of its 10⁷ rows.

On fourteen threads the lead runs from 1.18x to 3.61x over DuckDB's own table, and from 1.38x to
4.44x over its reader. Integer keys of 10³ to 10⁷ values lead by 2.04x to 3.61x warm, hashed keys by
1.73x and 1.92x, and db-benchmark's q1 to q7 by 1.37x to 2.69x warm and 1.61x to 3.45x cold.

q10 on fourteen threads is the narrowest margin, 1.18x warm (interval 1.16x to 1.19x) and 1.38x
cold. Its six keys are held as one 64-byte tuple of their values, which the lanes hand to a shared
table of groups split into parts. DuckDB packs the same keys into 38 bytes before grouping them, and
writes rows that are nearly all unique straight to its partitions. Narrower tuples are the open lead
for this query.

The figures were measured on 2026-10-14 on an Apple M4 Pro (14 cores: 10 performance, 4 efficiency)
under macOS, at commit `81a70ad2`, held awake by `caffeinate -dimsu`: with nobody at the machine,
macOS ran these processes at 2 to 3 GHz instead of 4.3. Two sessions ran back to back, from 02:52 to
03:02 and from 03:02 to 03:12, and agreed on every row. The machine was on mains power and close to
idle: the canaries strayed up to 4.9 % and 6.7 % from their first values, another process held
queries back 18 s in the first, and no row ran again.

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
rows named by the runner's scenario. A session took 11 to 17 minutes here.
