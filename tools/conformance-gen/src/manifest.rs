//! `manifest.json` — what is in the corpus, and what is not.
//!
//! The corpus contract makes the manifest a load-bearing artifact, not documentation: the
//! **corpus coverage gate** ("the union of array_specs and layout_specs across the corpus must
//! cover every component we claim to support; a claimed-but-untested encoding fails the build")
//! is decided from [`Coverage`], and the honest-gap rule ("for each you cannot, record WHY")
//! is decided from [`SkipRecord`].
//!
//! The one thing a manifest must never do is flatter the corpus. Every id in
//! [`FileRecord::array_ids`] was read back out of the written bytes; nothing is asserted from the
//! configuration that produced it.

use std::collections::BTreeMap;
use std::collections::BTreeSet;

use serde::Deserialize;
use serde::Serialize;

/// The Vortex release this corpus is generated against. A bump is a corpus regeneration, not a
/// dependency update — see the pin comment in `Cargo.toml`.
pub const VORTEX_VERSION: &str = "0.86.1";

/// Bumped whenever the manifest shape changes in a way a consumer must notice.
pub const MANIFEST_FORMAT: &str = "vortex-conformance-corpus/2";

/// The array encodings the registry puts **in 1.0 scope** — its three component tables
/// (canonical and structural, compressed integer, float/string/temporal), and nothing else.
///
/// This is the list the corpus coverage gate runs against. The registry's late arrivals, five
/// encodings it first deferred to 1.1 and that were built inside 1.0 since, are NOT in it: they
/// are tracked separately in [`DEFERRED_ARRAYS`], so a miss among them does not fail the gate.
pub const CLAIMED_ARRAYS: &[&str] = &[
    // canonical and structural
    "vortex.bool",
    "vortex.chunked",
    "vortex.constant",
    "vortex.decimal",
    "vortex.ext",
    "vortex.fixed_size_list",
    "vortex.list",
    "vortex.listview",
    "vortex.masked",
    "vortex.null",
    "vortex.primitive",
    "vortex.struct",
    "vortex.varbin",
    "vortex.varbinview",
    // compressed integer
    "fastlanes.bitpacked",
    "fastlanes.for",
    "fastlanes.rle",
    "vortex.bytebool",
    "vortex.dict",
    "vortex.runend",
    "vortex.sequence",
    "vortex.sparse",
    "vortex.zigzag",
    // float, string, temporal
    "vortex.alp",
    "vortex.alprd",
    "vortex.datetimeparts",
    "vortex.decimal_byte_parts",
    "vortex.fsst",
    "vortex.onpair",
    "vortex.zstd",
];

/// The registry's late arrivals, first deferred to 1.1 and built inside 1.0 since. Not gating:
/// a miss here is not a build failure, although all five are in 1.0 scope and the corpus carries
/// each of them.
pub const DEFERRED_ARRAYS: &[&str] = &[
    "vortex.map",
    "vortex.parquet.variant",
    "vortex.pco",
    "vortex.variant",
    "vortex.zstd_buffers",
];

/// The layouts the registry claims.
pub const CLAIMED_LAYOUTS: &[&str] = &[
    "vortex.chunked",
    "vortex.dict",
    "vortex.flat",
    "vortex.stats",
    "vortex.struct",
    "vortex.zoned",
];

/// Layout ids that exist in 0.86.1 but belong to no core edition, and so are not in
/// the registry's layout table. `vortex.list` is reachable only under
/// `VORTEX_EXPERIMENTAL_LIST_LAYOUT=1`; a file containing one is a forward-compatibility fixture,
/// not a conformance target, exactly as `vortex.patched` is on the array side. Listed here so the
/// unclaimed-observed report stays a signal rather than a permanent known-noise line.
pub const EXPERIMENTAL_LAYOUTS: &[&str] = &["vortex.list"];

/// The extension dtypes the registry claims.
pub const CLAIMED_EXTENSION_DTYPES: &[&str] = &[
    "vortex.date",
    "vortex.time",
    "vortex.timestamp",
    "vortex.uuid",
];

/// The zone-map aggregates the registry claims.
pub const CLAIMED_AGGREGATES: &[&str] = &[
    "vortex.bounded_max",
    "vortex.bounded_min",
    "vortex.max",
    "vortex.min",
    "vortex.nan_count",
    "vortex.null_count",
];

/// Reference behaviours that look like corpus bugs and are not. Each was established by
/// cross-checking the sidecar's decoded values against the same file's footer statistics.
pub const CAVEATS: &[&str] = &[
    "The file-level `sum` statistic is NOT the IEEE sum on a float column that contains an \
     infinity. Vortex binds `Sum` with `NumericalAggregateOpts::skip_nans()` \
     (vortex-array-0.86.1/src/stats/expr.rs:53), and on a column holding both +Inf and -Inf it \
     records 0.0 (non-nullable) or -inf (nullable) while marking the value `exact`, where the \
     true sum is NaN. 40 corpus files are affected — every `types/f{16,32,64}_*` file with 1023 \
     rows or more, and all four `distributions/float_specials_*`. Float columns with no \
     infinity agree with an independent Kahan sum to the bit. Do not silently correct this in a reader, \
     and do not use file-level `sum` on float columns for anything load-bearing.",
    "A statistic whose `precision` is `inexact` is a BOUND, not a value. \
     `distributions/huge_string_r16` carries `min` truncated to 64 bytes of a 1.1 MB string, \
     flagged `inexact`; comparing it for equality against the decoded minimum fails by design. \
     The zone-map spelling of the same thing is `vortex.bounded_min(64)` / \
     `vortex.bounded_max(64)`, whose `unknown` field says the bound is not tight.",
    "`vortex.dict` keys float values by bit pattern, not by IEEE equality: \
     `distributions/float_specials_f64_*` dictionaries hold 15 distinct values even though \
     0.0 == -0.0 and NaN != NaN.",
    "An all-null column does not keep its values buffer. Vortex never serializes \
     `Validity::AllInvalid` as a child, and the write pipeline folds an all-null column to a \
     single `vortex.constant` node even with every compression scheme removed \
     (`BtrBlocksCompressorBuilder::empty`). The shape \"nullable array whose values buffer is \
     materialized and whose validity child is entirely zero\" is therefore reachable only by \
     writing the array verbatim; `containers/all_null_i64_explicit_validity_r1025` and \
     `encodings/masked_all_invalid` are the two files that have it.",
    "Zero-row chunks never reach a layout. The file writer filters them out of the write stream \
     before any layout strategy sees them (`vortex-file-0.86.1/src/writer.rs:270`), and \
     `ChunkedLayoutStrategy` collapses a single-child chunked layout into that child \
     (`vortex-layout-0.86.1/src/layouts/chunked/writer.rs:86`). So no `vortex.chunked` LAYOUT in \
     any Vortex file has one chunk or an empty one. Both shapes exist at the ARRAY level, in \
     `encodings/chunked_one_chunk` and `encodings/chunked_empty_chunks`.",
    "The row encoder of docs/06-row-encoding.md has NO golden vectors in this release. \
     docs/04-conformance.md §7 assigns them to this crate via a `vortex-row` dependency; no such \
     crate is published, and no crate in the 0.86.1 release exposes a row-encoding API. See the \
     `rows/*` entry in `skipped`. Until it exists, the row encoder is anchored only by \
     self-consistent property tests, which is exactly the failure mode docs/04-conformance.md \
     opens by describing.",
    "The footer's `array_specs` over-reports. The writer pre-populates the array context with \
     every id the enabled editions permit, for byte determinism \
     (vortex-file-0.86.1/src/writer.rs:387), so `array_specs` names ids no array in the file \
     uses — across this corpus its union is 40 ids against 36 actually serialized, including \
     `vortex.union` and `fastlanes.transposed_bool`, which appear in no array anywhere. \
     `array_ids` per file is the walked truth; `declared_array_ids` is the declared count.",
];

#[derive(Debug, Serialize)]
pub struct Manifest {
    pub format: &'static str,
    pub generator: Generator,
    pub determinism: Determinism,
    pub coverage: Coverage,
    /// Reference-implementation behaviours a consumer must not mistake for its own bug. The
    /// sidecars record what Vortex 0.86.1 actually wrote, including where that is surprising; a
    /// .NET test that "corrects" one of these will disagree with every real Vortex file.
    pub caveats: Vec<&'static str>,
    pub files: Vec<FileRecord>,
    /// Every corpus dimension the API would not produce, and why. An honest gap is useful; a
    /// silent one is a hole in the conformance claim.
    pub skipped: Vec<SkipRecord>,
}

#[derive(Debug, Serialize)]
pub struct Generator {
    pub name: &'static str,
    pub version: &'static str,
    pub source: &'static str,
    pub vortex_version: &'static str,
    /// The edition the writing session targets when an entry does not say otherwise.
    pub default_edition: String,
}

#[derive(Debug, Serialize)]
pub struct Determinism {
    pub prng: &'static str,
    pub master_seed: u64,
    /// How per-entry seeds are derived, so a single file can be regenerated in isolation and come
    /// out byte-identical.
    pub per_entry_seed: &'static str,
    pub notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FileRecord {
    pub id: String,
    /// A..E, the corpus plan's dimensions: types, encodings, distributions, containers, editions.
    pub dimension: String,
    pub description: String,
    pub path: String,
    pub sidecar: String,
    pub vortex_version: String,
    pub target_edition: String,
    pub row_count: u64,
    pub dtype: String,
    /// Array encoding ids ACTUALLY present, walked out of the serialized array trees. Not the
    /// footer's `array_specs`, which interns every id the enabled editions permit — 34 of them
    /// for `core2026.08.3` — regardless of use.
    pub array_ids: Vec<String>,
    /// `<encoding id>/c<child count>/b<buffer count>` for every distinct serialized array node in
    /// the file. The flat id set cannot see a shape: `fastlanes.bitpacked` with 2 children is
    /// patches without chunk offsets and with 3 is patches with them, and both spell the same id.
    /// The sidecar carries the full tree; this is the summary the coverage gate can index.
    pub array_node_shapes: Vec<String>,
    /// One entry per distinct `vortex.dict` codes shape, read out of the node's metadata:
    /// `codes=<ptype> codes_nonnull|codes_nullable|codes_nullability_absent`. The absent state is
    /// its own read path — `is_nullable_codes` was added after stabilisation
    /// (vortex-array-0.86.1/src/arrays/dict/array.rs:35).
    pub dict_nodes: Vec<String>,
    /// Largest `vortex.bool` bit offset in the file. The registry names the bit offset as
    /// the distinguishing feature of the encoding, and a reader that ignores it passes a corpus
    /// where the value is always 0.
    pub max_bool_bit_offset: u32,
    pub layout_ids: Vec<String>,
    /// How many ids the footer declares. Recorded because the gap between this and
    /// `array_ids.len()` is exactly the trap a naive coverage gate falls into.
    pub declared_array_ids: usize,
    pub declared_layout_ids: usize,
    /// Zone-map aggregate component ids present in the file's `vortex.zoned` layouts, normalized
    /// to the bare id the edition manifests use.
    pub aggregate_ids: Vec<String>,
    /// The same aggregates as the reader spells them, arguments included
    /// (`vortex.bounded_min(64)`). The argument is part of the zone-map contract, so it is kept.
    pub aggregate_specs: Vec<String>,
    /// Extension dtype ids reachable from the file dtype.
    pub extension_dtype_ids: Vec<String>,
    /// SHA-256 of the `.vortex` file.
    pub sha256: String,
    /// SHA-256 of the sidecar, so `--verify` covers the expected values too and not just the
    /// bytes they describe.
    pub sidecar_sha256: String,
    pub size_bytes: u64,
    pub file_format_version: u16,
    pub postscript_bytes: u16,
    pub has_dtype_segment: bool,
    pub has_file_statistics: bool,
    /// User metadata segment keys with their payload lengths, in stored order. The payload BYTES
    /// are in the sidecar's `metadata` line.
    pub metadata_segments: Vec<MetadataSegmentRecord>,
    pub zone_maps: usize,
    pub total_zones: usize,
    /// Zone maps in this file whose zones are all distinct from one another. A zone map whose
    /// zones carry identical bounds cannot prune anything.
    pub zone_maps_with_distinct_zones: usize,
    /// This file contains at least one Utf8 value that is not pure ASCII.
    pub utf8_non_ascii: bool,
    /// ...and at least one containing an embedded U+0000.
    pub utf8_embedded_nul: bool,
    pub null_counts: BTreeMap<String, u64>,
    /// Ids this entry set out to force. Empty for entries that make no such claim.
    pub expected_array_ids: Vec<String>,
    /// Ids from `expected_array_ids` that the file does **not** contain. A non-empty list means
    /// the forcing recipe did not work and the file proves nothing about those ids.
    pub missing_expected_array_ids: Vec<String>,
    pub seed: u64,
    /// Environment variables set in the writing process, when the variant needs them.
    pub env: BTreeMap<String, String>,
    pub notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MetadataSegmentRecord {
    pub key: String,
    pub len: usize,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SkipRecord {
    pub id: String,
    pub dimension: String,
    pub description: String,
    /// Why the corpus cannot contain this, in enough detail to re-check against a future release.
    pub reason: String,
}

#[derive(Debug, Serialize)]
pub struct Coverage {
    pub files: usize,
    pub total_bytes: u64,
    pub total_rows: u64,
    pub row_counts: Vec<usize>,
    pub editions: Vec<String>,
    pub arrays: CoverageSet,
    pub layouts: CoverageSet,
    pub extension_dtypes: CoverageSet,
    pub aggregates: CoverageSet,
    /// The registry's late arrivals, [`DEFERRED_ARRAYS`]. Informational: not part of the gate.
    pub deferred_arrays: CoverageSet,
    /// Ids observed that the registry does not claim, per component kind. Not an error —
    /// the writer is free to emit an in-memory-only id we chose not to list — but it must be
    /// visible. Applied to layouts, aggregates and extension dtypes as well as arrays: through
    /// format/1 it covered arrays only, which is why `vortex.list` sat in the corpus as a layout
    /// the registry does not list while the manifest declared full coverage.
    pub unclaimed_observed: UnclaimedObserved,
    /// One entry per [`SHAPE_CHECKS`] property, with whether the corpus satisfies it.
    pub shapes: Vec<ShapeCheck>,
}

#[derive(Debug, Serialize)]
pub struct ShapeCheck {
    pub name: &'static str,
    pub requirement: &'static str,
    pub satisfied: bool,
}

#[derive(Debug, Default, Serialize)]
pub struct UnclaimedObserved {
    pub arrays: Vec<String>,
    pub layouts: Vec<String>,
    pub aggregates: Vec<String>,
    pub extension_dtypes: Vec<String>,
}

impl UnclaimedObserved {
    pub fn is_empty(&self) -> bool {
        self.arrays.is_empty()
            && self.layouts.is_empty()
            && self.aggregates.is_empty()
            && self.extension_dtypes.is_empty()
    }
}

fn unlisted(observed: &BTreeSet<String>, listed: &[&[&str]]) -> Vec<String> {
    let listed: BTreeSet<&str> = listed.iter().flat_map(|s| s.iter().copied()).collect();
    observed
        .iter()
        .filter(|id| !listed.contains(id.as_str()))
        .cloned()
        .collect()
}

#[derive(Debug, Serialize)]
pub struct CoverageSet {
    pub claimed: usize,
    pub covered: Vec<String>,
    /// Claimed but never observed in any corpus file. This is what fails the coverage gate.
    pub missing: Vec<String>,
}

impl CoverageSet {
    pub fn build(claimed: &[&str], observed: &BTreeSet<String>) -> Self {
        let claimed_set: BTreeSet<&str> = claimed.iter().copied().collect();
        let covered: Vec<String> = claimed_set
            .iter()
            .filter(|id| observed.contains(**id))
            .map(|id| (*id).to_string())
            .collect();
        let missing: Vec<String> = claimed_set
            .iter()
            .filter(|id| !observed.contains(**id))
            .map(|id| (*id).to_string())
            .collect();
        Self {
            claimed: claimed.len(),
            covered,
            missing,
        }
    }
}

/// The **shape gate**: properties the corpus must have that a per-component id set cannot see.
///
/// The coverage gate answers "is every claimed component present somewhere". It passed on a corpus
/// where every bool bit offset was 0, every dict had u16 non-nullable codes, every ALP array had
/// patches with chunk offsets, every zone map had two zones with identical bounds, and every Utf8
/// value was ASCII. Each of those is a whole class of reader bug that the id set cannot express,
/// so each gets a named check here.
pub const SHAPE_CHECKS: &[(&str, &str)] = &[
    ("bool bit offset", "some vortex.bool array has a non-zero bit offset"),
    ("dict codes u8", "some vortex.dict has u8 codes"),
    ("dict codes u16", "some vortex.dict has u16 codes"),
    ("dict codes u64", "some vortex.dict has u64 codes"),
    ("dict nullable codes", "some vortex.dict has nullable codes"),
    ("alp without patches", "some vortex.alp has 1 child (no patch slots)"),
    ("alp patches, no chunk offsets", "some vortex.alp has 3 children"),
    ("alp patches with chunk offsets", "some vortex.alp has 4 children"),
    ("bitpacked patches, no chunk offsets", "some fastlanes.bitpacked has 2 children"),
    ("chunked array, one chunk", "some vortex.chunked array wraps a single chunk"),
    ("many distinct zones", "some file has >= 32 zones with pairwise distinct bounds"),
    ("unicode utf8", "some file has a non-ASCII Utf8 value"),
    ("embedded NUL in utf8", "some file has a Utf8 value containing U+0000"),
    ("empty metadata segment", "some file has a present-but-zero-length metadata segment"),
    ("decimal i8 storage", "some file's dtype uses decimal(2,1)"),
    ("decimal i16 storage", "some file's dtype uses decimal(4,2)"),
    ("decimal i256 storage", "some file's dtype uses decimal(40,10)"),
];

/// Evaluate [`SHAPE_CHECKS`] against the corpus. Returns one flag per check, in the same order.
pub fn shape_gate(files: &[FileRecord]) -> Vec<bool> {
    let any = |f: &dyn Fn(&FileRecord) -> bool| files.iter().any(f);
    let shape = |needle: &str| {
        files
            .iter()
            .any(|r| r.array_node_shapes.iter().any(|s| s == needle))
    };
    let dict = |needle: &str| {
        files
            .iter()
            .any(|r| r.dict_nodes.iter().any(|s| s.contains(needle)))
    };
    vec![
        any(&|r| r.max_bool_bit_offset > 0),
        dict("codes=u8"),
        dict("codes=u16"),
        dict("codes=u64"),
        dict("codes_nullable"),
        shape("vortex.alp/c1/b0"),
        shape("vortex.alp/c3/b0"),
        shape("vortex.alp/c4/b0"),
        shape("fastlanes.bitpacked/c2/b1"),
        any(&|r| r.id == "encodings/chunked_one_chunk" && r.array_ids.iter().any(|a| a == "vortex.chunked")),
        any(&|r| r.zone_maps_with_distinct_zones > 0 && r.total_zones >= 32),
        any(&|r| r.utf8_non_ascii),
        any(&|r| r.utf8_embedded_nul),
        any(&|r| r.metadata_segments.iter().any(|m| m.len == 0)),
        any(&|r| r.dtype.contains("decimal(2,1)")),
        any(&|r| r.dtype.contains("decimal(4,2)")),
        any(&|r| r.dtype.contains("decimal(40,10)")),
    ]
}

/// Fold the per-file records into the coverage summary the gate reads.
pub fn summarize(files: &[FileRecord]) -> Coverage {
    let mut arrays = BTreeSet::new();
    let mut layouts = BTreeSet::new();
    let mut exts = BTreeSet::new();
    let mut aggregates = BTreeSet::new();
    let mut editions = BTreeSet::new();
    let mut row_counts = BTreeSet::new();
    let mut total_bytes = 0u64;
    let mut total_rows = 0u64;

    for f in files {
        arrays.extend(f.array_ids.iter().cloned());
        layouts.extend(f.layout_ids.iter().cloned());
        exts.extend(f.extension_dtype_ids.iter().cloned());
        aggregates.extend(f.aggregate_ids.iter().cloned());
        editions.insert(f.target_edition.clone());
        row_counts.insert(f.row_count as usize);
        total_bytes += f.size_bytes;
        total_rows += f.row_count;
    }

    // "Unclaimed" means the registry lists it nowhere — deferred ids are listed, just not gating.
    let unclaimed_observed = UnclaimedObserved {
        arrays: unlisted(&arrays, &[CLAIMED_ARRAYS, DEFERRED_ARRAYS]),
        layouts: unlisted(&layouts, &[CLAIMED_LAYOUTS, EXPERIMENTAL_LAYOUTS]),
        aggregates: unlisted(&aggregates, &[CLAIMED_AGGREGATES]),
        extension_dtypes: unlisted(&exts, &[CLAIMED_EXTENSION_DTYPES]),
    };

    Coverage {
        files: files.len(),
        total_bytes,
        total_rows,
        row_counts: row_counts.into_iter().collect(),
        editions: editions.into_iter().collect(),
        arrays: CoverageSet::build(CLAIMED_ARRAYS, &arrays),
        layouts: CoverageSet::build(CLAIMED_LAYOUTS, &layouts),
        extension_dtypes: CoverageSet::build(CLAIMED_EXTENSION_DTYPES, &exts),
        aggregates: CoverageSet::build(CLAIMED_AGGREGATES, &aggregates),
        deferred_arrays: CoverageSet::build(DEFERRED_ARRAYS, &arrays),
        unclaimed_observed,
        shapes: SHAPE_CHECKS
            .iter()
            .zip(shape_gate(files))
            .map(|((name, requirement), satisfied)| ShapeCheck {
                name,
                requirement,
                satisfied,
            })
            .collect(),
    }
}
