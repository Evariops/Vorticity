//! The corpus plan, the write configurations, and the produce-then-verify loop.
//!
//! Every corpus file is an [`Entry`]: a build closure plus a [`WriteSpec`]. Entries are
//! independently fallible on purpose — a recipe that 0.86.1 turns out not to support becomes a
//! [`SkipRecord`] with the error text, never a silent omission and never a failed run.
//!
//! The one invariant enforced here is the coverage gate's precondition: what an entry *claims* to
//! contain is checked against what was actually written, by walking the serialized array trees of
//! the file just produced. See [`array_encoding_ids`].

use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::path::Path;
use std::sync::Arc;

use vortex::array::ArrayId;
use vortex::array::ArrayRef;
use vortex::array::IntoArray;
use vortex::array::arrays::ChunkedArray;
use vortex::array::arrays::PrimitiveArray;
use vortex::array::arrays::StructArray;
use vortex::array::session::ArraySessionExt;
use vortex::array::validity::Validity;
use vortex::array::arrays::BoolArray;
use vortex::buffer::BitBuffer;
use vortex::buffer::Buffer;
use vortex::buffer::ByteBuffer;
use vortex::compressor::BtrBlocksCompressorBuilder;
use vortex::dtype::DType;
use vortex::dtype::Nullability;
use vortex::dtype::PType;
use vortex::editions::ComponentKind;
use vortex::editions::EditionSessionExt;
use vortex::editions::EditionId;
use vortex::error::VortexResult;
use vortex::error::vortex_err;
use vortex::file::OpenOptionsSessionExt;
use vortex::file::VortexFile;
use vortex::file::VortexWriteOptions;
use vortex::file::WriteOptionsSessionExt;
use vortex::file::WriteStrategyBuilder;
use vortex::io::session::RuntimeSessionExt;
use vortex::io::std_file::FileWrite;
use vortex::layout::LayoutStrategy;
use vortex::layout::layouts::chunked::writer::ChunkedLayoutStrategy;
use vortex::layout::layouts::collect::CollectStrategy;
use vortex::layout::layouts::compressed::CompressingStrategy;
use vortex::layout::layouts::flat::Flat;
use vortex::layout::layouts::flat::writer::FlatLayoutStrategy;
use vortex::layout::layouts::table::TableStrategy;
use vortex::layout::layouts::zoned::Zoned;
use vortex::scalar::Scalar;
use vortex::session::VortexSession;
use vortex::utils::aliases::hash_set::HashSet;

use crate::encodings;
use crate::manifest::FileRecord;
use crate::manifest::SkipRecord;
use crate::manifest::VORTEX_VERSION;
use crate::schema;
use crate::sidecar;
use crate::util::Rng;
use crate::util::sha256_file;

/// Editions the corpus targets, newest last. `PREVIEW_2026_08_0` is deliberately absent: it is not
/// frozen, so a file written against it carries no read-forever guarantee.
pub const EDITIONS: &[(&str, EditionId)] = &[
    ("core2025.05.0", vortex::editions::CORE_2025_05_0),
    ("core2025.06.0", vortex::editions::CORE_2025_06_0),
    ("core2025.10.0", vortex::editions::CORE_2025_10_0),
    ("core2026.08.0", vortex::editions::CORE_2026_08_0),
    ("core2026.08.1", vortex::editions::CORE_2026_08_1),
    ("core2026.08.2", vortex::editions::CORE_2026_08_2),
    ("core2026.08.3", vortex::editions::CORE_2026_08_3),
];

/// The edition a default `VortexSession` targets.
pub const DEFAULT_EDITION: &str = "core2026.08.3";

/// How the layout pipeline is assembled for one entry.
#[derive(Clone, Debug)]
pub enum StrategyKind {
    /// The writer's own default: struct split, zone maps, BtrBlocks compression, flat leaves.
    /// This is the production path, and it is what the type and distribution matrices use.
    Default,
    /// Default, but with an explicit row block size and coalescing disabled, so the blocks
    /// actually survive as `vortex.chunked` layouts.
    RowBlock(usize),
    /// `chunked(flat)`: no repartition, no canonicalization, no compression, no zone maps.
    /// Whatever encoding the array already carries is what lands in the file — the only way to
    /// force an encoding deterministically (API-NOTES.md §3.5).
    Verbatim,
    /// No compression and no zone maps, but still struct-split: only canonical encodings.
    Canonical,
    /// The default pipeline minus `ZonedStrategy`.
    NoZoneMaps,
    /// No zone maps, plus the compressor restricted to the session's enabled editions. Required
    /// for pre-`core2026.08.0` editions: passing `with_strategy` opts out of the writer's own
    /// `retain_allowed_encodings`, so it has to be reapplied by hand.
    EditionSafe,
}

/// Everything an entry says about how its file is written.
#[derive(Clone, Debug)]
pub struct WriteSpec {
    pub strategy: StrategyKind,
    pub file_statistics: bool,
    pub exclude_dtype: bool,
    pub disable_editions: bool,
    pub metadata: Vec<(String, Vec<u8>)>,
}

impl Default for WriteSpec {
    fn default() -> Self {
        Self {
            strategy: StrategyKind::Default,
            file_statistics: true,
            exclude_dtype: false,
            disable_editions: false,
            metadata: Vec::new(),
        }
    }
}

impl WriteSpec {
    fn with(strategy: StrategyKind) -> Self {
        Self {
            strategy,
            ..Self::default()
        }
    }

    /// The configuration used to force an encoding: verbatim layout, no file statistics (their
    /// own arrays would otherwise add ids the entry never asked for).
    fn forced() -> Self {
        Self {
            strategy: StrategyKind::Verbatim,
            file_statistics: false,
            ..Self::default()
        }
    }
}

/// What a build closure hands back.
pub struct Build {
    pub array: ArrayRef,
    pub spec: WriteSpec,
}

type BuildFn = Box<dyn Fn(&VortexSession, &mut Rng) -> VortexResult<Build>>;

/// One corpus file.
pub struct Entry {
    pub id: String,
    pub dimension: &'static str,
    pub description: String,
    /// `None` uses the session default (`core2026.08.3`).
    pub edition: Option<&'static str>,
    /// Process-level switches the variant needs. An entry with a non-empty `env` is produced by a
    /// re-exec of this binary, because every Vortex env switch is read once per process through a
    /// `LazyLock`.
    pub env: Vec<(String, String)>,
    /// Array ids the entry claims to force. Verified after writing; a miss is recorded, not hidden.
    pub expected_array_ids: Vec<String>,
    /// Layout ids the entry claims to force. Same treatment.
    pub expected_layout_ids: Vec<String>,
    pub notes: Vec<String>,
    pub build: BuildFn,
}

// -------------------------------------------------------------------------------------------
// Strategies
// -------------------------------------------------------------------------------------------

fn flat() -> Arc<dyn LayoutStrategy> {
    Arc::new(FlatLayoutStrategy::default())
}

fn verbatim_strategy() -> Arc<dyn LayoutStrategy> {
    Arc::new(ChunkedLayoutStrategy::new(flat()))
}

fn table_strategy(compressing: Arc<dyn LayoutStrategy>) -> Arc<dyn LayoutStrategy> {
    let validity = CollectStrategy::new(flat());
    Arc::new(TableStrategy::new(Arc::new(validity), compressing))
}

fn no_zonemap_strategy() -> Arc<dyn LayoutStrategy> {
    let chunked = ChunkedLayoutStrategy::new(flat());
    table_strategy(Arc::new(CompressingStrategy::new(
        chunked,
        BtrBlocksCompressorBuilder::default().build(),
    )))
}

fn canonical_strategy() -> Arc<dyn LayoutStrategy> {
    let chunked = ChunkedLayoutStrategy::new(flat());
    table_strategy(Arc::new(CompressingStrategy::new(
        chunked,
        BtrBlocksCompressorBuilder::empty().build(),
    )))
}

/// The in-memory array encodings the session's enabled editions permit, resolved exactly as
/// `vortex_file::writer::new_array_context` does: enabled ids are *serialized* ids, while
/// `retain_allowed_encodings` wants *plugin* ids.
fn allowed_encodings(session: &VortexSession) -> HashSet<ArrayId> {
    let arrays = session.arrays();
    session
        .enabled_component_ids(ComponentKind::Array)
        .iter()
        .filter_map(|serialized_id| arrays.registry().get(serialized_id))
        .map(|plugin| plugin.id())
        .collect()
}

fn edition_safe_strategy(session: &VortexSession) -> Arc<dyn LayoutStrategy> {
    let allowed = allowed_encodings(session);
    let chunked = ChunkedLayoutStrategy::new(flat());
    table_strategy(Arc::new(CompressingStrategy::new(
        chunked,
        BtrBlocksCompressorBuilder::default()
            .retain_allowed_encodings(&allowed)
            .build(),
    )))
}

fn build_options(session: &VortexSession, spec: &WriteSpec) -> VortexWriteOptions {
    let mut opts = session.write_options();

    match &spec.strategy {
        StrategyKind::Default => {}
        StrategyKind::RowBlock(rows) => {
            opts = opts.with_strategy(
                WriteStrategyBuilder::default()
                    .with_row_block_size(*rows)
                    // Required, not decorative: the 1 MiB coalescing target would merge the
                    // blocks straight back into one (API-NOTES.md §3.6).
                    .with_data_block_target_bytes(None)
                    // Passing `with_strategy` opts out of the writer's own
                    // `retain_allowed_encodings`, so it has to be reapplied by hand — otherwise a
                    // strictly increasing column elects `fastlanes.delta`, which belongs to no
                    // core edition, and the write is rejected at serialization time.
                    .with_btrblocks_builder(
                        BtrBlocksCompressorBuilder::default()
                            .retain_allowed_encodings(&allowed_encodings(session)),
                    )
                    .build(),
            );
        }
        StrategyKind::Verbatim => opts = opts.with_strategy(verbatim_strategy()),
        StrategyKind::Canonical => opts = opts.with_strategy(canonical_strategy()),
        StrategyKind::NoZoneMaps => opts = opts.with_strategy(no_zonemap_strategy()),
        StrategyKind::EditionSafe => opts = opts.with_strategy(edition_safe_strategy(session)),
    }

    if !spec.file_statistics {
        opts = opts.with_file_statistics(vec![]);
    }
    if spec.exclude_dtype {
        opts = opts.exclude_dtype();
    }
    if spec.disable_editions {
        opts = opts.disable_editions();
    }
    for (key, bytes) in &spec.metadata {
        opts = opts.with_metadata_segment(key.clone(), ByteBuffer::copy_from(bytes.as_slice()));
    }
    opts
}

// -------------------------------------------------------------------------------------------
// Produce
// -------------------------------------------------------------------------------------------

/// Build, write, read back, and describe one corpus file.
pub async fn produce(
    session: &VortexSession,
    entry: &Entry,
    out_dir: &Path,
    seed: u64,
) -> anyhow::Result<FileRecord> {
    let vortex_path = out_dir.join(format!("{}.vortex", entry.id));
    let sidecar_path = out_dir.join(format!("{}.jsonl", entry.id));
    if let Some(parent) = vortex_path.parent() {
        std::fs::create_dir_all(parent)?;
    }

    let mut rng = Rng::new(seed);
    let build = (entry.build)(session, &mut rng)?;
    let dtype = build.array.dtype().clone();

    let sink = FileWrite::create(&vortex_path, session.handle()).await?;
    build_options(session, &build.spec)
        .write(sink, build.array.to_array_stream())
        .await?;

    // Everything below is read back out of the bytes just written. Nothing is inferred from the
    // configuration that produced them.
    let mut open = session.open_options();
    if build.spec.exclude_dtype {
        open = open.with_dtype(dtype.clone());
    }
    let file = open.open_path(&vortex_path).await?;

    let layout_ids = layout_encoding_ids(&file)?;
    let (aggregate_ids, aggregate_specs) = aggregates(&file)?;
    let mut extension_dtype_ids = BTreeSet::new();
    collect_extension_ids(file.dtype(), &mut extension_dtype_ids);

    // Everything the sidecar needs that the reader will not tell it: which entry it is, and the
    // hash of the bytes it describes. Read once, reused for the EOF marker and for the user
    // metadata payloads (the footer gives their offsets, not their bytes).
    let file_bytes = std::fs::read(&vortex_path)?;
    let (file_format_version, postscript_bytes) = read_eof(&file_bytes, &vortex_path)?;
    let sha256 = crate::util::sha256_bytes(&file_bytes);
    let metadata_segments = read_metadata_segments(&file, &file_bytes)?;

    let info = sidecar::write_sidecar(
        session,
        &file,
        &sidecar_path,
        &sidecar::SidecarIdentity {
            entry_id: &entry.id,
            path: &format!("{}.vortex", entry.id),
            sha256: &sha256,
            metadata_segments: &metadata_segments,
        },
    )
    .await?;

    // The coverage gate reads what was actually serialized, walked out of the same trees the
    // sidecar records — one extraction path, not two that can drift.
    let mut array_ids: BTreeSet<String> = BTreeSet::new();
    let mut node_shapes: BTreeSet<String> = BTreeSet::new();
    let mut dict_nodes: BTreeSet<String> = BTreeSet::new();
    let mut max_bool_bit_offset = 0u32;
    for (_segment, tree) in &info.array_trees {
        tree.walk(&mut |node| {
            array_ids.insert(node.id.clone());
            node_shapes.insert(format!(
                "{}/c{}/b{}",
                node.id,
                node.children.len(),
                node.nbuffers
            ));
            match node.id.as_str() {
                "vortex.bool" => {
                    // BoolMetadata { offset: u32 @ tag 1 }. A protobuf field equal to zero is not
                    // serialized at all, so an empty metadata means offset 0.
                    if let Some(offset) = protobuf_varint_field(&node.metadata, 1) {
                        max_bool_bit_offset = max_bool_bit_offset.max(offset as u32);
                    }
                }
                "vortex.dict" => {
                    // DictMetadata { values_len @1, codes_ptype @2, is_nullable_codes @3 (opt),
                    // all_values_referenced @4 (opt) }.
                    let ptype = protobuf_varint_field(&node.metadata, 2)
                        .and_then(|v| i32::try_from(v).ok())
                        .and_then(|v| vortex::dtype::PType::try_from(v).ok())
                        .map(|p| p.to_string())
                        .unwrap_or_else(|| "u8".to_string());
                    // Three distinct states, and `absent` is the read-forever default rule.
                    let nullable = match protobuf_varint_field(&node.metadata, 3) {
                        None => "codes_nullability_absent",
                        Some(0) => "codes_nonnull",
                        Some(_) => "codes_nullable",
                    };
                    dict_nodes.insert(format!("codes={ptype} {nullable}"));
                }
                _ => {}
            }
        });
    }

    let missing_expected_array_ids: Vec<String> = entry
        .expected_array_ids
        .iter()
        .filter(|id| !array_ids.contains(*id))
        .cloned()
        .collect();
    let missing_expected_layout_ids: Vec<String> = entry
        .expected_layout_ids
        .iter()
        .filter(|id| !layout_ids.contains(*id))
        .cloned()
        .collect();

    let mut notes = entry.notes.clone();
    if !missing_expected_layout_ids.is_empty() {
        notes.push(format!(
            "expected layout ids not present: {}",
            missing_expected_layout_ids.join(", ")
        ));
    }

    let size_bytes = file_bytes.len() as u64;

    Ok(FileRecord {
        id: entry.id.clone(),
        dimension: entry.dimension.to_string(),
        description: entry.description.clone(),
        path: format!("{}.vortex", entry.id),
        sidecar: format!("{}.jsonl", entry.id),
        vortex_version: VORTEX_VERSION.to_string(),
        target_edition: entry.edition.unwrap_or(DEFAULT_EDITION).to_string(),
        row_count: file.row_count(),
        dtype: dtype.to_string(),
        array_ids: array_ids.into_iter().collect(),
        array_node_shapes: node_shapes.into_iter().collect(),
        dict_nodes: dict_nodes.into_iter().collect(),
        max_bool_bit_offset,
        layout_ids: layout_ids.into_iter().collect(),
        declared_array_ids: declared_array_ids(&file),
        declared_layout_ids: declared_layout_ids(&file),
        aggregate_ids: aggregate_ids.into_iter().collect(),
        aggregate_specs: aggregate_specs.into_iter().collect(),
        extension_dtype_ids: extension_dtype_ids.into_iter().collect(),
        sha256,
        sidecar_sha256: sha256_file(&sidecar_path)?,
        size_bytes,
        file_format_version,
        postscript_bytes,
        has_dtype_segment: !build.spec.exclude_dtype,
        has_file_statistics: info.has_file_statistics,
        metadata_segments: metadata_segments
            .iter()
            .map(|(k, v)| crate::manifest::MetadataSegmentRecord {
                key: k.clone(),
                len: v.len(),
            })
            .collect(),
        zone_maps: info.zone_maps,
        total_zones: info.total_zones,
        zone_maps_with_distinct_zones: info.zone_maps_with_distinct_zones,
        utf8_non_ascii: info.utf8_non_ascii,
        utf8_embedded_nul: info.utf8_embedded_nul,
        null_counts: info.null_counts,
        expected_array_ids: entry.expected_array_ids.clone(),
        missing_expected_array_ids,
        seed,
        env: entry
            .env
            .iter()
            .map(|(k, v)| (k.clone(), v.clone()))
            .collect::<BTreeMap<_, _>>(),
        notes,
    })
}

/// The last 8 bytes of a Vortex file: `u16` format version, `u16` postscript length, `VTXF`.
///
/// Read by hand rather than through the reader, because the postscript length is the number the
/// "postscript near the 65527-byte ceiling" corpus dimension is about, and the reader does not
/// expose it.
fn read_eof(bytes: &[u8], path: &Path) -> anyhow::Result<(u16, u16)> {
    let n = bytes.len();
    if n < 8 {
        anyhow::bail!("{} is only {n} bytes, too short for an EOF marker", path.display());
    }
    let eof = &bytes[n - 8..];
    if &eof[4..8] != b"VTXF" {
        anyhow::bail!("{} does not end with the VTXF magic", path.display());
    }
    Ok((
        u16::from_le_bytes([eof[0], eof[1]]),
        u16::from_le_bytes([eof[2], eof[3]]),
    ))
}

/// Every layout encoding id actually present in the file's layout tree.
fn layout_encoding_ids(file: &VortexFile) -> VortexResult<BTreeSet<String>> {
    let mut ids = BTreeSet::new();
    for layout in file.footer().layout().depth_first_traversal() {
        ids.insert(layout?.encoding_id().to_string());
    }
    Ok(ids)
}

/// The bytes of every user metadata segment, in the order the footer stores them.
///
/// The footer exposes each segment's offset and length, not its payload, so the payload is sliced
/// out of the file bytes. Nothing anchored the payloads before this: the manifest listed only the
/// keys, so a reader that returned the wrong bytes for a key, truncated one, or conflated a
/// present-but-empty segment with an absent one passed every check in the corpus.
fn read_metadata_segments(
    file: &VortexFile,
    bytes: &[u8],
) -> anyhow::Result<Vec<(String, Vec<u8>)>> {
    let mut out = Vec::new();
    for (key, spec) in file.footer().metadata_segments() {
        let range = spec.byte_range();
        let start = usize::try_from(range.start)?;
        let end = usize::try_from(range.end)?;
        if end > bytes.len() {
            anyhow::bail!(
                "metadata segment {key} claims bytes {start}..{end} of a {}-byte file",
                bytes.len()
            );
        }
        out.push((key.to_string(), bytes[start..end].to_vec()));
    }
    Ok(out)
}

/// Read one varint scalar field out of a protobuf message, or `None` if the field is absent.
///
/// Encoding metadata is a prost message of a handful of scalar fields, and two of them decide how
/// an array is read: `vortex.bool`'s bit offset and `vortex.dict`'s codes ptype / nullable-codes
/// flag. A field equal to its default is not serialized at all, so `None` genuinely means absent —
/// which for `is_nullable_codes` is a third state, not a synonym for `false`.
fn protobuf_varint_field(metadata: &[u8], field: u64) -> Option<u64> {
    let mut i = 0usize;
    while i < metadata.len() {
        let (key, next) = read_varint(metadata, i)?;
        i = next;
        let (number, wire_type) = (key >> 3, key & 0x7);
        match wire_type {
            0 => {
                let (value, next) = read_varint(metadata, i)?;
                i = next;
                if number == field {
                    return Some(value);
                }
            }
            1 => i = i.checked_add(8)?,
            2 => {
                let (len, next) = read_varint(metadata, i)?;
                i = next.checked_add(usize::try_from(len).ok()?)?;
            }
            5 => i = i.checked_add(4)?,
            // Groups (3, 4) are not emitted by prost and nothing here should hit them.
            _ => return None,
        }
    }
    None
}

fn read_varint(bytes: &[u8], mut i: usize) -> Option<(u64, usize)> {
    let mut value = 0u64;
    let mut shift = 0u32;
    loop {
        let byte = *bytes.get(i)?;
        i += 1;
        value |= u64::from(byte & 0x7F) << shift;
        if byte & 0x80 == 0 {
            return Some((value, i));
        }
        shift += 7;
        if shift >= 64 {
            return None;
        }
    }
}

/// Zone-map aggregates present in the file's `vortex.zoned` layouts.
///
/// Returns `(component ids, reader spellings)`. `present_aggregates` reports the aggregate
/// *expression* — `vortex.bounded_min(64)`, `vortex.max()` — while the edition manifests in
/// spec/editions/ list the bare component id, so the coverage gate needs the argument stripped.
/// Both are recorded: the argument is part of the zone-map contract, not noise.
fn aggregates(file: &VortexFile) -> VortexResult<(BTreeSet<String>, BTreeSet<String>)> {
    let mut ids = BTreeSet::new();
    let mut specs = BTreeSet::new();
    for layout in file.footer().layout().depth_first_traversal() {
        let layout = layout?;
        if let Some(zoned) = layout.as_opt::<Zoned>() {
            for aggregate in zoned.present_aggregates().iter() {
                let spec = aggregate.to_string();
                ids.insert(spec.split('(').next().unwrap_or(&spec).to_string());
                specs.insert(spec);
            }
        }
    }
    Ok((ids, specs))
}

fn declared_array_ids(file: &VortexFile) -> usize {
    for layout in file.footer().layout().depth_first_traversal().flatten() {
        if let Some(flat) = layout.as_opt::<Flat>() {
            return flat.array_ctx().ids().len();
        }
    }
    0
}

fn declared_layout_ids(file: &VortexFile) -> usize {
    // The footer's layout spec list is not directly exposed; the distinct ids in the tree are the
    // closest honest answer, and the difference from `layout_ids` is always zero here.
    file.footer()
        .layout()
        .depth_first_traversal()
        .flatten()
        .map(|l| l.encoding_id().to_string())
        .collect::<BTreeSet<_>>()
        .len()
}

fn collect_extension_ids(dtype: &DType, out: &mut BTreeSet<String>) {
    match dtype {
        DType::Extension(ext) => {
            out.insert(ext.id().to_string());
            collect_extension_ids(ext.storage_dtype(), out);
        }
        DType::List(elem, _) | DType::FixedSizeList(elem, _, _) => collect_extension_ids(elem, out),
        DType::Struct(fields, _) => {
            for field in fields.fields() {
                collect_extension_ids(&field, out);
            }
        }
        DType::Map(map, _) => {
            collect_extension_ids(&map.key_dtype(), out);
            collect_extension_ids(&map.value_dtype(), out);
        }
        DType::Union(variants, _) => {
            for variant in variants.variants() {
                collect_extension_ids(&variant, out);
            }
        }
        _ => {}
    }
}

// -------------------------------------------------------------------------------------------
// The plan
// -------------------------------------------------------------------------------------------

fn entry(
    id: impl Into<String>,
    dimension: &'static str,
    description: impl Into<String>,
    build: BuildFn,
) -> Entry {
    Entry {
        id: id.into(),
        dimension,
        description: description.into(),
        edition: None,
        env: Vec::new(),
        expected_array_ids: Vec::new(),
        expected_layout_ids: Vec::new(),
        notes: Vec::new(),
        build,
    }
}

/// The whole corpus: every entry to produce, and every dimension that cannot be produced at all.
pub fn plan() -> (Vec<Entry>, Vec<SkipRecord>) {
    let mut entries = Vec::new();
    entries.extend(type_matrix_entries());
    entries.extend(encoding_entries());
    entries.extend(distribution_entries());
    entries.extend(container_entries());
    entries.extend(edition_entries());
    (entries, static_skips())
}

// --- A. type matrix --------------------------------------------------------------------------

/// Whether the file root is a nullable struct, which the 0.86.1 writer refuses outright: the
/// file-statistics accumulator is built before any option is consulted and panics on one
/// (`vortex-layout-0.86.1/src/layouts/file_stats.rs:461`).
fn nullable_struct_root(dtype: &DType) -> bool {
    matches!(dtype, DType::Struct(_, n) if n.is_nullable())
}

fn type_matrix_entries() -> Vec<Entry> {
    let mut out = Vec::new();

    for case in schema::type_matrix() {
        let nullabilities: &[(Nullability, &str)] = if case.nullability_is_free {
            &[
                (Nullability::NonNullable, "nonnull"),
                (Nullability::Nullable, "nullable"),
            ]
        } else {
            &[(Nullability::Nullable, "nullable")]
        };

        for (nullability, nullability_name) in nullabilities {
            // A nullable top-level struct cannot be written by 0.86.1 at all: the file-statistics
            // accumulator is constructed unconditionally and panics on one, whatever
            // `with_file_statistics` says. See the matching skip record in `static_skips`. The
            // nullable-struct *column* case is still covered, by `struct_nested_deep`'s inner
            // field `b`.
            if matches!((case.dtype)(*nullability), Ok(d) if nullable_struct_root(&d)) {
                continue;
            }
            for rows in schema::ROW_COUNTS {
                let (rows, nullability) = (*rows, *nullability);
                let dtype_fn = case.dtype;
                let value_fn = case.value;
                out.push(entry(
                    format!("types/{}_{}_r{}", case.id, nullability_name, rows),
                    "A",
                    format!(
                        "{} ({}), {rows} rows, default write options",
                        case.id, nullability_name
                    ),
                    Box::new(move |_session, _rng| {
                        let dtype = dtype_fn(nullability)?;
                        Ok(Build {
                            array: schema::build_column(&dtype, rows, value_fn)?,
                            spec: WriteSpec::default(),
                        })
                    }),
                ));
            }
        }
    }

    // A wide struct root: every dtype as a field of one file, which is what a real table looks
    // like and what the default writer is tuned for.
    out.push(entry(
        "types/struct_root_all_dtypes",
        "A",
        "one struct with a column per logical dtype, 1025 rows",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: all_dtypes_struct(1025)?,
                spec: WriteSpec::default(),
            })
        }),
    ));

    // A non-struct root, named explicitly. Every single-column file above is already one, but a
    // reader-side test wants an unambiguous fixture to point at.
    out.push(entry(
        "types/non_struct_root_i64",
        "A",
        "a bare i64 column as the file root (no struct wrapper)",
        Box::new(|_session, _rng| {
            let dtype = DType::Primitive(PType::I64, Nullability::NonNullable);
            Ok(Build {
                array: schema::build_column_with(&dtype, 1025, |i| {
                    Ok(Scalar::primitive(i as i64 * 3 - 7, Nullability::NonNullable))
                })?,
                spec: WriteSpec::default(),
            })
        }),
    ));

    let mut no_dtype = entry(
        "types/no_dtype_segment",
        "A",
        "written with exclude_dtype(): the reader must be given the dtype out of band",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: all_dtypes_struct(256)?,
                spec: WriteSpec {
                    exclude_dtype: true,
                    ..WriteSpec::default()
                },
            })
        }),
    );
    no_dtype.notes.push(
        "the dtype is in the sidecar's `dtype` line; VortexOpenOptions::with_dtype is required"
            .into(),
    );
    out.push(no_dtype);

    // Field names that are not lowercase ASCII identifiers. The whole corpus used `[a-z0-9_]+`,
    // which leaves out the one hazard this format actually has: a `vortex.zoned` layout names its
    // children `data` and `zones`, a `vortex.dict` layout names them `codes` and `values`, and
    // those sit in the same tree as user field names. A reader that resolves layout children by
    // name rather than by index breaks on a struct field literally called `zones`.
    let mut field_names = entry(
        "types/struct_field_names",
        "A",
        "a struct whose field names are empty, non-ASCII, space- and dot-bearing, case-only \
         distinct, and colliding with the layout tree's own reserved child names",
        Box::new(|_session, _rng| {
            let nn = Nullability::NonNullable;
            let columns: Vec<(String, ArrayRef)> = STRUCT_FIELD_NAMES
                .iter()
                .enumerate()
                .map(|(idx, name)| {
                    let column = schema::build_column_with(
                        &DType::Primitive(PType::I32, nn),
                        1025,
                        |i| Ok(Scalar::primitive((i * 100 + idx) as i32, nn)),
                    )?;
                    Ok(((*name).to_string(), column))
                })
                .collect::<VortexResult<Vec<_>>>()?;
            Ok(Build {
                array: StructArray::try_from_iter(columns)?.into_array(),
                spec: WriteSpec::default(),
            })
        }),
    );
    field_names.notes.push(format!(
        "field names, in order: {}",
        STRUCT_FIELD_NAMES
            .iter()
            .map(|n| format!("{n:?}"))
            .collect::<Vec<_>>()
            .join(", ")
    ));
    out.push(field_names);

    out.push(entry(
        "types/user_metadata_segments",
        "A",
        "three user metadata segments alongside the data",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: all_dtypes_struct(256)?,
                spec: WriteSpec {
                    metadata: vec![
                        ("conformance.text".to_string(), b"hello, corpus".to_vec()),
                        ("conformance.binary".to_string(), (0u8..=255).collect()),
                        ("conformance.empty".to_string(), Vec::new()),
                    ],
                    ..WriteSpec::default()
                },
            })
        }),
    ));

    out
}

/// The adversarial struct field names of `types/struct_field_names`.
///
/// `zones`, `data`, `codes` and `values` are the names the layout tree itself uses for its
/// children; the rest cover an empty name, a case-only pair, non-ASCII in three encoded widths,
/// and the two separators (`.` and a space) a path-joining reader is most likely to split on.
pub const STRUCT_FIELD_NAMES: &[&str] = &[
    "",
    "zones",
    "data",
    "codes",
    "values",
    "\u{00E9}lan",
    "\u{540D}\u{524D}",
    "\u{1F600}",
    "has space",
    "a.b",
    "A",
    "a",
];

/// A struct with one field per logical dtype in the matrix, at `rows` rows.
fn all_dtypes_struct(rows: usize) -> VortexResult<ArrayRef> {
    let mut columns: Vec<(String, ArrayRef)> = Vec::new();
    for case in schema::type_matrix() {
        // Always nullable where the dtype allows a choice: a wide struct is the file shape the
        // default writer is tuned for, and the nullable variant exercises strictly more of it.
        // `DType::Null` ignores the argument.
        let dtype = (case.dtype)(Nullability::Nullable)?;
        columns.push((
            case.id.to_string(),
            schema::build_column(&dtype, rows, case.value)?,
        ));
    }
    Ok(StructArray::try_from_iter(columns)?.into_array())
}

// --- B. encoding matrix ----------------------------------------------------------------------

fn encoding_entries() -> Vec<Entry> {
    let mut out = Vec::new();
    for case in encodings::encoding_cases() {
        let fixed = encodings::FIXED_LENGTH_CASES.contains(&case.id);
        let lengths: Vec<usize> = if fixed {
            vec![encodings::ROWS]
        } else {
            std::iter::once(encodings::ROWS)
                .chain(encodings::DEGENERATE_ROWS.iter().copied())
                .collect()
        };

        for rows in lengths {
            let build_fn = case.build;
            let disable_editions = case.disable_editions;
            // The 4096-row file keeps the bare name; the degenerate lengths are suffixed. Keeping
            // the base name stable means the row-count matrix is additive rather than a rename of
            // every fixture the .NET side already points at.
            let id = if rows == encodings::ROWS {
                format!("encodings/{}", case.id)
            } else {
                format!("encodings/{}_r{rows}", case.id)
            };
            let mut e = entry(
                id,
                "B",
                format!("{} forced ({rows} rows): {}", case.array_id, case.how),
                Box::new(move |session: &VortexSession, _rng: &mut Rng| {
                    Ok(Build {
                        array: build_fn(session, rows)?,
                        spec: WriteSpec {
                            disable_editions,
                            ..WriteSpec::forced()
                        },
                    })
                }),
            );
            // A zero-row array reaches no segment at all, so claiming its encoding would report a
            // forcing failure for a file that is doing exactly what it should.
            if rows > 0 {
                e.expected_array_ids = vec![case.array_id.to_string()];
            }
            e.notes.push(
                "written with a chunked(flat) strategy so the array reaches the segment exactly \
                 as constructed"
                    .into(),
            );
            if rows == 0 {
                e.notes.push(
                    "zero rows: the layout has no children and no segments, so `array_ids` is \
                     empty by construction and no encoding is claimed"
                        .into(),
                );
            }
            if fixed {
                e.notes.push(
                    "emitted at 4096 rows only: the recipe needs at least one patch interval"
                        .into(),
                );
            }
            if disable_editions {
                e.notes.push(
                    "edition enforcement is off: this id belongs to no core edition, so the \
                     writer's per-kind allowlist rejects the write otherwise. A \
                     forward-compatibility fixture (docs/04-conformance.md §6), not a 1.0 \
                     conformance target."
                        .into(),
                );
            }
            out.push(e);
        }
    }
    out
}

// --- C. value distributions -------------------------------------------------------------------

fn distribution_entries() -> Vec<Entry> {
    let mut out = Vec::new();
    for dist in schema::distributions() {
        let row_counts: Vec<usize> = if dist.id == "huge_string" {
            vec![schema::HUGE_STRING_ROW_COUNT]
        } else {
            schema::DISTRIBUTION_ROW_COUNTS.to_vec()
        };
        for rows in row_counts {
            let build_fn = dist.build;
            out.push(entry(
                format!("distributions/{}_r{rows}", dist.id),
                "C",
                format!("{} ({rows} rows)", dist.description),
                Box::new(move |_session, rng: &mut Rng| {
                    Ok(Build {
                        array: build_fn(rows, rng)?,
                        spec: WriteSpec::default(),
                    })
                }),
            ));
        }
    }
    out
}

// --- D. container variations --------------------------------------------------------------------

/// A low-cardinality string column: what makes the default writer emit a `vortex.dict` *layout*.
fn dict_layout_data(rows: usize) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(Nullability::NonNullable);
    let vocab = ["alpha", "beta", "gamma", "delta", "epsilon"];
    let column = schema::build_column_with(&dtype, rows, |i| {
        Ok(Scalar::utf8(vocab[i % vocab.len()], Nullability::NonNullable))
    })?;
    Ok(StructArray::try_from_iter([("category", column)])?.into_array())
}

fn mixed_table(rows: usize) -> VortexResult<ArrayRef> {
    let ints: Buffer<i64> = (0..rows as i64).map(|i| 1_000_000 + (i * 7) % 977).collect();
    let strs = schema::build_column_with(&DType::Utf8(Nullability::NonNullable), rows, |i| {
        Ok(Scalar::utf8(
            format!("value-{}", i % 8),
            Nullability::NonNullable,
        ))
    })?;
    Ok(StructArray::try_from_iter([
        (
            "ints",
            PrimitiveArray::new(ints, Validity::NonNullable).into_array(),
        ),
        ("strs", strs),
    ])?
    .into_array())
}

/// A table shaped so that every claimed zone-map aggregate has something to report: a sorted
/// integer column (min/max/bounded_min/bounded_max), a nullable one (null_count), and a float
/// column carrying NaN (nan_count).
fn zone_map_table(rows: usize) -> VortexResult<ArrayRef> {
    let nn = Nullability::NonNullable;
    let nul = Nullability::Nullable;

    let ints = schema::build_column_with(&DType::Primitive(PType::I64, nn), rows, |i| {
        Ok(Scalar::primitive(1_000_000i64 + (i as i64 * 7) % 977, nn))
    })?;
    let nullable_ints =
        schema::build_column_with(&DType::Primitive(PType::I32, nul), rows, |i| {
            Ok(if i % 11 == 4 {
                Scalar::null(DType::Primitive(PType::I32, nul))
            } else {
                Scalar::primitive(i as i32, nul)
            })
        })?;
    let floats = schema::build_column_with(&DType::Primitive(PType::F64, nn), rows, |i| {
        Ok(Scalar::primitive(
            match i % 13 {
                3 => f64::NAN,
                7 => f64::INFINITY,
                _ => i as f64 * 0.25,
            },
            nn,
        ))
    })?;
    let strs = schema::build_column_with(&DType::Utf8(nn), rows, |i| {
        Ok(Scalar::utf8(format!("value-{}", i % 8), nn))
    })?;

    Ok(StructArray::try_from_iter([
        ("ints", ints),
        ("nullable_ints", nullable_ints),
        ("floats", floats),
        ("strs", strs),
    ])?
    .into_array())
}

/// Bit-packable values with rare far outliers: the shape that makes bit-packing want patches.
fn patchy_table(rows: usize) -> VortexResult<ArrayRef> {
    let nn = Nullability::NonNullable;
    let column = schema::build_column_with(&DType::Primitive(PType::I64, nn), rows, |i| {
        let v = if i % 211 == 7 {
            1_000_000_000i64 + i as i64
        } else {
            (i % 251) as i64
        };
        Ok(Scalar::primitive(v, nn))
    })?;
    Ok(StructArray::try_from_iter([("patchy", column)])?.into_array())
}

/// The columns of a table whose values are per-block disjoint, `block` rows to a block.
///
/// `mixed_table` repeats the same value cycle in every block, so a zone map over it has
/// byte-identical bounds in every zone and a predicate can never eliminate a strict, non-trivial
/// subset of them. That made the whole zone-map dimension unable to distinguish a reader that
/// prunes zone 0 from one that prunes zone 1, that inverts a bound comparison, or that ignores
/// `zone_len` when mapping a zone index back to a row range.
fn block_disjoint_columns(rows: usize, block: usize) -> VortexResult<Vec<(String, ArrayRef)>> {
    let nn = Nullability::NonNullable;

    // Strictly increasing: every zone's [min, max] is disjoint from every other zone's.
    let monotone: Buffer<i64> = (0..rows as i64).map(|i| 1_000_000 + i * 3).collect();
    // One distinct value per block: min == max within a zone, and differs across zones.
    let banded: Buffer<i32> = (0..rows).map(|i| (i / block) as i32).collect();
    // A per-zone-disjoint alphabet, so the string bounds — including `vortex.bounded_min(64)` and
    // `vortex.bounded_max(64)` — differ per zone too.
    let strs = schema::build_column_with(&DType::Utf8(nn), rows, |i| {
        Ok(Scalar::utf8(
            format!("z{:04}-{:04}", i / block, i % block),
            nn,
        ))
    })?;

    Ok(vec![
        (
            "monotone".to_string(),
            PrimitiveArray::new(monotone, Validity::NonNullable).into_array(),
        ),
        (
            "banded".to_string(),
            PrimitiveArray::new(banded, Validity::NonNullable).into_array(),
        ),
        ("strs".to_string(), strs),
    ])
}

fn block_disjoint_table(rows: usize, block: usize) -> VortexResult<ArrayRef> {
    Ok(StructArray::try_from_iter(block_disjoint_columns(rows, block)?)?.into_array())
}

/// The same shape, plus columns whose per-zone null and NaN counts sweep the whole range.
///
/// Zone 0 has no nulls, the last zone is entirely null, and the zones in between step through
/// intermediate counts, so `vortex.null_count()` varies from 0 to `block` across the map instead
/// of being the same number in every zone.
fn zone_null_sweep_table(rows: usize, block: usize) -> VortexResult<ArrayRef> {
    let nn = Nullability::NonNullable;
    let nul = Nullability::Nullable;
    let zones = rows.div_ceil(block).max(1);

    let mut columns = block_disjoint_columns(rows, block)?;

    columns.push((
        "nulls".to_string(),
        schema::build_column_with(&DType::Primitive(PType::I64, nul), rows, |i| {
            let zone = i / block;
            let within = i % block;
            // Linear sweep: zone 0 keeps everything, the last zone drops everything.
            let nulls_in_zone = (zone * block) / (zones - 1).max(1);
            Ok(if within < nulls_in_zone {
                Scalar::null(DType::Primitive(PType::I64, nul))
            } else {
                Scalar::primitive(i as i64, nul)
            })
        })?,
    ));
    columns.push((
        "nans".to_string(),
        schema::build_column_with(&DType::Primitive(PType::F64, nn), rows, |i| {
            let zone = i / block;
            let within = i % block;
            Ok(Scalar::primitive(
                if within < zone { f64::NAN } else { i as f64 * 0.5 },
                nn,
            ))
        })?,
    ));

    Ok(StructArray::try_from_iter(columns)?.into_array())
}

fn container_entries() -> Vec<Entry> {
    let mut out = Vec::new();

    let mut dict = entry(
        "containers/dict_layout",
        "D",
        "a low-cardinality string column, which makes the default writer emit a vortex.dict layout",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: dict_layout_data(8192)?,
                spec: WriteSpec::default(),
            })
        }),
    );
    dict.expected_layout_ids = vec!["vortex.dict".into()];
    out.push(dict);

    let mut zoned = entry(
        "containers/zoned_layout",
        "D",
        "the default writer's zone maps over a table that includes NaNs and nulls, so every \
         zone-map aggregate has something to report",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: zone_map_table(8192)?,
                spec: WriteSpec::default(),
            })
        }),
    );
    zoned.expected_layout_ids = vec!["vortex.zoned".into(), "vortex.struct".into()];
    zoned.notes.push(
        "vortex.nan_count only appears for a float column containing NaN, and vortex.null_count \
         only for a nullable one, so both are present here by construction"
            .into(),
    );
    out.push(zoned);

    let mut chunked_layout = entry(
        "containers/chunked_layout_rowblock1024",
        "D",
        "row block size 1024 with coalescing disabled: four vortex.chunked blocks, each block's \
         values disjoint from every other block's",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: block_disjoint_table(4096, 1024)?,
                spec: WriteSpec::with(StrategyKind::RowBlock(1024)),
            })
        }),
    );
    chunked_layout.expected_layout_ids = vec!["vortex.chunked".into()];
    chunked_layout.notes.push(
        "values are per-block disjoint on purpose: with `mixed_table`'s repeated cycle all four \
         zones carried byte-identical bounds, so no predicate could prune a strict subset of them"
            .into(),
    );
    out.push(chunked_layout);

    // The zone-map substrate. 64 zones, and every zone's bounds differ from every other zone's,
    // so a predicate can eliminate any strict non-trivial subset of them. docs/04-conformance.md
    // §6 wants every filter test run twice — pruning on and off, identical result sets — and that
    // test proves nothing on a corpus where pruning can never eliminate anything.
    let mut many_zones = entry(
        "containers/zoned_many_zones",
        "D",
        "64 zones of 1024 rows, every zone's min/max disjoint from every other zone's",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: block_disjoint_table(65_536, 1024)?,
                spec: WriteSpec::with(StrategyKind::RowBlock(1024)),
            })
        }),
    );
    many_zones.expected_layout_ids = vec!["vortex.zoned".into(), "vortex.chunked".into()];
    many_zones.notes.push(
        "monotone is strictly increasing (disjoint numeric bounds), banded holds one distinct \
         value per zone, and strs draws from a per-zone-disjoint alphabet so the bounded_min/max \
         string bounds differ per zone too"
            .into(),
    );
    out.push(many_zones);

    let mut many_zones_nulls = entry(
        "containers/zoned_many_zones_nulls",
        "D",
        "64 zones whose per-zone null count sweeps 0..1024, including an entirely null zone",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: zone_null_sweep_table(65_536, 1024)?,
                spec: WriteSpec::with(StrategyKind::RowBlock(1024)),
            })
        }),
    );
    many_zones_nulls.expected_layout_ids = vec!["vortex.zoned".into()];
    many_zones_nulls.notes.push(
        "vortex.null_count() and vortex.nan_count() both vary per zone here; everywhere else in \
         the corpus they are the same number in every zone of a file"
            .into(),
    );
    out.push(many_zones_nulls);

    let mut chunked_stream = entry(
        "containers/chunked_stream_3",
        "D",
        "a 3-chunk ChunkedArray handed to the writer as a stream, written verbatim",
        Box::new(|_session, _rng| {
            let chunks: Vec<ArrayRef> = (0..3)
                .map(|c| {
                    let values: Buffer<i64> = (0..100i64).map(|i| c * 1_000 + i).collect();
                    PrimitiveArray::new(values, Validity::NonNullable).into_array()
                })
                .collect();
            let dtype = chunks[0].dtype().clone();
            Ok(Build {
                array: ChunkedArray::try_new(chunks, dtype)?.into_array(),
                spec: WriteSpec::forced(),
            })
        }),
    );
    chunked_stream.expected_layout_ids = vec!["vortex.chunked".into(), "vortex.flat".into()];
    out.push(chunked_stream);

    // An all-null column that does NOT collapse to `vortex.constant`: written through the
    // uncompressed-canonical pipeline, so the values buffer is materialized and the validity is a
    // real all-zero child rather than an implied `Validity::AllInvalid`.
    let mut all_null_canonical = entry(
        "containers/all_null_i64_explicit_validity_r1025",
        "D",
        "1025 all-null i64 rows with no compression: a materialized column under an all-zero \
         validity child",
        Box::new(|_session, _rng| {
            // Built by hand rather than through the scalar builder: the builder folds an all-null
            // column into a `ConstantArray`, which is exactly the collapse this entry exists to
            // avoid. Here the values buffer is real and the validity is an all-zero bool child.
            let values: Buffer<i64> = (0..1025i64).map(|i| i * 31 + 7).collect();
            let validity = BoolArray::new(
                BitBuffer::collect_bool(1025, |_| false),
                Validity::NonNullable,
            )
            .into_array();
            let column =
                PrimitiveArray::new(values, Validity::Array(validity)).into_array();
            Ok(Build {
                array: StructArray::try_from_iter([("all_null", column)])?.into_array(),
                spec: WriteSpec::forced(),
            })
        }),
    );
    all_null_canonical.expected_array_ids =
        vec!["vortex.primitive".into(), "vortex.bool".into()];
    all_null_canonical.notes.push(
        "every other all-null column in the corpus serializes to a single vortex.constant node, \
         so the null_count == len short-circuit of an explicit validity array was never exercised"
            .into(),
    );
    all_null_canonical.notes.push(
        "written verbatim rather than through StrategyKind::Canonical: even with every \
         compression scheme removed (BtrBlocksCompressorBuilder::empty) the pipeline still folds \
         an all-null column to vortex.constant, so verbatim is the only way to keep the \
         materialized values buffer and the all-zero validity child on disk"
            .into(),
    );
    out.push(all_null_canonical);

    out.push(entry(
        "containers/no_zone_maps",
        "D",
        "the default pipeline minus ZonedStrategy, and no file statistics",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: mixed_table(4096)?,
                spec: WriteSpec {
                    strategy: StrategyKind::NoZoneMaps,
                    file_statistics: false,
                    ..WriteSpec::default()
                },
            })
        }),
    ));

    out.push(entry(
        "containers/uncompressed_canonical",
        "D",
        "no compression schemes at all: only canonical encodings reach the file",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: mixed_table(4096)?,
                spec: WriteSpec {
                    strategy: StrategyKind::Canonical,
                    file_statistics: false,
                    ..WriteSpec::default()
                },
            })
        }),
    ));

    out.push(entry(
        "containers/no_file_statistics",
        "D",
        "with_file_statistics(vec![]): the file-level statistics flatbuffer is absent",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: mixed_table(4096)?,
                spec: WriteSpec {
                    file_statistics: false,
                    ..WriteSpec::default()
                },
            })
        }),
    ));

    let mut editions_off = entry(
        "containers/editions_disabled",
        "D",
        "disable_editions(): no edition enforcement on the write",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: mixed_table(4096)?,
                spec: WriteSpec {
                    disable_editions: true,
                    ..WriteSpec::default()
                },
            })
        }),
    );
    editions_off.notes.push(
        "the array context then declares every registered id, not just the edition's members"
            .into(),
    );
    out.push(editions_off);

    // The largest postscript the 0.86.1 writer can produce: MAX_METADATA_SEGMENTS (16) keys of
    // MAX_METADATA_KEY_BYTES (64) each. See the skip record for why this is nowhere near 65527.
    let mut max_ps = entry(
        "containers/postscript_max_metadata",
        "D",
        "16 metadata segments with 64-byte keys: the largest postscript this writer can emit",
        Box::new(|_session, _rng| {
            let metadata = (0..16)
                .map(|i| {
                    let key = format!("conformance.metadata.segment.{i:02}.{}", "k".repeat(40));
                    (key[..64.min(key.len())].to_string(), vec![i as u8; 32])
                })
                .collect();
            Ok(Build {
                array: mixed_table(256)?,
                spec: WriteSpec {
                    metadata,
                    ..WriteSpec::default()
                },
            })
        }),
    );
    max_ps.notes.push(
        "the achieved postscript length is recorded in `postscript_bytes`; MAX_POSTSCRIPT_SIZE \
         is 65527 and the writer cannot come close"
            .into(),
    );
    out.push(max_ps);

    // A file whose segments hold `vortex.zstd` arrays. This is NOT segment-level Zstd (see the
    // skip record); it is the closest thing 0.86.1 can write, and it does exercise a
    // decompress-before-decode path.
    let mut zstd = entry(
        "containers/zstd_arrays_in_segments",
        "D",
        "every data segment holds a vortex.zstd array",
        Box::new(|session: &VortexSession, _rng: &mut Rng| {
            Ok(Build {
                array: encodings::encoding_cases()
                    .into_iter()
                    .find(|c| c.id == "zstd")
                    .map(|c| (c.build)(session, encodings::ROWS))
                    .transpose()?
                    .ok_or_else(|| vortex_err!("zstd encoding case missing"))?,
                spec: WriteSpec::forced(),
            })
        }),
    );
    zstd.expected_array_ids = vec!["vortex.zstd".into()];
    out.push(zstd);

    // Env-var variants. Each needs its own process, because every Vortex env switch is read once
    // per process through a LazyLock.
    let mut inline = entry(
        "containers/flat_inline_array_node",
        "D",
        "FLAT_LAYOUT_INLINE_ARRAY_NODE=1: the array flatbuffer lives in the layout metadata and \
         the segment holds only buffers",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: mixed_table(4096)?,
                spec: WriteSpec::default(),
            })
        }),
    );
    inline.env = vec![("FLAT_LAYOUT_INLINE_ARRAY_NODE".into(), "1".into())];
    inline
        .notes
        .push("a different offset-reconstruction path on read".into());
    out.push(inline);

    // Two halves of one experiment. The interesting result is the first: with the experimental
    // patched array switched on but a core edition enforced, `retain_allowed_encodings` drops
    // BitPackingScheme *whole* — a scheme survives only if **all** of its `produced_encodings()`
    // are permitted (vortex-btrblocks-0.86.1/src/builder.rs:207), and `vortex.patched` belongs to
    // no core edition. So the column ends up with no FastLanes encoding at all.
    let mut patched_kept = entry(
        "containers/experimental_patched_array",
        "D",
        "VORTEX_EXPERIMENTAL_PATCHED_ARRAY=1 with a core edition enforced",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: patchy_table(8192)?,
                spec: WriteSpec::default(),
            })
        }),
    );
    patched_kept.env = vec![("VORTEX_EXPERIMENTAL_PATCHED_ARRAY".into(), "1".into())];
    patched_kept.notes.push(
        "demonstrates the `retain_allowed_encodings` all-or-nothing rule: BitPackingScheme now \
         declares `vortex.patched` among its produced encodings, that id is in no core edition, \
         so the whole scheme is dropped and the bit-packable column is written uncompressed. \
         Compare with containers/experimental_patched_array_editions_off."
            .into(),
    );
    out.push(patched_kept);

    let mut patched_off = entry(
        "containers/experimental_patched_array_editions_off",
        "D",
        "VORTEX_EXPERIMENTAL_PATCHED_ARRAY=1 with edition enforcement disabled: vortex.patched \
         reaches the wire",
        Box::new(|_session, _rng| {
            Ok(Build {
                array: patchy_table(8192)?,
                spec: WriteSpec {
                    disable_editions: true,
                    ..WriteSpec::default()
                },
            })
        }),
    );
    patched_off.env = vec![("VORTEX_EXPERIMENTAL_PATCHED_ARRAY".into(), "1".into())];
    patched_off.expected_array_ids = vec!["vortex.patched".into()];
    patched_off.notes.push(
        "vortex.patched belongs to no core edition, so this is a forward-compatibility fixture, \
         not a conformance target: a conformant reader may reject it."
            .into(),
    );
    out.push(patched_off);

    let mut list_layout = entry(
        "containers/experimental_list_layout",
        "D",
        "VORTEX_EXPERIMENTAL_LIST_LAYOUT=1: lists get their own layout strategy",
        Box::new(|_session, _rng| {
            let elem = Arc::new(DType::Primitive(PType::I32, Nullability::NonNullable));
            let dtype = DType::List(Arc::clone(&elem), Nullability::NonNullable);
            let column = schema::build_column_with(&dtype, 8192, |i| {
                Ok(Scalar::list(
                    Arc::clone(&elem),
                    (0..(i % 5))
                        .map(|k| {
                            Scalar::primitive((i + k) as i32, Nullability::NonNullable)
                        })
                        .collect(),
                    Nullability::NonNullable,
                ))
            })?;
            Ok(Build {
                array: StructArray::try_from_iter([("items", column)])?.into_array(),
                spec: WriteSpec {
                    disable_editions: true,
                    ..WriteSpec::default()
                },
            })
        }),
    );
    list_layout.env = vec![("VORTEX_EXPERIMENTAL_LIST_LAYOUT".into(), "1".into())];
    list_layout.expected_layout_ids = vec!["vortex.list".into()];
    list_layout.notes.push(
        "edition enforcement is off: the `vortex.list` LAYOUT id belongs to no core edition \
         (spec/editions/*.toml lists six layouts and this is not one), so the write is rejected \
         otherwise. A forward-compatibility fixture, not a conformance target."
            .into(),
    );
    out.push(list_layout);

    out
}

// --- E. edition spread -------------------------------------------------------------------------

/// A table shaped to elect the components a given edition *adds*.
///
/// docs/04-conformance.md §8 wants one file per core edition precisely to catch hard-coding
/// today's encodings, and that only works if each file actually contains the thing its edition
/// froze. Through corpus format/1 all seven edition files wrote the same table, and five of the
/// seven contained none of their own additions: `core2025.10.0`'s array ids were byte-identical to
/// `core2025.06.0`'s, `core2026.08.2` had no map column, `core2026.08.3` had no extension dtype at
/// all. The dimension proved the writer accepted an edition target, not that a reader pinned to an
/// older edition set can read forward.
///
/// Columns are cumulative: the file for edition N carries every earlier edition's shapes plus its
/// own, which is the read-forever story stated as data.
fn edition_table(edition_index: usize, rows: usize) -> VortexResult<ArrayRef> {
    let nn = Nullability::NonNullable;
    let nul = Nullability::Nullable;
    let mut columns: Vec<(String, ArrayRef)> = Vec::new();

    // core2025.05.0: the canonical and structural base, plus a dictionary-shaped string column.
    let ints: Buffer<i64> = (0..rows as i64).map(|i| 1_000_000 + (i * 7) % 977).collect();
    columns.push((
        "ints".to_string(),
        PrimitiveArray::new(ints, Validity::NonNullable).into_array(),
    ));
    columns.push((
        "strs".to_string(),
        schema::build_column_with(&DType::Utf8(nn), rows, |i| {
            Ok(Scalar::utf8(format!("value-{}", i % 8), nn))
        })?,
    ));

    // core2025.06.0 adds vortex.sequence (and pco, zstd): a perfect arithmetic progression.
    if edition_index >= 1 {
        columns.push((
            "seq".to_string(),
            schema::build_column_with(&DType::Primitive(PType::I64, nn), rows, |i| {
                Ok(Scalar::primitive(1_000i64 + 7 * i as i64, nn))
            })?,
        ));
    }

    // core2025.10.0 adds fastlanes.rle, vortex.masked, vortex.fixed_size_list, vortex.listview.
    if edition_index >= 2 {
        columns.push((
            "runs".to_string(),
            schema::build_column_with(&DType::Primitive(PType::I32, nn), rows, |i| {
                Ok(Scalar::primitive((i / 257) as i32, nn))
            })?,
        ));
        columns.push((
            "nullable".to_string(),
            schema::build_column_with(&DType::Primitive(PType::I64, nul), rows, |i| {
                Ok(if i % 5 == 2 {
                    Scalar::null(DType::Primitive(PType::I64, nul))
                } else {
                    Scalar::primitive((i * 13) as i64, nul)
                })
            })?,
        ));
        let fsl_elem = Arc::new(DType::Primitive(PType::I32, nn));
        let fsl_dtype = DType::FixedSizeList(Arc::clone(&fsl_elem), 3, nn);
        columns.push((
            "fsl".to_string(),
            schema::build_column_with(&fsl_dtype, rows, |i| {
                Ok(Scalar::fixed_size_list(
                    Arc::clone(&fsl_elem),
                    (0..3).map(|k| Scalar::primitive((i * 3 + k) as i32, nn)).collect(),
                    nn,
                ))
            })?,
        ));
        let list_elem = Arc::new(DType::Utf8(nn));
        let list_dtype = DType::List(Arc::clone(&list_elem), nn);
        columns.push((
            "list".to_string(),
            schema::build_column_with(&list_dtype, rows, |i| {
                Ok(Scalar::list(
                    Arc::clone(&list_elem),
                    (0..(i % 4)).map(|k| Scalar::utf8(format!("e{}", i + k), nn)).collect(),
                    nn,
                ))
            })?,
        ));
    }

    // core2026.08.1 adds vortex.onpair: the long-shared-prefix distribution elects it.
    if edition_index >= 4 {
        columns.push((
            "prefixed".to_string(),
            schema::build_column_with(&DType::Utf8(nn), rows, |i| {
                Ok(Scalar::utf8(
                    format!("https://example.invalid/vortex/conformance/item/{i:09}"),
                    nn,
                ))
            })?,
        ));
    }

    // core2026.08.2 adds vortex.map.
    if edition_index >= 5 {
        let map_dtype = DType::Map(
            vortex::dtype::MapDType::try_new(
                DType::Utf8(nn),
                DType::Primitive(PType::I64, nul),
                true,
            )?,
            nn,
        );
        let map_for_value = map_dtype.clone();
        columns.push((
            "map".to_string(),
            schema::build_column_with(&map_dtype, rows, |i| {
                let entries = (0..(i % 3)).map(|k| {
                    (
                        Scalar::utf8(format!("k{k:03}"), nn),
                        if (i + k) % 4 == 3 {
                            Scalar::null(DType::Primitive(PType::I64, nul))
                        } else {
                            Scalar::primitive((i * 31 + k) as i64, nul)
                        },
                    )
                });
                Scalar::try_map(map_for_value.clone(), entries)
            })?,
        ));
    }

    // core2026.08.3 adds the vortex.uuid dtype (and the two variant arrays).
    if edition_index >= 6 {
        let uuid = DType::Extension(
            vortex::dtype::extension::ExtDType::<vortex::extension::uuid::Uuid>::try_new(
                vortex::extension::uuid::UuidMetadata::default(),
                DType::FixedSizeList(Arc::new(DType::Primitive(PType::U8, nn)), 16, nn),
            )?
            .erased(),
        );
        let uuid_for_value = uuid.clone();
        columns.push((
            "uuid".to_string(),
            schema::build_column_with(&uuid, rows, move |i| {
                let DType::Extension(ext) = &uuid_for_value else {
                    unreachable!("uuid dtype is an extension");
                };
                let bytes: Vec<Scalar> = (0..16)
                    .map(|k| Scalar::primitive(((i * 16 + k) % 251) as u8, nn))
                    .collect();
                Ok(Scalar::extension_ref(
                    ext.clone(),
                    Scalar::fixed_size_list(
                        Arc::new(DType::Primitive(PType::U8, nn)),
                        bytes,
                        nn,
                    ),
                ))
            })?,
        ));
    }

    Ok(StructArray::try_from_iter(columns)?.into_array())
}

/// What each edition's file must contain, checked against the ids walked back out of the bytes.
///
/// A miss lands in `missing_expected_array_ids`, which the generator prints and the manifest
/// records — the mechanism that turns "the writer accepted the target" into "the file demonstrates
/// what the edition froze". The lists name only members of that edition's own `[added]` block in
/// spec/editions/, and only the ones the default pipeline actually elects; the rest are named in
/// [`edition_unelected`] with the reason, rather than asserted and quietly missed.
fn edition_expectations(name: &str) -> (Vec<String>, Vec<String>) {
    let arrays: &[&str] = match name {
        "core2025.05.0" => &[
            "vortex.primitive",
            "vortex.constant",
            "vortex.dict",
            "vortex.fsst",
            "fastlanes.bitpacked",
            "fastlanes.for",
        ],
        "core2025.06.0" => &["vortex.sequence"],
        "core2025.10.0" => &["vortex.fixed_size_list"],
        "core2026.08.0" => &[],
        "core2026.08.1" => &["vortex.onpair"],
        "core2026.08.2" => &["vortex.map", "vortex.listview"],
        "core2026.08.3" => &["vortex.ext", "vortex.map"],
        _ => &[],
    };
    let layouts: &[&str] = match name {
        "core2026.08.0" | "core2026.08.1" | "core2026.08.2" | "core2026.08.3" => &["vortex.zoned"],
        _ => &[],
    };
    (
        arrays.iter().map(|s| (*s).to_string()).collect(),
        layouts.iter().map(|s| (*s).to_string()).collect(),
    )
}

/// Members of an edition's `[added]` block that its file does **not** contain, and why.
///
/// Every one of these is covered by a forced `encodings/*` file; what is missing here is only the
/// demonstration that the *default pipeline* elects it under that edition, which is a property of
/// the compressor's cost model rather than of the format.
fn edition_unelected(name: &str) -> Option<&'static str> {
    match name {
        "core2025.05.0" => Some(
            "vortex.alp, vortex.alprd, vortex.bytebool, vortex.chunked, vortex.datetimeparts, \
             vortex.decimal, vortex.decimal_byte_parts, vortex.ext, vortex.list, vortex.null, \
             vortex.runend, vortex.varbin, vortex.varbinview and vortex.zigzag are members of \
             this edition that the default pipeline does not elect for this table; each has its \
             own forced encodings/* file",
        ),
        "core2025.06.0" => Some(
            "vortex.pco and vortex.zstd are members of this edition that the compressor does not \
             elect over the cheaper alternatives; both have forced encodings/* files",
        ),
        "core2025.10.0" => Some(
            "fastlanes.rle, vortex.listview and vortex.masked are members of this edition that \
             the default pipeline does not elect: the run-heavy column wins with vortex.runend, a \
             DType::List column is written as the Arrow-offsets vortex.list rather than \
             vortex.listview, and a nullable column carries its validity as a child rather than a \
             vortex.masked wrapper. All three have forced encodings/* files",
        ),
        "core2026.08.3" => Some(
            "vortex.variant and vortex.parquet.variant are members of this edition that no \
             compressor elects — they are constructed, not chosen — and have forced encodings/* \
             files. The vortex.uuid DTYPE this edition adds is present, in extension_dtype_ids",
        ),
        _ => None,
    }
}

fn edition_entries() -> Vec<Entry> {
    EDITIONS
        .iter()
        .enumerate()
        .map(|(index, (name, _))| {
            // The `vortex.zoned` layout and its aggregates join core at `core2026.08.0`, and the
            // 0.86.1 writer never emits the legacy `vortex.stats` layout that older editions
            // would need. So editions before that can only be written zone-map-free, with the
            // compressor restricted by hand to the edition's own encodings.
            let pre_zoned = name.starts_with("core2025");
            let mut e = entry(
                format!("editions/{name}"),
                "E",
                format!("a table shaped to elect what {name} adds, written targeting {name}"),
                Box::new(move |_session, _rng| {
                    Ok(Build {
                        array: edition_table(index, 4096)?,
                        spec: if pre_zoned {
                            WriteSpec {
                                strategy: StrategyKind::EditionSafe,
                                file_statistics: false,
                                ..WriteSpec::default()
                            }
                        } else {
                            WriteSpec::default()
                        },
                    })
                }),
            );
            e.edition = Some(name);
            let (arrays, layouts) = edition_expectations(name);
            e.expected_array_ids = arrays;
            e.expected_layout_ids = layouts;
            e.notes.push(
                "columns are cumulative: this file carries every earlier edition's shapes plus \
                 the ones this edition adds"
                    .into(),
            );
            if let Some(unelected) = edition_unelected(name) {
                e.notes.push(unelected.to_string());
            }
            if pre_zoned {
                e.notes.push(
                    "no zone maps: vortex.zoned joins core at core2026.08.0 and the 0.86.1 writer \
                     never emits the legacy vortex.stats layout that this edition would allow"
                        .into(),
                );
                e.notes.push(
                    "file statistics disabled: their aggregates are not permitted by this edition"
                        .into(),
                );
            }
            e
        })
        .collect()
}

// --- dimensions this release cannot produce -------------------------------------------------

/// Corpus dimensions docs/04-conformance.md §3 asks for that Vortex 0.86.1 cannot write.
///
/// Each one was checked against the crate source, not guessed. An honest gap is useful; a silent
/// one is a hole in the conformance claim.
fn static_skips() -> Vec<SkipRecord> {
    vec![
        SkipRecord {
            id: "containers/zstd_compressed_segments".into(),
            dimension: "D".into(),
            description: "a file with Zstd-compressed segments".into(),
            reason:
                "The format reserves per-segment compression, but the 0.86.1 writer hard-codes it \
                 off: `PostscriptSegment::write_flatbuffer` passes `_compression: None` \
                 (vortex-file-0.86.1/src/footer/postscript.rs:251) and `FileLayout` passes \
                 `compression_specs: None` (footer/file_layout.rs:75). No public API sets either. \
                 `containers/zstd_arrays_in_segments` is the closest reachable variant: the \
                 segments hold `vortex.zstd` *arrays*, which is array-level, not segment-level."
                    .into(),
        },
        SkipRecord {
            id: "containers/lz4_compressed_buffer".into(),
            dimension: "D".into(),
            description: "a file with an LZ4-compressed buffer".into(),
            reason:
                "Same mechanism as the Zstd segment case, and additionally no LZ4 codec is \
                 registered anywhere in 0.86.1 (no `lz4` dependency in any vortex-* crate). This \
                 fixture has to be forged by hand or produced by a future release."
                    .into(),
        },
        SkipRecord {
            id: "containers/postscript_near_ceiling".into(),
            dimension: "D".into(),
            description: "a postscript near the 65527-byte ceiling".into(),
            reason:
                "The postscript holds four segment locators plus at most MAX_METADATA_SEGMENTS \
                 (16) metadata entries whose keys are capped at MAX_METADATA_KEY_BYTES (64) \
                 (vortex-file-0.86.1/src/footer/mod.rs:44,51) — a key budget of 1 KiB. The \
                 writer's own upper bound is therefore about two orders of magnitude below \
                 MAX_POSTSCRIPT_SIZE. `containers/postscript_max_metadata` records the largest \
                 postscript actually achievable; a near-ceiling file must be forged."
                    .into(),
        },
        SkipRecord {
            id: "containers/false_statistics".into(),
            dimension: "D".into(),
            description: "a file with deliberately false min/max statistics".into(),
            reason:
                "By construction: docs/04-conformance.md §3 already states this one cannot come \
                 from the Rust writer and must be forged. The forged-fixture set now exists — \
                 tests/Vorticity.Conformance/forged/, with its own manifest.json — but this \
                 particular fixture is not in it: a zone's min/max live inside a compressed, \
                 encoded stats array, so inverting one is a re-encode rather than a byte patch. \
                 `negative/unknown_encoding_id` is the one forged fixture this release ships."
                    .into(),
        },
        SkipRecord {
            id: "rows/*".into(),
            dimension: "F".into(),
            description:
                "byte-exact golden vectors for the row encoder of docs/06-row-encoding.md".into(),
            reason:
                "docs/04-conformance.md §7 assigns these to this crate, on the basis that a small \
                 binary here would encode a generated table with `vortex-row` and dump the bytes. \
                 There is no such crate: `cargo info vortex-row` reports it is not in the \
                 crates.io index, and no crate in the 0.86.1 release exposes a row-encoding API — \
                 `RowSortField`, `row_encode` and `RowEncoding` appear nowhere in the sources of \
                 vortex-0.86.1 or any vortex-* crate at that version. The row encoder therefore \
                 has no cross-implementation anchor in this release and its property tests remain \
                 self-consistent only. Re-check when a `vortex-row` crate is published; the \
                 fixture shape §7 asks for (the adversarial set plus a randomized table, with the \
                 crate version recorded) is unchanged."
                    .into(),
        },
        SkipRecord {
            id: "containers/chunked_layout_single_chunk".into(),
            dimension: "D".into(),
            description: "a `vortex.chunked` LAYOUT with exactly one chunk".into(),
            reason:
                "Unwritable: `ChunkedLayoutStrategy::write_stream` collapses a single-child layout \
                 into that child — `if child_layouts.len() == 1 { return child }` \
                 (vortex-layout-0.86.1/src/layouts/chunked/writer.rs:86) — so a one-chunk chunked \
                 layout cannot exist in a file this writer produces. The equivalent array shape IS \
                 in the corpus, as `encodings/chunked_one_chunk`, and exercises the same \
                 single-chunk row-offset translation."
                    .into(),
        },
        SkipRecord {
            id: "containers/chunked_layout_empty_chunk".into(),
            dimension: "D".into(),
            description: "a `vortex.chunked` LAYOUT containing a zero-row chunk".into(),
            reason:
                "Unwritable: the file writer filters empty chunks out of the write stream before \
                 any layout strategy sees them — `.try_filter(|chunk| ready(!chunk.is_empty()))` \
                 (vortex-file-0.86.1/src/writer.rs:270). Verified empirically: a 100/0/100 \
                 ChunkedArray written as a stream produces a two-child chunked layout. The \
                 equivalent array shape IS in the corpus, as `encodings/chunked_empty_chunks` \
                 (zero-row chunks first, middle and last), which exercises the same running-sum \
                 offset arithmetic."
                    .into(),
        },
        SkipRecord {
            id: "negative/unknown_encoding_id".into(),
            dimension: "D".into(),
            description:
                "a structurally valid file declaring an array encoding id that exists in no edition"
                    .into(),
            reason:
                "Not producible by any writer, by definition: the writer's per-kind allowlist \
                 rejects an unregistered id, and `disable_editions()` only widens the allowlist to \
                 ids the session has registered. docs/04-conformance.md §6 needs one for two \
                 tests — projecting the column must fail with the id and kind named, and scanning \
                 without projecting it must succeed, which is what locks in lazy component \
                 resolution. It is produced by byte-patching instead, and ships in \
                 tests/Vorticity.Conformance/forged/ with its own manifest.json; see \
                 `--forged` in tools/conformance-gen/README.md."
                    .into(),
        },
        SkipRecord {
            id: "layouts/vortex_stats".into(),
            dimension: "D".into(),
            description: "a file containing the legacy `vortex.stats` zone-map layout".into(),
            reason:
                "The vtable exists and readers handle it, but no writer path constructs it: over \
                 all of vortex-layout-0.86.1/src, `LegacyStats` appears only in session \
                 registration (session.rs:62) and in reader tests. `ZonedMetadata::metadata` even \
                 panics on the legacy schema (layouts/zoned/mod.rs:108). A `vortex.stats` file \
                 must come from an older Vortex release, which would break the single-version pin \
                 this corpus records."
                    .into(),
        },
        SkipRecord {
            id: "types/struct_flat_nullable, types/struct_nested_deep_nullable".into(),
            dimension: "A".into(),
            description: "a file whose ROOT dtype is a nullable struct".into(),
            reason:
                "Unwritable in 0.86.1 by any configuration. `VortexWriteOptions::write_internal` \
                 always calls `accumulate_stats`, which constructs a `FileStatsAccumulator` before \
                 looking at the requested statistics, and that constructor panics on a nullable \
                 top-level struct: \"FileStatsAccumulator temporarily does not support nullable \
                 top-level structs\" (vortex-layout-0.86.1/src/layouts/file_stats.rs:461). \
                 `with_file_statistics(vec![])` does not help — the accumulator is built either \
                 way. Nullable structs are still covered as non-root columns, by \
                 `types/struct_nested_deep_*`'s inner field `b`."
                    .into(),
        },
        SkipRecord {
            id: "types/union".into(),
            dimension: "A".into(),
            description: "a column with DType::Union".into(),
            reason:
                "`vortex.union` is a member of no core edition (spec/editions/*.toml lists 34 \
                 array ids for core2026.08.3 and union is not among them), so a conformant writer \
                 cannot emit one. The generic builder also refuses it outright \
                 (`todo!(\"TODO(connor)[Union]: unimplemented\")`, \
                 vortex-array-0.86.1/src/builders/mod.rs:462)."
                    .into(),
        },
        SkipRecord {
            id: "editions/preview2026.08.0".into(),
            dimension: "E".into(),
            description: "a file targeting the preview edition".into(),
            reason:
                "Preview editions are not frozen, so a file written against one carries no \
                 read-forever guarantee and has no place in an interoperability-regression \
                 corpus. docs/04-conformance.md §8 asks for frozen core editions only."
                    .into(),
        },
        SkipRecord {
            id: "editions/vortex_0_36_0_floor".into(),
            dimension: "E".into(),
            description: "files written by Vortex 0.36.0, the read-forever floor".into(),
            reason:
                "This crate is pinned to exactly 0.86.1, which is a conformance decision recorded \
                 in docs/04-conformance.md §3, not a dependency choice. Files from the 0.36.0 \
                 floor have to come from a separately pinned generator; `editions/core2025.05.0` \
                 is the closest this one can get (the same component set, written by a modern \
                 writer)."
                    .into(),
        },
    ]
}
