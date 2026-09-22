# Write without a record

Write a file whose columns are known only when the program runs: a schema built as a value, and
column builders asked for by position or by name.

```csharp
VortexSchema schema = [("id", VortexType.Int64), ("payload", VortexType.Binary.Nullable), ("origin", VortexType.Struct([("country", VortexType.Utf8)]))];
WriteReport report;
await using (VortexFileWriter writer = session.CreateWriter(path, schema))
{
    ColumnsBuilder b = writer.Builder();
    b.Column<long>(0).Append(ids);                                           // by position
    ColumnBuilder<ReadOnlyMemory<byte>?> payloads = b.Column<ReadOnlyMemory<byte>?>("payload");   // by name
    ColumnBuilder<string> country = b.Struct(2).Column<string>("country");  // a nested field, through its struct
    for (int i = 0; i < Rows; i++)
    {
        BitConverter.TryWriteBytes(payload, i);
        if (i % 3 == 0) payloads.AppendNull();
        else payloads.Append(payload.AsSpan(0, 16 + (i % 49)));
        country.Append(i % 2 == 0 ? "FR"u8 : "DE"u8);
    }

    await writer.WriteAsync(b, ct);
    report = await writer.CompleteAsync(ct);
}
```

```
100000 rows, 226508 bytes; id Sequence x13; payload Zstd x13; origin {country: Dictionary} x13
schema struct{id: i64, payload: binary?, origin: struct{country: utf8}}
read back by name: ids summing to 104999950000, 33334 null payloads, 2666523 payload bytes
```

## What happens

* **A schema is a value**: a collection expression of `(name, VortexType)` pairs. `VortexType` has a
  member per type the format has (`Int64`, `Utf8`, `Binary`, `Decimal(p, s)`, `List(element)`,
  `Struct(fields)`, `Timestamp(unit, zone)`, `Uuid` and the rest), and `.Nullable` makes any of them
  nullable.
* **`Builder()` is the untyped view of the writer's builder**, the same rows `Builder<T>()` would
  show through a record. `Column<T>(index)` and `Column<T>(name)` hand out a column's builder; `T`
  is the .NET type the column maps to: `long` for an i64, `ReadOnlyMemory<byte>?` for a nullable
  binary, `string` for text. The type is checked once, when the builder is asked for.
* **A struct column is a builder of its own**: `Struct(index)` returns a `ColumnsBuilder` over its
  fields, whose rows count as the parent's.
* From there everything is as with a record: the appends of [write-a-file.md](write-a-file.md),
  [write-text.md](write-text.md) and [write-nulls.md](write-nulls.md), then `WriteAsync` and
  `CompleteAsync`.

Reading such a file back uses the same names: `file.Scan("id", "payload")` yields `BatchView`s whose
`Column<long>("id")` and `Column<ReadOnlyMemory<byte>?>("payload")` are the typed columns
([untyped-files.md](untyped-files.md)).

## Watch out

* **A type that does not fit throws when the builder is asked for**, with `VortexSchemaException`:
  *Column 'id' is a column of i64, which System.Int32 does not map to: a Int32 reads a i32 column
  exactly; convert after the read.* There is no conversion on the way in.
* **Names match exactly.** `Column<T>(name)` compares ordinally, and a name the schema does not have
  throws: *The builder has no column 'identifier'; its schema is struct{…}.* A record binds with a
  case-insensitive fallback; the untyped builder does not.
* **A binary value can be written in place.** `ColumnBuilder<ReadOnlyMemory<byte>?>` offers
  `GetSpan(n)` and `Commit(length)` beside `Append(bytes)`, and `AppendNull()` or
  `AppendNulls(count)` for the rows without a payload, as the text builders do.
* The report reads a struct column field by field, `{country: Dictionary}`, the way it reads a list
  by its elements ([write-lists-and-records.md](write-lists-and-records.md)).
* The mapping between types and .NET types is [07-dotnet-mapping.md](../design/07-dotnet-mapping.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- write-without-a-record
```

The figures above come from that run.
