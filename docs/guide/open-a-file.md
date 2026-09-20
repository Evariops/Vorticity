# Open a file

Three ways in, and they differ in who holds the bytes.

## From a path

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
Console.WriteLine($"{file.RowCount} rows, {file.FileLength} bytes");
```

The file opens its own handle and reads through it. This is the one to use unless you have a reason
not to.

## Memory-mapped

```csharp
await using VortexFile file = await VortexFile.OpenAsync(
    MemoryMappedSegmentSource.Open(path), VortexOpenOptions.Default);
```

Every read becomes a pointer into the mapping instead of a syscall. A scan asks its source for the
same segment once per block that needs it (see [scan-a-table.md](scan-a-table.md)), and over a
mapping that repetition costs nothing — which makes this the right choice for a file read many
times, or read many ways.

## From bytes you already hold

```csharp
byte[] bytes = await File.ReadAllBytesAsync(path);
await using VortexFile file = await VortexFile.OpenAsync(
    new MemorySegmentSource(bytes), VortexOpenOptions.Default);
```

`MemorySegmentSource` takes a `ReadOnlyMemory<byte>` and never copies it. The bytes must outlive the
file.

## Choosing

| | when |
|---|---|
| a path | the default: one pass over a file on local disk |
| memory-mapped | the file is read more than once, or scanned several ways |
| bytes in hand | the file came over the wire, out of a cache, or from a test |

For a file on an object store, see `object-store.md`: the seam is the same
`ISegmentSource`, and `Vorticity.Dataset` has an implementation of it.

## Lending a source

By default the file disposes the source it was given. Tell it not to when the source is yours:

```csharp
MemoryMappedSegmentSource source = MemoryMappedSegmentSource.Open(path);
await using (VortexFile file = await VortexFile.OpenAsync(
    source, new VortexOpenOptions { LeaveSourceOpen = true }))
{
    // ...
}

// The source is still open, and serves the next open without a second handle.
await source.DisposeAsync();
```

## What an open costs

One read. Measured on a 1 523 369-byte file:

```
opening cost 1 read of 65535 bytes for a file of 1523369
```

The open reads the last 64 KiB and finds the footer, the schema, the layout tree, the statistics
and — if it fits in that window — the index directory and the file's identity. Nothing else is
touched until you scan. `VortexOpenOptions.InitialReadSize` changes the size of that window;
`PreloadIndexes = true` makes the open read the index directory even when it fell outside, which
costs a second read on a file where it did.

## Watch out

* **`file.Indexes` is `null` until the directory has been read** — by a scan, by
  `ReadIndexesAsync()`, or by opening with `PreloadIndexes = true`. Null means "not read yet", and
  an empty list means "this file has none".
* **`file.TornTail` is `null` for a file that opened whole.** When it is not, the file you are
  reading is the version *before* a torn append, and everything it answers is that version's. See
  [errors.md](errors.md).
* Two files opened over the same path are two independent readers, each with its own handle.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- open-a-file
```
