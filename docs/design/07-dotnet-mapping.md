# DType → .NET mapping

The type contract: what a caller receives for each Vortex dtype, and where a naive mapping would
lose data. The dtypes themselves are [02-format.md](02-format.md) §4.

## 1. The mapping

What a record member and a `Column<T>` may be, and what the column exposes:

| dtype | `T` | what `Column<T>` exposes | notes |
|---|---|---|---|
| `Null` | none | — | the schema shows it; no `T` reads it |
| `Bool` | `bool` | `Bits` as `ReadOnlySpan<ulong>`, an indexer | zero-copy |
| `Primitive` i8…i64, u8…u64 | `sbyte`…`long`, `byte`…`ulong` | `Values` as `ReadOnlySpan<T>`, an indexer | zero-copy |
| `Primitive` f16, f32, f64 | `Half`, `float`, `double` | the same | exact: `Half` is IEEE binary16 |
| `Decimal(p, s)` | `decimal` within §2's bounds, `VortexDecimal` always | an indexer, `Storage<TStorage>()`, `Scale` | never lossy: a column too wide for `decimal` is refused when it binds |
| `Utf8` | `string` | a UTF-8 span per value; `GetString(i)` on request | §4 |
| `Binary` | `ReadOnlyMemory<byte>` | a span per value | |
| `Struct` | a nested `[VortexRecord]` type | `Columns<TNested>` | |
| `List`, `FixedSizeList` | `ReadOnlyMemory<T>` | a `Range` per row, `Elements` as `Column<T>` | reading rows allocates one array per list |
| `vortex.date` | `DateOnly` | an indexer, `Storage<int>()` or `Storage<long>()` | |
| `vortex.time` | `TimeOnly` | an indexer, `Storage<TStorage>()`, `Unit` | |
| `vortex.timestamp` | `DateTime` when naive or UTC, `DateTimeOffset` with a zone | an indexer, `Storage<long>()`, `Unit`, `TimeZone` | §3 |
| `vortex.uuid` | `Guid` | an indexer | the byte order is converted explicitly, never implied |
| an extension registered on the session | the registered type | an indexer, `Storage<TStorage>()` | [14-public-api.md](14-public-api.md) §2 |

`Map`, `Union` and `Variant` columns appear in a schema but no `T` maps to them. An `enum` member
maps to its underlying integer; a `char` is refused, since it is no dtype.

**Nullability is carried by `T`**: `Column<double?>` is a nullable column and `Column<double>` is
not, and a non-nullable value type over a nullable column is refused when it binds
(`VortexSchemaException`). The `T?` names the column, not its storage: `Values` is still the
`ReadOnlySpan<double>` of the slots, a null slot holds an unspecified value, and `ValidityWords`
gives the validity as 64-bit words, empty when every row is valid, so a caller skips per-row checks
on the common case. Only the indexer and row materialization produce a `double?` per value.

## 2. Decimal: why `System.Decimal` is not the answer

Vortex allows a precision up to **76**, backed by `i256`; `System.Decimal` holds 28 to 29
significant digits. A default mapping to `decimal` would be **silently lossy over a legal range of
the format**, which is the one failure this library must not have.

So a `decimal` is accepted where it cannot lose anything, and refused everywhere else. A `decimal`
record member, `Column<decimal>`, `Sym<decimal>` or `ColumnBuilder<decimal>` binds to a column
whose precision is at most 28 and whose scale lies in `[0, 28]`, which `System.Decimal` holds
exactly. The check is made once, when the record or the column binds, and a wider column is refused
there with `VortexSchemaException` naming `VortexDecimal`, never rounded value by value.
`VortexDecimal` (`Types/Numerics/VortexDecimal.cs`) binds to every decimal column and is the only
mapping of one beyond those bounds; it converts to `decimal` or `Int128` when the value fits and
formats exactly always. Written from a record, a `decimal` member declares `Decimal(28, 10)` unless
`[VortexColumn]` gives its precision and scale.

The exact bounds, from the reference and transcribed in
[spec/METADATA.md](../../spec/METADATA.md):

* `1 ≤ precision ≤ 76`
* `scale ≤ 76`
* `scale ≤ precision` **only when `scale > 0`**: a negative scale is legal, bounded only by `i8`

A reader that refuses a precision above 38, or requires `scale ≥ −precision`, refuses legal files.

**Storage width** is carried in `vortex.decimal`'s metadata as `values_type`: `i8` (0), `i16` (1),
`i32` (2), `i64` (3), `i128` (4), `i256` (5). The precision selects the *smallest* legal width —
1–2 → `i8`, 3–4 → `i16`, 5–9 → `i32`, 10–18 → `i64`, 19–38 → `i128`, 39–76 → `i256` — but a file may
declare any **wider** one, and often does: upstream allows precision-2 values in an `i256` buffer,
and a decimal column from Arrow or Parquet arrives as `i128` or `i256` whatever its precision. The
width is therefore read from `values_type`, and only a width *narrower* than the precision requires
is refused; `Storage<TStorage>()` exposes what the file declared, for callers who do their own
arithmetic without widening.

.NET has `Int128` and no `Int256`, so this library carries an internal `Int256` with what the
decimal path needs: comparison, negation, two's-complement bytes in both endiannesses and exact
text. It is correctness insurance, not a hot path: precision ≤ 38 is what files hold in practice.

The row encoder's narrower table (up to `i128`, no `Decimal256`) is `vortex-row`'s own limit, not
the format's ([06-row-encoding.md](06-row-encoding.md) §6).

## 3. Temporal extensions: one resolution, at binding

`vortex.timestamp` carries a unit and a time zone in its metadata. Resolving an IANA zone takes the
operating system's time-zone database through `TimeZoneInfo`, which makes the result
**machine-dependent**.

So the zone is resolved once, when a `DateTimeOffset` member or column binds to the file, by
`TimeZoneInfo.FindSystemTimeZoneById`, and never per value. A naive or UTC timestamp binds as
`DateTime`, with `Kind` `Unspecified` or `Utc`, and needs no database. A zone the host cannot
resolve refuses the `DateTimeOffset` binding with `VortexSchemaException`; the column still binds
as `long`, its storage, and `Storage()`, `Unit` and `TimeZone` let a caller convert with a zone of
its own choosing. The machine dependence is confined to one call, at one moment, with a refusal
rather than a guess.

## 4. Strings: spans first, `string` on request

A `vortex.varbinview` value is a 16-byte view into the file's buffers, so the natural accessor is a
UTF-8 span, and a `string` is an allocation the caller asks for (`GetString`).
[text-columns.md](../guide/text-columns.md) measures the difference.

The lifetime rule is the sharp edge: **a span borrowed from a batch is invalid once the enumeration
moves on.** `Columns<TRecord>` and `Column<T>` are `ref struct`s, so the compiler keeps them inside
the loop body and out of an `await`, and analyzer VX1001 flags a span stored outside it. A caller
who needs the values later copies them, or takes an owned `RecordBatch`.

## 5. A root that is not a struct

A file's root dtype may be a bare `Float64`, a `Bool`, or any type, and the reader does not assume a
table. The schema of such a file is one field with an empty name, whose type is the root. A record
binds to the fields of a struct root only, and `Scan<TRecord>()` on any other root throws
`VortexSchemaException` pointing at the tool scan, which reads the root as its single column
([untyped-files.md](../guide/untyped-files.md)). Inventing a synthetic column name instead would
make a round trip through this writer produce another schema than its input, which is worse than an
awkward API.
