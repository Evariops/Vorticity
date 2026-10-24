//! Vortex Rust reads every shredded variant we write, value for value.
//!
//! The .NET side writes each file with shredded variant columns as `<name>.shredded.vortex`, and
//! beside it a twin, `<name>.whole.vortex`, the same rows in the same order with every variant
//! written whole. Both are read here by the reference and compared row by row, each value rendered
//! from the reference's own scalars after two normalizations, for two ways the reference itself
//! reads a shredded value differently from the same value unshredded:
//!
//! - an integer of any width reads as the width of the typed column it was shredded into, so every
//!   integer is rendered as a 64-bit one;
//! - a key an object does not hold reads as a null field, so a null field, and one holding the
//!   variant null, is left out of its object, on both sides.
//!
//! The rows that differ before the normalizations are counted and printed; those that still differ
//! after them fail the run.
//!
//! ```sh
//! cargo run --release --example verify_shredded -- <dir>
//! ```

use std::path::Path;
use std::path::PathBuf;

use vortex::VortexSessionDefault;
use vortex::array::ArrayRef;
use vortex::array::VortexSessionExecute;
use vortex::array::stream::ArrayStreamExt;
use vortex::dtype::DType;
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
            .ok_or_else(|| anyhow::anyhow!("usage: verify_shredded <dir>"))?,
    );
    let mut shredded: Vec<PathBuf> = std::fs::read_dir(&dir)?
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| path.to_string_lossy().ends_with(".shredded.vortex"))
        .collect();
    shredded.sort();
    if shredded.is_empty() {
        anyhow::bail!(
            "no .shredded.vortex files under {}; run the .NET side first",
            dir.display()
        );
    }

    block_on(|handle: Handle| async move {
        let session = VortexSession::default().with_handle(handle);
        let mut rows_compared: u64 = 0;
        let mut normalized: u64 = 0;
        let mut failures = Vec::new();
        for path in &shredded {
            let twin = PathBuf::from(
                path.to_string_lossy()
                    .replace(".shredded.vortex", ".whole.vortex"),
            );
            let ours = read_all(&session, path).await?;
            let theirs = read_all(&session, &twin).await?;
            let name = path.file_name().unwrap_or_default().to_string_lossy().to_string();
            if ours.len() != theirs.len() {
                failures.push(format!("{name}: {} rows against {} in its twin", ours.len(), theirs.len()));
                continue;
            }

            let mut ctx = session.create_execution_ctx();
            let mut differing = 0u64;
            for row in 0..ours.len() {
                let got = ours.execute_scalar(row, &mut ctx)?;
                let want = theirs.execute_scalar(row, &mut ctx)?;
                if got == want {
                    continue;
                }

                differing += 1;
                let (got, want) = (render(&got), render(&want));
                if got != want {
                    failures.push(format!("{name}: row {row} reads {got} shredded and {want} whole"));
                    break;
                }
            }

            println!("{name}: {} rows, {differing} equal only once normalized", ours.len());
            rows_compared += ours.len() as u64;
            normalized += differing;
        }

        if !failures.is_empty() {
            for failure in &failures {
                eprintln!("{failure}");
            }

            anyhow::bail!("{} shredded files read differently from their twins", failures.len());
        }

        println!(
            "SHREDDED: {} files with shredded variants read by Vortex Rust, {rows_compared} rows equal \
             to their twins' written whole, {normalized} of them once integers are widened and null \
             fields left out.",
            shredded.len()
        );
        Ok(())
    })
}

async fn read_all(session: &VortexSession, path: &Path) -> anyhow::Result<ArrayRef> {
    let file = session.open_options().open_path(path).await?;
    Ok(file.scan()?.into_array_stream()?.read_all().await?)
}

/// A scalar rendered with its integers at 64 bits and the null fields of its objects left out.
fn render(scalar: &Scalar) -> String {
    if scalar.is_null() {
        return "null".to_string();
    }

    match scalar.dtype() {
        DType::Variant(_) => match scalar.as_variant().value() {
            None => "null".to_string(),
            Some(inner) if inner.is_null() => "variant-null".to_string(),
            Some(inner) => render(inner),
        },
        DType::Primitive(ptype, _) if ptype.is_int() => {
            let primitive = scalar.as_primitive();
            match primitive.as_::<i64>() {
                Some(value) => format!("{value}i"),
                None => format!("{scalar}"),
            }
        }
        DType::Struct(..) => {
            let fields = scalar.as_struct();
            let names = fields.names().clone();
            let Some(values) = fields.fields_iter() else {
                return "null".to_string();
            };
            let mut rendered: Vec<String> = names
                .iter()
                .zip(values)
                .map(|(name, value)| (name.to_string(), render(&value)))
                .filter(|(_, value)| value != "null" && value != "variant-null")
                .map(|(name, value)| format!("{name}: {value}"))
                .collect();
            rendered.sort();
            format!("{{{}}}", rendered.join(", "))
        }
        DType::List(..) => match scalar.as_list().elements() {
            None => "null".to_string(),
            Some(elements) => format!(
                "[{}]",
                elements.iter().map(render).collect::<Vec<_>>().join(", ")
            ),
        },
        _ => format!("{scalar}"),
    }
}
