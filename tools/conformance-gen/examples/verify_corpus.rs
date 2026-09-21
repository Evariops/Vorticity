//! Independent coverage verifier: re-derives the corpus coverage union straight from the written
//! footers, without reading `manifest.json` and without sharing the generator's extraction code.
//!
//! The corpus coverage gate is stated in terms of `array_specs` and `layout_specs`, so this
//! reports *both* readings and the gap between them:
//!
//! * `declared` — the footer's `array_specs`, i.e. `ReadContext::ids()` on each flat leaf. The
//!   writer pre-populates that list with every id the enabled editions permit
//!   (`vortex-file-0.86.1/src/writer.rs:387`), so it over-reports and cannot decide the gate.
//! * `actual` — the encoding ids reached by walking each leaf's serialized array tree.
//!
//! ```sh
//! cargo run --release --example verify_corpus -- <corpus-dir>
//! ```

use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::path::Path;
use std::path::PathBuf;

use vortex::VortexSessionDefault;
use vortex::array::serde::SerializedArray;
use vortex::editions::EditionSessionExt;
use vortex::file::OpenOptionsSessionExt;
use vortex::io::runtime::Handle;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::layout::Layout;
use vortex::layout::layouts::flat::Flat;
use vortex::layout::layouts::zoned::Zoned;
use vortex::session::VortexSession;

fn main() -> anyhow::Result<()> {
    let root = PathBuf::from(
        std::env::args()
            .nth(1)
            .unwrap_or_else(|| "../../tests/Vorticity.Conformance/corpus".to_string()),
    );

    let mut paths = Vec::new();
    collect(&root, &mut paths)?;
    paths.sort();
    eprintln!("verifying {} files under {}", paths.len(), root.display());

    block_on(|handle: Handle| async move {
        let session = VortexSession::default().with_handle(handle);
        // Everything the reader might meet: the corpus spans every frozen core edition, and one
        // file is written with edition enforcement off.
        for id in [
            vortex::editions::CORE_2026_08_3,
            vortex::editions::PREVIEW_2026_08_0,
        ] {
            let _ = session.enable_edition(id);
        }

        let mut declared_union: BTreeSet<String> = BTreeSet::new();
        let mut actual_union: BTreeSet<String> = BTreeSet::new();
        let mut layout_union: BTreeSet<String> = BTreeSet::new();
        let mut aggregate_union: BTreeSet<String> = BTreeSet::new();
        let mut per_file: BTreeMap<String, serde_json::Value> = BTreeMap::new();
        let mut failures: Vec<String> = Vec::new();

        for path in &paths {
            let rel = path
                .strip_prefix(&root)
                .unwrap_or(path)
                .to_string_lossy()
                .replace('\\', "/");
            let opened = session.open_options().open_path(path).await;
            let file = match opened {
                Ok(f) => f,
                Err(e) => {
                    // The one file written with `exclude_dtype` has no dtype segment and cannot be
                    // opened without the caller supplying it. Note it and move on.
                    failures.push(format!("{rel}: {e}"));
                    continue;
                }
            };

            let mut declared: BTreeSet<String> = BTreeSet::new();
            let mut actual: BTreeSet<String> = BTreeSet::new();
            let mut layouts: BTreeSet<String> = BTreeSet::new();
            let mut aggregates: BTreeSet<String> = BTreeSet::new();

            for layout in file.footer().layout().depth_first_traversal() {
                let layout = layout?;
                layouts.insert(layout.encoding_id().to_string());
                if let Some(zoned) = layout.as_opt::<Zoned>() {
                    for aggregate in zoned.present_aggregates().iter() {
                        let spec = aggregate.to_string();
                        aggregates
                            .insert(spec.split('(').next().unwrap_or(&spec).to_string());
                    }
                }
                let Some(flat) = layout.as_opt::<Flat>() else {
                    continue;
                };
                let ctx = flat.array_ctx();
                for id in ctx.ids() {
                    declared.insert(id.to_string());
                }
                let serialized = read_leaf(&file, flat).await?;
                walk(&serialized, ctx, &mut actual);
            }

            declared_union.extend(declared.iter().cloned());
            actual_union.extend(actual.iter().cloned());
            layout_union.extend(layouts.iter().cloned());
            aggregate_union.extend(aggregates.iter().cloned());

            per_file.insert(
                rel.trim_end_matches(".vortex").to_string(),
                serde_json::json!({
                    "declared": declared,
                    "actual": actual,
                    "layouts": layouts,
                    "aggregates": aggregates,
                }),
            );
        }

        let out = serde_json::json!({
            "files_read": per_file.len(),
            "files_unopenable": failures,
            "declared_union": declared_union,
            "actual_union": actual_union,
            "layout_union": layout_union,
            "aggregate_union": aggregate_union,
            "per_file": per_file,
        });
        println!("{}", serde_json::to_string(&out)?);
        Ok::<_, anyhow::Error>(())
    })
}

async fn read_leaf(
    file: &vortex::file::VortexFile,
    flat: &Layout<Flat>,
) -> anyhow::Result<SerializedArray> {
    let segment = file.segment_source().request(flat.segment_id()).await?;
    Ok(match flat.array_tree() {
        Some(tree) => SerializedArray::from_flatbuffer_and_segment(tree.clone(), segment)?,
        None => SerializedArray::try_from(segment)?,
    })
}

fn walk(
    array: &SerializedArray,
    ctx: &vortex::session::registry::ReadContext,
    out: &mut BTreeSet<String>,
) {
    out.insert(
        ctx.resolve(array.encoding_id())
            .map(|id| id.to_string())
            .unwrap_or_else(|| format!("<unresolved {}>", array.encoding_id())),
    );
    for i in 0..array.nchildren() {
        walk(&array.child(i), ctx, out);
    }
}

fn collect(dir: &Path, out: &mut Vec<PathBuf>) -> anyhow::Result<()> {
    for entry in std::fs::read_dir(dir)? {
        let path = entry?.path();
        if path.is_dir() {
            collect(&path, out)?;
        } else if path.extension().and_then(|e| e.to_str()) == Some("vortex") {
            out.push(path);
        }
    }
    Ok(())
}
