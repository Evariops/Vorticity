# Benchmarks

## 1. The baseline is the Rust implementation

The reference point is **Vortex's own Rust implementation** — the fastest Vortex implementation
that exists, and the one the format was designed around. Comparing against .NET Parquet libraries
would measure a format difference, not an implementation difference, and would flatter us for the
wrong reasons.

Stated goal: **stay within 2× of Rust on a full scan**, and within 1.5× on the pure decode
kernels — *per ISA*. A single global figure would make the gate flap on whichever CI runner has the
weakest vector unit, so targets are tracked separately for x64/AVX2, x64/AVX-512 and arm64/NEON.
Every kernel has a NEON path by contract ([03-architecture.md](03-architecture.md) §1), so CI needs
an arm64 leg or that contract is untested. Anything worse points at a specific defect (a copy that should not exist, a missing
vectorized path, an allocation in a loop), and the benchmark suite must make that defect visible
rather than just reporting a ratio.

## 1b. Where this actually stands

The FFI harness of §2 now exists ([`tools/vxbench-rs`](../tools/vxbench-rs)), so the numbers below
**are** ratios against Rust, measured in one process on the same bytes with the same clock.

**They come from `--ratio-check`, not from a BenchmarkDotNet table, and that is a correction rather
than a preference.** The classes that produced the figures this section used to carry drove the Rust
side through the *lazy* scan, which hands back arrays in the file's own encodings without
decompressing them, against a .NET reader whose `RecordBatch` is canonical by construction. That is
a decode compared with an absence of decode. The gate drives it through `execute::<Canonical>`
instead, interleaves the two sides against one clock so thermal drift is common to both, and takes
a median of 51 rounds. BENCH-AUDIT.md A1 has the whole account; the practical consequence is that
every ratio below moved, and the full-scan one moved by half.

`containers/zoned_many_zones_nulls` — 65 536 rows over five mixed columns, Apple M4 Pro (arm64),
in-process toolchain, single-threaded on both sides. Both readers are checked to return the same
rows on every axis before it is timed:

| Axis | Vorticity | Vortex Rust | Ratio |
|---|---|---|---|
| **Full scan** | 614.8 µs | 1.347 ms | **0.46×** — criterion 4 wants ≤ 2× |
| Projected scan, 1 of 5 columns | 108.6 µs | 250.7 µs | 0.43× |
| Open → first batch | 99.5 µs | 1.202 ms | 0.08× (see below) |
| Open → footer only | 42.2 µs | 52.1 µs | 0.81× |
| Filtered scan, 1% band | 112.6 µs | 470.3 µs | 0.24× |
| Filtered scan, half the rows | 218.3 µs | 672.2 µs | 0.33× |
| Scattered take, 64 of 64 splits | 348.9 µs | 1.409 ms | 0.25× |
| Read and write back | 16.41 ms | 14.72 ms | 1.12× |
| Empty FFI call | — | 2.5 ns | the floor: noise on every axis above |

Each of those has a ceiling in `RatioCheck.cs` and `--ratio-check` exits non-zero over it, so these
are gated numbers rather than reported ones. Absolutes are one machine's; read the ratio column.

The full-scan row read **1.43× slower** when this section was first written, and the figure stayed
there through two commits that moved it. That is the failure this whole section exists to prevent,
so the chain is recorded rather than the endpoint. All three numbers are ours; Rust was the control
and did not move (1.312 → 1.343 ms, which is the run-to-run spread). **The chain below is against
the lazy reference**, which is why its last row says 0.96× where the table above says 0.46×: the
same bytes, the same reader, a reference asked to do twice the work:

| after | ours | ratio vs the lazy reference |
|---|---|---|
| the FFI harness first measured it | 1.878 ms | 1.43× |
| wide stores in the FSST and OnPair kernels | 1.403 ms | 1.05× |
| the vectorized FastLanes unpack | **1.294 ms** | **0.96×** |

The last step was A/B'd by building the library both ways and running the same benchmark in the
same session, because the first attempt at that comparison silently measured the same binary twice:
`dotnet run --no-build` after a failed build runs the previous binary, and the two "different"
numbers came back identical to four significant figures. Identical is not a small difference; it is
a broken experiment.

**"0.46×" is one file on one machine, and it is not a claim that this library is faster than Vortex
Rust.** It is `containers/zoned_many_zones_nulls` on an Apple M4 Pro — arm64, so NEON, so
`Vector128` only on our side — single-threaded on both. A machine with AVX-512 gives our kernels a
wider path *and* gives Rust's the same; a file dominated by FSST would put us back above 1. What it
does support is the narrower and more useful statement: on a mixed five-column file, the managed
scan is no longer the bottleneck anyone expected it to be.

**Per-encoding decode**, the axis §3 calls the one that matters most during development, and the
one §1 holds to a tighter target (**≤ 1.5×**).

**The table below is superseded and kept as history.** It was produced by `DecodeComparison`, a
BenchmarkDotNet class that no longer exists, on 4096-row files, against the lazy Rust scan — so it
carries both defects at once: a fixed cost larger than the signal, and a reference that did not
decode what it was being compared on. The instrument now is **`--throughput`**: the same question on
files of a million rows, against `execute::<Canonical>`, with a ceiling per encoding and a non-zero
exit over it. Its fifty rows live in [bench/BASELINE.md](../../bench/BASELINE.md) under "The 1M axis";
`bench/README.md` says how to run it. The correction is not cosmetic — published as `fsst`
"1.74× rather than 10.6×" — and it is why nothing should be quoted from here.

Ratios are ours ÷ Rust, so above 1.00 is slower:

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
| `vortex.varbinview` | 57.5 µs | 65.3 µs | **0.88× — we are faster** |

**This table is a RANKING, and must not be read as a measurement.** Two things are inside every
number. First, these corpus files hold 4096 rows, and roughly 35 µs of each row above is the
open-and-walk-the-layout cost both implementations pay before a single value is decoded — Rust's own
floor across the easy encodings is 34–38 µs, so the ratios are all pulled toward 1. Second, and
worse: **between-process variance here is larger than the effects being measured.** Three runs of
effectively identical code on `encodings/fsst` returned 135 µs, 146 µs and 202 µs, each with a
BenchmarkDotNet error bar under ±3 µs — the statistics describe the iterations inside one process
and say nothing about the next one. The in-process toolchain (§1b, last paragraph) is part of why.

So: use this table to decide WHAT to work on, and a microbenchmark to decide whether the work
helped. §3's "ns/value on 1M values, isolated" is the measurement that would give honest absolutes,
and it wants a bigger dataset than the corpus carries.

**The clearest demonstration of why is `fastlanes.bitpacked`.** Its unpack kernel became 3.6× faster
on i64 and 7.0× on i32 (measured below), and its row in this table moved from 1.26× to 1.18×. Both
numbers are correct. Only about 7 µs of that 42 is the kernel; the other 35 is the fixed cost, which
no kernel work can touch. The full-scan row, which decodes 64 batches rather than one 4096-row file,
moved 1.403 → 1.294 ms from the same change.

The ranking is unambiguous: the three byte-oriented decoders — FSST, OnPair and Zstd — are the slow
ones, and the bit-packing kernels are already close.

### The FSST kernel, measured properly

`FsstSymbolTable` argued for years of comments that reproducing the reference's shape "would buy
nothing until the rest of the library is vectorized". The ranking above said FSST was our slowest
kernel, so the shape was tried: one unaligned 8-byte store per symbol, advancing by the symbol's
real length, with an exact-copy tail where the slack runs out.

The end-to-end benchmark said it was **8% slower**, and that was nearly written down as a finding.
It was drift. `FsstKernelBenchmarks` runs the candidate shapes in one process over one code stream,
which is what §5 prescribes and what should have been done first. It now runs **four** arms, a 2×2
of store shape against validation, because comparing across both at once is what produced two wrong
numbers in this section's history:

| 64 KiB of codes | bare | with a decoder's validation |
|---|---|---|
| exact copy (what the library did) | 184.0 µs | 190.5 µs |
| **wide store** (what it does now) | **38.7 µs** | **53.5 µs** |

**4.7× faster like for like**, 3.6× with validation on both sides. Two lessons, both already in §5:
a microbenchmark of the thing being changed beats an end-to-end one diluted by a fixed cost, and two
candidates must be measured against ONE clock or thermal drift picks the winner.

**A third lesson cost this table the ability to prove any of that.** Its `exact copy` arm used to
call `FsstSymbolTable.Decode` — "what the library does" — and then the wide store landed *in* that
method. From that commit on, both arms ran the same shape and the table read 1.37×, with nothing
regressed and no way to tell from the numbers. A benchmark that measures the library and labels the
result "the old shape" has a shelf life of exactly one commit.
`FastLanesKernelBenchmarks` never had the problem because it always carried its own scalar loop;
this one now carries its own exact copy for the same reason.

**A fourth, from repairing it: do not attribute a gap you have not measured.** This section briefly
said the 14 µs between the two arms was the `FsstSymbolTable.Create` call only the baseline made.
It is not. `Create` validates the table's shape and walks its 200 symbol lengths — it cannot cost
microseconds. The gap is the validation `Decode` does **per code**, 65 536 times: that the code names
a symbol the table holds, and that the write stays inside the destination. The 2×2 above prices it
directly at 14.8 µs on the wide path and 6.5 µs on the exact one, which is the expected shape — the
same fixed work is a larger fraction of a faster loop.

**And the figure that does not reproduce, stated as such rather than quietly dropped.** The original
row read 306.8 µs for the exact copy where the like-for-like arm today reads 190.5 µs. The current
arm is a faithful transplant of the pre-change `Decode` — same checks, same `Slice().CopyTo()`, read
back out of the commit that replaced it — and `global.json` pins the same SDK now as then. The
`wide store` arm reproduces across the same interval to within 2% (38.8 → 38.7 µs), so a 1.6×
discrepancy confined to the other arm is more likely an artifact of that original run than a change
in anything since. **The reproducible number is 4.7×.** 7.9× should not be quoted again.

The same trick applies to `vortex.onpair`, whose tokens are at most 16 bytes and now take one
`Vector128` store each; its decoder also stopped re-reading the token offsets through a
physical-type switch twice per code. That one was measured end to end across four separate process
launches — 117 µs, then 81, 82, 81 — which is consistent enough to trust, though a kernel
microbenchmark would still be better evidence.

### The FastLanes unpack kernel, measured the same way

`fastlanes.bitpacked` is the encoding [90-registry.md](90-registry.md) calls "the single most
important kernel", and it was scalar. It vectorizes because of how the FastLanes layout is built
rather than by luck: `lane` IS the SIMD lane, so for a fixed row both the packed words read and the
positions written are contiguous — `index(row, lane) = base(row) + lane` at every element width —
and there is no gather or scatter anywhere in it.

`FastLanesKernelBenchmarks`, both shapes in one process, 64 blocks per operation, Apple M4 Pro
(NEON, so the `Vector128` path; the 256 and 512 paths exist and are exercised by CI's x64 legs):

| bits/value | i64 scalar | i64 vector | i32 scalar | i32 vector |
|---|---|---|---|---|
| 10 | 70.4 µs | **18.8 µs (3.7×)** | 76.3 µs | **10.7 µs (7.1×)** |
| 17 | 75.4 µs | **21.4 µs (3.5×)** | 86.0 µs | **12.3 µs (7.0×)** |
| 33 | 86.5 µs | **23.8 µs (3.6×)** | 104.6 µs | **15.6 µs (6.7×)** |

Two lanes per `Vector128` at i64 and four at i32, so the ceiling from width alone would be 2× and
4×. Both beat it, because interchanging the loops also hoists the per-row arithmetic out of the lane
loop: the scalar version recomputes four divisions and two masks per VALUE, all of them constant
across the 16 to 128 lanes of a row.

A defect this also found: `DecodeBenchmarks`'s parameter list named `encodings/bitpacked`,
`encodings/for` and `encodings/rle`, none of which exist — the corpus ids are
`encodings/fastlanes_*`. That class could never have run.

And the absolute axes the harness does not cover:

| Axis | Mean | Reading |
|---|---|---|
| `Take` of 1 000 scattered rows | 86.8 µs | was 110 µs before the take pushdown of `927bf0c` |
| Selective filter, pruning **on** | 74.2 µs | over a band that matches ~100 rows |
| Selective filter, pruning **off** | 546.3 µs | **7.4×** — what zone-map pruning is worth here |

**This row used to read 58 µs and 9.4×, and that pair was measuring an empty band.** `monotone`
starts at 1 000 000 and steps by 3, so the original filter — `[1 000, 1 100)` — matched **nothing**.
The benchmark was reporting the cost of pruning away an entire file, which is the easy half of the
claim; `820b1b6` found the same defect in `ZonePruningTests`, fixed the band to one that matches
~100 rows, and left this table describing the old one.

The correction is not an inference. Today's code run against the original band returns **59.44 µs**,
against the 58 µs first recorded — so nothing regressed, and the whole of the difference is the
band. The pruning-off arm is 546.9 µs either way, because it scans and filters all 65 536 rows
whichever band is asked for.

That last sentence is also the trap. "The unpruned arm did not move and the pruned one did" looks
like proof that code changed — it was written into this file as exactly that — but an arm that
cannot respond to the band is no evidence at all about the band. **7.4× is pruning against rows that
exist**, and it is the smaller and more honest number.

**Read the first-batch row carefully.** It is like-for-like in unit of work — both sides emit 64
batches — but Rust's array stream evidently materializes the whole local scan on its first poll:
its time to first batch (1.217 ms) is within 8% of its time to scan everything (1.312 ms). So the
number is a real difference in *latency to first row*, which is what the axis is for, and not a
decode-speed result. Quoting it as "14× faster than Rust" without that sentence would be
dishonest.

**One harness bias, found and fixed, because it moved a number.** The first version of the shim
built a `VortexSession::default()` per call. That registers every edition and initializes the arrow
and parquet-variant integrations — about 90 µs — which nothing on the .NET side pays per open,
since `EncodingRegistry` is static. Charging it to Rust made the footer-only axis read 141 µs
instead of 49.6 µs, and the projected scan 361 µs instead of 264 µs. The session is now built once
and the *file* is opened from scratch on both sides, which is the comparison that was intended.

The allocation figures (190 KB per full scan, 133 KB of it in the open path and the first batch)
are not in tension with criterion 3: that criterion is about the *steady state per batch*, which
works out to under a kilobyte here and is pinned exactly by `ScanAllocationTests` rather than by a
benchmark total.

Every figure in this section is restated with its machine, its commit and its reproduction command
in [bench/BASELINE.md](../bench/BASELINE.md), which is the file that moves in the same commit as any
change that moves a number. This section explains what the numbers mean; that file records what they
currently are.

One toolchain note, because it is load-bearing rather than incidental: BenchmarkDotNet 0.15.4 does
not know the `net11.0` moniker and its SDK validator throws before any benchmark runs, so the
project uses the **in-process** toolchain. That costs process isolation — no cross-runtime or
cross-GC comparison — and costs the measurement nothing. It reverts to the default toolchain the day
BenchmarkDotNet ships net11.0 support.

## 2. Measurement method

**Single process, single timer.** A cdylib exposing a stable C API is loaded through `DllImport`,
and the BenchmarkDotNet harness measures both implementations in the same process, on the same
data, with the same page-cache state and the same clock. This eliminates the usual cross-runner
noise (different timers, different warm-up, different filesystem state) which is exactly the noise
that makes a 1.4× ratio unreadable.

This section used to name `vortex-ffi` as that cdylib. It is not: `vortex-ffi` is `publish = false`
upstream, so using it would mean a second git dependency, and it is a general-purpose C API with
its own object model whose per-call overhead would land inside the measurement. The shim in
[`tools/vxbench-rs`](../tools/vxbench-rs) is built against the same crates.io pin the corpus was
generated with (`vortex = "=0.86.1"`), exposes one entry point per axis, and returns a row count —
no handles, no allocation across the boundary, nothing to measure but the scan. It is built by
hand and is not a CI dependency; the comparison benchmarks fail loudly when it is absent rather
than quietly reporting no ratio.

Caveat to control for: the FFI boundary costs a call. It is amortized by measuring whole-file
scans and whole-column decodes, never per-value operations. A dedicated empty-call benchmark
quantifies the floor so it can be subtracted when it matters.

A secondary out-of-process mode (hyperfine over a Rust binary vs. a Native AOT `vxdump`) serves as
a sanity check that the in-process numbers are not an artifact of the harness.

## 3. Benchmark axes

| Axis | What it measures | Why |
|---|---|---|
| **Full scan** | rows/s and uncompressed GB/s, all columns | The headline number |
| **Projection scan** | 1 column out of 50 | Tests I/O pruning, not decode; the case Vortex is built for |
| **Random access** | latency of reading row N, and of 1000 scattered rows | Vortex's main claim over Parquet |
| **Open latency** | time to first batch, local **and** over a simulated HTTP source with injected latency | The 1–2 round trip promise ([02-format.md](02-format.md) §1) is the object-storage metric, and it is invisible on a local file where round trips are free |
| **Per-encoding decode** | ns/value on 1M values, isolated | Localizes a regression to one kernel |
| **Filter pushdown** | scan with a 1%/10%/50%-selectivity predicate | Tests pruning and mask propagation |
| **Write** | MB/s and compression ratio | Output size ≤ **105%** of Rust's on the same data, edition and configuration, with the delta reported per dataset. Not byte-parity: the compressor is a sampler, so two honest implementations of the same algorithm diverge on borderline data — an absolute gate would be permanently red or silently overfitted to the corpus |
| **Row encoding** | MB/s and ns/row, vs `vortex-row` | Pure CPU, no I/O — the cleanest signal of code-generation quality we have |
| **Allocations** | bytes/op, gen0/1/2 | Must be zero per batch in steady state |
| **Peak RSS** | on a large scan | Detects buffer accumulation |

Per-encoding decode is the axis that matters most during development: `fastlanes.bitpacked`,
`fastlanes.for`, `fastlanes.rle`, `vortex.runend`, `vortex.dict`, `vortex.alp`, `vortex.alprd`,
`vortex.fsst`, `vortex.sparse`, `vortex.zigzag`, `vortex.varbinview`, `vortex.bool`, and
`vortex.zstd` — the last being a comparison of BCL Zstd bindings against Rust's, where any gap is
interop overhead rather than our code.

Row encoding is measured on the mix that matters: all-fixed-width schemas (where the encoder
should be near memory-bandwidth-bound) and string-heavy schemas (where the 32-byte block
structure dominates).

## 4. Datasets

Reuse upstream's benchmark datasets so numbers are comparable with published Vortex figures. The
upstream repository ships `compress-bench`, `random-access-bench`, `string-bench`, plus engine
benchmarks; mirroring their data choices and methodology is cheaper than inventing our own and
makes external comparison possible.

* **TPC-H** `lineitem`, SF1 and SF10 — mixed numeric/date/string, the standard reference.
* **ClickBench** `hits` — wide (100+ columns), string-heavy, the projection-pushdown case.
* **NYC taxi** — floats and timestamps, where ALP and DateTimeParts matter.
* **Synthetic per-encoding** — a generator producing data that provably selects one encoding, for
  the kernel microbenchmarks.

Every dataset is written once by the Rust writer with default settings and read by both
implementations. Reading identical bytes is what makes the comparison honest.

## 5. Reporting

**What is true today**, and it is less than this section used to promise:

* BenchmarkDotNet with `MemoryDiagnoser`, in the fast profile by default and `Job.Default` under
  `--full`. **No `ThreadingDiagnoser` and no hardware counters**: `[HardwareCounters]` is a Windows
  ETW feature, and the reference machine is arm64 macOS, where the equivalent is Instruments and
  interactive (BENCH-AUDIT.md D5).
* Ratio against Rust as the primary reported column; absolute values secondary. `--ratio-check` and
  `--throughput --check` carry a ceiling per axis and exit non-zero over it.
* **Measurement order alternated**, which is the part of "randomized" that matters here: the gates
  interleave the two sides round by round and swap which goes first, so thermal drift is common to
  both instead of a result. Interleaving by hand through FFI is exactly what defeats
  BenchmarkDotNet's usual protections, and it is why the gates do their own timing.
* Reference ratios are committed, in `RatioCheck.References` and `ThroughputCheck.References`, and
  move down in the same commit as the change that moved them.

**What is NOT true, and was written here as though it were.** Saying a document promises what
nothing does is worse than saying nothing:

* **Thread count is not pinned on either side, and no ratio is reported at N threads.** Both sides
  are single-threaded by default and nothing configures the Rust harness's threading explicitly.
  The multi-lane axis is BENCH-AUDIT.md D2, unbuilt.
* **CI runs no benchmark at all.** There is no perf job, reduced subset or otherwise, on any PR.
  The gates are commands a developer runs; `bench/README.md` lists them and what each costs. A CI
  job is BENCH-AUDIT.md §5 and needs an x64 runner to be worth having.
* **Nothing is committed per release tag.** There are no release tags.

## 6. Secondary context (not the target)

For ecosystem framing only — and clearly labelled as such in any report — the suite may include
ParquetSharp (Arrow C++ wrapper) and Parquet.Net (fully managed) reading an equivalent Parquet
file. This answers "should a .NET shop move to Vortex?", which is a different question from "is
our implementation good?". It must never be used to claim Vorticity is fast.
