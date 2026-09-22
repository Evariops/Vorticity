# Write text

Append text without allocating a string: UTF-8 bytes as they are, a `string` transcoded once, a
number or a date formatted straight into the column, and a long value written in place.

```csharp
ColumnsBuilder<Entry> b = writer.Builder<Entry>();

b.StartedAt.Append(now);
b.City.Append("Paris"u8);                                  // UTF-8 bytes, no transcoding
b.Note.AppendNull();

b.StartedAt.Append(now.AddMinutes(1));
b.City.Append(cityString);                                 // a string, transcoded by Utf8.FromUtf16
b.Note.Append(DateOnly.FromDateTime(now));                 // an IUtf8SpanFormattable, formatted in place

b.StartedAt.Append(now.AddMinutes(2));
b.City.Append("Nice"u8);
b.Note.Append(Guid.Parse("0199f0c4-7d2a-7c3e-9a51-3f6b2c1d4e5f"));

b.StartedAt.Append(now.AddMinutes(3));
b.City.Append("Lille"u8);
Span<byte> dst = b.Note.GetSpan(Encoding.UTF8.GetMaxByteCount(longText.Length));   // one value, written in place
int written = Encoding.UTF8.GetBytes(longText, dst);
b.Note.Commit(written);

b.StartedAt.Append(now.AddMinutes(4));
b.City.Append("Nantes"u8);
Span<byte> iso = b.Note.GetSpan(10);
DateOnly.FromDateTime(now).TryFormat(iso, out int length, "O");   // a format of your choosing, in place
b.Note.Commit(length);
```

`Entry` is `[VortexRecord] public partial record struct Entry(DateTime StartedAt, string City, string? Note)`.
`b.City` is a `ColumnBuilder<string>` and `b.Note` a `ColumnBuilder<string?>`: the nullable one takes
the same appends, plus `AppendNull` and `AppendNulls(count)`. Read back, the rows are:

```
  08:30 Paris: null
  08:31 Lyon: "09/22/2026"
  08:32 Nice: "0199f0c4-7d2a-7c3e-9a51-3f6b2c1d4e5f"
  08:33 Lille: 20008 characters ending in 'the end'
  08:34 Nantes: "2026-09-22"
```

## What each append does

| append | what it does |
|---|---|
| `Append(ReadOnlySpan<byte>)` | copies the bytes as they are, after checking they are valid UTF-8 |
| `Append(ReadOnlySpan<char>)` | transcodes with `Utf8.FromUtf16`, vectorised, straight into the column's buffer; a lone surrogate becomes U+FFFD. A `string` converts to it |
| `Append<TValue>(TValue)` | formats an `IUtf8SpanFormattable` (a number, a date, a `Guid`) with `TryFormat` into the column's buffer, with the default format and the invariant culture |
| `GetSpan(n)`, `Commit(length)` | hands out exactly `n` bytes of the column's buffer for one value; `Commit` appends the first `length` of them, after the UTF-8 check |

A value of twelve bytes or fewer is stored inside its sixteen-byte view; a longer one goes to the
data buffer, with its first four bytes in the view. The builder does both, and the caller never sees
a view.

## What it costs

The sample times the appends alone, a million per form, on a column of city names and numbers:

```
per value, 1000000 appends: UTF-8 bytes 6.2 ns, a string 8.8 ns, an int formatted 7.5 ns
```

Keeping the names as UTF-8 byte arrays saves the transcoding, about 30 % of the append here.
Formatting a number in place costs about what copying its bytes would, and allocates nothing.

## Watch out

* **The formatted append uses the default format.** Under the invariant culture a `DateOnly` comes
  out as `09/22/2026`, not as an ISO date. For another format, write it yourself through
  `GetSpan` and `TryFormat`, as the last row does.
* **A text column refuses bytes that are not UTF-8**, with `VortexSchemaException`: *The column is
  utf8: the bytes appended to it must be valid UTF-8.* Nothing is appended. A binary column
  (`ColumnBuilder<ReadOnlyMemory<byte>>`) takes any bytes.
* **`GetSpan(n)` gives exactly `n` bytes.** Ask for the most the value can take:
  `Encoding.UTF8.GetMaxByteCount(text.Length)` for a string.
* **`AppendNull` exists on every text builder**, and throws `VortexSchemaException` on a column that
  is not nullable.
* A `char` member is refused: it is not a type the format has ([records.md](records.md)).
* A text column's zones carry string bounds by default, so equality and prefix filters on it prune:
  `StringBoundBytes` in [writer-options.md](writer-options.md).
* Reading text back without a string is [text-columns.md](text-columns.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- write-text
```

The figures above come from that run.
