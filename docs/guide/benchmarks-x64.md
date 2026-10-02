# Benchmarks on x64

[The benchmark page](benchmarks.md) measured again on an x64 machine: an AMD Ryzen 9 7950X (Zen 4,
16 cores and 32 threads, AVX-512) under Windows 11, against the same Vortex Rust 0.86.1 built the
same way for that machine. The instruments, the files, the rows and the rules are that page's own,
section for section; what changes is the processor, the operating system and the file system under
the page cache. [05-benchmarks.md](../design/05-benchmarks.md) says what is compared and how each
instrument measures; [bench/README.md](../../bench/README.md) how to run them.

Vortex™ is a trademark of LF Projects, LLC. Vorticity is an independent implementation,
not affiliated with or endorsed by the Vortex project or LF Projects, LLC.

**Every comparison with Rust on this page is withdrawn** until an x64 machine measures it again.
Its figures were taken on 2026-09-27, before the harness stopped charging Rust for work Vorticity
did not do ([05-benchmarks.md](../design/05-benchmarks.md) §1), and favoured Vorticity. What stands
are the sections that compare Vorticity with itself: the encodings column by column, the advice,
and the kernels against the loops they replaced, measured then with each scan pass taking over the
mapping the previous one left, which [the benchmark page](benchmarks.md) no longer does.

* **The two sides.** Vorticity as a Native AOT binary built for the machine's instruction set,
  with the workstation garbage collector, where a section says so, and on the JIT otherwise. Vortex
  Rust 0.86.1 built as upstream builds its own benchmarks (mimalloc, `-C target-cpu=native`, one
  codegen unit, no LTO), through [tools/vxbench-rs](../../tools/vxbench-rs): `target-cpu=native`
  gives its compiler this machine's instruction set, AVX-512 included, as .NET's JIT has it. Both
  map the file and read it where it lies, decode every value they return to its plain form, a
  constant column kept as one value on both, and must return the same rows or the run fails.
* **Ratio** is Vorticity's time over Rust's: under 1.00x, Vorticity took less.
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
| [Kernels](#kernels-against-what-they-replaced) | `--out docs/guide/benchmarks-x64.md`, or a class name before it |

<!-- results: scenarios -->
## Reading and writing a table, process against process

*Withdrawn on 2026-10-02.* Measured on 2026-09-27 at commit b5104f2 with a harness that charged Rust
for work Vorticity did not do: its reader copied the file where ours read a mapping a previous round
had left it, it expanded constant columns ours keeps whole, its writer filled a growing `Vec<u8>`
where ours discarded its bytes, and it read the per-encoding files under a split that decoded each
of them ten times ([05-benchmarks.md](../design/05-benchmarks.md) §1 lists every difference). The
figures favoured Vorticity, by more than an order of magnitude on some rows, and are not quoted
until an x64 machine measures this section again with the arguments above.
<!-- /results: scenarios -->

<!-- results: decoding -->
## Decoding, per encoding

*Withdrawn on 2026-10-02.* Measured on 2026-09-27 at commit b5104f2 with a harness that charged Rust
for work Vorticity did not do: its reader copied the file where ours read a mapping a previous round
had left it, it expanded constant columns ours keeps whole, its writer filled a growing `Vec<u8>`
where ours discarded its bytes, and it read the per-encoding files under a split that decoded each
of them ten times ([05-benchmarks.md](../design/05-benchmarks.md) §1 lists every difference). The
figures favoured Vorticity, by more than an order of magnitude on some rows, and are not quoted
until an x64 machine measures this section again with the arguments above.
<!-- /results: decoding -->

<!-- results: in-process -->
## In one process, after warm-up

*Withdrawn on 2026-10-02.* Measured on 2026-09-27 at commit b5104f2 with a harness that charged Rust
for work Vorticity did not do: its reader copied the file where ours read a mapping a previous round
had left it, it expanded constant columns ours keeps whole, its writer filled a growing `Vec<u8>`
where ours discarded its bytes, and it read the per-encoding files under a split that decoded each
of them ten times ([05-benchmarks.md](../design/05-benchmarks.md) §1 lists every difference). The
figures favoured Vorticity, by more than an order of magnitude on some rows, and are not quoted
until an x64 machine measures this section again with the arguments above.
<!-- /results: in-process -->

<!-- results: take -->
## Taking rows, per encoding

*Withdrawn on 2026-10-02.* Measured on 2026-09-27 at commit b5104f2 with a harness that charged Rust
for work Vorticity did not do: its reader copied the file where ours read a mapping a previous round
had left it, it expanded constant columns ours keeps whole, its writer filled a growing `Vec<u8>`
where ours discarded its bytes, and it read the per-encoding files under a split that decoded each
of them ten times ([05-benchmarks.md](../design/05-benchmarks.md) §1 lists every difference). The
figures favoured Vorticity, by more than an order of magnitude on some rows, and are not quoted
until an x64 machine measures this section again with the arguments above.
<!-- /results: take -->

<!-- results: write -->
## Writing, per encoding

*Withdrawn on 2026-10-02.* Measured on 2026-09-27 at commit b5104f2 with a harness that charged Rust
for work Vorticity did not do: its reader copied the file where ours read a mapping a previous round
had left it, it expanded constant columns ours keeps whole, its writer filled a growing `Vec<u8>`
where ours discarded its bytes, and it read the per-encoding files under a split that decoded each
of them ten times ([05-benchmarks.md](../design/05-benchmarks.md) §1 lists every difference). The
figures favoured Vorticity, by more than an order of magnitude on some rows, and are not quoted
until an x64 machine measures this section again with the arguments above.
<!-- /results: write -->

<!-- results: tradeoffs -->
## Encodings, column by column

One column of 10,000,000 rows per file, written under each compression profile and each hint that
applies to it, then opened and read. A **scan** decodes every value to its plain form and reads it
once; a **take** reads 1,000 rows spread over the file. Each figure is the median of 3 passes after
1 warm-up, the file in the page cache; a write is net of generating its rows. **Crosses at** is the
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
| a sequence | Sequence x77 | 0.00 | 26 | 2.8 | 1.19 |  |
| sorted runs of 1 000 | RunEnd x77 | 0.01 | 55 | 2.3 | 1.15 | None: Canonical x77, 8.00 B/value, scan 1.4 ms, take 0.29 ms; reads faster above 88,339 MB/s |
| timestamps (ms, increasing, jittered) | BitPacked x77 | 3.38 | 85 | 6.6 | 0.60 | Smallest: Zstd x77, 2.07 B/value, scan 74.5 ms, take 60.30 ms; reads faster below 193 MB/s; None: Canonical x77, 8.00 B/value, scan 1.3 ms, take 0.25 ms; reads faster above 8,691 MB/s |
| random in 0..999 | BitPacked x77 | 1.25 | 0 | 2.4 | 0.30 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 38 | 2.3 | 0.49 | None: Canonical x77, 8.00 B/value, scan 1.4 ms, take 0.24 ms; reads faster above 79,377 MB/s |
| 100 003 distinct, repeating | BitPacked x77 | 4.63 | 32 | 2.6 | 0.26 | Auto, 16 MiB chunks: Dictionary x5, 2.36 B/value, scan 6.7 ms, take 0.47 ms; reads faster below 5,585 MB/s |
| uniform 64-bit | Canonical x77 | 8.00 | 31 | 1.4 | 0.22 |  |
| random in 0..999, 10 % null | BitPacked x82 | 1.38 | 15 | 3.6 | 0.31 | hint Dictionary: Dictionary x82, 1.27 B/value, scan 6.3 ms, take 0.53 ms; reads faster below 417 MB/s |

<details><summary>a sequence: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Sequence x77 | 38,836 | 0.00 | 26 | 2.8 | 1.19 |  |
| Fastest | Sequence x77 | 38,836 | 0.00 | 27 | 2.7 | 1.22 | same |
| Smallest | Sequence x77 | 38,836 | 0.00 | 29 | 2.8 | 1.48 | same |
| None | Canonical x77 | 80,043,716 | 8.00 | 51 | 2.8 | 1.42 | reads slower at any throughput |
| hint Dictionary | Sequence x77 | 38,836 | 0.00 | 195 | 1.7 | 0.43 | reads faster at any throughput |
| hint BitPacked | Sequence x77 | 38,836 | 0.00 | 5 | 1.7 | 0.42 | reads faster at any throughput |
| hint RunEnd | Sequence x77 | 38,836 | 0.00 | 4 | 1.7 | 0.41 | reads faster at any throughput |
| hint Zstd | Sequence x77 | 38,836 | 0.00 | 4 | 1.7 | 0.43 | reads faster at any throughput |

</details>

<details><summary>sorted runs of 1 000: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x77 | 87,996 | 0.01 | 55 | 2.3 | 1.15 |  |
| Fastest | RunEnd x77 | 87,996 | 0.01 | 67 | 2.3 | 1.14 | same |
| Smallest | RunEnd x76, Zstd x1 | 87,964 | 0.01 | 90 | 3.6 | 1.79 | reads slower at any throughput |
| None | Canonical x77 | 80,043,708 | 8.00 | 43 | 1.4 | 0.29 | reads faster above 88,339 MB/s |
| hint Dictionary | Dictionary x77 | 1,449,884 | 0.14 | 34 | 5.8 | 1.65 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 10,034,452 | 1.00 | 75 | 4.8 | 0.44 | reads slower at any throughput |
| hint RunEnd | RunEnd x77 | 87,996 | 0.01 | 23 | 2.4 | 0.65 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 136,620 | 0.01 | 13 | 6.3 | 4.86 | reads slower at any throughput |

</details>

<details><summary>timestamps (ms, increasing, jittered): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 33,795,164 | 3.38 | 85 | 6.6 | 0.60 |  |
| Fastest | BitPacked x77 | 33,795,164 | 3.38 | 16 | 3.6 | 0.29 | reads faster at any throughput |
| Smallest | Zstd x77 | 20,700,788 | 2.07 | 187 | 74.5 | 60.30 | reads faster below 193 MB/s |
| None | Canonical x77 | 80,043,716 | 8.00 | 28 | 1.3 | 0.25 | reads faster above 8,691 MB/s |
| hint Dictionary | BitPacked x77 | 33,795,164 | 3.38 | 93 | 3.6 | 0.26 | reads faster at any throughput |
| hint BitPacked | BitPacked x77 | 33,795,164 | 3.38 | 14 | 3.6 | 0.26 | reads faster at any throughput |
| hint RunEnd | BitPacked x77 | 33,795,164 | 3.38 | 26 | 3.6 | 0.28 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 20,700,788 | 2.07 | 150 | 74.1 | 60.66 | reads faster below 194 MB/s |

</details>

<details><summary>random in 0..999: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 12,544,276 | 1.25 | 0 | 2.4 | 0.30 |  |
| Fastest | BitPacked x77 | 12,544,276 | 1.25 | 0 | 2.4 | 0.25 | same |
| Smallest | BitPacked x77 | 12,544,276 | 1.25 | 151 | 2.4 | 0.25 | same |
| None | Canonical x77 | 80,043,708 | 8.00 | 5 | 1.3 | 0.23 | reads faster above 57,820 MB/s |
| hint Dictionary | Dictionary x77 | 12,652,716 | 1.27 | 28 | 5.7 | 0.56 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 12,544,276 | 1.25 | 0 | 2.7 | 0.41 | reads slower at any throughput |
| hint RunEnd | BitPacked x77 | 12,544,276 | 1.25 | 0 | 2.5 | 0.32 | same |
| hint Zstd | Zstd x77 | 20,141,164 | 2.01 | 143 | 78.7 | 64.11 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,740 | 0.51 | 38 | 2.3 | 0.49 |  |
| Fastest | Dictionary x77 | 5,063,740 | 0.51 | 37 | 2.0 | 0.30 | reads faster at any throughput |
| Smallest | Dictionary x77 | 5,063,740 | 0.51 | 169 | 2.0 | 0.31 | reads faster at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 31 | 1.4 | 0.24 | reads faster above 79,377 MB/s |
| hint Dictionary | Dictionary x77 | 5,063,740 | 0.51 | 37 | 2.0 | 0.34 | reads faster at any throughput |
| hint BitPacked | Dictionary x77 | 5,063,740 | 0.51 | 102 | 2.0 | 0.32 | reads faster at any throughput |
| hint RunEnd | Dictionary x77 | 5,063,740 | 0.51 | 93 | 2.0 | 0.31 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 10,539,388 | 1.05 | 125 | 61.4 | 49.82 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,924 | 0.50 | 51 | 2.9 | 0.50 | reads slower at any throughput |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x77 | 46,295,644 | 4.63 | 32 | 2.6 | 0.26 |  |
| Fastest | BitPacked x77 | 46,295,644 | 4.63 | 37 | 2.6 | 0.29 | same |
| Smallest | BitPacked x77 | 46,295,644 | 4.63 | 162 | 2.6 | 0.29 | same |
| None | Canonical x77 | 80,043,716 | 8.00 | 40 | 1.3 | 0.22 | reads faster above 25,357 MB/s |
| hint Dictionary | BitPacked x77 | 46,295,644 | 4.63 | 105 | 2.7 | 0.34 | reads slower at any throughput |
| hint BitPacked | BitPacked x77 | 46,295,644 | 4.63 | 33 | 2.6 | 0.32 | same |
| hint RunEnd | BitPacked x77 | 46,295,644 | 4.63 | 58 | 2.6 | 0.29 | same |
| hint Zstd | Zstd x77 | 51,815,860 | 5.18 | 132 | 60.3 | 49.04 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 23,603,540 | 2.36 | 117 | 6.7 | 0.47 | reads faster below 5,585 MB/s |

</details>

<details><summary>uniform 64-bit: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x77 | 80,043,724 | 8.00 | 31 | 1.4 | 0.22 |  |
| Fastest | Canonical x77 | 80,043,724 | 8.00 | 30 | 1.3 | 0.24 | reads faster at any throughput |
| Smallest | Canonical x77 | 80,043,724 | 8.00 | 95 | 1.4 | 0.28 | same |
| None | Canonical x77 | 80,043,724 | 8.00 | 30 | 1.3 | 0.22 | reads faster at any throughput |
| hint Dictionary | Canonical x77 | 80,043,724 | 8.00 | 130 | 1.3 | 0.24 | same |
| hint BitPacked | Canonical x77 | 80,043,724 | 8.00 | 97 | 1.3 | 0.22 | reads faster at any throughput |
| hint RunEnd | Canonical x77 | 80,043,724 | 8.00 | 101 | 1.5 | 0.27 | same |
| hint Zstd | Canonical x77 | 80,043,724 | 8.00 | 105 | 1.3 | 0.27 | reads faster at any throughput |

</details>

<details><summary>random in 0..999, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | BitPacked x82 | 13,800,452 | 1.38 | 15 | 3.6 | 0.31 |  |
| Fastest | BitPacked x82 | 13,800,452 | 1.38 | 15 | 3.5 | 0.30 | same |
| Smallest | BitPacked x82 | 13,800,452 | 1.38 | 164 | 3.5 | 0.31 | same |
| None | Canonical x82 | 81,294,660 | 8.13 | 34 | 1.4 | 0.33 | reads faster above 31,597 MB/s |
| hint Dictionary | Dictionary x82 | 12,676,388 | 1.27 | 68 | 6.3 | 0.53 | reads faster below 417 MB/s |
| hint BitPacked | BitPacked x82 | 13,800,452 | 1.38 | 14 | 3.5 | 0.32 | same |
| hint RunEnd | BitPacked x82 | 13,800,452 | 1.38 | 15 | 3.5 | 0.30 | same |
| hint Zstd | Zstd x82 | 19,283,932 | 1.93 | 156 | 74.0 | 58.66 | reads slower at any throughput |

</details>

### Floating point

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| prices (2 decimals) | Alp x77 | 2.50 | 59 | 6.9 | 0.55 |  |
| 16 distinct, random order | Dictionary x77 | 0.51 | 30 | 2.1 | 0.34 |  |
| 1 000 distinct prices | Dictionary x77 | 1.28 | 68 | 3.8 | 0.42 |  |
| 100 003 distinct, repeating | Zstd x77 | 1.69 | 185 | 79.0 | 71.68 | Auto, 16 MiB chunks: Dictionary x5, 2.21 B/value, scan 7.4 ms, take 3.33 ms; reads faster above 73 MB/s; None: Canonical x77, 8.00 B/value, scan 1.3 ms, take 0.23 ms; reads faster above 812 MB/s |
| uniform in [0, 1) | AlpRd x77 | 6.91 | 45 | 5.6 | 0.71 | None: Canonical x77, 8.00 B/value, scan 1.4 ms, take 0.27 ms; reads faster above 2,658 MB/s |

<details><summary>prices (2 decimals): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Alp x77 | 25,049,724 | 2.50 | 59 | 6.9 | 0.55 |  |
| Fastest | Alp x77 | 25,049,724 | 2.50 | 40 | 4.6 | 0.34 | reads faster at any throughput |
| Smallest | Alp x77 | 25,049,724 | 2.50 | 356 | 4.5 | 0.30 | reads faster at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 40 | 1.6 | 0.33 | reads faster above 10,473 MB/s |
| hint Dictionary | Alp x77 | 25,049,724 | 2.50 | 143 | 4.6 | 0.34 | reads faster at any throughput |
| hint Alp | Alp x77 | 25,049,724 | 2.50 | 39 | 4.6 | 0.35 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 41,240,252 | 4.12 | 286 | 77.1 | 62.95 | reads slower at any throughput |

</details>

<details><summary>16 distinct, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 5,063,740 | 0.51 | 30 | 2.1 | 0.34 |  |
| Fastest | Dictionary x77 | 5,063,740 | 0.51 | 30 | 2.0 | 0.32 | same |
| Smallest | Dictionary x77 | 5,063,740 | 0.51 | 168 | 2.0 | 0.32 | same |
| None | Canonical x77 | 80,043,724 | 8.00 | 32 | 1.3 | 0.24 | reads faster above 99,549 MB/s |
| hint Dictionary | Dictionary x77 | 5,063,740 | 0.51 | 28 | 2.0 | 0.32 | same |
| hint Alp | Dictionary x77 | 5,063,740 | 0.51 | 100 | 2.0 | 0.32 | same |
| hint Zstd | Zstd x77 | 10,545,660 | 1.05 | 127 | 61.5 | 49.92 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 5,032,924 | 0.50 | 43 | 2.0 | 0.27 | same |

</details>

<details><summary>1 000 distinct prices: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x77 | 12,756,252 | 1.28 | 68 | 3.8 | 0.42 |  |
| Fastest | Dictionary x77 | 12,756,252 | 1.28 | 87 | 5.1 | 0.68 | reads slower at any throughput |
| Smallest | Dictionary x77 | 12,756,252 | 1.28 | 334 | 5.3 | 0.65 | reads slower at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 46 | 1.6 | 0.34 | reads faster above 30,690 MB/s |
| hint Dictionary | Dictionary x77 | 12,756,252 | 1.28 | 99 | 5.4 | 0.67 | reads slower at any throughput |
| hint Alp | Alp x77 | 12,756,252 | 1.28 | 161 | 6.7 | 0.62 | reads slower at any throughput |
| hint Zstd | Zstd x77 | 26,073,404 | 2.61 | 293 | 103.3 | 82.83 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 12,545,724 | 1.25 | 110 | 5.4 | 0.62 | reads faster below 130 MB/s |

</details>

<details><summary>100 003 distinct, repeating: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x77 | 16,909,884 | 1.69 | 185 | 79.0 | 71.68 |  |
| Fastest | Zstd x77 | 16,909,884 | 1.69 | 203 | 87.1 | 62.72 | reads slower at any throughput |
| Smallest | Zstd x77 | 16,909,884 | 1.69 | 216 | 61.9 | 52.47 | reads faster at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 27 | 1.3 | 0.23 | reads faster above 812 MB/s |
| hint Dictionary | Zstd x77 | 16,909,884 | 1.69 | 247 | 77.3 | 50.25 | same |
| hint Alp | Zstd x77 | 16,909,884 | 1.69 | 224 | 62.2 | 50.60 | reads faster at any throughput |
| hint Zstd | Zstd x77 | 16,909,884 | 1.69 | 148 | 61.8 | 50.31 | reads faster at any throughput |
| Auto, 16 MiB chunks | Dictionary x5 | 22,129,980 | 2.21 | 98 | 7.4 | 3.33 | reads faster above 73 MB/s |

</details>

<details><summary>uniform in [0, 1): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | AlpRd x77 | 69,054,372 | 6.91 | 45 | 5.6 | 0.71 |  |
| Fastest | AlpRd x77 | 69,054,372 | 6.91 | 48 | 4.7 | 0.51 | reads faster at any throughput |
| Smallest | AlpRd x77 | 69,054,372 | 6.91 | 169 | 4.7 | 0.49 | reads faster at any throughput |
| None | Canonical x77 | 80,043,724 | 8.00 | 32 | 1.4 | 0.27 | reads faster above 2,658 MB/s |
| hint Dictionary | AlpRd x77 | 69,054,372 | 6.91 | 212 | 4.7 | 0.54 | reads faster at any throughput |
| hint Alp | AlpRd x77 | 69,054,372 | 6.91 | 175 | 4.7 | 0.59 | reads faster at any throughput |
| hint Zstd | AlpRd x77 | 69,054,372 | 6.91 | 231 | 4.7 | 0.51 | reads faster at any throughput |

</details>

### Text

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| 16 cities, random order | Dictionary x175 | 0.51 | 210 | 7.4 | 0.76 |  |
| 10 000 distinct ids | Dictionary x306 | 2.54 | 622 | 35.5 | 29.36 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 8.7 ms, take 2.43 ms; reads faster at any throughput; hint Fsst: Fsst x306, 9.06 B/value, scan 32.1 ms, take 1.05 ms; reads faster above 19,417 MB/s |
| 10 000 distinct ids, 10 % null | Dictionary x306 | 2.45 | 642 | 41.6 | 33.35 | Auto, 16 MiB chunks: Dictionary x18, 1.80 B/value, scan 14.5 ms, take 3.70 ms; reads faster at any throughput; hint Fsst: Fsst x306, 8.80 B/value, scan 38.0 ms, take 0.98 ms; reads faster above 17,261 MB/s |
| unique UUIDs | Zstd x611 | 20.61 | 1520 | 293.3 | 290.51 | hint Fsst: Fsst x611, 24.91 B/value, scan 148.5 ms, take 2.13 ms; reads faster above 297 MB/s |
| log lines (~100 B) | Zstd x1221 | 13.71 | 1406 | 394.9 | 321.43 | hint Fsst: Fsst x1221, 23.96 B/value, scan 161.6 ms, take 3.69 ms; reads faster above 440 MB/s |

<details><summary>16 cities, random order: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x175 | 5,117,276 | 0.51 | 210 | 7.4 | 0.76 |  |
| Fastest | Dictionary x175 | 5,117,276 | 0.51 | 201 | 8.0 | 0.95 | reads slower at any throughput |
| Smallest | Dictionary x175 | 5,117,276 | 0.51 | 357 | 4.9 | 0.54 | reads faster at any throughput |
| None | Canonical x175 | 96,746,748 | 9.67 | 159 | 22.8 | 2.78 | reads slower at any throughput |
| hint Dictionary | Dictionary x175 | 5,117,276 | 0.51 | 204 | 4.8 | 0.54 | reads faster at any throughput |
| hint Fsst | Fsst x175 | 64,458,972 | 6.45 | 226 | 25.9 | 0.72 | reads slower at any throughput |
| hint Zstd | Zstd x175 | 17,427,092 | 1.74 | 233 | 91.6 | 64.44 | reads slower at any throughput |

</details>

<details><summary>10 000 distinct ids: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 25,433,780 | 2.54 | 622 | 35.5 | 29.36 |  |
| Fastest | Dictionary x306 | 25,433,780 | 2.54 | 611 | 34.8 | 29.27 | same |
| Smallest | Dictionary x305, Zstd x1 | 25,427,444 | 2.54 | 1311 | 35.6 | 29.93 | same |
| None | Canonical x306 | 140,098,164 | 14.01 | 368 | 18.6 | 4.34 | reads faster above 6,808 MB/s |
| hint Dictionary | Dictionary x306 | 25,433,780 | 2.54 | 951 | 46.1 | 38.67 | reads slower at any throughput |
| hint Fsst | Fsst x306 | 90,615,708 | 9.06 | 374 | 32.1 | 1.05 | reads faster above 19,417 MB/s |
| hint Zstd | Zstd x306 | 27,097,620 | 2.71 | 368 | 90.7 | 68.49 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 18,014,004 | 1.80 | 398 | 8.7 | 2.43 | reads faster at any throughput |

</details>

<details><summary>10 000 distinct ids, 10 % null: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Dictionary x306 | 24,539,276 | 2.45 | 642 | 41.6 | 33.35 |  |
| Fastest | Dictionary x306 | 24,539,276 | 2.45 | 655 | 41.8 | 33.52 | same |
| Smallest | Dictionary x305, Zstd x1 | 24,533,580 | 2.45 | 1384 | 41.8 | 33.98 | same |
| None | Canonical x306 | 151,883,724 | 15.19 | 312 | 26.1 | 3.88 | reads faster above 8,195 MB/s |
| hint Dictionary | Dictionary x306 | 24,539,276 | 2.45 | 646 | 42.1 | 33.53 | same |
| hint Fsst | Fsst x306 | 87,950,764 | 8.80 | 470 | 38.0 | 0.98 | reads faster above 17,261 MB/s |
| hint Zstd | Zstd x306 | 25,595,628 | 2.56 | 391 | 113.5 | 62.19 | reads slower at any throughput |
| Auto, 16 MiB chunks | Dictionary x18 | 17,982,596 | 1.80 | 706 | 14.5 | 3.70 | reads faster at any throughput |

</details>

<details><summary>unique UUIDs: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x611 | 206,058,580 | 20.61 | 1520 | 293.3 | 290.51 |  |
| Fastest | Zstd x611 | 206,058,580 | 20.61 | 1509 | 293.2 | 288.92 | same |
| Smallest | Zstd x611 | 206,058,580 | 20.61 | 2820 | 290.7 | 288.95 | same |
| None | Canonical x611 | 360,155,508 | 36.02 | 196 | 21.0 | 7.27 | reads faster above 566 MB/s |
| hint Dictionary | Zstd x611 | 206,058,580 | 20.61 | 2884 | 289.7 | 288.12 | same |
| hint Fsst | Fsst x611 | 249,114,908 | 24.91 | 1497 | 148.5 | 2.13 | reads faster above 297 MB/s |
| hint Zstd | Zstd x611 | 206,058,580 | 20.61 | 1473 | 290.8 | 289.80 | same |

</details>

<details><summary>log lines (~100 B): every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Zstd x1221 | 137,107,180 | 13.71 | 1406 | 394.9 | 321.43 |  |
| Fastest | Zstd x1221 | 137,107,180 | 13.71 | 1400 | 394.4 | 321.13 | same |
| Smallest | Zstd x1221 | 137,107,180 | 13.71 | 3392 | 400.1 | 321.98 | same |
| None | Canonical x1221 | 725,468,436 | 72.55 | 756 | 34.1 | 12.23 | reads faster above 1,631 MB/s |
| hint Dictionary | Zstd x1221 | 137,107,180 | 13.71 | 2815 | 391.9 | 320.55 | same |
| hint Fsst | Fsst x1221 | 239,634,612 | 23.96 | 1920 | 161.6 | 3.69 | reads faster above 440 MB/s |
| hint Zstd | Zstd x1221 | 137,107,180 | 13.71 | 1399 | 393.9 | 320.61 | same |

</details>

### Booleans

| shape | `Auto` writes | B/value | write ms | scan ms | take ms | worth knowing |
|---|---|---:|---:|---:|---:|---|
| half true | Canonical x10 | 0.13 | 0 | 0.1 | 0.20 |  |
| 1 % true | RunEnd x10 | 0.05 | 1 | 2.6 | 1.95 | None: Canonical x10, 0.13 B/value, scan 0.1 ms, take 0.17 ms; reads faster above 289 MB/s |

<details><summary>half true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.20 |  |
| Fastest | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.18 | same |
| Smallest | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.21 | reads slower at any throughput |
| None | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.31 | reads slower at any throughput |
| hint RunEnd | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.17 | reads faster at any throughput |

</details>

<details><summary>1 % true: every configuration</summary>

| configuration | written as | bytes | B/value | write ms | scan ms | take ms | against Auto |
|---|---|---:|---:|---:|---:|---:|---|
| Auto | RunEnd x10 | 548,084 | 0.05 | 1 | 2.6 | 1.95 |  |
| Fastest | RunEnd x10 | 548,084 | 0.05 | 0 | 2.5 | 1.95 | same |
| Smallest | RunEnd x10 | 548,084 | 0.05 | 0 | 2.4 | 1.87 | same |
| None | Canonical x10 | 1,262,548 | 0.13 | 0 | 0.1 | 0.17 | reads faster above 289 MB/s |
| hint RunEnd | RunEnd x10 | 548,084 | 0.05 | 0 | 2.5 | 1.88 | same |

</details>

### The profiles side by side

`Fastest` wrote what `Auto` wrote on 20 of 20 columns. `Smallest` wrote something else on *i64, sorted runs of 1 000*, *i64, timestamps (ms, increasing, jittered)*, *utf8, 10 000 distinct ids*, *utf8, 10 000 distinct ids, 10 % null*, and took up to 10.8 times `Auto`'s write, 164 ms against 15 ms on *i64, random in 0..999, 10 % null*, since it tries every scheme on every chunk.
`None`, the plain form, made the largest file on 20 of 20 columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit b5104f2 with uncommitted changes, 2026-09-27 02:59 UTC.*
<!-- /results: tradeoffs -->

<!-- results: advice -->
## What the advice picks

`VortexSession.AdviseAsync` on the same columns of 10,000,000 rows, under five goals: whole scans with the
bytes read at 2 GB/s, 100 MB/s and 10 GB/s, a row in a thousand read by row, and the bytes alone.
A cell is what the advice takes where it departs from the writer's own choice, empty where it
does not; a column it never departs on is left out.

| column | scans at 2 GB/s | scans at 100 MB/s | scans at 10 GB/s | a row in 1 000 read by row | size |
|---|---|---|---|---|---|
| i64, timestamps (ms, increasing, jittered) |  | `Zstd` |  | `Zstd` |  |
| i64, 100 003 distinct, repeating | 16 MiB chunks | 16 MiB chunks |  |  | 16 MiB chunks |
| i64, random in 0..999, 10 % null |  | `Dictionary` |  | `Dictionary` | `Dictionary` |
| f64, 100 003 distinct, repeating | 16 MiB chunks | 16 MiB chunks | `Canonical` |  |  |
| f64, uniform in [0, 1) |  |  | `Canonical` |  |  |
| utf8, 10 000 distinct ids | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `Zstd` | 16 MiB chunks |
| utf8, 10 000 distinct ids, 10 % null | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | `Zstd` | 16 MiB chunks |
| utf8, unique UUIDs | `Canonical` |  | `Canonical` | `Fsst` |  |
| utf8, log lines (~100 B) | `Fsst` |  | `Canonical` | `Fsst` |  |
| bool, 1 % true | `Canonical` |  | `Canonical` | `Canonical` |  |

It keeps the writer's choice under every goal on the 10 other columns.

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; commit b5104f2 with uncommitted changes, 2026-09-27 03:01 UTC.*
<!-- /results: advice -->

## Kernels, against what they replaced

Each hot loop of the library against the loop it replaced, or against the floor of the work it
does, measured by BenchmarkDotNet in one process on one clock, both checked to give the same result
before any is timed. A **Ratio** column is the row's time over its baseline's, the row in bold.
These are the fast profile's figures, which tell a direction: `--full` before a class's name runs
the reference profile on that class, the one to quote a small difference from.

<!-- results: kernel:FastLanesKernelBenchmarks -->
### `FastLanesKernelBenchmarks`

| Method       | BitWidth | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | ns/row | GB/s  | Allocated | Alloc Ratio |
|------------- |--------- |-----------:|----------:|----------:|------:|---------------- |-------:|------:|----------:|------------:|
| &#39;i64 scalar&#39; | 17       |  95.737 μs | 1.3069 μs | 0.2023 μs |  1.00 | Baseline        |   1.46 |  5.48 |         - |          NA |
| &#39;i64 vector&#39; | 17       |   8.919 μs | 1.2853 μs | 0.1989 μs |  0.09 | Faster          |   0.14 | 58.78 |         - |          NA |
| &#39;i32 scalar&#39; | 17       | 107.554 μs | 0.4447 μs | 0.1155 μs |  1.12 | Same            |   1.64 |  2.44 |         - |          NA |
| &#39;i64 pack&#39;   | 17       |  11.620 μs | 0.2981 μs | 0.0461 μs |  0.12 | Faster          |   0.18 | 45.12 |         - |          NA |
| &#39;i32 pack&#39;   | 17       |  12.505 μs | 0.4404 μs | 0.0681 μs |  0.13 | Faster          |   0.19 | 20.96 |         - |          NA |
| &#39;i32 vector&#39; | 17       |   4.634 μs | 0.1085 μs | 0.0168 μs |  0.05 | Faster          |   0.07 | 56.58 |         - |          NA |

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

| Method                       | Column | Mean          | Error         | StdDev      | Ratio | MannWhitney(5%) | Allocated | Alloc Ratio |
|----------------------------- |------- |--------------:|--------------:|------------:|------:|---------------- |----------:|------------:|
| &#39;Choose, the whole decision&#39; | i64    | 57,255.034 ns | 1,758.2423 ns | 456.6099 ns | 1.000 | Baseline        |      72 B |        1.00 |
| &#39;candidate: sequence&#39;        | i64    |     13.330 ns |     3.0557 ns |   0.4729 ns | 0.000 | Faster          |         - |        0.00 |
| &#39;candidate: bit packing&#39;     | i64    | 21,888.618 ns | 3,518.0519 ns | 913.6269 ns | 0.382 | Faster          |         - |        0.00 |
| &#39;candidate: FSST&#39;            | i64    |      2.234 ns |     1.1546 ns |   0.2998 ns | 0.000 | Faster          |         - |        0.00 |
| &#39;candidate: zstd&#39;            | i64    | 26,766.801 ns | 2,571.6941 ns | 667.8608 ns | 0.468 | Faster          |      72 B |        1.00 |
| &#39;candidate: ALP&#39;             | i64    |      2.614 ns |     0.0307 ns |   0.0047 ns | 0.000 | Faster          |         - |        0.00 |

*Measured on AMD Ryzen 9 7950X 16-Core Processor (X64), 32 processors, Microsoft Windows 10.0.26200; .NET 11.0.0-rc.1.26425.128; BenchmarkDotNet 0.16.0-preview.1, the fast profile; commit b5104f2 with uncommitted changes, 2026-09-27 03:15 UTC.*
<!-- /results: kernel:CompressorBenchmarks -->

<!-- results: kernel:LanesBenchmarks -->
### `LanesBenchmarks`

*Withdrawn on 2026-10-02.* Measured on 2026-09-27 at commit b5104f2 with a harness that charged Rust
for work Vorticity did not do: its reader copied the file where ours read a mapping a previous round
had left it, it expanded constant columns ours keeps whole, its writer filled a growing `Vec<u8>`
where ours discarded its bytes, and it read the per-encoding files under a split that decoded each
of them ten times ([05-benchmarks.md](../design/05-benchmarks.md) §1 lists every difference). The
figures favoured Vorticity, by more than an order of magnitude on some rows, and are not quoted
until an x64 machine measures this section again with the arguments above.
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
