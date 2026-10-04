# Diagnostics

What the compiler tells you about your use of the library, and how to fix each one.

The `Vorticity.Generators` package ships the `[VortexRecord]` generator and four analyzers.
Together they report eight diagnostics. VX1001 to VX1004 are warnings about code that compiles
but would misbehave or throw; VX1005 to VX1008 are errors about a `[VortexRecord]` type the
generator cannot implement, and the generator emits nothing for a type that has one. Every
diagnostic's help link points at its section below.

| id | severity | reported when |
|---|---|---|
| [VX1001](#vx1001) | warning | a span borrowed from a batch is kept in a variable that outlives the loop body |
| [VX1002](#vx1002) | warning | an owned `RecordBatch` is never disposed |
| [VX1003](#vx1003) | warning | a hole of an interpolated `Where` has a type no column maps to |
| [VX1004](#vx1004) | warning | one scan selects rows by range and by indices |
| [VX1005](#vx1005) | error | a record member has no column type |
| [VX1006](#vx1006) | error | a record type, or a type containing it, is not `partial` |
| [VX1007](#vx1007) | error | a type cannot be a record at all |
| [VX1008](#vx1008) | error | a record member cannot be filled when rows are read |
| [VX1009](#vx1009) | error | a component of a group key is not a column |

## VX1001

**A borrowed span outlives its batch.** Warning.

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

**Fix:** declare the variable inside the body, or keep a copy that you own: `celsius.ToArray()`
for one column, `columns.ToOwned()` for the batch. See [scan-a-table.md](scan-a-table.md) and
[owned-batches.md](owned-batches.md).

## VX1002

**An owned batch is never disposed.** Warning.

A `RecordBatch` from `ToBatchesAsync()` or `ToOwned()` holds buffers from the session's pool
until it is disposed. The analyzer flags one that nothing disposes: no `using`, no `Dispose`, and
it is not returned, passed to a method, stored in a field or a collection, or captured by a
lambda, any of which hands it to a new owner.

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

**Fix:** `using (batch) { … }`, `using RecordBatch owned = columns.ToOwned();`, or pass the batch
to whoever will dispose it, as a channel does in [owned-batches.md](owned-batches.md).

## VX1003

**A filter hole has no column type.** Warning.

The tool scan takes a filter as an interpolated string, `Where($"Day >= {threshold}")`, and each
hole is compared with a column. Which column only the open file knows, so the analyzer cannot
check a hole against it; what it can check is that the hole's type maps to some column type at
all: a number, a `bool`, a `string`, a `decimal`, a date, a time, a timestamp, a `Guid`, bytes or a
list. A hole of any other type throws when the filter is built.

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

**Fix:** give the hole the type of the column it is compared with, `int threshold = 900`. A hole
of a mapped type that does not fit its column, a `string` against an `i32` column, is not
reported here: `Where` throws `VortexSchemaException` for it, naming the column and both types
(see [errors.md](errors.md) and [untyped-files.md](untyped-files.md)).

## VX1004

**Rows by range and by indices on one scan.** Warning.

`Rows(RowRange)` and `Rows(params ReadOnlySpan<long>)` exclude each other: the second call on one
scan throws `InvalidOperationException`. The analyzer finds both in one fluent chain, or on a
local that holds the scan and is not reassigned, on the typed scan and the tool scan alike.

```csharp
await file.Scan<Reading>().Rows(new RowRange(0, 10)).Rows(4, 5).CountAsync();

Scan<Reading> scan = file.Scan<Reading>().Rows(1, 2);
scan.Rows(new RowRange(0, 10));
```

```
warning VX1004: This scan already selects rows by range; a scan selects rows by range or by indices, not both, and throws here
warning VX1004: This scan already selects rows by indices; a scan selects rows by range or by indices, not both, and throws here
```

**Fix:** keep one of the two. A range is also a list of indices, if you need to add single rows to
it. See [read-rows-by-index.md](read-rows-by-index.md).

## VX1005

**A record member has no column type.** Error.

Every member of a `[VortexRecord]` type becomes a column, so its type must map to a column type
under the table in [records.md](records.md#the-mapping). The message says why this one does not,
and usually what to declare instead:

```csharp
[VortexRecord]
public partial record struct Grade(int Id, char Letter);                          // a char

[VortexRecord]
public partial record struct Scores(int Id, int[] Values);                        // an array

[VortexRecord]
public partial record struct Batch(int Id, ReadOnlyMemory<Reading> Readings);     // a list of records

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
error VX1005: Member 'Letter' of 'Grade' is of type 'char', which does not map to a column: a char is not a dtype; use string
error VX1005: Member 'Values' of 'Scores' is of type 'int[]', which does not map to a column: an array is not a column type; declare the member ReadOnlyMemory<int>
error VX1005: Member 'Readings' of 'Batch' is of type 'System.ReadOnlyMemory<Reading>', which does not map to a column: a list of records has no typed column to read it through; declare a list of a scalar type
error VX1005: Member 'At' of 'Event' is of type 'System.DateTime', which does not map to a column: a DateTime reads a naive or UTC timestamp, not one in 'Europe/Paris'; declare the member DateTimeOffset
error VX1005: Member 'Amount' of 'Price' is of type 'decimal', which does not map to a column: a decimal member reads a column of at most 28 digits and a scale of 0 to 28; declare it VortexDecimal for a wider one
error VX1005: Member 'Parent' of 'Node' is of type 'Node?', which does not map to a column: a record cannot hold itself, directly or through another record
```

The same diagnostic covers a `List<T>` or a `Memory<T>` member, a nullable extension type (an
extension is read and written without nulls), a time in days, and a decimal precision or scale
outside what the format allows.

**Fix:** change the member's type as the message says, or mark the member `[VortexIgnore]` if it
is not meant to be a column.

## VX1006

**A record type is not partial.** Error.

The generator adds the schema and the row copies to the type itself, which C# allows only for a
`partial` type, nested in `partial` types.

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

**Fix:** add `partial` to the type and to every type it is nested in.

## VX1007

**A type cannot be a record.** Error.

A record is a concrete, non-generic class or struct whose schema is known when the program is
compiled, and which reading can construct. The message names which of these the type is not:

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

A static class is refused the same way: it has no rows.

**Fix:** for a generic or abstract type, declare a concrete record for each shape you store. For
the constructor, add one whose parameters are all members (matched by name, ignoring case), or a
parameterless one; a positional record always has one.

## VX1008

**A record member cannot be filled from a column.** Error.

Reading builds each row through a constructor whose parameters are members, then sets the other
members. A member that is a column but that neither the constructor nor a setter reaches cannot be
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

A private setter on the record's own member is fine: the generated code is part of the type.

**Fix:** give the member a setter or an `init` accessor, make it a constructor parameter, or mark
a computed member `[VortexIgnore]`. For a `required` member you ignore, remove `required`, or put
`[SetsRequiredMembers]` on the constructor that reading uses.

## VX1009

**A component of a group key is not a column.** Error.

A group by takes a column of the scan, or a tuple of them, and each row falls into the group of its
values. A literal, or a value captured from outside the lambda, is the same for every row: it
groups nothing, and `GroupBy` refuses it with `ArgumentException` when it is built. The analyzer
flags it in the tuple, in method and in query syntax.

```csharp
file.Scan<Reading>().GroupBy(r => (r.City, 42));          // VX1009: component 2, '42', is an int

from r in file.Scan<Reading>()
group r by (r.Day, "all") into g                          // VX1009: component 2, '"all"', is a string
select (g.Key.Day, g.Count());
```

**Fix:** group by the columns alone, `r => (r.City, r.Day)`. A constant that should appear in every
row of the result belongs to C# after the query.
