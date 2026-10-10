# Writer options

Choose how the writer encodes, what it records beside the data, and what the file carries, then read
in the report what it actually did.

```csharp
VortexWriteOptions options = new()
{
    Compression = CompressionProfile.Auto,                     // prices size and decode speed together
    Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add(Reading.ColumnNames.Celsius, EncodingHint.Zstd),
    StringBoundBytes = 32,
    Metadata = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add("producer", "acme/1.4"u8.ToArray()),
    Identity = Guid.Parse("0199f0c4-7d2a-7c3e-9a51-3f6b2c1d4e5f"),   // a reproducible write: the same input gives the same bytes
};

WriteReport report;
await using (VortexFileWriter writer = session.CreateWriter<Reading>(path, options))
{
    await writer.WriteAsync<Reading>(readings.AsSpan(), ct);
    report = await writer.CompleteAsync(ct);
}
```

```
Day: RunEnd x16, Constant
Celsius: Zstd x16, Alp
City: RunEnd x17
metadata keys producer; producer = acme/1.4
identity 0199f0c4-7d2a-7c3e-9a51-3f6b2c1d4e5f, edition core2026.08.0, 564148 bytes
written again with the same identity: the same bytes; without one: different bytes
```

`VortexWriteOptions` is a record with `init` properties. Set what you want to change, and use `with`
to make a variant. The options are checked by `CreateWriter`: a hint, an index or a metadata entry
that does not fit throws `ArgumentException` there, before a single byte is written.

| option | default | what it changes |
|---|---|---|
| `BlockRows` | 8 192 | rows per block: the unit of pruning, of a take, and of a filtered scan's batch where the zone maps do not prove it whole ([blocks-and-chunks.md](blocks-and-chunks.md)) |
| `ChunkTargetBytes` | 0, the writer decides | the bytes gathered before whole blocks are sealed into a chunk. By default, a megabyte of the widest column ([blocks-and-chunks.md](blocks-and-chunks.md)) |
| `ColumnChunkTargetBytes` | empty | a chunk target of its own for a top-level column that wants larger chunks than the file's. Its chunks span whole chunks of the file ([blocks-and-chunks.md](blocks-and-chunks.md)) |
| `Compression` | `Auto` | what the encoder optimises for: `Auto`, `Fastest`, `Smallest` or `None` |
| `Hints` | empty | an encoding to try first, by column path |
| `DegreeOfParallelism` | 0, the session's | the threads the writer summarizes its columns and compresses zstd frames on. The file is byte for byte the same whatever the degree ([threads.md](threads.md)) |
| `TargetEdition` | `VortexEditions.Default` | the edition every component must belong to ([editions.md](editions.md)) |
| `Statistics` | `true` | the per-column statistics the open reads back: null count, order, and the minimum and maximum of a numeric column |
| `StringBoundBytes` | 16 | the length of the bounds a text or binary column's zones carry, 0 for none |
| `Indexes` | `IndexPolicy.None` | the indexes to build ([indexes.md](indexes.md)) |
| `Identity` | drawn per write | the sixteen bytes that name this version of the file |
| `Metadata` | empty | small values by key, read back through `file.Metadata` |
| `Durable` | `false` | makes a created or appended file reach the device before `CompleteAsync` returns, with everything the postscript names written before the postscript itself. It costs two flushes to the device per completion |

## Compression

The same million rows under each profile. The write time is the best of three, and the decode forces
every column of every batch to its plain form with `Canonical()`:

| profile | bytes | written in | decoded in | encodings |
|---|---|---|---|---|
| `Auto` | 1 508 316 | 34 ms | 1 ms | `Day` as runs, `Celsius` as a dictionary, `City` as runs |
| `Fastest` | 1 508 316 | 34 ms | 1 ms | the same |
| `Smallest` | 243 028 | 87 ms | 7 ms | `Day` as runs, `Celsius` and `City` as zstd, and pco on a chunk of `Day` and on the tail of `Celsius` |
| `None` | 20 929 828 | 25 ms | 4 ms | every column in its plain form |

Encoding is what makes the file fourteen times smaller than its plain form. `Auto` prices each
column's size and decode speed together. `Fastest` spends less time choosing and builds no index, and
on this data it chose the same encodings. `Smallest` prices bytes alone. It tries zstd, pco, FSST,
ALP and ALP-RD on every chunk, and on every array an encoding makes of it (an ALP's integers, a run's
values), and keeps the smallest: zstd for `Celsius` and `City`, a file six times smaller than
`Auto`'s, which a full decode pays for with a few milliseconds more.
[choose-encodings.md](choose-encodings.md) measures what each choice costs on ten million rows of
other shapes, and where the best choice changes.

## Hints

A hint names the encoding a column should try first, by path (dotted for a nested field). One hint at
a time, the rest left to the chooser:

| hint | bytes | written as |
|---|---|---|
| none | 1 508 316 | `Celsius` as a dictionary, `City` as runs |
| `City` as `Dictionary` | 1 551 708 | a dictionary |
| `City` as `Zstd` | 1 191 484 | zstd (runs on the 576-row tail) |
| `City` as `Fsst` | 4 919 828 | FSST |
| `City` as `Canonical` | 9 959 068 | the plain form |
| `Celsius` as `Alp` | 1 611 740 | ALP |
| `Celsius` as `Zstd` | 564 092 | zstd (ALP on the 576-row tail) |
| `Celsius` as `BitPacked` | 1 508 316 | a dictionary, since bit-packing does not apply to floats |
| `Celsius` as `Canonical` | 8 484 100 | the plain form |
| `Celsius` as `Canonical`, under `Smallest` | 8 164 052 | the plain form, with `Day` and `City` as `Smallest` writes them |

A hint is a preference, and the report is where you find out whether it held:

* A chunk holding a single value, or only nulls, is written as a constant, and an arithmetic
  progression is written as a sequence, whatever the hint says: nothing costs less per row. Runs
  are different. A hint comes before them, since runs cost something to decode, and the bitmap of a
  boolean column that is one percent true reads twelve times faster than its runs.
* Some shapes need no hint. A decimal whose values fit 64 bits is written as those integers
  (`DecimalByteParts`), which then get the integer schemes. A timestamp coarser than its unit
  (instants to the second stored in microseconds, dates at midnight) is split into days, seconds and
  subseconds (`DateTimeParts`). A column where nine rows in ten are null is stored as its valid rows
  at their positions (`Sparse`). A hint for another scheme still takes precedence on such a column,
  as you asked.
* `OnPair` is the hint for text made of repeated substrings, such as URLs, paths and log lines. It
  decodes as fast as FSST and, on such text, is a fifth to two fifths smaller. `Auto` picks it when it
  comes out a tenth smaller than both FSST and zstd.
* `Pco` is the hint for numbers of sixteen bits or more that bit-packing leaves wide: timestamps with
  jitter, counters, skewed amounts, multiples of a common unit. It entropy-codes the values after a
  delta or a common divisor when one pays. Two million event times in milliseconds take 1.7 MB as pco,
  against 5.7 MB bit-packed and 2.6 MB as zstd. `Smallest` tries it on every such chunk. `Auto` never
  does, like the reference's default compressor, because it decodes more slowly than bit-packing.
* A hint that does not apply falls back to what `Auto` would have chosen, as bit-packing does on
  floating-point values.
* `AlpRd` is the hint for floats with no short decimal form, measurements or ratios that `Alp`
  refuses. Their high bits go to a dictionary of eight entries and the rest are bit-packed. `Auto`
  keeps zstd on such a column whenever zstd saves a tenth of the bytes, since the file would grow
  otherwise. The split reads and writes several times faster than zstd for several times its bytes,
  which pays on a local disk and not over a slow network. `VortexSession.AdviseAsync` measures both on
  your data.
* Every profile honours the hint, `Smallest` included, and prices the columns that have none.
* `Auto` is not the smallest. Zstd shrank `Celsius` threefold and `City` by a fifth, but `Auto` weighs
  decode speed too, and a zstd block must be inflated whole before any of its values can be read. Use
  such a hint for a file written once and rarely read, and keep `Auto` for files scanned often. Better
  still, measure on your own data with `VortexSession.AdviseAsync`
  ([choose-encodings.md](choose-encodings.md#let-your-data-choose)).

## String bounds

`StringBoundBytes` sets how many bytes of a text column's minimum and maximum each zone keeps, so that
text filters can prune by block. On the same rows with the cities in eight runs of 125 000:

| `StringBoundBytes` | bytes | zone maps | `City == "Nice"` reads |
|---|---|---|---|
| 0 | 1 172 060 | 6 440 | 123 of 123 blocks |
| 16 | 1 174 436 | 8 752 | 20 of 123 blocks |
| 32 | 1 174 436 | 8 752 | 20 of 123 blocks |

The bounds cost 2 376 bytes here and cut the scan six times. 32 bytes changed nothing because no city
name is longer than 16. Longer bounds matter for values that share a long prefix, such as URLs.
Bounds only prune when the values come in runs: with the cities rotating every seven rows, every
block holds all of them and no bound can rule one out.

## Statistics, metadata and identity

* `Statistics = false` saved 224 bytes out of 1 508 316, and `file.Statistics.Count` is then 0. A
  question the statistics would have answered at open, such as a minimum or a count, then reads the
  zone maps or the data instead. The zone maps are kept, so `Day > 2000` still reads 0 of 123 blocks.
* `Metadata` holds at most 14 entries, with keys of at most 64 UTF-8 bytes, besides the library's
  own. `CreateWriter` refuses more: *A file carries at most 14 metadata entries besides its own; 15
  were given.* `file.Metadata.Keys` lists only the caller's entries, and `ReadAsync(key)` returns a
  copy of a value. An append keeps the file's entries, and an entry named again takes its new value.
* `Identity`, once pinned, makes the write reproducible: the same rows and options gave the same bytes
  twice, and a write without it gave different bytes. Otherwise each write draws a fresh identity,
  appends included.
* `TargetEdition` decides which readers can open the file, and which structures the writer may use.
  Under `core2025.05.0` this file is 1 544 268 bytes and carries no zone maps.
  [editions.md](editions.md) covers that subject.

## The options that are not the writer's

Reading has its own options. `VortexOpenOptions` (the tail read at open, the torn-tail policy, the
decompression ceiling, statistics verification, a schema for a file that embeds none, index
fragments) is described in [open-a-file.md](open-a-file.md), and its caps in [limits.md](limits.md).
`ScanOptions` (batch size, pruning, indexes, compaction, parallelism, read-ahead) belongs to a scan,
and the session's options (memory pool, segment cache, reads in flight, parallelism) are covered in
[threads.md](threads.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- writer-options
```

The figures above come from a run of every case, in which this one follows the others
([README.md](README.md)).
