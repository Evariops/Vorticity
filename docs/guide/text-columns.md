# Text columns

Read a text column as the UTF-8 bytes it holds, allocate a `string` only where you need one, and count
distinct values without a loop.

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

A `string` member produces a `Column<string>`, and a `string?` member a `Column<string?>`, with the
same accessors:

| member | returns | allocates |
|---|---|---|
| `city[i]` | the value as a `ReadOnlySpan<byte>` of UTF-8, pointing into the decoded batch | nothing |
| `city.GetLength(i)` | its length in bytes | nothing |
| `city.GetString(i)` | a `string`, or `null` for a null row | one string per call |
| `city.IsValid(i)` | false for a null row, whose span is empty | nothing |

You compare the span with C#'s `u8` literals, `SequenceEqual`, `StartsWith`, `IndexOf`,
`SearchValues<byte>` and the rest of `System.Memory`, byte for byte, with no culture and no
transcoding. Like everything a scan hands out, it is borrowed and only valid inside the loop body
([scan-a-table.md](scan-a-table.md)). Why spans come first and strings on request is explained in
[the .NET mapping](../design/07-dotnet-mapping.md#4-strings-spans-first-string-on-request).

## What a string costs

The same two counts, once over spans and once with `city.GetString(i)`:

```
UTF-8 spans           40.6 ms,       7064 bytes allocated: 125006 rows in Paris, 249999 starting with L
GetString             56.3 ms,   37007024 bytes allocated: 125006 rows in Paris, 249999 starting with L
the scan alone         2.2 ms,       6992 bytes allocated: 1000000 rows
CountDistinctAsync     9.4 ms,      15616 bytes allocated: 8 cities
```

A million strings are 37 MB of garbage for the collector, while the spans allocate barely more than
the scan itself. Keep `GetString` for values that leave the loop: a dictionary key, a log line, a
result.

## Distinct values, and filters on text

```csharp
long cities = await file.Scan<Reading>().CountDistinctAsync(r => r.City);
```

A distinct count is an operator of the scan, not a loop over the values. It counts non-null values,
counts codes rather than rows on a dictionary-encoded block, and merges the chunks by value. On the
visits file, whose `Referrer` is a nullable text column stored as a dictionary:

```
referrers: 13 distinct, 25000 null, 75000 starting with https://
```

The second and third numbers come from `Where(v => v.Referrer == null).CountAsync()` and
`Where(v => v.Referrer.StartsWith("https://")).CountAsync()`. Equality, `In`, `StartsWith`, `Contains`
and `Like` on text are pushed down like any predicate. A file written with string bounds, which is
the default (`StringBoundBytes = 16`), prunes blocks with them ([filter-rows.md](filter-rows.md)).
[encoded-forms.md](encoded-forms.md) shows how to read the dictionary codes yourself, and why you
rarely need to.

## Watch out

* An empty span is not a null. A null row gives an empty span and `GetLength(i) == 0`, exactly like
  an empty string. `IsValid(i)`, or `GetString(i)` returning `null`, tells them apart. Of the first
  eight visits, two have a null referrer.
* Comparisons are ordinal. A text predicate compares UTF-8 bytes, and culture-aware comparison is for
  the strings you materialise.
* A `string` member of a row costs the same allocation. `ToRecordsAsync()` allocates one string per
  row for it ([read-rows.md](read-rows.md)).

The figures come from one run of the sample on the demonstration files. Each timing is the best of
three passes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- text-columns
```
