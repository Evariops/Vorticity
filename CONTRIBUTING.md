# Contributing

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

## Style

* No file headers, and no `Copyright` lines: `LICENSE` and `NOTICE` carry that.
* No capitals for emphasis, and no history in a comment — say what the code does now and why it is
  this way, not what it used to be.
* A comment earns its place by saying something the code cannot: a measurement, a constraint from
  the format, a road not taken and the reason.
* Commit messages are English Conventional Commits, and the body says the why with the numbers.
