# Project columns

Read two columns out of seven and pay for two. There is no projection call: the record you scan with
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

The visits file has six top-level columns, one of which is a nested record of two fields, so seven
leaves in all: `Id`, `StartedAt`, `DurationMs`, `Referrer`, `Pages`, `Origin.Country` and
`Origin.City`. `VisitTiming` names two of them, and the scan reads those two.

## What it saves

The sample scans the file with four records and measures each one twice: with `ExplainAsync` before
the scan, from the layout alone, and with `Metrics` after it.

| record | columns | segments | bytes to read | requests | bytes read |
|---|---|---|---|---|---|
| `Visit` | 7 | 30 | 2 132 656 | 30 | 2 132 656 |
| `VisitTiming` | 2 | 10 | 53 096 | 10 | 53 096 |
| `VisitOrigin` | 1 | 5 | 65 580 | 5 | 65 580 |
| `VisitId` | 1 | 5 | 1 600 620 | 5 | 1 600 620 |

Each column lives in its own segments, so a column the record does not name is a branch of the
layout the scan never walks. It is not read and then discarded, it is not read at all. The saving
follows the size of the columns you drop rather than their number. The timestamp and the duration of
`VisitTiming` compress well and cost under 3 % of the whole record, while the sixteen raw bytes of
each `Id` account for three quarters of it.

## Nested records project too

```csharp
[VortexRecord]
public partial record struct OriginCountry(string Country);

[VortexRecord]
public partial record struct VisitOrigin(OriginCountry Origin);
```

A struct column is reached through a nested record, and the projection works on leaves. `Origin` in
`VisitOrigin` names only `Country`, so the scan reads that field's 5 segments and not those of its
sibling `City`.

## How a record binds to a file

At the first sink of a scan, each member is matched to a column by exact name, then by a unique
case-insensitive match. `[VortexColumn("name")]` on the member says which column to use when the
names differ ([records.md](records.md)). A file may have more columns than the record. A member with
no column, an ambiguous match, or a type that does not fit the column throws
`VortexSchemaException`, which names the member and the columns:

```
VisitLength: Member 'Duration' of VisitLength has no column; the columns are 'Id', 'StartedAt', 'DurationMs', 'Referrer', 'Pages', 'Origin'.
```

The batch delivers the record's columns in the record's order, whatever the file's order. That is why
the deconstruction above gives `StartedAt` first: `VisitTiming` declares it first.

## Watch out

* A filter is written against the same record. `Where` sees the record's members and nothing else,
  so a column you filter on must be a column you name, and it is read and delivered too
  ([filter-rows.md](filter-rows.md)). Declaring one record per query is the intended way to work.
* Nullability is part of the fit. A non-nullable member over a nullable column is refused at
  binding, so declare `int?` or `string?` wherever the file says the column can hold a null.
* `ToRecordsAsync()` materialises rows of the same record, with a field copy per row
  ([read-rows.md](read-rows.md)). The projection is the same, the cost is not.
* When you only know the columns at run time, the untyped scan takes column names instead of a
  record ([untyped-files.md](untyped-files.md)).

The figures come from one run of the sample on the demonstration file of a hundred thousand visits.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- project-columns
```
