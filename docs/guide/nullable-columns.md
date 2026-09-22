# Nullable columns

Read a column that can hold nulls: the values, the bitmap that says which of them mean anything,
and the call that makes both unnecessary.

```csharp
await foreach (var (_, celsius, _) in scan)
{
    ReadOnlySpan<double> values = celsius.Values;
    ReadOnlySpan<ulong> valid = celsius.ValidityWords;
    if (valid.IsEmpty)
    {
        allValid++;
        total += Sum(values);
        continue;
    }

    for (int w = 0; w < valid.Length; w++)
    {
        ulong word = valid[w];
        int start = w << 6;
        int width = Math.Min(64, values.Length - start);
        if (word == ulong.MaxValue)
        {
            fullWords++;
            total += Sum(values.Slice(start, width));
            continue;
        }

        otherWords++;
        while (word != 0)
        {
            int bit = BitOperations.TrailingZeroCount(word);
            total += values[start + bit];
            word &= word - 1;
        }
    }
}
```

`Sum` is ten lines of `System.Numerics.Vector<double>` at the bottom of the sample.

## Values and validity

`Celsius` is declared `double?`, so its column is a `Column<double?>`:

* `Values` is the raw buffer, contiguous and 64-byte aligned, one slot per row. At a null row the
  slot holds whatever the decoder left there: never read it without the bitmap.
* `ValidityWords` is the bitmap, 64 rows per `ulong`: bit `i % 64` of word `i / 64` is row `i`,
  least significant first, and the bits past `Length` are zero. It is empty when `IsAllValid`,
  which is what the first branch above tests.
* `IsValid(i)` asks for one row, and the indexer `celsius[i]` returns a `double?`.
* `NullCount` is counted from the words by `BitOperations.PopCount`, 64 rows per instruction.

## Two idioms, by where the nulls are

**By the words**, as above: a batch with no null is summed with SIMD and no mask, a word of 64
valid rows likewise, and only a word that holds a null is walked bit by bit. **Per element**, when
the per-row branch does not matter:

```csharp
for (int i = 0; i < celsius.Length; i++)
{
    if (celsius[i] is double value)
    {
        total += value;
    }
}
```

What each costs on the demonstration file, where one temperature in fifty is missing:

```
by the words                         8.6 ms  sum 29400000.0; 0 batches all valid, 0 words all valid, 15625 words with a null
by the words, nulls filtered out    13.6 ms  sum 29400000.0; 123 batches all valid, 0 words all valid, 0 words with a null
per element                         14.3 ms  sum 29400000.0
SumAsync                             4.6 ms  sum 29400000.0
```

A null every fifty rows puts one in every word of 64, so the two fast paths never fire here and the
words idiom does its bit-by-bit walk everywhere; it still beats the per-element branch. With
`Where(r => r.Celsius != null)`, the compacted batches come back all valid and take the first
branch, but the filter is paid for. The idiom pays when nulls are rare or clustered, and the words
tell you which case you are in.

## Or let the scan do it

```csharp
double sum = await file.Scan<Reading>().SumAsync(r => r.Celsius);
```

`SumAsync`, `AvgAsync` and the other aggregates take a nullable column and skip its nulls, block
by block, inside the scan, on the encoded form where the encoding allows it and in parallel when the
session allows it. Here it is the fastest of the four and has nothing to get wrong
([aggregates.md](aggregates.md)).

## Watch out

* **`foreach (double v in celsius.Values)` silently counts garbage.** That is the wrong mean of
  [scan-a-table.md](scan-a-table.md): 29.4001 instead of 30.0000.
* **Nullability is checked at binding.** A `double` member over a nullable column is refused with
  `VortexSchemaException`; a `double?` member over a non-nullable column is fine, and its
  `ValidityWords` are always empty.
* **A null nested record has its own bitmap**: `Columns<TNested>.ValidityWords` and `IsValid`, on
  top of the bitmaps of its fields ([nested-records.md](nested-records.md)).
* **Counting nulls needs no scan.** `NullCount` summed over the batches gives 20 000, and so does
  `Where(r => r.Celsius == null).CountAsync()`, answered from the null counts of the zone maps with
  one read of 3 108 bytes ([filter-rows.md](filter-rows.md)).

The figures come from one run of the sample on the demonstration file of a million rows; each
timing is the best of three passes.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- nullable-columns
```
