# Anticipated decisions: the read path from the plan, the encodings from the data

Two choices this library made one way for every file are better made from what is known before
the work starts. How to read a file's bytes: a scan's plan knows how many it will read before it
reads one. How to encode a column: a sample of the column says what it holds before a file is
written. This document specifies both: part A, the read path a scan chooses from its plan; part B,
an advisor that analyses the data to be encoded and proposes, per column, the choices that suit
its content and the reads it will serve.

The principle is the same on both sides, and it is the one the measurements behind this document
kept pointing at: a choice with a trade-off is taken where the numbers that decide it are known,
before the work, and it is not revised in mid-course. The writer keeps what
[11-write-strategy.md](11-write-strategy.md) §4 guarantees: it is exact, deterministic, and samples
nothing. The advisor samples, and what it produces is options, which the writer then applies
exactly as it applies any others.

---

## Part A — The read path from the plan

### A.1 What was measured

On an Apple M4 Pro, with the files in the page cache, medians of 15 rounds of 200 opens in the
Native AOT runner (2026-09-23):

| per open | mapped | positional |
|---|---:|---:|
| the report's mixed file, 1M rows, 3.2 MB | 58.3 µs | 51.5 µs |
| the report's mixed file, 10M rows, 32 MB | 62.9 µs | 52.9 µs |
| a corpus file of 4 KB | 38.4 µs | 34.4 µs |

An open reads the file's last 64 KiB and nothing else, and mapping the whole file for that — the
`mmap`, the faults on the pages the tail touches, the `munmap` at close — costs more than one
positional read. A direct `mmap` through P/Invoke costs what .NET's mapping costs, within a
microsecond, so the mapping is kept as .NET provides it.

Per read, from a 32 MB file (µs): positional into a pooled buffer, a mapping made for the read and
dropped after, a read from a mapping already made.

| bytes | positional | new mapping | mapping held |
|---:|---:|---:|---:|
| 4 KiB | 0.74 | 20.1 | 0.17 |
| 64 KiB | 3.7 | 18.8 | 2.2 |
| 1 MiB | 75 | 101 | 34 |
| 4 MiB | 323 | 327 | 135 |
| 16 MiB | 1 436 | 1 283 | 540 |

A new mapping costs about 16 µs, then about 0.8 µs for each 16 KiB page touched a first time,
which is the price of a copy: it catches up with positional reads at about 4 MiB. A mapping held
reads 2 to 2.7 times faster than a positional read at every size.

### A.2 The rule

A file opened from a path is read positionally until a scan's plan says it will read data, and
mapped from then on.

- **The open** reads the tail positionally, into a pooled buffer the file keeps, as it kept the
  mapped view.
- **A scan** asks, once its plan is settled and before its first data read, whether the plan reads
  any data at all (§A.3). If it does, the file is mapped; the structures read before that — the
  zone maps a filter prunes with, an index directory — are read positionally.
- **The mapping is the file's**: made once, by the first scan that asks, and kept until the file
  is disposed and the last lease on it released. Every read after it, of any size, is served from
  it: a mapping held is the cheapest read there is.

Why any data, and not the 4 MiB where a new mapping catches up with positional reads: the mapping
is kept. From the second scan of a file on, every read through it is two to three times faster
than a positional read, and a scan through positional reads rents a buffer per segment and copies
into it, where a mapped scan allocates nothing per batch. What a positional scan would save is the
mapping's own cost, about 16 µs and the first touch of each page, and only on a file read once and
under 4 MiB. A threshold on the volume was tried first, at 4 MiB: it put the scans of every small
file of the test suite on the positional path, and every ratchet that holds a scan to no
allocation per batch failed.

A positional read is a `pread` on the caller's thread, completed before the call returns, as a read
from the mapping is: a page the cache does not hold blocks either one the same way, and a hop to
the thread pool, which `RandomAccess.ReadAsync` makes on Unix, costs more than the read.

Nothing changes for a source the caller chose: `MemoryMappedSegmentSource`, `FileSegmentSource`,
`MemorySegmentSource` and every `ISegmentSource` keep their behaviour. The rule belongs to the
source `OpenAsync(path)` builds, which is internal.

What the rule does not do is bound what a mapping holds. A scan of the whole of a large file maps
it, and the pages it reads stay resident, clean and reclaimable, until the file is disposed, as
they do today: on the report's 10M-row file a full scan peaks at 47.1 MB mapped against 22.4 MB
read positionally, for positional reads 1.7 % slower warm. A plan that announces far more than a
scan holds at once could choose positional reads to bound that; it is left for when a workload
asks for it, with its own measurement.

### A.3 What the plan is asked

The share of the file's rows the plan reads: its range over the file's rows, narrowed by the share
of blocks the pruning left live, or by the share of the plan's splits a take's rows can touch. A
share of zero — an empty range, a filter the zone maps rule out entirely, a count the statistics
answer — maps nothing. The question costs a division; the exact count of segments and bytes
`ExplainAsync` makes is not repeated at every scan, since only zero or not decides.

### A.4 Where it is asked

Every driver that reads data segments asks once, after its pruning and before its first data
read: the batch enumerator; the terminal count that decodes, and the minimum or maximum for the
splits their bounds leave undecided; the key-ordered walk, once its key source is open. Through an
internal interface the path source implements; another source is never asked.

### A.5 Concurrency and lifetime

- Two scans may ask at once: the mapping is published by compare-and-exchange, and the loser
  disposes its own.
- A dispose that runs between a scan's check and its publication finds nothing to drop; the scan
  sees the dispose after publishing, and drops the mapping itself.
- The view does not need the handle once made: disposing the file closes the handle and drops the
  file's reference on the mapping, which lives on for as long as a lease reads from it.
- A read already under way when the mapping is published finishes positionally; the next is
  mapped.

### A.6 What was measured, and what holds it

In the Native AOT runner, the previous commit against this one, interleaved, nine processes of 25
rounds, warm medians: an open of the report's 10M-row file 100 → 81 µs (cold 282 → 223), and 328
bytes less; a projection of the 1M-row file 200 → 184 µs; a narrow filter of the 10M-row file
1 073 → 1 024 µs; a full scan, a wide filter and a thousand-row take unchanged.

Tests hold the positional and the mapped reads to the file's bytes, for one segment, a batch and a
range; an open that reads the statistics to no mapping, a scan to one, a scan the zone maps rule
out to none; a lease to outliving its reader; concurrent requests to one mapping; an empty file to
none. The read-path ratchets hold an open to the bytes it saves and a scan to the bytes it had.

---

## Part B — The encoding advisor

### B.1 What it is for

The writer chooses an encoding per column chunk, exactly, from the chunk's own statistics, and it
optimises for one thing: bytes, under a few rules that keep decoding fast. The measurements of
[choose-encodings.md](../guide/choose-encodings.md) show where that is not what a caller wants:

- text that does not repeat goes to zstd, which a scan decodes three times slower than FSST and a
  lookup seventy times slower, where FSST costs 21 to 75 % more bytes;
- a column whose values repeat across the file more than within a chunk cannot take a dictionary,
  and with larger chunks it would be smaller and scan three to six times faster;
- the right answer turns on the storage under the file — below 200 to 360 MB/s the smaller file
  reads faster end to end, above it the faster decode does — and on whether the file is scanned or
  read by row.

None of that is known to the writer, and all of it is known to the caller, or can be measured on
the caller's data. The advisor measures it: it takes a sample of the data, writes it under each
choice that applies, reads it back, and ranks the choices by the cost of the reads the caller
declares. It returns the numbers, the recommendation, why, and the options that apply it.

### B.2 The surface

```csharp
public sealed class VortexSession
{
    public ValueTask<EncodingAdvice> AdviseAsync(VortexFile file, EncodingGoal? goal = null, CancellationToken ct = default);
    public ValueTask<EncodingAdvice> AdviseAsync<TRecord>(ReadOnlyMemory<TRecord> rows, EncodingGoal? goal = null, CancellationToken ct = default)
        where TRecord : IVortexRecord<TRecord>;
}

public sealed record EncodingGoal
{
    public static EncodingGoal Default { get; }                         // the time to read, from local storage, by scans
    public static EncodingGoal Smallest { get; }                        // the bytes alone
    public EncodingObjective Objective { get; init; } = EncodingObjective.ReadTime;
    public long StorageBytesPerSecond { get; init; } = 2_000_000_000;   // what the file is read at; the default is a local drive
    public double LookupsPerScan { get; init; }                         // 0: whole scans only
    public long SampleRows { get; init; } = 1_048_576;                  // per column, spread over the data
}

public enum EncodingObjective : byte { ReadTime, Size }

public sealed record EncodingAdvice
{
    public EncodingGoal Goal { get; }
    public long Rows { get; }                                           // of the data analysed
    public long SampledRows { get; }
    public int ChunkTargetBytes { get; }                                // 0: the writer's own
    public ImmutableArray<ColumnEncodingAdvice> Columns { get; }
    public VortexWriteOptions ToWriteOptions(VortexWriteOptions? baseline = null);   // the hints and the chunk target, over the baseline
}

public sealed record ColumnEncodingAdvice
{
    public string Path { get; }
    public ColumnProfile Profile { get; }
    public ImmutableArray<EncodingCandidate> Candidates { get; }        // best first under the goal
    public EncodingCandidate Recommended { get; }
    public string Reason { get; }                                       // one sentence, with the numbers that decided it
}

public sealed record ColumnProfile
{
    public long Rows { get; }  public long Nulls { get; }
    public long Distinct { get; }                                       // in the sample, exact
    public double RowsPerDistinctInChunk { get; }                       // at the writer's own chunk size
    public double AverageRun { get; }                                   // rows per run of equal values
    public bool Ascending { get; }
    public double AverageLength { get; }                                // text and binary, in bytes; 0 otherwise
}

public sealed record EncodingCandidate
{
    public EncodingHint Hint { get; }                                   // Auto: the writer's own choice
    public int ChunkTargetBytes { get; }
    public ImmutableArray<string> WrittenAs { get; }                    // what the sample's chunks became, as WriteReport says
    public double BytesPerValue { get; }
    public double ScanNanosecondsPerValue { get; }                      // decode and read every value, in memory
    public double LookupMicroseconds { get; }                           // one row, the chunk it lies in decoded as a take decodes it
    public double Cost { get; }                                         // under the goal, per value of a scan; B.3
    public long? CrossesAtBytesPerSecond { get; }                       // against the recommended candidate, when a smaller one decodes slower or the reverse
}
```

The records are positional (`ColumnProfile(Rows, Nulls, Distinct, …)` and so on), so that a
caller can build one to test the code that consumes it.

The session is where the pool, the extensions and the parallelism live, so the advisor is there,
beside `CreateWriter` and `OpenAsync`. A file is advised on as it is; of rows, only the rows of the
sample's windows are written into memory, plain, and that file is advised on whole.

`ToWriteOptions` also sets the compression profile the candidates were written under:
`Smallest` for `Objective.Size`, `Auto` otherwise (B.5).

### B.3 The cost model

For a candidate, over the sample, per value:

```
Objective.ReadTime:  cost = scan + bytes ÷ throughput
                          + lookupsPerScan × (lookup + chunkBytes ÷ throughput) ÷ rows
Objective.Size:      cost = bytes, the scan cost breaking a tie
```

- `scan` is the time to open the sample written under the candidate, decode every value of the
  column to its plain form and read it once, per value.
- `bytes` is what the candidate wrote for the column, per value; `chunkBytes` is what a lookup
  reads, one chunk of the column.
- `lookup` is the time to take one row, measured on one row from each of up to 32 chunks spread
  over the sample.
- `throughput` is `EncodingGoal.StorageBytesPerSecond`. The decode is timed on one thread; a scan
  decoding on several shares the storage between them, so the caller gives the throughput divided
  by the threads.
- `rows` is the data's, not the sample's: `LookupsPerScan` counts lookups per scan of the data.

The times are measured on the machine the advisor runs on, in memory: the storage is the model's
term, not the measurement's, so that one sample prices any throughput. Three things keep the
measurement honest, each found by holding the advice to the matrix of B.8:

- **Each time is the least of its passes** after one to warm it: at least three, and as many as
  fill 20 ms, up to 64. A scan of a fast column takes a fraction of a millisecond, which one
  interruption doubles; three passes left a 16 MiB chunk target advised on a bit-packed column
  whose bytes had not moved, by the noise of its scan.
- **The JIT settles first.** Under the JIT the runtime compiles a method without its
  optimizations, and recompiles the ones called often once the process has been quiet for 100 ms.
  Measured before that, FSST and the plain text form read four to five times slower than they
  will, zstd (native) did not, and the two candidates swapped. Before a column is measured, the
  first 65 536 rows of its sample are written under every hint and read until
  `JitInfo.GetCompiledMethodCount` has not moved for 250 ms and three rounds, at most 3 s: that
  cost 3 s on the first advice of a process and 250 ms a column after, and made the JIT's figures
  the AOT's to a few percent. Ahead of time, nothing is compiled and nothing is waited for; the
  count is the test, since a project that publishes ahead of time turns the feature switches off
  for its runs under the JIT too.
- **The files in memory start on a 64-byte boundary**, the widest alignment a segment declares.
  `MemorySegmentSource` copies a segment its address does not align on every read, and a
  `byte[]` is aligned to 8: every scan and take paid those copies, 2 ns a value on the plain text
  form and ten times the take of an FSST row, and FSST came out 25 % slower than the bench's
  mapped file.

`CrossesAtBytesPerSecond` is where a candidate and the recommendation cost the same for a scan:
`(bytes₁ − bytes₂) ÷ (scan₂ − scan₁)` when the two differ in both, as the guide's tables give it.

### B.4 The sample

`SampleRows` rows per column — all of them when the data holds fewer — as eight windows of
contiguous rows spread evenly over the data. Contiguous because a chunk's encoding depends on the
rows that are together: runs, the order, and how many distinct values a chunk holds are properties
of windows, which rows drawn one by one would destroy. Spread because data drifts: a series sorted
by time, a table appended to over the years. Eight windows of 131 072 rows at the default, each at
least a chunk of an eight-byte column; the whole data when it is smaller than the windows.

What is counted on the sample, per column, is `ColumnProfile`: nulls, the exact distinct count,
the mean number of rows per distinct value inside a window of the writer's own chunk size, the mean
run of equal values, whether the values ascend, and the mean length of a text or binary value.

A column tried at larger chunks (B.5) is measured again on a sample of its own: two windows each
of one chunk of the largest target, from the plain sample's bytes per row, when the data holds
more than the sample. A 16 MiB chunk of 64-bit integers is two million rows, twice the default
sample: on the sample alone, the writer did not take the dictionary a real chunk of that size
takes, 2.35 B a value against 4.63, and the advice missed it. All of that column's candidates,
at the writer's size too, are then measured on those rows, so that they are compared on the same
data.

### B.5 The candidates

Per column, the sample is written as a file of that one column, once per candidate:

| column | hints tried, at the writer's own chunk size |
|---|---|
| every kind | `Auto`, `Canonical` |
| integers | `Dictionary`, `BitPacked`, `Zstd` |
| floating point | `Dictionary`, `Alp`, `Zstd` |
| text and binary | `Dictionary`, `Fsst`, `Zstd` |
| booleans | `RunEnd` |

and at 4 MiB and 16 MiB chunks, `Auto`, `Dictionary` and the column's best hint at its own size,
when the profile says the chunk decides: fewer than 100 rows per distinct value in a chunk of the
writer's size — below that a dictionary is not paid for: a chunk sweep of the report's table put a
dictionary at 100 rows per value within 10 % of its best, and at 13 rows per value 42 % above it —
and fewer distinct values in the sample than a tenth of its rows, so that larger chunks would hold
repeats.

The candidates are written under the profile the objective implies: `Auto` for `ReadTime`,
`Smallest` for `Size`, where the writer's own choice is the smallest it finds.

A hint that does not apply falls back to what `Auto` writes, which the candidate's `WrittenAs`
shows; two candidates that wrote the same bytes are one, and the first kept. A candidate at a
larger chunk that writes the encodings a candidate at the writer's own size wrote, within 1 % of its
bytes, is dropped too: what is left between the two is the noise of a time.

**What the writer had to change.** A hint was plan memory with an infinite tolerance, consulted
after a progression and after a run count, and not at all under `Smallest`. So a `Canonical` hint
on a column one percent true gave its runs, which scan twelve times slower than its bitmap, and
under `Size` every hint fell back to the profile's own choice, which prices a dictionary's codes a
whole byte wide and so missed the 8 % a dictionary saves on the nullable integers. A hint now comes
after the progression alone, which costs nothing per row, and before the run count, under every
profile; the writer's documentation says so, and two tests hold it.

### B.6 The choice

- Per column, the candidate of least cost, unless `Auto` at the writer's own chunk size is within
  5 % of it: then `Auto`, since the advice should not move with the noise of a measurement, and an
  option not given is an option that cannot go stale.
- The chunk target is a column's: the writer gives a column a target of its own
  (`VortexWriteOptions.ColumnChunkTargetBytes`, B.10), its chunks spanning whole chunks of the
  file, so each column weighs its own targets. The candidate to take at a larger target replaces
  the one at the writer's own size only when it costs the column 5 % less, the least costly of
  those when several do. The candidates were measured one column at a time, which is what a
  target of the column's own writes.
- `ToWriteOptions` sets `Hints` for the columns whose recommendation is not `Auto` and
  `ColumnChunkTargetBytes` for the columns whose recommendation was written at a target, over the
  baseline's other options; the baseline's file target stays.

`Reason` says it in one sentence with the numbers, for instance: *"text, 10 000 distinct values
over 1 048 576 rows, 2.1 rows each in a chunk: a dictionary at 16 MiB chunks, 1.80 B a value and
12.7 ns to scan, against 2.54 B and 38.7 ns at the writer's size."*

### B.7 What it costs

The advisor is a measurement, run once for a kind of data, not for every file: a candidate is a
write and the passes of B.3. On the machine of A.1, under the JIT, a million-row sample costs 0.3
to 0.8 s for a column of numbers or booleans, 1 to 1.5 s for one tried at larger chunks, and 2 to
3.5 s for a text column; the first column of a process pays the JIT's settling on top, up to 3 s.
Memory is one column's sample, its written candidates one at a time, the slices the JIT settles
on, and the profile's counters.

### B.8 Tests and gates

- The choice is a pure function of the candidates and the goal, and is tested as one, on numbers
  written in the test: the tolerance toward `Auto`, the chunk target's 5 %, the two objectives, the
  crossing throughput. No test asserts a measured time.
- The measurement is tested for what is exact: the bytes each candidate writes, the written
  encodings, the profile's counts, and that every value read back is the value written.
- The advice is held, outside CI, to the twenty shapes of the `--tradeoffs` bench:
  `--tradeoffs --advise` advises on each under five goals — scans at 2 GB/s, 100 MB/s and
  10 GB/s, a row in a thousand read by row, and the bytes — and prints the choice and its reason.
  On each, it must name the choice the guide's tables measured as best, and its crossings must
  fall where the tables' do. It does, on all twenty: zstd on the timestamps below 250 MB/s, a
  dictionary at 16 MiB chunks on the repeating integers, floats and ids, FSST on the log lines,
  the bitmap on the booleans one percent true above 340 MB/s, and the writer's own size for
  every column read by row. It also names what the tables did not compare: the plain form over
  FSST for the UUIDs from 1.3 GB/s up, and a dictionary on the nullable integers under `Size`.
- The advisor's tests run in a collection of their own, since the JIT does not settle in a process
  whose other tests compile all the while.

### B.9 Not in this part

- **`Auto` taking the advice by default.** The writer could run the advisor's profile on its first
  chunks under a default goal. That is a change to what a write produces and to its determinism,
  and is decided on its own evidence.
- **A stream as input.** An `IAsyncEnumerable<TRecord>` of unknown length is sampled by a reservoir
  of windows; it waits for a caller who has one.
- **A table of reference costs**, to advise without measuring, deterministically, on a machine that
  should not spend the time.
- **The writer sizing each column's chunks by its own width.** The writer's own chunks hold a
  megabyte of the widest column, so one wide column holds the narrow ones to a few blocks: in the
  table of B.10, 611 chunks of 16 384 rows for a key of eight bytes, of which a megabyte is 131 072
  rows. A megabyte of its own values for every column, measured there through the option, wrote the
  file 19 % faster and 3 % smaller and read every narrow column faster, the wide one unchanged. It
  changes every file the writer produces by default, and is decided on its own evidence.

### B.10 A column's own chunks

`VortexWriteOptions.ColumnChunkTargetBytes` names top-level columns and their targets. At each of
the file's chunks such a column copies its share into a context of its own, keeps its per-block
summaries and its distinct table running, and writes one chunk once it holds its target, so its
chunks span whole chunks of the file and end where one of the file's does. A flush and the
completion write what waits; the tail a block carries is not probed again into a table that kept
it. The layout takes each column's own chunk rows, and `WriteReport.ChunkRowsOf(column)` gives
them. An append refuses the option, as it refuses a file whose columns are not chunked alike.

The reader already cuts its splits at every column's boundaries and keeps a decoded chunk across
the splits it spans. Rust reads such files: `bench/crosscheck.sh` writes sixteen tables of the
corpus with every other column chunked apart, and `verify_written` compares them scalar by scalar.

Measured on 10 000 000 rows written as batches of 65 536 that own their buffers, a key, ids of
10 000 values, a measure, a city and a random 32-character text `Auto` writes as zstd:

| written | write | bytes | key scan | ids scan | ids take | text scan | text take |
|---|---:|---:|---:|---:|---:|---:|---:|
| the writer's own | 2.61 s | 237.8 MB | 5.6 ms | 128 ms | 44.3 ms | 379 ms | 277 ms |
| 16 MiB, the file | 2.11 s | 236.5 MB | 3.8 ms | 92 ms | 6.5 ms | 378 ms | 226 ms |
| 16 MiB, the ids alone | 1.88 s | 226.6 MB | 5.8 ms | 88 ms | 2.9 ms | 379 ms | 276 ms |
| a megabyte for each column | 2.11 s | 229.8 MB | 3.7 ms | 100 ms | 16.0 ms | 378 ms | 277 ms |
| the advice, default goal | 0.95 s | 364.8 MB | 5.8 ms | 87 ms | 2.86 ms | 137 ms | 2.84 ms |

A take reads 1 000 rows spread over the file. Under the default goal the advice gives the ids
16 MiB chunks of their own and the text its plain form, which the goal's 2 GB/s scan prefers to
zstd's decode.

A window of a larger batch keeps its batch's whole text heap, which the writer counted whole when it
sized its chunks: written as windows of one batch of 10 000 000 rows, the same table took chunks of
a block, 1 221 of them. The writer now counts of a text column the bytes its rows name
(`CanonicalArena.NamedBytes`), and the windows write 611 chunks, the file the same rows write as
batches of their own. A merge's runs are such windows; the batches of a scan hold their own chunk's
buffers, and a file written again from its scan came out byte for byte the same either way.
