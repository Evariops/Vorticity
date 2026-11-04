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
for each object the keys to write as typed columns inside the variant, the format's shredded form: every key whose
values of one class number 2 048 or more in the object becomes a column of its own, dense, or sparse where nine rows
in ten lack it, and the rarer keys stay in a binary residual, the path directory saying which granules hold them. No
saving in bytes decides: a question on a shredded key reads its values alone. Each object keeps a directory of the keys its rows hold, with their
counts and the granules they lie in, and a map of where each shredded key's buffers lie, so that a question on one key
reads that key's buffers alone. An append writes the directory of its own rows too, counted on another lane while it
writes them, so that a question on a key refutes the last minutes' objects and granules that lack it; set
`DatasetOptions.PathDirectoryOnAppend` to false for appends whose variants are never asked. A key the object left in its residual is read from the residual of the chunks that hold it, never every row's.
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

## Which keys a window holds

`PathsAsync` lists the paths of a variant column among the rows a scan keeps, each with the class a typed read takes
it as and the rows that hold it:

```csharp
VariantPaths paths = await dataset.Scan<LogRow>()
    .Where(r => r.Service == "checkout" & r.At >= from & r.At < to)
    .PathsAsync(r => r.Attributes);

foreach (VariantPath path in paths)
{
    Console.WriteLine(path);   // ['http.route'] String: 1840 rows
}
```

A path is a list of keys, `['cart']['items']` for a key inside an object, and a key keeps its dots. The class says
which read takes the values: `Integer` for `AsInt64()`, `Double` for `AsDouble()`, `String` for `AsString()`,
`Boolean` for `AsBool()`, `Timestamp` for `AsTimestamp()`, `Object` and `List` for `Has`, `Other` for the dedicated
readers of decimals, dates, uuids and bytes. The path with no key is the column's own values: how many rows hold an
object, a text, a null. A list is a path of its own, its elements are not.

The answer comes from the path directory each object keeps, at the cost of a count of the same rows: the objects a
count reads, and a directory each, read without opening an object the scan keeps whole.

* Every path a kept row holds is listed.
* A count is exact where the scan keeps every row of the granules the path lies in: the window holds whole objects,
  or the path lies in granules inside it.
* Elsewhere it is estimated from the share of rows the scan keeps there, and `Exact` is false. A path held only by
  rows of such a granule that the scan leaves out can be listed too, with an estimated count: the directory knows
  granules, not rows.
* `new PathsOptions { Exact = true }` counts the rows of those objects instead, every count exact.
* `VariantPaths.Complete` is false when an object's directory had left rare paths out under its size bound, as keys
  taken from data make it do, and no exact count replaced it.

Several columns are answered in one pass, `PathsAsync(r => (r.Attributes, r.Resource))`, and the tool path names
them, `scan.PathsAsync("Attributes")`. A variant column inside a nested record has no directory of its own: its kept
rows are counted.

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
some 0.57 µs a row in all, paid by a job that rewrites the rows anyway. The shredded rows took 12.2 MB where the
whole ones took 16.7 MB.

An append pays the count of its directory alone, a census of its rows beside the writer's work: on a million log
records appended 65 536 at a time (`vortex-queries --micro appenddirectory`, best of five), 0.37 µs of a processor a
row at one lane, and at sixteen 0.5 µs, of which 0.26 µs of time a row the writer's lanes do not hide: some 4 to 5 % of
a core at 100 000 rows a second. The directory of such an append takes about 12 KB, 0.46 % of its bytes.

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
