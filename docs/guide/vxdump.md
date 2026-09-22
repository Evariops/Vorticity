# vxdump

Look inside a file: schema, layout tree, encodings, segments, statistics, indexes, and the plan of
a query.

```
dotnet build tools/vxdump -c Release
tools/vxdump/bin/Release/net11.0/vxdump readings.vortex --schema --stats
```

```
file      readings.vortex
edition   core2026.08.0
bytes     1564708
rows      1000000
tabular   yes
identity  82a442c0f8354e23a57c73bea051a968

schema
  Day: i32
  Celsius: f64?
  City: utf8

statistics
  Day  nulls=0  min=0  max=999  sorted
  Celsius  nulls=20000  min=10.1  max=49.9
  City  nulls=0
```

The figures on this page are the demonstration file of a million readings the samples write. The
header is printed whatever you ask for: the oldest edition that holds every component the file
uses, its length, its rows, whether its root is a table of columns, and its identity, the sixteen
bytes that name this write; two files with the same identity are the same write, and a file whose
writer set none says `none`.

vxdump is written against the public surface of `Vorticity` alone, the tool scan, the options,
and the inspection members of `VortexFile`, and nothing else. That makes it the proof that the
surface is complete: a section vxdump could not print from it would be a gap in the surface, not in
vxdump.

## The options

| | |
|---|---|
| `--layout` | the layout tree, each flat node with its array encoding; what vxdump prints when no section is asked for |
| `--schema` | the file's columns and their types |
| `--encodings` | the array and layout encodings the footer declares, each marked when this build cannot read it |
| `--segments` | every segment's offset, length and alignment |
| `--stats` | the file's statistics, per column: nulls, exact bounds, sorted, constant |
| `--all` | the five sections above |
| `--scan` | read every batch, and report batches and rows |
| `--indexes` | the index directory: kind, column, block length, runs, blocks, entries, listed bytes, layout |
| `--explain E` | the plan of a scan filtered by `E`, then the count |
| `--fragment P` | add the index fragment in file `P` to the file's own indexes; repeatable |
| `--verify` | check every index region against its checksum, and a fragment's record of the file's hash against the file |
| `--repair` | truncate a torn append back to the last whole version, and print nothing else |

An option vxdump does not know is reported on the standard error and ignored.

## The layout tree

```
layout
  vortex.struct  rows=1000000  dtype=struct{Day: i32, Celsius: f64?, City: utf8}
    vortex.zoned  rows=1000000  zones=123x8192  dtype=i32
      vortex.chunked  rows=1000000  dtype=i32
        vortex.flat  rows=32768  segments=[0]  dtype=i32  encoding=vortex.runend(vortex.primitive,vortex.primitive)
        vortex.flat  rows=8192  segments=[3]  dtype=i32  encoding=vortex.runend(vortex.primitive,vortex.primitive)
        …
        vortex.flat  rows=576  segments=[147]  dtype=i32  encoding=vortex.sequence
      vortex.flat  rows=123  segments=[150]  dtype=struct{vortex.min(): i32?, vortex.max(): i32?, vortex.null_count(): u64?}  encoding=vortex.struct(vortex.primitive,vortex.primitive,vortex.primitive)
    vortex.zoned  rows=1000000  zones=123x8192  dtype=f64?
      vortex.chunked  rows=1000000  dtype=f64?
        vortex.flat  rows=32768  segments=[1]  dtype=f64?  encoding=vortex.dict(fastlanes.for(fastlanes.bitpacked),vortex.alp(fastlanes.for(fastlanes.bitpacked(vortex.bool))))
        …
```

Read it downwards: a struct of three columns; each column wrapped in a zone map of 123 zones of
8 192 rows, the minimum, maximum and null count of each block, which are what make pruning work;
under it the chunks, and under those the flat nodes that hold the bytes, each naming its encoding
as a nest of transforms. `vortex.runend(vortex.primitive,vortex.primitive)` is a run-end encoding
of plain run ends and plain values: `Day` changes once every thousand rows, so a chunk of 32 768
rows is 33 or 34 runs. The last chunk, 576 rows of an arithmetic progression, is a `vortex.sequence`:
two numbers. `Celsius` is a dictionary whose values are ALP-encoded doubles. The zone map of `City`
holds text bounds truncated to 16 bytes, `vortex.bounded_min(16)`, which is how text columns prune.
[how-it-works.md](how-it-works.md) walks through the same tree.

## Explaining a query without running it

```
vxdump readings.vortex --explain "Day >= 900"
```

```
explain   Day >= 900
  may match        yes
  rows             1000000
  blocks           14 live of 123
    zone map: 109 pruned, 1 segments / 2124 bytes read
  to read          22 segments, 165424 bytes of 1564708
  count tiers      exact (100000), 109 pruned, 13 proven, 1 decoded
  count            100000
```

The figures `ExplainAsync` returns in code, for a file you did not write and an expression you are
still writing; only the last line reads data. The count is settled in tiers: 109 blocks pruned by
the zone map, 13 proven whole by it, and one decoded to count its survivors. The expression is the
grammar the tool path parses: comparisons, `and`, `or`, `not`, `in`, `is null`, `like`, with text in
single quotes. `"Day >= 900 and City = 'Paris'"` reads 23 segments and counts 12 502 rows. See
[statistics-and-pruning.md](statistics-and-pruning.md) and [untyped-files.md](untyped-files.md).

## Indexes, and checking them

```
vxdump indexed.vortex --indexes --verify --explain "City = 'Nice'"
```

```
indexes
  vorticity.bloom.sbbf.v1  column=City  block=8192  runs=1  blocks=123  entries=0  listed-bytes=172  layout=FilterTree

explain   City = 'Nice'
  may match        yes
  rows             1000000
  blocks           123 live of 123
    zone map: 0 pruned, 1 segments / 2940 bytes read
    bloom filter: 0 pruned, 10 segments / 5760 bytes read
  to read          86 segments, 1499048 bytes of 1513461
  count tiers      0 pruned, 0 proven, 123 decoded
  count            124999

verify
  regions   1 hold their checksums, 0 do not, 0 carry none (the pages and nodes under them are checked as they are read)
  file hash no fragment records one
```

The same rows written with a Bloom filter on `City`. Every block holds every city, so neither the
zone map nor the filter can prune a block for `Nice`, and the plan says so before a byte of data
is read. `--verify` checks each index region against its checksum; a region that does not hold
exits with 6. [indexes.md](indexes.md) says when an index pays.

`--fragment P` adds an index fragment, a file of index runs built for this file elsewhere, to
what the file carries, exactly as `VortexOpenOptions.IndexFragments` does in code; `--verify` then
also checks the fragment's record of the file's hash against the file.

## A torn file, and repairing it

```
file      torn-copy.vortex
edition   core2026.08.0
bytes     1564708
rows      1000000
tabular   yes
identity  82a442c0f8354e23a57c73bea051a968
torn      128 bytes after the last whole version, of 1564836 (Malformed file: the EOF marker's magic is 0x00000000, expected 'VTXF'.); --repair truncates them
```

```
vxdump torn-copy.vortex --repair
repaired  torn-copy.vortex: 1564836 -> 1564708 bytes (128 torn bytes removed)
vxdump torn-copy.vortex --repair
valid     torn-copy.vortex: 1564708 bytes, nothing to repair
```

A file whose last append did not finish opens at its last whole version, and the header says how
much lies after it. `--repair` is `VortexFileRepair.RepairAsync`: it truncates the file to that
version, and does nothing to a file that is whole. See [append-and-repair.md](append-and-repair.md).

## Exit codes

| | |
|---|---|
| 0 | it worked |
| 2 | no file was given, an option is unknown, or the expression did not parse or does not fit the file's columns |
| 3 | the file needs a component this build does not have: `unsupported array: … 'vortex.alz'` |
| 4 | the file is malformed: `malformed: Malformed file: the EOF marker's magic is 0x00000000, expected 'VTXF'.` |
| 5 | the file could not be read: `io: Could not find file '…/missing.vortex'.` |
| 6 | `--verify` found a region that does not hold its checksum, or a file hash that is not a fragment's record |

The header and the sections go to the standard output, the diagnostics to the standard error, and
every number is formatted the same whatever the culture, so `vxdump f.vortex | diff -` works and
a script can run `--all` over a directory and look at every file that exits above 0. A component
this build cannot decode is refused only where it is needed: `--encodings` lists it and exits 0,
and `--scan` exits 3.

## Run it

```
dotnet run --project tools/vxdump -c Release -- <file.vortex> --all
```

On the shared runtime, `--schema` on the demonstration file takes about 60 ms from start to exit,
most of it the runtime starting. Published ahead of time it needs no runtime at all, which is what
you want from a tool that runs once per file over a directory: see [native-aot.md](native-aot.md).
