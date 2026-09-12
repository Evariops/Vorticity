# vxbench-rs

Vortex's Rust reader behind a C ABI, so that
[docs/05-benchmarks.md](../../docs/05-benchmarks.md) §2 can happen: **both implementations measured
in one process**, on the same bytes, with the same clock and the same page-cache state. Two runs of
two binaries can differ by more than the thing being measured, which is how a 1.4× ratio becomes
unreadable.

## Why this is not `vortex-ffi`

§2 originally named `vortex-ffi` as the cdylib. Two reasons it is not used:

* it is `publish = false` upstream, so it would mean a second git dependency;
* it is a general-purpose C API with its own object model, whose per-call overhead would land
  inside the measurement.

This shim is built against the same crates.io pin the corpus was generated with
(`vortex = "=0.86.1"`), exposes one entry point per benchmark axis, and returns a row count. No
handles, no allocation across the boundary, no object lifetime to manage — there is nothing in the
surface to measure except the scan. The empty-call floor is 2.5 ns.

## Fairness, item by item

* **The session is built once.** `VortexSession::default()` registers every edition and initializes
  the arrow and parquet-variant integrations. An earlier version paid that per call — about 90 µs —
  which nothing on the .NET side pays per open, since `EncodingRegistry` is static. It read as
  Rust being slow to open a file.
* **The file is opened from scratch on every call**, on both sides, so neither gets a warm segment
  cache the other is not offered.
* **Single-threaded on both sides.** The shim uses `vortex::io::runtime::single::block_on` rather
  than the default multi-threaded runtime; our reader has no worker pool. Otherwise the ratio
  measures a threading-model difference (docs/05 §5).
* **Panics are caught** at the boundary: unwinding across a C ABI is undefined behaviour. Every
  entry point returns a count, or a negative status.

## Building and running

Not built by CI: it is a three-minute Rust build producing a 36 MB artifact.

```sh
cd tools/vxbench-rs && cargo build --release

# Both readers must agree on what they read before any ratio means anything:
dotnet run -c Release --project bench/Vorticity.Benchmarks -- --ffi-check

# Then the ratios:
dotnet run -c Release --project bench/Vorticity.Benchmarks -- --filter '*Comparison*'
```

`VORTICITY_VXBENCH` points the loader at a prebuilt library; otherwise the resolver walks up from
the benchmark assembly to `tools/vxbench-rs/target/release/`.

The comparison benchmarks **fail loudly** when the library is missing, naming this command. Skipping
quietly would produce a run that reports no ratio and looks exactly like one that reported a good
one.
