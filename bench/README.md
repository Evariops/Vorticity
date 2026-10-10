# The bench, in one page

Every command below is run from the repository root. `PROJ` stands for
`--project bench/Vorticity.Benchmarks`. Always add `-c Release`, because a Debug benchmark measures
the JIT's unoptimized output and is worth nothing. If you forget it, every mode refuses with exit 2
rather than printing a table.

```
dotnet run -c Release PROJ -- <arguments>
```

The bench publishes speedups and gates on ratios. Every table of the benchmark page, and `--report`
and BenchmarkDotNet on the console, give the other side's time over the measured side's (the
reference's over ours, a kernel's baseline's over its own), so above 1.00x the measured side took
less. The gates and `ab.sh` hold and print the inverse, the measured side's time or bytes over the
side it is compared with (ours over the reference's, the later build's over the earlier one's), so
under 1.00 the measured side took less, and a ceiling bounds it from above.

Every figure it publishes is on one page, [docs/guide/benchmarks.md](../docs/guide/benchmarks.md).
With `--out docs/guide/benchmarks.md`, the report, `--ratio-check`, `--throughput --take`,
`--throughput --write`, `--tradeoffs`, `--tradeoffs --advise` and a BenchmarkDotNet run each write
their own sections of it, dated and signed with the machine and the commit, and leave the others
alone. A run narrowed to some axes, files or columns writes nothing, and a BenchmarkDotNet run writes
the classes it ran. The head of the page lists the command behind each section. On an x64 machine
the same runs take `--out docs/guide/benchmarks-x64.md`, which holds the same sections measured
there. Publish the Native AOT runner first, since the report times the one it finds.

## The loop: what you changed, what you run

| you changed | you run | it costs | what it answers |
|---|---|---|---|
| a kernel | `-- fastlanes` (its class) | 1–8 s | whether the kernel moved, against the ported arm on the same clock |
| anything, and you want a direction | no argument at all | 1 min | the four default classes, 17 benchmarks, fast profile |
| a number about to be written down | `-- --full fastlanes` | 1–4 min | the reference profile, on the one class concerned |
| a read path | `-- --ratio-check [axis…]` | 56 s, or 5 s for one axis | the twenty-six axes against Rust, interleaved, each held to a ceiling |
| whether your change moved anything | `bench/compare.sh --record before <filter>`, then `--record after`, then `bench/compare.sh before after` | 2x the class | Mann-Whitney per case: Faster, Same or Slower |
| a decoder | `-- --throughput <family>` | ~30 s | ns per value per encoding at a million rows, against Rust. It reports and never gates, since a run of one file reads +32 % on our side, the JIT not being finished with it |
| the writer, or a decoder | `-- --throughput --check` | 54 s | the same, as a gate over the whole per-encoding corpus |
| a selective decode (`DecodeSelected`) | `-- --throughput --take --check` | 90 s | 64 rows spread over each file of the corpus, against Rust |
| a lane, or the degree of parallelism | `-- LanesBench` (`--full` walks 1, 2, 4, 8) | 15 s | ours at n lanes against the reference on a Tokio runtime of n workers, with threads pinned on both sides |
| the compressor's decision | `-- CompressorBench` | 12 s | `Choose` and one arm per candidate, and `--full` adds the utf8 and f64 columns |
| sealing, or its frame size | `-- --explore SealedScan`, `-- --explore SealedScratch` | 80 s, 20 s | a sealed file's scan, filter, open and point read against the plain file mapped and read positionally, at frames of 4 KiB to 1 MiB; and a spill's scratch written and read back, sealed and plain |
| the writer, per encoding | `-- --throughput --write --check` | ~2 min | each file read back out to a discarding sink on both sides, against Rust, both writers given the rows as our reader delivers them, and the page prints each writer's bytes next to its time. It is a gate. Three files are noisier than the ×1.15 margin (`onpair`, `sparse`, `constant`), so run again before believing a red. Only `parquet_variant` produces no ratio, because the reference refuses to write it |
| every file we write, read by Rust | `bench/crosscheck.sh` | 80 s | 854 files compared scalar by scalar, 2 538 751 rows. Needs cargo, and `gate.sh --crosscheck` folds it in |
| anything, before you push | `bench/gate.sh` | 68 s | the ten ratchets, `--ffi-check` and `--ratio-check`, exiting 1 if one is red. `--throughput` adds the full axis (92 s) |
| a change too big for a ported arm | `bench/ab.sh <commit> [--after <commit>] <file> [scenario…]` | 7 s | two builds of the library in one process, interleaved, with a ratio per round |
| a dataset's write path, its compaction policy, anything a long-lived dataset pays for | `dotnet run -c Release --project bench/Vorticity.Benchmarks.Churn -- --rows 1000000 --ops 2000` | 45 s, or 3 min for ten million rows and ten thousand commits | a dataset loaded, then thousands of small commits (appends, updates and deletes of ten rows, with `--mix` and `--batch` to change them), with the caller's compaction and vacuum in between. Per window of commits it reports each kind's latency, requests, bytes written and allocated, and the compaction it made due. Per checkpoint it runs eleven reads, from a point lookup and seeks both ways to a full scan, with their requests, and reports the heap. A cost that grows with the commits shows as a slope. `--compact none` shows what a caller who never compacts pays, `--keys tail` gives a time series, `--marks off` a dataset that rewrites every object a delete or an update touches, `--purge off` one whose compaction leaves marks to the deletes, and `--probe-rounds 5` keeps each probe's fastest round on a machine that runs other work |
| the FFI harness, or before trusting any ratio | `-- --ffi-check` | < 1 s | both readers return the same rows on the same files |
| what a user would see, for the published page | `-- --report` | ~2 min | see [the report](#the-report) |
| a hot path you want to profile | `-- --profile <scenario> [seconds]` | as asked | a bare loop for `dotnet-trace`, with no harness in the profile |
| where a report scenario spends its cycles, line by line | `bench/profile.sh cycles <scenario> <file> <rows>` (macOS, Xcode) | 15 s | the Native AOT runner sampled every 25–30 µs by Instruments' CPU Profiler, weighed in cycles: self time per function and per source line, inlined code included, every sample charged to the innermost line of this repository. It runs from the command line, with no Instruments window |
| what a report scenario allocates, and where | `bench/profile.sh allocations <scenario> <file> <rows>` (macOS) | 15 s–5 min | every allocation of three rounds of the Native AOT runner, stopped on under lldb: type, size, stack down to the line, managed and native (the C allocator and `mmap`). It is not sampled, and the managed totals per round equal the runner's own `allocated_bytes` |
| every allocation site over many scenarios, and whether it grows with the rows | `python3 bench/profile/inventory.py . <out.md> <allocation output directories…>` | seconds | one table of sites (library line and its source, allocating line, type), with cold and warm counts per trace. A scenario traced at two sizes (`scan-1048576`, `scan-10485760`) shows which sites are paid per batch or chunk rather than per scan |
| every branch a scenario takes | `bench/profile.sh trace <scenario> <file> <rows>` | 30 s | Processor Trace, for Instruments to open. It needs an M4 or later, with Processor Trace allowed in System Settings → Privacy & Security → Developer Tools |
| who ran, and on what, when a scenario has lanes | `bench/profile.sh cycles <scenario> <file> <rows> --threads all` | 20 s | the cycles above, on as many lanes as the report's all-core rows, plus `threads.md` from `bench/profile/threads.py`: each thread's share, how many threads ran in each millisecond, and the hottest functions of the caller's thread and of the pool's workers. The profiler records fewer of each thread's cycles as more threads run, so take shares from it, and the process's processor time from the runner's `cpu_ms` |
| where a group by spends its cycles | `bench/profile.sh cycles group-total-k6+core ~/.cache/vorticity/queries/spread-random-4000000.vortex 0 --threads 14` | 30 s | the cycles above for a group by of the high-cardinality bench (`group-<aggregates>-<key>`, with the plan's switches after `+`, see `GroupScenarios.cs` in the runner), over the spread files `vortex-queries --matrix small` writes. The runner prints the engine's counts, the core's included. The bench compiles as it goes, and Instruments names no frame of a method compiled at run time |
| a change to the engine, against the last change to the same part | `bench/runners.sh keep <commit>` once, then `bench/runners.sh alt <kept> HEAD --threads 1,14 [--warm] group-<aggregates>-<key>[@file] …` | 1 min for three scenarios at one lane | see [runners](#comparing-two-runners) |
| our group bys against DuckDB's | `bench/duckdb.sh [--set hc\|db\|all] [--only <rows>] [--rows N] [--threads 1,14] [--bases both\|warm\|cold]`, then `bench/duckdb.sh --publish <session> <session>` | 15 to 25 min per session for both sets at 1 and 14 threads, files included, or 30 s for `--only db-q4,db-q10 --threads 14` | see [the DuckDB page](../docs/guide/benchmarks-duckdb.md), which describes the method. Everything is kept under `bench/.runs/duckdb-<date>/`, and `--publish` builds the page's tables from two sessions taken at different hours, a row being published when they agree within 10 %. `hc` is the high-cardinality table (keys of 10³ to 10⁷ values) and `db` db-benchmark's group by. `--only db-q4,total-k7` keeps the rows named by the runner's scenario without `group-`. It needs `duckdb` and its vortex extension, and informs without gating anything |
| the runner, its file kept mapped, and what a round cost | `VORTICITY_RUNNER_WARM=1 Vorticity.Benchmarks.Runner --scenario … --repeat N` | | by default each round maps its file anew, as the reference maps it at each call. `VORTICITY_RUNNER_WARM=1` keeps the session's default of 64 mappings, with the file already in memory, which is what a comparison against a table loaded beforehand needs. Each round line carries `cpu_us`, `instructions` and `cycles`, the process's on every thread, read with `proc_pid_rusage` as a user (macOS, zero elsewhere), so a time that moves while the cycles do not was moved by the machine |
| a ratchet | `dotnet test Vorticity.slnx -c Release` | ~1 min | the suite plus the ten allocation, count, budget and work ratchets |
| a change, and only the tests it can affect | `dotnet run -c Release --project tools/testimpact -- run` (after `-- map` once) | seconds | the test classes that ran a method the working tree changed against `HEAD` (`--base REF` for another), from a map of what each class runs, built by running every class in a process of its own with inlining off and recording the JIT's list of compiled methods (35 s). A constant, a build file or the source generator runs everything, a new class runs, and a comment or a `using` runs nothing |

### The report

`-- --report` runs eight high-level scenarios at 2^20 rows and ten times that, each side in its own
process, on one core and on all of them, on the file our writer makes and on the one the reference's
writer makes from the same rows. It reports the action's time with its spread, its throughput over
the plain size of the rows returned (`PlainSize.cs`), what our side allocates per call once warm (the
median of six calls after two), the peak resident memory, the rows rendered, and each file's chunks
and encodings per column. Then it scans every file of the per-encoding corpus with both sides, twelve
times in each of three processes per side, and keeps the median of the last ten: the decoders once
warm, which `--no-kernels` leaves out.

Our side runs as the Native AOT runner built for the machine's instruction set (`dotnet publish -c
Release bench/Vorticity.Benchmarks.Runner`), whose time is the divisor in the speedup, as on every
table of the page, and on one core also as this framework-dependent host on the JIT. The reference is
the `vxbench` binary (`cargo build --release` in `tools/vxbench-rs`), built the way upstream builds
its benchmarks: mimalloc, `target-cpu=native`, one codegen unit, no LTO. On all cores, `--threads all`
gives our scans a lane per processor and the reference a Tokio worker per processor. Build both
first. `--markdown --out docs/guide/benchmarks.md` writes its two sections of the benchmark page, and
the fixtures are written again on every run.

### Comparing two runners

`bench/runners.sh keep <commit>` publishes the Native AOT runner of a commit once and keeps it under
`~/.cache/vorticity/runners/<sha>`. Without a commit, `keep` publishes the working tree's tracked
changes. `bench/runners.sh alt` then runs two kept runners in turns, one process each, in pairs whose
order alternates: three pairs, then more while the 95 % interval of the median ratio is wider than
±3 % (eight at most), taking the median of the rounds after the first. That gives the ratio of the two
binaries, which a loaded machine moves less than it moves a number of milliseconds measured on
another day.

Two canary runs before and after each scenario, compared with the session's first, catch what other
processes did meanwhile, and a scenario framed by a canary 5 % off, or during which swap grew, runs
again once. The table prints the interval, the pairs, the ratio of the fastest rounds (a gap of 10 %
from the median marks the scenario noisy), the ratio of the rounds' cycles, the load and the
processes that were busy. `--warm` keeps each side's file mapped (`VORTICITY_RUNNER_WARM=1`). Every
run is kept with its time of day in `bench/.ab/runners-<date>/runs.tsv`.

A fast-profile figure is a direction, not a number. Anything under about 5 % on a kernel, and every
figure that gets recorded, is confirmed with `--full` on the one class concerned. `BenchmarkConfig.cs`
explains why, and what the fast profile costs in fidelity.

Never run the whole BenchmarkDotNet suite to decide one change. Run the class, before and after, on
the same build. The gates are the other instrument: they are commands, deliberately not `dotnet test`
cases, so CI calls them and a red one never silently stops the suite from running.

## Selecting

Three mechanisms, finest first:

| form | means |
|---|---|
| `-- fsst` | a bare word becomes `--filter '*fsst*'`, since the shell would eat the stars |
| `-- --filter '*Fsst*.Library'` | BenchmarkDotNet's own glob, over `Class.Method` |
| `-- --anyCategories kernel` | by category: every class is `kernel`, `path` or `explore` |
| `-- --list flat` | what exists under the current selection |

`-- --help` lists every mode, its flags and what each costs. `--full`, `--explore` and `--inprocess`
are ours and are consumed before BenchmarkDotNet sees the rest, so `-- --full fastlanes` works as
written. Everything else is forwarded.

`--full` runs out of process, and the fast profile does not. BenchmarkDotNet 0.16.0-preview.1, the
first version that can build a `net11.0` host, gives the reference profile process isolation per
case, GC and runtime jobs, and `--disasm`. It costs the host's build: `--full` on a two-case class
takes 2 min 10 s rather than 7 s, which is why the fast profile stays in this process.
`--full --inprocess` is the escape hatch.

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

That pair locates a slow read of our own rewrite: the same file rewritten with and without zstd, read
by both readers, says whether the frames are the cost. `vxdump --encodings` cannot say which column
holds them, because the reference interns all 37 encodings of the registry whatever the file uses, so
only our own dictionary is informative. What does answer it is `vxdump --layout`, whose `encoding=`
column names the array encoding of every terminal node:

```sh
dotnet run -c Release --project tools/vxdump -- /tmp/pair/ours.vortex --layout |
    grep -o 'encoding=.*' | sort | uniq -c | sort -rn
```

The `vortex.zstd` nodes it counts, and the columns they sit on, put the slowdown on a column rather
than leaving it to be inferred from a target edition.

## Reading the assembly a kernel actually got

There are two routes, and they answer slightly different questions.

```sh
# The whole class, through BenchmarkDotNet. Needs --full, because the disassembler needs the
# out-of-process toolchain. Writes <Class>-asm.md under the artifacts directory.
dotnet run -c Release --project bench/Vorticity.Benchmarks -- \
    --full --filter '*FastLanesKernelBenchmarks.Vector64*' --disasm

# One named method, no harness, no benchmark. Seconds, and it works in any run of anything.
DOTNET_TieredCompilation=0 \
DOTNET_JitDisasm='*UnpackBlocks*' \
DOTNET_JitStdOutFile=/tmp/jit.asm \
    dotnet run -c Release --project bench/Vorticity.Benchmarks -- FastLanesKernel
```

`DOTNET_TieredCompilation=0` is required if you want the code that runs in steady state. With tiering
on, the listing's header says `Tier0-FullOpts ... optimized using Synthesized PGO`, which is the first
full-opts compilation and not necessarily the last. With it off, the header says `FullOpts` and there
is one listing per generic instantiation.

Hardware counters do not work here. `[HardwareCounters]` needs `BenchmarkDotNet.Diagnostics.Windows`
and ETW. On macOS the run prints `Unable to resolve IHardwareCountersDiagnoser diagnoser using dynamic
assembly loading` and then completes without counters and without failing, so a table that looks
normal is simply missing the columns you asked for. Branch mispredictions and cache misses need a
Windows machine.

No argument means all of them, not a prompt. Without one, BenchmarkDotNet asks the console which
class to run, which would make the default run do nothing under a script or in CI, so `Program.cs`
supplies `--filter *` when nothing else has said what to run.

## The classes

There are ten, and `-- --list flat` is what answers this question for real: four in the default run,
and six more behind `--explore`.

| class | in the default run |
|---|---|
| `FastLanesKernelBenchmarks` | yes, at 17 bits, and `--full` adds 10 and 33 |
| `RowEncodingBenchmarks` | yes |
| `CompressorBenchmarks` | yes |
| `LanesBenchmarks` | yes |
| `RandomAccessBenchmarks` | no, `--explore` |
| `FilterSelectivityBenchmarks` | no, `--explore` |
| `ConstantFormBenchmarks` | no, `--explore` |
| `VarBinFormBenchmarks` | no, `--explore` |
| `ComplexityProbes` | no, `--explore` |
| `MergeProbes` | no, `--explore` |

`explore` holds the curves (a selectivity sweep, a take sweep, a complexity probe), which answered
their question once and do not guard against anything. They stay runnable but out of the default run,
and the 17 benchmarks become 39 with `--explore`.

A class is deleted rather than demoted when another instrument measures the same thing with a better
estimator. Our own bytes read by both readers, for instance, are the four `rewritten` axes of
`--ratio-check`.

## The gates, and their ceilings

### `--ratio-check`

Twenty-six axes compare ours against Rust, interleaved against one clock so that drift is common to
both arms. Ten run on the dataset (full scan, full scan upstream-lazy, projected, projected
upstream-lazy, first batch, footer only, read-and-write-back, filtered 1 %, filtered half, scattered
take). Four `rewritten` axes read a file our writer produced next to the reference's, the only place
our own encoding choices are measured at all. Three are on key order, one of them held to a take of
rows whose positions the reference is given, and one is the exact count. Six are predicates pushed
into a string or a packed column (equality and prefix over `fsst` and over `dict`, a band over
`runend` and over `bitpacked`). The last two read generated tables, one of a million rows and six
columns and one of fifty columns. Each has a ceiling in `RatioCheck.cs`, and going over it gives a
non-zero exit. It takes 56 s.

A bare word narrows it to the axes whose name contains it (`-- --ratio-check write`,
`-- --ratio-check rewritten`, `-- --ratio-check string`), which is the difference between checking
one change and waiting for twenty-six axes.

Do not pin tiered compilation for this gate. `DOTNET_TieredCompilation=0` costs our side dynamic
profile-guided optimization, while the reference, being native, loses nothing, so the same command
that is green unpinned reports several axes over their ceiling with the pin, `full scan` among them.
`bench/ab.sh` pins on purpose and says why: both of its sides are the same runtime, and the pin
removes a JIT difference between them. A ratio against native code is the opposite case.

### `--throughput [family…] [--check]`

This reads the per-encoding corpus at a million rows per file, where the fixed open-and-walk cost is
under a percent instead of most of the measurement. The generator writes 61 files today, and a corpus
generated before the four `pco` shapes were added holds 57. `--check` makes it a gate. Its inputs are
about half a gigabyte and are not committed: run `bench/gen-throughput.sh` once, and the bench finds
them in `~/.cache/vorticity/throughput-1M` by itself (`VORTICITY_THROUGHPUT_CORPUS` overrides it).
`--check` refuses a corpus with no `manifest.json`, one generated at another row count, or one whose
files do not match their recorded sha256, since ratchets against bytes that live outside the
repository need to know which bytes.

`--take` asks the same files for 64 rows spread evenly over each (one every 15 625), against
`vxbench_take`, with its own ratchet table. The two axes do not move together, and that is the point:
a decoder without a `DecodeSelected` override decodes the whole chunk a taken row falls in, once for
the take, while one with it only decodes the taken rows, and `zstd` only inflates the frames that hold
them. The benchmark page's table says what each encoding makes of it. `--ratio-check`'s single
`scattered take` axis says none of this, because it reads one file whose encodings all have the
override.

`--write` reads each file back out into a sink that keeps nothing, against `vxbench_write`, which
gives the reference's writer the rows as our reader gives ours (decoded, with a constant kept as one
value) and hands its bytes to a sink that keeps nothing too. The read is inside the measurement on
both sides, so subtract the scan axis before reading the quotient as a statement about writers. With
`--out`, the page prints each writer's bytes next to its time, because a writer can be fast by
compressing less. It has its own ratchet table, and it reports an encoding it cannot write rather
than dying on it. That only happens with `parquet_variant`, and it is the reference that declines,
because its variant, once decoded, keeps a lazy slice its writer cannot serialize.

`--quick` (23 s instead of 70) shortens the warm-up and the per-file budget, which gives a direction,
not a gate. `--recalibrate N` prints a replacement reference table, as it does for `--ratio-check`.

A bare family name narrows the report but does not give the gate's ratio, because a file measured on
its own reads higher than in the full run, on the same bytes, the JIT not being finished with it. Use
the narrow form to see a direction, and confirm with the full run before believing a red.

### Ceilings

A ceiling only ever comes down, and only behind a real improvement. Raising one to make a run pass is
the one thing this directory forbids outright.

Both gates decide on an interval, not a point. Each round times both sides microseconds apart, so
the rounds are paired and a per-round ratio is a sample. The median of those ratios is what is held,
with a 95 % bootstrap interval around it. A ratio is `OVER` only when the whole interval is above the
ceiling, `STALE` only when all of it is under 0.85 × reference, and `noisy` when the median is over
but the interval straddles the ceiling, which a single run cannot decide. Rounds keep coming until
half the interval is within 5 % of the median, and a call shorter than the timer's noise floor is
repeated inside one timed round (the `k` column). The `mde` column is the smallest change that axis
can currently detect.

An interval is computed within one run, and the variance between runs is 2–3× larger. Measured over
twenty processes per axis, a 95 % interval contains the grand median 11–12 times out of 20 rather than
19. So a ratio can read 1.30 [1.28; 1.31] in eight runs and 1.71 [1.65; 1.77] in the ninth, narrow and
wrong. A narrow interval does not mean a reproducible one, which is why `--recalibrate` runs each pass
in its own process, and why the replay rule below still stands for a single red.

When the code outruns a ceiling, `--ratio-check` says `STALE` (more than 15 % under its reference),
because a ratchet that is never lowered defends nothing: a reference left far above its axis leaves
room for a regression of that size to pass. Lower them with:

```
dotnet run -c Release PROJ -- --ratio-check --recalibrate 3     # ~90 s, prints a table to paste
```

It runs N passes in N separate processes (variance between runs is the larger part, and passes inside
one process cannot sample it), takes the maximum of the per-pass medians, and prints the
`RatioCheck.References` table ready to paste. `--throughput --recalibrate N` does the same for the
per-encoding table. An axis that measures above its reference is printed back unchanged and flagged
`HELD`, since the command lowers ratchets and never raises one. If such an axis is a real regression,
plain `--ratio-check` says `OVER`, and the answer is the code, not the number.

`--rebase` is the one exception, and it is deliberately awkward. It lets a reference rise, but only
on an axis whose `k` moved, because grouping calls into a round changes what is being measured (k
calls in a row are a warm path where a single timed call was cache-cold), or under a reference binary
other than the one the table records. Use it when the harness changed, never when the number did, and
say which change in the commit message.

A new reference binary is carried over, not recalibrated. Both gates record the fingerprint of the
`vxbench` build their tables were set under (`CalibratedShim`) and refuse to gate under another one,
since a ratio through a different denominator is a number about the rebuild. Another machine builds
another binary, and its ratios are its own: there, `--ratio-check --out` and `--throughput --take
--out` (or `--write`) measure every axis or file for that machine's page and hold none to a
reference. After rebuilding `tools/vxbench-rs` (a new `vortex` pin, a new toolchain, a changed entry
point), keep the previous build and carry the tables over:

```
dotnet run -c Release PROJ -- --ratio-check --recalibrate 3 --rebase-from <previous libvxbench.dylib>
dotnet run -c Release PROJ -- --throughput [--take|--write] --recalibrate 3 --rebase-from <previous libvxbench.dylib>
```

Each pass is a pair of processes, one under each binary, and each reference moves by as much as the
new binary moved its ratio. Our side is the same build under both, so an axis the table held over its
ceiling, or stale under it, is still over or stale after the carry, where a plain `--rebase` to the
new measurement would have absorbed it. Paste the tables and the `CalibratedShim` line they print
together, the three throughput tables before the fingerprint.

A red gate is not believed on the first run. The spread from run to run reaches a fifth on some axes,
and a run can come out red with no byte changed. Replay it three times: two reds out of three is a
regression, and anything else is noise.

## Where the figures live

The figures are on [the benchmark page](../docs/guide/benchmarks.md), each section dated and signed
with the machine and the commit it was measured on.
