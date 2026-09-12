# Query semantics: pruning, predicates, unknown components

The rules that decide whether a scan returns *correct* rows. None of these are visible in the byte
format; all of them produce silently wrong results if guessed. Where a rule was resolved by
reading the reference implementation rather than the prose spec, the source is cited inline.

## 1. Statistic precision: what `Inexact` licenses

`ArrayStats` carries `min_precision` / `max_precision` with values `Exact` / `Inexact`
([02-format.md](02-format.md) §5.2). The reference defines the semantics unambiguously
(`vortex-array/src/expr/stats/precision.rs`):

> Inexact statistics form a range of possible values that the statistic could be. This is statistic
> specific, for max this will be an upper bound. Meaning that the actual max in an array is
> guaranteed to be less than or equal to the inexact value, but equal to the exact value.

So **`Inexact` is a conservative bound, not an unqualified approximation**: `min` is a lower bound
on the true minimum, `max` an upper bound on the true maximum. The consequences are exact:

| Operation | `Exact` | `Inexact` |
|---|---|---|
| Range pruning (`x > k` vs zone `max`) | legal | **legal** — the bound points the safe way |
| Equality shortcut (`min == max` ⇒ constant zone) | legal | forbidden |
| Reporting the value to the caller as a true min/max | legal | forbidden |
| Skipping decode because `min == max` | legal | forbidden |

A third state, `Absent`, exists in the reference and maps to `Inexact` with no value on the wire.
A statistic with no value licenses nothing.

**The invariant that governs every pruning decision**, and the one the property test asserts:

> Pruning may never eliminate a row that full materialization would have returned.

The converse — pruning too little — is only a performance loss. This asymmetry is what makes the
test writable: generate a file, run the filter with pruning enabled and with pruning forced off,
and assert the pruned result is a *subset-free* match of the unpruned one. Feed it deliberately
sloppy (but legal) `Inexact` statistics to prove the reader does not over-trust them.

## 2. NaN

The reference computes `min`/`max` with `NumericalAggregateOpts::skip_nans()`
(`vortex-array/src/stats/expr.rs`), and tracks NaN separately in the `nan_count` statistic. So:

* **Zone `min`/`max` exclude NaN.** A zone containing NaN still reports the min/max of its
  non-NaN values.
* Filter expressions follow **IEEE 754**, which is more precise than "NaN never matches". The five
  ordering-and-equality predicates — `==`, `<`, `<=`, `>`, `>=` — are all **false** when either
  operand is NaN, `NaN == NaN` included. **`!=` is the exception**: it is defined as the negation of
  `==`, so `NaN != x` is **true**, for every `x`, NaN included. C, C#, Rust and SQL all agree on
  this; a reader that makes `!=` false for NaN "for consistency" drops rows every other
  implementation returns. That is not a hypothetical — the first draft of this paragraph said every
  comparison involving NaN is false, and the test written from it failed against a correct
  implementation.
* This is all distinct from the row encoding, which deliberately defines a *total* order over floats
  where NaN is ordered by raw bit pattern ([06-row-encoding.md](06-row-encoding.md) §3). The two
  orderings serve different purposes and must not be unified — an implementer who reuses the
  row-encoding comparator for filter evaluation introduces a correctness bug.
* Therefore a zone with `max <= 10` may be pruned for `x > 10` **even when `nan_count > 0`**,
  because the NaN rows would not have matched anyway. This is only sound given the two facts
  above; it is written down here so it is not rediscovered by guesswork.

## 3. Three-valued logic

Predicates evaluate over `{true, false, unknown}`, SQL-style:

* A comparison with a null operand yields `unknown`.
* `unknown AND false = false`; `unknown OR true = true`; `NOT unknown = unknown`.
* A row is returned only when the filter evaluates to `true` — `unknown` does not match.
* `IS NULL` / `IS NOT NULL` never yield `unknown`.

Pruning consequence: a zone where `null_count == row_count` can be skipped for any predicate that
is not satisfiable by nulls — that is, anything except `IS NULL` and expressions reducible to it.
This is one of the highest-value pruning rules in practice and it depends entirely on the 3VL
choice above being fixed.

## 4. Unknown components: resolve lazily, fail on use

[02-format.md](02-format.md) §5.3 covers the *contract* rule (a frozen ID means a frozen payload
shape). This section covers the *resolution* rule, which decides how long a reader survives format
evolution.

**Unknown component IDs are resolved at open time into a known/unknown-inert classification, but
only fail when the component is actually required.**

| Situation | Behavior |
|---|---|
| Unknown array encoding in a column that is not projected | scan succeeds |
| Unknown array encoding in a projected column | `VortexUnsupportedException(id, "array")` |
| Unknown layout encoding on a subtree never visited | scan succeeds |
| Unknown layout encoding on the path to projected data | throws |
| Unknown extension dtype in the schema | schema exposes it as opaque; throws only if that field is read |
| Unknown zone-map aggregate | pruning for that aggregate is disabled; scan succeeds |

The aggregate row is already mandated by the format spec. Extending the same principle to arrays,
layouts and dtypes is what lets a reader keep working when upstream freezes a new core edition and
the default writer starts emitting a new ID in *one* column of a fifty-column file. An
open-time hard failure would make that file entirely unreadable for no reason.

`AllowUnknownComponents` therefore describes an *inspection* mode (unknown nodes are preserved as
inert and can be listed by `vxdump`), not the switch that makes lazy resolution happen. Lazy
resolution is the default and unconditional behavior.

Every such exception must name the component ID and kind, because that is exactly the input the
upstream troubleshooting procedure requires ("which edition, which minimum library version").

### Binary preservation on rewrite

Preserving unknown nodes byte-for-byte through a read-modify-write cycle is a *separate feature*
with its own invariants (which segments are copied verbatim, what happens when the surrounding
layout changes, how edition enforcement applies to a component we cannot name). It is **out of
scope for 1.0**. Inspection-only preservation is in scope.

## 5. Untrusted hints: a three-class policy

Offsets and lengths are validated ([03-architecture.md](03-architecture.md) §6). The subtler class
is *semantic hints* the file asserts and a decoder might believe. Every field falls into exactly
one class:

**Class I — memory safety depends on it. Validated unconditionally, whatever the cost.**
`DictMetadata.values_len` vs the actual values length and every code in `[0, values_len)`;
`BitPackedMetadata.bit_width` ≤ the ptype width; `BitPackedMetadata.offset` < 1024;
`RLEMetadata.offset` < 1024; `PatchesMetadata` indices in range; ALP `exp_e`/`exp_f` within the
target float's domain; `BoolMetadata.offset` < 8; a `vortex.listview` row's `offset + size` ≤
`elements_len`, added **unsigned** so two legal i64 operands cannot wrap past the bound; a
`vortex.varbinview` reference view's 4-byte prefix equal to the value it points at; every buffer
index in `ArrayNode.buffers`; every segment index in `Layout.segments`; `alignment_exponent` ≤ 6
(see §6).

**Class II — only result correctness depends on it.** Monotonicity of `RunEndMetadata` ends,
`is_sorted`, `is_strict_sorted`, zone-map min/max. Default: **not** validated, and the threat model
says so explicitly — a well-formed file that lies produces wrong results, exactly as in every
format with embedded statistics, Parquet included. An opt-in `VerifyStatistics` mode validates
them in O(n) at first decode, for callers reading files from untrusted producers.

The distinction matters because a lying `is_sorted` cannot corrupt memory but a lying
`values_len` can. Conflating the two either costs throughput everywhere or leaves a hole.

**Class III — pure performance hints.** `is_constant`, `uncompressed_size_in_bytes`,
`null_count` used only for fast paths. Ignored unless free to verify; never a correctness input.

A dedicated fuzzing mutation targets Class I fields specifically — a generic bit-flip fuzzer
almost never produces a *structurally valid* file with an out-of-range `values_len`, which is
precisely the input that finds these bugs.

## 6. Resource caps

`VortexFormatException` on violation. Every cap is a constant, documented here so it is not
invented per call site:

| Cap | Value | Why |
|---|---|---|
| `alignment_exponent` | ≤ 6 (64 bytes) | the field is a `u8`; `2^255` is expressible. 64 covers every legitimate alignment including 16-byte views |
| DType nesting depth | 64 | a 10 000-deep nested struct blows the stack during schema parsing, before any data is touched |
| Layout tree depth | 64 | same |
| Array tree depth | 64 | same |
| Postscript length | 65527 (format-mandated) | enforced on write too, with an early, clear error |
| Decompressed segment size | configurable, default 256 MiB | a 1 KiB Zstd segment can claim to expand to 100 GiB |
| Metadata segment count | 16 (format-mandated) | — |
| FlatBuffers tables per traversal | 1 000 000 | depth cannot bound work: forward-only uoffsets exclude cycles but not *sharing*, so a few hundred bytes of shared children describe a DAG with 2^depth paths. Matches the reference verifiers' `max_tables` |

The decompression cap shapes the *design*, not just a constant: `ZstandardDecoder.TryDecompress`
is one-shot and needs a pre-sized destination, so the frame's declared content size is validated
against the cap **before** allocating. When a frame omits its content size, decompression falls
back to a streaming loop with a running budget.

## 7. Buffer-level LZ4

`Buffer.compression` admits `LZ4` ([02-format.md](02-format.md) §5.2). The decision here used to be
"1.0 reads it", on the grounds that an LZ4 block decoder is ~200 lines with no dependency and that a
conformance hole is better closed by us than found by a user. **That decision is reversed, and the
reason is not effort.**

Grepping the whole of Vortex 0.86.1 for `lz4` returns four files: the two `.fbs` schemas that
declare the enum, and the two generated Rust files that mirror it. Nothing else. `vortex-array`'s
`serde.rs` *writes* `Compression::None` and **never reads `Buffer.compression` at all** — not even
to reject it — and no vortex crate depends on an lz4 implementation. The enum value is a
placeholder, exactly like `SegmentSpec._compression`, which `footer.fbs` says outright is "reserved
for future use ... not used in the current version of the file format".

So there is nothing to be conformant *with*. The schema names an algorithm and stops: it does not
say whether the bytes are a raw LZ4 block or an LZ4 frame, and — decisively — it provides no
decompressed length anywhere. `Buffer.length` is documented as "the length of the buffer in bytes"
and is used by every consumer as the on-disk extent. A raw LZ4 block carries no size of its own, so
a decoder could not even size its output without inventing a rule. Writing one would mean choosing
a framing and a length convention and calling the result the format, which is the same mistake as
inventing an ordering for `List` in the row encoder ([06-row-encoding.md](06-row-encoding.md) §6).

**Decision: 1.0 refuses a compressed buffer**, with `VortexUnsupportedException` naming the id
`lz4` and the kind `compression`. Note what that buys: the reference, which never inspects the
field, would read the compressed bytes AS DATA and return silently wrong values. Refusing is not
merely defensible here, it is the safer of the two behaviours. We do not write LZ4 either; the
default writer emits `None` and so do we.

This reverses when — and only when — upstream implements it, at which point the framing becomes
observable in a real file and the ~200 lines can be written against something.
