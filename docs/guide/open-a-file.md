# Open a file

A path, a mapped file, positional reads, or bytes you already hold: the ways in differ in who holds
the bytes, and the open reads the same thing whichever you choose.

## From a path

```csharp
await using (VortexFile file = await VortexFile.OpenAsync(path))
{
    Console.WriteLine(file.Schema);
    Console.WriteLine($"{file.RowCount} rows, {file.Length} bytes, edition {file.Edition}");
    Console.WriteLine($"identity {file.Identity}, metadata keys [{string.Join(", ", file.Metadata.Keys)}]");
}
```

```
struct{Day: i32, Celsius: f64?, City: utf8}
1000000 rows, 1508212 bytes, edition Core20260800
identity f6595d25-2b99-4160-9071-63ffd454d25f, metadata keys []
```

`VortexFile.OpenAsync(path)` opens in `VortexSession.Default`: it reads the file's tail, and the
first scan that reads data maps the file into memory, which every read after it goes through. An
open that asks for nothing but the schema, the statistics or a count they answer maps nothing,
and costs a fifth less. This is the one to use unless you have a reason not to.

What the file tells you before a scan:

| member | what it is |
|---|---|
| `Schema` | the columns and their types, a `VortexSchema` you can index, enumerate and print |
| `RowCount`, `Length` | rows, and bytes on disk |
| `Edition` | the oldest edition whose readers can open this file, worked out from what it declares ([editions.md](editions.md)) |
| `Identity` | a `Guid` drawn by every write and append of this library; `Guid.Empty` for a file another writer produced |
| `Metadata` | the user metadata by key; `ReadAsync(key)` returns a copy of a value |
| `Statistics` | per column null count, order flags, and the bounds of a numeric column ([statistics-and-pruning.md](statistics-and-pruning.md)) |
| `TornTail` | not null when the file opened at the version before a torn append ([append-and-repair.md](append-and-repair.md)) |

The identity is stored as metadata of the library's own, which `Metadata.Keys` leaves out: the keys
it lists are the ones the writer's `VortexWriteOptions.Metadata` gave.

## In a session, from a source

```csharp
await using VortexSession session = VortexSession.Create(options => options.MaxConcurrentReads = 8);
byte[] bytes = await System.IO.File.ReadAllBytesAsync(path);

await TimeAsync("a path", await session.OpenAsync(path));
await TimeAsync("a mapped source", await session.OpenAsync(new MemoryMappedSegmentSource(path)));
await TimeAsync("positional reads", await session.OpenAsync(new FileSegmentSource(path)));
await TimeAsync("bytes in memory", await session.OpenAsync(new MemorySegmentSource(bytes)));
```

`TimeAsync` scans the whole file three times and prints the best:

```
a path            a full scan in 2.8 ms
a mapped source   a full scan in 2.2 ms
positional reads  a full scan in 2.4 ms
bytes in memory   a full scan in 2.3 ms
```

A session owns the memory pool, the segment cache, the bound on reads in flight and the degree of
parallelism of every file opened through it ([threads.md](threads.md)). `OpenAsync(path)` on a
session reads a path as the static call does; `OpenAsync(ISegmentSource)` takes any source, and
the file then owns it: disposing the file disposes the source. The three sources of
`Vorticity.IO`:

| source | reads by | when |
|---|---|---|
| `MemoryMappedSegmentSource` | pointers into one mapping of the whole file, made at the open | a file you want mapped before its first scan; a path gives the same once a scan reads data |
| `FileSegmentSource` | `RandomAccess` positional reads on one handle | a file too large to map, or a host that forbids mappings |
| `MemorySegmentSource` | views into bytes you hold, pinned for the source's life, or into one aligned copy of them | the file came over the wire, out of a cache, or from a test |

A `MemorySegmentSource` keeps its bytes on a 64-byte boundary, the widest alignment a segment
declares, so that every read is a view a decoder can take as it is. Bytes already on one are read
where they lie; bytes that are not, which is where a `byte[]` puts them, are copied once, when the
source is made, into memory the source owns and frees. To spare that copy, rent the memory from an
`AlignedMemoryPool` and read the file into it.

For a file on an object store, implement `ISegmentSource` for your service: a length and two read
methods ([object-store.md](object-store.md)). The demonstration file fits in
the page cache, and the four ways in scan it within a millisecond of each other: the choice is about
who holds the bytes, not about speed on a warm disk.

## What an open reads

The sample wraps a `FileSegmentSource` in `CountingSource`, an `ISegmentSource` of thirty lines at
the bottom of the sample that counts what it is asked for and passes every call through:

```csharp
CountingSource counting = new CountingSource(new FileSegmentSource(path));
await using (VortexFile file = await session.OpenAsync(counting))
{
    Console.WriteLine($"the open: {counting.Requests} request, {counting.Bytes} bytes");
    long rows = await file.Scan<Reading>().CountAsync();
    double? hottest = await file.Scan<Reading>().MaxAsync(r => r.Celsius);
    Console.WriteLine($"CountAsync {rows}, MaxAsync {hottest}: still {counting.Requests} request");
```

```
the open: 1 request, 65588 bytes
CountAsync 1000000, MaxAsync 49.9: still 1 request
```

One read of the last 64 KiB, from the 64-byte boundary before them so that the segments inside keep
their alignment: 52 bytes more here. It finds the postscript, and through it the schema, the
layout, the footer and the file statistics; one of them that begins before that window costs one
more read, issued once for the whole gap. Metadata values are read when asked for, from the tail
the open already holds when they lie in it. Neither the row count nor `MaxAsync` on a column the
statistics cover asks for anything more.

`VortexOpenOptions` shapes the open:

```csharp
CountingSource told = new CountingSource(new FileSegmentSource(path));
VortexOpenOptions wide = new VortexOpenOptions { Length = bytes.Length, InitialReadSize = 256 * 1024 };
await using (VortexFile file = await session.OpenAsync(told, wide))
```

```
InitialReadSize 256 KiB: 1 request, 262196 bytes
```

| option | default | what it changes |
|---|---|---|
| `InitialReadSize` | 65 536 | the tail read; raise it for a file whose footer is large, so that the open stays one request, or, over a source that fetches, to hold a small file whole |
| `Length` | probed | skips asking the source for its length |
| `Schema` | the file's | a schema for a file written without one, or to skip reading the embedded one |
| `TornTail` | `ReadPrevious` | `Refuse` throws on a torn tail instead of opening the version before it |
| `VerifyStatistics`, `MaxDecompressedSize` | off, 256 MiB | how much the open trusts the bytes ([limits.md](limits.md)) |
| `IndexFragments` | none | indexes built for this file elsewhere ([indexes.md](indexes.md)) |

## What a scan then reads

```csharp
await using VortexSession cached = VortexSession.Create(options => options.SegmentCache = new SegmentCache(64L * 1024 * 1024));
```

```
a full scan: the plan names 45 segments, 1465556 bytes; the source served 45 requests, 1465556 bytes
with a segment cache, scan 1: the source served 45 requests, 1465556 bytes; 0 cache hits
with a segment cache, scan 2: the source served 0 requests, 0 bytes; 45 cache hits
```

The plan of a full scan names 45 segments, 1.47 MB of the file's 1.51 MB, and the source serves
exactly that: 45 requests for 1.47 MB. The file has 51 segments; the six that lie in the window
the open read are served from it, and a file shorter than the window is read by its open alone. A
scan reads each segment once, and a segment that spans several batches is shared by them rather
than read again for each. The cache works across scans: the first scan of a session with a
`SegmentCache` reads the same 45 segments and finds none in the cache, and a second scan of the
same file reads nothing, the 45 segments coming from the cache. Over a source where a read is a
request, that is the reason to give the session one.

## Watch out

* **A source handed to `OpenAsync` belongs to the file.** Keep the bytes of a
  `MemorySegmentSource` alive until the file is disposed.
* **Dispose the files before the session.** Disposing a session while one of its files is open
  throws `InvalidOperationException` naming the file.
* Two files opened over the same path are two independent readers. Opened from a path in one
  session they share one mapping, which the session keeps once both are closed, for the next open
  of the same file (`MappedFileCacheCount`). A file is immutable once open and safe to scan from
  several threads at once.
* **A file deleted once closed keeps its disk space while the session keeps it mapped**, up to 64
  files by default. A process that deletes the files it has read calls `ReleaseMappedFiles()` on
  their session, `VortexSession.Default` for a file opened with `VortexFile.OpenAsync(path)`, or
  creates its session with `MappedFileCacheCount = 0`.
* **A mapped file must not be cut short under a scan.** On Linux and macOS, a file that another
  process truncates while a scan reads it through its mapping faults on the pages past its new
  end, and the fault kills the process. A writer of this library cannot do it while the file is
  open. A service that reads files other processes may truncate creates its session with
  `MapFiles = false`: every read is then positional, and a cut file throws
  `VortexFormatException`.

The figures come from one run of the sample on the demonstration file of a million rows.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- open-a-file
```
