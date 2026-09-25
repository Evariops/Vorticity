# The bench, in one page

Every command below is run from the repository root. `PROJ` stands for
`--project bench/Vorticity.Benchmarks`; add `-c Release` always — a Debug benchmark measures the
JIT's unoptimized output and is worth nothing. Forget it and every mode refuses with exit 2 rather
than printing a table.

```
dotnet run -c Release PROJ -- <arguments>
```

**Every ratio the bench prints reads one way**: the measured side's time, or bytes, over the side
it is compared with — ours over the reference's, the later build's over the earlier's in `ab.sh` —
so under 1.00 the measured side took less.

**Every figure it publishes is on one page**, [docs/guide/benchmarks.md](../docs/guide/benchmarks.md).
`--out docs/guide/benchmarks.md` makes the report, `--ratio-check`, `--throughput --take`,
`--throughput --write`, `--tradeoffs`, `--tradeoffs --advise` and a BenchmarkDotNet run each write
their own sections of it, dated and signed with the machine and the commit, and leave the others;
a run narrowed to some axes, files or columns writes nothing, and a BenchmarkDotNet run writes the
classes it ran. The page's head lists the command behind each section.

## The loop: what you changed, what you run

| you changed | you run | it costs | what it answers |
|---|---|---|---|
| a kernel | `-- fastlanes` (its class) | 1–8 s | did the kernel move, against the ported arm on the same clock |
| the bit-packing unpack | `-- BitPackingBenchmarks` | 12 s | `FastLanes.UnpackBlocks` against a frozen copy of the row-at-a-time kernel and against storing the same output bare, checked value for value first, on 8 blocks whose output stays in the first-level cache and on a scan window of 128: ns/value, GB/s in and out, cycles per value. `--full` adds eight shapes, the corpus file's 977 blocks and buffers half a cache line off |
| the FSST decode | `-- FsstDecodeBenchmarks` | 20 s | `FsstDecodeTable.Decode` against a frozen copy of the eight-codes-a-word kernel and against copying the decoded bytes, checked against the text first, on URLs, UUIDs and log lines the writer compressed, 1 024 rows and a scan window of 131 072 |
| a varbinview's view validation | `-- ViewValidationBenchmarks` | 15 s | `VarBinViewDecoder.ValidateViews` against a frozen copy of the view-at-a-time loop, after checking what it must refuse and accept, on the corpus file's mix of inline and referencing views, all inline, all referencing |
| a dictionary of strings | `-- DictGatherBenchmarks` | 20 s | `RowKernels.Gather` and `GatherMasked` on sixteen-byte views against a frozen copy of the kernels, checked against each other first, on the corpus's nullable codes, nullable values, 64-bit and 8-bit codes |
| a datetimeparts recomposition | `-- RecomposeBenchmarks` | 10 s | `IntegerKernels.Recompose` against a frozen copy of the loop, checked against it first, on the corpus's parts and on the same values in narrow types |
| a listview's range check | `-- ListViewValidationBenchmarks` | 10 s | `ListViewDecoder.ValidateRanges` against a frozen copy of the loop, after checking what it must refuse, on 64-bit and 32-bit offsets and sizes |
| a heap cut into views | `-- ViewBuildBenchmarks` | 20 s | `ViewKernels.BuildFromOffsets` and `BuildFromLengths` against a frozen copy of the kernels, checked view for view first, on the corpus's varbin, FSST, OnPair and mixed shapes |
| anything, want a direction | no argument at all | **3 min** | the seventeen default classes, 65 benchmarks, fast profile |
| a number about to be written down | `-- --full fastlanes` | 1–4 min | the reference profile, on the ONE class concerned |
| a read path | `-- --ratio-check [axis…]` | 56 s, or 5 s for one axis | the twenty-five axes against Rust, interleaved, each held to a ceiling |
| a bitmap kernel | `-- BitmapKernel` | 15 s | each of `Classify`, `CountSet`, `CopyRange`, `PackBytes` against the loop it replaced |
| a gather, a tile, a dictionary | `-- RowKernel` | 9 s | `Gather`, `GatherMasked`, `Tile` against the per-row type switch each replaced |
| a string heap cut into views | `-- ViewKernel` | 9 s | `SumLengths`, `BuildFromLengths`, `RequireAscending` against the per-row loops |
| the OnPair token concatenation | `-- OnPairKernel` | 6 s | the bulk of an OnPair scan, against the per-code switch it replaced |
| whether your change moved anything | `bench/compare.sh --record before <filter>`, then `--record after`, then `bench/compare.sh before after` | 2x the class | Mann-Whitney per case: Faster / Same / Slower |
| a decoder | `-- --throughput <family>` | ~30 s | ns/value per encoding at a million rows, against Rust. **Reports, never gates**: a run of one file is +32% on our side, the JIT not finished with it |
| the writer, or a decoder | `-- --throughput --check` | 54 s | the same, as a gate over all 57 files |
| a selective decode (`DecodeSelected`) | `-- --throughput --take --check` | 90 s | 64 rows spread over each of the 57 files, against Rust |
| a lane, or the degree of parallelism | `-- LanesBench` (`--full` walks 1, 2, 4, 8) | 15 s | ours at n lanes against the reference on a Tokio runtime of n workers, threads pinned both sides |
| the compressor's decision | `-- CompressorBench` | 12 s | `Choose` and one arm per candidate; `--full` adds the utf8 and f64 columns |
| the writer, per encoding | `-- --throughput --write --check` | ~2 min | each file read back out to a discarding sink, against Rust, both writers given decoded rows. **A gate** over 56 files; three are noisier than the ×1.15 margin (`onpair`, `sparse`, `constant`): re-run before believing a red. Only `parquet_variant` produces no ratio, and that is **the reference** refusing to write it |
| every file we write, read by Rust | `bench/crosscheck.sh` | 80 s | 854 files compared scalar by scalar, 2 538 751 rows; needs cargo. `gate.sh --crosscheck` folds it in |
| **anything, before you push** | `bench/gate.sh` | 68 s | the nine ratchets, `--ffi-check`, `--ratio-check`; exit 1 if one is red. `--throughput` adds the full axis (92 s) |
| a change too big for a ported arm | `bench/ab.sh <commit> [--after <commit>] <file> [scenario…]` | 7 s | two builds of the library in one process, interleaved, ratio per round |
| the FFI harness, or before trusting any ratio | `-- --ffi-check` | < 1 s | both readers return the same rows on the same files |
| **what a user would see**, for the published page | `-- --report` | ~2 min | eight high-level scenarios at 2^20 rows and ten times that, **each side in its own process**, **on one core and on all of them**, on the file our writer makes and on the one the reference's writer makes from the same rows: the action's time with its spread, its throughput over the plain size of the rows returned (`PlainSize.cs`), what our side allocates for a call once warm (the median of six calls after two), peak resident memory, rows rendered, and each file's chunks and encodings per column. Then **every file of the per-encoding corpus**, scanned by both sides twelve times in each of three processes a side, the median of the last ten: the decoders once warm, which `--no-kernels` leaves out. Our side runs as the **Native AOT runner** built for the machine's instruction set (`dotnet publish -c Release bench/Vorticity.Benchmarks.Runner`), whose time the ratio divides by the reference's, as every ratio of the bench does, and on one core also as this framework-dependent host on the JIT. The reference is the `vxbench` **binary** (`cargo build --release` in `tools/vxbench-rs`), built as upstream builds its benchmarks: mimalloc, `target-cpu=native`, one codegen unit, no LTO. On all cores, `--threads all` gives our scans a lane per processor and the reference a Tokio worker per processor. Build both first. `--markdown --out docs/guide/benchmarks.md` writes its two sections of the benchmark page. The fixtures are written again on every run |
| a hot path you want to profile | `-- --profile <scenario> [seconds]` | as asked | a bare loop for `dotnet-trace`, no harness in the profile |
| where a report scenario spends its cycles, line by line | `bench/profile.sh cycles <scenario> <file> <rows>` (macOS, Xcode) | 15 s | the **Native AOT runner** sampled every 25–30 µs by Instruments' CPU Profiler, weighed in cycles: self time per function and **per source line, inlined code included**, and every sample charged to the innermost line of this repository. Run from the command line, no Instruments window |
| what a report scenario allocates, and where | `bench/profile.sh allocations <scenario> <file> <rows>` (macOS) | 15 s–5 min | **every** allocation of three rounds of the Native AOT runner, stopped on under lldb: type, size, stack to the line, managed and native (the C allocator and `mmap`). Not sampled — the managed totals per round equal the runner's own `allocated_bytes` |
| every allocation site over many scenarios, and whether it grows with the rows | `python3 bench/profile/inventory.py . <out.md> <allocation output directories…>` | seconds | one table of sites (library line and its source, allocating line, type), cold and warm counts per trace; a scenario traced at two sizes (`scan-1048576`, `scan-10485760`) says which sites are paid per batch or chunk rather than per scan |
| every branch a scenario takes | `bench/profile.sh trace <scenario> <file> <rows>` | 30 s | Processor Trace, for Instruments to open. An M4 or later, and Processor Trace allowed in System Settings → Privacy & Security → Developer Tools |
| who ran, and on what, when a scenario has lanes | `bench/profile.sh cycles <scenario> <file> <rows> --threads all` | 20 s | the cycles above, on as many lanes as the report's all-core rows, plus `threads.md` from `bench/profile/threads.py`: each thread's share, how many threads ran in each millisecond, and the hottest functions of the caller's thread and of the pool's workers. The profiler records fewer of each thread's cycles as more of them run: take shares from it, and the process's processor time from the runner's `cpu_ms` |
| a ratchet | `dotnet test Vorticity.slnx -c Release` | ~1 min | the suite plus the nine allocation, count and budget ratchets |
| a change, and only the tests it can affect | `dotnet run -c Release --project tools/testimpact -- run` (after `-- map` once) | seconds | the test classes that ran a method the working tree changed against `HEAD` (`--base REF` for another), from a map of what each class runs: every class in a process of its own with inlining off, the JIT's list of compiled methods recorded (35 s). A constant, a build file or the source generator runs everything; a new class runs; a comment or a `using` runs nothing |

**A fast-profile figure is a direction, not a number.** Anything under about 5 % on a kernel, and
every figure that gets recorded, is confirmed with `--full` on the one class concerned. Why, and
what the fast profile costs in fidelity: `BenchmarkConfig.cs`.

**Never run the whole BDN suite to decide one change.** Run the class, before and after, on the same
build. The gates are the other instrument: they are *commands*, deliberately not `dotnet test`
cases, so CI calls them and a red one never silently stops the suite from running.

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
# selective scan costs and how much per-chunk fixed cost the file pays. `off` disables the
# 1 MiB coalescing, which leaves chunks of exactly one row block.
dotnet run -c Release --project bench/Vorticity.Benchmarks -- \
    --rewrite in.vortex out.vortex --row-block 8192 --data-block-bytes off

# Then measure any directory of files, both readers, one clock.
VORTICITY_THROUGHPUT_CORPUS=/tmp/pair \
    dotnet run -c Release --project bench/Vorticity.Benchmarks -- --throughput
```

That pair locates a slow read of our own rewrite: the same file rewritten with and without zstd,
read by both readers, says whether the frames are the cost. `vxdump --encodings` cannot say which
column holds them — the reference interns all 37 encodings of the registry whatever the file uses,
so only OUR dictionary is informative. What does answer it is `vxdump --layout`, whose `encoding=`
column names the array encoding of every terminal node:

```sh
dotnet run -c Release --project tools/vxdump -- /tmp/pair/ours.vortex --layout |
    grep -o 'encoding=.*' | sort | uniq -c | sort -rn
```

The `vortex.zstd` nodes it counts, and the columns they sit on, put the slowdown on a column rather
than leave it to be inferred from a target edition.

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
asked for. Branch mispredictions and cache misses need a Windows machine.

**No argument means all of them**, not a prompt: without one, BenchmarkDotNet asks the console which
class to run, which makes the default run do nothing under a script or in CI. `Program.cs` supplies
`--filter *` when nothing else has said what to run.

## The classes

Twenty-three, and `-- --list flat` is what answers this question for real: seventeen in the default run,
six more behind `--explore`.

| class | in the default run |
|---|---|
| `FastLanesKernelBenchmarks` | yes — 17 bits; `--full` adds 10 and 33 |
| `BitPackingBenchmarks` | yes — 32-bit values at 10 bits, 8 and 128 blocks; `--full` adds eight shapes, 977 blocks and misaligned buffers |
| `FsstDecodeBenchmarks` | yes — URLs, UUIDs and log lines, 1 024 and 131 072 rows |
| `ViewValidationBenchmarks` | yes — mixed, inline and referencing views, 1 024 and 131 072 rows |
| `DictGatherBenchmarks` | yes — nullable codes, nullable values, `u64` and `u8` codes, 1 024 and 131 072 rows |
| `RecomposeBenchmarks` | yes — the corpus's parts and narrow ones, 1 024 and 131 072 rows |
| `ListViewValidationBenchmarks` | yes — `u64`, `u32` and `i32` offsets and sizes, 1 024 and 131 072 rows |
| `ViewBuildBenchmarks` | yes — varbin, FSST, OnPair and mixed values, 1 024 and 131 072 rows |
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
the default run: 65 benchmarks become 87 with `--explore`.

Classes have been deleted rather than demoted whenever another instrument measured the same thing
with a better estimator, and the journals name the replacement for each. `RewrittenComparison` went
that way, its unique question — our own bytes, read by both readers — being the four `rewritten`
axes of `--ratio-check`. The numbers they produced are not lost: they are in the maintainers'
journals and in the commits.

## The gates, and their ceilings

* **`--ratio-check`** — twenty-five axes, ours against Rust, **interleaved against one clock** so
  that drift is common to both arms. Ten on the dataset (full scan, full scan upstream-lazy,
  projected, projected upstream-lazy, first batch, footer only, read-and-write-back, filtered 1 %,
  filtered half, scattered
  take); four `rewritten` ones that read a file **our writer produced** beside the reference's — the
  only place our own encoding choices are measured at all; two on key order and one on the exact
  count; six on predicates pushed into a string or a packed column (equality and prefix over `fsst`
  and over `dict`, a band over `runend` and over `bitpacked`); and two on a fifty-column table. Each
  has a ceiling in `RatioCheck.cs`; over it, non-zero exit. 56 s.
  A bare word narrows it to the axes whose name contains it — `-- --ratio-check write`, `--
  --ratio-check rewritten`, `-- --ratio-check string` — which is the difference between checking one
  change and waiting for twenty-five axes.

  **Do not pin tiered compilation for this gate.** `DOTNET_TieredCompilation=0` costs our side
  dynamic profile-guided optimization while the reference, being native, loses nothing: the same
  command that is green unpinned reports **several axes over their ceiling** with the pin, `full
  scan` among them. `bench/ab.sh` pins on purpose and says why — both of its sides are the same
  runtime and the pin removes a JIT difference between them. A ratio against native code is the
  opposite case.
* **`--throughput [family…] [--check]`** — 57 encodings at a million rows, where the fixed
  open-and-walk cost is under a percent instead of most of the measurement. `--check` makes it a
  gate. Its inputs are 468 MB and are not committed: run `bench/gen-throughput.sh` once, and it
  finds them in `~/.cache/vorticity/throughput-1M` by itself (`VORTICITY_THROUGHPUT_CORPUS`
  overrides). `--check` refuses a corpus with no `manifest.json`, one generated at another row
  count, or one whose files do not match their recorded sha256 — fifty-seven ratchets against bytes
  that live outside the repository need to know *which* bytes.

  **`--take`** asks the same fifty-seven files for 64 rows spread evenly over each (one every 15 625),
  against `vxbench_take`, with its own ratchet table — the two axes do not move together, and that
  is the point: a decoder without a `DecodeSelected` override decodes the whole chunk a taken row
  falls in, once for the take, where one with it decodes only the taken rows: `zstd` inflates only
  the frames that hold the taken rows, and the benchmark page's table says what each encoding makes
  of it. `--ratio-check`'s single `scattered take` axis says none of this, because it is one file
  whose encodings all have the override.

  **`--write`** reads each file back out into a sink that keeps nothing, against `vxbench_write`,
  which gives the reference's writer the rows decoded, as our reader gives ours, and writes into a
  `Vec<u8>`: the read is inside the measurement on both sides, so subtract the scan axis before
  reading the quotient as a statement about writers. It has its own ratchet table of 56
  references, and it reports an encoding it cannot write rather than dying on it — today that is
  `parquet_variant` alone, and it is the reference that declines: its variant, decoded, keeps a
  lazy slice its writer cannot serialize.

  `--quick` (23 s instead of 70) shortens the warm-up and the per-file budget: a direction, not a
  gate. `--recalibrate N` prints a replacement reference table, as it does for `--ratio-check`.

  **A bare family name narrows the report, and does not give the gate's ratio**: a file measured
  on its own reads higher than in the full run, on the same bytes, the JIT not finished with it.
  Use the narrow form to see a direction; confirm with the full run before believing a red.

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
processes per axis, a 95 % interval contains the grand median 11–12 times out
of 20 rather than 19. So a ratio can read 1.30 [1.28; 1.31] in eight runs and 1.71 [1.65; 1.77] in
the ninth — narrow and wrong. **Narrow does not mean reproducible**, which is why `--recalibrate`
runs each pass in its own process, and why the replay rule below still stands for a single red.

**When the code outran a ceiling, `--ratio-check` says `STALE`** — more than 15 % under its reference
— because a ratchet that is never lowered defends nothing: a reference left far above its axis leaves
room for a regression of that size to pass. Lower them with:

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
an axis whose `k` moved, because grouping calls into a round changes what is being measured — k calls
in a row are a *warm* path where a single timed call was cache-cold — or under a reference binary
other than the one the table records. Use it when the harness changed, never when the number did,
and say which change in the commit message.

**A new reference binary is carried, not recalibrated.** Both gates record the fingerprint of the
`vxbench` build their tables were set under (`CalibratedShim`) and refuse to gate under another: a
ratio through a different denominator is a number about the rebuild. After rebuilding
`tools/vxbench-rs` — a new `vortex` pin, a new toolchain, a changed entry point — keep the previous
build and carry the tables over:

```
dotnet run -c Release PROJ -- --ratio-check --recalibrate 3 --rebase-from <previous libvxbench.dylib>
dotnet run -c Release PROJ -- --throughput [--take|--write] --recalibrate 3 --rebase-from <previous libvxbench.dylib>
```

Each pass is a pair of processes, one under each binary, and each reference moves by what the new
binary moved its ratio. Our side is the same build under both, so an axis the table held over its
ceiling, or stale under it, is still over or stale after the carry: a plain `--rebase` to the new
measurement would have absorbed it. Paste the tables and the `CalibratedShim` line they print
together, the three throughput tables before the fingerprint.

**A red gate is not believed on the first run.** Run-to-run spread reaches a fifth on some axes,
and a run can come out red with no byte changed. Replay three times: two reds out of three is a
regression, otherwise it is noise.

## Where the rest lives

The figures are on [the benchmark page](../docs/guide/benchmarks.md). What follows are engineering
journals: French, dated, and kept outside the repository. They are named and not linked, because
the name is the reference.

| file | what it holds |
|---|---|
| `bench-PROFILE.md` | the CPU profiling session: what actually costs, as opposed to what should |
| `bench-ALLOCATIONS.md`, `bench-BRANCHING.md`, `bench-STRUCTURE.md` | the three code audits |
| `bench-PERF-AUDIT.md` | v1, the archive: three passes, 41 steps, the negative results |
| `PERF-AUDIT-v2.md` | the open work list |
| `BENCH-AUDIT.md` | this instrument, audited: what every figure above comes from |
