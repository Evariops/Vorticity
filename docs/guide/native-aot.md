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

Nothing else is required. The four libraries (`Vorticity`, `Vorticity.Dataset`,
`Vorticity.RowEncoding` and `Vorticity.Zstd`) are built with `IsAotCompatible` and `IsTrimmable`, so
the trim and AOT analyzers run over your code when you publish, and an `IL2xxx` or `IL3xxx` warning
points at the call of yours that causes the problem. `InvariantGlobalization` is the application's
choice, not the library's. Set it if your application can do without culture data, as vxdump and
the samples do.

## What makes it possible

Ahead-of-time compilation needs to see, at publish time, every type and every generic instantiation
the program will use. The API is designed so that it can:

* A record is compiled, not discovered. `[VortexRecord]` runs inside the compiler and writes
  `Schema`, `ReadRows` and `WriteRows` into the type as static members of `IVortexRecord<T>`. The
  library calls them through the constraint `where TRecord : IVortexRecord<TRecord>`, so
  `file.Scan<Order>()` and `writer.WriteAsync<Order>(rows)` are ordinary generic code over a type the
  compiler knows. Nothing reads a member by name, builds a delegate or emits code at run time. A
  record written by hand works the same way ([records.md](records.md)).
* A filter is data, not an expression tree. The lambda given to `Where` runs once over a `Probe<T>`,
  and its `Sym<T>` operators record a predicate. There is no `Expression<T>` to compile, so nothing
  needs an interpreter in its place.
* An extension type is a generic registration. `o.Extensions.Register<Money>()` reaches `Money`'s
  static members through `IVortexExtension<Money>`, and no type is created by name.
* The decoders are a table. Every encoding the library reads is registered by a static constructor
  that names each decoder, so the trimmer keeps what is reachable and nothing is resolved from an id
  string by reflection.
* The source generator is a build-time dependency. It ships as an analyzer and never loads in your
  process.

One path uses reflection, and it is guarded. A custom aggregator that implements
`IEncodedAggregator<T, TState>` has its encoded steps reached through `MakeGenericMethod` when the
runtime can compile code. Under Native AOT it falls back to its canonical `Step` over decoded
values instead. The answer is the same, only the decode is no longer skipped. See
[aggregates.md](aggregates.md).

One check runs at load. A module initializer refuses a big-endian host with
`PlatformNotSupportedException`, because the reader casts file buffers directly and would otherwise
return byte-swapped values.

## vxdump is the proof

A library cannot prove it is AOT-clean on its own, but an executable that uses it can. vxdump, the
inspection tool in this repository, uses only the public API and is published ahead of time with
`PublishAot`, `PublishTrimmed`, full trimming and warnings as errors, so a reflection-shaped mistake
fails the publish rather than scrolling past.

CI then runs the published binary over every file of the conformance corpus twice. With `--all` it
opens each file, and fails if more than one is refused (the one refusal is a file that embeds no
schema and cannot be opened without one supplied). With `--scan` it decodes every batch of every
file, and fails on any crash or if fewer than 800 files scan. A clean publish plus a run over real
files is what lets this page claim the library works under Native AOT rather than hope it does.

```
dotnet publish tools/vxdump -c Release -r linux-x64
tools/vxdump/bin/Release/net11.0/linux-x64/publish/vxdump readings.vortex --all
```

What AOT buys is startup. On the shared runtime, vxdump spends about 55 ms from start to exit on
`--schema` over the demonstration file, most of it starting the runtime and compiling the first
calls. That is nothing inside a server and a lot for a tool that runs once per file over a directory
of ten thousand. A native binary pays neither cost.

## Watch out

* Publish your own executable ahead of time in your CI, and run it over real files. A trim warning
  at publish is cheap, a missing instantiation in production is not.
* Generic code you write over records stays generic. A method of yours that takes a
  `TRecord : IVortexRecord<TRecord>` is compiled for each record it is called with, which is what you
  want, as long as every call site names a concrete record type the compiler can see.
* A timestamp column with a named time zone resolves it once, at open, with
  `TimeZoneInfo.FindSystemTimeZoneById`. A host without time zone data, such as a minimal container
  image, cannot resolve it, and the column then binds only as its `long` storage, not as
  `DateTimeOffset`.
* Trimming and AOT are separate switches. `PublishTrimmed` alone already removes what nothing
  reaches, and is worth having where AOT is not an option.
