//! Vortex Rust reads every object a dataset of periods writes in sorted slices.
//!
//! The .NET side writes each such object out as `<name>.sorted.vortex`, and beside it a twin,
//! `<name>.time.vortex`, the same rows in time order written plainly. Both are read here by the
//! reference, and their rows compared as multisets, each row rendered from the reference's own
//! scalars: the order of the rows is the one thing the two files may disagree on. What the dataset
//! adds, the slice table and the chunk directory, lives in the index directory, which the reference
//! does not read, so this is also the check that a strict reader opens such a file.
//!
//! ```sh
//! cargo run --release --example verify_sliced -- <dir>
//! ```

use std::path::Path;
use std::path::PathBuf;

use vortex::VortexSessionDefault;
use vortex::array::ArrayRef;
use vortex::array::VortexSessionExecute;
use vortex::array::stream::ArrayStreamExt;
use vortex::file::OpenOptionsSessionExt;
use vortex::io::runtime::Handle;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::scalar::Scalar;
use vortex::session::VortexSession;

fn main() -> anyhow::Result<()> {
    let dir = PathBuf::from(
        std::env::args()
            .nth(1)
            .ok_or_else(|| anyhow::anyhow!("usage: verify_sliced <dir>"))?,
    );
    let mut sorted: Vec<PathBuf> = std::fs::read_dir(&dir)?
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| path.to_string_lossy().ends_with(".sorted.vortex"))
        .collect();
    sorted.sort();
    if sorted.is_empty() {
        anyhow::bail!(
            "no .sorted.vortex files under {}; run the .NET side first",
            dir.display()
        );
    }

    block_on(|handle: Handle| async move {
        let session = VortexSession::default().with_handle(handle);
        let mut rows_compared: u64 = 0;
        for path in &sorted {
            let twin = PathBuf::from(
                path.to_string_lossy()
                    .replace(".sorted.vortex", ".time.vortex"),
            );
            let ours = rows(&session, path).await?;
            let theirs = rows(&session, &twin).await?;
            if ours.len() != theirs.len() {
                anyhow::bail!(
                    "{}: {} rows against {} in its twin in time order",
                    path.display(),
                    ours.len(),
                    theirs.len()
                );
            }

            if let Some(at) = (0..ours.len()).find(|&i| ours[i] != theirs[i]) {
                anyhow::bail!(
                    "{}: the rows differ from its twin's, first at sorted row {at}: {} against {}",
                    path.display(),
                    ours[at],
                    theirs[at]
                );
            }

            rows_compared += ours.len() as u64;
        }

        println!(
            "SLICES: {} objects in sorted slices read by Vortex Rust, {rows_compared} rows equal \
             to their twins' in time order.",
            sorted.len()
        );
        Ok(())
    })
}

/// A file's rows as the reference reads them, each its scalar rendered, sorted.
async fn rows(session: &VortexSession, path: &Path) -> anyhow::Result<Vec<String>> {
    let file = session.open_options().open_path(path).await?;
    let array: ArrayRef = file.scan()?.into_array_stream()?.read_all().await?;
    let mut ctx = session.create_execution_ctx();
    let mut rows = Vec::with_capacity(array.len());
    for i in 0..array.len() {
        let scalar: Scalar = array.execute_scalar(i, &mut ctx)?;
        rows.push(format!("{scalar:?}"));
    }

    rows.sort();
    Ok(rows)
}
