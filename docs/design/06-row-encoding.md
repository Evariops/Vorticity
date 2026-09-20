# Byte-sortable row encoding

A separate specification from the file format, implemented upstream by the `vortex-row` crate. It
converts one or more columnar arrays into a `ListView<u8>`: one byte string per row, such that
**lexicographic byte comparison of the encoded rows equals logical tuple comparison of the input
values**, under per-column sort direction and null placement.

Use cases: sort keys, row keys, range partitioning, merge joins, index entries — anywhere a
comparator must become a `memcmp`.

> **Stability caveat.** Upstream marks this format *experimental*: byte layout, supported type set
> and edge-case semantics may change between Vortex releases, and the spec explicitly says not to
> persist these bytes as a stable interchange format. We implement it, but the same warning must
> appear in our public API docs, and the encoder must be versioned against the Vortex release it
> was transcribed from. Encoded bytes are safe to compare within one process or one cluster
> running one version; they are not safe in a durable index.

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
pattern and are **not** canonicalized — an important behavioral note for our API docs.

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

Rows are not self-delimiting without `sizes`. The encoder computes sizes first (fixed-width
columns contribute a constant, variable-width columns a data-dependent width), then reuses the
`sizes` array as the per-row write cursor. We do the same: **one sizing pass, one write pass**, and
three pooled buffers — elements, offsets and sizes. (Calling it "one allocation" would be wrong by
two, and in a library whose acceptance criteria count allocations, that kind of imprecision is
expensive in review.)

## 6. Unsupported types

Variable-size `List`, `Variant`, `Union`, `Extension`, `Decimal256`. The absence is deliberate
upstream — adding one requires defining both the logical order and the byte representation. Our
encoder rejects them with the same error, and must not invent an ordering.

Note that `Extension` being unsupported means timestamps and dates cannot be row-encoded directly
today; upstream flags normalizing them to their storage arrays as a possible future addition. If
we need temporal sort keys before that lands, the caller normalizes to storage type explicitly —
we do not silently unwrap extensions, or our bytes would stop matching Rust's.

## 7. Implementation notes

This is an unusually good fit for the project's constraints:

* **Vectorizable.** Big-endian conversion is `BinaryPrimitives`/`BSWAP`; descending is a bitwise
  NOT over a span; the 32-byte block layout maps onto `Vector256`. A column-at-a-time encoder
  (encode field *f* for all rows, then field *f+1*) beats a row-at-a-time one on both branch
  prediction and vector width — and the two-pass sizing model already assumes column-major work.
* **Zero-allocation by construction.** Output size is known before writing.
* **`BE(i128)` needs no helper on `net11.0`.** This note used to say `BinaryPrimitives` had no
  `Int128` big-endian path and that we would hand-write one. It has had `WriteInt128BigEndian` and
  `WriteUInt128BigEndian` since .NET 8, so the encoder calls those directly.
* **Use `TryWriteBigEndian`, never `WriteBigEndian`.** `IBinaryInteger<T>.WriteBigEndian` is a
  *default interface method* that the primitive types do not override, so a constrained call to it
  from a generic kernel has to **box the receiver** to reach the interface's implementation — 24
  bytes on every row of every fixed-width column, which is exactly the allocation the design
  promises not to make. `TryWriteBigEndian` is abstract, implemented by each type, devirtualizes,
  and spells the same bytes. Found by the allocation test, not by review.
* **Independently testable.** It touches neither the file format nor I/O, so it can proceed in
  parallel with Phase 1 and its correctness is fully characterized by one property: byte order
  equals tuple order.

Public API, as built:

```csharp
ReadOnlySpan<RowSortField> fields = [new RowSortField(descending: false, nullsFirst: true), /* … */];

// Pooled: three buffers in, one disposable out.
using RowKeys keys = RowEncoder.Encode(arena, columns, fields);
keys.SortIndices(indices);                                          // memcmp is the comparator

// Or caller-owned, for a caller that pools its own memory:
int total = RowEncoder.ComputeSizes(arena, columns, fields, sizes);        // pass 1
RowEncoder.ComputeOffsets(sizes, offsets);
RowEncoder.Encode(arena, columns, fields, offsets, cursors, destination);  // pass 2
```

`columns` is a `ReadOnlySpan<int>` of canonical node indices into a `CanonicalArena`, because
`CanonicalNode` is a `ref struct` and cannot be put in an array; a `RecordBatch` overload encodes
the root struct's fields. `cursors` goes in zeroed and comes back holding each row's size — the
reference reuses one array for both, and so do we.

Synchronous by design: this is pure CPU work over in-memory arrays, with no I/O to await. The
async-only rule applies to the I/O and scan surface, not to a comparator.
