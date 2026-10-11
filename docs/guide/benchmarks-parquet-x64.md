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

* **On 32 threads, Parquet reads in 1.1 to 1.4 times Vortex's time**: the taxi trips in 21.9 ms
  against 15.3 for their Vortex copy, ClickBench's hits in 33.6 against 28.9, the nested file in 31.1
  against 29.6.
* **On one thread, a compressed Parquet file pays for its codec**: the taxi trips, compressed with
  GZIP, read in 148 ms against 21.8, four fifths of it decompression. ClickBench's hits, compressed
  with Snappy, read in 121 against 54.2.
* **Parquet writes the report's table faster than Vortex**, in 0.70 of Vortex's time on one thread
  and 0.68 on 32, and ClickBench's hits on 32 threads, in 0.74. Vortex writes the taxi trips and the
  nested file faster at every degree. The taxi trips, whose GZIP pages Parquet reads first, take 592
  ms on one thread against 225.
* **Threads pay** for Parquet's scans up to 32 on the taxi trips, 8 on ClickBench's hits and 4 on the
  nested file.
* **A file read again reads faster**: a session keeps the files it closed mapped, and ClickBench's
  scan on 32 threads then takes 23.8 ms instead of 33.6.
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
  and each call opens its file again, as a first read does. The session keeps no mapping, so on
  Windows each call copies the segments it reads rather than mapping the file, as it does for every
  file on this page.
* **Threads** are the scan's or the writer's degree of parallelism (`DegreeOfParallelism`), 1 unless
  set, and 32 is every hardware thread of the machine.
* **Allocated** is what a call allocates on the managed heap once warm.
* **Vortex is the yardstick**, measured the same way. Its times move more than Parquet's from one
  process to the next: its scan of the taxi trips on one thread took 22 to 36 ms over four runs.
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
| `open` | 1 | 0.13 | 0.14 | 1.03 | 3.0 KiB | 6.6 KiB |
| `scan` | 1 | 4.74 | 2.57 | 0.54 | 22.3 KiB | 45.4 KiB |
| `project` | 1 | 0.23 | 0.32 | 1.39 | 16.8 KiB | 43.5 KiB |
| `filter-narrow` | 1 | 0.29 | 0.24 | 0.82 | 50.1 KiB | 122.6 KiB |
| `filter-wide` | 1 | 1.63 | 1.45 | 0.89 | 32.6 KiB | 90.6 KiB |
| `take` | 1 | 3.69 | 3.03 | 0.82 | 70.5 KiB | 84.8 KiB |
| `write` | 1 | 25.1 | 17.7 | 0.70 | 59.1 KiB | 406.9 KiB |
| `open` | 32 | 0.12 | 0.12 | 1.07 | 3.0 KiB | 6.6 KiB |
| `scan` | 32 | 1.45 | 1.28 | 0.88 | 33.0 KiB | 84.3 KiB |
| `project` | 32 | 0.24 | 0.33 | 1.38 | 31.3 KiB | 44.3 KiB |
| `filter-narrow` | 32 | 0.27 | 0.24 | 0.88 | 51.0 KiB | 123.5 KiB |
| `filter-wide` | 32 | 0.62 | 1.45 | 2.34 | 37.9 KiB | 91.4 KiB |
| `take` | 32 | 1.37 | 1.83 | 1.34 | 119.3 KiB | 123.8 KiB |
| `write` | 32 | 10.5 | 7.12 | 0.68 | 69.8 KiB | 595.2 KiB |

Parquet's file is about half the size of Vortex's, 1.3 MB against 2.4. Parquet scans, writes and
reads the narrow filter in less time on one thread and on 32, and takes rows faster on one. Vortex
leads on the projection, and on 32 threads on the wide filter and the take.

### The taxi trips

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.19 | 0.11 | 0.58 | 7.4 KiB | 11.7 KiB |
| `scan` | 1 | 21.8 | 148 | 6.82 | 126.1 KiB | 82.2 KiB |
| `project` | 1 | 1.25 | 2.44 | 1.94 | 52.7 KiB | 49.9 KiB |
| `take` | 1 | 20.6 | 152 | 7.38 | 173.2 KiB | 121.6 KiB |
| `write` | 1 | 225 | 592 | 2.63 | 19.9 MiB | 2.3 MiB |
| `open` | 32 | 0.14 | 0.11 | 0.81 | 7.4 KiB | 11.7 KiB |
| `scan` | 32 | 15.3 | 21.9 | 1.43 | 128.9 KiB | 128.1 KiB |
| `project` | 32 | 1.06 | 2.44 | 2.29 | 83.0 KiB | 51.0 KiB |
| `take` | 32 | 17.9 | 22.4 | 1.25 | 241.2 KiB | 167.7 KiB |
| `write` | 32 | 89.1 | 143 | 1.60 | 19.4 MiB | 4.3 MiB |

GZIP sets the scan on one thread: inflating the pages takes 121 ms of its 148
([decompression](#decompression)). On 32 threads the pages inflate on every thread ahead of the read.
The projection reads one small column, which more threads do not speed up: 2.4 ms on one thread as
on 32.

### ClickBench's hits

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.18 | 0.17 | 0.94 | 25.9 KiB | 54.4 KiB |
| `scan` | 1 | 54.2 | 121 | 2.24 | 1.4 MiB | 313.6 KiB |
| `project` | 1 | 1.30 | 0.96 | 0.73 | 435.0 KiB | 98.0 KiB |
| `take` | 1 | 53.2 | 129 | 2.41 | 1.4 MiB | 335.8 KiB |
| `write` | 1 | 826 | 1,209 | 1.46 | 35.1 MiB | 6.4 MiB |
| `open` | 32 | 0.17 | 0.17 | 0.97 | 25.9 KiB | 54.4 KiB |
| `scan` | 32 | 28.9 | 33.6 | 1.16 | 1.6 MiB | 481.7 KiB |
| `project` | 32 | 1.34 | 1.00 | 0.75 | 435.7 KiB | 101.7 KiB |
| `take` | 32 | 28.3 | 36.8 | 1.30 | 1.6 MiB | 503.0 KiB |
| `write` | 32 | 354 | 264 | 0.74 | 27.1 MiB | 10.2 MiB |

Snappy and four columns of long text, `Title`, `URL`, `Referer` and `OriginalURL`, which hold 86% of
the decompressed bytes, set the scan on one thread. On 32 threads Parquet scans in 33.6 ms against
Vortex's 28.9, and writes in 264 ms where Vortex takes 354.

### The nested file

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.14 | 0.14 | 1.00 | 7.7 KiB | 26.3 KiB |
| `scan` | 1 | 35.1 | 73.0 | 2.08 | 332.0 KiB | 458.5 KiB |
| `project` | 1 | 0.36 | 0.49 | 1.36 | 124.5 KiB | 64.8 KiB |
| `take` | 1 | 34.0 | 74.5 | 2.19 | 379.3 KiB | 494.9 KiB |
| `write` | 1 | 678 | 858 | 1.27 | 1.3 MiB | 2.5 MiB |
| `open` | 32 | 0.14 | 0.12 | 0.91 | 7.7 KiB | 26.3 KiB |
| `scan` | 32 | 29.6 | 31.1 | 1.05 | 340.3 KiB | 461.1 KiB |
| `project` | 32 | 0.39 | 0.51 | 1.30 | 125.2 KiB | 65.6 KiB |
| `take` | 32 | 36.8 | 31.2 | 0.85 | 513.5 KiB | 497.1 KiB |
| `write` | 32 | 287 | 326 | 1.14 | 1.3 MiB | 4.0 MiB |

Parquet scans the lists and the text that seldom repeats in 73.0 ms on one thread against Vortex's
35.1, and in 31.1 on 32 against 29.6. It writes the file in 858 ms on one thread and in 326 on 32,
where Vortex takes 678 and 287.

## From one thread to 32

The scan and the write of each file at every degree, in milliseconds, from the same processes as the
tables above.

**Scan**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 148 | 21.8 | 121 | 54.2 | 73.0 | 35.1 |
| 2 | 74.0 | 15.4 | 72.7 | 55.2 | 39.4 | 36.3 |
| 4 | 40.4 | 14.6 | 40.6 | 31.7 | 29.8 | 22.9 |
| 8 | 28.5 | 15.2 | 31.2 | 31.5 | 31.0 | 28.5 |
| 16 | 25.8 | 15.7 | 32.1 | 37.1 | 29.9 | 33.1 |
| 32 | 21.9 | 15.3 | 33.6 | 28.9 | 31.1 | 29.6 |

**Write**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 592 | 225 | 1,209 | 826 | 858 | 678 |
| 2 | 238 | 129 | 522 | 470 | 485 | 367 |
| 4 | 179 | 84.8 | 330 | 285 | 384 | 231 |
| 8 | 191 | 73.9 | 277 | 311 | 344 | 244 |
| 16 | 159 | 92.5 | 259 | 334 | 332 | 284 |
| 32 | 143 | 89.1 | 264 | 354 | 326 | 287 |

* **Scans.** The taxi trips gain up to 32 threads: their 19 columns of GZIP pages decompress on every
  thread. A batch waits for its slowest column, so ClickBench's hits gain little past 8, waiting on
  `Title`, a third of their bytes once decompressed, and the nested file past 4, whose columns of
  lists decode side by side rather than ahead of the read.
* **Writes** gain most up to 4 threads, and less past it: the writer prepares a block's columns on up
  to eight threads and compresses its pages on all of them.

### A file read again

The tables open each file again at every call, as a first read does. A session keeps the files it
closed mapped, 64 of them unless `MappedFileCacheCount` says otherwise, so that a file opened again is
read where it lies, its pages already mapped. Parquet's scans, in milliseconds:

| file | threads | opened again | kept mapped |
|---|---:|---:|---:|
| the taxi trips | 1 | 148 | 144 |
| | 32 | 21.9 | 17.7 |
| ClickBench's hits | 1 | 121 | 116 |
| | 32 | 33.6 | 23.8 |
| the nested file | 1 | 73.0 | 72.8 |
| | 32 | 31.1 | 18.7 |

## Decompression

| file | codec | decompressed | ratio | Vorticity | .NET's `GZipStream` |
|---|---|---:|---:|---:|---:|
| the taxi trips | GZIP | 83.2 MB | 1.75 | 0.69 GB/s | 0.58 GB/s |
| ClickBench's hits | Snappy | 378.2 MB | 3.09 | 5.28 GB/s | |
| the nested file | Snappy | 46.2 MB | 1.89 | 1.56 GB/s | |

Every compressed page of each file, decompressed in memory one after another on one thread: the
median of 9 passes. Vorticity reads GZIP with its own DEFLATE decoder, which gives the same bytes as
.NET's on every page, 19% faster. Snappy is the repository's own too, ZSTD and LZ4_RAW come from
the managed codecs of ManagedCompressions, and BROTLI is .NET's.

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
