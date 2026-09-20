# Write a file

Write batches out, and see what the writer decided on your behalf.

## The schema

```csharp
DTypeArena types = new DTypeArena();
DType schema = types.Struct(
    ["city", "celsius"],
    [types.Utf8(Nullability.NonNullable),
     types.Primitive(PType.F64, Nullability.Nullable)],
    Nullability.NonNullable);
```

The arena owns the type nodes; a `DType` is an index into it. Keep the arena alive as long as the
types are in use — the writer, the batches and the file all point back into it.

## The batch

Three column shapes cover most files: a primitive, a text column, and a column with nulls.

```csharp
CanonicalArena arena = new CanonicalArena();

// A primitive column: one buffer, filled in place.
VortexBuffer degrees = arena.Allocate(rows * sizeof(double), 8, out Span<byte> degreeBytes);
Span<double> celsius = MemoryMarshal.Cast<byte, double>(degreeBytes);
for (int i = 0; i < rows; i++)
{
    celsius[i] = 10.0 + (i * 7919L % 3001) / 100.0;
}

// A text column is a view per row: four bytes of length, then the bytes themselves when they are
// twelve or fewer, otherwise a prefix and the offset of the rest in a data buffer.
VortexBuffer views = arena.Allocate(rows * 16, 8, out Span<byte> viewBytes);
for (int i = 0; i < rows; i++)
{
    Span<byte> view = viewBytes.Slice(i * 16, 16);
    int length = Encoding.UTF8.GetBytes(Cities[i % Cities.Length], view[4..]);
    BinaryPrimitives.WriteInt32LittleEndian(view, length);
}

// One bit per row says whether the value is there; one in a thousand is not.
VortexBuffer bitmap = arena.Allocate((rows + 7) / 8, 8, out Span<byte> bits);
bits.Fill(0xFF);
for (int i = 0; i < rows; i += 1_000)
{
    bits[i >> 3] &= (byte)~(1 << (i & 7));
}

int validity = arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bitmap, 0);
int city = arena.AddVarBinView(schema.GetField(0), rows, Validity.NonNullable, views, [VortexBuffer.Empty]);
int temperature = arena.AddPrimitive(schema.GetField(1), rows, Validity.Bitmap(validity), PType.F64, degrees);
int root = arena.AddStruct(schema, rows, Validity.NonNullable, [city, temperature]);
```

`Validity` says which of four states a column is in: `NonNullable` (the type forbids nulls),
`AllValid` (nullable, none here), `AllInvalid`, or `Bitmap(node)` (a bit per row). The first two
cost nothing at all.

Every city here is short enough to live inside its view, so the data buffer is empty —
`[VortexBuffer.Empty]`. A value over twelve bytes needs its bytes in that buffer, its first four
bytes copied into the view as a prefix, and the buffer index and offset written at bytes 8 and 12 of
the view.

## Writing it

```csharp
await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema))
{
    using RecordBatch batch = new RecordBatch(arena, root, 0);
    await writer.WriteAsync(batch);
    report = await writer.CompleteAsync();
}
```

`WriteAsync` takes as many batches as you have; the third argument of a `RecordBatch` is the file row
its first row is, so batches follow one another. `writer.PreferredBatchRows` is the size the writer
would rather have — 8 192 — and handing it more at once is fine, as here.

`CompleteAsync` is what makes the file a file: statistics, zone maps, indexes, footer. A writer
disposed without it, or after `Abandon()`, leaves nothing usable behind.

## What the writer decided

```csharp
Console.WriteLine($"{report.RowCount} rows in blocks of {report.BlockRows}, " +
    $"{report.ChunkRows.Count} chunks, {report.Bytes.Total} bytes");
foreach (ColumnWriteReport column in report.Columns)
{
    Console.WriteLine($"  {column.Path}: {string.Join(", ", column.Encodings)}");
}
```

```
200000 rows in blocks of 8192, 2 chunks, 396025 bytes
  data 393172, statistics 160, zone maps 1136, indexes 101, footer 1456
  city: Dict, Dict
  celsius: Dict, Alp
  2800000 bytes of values became 396025
```

The report is the only place that says what was chosen. Five city names became a dictionary; the
temperatures became a dictionary and then a floating-point encoding. Nothing was sampled or guessed:
the writer priced the candidates on the block it was holding.

`report.Indexes` says the same for indexes, each one `Built` or `Abandoned` with the reason — the
default policy builds what it can pay for out of a budget of a tenth of the file — 100 per mille —
and gives up the rest. [indexes.md](indexes.md) is that subject.

## Appending

```csharp
await using VortexFileWriter appender = await VortexFileWriter.AppendAsync(path);
Console.WriteLine($"appending after {appender.RowCount} rows");   // 196608, not 200000
await appender.WriteAsync(batch);
WriteReport appended = await appender.CompleteAsync();            // 210000
```

An append rewinds to the last whole block and rewrites the tail: after 200 000 rows written in
blocks of 8 192, it starts again at 196 608 and the 3 392 rows of the partial block are written
afresh along with the new ones. Give the new batch a `startRow` of `appender.RowCount`.

**Nothing else may hold the file.** An append opens it for writing, so a `VortexFile` still open
over the same path makes it fail with `IOException`.

## Watch out

* The arena is reusable: `Reset()` gives its buffers back and lets the next batch start clean. A
  writing loop should reset rather than allocate a new arena per batch.
* A `RecordBatch` must be disposed, and disposing it does not reset the arena.
* The options that change the file are in [options.md](options.md): compression, block size, write
  profile, target edition, statistics, index budget.
* For a file whose tail was torn by a crash mid-append, see `append-and-repair.md`.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- write-a-file
```
