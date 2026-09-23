# Choose encodings

What each compression profile and hint costs on ten million rows of common column shapes, and where
the storage under a file, or the way a column is read, makes another choice the better one.

```csharp
// The default: each chunk of each column takes the smallest encoding that decodes fast.
VortexWriteOptions scanned = new();

// A file read rarely, or over a slow link: the smallest encoding, whatever it costs to decode.
VortexWriteOptions archived = new() { Compression = CompressionProfile.Smallest };

// Text read row by row, or from fast storage: FSST, which a take reads a row at a time,
// where a zstd frame is inflated whole.
VortexWriteOptions lookedUp = new()
{
    Hints = ImmutableDictionary<string, EncodingHint>.Empty.Add("Message", EncodingHint.Fsst),
};

// A column whose values repeat across the file more than within a chunk: larger chunks of its
// own let a dictionary pay for its entries, and the other columns keep theirs.
VortexWriteOptions repeating = new()
{
    ColumnChunkTargetBytes = ImmutableDictionary<string, int>.Empty.Add("CustomerId", 16 << 20),
};
```

## How the figures were made

One column of 10 000 000 rows per file, written under each configuration, then opened and read. A
**scan** decodes every value to its plain form and reads it once; a **take** reads 1 000 rows spread
over the file. Each figure is the median of three passes after a warm-up, on an Apple M4 Pro, with
the file in the page cache. The volume only scales them: a column chooses its encoding chunk by
chunk, so a million rows cost a tenth of these times and take the same encodings.

**Crosses at** is the storage throughput at which a configuration and `Auto` read the column whole
in the same time, counting its bytes at that throughput and then its scan. Below it the smaller file
reads faster end to end; above it the faster decode does. For reference: the page cache delivers
over 10 GB/s, a local NVMe drive 3 to 7, a SATA SSD 0.5, a gigabit link 0.125, and one stream from an
object store about 0.1.

## Integers

| shape | `Auto` writes | B/value | scan | take | worth knowing |
|---|---|---:|---:|---:|---|
| a sequence | a progression | 0.00 | 3.4 ms | 1.1 ms | |
| sorted, runs of 1 000 | runs | 0.01 | 3.7 ms | 1.4 ms | |
| timestamps: milliseconds, increasing, jittered | bit-packing | 3.38 | 8.1 ms | 2.0 ms | zstd, what `Smallest` writes: 2.07 B/value, scan 61 ms, take 59 ms; crosses at 250 MB/s |
| random in 0..999 | bit-packing | 1.25 | 5.6 ms | 1.6 ms | |
| 16 distinct, random order | a dictionary | 0.51 | 5.8 ms | 1.5 ms | |
| 100 003 distinct, repeating | bit-packing | 4.63 | 7.1 ms | 1.6 ms | 16 MiB chunks: a dictionary, 2.36 B/value, scan 7.6 ms |
| uniform over 64 bits | the plain form | 8.00 | 5.8 ms | 1.3 ms | |
| random in 0..999, one row in ten null | bit-packing | 1.38 | 7.0 ms | 1.7 ms | a `Dictionary` hint: 1.27 B/value, scan 8.1 ms |

The plain column scans in 5.8 ms, and every form above within one and a half times of it. A zstd
frame is the exception, at 60 to 90 ms a column: where it is not the smallest, as on every integer
shape but the timestamps, `Auto` never writes one.

## Floating point

| shape | `Auto` writes | B/value | scan | take | worth knowing |
|---|---|---:|---:|---:|---|
| prices, two decimals | ALP | 2.50 | 8.6 ms | 1.8 ms | |
| 16 distinct, random order | a dictionary | 0.51 | 5.7 ms | 1.4 ms | |
| 1 000 distinct prices | a dictionary | 1.28 | 6.6 ms | 1.9 ms | |
| 100 003 distinct, repeating | zstd | 1.69 | 62 ms | 62 ms | 16 MiB chunks: a dictionary, 2.21 B/value, scan 10.4 ms, take 4.9 ms; crosses at 103 MB/s |
| uniform in [0, 1) | ALP | 6.91 | 14.8 ms | 4.0 ms | the plain form: 8.00 B/value, scan 7.4 ms; crosses at 1.5 GB/s |

The repeating column is the one to look at twice. Its values come back every 100 003 rows, and a
chunk of the default size holds 131 072 of them: a dictionary would store nearly every value once
per chunk, so zstd, which finds the repeats anyway, is the smallest. Chunks of 16 MiB hold enough
repeats for the dictionary to pay, and it then scans six times faster and takes twelve times faster
than the frames, for a file 31 % larger.

## Text

| shape | `Auto` writes | B/value | scan | take | worth knowing |
|---|---|---:|---:|---:|---|
| 16 city names, random order | a dictionary | 0.51 | 8.9 ms | 1.7 ms | |
| 10 000 distinct ids | a dictionary | 2.54 | 39 ms | 34 ms | 16 MiB chunks: 1.80 B/value, scan 12.7 ms, take 9.3 ms, written faster |
| the same, one row in ten null | a dictionary | 2.45 | 43 ms | 36 ms | 16 MiB chunks: 1.80 B/value, scan 14.3 ms, take 9.1 ms |
| unique UUIDs | zstd | 20.61 | 335 ms | 335 ms | FSST: 24.91 B/value, scan 122 ms, take 4.3 ms; crosses at 200 MB/s |
| log lines, about 100 bytes | zstd | 13.71 | 421 ms | 344 ms | FSST: 23.96 B/value, scan 136 ms, take 5.0 ms; crosses at 360 MB/s |

Text is where the choice matters most. On values that do not repeat, `Auto` writes zstd frames, the
smallest form, and a scan then costs three times what FSST costs; a take costs as much as the scan,
because each row it wants sits in a frame that is inflated whole. FSST is 21 to 75 % larger here and
reads a row at a time: from anything faster than 200 to 360 MB/s it is also the faster file to scan,
and it takes seventy to eighty times faster. A column read by row, or from a local drive, wants the
`Fsst` hint.

The ids show the other lever. A dictionary of 10 000 entries stored again in each of 306 chunks is
most of what the column costs to read; in chunks of 16 MiB it is stored eighteen times, and the
column is smaller, faster to scan and faster to write.

## Booleans

| shape | `Auto` writes | B/value | scan | take | worth knowing |
|---|---|---:|---:|---:|---|
| half true | a bitmap | 0.13 | 0.2 ms | 0.7 ms | |
| 1 % true | runs | 0.05 | 2.4 ms | 5.7 ms | the bitmap, `None`: 0.13 B/value, scan 0.2 ms, take 0.7 ms; crosses at 340 MB/s |

## The profiles side by side

On these twenty columns, `Fastest` chose what `Auto` chose every time, and wrote no index. `Smallest`
chose the same as well, but for a chunk here and there and the timestamps, where zstd saves 39 % for
a scan seven times slower; it wrote two to six times slower than `Auto`, since it tries every
scheme on every chunk: 3.2 s for the ten million log lines against 1.5 s. `None` writes the plain form, which scans
fastest when the storage is fast enough to deliver its bytes, 8 per value for a number and as many
as the text holds, and is the largest file every time.

## What to steer, and with what

* **Where the bytes come from.** Local drives and the page cache favour `Auto`, with the `Fsst`
  hint on text that does not repeat. An object store read one stream at a time favours `Smallest`.
* **How a column is read.** A column read by row, through a take, a key or a join, wants neither
  zstd nor a large dictionary: `Fsst` for its text, and chunks of the default size.
* **How often values repeat, against the chunk.** A column whose values come back across the file
  more often than within a chunk takes a dictionary only with larger chunks. `ColumnChunkTargetBytes`
  sets them for that column alone, and the others keep theirs, which is what a selective read of
  them fetches; `ChunkTargetBytes` sets them for the whole file
  ([blocks-and-chunks.md](blocks-and-chunks.md)).
* **What a hint cannot do.** A hint is tried first and falls back when it does not apply: a
  `Dictionary` hint on the repeating floats still gave zstd, because a chunk held too few repeats
  for a dictionary to pay ([writer-options.md](writer-options.md)).
* **All of it at once, on your data**: the advice below measures these levers for your columns and
  your reads, and returns the options that pull them.

## Let your data choose

The tables above measure twenty shapes on one machine. `VortexSession.AdviseAsync` makes the same
measurements on your data and your machine, and ranks every way to write each column for the reads
you describe:

```csharp
await using VortexFile file = await session.OpenAsync("events.vortex");
EncodingAdvice advice = await session.AdviseAsync(file, new EncodingGoal
{
    StorageBytesPerSecond = 100_000_000,   // one stream from an object store
    LookupsPerScan = 10_000,               // rows read one at a time, for each whole scan
});

foreach (ColumnEncodingAdvice column in advice.Columns)
{
    Console.WriteLine($"{column.Path}: {column.Reason}");
}

VortexWriteOptions options = advice.ToWriteOptions();
```

Rows in memory are advised on the same way, `session.AdviseAsync<Event>(events)`, which writes only
the rows it samples. Per column of numbers, booleans, text or binary, the advice:

* **samples** a million rows, `SampleRows`, in eight windows spread over the data;
* **writes** them under the writer's own choice and under each hint the column's kind takes, and
  at 4 and 16 MiB chunks when its values come back across the data more than within a chunk, on
  rows enough for whole chunks of that size;
* **reads** each back from memory: a full scan, and single rows spread over its chunks;
* **ranks** them by the time a scan of the data would take: the decode, the bytes at
  `StorageBytesPerSecond`, and for each lookup its decode and the chunk it reads. `Objective.Size`
  ranks by bytes alone, under `Smallest`. The writer's own choice stands unless another is 5 %
  cheaper, and a larger chunk is taken only when it saves the column 5 %: the writer gives it to
  that column alone.

`Reason` says why in one sentence with the numbers. Each candidate carries its bytes, its scan and
lookup times, its cost, and the throughput at which it and the recommended one cross.
`ToWriteOptions` returns the hints, each column's chunk target and the profile, over a baseline of
yours.

On the twenty shapes above, the advice departs from `Auto` here and nowhere else:

| shape | scans at 2 GB/s | at 100 MB/s | at 10 GB/s | a row in 1 000 read by row | the bytes |
|---|---|---|---|---|---|
| timestamps | | `Zstd` | | | |
| 100 003 distinct integers, repeating | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | | 16 MiB chunks |
| integers in 0..999, one in ten null | | `Dictionary` | | `Dictionary` | `Dictionary` |
| 100 003 distinct floats, repeating | 16 MiB chunks | | `Canonical` | `Canonical` | |
| uniform floats | | | `Canonical` | | |
| 10 000 distinct ids, with or without nulls | 16 MiB chunks | 16 MiB chunks | 16 MiB chunks | | 16 MiB chunks |
| unique UUIDs | `Canonical` | | `Canonical` | `Fsst` | |
| log lines | `Fsst` | | `Canonical` | `Fsst` | |
| booleans, 1 % true | `Canonical` | | `Canonical` | | |

It falls where the tables' crossings put it, and it compares every candidate with every other,
where the tables compare each with `Auto`. So it finds the plain form ahead of FSST for UUIDs read
at 2 GB/s: 36 bytes a value, read in 18 ns and decoded in 3, against 25 bytes read in 12.5 ns and
decoded in 12; the two cross at 1.3 GB/s. And under the bytes alone it finds the dictionary that
saves 8 % on the nullable integers, which `Smallest` alone does not take.

* **It is a measurement.** A column costs 0.3 to 3.5 s on the machine above: run it once for a
  kind of data, keep the options, and run it again when the data or the machine changes. Under the
  JIT, it first waits for the runtime to finish compiling the decoders it times, up to 3 s a
  column; a process whose other threads keep compiling is measured on the code it runs then.
* **The decode is timed on one thread.** A scan decoding on several shares the storage between
  them: give `StorageBytesPerSecond` divided by the threads.
* **A chunk target is the file's.** A column that wants 16 MiB chunks gets them for every column,
  which the advice weighs over all of them, and which a selective read pays for in larger reads.

## Watch out

* **These are one machine's times**, with the file in the page cache. The bytes carry to any
  machine, and so do the ratios between the scans; where a choice crosses scales with the machine's
  decode speed.
* **A take pays per block it touches.** The thousand rows above touch every chunk of the file, which
  is the worst case for a frame and a large dictionary alike; rows that sit together cost far less.
* **The report says what each chunk became**: `WriteReport.Columns[i].Encodings`, as the second
  column of the tables above.

## Run it

```
DOTNET_TieredCompilation=0 dotnet run -c Release --project bench/Vorticity.Benchmarks -- --tradeoffs
```

`--rows N` changes the volume, and words after it pick columns by name: `-- --tradeoffs --rows
1000000 utf8` runs the text columns on a million rows. It writes its tables in Markdown; the figures
above come from it, the 16 MiB chunks from its columns named `distinct`. `--advise` runs the advice
on each column instead, under the five goals of the table above, with the choice and its reason.
