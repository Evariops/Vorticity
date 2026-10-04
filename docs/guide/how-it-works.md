# How it works

The shape of the library in two pages, and where to read further.

```csharp
await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path))
{
    ColumnsBuilder<Reading> builder = writer.Builder<Reading>();
    for (int row = 0; row < Demo.ReadingRows; row++)
    {
        builder.Day.Append(row / 1_000);
        builder.Celsius.Append(row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0));
        builder.City.Append(Demo.Cities[row / 7 % Demo.Cities.Length]);
        if (builder.RowCount == writer.BlockRows)
        {
            await writer.WriteAsync(builder);
        }
    }

    await writer.WriteAsync(builder);
    report = await writer.CompleteAsync();
}

await using VortexFile file = await VortexFile.OpenAsync(path);
```

```
written: 1000000 rows in blocks of 8192, 17 chunks of 65536, 16384, 576 rows; 1508212 bytes: data 1495924, statistics 200, zone maps 8264, indexes 0, footer 3824
  Day: RunEnd, Sequence
  Celsius: Dictionary, Alp
  City: RunEnd
opened: struct{Day: i32, Celsius: f64?, City: utf8}, 1000000 rows, 1508212 bytes, 54 segments, edition core2026.08.0
layout: vortex.struct of vortex.zoned(123 zones of 8192), vortex.zoned(123 zones of 8192), vortex.zoned(123 zones of 8192)
plan: 14 of 123 blocks live, 13 segments, 210980 bytes; ran: 100000 rows, 14 blocks decoded, 109 pruned; the lambda ran 1 time
```

## The session

Everything runs in a `VortexSession`: one memory pool that every batch, segment and builder buffer
comes from, one optional segment cache for all its files, one bound on the reads in flight, one
degree of parallelism, and the extension types it knows. It is thread-safe and meant to be shared
by a whole process. `VortexSession.Default`, which `VortexFile.OpenAsync(path)` and the samples
use, is immutable and takes no cores it was not given: its parallelism is 1. See
[threads.md](threads.md).

## A file

A Vortex file is read from the end. Its last bytes are a marker and a postscript; before them the
footer, which holds the schema, the layout tree, the statistics of each column and the map of every
segment. One read of the tail, 64 KiB by default, gets all of it for an ordinary file, so opening
costs one round trip whatever the file's size; a path, where a read costs no round trip, reads its
last 8 KiB first and a larger footer next. A path is memory-mapped by the first scan that reads
data, so that every read after it is a page fault, unless the open read it whole, a file of 64 KiB
or less, whose scans that read serves. Everything else is **segments**: aligned
runs of bytes, 153 in this file, which the reader fetches only when something it needs lies in
them.

## A layout tree, and its encodings

The layout tree says how the rows are arranged, and [vxdump.md](vxdump.md) prints it:

```
vortex.struct                    the columns
  vortex.zoned                   the minimum, maximum and null count of each block of 8 192 rows
    vortex.chunked               the chunks the writer sealed
      vortex.flat                bytes, in one segment, under one encoding
```

`struct` splits the columns, which is what makes a projection cheap: a column no record member
asks for is a branch not walked. `zoned` holds the bounds of each block, which is what makes a
filter cheap: a block whose bounds exclude the predicate is never read. `chunked` is where appends
join. `flat` is a leaf, and its bytes are not raw values but a nest of reversible transforms,
chosen by the writer per chunk after pricing candidates on the data it held: `Day` became run ends,
`Celsius` a dictionary of ALP-encoded doubles, and the whole million readings 1.5 MB.

## A scan

`file.Scan<Reading>()` is a builder. The lambda given to `Where` runs **once**, when the scan is
built, over a probe whose members are symbols: `r.Day >= 900` records a predicate instead of
comparing anything, which is why the sample counts one call for a million rows, and why what
cannot be pushed down does not compile. The predicate is planned before a byte of data is read:
the file's statistics may settle it, the zone maps prune blocks, an index prunes more, and the
plan lists the segments the live blocks need: here 14 of 123 blocks, 13 segments.

Then a pipeline reads those segments, decodes one block per batch into buffers from the session's
pool, overlapping the next decode with your work on the current one, and hands you a
`Columns<Reading>`: a `ref struct` over the decoded block, valid until the next `MoveNextAsync`,
which the compiler keeps inside the loop body. Nothing is copied for you and nothing is left to
dispose. What you want beyond borrowed columns is a sink you choose, and pay for: `ToBatchesAsync`
copies each batch once into buffers you own, `ToRecordsAsync` builds rows, and `CountAsync`,
`SumAsync`, `GroupBy(…).AggAsync(…)` run as operators of the scan, on the encoded blocks, reading
from the statistics when those suffice. `ExplainAsync` is the plan before; `Statistics` is what
the scan did, after. See [scan-a-table.md](scan-a-table.md) and
[statistics-and-pruning.md](statistics-and-pruning.md).

## A write

A writer is fed through builders, as a `PipeWriter` is fed through buffers. `Builder<Reading>()`
hands out the writer's own column builders; you append, or fill spans in place, and `WriteAsync`
takes the rows. The writer keeps them until it holds whole blocks of `BlockRows`, encodes each
column from where its bytes lie, and seals the blocks into a chunk at `FlushAsync` or when a chunk
reaches its target size; `CompleteAsync` writes what is pending as the tail block, then the
statistics, the zone maps, any index, and the footer. The `WriteReport` says what it chose, down to
each chunk's encoding. See [write-a-file.md](write-a-file.md).

## The engine under it

Under the surface sits the engine the scans and the writers compile to: a type arena built once at
open, arenas of canonical arrays that a batch borrows, a decoder per encoding registered in a
static table, a reader per layout, the pruners, the index readers, and the encoders and the
compressor of the writer. None of it is public: a caller names only the types of the surface, each
on purpose, and the engine can change under them.

## The packages

| | |
|---|---|
| `Vorticity` | the format: session, files, scans, columns, writers, options, plans, the I/O seam |
| `Vorticity.Generators` | the `[VortexRecord]` generator and the analyzers, at build time only: [records.md](records.md), [diagnostics.md](diagnostics.md) |
| `Vorticity.Dataset` | a versioned dataset over an object store, experimental: [datasets.md](datasets.md) |
| `Vorticity.RowEncoding` | the byte-sortable row encoding, experimental: [row-keys.md](row-keys.md) |

Only the first is needed to read or write a file, and it depends on nothing beyond the shared
framework but `System.IO.Hashing` and `Vorticity.Zstd`, this repository's managed Zstandard.

## Where to read further

The design documents beside this guide say why, in far more depth than a page here should.

| | |
|---|---|
| [01-scope.md](../design/01-scope.md) | what the library reads, writes and answers, and the guarantees it holds |
| [02-format.md](../design/02-format.md) | the binary format, condensed |
| [03-architecture.md](../design/03-architecture.md) | the .NET architecture and its performance invariants |
| [07-dotnet-mapping.md](../design/07-dotnet-mapping.md) | which .NET type each column type becomes, and where a naive mapping loses data |
| [08-semantics.md](../design/08-semantics.md) | pruning, predicate semantics, unknown components, resource caps |
| [10-indexes.md](../design/10-indexes.md) | the skipping and locating indexes |
| [11-write-strategy.md](../design/11-write-strategy.md) | how the writer chooses an encoding, block by block |
| [12-index-reads.md](../design/12-index-reads.md) | the key cursor, and answers that need no rows |
| [13-dataset.md](../design/13-dataset.md) | the versioned dataset |
| [14-public-api.md](../design/14-public-api.md) | the rules the public surface follows, and where each part lives |
| [90-registry.md](../design/90-registry.md) | every encoding, layout and column type, and its state |
| [05-benchmarks.md](../design/05-benchmarks.md) | how performance is measured against the Rust implementation, whose figures are on [benchmarks.md](benchmarks.md) |

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- how-it-works
```
