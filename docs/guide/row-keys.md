# Row keys

Encode tuples into bytes whose `memcmp` order is the tuple order.

```csharp
[VortexRecord]
public partial record struct CityCelsius(string City, double? Celsius);
```

```csharp
await foreach (Columns<CityCelsius> batch in file.Scan<CityCelsius>().With(new ScanOptions { BatchRows = 8 }))
{
    using RowEncoding.RowKeys keys = RowEncoder.Encode(batch);
    Console.WriteLine($"{keys.RowCount} keys, {keys.TotalBytes} bytes in all");
    for (int row = 0; row < 4; row++)
    {
        Console.WriteLine($"  row {row}: {batch.City.GetString(row)}, {batch.Celsius[row]?.ToString() ?? "null"} -> {Convert.ToHexString(keys.Row(row))}");
    }
```

```
8 keys, 344 bytes in all
  row 0: Paris, null -> 02506172697300000000000000000000000000000000000000000000000000000005000000000000000000
  row 1: Paris, 10.1 -> 0250617269730000000000000000000000000000000000000000000000000000000501C024333333333333
  row 2: Paris, 10.2 -> 0250617269730000000000000000000000000000000000000000000000000000000501C024666666666666
  row 3: Paris, 10.3 -> 0250617269730000000000000000000000000000000000000000000000000000000501C02499999999999A
```

The sample's own class is called `RowKeys`, which is why it spells the library's type
`RowEncoding.RowKeys`; with `using Vorticity.RowEncoding;` your code writes `RowKeys`.

This is `Vorticity.RowEncoding`, a separate package, and it is **experimental**: its assembly is
marked `[Experimental("VX0002")]`, reported as an error wherever you use it until you opt in, as
[datasets.md](datasets.md) describes for VX0001. The diagnostic says why: the encoding follows an
upstream format that may change its bytes between Vortex releases, so keys are comparable only
with keys of the same `RowKeyEncoder.Format`. This build reproduces the bytes of
Vortex 0.86.1, which `RowEncoder.VortexVersion` states.

What it is for: when the comparison you need is the one a B-tree, a sort, a merge or an object
store already does on bytes, encoding each tuple once and comparing bytes is both faster and
simpler than comparing fields.

## The promise

```
row 6 against row 7: keys >, bytes >
sorted by key: 7, 0, 1, 2, 3, 4, 5, 6
```

`keys.Compare(6, 7)` is the tuple comparison and `keys.Row(6).SequenceCompareTo(keys.Row(7))` the
byte comparison, and they agree, always. That is the whole point, and what makes
`keys.SortIndices(order)` work: it sorts an array of row indices by their encoded keys, with no
comparer, no boxing and no field access. Row 7 is Lyon, the others Paris, and a null sorts before
every value by default, which is why row 0 comes before row 1.

`RowKeys` is disposable and owns one pooled buffer for every key of the batch: `Row(i)` is a span
into it, `Elements` the whole buffer, `Offsets` and `Sizes` its layout, `TotalBytes` its length.
The keys cover every row of the batch, selected or not.

## Direction and nulls

One `RowSortField` per column, in the record's member order; one field for every column; or none,
for ascending with nulls first:

```csharp
Show("descending, nulls first", batch, [RowSortField.Ascending, RowSortField.Ascending.WithDescending(true)]);
Show("ascending, nulls last", batch, [RowSortField.Ascending, RowSortField.Ascending.WithNullsLast()]);
```

```
  descending, nulls first: row 0 02506172697300000000000000000000000000000000000000000000000000000005000000000000000000, row 1 02506172697300000000000000000000000000000000000000000000000000000005013FDBCCCCCCCCCCCC
  ascending, nulls last: row 0 02506172697300000000000000000000000000000000000000000000000000000005020000000000000000, row 1 0250617269730000000000000000000000000000000000000000000000000000000501C024333333333333
```

Each option changes the bytes, not the comparison. Descending inverts the value bytes of that
column, so `memcmp` still yields the order you asked for; a null is one byte, placed before or
after every value in either direction.

## A key for a tuple you hold

```csharp
byte[] probe = RowEncoder.EncodeKey(new CityCelsius("Paris", 10.5));
byte[] prefix = RowEncoder.EncodeKey(new CityKey("Paris"));
```

```
the key of (Paris, 10.5): 0250617269730000000000000000000000000000000000000000000000000000000501C025000000000000; against row 1: >
the key of (Paris) is a prefix of row 1: True
```

`EncodeKey` writes one tuple, given as a record whose members are the key's columns in key order.
The record's schema gives the widths and the nullability the bytes depend on, so a probe encoded
from the same record type as the rows compares with them: to search sorted keys, to look one up,
or to build an object key from it. A record of the leading columns alone, `CityKey` here, encodes
a byte prefix of every key that starts with them, so a prefix query is a seek and a walk.

## From the tool path

```csharp
await foreach (BatchView batch in file.Scan("City", "Celsius").With(new ScanOptions { BatchRows = 8 }))
{
    using RowEncoding.RowKeys keys = RowEncoder.Encode(batch);
```

```
the tool path, 8 rows: row 1 01C02433333333333302506172697300000000000000000000000000000000000000000000000000000005
```

`Encode(BatchView)` encodes the columns of a batch in its schema order, and a batch holds its
columns in the file's order, not in the order `Scan` named them: here `Celsius` comes first, and
the keys order by it. Project exactly the key's columns, in a file whose order is the key's, or use
a record.

## Handing it to the writer

```csharp
VortexWriteOptions options = new VortexWriteOptions
{
    Indexes = IndexPolicy.None.ForKey([Reading.ColumnNames.City, Reading.ColumnNames.Day], IndexKind.SortedRuns, new RowKeyEncoder(), required: true),
};
```

```
index on (City, Day): vorticity.sorted.runs.v1, Built, 458520 bytes 
in the file: vorticity.sorted.runs.v1 on (City, Day), 2 runs, 200000 entries, 458520 bytes
the format it names: vortex-row 0.86.1 asc-nf; descending: vortex-row 0.86.1 asc-nf,desc-nf
```

The core ships no key encoder and finds none by reflection, so an index over a composite key names
its encoder in `IndexPolicy.ForKey`. `RowKeyEncoder` is one: it implements `IKeyEncoder`, turns
each tuple of the key's columns into one row key, and names its layout in `Format`, which the
writer records in the file so that a reader can tell keys it may compare from keys it may not. The
composite keys of one file share one encoder. A dataset with a clustering key of several columns
writes each object's sorted run this way. [indexes.md](indexes.md) says what an index costs and
when to ask for one.

## Watch out

* **The bytes are not stable across releases.** Keep them inside files this library writes, and
  compare only keys of the same `Format`.
* **The field list follows the columns you encode**, in their order: members for a record, schema
  order for a batch. No name is checked.
* `RowKeys` is disposable and its spans die with it.
* A date, time, timestamp or uuid column orders by its storage, which is its order. A variable-size list, a
  map, a variant, a union and a decimal wider than 128 bits have no order the format defines, and
  encoding one throws `VortexUnsupportedException`. A NaN is not canonicalised: two NaNs with
  different payloads differ.
* Encoding is a pass over the batch that produces a byte string per row: 344 bytes for these eight
  rows, 43 per row for a short text and a nullable double.

## Run it

```
dotnet run -c Release --project samples/Vorticity.Samples -- row-keys
```
