# Choose encodings

What each compression profile and hint trades on common column shapes, and where the storage under a
file, or the way a column is read, makes another choice the better one. The measurements are on the
benchmark page: [every profile and hint on twenty column shapes](benchmarks.md#encodings-column-by-column)
and [what the advice picks on them](benchmarks.md#what-the-advice-picks). This page says what they
mean for a choice.

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

## Reading the measurements

Each shape is one column of ten million rows, written under each configuration, then opened and
read: a **scan** decodes every value to its plain form and reads it once, a **take** reads a thousand
rows spread over the file. Each read opens the file anew and maps it again, so a larger file pays for
its pages every time, as a file opened once does. A column chooses its encoding chunk by chunk, so the
volume only scales the times.

**Crosses at** is the storage throughput at which a configuration and `Auto` read the column whole
in the same time, counting its bytes at that throughput and then its scan. Below it the smaller file
reads faster end to end; above it the faster decode does. For reference: the page cache delivers
over 10 GB/s, a local NVMe drive 3 to 7, a SATA SSD 0.5, a gigabit link 0.125, and one stream from an
object store about 0.1.

## Integers

Bit-packing, runs, a progression and a dictionary each scan in about the time of the plain column,
within a fifth either way and a third faster for runs and a progression, the plain column's eight
bytes a value having to be paged in; and they take a row as cheaply. Zstd
frames are the exception, an order of magnitude slower to scan and far slower to take from where the
values do not come in runs: where zstd is not the smallest form, `Auto` never writes it. On
increasing timestamps Pco is the smallest, 1.35 bytes a value against bit-packing's 3.38, and
`Smallest` takes it, for a scan more than ten times as long: worth it only from storage slower than
its crossing, about 300 MB/s.

A column whose values repeat across the file more than within a chunk, 100 003 values over ten
million rows, takes a dictionary only in larger chunks: at 16 MiB it halves the bytes for the same
scan. `Smallest` writes it with Pco instead, a hundredth of the bytes for a scan ten times as long.
On the nullable integers a `Dictionary` hint saves a few percent of the bytes bit-packing leaves, for
a slower scan.

## Floating point

Prices take ALP, and a few distinct values a dictionary, each within a few times the plain scan.
Uniform doubles take ALP-RD, which the plain form beats end to end from storage faster than about
2 GB/s.

The repeating column is the one to look at twice. Its values come back every 100 003 rows, about as
many as a chunk of the default size holds: a dictionary would store nearly every value once per
chunk, so zstd, which finds the repeats anyway, is the smallest. Chunks of 16 MiB hold enough repeats
for the dictionary to pay: a scan and a take then cost a small fraction of the frames', for a file a
third larger.

## Text

Text is where the choice matters most. On values that do not repeat, `Auto` writes zstd frames, the
smallest form, and FSST then scans in a fraction of their time; a take costs the frames about what a
scan costs them, because each row it wants sits in a frame that is inflated whole, where FSST reads
a row at a time. FSST is the larger file here: from storage faster than its crossing, under 100 MB/s
on the UUIDs and a few hundred on the log lines, it is also the faster file to scan. A column read by
row, or from a local drive, wants the `Fsst` hint.

The ids show the other lever. A dictionary of 10 000 entries stored again in every chunk is most of
what the column costs to read; in chunks of 16 MiB it is stored far fewer times, and the column is
smaller, faster to scan and faster to write.

## Booleans

Booleans half true stay a bitmap. Rare trues take runs, smaller than the bitmap and slower to scan:
from anything but slow storage the bitmap, which `None` keeps, reads faster.

## The profiles side by side

`Fastest` chooses what `Auto` chooses on these shapes, and writes no index. `Smallest` tries every
scheme on every chunk and writes several times slower than `Auto`, up to fifteen, for a smaller file
where one exists: Pco on the timestamps and the repeating integers, a chunk here and there. `None`
writes the plain form, the largest file every time, 8 bytes a value for a number and as many as the
text holds; it scans fastest only where decoding costs more than paging those bytes in, on most
doubles and on text that does not repeat, about as fast as the packed integers, and slower than a
dictionary of a few values. The benchmark page counts them under
[the encodings, column by column](benchmarks.md#encodings-column-by-column).

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
  `Dictionary` hint on the repeating floats still gives zstd, because a chunk holds too few repeats
  for a dictionary to pay ([writer-options.md](writer-options.md)).
* **All of it at once, on your data**: the advice below measures these levers for your columns and
  your reads, and returns the options that pull them.

## Let your data choose

The benchmark page measures twenty shapes on one machine. `VortexSession.AdviseAsync` makes the same
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

[What the advice picks](benchmarks.md#what-the-advice-picks) on the benchmark page's twenty shapes
falls where the tables' crossings put it, and compares every candidate with every other, where the
tables compare each with `Auto`. So it can find the plain form ahead of FSST for text scanned from
fast storage, the plain form being larger and faster to decode; under the bytes alone, a dictionary
in 16 MiB chunks for the columns that repeat across the file; and for rows read one at a time, zstd,
which reaches a row by inflating only the frame that holds it, or OnPair on text.

* **It is a measurement.** A column costs seconds: run it once for a kind of data, keep the
  options, and run it again when the data or the machine changes. Under the JIT, it first waits for
  the runtime to finish compiling the decoders it times, up to 3 s a column; a process whose other
  threads keep compiling is measured on the code it runs then.
* **The decode is timed on one thread.** A scan decoding on several shares the storage between
  them: give `StorageBytesPerSecond` divided by the threads.
* **A chunk target is the column's.** A column that wants 16 MiB chunks gets them alone, through
  `ColumnChunkTargetBytes`, and a selective read of that column pays for them in larger reads.

## Watch out

* **The benchmark page's times are one machine's**, with the file in the page cache and mapped anew
  by each read. The bytes carry to any machine, and so do the ratios between the scans; where a
  choice crosses scales with the machine's decode speed. A process that opens the same file again
  keeps its mapping (`MappedFileCacheCount`), and there the plain form, whose pages are already in,
  scans faster than these tables say.
* **A take pays per block it touches.** The thousand rows measured touch every chunk of the file,
  which is the worst case for a frame and a large dictionary alike; rows that sit together cost far
  less.
* **The report says what each chunk became**: `WriteReport.Columns[i].Encodings`, as the *written
  as* column of the benchmark page's tables.

## Run it

The benchmark page's two sections are regenerated by

```
DOTNET_TieredCompilation=0 dotnet run -c Release --project bench/Vorticity.Benchmarks -- --tradeoffs --out docs/guide/benchmarks.md
DOTNET_TieredCompilation=0 dotnet run -c Release --project bench/Vorticity.Benchmarks -- --tradeoffs --advise --out docs/guide/benchmarks.md
```

`--rows N` changes the volume, and words after it pick columns by name: `-- --tradeoffs --rows
1000000 utf8` runs the text columns on a million rows and prints their tables without touching the
page. `--advise` runs the advice on each column under the five goals, with the choice and its
reason.
