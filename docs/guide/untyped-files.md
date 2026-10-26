# Untyped files

Read a file whose schema is unknown when the program is compiled: look at its schema, name its columns
as strings, and write filters as text or pass sets of values.

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
Console.WriteLine($"{file.Schema}, {file.RowCount} rows");

int threshold = 900;
long rows = 0;
double total = 0;
await foreach (BatchView batch in file.Scan("Celsius", "Day").Where($"Day >= {threshold}"))
{
    Column<int> day = batch.Column<int>("Day");
    Column<double?> celsius = batch.Column<double?>("Celsius");
    if (rows == 0)
    {
        Console.WriteLine($"asked for Celsius, Day; the batch holds {batch.Schema}, first day {day[0]}");
    }

    for (int i = 0; i < celsius.Length; i++)
    {
        total += celsius[i] ?? 0;
    }

    rows += batch.RowCount;
}
```

```
struct{Day: i32, Celsius: f64?, City: utf8}, 1000000 rows
asked for Celsius, Day; the batch holds struct{Day: i32, Celsius: f64?}, first day 900
Day >= 900: 100000 rows, 2940000.0 degrees in all
```

This is the tool path, used by vxdump, ad hoc queries and tests. It is the one place where columns are
named by strings, because there is no other way to name them. When the schema is known, a record is
the better tool ([records.md](records.md)), since the compiler then checks every name and every
literal.

## The schema

`file.Schema` is a `VortexSchema`, read at open, so asking for it costs nothing. It is a list of
`VortexField`, each a name and a `VortexType`, and it prints on one line, which is what a log or an
error message wants. A type is a value, and a tree:

| `VortexType` member | for |
|---|---|
| `Kind` | `Primitive`, `Utf8`, `Struct`, `List`, `Extension`, and the rest |
| `IsNullable`, `Nullable`, `NonNullable` | nullability, and the same type with or without it |
| `Fields` | a struct's fields |
| `ElementType`, `FixedSize` | a list's elements |
| `Precision`, `Scale` | a decimal |
| `ExtensionId`, `StorageType`, `ExtensionMetadata` | an extension: a date, a timestamp, a uuid, or your own type |

The visits file, walked field by field:

```
  Id: uuid (Extension, extension vortex.uuid stored as fsl(u8, 16))
  StartedAt: timestamp(µs) (Extension, extension vortex.timestamp stored as i64)
  DurationMs: i32 (Primitive)
  Referrer: utf8? (Utf8, nullable)
  Pages: list(i32) (List, elements i32)
  Origin: struct{Country: utf8, City: utf8?} (Struct, 2 fields)
```

`schema.IndexOf("Origin.City")` finds a nested field by its dotted path and returns its index within
its own struct, 1 here. It returns -1 when there is no such field, and nothing throws. A schema can
also be written by hand as a collection expression, and schemas compare by value:

```csharp
VortexSchema declared = [("Day", VortexType.Int32), ("Celsius", VortexType.Float64.Nullable), ("City", VortexType.Utf8)];
```

## Columns by name

`file.Scan("Celsius", "Day")` reads those columns, `file.Scan()` reads all of them, and a dotted path
reaches a nested field. A batch holds its columns in the file's order, not in the order you named
them, because that is the order the projection reads them in. Above, `Celsius` was named first and the
batch holds `Day` first. Read columns by name, as in `batch.Column<int>("Day")`, and the order does
not matter. A nested path keeps its parents: `Scan("Origin.City", "Id")` delivers
`struct{Id: uuid, Origin: struct{City: utf8?}}`.

`Column<T>` takes the .NET type the column maps to, and throws `VortexSchemaException` when it does
not fit, including when you ask for a non-nullable type over a nullable column. The columns are the
same `Column<T>` as on the typed path, and they are borrowed in the same way.

## Filters as text

There are two forms, joined with `and` when you use both:

```csharp
VortexExpr parsed = VortexExpr.Parse("Day >= 900 and City = 'Paris' and Celsius is not null");
VortexExpr combined = VortexExpr.Parse("City in ('Lyon', 'Nice')") & !VortexExpr.Parse("Celsius < 40");
```

```
parsed: Day >= 900 and City = 'Paris' and Celsius is not null, 12216 rows
combined: City in ('Lyon', 'Nice') and not (Celsius < 40), 61426 rows
```

The grammar covers comparisons of a column with a literal or with another column, `and`, `or`, `not`,
parentheses, `in (…)`, `is [not] null`, `like`, `starts with` and `contains`. Text outside it throws
`FormatException`. `&`, `|` and `!` combine parsed filters, and a `VortexExpr` prints back in the
same grammar.

The interpolated form, `Where($"Day >= {threshold}")`, is the one to use with values. Each hole
reaches the filter as a typed value rather than being formatted into the text, and it is checked
against the column it is compared with. A hole of the wrong type throws at `Where`, naming both:

```
a string hole against an integer column: Column 'Day' is a column of i32, which string does not map to: a string reads a utf8 column.
a column the file does not have: The file has no column 'Month'; its schema is struct{Day: i32, Celsius: f64?, City: utf8}.
```

A number compared with a decimal column is scaled exactly from its digits, and text compared with a
date, time, timestamp, uuid or decimal column is parsed in the column's type. Nulls follow
three-valued logic, so `not (Celsius < 40)` does not return a row whose `Celsius` is null
([three-valued logic](../design/08-semantics.md#3-three-valued-logic)).

A variant column is walked by path. Each key is quoted whole, so `'http.route'` is one key with a dot
in it, and an index is a number. What the path is read as follows it, or, when nothing follows, the
literal decides: an integer reads it as `AsInt64`, a float as `AsDouble`, text as `AsString` and a
boolean as `AsBool`. `exists` asks whether the path is there, and is never unknown:

```csharp
file.Scan().Where($"Attributes['http.response.status_code'] >= 500");          // integers only
file.Scan().Where($"Attributes['http.response.status_code'].ToInt64() >= 500"); // "502" too
file.Scan().Where($"Attributes['http']['route'] = {route} and Attributes['user.id'] exists");
file.Scan().Where($"Attributes['at'].AsTimestamp() >= {since}");
```

The text `"502"` is not the integer `502`: only `ToInt64()` reads it as one. A path absent, or of
another type than the one read, is null, so the row is not returned. A hole after a path without a
read is read as the hole's type says, and a timestamp needs `.AsTimestamp()` written. The same
paths, with the same rules, are the typed path's `r.Attributes["http.route"].AsString()`
([records.md](records.md)).

A set of values does not need text at all. `VortexExpr.In` takes them as they are, a few or a
million, and combines with the other forms:

```csharp
int[] days = new int[200];
for (int i = 0; i < days.Length; i++)
{
    days[i] = i * 5;
}

VortexExpr chosen = VortexExpr.In("Day", days) & VortexExpr.Parse("City = 'Paris'");
```

```
a set of 200 days in Paris: 25004 rows
```

The values are copied when the filter is built, so the array can be reused at once. They are checked
against the column when a scan takes the filter, as a hole is: a number goes to any numeric column, a
value of another type to a column of that type, and a value the column cannot hold, or a null, matches
no row.

## Aggregates by name

A group by takes its key and its results as text too, so a program can count by a column it learns
at run time:

```csharp
Scan perService = file.Scan()
    .Where($"Status >= {500}")
    .GroupBy("Service")
    .Select("Service", "count()", "sum(Bytes)", "avg(Duration)");
await foreach (BatchView groups in perService)
{
    // Columns named "Service", "count()", "sum(Bytes)" and "avg(Duration)".
}

Scan totals = file.Scan().Aggregate("count()", "max(Bytes)");
```

A result is a column of the key, or one of `count()`, `count_distinct(c)`, `sum(c)`, `min(c)`,
`max(c)` and `avg(c)`. A sum of integers is a 64-bit integer, signed or not, a sum of floats and a
mean are doubles, and a minimum, a maximum and a key keep their column's type. Each result column is
named by the text that asked for it. The engine and the plan are the typed path's: on a dataset, the
same question reads the same rows.

## Watch out

* A non-nullable type over a nullable column is refused. `batch.Column<double>("Celsius")` throws
  `VortexSchemaException`: *Column 'Celsius' is f64?, which is nullable; read it as Column<double?>,
  whose accessors say which rows are null.* Ask for `Column<double?>`, whose indexer returns `null`.
* Names are matched exactly, case included, and an unknown name throws `VortexSchemaException` when
  the scan or the filter is built.
* The tool scan is single-use, like the typed one. Its sinks are the borrowed batches,
  `ToBatchesAsync`, `CountAsync`, `AnyAsync`, `ExplainAsync` and the group by. Rows, `OrderBy`, the
  key cursor, and the operators on groups (a filter on their results, an order, a window) need a
  record type.
* The same scan exists on a dataset ([datasets.md](datasets.md)).

The tool path is described in [the public API design](../design/14-public-api.md#58-the-tool-path),
and [vxdump.md](vxdump.md) is a program built on it.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- untyped-files
```
