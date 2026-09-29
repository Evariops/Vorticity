//! Acceptance criterion 2: **any file written by Vorticity is read back correctly by
//! Vortex Rust**.
//!
//! This is the direction the golden corpus cannot test. A corpus proves we read what Rust wrote; a
//! round trip through our own reader proves only that our writer and our reader agree, and two
//! halves of one implementation can agree on the same mistake. The only way to find that mistake is
//! to hand our bytes to the reference and ask what it sees.
//!
//! The comparison is scalar by scalar against the ORIGINAL corpus file, read by the same Rust
//! reader in the same process. So the question is not "does this file parse" -- a file can parse and
//! still be wrong -- but "does Rust get the same values out of our file as it gets out of the
//! reference's". Anything less would pass on a writer that dropped a validity bitmap.
//!
//! ```sh
//! cargo run --release --example verify_written -- <original-corpus-dir> <written-dir>
//! ```
//!
//! Files are paired by their path relative to each root, so the .NET side decides what to write and
//! this decides nothing.

use std::collections::BTreeSet;
use std::path::Path;
use std::path::PathBuf;

use vortex::VortexSessionDefault;
use vortex::array::ArrayRef;
use vortex::array::Canonical;
use vortex::array::ExecutionCtx;
use vortex::array::IntoArray;
use vortex::array::VortexSessionExecute;
use vortex::array::arrays::Struct;
use vortex::array::arrays::struct_::StructArrayExt;
use vortex::array::expr::stats::Stat;
use vortex::array::stream::ArrayStreamExt;
use vortex::dtype::DType;
use vortex::expr::col;
use vortex::expr::eq;
use vortex::expr::gt;
use vortex::expr::lit;
use vortex::expr::lt;
use vortex::expr::stats::Precision;
use vortex::editions::EditionSessionExt;
use vortex::file::OpenOptionsSessionExt;
use vortex::io::runtime::Handle;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::scalar::Scalar;
use vortex::session::VortexSession;

fn main() -> anyhow::Result<()> {
    let mut args = std::env::args().skip(1);
    let original = PathBuf::from(
        args.next()
            .unwrap_or_else(|| "../../tests/Vorticity.Conformance/corpus".to_string()),
    );
    let written = PathBuf::from(
        args.next()
            .unwrap_or_else(|| "../../artifacts/written".to_string()),
    );

    let mut relative = BTreeSet::new();
    collect(&written, &written, &mut relative)?;
    eprintln!(
        "verifying {} files written under {} against {}",
        relative.len(),
        written.display(),
        original.display()
    );

    if relative.is_empty() {
        anyhow::bail!(
            "no .vortex files under {}; run the .NET side first",
            written.display()
        );
    }

    block_on(|handle: Handle| async move {
        let session = VortexSession::default().with_handle(handle);
        for id in [
            vortex::editions::CORE_2026_08_3,
            vortex::editions::PREVIEW_2026_08_0,
        ] {
            let _ = session.enable_edition(id);
        }

        let mut failures: Vec<String> = Vec::new();
        let mut skipped: Vec<String> = Vec::new();
        let mut rows_compared: u64 = 0;
        let mut files_compared: usize = 0;

        for rel in &relative {
            let ours = written.join(rel);
            let theirs = original.join(rel);
            if !theirs.exists() {
                failures.push(format!("{}: no original to compare against", rel.display()));
                continue;
            }

            // A FILE THE REFERENCE CANNOT READ ON ITS OWN SIDE IS NOT A DISAGREEMENT. The session
            // above pins two editions, and the corpus deliberately contains a file written with
            // editions OFF -- `experimental_patched_array_editions_off`, whose `vortex.patched`
            // belongs to no edition. Asking a pinned session to read it fails on the REFERENCE's
            // own bytes, before ours are looked at, and reporting that as "1 of 820 files
            // disagreed" said the opposite of what had happened for as long as anyone remembered
            // to run this.
            if let Err(error) = read_all(&session, &theirs).await {
                skipped.push(format!(
                    "{}: the reference's own file is unreadable under the enabled editions \
                     ({error}); nothing to compare against",
                    rel.display()
                ));
                continue;
            }

            match compare(&session, &theirs, &ours).await {
                Ok(rows) => {
                    rows_compared += rows;
                    files_compared += 1;
                }
                Err(error) => failures.push(format!("{}: {error}", rel.display())),
            }
        }

        println!(
            "CROSS-CHECK: {files_compared} Vorticity-written files read by Vortex Rust, \
             {rows_compared} rows compared scalar by scalar against the reference's own file."
        );

        if !skipped.is_empty() {
            println!(
                "  {} file(s) skipped, the reference's own bytes being unreadable here:",
                skipped.len()
            );
            for skip in &skipped {
                println!("    {skip}");
            }
        }

        if !failures.is_empty() {
            for failure in failures.iter().take(40) {
                eprintln!("  {failure}");
            }
            anyhow::bail!("{} of {} files disagreed", failures.len(), relative.len());
        }

        Ok(())
    })
}

/// Reads both files with the reference reader and compares every scalar.
async fn compare(session: &VortexSession, theirs: &Path, ours: &Path) -> anyhow::Result<u64> {
    let expected = read_all(session, theirs).await?;
    let actual = read_all(session, ours).await?;

    if expected.len() != actual.len() {
        anyhow::bail!(
            "row count {} in ours against {} in the reference's",
            actual.len(),
            expected.len()
        );
    }

    if expected.dtype() != actual.dtype() {
        anyhow::bail!(
            "dtype {} in ours against {} in the reference's",
            actual.dtype(),
            expected.dtype()
        );
    }

    let mut ctx = session.create_execution_ctx();
    for i in 0..expected.len() {
        let want: Scalar = expected.execute_scalar(i, &mut ctx)?;
        let got: Scalar = actual.execute_scalar(i, &mut ctx)?;

        // Scalar equality here is the reference's own, which compares values rather than
        // representations: a column we wrote as a plain primitive where the reference wrote it
        // bit-packed is EQUAL, and that is the point. What it must not tolerate is a different
        // value or a different nullness.
        if want != got {
            anyhow::bail!("row {i} is {got:?} in ours and {want:?} in the reference's");
        }
    }

    check_statistics(session, ours, &actual, &mut ctx).await?;
    check_string_pruning(session, theirs, ours, &actual, &mut ctx).await?;

    Ok(expected.len() as u64)
}

/// Asks the reference to PRUNE with our string zones: for every utf8 or binary field, equality and
/// both strict comparisons against values taken from the data, scanned with the filter in our file
/// and in the reference's own. The reference's file answers with its own zone maps, so the counts
/// agree unless one of our `vortex.bounded_min` / `vortex.bounded_max` zones claims a bound its
/// rows break -- the one failure a zone map may never have, and one no unfiltered read can see.
async fn check_string_pruning(
    session: &VortexSession,
    theirs: &Path,
    ours: &Path,
    actual: &ArrayRef,
    ctx: &mut ExecutionCtx,
) -> anyhow::Result<()> {
    let Some(root) = actual.as_opt::<Struct>() else {
        return Ok(());
    };

    let ours_file = session.open_options().open_path(ours).await?;
    let theirs_file = session.open_options().open_path(theirs).await?;
    let dtype = ours_file.dtype().clone();
    let DType::Struct(fields, _) = &dtype else {
        return Ok(());
    };

    for (index, name) in fields.names().iter().enumerate() {
        if !matches!(fields.field_by_index(index), Some(DType::Utf8(_) | DType::Binary(_))) {
            continue;
        }

        let Some(column) = root.unmasked_field_opt(index) else {
            continue;
        };
        let rows = column.len();
        if rows == 0 {
            continue;
        }

        for row in [0, rows / 3, rows / 2, rows - 1] {
            let probe = column.execute_scalar(row, ctx)?;
            if probe.is_null() {
                continue;
            }

            for (op, filter) in [
                ("=", eq(col(name.clone()), lit(probe.clone()))),
                ("<", lt(col(name.clone()), lit(probe.clone()))),
                (">", gt(col(name.clone()), lit(probe.clone()))),
            ] {
                let bound = filter.bind(&dtype)?;
                let want = theirs_file
                    .scan()?
                    .with_filter(bound.clone())
                    .into_array_stream()?
                    .read_all()
                    .await?
                    .len();
                let got = ours_file
                    .scan()?
                    .with_filter(bound)
                    .into_array_stream()?
                    .read_all()
                    .await?
                    .len();
                if want != got {
                    anyhow::bail!(
                        "field {name}: `{op} {probe:?}` selects {got} rows in ours and {want} in the reference's"
                    );
                }
            }
        }
    }

    Ok(())
}

/// Holds every exact file statistic our writer claims to the reference's own recomputation of
/// it over the data the reference just read back: min, max, null_count, and the two order flags
/// the reference writer never emits (vortex-layout-0.86.1 layouts/file_stats.rs drops IsSorted
/// and IsStrictSorted at the file level), so this is the one place their meaning is checked
/// against the implementation that defines it.
async fn check_statistics(
    session: &VortexSession,
    ours: &Path,
    actual: &ArrayRef,
    ctx: &mut ExecutionCtx,
) -> anyhow::Result<()> {
    let file = session.open_options().open_path(ours).await?;
    let Some(statistics) = file.footer().statistics() else {
        return Ok(());
    };

    let Some(root) = actual.as_opt::<Struct>() else {
        return Ok(());
    };

    let checked: &[Stat] = &[
        Stat::Min,
        Stat::Max,
        Stat::NullCount,
        Stat::IsSorted,
        Stat::IsStrictSorted,
    ];
    for (index, set) in statistics.stats_sets().iter().enumerate() {
        let Some(encoded) = root.unmasked_field_opt(index) else {
            anyhow::bail!("file statistics name field {index}, which the data does not have");
        };

        // CANONICAL FIRST. The reference recomputes a statistic through the kernel of whatever
        // encoding holds the column, and on the arrays this writer produces some of those kernels
        // answer `is_sorted = true` for a column that is not (a sawtooth i64 under fastlanes, a
        // cycling label under a dictionary, a null after a value under a validity array). The
        // primitive and varbinview kernels over canonical data are the definition the reference's
        // own tests exercise, so the column is canonicalized before it is asked.
        let column = encoded.clone().execute::<Canonical>(ctx)?.into_array();

        for (stat, precision) in set.iter() {
            if !checked.contains(stat) {
                continue;
            }
            let Precision::Exact(claimed) = precision else {
                continue;
            };
            let Some(stat_dtype) = stat.dtype(column.dtype()) else {
                anyhow::bail!("field {index}: {} is claimed on a type it has no meaning for", stat.name());
            };
            let Some(computed) = column.statistics().compute_stat(*stat, ctx)? else {
                anyhow::bail!("field {index}: {} is claimed but the reference cannot compute it", stat.name());
            };

            // Scalar equality is the reference's own, over values: an i32 column's minimum written
            // as the widened i64 the protobuf carries is EQUAL to the i32 the reference computes.
            let ours_scalar = Scalar::try_new(stat_dtype, Some(claimed.clone()))?;
            if computed != ours_scalar {
                anyhow::bail!(
                    "field {index}: {} is {ours_scalar:?} in our file statistics, the reference computes {computed:?}",
                    stat.name()
                );
            }
        }
    }

    Ok(())
}

async fn read_all(session: &VortexSession, path: &Path) -> anyhow::Result<ArrayRef> {
    let file = session.open_options().open_path(path).await?;
    Ok(file.scan()?.into_array_stream()?.read_all().await?)
}

fn collect(root: &Path, dir: &Path, into: &mut BTreeSet<PathBuf>) -> anyhow::Result<()> {
    if !dir.exists() {
        return Ok(());
    }

    for entry in std::fs::read_dir(dir)? {
        let path = entry?.path();
        if path.is_dir() {
            collect(root, &path, into)?;
        } else if path.extension().is_some_and(|e| e == "vortex") {
            into.insert(path.strip_prefix(root)?.to_path_buf());
        }
    }

    Ok(())
}
