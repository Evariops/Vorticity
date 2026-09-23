# Nested records

Read a struct column as a nested record, a list column as one buffer of elements, and filter on
both.

```csharp
await foreach (var v in file.Scan<Visit>())
{
    Columns<Address> origin = v.Origin;
    Column<string?> originCity = origin.City;
    withoutCity += originCity.NullCount;
    for (int i = 0; i < origin.RowCount; i++)
    {
        if (origin.Country[i].SequenceEqual("FR"u8))
        {
            french++;
        }
    }

    Column<ReadOnlyMemory<int>> pages = v.Pages;
    ReadOnlySpan<int> allPages = pages.Elements.Values;
    for (int i = 0; i < pages.Length; i++)
    {
        ReadOnlySpan<int> list = allPages[pages[i]];
        longest = Math.Max(longest, list.Length);
        foreach (int page in list)
        {
            byPage[page]++;
        }
    }

    visits += v.RowCount;
    pagesSeen += allPages.Length;
}
```

```
100000 visits, 200000 pages in their lists, the longest list 4, page 7 seen 5000 times
origin: 66666 from FR, 9091 without a city
```

The records are those of the demonstration's visits file:

```csharp
[VortexRecord] public partial record struct Visit(Guid Id, DateTime StartedAt, int DurationMs, string? Referrer, ReadOnlyMemory<int> Pages, Address Origin);
[VortexRecord] public partial record struct Address(string Country, string? City);
```

## A struct column is a nested `Columns<T>`

`v.Origin` is a `Columns<Address>`: the same rows as the batch, seen through the nested record, with
the generated members `origin.Country` and `origin.City` as ordinary columns. Nothing is copied to
get there, and a nested record nests again the same way.

A nested record is a projection too. The record names only the leaves it wants, and only their
segments are read:

```csharp
[VortexRecord] public partial record struct VisitCountry(CountryOnly Origin);
[VortexRecord] public partial record struct CountryOnly(string Country);
```

Every column of `Visit` requested 2 162 496 bytes; `VisitCountry`, which reads `Origin.Country`
alone, 65 580. See [project-columns.md](project-columns.md) for the projection in general.

## A list column is its elements and their ranges

`v.Pages` is a `Column<ReadOnlyMemory<int>>`. It does not hand out one array per list: `Elements` is
a `Column<int>` holding every list's elements back to back, and `pages[i]` is the `Range` of list
`i` inside it. `allPages[pages[i]]` slices it, with no allocation. Here 100 000 lists hold 200 000
elements, none longer than four.

Only `ToRecordsAsync` allocates one array per list, because a `Visit` row owns its
`ReadOnlyMemory<int>` ([read-rows.md](read-rows.md)).

## A nullable nested record

A record member of a nullable record type makes a nullable struct column:

```csharp
[VortexRecord] public partial record struct Parcel(long Id, Address? Destination);
```

`p.Destination` is still a `Columns<Address>`, and its own nulls are on it: `IsValid(i)`,
`IsAllValid` and `ValidityWords`, the same bitmap shape as a column's. The fields of a null record
say nothing; check `IsValid` first. On 20 000 parcels, one in five without a destination:

```
parcels: 4000 without a destination, by IsValid; 0 with one but no city
Where(p => p.Destination.IsNull): 4000
  as a record: 0 -> no destination
  as a record: 1 -> Lyon
```

As a row, a null nested record is a null `Address?`.

## Filtering on nested fields and lists

The probe of a scan nests the same way, so a filter reaches into a record and into a list:

```
Where(v => v.Origin.Country == "FR"): 66666
Where(v => v.Origin.City.IsNull): 9091
Where(v => v.Pages.Contains(7)): 5000
Where(v => v.Origin.IsNull), on a member that is never null: 0
```

`v.Origin.IsNull` and `IsNotNull` test the nested record itself; on a record member that is not
nullable, `IsNull` is simply never true. `Contains` on a list column is list membership, evaluated
per row by the kernel; zone maps do not prune it.

## Watch out

* **A list of records is refused** by the generator (VX1005): no typed column reads one. A list of
  primitives, strings or dates is fine.
* A nullable list member is exposed as `ReadOnlyMemory<T>`, and its nulls are read from the list
  column's `ValidityWords` or `IsValid`, not from the elements.
* `allPages` and every other span are borrowed from the batch, like any column: they end with the
  loop body.

The mapping of struct and list types to .NET is [07-dotnet-mapping.md](../design/07-dotnet-mapping.md),
and the record contract §3.1 and §4 of [14-public-api.md](../design/14-public-api.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- nested-records
```
