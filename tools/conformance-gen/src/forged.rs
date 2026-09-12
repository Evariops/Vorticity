//! Fixtures no conformant writer can produce, made by byte-patching one that it can.
//!
//! docs/04-conformance.md §6 needs a structurally valid file that declares an array encoding id
//! belonging to no edition, and uses it for two tests:
//!
//! 1. projecting the affected column fails with `VortexUnsupportedException`, naming the id and
//!    the component kind;
//! 2. scanning **without** projecting that column succeeds — which is what locks in lazy
//!    component resolution, and is the half a reader is most likely to get wrong by resolving
//!    every declared id up front.
//!
//! The Rust writer cannot emit one by construction: its per-kind allowlist rejects an
//! unregistered id, and `disable_editions()` only widens the allowlist to ids the session has
//! registered. So the file is produced here, from a corpus file, by replacing one id string in
//! the footer's `array_specs` with an equal-length name that is registered nowhere.
//!
//! Equal length matters: the id is a length-prefixed flatbuffer string, so an in-place
//! same-length overwrite leaves every offset in the file valid. Nothing in a Vortex file is
//! checksummed, so no other byte needs to change. The patch is recorded in the forged manifest —
//! source file, source hash, byte offset, before and after — so the fixture can be re-derived and
//! audited rather than taken on trust.
//!
//! The second fixture is not negative at all, and the same mechanism produces it. `vortex.stats` is
//! the legacy zone map of editions core2025.05.0 through core2025.10.0, read upstream by the same
//! `ZonedReader` as `vortex.zoned` and structurally identical to it — data at child 0. Vortex 0.86.1
//! has no writer path that emits one, so no corpus file contains one, so a reader can support it and
//! never once be exercised. Both ids are twelve bytes, which makes the equal-length patch above
//! sufficient: the result is a file whose values must read back IDENTICALLY to its source through a
//! different reader. That is a stronger oracle than a hand-built fixture would have, because the
//! expected values are not asserted from a manifest — they are whatever the unpatched file says.

use std::path::Path;

use anyhow::Context;
use anyhow::bail;
use serde_json::json;

use crate::manifest::VORTEX_VERSION;
use crate::util::sha256_bytes;

/// The corpus file the forged fixture is derived from.
///
/// Two columns, both written through the uncompressed-canonical pipeline: `ints` is
/// `vortex.primitive` and `strs` is `vortex.varbinview`, so renaming the primitive id makes
/// exactly one of the two columns unreadable and leaves the other intact — which is what the
/// "scan without projecting it" half of the test needs.
const SOURCE: &str = "containers/uncompressed_canonical.vortex";

/// The id to rename, and its replacement. Both are 16 bytes.
const FROM: &[u8] = b"vortex.primitive";
const TO: &[u8] = b"vortex.unknown01";

/// One byte-patched fixture: which file, which id, and what the resulting file is for.
struct Forge {
    id: &'static str,
    rel: &'static str,
    source: &'static str,
    from: &'static [u8],
    to: &'static [u8],
    description: &'static str,
    row_count: u64,
    expectations: &'static [&'static str],
}

const FORGES: &[Forge] = &[
    Forge {
        id: "negative/unknown_encoding_id",
        rel: "negative/unknown_encoding_id.vortex",
        source: SOURCE,
        from: FROM,
        to: TO,
        description:
            "a structurally valid Vortex file whose array_specs declares `vortex.unknown01`, an \
             encoding id registered in no edition and by no implementation",
        row_count: 4096,
        expectations: &[
            "opening the file and reading its dtype and layout tree succeeds: an unknown id in \
             array_specs is not itself an error",
            "scanning with a projection of only `strs` succeeds and returns the same values as \
             containers/uncompressed_canonical — this is the lazy-component-resolution test of \
             docs/04-conformance.md §6",
            "scanning with `ints` projected fails with VortexUnsupportedException, and the message \
             names both the id `vortex.unknown01` and the component kind `array`",
        ],
    },
    Forge {
        id: "legacy/stats_layout",
        rel: "legacy/stats_layout.vortex",
        source: "containers/zoned_many_zones_nulls.vortex",
        from: b"vortex.zoned",
        to: b"vortex.stats",
        description:
            "the canonical zone-map file with its layout id renamed to `vortex.stats`, the legacy \
             zone map of editions core2025.05.0 through core2025.10.0 that Vortex 0.86.1 has no \
             writer path for. Both vtables take (data, zones) with data at child 0 — \
             vortex-layout-0.86.1/src/layouts/zoned/mod.rs asserts exactly that for each — so a \
             structural reader must return the source's values through a code path nothing else \
             exercises. PARTIAL BY CONSTRUCTION: child 1 still carries the zoned zone map, not a \
             legacy stats table, because only the twelve-byte id was patched. That is enough to \
             exercise a reader that treats vortex.stats as structural only and never parses the \
             child, and it is NOT a conformant legacy file — a reader that parsed the legacy stats \
             table would find the wrong schema there",
        row_count: 65536,
        expectations: &[
            "opening the file succeeds and the layout tree reports `vortex.stats` where the source \
             reports `vortex.zoned`, with the same layout count and the same dtype",
            "a full scan returns EXACTLY the values containers/zoned_many_zones_nulls returns: the \
             two files differ in one twelve-byte id and in nothing else",
            "a filtered scan returns the same ROWS as the source: vortex.stats reports pruning \
             unavailable, so the filter is evaluated everywhere rather than skipped by zone, and a \
             reader that pruned on a zone map it had not parsed would drop rows here",
            "NOT asserted, because the fixture cannot support it: that legacy stats-table metadata \
             parses. Child 1 holds a zoned zone map",
        ],
    },
];

/// Produce the forged fixture set next to the corpus. Returns the number of fixtures written.
pub fn write_forged(corpus: &Path, out: &Path) -> anyhow::Result<usize> {
    let mut records = Vec::new();

    for forge in FORGES {
        let source_path = corpus.join(forge.source);
        if !source_path.exists() {
            // A filtered run may not have produced the base file. Not an error: the forged set is
            // regenerated on the next full run.
            continue;
        }

        let bytes = std::fs::read(&source_path)
            .with_context(|| format!("reading {}", source_path.display()))?;
        let source_sha256 = sha256_bytes(&bytes);

        debug_assert_eq!(forge.from.len(), forge.to.len(), "the patch must not move any offset");
        let hits: Vec<usize> = bytes
            .windows(forge.from.len())
            .enumerate()
            .filter(|(_, w)| *w == forge.from)
            .map(|(i, _)| i)
            .collect();
        if hits.len() != 1 {
            bail!(
                "expected exactly one occurrence of {} in {}, found {} — the patch would be ambiguous",
                String::from_utf8_lossy(forge.from),
                source_path.display(),
                hits.len()
            );
        }
        let offset = hits[0];

        let mut patched = bytes.clone();
        patched[offset..offset + forge.to.len()].copy_from_slice(forge.to);
        let sha256 = sha256_bytes(&patched);

        let path = out.join(forge.rel);
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        std::fs::write(&path, &patched).with_context(|| format!("writing {}", path.display()))?;

        records.push(json!({
            "id": forge.id,
            "path": forge.rel,
            "description": forge.description,
            "source": forge.source,
            "source_sha256": source_sha256,
            "patch": {
                "offset": offset,
                "length": forge.from.len(),
                "from": String::from_utf8_lossy(forge.from),
                "to": String::from_utf8_lossy(forge.to),
                "why_same_length":
                    "the id is a length-prefixed flatbuffer string; an equal-length overwrite \
                     keeps every offset in the file valid, and Vortex checksums nothing",
            },
            "sha256": sha256,
            "size_bytes": patched.len(),
            "row_count": forge.row_count,
            "expectations": forge.expectations,
        }));
    }

    let count = records.len();
    let manifest = json!({
        "format": "vortex-conformance-forged/1",
        "generator": {
            "name": env!("CARGO_PKG_NAME"),
            "version": env!("CARGO_PKG_VERSION"),
            "source": "tools/conformance-gen",
            "vortex_version": VORTEX_VERSION,
        },
        "about":
            "Fixtures that no conformant writer can produce, made by byte-patching a corpus file. \
             These are NOT reference-implementation output. Most are deliberately malformed or \
             out-of-scope files the Rust reader is expected to reject exactly as a .NET reader \
             should; `legacy/stats_layout` is the exception, a perfectly valid file in an encoding \
             0.86.1 can read but not write. Each record states the source file, its hash, and the \
             exact byte patch, so the fixture can be re-derived rather than trusted.",
        "files": records,
    });

    let manifest_path = out.join("manifest.json");
    std::fs::write(&manifest_path, serde_json::to_string_pretty(&manifest)?)
        .with_context(|| format!("writing {}", manifest_path.display()))?;

    Ok(count)
}
