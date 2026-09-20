# Editions

Target an edition so that older readers, and Vortex Rust, can read what you write.

An edition is a frozen set of components — encodings, layouts, dtypes, aggregates — that every
implementation claiming it must understand. Writing to an edition is how you promise that a reader
of that vintage can read your file.

## What this build knows

```csharp
Console.WriteLine(EditionRegistry.Name(EditionRegistry.Newest));            // core2026.08.3
Console.WriteLine(EditionRegistry.Name(EditionRegistry.ReadForeverFloor));  // core2025.05.0
```

| edition | name | from Vortex |
|---|---|---|
| `Core20250500` | core2025.05.0 | 0.36.0 |
| `Core20250600` | core2025.06.0 | 0.40.0 |
| `Core20251000` | core2025.10.0 | 0.54.0 |
| `Core20260800` | core2026.08.0 | 0.84.0 |
| `Core20260801` | core2026.08.1 | 0.84.0 |
| `Core20260802` | core2026.08.2 | 0.85.0 |
| `Core20260803` | core2026.08.3 | 0.85.0 |

`ReadForeverFloor` is the oldest edition this library reads and will keep reading. `Newest` is the
default for writing, and `MinimumLibraryVersion` is the Rust version a reader needs.

## What is in one

```csharp
EditionRegistry.IntroducedIn(ComponentKind.Array, "vortex.zstd");   // Core20250600
EditionRegistry.Contains(EditionRegistry.ReadForeverFloor, ComponentKind.Array, "vortex.zstd");  // False
```

| encoding | introduced in |
|---|---|
| `vortex.alp` | `Core20250500` |
| `vortex.fsst` | `Core20250500` |
| `vortex.zstd` | `Core20250600` |
| `vortex.sequence` | `Core20250600` |

`IntroducedIn` returns `null` for a component that belongs to no pinned edition — this library's own
extensions, and anything upstream has not frozen. A file that carries one can only be read by
something that knows it, which is the reason the option exists.

## Writing to one

```csharp
VortexWriteOptions options = new VortexWriteOptions { TargetEdition = VortexEdition.Core20250500 };
```

The writer then chooses only among the components that edition contains. On the demonstration file
of 200 000 rows, that costs nothing at all:

| target | bytes | encodings |
|---|---|---|
| `Core20250500` to `Core20251000` | 394 545 | `Dict` on both columns |
| `Core20260800` to `Core20260803` | 396 025 | the same |

Every one of them reads back 200 000 rows. The encodings this data wants — a dictionary and a
floating-point encoding — have been in every edition since the floor, so targeting the oldest loses
nothing here and the 1 480 bytes of difference are in what the newer editions let the writer record
beside the data, not in the data.

That will not hold for every file. A column the newest encodings suit, and nothing older does, is
where an old target costs you size — the writer falls back rather than refusing, and the report says
what it chose.

## Watch out

* **The default is the newest**, which means a file written with no thought about this is readable
  by Vortex 0.85.0 and later, and not before. Set `TargetEdition` when that matters.
* Changing the default edition would be a **major** version change for this library, precisely
  because it changes who can read the output.
* An edition constrains the *writer*. Reading is not limited to one edition: this library reads
  everything from the floor upwards, and refuses a component it does not know with
  `VortexUnsupportedException` naming it.
* `AllowUnknownComponents = true` at open postpones that refusal to the moment the component is
  needed, which is how a file with one unknown encoding in one column stays readable everywhere
  else.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- editions
```
