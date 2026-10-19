//! Vortex Rust opens and reads every row of every `.vortex` file in a directory, and prints what it
//! read: the check that a strict reader takes a file at all, whatever arrays it holds, before any
//! comparison of values.
//!
//! ```sh
//! cargo run --release --example scan_files -- <dir>
//! ```

use std::path::PathBuf;

use vortex::VortexSessionDefault;
use vortex::array::VortexSessionExecute;
use vortex::array::stream::ArrayStreamExt;
use vortex::file::OpenOptionsSessionExt;
use vortex::io::runtime::Handle;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::session::VortexSession;

fn main() -> anyhow::Result<()> {
    let dir = PathBuf::from(
        std::env::args()
            .nth(1)
            .ok_or_else(|| anyhow::anyhow!("usage: scan_files <dir>"))?,
    );
    let mut files: Vec<PathBuf> = std::fs::read_dir(&dir)?
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| path.extension().is_some_and(|e| e == "vortex"))
        .collect();
    files.sort();
    if files.is_empty() {
        anyhow::bail!("no .vortex files under {}", dir.display());
    }

    block_on(|handle: Handle| async move {
        let session = VortexSession::default().with_handle(handle);
        for path in &files {
            let file = session.open_options().open_path(path).await?;
            let array = file.scan()?.into_array_stream()?.read_all().await?;
            let mut ctx = session.create_execution_ctx();
            for i in 0..array.len() {
                array.execute_scalar(i, &mut ctx)?;
            }

            println!(
                "{}: {} rows read, every scalar",
                path.file_name().unwrap_or_default().to_string_lossy(),
                array.len()
            );
        }

        Ok(())
    })
}
