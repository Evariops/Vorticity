# Parquet benchmarks on x64

How fast Vorticity reads and writes Parquet files, measured on real files on one x64 machine. A
Parquet file goes through the same engine as a Vortex file, so each file is also rewritten as Vortex
and given the same work: the gap between the two is what the format costs. No other Parquet library
is measured here, and [17-parquet.md](../design/17-parquet.md) says why the package was written without
one.

Apache Parquet and Apache Arrow are trademarks of The Apache Software Foundation. Vortex™ is a
trademark of LF Projects, LLC. Vorticity is an independent implementation, not affiliated with or
endorsed by The Apache Software Foundation, the Vortex project or LF Projects, LLC.

## In short

* **On 32 threads, Parquet reads about as fast as Vortex, or faster**: the taxi trips in 21.2 ms
  against 28.3 for their Vortex copy, the nested file in 29.7 against 40.5, ClickBench's hits in 35.6
  against 34.1.
* **On one thread, a compressed Parquet file pays for its codec**: the taxi trips, compressed with
  GZIP, read in 159 ms against 21.9, three quarters of it decompression. ClickBench's hits, compressed
  with Snappy, read in 151 against 66.0.
* **On 32 threads, Parquet writes faster than Vortex**, in 0.21 of Vortex's time on ClickBench's
  hits to 0.73 on the taxi trips. On one thread it writes three of the four files faster. The taxi
  trips, whose GZIP pages it reads first, take 608 ms against 223.
* **Threads pay** for Parquet's scans up to 16 on the taxi trips, 8 on ClickBench's hits and 4 on the
  nested file.
* **A file read again reads faster**: a session keeps the files it closed mapped, and ClickBench's
  scan on 32 threads then takes 24.9 ms instead of 35.6.
* **A read allocates per call, not per row**: a scan of the taxi trips' three million rows allocates
  82 KiB.

## How it is measured

* **The machine.** An AMD Ryzen 9 7950X (Zen 4, 16 cores and 32 threads, AVX-512) under Windows 11,
  with .NET 11 (11.0.0-rc.1) and its JIT.
* **The same work on both formats**, in one process, through the same scan: only the open and the
  write are each format's own, and both must return the same rows. The **ratio** is Parquet's time
  over Vortex's: under 1.00, Parquet took less.
* **The actions.** `open` opens the file. `scan` reads every row of every column, and `project` the
  file's first column alone. `filter-narrow` and `filter-wide` read a hundredth and a half of the
  report's table, chosen by a range of its increasing column. `take` reads a thousand rows spread over
  the file. `write` reads every row and writes it again with the format's default options, to a sink
  that keeps nothing, and Parquet's defaults compress with ZSTD at level 3.
* **Warm.** An action runs for a second, and 32 times at least, before 5 timed calls, of which a
  figure is the median. Each file's figures come from one process. The files are in the page cache,
  and each call opens and maps its file again, as a first read does.
* **Threads** are the scan's or the writer's degree of parallelism (`DegreeOfParallelism`), 1 unless
  set, and 32 is every hardware thread of the machine.
* **Allocated** is what a call allocates on the managed heap once warm.
* **Vortex is the yardstick**, measured the same way. Its times move more than Parquet's from one
  process to the next: its scan of the taxi trips on one thread took 22 to 32 ms over three runs.
  [benchmarks-x64.md](benchmarks-x64.md) measures it on its own.

| file | written by | rows | columns | codec | size |
|---|---|---:|---|---|---:|
| the report's table, from the [core benchmarks](benchmarks.md) | this repository's writers | 1,048,576 | 4: an increasing `i64`, an `f64`, one of five short strings, a `bool` with nulls | ZSTD | 1.3 MB |
| the January 2023 [yellow taxi trips][tlc] | Arrow C++ 8.0 | 3,066,766 | 19 | GZIP | 47.7 MB |
| `hits_0.parquet`, the first part of [ClickBench][clickbench]'s hits | parquet-cpp 1.5.1 | 1,000,000 | 105, four of them long text | Snappy | 122.4 MB |
| a nested file | this package's writer, `--nested-file` | 2,000,000 | 22, six of them lists | Snappy | 120.7 MB |

The nested file is drawn from a fixed seed, so that every machine writes the same one.

## Reading and writing

### The report's table

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.12 | 0.12 | 1.01 | 5.5 KiB | 6.6 KiB |
| `scan` | 1 | 3.49 | 2.84 | 0.81 | 17.4 KiB | 45.3 KiB |
| `project` | 1 | 0.29 | 0.35 | 1.20 | 20.3 KiB | 43.5 KiB |
| `filter-narrow` | 1 | 0.34 | 0.40 | 1.20 | 45.2 KiB | 101.3 KiB |
| `filter-wide` | 1 | 1.67 | 1.68 | 1.01 | 27.6 KiB | 69.3 KiB |
| `take` | 1 | 4.30 | 3.41 | 0.79 | 65.2 KiB | 85.1 KiB |
| `write` | 1 | 25.1 | 17.6 | 0.70 | 54.0 KiB | 406.9 KiB |
| `open` | 32 | 0.11 | 0.12 | 1.05 | 5.5 KiB | 6.6 KiB |
| `scan` | 32 | 1.76 | 1.41 | 0.80 | 28.5 KiB | 84.3 KiB |
| `project` | 32 | 0.25 | 0.33 | 1.32 | 34.6 KiB | 44.4 KiB |
| `filter-narrow` | 32 | 0.31 | 0.41 | 1.29 | 45.6 KiB | 105.3 KiB |
| `filter-wide` | 32 | 0.78 | 1.01 | 1.30 | 32.6 KiB | 91.6 KiB |
| `take` | 32 | 1.65 | 1.90 | 1.15 | 111.4 KiB | 124.1 KiB |
| `write` | 32 | 14.1 | 6.85 | 0.49 | 65.7 KiB | 595.1 KiB |

Parquet's file is about half the size of Vortex's, 1.3 MB against 2.4. Parquet scans and writes in
less time on one thread and on 32, and takes rows faster on one. Vortex leads on the projection and
the narrow filter, and on 32 threads on both filters and the take.

### The taxi trips

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.17 | 0.11 | 0.65 | 35.5 KiB | 11.7 KiB |
| `scan` | 1 | 21.9 | 159 | 7.26 | 119.5 KiB | 82.1 KiB |
| `project` | 1 | 1.92 | 2.53 | 1.32 | 121.9 KiB | 49.9 KiB |
| `take` | 1 | 27.2 | 161 | 5.94 | 167.3 KiB | 121.8 KiB |
| `write` | 1 | 223 | 608 | 2.73 | 19.9 MiB | 2.3 MiB |
| `open` | 32 | 0.12 | 0.11 | 0.85 | 35.5 KiB | 11.7 KiB |
| `scan` | 32 | 28.3 | 21.2 | 0.75 | 196.9 KiB | 128.0 KiB |
| `project` | 32 | 1.26 | 2.56 | 2.03 | 151.9 KiB | 51.0 KiB |
| `take` | 32 | 33.0 | 21.3 | 0.64 | 412.7 KiB | 167.8 KiB |
| `write` | 32 | 201 | 148 | 0.73 | 20.0 MiB | 4.4 MiB |

GZIP sets the scan on one thread: inflating the pages takes 121 ms of its 159
([decompression](#decompression)). On 32 threads the pages inflate on every thread ahead of the read.
The projection reads one small column, which more threads do not speed up: 2.5 ms on one thread as
on 32.

### ClickBench's hits

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.21 | 0.15 | 0.73 | 397.0 KiB | 54.4 KiB |
| `scan` | 1 | 66.0 | 151 | 2.29 | 1.4 MiB | 295.5 KiB |
| `project` | 1 | 2.54 | 3.13 | 1.23 | 1.4 MiB | 97.1 KiB |
| `take` | 1 | 67.6 | 151 | 2.24 | 1.4 MiB | 335.2 KiB |
| `write` | 1 | 1,347 | 1,214 | 0.90 | 35.0 MiB | 6.3 MiB |
| `open` | 32 | 0.20 | 0.15 | 0.74 | 397.0 KiB | 54.4 KiB |
| `scan` | 32 | 34.1 | 35.6 | 1.05 | 1.5 MiB | 432.2 KiB |
| `project` | 32 | 2.89 | 1.61 | 0.56 | 1.4 MiB | 100.8 KiB |
| `take` | 32 | 69.4 | 36.5 | 0.53 | 2.2 MiB | 471.8 KiB |
| `write` | 32 | 1,243 | 265 | 0.21 | 35.2 MiB | 10.1 MiB |

Snappy and four columns of long text, `Title`, `URL`, `Referer` and `OriginalURL`, which hold 86% of
the decompressed bytes, set the scan on one thread. On 32 threads Parquet scans in 35.6 ms against
Vortex's 34.1, and writes in 265 ms where Vortex takes 1,243.

### The nested file

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.13 | 0.13 | 0.95 | 85.3 KiB | 26.3 KiB |
| `scan` | 1 | 48.5 | 95.5 | 1.97 | 304.1 KiB | 100.8 KiB |
| `project` | 1 | 1.09 | 0.52 | 0.48 | 307.4 KiB | 64.4 KiB |
| `take` | 1 | 52.0 | 95.9 | 1.84 | 351.9 KiB | 140.5 KiB |
| `write` | 1 | 1,014 | 874 | 0.86 | 1.3 MiB | 2.1 MiB |
| `open` | 32 | 0.13 | 0.12 | 0.89 | 85.3 KiB | 26.3 KiB |
| `scan` | 32 | 40.5 | 29.7 | 0.73 | 369.4 KiB | 244.2 KiB |
| `project` | 32 | 1.06 | 0.53 | 0.50 | 337.6 KiB | 65.3 KiB |
| `take` | 32 | 150 | 31.1 | 0.21 | 746.8 KiB | 283.9 KiB |
| `write` | 32 | 849 | 344 | 0.40 | 1.4 MiB | 3.8 MiB |

Parquet scans the lists and the text that seldom repeats in 95.5 ms on one thread against Vortex's
48.5, and in 29.7 on 32 against 40.5. It writes the file in 874 ms on one thread and in 344 on 32,
where Vortex takes 1,014 and 849.

## From one thread to 32

The scan and the write of each file at every degree, in milliseconds, from the same processes as the
tables above.

**Scan**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 159 | 21.9 | 151 | 66.0 | 95.5 | 48.5 |
| 2 | 73.9 | 24.3 | 79.7 | 69.6 | 45.7 | 53.1 |
| 4 | 40.7 | 18.8 | 48.6 | 43.5 | 31.5 | 38.1 |
| 8 | 31.6 | 23.2 | 39.0 | 32.2 | 30.3 | 34.4 |
| 16 | 22.7 | 27.5 | 36.5 | 30.2 | 30.6 | 36.4 |
| 32 | 21.2 | 28.3 | 35.6 | 34.1 | 29.7 | 40.5 |

**Write**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 608 | 223 | 1,214 | 1,347 | 874 | 1,014 |
| 2 | 235 | 187 | 538 | 1,270 | 501 | 852 |
| 4 | 175 | 180 | 329 | 1,230 | 407 | 794 |
| 8 | 191 | 182 | 277 | 1,309 | 370 | 808 |
| 16 | 155 | 193 | 268 | 1,239 | 353 | 822 |
| 32 | 148 | 201 | 265 | 1,243 | 344 | 849 |

* **Scans.** The taxi trips gain up to 16 threads: their 19 columns of GZIP pages decompress on every
  thread. A batch waits for its slowest column, so ClickBench's hits gain little past 8, waiting on
  `Title`, a third of their bytes once decompressed, and the nested file past 4, whose columns of
  lists decode side by side rather than ahead of the read.
* **Writes** gain most up to 8 threads, and a little more up to 32: the writer prepares a block's
  columns on up to eight threads and compresses its pages on all of them.

### A file read again

The tables map each file again at every call, as a first read does. A session keeps the files it
closed mapped, 64 of them unless `MappedFileCacheCount` says otherwise, so that a file opened again
skips the page faults of its mapping. Parquet's scans, in milliseconds:

| file | threads | mapped again | kept mapped |
|---|---:|---:|---:|
| the taxi trips | 1 | 159 | 147 |
| | 32 | 21.2 | 16.4 |
| ClickBench's hits | 1 | 151 | 117 |
| | 32 | 35.6 | 24.9 |
| the nested file | 1 | 95.5 | 72.0 |
| | 32 | 29.7 | 18.9 |

## Decompression

| file | codec | decompressed | ratio | Vorticity | .NET's `GZipStream` |
|---|---|---:|---:|---:|---:|
| the taxi trips | GZIP | 83.2 MB | 1.75 | 0.69 GB/s | 0.59 GB/s |
| ClickBench's hits | Snappy | 378.2 MB | 3.09 | 5.42 GB/s | |
| the nested file | Snappy | 46.2 MB | 1.89 | 1.63 GB/s | |

Every compressed page of each file, decompressed in memory one after another on one thread: the
median of 9 passes. Vorticity reads GZIP with its own DEFLATE decoder, which gives the same bytes as
.NET's on every page, 17% faster. Snappy, LZ4_RAW and ZSTD are the repository's own too, and BROTLI
is .NET's.

## Reproducing

From the repository root, the files at hand, `dotnet run -c Release --project
bench/Vorticity.Benchmarks --` and:

| figures | arguments |
|---|---|
| the report's table | `--format-cost` |
| a Parquet file, from one thread to 32 | `--format-cost --file <file.parquet> --degrees 1,2,4,8,16,32` |
| a file read again | `--format-cost --file <file.parquet> --warm-maps --degrees 1,32 --actions scan` |
| decompression | `--page-codecs <file.parquet>` |
| the nested file | `--nested-file <path>` writes it |

[tlc]: https://www.nyc.gov/site/tlc/about/tlc-trip-record-data.page
[clickbench]: https://github.com/ClickHouse/ClickBench
