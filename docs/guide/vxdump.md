# vxdump

vxdump looks inside a Vortex file and prints its schema, layout tree, encodings, segments,
statistics and indexes. It can also show the plan of a query before running it, check index
checksums and repair a torn append.

```
dotnet build tools/vxdump -c Release
tools/vxdump/bin/Release/net11.0/vxdump readings.vortex --schema --stats
```

```
file      readings.vortex
edition   core2026.08.0
bytes     1508316
rows      1000000
tabular   yes
identity  960e4de9e1994575838d481d8539e1d8

schema
  Day: i32
  Celsius: f64?
  City: utf8

statistics
  Day  nulls=0  min=0  max=999  sorted
  Celsius  nulls=20000  min=10.1  max=49.9
  City  nulls=0
```

The file on this page is the million readings the samples write. The header comes first whatever
you ask for. It gives the oldest edition that holds every component the file uses, the length, the
row count, whether the root is a table of columns, and the identity, sixteen bytes that name this
particular write. Two files with the same identity come from the same write, and a file whose writer
set none prints `none`.

vxdump uses nothing but the public surface of `Vorticity`: the tool scan, the options and the
inspection members of `VortexFile`. That makes it a test of the surface. If vxdump could not print
something, the gap would be in the library, not in the tool.

## The options

| | |
|---|---|
| `--layout` | the layout tree, each flat node with its array encoding. This is the default when no section is asked for |
| `--schema` | the file's columns and their types |
| `--encodings` | the array and layout encodings the footer declares, each marked when this build cannot read it |
| `--segments` | every segment's offset, length and alignment |
| `--stats` | the file's statistics per column: nulls, exact bounds, sorted, constant |
| `--all` | the five sections above |
| `--scan` | read every batch and report batches and rows |
| `--indexes` | the index directory: kind, column, block length, runs, blocks, entries, listed bytes, layout |
| `--explain E` | the plan of a scan filtered by `E`, then the count |
| `--fragment P` | add the index fragment in file `P` to the file's own indexes (repeatable) |
| `--verify` | check every index region against its checksum, and a fragment's record of the file's hash against the file |
| `--repair` | truncate a torn append back to the last whole version, and print nothing else |

`--help` prints the same list. An option vxdump does not know is reported on the standard error
and the run exits with 2.

## The layout tree

```
layout
  vortex.struct  rows=1000000  dtype=struct{Day: i32, Celsius: f64?, City: utf8}
    vortex.zoned  rows=1000000  zones=123x8192  dtype=i32
      vortex.chunked  rows=1000000  dtype=i32
        vortex.flat  rows=65536  segments=[0]  dtype=i32  encoding=vortex.runend(vortex.primitive,vortex.sequence)
        vortex.flat  rows=65536  segments=[3]  dtype=i32  encoding=vortex.runend(vortex.primitive,vortex.sequence)
        …
        vortex.flat  rows=16384  segments=[45]  dtype=i32  encoding=vortex.runend(vortex.primitive,vortex.primitive)
        vortex.flat  rows=576  segments=[48]  dtype=i32  encoding=vortex.constant
      vortex.flat  rows=123  segments=[51]  dtype=struct{vortex.min(): i32?, vortex.max(): i32?, vortex.null_count(): u64?}  encoding=vortex.struct(vortex.primitive,vortex.primitive,vortex.primitive)
    vortex.zoned  rows=1000000  zones=123x8192  dtype=f64?
      vortex.chunked  rows=1000000  dtype=f64?
        vortex.flat  rows=65536  segments=[1]  dtype=f64?  encoding=vortex.dict(fastlanes.for(fastlanes.bitpacked),vortex.alp(fastlanes.for(fastlanes.bitpacked(vortex.bool))))
        …
```

Read it from the top down. The root is a struct of three columns. Each column sits in a zone map of
123 zones of 8 192 rows, which keeps the minimum, maximum and null count of every block, and that is
what pruning works from. Under the zone map come the chunks, and under those the flat nodes that
hold the bytes, each naming its encoding as a nest of transforms.

`vortex.runend(vortex.primitive,vortex.sequence)` is a run-end encoding whose values form an
arithmetic progression. `Day` changes once every thousand rows, one day after the other, so a chunk
of 65 536 rows is 66 or 67 runs of consecutive days and the values cost two numbers. The last chunk
holds 576 rows of the same day, so it is a `vortex.constant`. `Celsius` is a dictionary whose values
are ALP-encoded doubles. The zone map of `City` keeps text bounds cut to 16 bytes,
`vortex.bounded_min(16)`, which is how text columns prune. [how-it-works.md](how-it-works.md) walks
through the same tree.

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
  to read          13 segments, 211004 bytes of 1508316
  count tiers      exact (100000), 109 pruned, 13 proven, 1 decoded
  count            100000
```

These are the figures `ExplainAsync` returns in code, available here for a file you did not write
and an expression you are still working on. Only the last line reads data. The count is settled in
tiers: the zone map prunes 109 blocks, proves 13 others match in full, and one block is decoded to
count its survivors.

The expression uses the grammar of the tool path: comparisons, `and`, `or`, `not`, `in`, `is null`,
`like`, with text in single quotes. `"Day >= 900 and City = 'Paris'"` reads 14 segments and counts
12 502 rows. [statistics-and-pruning.md](statistics-and-pruning.md) explains the plan and
[untyped-files.md](untyped-files.md) the grammar.

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
  to read          62 segments, 1502768 bytes of 1514786
  count tiers      0 pruned, 0 proven, 123 decoded
  count            124999

verify
  regions   1 hold their checksums, 0 do not, 0 carry none (the pages and nodes under them are checked as they are read)
  file hash no fragment records one
```

This is the same data written with a Bloom filter on `City`. Every block holds every city, so
neither the zone map nor the filter can rule out a block for `Nice`, and the plan says so before a
single byte of data is read. `--verify` checks each index region against its checksum, and a region
that fails makes the run exit with 6. [indexes.md](indexes.md) explains when an index pays off.

`--fragment P` adds an index fragment, a file of index runs built for this file elsewhere, exactly as
`VortexOpenOptions.IndexFragments` does in code. `--verify` then also checks the fragment's record of
the file's hash against the file.

## A torn file, and repairing it

```
file      torn.vortex
edition   core2026.08.0
bytes     1508316
rows      1000000
tabular   yes
identity  960e4de9e1994575838d481d8539e1d8
torn      128 bytes after the last whole version, of 1508444 (Malformed file: the EOF marker's magic is 0x00000000, expected 'VTXF'.); --repair truncates them
```

```
vxdump torn.vortex --repair
repaired  torn.vortex: 1508444 -> 1508316 bytes (128 torn bytes removed)
vxdump torn.vortex --repair
valid     torn.vortex: 1508316 bytes, nothing to repair
```

When an append did not finish, the file still opens at its last whole version, and the header says
how many bytes lie after it. `--repair` calls `VortexFileRepair.RepairAsync`, which truncates the
file back to that version and leaves a whole file alone. See
[append-and-repair.md](append-and-repair.md).

## Exit codes

| | |
|---|---|
| 0 | it worked |
| 2 | no file was given, an option is unknown, or the expression did not parse or does not fit the file's columns |
| 3 | the file needs a component this build does not have: `unsupported array: … 'vortex.alz'` |
| 4 | the file is malformed: `malformed: Malformed file: the EOF marker's magic is 0x00000000, expected 'VTXF'.` |
| 5 | the file could not be read: `io: Could not find file '…/missing.vortex'.` |
| 6 | `--verify` found a region that does not hold its checksum, or a file hash that does not match a fragment's record |

The header and the sections go to the standard output and the diagnostics to the standard error.
Numbers print the same way whatever the culture, so `vxdump f.vortex | diff -` works, and a script
can run `--all` over a directory and look at every file that exits above 0. A component this build
cannot decode only fails the run where it is needed: `--encodings` lists it and exits 0, while
`--scan` exits 3.

## Run it

```
dotnet run --project tools/vxdump -c Release -- <file.vortex> --all
```

On the shared runtime, `--schema` on the demonstration file takes about 55 ms from start to exit,
most of it spent starting the runtime. Published ahead of time it needs no runtime at all, which is
what a tool run once per file over a whole directory wants. See [native-aot.md](native-aot.md).
