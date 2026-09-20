# How it works

The shape of the library in two pages, and where to read further.

## A file

A Vortex file is read from the end. The last eight bytes are a marker; before them a postscript;
before that the footer, which holds the schema, the layout tree, the per-column statistics, the
segment table and any metadata the writer attached. One read of 64 KiB gets all of it on a normal
file, which is why opening costs a single round trip.

Everything else is **segments**: opaque, aligned runs of bytes the layout tree points into. The
reader fetches a segment only when something it holds is needed.

## A layout tree

The tree says how rows are arranged, and it is what [vxdump.md](vxdump.md) prints:

```
vortex.struct                    the columns
  vortex.zoned                   the bounds of each block of 8192 rows
    vortex.chunked               the chunks the writer sealed
      vortex.flat                bytes, in one segment, under one encoding
```

Four node kinds carry almost every file. `struct` splits the columns, which is what makes a
projection cheap: a column not asked for is a branch not walked. `zoned` holds the minimum and
maximum of each block, which is what makes a filter cheap: a block whose bounds exclude the
predicate is never read. `chunked` is the append seam. `flat` is the leaf, and it names an
encoding.

## An encoding

A leaf is not raw values: it is a nest of transforms, each one reversible and most decodable a
block at a time.

```
vortex.runend(fastlanes.for(fastlanes.bitpacked), vortex.sequence)
```

A run-end encoding whose run values are frame-of-reference bit-packed and whose run ends are an
arithmetic sequence. The writer chose that nest by pricing candidates on the block it held — no
sampling, no guessing — and the reader undoes it with a kernel per layer, most of them SIMD.

That is also why a scan decodes rather than copies: the values you read from a batch are the
decoded block, and reading them costs nothing beyond the decode.

## A scan

A scan is planned before it reads. The plan prunes blocks by their zone maps, consults any index
the file carries, decides which segments it must fetch, and only then reads. What it returns is a
`RecordBatch` per block: decoded columns, borrowed spans, recycled on dispose.

The pieces are separately visible: `ExplainAsync` gives the plan, `ScanMetrics` gives what happened,
and a `CountingSegmentSource` gives what the file was asked for.

## The three packages

| | |
|---|---|
| `Vorticity` | the format: open, layout tree, decoders, scan, filter, indexes, writer |
| `Vorticity.Dataset` | a versioned dataset over an object store — experimental, and this repository's own format |
| `Vorticity.RowEncoding` | the byte-sortable row encoding — experimental, and upstream may change its layout |

Only the first is needed to read or write a file.

## Where to read further

The design documents beside this guide say *why*, in far more depth than a page here should. They
are written for someone implementing the format, not using it.

| | |
|---|---|
| [../02-format.md](../02-format.md) | the binary format, byte by byte |
| [../03-architecture.md](../03-architecture.md) | the .NET architecture and its performance invariants |
| [../07-dotnet-mapping.md](../07-dotnet-mapping.md) | which .NET type each dtype becomes, and where a naive mapping loses data |
| [../08-semantics.md](../08-semantics.md) | pruning algebra, predicate semantics, unknown components, resource caps |
| [../10-indexes.md](../10-indexes.md) | the skipping and locating indexes |
| [../11-write-strategy.md](../11-write-strategy.md) | how the writer chooses an encoding, block by block |
| [../12-index-reads.md](../12-index-reads.md) | the key cursor, and answers that need no rows |
| [../90-registry.md](../90-registry.md) | every encoding, layout and dtype, and its state |
