# Native AOT

Publish an application that uses this library ahead of time.

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

```
dotnet publish -c Release -r linux-x64
```

Nothing else is required. The three libraries are built with `IsAotCompatible` and `IsTrimmable`,
so the trim and AOT analyzers run over your code when you publish, and an `IL2xxx` or `IL3xxx`
warning names the call of yours that is the problem. `InvariantGlobalization` is an application's
choice, not a library's: set it if your application can live without culture data. vxdump and the
samples set it.

## What makes it possible

Ahead-of-time compilation needs to see, at publish time, every type and every generic
instantiation the program will use. The surface is built so that it can:

* **A record is compiled, not discovered.** `[VortexRecord]` runs in the compiler and writes
  `Schema`, `ReadRows` and `WriteRows` into the type as static members of `IVortexRecord<T>`. The
  library calls them through the constraint `where TRecord : IVortexRecord<TRecord>`, so
  `file.Scan<Order>()` and `writer.WriteAsync<Order>(rows)` are ordinary generic code over a type
  the compiler knows. Nothing reads a member by name, builds a delegate or emits code at run time.
  A record written by hand works the same way ([records.md](records.md)).
* **A filter is data, not an expression tree.** The lambda given to `Where` runs once over a
  `Probe<T>`, and its `Sym<T>` operators record a predicate. There is no `Expression<T>` to
  compile, so there is nothing an interpreter would have to stand in for.
* **An extension type is a generic registration.** `o.Extensions.Register<Money>()` reaches
  `Money`'s static members through `IVortexExtension<Money>`; no type is activated by name.
* **The decoders are a table.** Every encoding the library reads is registered by a static
  constructor that names each decoder, so the trimmer keeps what is reachable and nothing is
  resolved from an id string by reflection.
* **The generator is a build-time dependency.** It ships as an analyzer and never loads in your
  process.

One path reflects, and it is guarded: a caller's aggregator that implements
`IEncodedAggregator<T, TState>` has its encoded steps reached through `MakeGenericMethod` when
the runtime can compile code, and under Native AOT it takes its canonical `Step` instead, on
decoded values. The answer is the same, the decode is not skipped. See
[aggregates.md](aggregates.md).

One check runs at load: a module initializer refuses a big-endian host with
`PlatformNotSupportedException`, because the reader casts file buffers directly and would
otherwise return byte-swapped values.

## vxdump is the proof

A library cannot prove it is AOT-clean; an executable that uses it can. vxdump, the inspection
tool in this repository, is written against the public surface alone and is published ahead of
time with `PublishAot`, `PublishTrimmed`, `TrimMode` full and warnings as errors, so a
reflection-shaped mistake fails the publish rather than scrolling past. The CI job then opens
**every file of the conformance corpus** with the published binary, `--all`, and fails if more than
one is refused; the one refusal is a file that embeds no schema, which cannot be opened without
one supplied. That pair, a clean publish and a run over real files, is what lets this page say the
library works under Native AOT rather than believe it.

```
dotnet publish tools/vxdump -c Release -r linux-x64
tools/vxdump/bin/Release/net11.0/linux-x64/publish/vxdump readings.vortex --all
```

What it buys is startup. On the shared runtime, vxdump spends about 60 ms from start to exit on
`--schema` over the demonstration file, most of it the runtime starting and the first calls being
compiled. That is nothing inside a server and everything in a tool that runs once per file over a
directory of ten thousand; a native binary starts without either.

## Watch out

* **Publish your own executable ahead of time in your CI, and run it over real files.** A trim
  warning at publish is cheap; a missing instantiation in production is not.
* **Generic code you write over records stays generic.** A method of yours that takes a
  `TRecord : IVortexRecord<TRecord>` is compiled for each record it is called with, which is what
  you want, as long as every call site names a concrete record type the compiler can see.
* A timestamp column with a named zone resolves it with `TimeZoneInfo.FindSystemTimeZoneById`
  once, at open. A host without time zone data, such as a minimal container image, cannot resolve
  it, and the column then binds only as its `long` storage, not as `DateTimeOffset`.
* Trimming and AOT are separate switches: `PublishTrimmed` alone already removes what nothing
  reaches, and is worth having where AOT is not an option.
