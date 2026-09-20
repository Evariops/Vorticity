# The bench, in one page

Every command below is run from the repository root. `PROJ` stands for
`--project bench/Vorticity.Benchmarks`; add `-c Release` always — a Debug benchmark measures the
JIT's unoptimized output and is worth nothing. Forget it and every mode refuses with exit 2 rather
than printing a table.

```
dotnet run -c Release PROJ -- <arguments>
```

## The loop: what you changed, what you run

| you changed | you run | it costs | what it answers |
|---|---|---|---|
| a kernel | `-- fastlanes` (its class) | 1–8 s | did the kernel move, against the ported arm on the same clock |
| anything, want a direction | no argument at all | **2 min 40** | the ten default classes, 49 cases, fast profile |
| a number about to be written down | `-- --full fastlanes` | 1–4 min | the reference profile, on the ONE class concerned |
| a read path | `-- --ratio-check [axis…]` | 56 s, or 5 s for one axis | the twenty-four axes against Rust, interleaved, each held to a ceiling |
| a bitmap kernel | `-- BitmapKernel` | 15 s | each of `Classify`, `CountSet`, `CopyRange`, `PackBytes` against the loop it replaced |
| a gather, a tile, a dictionary | `-- RowKernel` | 9 s | `Gather`, `GatherMasked`, `Tile` against the per-row type switch each replaced |
| a string heap cut into views | `-- ViewKernel` | 9 s | `SumLengths`, `BuildFromLengths`, `RequireAscending` against the per-row loops |
| the OnPair token concatenation | `-- OnPairKernel` | 6 s | 48% of an OnPair scan, against the per-code switch it replaced |
| whether your change moved anything | `bench/compare.sh --record before <filter>`, then `--record after`, then `bench/compare.sh before after` | 2x the class | Mann-Whitney per case: Faster / Same / Slower |
| a decoder | `-- --throughput <family>` | ~30 s | ns/value per encoding at a million rows, against Rust. **Reports, never gates**: a run of one file is +32% on our side (B8) |
| the writer, or a decoder | `-- --throughput --check` | 54 s | the same, as a gate over all 57 files |
| a selective decode (`DecodeSelected`) | `-- --throughput --take --check` | 90 s | 64 rows spread over each of the 57 files, against Rust |
| a lane, or the degree of parallelism | `-- LanesBench` (`--full` walks 1, 2, 4, 8) | 15 s | ours at n lanes against the reference's pool at n workers, threads pinned both sides |
| the compressor's decision | `-- CompressorBench` | 12 s | `Choose` and one arm per candidate; `--full` adds the utf8 and f64 columns |
| the writer, per encoding | `-- --throughput --write --check` | 9 min | each file read back out to a discarding sink, against Rust. **Gates since B14** — 56 references, median **0.290**, nothing above ×2 and the slowest axis `parquet_variant` at 1.01. Three are noisier than the ×1.15 margin (`onpair`, `sparse`, `constant`): re-run before believing a red. Only `zstd_nullable` produces no ratio, and that is **the reference** refusing to write it |
| every file we write, read by Rust | `bench/crosscheck.sh` | 80 s | 854 files compared scalar by scalar, 2 538 751 rows; needs cargo. `gate.sh --crosscheck` folds it in |
| **anything, before you push** | `bench/gate.sh` | 68 s | the nine ratchets, `--ffi-check`, `--ratio-check`; exit 1 if one is red. `--throughput` adds the full axis (92 s) |
| a change too big for a ported arm | `bench/ab.sh <commit> [--after <commit>] <file> [scenario…]` | 7 s | two builds of the library in one process, interleaved, ratio per round |
| the FFI harness, or before trusting any ratio | `-- --ffi-check` | < 1 s | both readers return the same rows on the same files |
| **what a user would see**, for the published page | `-- --report` | ~4 min | eight high-level scenarios at a million rows and ten million, **each side in its own process**: wall time with its spread, peak resident memory, rows rendered. The reference is the `vxbench` **binary**, so build it first. `--markdown --out <path>` writes the page. Every figure here includes starting a runtime, which the in-process ratios above do not — the `open` row is that floor |
| a hot path you want to profile | `-- --profile <scenario> [seconds]` | as asked | a bare loop for `dotnet-trace`, no harness in the profile |
| a ratchet | `dotnet test Vorticity.slnx -c Release` | ~1 min | the suite plus the nine allocation, count and budget ratchets |

**A fast-profile figure is a direction, not a number.** Anything under about 5 % on a kernel, and
every figure that goes into `bench-BASELINE.md`, is confirmed with `--full` on the one class
concerned. Why, and what the fast profile costs in fidelity: `BenchmarkConfig.cs`, and BENCH-AUDIT.md
§4.2 for the measurements behind it.

**Never run the whole BDN suite to decide one change.** Run the class, before and after, on the same
build. The gates are the other instrument: they are *commands*, deliberately not `dotnet test` cases
(BENCH-AUDIT.md §8), so CI calls them and a red one never silently stops the suite from running.

## Selecting

Three mechanisms, finest first:

| form | means |
|---|---|
| `-- fsst` | a bare word becomes `--filter '*fsst*'` — the shell would eat the stars |
| `-- --filter '*Fsst*.Library'` | BenchmarkDotNet's own glob, over `Class.Method` |
| `-- --anyCategories kernel` | by category: every class is `kernel`, `path` or `explore` |
| `-- --list flat` | what exists under the current selection |

**`-- --help` lists every mode**, its flags and what each costs. `--full`, `--explore` and `--inprocess` are ours and are consumed before BenchmarkDotNet sees the
rest, so `-- --full fastlanes` works as written. Everything else is forwarded.

**`--full` runs out of process; the fast profile does not.** BenchmarkDotNet 0.16.0-preview.1 is the
first version that can build a `net11.0` host, so the reference profile now gets process isolation
per case, GC and runtime jobs, and `--disasm` (41 874 bytes of arm64 for `FsstKernelBenchmarks`).
It costs the host's build: `--full` on a two-case class is 2 min 10 rather than 7 s, which is why
the fast profile stays in this process. `--full --inprocess` is the escape hatch.

## Asking what our own writer costs to read back

```sh
# Write the file out with our writer, keeping the bytes. A third argument is a target edition,
# which excludes the encodings that came later: Core20250500 has no vortex.zstd.
dotnet run -c Release --project bench/Vorticity.Benchmarks -- \
    --rewrite tests/Vorticity.Conformance/corpus/containers/zoned_many_zones_nulls.vortex \
    /tmp/pair/ours.vortex

# The two block knobs decide the file's chunking, and a zone IS a chunk, so they decide what a
# selective scan costs and how much per-chunk fixed cost the file pays (B12). `off` disables the
# 1 MiB coalescing, which leaves chunks of exactly one row block.
dotnet run -c Release --project bench/Vorticity.Benchmarks -- \
    --rewrite in.vortex out.vortex --row-block 8192 --data-block-bytes off

# Then measure any directory of files, both readers, one clock.
VORTICITY_THROUGHPUT_CORPUS=/tmp/pair \
    dotnet run -c Release --project bench/Vorticity.Benchmarks -- --throughput
```

That pair is how BENCH-AUDIT.md A5 was attributed: our rewrite scans in 914 µs against 342 µs for
the same rewrite without zstd, while the reference reads both in 140 µs. Note that
`vxdump --encodings` cannot answer the same question by itself — the reference interns all 37
encodings of the registry whatever the file uses, so only OUR dictionary is informative. What does
answer it is `vxdump --layout`, whose `encoding=` column names the array encoding of every terminal
node (B10):

```sh
dotnet run -c Release --project tools/vxdump -- /tmp/pair/ours.vortex --layout |
    grep -o 'encoding=.*' | sort | uniq -c | sort -rn
```

Three `vortex.zstd` nodes on our side and none on the reference's, all three on the `strs` column —
which is A5's 63 % located to a column rather than inferred from a target edition.

## Reading the assembly a kernel actually got

Two routes, and they answer slightly different questions.

```sh
# The whole class, through BenchmarkDotNet. Needs --full, because the disassembler needs the
# out-of-process toolchain (bench/README's profile table). Writes <Class>-asm.md under the
# artifacts directory: 41 874 bytes of arm64 for FsstKernelBenchmarks, in 36 s.
dotnet run -c Release --project bench/Vorticity.Benchmarks -- \
    --full --filter '*FsstKernelBenchmarks.Library*' --disasm

# One named method, no harness, no benchmark. Seconds, and it works in any run of anything.
DOTNET_TieredCompilation=0 \
DOTNET_JitDisasm='*UnpackBlocks*' \
DOTNET_JitStdOutFile=/tmp/jit.asm \
    dotnet run -c Release --project bench/Vorticity.Benchmarks -- FastLanesKernel
```

`DOTNET_TieredCompilation=0` is not optional if you want the code that runs in steady state: with
tiering on, the listing's header says `Tier0-FullOpts ... optimized using Synthesized PGO`, which is
the first full-opts compilation and not necessarily the last. With it off the header says `FullOpts`
and there is one listing per generic instantiation.

**Hardware counters do not work here.** `[HardwareCounters]` needs
`BenchmarkDotNet.Diagnostics.Windows` and ETW; on this machine the run prints `Unable to resolve
IHardwareCountersDiagnoser diagnoser using dynamic assembly loading` and then completes **without
counters and without failing** — so a table that looks normal is simply missing the columns you
asked for. Branch mispredictions and cache misses need a Windows machine (BENCH-AUDIT.md D5b).

**No argument means all of them**, not a prompt: without one, BenchmarkDotNet asks the console which
class to run, which makes the default run do nothing under a script or in CI. `Program.cs` supplies
`--filter *` when nothing else has said what to run.

## The classes

Sixteen, and `-- --list flat` is what answers this question for real: ten in the default run,
six more behind `--explore`.

| class | in the default run |
|---|---|
| `FastLanesKernelBenchmarks` | yes — 17 bits; `--full` adds 10 and 33 |
| `FsstKernelBenchmarks` | yes |
| `OnPairKernelBenchmarks` | yes |
| `FilterKernelBenchmarks` | yes |
| `BitmapKernelBenchmarks` | yes |
| `RowKernelBenchmarks` | yes |
| `ViewKernelBenchmarks` | yes |
| `RowEncodingBenchmarks` | yes |
| `CompressorBenchmarks` | yes |
| `LanesBenchmarks` | yes |
| `RandomAccessBenchmarks` | no — `--explore` |
| `FilterSelectivityBenchmarks` | no — `--explore` |
| `ConstantFormBenchmarks` | no — `--explore` |
| `VarBinFormBenchmarks` | no — `--explore` |
| `ComplexityProbes` | no — `--explore` |
| `MergeProbes` | no — `--explore` |

`explore` holds the **curves** — a selectivity sweep, a take sweep, a complexity probe — which
answered their question once and do not guard against anything. They stay runnable and stay out of
the default run: 49 cases become 71 with `--explore`.

Classes have been deleted rather than demoted whenever another instrument measured the same thing
with a better estimator, and the journals name the replacement for each. `RewrittenComparison` went
that way, its unique question — our own bytes, read by both readers — being the four `rewritten`
axes of `--ratio-check`. The numbers they produced are not lost: they are in `bench-BASELINE.md`
and in the commits.

## The gates, and their ceilings

* **`--ratio-check`** — twenty-four axes, ours against Rust, **interleaved against one clock** so
  that drift is common to both arms. Nine on the dataset (full scan, full scan upstream-lazy,
  projected, first batch, footer only, read-and-write-back, filtered 1 %, filtered half, scattered
  take); four `rewritten` ones that read a file **our writer produced** beside the reference's — the
  only place our own encoding choices are measured at all; two on key order and one on the exact
  count; six on predicates pushed into a string or a packed column (equality and prefix over `fsst`
  and over `dict`, a band over `runend` and over `bitpacked`); and two on a fifty-column table. Each
  has a ceiling in `RatioCheck.cs`; over it, non-zero exit. 56 s.
  A bare word narrows it to the axes whose name contains it — `-- --ratio-check write`, `--
  --ratio-check rewritten`, `-- --ratio-check string` — which is the difference between checking one
  change and waiting for twenty-four axes.

  **Do not pin tiered compilation for this gate.** `DOTNET_TieredCompilation=0` costs our side
  dynamic profile-guided optimization while the reference, being native, loses nothing: the same
  command that is green unpinned reports **nine axes over their ceiling** with the pin, `full scan`
  among them at 0.417 against a 0.385 ceiling. `bench/ab.sh` pins on purpose and says why — both of
  its sides are the same runtime and the pin removes a JIT difference between them. A ratio against
  native code is the opposite case.
* **`--throughput [family…] [--check]`** — 57 encodings at a million rows, where the fixed
  open-and-walk cost is under a percent instead of most of the measurement. `--check` makes it a
  gate. Its inputs are 468 MB and are not committed: run `bench/gen-throughput.sh` once, and it
  finds them in `~/.cache/vorticity/throughput-1M` by itself (`VORTICITY_THROUGHPUT_CORPUS`
  overrides). `--check` refuses a corpus with no `manifest.json`, one generated at another row
  count, or one whose files do not match their recorded sha256 — fifty-seven ratchets against bytes
  that live outside the repository need to know *which* bytes.

  **`--take`** asks the same fifty-seven files for 64 rows spread evenly over each (one every 15 625),
  against `vxbench_take`, with its own ratchet table — the two axes do not move together, and that
  is the point: a decoder without a `DecodeSelected` override decodes the whole split around each
  taken row. Nine of them have none (PERF-AUDIT-v2.md R17), and the axis prices it: `zstd` 64×,
  `datetimeparts` 60×, `alprd` 95×, against `fsst` 0.22× and `onpair` 0.46× where the override
  exists. `--ratio-check`'s single `scattered take` axis reads 0.25× and says none of this, because
  it is one file whose encodings all have the override.

  **`--write`** reads each file back out into a sink that keeps nothing, against `vxbench_write`,
  which does the same into a `Vec<u8>`: the read is inside the measurement on both sides, so
  subtract the scan axis before reading the quotient as a statement about writers. It has had its
  own ratchet table of 56 references since B14, and it reports an encoding it cannot write rather
  than dying on it — today that is `zstd_nullable` alone, and it is the reference that declines.

  `--quick` (23 s instead of 70) shortens the warm-up and the per-file budget: a direction, not a
  gate. `--recalibrate N` prints a replacement reference table, as it does for `--ratio-check`.

  **A bare family name narrows the report, and does not currently give the gate's ratio**:
  `fsst` reads 1.27 in the full run and 1.63–1.66 on its own, reproducibly, on the same bytes — over
  its ceiling on a healthy tree. Use the narrow form to see a direction; confirm with the full run
  before believing a red. BENCH-AUDIT.md B8.

**A ceiling only ever comes down, and only behind a real improvement.** Raising one to make a run
pass is the one thing this directory forbids outright.

**Both gates decide on an interval, not a point.** Each round times both sides microseconds apart,
so the rounds are paired and a per-round ratio is a sample; the median of those ratios is what is
held, with a 95% bootstrap interval around it. A ratio is `OVER` only when the *whole* interval is
above the ceiling, `STALE` only when all of it is under 0.85 × reference, and `noisy` when the median
is over but the interval straddles — which used to be reported as a failure and was a coin toss.
Rounds keep coming until half the interval is within 5% of the median, and a call shorter than the
timer's noise floor is repeated inside one timed round (the `k` column). The `mde` column is the
smallest change that axis can currently see.

**An interval is within one run, and between-run variance is 2–3× larger.** Measured over twenty
processes per axis (BENCH-AUDIT.md B2.5): a 95 % interval contains the grand median 11–12 times out
of 20 rather than 19. So a ratio can read 1.30 [1.28; 1.31] in eight runs and 1.71 [1.65; 1.77] in
the ninth — narrow and wrong. **Narrow does not mean reproducible**, which is why `--recalibrate`
runs each pass in its own process, and why the replay rule below still stands for a single red.

**When the code outran a ceiling, `--ratio-check` says `STALE`** — more than 15 % under its reference
— because a ratchet that is never lowered defends nothing. `read and write back` sat 43 % under its
own for five commits, which left room for a 75 % regression to pass. Lower them with:

```
dotnet run -c Release PROJ -- --ratio-check --recalibrate 3     # ~90 s, prints a table to paste
```

It runs N passes **in N separate processes** — between-run variance is the larger part and passes
inside one process cannot sample it — takes the max of the per-pass medians, and prints the
`RatioCheck.References` table ready to paste. `--throughput --recalibrate N` does the same for the
per-encoding table. An axis that measures *above* its reference is printed back **unchanged** and
flagged `HELD`: the command lowers ratchets and never raises one. If such an axis is a real
regression, plain `--ratio-check` says `OVER`, and the answer is the code, not the number.

`--rebase` is the one exception and it is deliberately awkward: it lets a reference rise, and only on
an axis whose `k > 1`, because grouping calls into a round changes what is being measured — k calls
in a row are a *warm* path where a single timed call was cache-cold. Use it when the harness changed,
never when the number did, and say which change in the commit message.

**A red gate is not believed on the first run, yet.** Measured run-to-run spread is +12 to +22 % on
four of the nine axes the dataset then had (BENCH-AUDIT.md annexe A.1), and two invocations in four were red with
no byte changed. Replay three times: two reds out of three is a regression, otherwise it is noise —
and either way the observation is data for B2, the statistical gate that is meant to end this
paragraph.

## Where the rest lives

These are engineering journals: French, dated, and kept on the maintainer's machine rather than in
the repository. They are named and not linked, because the name is the reference.

| file | what it holds |
|---|---|
| `bench-BASELINE.md` | the current number on every axis, its machine, its commit. A record, not a gate |
| `bench-PROFILE.md` | the CPU profiling session: what actually costs, as opposed to what should |
| `bench-ALLOCATIONS.md`, `bench-BRANCHING.md`, `bench-STRUCTURE.md` | the three code audits |
| `bench-PERF-AUDIT.md` | v1, the archive: three passes, 41 steps, the negative results |
| `PERF-AUDIT-v2.md` | the open work list |
| `BENCH-AUDIT.md` | this instrument, audited: what every figure above comes from |
