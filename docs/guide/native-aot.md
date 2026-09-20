# Native AOT

Publish an application that uses this library ahead of time.

The library is built for it: no reflection and no assembly scanning. The decoder table is a static
constructor that names every decoder explicitly, so a trimmer keeps what is reachable and nothing is
resolved by name at run time.

One thing does run at load: a module initializer that refuses a big-endian host, because the reader
casts file buffers directly and would otherwise hand back byte-swapped values. It is a single branch
and it throws `PlatformNotSupportedException` rather than reading wrong data.

## Your application

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>full</TrimMode>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

```
dotnet publish -c Release -r linux-x64
```

Nothing else is required. `Vorticity` ships with `IsAotCompatible` and `IsTrimmable` set, so the
analyzers run against your code as well and an `IL2xxx` or `IL3xxx` warning tells you which of your
own calls is the problem.

`InvariantGlobalization` is an application property, not a library one: set it in your project if
you want it, and never expect a library to have decided it for you.

## What it buys

`vxdump`, the tool in this repository, is the proof and the example. Published for arm64 on a
laptop:

| | |
|---|---|
| the binary | 5 032 280 bytes, self-contained |
| startup, ahead of time | 0 to 10 ms |
| startup, on the shared runtime | 30 to 50 ms |

Thirty milliseconds per invocation is nothing inside a server and everything in a tool that runs
once per file over a directory of ten thousand.

## What CI proves on every pull request

The publish is done with warnings as errors, so a reflection-shaped mistake fails the build rather
than being scrolled past. Then the published binary opens **every file of the conformance corpus**
and walks its layout tree: 855 of 856 open, and the one refusal is a file that embeds no schema and
so cannot be opened without one supplied.

A separate step runs `--row-keys` over the corpus, because the row encoder is generic over eleven
value types and dispatches through static abstract interface members — the shape most likely to
compile clean and then fail to find an instantiation at run time. That it scans as well means it
also meets encodings no pinned edition carries.

That pair is what lets this page say the library is AOT-clean rather than believe it.

## Watch out

* **A library cannot prove it is AOT-clean; an executable can.** If you build one, publish it
  ahead of time in your own CI and run it over real files. A trim warning at publish is cheap; a
  missing instantiation at run time in production is not.
* `Vorticity.RowEncoding` is the part to exercise hardest under AOT, for the reason above.
* Trimming and AOT are separate switches. `PublishTrimmed` alone already removes what nothing
  reaches, and is worth having even where AOT is not an option.

## Run it

```
dotnet publish tools/vxdump -c Release -r osx-arm64
./tools/vxdump/bin/Release/net11.0/osx-arm64/publish/vxdump <file.vortex> --all
```
