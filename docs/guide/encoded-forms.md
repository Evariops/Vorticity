# Encoded forms

Read a column the way the file stores it, as a dictionary, as runs or as a single constant, without
decoding it. This page also shows when `GroupBy` does that for you.

```csharp
double[] totalByCity = new double[Demo.Cities.Length];
await foreach (var (_, celsius, city) in file.Scan<Reading>())
{
    ReadOnlySpan<double> values = celsius.Values;
    if (city.Encoding == ColumnEncoding.RunEnd)
    {
        RunEndView<string> runs = city.AsRunEnd();
        ReadOnlySpan<uint> ends = runs.RunEnds;
        for (int r = 0, start = 0; r < runs.RunCount; r++)
        {
            int end = (int)ends[r];
            double sum = 0;
            for (int i = start; i < end; i++)
            {
                sum += celsius.IsValid(i) ? values[i] : 0;
            }

            totalByCity[IndexOf(runs.Values[r])] += sum;
            start = end;
        }
    }
    else
    {
        for (int i = 0; i < city.Length; i++)
        {
            totalByCity[IndexOf(city[i])] += celsius.IsValid(i) ? values[i] : 0;
        }
    }
}
```

The city is looked up once per run instead of once per row. `IndexOf` compares a UTF-8 span with the
eight city names.

## What the demonstration files hold

`Column<T>.Encoding` says how a delivered column holds its values, after the scan's pruning and before
anything decodes them. The sample counts it for every batch of both files:

| batches of | column | canonical | dictionary | run-end | constant |
|---|---|---|---|---|---|
| readings | `Day` | 0 | 0 | 16 | 1 |
| readings | `Celsius` | 1 | 16 | 0 | 0 |
| readings | `City` | 0 | 0 | 17 | 0 |
| visits | `Referrer` | 0 | 5 | 0 | 0 |
| visits | the seven other leaves | 5 each | 0 | 0 | 0 |
| readings, `Where(r => r.Day == 500)` | `Day`, `City` | 1 each | 0 | 0 | 0 |
| readings, `Where(r => r.Day == 500)` | `Celsius` | 0 | 1 | 0 | 0 |

`Day` keeps the same value for a thousand rows and `City` for seven, so both come out as runs. The last
576 rows of `Day` all hold day 999, and that small tail chunk is a constant. `Celsius` takes 400
values, and its dictionary holds 393 of them per batch, one of which is the null. `Referrer` is a
dictionary of thirteen URLs. Only these four forms are surfaced. Bit-packing, frame of reference,
ALP, FSST and the general-purpose compressors are decoded by the scan and arrive as `Canonical`. The
writer chooses per column and per chunk, and its `WriteReport` lists what it chose
([writer-options.md](writer-options.md)).

## The views

| call | returns | on any other encoding |
|---|---|---|
| `AsDictionary()` | `DictionaryView<T>`: `Codes`, one `uint` per row, `Values`, the distinct values as a `Column<T>` in code order, and `Cardinality` | `InvalidOperationException` |
| `AsRunEnd()` | `RunEndView<T>`: `RunEnds`, the exclusive end of each run in the batch, `Values`, one per run, and `RunCount` | `InvalidOperationException` |
| `AsConstant()` | the one value | `InvalidOperationException` |
| `AsCanonical()` | the column decoded once into contiguous memory | returns the column itself |

These views are described in
[the public API design](../design/14-public-api.md#54-encoded-views-and-the-selection). Check
`Encoding` before asking for one. `Values`, the indexer and `GetString` decode an encoded column
first, once per batch, which is what a consumer that does not care about encodings gets. A null row
of a dictionary column has code 0 and its validity bit cleared, and the dictionary's own values may
hold the null too, as they do for `Celsius`.

Grouping by code, over the dictionary of `Referrer`:

```csharp
DictionaryView<string?> dict = referrer.AsDictionary();
ReadOnlySpan<uint> codes = dict.Codes;
for (int i = 0; i < codes.Length; i++)
{
    if (!referrer.IsValid(i))
    {
        nulls = (nulls.Visits + 1, nulls.Duration + duration[i]);
        continue;
    }

    visitsByCode[codes[i]]++;
    durationByCode[codes[i]] += duration[i];
}
```

This is an array indexed by code, one increment per row and no hashing. The sample then names each
code once per batch with `dict.Values.GetString(code)`.

## And why you rarely write it

```csharp
Scan<CityTotal> byCity = file.Scan<Reading>()
    .GroupBy(r => r.City)
    .Select(g => (g.Key, g.Sum(r => r.Celsius)))
    .As<CityTotal>();
```

The group by handles the dictionary case by code, the run-end case by run and the canonical case by
hash, then merges the chunks by value, so the answer has one row per city whatever the batches held.
It is one line, it treats a null key as a group of its own, and it uses the session's parallelism
([aggregates.md](aggregates.md)). Measured against the loops above:

```
City by run, by hand        8 groups,   14.9 ms
GroupBy(r => r.City)        8 groups,    7.8 ms
  8 of 8 cities agree, Paris 3659129.4; the GroupBy decoded 1 blocks
Referrer by code, by hand  14 groups,    1.1 ms
GroupBy(v => v.Referrer)   14 groups,    0.5 ms
  14 of 14 groups agree, null referrers 25000; the GroupBy decoded 13 blocks
```

Both give the same answers, and the operator wins on both keys. Write the loop yourself only when a
measurement says it pays. "Decoded" counts the blocks where a column reached the plain form: the one
block of readings whose `Celsius` arrives canonical, and all 13 blocks of the visits, whose
`DurationMs` always does.

## Watch out

* The forms are per batch. A dictionary's codes and a run's values only mean something in the batch
  that holds them, so map them through that batch's `Values`, as both loops do.
* A filter changes the form. Compacting a batch decodes its run-end columns, which is why `Day` and
  `City` come back canonical under `Day == 500` while `Celsius` stays a dictionary. With
  `ScanOptions.Compact = false` the blocks arrive whole ([selection.md](selection.md)).
* A view is borrowed like the column it comes from, and is valid inside the loop body only.

The figures come from one run of the sample on the demonstration files. Each timing is the best of
three passes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- encoded-forms
```
