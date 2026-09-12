//! Compiled, runnable proof for the write-control answers in API-NOTES.md.
//!
//! `cargo run --release -j 6 --example api_experiments`
//!
//! Every experiment writes a real file into a temp dir, reads it back, and reports the layout
//! encoding ids and array encoding ids that ended up in the bytes.

use std::collections::BTreeSet;
use std::path::Path;
use std::path::PathBuf;
use std::sync::Arc;

use vortex::VortexSessionDefault;
use vortex::array::ArrayId;
use vortex::array::ArrayRef;
use vortex::array::IntoArray;
use vortex::array::arrays::ChunkedArray;
use vortex::array::arrays::DictArray;
use vortex::array::arrays::PrimitiveArray;
use vortex::array::arrays::StructArray;
use vortex::array::arrays::VarBinViewArray;
use vortex::array::serde::SerializedArray;
use vortex::array::validity::Validity;
use vortex::buffer::Buffer;
use vortex::compressor::BtrBlocksCompressorBuilder;
use vortex::editions::CORE_2025_05_0;
use vortex::editions::CORE_2026_08_1;
use vortex::editions::CORE_2026_08_3;
use vortex::editions::ComponentKind;
use vortex::editions::EditionSessionExt;
use vortex::error::VortexResult;
use vortex::file::OpenOptionsSessionExt;
use vortex::file::VortexFile;
use vortex::file::VortexWriteOptions;
use vortex::file::WriteOptionsSessionExt;
use vortex::file::WriteStrategyBuilder;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::io::std_file::FileWrite;
use vortex::layout::LayoutStrategy;
use vortex::layout::layouts::chunked::writer::ChunkedLayoutStrategy;
use vortex::layout::layouts::collect::CollectStrategy;
use vortex::layout::layouts::compressed::CompressingStrategy;
use vortex::layout::layouts::flat::Flat;
use vortex::layout::layouts::flat::writer::FlatLayoutStrategy;
use vortex::layout::layouts::table::TableStrategy;
use vortex::array::session::ArraySessionExt;
use vortex::session::VortexSession;
use vortex::utils::aliases::hash_set::HashSet;
use vortex_btrblocks::SchemeExt;
use vortex_btrblocks::schemes::integer::FoRScheme;
use vortex_btrblocks::schemes::integer::IntDictScheme;
use vortex_btrblocks::schemes::string::StringDictScheme;

fn out_dir() -> PathBuf {
    let dir = std::env::temp_dir().join("conformance-gen-experiments");
    std::fs::create_dir_all(&dir).expect("create temp dir");
    dir
}

fn main() -> anyhow::Result<()> {
    // 1. Baseline: the default write options / default strategy.
    run("01-default", |s| s.write_options(), sample_struct)?;

    // 2. Completely uncompressed: a BtrBlocks builder with no schemes registered at all.
    run(
        "02-uncompressed",
        |s| {
            s.write_options().with_strategy(
                WriteStrategyBuilder::default()
                    .with_btrblocks_builder(BtrBlocksCompressorBuilder::empty())
                    .build(),
            )
        },
        sample_struct,
    )?;

    // 2b. Truly canonical: no compression, no zone maps, no file statistics.
    run(
        "02b-uncompressed-no-zonemaps",
        |s| {
            s.write_options()
                .with_file_statistics(vec![])
                .with_strategy(canonical_strategy())
        },
        sample_struct,
    )?;

    // 3a. Encoding allowlist by ARRAY ENCODING ID (no vortex-btrblocks dependency needed).
    //     Only schemes whose produced encodings are all in the set survive.
    run(
        "03a-allowlist-for-bitpacked",
        |s| {
            let allowed: HashSet<ArrayId> = ["fastlanes.for", "fastlanes.bitpacked"]
                .into_iter()
                .map(ArrayId::new)
                .collect();
            s.write_options().with_strategy(
                WriteStrategyBuilder::default()
                    .with_btrblocks_builder(
                        BtrBlocksCompressorBuilder::default().retain_allowed_encodings(&allowed),
                    )
                    .build(),
            )
        },
        sample_struct,
    )?;

    // 3b. Scheme-level control: start empty, add exactly one scheme. Requires a direct
    //     `vortex-btrblocks` dependency because `SchemeId` is opaque outside that crate.
    run(
        "03b-only-for-scheme",
        |s| {
            s.write_options().with_strategy(
                WriteStrategyBuilder::default()
                    .with_btrblocks_builder(
                        BtrBlocksCompressorBuilder::empty().with_new_scheme(&FoRScheme),
                    )
                    .build(),
            )
        },
        sample_struct,
    )?;

    // 3c. Subtractive: drop dictionary schemes from the default set.
    run(
        "03c-no-dict-schemes",
        |s| {
            s.write_options().with_strategy(
                WriteStrategyBuilder::default()
                    .with_btrblocks_builder(
                        BtrBlocksCompressorBuilder::default()
                            .exclude_schemes([IntDictScheme.id(), StringDictScheme.id()]),
                    )
                    .build(),
            )
        },
        sample_struct,
    )?;

    // 4. Row block size: 1024 rows per block instead of the 8192 default.
    run(
        "04-row-block-1024",
        |s| {
            s.write_options().with_strategy(
                WriteStrategyBuilder::default()
                    .with_row_block_size(1024)
                    .with_data_block_target_bytes(None)
                    .build(),
            )
        },
        sample_struct,
    )?;

    // 4b. Row block size alone, leaving the 1 MiB coalescing target in place.
    run(
        "04b-row-block-1024-with-default-coalescing",
        |s| {
            s.write_options().with_strategy(
                WriteStrategyBuilder::default()
                    .with_row_block_size(1024)
                    .build(),
            )
        },
        sample_struct,
    )?;

    // 5. Zone maps OFF + file statistics OFF.
    run(
        "05-no-zonemaps",
        |s| {
            s.write_options()
                .with_file_statistics(vec![])
                .with_strategy(no_zonemap_strategy())
        },
        sample_struct,
    )?;

    // 6. Force an exact encoding: pre-encode the array, then write it verbatim.
    run(
        "06-forced-dict",
        |s| {
            s.write_options()
                .with_file_statistics(vec![])
                .with_strategy(verbatim_strategy())
        },
        forced_dict_column,
    )?;

    // 6b. The same pre-encoded array through the DEFAULT strategy, to show the difference.
    run("06b-forced-dict-default-strategy", |s| s.write_options(), forced_dict_column)?;

    // 7. Target edition.
    for edition in [CORE_2025_05_0, CORE_2026_08_1, CORE_2026_08_3] {
        block_on(|handle| async move {
            let session = VortexSession::default().with_handle(handle);
            session.enable_edition(edition)?;
            println!(
                "-- edition {edition}: {} array ids, {} layout ids, {} dtype ids, {} aggregates",
                session.enabled_component_ids(ComponentKind::Array).len(),
                session.enabled_component_ids(ComponentKind::Layout).len(),
                session.enabled_component_ids(ComponentKind::DType).len(),
                session.enabled_component_ids(ComponentKind::Aggregate).len(),
            );

            let path = out_dir().join(format!("07-edition-{edition}-default.vortex"));
            match write_with(
                &session,
                &path,
                session.write_options(),
                sample_struct(&session)?,
            )
            .await
            {
                Ok(()) => {
                    print!("   default strategy: ");
                    report(&session, &path).await?
                }
                Err(e) => println!("   default strategy FAILED: {e}"),
            }

            let path = out_dir().join(format!("07-edition-{edition}-nozone.vortex"));
            match write_with(
                &session,
                &path,
                session
                    .write_options()
                    .with_file_statistics(vec![])
                    .with_strategy(no_zonemap_strategy()),
                sample_struct(&session)?,
            )
            .await
            {
                Ok(()) => {
                    print!("   no-zonemap strategy: ");
                    report(&session, &path).await?
                }
                Err(e) => println!("   no-zonemap strategy FAILED: {e}"),
            }
            Ok::<_, anyhow::Error>(())
        })?;
    }

    // 9. core2025.05.0 done properly: no zone maps AND a compressor restricted to that
    //    edition's encodings. Passing `with_strategy` opts out of the writer's automatic
    //    `retain_allowed_encodings`, so we have to reapply it ourselves.
    run(
        "09-edition-2025-05-0-working",
        |s| {
            s.enable_edition(CORE_2025_05_0).expect("registered edition");
            let allowed = allowed_encodings_for_session(s);
            s.write_options()
                .with_file_statistics(vec![])
                .with_strategy(edition_safe_strategy(&allowed))
        },
        sample_struct,
    )?;

    // 10. An allowlist wide enough for bit-packing to actually be selected: BitPackingScheme
    //     also produces patch encodings, and `retain_allowed_encodings` drops any scheme whose
    //     produced set is not fully covered.
    run(
        "10-allowlist-bitpacking-with-patches",
        |s| {
            let allowed: HashSet<ArrayId> = [
                "fastlanes.for",
                "fastlanes.bitpacked",
                "vortex.sparse",
                "vortex.patched",
                "vortex.constant",
                "vortex.primitive",
            ]
            .into_iter()
            .map(ArrayId::new)
            .collect();
            s.write_options().with_strategy(
                WriteStrategyBuilder::default()
                    .with_btrblocks_builder(
                        BtrBlocksCompressorBuilder::default().retain_allowed_encodings(&allowed),
                    )
                    .build(),
            )
        },
        sample_struct,
    )?;

    // 8. Edition enforcement off entirely.
    run(
        "08-editions-disabled",
        |s| s.write_options().disable_editions(),
        sample_struct,
    )?;

    // 12. A stream of chunks: `ChunkedArray::to_array_stream()` yields one stream item per
    //     chunk, and a strategy that does not repartition preserves those boundaries.
    run(
        "12-chunked-stream",
        |s| {
            s.write_options()
                .with_file_statistics(vec![])
                .with_strategy(verbatim_strategy())
        },
        chunked_column,
    )?;

    // 11. Array-level tree display for the physical array stored in one flat leaf.
    block_on(|handle| async move {
        let session = VortexSession::default().with_handle(handle);
        let file = session
            .open_options()
            .open_path(out_dir().join("06-forced-dict.vortex"))
            .await?;
        println!("-- 11-array-tree-display (06-forced-dict.vortex, first flat leaf)");
        for l in file.footer().layout().depth_first_traversal() {
            let l = l?;
            let Some(flat) = l.as_opt::<Flat>() else {
                continue;
            };
            let buffer = file.segment_source().request(flat.segment_id()).await?;
            let parts = SerializedArray::try_from(buffer)?;
            let array = parts.decode(
                flat.dtype(),
                usize::try_from(flat.row_count()).expect("row count fits"),
                flat.array_ctx(),
                &session,
            )?;
            println!("{}", array.tree_display());
            break;
        }
        Ok::<_, anyhow::Error>(())
    })?;

    Ok(())
}

// ------------------------------------------------------------------------------------------
// Harness
// ------------------------------------------------------------------------------------------

fn run(
    name: &str,
    opts: impl FnOnce(&VortexSession) -> VortexWriteOptions,
    data: impl FnOnce(&VortexSession) -> VortexResult<ArrayRef>,
) -> anyhow::Result<()> {
    block_on(|handle| async move {
        let session = VortexSession::default().with_handle(handle);
        let path = out_dir().join(format!("{name}.vortex"));
        println!("-- {name}");
        write_with(&session, &path, opts(&session), data(&session)?).await?;
        print!("   ");
        report(&session, &path).await?;
        Ok::<_, anyhow::Error>(())
    })
}

async fn write_with(
    session: &VortexSession,
    path: &Path,
    opts: VortexWriteOptions,
    array: ArrayRef,
) -> VortexResult<()> {
    let sink = FileWrite::create(path, session.handle()).await?;
    opts.write(sink, array.to_array_stream()).await?;
    Ok(())
}

async fn report(session: &VortexSession, path: &Path) -> VortexResult<()> {
    let file = session.open_options().open_path(path).await?;
    println!(
        "{} bytes, {} rows",
        std::fs::metadata(path).map(|m| m.len()).unwrap_or(0),
        file.row_count()
    );
    println!("     layout ids: {:?}", layout_ids(&file)?);
    println!("     array  ids: {:?}", array_ids(&file).await?);
    Ok(())
}

fn layout_ids(file: &VortexFile) -> VortexResult<BTreeSet<String>> {
    let mut ids = BTreeSet::new();
    for l in file.footer().layout().depth_first_traversal() {
        ids.insert(l?.encoding_id().to_string());
    }
    Ok(ids)
}

async fn array_ids(file: &VortexFile) -> VortexResult<BTreeSet<String>> {
    let mut ids = BTreeSet::new();
    let source = file.segment_source();
    for l in file.footer().layout().depth_first_traversal() {
        let l = l?;
        let Some(flat) = l.as_opt::<Flat>() else {
            continue;
        };
        let ctx = flat.array_ctx().clone();
        let serialized = match flat.array_tree() {
            Some(tree) => SerializedArray::from_array_tree(tree.clone())?,
            None => SerializedArray::try_from(source.request(flat.segment_id()).await?)?,
        };
        walk(&serialized, &ctx, &mut ids);
    }
    Ok(ids)
}

fn walk(
    a: &SerializedArray,
    ctx: &vortex::session::registry::ReadContext,
    out: &mut BTreeSet<String>,
) {
    out.insert(
        ctx.resolve(a.encoding_id())
            .map(|i| i.to_string())
            .unwrap_or_else(|| format!("<unresolved {}>", a.encoding_id())),
    );
    for i in 0..a.nchildren() {
        walk(&a.child(i), ctx, out);
    }
}

// ------------------------------------------------------------------------------------------
// Strategies
// ------------------------------------------------------------------------------------------

/// The default pipeline minus `ZonedStrategy`: struct split, compress, chunk, flat.
fn no_zonemap_strategy() -> Arc<dyn LayoutStrategy> {
    let flat: Arc<dyn LayoutStrategy> = Arc::new(FlatLayoutStrategy::default());
    let chunked = ChunkedLayoutStrategy::new(Arc::clone(&flat));
    let compressing =
        CompressingStrategy::new(chunked, BtrBlocksCompressorBuilder::default().build());
    let validity = CollectStrategy::new(Arc::clone(&flat));
    Arc::new(TableStrategy::new(
        Arc::new(validity),
        Arc::new(compressing),
    ))
}

/// The set of in-memory array encodings the session's enabled editions permit, resolved the same
/// way `vortex_file::writer::new_array_context` does: serialized id -> registry -> plugin id.
fn allowed_encodings_for_session(session: &VortexSession) -> HashSet<ArrayId> {
    let arrays = session.arrays();
    session
        .enabled_component_ids(ComponentKind::Array)
        .iter()
        .filter_map(|serialized_id| arrays.registry().get(serialized_id))
        .map(|plugin| plugin.id())
        .collect()
}

/// No zone maps, and the data compressor restricted to `allowed`.
fn edition_safe_strategy(allowed: &HashSet<ArrayId>) -> Arc<dyn LayoutStrategy> {
    let flat: Arc<dyn LayoutStrategy> = Arc::new(FlatLayoutStrategy::default());
    let chunked = ChunkedLayoutStrategy::new(Arc::clone(&flat));
    let compressing = CompressingStrategy::new(
        chunked,
        BtrBlocksCompressorBuilder::default()
            .retain_allowed_encodings(allowed)
            .build(),
    );
    let validity = CollectStrategy::new(Arc::clone(&flat));
    Arc::new(TableStrategy::new(
        Arc::new(validity),
        Arc::new(compressing),
    ))
}

/// No compression at all, no zone maps: only canonical encodings reach the file.
fn canonical_strategy() -> Arc<dyn LayoutStrategy> {
    let flat: Arc<dyn LayoutStrategy> = Arc::new(FlatLayoutStrategy::default());
    let chunked = ChunkedLayoutStrategy::new(Arc::clone(&flat));
    let compressing =
        CompressingStrategy::new(chunked, BtrBlocksCompressorBuilder::empty().build());
    let validity = CollectStrategy::new(Arc::clone(&flat));
    Arc::new(TableStrategy::new(
        Arc::new(validity),
        Arc::new(compressing),
    ))
}

/// Writes each chunk exactly as handed in: no repartition, no canonicalization, no compression,
/// no zone maps. Whatever encoding the array already carries is what lands in the file.
fn verbatim_strategy() -> Arc<dyn LayoutStrategy> {
    let flat: Arc<dyn LayoutStrategy> = Arc::new(FlatLayoutStrategy::default());
    Arc::new(ChunkedLayoutStrategy::new(flat))
}

// ------------------------------------------------------------------------------------------
// Data
// ------------------------------------------------------------------------------------------

fn sample_struct(_session: &VortexSession) -> VortexResult<ArrayRef> {
    let ints = PrimitiveArray::new(
        (0..4096i64).map(|i| 1_000_000 + (i * 7) % 977).collect::<Buffer<i64>>(),
        Validity::NonNullable,
    );
    let strs = VarBinViewArray::from_iter_str((0..4096).map(|i| format!("value-{}", i % 4)));
    Ok(StructArray::try_from_iter([
        ("ints", ints.into_array()),
        ("strs", strs.into_array()),
    ])?
    .into_array())
}

/// Three chunks of i64, handed to the writer as one `ChunkedArray`.
fn chunked_column(_session: &VortexSession) -> VortexResult<ArrayRef> {
    let chunks: Vec<ArrayRef> = (0..3)
        .map(|c| {
            PrimitiveArray::new(
                (0..100i64).map(|i| c * 1000 + i).collect::<Buffer<i64>>(),
                Validity::NonNullable,
            )
            .into_array()
        })
        .collect();
    let dtype = chunks[0].dtype().clone();
    Ok(ChunkedArray::try_new(chunks, dtype)?.into_array())
}

/// A column that already IS a `vortex.dict` array before it ever reaches the writer.
fn forced_dict_column(_session: &VortexSession) -> VortexResult<ArrayRef> {
    let codes = PrimitiveArray::new(
        (0..64u32).map(|i| i % 4).collect::<Buffer<u32>>(),
        Validity::NonNullable,
    );
    let values = VarBinViewArray::from_iter_str(["a", "bb", "ccc", "dddd"]);
    Ok(DictArray::try_new(codes.into_array(), values.into_array())?.into_array())
}
