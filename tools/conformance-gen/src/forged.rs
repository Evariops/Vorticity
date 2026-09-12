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

/// Produce the forged fixture set next to the corpus. Returns the number of fixtures written.
pub fn write_forged(corpus: &Path, out: &Path) -> anyhow::Result<usize> {
    let source_path = corpus.join(SOURCE);
    if !source_path.exists() {
        // A filtered run may not have produced the base file. Not an error: the forged set is
        // regenerated on the next full run.
        return Ok(0);
    }

    let bytes = std::fs::read(&source_path)
        .with_context(|| format!("reading {}", source_path.display()))?;
    let source_sha256 = sha256_bytes(&bytes);

    debug_assert_eq!(FROM.len(), TO.len(), "the patch must not move any offset");
    let hits: Vec<usize> = bytes
        .windows(FROM.len())
        .enumerate()
        .filter(|(_, w)| *w == FROM)
        .map(|(i, _)| i)
        .collect();
    if hits.len() != 1 {
        bail!(
            "expected exactly one occurrence of {} in {}, found {} — the patch would be ambiguous",
            String::from_utf8_lossy(FROM),
            source_path.display(),
            hits.len()
        );
    }
    let offset = hits[0];

    let mut patched = bytes.clone();
    patched[offset..offset + TO.len()].copy_from_slice(TO);
    let sha256 = sha256_bytes(&patched);

    let rel = "negative/unknown_encoding_id.vortex";
    let path = out.join(rel);
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(&path, &patched).with_context(|| format!("writing {}", path.display()))?;

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
             These are NOT reference-implementation output: they are deliberately malformed or \
             out-of-scope files, and the Rust reader is expected to reject them exactly as a .NET \
             reader should. Each record states the source file, its hash, and the exact byte \
             patch, so the fixture can be re-derived rather than trusted.",
        "files": [{
            "id": "negative/unknown_encoding_id",
            "path": rel,
            "description":
                "a structurally valid Vortex file whose array_specs declares `vortex.unknown01`, \
                 an encoding id registered in no edition and by no implementation",
            "source": SOURCE,
            "source_sha256": source_sha256,
            "patch": {
                "offset": offset,
                "length": FROM.len(),
                "from": String::from_utf8_lossy(FROM),
                "to": String::from_utf8_lossy(TO),
                "why_same_length":
                    "the id is a length-prefixed flatbuffer string; an equal-length overwrite \
                     keeps every offset in the file valid, and Vortex checksums nothing",
            },
            "sha256": sha256,
            "size_bytes": patched.len(),
            "row_count": 4096,
            "columns": { "ints": "the patched column", "strs": "unaffected, vortex.varbinview" },
            "expectations": [
                "opening the file and reading its dtype and layout tree succeeds: an unknown id \
                 in array_specs is not itself an error",
                "scanning with a projection of only `strs` succeeds and returns the same values \
                 as containers/uncompressed_canonical — this is the lazy-component-resolution \
                 test of docs/04-conformance.md §6",
                "scanning with `ints` projected fails with VortexUnsupportedException, and the \
                 message names both the id `vortex.unknown01` and the component kind `array`"
            ],
        }],
    });

    let manifest_path = out.join("manifest.json");
    std::fs::write(&manifest_path, serde_json::to_string_pretty(&manifest)?)
        .with_context(|| format!("writing {}", manifest_path.display()))?;

    Ok(1)
}
