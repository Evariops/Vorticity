# DType → .NET mapping

The public type contract. Vortex's logical type system is specified in
[02-format.md](02-format.md) §4; this document says what a caller actually receives, and where a
naive mapping would lose data.

## 1. Mapping table

| DType | Canonical .NET representation | Zero-copy | Overflow / loss behavior |
|---|---|---|---|
| `Null` | `VortexNullColumn` (length only) | n/a | — |
| `Bool` | `ReadOnlySpan<byte>` bitmap + `bool this[int]` | yes | — |
| `Primitive(U8…I64)` | `ReadOnlySpan<T>` for the matching `byte…long` | yes | — |
| `Primitive(F32/F64)` | `ReadOnlySpan<float/double>` | yes | — |
| `Primitive(F16)` | `ReadOnlySpan<Half>` | yes | `Half` is IEEE binary16 — exact match |
| `Decimal(p, s)` | `VortexDecimal` (see §2) | yes (storage) | never lossy; `ToDecimal()` throws out of range |
| `Utf8` | `ReadOnlySpan<byte>` UTF-8 per value | yes | `ToString()` allocates, opt-in |
| `Binary` | `ReadOnlySpan<byte>` per value | yes | — |
| `Struct` | `VortexStructColumn` → child columns by index or name | yes | — |
| `List` / `ListView` | offsets/sizes + child column | yes | — |
| `FixedSizeList` | child column + constant stride | yes | — |
| `Extension(vortex.date)` | `int`/`long` days + `ToDateOnly()` | yes | — |
| `Extension(vortex.time)` | storage int + unit metadata + `ToTimeOnly()` | yes | — |
| `Extension(vortex.timestamp)` | storage int + unit + timezone metadata (see §3) | yes | conversion is environment-dependent |
| `Extension(vortex.uuid)` | 16-byte storage + `ToGuid()` | yes | endianness conversion is explicit, not implied |

Nullability is orthogonal and never encoded in the .NET type: every column exposes
`bool IsValid(int index)` and a `ValidityKind` (`NonNullable`, `AllValid`, `AllInvalid`, `Bitmap`)
so callers can skip per-row checks on the common cases. Nullable columns do **not** map to
`T?`/`Nullable<T>` — that would allocate and destroy the span contract.

## 2. Decimal: why `System.Decimal` is not the answer

Vortex allows precision up to **76**, backed by `i256`. `System.Decimal` holds 28–29 significant
digits. A default mapping to `decimal` would therefore be **silently lossy over a legal range of
the format** — the exact failure mode this library must not have.

The exact bounds, from `vortex-array/src/dtype/decimal/mod.rs` (`MAX_PRECISION` is
`<i256>::MAX_PRECISION`), transcribed in [spec/METADATA.md](../spec/METADATA.md):

* `1 ≤ precision ≤ 76`
* `scale ≤ 76`
* `scale ≤ precision` **only when `scale > 0`** — negative scale is legal and bounded only by `i8`

A reader that rejects precision > 38, or that requires `scale ≥ -precision`, refuses legal files.

```csharp
public readonly struct VortexDecimal
{
    public Int256 Unscaled { get; }      // see below: not in the BCL, we write it
    public byte   Precision { get; }
    public sbyte  Scale { get; }

    public decimal ToDecimal();          // throws OverflowException outside decimal's range
    public bool TryToDecimal(out decimal value);
    public bool TryToInt128(out Int128 value);
    public override string ToString();   // always exact
}
```

Storage width is the `DecimalType` enum, carried in `vortex.decimal`'s metadata as `values_type`:
`i8` (0), `i16` (1), `i32` (2), `i64` (3), `i128` (4), `i256` (5). Precision selects the *smallest*
legal width — p1–2 → `i8`, 3–4 → `i16`, 5–9 → `i32`, 10–18 → `i64`, 19–38 → `i128`, 39–76 → `i256` —
but the file may declare any **wider** one, and often does: upstream's own `DecimalArray` doc allows
precision-2 values in an `i256` buffer, and every Arrow- or Parquet-sourced decimal column arrives
as `i128`/`i256` regardless of precision. A reader must therefore take the width from `values_type`
and reject only a width *narrower* than the precision requires; `DecimalColumn.Storage` reports what
the file declared. The column exposes the storage span directly for callers who want to do their own
arithmetic without widening.

**`Decimal256` is reachable and `net11.0` has no `Int256`.** `Int128` is in-box; the 256-bit case
is not, so a `readonly struct Int256` of our own is a Phase 1 deliverable, not an optional extra:
without it, a legal `Decimal(40, 2)` column cannot be read at all. It needs comparison, negation,
two's-complement byte access in both endiannesses, and exact decimal `ToString` — not general
arithmetic. `Int128` covers precision ≤ 38, which is every decimal anyone actually writes, so the
`i256` path is correctness insurance rather than a hot path and may be scalar and simple.

Note that the row encoder's narrower table (19–38 → `i128`, no `Decimal256`) stays correct: that
limit is `vortex-row`'s own, not the format's — see [06-row-encoding.md](06-row-encoding.md) §6.

## 3. Temporal extensions: the core stays dumb on purpose

`vortex.timestamp` carries a unit and a timezone in its extension metadata. Resolving an IANA
timezone identifier requires the OS timezone database via `TimeZoneInfo`, which makes the result
**machine-dependent** and interacts badly with `InvariantGlobalization`.

Decision: the core exposes the raw storage value plus the parsed metadata (unit, timezone string)
and performs no timezone resolution. Conversion helpers are explicit and documented as
environment-dependent:

```csharp
DateTimeOffset ToDateTimeOffset(TimeZoneInfo tz);   // caller supplies the zone
DateTime       ToUtcDateTime();                     // valid only when the column is UTC or naive
```

This keeps the library deterministic and dependency-free, and pushes an inherently ambiguous
decision to the layer that has the context to make it.

Note that `Extension` is **not** supported by the row encoder (see [06-row-encoding.md](06-row-encoding.md) §6):
temporal sort keys require the caller to normalize to the storage column explicitly. We do not
silently unwrap extensions there, or our bytes would stop matching Rust's.

## 4. Strings: spans first, `string` on request

`vortex.varbinview` values are 16-byte views pointing into file buffers. The nominal accessor is
therefore a UTF-8 span, and materializing a `string` is an opt-in allocation:

```csharp
var col = batch.Column(2).AsBinary();
for (int i = 0; i < col.Length; i++)
{
    if (!col.IsValid(i)) continue;
    ReadOnlySpan<byte> utf8 = col.GetSpan(i);   // zero-copy, valid until batch.Dispose()
    // col.GetString(i) allocates
}
```

The lifetime rule is the sharp edge of this design and must be stated at every entry point:
**spans borrowed from a batch are invalid after that batch is disposed.** Callers who need to
outlive the batch copy explicitly.

## 5. Non-struct root

A Vortex file's root DType may be a bare `Float64`, `Bool`, or any other type — the reader must
not assume a tabular schema. The API models this directly rather than faking a table:

```csharp
public DType Schema { get; }                 // may be any DType
public bool  IsTabular => Schema.Kind == DTypeKind.Struct;
```

`Scan()` on a non-struct root yields batches whose single column is the root itself, exposed as
`batch.Root` with `batch.FieldCount == 1` and a null field name. The alternative — inventing a
synthetic column name — would make round-tripping through our own writer produce a different
schema than the input, which is worse than an awkward API.
