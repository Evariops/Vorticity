# Records

Declare a .NET type whose members are the columns of a file.

```csharp
public enum OrderStatus : byte
{
    Placed,
    Shipped,
    Delivered,
}

[VortexRecord]
public partial record struct Order(
    [VortexColumn("order_id")] long Id,
    [VortexColumn(Precision = 12, Scale = 2)] decimal Amount,
    [VortexColumn(Unit = TimeUnit.Milliseconds)] DateTime PlacedAt,
    [VortexColumn(TimeZone = "Europe/Paris")] DateTimeOffset DeliverBy,
    OrderStatus Status,
    string? Note,
    ReadOnlyMemory<int> Items,
    Address ShipTo)
{
    [VortexIgnore]
    public readonly decimal AmountWithTax => Amount * 1.2m;
}
```

```csharp
await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Order>(path))
{
    await writer.WriteAsync<Order>(Orders(10_000));
    await writer.CompleteAsync();
}

await using VortexFile file = await VortexFile.OpenAsync(path);
Order first = await file.Scan<Order>().ToRecordsAsync().FirstAsync();
```

```
Order    struct{order_id: i64, Amount: decimal(12,2), PlacedAt: timestamp(ms), DeliverBy: timestamp(µs, Europe/Paris), Status: u8, Note: utf8?, Items: list(i32), ShipTo: struct{Country: utf8, City: utf8?}}
first row: 1000, 5.00, 2026-09-01T08:00:00.0000000, 2026-09-04T10:00:00.0000000+02:00, Placed, leave at the door, [0], FR/null
```

**A record is a schema, not a row.** The type says which columns exist and how they are typed;
reading it yields columns, and rows only when you ask for them. The `[VortexRecord]` source
generator, which ships in the `Vorticity.Generators` package, turns the declaration into
everything the library needs, and it does so at compile time: nothing is discovered by
reflection when the program runs.

## What can be a record

A `partial` `record struct`, `record class`, `struct` or `class`, nested only in `partial` types.
Its columns are its public instance properties and fields, and a positional record's parameters:
the parameters first, then the other members, base types first, each level in declaration order.

```csharp
[VortexRecord]
public partial record class Customer(long Id, string Name, DateOnly? Since);

[VortexRecord]
public partial struct Point
{
    public double X;
    public double Y;
}

[VortexRecord]
public partial class Shipment
{
    public long Id { get; set; }

    public TimeOnly Slot { get; init; }

    public string Carrier { get; set; } = string.Empty;
}
```

```
Customer struct{Id: i64, Name: utf8, Since: date?}
Point    struct{X: f64, Y: f64}
Shipment struct{Id: i64, Slot: time(µs), Carrier: utf8}
```

Reading builds each row through a constructor whose parameters are members, then sets the other
members, so every column member needs a way in: a constructor parameter, a setter or an `init`
accessor. A member that has none, such as `AmountWithTax` above, is marked `[VortexIgnore]`. The
compiler says what is wrong when a type does not qualify: [diagnostics.md](diagnostics.md) lists
the eight diagnostics and their fixes.

## What the generator writes

For `Order`, the generator adds to the type itself the three static members of
`IVortexRecord<Order>`:

| member | what it is |
|---|---|
| `Schema` | the `VortexSchema` above, one field per member |
| `ReadRows(Columns<Order>, Span<Order>)` | fills rows from a batch, one column at a time |
| `WriteRows(ColumnsBuilder<Order>, ReadOnlySpan<Order>)` | appends rows to a builder, one column at a time |

and a nested `Order.ColumnNames` class with one constant per member, for the options that name a
column by string:

```
Order.ColumnNames.Id = "order_id"
```

Beside the type, in an `OrderVortexExtensions` class, it adds C# extension members that name the
columns wherever the library hands you a record's columns (a record declared inside `Shop` gets a
`Shop_OrderVortexExtensions`):

| on | each member is | a nested record is |
|---|---|---|
| `Probe<Order>`, in the lambdas of a scan | `Sym<T>` | `Probe<Address>` |
| `Columns<Order>`, in the body of `await foreach` | `Column<T>`, plus a `Deconstruct` over all of them | `Columns<Address>` |
| `ColumnsBuilder<Order>`, from `writer.Builder<Order>()` | `ColumnBuilder<T>` | `ColumnsBuilder<Address>` |

That is what makes the rest of the guide read as it does:

```csharp
long shipped = await file.Scan<Order>()
    .Where(o => o.Status == OrderStatus.Shipped && o.Amount > 100m)
    .CountAsync();
```

```csharp
await foreach (var (_, amount, _, _, status, note, _, shipTo) in file.Scan<Order>())
{
    ReadOnlySpan<byte> statuses = status.Values;
    for (int i = 0; i < amount.Length; i++)
    {
        if (statuses[i] == (byte)OrderStatus.Delivered)
        {
            total += amount[i];
        }
    }

    notes += note.Length - note.NullCount;
    _ = shipTo.Country;
}
```

```
shipped and above 100: 2275 of 10000
delivered total 515415.00, 2000 notes
```

An enum is a `Sym<OrderStatus>` in a filter, so an enum literal compares, and its underlying
integer on the columns and the builders, which have no enum accessors: `status.Values` above is a
`ReadOnlySpan<byte>`.

## `[VortexColumn]` and `[VortexIgnore]`

A .NET type does not say everything a column type does, and `[VortexColumn]` carries the rest. It
goes on a property, a field or a positional parameter.

| | for | by default |
|---|---|---|
| `[VortexColumn("order_id")]` | any member: the column's name | the member's name |
| `Precision`, `Scale` | a `decimal` or `VortexDecimal` member | 28 and 10 |
| `Precision` | an `Int128`, `UInt128` or `BigInteger` member, whose scale is 0 | 38, 38 and 76 |
| `Unit` | a `DateTime`, `DateTimeOffset` or `TimeOnly` member | `TimeUnit.Microseconds` |
| `TimeZone` | a `DateTimeOffset` member | `"UTC"` |

They shape the schema the record writes. When the record reads a file, the file's column type
decides, and the binding only checks that the member's .NET type fits it. `[VortexIgnore]` leaves
a member out of the columns entirely.

## The mapping

| .NET member | column |
|---|---|
| `bool` | bool |
| `sbyte`, `short`, `int`, `long`, `byte`, `ushort`, `uint`, `ulong` | i8 to i64, u8 to u64 |
| `char` | u16: the UTF-16 code unit, a lone surrogate included |
| `nint`, `nuint` | i64, u64, on a 64-bit host; refused on another |
| `Half`, `float`, `double` | f16, f32, f64 |
| an `enum` | its underlying integer type |
| `string` | utf8 |
| `ReadOnlyMemory<byte>`, `Memory<byte>`, `byte[]` | binary |
| `decimal` | decimal of at most 28 digits and a scale of 0 to 28 |
| `VortexDecimal` | decimal of any precision up to 76 |
| `Int128`, `UInt128` | decimal of scale 0: 38 digits by default, 39 for every value of the type |
| `BigInteger` | decimal of scale 0, 76 digits by default |
| `TimeSpan` | i64, its ticks of 100 ns |
| `DateOnly` | `vortex.date` |
| `TimeOnly` | `vortex.time` |
| `DateTime` | `vortex.timestamp`, naive or UTC |
| `DateTimeOffset` | `vortex.timestamp` with a zone |
| `Guid` | `vortex.uuid` |
| `ReadOnlyMemory<T>`, `Memory<T>`, `T[]`, `List<T>`, `ImmutableArray<T>`, `IReadOnlyList<T>`, `IList<T>`, `ICollection<T>`, `IReadOnlyCollection<T>`, `IEnumerable<T>` | list of `T`, a record included |
| `Dictionary<TKey, TValue>`, `IDictionary<TKey, TValue>`, `IReadOnlyDictionary<TKey, TValue>` | map, of scalar keys and values |
| another `[VortexRecord]` type | struct |
| a type implementing `IVortexExtension<T>` | the extension it registers |
| `T?` or `string?` of any of these, but an extension | the nullable column |

A list read back is the container the member declares: an array for an array and for an
interface an array implements, a `List<T>` filled in place, an `ImmutableArray<T>` over the array
it was read into, with no copy. A dictionary is read back as a `Dictionary<TKey, TValue>`. A list
of records is read and written through `ListOf<TNested>`, a map through `MapOf<TKey, TValue>`; a
filter compares neither.

An integer of 128 bits is a decimal of scale 0, because no dtype is 128 bits wide. Thirty-eight
digits hold every value a 128-bit decimal holds, which every reader of one reads and a column reads
without a copy; a value past them is refused when it is written, with the way out in the message:
`[VortexColumn(Precision = 39)]`, which holds every value of the type in 256 bits. Reading, an
`Int128` binds to a decimal of scale 0 and at most 39 digits, and a value past the type in a
39-digit column is refused as it is read, as a timestamp past `DateTime` is.

A type with no column -- an array of more than one dimension, a dictionary of lists, a list of
lists of records, a `Uri` -- is refused by the generator with VX1005, which says what to declare
instead. The whole contract, and where a naive mapping would lose data, is in
[07-dotnet-mapping.md](../design/07-dotnet-mapping.md).

## Binding a record to a file

A record meets a file at the first sink of a scan, or when a writer is created. Each member is
matched to a column by exact name, then by a unique case-insensitive match, and its type must fit
the column's. A file with more columns than the record is fine: the record is the projection, and
the other columns are not read. A file without one of the record's columns is not:

```csharp
[VortexRecord]
public partial record struct OrderAmount(long Order_Id, decimal amount);

[VortexRecord]
public partial record struct OrderById(long Id, decimal Amount);
```

```
bound case-insensitively: Order_Id 1000, amount 5.00
a member with no column: Member 'Id' of OrderById has no column; the columns are 'order_id', 'Amount', 'PlacedAt', 'DeliverBy', 'Status', 'Note', 'Items', 'ShipTo'.
```

That is a `VortexSchemaException`, and so is a non-nullable member over a nullable column. A
member binds by the column's name, so the renamed `order_id` is not found by a member called
`Id`.

## A record written by hand

The generator is sugar over a public interface, and a type can implement it itself:

```csharp
public readonly struct Pair : IVortexRecord<Pair>
{
    public Pair(int key, double value)
    {
        Key = key;
        Value = value;
    }

    public int Key { get; }

    public double Value { get; }

    public static VortexSchema Schema { get; } = [("key", VortexType.Int32), ("value", VortexType.Float64)];

    public static void ReadRows(Columns<Pair> columns, Span<Pair> rows)
    {
        ReadOnlySpan<int> keys = columns.Column<int>(0).Values;
        ReadOnlySpan<double> values = columns.Column<double>(1).Values;
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Pair(keys[i], values[i]);
        }
    }

    public static void WriteRows(ColumnsBuilder<Pair> builder, ReadOnlySpan<Pair> rows)
    {
        Span<int> keys = builder.Column<int>(0).GetSpan(rows.Length);
        Span<double> values = builder.Column<double>(1).GetSpan(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            keys[i] = rows[i].Key;
            values[i] = rows[i].Value;
        }

        builder.Column<int>(0).Advance(rows.Length);
        builder.Column<double>(1).Advance(rows.Length);
    }
}
```

```csharp
long above = await pairFile.Scan<Pair>().Where(p => p.Column<int>("key") >= 990).CountAsync();
```

```
hand-written record: 10 keys at or above 990, last (999, 249.75)
```

It loses the named members on the probe, the columns and the builder, and nothing else: the index
passed to `Column<T>` is the member's position in `Schema`, whatever the file's order. The
attributes live in the core package, so a project that writes its records by hand does not need
the generator at all.

## An extension type

A column type the format does not define is an extension: an id, and a storage type the values
are kept as. A .NET type becomes one by implementing `IVortexExtension<T>`, whose members are all
static, so the conversion is compiled per type and costs no virtual call per value:

```csharp
public readonly record struct Money(long Cents) : IVortexExtension<Money>
{
    public static string Id => "acme.money";

    public static VortexType StorageType => VortexType.Int64;

    public static Money FromStorage(ReadOnlySpan<byte> storage, ReadOnlySpan<byte> metadata) =>
        new Money(BinaryPrimitives.ReadInt64LittleEndian(storage));

    public static void ToStorage(in Money value, Span<byte> storage, ReadOnlySpan<byte> metadata) =>
        BinaryPrimitives.WriteInt64LittleEndian(storage, value.Cents);
}

[VortexRecord]
public partial record struct Invoice(long Number, Money Total);
```

A session knows it once it is registered, and only then:

```csharp
await using VortexSession session = VortexSession.Create(o => o.Extensions.Register<Money>());
```

```
writing Money without registering it: VortexUnsupportedException: Unsupported Vortex component: dtype 'acme.money'. No core edition contains it, so no target can emit it.
with the registration: struct{Number: i64, Total: ext(acme.money, i64)}; Invoice { Number = 1, Total = Money { Cents = 1999 } }, Invoice { Number = 2, Total = Money { Cents = 250000 } }; 1 above 1000.00
without it, the schema reads struct{Number: i64, Total: ext(acme.money, i64)}
and reading Invoice throws VortexUnsupportedException: Unsupported Vortex component: dtype 'acme.money'. This id is not implemented by Vorticity; check the edition that introduced it and the minimum library version required to read it.
```

The writer and every reader need the same registration. A filter compares a `Money` literal
through its storage, `i.Total > new Money(100_000)`, which is why the storage of an extension you
want to filter on should be a primitive. Registration happens inside `VortexSession.Create`; the
session's registry is frozen when it returns, and an id in the `vortex.` namespace is refused.

## Watch out

* **A naive timestamp reads back as `DateTimeKind.Unspecified`**, whatever `Kind` the value had
  when it was written: `PlacedAt` above lost its `Utc`. Declare the member with
  `TimeZone = "UTC"` to read it back as `Utc`. A `DateTime` member with any other zone is refused
  at compile time: declare it `DateTimeOffset`.
* A nullable extension member, a list of nullable records and a list of lists of records are
  refused with VX1005: no builder appends a null to the first, and no typed column reads the others.
* **A `DateTime`, a `DateTimeOffset` or a `TimeOnly` is stored in its column's unit**, microseconds
  unless `Unit` says otherwise: the tenth of a microsecond a tick adds is floored away. Declare
  `Unit = TimeUnit.Nanoseconds` to keep it, within the years 1677 to 2262 a nanosecond count holds.
* **Vortex Rust reads no instant after 9999-12-30T22:00Z**, which leaves room for any offset: a
  `DateTime.MaxValue` written as a sentinel is a value this library reads back and the reference
  refuses to hand out as a scalar.
* A member called `Schema`, `ReadRows`, `WriteRows` or `ColumnNames` does not stop the generator:
  it implements the interface explicitly instead, and leaves `ColumnNames` out when that name is
  taken.
* The record's accessibility carries to its extension class: an `internal` record gets `internal`
  members.

## What it costs

`ReadRows` and `WriteRows` move one column at a time, so the loop over a column is a loop over a
span. What rows cost on top of the columns is the rows themselves: `ToRecordsAsync` copies every
field, and allocates per row for a `string`, a list (read into a new array, or straight into a new
`List<T>`), a dictionary, a `BigInteger` and a nested record that is a class. `WriteRows` transcodes each `string` once, and
fills primitive columns through `GetSpan`, in place. When the columns are what you want, read the
columns: see [scan-a-table.md](scan-a-table.md) and [read-rows.md](read-rows.md).

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- records
```
