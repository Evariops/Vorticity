# vxdump

Look inside a file: schema, layout tree, encodings, segments, statistics.

```
dotnet run --project tools/vxdump -c Release -- readings.vortex --schema --stats
```

```
file      readings.vortex
version   1
bytes     1523369
rows      1000000
tabular   yes
identity  f344d9f5ec594a66bf2b19f93b07b5d2

schema
  day: i32
  celsius: f64

statistics
  field 0  nulls=0  min=set  max=set
  field 1  nulls=0  min=set  max=set
```

The header is printed whatever you ask for. `identity` is the sixteen bytes that name this version
of these bytes; two files with the same identity are the same write.

## The sections

| | |
|---|---|
| `--layout` | the layout tree, each node with its array encoding (the default) |
| `--schema` | the dtype, one line per root field |
| `--encodings` | the array and layout encoding dictionaries |
| `--segments` | every segment's offset, length and alignment |
| `--stats` | file-level statistics, when the file carries them |
| `--all` | all of the above |
| `--indexes` | the index directory: policy, entries, runs and their bytes |
| `--scan` | read every batch and report rows, batches and null counts |
| `--explain E` | the plan of a scan filtered by `E` |
| `--verify` | check every listed index region against its checksum |
| `--repair` | truncate a torn append back to the last valid file |
| `--row-keys` | row-encode every batch and report the keys (experimental) |
| `--fragment P` | add the index fragment in file `P` to the file's own indexes |

## The layout tree

`--layout` is where a file stops being opaque:

```
layout
  vortex.struct  rows=1000000  dtype=struct{day: i32, celsius: f64}
    vortex.zoned  rows=1000000  zones=123x8192  dtype=i32
      vortex.chunked  rows=1000000  dtype=i32
        vortex.flat  rows=999424  segments=[0]  dtype=i32  encoding=vortex.runend(fastlanes.for(fastlanes.bitpacked),vortex.sequence)
        vortex.flat  rows=576  segments=[2]  dtype=i32  encoding=vortex.sequence
      vortex.flat  rows=123  segments=[4]  dtype=struct{vortex.min(): i32?, vortex.max(): i32?, vortex.null_count(): u64?}
    vortex.zoned  rows=1000000  zones=123x8192  dtype=f64
      ...
```

Read downwards: a struct of two columns; each column wrapped in a zone map of 123 zones of 8 192
rows — the bounds that make pruning work; under it the chunks, and under those the flat nodes that
hold the bytes, each naming its encoding as a nest of transforms. `vortex.runend(fastlanes.for(
fastlanes.bitpacked), vortex.sequence)` is a run-end encoding whose run values are frame-of-
reference bit-packed and whose run ends are a sequence: that is the whole of how the `day` column
became almost nothing.

## Explaining a query without running it

```
vxdump readings.vortex --explain "day >= 900"
```

```
explain   day >= 900
  file may match   yes
  rows             1000000
  blocks           14 live of 123 (8192 rows each)
    zone map: 109 pruned, 1 segments / 2140 bytes read
  splits           14 live of 123
  to read          5 segments, 1518308 bytes of 1523369
  count tiers      exact cover (100000), 109 pruned, 13 proven, 1 decoded
  count            100000
```

The same figures `ExplainAsync` returns in code, for a file you did not write and an expression you
are still writing. The syntax is what you would type in SQL — `id >= 10 and name = 'x'`.

## Checking and repairing

`--verify` walks the index regions and checks each against its checksum, and against a fragment's
record of the file's hash when one is given with `--fragment`. `--repair` truncates a torn append,
the same operation as `VortexFileRepair.RepairAsync` — see
[append-and-repair.md](append-and-repair.md).

## Exit codes

| | |
|---|---|
| 0 | it worked |
| 2 | the arguments or the expression did not parse |
| 3 | the file needs a component this build does not have |
| 4 | the file is malformed |
| 5 | the file could not be read |
| 6 | `--verify` found something that does not hold |

They are what makes vxdump usable in a script: `--all` over a directory, and anything above 0 is a
file to look at.

## Run it

```
dotnet run --project tools/vxdump -c Release -- <file.vortex> --all
```

Published ahead of time it is a 4.8 MB binary that starts in about a millisecond, which is what you
want when it runs once per file over a directory — see [native-aot.md](native-aot.md).
