# Text columns

Read a text column as the UTF-8 bytes it holds, allocate a `string` only where you need one, and
count distinct values without a loop.

```csharp
long paris = 0;
long startingWithL = 0;
await foreach (var (_, _, city) in file.Scan<Reading>())
{
    for (int i = 0; i < city.Length; i++)
    {
        ReadOnlySpan<byte> name = city[i];
        if (name.SequenceEqual("Paris"u8))
        {
            paris++;
        }

        if (name.StartsWith("L"u8))
        {
            startingWithL++;
        }
    }
}
```

## What a text column gives you

A `string` member makes a `Column<string>`, and a `string?` member a `Column<string?>`, with the
same accessors:

| member | returns | allocates |
|---|---|---|
| `city[i]` | the value as a `ReadOnlySpan<byte>` of UTF-8, pointing into the decoded batch | nothing |
| `city.GetLength(i)` | its length in bytes | nothing |
| `city.GetString(i)` | a `string`, or `null` at a null row | one string per call |
| `city.IsValid(i)` | false at a null row, whose span is empty | nothing |

The span is compared with the `u8` literals of C#, `SequenceEqual`, `StartsWith`, `IndexOf`,
`SearchValues<byte>` and the rest of `System.Memory`, byte for byte, with no culture and no
transcoding. It is borrowed like everything else from a scan: valid inside the loop body only
([scan-a-table.md](scan-a-table.md)). Why spans come first and strings on request is §4 of
[07-dotnet-mapping.md](../design/07-dotnet-mapping.md).

## What a string costs

The same two counts, once over spans and once with `city.GetString(i)`:

```
UTF-8 spans           44.5 ms,     111560 bytes allocated: 125006 rows in Paris, 249999 starting with L
GetString             57.7 ms,   37111520 bytes allocated: 125006 rows in Paris, 249999 starting with L
the scan alone         6.5 ms,     111488 bytes allocated: 1000000 rows
CountDistinctAsync    11.3 ms,      80848 bytes allocated: 8 cities
```

A million strings are 37 MB of garbage for the collector, where the spans allocate nothing beyond
what the scan itself does. Keep `GetString` for the values that leave the loop: a dictionary key, a
log line, a result.

## Distinct values, and filters on text

```csharp
long cities = await file.Scan<Reading>().CountDistinctAsync(r => r.City);
```

A distinct count is an operator of the scan, not a loop over the values: it counts non-null values,
counts codes rather than rows on a dictionary-encoded block, and merges the chunks by value.
On the visits file, whose `Referrer` is a nullable text column stored as a dictionary:

```
referrers: 13 distinct, 25000 null, 75000 starting with https://
```

The second and third numbers are `Where(v => v.Referrer == null).CountAsync()` and
`Where(v => v.Referrer.StartsWith("https://")).CountAsync()`: equality, `In`, `StartsWith`,
`Contains` and `Like` on text are pushed down like any predicate, and a file written with string
bounds (the default, `StringBoundBytes = 16`) prunes blocks with them
([filter-rows.md](filter-rows.md)). [encoded-forms.md](encoded-forms.md) shows how to read the
dictionary codes yourself, and why you rarely need to.

## Watch out

* **An empty span is not a null.** A null row gives an empty span and `GetLength(i) == 0`, as an
  empty string does; `IsValid(i)`, or `GetString(i)` returning `null`, tells them apart. Of the
  first eight visits, two have a null referrer.
* **Comparisons are ordinal.** A text predicate compares UTF-8 bytes; culture-aware comparison is
  for the strings you materialise.
* **A `string` member of a row costs the same allocation.** `ToRecordsAsync()` allocates one string
  per row for it ([read-rows.md](read-rows.md)).

The figures come from one run of the sample on the demonstration files; each timing is the best of
three passes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- text-columns
```
