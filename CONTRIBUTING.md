# Contributing

## What a fresh clone can run, and what it needs first

A clone with nothing else installed but the SDK `global.json` asks for builds, tests and publishes.
The conformance corpus is committed — 856 files and their sidecars — so `dotnet test` runs the
whole thing on a bare clone with no toolchain beyond .NET.

```
dotnet build Vorticity.slnx -c Release
dotnet test Vorticity.slnx -c Release
dotnet publish tools/vxdump -c Release -r <rid>      # Native AOT, ~5 MB, no trim warning
```

Three things are not in the repository, all deliberately, and each one refuses with the command
that produces it rather than with a stack trace.

**The Rust shim, for anything that compares against the reference.** `--ffi-check` and
`--ratio-check` load a cdylib built from `tools/vxbench-rs`, which means **`bench/gate.sh` is red
on a fresh clone** until you build it. So is `--throughput --check` in any of its forms.

```
cd tools/vxbench-rs && cargo build --release
```

`bench/crosscheck.sh` needs `cargo` too, and builds a different crate: it has the .NET side write
the corpus out and has Vortex Rust read every file back.

**The throughput inputs**, 468 MB for 57 encodings, generated rather than committed — a git history
is not the place for half a gigabyte that a script reproduces.

```
bench/gen-throughput.sh                              # once; or point VORTICITY_THROUGHPUT_CORPUS
                                                     # at a directory that already holds them
```

**Vorticity.Zstd's test data**: zstd's golden files and a corpus of frames from zstd's decodecorpus,
binary files written from zstd 1.5.7's sources rather than committed. The tests that read them are
skipped without them, and fail in CI, whose workflow writes them first.
The script needs `curl`, `tar` and a C compiler.

```
tools/native-ref/testdata.sh                         # once
```

## What to run before a change is done

The chain runs when a change is finished, not at every commit. Between two, the suite is enough.
Pick the row that matches what moved; doing more is always allowed, doing less is not.

| what moved | run |
|---|---|
| a decoder, a canonical form, the scan, the filter | the suite in both intrinsic modes · `bench/gate.sh` · `--throughput --check` |
| the compressor, a plan, the blob, the writer, with no intent to change a byte | the same · `bench/crosscheck.sh` · `--throughput --write --check` |
| bytes on disk, deliberately | the same, plus the corpus byte count before and after in the commit message |
| indexes, keys, the dataset | the suite in both modes · `bench/gate.sh` |
| a public signature, a visibility, an XML summary | the suite · a Release build with no warning · the public surface record updated in the same commit |
| the bench or the tools | `bench/gate.sh`, and the command you touched, once |
| documents and comments only | a Release build, so the `<see cref>` links still resolve |

What each command costs, so you can choose knowingly:

```
dotnet test Vorticity.slnx -c Release                                             ~1 min
DOTNET_EnableHWIntrinsic=0 dotnet test Vorticity.slnx -c Release                  ~1 min
bash bench/gate.sh                                                                  ~70 s
bash bench/crosscheck.sh                                                            ~80 s
dotnet run -c Release --project bench/Vorticity.Benchmarks -- --throughput --check  ~55 s
                                                          -- --throughput --take --check   ~90 s
                                                          -- --throughput --write --check  ~9 min
```

`bench/gate.sh` is the short answer to "is this safe to push": the nine deterministic ratchets, a
row-count agreement with the reference through FFI, and every ratio against it. It is a command
with an exit code, so a hook can run it:

```
ln -s ../../bench/gate.sh .git/hooks/pre-push
```

The two intrinsic modes apply to the suite only. Without intrinsics our side loses the SIMD the
reference keeps, so the ratio gates are red by construction and mean nothing there.

`bench/crosscheck.sh` is the only oracle that catches a fault our reader and our writer share: it
writes the corpus with this library and has Vortex Rust read it back, scalar by scalar. Needs
`cargo`.

## The ratchet rule

Nine test classes hold a number rather than a behaviour — `PathAllocationTests`,
`ScanAllocationTests`, `WriteAllocationTests`, `WrittenSizeTests`, `FlatLayoutDecodeCountTests`,
`RoundTripCountTests`, `LiveMemoryTests`, `CorpusCoverageTests`, `DatasetBudgetTests` — and the
benchmark harness holds a reference ratio per axis. They exist to fail on a regression nobody was
watching for.

**A ratchet comes down, and does not go up to make something pass.** That is the whole rule, and it
is the one that decides whether any of these numbers is still worth having: a reference that stops
following the code stops guarding it, and a reference raised to accommodate a change guards
nothing at all.

Three things follow.

* **Lower one whenever the measurement allows it**, in the commit that earned the gain. A ratchet
  left far above its axis is slack, not safety.
* **A red ratio is replayed before it is believed.** Several axes are noisier between processes
  than the 15 % margin, and the harness names them when it prints one. Re-run in a second process;
  if it is still red, it is real.
* **Raising one is a deliberate, dated, justified edit**, with the measurement written at the site
  and the reason a reader can check. `--recalibrate` refuses to loosen on its own, by design.

One ceiling is not portable, and it is worth knowing before you go looking for a regression.
`PathAllocationTests` measures a whole read path from a corpus file's absolute path, so the figure
carries that path's string — two bytes per character. The ceilings were set where this repository
lives, and a clone thirty characters deeper is red by a couple of hundred bytes on every axis at
once, in lockstep. Several axes moving together by the same amount is the signature: compare the
two clone paths before believing the code moved.

The same applies to analyzer warnings. There is no `#pragma` in this repository and no rule turned
off in `.editorconfig`: an analyzer worth silencing is usually right about the code.

## References in comments

Comments cite things like `BENCH-AUDIT.md B19`, `PERF-AUDIT-v2.md Z1b`, `WRITE-AUDIT.md W-36`,
`IMPL-PLAN.md §1.34` or `PERF-GAPS.md W3.1`. There are 215 of them.

**These are not links, and the files are not in this repository.** They are the maintainers'
engineering journals — measurement sessions, profiles, the argument behind a decision — written in
French and kept out of the published tree. A reference of that shape dates a decision and names
where its evidence was recorded. Read the comment around it: it is written to stand on its own, and
the reference is there so that whoever has the journals can find the measurement, not so that you
can follow a link.

Do not add new ones. A comment that needs a journal to be understood is a comment that has not been
written yet.

## Commit messages

English, Conventional Commits, and the body carries the argument.

```
<type>(<scope>): <summary>

<why, in sentences that stand on their own>

BREAKING CHANGE: <what a caller has to change>
```

**Type** is one of `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `build`, `ci`, `chore`,
`bench`. **Scope** is optional and names the part that moved: `reader`, `writer`, `scan`, `filter`,
`index`, `dataset`, `rowenc`, `bench`, `tools`, `docs`. A change that breaks a caller takes a `!`
after the scope and a `BREAKING CHANGE:` trailer saying what they have to do.

**The summary is an instruction, not a report** — `fix the budget`, never `fixed the budget`. Aim
for 72 characters and do not pass 80; a summary that needs more is usually two commits. No full
stop: it is a title.

**The body says why, and the numbers belong in it.** Before and after, measured, with the unit:
that is what makes a commit worth reading a year later. What the diff already shows does not need
saying twice.

**Every sentence stands on its own.** No document name, no section mark, no numbered step, no
finding id, no commit hash. Those point at things a reader of this repository cannot open — the
journals are not published, and a hash does not survive a rewrite. The reasoning in
[References in comments](#references-in-comments) is the same reasoning: if a message needs a
journal to be understood, it has not been written yet.

## Style

* No file headers, and no `Copyright` lines: `LICENSE` and `NOTICE` carry that.
* No capitals for emphasis, and no history in a comment — say what the code does now and why it is
  this way, not what it used to be.
* A comment earns its place by saying something the code cannot: a measurement, a constraint from
  the format, a road not taken and the reason.
