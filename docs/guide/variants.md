# Variant columns

Keep values whose keys are data, such as OpenTelemetry attributes, in one column, and ask any key of it as fast as a
declared column.

```csharp
[VortexRecord]
public partial record struct Event(DateTimeOffset At, Variant Attributes, Variant? Body);

Event e = new(
    DateTimeOffset.UtcNow,
    Variant.Object(("http.route", "/orders/{id}"), ("http.response.status_code", 502L)),
    Variant.FromJson("""{"message": "upstream timed out", "retries": [1, 2]}"""));

long? status = e.Attributes["http.response.status_code"]?.AsInt64();   // 502
string? text = e.Body?["message"]?.AsString();                         // "upstream timed out"
```

In a dataset, a question names a key as it would a column:

```csharp
var routes = dataset.Scan<LogRow>()
    .Where(r => r.At < to && r.Attributes["http.response.status_code"].AsInt64() >= 500)
    .GroupBy(r => r.Attributes["http.route"].AsString())
    .Select(g => (g.Key, g.Count()));
```

```
status 502, message "upstream timed out", as text null
errors by route over two days: /orders/0 1113, /orders/1 1217, /orders/2 1173; 3503 rows read
errors by service, asked as text: 4 services, 5206 errors
```

## A value whose keys are data

`Variant` holds null, a primitive, a list or an object of keys to values, in the binary encoding the format's
variant type shares with Parquet's. A record declares a variant column as a `Variant` member, or `Variant?` for one
whose rows may hold no value at all.

* Build one with `Variant.Object((key, value), …)`, `Variant.Array(…)`, `Variant.FromJson(text)`, `Variant.From(value)`
  for each primitive, or the implicit conversions from `long`, `double`, `bool` and `string`.
* Walk it with `v["key"]` and `v[index]`, which give null where the key or the position is absent; `Has(key)`,
  `GetKeys()`, `Count` and `Kind` say what it holds, and `ToJson()` writes it back as text.
* Read a value with `AsInt64()`, `AsDouble()`, `AsString()`, `AsBool()`, `AsDecimal()`, `AsTimestamp()` and the others.
  They convert nothing: a value of another type reads as null, which is why the status above reads as text `null`.
  `AsDouble()` takes floats as well as doubles.
* Convert with `ToInt64()`, `ToDouble()` and `ToBool()`, which read a value of any type that converts: a text parsed,
  a number widened.

A key is whole, dots included: `http.route` is one key, not `route` inside `http`.

## In a dataset

Appends write variants as they come, and the jobs that rewrite rows anyway, the merges and the consolidations, choose
for each object the keys that pay and write them as typed columns inside the variant, the format's shredded form: a
key earns a column when what its values cost in the residual outweighs a column's overhead, some 200 bytes a chunk,
and the rare keys stay in a binary residual. Each object keeps a directory of the keys its rows hold, with their
counts, and a map of where each shredded key's buffers lie, so that a question on one key reads that key's buffers
alone. A key the object left in its residual is read from the residual of the chunks that hold it, never every row's.
Until a job rewrites an appended object, the first question that reads its variants shreds a copy in memory, once per
object and within the session's budget, and the questions after it read typed columns there too.

A shredded key then has what a declared column has: its own bytes, statistics over each granule, its sheet and where
its values live, so the same pruning and the same synopses answer it. `AsInt64()` on a key reads only the key's
integers; a source that sends the same key as text answers null there, and its rows are never read.

`DatasetOptions.ShreddedPaths` declares keys every object shreds whatever share of its rows holds them, and
`SetShreddedPathsAsync` changes them in a commit. `ShredOnAppend` makes appends shred the declared keys too, for a
dataset whose questions read rows before any job rewrites them.

A key can order the rows as a column does. A sort key component reads a key of a variant column in one class:

```csharp
SortKey =
[
    LayoutValue.Column("Service"),
    LayoutValue.Path("Attributes", "http.route", VariantClass.String),
    LayoutValue.Column("At"),
],
```

A row that does not hold the key, or holds it in another class, sorts as null, after every value. See
[dataset-layout.md](dataset-layout.md).

## On the tool path

A tool that learns its columns at run time names a key in a filter's text, `Attributes['http.route']`, and reads it
in the class its comparison asks for:

```csharp
Scan perService = dataset.Scan()
    .Where($"Attributes['http.response.status_code'] >= {500}")
    .GroupBy("Service")
    .Select("Service", "count()");
```

See [untyped-files.md](untyped-files.md).

## What it costs

On a million rows of six to eight attributes (`vortex-queries --micro shredwrite`, best of five), a job counts and
chooses the keys in 256 ns a row, and writes the rows shredded in 438 ns a row where it writes them whole in 128 ns:
some 0.57 µs a row in all, paid by a job that rewrites the rows anyway, never by an append. The shredded rows took
12.2 MB where the whole ones took 16.7 MB.

## Watch out

* A variant column's files read in Vortex Rust 0.85.0 and later, under the edition `core2026.08.3`
  ([editions.md](editions.md)).
* A key that is data, such as `user.4711.score`, is a key of its own each time: such keys stay in the residual, and a
  question on one reads the residual of the chunks that hold it.
* The strict `As…` readers are what makes a question read one typed column. Use `To…` only where a key really arrives
  in several types, since it reads every class the key holds.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- variants
```
