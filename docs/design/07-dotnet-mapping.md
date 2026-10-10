# DType → .NET mapping

The type contract: what a caller receives for each Vortex dtype, and where a naive mapping would lose
data. The dtypes themselves are described in [the format's DType
section](02-format.md#4-dtype-dtypefbs--dtypeproto).

## 1. The mapping

What a record member and a `Column<T>` may be, and what the column exposes:

| dtype | `T` | what `Column<T>` exposes | notes |
|---|---|---|---|
| `Null` | none | nothing | the schema shows it, but no `T` reads it |
| `Bool` | `bool` | `Bits` as `ReadOnlySpan<ulong>`, an indexer | zero-copy |
| `Primitive` i8…i64, u8…u64 | `sbyte`…`long`, `byte`…`ulong` | `Values` as `ReadOnlySpan<T>`, an indexer | zero-copy |
| `Primitive` u16 | `char` too | the same | the UTF-16 code unit, lone surrogates included |
| `Primitive` i64, u64 | `nint`, `nuint` too, on a 64-bit host | the same | refused on a 32-bit host, whose native integer is narrower |
| `Primitive` i64 | `TimeSpan` too | an indexer, `CopyTo`, `AsStorage()` | 100 ns ticks, exact over the whole range |
| `Primitive` f16, f32, f64 | `Half`, `float`, `double` | the same | exact, since `Half` is IEEE binary16 |
| `Decimal(p, s)` | `decimal` within [the bounds below](#2-decimal-why-systemdecimal-is-not-the-answer), `VortexDecimal` always | an indexer, `AsStorage<TStorage>()`, `Scale` | never lossy: a column too wide for `decimal` is refused when it binds |
| `Decimal(p, 0)` | `Int128`, `UInt128` for `p ≤ 39`, `BigInteger` always | an indexer, `CopyTo`, and `Values` zero-copy over 128-bit storage | see [wider integers](#21-integers-wider-than-64-bits) |
| `Utf8` | `string` | a UTF-8 span per value, `GetString(i)` on request | see [strings](#4-strings-spans-first-string-on-request) |
| `Binary` | `ReadOnlyMemory<byte>`, `Memory<byte>`, `byte[]` | a span per value | |
| `Struct` | a nested `[VortexRecord]` type | `Columns<TNested>` | |
| `List`, `FixedSizeList` | `ReadOnlyMemory<T>`, `Memory<T>`, `T[]`, `List<T>`, `ImmutableArray<T>`, an interface an array implements | a `Range` per row, `Elements` as `Column<T>` | reading rows allocates one array, or one `List<T>`, per list |
| `List` of `Struct` | any of the above over a `[VortexRecord]` type | `ListOf<TNested>()`: a `Range` per row, `Elements` as `Columns<TNested>` | the elements are read in one pass, then cut per row |
| `Map` | `Dictionary<TKey, TValue>`, `IDictionary`, `IReadOnlyDictionary` | `MapOf<TKey, TValue>()`: a `Range` per row, `Keys` and `Values` | scalar keys and values, and a key is never null |
| `vortex.date` | `DateOnly` | an indexer, `AsStorage<int>()` or `AsStorage<long>()` | |
| `vortex.time` | `TimeOnly` | an indexer, `AsStorage<TStorage>()`, `Unit` | |
| `vortex.timestamp` | `DateTime` when naive or UTC, `DateTimeOffset` with a zone | an indexer, `AsStorage<long>()`, `Unit`, `TimeZone` | see [temporal extensions](#3-temporal-extensions-one-resolution-at-binding) |
| `vortex.uuid` | `Guid` | an indexer | the byte order is converted explicitly, never implied |
| an extension registered on the session | the registered type | an indexer, `AsStorage<TStorage>()` | see [the session](14-public-api.md#2-the-session) |

`Union` and `Variant` columns appear in a schema but no `T` maps to them. An `enum` member maps to
its underlying integer.

Nullability is carried by `T`. `Column<double?>` is a nullable column and `Column<double>` is not,
and a non-nullable value type over a nullable column is refused when it binds
(`VortexSchemaException`). The `T?` names the column, not its storage: `Values` is still the
`ReadOnlySpan<double>` of the slots, where a null slot holds an unspecified value, and
`ValidityWords` gives the validity as 64-bit words, empty when every row is valid, so a caller can
skip per-row checks in the common case. Only the indexer and row materialization produce a `double?`
per value.

## 2. Decimal: why `System.Decimal` is not the answer

Vortex allows a precision of up to 76, backed by `i256`, while `System.Decimal` holds 28 to 29
significant digits. A default mapping to `decimal` would be silently lossy over a legal range of the
format, which is the one failure this library must not have.

So a `decimal` is accepted where it cannot lose anything, and refused everywhere else. A `decimal`
record member, `Column<decimal>`, `Sym<decimal>` or `ColumnBuilder<decimal>` binds to a column whose
precision is at most 28 and whose scale lies in `[0, 28]`, which `System.Decimal` holds exactly. The
check is made once, when the record or the column binds, and a wider column is refused there with a
`VortexSchemaException` naming `VortexDecimal`, never rounded value by value. `VortexDecimal`
(`Types/Numerics/VortexDecimal.cs`) binds to every decimal column and is the only mapping for one
beyond those bounds. It converts to `decimal` or `Int128` when the value fits, and always formats
exactly. Written from a record, a `decimal` member declares `Decimal(28, 10)` unless `[VortexColumn]`
gives its precision and scale.

The exact bounds, from the reference and transcribed in [spec/METADATA.md](../../spec/METADATA.md):

* `1 ≤ precision ≤ 76`
* `scale ≤ 76`
* `scale ≤ precision` only when `scale > 0`. A negative scale is legal, bounded only by `i8`

A reader that refuses a precision above 38, or requires `scale ≥ −precision`, refuses legal files.

The storage width is carried in `vortex.decimal`'s metadata as `values_type`: `i8` (0), `i16` (1),
`i32` (2), `i64` (3), `i128` (4), `i256` (5). The precision selects the smallest legal width (1–2 →
`i8`, 3–4 → `i16`, 5–9 → `i32`, 10–18 → `i64`, 19–38 → `i128`, 39–76 → `i256`), but a file may
declare any wider one, and often does. Upstream allows precision-2 values in an `i256` buffer, and a
decimal column from Arrow or Parquet arrives as `i128` or `i256` whatever its precision. The width is
therefore read from `values_type`, and only a width narrower than the precision requires is refused.
`AsStorage<TStorage>()` exposes what the file declared, for callers who do their own arithmetic without
widening.

.NET has `Int128` but no `Int256`, so this library carries an internal `Int256` with what the decimal
path needs: comparison, negation, two's-complement bytes in both endiannesses and exact text. It is
correctness insurance, not a hot path, since precision ≤ 38 is what files hold in practice.

The row encoder's narrower table (up to `i128`, no `Decimal256`) is `vortex-row`'s own limit, not the
format's (see [unsupported types](06-row-encoding.md#6-unsupported-types)).

### 2.1 Integers wider than 64 bits

No primitive dtype is wider than 64 bits, so an `Int128`, a `UInt128` or a `BigInteger` is stored as
a decimal of scale 0, whose unscaled value is the integer. The default width is the choice that
matters:

* `Int128` and `UInt128` declare `Decimal(38, 0)`, stored in 128 bits. Every reader of a 128-bit
  decimal reads it, Arrow's `Decimal128` among them, and `Values` is the stored buffer with no copy.
  Thirty-eight digits do not cover every value of either type, whose extremes have 39, so a value
  beyond them is refused when it is written, never wrapped or rounded, and the message gives the way
  out: `[VortexColumn(Precision = 39)]`, stored in 256 bits.
* `BigInteger` declares `Decimal(76, 0)`, as far as a decimal goes, and a value beyond it is refused.

On the read side, `Int128` and `UInt128` bind to a decimal of scale 0 and at most 39 digits, whatever
width stores it. Thirty-eight digits always fit. A 39th digit can hold a value beyond the type, which
is refused as it is read (the same way a timestamp beyond `DateTime` is), and a `UInt128` refuses a
negative value. `BigInteger` binds to any decimal of scale 0 and reads every value.

The aggregates follow suit. A sum over such a column is exact in 320 bits whatever its row count, and
converts to the type asked for, throwing `OverflowException` when it does not fit. A sum as
`BigInteger` never throws.

## 3. Temporal extensions: one resolution, at binding

`vortex.timestamp` carries a unit and a time zone in its metadata. Resolving an IANA zone goes
through the operating system's time-zone database via `TimeZoneInfo`, which makes the result
machine-dependent.

So the zone is resolved once, when a `DateTimeOffset` member or column binds to the file, by
`TimeZoneInfo.FindSystemTimeZoneById`, and never per value. A naive or UTC timestamp binds as
`DateTime`, with `Kind` `Unspecified` or `Utc`, and needs no database. A zone the host cannot resolve
makes the `DateTimeOffset` binding fail with `VortexSchemaException`. The column still binds as
`long`, its storage, and `AsStorage()`, `Unit` and `TimeZone` let a caller convert with a zone of its
own choosing. The machine dependence is confined to one call, at one moment, with a refusal rather
than a guess.

## 4. Strings: spans first, `string` on request

A `vortex.varbinview` value is a 16-byte view into the file's buffers, so the natural accessor is a
UTF-8 span, and a `string` is an allocation the caller asks for (`GetString`).
[text-columns.md](../guide/text-columns.md) measures the difference.

The lifetime rule is the sharp edge: a span borrowed from a batch becomes invalid once the
enumeration moves on. `Columns<TRecord>` and `Column<T>` are `ref struct`s, so the compiler keeps
them inside the loop body and out of an `await`, and analyzer VX1001 flags a span stored outside it.
A caller who needs the values later copies them, or takes an owned `RecordBatch`.

## 5. A root that is not a struct

A file's root dtype may be a bare `Float64`, a `Bool` or any other type, and the reader does not
assume a table. The schema of such a file is one field with an empty name, whose type is the root. A
record only binds to the fields of a struct root, and `Scan<TRecord>()` on any other root throws a
`VortexSchemaException` pointing at the tool scan, which reads the root as its single column
([untyped-files.md](../guide/untyped-files.md)). Inventing a synthetic column name instead would make
a round trip through this writer produce a different schema from its input, which is worse than an
awkward API.
