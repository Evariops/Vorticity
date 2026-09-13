# The bench, in one page

Every command below is run from the repository root. `PROJ` stands for
`--project bench/Vorticity.Benchmarks`; add `-c Release` always — a Debug benchmark measures the
JIT's unoptimized output and is worth nothing.

```
dotnet run -c Release PROJ -- <arguments>
```

## The loop: what you changed, what you run

| you changed | you run | it costs | what it answers |
|---|---|---|---|
| a kernel | `-- fastlanes` (its class) | 1–8 s | did the kernel move, against the ported arm on the same clock |
| anything, want a direction | no argument at all | **14 s** | the four default classes, 10 cases, fast profile |
| a number about to be written down | `-- --full fastlanes` | 1–4 min | the reference profile, on the ONE class concerned |
| a read path | `-- --ratio-check [axis…]` | 29 s, or 5 s for one axis | the thirteen axes against Rust, interleaved, each held to a ceiling |
| a decoder | `-- --throughput <family>` | ~30 s | ns/value per encoding at a million rows, against Rust |
| the writer, or a decoder | `-- --throughput --check` | 54 s | the same, as a gate over all 50 files |
| the FFI harness, or before trusting any ratio | `-- --ffi-check` | < 1 s | both readers return the same rows on the same files |
| a hot path you want to profile | `-- --profile <scenario> [seconds]` | as asked | a bare loop for `dotnet-trace`, no harness in the profile |
| a ratchet | `dotnet test Vorticity.slnx -c Release` | ~1 min | the suite plus the eight allocation and count ratchets |

**A fast-profile figure is a direction, not a number.** Anything under about 5 % on a kernel, and
every figure that goes into [BASELINE.md](BASELINE.md), is confirmed with `--full` on the one class
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

`--full` and `--explore` are ours and are consumed before BenchmarkDotNet sees the rest, so
`-- --full fastlanes` works as written. Everything else is forwarded.

**No argument means all of them**, not a prompt: without one, BenchmarkDotNet asks the console which
class to run, which makes the default run do nothing under a script or in CI. `Program.cs` supplies
`--filter *` when nothing else has said what to run.

## The classes, and why there are only six

| class | category | in the default run |
|---|---|---|
| `FastLanesKernelBenchmarks` | `kernel` | yes — 17 bits; `--full` adds 10 and 33 |
| `FsstKernelBenchmarks` | `kernel` | yes |
| `FilterKernelBenchmarks` | `kernel` | yes |
| `RowEncodingBenchmarks` | `path` | yes |
| `RandomAccessBenchmarks` | `explore` | no — `--explore` |
| `FilterSelectivityBenchmarks` | `explore` | no — `--explore` |

`explore` holds the **curves** — a selectivity sweep, a take sweep — which answered their question
once and do not guard against anything. They stay runnable and stay out of the default run.

Nine other classes were deleted rather than demoted: each measured something another instrument
measures with a better estimator, and BENCH-AUDIT.md §3.1 names the replacement for every one. The
last to go was `RewrittenComparison`, whose unique question — our own bytes, read by both readers —
is now the four `rewritten` axes of `--ratio-check`. The numbers they produced are not lost: they are
in [BASELINE.md](BASELINE.md) and in the commits.

## The gates, and their ceilings

* **`--ratio-check`** — thirteen axes, ours against Rust, **interleaved against one clock** so that
  drift is common to both arms. Nine on the dataset (full scan, full scan upstream-lazy, projected,
  first batch, footer only, read-and-write-back, filtered 1 %, filtered half, scattered take), and
  four `rewritten` ones that read a file **our writer produced** beside the reference's — the only
  place our own encoding choices are measured at all. Each has a ceiling in `RatioCheck.cs`; over it,
  non-zero exit. 29 s.
  A bare word narrows it to the axes whose name contains it — `-- --ratio-check write`, `--
  --ratio-check rewritten` — which is the difference between checking one change and waiting for
  thirteen axes.
* **`--throughput [family…] [--check]`** — 50 encodings at a million rows, where the fixed
  open-and-walk cost is under a percent instead of most of the measurement. `--check` makes it a
  gate. Its inputs are 330 MB and are not committed: run `bench/gen-throughput.sh` once, and it
  finds them in `~/.cache/vorticity/throughput-1M` by itself (`VORTICITY_THROUGHPUT_CORPUS`
  overrides). `--check` refuses a corpus with no `manifest.json`, one generated at another row
  count, or one whose files do not match their recorded sha256 — fifty ratchets against bytes that
  live outside the repository need to know *which* bytes.

  **A bare family name narrows the report, and does not currently give the gate's ratio**:
  `fsst` reads 1.27 in the full run and 1.63–1.66 on its own, reproducibly, on the same bytes — over
  its ceiling on a healthy tree. Use the narrow form to see a direction; confirm with the full run
  before believing a red. BENCH-AUDIT.md B8.

**A ceiling only ever comes down, and only behind a real improvement.** Raising one to make a run
pass is the one thing this directory forbids outright.

**When the code outran a ceiling, `--ratio-check` says `STALE`** — more than 15 % under its reference
— because a ratchet that is never lowered defends nothing. `read and write back` sat 43 % under its
own for five commits, which left room for a 75 % regression to pass. Lower them with:

```
dotnet run -c Release PROJ -- --ratio-check --recalibrate 3     # ~90 s, prints a table to paste
```

It measures N passes, takes the max per axis, and prints the `RatioCheck.References` table ready to
paste. An axis that measures *above* its reference is printed back **unchanged** and flagged `HELD`:
the command lowers ratchets and never raises one. If such an axis is a real regression, plain
`--ratio-check` says `OVER`, and the answer is the code, not the number.

**A red gate is not believed on the first run, yet.** Measured run-to-run spread is +12 to +22 % on
four of the nine ratio axes (BENCH-AUDIT.md annexe A.1), and two invocations in four were red with
no byte changed. Replay three times: two reds out of three is a regression, otherwise it is noise —
and either way the observation is data for B2, the statistical gate that is meant to end this
paragraph.

## Where the rest lives

| file | what it holds |
|---|---|
| [BASELINE.md](BASELINE.md) | the current number on every axis, its machine, its commit. A record, not a gate |
| [PROFILE.md](PROFILE.md) | the CPU profiling session: what actually costs, as opposed to what should |
| [ALLOCATIONS.md](ALLOCATIONS.md), [BRANCHING.md](BRANCHING.md), [STRUCTURE.md](STRUCTURE.md) | the three code audits |
| [PERF-AUDIT.md](PERF-AUDIT.md) | v1, the archive: three passes, 41 steps, the negative results |
| `../PERF-AUDIT-v2.md` | the open work list |
| `../BENCH-AUDIT.md` | this instrument, audited: what every figure above comes from |
