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

* **On 32 threads, Parquet reads in 1.3 to 1.8 times Vortex's time**: the taxi trips in 31.0 ms
  against 17.3 for their Vortex copy, ClickBench's hits in 61.1 against 47.3, the nested file in 69.2
  against 46.0. Every value a scan returns is read, on the caller's thread.
* **On one thread, a compressed Parquet file pays for its codec**: the taxi trips, compressed with
  GZIP, read in 154 ms against 22.2, four fifths of it decompression. ClickBench's hits, compressed
  with Snappy, read in 163 against 72.3.
* **Parquet writes the report's table faster than Vortex**, in 0.70 of Vortex's time on one thread
  and 0.62 on 32, and ClickBench's hits from 8 threads, in 0.74 on 32. Vortex writes the taxi trips
  and the nested file faster at every degree. The taxi trips, whose GZIP pages Parquet reads first,
  take 586 ms on one thread against 206.
* **Threads pay up to 4** for every scan here but Parquet's taxi trips. Past them a scan takes what
  the caller's thread takes to read the values it is given.
* **A file read again reads faster**: a session keeps the files it closed mapped, and ClickBench's
  scan on 32 threads then takes 52.3 ms instead of 61.1.
* **A read allocates per call, not per row**: a scan of the taxi trips' three million rows allocates
  88 KiB.

## How it is measured

* **The machine.** An AMD Ryzen 9 7950X (Zen 4, 16 cores and 32 threads, AVX-512) under Windows 11,
  with .NET 11 (11.0.0-rc.3) and its JIT. The figures were measured on 2026-11-12, at commit
  `acd910ae`.
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
  figure is the median. Each file's figures come from one process, but the report's table's: its
  figures of a fraction of a millisecond move by as much as twice from one process to the next, so
  each is the median of five. The files are in the page cache,
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
| `open` | 1 | 0.13 | 0.13 | 0.94 | 3.1 KiB | 6.7 KiB |
| `scan` | 1 | 3.56 | 3.00 | 0.84 | 21.9 KiB | 47.0 KiB |
| `project` | 1 | 0.30 | 0.38 | 1.27 | 15.9 KiB | 45.0 KiB |
| `filter-narrow` | 1 | 0.31 | 0.24 | 0.78 | 51.6 KiB | 124.7 KiB |
| `filter-wide` | 1 | 1.69 | 1.69 | 1.00 | 32.9 KiB | 92.7 KiB |
| `take` | 1 | 4.05 | 3.56 | 0.88 | 62.1 KiB | 86.5 KiB |
| `write` | 1 | 24.4 | 17.2 | 0.70 | 41.4 KiB | 273.5 KiB |
| `open` | 32 | 0.12 | 0.12 | 1.04 | 3.1 KiB | 6.7 KiB |
| `scan` | 32 | 2.55 | 1.77 | 0.70 | 27.3 KiB | 77.5 KiB |
| `project` | 32 | 0.27 | 0.39 | 1.44 | 21.6 KiB | 45.9 KiB |
| `filter-narrow` | 32 | 0.29 | 0.24 | 0.84 | 52.3 KiB | 125.6 KiB |
| `filter-wide` | 32 | 1.55 | 1.69 | 1.09 | 35.8 KiB | 93.6 KiB |
| `take` | 32 | 1.79 | 2.38 | 1.33 | 87.2 KiB | 117.0 KiB |
| `write` | 32 | 11.7 | 7.25 | 0.62 | 56.4 KiB | 435.5 KiB |

Parquet's file is about half the size of Vortex's, 1.3 MB against 2.4. Parquet scans, writes and
reads the narrow filter in less time on one thread and on 32, and takes rows faster on one. Vortex
leads on the projection, and on 32 threads on the take and, by less, the wide filter: its 32-thread
figure lands between 1.2 and 1.8 ms from one process to the next.

### The taxi trips

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.18 | 0.12 | 0.66 | 7.5 KiB | 11.9 KiB |
| `scan` | 1 | 22.2 | 154 | 6.91 | 124.6 KiB | 88.1 KiB |
| `project` | 1 | 1.33 | 2.57 | 1.93 | 52.0 KiB | 55.1 KiB |
| `take` | 1 | 22.0 | 153 | 6.95 | 167.0 KiB | 127.5 KiB |
| `write` | 1 | 206 | 586 | 2.84 | 217.3 KiB | 1.4 MiB |
| `open` | 32 | 0.13 | 0.11 | 0.86 | 7.5 KiB | 11.9 KiB |
| `scan` | 32 | 17.3 | 31.0 | 1.79 | 125.7 KiB | 162.5 KiB |
| `project` | 32 | 1.12 | 2.58 | 2.31 | 63.3 KiB | 56.2 KiB |
| `take` | 32 | 18.3 | 24.6 | 1.34 | 187.8 KiB | 228.2 KiB |
| `write` | 32 | 87.8 | 152 | 1.73 | 243.0 KiB | 3.0 MiB |

GZIP sets the scan on one thread: inflating the pages takes 122 ms of its 154
([decompression](#decompression)). On 32 threads the pages inflate on every thread ahead of the read.
The projection reads one small column, which more threads do not speed up: 2.6 ms on one thread as
on 32.

### ClickBench's hits

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.17 | 0.17 | 0.98 | 25.9 KiB | 55.3 KiB |
| `scan` | 1 | 72.3 | 163 | 2.26 | 1.4 MiB | 368.3 KiB |
| `project` | 1 | 1.43 | 1.01 | 0.71 | 434.7 KiB | 148.8 KiB |
| `take` | 1 | 55.4 | 133 | 2.39 | 1.4 MiB | 390.5 KiB |
| `write` | 1 | 817 | 1,127 | 1.38 | 1.9 MiB | 4.0 MiB |
| `open` | 32 | 0.17 | 0.16 | 0.95 | 25.9 KiB | 55.3 KiB |
| `scan` | 32 | 47.3 | 61.1 | 1.29 | 1.4 MiB | 488.9 KiB |
| `project` | 32 | 1.39 | 1.08 | 0.78 | 435.4 KiB | 151.8 KiB |
| `take` | 32 | 28.7 | 41.1 | 1.43 | 1.4 MiB | 524.1 KiB |
| `write` | 32 | 338 | 250 | 0.74 | 1.9 MiB | 7.1 MiB |

Snappy and four columns of long text, `Title`, `URL`, `Referer` and `OriginalURL`, which hold 86% of
the decompressed bytes, set the scan on one thread. On 32 threads Parquet scans in 61.1 ms against
Vortex's 47.3, most of it the caller reading those texts, and writes in 250 ms where Vortex takes
338.

### The nested file

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.15 | 0.13 | 0.83 | 7.7 KiB | 26.5 KiB |
| `scan` | 1 | 53.4 | 112 | 2.09 | 331.1 KiB | 470.7 KiB |
| `project` | 1 | 0.51 | 0.60 | 1.19 | 124.3 KiB | 75.9 KiB |
| `take` | 1 | 35.4 | 74.4 | 2.10 | 371.3 KiB | 506.6 KiB |
| `write` | 1 | 675 | 839 | 1.24 | 476.6 KiB | 1.7 MiB |
| `open` | 32 | 0.13 | 0.12 | 0.93 | 7.7 KiB | 26.5 KiB |
| `scan` | 32 | 46.0 | 69.2 | 1.50 | 333.0 KiB | 474.6 KiB |
| `project` | 32 | 0.48 | 0.61 | 1.28 | 125.0 KiB | 76.8 KiB |
| `take` | 32 | 25.5 | 32.6 | 1.28 | 377.9 KiB | 512.5 KiB |
| `write` | 32 | 290 | 370 | 1.28 | 570.5 KiB | 2.9 MiB |

Parquet scans the lists and the text that seldom repeats in 112 ms on one thread against Vortex's
53.4, and in 69.2 on 32 against 46.0. It writes the file in 839 ms on one thread and in 370 on 32,
where Vortex takes 675 and 290.

## From one thread to 32

The scan and the write of each file at every degree, in milliseconds, from the same processes as the
tables above.

**Scan**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 154 | 22.2 | 163 | 72.3 | 112 | 53.4 |
| 2 | 70.6 | 17.8 | 86.4 | 73.5 | 78.5 | 54.2 |
| 4 | 41.8 | 16.7 | 59.7 | 44.8 | 68.5 | 44.4 |
| 8 | 36.6 | 17.0 | 57.2 | 47.0 | 70.0 | 47.4 |
| 16 | 33.0 | 16.9 | 60.6 | 46.7 | 69.0 | 46.1 |
| 32 | 31.0 | 17.3 | 61.1 | 47.3 | 69.2 | 46.0 |

**Write**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 586 | 206 | 1,127 | 817 | 839 | 675 |
| 2 | 228 | 120 | 477 | 463 | 482 | 382 |
| 4 | 191 | 77.8 | 307 | 285 | 414 | 236 |
| 8 | 192 | 77.5 | 266 | 297 | 372 | 263 |
| 16 | 161 | 86.1 | 251 | 319 | 369 | 281 |
| 32 | 152 | 87.8 | 250 | 338 | 370 | 290 |

* **Scans** gain up to 4 threads, and little or nothing past them. The values are read on the
  caller's thread, as a loop over a scan reads them, so once the pages decode ahead of that thread a
  scan takes what it takes to read them: about 17 ms for the taxi trips' Vortex copy, and 45 to 70
  ms for ClickBench's hits and the nested file in either format. Parquet's taxi trips keep gaining
  to 32 threads, their 19 columns of GZIP pages inflating on every one of them.
* **Writes** gain most up to 4 threads: the writer prepares a block's columns on up to eight threads
  and compresses its pages on all of them. Past 4, Parquet's writes still gain a little and Vortex's
  lose a little, the hits most: 285 ms on 4 threads, 338 on 32.

### A file read again

The tables open each file again at every call, as a first read does. A session keeps the files it
closed mapped, 64 of them unless `MappedFileCacheCount` says otherwise, so that a file opened again is
read where it lies, its pages already mapped. Parquet's scans, in milliseconds:

| file | threads | opened again | kept mapped |
|---|---:|---:|---:|
| the taxi trips | 1 | 154 | 151 |
| | 32 | 31.0 | 25.0 |
| ClickBench's hits | 1 | 163 | 156 |
| | 32 | 61.1 | 52.3 |
| the nested file | 1 | 112 | 110 |
| | 32 | 69.2 | 59.3 |

## Decompression

| file | codec | decompressed | ratio | Vorticity | .NET's `GZipStream` |
|---|---|---:|---:|---:|---:|
| the taxi trips | GZIP | 83.2 MB | 1.75 | 0.68 GB/s | 0.59 GB/s |
| ClickBench's hits | Snappy | 378.2 MB | 3.09 | 5.26 GB/s | |
| the nested file | Snappy | 46.2 MB | 1.89 | 1.56 GB/s | |

Every compressed page of each file, decompressed in memory one after another on one thread: the
median of 9 passes. Vorticity reads GZIP with its own DEFLATE decoder, which gives the same bytes as
.NET's on every page, 15% faster. Snappy is the repository's own too, ZSTD and LZ4_RAW come from
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
