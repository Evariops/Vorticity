# The guide

Pages named by what you are trying to do. Each one is short, opens with a working example, and says
what happens, what to watch out for, and what it costs.

Every example is a program in [samples/Vorticity.Samples](../../samples/Vorticity.Samples),
compiled by the build and runnable page by page:

```
dotnet run --project samples/Vorticity.Samples -- filter-rows
```

The numbers a page quotes come from that run, on a demonstration file of a million rows.

The design documents are a different thing and live beside this directory: they say why the format
and this implementation are shaped as they are. A page here points at one when you want the depth,
and never repeats it.

## Start here

| page | what you get |
|---|---|
| [getting-started.md](getting-started.md) | reference the library, write a file, read a column back |

## The everyday cases

| page | what you are trying to do |
|---|---|
| [open-a-file.md](open-a-file.md) | open from a path, from a memory-mapped file, or from bytes you already hold, and choose between them |
| [read-the-schema.md](read-the-schema.md) | find out what columns a file has, of what types, before reading a row |
| [scan-a-table.md](scan-a-table.md) | read every row, in batches, without copying |
| [project-columns.md](project-columns.md) | read three columns of fifty and pay for three |
| [filter-rows.md](filter-rows.md) | push a predicate down so that whole blocks are never read |
| [read-rows-by-index.md](read-rows-by-index.md) | take row 4 and row 900 000 without decoding what lies between |
| [write-a-file.md](write-a-file.md) | write batches out, and what the writer decides on your behalf |
| [options.md](options.md) | the read and write options worth knowing, and the defaults they change |
| [cancel-work.md](cancel-work.md) | how cancellation propagates through a scan, a write and a cursor |
| [errors.md](errors.md) | the two exceptions this library raises, what each one means, and which are your fault |

## Going further

| page | what you are trying to do |
|---|---|
| [indexes.md](indexes.md) | what `Auto` builds and what it costs, and when to ask for more by name |
| [keys-in-order.md](keys-in-order.md) | walk a column in key order, seek, step, rank, and read distinct keys |
| [statistics-and-pruning.md](statistics-and-pruning.md) | see which blocks were skipped and why, and what makes pruning work |
| [editions.md](editions.md) | target an edition so that older readers, and Vortex Rust, can read what you write |
| [encoding-hints.md](encoding-hints.md) | steer the compressor per column, and measure whether it helped |
| [append-and-repair.md](append-and-repair.md) | add rows to a file you already wrote, and recover one whose tail was torn |
| `datasets.md` | create a versioned dataset over an object store, import, append, commit |
| `dataset-maintenance.md` | compact, vacuum, verify, and what each costs in requests |
| `object-store.md` | implement the store seam for the service you use; also how to open a single file out of one |
| `row-keys.md` | encode a tuple into bytes whose `memcmp` order is tuple order |
| [vxdump.md](vxdump.md) | look inside a file: schema, layout tree, encodings, segments, statistics |
| [native-aot.md](native-aot.md) | publish an application that uses this library ahead of time |
| [threads.md](threads.md) | what is safe to share, what is not, and how to turn on parallel decoding |
| [limits.md](limits.md) | the caps that protect a reader from a hostile file, and how to change them |

## Reference

| page | what you get |
|---|---|
| [how-it-works.md](how-it-works.md) | the shape of the library in two pages, and where to read further |
| `benchmarks.md` | what this costs against the Rust implementation, on published scenarios |
