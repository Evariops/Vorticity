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

The FFI harness of §2 does not exist yet, so **no number below is a ratio against Rust** and none
should be quoted as one. What `bench/Vorticity.Benchmarks` measures today is the other half of the
protocol: absolute figures per axis, with allocations, on a fixed dataset, which is what makes a
regression visible and what a SIMD kernel has to beat.

A first run on `containers/zoned_many_zones_nulls` (65 536 rows over five mixed columns, Apple
arm64, in-process toolchain):

| Axis | Mean | Reading |
|---|---|---|
| Full scan | 1.91 ms | ~34 M rows/s across five columns |
| Projected scan, 1 of 5 columns | 102 µs | 19× the full scan: projection pushdown is real |
| Open to first batch | 80 µs | the local floor for the 1–2 round trip promise |
| `Take` of 1 000 scattered rows | 110 µs | |
| Selective filter, pruning **on** | 58 µs | |
| Selective filter, pruning **off** | 546 µs | 9.4× — what zone-map pruning is worth here |

One toolchain note, because it is load-bearing rather than incidental: BenchmarkDotNet 0.15.4 does
not know the `net11.0` moniker and its SDK validator throws before any benchmark runs, so the
project uses the **in-process** toolchain. That costs process isolation — no cross-runtime or
cross-GC comparison — and costs the measurement nothing. It reverts to the default toolchain the day
BenchmarkDotNet ships net11.0 support.

## 2. Measurement method

**Single process, single timer.** `vortex-ffi` builds as a cdylib exposing a stable C API. The
BenchmarkDotNet harness loads it through `DllImport` and measures both implementations in the same
process, on the same data, with the same page-cache state and the same clock. This eliminates the
usual cross-runner noise (different timers, different warm-up, different filesystem state) which
is exactly the noise that makes a 1.4× ratio unreadable.

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
