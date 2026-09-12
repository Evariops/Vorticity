# Sidecar format — `vortex-conformance-sidecar/2`

One `.jsonl` per `.vortex`, paired by filename (`x.vortex` <-> `x.jsonl`) **and** by the
`entry_id` / `path` / `sha256` fields of the header line. A loader should hash the `.vortex` and
compare it against the header before trusting anything else in the file: a sidecar regenerated
against a different file is otherwise undetectable from inside.

Every line is one JSON object. **Dispatch on the top-level `kind`.** `kind` is also the
discriminator inside dtype trees, so scanning a line's text for `"kind":"..."` instead of parsing
it will mis-dispatch. With a real JSON parser this is a non-issue.

## Line order

| # | `kind` | count | what it carries |
|---|---|---|---|
| 1 | `header` | 1 | format, entry id, path, sha256 of the `.vortex`, dtype Display, row count, legend |
| 2 | `dtype` | 1 | the file dtype as a parsed tree |
| 3 | `layout` | 1 | the layout tree; each `vortex.flat` node carries its `array_tree` |
| 4 | `metadata` | 1 | user metadata segments in stored order, payloads included |
| 5 | `file_stats` | 1 | `{present:false}`, or per-field statistics with precision |
| 6 | `zone_map` | 0..n | one per `vortex.zoned` layout, in depth-first order |
| 7 | `rows` | 0..n | up to 128 values each; row index = `from` + position in `v` |
| 8 | `null_counts` | 1 | per field path, rows null there or under a null ancestor |

A zero-row file has no `rows` lines and an empty `null_counts.by_path`.

## Value encodings

Read the header's `legend`; it is normative and travels with each file. The rules that catch the
most readers:

* `null` is JSON `null` and **nothing else ever is**. An empty string, an empty list and an empty
  map are all distinct from null and all encode as themselves.
* Integers are decimal **strings**, because JSON numbers are `f64` in most parsers and `u64::MAX`
  does not survive one.
* Floats are `{bits, dec}`, plus `special` when the value is not finite. **`bits` is normative**:
  it is the hex of the raw IEEE bytes, big-endian, so a wrong NaN payload or a `-0.0` read as
  `+0.0` fails the comparison. `dec` spells the infinities `Infinity` / `-Infinity` / `NaN`, which
  both Rust and .NET parse.
* Utf8 is `{b64, len, char_count}` — base64 of the UTF-8 **bytes**, the **byte** length, and the
  Unicode scalar count. They differ on every non-ASCII value, which is the point.
* Binary is `{b64, len}`. Base64 rather than a JSON string because a JSON string cannot carry
  invalid UTF-8, and silently repairing it would hide the bug the corpus exists to find.
* Decimals are `{unscaled, storage}` with `storage` in `i8|i16|i32|i64|i128|i256`.

## `null_counts` paths

`.`-joined field names from the root; the root itself is the empty string `""`. A row counts as
null at a path when the value there is null **or** when an ancestor struct is null — the number a
reader gets by materializing that leaf column. Note that a struct field may itself be named with a
`.` or be empty (`types/struct_field_names` does exactly that), so these paths are ambiguous for
that file by construction; use the `dtype` tree and index-based access there.

## `layout` -> `array_tree`

A layout node with `encoding_id == "vortex.flat"` carries `array_tree`: the **array** encoding tree
serialized inside that leaf, with child order preserved. Each node is
`{id, nchildren, nbuffers, metadata_len, metadata_b64, children}`.

`nchildren` is load-bearing on its own. For `fastlanes.bitpacked` and `vortex.alp` the patch shape
*is* the child count — no patches, patches without chunk offsets, patches with them — and all three
spell the same encoding id. `metadata_b64` is the raw encoding metadata protobuf; `vortex.bool`'s
bit offset lives in there and is the only thing that distinguishes one bool array from another.

## `zone_map` -> `aggregate_details`

Zone maps are the pruning input, and a bound is not a value. Each aggregate carries a `precision`:

* `exact` — `vortex.min()`, `vortex.max()`, `vortex.null_count()`, `vortex.nan_count()`.
* `bound` — `vortex.bounded_min(n)`, `vortex.bounded_max(n)`. The stored value is a truncated
  bound, not an extreme. A bounded partial is a struct `{bound, unknown}`; when `unknown` is
  `true` there is **no** bound at all, and treating the null `bound` as "no data" prunes wrongly.

## Caveats

`manifest.json`'s `caveats` array lists reference-implementation behaviours that look like corpus
bugs and are not — the float `sum` on a column containing an infinity, above all. A reader that
"corrects" one of them will disagree with every real Vortex file. Read it before filing one.
