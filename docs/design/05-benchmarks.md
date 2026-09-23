# Benchmarks

## 1. The baseline is the Rust implementation

The reference point is **Vortex's own Rust implementation** — the fastest Vortex implementation
that exists, and the one the format was designed around. Comparing against .NET Parquet libraries
would measure a format difference, not an implementation difference, and would flatter us for the
wrong reasons.

Stated goal: **stay within 2× of Rust on a full scan**, and within 1.5× on the pure decode
kernels — *per ISA*. A single global figure would make the gate flap on whichever runner has the
weakest vector unit, so the targets are meant to be tracked separately for x64/AVX2, x64/AVX-512
and arm64/NEON. Every kernel has a NEON path by contract ([03-architecture.md](03-architecture.md)
§1). Anything worse than the target points at a specific defect (a copy that should not exist, a
missing vectorized path, an allocation in a loop), and the benchmark suite must make that defect
visible rather than just reporting a ratio.

**Every figure today is one machine's**: an Apple M4 Pro, arm64, so the `Vector128` path. CI builds
and tests on x64 and on arm64 (`ubuntu-24.04-arm` and macOS), which holds the kernels' contract for
correctness on both, but runs no benchmark (§6): the x64 targets are stated and unmeasured.

## 2. Where this stands

Measured on 2026-09-23 at `592e053`: Apple M4 Pro (14 cores, ten of performance and four of
efficiency), macOS 26.7, .NET 11.0.0-rc.1, and the reference at Vortex 0.86.1, **built as upstream
builds its own benchmarks**: mimalloc as the allocator, `-C target-cpu=native -C
force-frame-pointers=yes`, one codegen unit, no LTO. Absolutes are this machine's; the ratios are
what carries. How to run each instrument, and what it costs, is [bench/README.md](../../bench/README.md).

### 2.1 What a caller sees

`--report` runs eight scenarios at 2^20 rows and at ten times that, **each side in its own
process** (§3.2), **on one core and on all fourteen**, and on two files: the one our writer makes and
the one the reference's writer makes from the same rows. Our side runs as the Native AOT build,
which the ratio is taken against, and on one core again on the JIT. [The guide's benchmark
page](../guide/benchmarks.md) is generated from it and carries every figure with its spread. **Its
ratio is the reference's time over ours**, so above 1.00 we took less — the inverse of every
in-process table below.

On one core, the reference on its single-threaded runtime and our scans at one lane:

| scenario | 2^20 rows, our file | 2^20 rows, Rust's file | 10 × 2^20, our file | 10 × 2^20, Rust's file |
|---|---|---|---|---|
| open the file, read no rows | 3.48× | 3.32× | 2.80× | 3.23× |
| scan every column | 1.22× | 1.43× | 1.15× | 1.28× |
| project one column of four | 2.76× | 2.06× | 2.61× | 1.70× |
| filter, a band of one row in a hundred | 1.33× | 1.76× | 2.29× | 3.88× |
| filter, a band of half the rows | 1.31× | 1.52× | 1.23× | 1.51× |
| take a thousand rows | **0.93×** | 1.67× | **0.88×** | 2.23× |
| read the file and write it back | 1.85× | 2.06× | 1.92× | 2.09× |

On all fourteen cores, the reference on a Tokio runtime of fourteen workers and our scans at
fourteen lanes:

| scenario | 2^20 rows, our file | 2^20 rows, Rust's file | 10 × 2^20, our file | 10 × 2^20, Rust's file |
|---|---|---|---|---|
| open the file, read no rows | 4.18× | 3.20× | 4.86× | 3.86× |
| scan every column | 2.14× | 1.95× | 1.33× | 1.60× |
| project one column of four | 2.66× | 2.49× | 3.37× | 1.95× |
| filter, a band of one row in a hundred | 1.64× | 2.33× | 3.28× | 4.52× |
| filter, a band of half the rows | **0.88×** | 1.34× | **0.38×** | **0.65×** |
| take a thousand rows | **0.53×** | 1.89× | **0.31×** | 2.08× |
| read the file and write it back | **0.63×** | **0.64×** | **0.42×** | **0.43×** |

How to read it:

* **The reference was measured slower than it is until `592e053`**: on the system allocator, the
  baseline instruction set, one thread, and decoding in the loop that drained its stream. Built and
  run as upstream runs it, it is 10 to 35 % faster on the in-process axes (§2.2), and the page's
  `write` went from 7.87× to 1.92×. Every ratio before that commit is against the slower build.
* **The two files differ where it matters.** Our writer stores the `f64` column as `vortex.zstd`,
  the reference's as `vortex.alprd`; our file is 24.5 MB at ten million rows, the reference's 79.0 MB,
  and a full scan of ours costs both readers about four times the scan of theirs (67.8 against
  17.7 ms on our side). A take has to inflate a whole zstd frame for each row it wants, which is why
  our only one-core losses are the take on our own file. What `Auto` should do there is a decision
  of the writer's ([11-write-strategy.md](11-write-strategy.md) §3.4), not of the bench.
* **Our writer is single-threaded**; the reference compresses its chunks on every core. On all
  cores, the `write` rows compare one core against fourteen, which is what a caller gets.
* **Our lanes do not help a filter or a take as they help a scan**: from one core to fourteen, the
  full scan of our file goes from 67.8 to 8.8 ms, the wide filter from 34.9 to 24.2, the take from
  64.3 to 28.6, where the reference's take goes from 56.5 to 9.0.

Peak resident memory is lower than the reference's on most reads and on every write: 41 MiB
against 427 on the one-core write of our file at ten million rows. **The JIT column makes another
claim**: the same code in a fresh `dotnet` process, compiling as it goes — 12 ms to open a file the
native build opens in 0.2. That is what a process pays once, not what a warm one pays per call,
which is what §2.2 measures.

### 2.2 In one process, after warm-up

**`--ratio-check`**: twenty-five axes, on the conformance corpus and two generated tables, each
side called in turn against one clock (§3.1). Ours over the reference, so under 1.00 we are faster;
the median of the per-round ratios with its 95 % interval; the reference is what the gate's ceiling
is built on, × 1.15.

| axis | ours | Rust | ratio [95 %] | reference |
|---|---:|---:|---|---:|
| full scan | 608.0 µs | 1 807.1 µs | 0.333 [0.325; 0.337] | 0.344 |
| full scan, upstream lazy | 641.2 µs | 1 326.3 µs | 0.472 [0.446; 0.486] | 0.500 |
| projected scan, 1 of 5 columns | 105.5 µs | 260.6 µs | 0.395 [0.382; 0.417] | 0.442 |
| projected scan, upstream lazy | 91.2 µs | 209.4 µs | 0.439 [0.429; 0.445] | 0.484 |
| open to first batch | 100.9 µs | 1 144.7 µs | 0.089 [0.086; 0.093] | 0.090 |
| open, footer only | 32.9 µs | 54.9 µs | 0.607 [0.595; 0.630] | 0.724 |
| read and write back | 5 567.7 µs | 14 036.7 µs | **0.398** [0.379; 0.403] | 0.321 |
| filtered scan, 1 % band | 93.2 µs | 402.3 µs | 0.233 [0.228; 0.236] | 0.258 |
| filtered scan, half the rows | 183.0 µs | 682.3 µs | 0.269 [0.265; 0.271] | 0.294 |
| scattered take, 64 of 64 splits | 376.5 µs | 1 655.3 µs | 0.227 [0.224; 0.229] | 0.228 |
| rewritten zoned, reference's | 603.6 µs | 1 816.7 µs | 0.332 [0.327; 0.338] | 0.356 |
| rewritten zoned, ours | 776.3 µs | 826.2 µs | 0.925 [0.906; 0.940] | 1.089 |
| rewritten high card, reference's | 40.3 µs | 44.8 µs | 0.905 [0.872; 0.925] | 0.920 |
| rewritten high card, ours | 58.1 µs | 59.5 µs | 0.931 [0.888; 0.974] | 1.001 |
| key order, sorted column, 1 % band | 83.7 µs | 109.3 µs | 0.764 [0.739; 0.774] | 0.754 |
| key order, uncorrelated, 64 rows | 234.2 µs | 191.3 µs | **1.179** [1.136; 1.251] | 1.262 |
| count, exact cover, 1 % band | 210.2 µs | 302.5 µs | 0.640 [0.614; 0.677] | 0.687 |
| filtered scan, string equality, fsst | 270.1 µs | 316.2 µs | 0.844 [0.814; 0.875] | 0.898 |
| filtered scan, string prefix, fsst | 851.2 µs | 687.0 µs | **1.254** [1.227; 1.288] | 1.237 |
| filtered scan, string equality, dict | 198.1 µs | 106.5 µs | **1.776** [1.716; 1.874] | 1.933 |
| filtered scan, string prefix, dict | 206.8 µs | 148.5 µs | **1.328** [1.312; 1.424] | 1.573 |
| filtered scan, band, runend | 110.6 µs | 115.4 µs | 0.983 [0.940; 1.037] | 1.301 |
| filtered scan, band, bitpacked | 100.9 µs | 92.6 µs | **1.084** [1.025; 1.120] | 1.257 |
| full scan, 1M table | 6 369.3 µs | 101 297.6 µs | 0.062 [0.061; 0.065] | 0.060 |
| projected scan, 1 of 50 columns | 83.8 µs | 1 275.5 µs | 0.069 [0.066; 0.072] | 0.088 |

How to read it:

* **These are against the reference built as upstream builds it** (§3.1), which takes 10 to 35 %
  less time than the build before `592e053` on most axes; the references were carried over from
  that build by what it moved each ratio, so the verdicts are those it gave. Measured under both
  builds on one build of ours, `full scan` read 0.300 before and 0.337 after, our time unchanged.
* **Twenty axes are under 1.** The five over it are predicates pushed into strings (equality over a
  dictionary, a prefix over a dictionary, a prefix over FSST), a band over bit-packing, and the key
  cursor over an uncorrelated column.
* **`open to first batch` is not a decode result.** The reference's stream materializes the whole
  local scan on its first poll: its first batch takes 1.14 ms of the 1.81 ms its whole scan takes.
  Both sides emit the same unit of work, so 0.089 is a real difference in latency to the first row,
  and nothing more; quoting it as "eleven times faster than Rust" would be dishonest.
* **The `upstream lazy` pairs ask another question.** They time the reference's scan without
  canonicalizing: arrays handed back in the file's own encodings, their length read off metadata.
  Kept because "how long to get a stream of arrays you may never read" is a real question about a
  real API, and never to be quoted as a decode ratio.
* **The `rewritten` axes read one file as the reference wrote it and as our writer rewrote it**, by
  both readers. Every other axis reads bytes the reference wrote, so these four are the only place
  our own encoding choices are measured against a decode. Read them as a 2×2: the two `ours`
  figures are our reader on the two files, the two `Rust` figures the reference's.
* **`read and write back` is over its ceiling** (0.369), as it was under the build before (0.367
  against 0.342). It is ours. `bench/ab.sh` measures our own write of that file 17 % slower than at
  `a6ee391`, and puts the step on `82b8bba`, which sizes a chunk by its widest column: on this file
  of local ranges the larger chunks it now writes cost about 30 % more to write, besides the 11.6 %
  more bytes that commit recorded. Later commits took back part of it; the rest is open.
* **Two axes are `STALE`**: the band over run-ends, 0.983 against 1.301, and the projection of one
  column of fifty, 0.069 against 0.088.

**`--throughput`**: the fifty-seven encoding files of a million rows (§5), each read by both sides in
turn. Three axes, each with its own reference table:

| axis | what each side does | encodings | median | at or under 1.00 | highest |
|---|---|---:|---:|---:|---|
| read | decodes every value | 57 | 0.57 | 51 | `fastlanes.bitpacked` 1.21 |
| take | takes 64 rows spread over the file | 57 | 0.33 | 52 | `vortex.sequence` and a chunked column of mixed validity, 1.08 |
| write | reads the file back out to a sink that keeps nothing | 56 | 0.30 | 55 | `vortex.zstd` 1.43 |

The six reads above 1 are `fastlanes.bitpacked` 1.21, bit-packing with patches 1.11, `vortex.alprd`
1.10, `vortex.fsst` and a chunked column of mixed validity 1.08, and a dictionary of 64-bit codes
1.01. Both writers are given the rows decoded (§3.1), which is what moved the write axis most:
re-encoding from the file's own zstd, the reference wrote `zstd_buffers` eight times slower than it
writes the same rows decoded, and it now writes `zstd_nullable`, which it refused to re-encode. It
refuses `parquet_variant` instead: decoded, the variant keeps a lazy slice its writer cannot
serialize. Our side of `vortex.zstd` decompresses with the base library's `ZstandardDecoder` and the
reference with the zstd crate, so its ratio mixes the two bindings with what each reader does around
them — how its frames are cut, and what a take inflates — and that part is ours: its references read
1.06 and take 1.05, and it reads 0.57 and takes 0.24 here. On the write, from decoded rows on both
sides, ours takes 1.43 times the reference's time.

**The references were carried over to the reference's new build on 2026-09-23** (§3.1), each by what
the build moved its ratio, so what they held against our code they still hold:

* **More than 15 % under them** (`STALE`), gains not yet written into the ratchets: reading `pco`,
  `vortex.zstd`, `fastlanes.delta`, `parquet_variant` and five more; taking `vortex.zstd`, `pco`,
  `parquet_variant` and four more; writing `pco`, two dictionaries, `vortex.null` and
  `vortex.sparse`; and on `--ratio-check`, the run-end band and `projected scan, 1 of 50 columns`.
* **Over them in two processes** (`OVER`), six writes, each over before the carry and measured
  against our own earlier build on one clock before being called anything:
  * `vortex.alprd`, 0.42 (0.23): ours, and deliberate. `0a191b2` offers ALP-RD to the floats ALP
    refuses, and the file this axis writes, which went out plain, now goes out as ALP-RD at about
    86 % of the plain form: 1.88× the write time for 14 % fewer bytes, in the encoding the reference
    writes.
  * `vortex.zstd`, 1.43 (1.07): ours by 5 % since `a6ee391`, the rest the reference's.
  * `varbin`, `varbinview` and a chunked `varbinview`, 0.15 to 0.29 (0.12 to 0.21): **not ours**. Our
    own time is 6 to 13 % lower than at `a6ee391`, so the reference's fell, by a quarter. The
    reference was seen to run in two states, one about a quarter slower on such columns, for a
    reason not known; these references were set while it ran slow.
  * a chunked column of one chunk, 0.14 (0.11), not yet attributed.

### 2.3 Allocations

Allocations are held by ratchets in the test suite, not by a benchmark total: counted in a quiet
process, they are exact.

* **Nothing per batch** in steady state, the batch included, on every scan
  (`ScanAllocationTests`), and under 1 000 bytes a batch across every thread of a parallel one.
* **Per operation** on the five-column corpus file, open included (`PathAllocationTests`): a full
  scan costs what its first batch does, 43 848 bytes; a projection of one column 46 200; a take of 64
  rows over 64 splits 45 096. A full scan of one encoding's file, 3.1 to 3.9 KB.
* **Per write**: `WriteAllocationTests`, per encoding.

Each ceiling comes down in the commit that lowers it.

## 3. Measurement method

### 3.1 One process, one clock: the gates

**One shim, one clock.** [`tools/vxbench-rs`](../../tools/vxbench-rs) is built against the same
crates.io pin the corpus was generated with (`vortex = "=0.86.1"`) and loaded through
`LibraryImport`: one entry point per axis, returning a row count, no handles and no allocation
across the boundary, nothing to measure but the work. It is built by hand and is not a CI
dependency; the gates refuse to run without it rather than report no ratio. `vortex-ffi` was the
first candidate and is not used: it is `publish = false` upstream, and a general-purpose C API whose
per-call overhead would land inside the measurement.

**Built as upstream builds its benchmarks**: mimalloc as the global allocator, `-C target-cpu=native
-C force-frame-pointers=yes`, one codegen unit, no LTO. In the gates it runs on
`vortex::io::runtime::single`, all the work on the calling thread, against our reader at one lane;
`vxbench_set_threads(n)` moves it to a Tokio runtime of n workers, which is how upstream's
benchmarks use every core. Each split is decoded on its own task, through `ScanBuilder::map`, as
upstream's Arrow conversion does, and its writer is given the rows decoded, which is what our reader
gives ours.

**The denominator is recorded.** Both gates keep the fingerprint of the build their references were
set under and refuse to gate under another: a rebuild moves every ratio, and a move there reads
exactly like one of ours. A new build is carried over with `--recalibrate N --rebase-from <previous
build>`: each pass runs under both builds, the same build of ours, and each reference moves by what
the new build moved its ratio, so that whatever the table held against our code, it still holds.

**Both sides do the same work.** Every entry point that reads values executes
`RecursiveCanonical`. `execute::<Canonical>` stops as soon as the root array is one of twelve
canonical kinds, a struct among them, which on a table is before a single column has been decoded.
Before any ratio is trusted, `--ffi-check` asserts that both readers return the same row count and
the same checksum of every decoded value, in file order, on seven corpus files.

**Interleaved and paired.** Each round times both sides microseconds apart, alternating which goes
first, so thermal drift is common to both instead of a result; the ratio is taken per round, and
their median is what is held, with a 95 % bootstrap interval. Rounds start at 21 and continue until
half the interval is within 5 % of the median, 501 at most. A call shorter than the timer's
resolution is repeated k times inside a round, and k is part of the reference: k calls in a row are
a warm path where one was cache-cold. A ratio is `OVER` only when its whole interval is above the
ceiling, `STALE` when all of it is under 0.85 × the reference, and `noisy` in between.

**Between processes varies more than within one**, two to three times. So `--recalibrate N`
measures N passes in N processes and takes the highest of their medians, and never raises a
reference; a red run is replayed three times, and two reds out of three is a regression; the
throughput gate confirms an `OVER` in a second process by itself. A reference only comes down, in
the commit whose improvement moved it, and raising one is a dated, justified edit.

**Never under `DOTNET_TieredCompilation=0`.** The pin costs our side dynamic profile-guided
optimization and the native reference nothing, and turns green axes red. `bench/ab.sh`, which
compares two builds of this library, pins it on purpose: both of its sides are the same runtime.

**The boundary costs a call**, which the axes amortize by timing whole scans and whole columns, never
a value. The shim's empty call is now only the probe of whether it loaded: the shortest axis, a
footer read at 36 µs, is four orders of magnitude above it.

### 3.2 One process per side: `--report`

The published comparison starts a process per side and per run, and each times its action from its
own clock once it is up — opening the file, doing the work, rendering the rows — with the process
start reported apart. Our side is the Native AOT runner, `bench/Vorticity.Benchmarks.Runner`, a
native binary like the reference's `vxbench`, built for the machine's instruction set
(`IlcInstructionSet=native`) with the workstation garbage collector, and on one core again the
framework-dependent host on the JIT. Every scenario runs on one core — the reference's
single-threaded runtime, our scans at one lane — and on all of them: `--threads all` gives the
reference a Tokio worker per processor and our scans a lane per processor.

Both sides read two files: the one our writer makes, written again on every run, and the one the
reference's writer makes from the same rows (`vxbench rewrite`). The page names each file's chunks
and outermost encodings per column. Peak resident memory and processor time are each process's own
`getrusage`. Both sides must render the same rows or the harness fails. The sides take turns, the
first changing from run to run. Five runs, the median with the lowest and highest beside it, one
discarded before them, in about 75 s. This is the out-of-process check this section once planned
around `hyperfine` and `vxdump`, and it is the one the guide publishes.

### 3.3 Kernels: BenchmarkDotNet

BenchmarkDotNet 0.16.0-preview.1, the first version that builds a `net11.0` host. The fast profile
runs in this process and gives a direction, not a number; `--full` runs out of process, with
isolation per case, GC and runtime jobs and `--disasm`; `--full --inprocess` keeps it in this
process when the generated host is the problem. Sixteen classes: ten in the default run, six curves
behind `--explore`. A kernel class carries the arm it replaced — the scalar loop, the exact copy —
so that it never measures the library twice under two labels (§8.4).

### 3.4 Profiling and attribution

* **`bench/profile.sh cycles|allocations|trace`** samples the Native AOT runner through Instruments,
  from the command line: cycles per function and per source line, inlined code included; every
  allocation with its stack, managed and native; Processor Trace where the machine allows it.
  BenchmarkDotNet's hardware counters remain a Windows feature.
* **`bench/ab.sh <commit> [--after <commit>] <file> [scenario…]`** builds another commit of the
  library in a worktree, with today's scenarios, and times both builds in one process, interleaved.
  It is what tells a red gate's cause, our code or the reference, and which commit: §2.2 was
  attributed with it. It reaches back to the commit where the bench was rebuilt on the public
  surface, `a6ee391`; before it, today's scenarios do not compile against the library.
* **`bench/compare.sh`** compares two recorded BenchmarkDotNet runs, Mann-Whitney per case.

## 4. Benchmark axes

| Axis | What it measures | Instrument | Why |
|---|---|---|---|
| **Full scan** | every column of every row | `--ratio-check`, `--report` `scan` | The headline number |
| **Projection** | 1 column of 5, 1 of 50 | `--ratio-check`, `--report` `project` | Tests I/O pruning, not decode; the case Vortex is built for |
| **Random access** | 64 rows over 64 splits; 1 000 rows spread over a file; 64 rows of each encoding | `--ratio-check`, `--report` `take`, `--throughput --take` | Vortex's main claim over Parquet |
| **Open latency** | to the footer, and to the first batch, on a local file | `--ratio-check`, `--report` `open` | The 1–2 round trip promise ([02-format.md](02-format.md) §1). **Not measured where it matters**: the source over simulated HTTP with injected latency this axis calls for was never built, and round trips are free on a local file |
| **Per-encoding decode** | ns/value on a million values | `--throughput` | Localizes a regression to one kernel |
| **Filter pushdown** | a 1 % and a 50 % band; equality and prefix over `fsst` and `dict`; a band over `runend` and `bitpacked`; key order; an exact count | `--ratio-check`, `--report` `filter-narrow` and `filter-wide`; the selectivity curve in `FilterSelectivityBenchmarks` (`--explore`) | Tests pruning and mask propagation |
| **Write** | time, and output size ≤ **105 %** of Rust's on the same data, edition and configuration | `--throughput --write`, `--ratio-check` `read and write back` and `rewritten`, `--report` `write` and `append`; `WrittenSizeTests` for the size | Size is measured over the whole corpus with the delta reported per dataset, and [90-registry.md](90-registry.md) carries the figure. Not byte-parity: the two sides choose schemes by different means — the reference samples, this writer measures the whole block in one fused pass — so they diverge on borderline data by design, and an absolute gate would be permanently red or silently overfitted to the corpus |
| **Row encoding** | MB/s and ns/row | `RowEncodingBenchmarks` | Pure CPU, no I/O — the cleanest signal of code-generation quality. **No reference beside it**: the shim takes a path and returns rows, so there is no way to hand `vortex-row` a batch. The figures are a before and after on one machine, not a ratio |
| **Threads** | our scan at n lanes against the reference on a Tokio runtime of n workers, threads pinned on both sides; every report scenario on one core and on all of them | `LanesBenchmarks`; `--ratio-check --lanes N`, reported without a reference; `--report` | Where the speed-up stops, and whether both sides stop in the same place |
| **Allocations** | bytes per batch and per operation | the ratchets of §2.3 | Must be zero per batch in steady state: it is |
| **Peak RSS** | resident memory of a whole process | `--report` | Detects buffer accumulation |
| **Encoding trade-offs** | bytes, write, scan and take of every profile and hint on twenty column shapes; the encoding advice under five goals | `--tradeoffs`, `--tradeoffs --advise` | What [choose-encodings.md](../guide/choose-encodings.md) and `VortexSession.AdviseAsync` stand on; a measurement, not a gate |

Per-encoding decode is the axis that matters most during development: fifty-seven files, one per
encoding and per notable shape of one, each a million rows (`table_wide`, fifty columns, holds
fifty thousand), each held to its own ceiling on read, on take and on write.

Row encoding is measured on the mix that matters: all-fixed-width schemas, where the encoder should
be near memory-bandwidth-bound, and string-heavy ones, where the 32-byte block structure dominates.

## 5. Datasets

The intention was to reuse upstream's benchmark datasets — TPC-H `lineitem`, ClickBench `hits`, NYC
taxi — so that numbers would be comparable with published Vortex figures. **None of them is wired
up, and TPC-H was decided against** (2026-09-13): they are gigabytes that do not belong in a
repository, and the property that makes a comparison honest is *both implementations read identical
bytes*, which holds whatever the bytes are. `Corpus.cs` keeps the seam — a path, resolved once — so
wiring one up later is a path and not a redesign.

What is measured instead:

* **The conformance corpus**, 856 files written by the Rust writer with real distributions, which
  is also what every correctness gate reads. `bench/crosscheck.sh` has the reference read every file
  our writer writes from it, value by value.
* **Synthetic per encoding**: fifty-seven files that provably select one encoding each, 468 MB,
  written by the Rust generator (`bench/gen-throughput.sh`) outside the repository, with a manifest
  of their sha256 that `--check` verifies before it measures. Beside them, the two tables
  `--ratio-check` reads: `table_mixed`, a million rows of a key, a measure, a price, sixteen labels, a
  million distinct names and a timestamp, and `table_wide`, fifty columns.
* **The report's table**: four columns — a monotone `i64`, an `f64`, a short `utf8` and a nullable
  `bool` — at 2^20 and at ten times 2^20 rows, written by our writer on every run, and written again
  from the same rows by the reference's.
* **The trade-offs' twenty column shapes**, ten million rows each, generated from the row number.

The corpus and the synthetic files are written by the Rust writer, the report's table and the
`rewritten` files by ours, and every comparison reads the same bytes on both sides, which is what
makes it honest.

## 6. Reporting

**What is true today:**

* The gates are commands with an exit code: `--ratio-check` and `--throughput --check` (with
  `--take` and `--write`) exit non-zero over a ceiling, and `bench/gate.sh` runs the allocation and
  count ratchets, `--ffi-check` and `--ratio-check` before a push.
* Ratio against Rust is the primary column and absolutes are secondary. The references are
  committed — `RatioCheck.References` and the three tables of `ThroughputCheck` — and move with the
  code as §3.1 says.
* Kernels go through BenchmarkDotNet with `MemoryDiagnoser`, and `LanesBenchmarks` with
  `ThreadingDiagnoser`.
* [The guide's benchmark page](../guide/benchmarks.md) is generated by `--report --markdown`, never
  edited, and names its machine, runtime, reference, commit and date.

**What is not true, and was once written here as though it were:**

* **CI runs no benchmark at all.** The gates are commands a developer runs; `bench/README.md` lists
  them and what each costs. A CI job needs an x64 runner to be worth having, and there is no x64
  figure anywhere yet.
* **No reference is kept per ISA.** Every table is the M4 Pro's.
* **Nothing is committed per release tag.** There are no release tags.

## 7. Secondary context (not the target)

For ecosystem framing only — and clearly labelled as such in any report — the suite may include
ParquetSharp (Arrow C++ wrapper) and Parquet.Net (fully managed) reading an equivalent Parquet
file. This answers "should a .NET shop move to Vortex?", which is a different question from "is our
implementation good?". It must never be used to claim Vorticity is fast. Neither is in the suite
today.

## 8. History: what the numbers used to say

Kept for what each correction cost to learn. None of these figures is current.

### 8.1 A decode compared with an absence of decode

The first ratios of this section came from BenchmarkDotNet classes that drove the Rust side through
the *lazy* scan, which hands back arrays in the file's own encodings without decompressing them,
against a .NET reader whose `RecordBatch` is canonical by construction. That is a decode compared
with an absence of decode. Moving the reference onto a canonicalizing call, interleaving the two
sides against one clock and taking the median of many rounds moved every ratio, and the full-scan
one by half: `containers/zoned_many_zones_nulls` read 0.96× against the lazy reference and 0.46×
against the decoding one, with the same bytes and the same reader. The `upstream lazy` axes of §2.2
keep the old question, labelled.

### 8.2 The full scan, commit by commit

The full-scan row read **1.43× slower** than the lazy reference when it was first measured, and
the figure stayed there through two commits that moved it. Rust was the control and did not move
(1.312 → 1.343 ms, the run-to-run spread):

| after | ours | ratio vs the lazy reference |
|---|---|---|
| the FFI harness first measured it | 1.878 ms | 1.43× |
| wide stores in the FSST and OnPair kernels | 1.403 ms | 1.05× |
| the vectorized FastLanes unpack | **1.294 ms** | **0.96×** |

The last step was A/B'd by building the library both ways and running the same benchmark in the
same session, because the first attempt silently measured the same binary twice: `dotnet run
--no-build` after a failed build runs the previous binary, and the two "different" numbers came back
identical to four significant figures. Identical is not a small difference; it is a broken
experiment. `bench/ab.sh` exists because of it.

### 8.3 A ranking read as a measurement

Per-encoding decode was first compared on the corpus's 4 096-row files, by a class that no longer
exists, against the lazy reference: a fixed cost larger than the signal and a reference that did not
decode, both at once. Ratios, ours over Rust, so above 1.00 is slower:

| Encoding | Vorticity | Vortex Rust | ratio |
|---|---|---|---|
| `vortex.fsst` | 146.1 µs | 43.3 µs | **3.37×** |
| `vortex.onpair` | 86.6 µs | 38.2 µs | **2.27×** |
| `vortex.zstd` | 75.5 µs | 36.3 µs | **2.08×** |
| `fastlanes.rle` | 55.1 µs | 36.6 µs | 1.50× |
| `vortex.dict` | 52.4 µs | 36.7 µs | 1.43× |
| `vortex.alprd` | 52.2 µs | 37.1 µs | 1.41× |
| `vortex.runend` | 48.0 µs | 36.1 µs | 1.33× |
| `vortex.alp` | 49.7 µs | 38.9 µs | 1.28× |
| `vortex.sparse` | 45.8 µs | 36.3 µs | 1.26× |
| `vortex.bool` | 42.7 µs | 35.0 µs | 1.22× |
| `fastlanes.bitpacked` | 42.0 µs | 35.5 µs | 1.18× |
| `fastlanes.for` | 42.5 µs | 37.0 µs | 1.15× |
| `vortex.zigzag` | 42.0 µs | 36.5 µs | 1.15× |
| `vortex.datetimeparts` | 42.1 µs | 38.1 µs | 1.10× |
| `vortex.decimal_byte_parts` | 48.7 µs | 44.6 µs | 1.09× |
| `vortex.varbinview` | 57.5 µs | 65.3 µs | 0.88× |

**That table was a ranking, and could not be read as a measurement.** Roughly 35 µs of each row is
the open-and-walk-the-layout cost both implementations pay before a single value is decoded — Rust's
own floor across the easy encodings was 34–38 µs — so every ratio is pulled toward 1. And between
processes the variance was larger than the effects: three runs of effectively identical code on
`encodings/fsst` returned 135, 146 and 202 µs, each with an error bar under ±3 µs, which describes the
iterations inside one process and says nothing about the next. The clearest demonstration is
`fastlanes.bitpacked`: its unpack kernel became 3.9× faster on i64 and 8.7× on i32, and its row moved
from 1.26× to 1.18×. Both numbers are correct; only about 7 µs of the 42 is the kernel. The table
decided what to work on, and a microbenchmark whether the work helped. `--throughput` answers the
same question on files of a million rows, where the fixed cost is under a percent: FSST, published
there as 1.74× rather than 10.6×, reads 1.11 in §2.2.

### 8.4 The FSST kernel, measured properly

`FsstSymbolTable` argued for years of comments that reproducing the reference's shape "would buy
nothing until the rest of the library is vectorized". The ranking said FSST was our slowest kernel,
so the shape was tried: one unaligned 8-byte store per symbol, advancing by the symbol's real length,
with an exact-copy tail where the slack runs out. The end-to-end benchmark said it was **8 % slower**,
and that was nearly written down as a finding. It was drift. `FsstKernelBenchmarks` runs the
candidate shapes in one process over one code stream, as a 2×2 of store shape against validation,
because comparing across both at once produced two wrong numbers:

| 64 KiB of codes | bare | with a decoder's validation |
|---|---|---|
| exact copy (what the library did) | 184.0 µs | 190.5 µs |
| **wide store** (what it does since) | **38.7 µs** | **53.5 µs** |

**4.7× faster like for like**, 3.6× with validation on both sides. The lessons:

* A microbenchmark of the thing being changed beats an end-to-end one diluted by a fixed cost, and
  two candidates must be measured against one clock or thermal drift picks the winner.
* **A benchmark that measures the library and labels the result "the old shape" has a shelf life of
  one commit.** The `exact copy` arm used to call `FsstSymbolTable.Decode`, and then the wide store
  landed in that method: both arms ran the same shape and the table read 1.37×, with nothing
  regressed. The class now carries its own exact copy, as `FastLanesKernelBenchmarks` always carried
  its own scalar loop.
* **Do not attribute a gap you have not measured.** The 14 µs between the two arms was once put on
  the `FsstSymbolTable.Create` call only the baseline made; it is the validation `Decode` does per
  code, 65 536 times, which the 2×2 prices at 14.8 µs on the wide path and 6.5 µs on the exact one.
* **A figure that does not reproduce is said so.** The first exact-copy row read 306.8 µs where a
  faithful transplant of the pre-change `Decode` reads 190.5, under the same pinned SDK, while the
  wide arm reproduced within 2 %. The reproducible number is 4.7×; 7.9× should not be quoted again.

The same trick applies to `vortex.onpair`, whose tokens are at most 16 bytes and take one
`Vector128` store each; its decoder also stopped re-reading the token offsets through a
physical-type switch twice per code (117 µs, then 81, 82, 81 over four launches).

### 8.5 The FastLanes unpack kernel

`fastlanes.bitpacked` is the encoding [90-registry.md](90-registry.md) calls "the single most
important kernel", and it was scalar. It vectorizes because of how the FastLanes layout is built
rather than by luck: `lane` IS the SIMD lane, so for a fixed row both the packed words read and the
positions written are contiguous — `index(row, lane) = base(row) + lane` at every element width —
and there is no gather or scatter anywhere in it. `FastLanesKernelBenchmarks`, both shapes in one
process, 64 blocks per operation, NEON so the `Vector128` path; the arms call `UnpackBlocks`, the
entry point the library uses for a run of blocks (the per-block one cost 25 % and read as a
regression that was not one):

| bits/value | i64 scalar | i64 vector | i32 scalar | i32 vector |
|---|---|---|---|---|
| 10 | 71.3 µs | **17.7 µs (4.0×)** | 77.5 µs | **9.4 µs (8.2×)** |
| 17 | 76.0 µs | **19.6 µs (3.9×)** | 87.2 µs | **10.0 µs (8.7×)** |
| 33 | 87.0 µs | **21.7 µs (4.0×)** | 105.1 µs | **11.7 µs (9.0×)** |

Two lanes per `Vector128` at i64 and four at i32, so the ceiling from width alone would be 2× and
4×. Both beat it, because interchanging the loops also hoists the per-row arithmetic out of the lane
loop: the scalar version recomputes four divisions and two masks per value, all of them constant
across the 16 to 128 lanes of a row. A defect it also found: a decode class's parameter list named
`encodings/bitpacked`, `encodings/for` and `encodings/rle`, none of which exist — the corpus ids are
`encodings/fastlanes_*` — so that class could never have run.

### 8.6 An empty band

The selective filter once read 58 µs with pruning and 9.4× without, and that pair measured an empty
band: `monotone` starts at 1 000 000 and steps by 3, so the filter `[1 000, 1 100)` matched
nothing, and the benchmark reported the cost of pruning away a whole file, the easy half of the
claim. Fixed to a band that matches about a hundred rows, it read 74.2 µs against 546.3: **7.4× is
pruning against rows that exist.** The correction was measured, not inferred: the code of the day
against the old band returned 59.44 µs, against the 58 first recorded. And the trap it left: "the
unpruned arm did not move and the pruned one did" looks like proof that code changed, but an arm
that cannot respond to the band is no evidence at all about the band.

### 8.7 A cost charged to the wrong side

The first version of the shim built a `VortexSession::default()` per call, which registers every
edition and initializes the arrow and parquet-variant integrations — about 90 µs that nothing on the
.NET side pays per open. It made the footer-only axis read 141 µs instead of 49.6, and the projected
scan 361 µs instead of 264. The session is built once since, and the *file* is opened from scratch
on both sides, which is the comparison that was intended.
