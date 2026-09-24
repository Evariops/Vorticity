# Byte-sortable row encoding

A separate specification from the file format, implemented upstream by the `vortex-row` crate. It
converts one or more columnar arrays into a `ListView<u8>`: one byte string per row, such that
**lexicographic byte comparison of the encoded rows equals logical tuple comparison of the input
values**, under per-column sort direction and null placement.

Use cases: sort keys, row keys, range partitioning, merge joins, index entries — anywhere a
comparator must become a `memcmp`.

> **Stability.** Upstream marks this format *experimental*: the byte layout, the supported types and
> the edge cases may change between Vortex releases, and its spec says not to persist these bytes as
> an interchange format. So it ships in its own package, `Vorticity.RowEncoding`, marked
> `[Experimental("VX0002")]`, and the encoder names the release its bytes follow (`vortex-row
> 0.86.1`). Encoded bytes are safe to compare within one process, or one cluster running one
> version; a durable index that stores them records that name
> ([10-indexes.md](10-indexes.md) §5.4).

## 1. Contract

For columns `c0…cn` with per-column `RowSortField { descending: bool, nulls_first: bool }`:

```
encode(row_a) < encode(row_b)  ⟺  (a.c0, …, a.cn) < (b.c0, …, b.cn)
```

The bytes carry **no type tags, no field names, no sort options**. Two encoded rows are comparable
only if produced from the same schema with the same field settings. `descending` and `nulls_first`
are independent: nulls can sort first or last in either direction.

Notation: `||` concatenation, `BE(x)` fixed-width big-endian bytes (never length-prefixed, never
trimmed), `!b` = `b XOR 0xFF`, `zero(n)` / `ff(n)` = n bytes of `0x00` / `0xFF`.

## 2. Sentinels

Every value starts with a sentinel byte that classifies nullness before any value byte is
compared.

| Family | Case | Asc, nulls first | Desc, nulls first | Asc, nulls last | Desc, nulls last |
|---|---|---|---|---|---|
| Fixed-width | Null | `0x00` | `0x00` | `0x02` | `0x02` |
| Fixed-width | Non-null | `0x01` | `0x01` | `0x01` | `0x01` |
| Variable-width | Null | `0x00` | `0x00` | `0xFF` | `0xFF` |
| Variable-width | Empty | `0x01` | `0xFE` | `0x01` | `0xFE` |
| Variable-width | Non-empty | `0x02` | `0xFD` | `0x02` | `0xFD` |

Fixed-width sentinels serve Null, Bool, Primitive, Decimal, Struct and FixedSizeList.
Variable-width sentinels serve Utf8 and Binary.

**The null sentinel is never inverted by `descending`** — only non-null value bytes (and, for
variable-width, the empty/non-empty sentinels) are. This is what keeps null placement independent
of sort direction, and it is the single easiest thing to get wrong.

A fixed-width null is followed by zero-filled value bytes, so fixed types keep a constant encoded
width on every row.

## 3. Per-type encodings

| Type | Ascending | Descending |
|---|---|---|
| `Null` | `sentinel` (no body) | same |
| `Bool` | `0x01 \|\| 0x01` (false) / `0x01 \|\| 0x02` (true) | `0x01 \|\| 0xFE` (false) / `0x01 \|\| 0xFD` (true) |
| `u8/u16/u32/u64` | `0x01 \|\| BE(v)` | `0x01 \|\| !BE(v)` |
| `i8/i16/i32/i64` (and `i128`) | `0x01 \|\| ordered`, where `ordered = BE(v)` with `ordered[0] ^= 0x80` | `0x01 \|\| !ordered` |
| `f16/f32/f64` | `0x01 \|\| BE(ordered)` | `0x01 \|\| !BE(ordered)` |
| `Decimal` | signed-integer encoding of the scaled storage value | idem |
| `Utf8` / `Binary` | variable-width sentinel + block body (§4) | inverted (§4) |
| `Struct` | `sentinel \|\| field_0 \|\| … \|\| field_n`, fields recursive in schema order | idem |
| `FixedSizeList<T, N>` | `sentinel \|\| elem_0 \|\| … \|\| elem_{N-1}` | idem |

Null bodies for fixed-width types: `null_sentinel || zero(width(T))`.

**Float transform**, from raw IEEE bits:

```
ordered = sign_bit(bits) == 0 ? bits XOR sign_bit_mask
                              : bits XOR all_ones
```

This yields a total order: negatives before positives, `-0.0` before `+0.0`. NaNs order by raw bit
pattern and are **not** canonicalized: two NaNs with different payloads are two keys.

**Decimal storage width**, selected from precision:

| Precision | Storage |
|---|---|
| 1–2 | `i8` |
| 3–4 | `i16` |
| 5–9 | `i32` |
| 10–18 | `i64` |
| 19–38 | `i128` |

`Decimal256` is not supported.

**Option inheritance is identity, and inversion happens once.** A nested field uses its parent's
`RowSortField` unchanged — verified in `vortex-row/src/codec.rs`, where `encode_struct` passes the
same `field` to `field_encode` for every child. Inversion for `descending` is applied by the leaf
encoder to its own value bytes only; the parent never re-inverts the concatenated body. The classic
double-inversion trap is therefore avoided by *not* doing anything at the parent level.

**The child null sentinel is chosen by the child's dtype, using the inherited field.**
`child_canonical_null_byte(child_dtype, field)` returns the variable-width null sentinel for
`Utf8`/`Binary` children and the fixed-width null sentinel for everything else (including a nested
struct or FSL acting as a variable-width child). The `nulls_first` that selects between `0x00` and
`0x02`/`0xFF` is the inherited one, i.e. the root column's. For a fixed-width child under a null
parent, the body is that sentinel byte followed by **zero** fill to the child's fixed width — zero
even under `descending`.

**Nested nulls are canonicalized.** A null struct or null fixed-size list must still emit a body,
so that two null parents compare byte-equal regardless of what their physical child arrays
contain: fixed-width children contribute their fixed-width null encoding; variable-width children
contribute exactly one child null sentinel byte. Skipping this is a correctness bug that only
shows up on arrays with garbage under a null — exactly what a differential test against Rust will
catch and a naive round-trip test will not.

## 4. Variable-length body

Non-empty Utf8/Binary bodies are written in blocks of **32 data bytes + 1 marker byte**:

* Ascending: every non-final full block uses marker `0xFF`; the final block is zero-padded to 32
  data bytes and its marker is the real data length in `1..=32`.
* Descending: every data byte inverted, continuation marker `0x00`, final block padded with
  `0xFF`, final marker `final_len XOR 0xFF`.
* If the length is an exact multiple of 32, the final block carries marker `32` and earlier blocks
  use the continuation marker.

This is what preserves prefix ordering: a shorter value reaches its final marker (`≤ 32`) while a
longer one is still emitting continuation markers (`0xFF`), so the prefix sorts first.

## 5. Output layout

```
elements: contiguous u8 buffer holding all row bytes
offsets:  per-row start offset into elements
sizes:    per-row byte length
```

Rows are not self-delimiting without `sizes`. The encoder computes the sizes first (a fixed-width
column contributes a constant, a variable-width one what its values need), then reuses the `sizes`
array as the per-row write cursor: **one sizing pass, one write pass**, into three pooled buffers.

## 6. Unsupported types

Variable-size `List`, `Variant`, `Union`, `Extension` and `Decimal256`. The absence is deliberate
upstream: adding one means defining both its order and its bytes. This encoder refuses them as the
reference does, and never invents an ordering.

`Extension` being unsupported means a timestamp or a date is not row-encoded directly: a caller
encodes its storage column. Unwrapping extensions silently would make the bytes stop matching
Rust's.

## 7. Implementation

- **Column at a time.** The encoder writes one field for every row, then the next field: better
  for branch prediction and vector width than a row at a time, and what the two-pass sizing assumes.
  Big-endian conversion is a byte swap, descending a bitwise NOT over a span.
- **No allocation per row.** The output size is known before a byte is written. The kernels call
  `TryWriteBigEndian`, never `WriteBigEndian`: the latter is a default interface method the
  primitive types do not override, so a generic call to it boxes its receiver on every row.
- **Synchronous.** It is CPU work over arrays already in memory; the async-only rule is for I/O.

The surface is `RowEncoder` (a borrowed batch in, a pooled `RowKeys` out; one key's bytes with
`EncodeKey`), `RowSortField`, and `RowKeyEncoder`, which a writer uses to key a composite index
([10-indexes.md](10-indexes.md) §5.4). The code is `src/Vorticity.RowEncoding`, and
[row-keys.md](../guide/row-keys.md) is the guide's page.
