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

* **On 32 threads, Parquet reads in 1.1 to 1.7 times Vortex's time**: the taxi trips in 37.7 ms
  against 22.2 for their Vortex copy, ClickBench's hits in 74.3 against 67.5, the nested file in 76.3
  against 62.0. Every value a scan returns is read, on the caller's thread.
* **On one thread, a compressed Parquet file pays for its codec**: the taxi trips, compressed with
  GZIP, read in 158 ms against 20.1, three quarters of it decompression. ClickBench's hits, compressed
  with Snappy, read in 174 against 78.8.
* **Parquet writes the report's table faster than Vortex**, in 0.65 of Vortex's time on one thread
  and 0.67 on 32, and ClickBench's hits on 32 threads, in 0.76. Vortex writes the taxi trips and the
  nested file faster at every degree. The taxi trips, whose GZIP pages Parquet reads first, take 601
  ms on one thread against 230.
* **Threads pay up to 4** for every scan here. Past them a scan takes what the caller's thread takes
  to read the values it is given.
* **A file read again reads faster**: a session keeps the files it closed mapped, and ClickBench's
  scan on 32 threads then takes 62.9 ms instead of 74.3.
* **A read allocates per call, not per row**: a scan of the taxi trips' three million rows allocates
  82 KiB.

## How it is measured

* **The machine.** An AMD Ryzen 9 7950X (Zen 4, 16 cores and 32 threads, AVX-512) under Windows 11,
  with .NET 11 (11.0.0-rc.1) and its JIT.
* **The same work on both formats**, in one process, through the same scan: only the open and the
  write are each format's own, and both must return the same rows. Every action reads every value
  it returns, on the caller's thread, as a loop over a scan reads them: a string's bytes, a list's
  elements, and for a column Parquet hands out as a dictionary, each row's value through its code.
  The **ratio** is Parquet's time over Vortex's: under 1.00, Parquet took less.
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
* **Vortex is the yardstick**, measured the same way. [benchmarks-x64.md](benchmarks-x64.md)
  measures it on its own.

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
| `open` | 1 | 0.13 | 0.13 | 0.98 | 3.0 KiB | 6.6 KiB |
| `scan` | 1 | 4.03 | 3.25 | 0.81 | 22.3 KiB | 45.4 KiB |
| `project` | 1 | 0.32 | 0.38 | 1.18 | 16.8 KiB | 43.5 KiB |
| `filter-narrow` | 1 | 0.31 | 0.25 | 0.80 | 50.3 KiB | 122.6 KiB |
| `filter-wide` | 1 | 1.72 | 1.78 | 1.03 | 32.6 KiB | 90.6 KiB |
| `take` | 1 | 4.12 | 3.52 | 0.85 | 70.4 KiB | 84.8 KiB |
| `write` | 1 | 25.1 | 16.2 | 0.65 | 59.1 KiB | 406.9 KiB |
| `open` | 32 | 0.12 | 0.12 | 1.00 | 3.0 KiB | 6.6 KiB |
| `scan` | 32 | 2.58 | 2.42 | 0.93 | 33.0 KiB | 84.3 KiB |
| `project` | 32 | 0.26 | 0.38 | 1.49 | 31.3 KiB | 44.3 KiB |
| `filter-narrow` | 32 | 0.29 | 0.24 | 0.84 | 51.0 KiB | 123.5 KiB |
| `filter-wide` | 32 | 1.14 | 1.77 | 1.56 | 37.9 KiB | 91.4 KiB |
| `take` | 32 | 1.93 | 2.35 | 1.22 | 118.9 KiB | 123.8 KiB |
| `write` | 32 | 10.7 | 7.20 | 0.67 | 69.8 KiB | 595.1 KiB |

Parquet's file is about half the size of Vortex's, 1.3 MB against 2.4. Parquet scans, writes and
reads the narrow filter in less time on one thread and on 32, and takes rows faster on one. Vortex
leads on the projection, and on 32 threads on the wide filter and the take.

### The taxi trips

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.19 | 0.12 | 0.62 | 7.4 KiB | 11.7 KiB |
| `scan` | 1 | 20.1 | 158 | 7.86 | 126.1 KiB | 82.2 KiB |
| `project` | 1 | 1.33 | 2.62 | 1.96 | 52.7 KiB | 49.9 KiB |
| `take` | 1 | 23.1 | 156 | 6.77 | 173.6 KiB | 121.6 KiB |
| `write` | 1 | 230 | 601 | 2.62 | 19.9 MiB | 2.3 MiB |
| `open` | 32 | 0.13 | 0.11 | 0.85 | 7.4 KiB | 11.7 KiB |
| `scan` | 32 | 22.2 | 37.7 | 1.70 | 128.9 KiB | 128.1 KiB |
| `project` | 32 | 1.09 | 2.63 | 2.42 | 83.0 KiB | 51.0 KiB |
| `take` | 32 | 17.5 | 24.9 | 1.42 | 245.0 KiB | 167.4 KiB |
| `write` | 32 | 89.4 | 139 | 1.55 | 19.4 MiB | 4.3 MiB |

GZIP sets the scan on one thread: inflating the pages takes 121 ms of its 158
([decompression](#decompression)). On 32 threads the pages inflate on every thread ahead of the read.
The projection reads one small column, which more threads do not speed up: 2.6 ms on one thread as
on 32.

### ClickBench's hits

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.18 | 0.17 | 0.95 | 25.9 KiB | 54.4 KiB |
| `scan` | 1 | 78.8 | 174 | 2.21 | 1.4 MiB | 313.6 KiB |
| `project` | 1 | 1.47 | 1.07 | 0.73 | 435.0 KiB | 98.0 KiB |
| `take` | 1 | 55.2 | 132 | 2.40 | 1.4 MiB | 335.8 KiB |
| `write` | 1 | 836 | 1,183 | 1.41 | 35.1 MiB | 6.4 MiB |
| `open` | 32 | 0.18 | 0.17 | 0.90 | 25.9 KiB | 54.4 KiB |
| `scan` | 32 | 67.5 | 74.3 | 1.10 | 1.6 MiB | 482.4 KiB |
| `project` | 32 | 1.40 | 1.04 | 0.74 | 435.7 KiB | 101.7 KiB |
| `take` | 32 | 27.9 | 41.2 | 1.48 | 1.6 MiB | 503.3 KiB |
| `write` | 32 | 355 | 271 | 0.76 | 27.1 MiB | 10.2 MiB |

Snappy and four columns of long text, `Title`, `URL`, `Referer` and `OriginalURL`, which hold 86% of
the decompressed bytes, set the scan on one thread. On 32 threads Parquet scans in 74.3 ms against
Vortex's 67.5, most of it the caller reading those texts, and writes in 271 ms where Vortex takes
355.

### The nested file

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.16 | 0.13 | 0.86 | 7.7 KiB | 26.3 KiB |
| `scan` | 1 | 57.1 | 119 | 2.08 | 332.0 KiB | 458.5 KiB |
| `project` | 1 | 0.49 | 0.60 | 1.21 | 124.5 KiB | 64.8 KiB |
| `take` | 1 | 35.4 | 75.8 | 2.14 | 379.3 KiB | 494.5 KiB |
| `write` | 1 | 681 | 849 | 1.25 | 1.3 MiB | 2.5 MiB |
| `open` | 32 | 0.13 | 0.12 | 0.92 | 7.7 KiB | 26.3 KiB |
| `scan` | 32 | 62.0 | 76.3 | 1.23 | 340.3 KiB | 459.8 KiB |
| `project` | 32 | 0.54 | 0.61 | 1.14 | 125.2 KiB | 65.7 KiB |
| `take` | 32 | 38.2 | 32.9 | 0.86 | 516.4 KiB | 496.5 KiB |
| `write` | 32 | 282 | 322 | 1.14 | 1.4 MiB | 4.0 MiB |

Parquet scans the lists and the text that seldom repeats in 119 ms on one thread against Vortex's
57.1, and in 76.3 on 32 against 62.0. It writes the file in 849 ms on one thread and in 322 on 32,
where Vortex takes 681 and 282.

## From one thread to 32

The scan and the write of each file at every degree, in milliseconds, from the same processes as the
tables above.

**Scan**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 158 | 20.1 | 174 | 78.8 | 119 | 57.1 |
| 2 | 77.0 | 19.0 | 84.5 | 78.1 | 85.6 | 57.4 |
| 4 | 44.2 | 18.8 | 70.7 | 52.3 | 74.8 | 50.3 |
| 8 | 45.0 | 19.1 | 70.5 | 58.0 | 75.6 | 57.2 |
| 16 | 38.8 | 19.3 | 75.3 | 67.8 | 75.2 | 62.3 |
| 32 | 37.7 | 22.2 | 74.3 | 67.5 | 76.3 | 62.0 |

**Write**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 601 | 230 | 1,183 | 836 | 849 | 681 |
| 2 | 239 | 133 | 519 | 468 | 477 | 367 |
| 4 | 180 | 88.2 | 329 | 285 | 375 | 239 |
| 8 | 190 | 87.3 | 273 | 296 | 336 | 247 |
| 16 | 155 | 87.2 | 258 | 331 | 320 | 284 |
| 32 | 139 | 89.4 | 271 | 355 | 322 | 282 |

* **Scans** gain up to 4 threads, and little or nothing past them. The values are read on the
  caller's thread, as a loop over a scan reads them, so once the pages decode ahead of that thread a
  scan takes what it takes to read them: about 19 ms for the taxi trips' Vortex copy, and 50 to 76
  ms for ClickBench's hits and the nested file in either format. Parquet's taxi trips keep gaining
  to 16 threads, their 19 columns of GZIP pages inflating on every one of them.
* **Writes** gain most up to 4 threads, and less past it: the writer prepares a block's columns on up
  to eight threads and compresses its pages on all of them.

### A file read again

The tables open each file again at every call, as a first read does. A session keeps the files it
closed mapped, 64 of them unless `MappedFileCacheCount` says otherwise, so that a file opened again is
read where it lies, its pages already mapped. Parquet's scans, in milliseconds:

| file | threads | opened again | kept mapped |
|---|---:|---:|---:|
| the taxi trips | 1 | 158 | 150 |
| | 32 | 37.7 | 31.2 |
| ClickBench's hits | 1 | 174 | 169 |
| | 32 | 74.3 | 62.9 |
| the nested file | 1 | 119 | 117 |
| | 32 | 76.3 | 64.9 |

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
