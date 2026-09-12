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

`containers/zoned_many_zones_nulls` — 65 536 rows over five mixed columns, Apple M4 Pro (arm64),
in-process toolchain, single-threaded on both sides. Both readers return 65 536 rows in **64
batches**, checked before every run:

| Axis | Vorticity | Vortex Rust | Ratio |
|---|---|---|---|
| **Full scan** | 1.878 ms | 1.312 ms | **1.43× slower** — criterion 4 wants ≤ 2× |
| Projected scan, 1 of 5 columns | 102 µs | 264 µs | 2.6× faster |
| Open → first batch | 82.5 µs | 1.217 ms | 14.7× faster (see below) |
| Open → footer only | 41.6 µs | 49.6 µs | 1.19× faster |
| Empty FFI call | — | 2.5 ns | the floor: noise on every axis above |

And the absolute axes the harness does not cover:

| Axis | Mean | Reading |
|---|---|---|
| `Take` of 1 000 scattered rows | 110 µs | |
| Selective filter, pruning **on** | 58 µs | |
| Selective filter, pruning **off** | 546 µs | 9.4× — what zone-map pruning is worth here |

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

* BenchmarkDotNet with `MemoryDiagnoser`, `ThreadingDiagnoser`, and hardware counters
  (`BranchMispredictions`, `CacheMisses`) where the platform allows.
* Ratio against Rust as the primary reported column; absolute values secondary.
* **Thread count pinned on both sides**, with ratios reported at 1 thread and at N threads. The
  Rust harness's threading is configured explicitly in the FFI setup rather than left to its
  default — otherwise the ratio measures a threading-model difference, not implementation quality
  ([09-contracts.md](09-contracts.md) §2).
* **Measurement order randomized.** Interleaving two implementations by hand through FFI defeats
  BenchmarkDotNet's usual protections: thermal drift over a long run systematically favors whoever
  goes first.
* Results are committed per release tag so regressions are visible in history.
* CI runs a reduced subset on every PR touching `Compute/` or `Arrays/`, with a failure threshold
  on regression rather than on absolute value (CI hardware is too noisy for absolutes).

## 6. Secondary context (not the target)

For ecosystem framing only — and clearly labelled as such in any report — the suite may include
ParquetSharp (Arrow C++ wrapper) and Parquet.Net (fully managed) reading an equivalent Parquet
file. This answers "should a .NET shop move to Vortex?", which is a different question from "is
our implementation good?". It must never be used to claim Vorticity is fast.
