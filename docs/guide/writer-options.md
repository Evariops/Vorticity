# Writer options

Choose how the writer encodes, what it records beside the data, and what the file carries, then
read in the report what it did.

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
Day: RunEnd x16, Sequence
Celsius: Zstd x16, Alp
City: RunEnd x17
metadata keys producer; producer = acme/1.4
identity 0199f0c4-7d2a-7c3e-9a51-3f6b2c1d4e5f, edition core2026.08.0, 564052 bytes
written again with the same identity: the same bytes; without one: different bytes
```

`VortexWriteOptions` is a record with `init` properties: set what you mean to change, and `with`
makes a variant. The options are checked at `CreateWriter`: a hint, an index or a metadata entry that
does not fit throws `ArgumentException` there, before a byte is written.

| option | default | what it changes |
|---|---|---|
| `BlockRows` | 8 192 | rows per block: the unit of pruning, of a take and of a filtered scan's batch ([blocks-and-chunks.md](blocks-and-chunks.md)) |
| `ChunkTargetBytes` | 0, the writer's | the bytes gathered before whole blocks are sealed into a chunk; by default a megabyte of the widest column ([blocks-and-chunks.md](blocks-and-chunks.md)) |
| `ColumnChunkTargetBytes` | empty | a chunk target of its own, by top-level column, for a column that wants larger chunks than the file's: its chunks span whole chunks of the file ([blocks-and-chunks.md](blocks-and-chunks.md)) |
| `Compression` | `Auto` | what the encoder optimises for: `Auto`, `Fastest`, `Smallest`, `None` |
| `Hints` | empty | an encoding to try first, by column path |
| `TargetEdition` | `VortexEditions.Default` | the edition every component must belong to ([editions.md](editions.md)) |
| `Statistics` | `true` | the per-column minimum, maximum, sum, null count and order the open reads back |
| `StringBoundBytes` | 16 | the length of the bounds a text or binary column's zones carry; 0 for none |
| `Indexes` | `IndexPolicy.None` | the indexes to build ([indexes.md](indexes.md)) |
| `Identity` | drawn per write | the sixteen bytes naming this version of the file |
| `Metadata` | empty | small values by key, read back through `file.Metadata` |

## Compression

The same million rows, under each profile. The write is the best of three; the decode forces every
column of every batch to its plain form with `Canonical()`:

| profile | bytes | written in | decoded in | encodings |
|---|---|---|---|---|
| `Auto` | 1 508 212 | 33 ms | 1 ms | `Day` runs, `Celsius` a dictionary, `City` runs |
| `Fastest` | 1 508 212 | 33 ms | 1 ms | the same |
| `Smallest` | 247 124 | 63 ms | 2 ms | `Day` runs, `Celsius` and `City` zstd |
| `None` | 20 929 820 | 28 ms | 4 ms | every column `Canonical`, the plain form |

Encoding is what makes the file fourteen times smaller than its plain form. `Auto` prices each
column's size and decode speed together; `Fastest` spends less time choosing and builds no index,
and on this data chose the same encodings; `Smallest` prices bytes alone, tries zstd, FSST, ALP and
ALP-RD on every chunk and keeps the smallest: zstd for `Celsius` and `City`, a file six times
smaller than `Auto`'s, which a full decode pays for with a millisecond more.
[choose-encodings.md](choose-encodings.md) measures what each choice costs on ten million rows of
other shapes, and where it turns.

## Hints

A hint names the encoding a column should try first, by path (dotted for a nested field). One hint
at a time, the rest left to the chooser:

| hint | bytes | written as |
|---|---|---|
| none | 1 508 212 | `Celsius` a dictionary, `City` runs |
| `City` as `Dictionary` | 1 551 604 | a dictionary |
| `City` as `Zstd` | 1 191 380 | zstd (runs on the 576-row tail) |
| `City` as `Fsst` | 7 393 428 | FSST |
| `City` as `Canonical` | 9 958 964 | `Canonical`, the plain form |
| `Celsius` as `Alp` | 1 611 636 | ALP |
| `Celsius` as `Zstd` | 563 988 | zstd (ALP on the 576-row tail) |
| `Celsius` as `BitPacked` | 1 508 212 | a dictionary: bit-packing does not apply to floats |
| `Celsius` as `Canonical` | 8 483 996 | `Canonical`, the plain form |
| `Celsius` as `Canonical`, under `Smallest` | 8 167 092 | `Canonical`; `Day` and `City` as `Smallest` writes them |

A hint is a preference, and the report is where you find out whether it held:

* **A column that is a progression is written as one whatever the hint says**: nothing costs less
  per row. Runs are not: a hint comes before them, since runs cost to decode, and the bitmap of a
  column one percent true reads twelve times faster than its runs.
* **A hint that does not apply falls back** to what `Auto` would have chosen, as bit-packing on
  floating-point values does.
* **Every profile takes the hint**, `Smallest` included, and prices the columns without one.
* `Auto` is not the smallest: zstd shrank `Celsius` threefold, and `City` by a fifth. `Auto` weighs
  decode speed too, and a zstd block must be inflated whole before one value is read. Take such a
  hint for a file written once and read rarely; leave `Auto` for one scanned often, and measure on
  your own data: `VortexSession.AdviseAsync` does
  ([choose-encodings.md](choose-encodings.md#let-your-data-choose)).

## String bounds

`StringBoundBytes` sets how many bytes of a text column's minimum and maximum each zone keeps, so
that text filters prune by block. On the same rows with the cities in eight runs of 125 000:

| `StringBoundBytes` | bytes | zone maps | `City == "Nice"` reads |
|---|---|---|---|
| 0 | 1 173 236 | 6 440 | 123 of 123 blocks |
| 16 | 1 175 612 | 8 752 | 20 of 123 blocks |
| 32 | 1 175 612 | 8 752 | 20 of 123 blocks |

Bounds cost 2 376 bytes here and cut the scan six times. 32 bytes changed nothing because no city
name is longer than 16; longer bounds matter for values that share a long prefix, such as URLs.
Bounds prune only when the values come in runs: with the cities rotating every seven rows, every
block holds all of them and no bound can rule one out.

## Statistics, metadata, identity

* **`Statistics = false`** saved 224 bytes of 1 508 212, and `file.Statistics.Count` is then 0: a
  question the statistics would answer at open, such as a minimum or a sum, reads the zone maps or
  the data instead. The zone maps are kept, so `Day > 2000` still reads 0 of 123 blocks.
* **`Metadata`** holds at most 14 entries, with keys of at most 64 UTF-8 bytes, besides the library's
  own; `CreateWriter` refuses more: *A file carries at most 14 metadata entries besides its own; 15
  were given.* `file.Metadata.Keys` lists the caller's entries only, and `ReadAsync(key)` returns a
  copy of the value. An append keeps the file's entries, and an entry named
  again takes its new value.
* **`Identity`** pinned makes the write reproducible: the same rows and options gave the same bytes
  twice, and a write without it gave different bytes. Each write draws a fresh identity otherwise,
  appends included.
* **`TargetEdition`** decides which readers can open the file, and which structures the writer may
  use: under `core2025.05.0` this file is 1 544 292 bytes and carries no zone maps. [editions.md](editions.md)
  is that subject.

## The options that are not the writer's

Reading has its own options, apart from these. `VortexOpenOptions` (the tail read at open, the
torn-tail policy, the decompression ceiling, whether statistics are verified, a schema for a file
that embeds none, index fragments) is described in [open-a-file.md](open-a-file.md),
and its caps in [limits.md](limits.md). `ScanOptions` (batch size, pruning, indexes, compaction,
parallelism, read-ahead) belongs to a scan, and the session's options (memory pool, segment cache,
reads in flight, parallelism) to [threads.md](threads.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- writer-options
```

The figures above come from that run.
