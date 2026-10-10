# Choose encodings

What each compression profile and hint trades on common column shapes, and when the storage under a
file, or the way a column is read, makes another choice the better one. The measurements themselves
are on the benchmark page: [every profile and hint on twenty column shapes](benchmarks.md#encodings-column-by-column)
and [what the advice picks on them](benchmarks.md#what-the-advice-picks). This page says what they
mean for your choice.

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

Each shape is one column of ten million rows, written under each configuration, then opened and read.
A scan decodes every value to its plain form once, and a take reads a thousand rows spread over the
file. Each read opens the file anew and maps it again, so a larger file pays for its pages every
time, as a file opened once would. A column chooses its encoding chunk by chunk, so the volume only
scales the times.

The "crosses at" figure is the storage throughput at which a configuration and `Auto` read the whole
column in the same time, counting the bytes at that throughput plus the decode. Below it the smaller
file reads faster end to end, above it the faster decode wins. For reference, the page cache delivers
over 10 GB/s, a local NVMe drive 3 to 7 GB/s, a SATA SSD 0.5, a gigabit link 0.125, and one stream
from an object store about 0.1.

## Integers

Bit-packing, runs, sequences and dictionaries all scan in about the time of the plain column, within
a fifth either way, and runs and sequences even a third faster, since the plain column's eight bytes
per value have to be paged in. They also read a single row cheaply. Zstd frames are the exception:
an order of magnitude slower to scan, and far slower to take from unless the values come in runs. So
`Auto` never writes zstd where it is not the smallest form.

On increasing timestamps, pco is the smallest, 1.35 bytes per value against 3.38 for bit-packing,
and `Smallest` takes it, for a scan more than ten times as long. That is only worth it from storage
slower than its crossing point, about 300 MB/s.

A column whose values repeat across the file more than within a chunk, here 100 003 values over ten
million rows, only takes a dictionary in larger chunks: at 16 MiB it halves the bytes for the same
scan. `Smallest` writes it with pco instead, at a hundredth of the bytes for a scan ten times as long.
On nullable integers, a `Dictionary` hint saves a few percent of the bytes bit-packing leaves, for a
slower scan.

## Floating point

Prices take ALP, and columns with few distinct values take a dictionary, each scanning within a few
times the plain form. Uniform doubles take ALP-RD, which the plain form beats end to end from storage
faster than about 2 GB/s.

The repeating column deserves a second look. Its values come back every 100 003 rows, about as many
as a default chunk holds, so a dictionary would store nearly every value once per chunk. Zstd finds
the repeats anyway and is the smallest. In chunks of 16 MiB there are enough repeats for the
dictionary to pay: a scan and a take then cost a small fraction of what the zstd frames cost, for a
file a third larger.

## Text

Text is where the choice matters most. On values that do not repeat, `Auto` writes zstd frames, the
smallest form, and FSST scans in a fraction of their time. A take costs the frames about as much as a
scan, because each row it wants sits in a frame that must be inflated whole, while FSST reads one row
at a time. FSST makes the larger file here, but from storage faster than its crossing point, about
115 MB/s on the UUIDs and a few hundred MB/s on the log lines, it is also the faster file to scan. A
column read by row, or from a local drive, wants the `Fsst` hint.

The ids show the other lever. A dictionary of 10 000 entries stored again in every chunk is most of
what the column costs to read. In chunks of 16 MiB it is stored far fewer times, and the column gets
smaller, faster to scan and faster to write.

## Booleans

Booleans that are half true stay a bitmap. Rare trues take runs, smaller than the bitmap but slower to
scan, so from anything but slow storage the bitmap, which `None` keeps, reads faster.

## The profiles side by side

`Fastest` chooses what `Auto` chooses on these shapes, and writes no index. `Smallest` tries every
scheme on every chunk and writes several times slower than `Auto`, up to about eighteen times, for a
smaller file where one exists: pco on the timestamps and the repeating integers, and a chunk here and
there elsewhere. `None` writes the plain form, the largest file every time, eight bytes per number and
as many bytes as the text holds. It scans fastest only where decoding costs more than paging those
bytes in, which is the case for most doubles and for text that does not repeat. It scans about as
fast as packed integers, and slower than a dictionary of a few values. The benchmark page counts them
under [the encodings, column by column](benchmarks.md#encodings-column-by-column).

## What to steer, and with what

* The storage the bytes come from matters. Local drives and the page cache favour `Auto`, with the
  `Fsst` hint on text that does not repeat, while an object store read one stream at a time favours
  `Smallest`.
* So does the way a column is read. A column read by row, through a take, a key or a join, wants
  neither zstd nor a large dictionary: use `Fsst` for its text, and chunks of the default size.
* So does how often values repeat compared with the chunk. A column whose values come back across
  the file more often than within a chunk only takes a dictionary with larger chunks.
  `ColumnChunkTargetBytes` sets them for that column alone while the others keep theirs, which is
  what a selective read of the other columns fetches. `ChunkTargetBytes` sets them for the whole file
  ([blocks-and-chunks.md](blocks-and-chunks.md)).
* A hint cannot force what does not pay. It is tried first and falls back when it does not apply: a
  `Dictionary` hint on the repeating floats still gives zstd, because a chunk holds too few repeats for
  a dictionary to pay ([writer-options.md](writer-options.md)).
* All of it at once, on your data: the advice below measures these levers for your columns and your
  reads, and returns the options that set them.

## Let your data choose

The benchmark page measures twenty shapes on one machine. `VortexSession.AdviseAsync` makes the same
measurements on your data and your machine, and ranks every way of writing each column for the reads
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

Rows in memory are advised on the same way, with `session.AdviseAsync<Event>(events)`, which only
writes the rows it samples. For each column of numbers, booleans, text or binary, the advice:

* samples about a million rows (`SampleRows`, 1 048 576 by default), in eight windows spread over the
  data,
* writes them under the writer's own choice and under each hint the column's kind accepts, and also
  with 4 MiB and 16 MiB chunks when its values come back across the data more than within a chunk,
* reads each version back from memory, as a full scan and as single rows spread over its chunks,
* ranks them by the time a scan of the data would take: the decode, plus the bytes at
  `StorageBytesPerSecond`, plus for each lookup its decode and the chunk it reads. Setting
  `Objective` to `EncodingObjective.Size` ranks by bytes alone, under `Smallest`. The writer's own
  choice stands unless another is at least 5 % cheaper, and a larger chunk is taken only when it saves
  the column 5 %, in which case the writer gives it to that column alone.

`Reason` explains the choice in one sentence with the numbers. Each candidate carries its bytes, its
scan and lookup times, its cost, and the throughput at which it and the recommended one cross.
`ToWriteOptions` returns the hints, each column's chunk target and the profile, on top of a baseline
you provide.

[What the advice picks](benchmarks.md#what-the-advice-picks) on the benchmark page's twenty shapes
matches where the tables' crossing points put it. Since it compares every candidate with every other,
while the tables compare each one with `Auto`, it can find the plain form ahead of FSST for text
scanned from fast storage, the plain form being larger but faster to decode. Under the bytes alone, it
finds a dictionary in 16 MiB chunks for the columns that repeat across the file, and for rows read one
at a time it finds zstd, which reaches a row by inflating only the frame that holds it, or OnPair on
text.

Keep in mind that the advice is a measurement:

* A column takes seconds to measure. Run it once for a kind of data, keep the options, and run it
  again when the data or the machine changes. Under the JIT it first waits, up to 3 s per column, for
  the runtime to finish compiling the decoders it times, so a process whose other threads keep
  compiling is measured on the code it actually runs.
* The decode is timed on one thread. A scan decoding on several threads shares the storage between
  them, so pass `StorageBytesPerSecond` divided by the number of threads.
* A chunk target belongs to its column. A column that wants 16 MiB chunks gets them alone, through
  `ColumnChunkTargetBytes`, and a selective read of that column pays for them with larger reads.

## Watch out

* The benchmark page's times come from one machine, with the file in the page cache and mapped anew
  by each read. The bytes carry over to any machine, and so do the ratios between scans, but the
  crossing points scale with the machine's decode speed. A process that opens the same file again
  keeps its mapping (`MappedFileCacheCount`), and there the plain form, whose pages are already in
  memory, scans faster than the tables say.
* A take pays for every block it touches. The thousand rows measured touch every chunk of the file,
  which is the worst case for both a zstd frame and a large dictionary. Rows that sit together cost
  far less.
* The report says what each chunk became: `WriteReport.Columns[i].Encodings`, like the *written as*
  column of the benchmark page's tables.

## Run it

The benchmark page's two sections are regenerated by:

```
DOTNET_TieredCompilation=0 dotnet run -c Release --project bench/Vorticity.Benchmarks -- --tradeoffs --out docs/guide/benchmarks.md
DOTNET_TieredCompilation=0 dotnet run -c Release --project bench/Vorticity.Benchmarks -- --tradeoffs --advise --out docs/guide/benchmarks.md
```

`--rows N` changes the volume, and words after it select columns by name. For example,
`-- --tradeoffs --rows 1000000 utf8` runs the text columns on a million rows and prints their tables
without touching the page. `--advise` runs the advice on each column under the five goals and prints
the choice and its reason.
