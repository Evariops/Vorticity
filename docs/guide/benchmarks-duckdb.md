# Benchmarks against DuckDB

Our group bys against DuckDB's, on the same files: DuckDB reads them through its Vortex extension,
and again from a table of its own it loaded from them first. Both sides answer the same query, and
the answers are compared. [benchmarks.md](benchmarks.md) compares Vorticity with Vortex's Rust
implementation; this page has a reference of its own, so that neither blurs the other.

Vortex™ is a trademark of LF Projects, LLC. DuckDB is a trademark of the DuckDB Foundation.
Vorticity is an independent implementation, not affiliated with or endorsed by the Vortex project,
LF Projects, LLC, DuckDB or the DuckDB Foundation.

* **The two sides.** Ours: the bench's runner (`bench/Vorticity.Benchmarks.Runner`) compiled with
  Native AOT, a process a query and a degree. DuckDB 1.5.2 (`8a5851971f`) and its `vortex` extension
  (`6ea8bd7`), a process a file, at the same number of threads (`SET threads`).
* **The same work on both sides.** Each query is a group by whose result is aggregated once more:
  DuckDB's outer query sums every column the group by computes, and the runner sums the same columns
  as it reads the groups. The two sums are the answer, and every row of the tables below gave the
  same one on both sides, to a part in 10⁹ (a float's sum depends on its order).
* **The files** are the bench's own, written by its generators, canonical, at edition `core2025.10`,
  the one the extension reads (it does not know `vortex.zoned`); both sides read the same file, its
  pages in the cache. 40 million rows of keys at random of 10³, 10⁶ and 10⁷ values, and the same
  number of keys spread by a stride, which no span bounds and every table hashes. And db-benchmark's
  group by at 10⁷ rows, the benchmark H2O.ai started and DuckDB Labs has kept since 2023: its data
  drawn by the laws of its `groupby-datagen.R` from the bench's own stream, not R's, and the queries
  our native aggregates cover, q1 to q5, q7 and q10. q6 (a median), q8 (the two largest of each
  group) and q9 (a correlation) are not covered, and not run.
* **DuckDB's own table** is loaded before any query is timed: its time leaves the load out. Its
  reader's time, like ours, reads the file at every query.
* **A figure** is the median of the last 7 of 10 runs in the side's process. DuckDB's timer counts
  whole milliseconds.
* **Speedup** is DuckDB's time over ours: above 1.00x, Vorticity took less.

These figures inform; they gate nothing. Each change to the group by is judged against the
engine's own earlier runs ([05-benchmarks.md](../design/05-benchmarks.md)).

## One thread

| query | rows | ours (ms) | DuckDB, reader (ms) | DuckDB, own table (ms) | speedup over the reader | speedup over the table |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 92.8 | 234 | 115 | 2.52x | 1.24x |
| count and sum by 10⁶ keys | 40M | 135.7 | 766 | 670 | 5.64x | 4.94x |
| count and sum by 10⁷ keys | 40M | 579.1 | 1 369 | 1 282 | 2.36x | 2.21x |
| count, least and largest by 10⁶ keys | 40M | 296.0 | 895 | 818 | 3.02x | 2.76x |
| count and sum by 10⁶ hashed keys | 40M | 274.2 | 848 | 688 | 3.09x | 2.51x |
| count and sum by 10⁷ hashed keys | 40M | 694.4 | 1 417 | 1 301 | 2.04x | 1.87x |
| db q1: sum v1 by id1 | 10M | 81.9 | 134 | 72 | 1.64x | 0.88x |
| db q2: sum v1 by id1, id2 | 10M | 160.7 | 256 | 137 | 1.59x | 0.85x |
| db q3: sum v1, mean v3 by id3 | 10M | 132.9 | 190 | 177 | 1.43x | 1.33x |
| db q4: mean v1:v3 by id4 | 10M | 55.2 | 65 | 42 | 1.18x | 0.76x |
| db q5: sum v1:v3 by id6 | 10M | 79.3 | 122 | 117 | 1.54x | 1.48x |
| db q7: max v1 − min v2 by id3 | 10M | 98.5 | 186 | 173 | 1.89x | 1.76x |
| db q10: sum v3, count by id1:id6 | 10M | 924.4 | 887 | 790 | 0.96x | 0.85x |

## Fourteen threads

| query | rows | ours (ms) | DuckDB, reader (ms) | DuckDB, own table (ms) | speedup over the reader | speedup over the table |
|---|---:|---:|---:|---:|---:|---:|
| count and sum by 10³ keys | 40M | 13.7 | 45 | 12 | 3.28x | 0.88x |
| count and sum by 10⁶ keys | 40M | 75.7 | 176 | 169 | 2.32x | 2.23x |
| count and sum by 10⁷ keys | 40M | 202.3 | 511 | 399 | 2.53x | 1.97x |
| count, least and largest by 10⁶ keys | 40M | 113.6 | 468 | 425 | 4.12x | 3.74x |
| count and sum by 10⁶ hashed keys | 40M | 181.4 | 183 | 182 | 1.01x | 1.00x |
| count and sum by 10⁷ hashed keys | 40M | 494.6 | 535 | 407 | 1.08x | 0.82x |
| db q1: sum v1 by id1 | 10M | 21.3 | 21 | 10 | 0.99x | 0.47x |
| db q2: sum v1 by id1, id2 | 10M | 36.9 | 33 | 16 | 0.89x | 0.43x |
| db q3: sum v1, mean v3 by id3 | 10M | 59.7 | 57 | 44 | 0.95x | 0.74x |
| db q4: mean v1:v3 by id4 | 10M | 13.8 | 18 | 5 | 1.30x | 0.36x |
| db q5: sum v1:v3 by id6 | 10M | 32.7 | 61 | 66 | 1.87x | 2.02x |
| db q7: max v1 − min v2 by id3 | 10M | 42.9 | 98 | 108 | 2.28x | 2.52x |
| db q10: sum v3, count by id1:id6 | 10M | 526.8 | 331 | 257 | 0.63x | 0.49x |

## Where Vorticity leads, and where it trails

* **On one thread it leads every query on high-cardinality keys**, 2.04x to 5.64x over DuckDB's
  reader and 1.24x to 4.94x over its own table, and db-benchmark's queries but q10 over the reader,
  1.18x to 1.89x.
* **On fourteen threads it leads the integer keys of 10⁶ and 10⁷ values**, 1.97x to 4.12x, and
  db-benchmark's q5 and q7, 1.87x to 2.52x. Hashed keys are level with DuckDB's reader, and 10⁷ of
  them trail its own table (0.82x).
* **A table already in memory wins a group by of few groups.** Over 100 to 10 000 groups
  (db-benchmark's q1, q2 and q4, and 10³ keys), DuckDB's own table answers in 5 to 16 ms at fourteen
  threads, where both readers spend their time decoding the file: 0.36x to 0.88x for us, and on one
  thread 0.76x to 0.88x for q1, q2 and q4. Against DuckDB's reader of the same file, those three
  read from 0.89x to 1.64x.
* **db-benchmark's q10 trails**: six keys of which three are texts, a group for nearly every one of
  10⁷ rows. 0.96x and 0.85x on one thread, 0.63x and 0.49x on fourteen, where the lanes' tables of
  ten million groups are merged.

Measured on 2026-10-09 on an Apple M4 Pro (14 cores: 10 performance, 4 efficiency) under macOS, at
commit `6fe2190f`.

## Run it again

```
bench/duckdb.sh --set all --threads 1,14
```

`duckdb` on the `PATH` (or `DUCKDB` naming it) with its extension installed (`INSTALL vortex`), as
`cargo` is for the comparison with Rust: a workstation's dependency, never CI's. The script writes
any file it lacks first, through `bench/Vorticity.Benchmarks.Queries --fixture`, keeps the runner of
the commit it measures (`--runner`, `HEAD` by default) and prints the tables above with the machine,
the load, the commit and the versions. `--set hc` or `--set db` runs one half, `--rows` and
`--db-rows` change the sizes. It took four minutes here.
