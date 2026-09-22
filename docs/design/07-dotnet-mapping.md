# DType → .NET mapping

The public type contract. Vortex's logical type system is specified in
[02-format.md](02-format.md) §4; this document says what a caller actually receives, and where a
naive mapping would lose data.

## 1. Mapping table

What a record member and a `Column<T>` may be; [14-public-api.md](14-public-api.md) §3.1 lists
the accessors of each.

| DType | `T` | Zero-copy | Overflow / loss behavior |
|---|---|---|---|
| `Null` | none: the schema shows it, no `T` reads it | n/a | — |
| `Bool` | `bool`: `Bits` as `ReadOnlySpan<ulong>`, an indexer | yes | — |
| `Primitive(U8…I64)` | `byte…long`: `Values` as `ReadOnlySpan<T>` | yes | — |
| `Primitive(F32/F64)` | `float`, `double` | yes | — |
| `Primitive(F16)` | `Half` | yes | `Half` is IEEE binary16 — exact match |
| `Decimal(p, s)` | `decimal` within §2's bounds, `VortexDecimal` always | yes (storage) | never lossy; a column too wide for `decimal` is refused at binding |
| `Utf8` | `string`: a UTF-8 span per value | yes | `GetString` allocates, opt-in |
| `Binary` | `ReadOnlyMemory<byte>`: a span per value | yes | — |
| `Struct` | a nested `[VortexRecord]` type: `Columns<TNested>` | yes | — |
| `List`, `ListView`, `FixedSizeList` | `ReadOnlyMemory<T>`: a `Range` per row, `Elements` as `Column<T>` | yes | a row allocates one array per list |
| `Extension(vortex.date)` | `DateOnly`, over its `int` or `long` days | yes | — |
| `Extension(vortex.time)` | `TimeOnly`, with the column's `Unit` | yes | — |
| `Extension(vortex.timestamp)` | `DateTime` when naive or UTC, `DateTimeOffset` with a zone (see §3) | yes | — |
| `Extension(vortex.uuid)` | `Guid` | yes | the byte order is converted explicitly, never implied |

Nullability is carried by `T`: `Column<double?>` is a nullable column and `Column<double>` is not,
and a non-nullable value type over a nullable column is refused at binding with
`VortexSchemaException`. The `T?` is the name of the column, not of its storage: `Values` is still
the `ReadOnlySpan<double>` of the slots, a null slot holds an unspecified value, and
`ValidityWords` gives the validity as 64-bit words, empty when every row is valid, so a caller
skips the per-row checks on the common case. Only the indexer and `ToRecordsAsync` produce a
`double?` per value.

## 2. Decimal: why `System.Decimal` is not the answer

Vortex allows precision up to **76**, backed by `i256`. `System.Decimal` holds 28–29 significant
digits. A default mapping to `decimal` would therefore be **silently lossy over a legal range of
the format** — the exact failure mode this library must not have.

What is accepted instead is a `decimal` where it cannot lose anything, and a refusal everywhere
else. A `decimal` record member, `Column<decimal>`, `Sym<decimal>` or `ColumnBuilder<decimal>`
binds to a decimal column whose precision is at most 28 and whose scale lies in `[0, 28]`, which
`System.Decimal` holds exactly; the check is made once, when the record or the column binds to the
file, and a wider column is refused there with `VortexSchemaException` naming `VortexDecimal`, never
rounded value by value. `VortexDecimal` binds to every decimal column, and it is the only mapping of
a column beyond those bounds, for a record member and a `Column<T>` alike. Written from a record, a
`decimal` member declares `Decimal(28, 10)` unless `[VortexColumn]` gives its precision and scale.

The exact bounds, from `vortex-array/src/dtype/decimal/mod.rs` (`MAX_PRECISION` is
`<i256>::MAX_PRECISION`), transcribed in [spec/METADATA.md](../../spec/METADATA.md):

* `1 ≤ precision ≤ 76`
* `scale ≤ 76`
* `scale ≤ precision` **only when `scale > 0`** — negative scale is legal and bounded only by `i8`

A reader that rejects precision > 38, or that requires `scale ≥ -precision`, refuses legal files.

```csharp
public readonly struct VortexDecimal : IEquatable<VortexDecimal>, IComparable<VortexDecimal>
{
    public static VortexDecimal FromInt128(Int128 unscaled, byte precision, sbyte scale);
    public static VortexDecimal FromInt64(long unscaled, byte precision, sbyte scale);
    public byte   Precision { get; }
    public sbyte  Scale { get; }
    public bool   IsNegative { get; }

    public decimal ToDecimal();          // throws OverflowException outside decimal's range
    public bool TryToDecimal(out decimal value);
    public bool TryToInt128(out Int128 value);
    public override string ToString();   // always exact
    public bool TryFormat(Span<char> destination, out int charsWritten);
}
```

The unscaled value is held in an `Int256` of our own (see below), which stays internal: a caller
reaches it as an `Int128` when it fits, and as exact text always.

Storage width is the `DecimalType` enum, carried in `vortex.decimal`'s metadata as `values_type`:
`i8` (0), `i16` (1), `i32` (2), `i64` (3), `i128` (4), `i256` (5). Precision selects the *smallest*
legal width — p1–2 → `i8`, 3–4 → `i16`, 5–9 → `i32`, 10–18 → `i64`, 19–38 → `i128`, 39–76 → `i256` —
but the file may declare any **wider** one, and often does: upstream's own `DecimalArray` doc allows
precision-2 values in an `i256` buffer, and every Arrow- or Parquet-sourced decimal column arrives
as `i128`/`i256` regardless of precision. A reader must therefore take the width from `values_type`
and reject only a width *narrower* than the precision requires; `Storage<TStorage>()` on a
`Column<decimal>` or a `Column<VortexDecimal>` reads what the file declared. It exposes the storage
span directly for callers who want to do their own arithmetic without widening.

**`Decimal256` is reachable and `net11.0` has no `Int256`.** `Int128` is in-box; the 256-bit case
is not, so a `readonly struct Int256` of our own is a Phase 1 deliverable, not an optional extra:
without it, a legal `Decimal(40, 2)` column cannot be read at all. It needs comparison, negation,
two's-complement byte access in both endiannesses, and exact decimal `ToString` — not general
arithmetic. `Int128` covers precision ≤ 38, which is every decimal anyone actually writes, so the
`i256` path is correctness insurance rather than a hot path and may be scalar and simple.

Note that the row encoder's narrower table (19–38 → `i128`, no `Decimal256`) stays correct: that
limit is `vortex-row`'s own, not the format's — see [06-row-encoding.md](06-row-encoding.md) §6.

## 3. Temporal extensions: one resolution, at binding

`vortex.timestamp` carries a unit and a timezone in its extension metadata. Resolving an IANA
timezone identifier requires the OS timezone database via `TimeZoneInfo`, which makes the result
**machine-dependent** and interacts badly with `InvariantGlobalization`.

Decision: the zone is resolved once, when a `DateTimeOffset` member or column binds to the file,
by `TimeZoneInfo.FindSystemTimeZoneById`, and never per value. A naive or UTC timestamp binds as
`DateTime`, with `Kind` `Unspecified` or `Utc`, and needs no database. A zone the host cannot
resolve refuses the `DateTimeOffset` binding with `VortexSchemaException`; the column still binds
as `long`, its storage, and `Storage()`, `Unit` and `TimeZone` give a caller what it needs to
convert with a zone of its own choosing.

The machine dependence is therefore confined to one call, at one moment, with a refusal rather
than a guess when the host lacks the zone.

Note that `Extension` is **not** supported by the row encoder (see [06-row-encoding.md](06-row-encoding.md) §6):
temporal sort keys require the caller to normalize to the storage column explicitly. We do not
silently unwrap extensions there, or our bytes would stop matching Rust's.

## 4. Strings: spans first, `string` on request

`vortex.varbinview` values are 16-byte views pointing into file buffers. The nominal accessor is
therefore a UTF-8 span, and materializing a `string` is an opt-in allocation:

```csharp
await foreach (Columns<Reading> batch in file.Scan<Reading>())
{
    Column<string> city = batch.City;
    for (int i = 0; i < city.Length; i++)
    {
        ReadOnlySpan<byte> utf8 = city[i];          // zero-copy, valid for this iteration of the loop
        // city.GetString(i) allocates
    }
}
```

The lifetime rule is the sharp edge of this design: **a span borrowed from a batch is invalid once
the enumeration moves on.** `Columns<TRecord>` and `Column<T>` are `ref struct`s, so the compiler
keeps them inside the loop body and out of an `await`; VX1001 flags a span stored outside it. A
caller who needs the values later copies them, or takes an owned `RecordBatch` with
`ToBatchesAsync` or `ToOwned`.

## 5. Non-struct root

A Vortex file's root DType may be a bare `Float64`, `Bool`, or any other type — the reader must
not assume a tabular schema. The schema of such a file is one field with an empty name, whose type
is the root:

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
bool tabular = file.Schema.Count != 1 || file.Schema[0].Name.Length != 0;
await foreach (BatchView batch in file.Scan())
{
    Column<double> values = batch.Column<double>(0);    // the root itself
}
```

A record binds to the columns of a struct root, and `Scan<TRecord>()` on any other root throws
`VortexSchemaException` pointing at the tool scan, which reads the root as its single column. The
alternative, inventing a synthetic column name, would make round-tripping through our own writer
produce a different schema than the input, which is worse than an awkward API.
