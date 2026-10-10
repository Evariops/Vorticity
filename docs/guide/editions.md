# Editions

Target an edition so that older readers, Vortex Rust among them, can open what you write, and know
what that costs.

```csharp
foreach (VortexEdition edition in Enum.GetValues<VortexEdition>())
{
    VortexWriteOptions options = new() { TargetEdition = edition };
    WriteReport report;
    await using (VortexFileWriter writer = session.CreateWriter<Reading>(path, options))
    {
        await writer.WriteAsync<Reading>(readings.AsSpan(), ct);
        report = await writer.CompleteAsync(ct);
    }

    await using VortexFile file = await VortexFile.OpenAsync(path);
    ScanPlan plan = await file.Scan<Reading>().Where(r => r.Day >= 900).ExplainAsync(ct);
    // …
}
```

An edition is a frozen set of components (encodings, layouts, dtypes, aggregates) that every
implementation claiming it must understand. Writing to an edition is a promise that any reader of
that vintage can read the file.

## What this build knows

```
Default core2026.08.3, Newest core2026.08.3, ReadFloor core2025.05.0
```

| edition | name | read by Vortex |
|---|---|---|
| `Core20250500` | core2025.05.0 | 0.36.0 and later |
| `Core20250600` | core2025.06.0 | 0.40.0 and later |
| `Core20251000` | core2025.10.0 | 0.54.0 and later |
| `Core20260800` | core2026.08.0 | 0.84.0 and later |
| `Core20260801` | core2026.08.1 | 0.84.0 and later |
| `Core20260802` | core2026.08.2 | 0.85.0 and later |
| `Core20260803` | core2026.08.3 | 0.85.0 and later |

`VortexEditions.Default` is what a write targets unless told otherwise. It is the edition the most
deployed Rust reader accepts, and the first one that carries `vortex.uuid`, so that a `Guid` member
can be written. It equals `Newest` today, but that will not always be the case. `ReadFloor` is the
oldest edition this library reads and will keep reading. `VortexEditions.Name` and
`MinimumRustVersion` give the other two columns of the table.

`IntroducedIn` and `Contains` tell you where a component comes from:

| component | introduced in | in the read floor |
|---|---|---|
| array `vortex.alp` | core2025.05.0 | yes |
| array `vortex.zstd` | core2025.06.0 | no |
| array `fastlanes.rle` | core2025.10.0 | no |
| layout `vortex.zoned` | core2026.08.0 | no |
| dtype `vortex.uuid` | core2026.08.3 | no |
| array `vortex.patched` | no edition | no |

A component that belongs to no edition can only be read by a reader that happens to know it, which
is why the target exists.

## What an older target costs

The same million rows, written to each edition:

| target | bytes | zone maps | the file reads as | `Day >= 900` reads |
|---|---|---|---|---|
| core2025.05.0 | 1 544 268 | 0 | core2025.05.0 | 123 of 123 blocks |
| core2025.06.0 | 1 499 500 | 0 | core2025.06.0 | 123 of 123 blocks |
| core2025.10.0 | 1 499 500 | 0 | core2025.06.0 | 123 of 123 blocks |
| core2026.08.0 to core2026.08.3 | 1 508 316 | 8 264 | core2026.08.0 | 14 of 123 blocks |

Before core2026.08.0, a file carries no zone maps. The zoned layout arrived in that edition, so a
file written for an older one cannot prune by block: the filter that reads 14 blocks of the default
file reads all 123 of the old one. The encodings barely change. The file written for core2025.05.0,
which lacks sequences and zstd, is 45 KB larger, and from core2025.06.0 on the only difference is the
zone maps.

`file.Edition` is the oldest edition that contains every component the file declares. So the default
write of these rows opens in any reader of core2026.08.0, Vortex 0.84.0, even though it targeted
core2026.08.3: nothing in it needed more.

## What refuses to write

A schema that needs a component the target lacks is refused at `CreateWriter`:

```
a Guid member under core2026.08.2: Kind DType, ComponentId vortex.uuid
  Unsupported Vortex component: dtype 'vortex.uuid'. The write targets edition core2026.08.2, which does not contain it; it was introduced in core2026.08.3.
```

`VortexUnsupportedException.Kind` says which table the id belongs to, and so which edition introduced
it: `Array` or `Layout` for an encoding, `DType`, `Aggregate`, `Compression`, `Encryption`, `Index`
for an index kind, and `Feature` for something the library does not offer on this input, such as an
append over a layout it cannot continue. `ComponentId` is the id as the file spells it. An encoding
the target lacks is not refused: the writer simply chooses among what the edition contains, and the
report says what it chose.

## Watch out

* The default is chosen per release, and changing it is a major version change for this library
  because it changes who can read the output. Set `TargetEdition` when that matters to you.
* An edition constrains the writer, not the reader. This library reads everything from the floor
  upwards, and refuses a component it does not know with `VortexUnsupportedException`, naming it,
  when a scan first needs it ([limits.md](limits.md)).
* A schema may name an extension registered on the session. Such an extension belongs to no edition,
  and a reader needs the same registration.
* [90-registry.md](../design/90-registry.md) lists every component and the edition that introduced
  it.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- editions
```

The figures above come from that run.
