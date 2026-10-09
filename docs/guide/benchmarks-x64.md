# Benchmarks on x64

[The benchmark page](benchmarks.md) on an x64 machine: an AMD Ryzen 9 7950X (Zen 4,
16 cores and 32 threads, AVX-512) under Windows 11, against the same Vortex Rust 0.86.1 built the
same way for that machine. The instruments, the files, the rows and the rules are that page's own,
section for section; what changes is the processor, the operating system and the file system under
the page cache. [05-benchmarks.md](../design/05-benchmarks.md) says what is compared and how each
instrument measures; [bench/README.md](../../bench/README.md) how to run them.

Vortex™ is a trademark of LF Projects, LLC. Vorticity is an independent implementation,
not affiliated with or endorsed by the Vortex project or LF Projects, LLC.

**The sections that compare Vorticity with Rust are not measured on x64**: each says which
arguments write it. The sections that compare Vorticity with itself are: the encodings column by
column, the advice, and the kernels against their baselines, each scan mapping its file anew, as on
[the benchmark page](benchmarks.md). The kernel tables' speedups are computed from the Mean column
of their run.

* **The two sides.** Vorticity as a Native AOT binary built for the machine's instruction set,
  with the workstation garbage collector, where a section says so, and on the JIT otherwise. Vortex
  Rust 0.86.1 built as upstream builds its own benchmarks (mimalloc, `-C target-cpu=native`, one
  codegen unit, no LTO), through [tools/vxbench-rs](../../tools/vxbench-rs): `target-cpu=native`
  gives its compiler this machine's instruction set, AVX-512 included, as .NET's JIT has it. Both
  map the file and read it where it lies, decode every value they return to its plain form, a
  constant column kept as one value on both, and must return the same rows or the run fails.
* **Speedup** is Rust's time over Vorticity's: above 1.00x, Vorticity took less.
* **Throughput** is the plain size of the rows returned over the time, as on [the benchmark
  page](benchmarks.md).
* **All cores** are the 32 hardware threads, simultaneous multithreading included, where the arm64
  page's are 14 cores of two kinds.
* **The page cache is warm** for every run, on both sides, and every call maps its file anew, on
  both sides, as on [the benchmark page](benchmarks.md).

Each section is regenerated on an x64 machine by `dotnet run -c Release --project
bench/Vorticity.Benchmarks --` and the arguments below, which rewrite that section of this page and
leave the others. The Native AOT runner that `--report` times is published first (`dotnet publish -c
Release bench/Vorticity.Benchmarks.Runner`): an older one times older code.

| section | arguments |
|---|---|
| [Reading and writing a table](#reading-and-writing-a-table-process-against-process), [decoding, per encoding](#decoding-per-encoding) | `--report --markdown --out docs/guide/benchmarks-x64.md` |
| [In one process](#in-one-process-after-warm-up) | `--ratio-check --out docs/guide/benchmarks-x64.md` |
| [Taking rows](#taking-rows-per-encoding), [writing](#writing-per-encoding), per encoding | `--throughput --take --out docs/guide/benchmarks-x64.md`, and `--write` |
| [Encodings, column by column](#encodings-column-by-column), [what the advice picks](#what-the-advice-picks) | `--tradeoffs --out docs/guide/benchmarks-x64.md`, and `--advise`, under `DOTNET_TieredCompilation=0` |
| [Kernels](#kernels-against-their-baselines) | `--out docs/guide/benchmarks-x64.md`, or a class name before it |

<!-- results: scenarios -->
## Reading and writing a table, process against process

*Not measured on x64.* This section compares Vorticity with Rust; the arguments above write it
on an x64 machine.
<!-- /results: scenarios -->

<!-- results: decoding -->
## Decoding, per encoding

*Not measured on x64.* This section compares Vorticity with Rust; the arguments above write it
on an x64 machine.
<!-- /results: decoding -->

<!-- results: in-process -->
## In one process, after warm-up

*Not measured on x64.* This section compares Vorticity with Rust; the arguments above write it
on an x64 machine.
<!-- /results: in-process -->

<!-- results: take -->
## Taking rows, per encoding

*Not measured on x64.* This section compares Vorticity with Rust; the arguments above write it
on an x64 machine.
<!-- /results: take -->

<!-- results: write -->
## Writing, per encoding

*Not measured on x64.* This section compares Vorticity with Rust; the arguments above write it
on an x64 machine.
<!-- /results: write -->

<!-- results: tradeoffs -->
## Encodings, column by column

One column of 10,000,000 rows per file, written under each compression profile and each hint that
applies to it, then opened and read. A **scan** decodes every value to its plain form and reads it
once; a **take** reads 1,000 rows spread over the file. Each figure is the median of 3 passes after
1 warm-up, the file in the page cache and mapped anew by each pass, so that a larger file pays for its
pages on every read; a write is net of generating its rows. **Crosses at** is the
storage throughput at which a configuration and `Auto` read the column whole in the same time,
counting its bytes at that throughput and then its scan: below it the smaller file reads faster
end to end, above it the faster decode does. What the figures mean for a choice is in
[choose-encodings.md](choose-encodings.md).

**Worth knowing** is picked by rule, against `Auto`, among the configurations that write something
else: the smallest, when it saves 5 % of the bytes; of those a fifth faster to scan, the one that
overtakes `Auto` on the slowest storage, when that storage is no faster than the page cache, 10
GB/s; and the fastest take, when it halves `Auto`'s.

### Integers

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| a sequence | Sequence x77 | 0.00 | 13 | 1.8 | 0.26 |  |
| sorted runs of 1 000 | RunEnd x77 | 0.01 | 14 | 2.7 | 0.36 | Smallest: RunEnd x76, Pco x1, 0.01 B/value, scan 3.0 ms, take 0.74 ms; reads faster below 96 MB/s |
| timestamps (ms, increasing, jittered) | BitPacked x77 | 3.38 | 70 | 12.9 | 1.72 | Smallest: Pco x77, 1.35 B/value, scan 112.6 ms, take 118.47 ms; reads faster below 204 MB/s |
| random in 0..999 | BitPacked x77 | 1.25 | 22 | 5.5 | 1.54 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 55 | 3.3 | 1.14 |  |
| 100 003 distinct, repeating | BitPacked x77 | 4.63 | 38 | 12.9 | 1.51 | Smallest: Pco x77, 0.06 B/value, scan 72.5 ms, take 69.30 ms; reads faster below 767 MB/s; Auto, 16 MiB chunks: Dictionary x5, 2.36 B/value, scan 9.5 ms, take 1.32 ms; reads faster at any throughput |
| uniform 64-bit | Canonical x77 | 8.00 | 43 | 27.0 | 2.14 |  |
| random in 0..999, 10 % null | BitPacked x82 | 1.38 | 25 | 6.3 | 1.35 | Smallest: Pco x82, 1.26 B/value, scan 45.3 ms, take 44.57 ms; reads faster below 32 MB/s |

<details><summary>a sequence: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Sequence x77 | 38,844 | 0.00 | 13 | 1.8 | 0.26 |  |
| Fastest | Sequence x77 | 38,844 | 0.00 | 22 | 1.9 | 0.28 | reads slower at any throughput |
| Smallest | Sequence x77 | 38,844 | 0.00 | 15 | 1.8 | 0.27 | same |
| None | Canonical x77 | 80,043,724 | 8.00 | 51 | 23.2 | 1.68 | reads slower at any throughput |
| hint Dictionary | Sequence x77 | 38,844 | 0.00 | 121 | 1.8 | 0.28 | same |
| hint BitPacked | Sequence x77 | 38,844 | 0.00 | 15 | 1.8 | 0.28 | same |
| hint RunEnd | Sequence x77 | 38,844 | 0.00 | 13 | 2.1 | 0.37 | reads slower at any throughput |
| hint Zstd | Sequence x77 | 38,844 | 0.00 | 11 | 1.8 | 0.27 | same |

</details>

<details><summary>sorted runs of 1 000: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x77 | 88,004 | 0.01 | 14 | 2.7 | 0.36 |  |
| Fastest | RunEnd x77 | 88,004 | 0.01 | 10 | 2.6 | 0.38 | same |
| Smallest | RunEnd x76, Pco x1 | 53,748 | 0.01 | 110 | 3.0 | 0.74 | reads faster below 96 MB/s |
| None | Canonical x77 | 80,043,716 | 8.00 | 83 | 21.3 | 1.80 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 1,449,892 | 0.14 | 17 | 6.2 | 1.53 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 10,034,460 | 1.00 | 21 | 5.7 | 1.13 | reads slower at any throughput |
| hint RunEnd | RunEnd x77 | 88,004 | 0.01 | 10 | 2.6 | 0.36 | same |
| hint Zstd | Zstd x77 | 136,628 | 0.01 | 12 | 3.0 | 1.15 | reads slower at any throughput |

</details>

<details><summary>timestamps (ms, increasing, jittered): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 33,795,172 | 3.38 | 70 | 12.9 | 1.72 |  |
| Fastest | BitPacked x77 | 33,795,172 | 3.38 | 30 | 11.5 | 1.42 | reads faster at any throughput |
| Smallest | Pco x77 | 13,472,444 | 1.35 | 453 | 112.6 | 118.47 | reads faster below 204 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 43 | 19.3 | 1.57 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 33,795,172 | 3.38 | 156 | 11.2 | 1.45 | reads faster at any throughput |
| hint BitPacked | BitPacked x77 | 33,795,172 | 3.38 | 29 | 11.3 | 1.39 | reads faster at any throughput |
| hint RunEnd | BitPacked x77 | 33,795,172 | 3.38 | 49 | 11.1 | 1.41 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 20,700,796 | 2.07 | 182 | 80.8 | 35.05 | reads faster below 193 MB/s |

</details>

<details><summary>random in 0..999: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 12,544,284 | 1.25 | 22 | 5.5 | 1.54 |  |
| Fastest | BitPacked x77 | 12,544,284 | 1.25 | 32 | 6.9 | 1.67 | reads slower at any throughput |
| Smallest | BitPacked x76, Pco x1 | 12,543,924 | 1.25 | 344 | 5.1 | 1.34 | reads faster at any throughput |
| None | Canonical x77 | 80,043,716 | 8.00 | 43 | 21.5 | 1.82 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,652,724 | 1.27 | 70 | 6.5 | 1.25 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 12,544,284 | 1.25 | 23 | 4.9 | 1.15 | reads faster at any throughput |
| hint RunEnd | BitPacked x77 | 12,544,284 | 1.25 | 23 | 7.0 | 1.64 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 20,141,172 | 2.01 | 186 | 78.1 | 31.66 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,748 | 0.51 | 55 | 3.3 | 1.14 |  |
| Fastest | Dictionary x77 | 5,063,748 | 0.51 | 66 | 3.1 | 1.09 | reads faster at any throughput |
| Smallest | Dictionary x76, Pco x1 | 5,063,588 | 0.51 | 614 | 3.4 | 1.37 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 55 | 19.7 | 1.47 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,063,748 | 0.51 | 54 | 3.1 | 1.08 | reads faster at any throughput |
| hint BitPacked | Dictionary x77 | 5,063,748 | 0.51 | 127 | 3.1 | 1.07 | reads faster at any throughput |
| hint RunEnd | Dictionary x77 | 5,063,748 | 0.51 | 105 | 3.2 | 1.08 | same |
| hint Zstd | Zstd x77 | 10,539,396 | 1.05 | 139 | 51.4 | 24.40 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,932 | 0.50 | 84 | 3.1 | 1.05 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 46,295,652 | 4.63 | 38 | 12.9 | 1.51 |  |
| Fastest | BitPacked x77 | 46,295,652 | 4.63 | 64 | 16.5 | 1.96 | reads slower at any throughput |
| Smallest | Pco x77 | 580,924 | 0.06 | 423 | 72.5 | 69.30 | reads faster below 767 MB/s |
| None | Canonical x77 | 80,043,724 | 8.00 | 42 | 19.6 | 1.56 | reads slower at any throughput |
| hint Dictionary | BitPacked x77 | 46,295,652 | 4.63 | 123 | 12.7 | 1.46 | same |
| hint BitPacked | BitPacked x77 | 46,295,652 | 4.63 | 37 | 13.2 | 1.55 | same |
| hint RunEnd | BitPacked x77 | 46,295,652 | 4.63 | 86 | 13.7 | 1.56 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 51,815,868 | 5.18 | 128 | 62.1 | 42.11 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 23,603,548 | 2.36 | 138 | 9.5 | 1.32 | reads faster at any throughput |

</details>

<details><summary>uniform 64-bit: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x77 | 80,043,732 | 8.00 | 43 | 27.0 | 2.14 |  |
| Fastest | Canonical x77 | 80,043,732 | 8.00 | 43 | 20.0 | 1.62 | reads faster at any throughput |
| Smallest | Canonical x77 | 80,043,732 | 8.00 | 350 | 20.1 | 1.70 | reads faster at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 43 | 23.3 | 1.64 | reads faster at any throughput |
| hint Dictionary | Canonical x77 | 80,043,732 | 8.00 | 152 | 20.0 | 1.58 | reads faster at any throughput |
| hint BitPacked | Canonical x77 | 80,043,732 | 8.00 | 117 | 19.9 | 1.68 | reads faster at any throughput |
| hint RunEnd | Canonical x77 | 80,043,732 | 8.00 | 114 | 24.1 | 2.27 | reads faster at any throughput |
| hint Zstd | Canonical x77 | 80,043,732 | 8.00 | 132 | 18.5 | 1.52 | reads faster at any throughput |

</details>

<details><summary>random in 0..999, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x82 | 13,800,460 | 1.38 | 25 | 6.3 | 1.35 |  |
| Fastest | BitPacked x82 | 13,800,460 | 1.38 | 23 | 7.6 | 1.97 | reads slower at any throughput |
| Smallest | Pco x82 | 12,567,268 | 1.26 | 311 | 45.3 | 44.57 | reads faster below 32 MB/s |
| None | Canonical x82 | 81,294,668 | 8.13 | 42 | 20.6 | 2.10 | reads slower at any throughput |
| hint Dictionary | Dictionary x82 | 12,676,396 | 1.27 | 89 | 10.0 | 1.52 | reads faster below 307 MB/s |
| hint BitPacked | BitPacked x82 | 13,800,460 | 1.38 | 27 | 6.3 | 1.34 | same |
| hint RunEnd | BitPacked x82 | 13,800,460 | 1.38 | 56 | 9.5 | 2.06 | reads slower at any throughput |
| hint Zstd | Zstd x82 | 19,283,940 | 1.93 | 169 | 65.0 | 30.96 | reads slower at any throughput |

</details>

### Floating point

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| prices (2 decimals) | Alp x77 | 2.50 | 47 | 10.5 | 1.33 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 47 | 3.1 | 1.08 |  |
| 1 000 distinct prices | Dictionary x77 | 1.28 | 90 | 9.8 | 1.84 | hint Alp: Alp x77, 1.28 B/value, scan 7.5 ms, take 1.31 ms; reads faster at any throughput |
| 100 003 distinct, repeating | Zstd x77 | 1.69 | 161 | 48.8 | 23.24 | Auto, 16 MiB chunks: Dictionary x5, 2.21 B/value, scan 11.0 ms, take 3.70 ms; reads faster above 138 MB/s; None: Canonical x77, 8.00 B/value, scan 19.8 ms, take 1.56 ms; reads faster above 2,178 MB/s |
| uniform in [0, 1) | AlpRd x77 | 6.91 | 60 | 22.9 | 2.46 |  |

<details><summary>prices (2 decimals): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Alp x77 | 25,049,732 | 2.50 | 47 | 10.5 | 1.33 |  |
| Fastest | Alp x77 | 25,049,732 | 2.50 | 50 | 9.6 | 1.20 | reads faster at any throughput |
| Smallest | Alp x77 | 25,048,868 | 2.50 | 880 | 9.9 | 1.38 | same |
| None | Canonical x77 | 80,043,732 | 8.00 | 43 | 19.3 | 1.59 | reads slower at any throughput |
| hint Dictionary | Alp x77 | 25,049,732 | 2.50 | 177 | 9.7 | 1.21 | reads faster at any throughput |
| hint Alp | Alp x77 | 25,049,732 | 2.50 | 48 | 9.7 | 1.23 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 41,240,260 | 4.12 | 295 | 66.1 | 36.34 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,748 | 0.51 | 47 | 3.1 | 1.08 |  |
| Fastest | Dictionary x77 | 5,063,748 | 0.51 | 41 | 3.1 | 1.07 | same |
| Smallest | Dictionary x76, Pco x1 | 5,063,588 | 0.51 | 560 | 3.4 | 1.33 | reads slower at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 27 | 20.2 | 1.65 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 5,063,748 | 0.51 | 42 | 3.5 | 1.19 | reads slower at any throughput |
| hint Alp | Dictionary x77 | 5,063,748 | 0.51 | 100 | 3.1 | 1.08 | same |
| hint Zstd | Zstd x77 | 10,545,668 | 1.05 | 139 | 47.7 | 22.21 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,932 | 0.50 | 66 | 3.0 | 1.03 | same |

</details>

<details><summary>1 000 distinct prices: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 12,756,260 | 1.28 | 90 | 9.8 | 1.84 |  |
| Fastest | Dictionary x77 | 12,756,260 | 1.28 | 75 | 7.8 | 1.42 | reads faster at any throughput |
| Smallest | Dictionary x77 | 12,751,356 | 1.28 | 499 | 6.8 | 1.61 | reads faster at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 46 | 23.2 | 1.73 | reads slower at any throughput |
| hint Dictionary | Dictionary x77 | 12,756,260 | 1.28 | 68 | 6.3 | 1.28 | reads faster at any throughput |
| hint Alp | Alp x77 | 12,756,260 | 1.28 | 121 | 7.5 | 1.31 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 26,073,412 | 2.61 | 213 | 61.7 | 31.41 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 12,545,732 | 1.25 | 100 | 7.0 | 1.22 | reads faster at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x77 | 16,909,892 | 1.69 | 161 | 48.8 | 23.24 |  |
| Fastest | Zstd x77 | 16,909,892 | 1.69 | 174 | 47.9 | 22.40 | same |
| Smallest | Zstd x77 | 16,909,892 | 1.69 | 296 | 61.3 | 28.88 | reads slower at any throughput |
| None | Canonical x77 | 80,043,732 | 8.00 | 43 | 19.8 | 1.56 | reads faster above 2,178 MB/s |
| hint Dictionary | Zstd x77 | 16,909,892 | 1.69 | 260 | 63.0 | 28.51 | reads slower at any throughput |
| hint Alp | Zstd x77 | 16,909,892 | 1.69 | 284 | 47.8 | 22.96 | same |
| hint Zstd | Zstd x77 | 16,909,892 | 1.69 | 163 | 48.3 | 24.00 | same |
| Auto, 16 MiB chunks | Dictionary x5 | 22,129,988 | 2.21 | 132 | 11.0 | 3.70 | reads faster above 138 MB/s |

</details>

<details><summary>uniform in [0, 1): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | AlpRd x77 | 69,054,380 | 6.91 | 60 | 22.9 | 2.46 |  |
| Fastest | AlpRd x77 | 69,054,380 | 6.91 | 82 | 24.1 | 3.54 | reads slower at any throughput |
| Smallest | Pco x77 | 67,609,668 | 6.76 | 548 | 118.0 | 117.47 | reads faster below 15 MB/s |
| None | Canonical x77 | 80,043,732 | 8.00 | 45 | 18.9 | 1.57 | reads faster above 2,761 MB/s |
| hint Dictionary | AlpRd x77 | 69,054,380 | 6.91 | 288 | 21.2 | 2.62 | reads faster at any throughput |
| hint Alp | AlpRd x77 | 69,054,380 | 6.91 | 235 | 23.3 | 3.00 | same |
| hint Zstd | AlpRd x77 | 69,054,380 | 6.91 | 269 | 21.8 | 2.56 | same |

</details>

### Text

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| 16 cities, random order | Dictionary x175 | 0.51 | 124 | 7.7 | 1.60 |  |
| 10 000 distinct ids | Dictionary x306 | 2.54 | 1032 | 41.2 | 29.59 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 14.8 ms, take 3.78 ms; reads faster at any throughput |
| 10 000 distinct ids, 10 % null | Dictionary x306 | 2.45 | 995 | 54.5 | 36.86 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 13.5 ms, take 3.38 ms; reads faster at any throughput |
| unique UUIDs | Zstd x611 | 20.61 | 1792 | 309.4 | 301.45 | hint Fsst: Fsst x611, 22.44 B/value, scan 175.1 ms, take 13.05 ms; reads faster above 136 MB/s |
| log lines (~100 B) | Zstd x1221 | 13.71 | 1468 | 406.8 | 321.21 | hint Fsst: Fsst x1221, 22.01 B/value, scan 177.4 ms, take 13.64 ms; reads faster above 362 MB/s |

<details><summary>16 cities, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x175 | 5,117,284 | 0.51 | 124 | 7.7 | 1.60 |  |
| Fastest | Dictionary x175 | 5,117,284 | 0.51 | 141 | 6.1 | 1.39 | reads faster at any throughput |
| Smallest | Dictionary x175 | 5,117,284 | 0.51 | 323 | 6.4 | 1.37 | reads faster at any throughput |
| None | Canonical x175 | 96,746,756 | 9.67 | 183 | 46.6 | 20.78 | reads slower at any throughput |
| hint Dictionary | Dictionary x175 | 5,117,284 | 0.51 | 122 | 6.2 | 1.42 | reads faster at any throughput |
| hint Fsst | Fsst x175 | 41,142,948 | 4.11 | 315 | 42.9 | 8.95 | reads slower at any throughput |
| hint Zstd | Zstd x175 | 17,427,100 | 1.74 | 287 | 97.8 | 61.74 | reads slower at any throughput |

</details>

<details><summary>10 000 distinct ids: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 25,433,788 | 2.54 | 1032 | 41.2 | 29.59 |  |
| Fastest | Dictionary x306 | 25,433,788 | 2.54 | 1009 | 58.0 | 32.81 | reads slower at any throughput |
| Smallest | Dictionary x305, Zstd x1 | 23,452,500 | 2.35 | 2062 | 138.8 | 134.32 | reads faster below 20 MB/s |
| None | Canonical x306 | 140,098,172 | 14.01 | 225 | 55.6 | 40.79 | reads slower at any throughput |
| hint Dictionary | Dictionary x306 | 25,433,788 | 2.54 | 713 | 38.1 | 28.72 | reads faster at any throughput |
| hint Fsst | Fsst x306 | 62,863,260 | 6.29 | 461 | 47.5 | 8.41 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 27,097,628 | 2.71 | 430 | 92.2 | 63.15 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 18,014,012 | 1.80 | 446 | 14.8 | 3.78 | reads faster at any throughput |

</details>

<details><summary>10 000 distinct ids, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 24,539,284 | 2.45 | 995 | 54.5 | 36.86 |  |
| Fastest | Dictionary x306 | 24,539,284 | 2.45 | 944 | 40.5 | 34.87 | reads faster at any throughput |
| Smallest | Dictionary x305, Zstd x1 | 22,854,628 | 2.29 | 1984 | 148.6 | 139.29 | reads faster below 18 MB/s |
| None | Canonical x306 | 151,883,732 | 15.19 | 293 | 69.6 | 39.43 | reads slower at any throughput |
| hint Dictionary | Dictionary x306 | 24,539,284 | 2.45 | 817 | 39.9 | 28.75 | reads faster at any throughput |
| hint Fsst | Fsst x306 | 64,408,284 | 6.44 | 533 | 60.9 | 11.45 | reads slower at any throughput |
| hint Zstd | Zstd x306 | 25,595,636 | 2.56 | 400 | 99.0 | 63.07 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 17,982,604 | 1.80 | 504 | 13.5 | 3.38 | reads faster at any throughput |

</details>

<details><summary>unique UUIDs: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x611 | 206,058,588 | 20.61 | 1792 | 309.4 | 301.45 |  |
| Fastest | Zstd x611 | 206,058,588 | 20.61 | 1702 | 302.5 | 306.99 | same |
| Smallest | Zstd x611 | 206,058,588 | 20.61 | 3473 | 299.3 | 307.14 | same |
| None | Canonical x611 | 360,155,516 | 36.02 | 281 | 109.3 | 96.06 | reads faster above 770 MB/s |
| hint Dictionary | Zstd x610, Fsst x1 | 206,059,580 | 20.61 | 6414 | 301.3 | 323.16 | same |
| hint Fsst | Fsst x611 | 224,384,604 | 22.44 | 1798 | 175.1 | 13.05 | reads faster above 136 MB/s |
| hint Zstd | Zstd x611 | 206,058,588 | 20.61 | 1761 | 305.3 | 320.92 | same |

</details>

<details><summary>log lines (~100 B): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x1221 | 137,107,188 | 13.71 | 1468 | 406.8 | 321.21 |  |
| Fastest | Zstd x1221 | 137,107,188 | 13.71 | 1450 | 393.2 | 317.69 | same |
| Smallest | Zstd x1221 | 137,107,188 | 13.71 | 2932 | 387.2 | 335.15 | same |
| None | Canonical x1221 | 725,468,444 | 72.55 | 790 | 206.6 | 158.59 | reads faster above 2,939 MB/s |
| hint Dictionary | Zstd x1221 | 137,107,188 | 13.71 | 6070 | 404.3 | 347.13 | same |
| hint Fsst | Fsst x1221 | 220,058,684 | 22.01 | 1838 | 177.4 | 13.64 | reads faster above 362 MB/s |
| hint Zstd | Zstd x1221 | 137,107,188 | 13.71 | 1398 | 401.5 | 320.92 | same |

</details>

### Booleans

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| half true | Canonical x10 | 0.13 | 10 | 0.4 | 0.46 |  |
| 1 % true | RunEnd x10 | 0.05 | 5 | 2.5 | 0.49 | None: Canonical x10, 0.13 B/value, scan 0.6 ms, take 0.66 ms; reads faster above 390 MB/s |

<details><summary>half true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x10 | 1,262,556 | 0.13 | 10 | 0.4 | 0.46 |  |
| Fastest | Canonical x10 | 1,262,556 | 0.13 | 0 | 0.5 | 0.46 | same |
| Smallest | Canonical x10 | 1,262,556 | 0.13 | 0 | 0.5 | 0.47 | same |
| None | Canonical x10 | 1,262,556 | 0.13 | 0 | 0.5 | 0.46 | same |
| hint RunEnd | Canonical x10 | 1,262,556 | 0.13 | 7 | 0.4 | 0.46 | same |

</details>

<details><summary>1 % true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x10 | 548,092 | 0.05 | 5 | 2.5 | 0.49 |  |
| Fastest | RunEnd x10 | 548,092 | 0.05 | 5 | 2.5 | 0.50 | same |
| Smallest | RunEnd x10 | 164,692 | 0.02 | 27 | 4.0 | 3.67 | reads faster below 246 MB/s |
| None | Canonical x10 | 1,262,556 | 0.13 | 13 | 0.6 | 0.66 | reads faster above 390 MB/s |
| hint RunEnd | RunEnd x10 | 548,092 | 0.05 | 6 | 2.4 | 0.49 | same |

</details>

### The profiles side by side

`Fastest` wrote what `Auto` wrote on 20 of 20 columns. `Smallest` wrote something else on *i64, sorted runs of 1 000*, *i64, timestamps (ms, increasing, jittered)*, *i64, random in 0..999*, *i64, 16 distinct, random order*, *i64, 100 003 distinct, repeating*, *i64, random in 0..999, 10 % null*, *f64, 16 distinct, random order*, *f64, uniform in [0, 1)*, *utf8, 10 000 distinct ids*, *utf8, 10 000 distinct ids, 10 % null*, and took up to 18.8 times `Auto`'s write, 880 ms against 47 ms on *f64, prices (2 decimals)*, since it tries every scheme on every chunk.
`None`, the plain form, made the largest file on 20 of 20 columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit a3dfc8ab, 2026-10-09 16:49 UTC.*
<!-- /results: tradeoffs -->

<!-- results: advice -->
## What the advice picks

`VortexSession.AdviseAsync` on the same columns of 10,000,000 rows, under five goals: whole scans with the
bytes read at 2 GB/s, 100 MB/s and 10 GB/s, a row in a thousand read by row, and the bytes alone.
A cell is what the advice takes where it departs from the writer's own choice, empty where it
does not; a column it never departs on is left out.

| column | scans at 2 GB/s | scans at 100 MB/s | scans at 10 GB/s | a row in 1 000 read by row | size |
|---|---|---|---|---|---|
| i64, sorted runs of 1 000 |  |  | `Zstd` |  |  |
| i64, timestamps (ms, increasing, jittered) |  | `Pco` |  | `Zstd` |  |
| i64, 100 003 distinct, repeating | 16 MiB chunks | `Pco` | 16 MiB chunks |  | `Dictionary`, 16 MiB chunks |
| i64, random in 0..999, 10 % null |  | `Dictionary` |  | `Dictionary` | `Dictionary` |
| f64, 100 003 distinct, repeating | 16 MiB chunks |  | 16 MiB chunks |  | `Dictionary`, 16 MiB chunks |
| f64, uniform in [0, 1) |  |  | `Canonical` |  |  |
| utf8, 10 000 distinct ids | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `OnPair` | 16 MiB chunks |
| utf8, 10 000 distinct ids, 10 % null | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `Zstd` | 16 MiB chunks |
| utf8, unique UUIDs | `Canonical` |  | `Canonical` | `Fsst` |  |
| utf8, log lines (~100 B) | `OnPair` |  | `Canonical` | `OnPair` |  |
| bool, 1 % true | `Canonical` |  | `Canonical` |  |  |

It keeps the writer's choice under every goal on the 9 other columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit a3dfc8ab, 2026-10-09 16:52 UTC.*
<!-- /results: advice -->

## Kernels, against their baselines

Each hot loop of the library against a baseline, the plain loop it stands in for or the floor of the work it
does, measured by BenchmarkDotNet in one process on one clock, both checked to give the same result
before any is timed. A **Speedup** column is the mean time of the row's baseline, the row whose
`MannWhitney(5%)` cell reads `Baseline`, over the row's own: above 1.00x, the row took less. These
are the fast profile's figures, which tell a direction: `--full` before a class's name runs the
reference profile on that class, the one to quote a small difference from.

<!-- results: kernel:FastLanesKernelBenchmarks -->
### `FastLanesKernelBenchmarks`

| Method       | BitWidth | Mean       | Error     | StdDev    | Speedup | MannWhitney(5%) | ns/row | GB/s  | Allocated | Alloc Ratio |
|------------- |--------- |-----------:|----------:|----------:|--------:|---------------- |-------:|------:|----------:|------------:|
| &#39;i64 scalar&#39; | 17       |  95.737 μs | 1.3069 μs | 0.2023 μs |   1.00x | Baseline        |   1.46 |  5.48 |         - |          NA |
| &#39;i64 vector&#39; | 17       |   8.919 μs | 1.2853 μs | 0.1989 μs |   10.7x | Faster          |   0.14 | 58.78 |         - |          NA |
| &#39;i32 scalar&#39; | 17       | 107.554 μs | 0.4447 μs | 0.1155 μs |   0.89x | Same            |   1.64 |  2.44 |         - |          NA |
| &#39;i64 pack&#39;   | 17       |  11.620 μs | 0.2981 μs | 0.0461 μs |   8.24x | Faster          |   0.18 | 45.12 |         - |          NA |
| &#39;i32 pack&#39;   | 17       |  12.505 μs | 0.4404 μs | 0.0681 μs |   7.66x | Faster          |   0.19 | 20.96 |         - |          NA |
| &#39;i32 vector&#39; | 17       |   4.634 μs | 0.1085 μs | 0.0168 μs |   20.7x | Faster          |   0.07 | 56.58 |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.1, the fast profile; commit b5104f2 with uncommitted changes, 2026-09-27 03:15 UTC.*
<!-- /results: kernel:FastLanesKernelBenchmarks -->

<!-- results: kernel:RowEncodingBenchmarks -->
### `RowEncodingBenchmarks`

| Method                                   | Entry                | Mean       | Error      | StdDev    | ns/row | GB/s | Allocated |
|----------------------------------------- |--------------------- |-----------:|-----------:|----------:|-------:|-----:|----------:|
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nical [33]** |  **62.348 μs** |  **2.2289 μs** | **0.3449 μs** |  **15.22** | **2.82** |      **48 B** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nical [33] |  67.385 μs |  0.1931 μs | 0.0299 μs |  16.45 | 2.61 |      48 B |
| &#39;encode, then sort rows by key&#39;          | conta(...)nical [33] | 356.329 μs | 11.1380 μs | 2.8925 μs |  86.99 | 0.49 |      48 B |
| **&#39;encode a batch to row keys&#39;**             | **conta(...)nulls [33]** |         **NA** |         **NA** |        **NA** |      **-** |    **-** |        **NA** |
| &#39;encode a batch to row keys, descending&#39; | conta(...)nulls [33] |         NA |         NA |        NA |      - |    - |        NA |
| &#39;encode, then sort rows by key&#39;          | conta(...)nulls [33] |         NA |         NA |        NA |      - |    - |        NA |
| **&#39;encode a batch to row keys&#39;**             | **encod(...)r1025 [31]** |   **9.784 μs** |  **0.0574 μs** | **0.0089 μs** |   **9.55** | **0.94** |      **48 B** |
| &#39;encode a batch to row keys, descending&#39; | encod(...)r1025 [31] |   9.722 μs |  0.0700 μs | 0.0108 μs |   9.48 | 0.95 |      48 B |
| &#39;encode, then sort rows by key&#39;          | encod(...)r1025 [31] |  50.392 μs |  7.0184 μs | 1.0861 μs |  49.16 | 0.18 |      48 B |

Benchmarks with issues:
  RowEncodingBenchmarks.'encode a batch to row keys': fast(MinIterationTime=50ms, Toolchain=InProcessEmitToolchain, IterationCount=5, IterationTime=100ms, MaxWarmupIterationCount=30, MinWarmupIterationCount=4) [Entry=conta(...)nulls [33]]
  RowEncodingBenchmarks.'encode a batch to row keys, descending': fast(MinIterationTime=50ms, Toolchain=InProcessEmitToolchain, IterationCount=5, IterationTime=100ms, MaxWarmupIterationCount=30, MinWarmupIterationCount=4) [Entry=conta(...)nulls [33]]
  RowEncodingBenchmarks.'encode, then sort rows by key': fast(MinIterationTime=50ms, Toolchain=InProcessEmitToolchain, IterationCount=5, IterationTime=100ms, MaxWarmupIterationCount=30, MinWarmupIterationCount=4) [Entry=conta(...)nulls [33]]

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.1, the fast profile; commit b5104f2 with uncommitted changes, 2026-09-27 03:15 UTC.*
<!-- /results: kernel:RowEncodingBenchmarks -->

<!-- results: kernel:CompressorBenchmarks -->
### `CompressorBenchmarks`

| Method                       | Column | Mean          | Error       | StdDev     | Speedup | MannWhitney(5%) | Allocated | Alloc Ratio |
|----------------------------- |------- |--------------:|------------:|-----------:|--------:|---------------- |----------:|------------:|
| &#39;Choose, the whole decision&#39; | i64    | 52,503.854 ns | 230.5989 ns | 35.6854 ns |   1.00x | Baseline        |         - |          NA |
| &#39;candidate: sequence&#39;        | i64    |     10.728 ns |   0.1120 ns |  0.0291 ns |  4,894x | Faster          |         - |          NA |
| &#39;candidate: bit packing&#39;     | i64    |  4,950.600 ns | 148.8102 ns | 23.0285 ns |   10.6x | Faster          |         - |          NA |
| &#39;candidate: FSST&#39;            | i64    |      1.846 ns |   0.0144 ns |  0.0038 ns | 28,448x | Faster          |         - |          NA |
| &#39;candidate: zstd&#39;            | i64    |  7,546.505 ns | 194.1759 ns | 30.0489 ns |   6.96x | Faster          |         - |          NA |
| &#39;candidate: ALP&#39;             | i64    |      2.599 ns |   0.0136 ns |  0.0035 ns | 20,204x | Faster          |         - |          NA |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.2, the fast profile; commit a3dfc8ab, 2026-10-09 16:52 UTC.*
<!-- /results: kernel:CompressorBenchmarks -->

<!-- results: kernel:LanesBenchmarks -->
### `LanesBenchmarks`

*Not measured on x64.* This section compares Vorticity with Rust; the arguments above write it
on an x64 machine.
<!-- /results: kernel:LanesBenchmarks -->

## What this does not measure

* **A cold cache.** The page cache is warm for every run, on both sides; upstream's own benchmarks
  flush it before each query.
* **Pinned cores.** Windows pins no process, and two hardware threads share each core's execution
  units; both sides see all 32. Upstream measures on 94 pinned cores of one kind.
* **Another operating system on this processor.** The file system, the page cache and the thread
  scheduler are Windows' here, and macOS' on [the benchmark page](benchmarks.md): a difference
  between the two pages is the machine's as much as the processor's.
* **Your data and your machine.** A handful of tables and one machine: a column the compressor likes
  less, or a filter the zone maps cannot prune, moves these numbers more than either implementation
  does.
