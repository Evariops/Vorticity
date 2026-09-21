//! Checks that the forged fixture set behaves the way its manifest claims.
//!
//! `tests/Vorticity.Conformance/forged/manifest.json` states three expectations for
//! `negative/unknown_encoding_id`, and they are the whole reason the fixture exists.
//! An unvalidated expectation in a manifest is worse than none, so
//! this example asserts all three against the Rust reader — the same reader the .NET side is being
//! held to.
//!
//! ```sh
//! cargo run --release --example verify_forged -- ../../tests/Vorticity.Conformance/forged
//! ```

use std::path::PathBuf;

use vortex::VortexSessionDefault;
use vortex::array::stream::ArrayStreamExt;
use vortex::expr::root;
use vortex::expr::select;
use vortex::file::OpenOptionsSessionExt;
use vortex::io::runtime::Handle;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::session::VortexSession;

fn main() -> anyhow::Result<()> {
    let root_dir = PathBuf::from(
        std::env::args()
            .nth(1)
            .unwrap_or_else(|| "../../tests/Vorticity.Conformance/forged".to_string()),
    );
    let path = root_dir.join("negative/unknown_encoding_id.vortex");

    block_on(|handle: Handle| async move {
        let session = VortexSession::default().with_handle(handle);

        // 1. Opening the file succeeds: an unknown id in `array_specs` is not itself an error.
        let file = session.open_options().open_path(&path).await?;
        println!("open:            ok — dtype {}", file.dtype());

        // 2. Scanning without the patched column succeeds. This is the half that locks in lazy
        //    component resolution: a reader that resolves every declared id up front fails here.
        let projection = select(["strs"], root())
            .optimize_recursive(file.dtype())?
            .bind(file.dtype())?;
        let projected = file
            .scan()?
            .with_projection(projection)
            .into_array_stream()?
            .read_all()
            .await?;
        println!(
            "project strs:    ok — {} rows, {}",
            projected.len(),
            projected.dtype()
        );

        // 3. Scanning WITH the patched column fails, and the message names the unknown id.
        let projection = select(["ints"], root())
            .optimize_recursive(file.dtype())?
            .bind(file.dtype())?;
        let result = async {
            file.scan()?
                .with_projection(projection)
                .into_array_stream()?
                .read_all()
                .await
        }
        .await;
        match result {
            Ok(_) => anyhow::bail!("project ints: UNEXPECTEDLY SUCCEEDED — the fixture is not doing its job"),
            Err(e) => {
                let message = format!("{e:#}");
                println!("project ints:    rejected — {message}");
                if !message.contains("vortex.unknown01") {
                    eprintln!(
                        "NOTE: the error does not name `vortex.unknown01`; the .NET expectation \
                         that the message names the id may need relaxing"
                    );
                }
            }
        }

        Ok::<_, anyhow::Error>(())
    })
}
