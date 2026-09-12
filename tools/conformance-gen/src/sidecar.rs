//! The expected-value sidecar: what the **Rust reader** says the file contains.
//!
//! docs/04-conformance.md §3.2 makes this the contract, so that day-to-day .NET test runs need no
//! Rust toolchain. It has to be lossless enough to catch a wrong decode, which rules out every
//! convenient shortcut:
//!
//! | trap | what we do instead |
//! |---|---|
//! | `1.0` for a float | raw IEEE bits in hex **and** a round-trippable decimal |
//! | a JSON number for `u64::MAX` | a decimal **string** (JSON numbers are f64 in most parsers) |
//! | a JSON string for text/binary | base64 — a JSON string cannot carry invalid UTF-8 |
//! | "empty means null" | `null` is JSON null and nothing else ever is |
//!
//! Format: JSON Lines. The first line is a header describing the encoding, then one line each for
//! the dtype tree, the layout tree, the file statistics and each zone map, then the values in
//! `rows` batches of [`ROWS_PER_LINE`], then a `null_counts` line. Values are batched rather than
//! one per line purely for size: a per-row envelope costs more than the values it wraps once the
//! corpus reaches a million rows, and the batch carries its own start index so a consumer can
//! still address any single row.

use std::collections::BTreeMap;
use std::io::BufWriter;
use std::io::Write;
use std::path::Path;

use serde_json::Value;
use serde_json::json;
use vortex::array::ArrayRef;
use vortex::array::VortexSessionExecute;
use vortex::array::serde::SerializedArray;
use vortex::array::stream::ArrayStreamExt;
use vortex::dtype::DType;
use vortex::dtype::Nullability;
use vortex::error::VortexResult;
use vortex::error::vortex_err;
use vortex::expr::stats::Precision;
use vortex::file::VortexFile;
use vortex::layout::Layout;
use vortex::layout::LayoutRef;
use vortex::layout::layouts::flat::Flat;
use vortex::layout::layouts::zoned::Zoned;
use vortex::scalar::DecimalValue;
use vortex::scalar::PValue;
use vortex::scalar::Scalar;
use vortex::scalar::ScalarValue;
use vortex::session::VortexSession;

use crate::util::base64;
use crate::util::hex;

/// Bumped whenever the sidecar encoding changes in a way a consumer must notice.
pub const SIDECAR_FORMAT: &str = "vortex-conformance-sidecar/2";

/// Values per `rows` line. Bounded so a consumer never has to buffer a whole column, large enough
/// that the JSON envelope is noise.
pub const ROWS_PER_LINE: usize = 128;

/// What the sidecar observed, folded back into the manifest.
pub struct SidecarInfo {
    pub null_counts: BTreeMap<String, u64>,
    pub has_file_statistics: bool,
    pub zone_maps: usize,
    pub total_zones: usize,
    /// Zone maps whose per-zone rows are all distinct. A zone map whose zones carry identical
    /// bounds cannot prune anything, so it proves a zone map can be parsed and nothing else.
    pub zone_maps_with_distinct_zones: usize,
    /// Any Utf8 value in the file with a byte >= 0x80 — the UTF-8 -> UTF-16 conversion paths.
    pub utf8_non_ascii: bool,
    /// Any Utf8 value with an embedded U+0000, which a C-string reader truncates at.
    pub utf8_embedded_nul: bool,
    /// One entry per `vortex.flat` leaf, keyed by that leaf's segment id: the *serialized array
    /// encoding tree* inside it, parent/child order preserved. The manifest folds a flattened set
    /// out of this; the sidecar carries the shape.
    pub array_trees: Vec<(u32, ArrayNode)>,
}

/// One node of a serialized array encoding tree, read back out of the written bytes.
///
/// docs/04-conformance.md's whole point is that a value-only comparison cannot see a structurally
/// different decode: `map(listview(struct(...)))` and `listview(map(...))` hold the same values.
/// So the tree is recorded as a tree, with each node's metadata bytes, buffer count and child
/// order — the three things a reader must reproduce exactly.
#[derive(Clone, Debug)]
pub struct ArrayNode {
    pub id: String,
    /// Raw encoding metadata. Small by construction (a protobuf message of a handful of scalar
    /// fields); recorded verbatim so a consumer can parse it rather than trust our summary.
    pub metadata: Vec<u8>,
    pub nbuffers: usize,
    pub children: Vec<ArrayNode>,
}

impl ArrayNode {
    /// Depth-first pre-order over this node and its descendants.
    pub fn walk(&self, f: &mut impl FnMut(&ArrayNode)) {
        f(self);
        for child in &self.children {
            child.walk(f);
        }
    }
}

/// What `produce` knows about the file that the reader does not: which corpus entry it is, where
/// it lives, and the hash of the bytes this sidecar describes.
pub struct SidecarIdentity<'a> {
    pub entry_id: &'a str,
    /// Corpus-relative path of the `.vortex` file.
    pub path: &'a str,
    /// SHA-256 of the `.vortex` file, so a sidecar cannot be silently paired with another file.
    pub sha256: &'a str,
    /// User metadata segments in the order the footer stores them, with their payload bytes.
    pub metadata_segments: &'a [(String, Vec<u8>)],
}

/// Read `file` back and write its expected values to `path`.
pub async fn write_sidecar(
    session: &VortexSession,
    file: &VortexFile,
    path: &Path,
    identity: &SidecarIdentity<'_>,
) -> VortexResult<SidecarInfo> {
    let out = std::fs::File::create(path)
        .map_err(|e| vortex_err!("creating sidecar {}: {e}", path.display()))?;
    let mut out = BufWriter::new(out);

    let dtype = file.dtype().clone();
    let row_count = file.row_count();

    writeln_json(
        &mut out,
        &json!({
            "kind": "header",
            "format": SIDECAR_FORMAT,
            // Which file this sidecar describes. Pairing was filename convention only through
            // format/1, so a partially regenerated corpus was undetectable from inside a sidecar.
            "entry_id": identity.entry_id,
            "path": identity.path,
            "sha256": identity.sha256,
            "dtype": dtype.to_string(),
            "row_count": row_count,
            "legend": {
                "lines": "one JSON object per line; dispatch on the TOP-LEVEL `kind`. `kind` is \
                          also the discriminator inside dtype trees, so a consumer that greps a \
                          line for \"kind\" instead of parsing it will mis-dispatch.",
                "line_kinds": "header, dtype, layout, metadata, file_stats, zone_map, rows, \
                               null_counts",
                "null": "JSON null, and nothing else ever is",
                "bool": "JSON true/false",
                "integer": "decimal string, so u64/i64 extremes survive JSON",
                "float": "{bits: hex of the raw IEEE bytes (NORMATIVE), dec: decimal, special: \
                          Infinity|-Infinity|NaN when the value is not finite}. `dec` spells the \
                          infinities `Infinity`/`-Infinity` so both Rust and .NET parse it; when \
                          the two ever disagree, `bits` wins.",
                "decimal": "{unscaled: decimal string, storage: i8|i16|i32|i64|i128|i256}",
                "utf8": "{b64: standard base64 of the UTF-8 BYTES, len: byte length, char_count: \
                         Unicode scalar count}. len != char_count wherever the value is not ASCII.",
                "binary": "{b64: standard base64, len: byte length}",
                "list/fixed_size_list": "JSON array of element values",
                "struct": "JSON object keyed by field name, in schema order",
                "map": "JSON array of {key, value} objects, in stored order",
                "extension": "the storage value; the extension id and metadata are in the dtype line",
                "variant": "{dtype: <dtype tree>, value: <value>}",
                "rows": "each `rows` line holds up to 128 values; row index = `from` + position in `v`",
                "null_counts": "per field path, rows that are null there OR under a null ancestor \
                                struct. The path is `.`-joined field names from the root; the root \
                                itself is the empty string.",
                "layout.array_tree": "on a vortex.flat node, the serialized ARRAY encoding tree \
                                      inside that leaf, child order preserved. Absent on non-flat \
                                      nodes.",
                "zone_map.aggregate_details": "per aggregate, `precision`: `exact` for \
                                               vortex.min/max/null_count/nan_count, `bound` for \
                                               vortex.bounded_min/bounded_max — a bound is NOT a \
                                               value, and a bounded entry with `unknown: true` \
                                               carries no bound at all.",
                "metadata": "user metadata segments in stored order; `b64` is \"\" for a \
                             present-but-empty segment and the key is simply absent when there is \
                             no such segment."
            }
        }),
    )?;

    writeln_json(
        &mut out,
        &json!({ "kind": "dtype", "tree": dtype_json(&dtype) }),
    )?;

    let trees = array_trees(file).await?;
    let tree_json: BTreeMap<u32, Value> = trees
        .iter()
        .map(|(segment, node)| (*segment, array_node_json(node)))
        .collect();
    writeln_json(
        &mut out,
        &json!({ "kind": "layout", "tree": layout_json(file.footer().layout(), "", &tree_json)? }),
    )?;

    write_metadata_segments(&mut out, identity.metadata_segments)?;

    let has_file_statistics = write_file_statistics(&mut out, file)?;
    let zone_maps = write_zone_maps(&mut out, session, file).await?;

    let mut utf8_scan = Utf8Scan::default();
    let null_counts =
        write_rows(&mut out, session, file, &dtype, row_count, &mut utf8_scan).await?;
    writeln_json(
        &mut out,
        &json!({ "kind": "null_counts", "by_path": null_counts }),
    )?;

    out.flush()
        .map_err(|e| vortex_err!("flushing sidecar {}: {e}", path.display()))?;

    Ok(SidecarInfo {
        null_counts,
        has_file_statistics,
        zone_maps: zone_maps.0,
        total_zones: zone_maps.1,
        zone_maps_with_distinct_zones: zone_maps.2,
        utf8_non_ascii: utf8_scan.non_ascii,
        utf8_embedded_nul: utf8_scan.embedded_nul,
        array_trees: trees,
    })
}

/// User metadata segments, payloads included.
///
/// The manifest records only the keys, which cannot distinguish a reader that returns the wrong
/// bytes, truncates a payload, or conflates `present but zero-length` with `absent`. `b64` is the
/// empty string for a zero-length segment and never `null`.
fn write_metadata_segments(
    out: &mut impl Write,
    segments: &[(String, Vec<u8>)],
) -> VortexResult<()> {
    let entries: Vec<Value> = segments
        .iter()
        .map(|(key, bytes)| {
            json!({ "key": key, "b64": base64(bytes), "len": bytes.len() })
        })
        .collect();
    writeln_json(
        out,
        &json!({
            "kind": "metadata",
            "present": !segments.is_empty(),
            // Stored order, not sorted order. They coincide today because the writer sorts by key
            // (vortex-file-0.86.1/src/footer/serializer.rs:116) — recording the stored order is
            // what makes that testable rather than assumed.
            "segments": entries,
        }),
    )
}

/// Walk every `vortex.flat` leaf and record the serialized array tree inside it.
pub async fn array_trees(file: &VortexFile) -> VortexResult<Vec<(u32, ArrayNode)>> {
    let mut out = Vec::new();
    for layout in file.footer().layout().depth_first_traversal() {
        let layout = layout?;
        let Some(flat) = layout.as_opt::<Flat>() else {
            continue;
        };
        let serialized = flat_serialized_array(file, flat).await?;
        let ctx = flat.array_ctx().clone();
        out.push((*flat.segment_id(), array_node(&serialized, &ctx)));
    }
    Ok(out)
}

fn array_node(
    array: &SerializedArray,
    ctx: &vortex::session::registry::ReadContext,
) -> ArrayNode {
    ArrayNode {
        id: ctx
            .resolve(array.encoding_id())
            .map(|id| id.to_string())
            .unwrap_or_else(|| format!("<unresolved index {}>", array.encoding_id())),
        metadata: array.metadata().to_vec(),
        nbuffers: array.nbuffers(),
        children: (0..array.nchildren())
            .map(|i| array_node(&array.child(i), ctx))
            .collect(),
    }
}

fn array_node_json(node: &ArrayNode) -> Value {
    json!({
        "id": node.id,
        // The child COUNT is load-bearing on its own: for BitPacked and ALP the patch shape is
        // encoded in it (0/1 = no patches, 2/3 = patches without chunk offsets, 3/4 = with).
        "nchildren": node.children.len(),
        "nbuffers": node.nbuffers,
        "metadata_len": node.metadata.len(),
        // Verbatim, because a summary would be our reading of the protobuf rather than the bytes.
        // `vortex.bool`'s bit offset lives in here, and it is the only thing that distinguishes
        // the encoding.
        "metadata_b64": base64(&node.metadata),
        "children": node.children.iter().map(array_node_json).collect::<Vec<_>>(),
    })
}

/// Read the serialized array out of one `vortex.flat` leaf.
///
/// Two on-disk shapes, and getting the second wrong is silent until the buffers are needed: by
/// default the segment holds the array flatbuffer *and* its buffers, but under
/// `FLAT_LAYOUT_INLINE_ARRAY_NODE=1` the flatbuffer lives in the layout metadata and the segment
/// holds only the buffers (`vortex-layout-0.86.1/src/layouts/flat/reader.rs:77`).
pub async fn flat_serialized_array(
    file: &VortexFile,
    flat: &Layout<Flat>,
) -> VortexResult<SerializedArray> {
    let segment = file.segment_source().request(flat.segment_id()).await?;
    match flat.array_tree() {
        Some(tree) => SerializedArray::from_flatbuffer_and_segment(tree.clone(), segment),
        None => SerializedArray::try_from(segment),
    }
}

fn writeln_json(out: &mut impl Write, value: &Value) -> VortexResult<()> {
    serde_json::to_writer(&mut *out, value).map_err(|e| vortex_err!("serializing sidecar: {e}"))?;
    out.write_all(b"\n")
        .map_err(|e| vortex_err!("writing sidecar: {e}"))?;
    Ok(())
}

// -------------------------------------------------------------------------------------------
// Rows
// -------------------------------------------------------------------------------------------

/// What the value pass noticed about the file's strings, folded back into the manifest so a
/// coverage gate can assert the corpus is not ASCII-only.
#[derive(Default)]
pub struct Utf8Scan {
    pub non_ascii: bool,
    pub embedded_nul: bool,
}

impl Utf8Scan {
    /// Walk an encoded value looking for Utf8 leaves. Cheaper and simpler than a second pass over
    /// the scalars: `utf8_json` already recorded byte length and character count, and they differ
    /// exactly when the value is not ASCII.
    fn observe(&mut self, dtype: &DType, value: &Value) {
        match (dtype, value) {
            (DType::Utf8(_), Value::Object(obj)) => {
                let len = obj.get("len").and_then(Value::as_u64).unwrap_or(0);
                let chars = obj.get("char_count").and_then(Value::as_u64).unwrap_or(0);
                if len != chars {
                    self.non_ascii = true;
                }
                if let Some(b64) = obj.get("b64").and_then(Value::as_str) {
                    // A U+0000 is one zero byte, which base64 encodes as an "A" sextet; decoding
                    // the handful of string values that could carry one is cheaper than being
                    // clever about it.
                    if b64.contains('A') && crate::util::base64_has_zero_byte(b64) {
                        self.embedded_nul = true;
                    }
                }
            }
            (DType::List(elem, _), Value::Array(items))
            | (DType::FixedSizeList(elem, _, _), Value::Array(items)) => {
                for item in items {
                    self.observe(elem, item);
                }
            }
            (DType::Struct(fields, _), Value::Object(obj)) => {
                for (idx, name) in fields.names().iter().enumerate() {
                    if let (Some(child_dtype), Some(child)) =
                        (fields.field_by_index(idx), obj.get(name.as_ref()))
                    {
                        self.observe(&child_dtype, child);
                    }
                }
            }
            (DType::Map(map, _), Value::Array(entries)) => {
                let (k, v) = (map.key_dtype(), map.value_dtype());
                for entry in entries {
                    if let Some(obj) = entry.as_object() {
                        if let Some(key) = obj.get("key") {
                            self.observe(&k, key);
                        }
                        if let Some(value) = obj.get("value") {
                            self.observe(&v, value);
                        }
                    }
                }
            }
            (DType::Extension(ext), value) => self.observe(ext.storage_dtype(), value),
            _ => {}
        }
    }
}

async fn write_rows(
    out: &mut impl Write,
    session: &VortexSession,
    file: &VortexFile,
    dtype: &DType,
    row_count: u64,
    utf8: &mut Utf8Scan,
) -> VortexResult<BTreeMap<String, u64>> {
    let mut null_counts: BTreeMap<String, u64> = BTreeMap::new();
    if row_count == 0 {
        return Ok(null_counts);
    }

    let array: ArrayRef = file.scan()?.into_array_stream()?.read_all().await?;
    let mut ctx = session.create_execution_ctx();

    let mut batch: Vec<Value> = Vec::with_capacity(ROWS_PER_LINE);
    let mut from = 0usize;
    for i in 0..array.len() {
        let scalar: Scalar = array.execute_scalar(i, &mut ctx)?;
        let value = value_json(dtype, scalar.value())?;
        count_nulls(dtype, &value, "", &mut null_counts);
        utf8.observe(dtype, &value);
        batch.push(value);
        if batch.len() == ROWS_PER_LINE {
            writeln_json(out, &json!({ "kind": "rows", "from": from, "v": batch }))?;
            from = i + 1;
            batch = Vec::with_capacity(ROWS_PER_LINE);
        }
    }
    if !batch.is_empty() {
        writeln_json(out, &json!({ "kind": "rows", "from": from, "v": batch }))?;
    }
    Ok(null_counts)
}

/// Tally nulls per field path, so a .NET test can cross-check its validity handling in one
/// comparison instead of re-deriving it from every value.
///
/// A path counts a row as null when the value there is null **or** when an ancestor struct is
/// null — the same number a reader gets by materializing that leaf column, and the reason a null
/// struct still descends into its children here.
fn count_nulls(dtype: &DType, value: &Value, path: &str, out: &mut BTreeMap<String, u64>) {
    let is_null = value.is_null();
    *out.entry(path.to_string()).or_insert(0) += u64::from(is_null);

    let DType::Struct(fields, _) = dtype else {
        return;
    };
    for (idx, name) in fields.names().iter().enumerate() {
        let Some(child_dtype) = fields.field_by_index(idx) else {
            continue;
        };
        let child_path = if path.is_empty() {
            name.to_string()
        } else {
            format!("{path}.{name}")
        };
        let child = if is_null {
            &Value::Null
        } else {
            value.get(name.as_ref()).unwrap_or(&Value::Null)
        };
        count_nulls(&child_dtype, child, &child_path, out);
    }
}

// -------------------------------------------------------------------------------------------
// Values
// -------------------------------------------------------------------------------------------

/// Encode one scalar value. `None` means null; see the header legend for the shapes.
pub fn value_json(dtype: &DType, value: Option<&ScalarValue>) -> VortexResult<Value> {
    let Some(value) = value else {
        return Ok(Value::Null);
    };
    Ok(match (dtype, value) {
        (DType::Null, _) => Value::Null,
        (DType::Bool(_), ScalarValue::Bool(b)) => Value::Bool(*b),
        (DType::Primitive(_, _), ScalarValue::Primitive(p)) => primitive_json(p),
        (DType::Decimal(_, _), ScalarValue::Decimal(d)) => decimal_json(d),
        (DType::Utf8(_), ScalarValue::Utf8(s)) => utf8_json(s.as_str()),
        (DType::Binary(_), ScalarValue::Binary(b)) => bytes_json(b.as_slice()),
        (DType::List(elem, _), ScalarValue::Tuple(children)) => Value::Array(
            children
                .iter()
                .map(|c| value_json(elem, c.as_ref()))
                .collect::<VortexResult<Vec<_>>>()?,
        ),
        (DType::FixedSizeList(elem, _, _), ScalarValue::Tuple(children)) => Value::Array(
            children
                .iter()
                .map(|c| value_json(elem, c.as_ref()))
                .collect::<VortexResult<Vec<_>>>()?,
        ),
        (DType::Struct(fields, _), ScalarValue::Tuple(children)) => {
            let mut obj = serde_json::Map::with_capacity(children.len());
            for (idx, name) in fields.names().iter().enumerate() {
                let child_dtype = fields
                    .field_by_index(idx)
                    .ok_or_else(|| vortex_err!("struct field {idx} missing from {dtype}"))?;
                let child = children.get(idx).and_then(|c| c.as_ref());
                obj.insert(name.to_string(), value_json(&child_dtype, child)?);
            }
            Value::Object(obj)
        }
        (DType::Map(map, _), ScalarValue::Tuple(entries)) => {
            let key_dtype = map.key_dtype();
            let value_dtype = map.value_dtype();
            let mut out = Vec::with_capacity(entries.len());
            for entry in entries {
                match entry {
                    Some(ScalarValue::Tuple(kv)) => out.push(json!({
                        "key": value_json(&key_dtype, kv.first().and_then(|k| k.as_ref()))?,
                        "value": value_json(&value_dtype, kv.get(1).and_then(|v| v.as_ref()))?,
                    })),
                    // A null entry inside a map is not expressible through the public API, but
                    // recording it honestly beats pretending it cannot happen.
                    _ => out.push(Value::Null),
                }
            }
            Value::Array(out)
        }
        (DType::Extension(ext), v) => value_json(ext.storage_dtype(), Some(v))?,
        (DType::Variant(_), ScalarValue::Variant(inner)) => json!({
            "dtype": dtype_json(inner.dtype()),
            "value": value_json(inner.dtype(), inner.value())?,
        }),
        (DType::Union(variants, _), ScalarValue::Union(u)) => {
            let child_index = variants.tag_to_child_index(u.type_id()).ok_or_else(|| {
                vortex_err!("union tag {} not present in {dtype}", u.type_id())
            })?;
            let child_dtype = variants
                .variant_by_index(child_index)
                .ok_or_else(|| vortex_err!("union child {child_index} missing from {dtype}"))?;
            json!({
                "type_id": u.type_id(),
                "value": value_json(&child_dtype, u.child_value())?,
            })
        }
        (dtype, value) => {
            return Err(vortex_err!(
                "sidecar has no encoding for value {value:?} under dtype {dtype}"
            ));
        }
    })
}

fn primitive_json(value: &PValue) -> Value {
    match value {
        PValue::U8(v) => json!(v.to_string()),
        PValue::U16(v) => json!(v.to_string()),
        PValue::U32(v) => json!(v.to_string()),
        PValue::U64(v) => json!(v.to_string()),
        PValue::I8(v) => json!(v.to_string()),
        PValue::I16(v) => json!(v.to_string()),
        PValue::I32(v) => json!(v.to_string()),
        PValue::I64(v) => json!(v.to_string()),
        // Bits first: a wrong NaN payload or a -0.0 read as +0.0 is invisible in the decimal.
        PValue::F16(v) => float_json(&v.to_bits().to_be_bytes(), format!("{:?}", v.to_f32())),
        PValue::F32(v) => float_json(&v.to_bits().to_be_bytes(), format!("{v:?}")),
        PValue::F64(v) => float_json(&v.to_bits().to_be_bytes(), format!("{v:?}")),
    }
}

/// `bits` is normative; `dec` is a convenience.
///
/// Rust's `{:?}` spells the infinities `inf` / `-inf`, which `double.Parse` rejects — a trap laid
/// in the sidecar for exactly the consumer it was written for. Both Rust and .NET parse
/// `Infinity` / `-Infinity` / `NaN`, so that is what goes on the wire, and a `special` field names
/// the case explicitly for a consumer that would rather branch than parse.
fn float_json(be_bits: &[u8], decimal: String) -> Value {
    let (dec, special) = match decimal.as_str() {
        "inf" => ("Infinity".to_string(), Some("Infinity")),
        "-inf" => ("-Infinity".to_string(), Some("-Infinity")),
        "NaN" => ("NaN".to_string(), Some("NaN")),
        _ => (decimal, None),
    };
    match special {
        Some(special) => {
            json!({ "bits": format!("0x{}", hex(be_bits)), "dec": dec, "special": special })
        }
        None => json!({ "bits": format!("0x{}", hex(be_bits)), "dec": dec }),
    }
}

fn decimal_json(value: &DecimalValue) -> Value {
    let (unscaled, storage) = match value {
        DecimalValue::I8(v) => (v.to_string(), "i8"),
        DecimalValue::I16(v) => (v.to_string(), "i16"),
        DecimalValue::I32(v) => (v.to_string(), "i32"),
        DecimalValue::I64(v) => (v.to_string(), "i64"),
        DecimalValue::I128(v) => (v.to_string(), "i128"),
        DecimalValue::I256(v) => (v.to_string(), "i256"),
    };
    json!({ "unscaled": unscaled, "storage": storage })
}

fn bytes_json(bytes: &[u8]) -> Value {
    json!({ "b64": base64(bytes), "len": bytes.len() })
}

/// Utf8 carries both counts.
///
/// .NET strings are UTF-16: the whole class of bug that lives in UTF-8 -> UTF-16 conversion is
/// invisible on ASCII, where the byte count and the character count coincide. Stating both makes
/// a byte-vs-char confusion a failed comparison rather than a silent agreement.
fn utf8_json(s: &str) -> Value {
    json!({
        "b64": base64(s.as_bytes()),
        "len": s.len(),
        "char_count": s.chars().count(),
    })
}

// -------------------------------------------------------------------------------------------
// DType tree
// -------------------------------------------------------------------------------------------

fn nullable(n: Nullability) -> bool {
    n.is_nullable()
}

/// A machine-readable dtype tree, so the .NET side can assert its *parsed* dtype rather than
/// string-comparing a Display form that is free to change.
pub fn dtype_json(dtype: &DType) -> Value {
    match dtype {
        DType::Null => json!({ "kind": "null" }),
        DType::Bool(n) => json!({ "kind": "bool", "nullable": nullable(*n) }),
        DType::Primitive(p, n) => json!({
            "kind": "primitive",
            "ptype": p.to_string(),
            "nullable": nullable(*n),
        }),
        DType::Decimal(d, n) => json!({
            "kind": "decimal",
            "precision": d.precision(),
            "scale": d.scale(),
            "nullable": nullable(*n),
        }),
        DType::Utf8(n) => json!({ "kind": "utf8", "nullable": nullable(*n) }),
        DType::Binary(n) => json!({ "kind": "binary", "nullable": nullable(*n) }),
        DType::List(elem, n) => json!({
            "kind": "list",
            "element": dtype_json(elem),
            "nullable": nullable(*n),
        }),
        DType::FixedSizeList(elem, size, n) => json!({
            "kind": "fixed_size_list",
            "element": dtype_json(elem),
            "size": size,
            "nullable": nullable(*n),
        }),
        DType::Map(m, n) => json!({
            "kind": "map",
            "key": dtype_json(&m.key_dtype()),
            "value": dtype_json(&m.value_dtype()),
            "keys_sorted": m.keys_sorted(),
            "nullable": nullable(*n),
        }),
        DType::Struct(fields, n) => json!({
            "kind": "struct",
            "fields": fields
                .names()
                .iter()
                .enumerate()
                .map(|(idx, name)| json!({
                    "name": name.to_string(),
                    "dtype": fields.field_by_index(idx).map(|d| dtype_json(&d)),
                }))
                .collect::<Vec<_>>(),
            "nullable": nullable(*n),
        }),
        DType::Union(variants, n) => json!({
            "kind": "union",
            "variants": (0..variants.len())
                .map(|i| json!({
                    "type_id": variants.type_ids().get(i),
                    "dtype": variants.variant_by_index(i).map(|d| dtype_json(&d)),
                }))
                .collect::<Vec<_>>(),
            "nullable": nullable(*n),
        }),
        DType::Variant(n) => json!({ "kind": "variant", "nullable": nullable(*n) }),
        DType::Extension(ext) => json!({
            "kind": "extension",
            "id": ext.id().to_string(),
            // The raw metadata bytes, because unit and timezone live in there and a reader that
            // drops them produces the wrong civil time with no other symptom.
            "metadata_hex": ext.serialize_metadata().map(|b| hex(&b)).unwrap_or_default(),
            "metadata_display": ext.display_metadata().to_string(),
            "storage": dtype_json(ext.storage_dtype()),
            "nullable": ext.is_nullable(),
        }),
    }
}

// -------------------------------------------------------------------------------------------
// Layout tree
// -------------------------------------------------------------------------------------------

fn layout_json(
    layout: &LayoutRef,
    name: &str,
    trees: &BTreeMap<u32, Value>,
) -> VortexResult<Value> {
    let children: Vec<Value> = layout
        .children()?
        .iter()
        .zip(layout.child_names())
        .map(|(child, child_name)| layout_json(child, &child_name, trees))
        .collect::<VortexResult<Vec<_>>>()?;
    let segment_ids: Vec<u32> = layout.segment_ids().iter().map(|s| **s).collect();
    // The array tree belongs to the `vortex.flat` leaf that owns the segment holding it.
    let array_tree = layout
        .as_opt::<Flat>()
        .and_then(|flat| trees.get(&*flat.segment_id()))
        .cloned();
    let mut obj = serde_json::Map::new();
    obj.insert("name".into(), json!(name));
    obj.insert("encoding_id".into(), json!(layout.encoding_id().to_string()));
    // A parsed dtype tree, not a Display string: string-comparing Vortex's Display grammar would
    // make the .NET side implement a second spec that lives only in the Rust source.
    obj.insert("dtype".into(), dtype_json(layout.dtype()));
    obj.insert("dtype_display".into(), json!(layout.dtype().to_string()));
    obj.insert("row_count".into(), json!(layout.row_count()));
    obj.insert("segment_ids".into(), json!(segment_ids));
    obj.insert("metadata_bytes".into(), json!(layout.metadata().len()));
    if let Some(tree) = array_tree {
        obj.insert("array_tree".into(), tree);
    }
    obj.insert("children".into(), json!(children));
    Ok(Value::Object(obj))
}

// -------------------------------------------------------------------------------------------
// Statistics
// -------------------------------------------------------------------------------------------

fn write_file_statistics(out: &mut impl Write, file: &VortexFile) -> VortexResult<bool> {
    let Some(stats) = file.footer().statistics() else {
        writeln_json(out, &json!({ "kind": "file_stats", "present": false }))?;
        return Ok(false);
    };

    let names: Vec<String> = match file.dtype() {
        DType::Struct(fields, _) => fields.names().iter().map(|n| n.to_string()).collect(),
        _ => vec![String::new()],
    };

    let fields: Vec<Value> = stats
        .stats_sets()
        .iter()
        .zip(stats.dtypes().iter())
        .enumerate()
        .map(|(idx, (set, dtype))| {
            Ok(json!({
                "field": names.get(idx).cloned().unwrap_or_else(|| idx.to_string()),
                "dtype": dtype_json(dtype),
                "dtype_display": dtype.to_string(),
                "stats": stats_set_json(set, dtype)?,
            }))
        })
        .collect::<VortexResult<Vec<_>>>()?;

    writeln_json(
        out,
        &json!({ "kind": "file_stats", "present": true, "fields": fields }),
    )?;
    Ok(true)
}

fn stats_set_json(
    set: &vortex::array::stats::StatsSet,
    field_dtype: &DType,
) -> VortexResult<Value> {
    let mut obj = serde_json::Map::new();
    for (stat, precision) in set.iter() {
        let stat_dtype = stat.dtype(field_dtype);
        let (kind, value) = match precision {
            Precision::Exact(v) => ("exact", Some(v)),
            Precision::Inexact(v) => ("inexact", Some(v)),
            Precision::Absent => ("absent", None),
        };
        let encoded = match (value, &stat_dtype) {
            (Some(v), Some(d)) => value_json(d, Some(v))?,
            (Some(_), None) => Value::String("<no dtype for stat>".into()),
            (None, _) => Value::Null,
        };
        obj.insert(
            stat.name().to_string(),
            json!({ "precision": kind, "value": encoded }),
        );
    }
    Ok(Value::Object(obj))
}

/// Split `vortex.bounded_min(64)` into its component id and argument, and say whether the value
/// under it is exact or a bound.
///
/// docs/08-semantics.md §1 makes the distinction load-bearing for pruning: an `Inexact` minimum is
/// a lower bound on the true minimum, so a predicate may not use it to prove a zone empty the way
/// it can with an exact one.
fn aggregate_detail_json(spec: &str) -> Value {
    let (id, arg) = match spec.split_once('(') {
        Some((id, rest)) => (id, rest.strip_suffix(')').unwrap_or(rest)),
        None => (spec, ""),
    };
    let bounded = matches!(id, "vortex.bounded_min" | "vortex.bounded_max");
    json!({
        "spec": spec,
        "id": id,
        "arg": if arg.is_empty() { Value::Null } else { json!(arg) },
        "precision": if bounded { "bound" } else { "exact" },
        // A bounded partial is a struct {bound, unknown}. `unknown: true` means the input has a
        // non-null extreme but no bound representable inside the byte limit — there is no value
        // there at all, and a reader that treats the null `bound` as "no data" prunes wrongly.
        "unknown_field": if bounded { json!("unknown") } else { Value::Null },
    })
}

/// Zone maps, decoded from the `zones` child of every `vortex.zoned` layout.
///
/// This is the pruning input: a reader that mis-parses a zone map does not fail, it silently
/// skips matching rows, which is exactly the class of bug the corpus exists to catch.
async fn write_zone_maps(
    out: &mut impl Write,
    session: &VortexSession,
    file: &VortexFile,
) -> VortexResult<(usize, usize, usize)> {
    let mut nmaps = 0usize;
    let mut nzones_total = 0usize;
    let mut ndistinct_maps = 0usize;
    let mut ctx = session.create_execution_ctx();

    for layout in file.footer().layout().depth_first_traversal() {
        let layout = layout?;
        let Some(zoned) = layout.as_opt::<Zoned>() else {
            continue;
        };
        nmaps += 1;
        nzones_total += zoned.nzones();

        // Children are (data, zones) in slot order; the zone table is the auxiliary one.
        let children = layout.children()?;
        let names: Vec<String> = layout.child_names().map(|n| n.to_string()).collect();
        let zones_layout = names
            .iter()
            .position(|n| n == "zones")
            .and_then(|idx| children.get(idx));

        let mut distinct_zones = false;
        let zones = match zones_layout.and_then(|l| l.as_opt::<Flat>()) {
            Some(flat) => {
                let serialized = flat_serialized_array(file, flat).await?;
                let decoded = serialized.decode(
                    flat.dtype(),
                    usize::try_from(flat.row_count())
                        .map_err(|e| vortex_err!("zone row count does not fit: {e}"))?,
                    flat.array_ctx(),
                    session,
                )?;
                let zone_dtype = flat.dtype().clone();
                let mut rows = Vec::with_capacity(decoded.len());
                for i in 0..decoded.len() {
                    let scalar = decoded.execute_scalar(i, &mut ctx)?;
                    rows.push(value_json(&zone_dtype, scalar.value())?);
                }
                // Whether the zones actually differ. A map whose zones carry byte-identical
                // bounds cannot prune anything, so it proves a zone map can be parsed and nothing
                // more; the manifest counts the ones that can.
                let distinct: std::collections::BTreeSet<String> =
                    rows.iter().map(|r| r.to_string()).collect();
                distinct_zones = zoned.nzones() > 1 && distinct.len() == zoned.nzones();
                json!({
                    "dtype": dtype_json(&zone_dtype),
                    "distinct_zones": distinct_zones,
                    "rows": rows,
                })
            }
            // A zone table that is not a flat leaf would need its own segment handling; say so
            // rather than emitting an empty table that looks like "no zones".
            None => json!({ "unavailable": "zones child is not a vortex.flat leaf" }),
        };

        let specs: Vec<String> = zoned
            .present_aggregates()
            .iter()
            .map(|a| a.to_string())
            .collect();

        writeln_json(
            out,
            &json!({
                "kind": "zone_map",
                "layout_dtype": dtype_json(layout.dtype()),
                "layout_dtype_display": layout.dtype().to_string(),
                "zone_len": zoned.zone_len(),
                "nzones": zoned.nzones(),
                "aggregates": specs,
                // Precision per aggregate, derived from the aggregate kind. Without it the zone
                // map — which is the actual pruning input — cannot say which of its values is a
                // real extreme and which is a truncated bound, and a reader that prunes on a
                // bound as if it were exact drops rows that do match.
                "aggregate_details": specs
                    .iter()
                    .map(|spec| aggregate_detail_json(spec))
                    .collect::<Vec<_>>(),
                "zones": zones,
            }),
        )?;
        if distinct_zones {
            ndistinct_maps += 1;
        }
    }
    Ok((nmaps, nzones_total, ndistinct_maps))
}
