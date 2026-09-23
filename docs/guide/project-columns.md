# Project columns

Read two columns of seven and pay for two. There is no projection call: the record you scan with
is the projection.

```csharp
[VortexRecord]
public partial record struct VisitTiming(DateTime StartedAt, int DurationMs);
```

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);

int longest = 0;
DateTime last = default;
await foreach (var (startedAt, durationMs) in file.Scan<VisitTiming>())
{
    foreach (int duration in durationMs.Values)
    {
        longest = Math.Max(longest, duration);
    }

    last = startedAt[startedAt.Length - 1];
}
```

```
longest visit 89999 ms, the last one started at 2026-09-04 11:19:57Z
```

The visits file has six top-level columns, one of them a nested record of two fields: `Id`,
`StartedAt`, `DurationMs`, `Referrer`, `Pages`, `Origin.Country` and `Origin.City`. `VisitTiming`
names two of them, and the scan reads those two.

## What it saves

The sample scans the file with four records and measures each twice: `ExplainAsync` before, from
the layout alone, and `Statistics` after the scan ran.

| record | columns | segments | bytes to read | requests | bytes read |
|---|---|---|---|---|---|
| `Visit` | 7 | 30 | 2 162 496 | 30 | 2 162 496 |
| `VisitTiming` | 2 | 10 | 53 096 | 10 | 53 096 |
| `VisitOrigin` | 1 | 5 | 65 580 | 5 | 65 580 |
| `VisitId` | 1 | 5 | 1 600 620 | 5 | 1 600 620 |

Each column lives in its own segments, so a column the record does not name is a branch of the
layout the scan never walks: not read and discarded, not read at all. The saving follows the
columns you drop, weighted by their size, not by their number: the timestamp and the duration of
`VisitTiming` compress to little and cost under 3 % of the whole record, while the sixteen bytes
of each `Id`, stored as they are, make three quarters of it.

## Nested records project too

```csharp
[VortexRecord]
public partial record struct OriginCountry(string Country);

[VortexRecord]
public partial record struct VisitOrigin(OriginCountry Origin);
```

A struct column is reached through a nested record, and the projection is over leaves: `Origin` in
`VisitOrigin` names only `Country`, so the scan reads that field's 5 segments and not those of
its sibling `City`.

## How a record binds to a file

At the first sink of a scan, each member is matched to a column by exact name, then by a unique
case-insensitive match; `[VortexColumn("name")]` on the member says which column when the names
differ ([records.md](records.md)). A file with more columns than the record is fine. A member with
no column, an ambiguous match, or a type that does not fit the column's type throws
`VortexSchemaException`, naming the member and the columns:

```
VisitLength: Member 'Duration' of VisitLength has no column; the columns are 'Id', 'StartedAt', 'DurationMs', 'Referrer', 'Pages', 'Origin'.
```

The batch delivers the record's columns in the record's order, whatever the file's order: the
deconstruction above gives `StartedAt` first because `VisitTiming` declares it first.

## Watch out

* **A filter is written against the same record.** `Where` sees the record's members and no
  others, so a column you filter on is a column you name, read for the filter and delivered too
  ([filter-rows.md](filter-rows.md)). A record per query is the idiom, not a smell.
* **Nullability is part of the fit.** A non-nullable member over a nullable column is refused at
  binding; declare `int?` or `string?` where the file says the column can hold a null.
* `ToRecordsAsync()` materialises rows of the same record, one field copy per row
  ([read-rows.md](read-rows.md)); the projection is the same, the cost is not.
* For a file whose columns you only know at run time, the untyped scan takes column names instead
  of a record ([untyped-files.md](untyped-files.md)).

The figures come from one run of the sample on the demonstration file of a hundred thousand visits.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- project-columns
```
