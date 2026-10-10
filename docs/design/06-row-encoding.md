# Byte-sortable row encoding

The row encoding is a separate specification from the file format, implemented upstream by the
`vortex-row` crate. It converts one or more columnar arrays into a `ListView<u8>`, one byte string
per row, such that lexicographic byte comparison of the encoded rows equals logical tuple comparison
of the input values, under per-column sort direction and null placement.

It serves sort keys, row keys, range partitioning, merge joins and index entries: anywhere a
comparator has to become a `memcmp`.

> Upstream marks this format experimental. The byte layout, the supported types and the edge cases
> may change between Vortex releases, and its spec says not to persist these bytes as an interchange
> format. So it ships in its own package, `Vorticity.RowEncoding`, marked
> `[Experimental("VX0002")]`, and the encoder names the release its bytes follow (`vortex-row
> 0.86.1`). Encoded bytes are safe to compare within one process, or within one cluster running one
> version, and a durable index that stores them records that name (see [composite
> keys](10-indexes.md#54-composite-keys-through-the-row-encoding)).

## 1. Contract

For columns `c0…cn` with a per-column `RowSortField { descending: bool, nulls_first: bool }`:

```
encode(row_a) < encode(row_b)  ⟺  (a.c0, …, a.cn) < (b.c0, …, b.cn)
```

The bytes carry no type tags, no field names and no sort options. Two encoded rows are only
comparable if they were produced from the same schema with the same field settings. `descending` and
`nulls_first` are independent, so nulls can sort first or last in either direction.

Notation: `||` is concatenation, `BE(x)` fixed-width big-endian bytes (never length-prefixed, never
trimmed), `!b` is `b XOR 0xFF`, and `zero(n)` and `ff(n)` are n bytes of `0x00` and `0xFF`.

## 2. Sentinels

Every value starts with a sentinel byte that classifies nullness before any value byte is compared.

| Family | Case | Asc, nulls first | Desc, nulls first | Asc, nulls last | Desc, nulls last |
|---|---|---|---|---|---|
| Fixed-width | Null | `0x00` | `0x00` | `0x02` | `0x02` |
| Fixed-width | Non-null | `0x01` | `0x01` | `0x01` | `0x01` |
| Variable-width | Null | `0x00` | `0x00` | `0xFF` | `0xFF` |
| Variable-width | Empty | `0x01` | `0xFE` | `0x01` | `0xFE` |
| Variable-width | Non-empty | `0x02` | `0xFD` | `0x02` | `0xFD` |

Fixed-width sentinels serve Null, Bool, Primitive, Decimal, Struct and FixedSizeList.
Variable-width sentinels serve Utf8 and Binary.

`descending` never inverts the null sentinel. Only non-null value bytes are inverted, plus, for
variable-width types, the empty and non-empty sentinels. That is what keeps null placement
independent of sort direction, and it is the single easiest thing to get wrong.

A fixed-width null is followed by zero-filled value bytes, so fixed types keep a constant encoded
width on every row.

## 3. Per-type encodings

| Type | Ascending | Descending |
|---|---|---|
| `Null` | `sentinel` (no body) | same |
| `Bool` | `0x01 \|\| 0x01` (false), `0x01 \|\| 0x02` (true) | `0x01 \|\| 0xFE` (false), `0x01 \|\| 0xFD` (true) |
| `u8/u16/u32/u64` | `0x01 \|\| BE(v)` | `0x01 \|\| !BE(v)` |
| `i8/i16/i32/i64` (and `i128`) | `0x01 \|\| ordered`, where `ordered = BE(v)` with `ordered[0] ^= 0x80` | `0x01 \|\| !ordered` |
| `f16/f32/f64` | `0x01 \|\| BE(ordered)` | `0x01 \|\| !BE(ordered)` |
| `Decimal` | signed-integer encoding of the scaled storage value | same |
| `Utf8` / `Binary` | variable-width sentinel + block body ([below](#4-variable-length-body)) | inverted ([below](#4-variable-length-body)) |
| `Struct` | `sentinel \|\| field_0 \|\| … \|\| field_n`, fields recursive in schema order | same |
| `FixedSizeList<T, N>` | `sentinel \|\| elem_0 \|\| … \|\| elem_{N-1}` | same |

Null bodies for fixed-width types are `null_sentinel || zero(width(T))`.

Floats are transformed from their raw IEEE bits:

```
ordered = sign_bit(bits) == 0 ? bits XOR sign_bit_mask
                              : bits XOR all_ones
```

This yields a total order, negatives before positives and `-0.0` before `+0.0`. NaNs order by their
raw bit pattern and are not canonicalized, so two NaNs with different payloads are two different
keys.

A decimal's storage width is selected from its precision:

| Precision | Storage |
|---|---|
| 1–2 | `i8` |
| 3–4 | `i16` |
| 5–9 | `i32` |
| 10–18 | `i64` |
| 19–38 | `i128` |

`Decimal256` is not supported.

Option inheritance is the identity, and inversion happens exactly once. A nested field uses its
parent's `RowSortField` unchanged, as `vortex-row/src/codec.rs` shows: `encode_struct` passes the
same `field` to `field_encode` for every child. Inversion for `descending` is applied by the leaf
encoder to its own value bytes only, and the parent never inverts the concatenated body again. The
classic double-inversion trap is avoided by doing nothing at the parent level.

The child null sentinel is chosen by the child's dtype, using the inherited field.
`child_canonical_null_byte(child_dtype, field)` returns the variable-width null sentinel for `Utf8`
and `Binary` children and the fixed-width null sentinel for everything else, including a nested
struct or FSL acting as a variable-width child. The `nulls_first` that selects between `0x00` and
`0x02` or `0xFF` is the inherited one, which is the root column's. For a fixed-width child under a
null parent, the body is that sentinel byte followed by zero fill up to the child's fixed width, zero
even under `descending`.

Nested nulls are canonicalized. A null struct or null fixed-size list must still emit a body, so that
two null parents compare byte-equal whatever their physical child arrays contain. Fixed-width
children contribute their fixed-width null encoding, and variable-width children contribute exactly
one child null sentinel byte. Skipping this is a correctness bug that only shows up on arrays with
garbage under a null, which is exactly what a differential test against Rust catches and a naive
round-trip test does not.

## 4. Variable-length body

Non-empty Utf8 and Binary bodies are written in blocks of 32 data bytes plus 1 marker byte:

* Ascending: every non-final full block uses marker `0xFF`, and the final block is zero-padded to 32
  data bytes with the real data length, in `1..=32`, as its marker.
* Descending: every data byte is inverted, the continuation marker is `0x00`, the final block is
  padded with `0xFF`, and the final marker is `final_len XOR 0xFF`.
* If the length is an exact multiple of 32, the final block carries marker `32` and earlier blocks
  use the continuation marker.

This preserves prefix ordering: a shorter value reaches its final marker (`≤ 32`) while a longer one
is still emitting continuation markers (`0xFF`), so the prefix sorts first.

## 5. Output layout

```
elements: contiguous u8 buffer holding all row bytes
offsets:  per-row start offset into elements
sizes:    per-row byte length
```

Rows are not self-delimiting without `sizes`. The encoder computes the sizes first (a fixed-width
column contributes a constant, a variable-width one what its values need), then reuses the `sizes`
array as the per-row write cursor. That makes one sizing pass and one write pass, into three pooled
buffers.

## 6. Unsupported types

Variable-size `List`, `Variant`, `Union` and `Decimal256` are not supported. The absence is
deliberate upstream, since adding one means defining both its order and its bytes. This encoder
refuses them as the reference does, and never invents an ordering.

Upstream does not support `Extension` either. Here, an extension column at the top level of the key
(a date, a time, a timestamp, a uuid) is encoded as its storage column, which gives exactly the bytes
Rust produces when a caller encodes that storage column itself. An extension nested inside a struct
or a fixed-size list is refused with `VortexUnsupportedException`, as the reference refuses it,
because the encoder would otherwise be silently choosing an ordering the format has not defined.

## 7. Implementation

- The encoder works a column at a time. It writes one field for every row, then the next field,
  which suits branch prediction and vector width better than a row at a time, and is what the
  two-pass sizing assumes. Big-endian conversion is a byte swap, and descending is a bitwise NOT over
  a span.
- There is no allocation per row, since the output size is known before a byte is written. The
  kernels call `TryWriteBigEndian`, never `WriteBigEndian`: the latter is a default interface method
  the primitive types do not override, so a generic call to it boxes its receiver on every row.
- It is synchronous. It is CPU work over arrays already in memory, and the async-only rule is for
  I/O.

The public API is `RowEncoder` (a borrowed batch in, a pooled `RowKeys` out, and one key's bytes
with `EncodeKey`), `RowSortField`, and `RowKeyEncoder`, which a writer uses to key a composite index
(see [composite keys](10-indexes.md#54-composite-keys-through-the-row-encoding)). The code is in
`src/Vorticity.RowEncoding`, and [row-keys.md](../guide/row-keys.md) is the guide's page.
