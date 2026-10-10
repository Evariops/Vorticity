# Diagnostics

What the compiler tells you about the way you use the library, and how to fix each case.

The `Vorticity.Generators` package ships the `[VortexRecord]` generator and six analyzers, which
report eleven diagnostics together:

* VX1001 to VX1004 are warnings about code that compiles but would misbehave or throw.
* VX1005 to VX1008 are errors about a `[VortexRecord]` type the generator cannot implement. The
  generator emits nothing for a type that has one.
* VX1009 to VX1011 are errors about a query that would throw when it is built, or that the compiler
  refuses without saying how to fix it.

Each diagnostic's help link names its section on this page.

| id | severity | reported when |
|---|---|---|
| [VX1001](#vx1001) | warning | a span borrowed from a batch is kept in a variable that outlives the loop body |
| [VX1002](#vx1002) | warning | an owned `RecordBatch` is never disposed |
| [VX1003](#vx1003) | warning | a hole of an interpolated `Where` has a type no column maps to |
| [VX1004](#vx1004) | warning | one scan selects rows both by range and by indices |
| [VX1005](#vx1005) | error | a record member has no column type |
| [VX1006](#vx1006) | error | a record type, or a type containing it, is not `partial` |
| [VX1007](#vx1007) | error | a type cannot be a record at all |
| [VX1008](#vx1008) | error | a record member cannot be filled when rows are read |
| [VX1009](#vx1009) | error | a component of a group key is not a column |
| [VX1010](#vx1010) | error | a record does not match the values of a selection |
| [VX1011](#vx1011) | error | several values are read without a record |

## VX1001

A borrowed span outlives its batch. This is a warning.

Everything a scan delivers in the body of `await foreach` points into buffers that the next
`MoveNextAsync` reuses. The compiler already keeps `Columns<T>` and `Column<T>` inside the body,
because they are `ref struct`s. What it does not see is a span taken from them and assigned to a
variable declared outside the body. The analyzer flags that assignment, for a span, a column, a
`BatchView`, a `Selection`, a `DictionaryView<T>` or a `RunEndView<T>`.

```csharp
ReadOnlySpan<double> last = default;
await foreach (var (_, celsius, _) in file.Scan<Reading>())
{
    last = celsius.Values;
}
```

```
warning VX1001: 'celsius.Values' is borrowed from a column of the current batch and is valid only inside the body of the enumeration that produced it; 'last' is declared outside that body
```

To fix it, declare the variable inside the body, or keep a copy you own: `celsius.ToArray()` for one
column, `columns.ToOwned()` for the whole batch. See [scan-a-table.md](scan-a-table.md) and
[owned-batches.md](owned-batches.md).

## VX1002

An owned batch is never disposed. This is a warning.

A `RecordBatch` from `ToBatchesAsync()` or `ToOwned()` holds buffers from the session's pool until it
is disposed. The analyzer flags a batch that nothing disposes: no `using`, no `Dispose`, and it is not
returned, passed to a method, stored in a field or a collection, or captured by a lambda, any of which
hands it to a new owner.

```csharp
await foreach (RecordBatch batch in file.Scan<Reading>().ToBatchesAsync())
{
    Console.WriteLine(batch.RowCount);
}

await foreach (Columns<Reading> columns in file.Scan<Reading>())
{
    RecordBatch owned = columns.ToOwned();
    Console.WriteLine(owned.RowCount);
}
```

```
warning VX1002: The RecordBatch from 'ToBatchesAsync' is never disposed; dispose it, or hand it on
warning VX1002: The RecordBatch from 'ToOwned' is never disposed; dispose it, or hand it on
```

To fix it, write `using (batch) { … }` or `using RecordBatch owned = columns.ToOwned();`, or pass the
batch to whoever will dispose it, as a channel does in [owned-batches.md](owned-batches.md).

## VX1003

A filter hole has no column type. This is a warning.

The tool scan takes a filter as an interpolated string, as in `Where($"Day >= {threshold}")`, and each
hole is compared with a column. Which column only the open file knows, so the analyzer cannot check a
hole against it. What it can check is that the hole's type maps to some column type at all: a number,
a `bool`, a `string`, a `decimal`, a date, a time, a timestamp, a `Guid`, bytes or a list. A hole of
any other type throws when the filter is built.

```csharp
object threshold = 900;
await file.Scan("Day").Where($"Day >= {threshold}").CountAsync();

TimeSpan span = TimeSpan.FromDays(1);
await file.Scan("Day").Where($"Day >= {span}").CountAsync();
```

```
warning VX1003: The hole 'threshold' is of type 'object', which no column type maps to; the filter throws when it is built
warning VX1003: The hole 'span' is of type 'System.TimeSpan', which no column type maps to; the filter throws when it is built
```

To fix it, give the hole the type of the column it is compared with, as in `int threshold = 900`. A
hole of a mapped type that does not fit its column, such as a `string` against an `i32` column, is not
reported here: `Where` throws `VortexSchemaException` for it, naming the column and both types (see
[errors.md](errors.md) and [untyped-files.md](untyped-files.md)).

## VX1004

Rows are selected both by range and by indices on one scan. This is a warning.

`Rows(RowRange)` and `Rows(params ReadOnlySpan<long>)` exclude each other, and the second call on a
scan throws `InvalidOperationException`. The analyzer finds both in one fluent chain, or on a local
that holds the scan and is not reassigned, on the typed scan and the tool scan alike.

```csharp
await file.Scan<Reading>().Rows(new RowRange(0, 10)).Rows(4, 5).CountAsync();

Scan<Reading> scan = file.Scan<Reading>().Rows(1, 2);
scan.Rows(new RowRange(0, 10));
```

```
warning VX1004: This scan already selects rows by range; a scan selects rows by range or by indices, not both, and throws here
warning VX1004: This scan already selects rows by indices; a scan selects rows by range or by indices, not both, and throws here
```

To fix it, keep one of the two. A range can always be expressed as a list of indices if you also need
single rows. See [read-rows-by-index.md](read-rows-by-index.md).

## VX1005

A record member has no column type. This is an error.

Every member of a `[VortexRecord]` type becomes a column, so its type must map to a column type
according to the table in [records.md](records.md#the-mapping). Most .NET types do, including `char`,
arrays, `List<T>`, dictionaries and lists of records. The message says why a member does not, and
usually what to declare instead:

```csharp
[VortexRecord]
public partial record struct Grid(int Id, int[,] Cells);                          // a two-dimensional array

[VortexRecord]
public partial record struct Route(int Id, ReadOnlyMemory<Address?> Stops);      // a list of nullable records

[VortexRecord]
public partial record struct Tags(int Id, Dictionary<string, int[]> ByName);     // a dictionary of lists

[VortexRecord]
public partial record struct Site(int Id, Uri Home);                              // a type with no column

[VortexRecord]
public partial record struct Event(int Id, [VortexColumn(TimeZone = "Europe/Paris")] DateTime At);

[VortexRecord]
public partial record struct Price(int Id, [VortexColumn(Precision = 38, Scale = 2)] decimal Amount);

[VortexRecord]
public partial class Node
{
    public int Id { get; set; }

    public Node? Parent { get; set; }
}
```

```
error VX1005: Member 'Cells' of 'Grid' is of type 'int[*,*]', which does not map to a column: an array of more than one dimension is not a column type; declare an array of arrays
error VX1005: Member 'Stops' of 'Route' is of type 'System.ReadOnlyMemory<Address?>', which does not map to a column: a list of records holds records, not nulls; declare its element type without '?'
error VX1005: Member 'ByName' of 'Tags' is of type 'System.Collections.Generic.Dictionary<string, int[]>', which does not map to a column: a map's keys and values are scalars; hold anything deeper in a record
error VX1005: Member 'Home' of 'Site' is of type 'System.Uri', which does not map to a column: the type has no mapping to a dtype
error VX1005: Member 'At' of 'Event' is of type 'System.DateTime', which does not map to a column: a DateTime reads a naive or UTC timestamp, not one in 'Europe/Paris'; declare the member DateTimeOffset
error VX1005: Member 'Amount' of 'Price' is of type 'decimal', which does not map to a column: a decimal member reads a column of at most 28 digits and a scale of 0 to 28; declare it VortexDecimal for a wider one
error VX1005: Member 'Parent' of 'Node' is of type 'Node?', which does not map to a column: a record cannot hold itself, directly or through another record
```

The same diagnostic covers a list of lists of records, a map with nullable keys, a nullable extension
type (an extension is read and written without nulls), a time in days, and a decimal precision or
scale outside what the format allows.

To fix it, change the member's type as the message says, or mark the member `[VortexIgnore]` if it is
not meant to be a column.

## VX1006

A record type is not partial. This is an error.

The generator adds the schema and the row copies to the type itself, which C# only allows for a
`partial` type nested in `partial` types.

```csharp
[VortexRecord]
public record struct Plain(int Id);

public class Outer
{
    [VortexRecord]
    public partial record struct Inner(int Id);
}
```

```
error VX1006: 'Plain' must be declared partial, as must every type that contains it, for the generator to implement IVortexRecord
error VX1006: 'Outer.Inner' must be declared partial, as must every type that contains it, for the generator to implement IVortexRecord
```

To fix it, add `partial` to the type and to every type it is nested in.

## VX1007

A type cannot be a record. This is an error.

A record is a concrete, non-generic class or struct whose schema is known when the program is
compiled, and which reading can construct. The message names which of these conditions the type
fails:

```csharp
[VortexRecord]
public partial record struct Boxed<T>(T Value);                  // generic, or nested in a generic type

[VortexRecord]
public abstract partial class Shape
{
    public int Id { get; set; }
}

[VortexRecord]
public ref partial struct Cursor
{
    public int Id;
}

[VortexRecord]
file partial class Local
{
    public int Id { get; set; }
}

[VortexRecord]
public partial class Seeded
{
    public Seeded(int seed)
    {
        Id = seed;
    }

    public int Id { get; set; }
}
```

```
error VX1007: 'Boxed<T>' cannot be a [VortexRecord]: a generic type has no schema known when the program is compiled
error VX1007: 'Shape' cannot be a [VortexRecord]: an abstract type cannot be constructed when rows are read
error VX1007: 'Cursor' cannot be a [VortexRecord]: a ref struct cannot be held in the span of rows that reading fills
error VX1007: 'Local' cannot be a [VortexRecord]: a file-local type cannot be completed from the file the generator adds
error VX1007: 'Seeded' cannot be a [VortexRecord]: no constructor takes only members as its parameters, and none takes no parameter
```

A static class is refused the same way, since it has no rows.

To fix it, declare a concrete record for each shape you store instead of a generic or abstract type.
For the constructor, add one whose parameters are all members (matched by name, ignoring case), or a
parameterless one. A positional record always has one.

## VX1008

A record member cannot be filled from a column. This is an error.

Reading builds each row through a constructor whose parameters are members, then sets the other
members. A member that is a column but that neither the constructor nor a setter can reach cannot be
filled:

```csharp
[VortexRecord]
public partial class Doubler
{
    public int Id { get; set; }

    public int Doubled => Id * 2;                // get-only, and no constructor parameter
}

[VortexRecord]
public partial struct Frozen
{
    public readonly int Id;                      // a readonly field
}

public class Base
{
    public int Id { get; private set; }          // a setter the derived record cannot reach
}

[VortexRecord]
public partial class Derived : Base
{
    public string Name { get; set; } = "";
}

[VortexRecord]
public partial class Noted
{
    public int Id { get; set; }

    [VortexIgnore]
    public required string Note { get; set; }    // required, yet not a column
}
```

```
error VX1008: Member 'Doubled' of 'Doubler' cannot be filled when rows are read: it has no setter and no constructor parameter takes it
error VX1008: Member 'Id' of 'Frozen' cannot be filled when rows are read: it is a readonly field and no constructor parameter takes it
error VX1008: Member 'Id' of 'Derived' cannot be filled when rows are read: its setter is not accessible from the record
error VX1008: Member 'Note' of 'Noted' cannot be filled when rows are read: it is required, and a member marked [VortexIgnore] has no value to set it to
```

A private setter on the record's own member is fine, since the generated code is part of the type.

To fix it, give the member a setter or an `init` accessor, make it a constructor parameter, or mark a
computed member `[VortexIgnore]`. For a `required` member you ignore, remove `required`, or put
`[SetsRequiredMembers]` on the constructor that reading uses.

## VX1009

A component of a group key is not a column. This is an error.

A group by takes a column of the scan, or a tuple of columns, and each row falls into the group of its
values. A literal, or a value captured from outside the lambda, is the same for every row, so it groups
nothing, and `GroupBy` refuses it with `ArgumentException` when it is built. The analyzer flags it in
the tuple, in both method and query syntax.

```csharp
file.Scan<Reading>().GroupBy(r => (r.City, 42));          // VX1009: component 2, '42', is an int

from r in file.Scan<Reading>()
group r by (r.Day, "all") into g                          // VX1009: component 2, '"all"', is a string
select (g.Key.Day, g.Count());
```

To fix it, group by the columns alone, as in `r => (r.City, r.Day)`. A constant that should appear in
every row of the result belongs in C# code after the query.

## VX1010

A record does not match the values of a selection. This is an error.

A selection of several values is read through a record: `As<TRecord>()` after a `Select`, and
`AggregateAsync<TResult>` for the answers of a whole scan. The record's members, in declaration
order, take the elements in order, one each, and each member has its element's type or its nullable
form. `As` and `AggregateAsync` check this when they are built and throw `VortexSchemaException`,
and the analyzer reads the types of the tuple's elements in the lambda to flag the problem at
compile time.

```csharp
[VortexRecord]
public partial record struct CityDays(string City, int Days);

file.Scan<Reading>().GroupBy(r => r.City)
    .Select(g => (g.Key, g.Count()))
    .As<CityDays>();                         // VX1010: element 2 is of type long, which member 'Days', of type int, does not take

file.Scan<Reading>().GroupBy(r => r.City)
    .Select(g => (g.Key, g.Count(), g.Max(r => r.Day)))
    .As<CityDays>();                         // VX1010: CityDays has 2 members and the selection 3 elements
```

A selection kept in a variable and read in another method is checked when `As` runs, because the
analyzer only sees a `Select` it is called on.

To fix it, declare each member with its element's type, `long Days`, or its nullable form, `long?`.
A count is a `long`, an average a `double?`, and a key component or a minimum has the column's type.
Declare one member per element, in the order of the tuple.

## VX1011

Several values are read without a record. This is an error, reported beside the compiler's own.

Several values have no .NET type until a record gives them one. So a `Select` of several values
returns an `Aggregation`, which is not enumerable, and `AggregateAsync` of several answers needs the
record they go into. The compiler refuses both cases, with CS8411 for an `await foreach`, and CS0411
for an `AggregateAsync` whose type it cannot infer or for `System.Linq` operators applied to an
`Aggregation`, but its message does not say what to do. The analyzer reports this diagnostic beside
it.

```csharp
await foreach (var (city, count) in file.Scan<Reading>()
    .GroupBy(r => r.City)
    .Select(g => (g.Key, g.Count())))            // CS8411 and VX1011

var (min, max) = await file.Scan<Reading>()
    .AggregateAsync(a => (a.Min(r => r.Day), a.Max(r => r.Day)));   // CS0411 and VX1011
```

To fix it, declare a `[VortexRecord]` whose members take the values in order, and read the selection
through it: `.As<CityCount>()`, enumerated as batches or with `ToRecordsAsync()`, or
`AggregateAsync<MinMax>(a => (…))`. A selection of one value, such as `Select(g => g.Count())`,
comes back as itself.
