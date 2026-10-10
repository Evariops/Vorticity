# Contributing

## What a fresh clone can run, and what it needs first

A clone with nothing installed but the SDK that `global.json` asks for can build, test and publish.
The conformance corpus is committed (876 files and their sidecars), so `dotnet test` runs everything
on a bare clone with no toolchain beyond .NET.

```
dotnet build Vorticity.slnx -c Release
dotnet test Vorticity.slnx -c Release
dotnet publish tools/vxdump -c Release -r <rid>      # Native AOT, ~5 MB, no trim warning
```

Three things are deliberately not in the repository, and each one, when missing, fails with the
command that produces it rather than with a stack trace.

The first is the Rust shim, needed for anything that compares against the reference. `--ffi-check` and
`--ratio-check` load a cdylib built from `tools/vxbench-rs`, which means `bench/gate.sh` is red on a
fresh clone until you build it, and so is `--throughput --check` in any of its forms.

```
cd tools/vxbench-rs && cargo build --release
```

`bench/crosscheck.sh` needs `cargo` too, and builds a different crate: it has the .NET side write the
corpus out and has Vortex Rust read every file back.

The second is the throughput inputs, about half a gigabyte for 61 files, generated rather than
committed, since a git history is not the place for data a script reproduces.

```
bench/gen-throughput.sh                              # once, or point VORTICITY_THROUGHPUT_CORPUS
                                                     # at a directory that already holds them
```

The third is Vorticity.Zstd's test data: zstd's golden files and a corpus of frames from zstd's
decodecorpus, binary files written from zstd 1.5.7's sources rather than committed. The tests that
read them are skipped without them, and fail in CI, whose workflow writes them first. The script needs
`curl`, `tar` and a C compiler.

```
tools/native-ref/testdata.sh                         # once
```

## What to run before a change is done

The chain runs when a change is finished, not at every commit. Between two, the suite is enough. Pick
the row that matches what moved. Doing more is always allowed, doing less is not.

| what moved | run |
|---|---|
| a decoder, a canonical form, the scan, the filter | the suite, `tests/scalar-pass.sh`, `bench/gate.sh`, `--throughput --check` |
| the compressor, a plan, the blob, the writer, with no intent to change a byte | the same, plus `bench/crosscheck.sh` and `--throughput --write --check` |
| bytes on disk, deliberately | the same, plus the corpus byte count before and after in the commit message |
| indexes, keys, the dataset | the suite, `tests/scalar-pass.sh`, `bench/gate.sh` |
| a public signature, a visibility, an XML summary | the suite, a Release build with no warning, and the public surface record updated in the same commit |
| the bench or the tools | `bench/gate.sh`, and the command you touched, once |
| documents and comments only | a Release build, so the `<see cref>` links still resolve |

What each command costs, so you can choose knowingly:

```
dotnet test Vorticity.slnx -c Release                                           ~1.5 min
bash tests/scalar-pass.sh                                                          ~10 s
bash bench/gate.sh                                                                  ~70 s
bash bench/crosscheck.sh                                                            ~80 s
dotnet run -c Release --project bench/Vorticity.Benchmarks -- --throughput --check  ~55 s
                                                          -- --throughput --take --check   ~90 s
                                                          -- --throughput --write --check  ~9 min
```

`bench/gate.sh` is the short answer to "is this safe to push": the ten deterministic ratchets, a
row-count agreement with the reference through FFI, and every ratio against it. It is a command with
an exit code, so a hook can run it:

```
ln -s ../../bench/gate.sh .git/hooks/pre-push
```

The two intrinsic modes only apply to the suite. Without intrinsics our side loses the SIMD the
reference keeps, so the ratio gates are red by construction and mean nothing there.

`bench/crosscheck.sh` is the only oracle that catches a fault our reader and our writer share. It
writes the corpus with this library and has Vortex Rust read it back, scalar by scalar. It needs
`cargo`.

## Tests run side by side

Every test runs alongside every other one, and each row of a theory is a test of its own:
`tests/xunit.runner.json` sets xunit's `parallelMode` to `all` and pre-enumerates theories, for the
three test projects. A test therefore owns what it writes (a temporary file of its own, or a file of
`SharedFiles`, which a run writes once and its tests only read) and changes nothing another test
reads.

A test that measures the process rather than its own work (its allocations, the blocks finalized
anywhere, its live memory, a JIT that has settled, the memory pressure on the query budget) joins one
of the collections that run alone, one test at a time: `AllocationCollection`, `FinalizerCollection`,
`AdviceCollection` and `CorePressureCollection`.

## The ratchet rule

Ten test classes hold a number rather than a behaviour (`PathAllocationTests`, `ScanAllocationTests`,
`WriteAllocationTests`, `WrittenSizeTests`, `FlatLayoutDecodeCountTests`, `RoundTripCountTests`,
`LiveMemoryTests`, `CorpusCoverageTests`, `DatasetBudgetTests`, `WorkCounterTests`), and the
benchmark harness holds a reference ratio per axis. They exist to fail on a regression nobody was
watching for. `WorkCounterTests` counts the work of a group by at one lane (the rows, groups, arrays
and bytes its tables took), which a clock on a shared machine cannot resolve, so a change that moves
one of its lines writes the line again in the same commit, with the reason.

A ratchet comes down, and never goes up to make something pass. That is the whole rule, and it
decides whether any of these numbers is still worth having: a reference that stops following the
code stops guarding it, and a reference raised to accommodate a change guards nothing at all.

Three things follow:

* Lower a ratchet whenever the measurement allows it, in the commit that earned the gain. A ratchet
  left far above its axis is slack, not safety.
* Replay a red ratio before believing it. Several axes are noisier between processes than the 15 %
  margin, and the harness names them when it prints one. Run it again in a second process, and if it
  is still red, it is real.
* Raising a ratchet is a deliberate, dated and justified edit, with the measurement written at the
  site and a reason a reader can check. `--recalibrate` refuses to loosen on its own, by design.

One ceiling is not portable, and it is worth knowing before you go looking for a regression.
`PathAllocationTests` measures a whole read path from a corpus file's absolute path, so the figure
includes that path's string, two bytes per character. The ceilings were set where this repository
lives, so a clone thirty characters deeper is red by a couple of hundred bytes on every axis at once,
in lockstep. Several axes moving together by the same amount is the signature, so compare the two
clone paths before believing the code moved.

The same applies to analyzer warnings. There is no `#pragma` in this repository and no rule turned
off in `.editorconfig`, because an analyzer worth silencing is usually right about the code.

## References in comments

A comment stands on its own. It does not cite documents that are not in this repository (private
notes, plans, audits), finding ids or commit hashes, because a reader cannot open them. If a comment
needs such a document to be understood, it has not been written yet. Say what the measurement or the
constraint is, in the comment itself.

## Commit messages

English, Conventional Commits, and the body carries the argument.

```
<type>(<scope>): <summary>

<why, in sentences that stand on their own>

BREAKING CHANGE: <what a caller has to change>
```

The type is one of `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `build`, `ci`, `chore` and
`bench`. The scope is optional and names the part that moved: `reader`, `writer`, `scan`, `filter`,
`index`, `dataset`, `rowenc`, `bench`, `tools`, `docs`. A change that breaks a caller takes a `!` after
the scope and a `BREAKING CHANGE:` trailer saying what they have to do.

The summary is an instruction, not a report: `fix the budget`, never `fixed the budget`. Aim for 72
characters and do not pass 80, since a summary that needs more is usually two commits. Leave out the
full stop, since it is a title.

The body says why, and the numbers belong in it: before and after, measured, with the unit. That is
what makes a commit worth reading a year later. What the diff already shows does not need saying
twice.

Every sentence stands on its own, with no document name, no section mark, no numbered step, no
finding id and no commit hash. Those point at things a reader of this repository cannot open, and a
hash does not survive a rewrite. It is the same reasoning as for [references in
comments](#references-in-comments).

## Style

* No file headers, and no `Copyright` lines, since `LICENSE` and `NOTICE` carry that.
* No capitals for emphasis, and no history in a comment. Say what the code does now and why it is
  this way, not what it used to be.
* A comment earns its place by saying something the code cannot: a measurement, a constraint from the
  format, a road not taken and the reason.
