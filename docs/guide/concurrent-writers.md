# Concurrent writers

Let several writers turn the same input into rows, and count each entry of it once, without a lease
or a lock.

```csharp
ulong from = a.SourceOffset("raw/spans/17");

await using ObjectDraft draft = b.StartObject();
await draft.Writer.WriteAsync<SpanCount>([new SpanCount("raw/spans/17", 42_300, 42_300 - (long)from)]);
SourceCommit first = await b.AppendAsync(draft, [new SourceRange("raw/spans/17", from, 42_300)]);
```

```
counted up to 41200: Applied, version 2
B counts (41200, 42300]: Applied, version 3
A counts (41200, 41950]: AlreadyThere, version 3, and now reads 42300
A counts (42300, 42900]: Applied, version 4
B counts (42300, 43000]: conflict, the dataset counts the source up to 42900 at version 4
B counts (42900, 43000]: Applied, version 5
in the dataset: 43000 spans counted, the source's offset 43000, every span once
```

An aggregator reads entries from a source, a shard of raw data or a partition of a log, and appends
what it makes of them. Two aggregators can always end up on the same entries: one pauses for a
garbage collection, another takes over, and the first comes back. Both appends would apply, and
the entries would count twice. A lease does not prevent it, since the first aggregator does not
know it lost the lease until it has committed.

The dataset counts instead. Its commits record, for each source, the offset up to which its entries
are counted. A writer reads the offset (`SourceOffset`, free, from the version the handle holds),
takes the entries past it, and declares the range it consumed with the commit, `(from, to]`.

## How a commit decides

The commit is decided against the version it lands on, at every attempt, rebases included:

| the source's offset in that version | the commit |
|---|---|
| equal to `from` | applies, and the offset moves to `to` |
| at or past `to` | adds nothing: another writer counted the range first. `Outcome` is `AlreadyThere` and `Version` the version that counts it |
| anything else | throws `SourceOffsetConflictException`: the range would leave a gap, or overlap one counted |

In the run above, A's range ends before the offset B's commit recorded, so A's rows are not added,
and its handle moves to the version that counts them. B, still on its own commit when A moves the
offset again, overlaps A's range: the exception names the offset recorded, `Recorded`, and B's
handle reads the version that decided it, so B takes up from there. However the writers interleave,
the ranges that applied tile the source from 0 without a gap or an overlap, because an offset moves
only by a commit whose range starts exactly at it, and the store orders the commits.

A writer whose answer was lost, or that restarts not knowing it committed, replays the same range
and gets `AlreadyThere`. Act on a commit, by sending a notification for instance, only when it is
`Applied`: the commit names the one writer that counted the range.

## Several sources, and the other commits

A commit can declare one range per source. It applies when every range starts at its source's
offset, adds nothing when every one is counted, and throws otherwise, since its rows mix the sources
and cannot apply in part. Writers on different sources never get in each other's way, so the sources
can be spread over the writers as you like, and spread again at any time.

`AppendAsync`, `ReplaceObjectsAsync`, `DeleteAsync` and `UpdateAsync` all take the ranges. A
commit that consumed entries and produced no row still commits, so the offset moves: an append of an
empty draft, or a delete that matched nothing. Compactions, indexings and vacuums declare nothing and
carry the offsets on. `ForgetSourcesAsync` drops the offsets of sources no writer will take again.

The writers leave compaction to a process of their own, which runs the dataset's only compaction
loop, and keep inline compaction off, as it is by default. Two compactions then never race for the
same objects ([dataset-maintenance.md](dataset-maintenance.md#in-the-background)).

## Watch out

* Forget a source only once no writer can take it again. A writer that did would start it from 0,
  and count its entries a second time.
* A dataset whose commits count offsets is written in commit format 3, which a library that predates
  them refuses to open. It would otherwise rebuild the headers without the offsets and forget them at
  its first commit. A dataset that counts no source stays readable by it.
* The rows of a commit that ended `AlreadyThere` or in a conflict are in the store and in no version.
  Vacuum removes them after the retention window, like any object no version references.
* The offsets are the application's numbers, any that grow with the entries: a sequence number, a log
  offset. The dataset does not interpret them, and every header carries the whole table, about 30
  bytes a source.

The design is in [13-dataset.md](../design/13-dataset.md#84-sources-and-offsets), and
[datasets.md](datasets.md) covers the rest of a dataset.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- concurrent-writers
```
