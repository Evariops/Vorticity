# Row keys

Encode a tuple into bytes whose `memcmp` order is tuple order.

This is `Vorticity.RowEncoding`, a separate package. It is **experimental**: upstream marks the
format so and reserves the right to change the byte layout between releases, which is why it ships
apart from the core and versioned `0.x`. This build follows Vortex 0.86.1.

What it is for: when the comparison you need is the one a B-tree, a sort, a merge or an object store
already does on bytes, encoding the tuple once and comparing bytes is both faster and simpler than
comparing fields.

## Encoding a batch

```csharp
ReadOnlySpan<RowSortField> fields = [RowSortField.Ascending, RowSortField.Ascending];
using RowKeys keys = RowEncoder.Encode(batch, fields);
Console.WriteLine($"{keys.RowCount} keys, {keys.TotalBytes} bytes in all");
for (int row = 0; row < 4; row++)
{
    Console.WriteLine(Convert.ToHexString(keys.Row(row)));
}
```

```
8 keys, 344 bytes in all
  row 0: 02506172697300...0005000000000000000000
  row 1: 024C796F6E0000...000401C03D2B851EB851EC
  row 2: 024D617273656900...0901C032547AE147AE14
  row 3: 024C696C6C650000...000501C042C00000000000
```

One `RowSortField` per column, in schema order. `RowKeys` is disposable and owns one buffer for all
the keys; `Row(i)` is a span into it, `Offsets` and `Sizes` describe the layout, and `Elements` is
the whole thing.

## The promise

```csharp
keys.Compare(0, 1);                              // the tuple order
keys.Row(0).SequenceCompareTo(keys.Row(1));      // the byte order
```

The two agree, always. That is the whole point, and it is what makes the next line work:

```csharp
int[] order = [7, 6, 5, 4, 3, 2, 1, 0];
keys.SortIndices(order);                         // 4, 3, 1, 6, 2, 7, 0, 5
```

`SortIndices` sorts an index array by the encoded keys — no comparator, no boxing, no field access.

## Direction and nulls

```csharp
RowSortField.Ascending
RowSortField.Ascending.WithDescending(true)
RowSortField.Ascending.WithNullsFirst()
RowSortField.Ascending.WithNullsLast()
```

Each is a property of one field, and each changes the bytes rather than the comparison:

```
ascending, nulls first: 025061726973...05 00 0000000000000000
ascending, nulls last:  025061726973...05 02 0000000000000000
descending:             FDAF9E8D968C...FA 00 0000000000000000
```

Descending inverts every byte of the field, which is why a plain `memcmp` still gives the order you
asked for. A null is one byte, before or after every value depending on which you chose.

## A key for a tuple you hold

```csharp
byte[] key = RowEncoder.EncodeKey(
    [FilterLiteral.From("Lyon"), FilterLiteral.From(12.5)],
    [types.Utf8(Nullability.NonNullable), types.Primitive(PType.F64, Nullability.Nullable)],
    fields);
```

The same encoding for a probe rather than a batch, so it can be compared against encoded rows, used
as a lookup key, or handed to a store as an object key component.

## Handing it to the writer

```csharp
VortexWriteOptions options = VortexWriteOptions.Default
    .WithKeyEncoder(new RowKeyEncoder([RowSortField.Ascending, RowSortField.Ascending]));
```

`RowKeyEncoder` implements `IKeyEncoder`, which is what the writer uses to build an index over a
composite key: the tuple becomes one byte string and the index is over that. `WritePolicy.ForKey`
is where you say which columns — see [indexes.md](indexes.md).

## Watch out

* **The bytes are not stable across releases.** Do not persist them outside a file this library
  wrote, and do not compare keys produced by two different versions.
* The field list must match the columns you are encoding, in order. There is no name checking.
* `RowKeys` is disposable and its spans die with it, like a `RecordBatch`.
* Encoding is not free: it is a pass over the batch producing a byte string per row.
  `ComputeSizes` and `ComputeOffsets` are there for a caller who wants to allocate once and encode
  into its own memory.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- row-keys
```
