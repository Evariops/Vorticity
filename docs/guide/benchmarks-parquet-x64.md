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

* **On 32 threads, Parquet reads in 1.2 to 2.0 times Vortex's time**: the taxi trips in 35.0 ms
  against 17.4 for their Vortex copy, ClickBench's hits in 59.9 against 48.1, the nested file in 69.7
  against 46.3. Every value a scan returns is read, on the caller's thread.
* **On one thread, a compressed Parquet file pays for its codec**: the taxi trips, compressed with
  GZIP, read in 152 ms against 25.3, four fifths of it decompression. ClickBench's hits, compressed
  with Snappy, read in 165 against 73.4.
* **Parquet writes the report's table faster than Vortex**, in 0.69 of Vortex's time on one thread
  and 0.73 on 32, and ClickBench's hits from 8 threads, in 0.78 on 32. Vortex writes the taxi trips
  and the nested file faster at every degree. The taxi trips, whose GZIP pages Parquet reads first,
  take 586 ms on one thread against 216.
* **Threads pay up to 4** for every scan here but Parquet's taxi trips. Past them a scan takes what
  the caller's thread takes to read the values it is given.
* **A file read again reads faster**: a session keeps the files it closed mapped, and ClickBench's
  scan on 32 threads then takes 54.5 ms instead of 59.9.
* **A read allocates per call, not per row**: a scan of the taxi trips' three million rows allocates
  83 KiB.

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
| `open` | 1 | 0.14 | 0.13 | 0.90 | 3.0 KiB | 6.6 KiB |
| `scan` | 1 | 3.71 | 2.98 | 0.80 | 22.7 KiB | 45.8 KiB |
| `project` | 1 | 0.31 | 0.38 | 1.21 | 17.2 KiB | 43.9 KiB |
| `filter-narrow` | 1 | 0.29 | 0.25 | 0.84 | 52.1 KiB | 123.5 KiB |
| `filter-wide` | 1 | 1.67 | 1.69 | 1.01 | 33.8 KiB | 91.5 KiB |
| `take` | 1 | 4.18 | 3.53 | 0.85 | 70.8 KiB | 85.2 KiB |
| `write` | 1 | 24.9 | 17.3 | 0.69 | 60.7 KiB | 407.3 KiB |
| `open` | 32 | 0.12 | 0.12 | 1.03 | 3.0 KiB | 6.6 KiB |
| `scan` | 32 | 2.69 | 1.75 | 0.65 | 33.5 KiB | 84.8 KiB |
| `project` | 32 | 0.27 | 0.38 | 1.43 | 31.7 KiB | 44.8 KiB |
| `filter-narrow` | 32 | 0.30 | 0.24 | 0.81 | 52.8 KiB | 124.4 KiB |
| `filter-wide` | 32 | 0.91 | 1.69 | 1.86 | 39.1 KiB | 92.3 KiB |
| `take` | 32 | 1.79 | 2.38 | 1.33 | 119.4 KiB | 124.2 KiB |
| `write` | 32 | 10.5 | 7.60 | 0.73 | 71.5 KiB | 595.5 KiB |

Parquet's file is about half the size of Vortex's, 1.3 MB against 2.4. Parquet scans, writes and
reads the narrow filter in less time on one thread and on 32, and takes rows faster on one. Vortex
leads on the projection, and on 32 threads on the wide filter and the take.

### The taxi trips

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.19 | 0.12 | 0.62 | 7.5 KiB | 11.7 KiB |
| `scan` | 1 | 25.3 | 152 | 6.01 | 126.7 KiB | 82.6 KiB |
| `project` | 1 | 1.31 | 2.63 | 2.00 | 53.3 KiB | 50.3 KiB |
| `take` | 1 | 21.8 | 150 | 6.87 | 173.9 KiB | 122.0 KiB |
| `write` | 1 | 216 | 586 | 2.72 | 1.1 MiB | 2.3 MiB |
| `open` | 32 | 0.13 | 0.11 | 0.85 | 7.5 KiB | 11.7 KiB |
| `scan` | 32 | 17.4 | 35.0 | 2.01 | 127.6 KiB | 129.4 KiB |
| `project` | 32 | 0.95 | 2.62 | 2.75 | 83.5 KiB | 51.4 KiB |
| `take` | 32 | 18.9 | 22.4 | 1.19 | 232.7 KiB | 168.0 KiB |
| `write` | 32 | 83.6 | 137 | 1.64 | 600.0 KiB | 4.3 MiB |

GZIP sets the scan on one thread: inflating the pages takes 122 ms of its 152
([decompression](#decompression)). On 32 threads the pages inflate on every thread ahead of the read.
The projection reads one small column, which more threads do not speed up: 2.6 ms on one thread as
on 32.

### ClickBench's hits

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.19 | 0.17 | 0.89 | 25.9 KiB | 54.4 KiB |
| `scan` | 1 | 73.4 | 165 | 2.25 | 1.4 MiB | 314.0 KiB |
| `project` | 1 | 1.50 | 0.98 | 0.66 | 435.4 KiB | 98.5 KiB |
| `take` | 1 | 54.6 | 130 | 2.38 | 1.4 MiB | 336.2 KiB |
| `write` | 1 | 824 | 1,171 | 1.42 | 12.6 MiB | 6.3 MiB |
| `open` | 32 | 0.17 | 0.17 | 0.97 | 25.9 KiB | 54.4 KiB |
| `scan` | 32 | 48.1 | 59.9 | 1.24 | 1.4 MiB | 446.6 KiB |
| `project` | 32 | 2.28 | 1.02 | 0.45 | 436.1 KiB | 102.1 KiB |
| `take` | 32 | 27.1 | 39.8 | 1.47 | 1.5 MiB | 468.5 KiB |
| `write` | 32 | 339 | 266 | 0.78 | 4.2 MiB | 10.3 MiB |

Snappy and four columns of long text, `Title`, `URL`, `Referer` and `OriginalURL`, which hold 86% of
the decompressed bytes, set the scan on one thread. On 32 threads Parquet scans in 59.9 ms against
Vortex's 48.1, most of it the caller reading those texts, and writes in 266 ms where Vortex takes
339.

### The nested file

| action | threads | Vortex, ms | Parquet, ms | ratio | Vortex, allocated | Parquet, allocated |
|---|---:|---:|---:|---:|---:|---:|
| `open` | 1 | 0.15 | 0.13 | 0.85 | 7.7 KiB | 26.3 KiB |
| `scan` | 1 | 54.4 | 113 | 2.07 | 332.4 KiB | 459.2 KiB |
| `project` | 1 | 0.55 | 0.60 | 1.09 | 124.9 KiB | 65.3 KiB |
| `take` | 1 | 35.8 | 76.6 | 2.14 | 380.0 KiB | 495.2 KiB |
| `write` | 1 | 694 | 841 | 1.21 | 1.2 MiB | 2.5 MiB |
| `open` | 32 | 0.13 | 0.12 | 0.96 | 7.7 KiB | 26.3 KiB |
| `scan` | 32 | 46.3 | 69.7 | 1.50 | 335.5 KiB | 460.4 KiB |
| `project` | 32 | 0.48 | 0.62 | 1.27 | 125.6 KiB | 66.1 KiB |
| `take` | 32 | 26.5 | 32.7 | 1.24 | 392.8 KiB | 497.2 KiB |
| `write` | 32 | 272 | 331 | 1.21 | 1.2 MiB | 4.0 MiB |

Parquet scans the lists and the text that seldom repeats in 113 ms on one thread against Vortex's
54.4, and in 69.7 on 32 against 46.3. It writes the file in 841 ms on one thread and in 331 on 32,
where Vortex takes 694 and 272.

## From one thread to 32

The scan and the write of each file at every degree, in milliseconds, from the same processes as the
tables above.

**Scan**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 152 | 25.3 | 165 | 73.4 | 113 | 54.4 |
| 2 | 74.1 | 18.0 | 76.7 | 73.3 | 79.4 | 54.9 |
| 4 | 41.0 | 16.9 | 57.9 | 45.5 | 69.5 | 44.7 |
| 8 | 44.3 | 16.9 | 58.4 | 47.6 | 70.4 | 46.3 |
| 16 | 39.1 | 17.3 | 59.7 | 47.8 | 69.8 | 46.1 |
| 32 | 35.0 | 17.4 | 59.9 | 48.1 | 69.7 | 46.3 |

**Write**

| threads | taxi trips, Parquet | taxi trips, Vortex | ClickBench, Parquet | ClickBench, Vortex | nested, Parquet | nested, Vortex |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 586 | 216 | 1,171 | 824 | 841 | 694 |
| 2 | 236 | 123 | 510 | 465 | 479 | 368 |
| 4 | 175 | 78.7 | 314 | 281 | 388 | 225 |
| 8 | 188 | 66.9 | 256 | 292 | 346 | 237 |
| 16 | 153 | 83.3 | 250 | 320 | 322 | 273 |
| 32 | 137 | 83.6 | 266 | 339 | 331 | 272 |

* **Scans** gain up to 4 threads, and little or nothing past them. The values are read on the
  caller's thread, as a loop over a scan reads them, so once the pages decode ahead of that thread a
  scan takes what it takes to read them: about 17 ms for the taxi trips' Vortex copy, and 45 to 70
  ms for ClickBench's hits and the nested file in either format. Parquet's taxi trips keep gaining
  to 32 threads, their 19 columns of GZIP pages inflating on every one of them.
* **Writes** gain most up to 4 threads: the writer prepares a block's columns on up to eight threads
  and compresses its pages on all of them. Past 4, Parquet's writes still gain a little and Vortex's
  lose a little, the hits most: 281 ms on 4 threads, 339 on 32.

### A file read again

The tables open each file again at every call, as a first read does. A session keeps the files it
closed mapped, 64 of them unless `MappedFileCacheCount` says otherwise, so that a file opened again is
read where it lies, its pages already mapped. Parquet's scans, in milliseconds:

| file | threads | opened again | kept mapped |
|---|---:|---:|---:|
| the taxi trips | 1 | 152 | 152 |
| | 32 | 35.0 | 31.1 |
| ClickBench's hits | 1 | 165 | 157 |
| | 32 | 59.9 | 54.5 |
| the nested file | 1 | 113 | 112 |
| | 32 | 69.7 | 59.3 |

## Decompression

| file | codec | decompressed | ratio | Vorticity | .NET's `GZipStream` |
|---|---|---:|---:|---:|---:|
| the taxi trips | GZIP | 83.2 MB | 1.75 | 0.68 GB/s | 0.58 GB/s |
| ClickBench's hits | Snappy | 378.2 MB | 3.09 | 5.36 GB/s | |
| the nested file | Snappy | 46.2 MB | 1.89 | 1.55 GB/s | |

Every compressed page of each file, decompressed in memory one after another on one thread: the
median of 9 passes. Vorticity reads GZIP with its own DEFLATE decoder, which gives the same bytes as
.NET's on every page, 17% faster. Snappy is the repository's own too, ZSTD and LZ4_RAW come from
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
