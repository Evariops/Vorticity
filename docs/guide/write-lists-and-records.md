# Write lists and nested records

Append a list in one call or element by element, fill a nested record through its own builder, and
write a null record.

```csharp
ColumnsBuilder<Visit> v = writer.Builder<Visit>();

v.Id.Append(Guid.CreateVersion7(now));
v.StartedAt.Append(now);
v.DurationMs.Append(1_250);
v.Referrer.AppendNull();
v.Pages.Append([1, 4, 9]);                                 // one list, one call
ColumnsBuilder<Address> origin = v.Struct<Address>(5);     // the same builder as v.Origin
origin.Country.Append("FR"u8);
origin.City.AppendNull();

v.Id.Append(Guid.CreateVersion7(now.AddSeconds(1)));
v.StartedAt.Append(now.AddSeconds(1));
v.DurationMs.Append(830);
v.Referrer.Append("https://example.org/"u8);
v.Pages.BeginList();                                       // or element by element
foreach (int page in pagesSeen) v.Pages.Elements.Append(page);
v.Pages.EndList();
v.Origin.Country.Append("DE"u8);
v.Origin.City.Append("Berlin"u8);
```

The records are `Visit(Guid Id, DateTime StartedAt, int DurationMs, string? Referrer, ReadOnlyMemory<int> Pages, Address Origin)`
and `Address(string Country, string? City)`, both `[VortexRecord]`. Read back:

```
  1250 ms, pages [1, 4, 9], from FR/null, referrer null
  830 ms, pages [2, 3, 5, 7], from DE/Berlin, referrer https://example.org/
```

## Lists

`v.Pages` is a `ColumnBuilder<ReadOnlyMemory<int>>`. `Append(ReadOnlySpan<int>)` appends one list in
one call, and a list refused part way leaves nothing behind. `BeginList` opens a list, `Elements` is
the `ColumnBuilder<int>` of every list's elements, and `EndList` closes it: whatever was appended to
`Elements` in between forms one list. A nullable list column also accepts `AppendNull`.

A list of records, such as a member declared `ReadOnlyMemory<Item>` or `List<Item>`, gets a
`ListColumnsBuilder<Item>` instead. It works the same way: `BeginList`, then one row of the nested
record per element through `Elements`, a `ColumnsBuilder<Item>`, then `EndList`. Writing rows through
`WriteAsync<TRecord>` does all of this for you.

## Nested records

`v.Struct<Address>(5)` returns the builder of the nested record held by member 5, and the generated
`v.Origin` is the same builder. Its columns fill like any others. Each of its rows is one row of
`Visit`, so every field must get a value per row.

A nullable nested record accepts `AppendNull()` on its builder. The row is then null and its fields
get placeholder values. With `[VortexRecord] public partial record struct Trip(int Id, Address? Destination)`:

```csharp
ColumnsBuilder<Trip> t = writer.Builder<Trip>();
t.Id.Append(1);
t.Destination.Country.Append("FR"u8);
t.Destination.City.Append("Lyon"u8);

t.Id.Append(2);
t.Destination.AppendNull();                                // a null record: its fields get placeholders
```

```
  trip 1: FR/Lyon
  trip 2: no destination
```

## What it costs

The sample writes a hundred thousand visits twice. The first time it goes column by column, with the
uuids and timestamps appended in bulk (`Append(ReadOnlySpan<Guid>)`, `Append(ReadOnlySpan<DateTime>)`),
the durations through `GetSpan`, and the rest row by row:

```
100000 visits column by column: 2139788 bytes, best of three 36 ms
  Id: Canonical x5
  StartedAt: Sequence x5
  DurationMs: Sequence x4, BitPacked
  Referrer: Dictionary x5
  Pages: BitPacked x5
  Origin: {Country: Dictionary, City: Dictionary} x5
the same visits as rows: 2168748 bytes, best of three 26 ms
read back: 100000 visits, 100000 lists holding 200000 pages, 9091 origins without a city, 2168748 bytes
```

The report reads each chunk's encoding from what was written: a list by its elements, a timestamp or
a uuid by its storage, and a nested record field by field.

The rows won here, and the reason is worth knowing. The generated `WriteRows` fills one column at a
time over the whole span, while the sample's loop appends the referrer, the list and both origin
fields of a row before moving on to the next. Filling one column at a time is what paid off. The two
files differ by about 1 % in size.

## Watch out

* A list left open makes `WriteAsync` throw `VortexSchemaException`: *…: a list of list(i32) is open.
  Complete every row before writing it.*
* A hint on a timestamp or a uuid column does not reach its storage. The hint pins the extension, and
  the chooser still picks the storage's encoding, so a `Canonical` hint on `StartedAt` is still
  reported as `Sequence`. `vxdump --layout` shows the whole encoding tree ([vxdump.md](vxdump.md)).
* A list of nullable records and a list of lists of records are refused by the generator with VX1005
  ([records.md](records.md)). Hold the inner lists in a record instead.
* `StartedAt.Append(DateTime)` converts a local time for a UTC column, and `Id.Append(Guid)` stores the
  uuid in network byte order. A `Guid` column needs an edition that carries `vortex.uuid`, which the
  default does ([editions.md](editions.md)).
* Reading nested records and lists back is covered in [nested-records.md](nested-records.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- write-lists-and-records
```

The figures above come from a run of every case, in which this one follows the others
([README.md](README.md)).
