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
Day: RunEnd x31, Sequence
Celsius: Zstd x31, Alp
City: RunEnd x32
metadata keys producer; producer = acme/1.4
identity 0199f0c4-7d2a-7c3e-9a51-3f6b2c1d4e5f, edition core2026.08.0, 499196 bytes
written again with the same identity: the same bytes; without one: different bytes
```

`VortexWriteOptions` is a record with `init` properties: set what you mean to change, and `with`
makes a variant. The options are checked at `CreateWriter`: a hint, an index or a metadata entry that
does not fit throws `ArgumentException` there, before a byte is written.

| option | default | what it changes |
|---|---|---|
| `BlockRows` | 8 192 | rows per block: the unit of pruning, of a take and of a batch ([blocks-and-chunks.md](blocks-and-chunks.md)) |
| `ChunkTargetBytes` | 1 MiB | the bytes gathered before whole blocks are sealed into a chunk |
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
| `Auto` | 1 522 396 | 68 ms | 10 ms | `Day` runs, `Celsius` a dictionary, `City` runs |
| `Fastest` | 1 522 396 | 52 ms | 8 ms | the same |
| `Smallest` | 1 290 036 | 80 ms | 8 ms | `Day` and `City` zstd, `Celsius` ALP |
| `None` | 20 848 260 | 60 ms | 5 ms | none |

Encoding is what makes the file fourteen times smaller than its plain form. `Auto` prices each
column's size and decode speed together; `Fastest` spends less time choosing and builds no index,
and on this data chose the same encodings; `Smallest` prices bytes alone and tries zstd, FSST and ALP
on every chunk. It is not always the smallest there is: a zstd hint on `Celsius` under `Auto` gives
499 196 bytes, where `Smallest` chose ALP for that column.

## Hints

A hint names the encoding a column should try first, by path (dotted for a nested field). One hint
at a time, the rest left to the chooser:

| hint | bytes | written as |
|---|---|---|
| none | 1 522 396 | `Celsius` a dictionary, `City` runs |
| `City` as `Dictionary`, `Zstd`, `Fsst` or `Canonical` | 1 522 396 | `City` runs, every time |
| `Celsius` as `Alp` | 1 605 660 | ALP |
| `Celsius` as `Zstd` | 499 132 | zstd (ALP on the 576-row tail) |
| `Celsius` as `BitPacked` | 1 522 396 | a dictionary: bit-packing does not apply to floats |
| `Celsius` as `Canonical` | 8 476 100 | none |
| `Celsius` as `Canonical`, under `Smallest` | 1 290 036 | ALP |

A hint is a preference, and the report is where you find out whether it held:

* **A column that is a progression or made of long runs is written that way whatever the hint
  says**, `Canonical` included: those verdicts come before the hint. `City` changes every seven
  rows, so every hint on it gave runs.
* **A hint that does not apply falls back** to what `Auto` would have chosen, as bit-packing on
  floating-point values does.
* **`Smallest` does not consult hints.** It prices every candidate by its bytes.
* `Auto` is not the smallest: zstd shrank `Celsius` threefold. `Auto` weighs decode speed too, and a
  zstd block must be inflated whole before one value is read. Take such a hint for a file written
  once and read rarely; leave `Auto` for one scanned often, and measure on your own data.

## String bounds

`StringBoundBytes` sets how many bytes of a text column's minimum and maximum each zone keeps, so
that text filters prune by block. On the same rows with the cities in eight runs of 125 000:

| `StringBoundBytes` | bytes | zone maps | `City == "Nice"` reads |
|---|---|---|---|
| 0 | 1 207 836 | 6 440 | 123 of 123 blocks |
| 16 | 1 210 212 | 8 752 | 20 of 123 blocks |
| 32 | 1 210 212 | 8 752 | 20 of 123 blocks |

Bounds cost 2 376 bytes here and cut the scan six times. 32 bytes changed nothing because no city
name is longer than 16; longer bounds matter for values that share a long prefix, such as URLs.
Bounds prune only when the values come in runs: with the cities rotating every seven rows, every
block holds all of them and no bound can rule one out.

## Statistics, metadata, identity

* **`Statistics = false`** saved 224 bytes of 1 522 396, and `file.Statistics.Count` is then 0: a
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
  use: under `core2025.05.0` this file is 1 551 068 bytes and carries no zone maps. [editions.md](editions.md)
  is that subject.

## The options that are not the writer's

Reading has its own options, apart from these. `VortexOpenOptions` (the tail read at open, the
torn-tail policy, the decompression ceiling, whether statistics are verified, unknown components, a
schema for a file that embeds none, index fragments) is described in [open-a-file.md](open-a-file.md),
and its caps in [limits.md](limits.md). `ScanOptions` (batch size, pruning, indexes, compaction,
parallelism, read-ahead) belongs to a scan, and the session's options (memory pool, segment cache,
reads in flight, parallelism) to [threads.md](threads.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- writer-options
```

The figures above come from that run.
