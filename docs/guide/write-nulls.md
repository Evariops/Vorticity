# Write nulls

Append a missing value one at a time, in bulk with a bitmap, or many at once, and pay for the
bitmap only when there is a null to record.

```csharp
ColumnsBuilder<Sample> b = writer.Builder<Sample>();
b.Day.Append([1, 1, 1, 2, 2, 2, 2, 2]);

b.Celsius.Append(21.5);
b.Celsius.AppendNull();
b.Celsius.Append((double?)null);
b.Celsius.Append(values, validity);                        // bulk: bit i of the bitmap for value i, 1 for a value

b.Station.Append("Paris-Montsouris"u8);
b.Station.AppendNulls(7);                                  // a nullable text column takes nulls the same way
```

`Sample` is `[VortexRecord] public partial record struct Sample(int Day, double? Celsius, string? Station)`,
`values` is `[18.5, 19.0, 19.5, 20.0, 20.5]` and `validity` is `[0b11011]`: the third value is
missing. The eight temperatures read back as:

```
21.5 null null 18.5 19.0 null 20.0 20.5
```

## What happens

* `ColumnBuilder<double?>` takes `Append(double)`, `Append(double?)`, `AppendNull()`,
  `AppendNulls(count)` and the bulk `Append(values, validity)`. The text builder has the same null
  appends ([write-text.md](write-text.md)).
* **The bitmap is lazy.** A nullable column has none until its first null. A bulk append whose bitmap
  has every bit set, for the rows it covers, allocates nothing either.
* On disk the validity is decided per chunk: a chunk without a null carries none.

## What it costs

A million rows, with `Celsius` written five ways:

| `Celsius` | bytes | null count in the statistics | batches carrying a bitmap |
|---|---|---|---|
| no null, `Append(values)` | 1 552 228 | 0 | 0 of 123 |
| no null, `Append(values, validity)` with every bit set | 1 552 228 | 0 | 0 of 123 |
| one null, at row 500 000 | 1 552 356 | 1 | 4 of 123 |
| one row in fifty | 1 564 708 | 20 000 | 123 of 123 |
| every row, `AppendNulls` | 371 124 | 1 000 000 | 123 of 123 |

A nullable column with no null costs exactly what a column without nulls does. One null costs 128
bytes, and only the four blocks of the chunk that holds it carry a bitmap. A column that is entirely
null is written as runs, and most of the remaining 371 KB is the other two columns.

The null count comes back three ways: from the file's statistics (`file.Statistics[1].TryGetNullCount`),
per batch from `Column<T>.NullCount`, and as `IsAllValid` on a batch without a bitmap. Reading
nullable columns is [nullable-columns.md](nullable-columns.md).

## Watch out

* **A non-nullable column refuses a nullable builder.** Asking for `ColumnBuilder<int?>` over `Day`
  throws `VortexSchemaException`: *Column 'Day' is i32, which is not nullable; ask for a builder of
  Int32 instead of Nullable`1.* The other way round is fine: a `ColumnBuilder<double>` over a nullable
  column writes it all valid.
* **The value under a null is not a value.** Whatever the slot holds is written, and a reader goes by
  the bitmap.
* **The bitmap must cover the values.** `Append(values, validity)` needs at least one bit per value,
  and throws `ArgumentException` otherwise; bits past the last value are ignored.
* **`AppendNull` on a non-nullable text column throws** `VortexSchemaException`; the method exists on
  every text builder because nullability is not part of the builder's type there.
* A null nested record is [write-lists-and-records.md](write-lists-and-records.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- write-nulls
```

The figures above come from that run.
