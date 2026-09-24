# Query semantics: pruning, predicates, unknown components

The rules that decide whether a scan returns *correct* rows. None of them is visible in the byte
format, and each produces silently wrong results if guessed. Where a rule was settled by reading the
reference implementation rather than its prose, the source is cited.

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

The converse, pruning too little, only costs time. That asymmetry is what makes the invariant
testable: every filter test runs with pruning and indexes on and off, and the row sets must be
equal, including over deliberately loose but legal `Inexact` statistics.

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
  implementation returns.
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
* `ListContains(list, v)` is `unknown` on a null list and under a null `v`; a null element matches
  nothing and leaves the row `false` ([12-index-reads.md](12-index-reads.md) §6).

A literal of a type the column cannot compare to is **not a silent non-match**: it is refused
before a row is read. On the typed path it does not compile, because `Sym<T>` compares only with a
`T` or with another column of the same `T`. On the tool path `Where` throws
`VortexSchemaException` naming the column, its type and the literal, and a hole of an interpolated
filter throws as it is appended; two columns compared with each other must be of the same type, or
`Where` throws the same exception. What looks like a mismatch and is a conversion: text compared
with a date, time, timestamp, uuid or decimal column is parsed in the column's type, and refused
when it does not parse; a number compared with a decimal column is scaled exactly from its digits,
and a value the column's scale cannot hold makes an equality false rather than rounding into a
match. On the typed path a comparison with `null` is nullity, never `unknown`: `== null` is
`IS NULL`, `!= null` is `IS NOT NULL`, an ordering against `null` matches nothing, and a `null` among
the values of `In` is ignored.

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
| Unknown array encoding in a projected column | `VortexUnsupportedException(id, ComponentKind.Array)` |
| Unknown layout encoding on a subtree never visited | scan succeeds |
| Unknown layout encoding on the path to projected data | throws |
| Unknown extension dtype in the schema | schema exposes it as opaque; throws only if that field is read |
| Unknown zone-map aggregate | pruning for that aggregate is disabled; scan succeeds |

The aggregate row is already mandated by the format spec. Extending the same principle to arrays,
layouts and dtypes is what lets a reader keep working when upstream freezes a new core edition and
the default writer starts emitting a new ID in *one* column of a fifty-column file. An
open-time hard failure would make that file entirely unreadable for no reason.

Lazy resolution is unconditional, and there is no option to fail at open instead: the inspection
such an option would give is always there. `VortexFile.ArrayEncodings` and `LayoutEncodings` list
what the footer declares, each with whether this build supports it, and `vxdump` prints them.

Every such exception must name the component ID and kind, because that is exactly the input the
upstream troubleshooting procedure requires ("which edition, which minimum library version").

### Binary preservation on rewrite

Preserving unknown nodes byte for byte through a read-modify-write cycle is a separate feature with
invariants of its own: which segments are copied verbatim, what happens when the surrounding layout
changes, how edition enforcement applies to a component nobody can name. It is not offered; a
rewrite fails on the component it cannot decode.

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
`is_sorted`, `is_strict_sorted`, zone-map min/max. By default they are **not** validated, and the
threat model says so ([09-contracts.md](09-contracts.md) §4): a well-formed file that lies produces
wrong results, as in every format with embedded statistics, Parquet included.
`VortexOpenOptions.VerifyStatistics` validates them in O(n) at first decode, for files from
producers a caller does not trust.

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

`Buffer.compression` admits `LZ4` ([02-format.md](02-format.md) §5.2), and the segment table
reserves a compression byte too. **A compressed buffer is refused**, with
`VortexUnsupportedException` naming the id `lz4` and the kind `compression`, and nothing is written
compressed.

There is nothing to be conformant with. In the whole of Vortex 0.86.1, `lz4` appears in the two
schemas that declare the enum and the generated code that mirrors them, and nowhere else: the
reference writes `Compression::None`, never reads the field, and depends on no LZ4 implementation.
The schema names an algorithm and stops: it does not say whether the bytes would be a raw LZ4 block
or a frame, and it records no decompressed length anywhere, so a decoder could not even size its
output without inventing a rule. Writing one would mean choosing a framing and calling it the
format, the same mistake as inventing an order for `List` in the row encoder
([06-row-encoding.md](06-row-encoding.md) §6).

Refusing is also the safer behaviour: the reference, which never inspects the field, would read
compressed bytes as data and return wrong values without a word. The decision changes the day
upstream implements it and a real file shows the framing.
