//! Criterion 2 of docs/01-scope.md §4: **any file written by Vorticity is read back correctly by
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
use vortex::array::VortexSessionExecute;
use vortex::array::stream::ArrayStreamExt;
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
            // to run this (BENCH-AUDIT.md B7).
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

    Ok(expected.len() as u64)
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
