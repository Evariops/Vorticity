//! Vortex Rust reads every object a dataset's jobs write.
//!
//! The .NET side writes each such object out as `<name>.stored.vortex`, and beside it a twin,
//! `<name>.plain.vortex`, the same rows written plainly. Both are read here by the reference, and
//! their rows compared as multisets, each row rendered from the reference's own scalars: the order of
//! the rows is the one thing the two files may disagree on. What the dataset adds, its table of
//! contents, its synopses and its chunk directories, lives in the index directory, which the
//! reference does not read, so this is also the check that a strict reader opens such a file, its
//! columns written against object dictionaries and chunked apart. Then the reference prunes with the
//! object's zone maps, filters answered by a file it writes of the same rows,
//! `<name>.reference.vortex`, as they are by the object.
//!
//! ```sh
//! cargo run --release --example verify_objects -- <dir>
//! ```

use std::path::Path;
use std::path::PathBuf;

use vortex::VortexSessionDefault;
use vortex::array::ArrayRef;
use vortex::array::ExecutionCtx;
use vortex::array::IntoArray;
use vortex::array::RecursiveCanonical;
use vortex::array::VortexSessionExecute;
use vortex::array::arrays::Struct;
use vortex::array::arrays::struct_::StructArrayExt;
use vortex::array::stream::ArrayStreamExt;
use vortex::dtype::DType;
use vortex::expr::col;
use vortex::expr::eq;
use vortex::expr::gt;
use vortex::expr::lit;
use vortex::expr::lt;
use vortex::file::OpenOptionsSessionExt;
use vortex::file::WriteOptionsSessionExt;
use vortex::io::runtime::Handle;
use vortex::io::runtime::single::block_on;
use vortex::io::session::RuntimeSessionExt;
use vortex::io::std_file::FileWrite;
use vortex::scalar::Scalar;
use vortex::session::VortexSession;

fn main() -> anyhow::Result<()> {
    let dir = PathBuf::from(
        std::env::args()
            .nth(1)
            .ok_or_else(|| anyhow::anyhow!("usage: verify_objects <dir>"))?,
    );
    let mut stored: Vec<PathBuf> = std::fs::read_dir(&dir)?
        .filter_map(|entry| entry.ok().map(|entry| entry.path()))
        .filter(|path| path.to_string_lossy().ends_with(".stored.vortex"))
        .collect();
    stored.sort();
    if stored.is_empty() {
        anyhow::bail!(
            "no .stored.vortex files under {}; run the .NET side first",
            dir.display()
        );
    }

    block_on(|handle: Handle| async move {
        let session = VortexSession::default().with_handle(handle);
        let mut ctx = session.create_execution_ctx();
        let mut rows_compared: u64 = 0;
        let mut dictionaries: usize = 0;
        let mut filters: usize = 0;
        for path in &stored {
            let twin = PathBuf::from(
                path.to_string_lossy()
                    .replace(".stored.vortex", ".plain.vortex"),
            );
            let array = read(&session, path).await?;
            let ours = rendered(&array, &mut ctx)?;
            let theirs = rendered(&read(&session, &twin).await?, &mut ctx)?;
            if ours.len() != theirs.len() {
                anyhow::bail!(
                    "{}: {} rows against {} in its twin written plainly",
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
            dictionaries += layouts(&session, path, "vortex.dict").await?;
            filters += check_pruning(&session, path, &array, &mut ctx).await?;
        }

        // The objects carry what the check is for: columns written against an object dictionary.
        if dictionaries == 0 {
            anyhow::bail!(
                "no column of the objects under {} is written against an object dictionary",
                dir.display()
            );
        }

        println!(
            "OBJECTS: {} objects a dataset's jobs wrote read by Vortex Rust, {dictionaries} columns \
             written against an object dictionary, {rows_compared} rows equal to their twins' \
             written plainly, {filters} filters pruned alike by their zone maps and the reference's.",
            stored.len()
        );
        Ok(())
    })
}

/// Asks the reference to PRUNE with the object's zone maps: for each column of a number or a text,
/// equality and both strict comparisons against values of its rows (four at fixed places, its least
/// and its greatest, a NaN when it holds one), scanned with the filter in the object and in a file the
/// reference writes of the same rows, which answers with its own zone maps. The counts agree unless
/// one of the object's zones claims what its rows break: a bound over a column written against an
/// object dictionary, a float's NaN count, a null count. Returns the filters compared.
async fn check_pruning(
    session: &VortexSession,
    ours: &Path,
    array: &ArrayRef,
    ctx: &mut ExecutionCtx,
) -> anyhow::Result<usize> {
    let canonical = array.clone().execute::<RecursiveCanonical>(ctx)?.0.into_array();
    let Some(root) = canonical.as_opt::<Struct>() else {
        return Ok(0);
    };

    let reference = PathBuf::from(
        ours.to_string_lossy()
            .replace(".stored.vortex", ".reference.vortex"),
    );
    let sink = FileWrite::create(&reference, session.handle()).await?;
    session
        .write_options()
        .write(sink, canonical.to_array_stream())
        .await?;

    let ours_file = session.open_options().open_path(ours).await?;
    let theirs_file = session.open_options().open_path(&reference).await?;
    let dtype = ours_file.dtype().clone();
    let DType::Struct(fields, _) = &dtype else {
        return Ok(0);
    };

    let mut filters = 0;
    for (index, name) in fields.names().iter().enumerate() {
        if !matches!(
            fields.field_by_index(index),
            Some(DType::Primitive(..) | DType::Utf8(_) | DType::Binary(_))
        ) {
            continue;
        }

        let Some(column) = root.unmasked_field_opt(index) else {
            continue;
        };
        let rows = column.len();
        let mut probes: Vec<Scalar> = Vec::new();
        let mut least: Option<Scalar> = None;
        let mut greatest: Option<Scalar> = None;
        let mut nan: Option<Scalar> = None;
        for row in 0..rows {
            let value = column.execute_scalar(row, ctx)?;
            if value.is_null() {
                continue;
            }

            if matches!(value.dtype(), DType::Primitive(..)) && value.as_primitive().is_nan() {
                nan.get_or_insert(value);
                continue;
            }

            if least.as_ref().is_none_or(|least| value < *least) {
                least = Some(value.clone());
            }

            if greatest.as_ref().is_none_or(|greatest| value > *greatest) {
                greatest = Some(value.clone());
            }

            if row == 0 || row == rows / 3 || row == rows / 2 || row == rows - 1 {
                probes.push(value);
            }
        }

        probes.extend(least);
        probes.extend(greatest);
        probes.extend(nan);
        for probe in probes {
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
                        "{}, field {name}: `{op} {probe:?}` selects {got} rows in the object and {want} in the reference's file",
                        ours.display()
                    );
                }

                filters += 1;
            }
        }
    }

    Ok(filters)
}

/// How many of a file's layouts the reference reads as `id`.
async fn layouts(session: &VortexSession, path: &Path, id: &str) -> anyhow::Result<usize> {
    let file = session.open_options().open_path(path).await?;
    let mut count = 0;
    for layout in file.footer().layout().depth_first_traversal() {
        if layout?.encoding_id().to_string() == id {
            count += 1;
        }
    }

    Ok(count)
}

/// A file's rows as the reference reads them.
async fn read(session: &VortexSession, path: &Path) -> anyhow::Result<ArrayRef> {
    let file = session.open_options().open_path(path).await?;
    Ok(file.scan()?.into_array_stream()?.read_all().await?)
}

/// Rows, each its scalar rendered, sorted.
fn rendered(array: &ArrayRef, ctx: &mut ExecutionCtx) -> anyhow::Result<Vec<String>> {
    let mut rows = Vec::with_capacity(array.len());
    for i in 0..array.len() {
        let scalar: Scalar = array.execute_scalar(i, ctx)?;
        rows.push(format!("{scalar:?}"));
    }

    rows.sort();
    Ok(rows)
}
