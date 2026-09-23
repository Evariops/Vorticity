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

`v.Pages` is a `ColumnBuilder<ReadOnlyMemory<int>>`. `Append(ReadOnlySpan<int>)` appends one list
in one call, and a list refused part way leaves nothing behind. `BeginList` opens a list,
`Elements` is the `ColumnBuilder<int>` of every list's elements, and `EndList` closes it: whatever
was appended to `Elements` in between is one list. A nullable list column also takes `AppendNull`.

## Nested records

`v.Struct<Address>(5)` returns the builder of the nested record held by member 5, and the generated
`v.Origin` is the same builder. Its columns fill like any other; each row of it is one row of
`Visit`, so every field must get a value per row.

A nullable nested record takes `AppendNull()` on its builder: the row is null and its fields get
placeholder values. With `[VortexRecord] public partial record struct Trip(int Id, Address? Destination)`:

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

The sample writes a hundred thousand visits twice. Column by column, with the uuids and timestamps
appended in bulk (`Append(ReadOnlySpan<Guid>)`, `Append(ReadOnlySpan<DateTime>)`), the durations
through `GetSpan` and the rest row by row:

```
100000 visits column by column: 2169236 bytes, best of three 36 ms
  Id: Canonical x5
  StartedAt: Sequence x5
  DurationMs: Sequence x4, BitPacked
  Referrer: Dictionary x5
  Pages: BitPacked x5
  Origin: {Country: Dictionary, City: Dictionary} x5
the same visits as rows: 2169236 bytes, best of three 24 ms
read back: 100000 visits, 100000 lists holding 200000 pages, 9091 origins without a city, 2169236 bytes
```

The report reads each chunk's encoding from what was written: a list by its elements, a timestamp
or a uuid by its storage, and a nested record field by field.

The rows won here, and the reason is worth knowing: the generated `WriteRows` fills one column at a
time over the whole span, while the sample's loop appends the referrer, the list and both origin
fields of a row before moving to the next. Filling one column at a time is what paid.

## Watch out

* **A list left open makes `WriteAsync` throw** `VortexSchemaException`: *…: a list of list(i32) is
  open. Complete every row before writing it.*
* **A hint on a timestamp or a uuid column does not reach its storage.** The hint pins the
  extension, and the chooser still picks the storage's encoding: a `Canonical` hint on `StartedAt`
  is still reported `Sequence`. `vxdump --layout` shows the whole encoding tree
  ([vxdump.md](vxdump.md)).
* **A list of records cannot be written**: the generator refuses such a member with VX1005, since
  no typed column reads it back ([records.md](records.md)).
* `StartedAt.Append(DateTime)` converts a local time for a UTC column; `Id.Append(Guid)` stores the
  uuid in network order. A `Guid` column needs the edition that carries `vortex.uuid`, the default
  ([editions.md](editions.md)).
* Reading nested records and lists is [nested-records.md](nested-records.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- write-lists-and-records
```

The figures above come from a run of every case, in which this one follows the others
([README.md](README.md)).
