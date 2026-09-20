# Read the schema

What columns a file has, of what types, before reading a row.

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
DType schema = file.Schema;
Console.WriteLine($"{schema.FieldCount} columns, {file.RowCount} rows, tabular: {file.IsTabular}");
for (int i = 0; i < schema.FieldCount; i++)
{
    DType field = schema.GetField(i);
    Console.WriteLine($"  {schema.GetFieldName(i)}: {DTypeFormatter.Format(field)}" +
        (field.IsNullable ? ", may be null" : string.Empty));
}
```

```
2 columns, 1000000 rows, tabular: True
  day: i32
  celsius: f64
```

The schema and the row count come from the open. Asking for them costs nothing.

## A type is a tree

`DType` is one node of a tree, not a name. A struct has `FieldCount` fields reached by `GetField(i)`
and named by `GetFieldName(i)`; a list has an `ElementType`; a map has a `KeyType` and a
`ValueType`; an extension has a `StorageType` and an `ExtensionId`. `Kind` says which of those you
are holding, and `PType` narrows a primitive to `I32`, `F64` and the rest.

`DTypeFormatter.Format` renders a whole tree in one line — `struct{day: i32, celsius: f64}` — which
is what you want in a log or an error, and never what you want to parse.

**`IsTabular` is not a given.** A Vortex file's root may be any type. When it is not a struct, there
are no columns to name and a projection has nothing to select.

## Finding a column by name

```csharp
int index = schema.IndexOfField("celsius");
```

`-1` when there is no such field. Nothing throws. The scan builder takes names, so you rarely need
the index; you need it when you want `ProjectFields`, or the field's type before scanning.

## What the file already knows about each column

```csharp
if (file.HasFileStatistics)
{
    FileStatistics statistics = file.Statistics;
    for (int i = 0; i < statistics.FieldCount; i++)
    {
        FieldStatistics field = statistics.GetField(i);
        string nulls = field.TryGetNullCount(out ulong count) ? count.ToString() : "unknown";
        string sorted = field.TryGetIsSorted(out bool isSorted) ? isSorted.ToString() : "unknown";
        Console.WriteLine($"  {schema.GetFieldName(i)}: nulls {nulls}, sorted {sorted}, " +
            $"bounds known: {field.HasMin && field.HasMax}");
    }
}
```

```
  day: nulls 0, sorted True, bounds known: True
  celsius: nulls 0, sorted False, bounds known: True
```

These are the file's own summaries, written by the writer and read at open: minimum, maximum, sum,
null count, whether the column is sorted or constant. Every one of them is optional, which is why
each is a `TryGet`: a writer that did not compute one leaves it out, and `HasFileStatistics` is
false for a file written with `FileStatistics = false`.

They are worth asking before a scan. A column the file says is sorted can be walked in key order
(`keys-in-order.md`); a minimum and a maximum are what let a predicate be
answered without reading rows ([filter-rows.md](filter-rows.md)).

## Watch out

* **A statistic is a claim the file makes about itself.** Open with `VerifyStatistics = true` to
  have the reader check them against what it decodes, at the cost of the check.
* Field names are UTF-8 in the file. `GetFieldName` allocates a string; `GetFieldNameUtf8` and the
  `ReadOnlySpan<byte>` overloads do not, and `"celsius"u8` is how you compare without allocating.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- read-the-schema
```
