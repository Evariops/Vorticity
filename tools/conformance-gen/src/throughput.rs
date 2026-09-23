//! Single-encoding files large enough to measure a decoder instead of ranking one.
//!
//! WHY THESE ARE NOT CORPUS FILES. The conformance corpus answers "does this read correctly", and
//! for that 4096 rows is plenty — every file carries a sidecar holding all of its values, 128 to a
//! line, which is the oracle the .NET tests compare against. These answer "how fast", and for that
//! 4096 rows is useless: the per-encoding baseline table says so in its own words, "this is
//! a ranking, not a measurement", because ~35 µs of every row in it is the fixed open-and-walk cost
//! both implementations pay before a single value is decoded. `fastlanes.bitpacked` reads 40.9 µs
//! against Rust's 35.4 — five microseconds of signal under thirty-five of noise, which is why that
//! row barely moved when its kernel went to 0.28x its time.
//!
//! So these files are BIG and have NO SIDECAR. A 1M-row sidecar would be hundreds of megabytes of
//! JSON to answer a question the 4096-row file already answers. Correctness is the corpus's job;
//! these exist to push the fixed cost below the noise floor, and nothing should ever assert a value
//! from them.
//!
//! AND THEY ARE NOT COMMITTED. `bench/Vorticity.Benchmarks/Corpus.cs` already draws this line —
//! "those are gigabytes that do not belong in a repository" — and resolves real datasets through an
//! environment variable. These go the same way: generated on demand, read through
//! `VORTICITY_THROUGHPUT_CORPUS`, and the benchmark says so rather than failing when they are
//! absent.
//!
//! THEY DO CARRY A MANIFEST, and that is the one place they are not like the note above says.
//! Uncommitted and unidentified are different problems. `ThroughputCheck.References` is fifty
//! ratchets measured against *some* set of bytes, and until this file wrote a manifest nothing
//! recorded which: not the row count, not the Vortex release, not the commit of the generator, not
//! a hash. That is not hypothetical here — this generator once changed what these files contain,
//! silently dropping `fastlanes.delta` and `vortex.zstd_buffers` entirely (`2575a6f`), and a
//! reference measured before and checked after would have been wrong with nothing to say so.
//! `manifest.json` is small, it is written beside the files, and `--throughput --check` refuses to
//! gate without it.

use std::path::Path;
use std::process::Command;

use anyhow::Context;
use serde::Serialize;
use vortex::array::session::ArraySessionExt;
use vortex::io::session::RuntimeSessionExt;
use vortex::io::std_file::FileWrite;
use vortex::session::VortexSession;

use crate::emit::WriteSpec;
use crate::emit::build_options;
use crate::encodings;
use crate::manifest::VORTEX_VERSION;
use crate::util::sha256_file;

/// Bumped when the shape changes in a way `ThroughputCheck` must notice.
const THROUGHPUT_MANIFEST_FORMAT: &str = "vorticity-throughput-corpus/1";

/// What produced a set of throughput inputs, and what is in it.
#[derive(Debug, Serialize)]
struct ThroughputManifest {
    format: &'static str,
    /// Rows per file. Every ratchet in `ThroughputCheck.References` is a ns/value figure at this
    /// length; a corpus generated at another one is a different measurement under the same name.
    rows: usize,
    vortex: &'static str,
    generator: Generator,
    files: Vec<FileRecord>,
}

/// The working tree that ran the generator. `dirty` is load-bearing: a hash alone would claim a
/// provenance the commit does not have when the tree carries uncommitted changes.
#[derive(Debug, Serialize)]
struct Generator {
    commit: String,
    dirty: bool,
}

#[derive(Debug, Serialize)]
struct FileRecord {
    id: String,
    bytes: u64,
    sha256: String,
}

/// Build every single-encoding case at `rows` rows and write it to `out`.
///
/// Returns the number of files written. Cases that refuse the length are reported and skipped
/// rather than failing the run: a builder with a construction minimum is not a defect here.
pub async fn write_throughput_corpus(
    session: &VortexSession,
    out: &Path,
    rows: usize,
) -> anyhow::Result<usize> {
    std::fs::create_dir_all(out).with_context(|| format!("creating {}", out.display()))?;

    let mut written = 0usize;
    let mut records: Vec<FileRecord> = Vec::new();
    for case in encodings::encoding_cases() {
        let array = match (case.build)(session, rows) {
            Ok(a) => a,
            Err(e) => {
                eprintln!("  skip {:<28} {e}", case.id);
                continue;
            }
        };

        let path = out.join(format!("{}.vortex", case.id));
        let sink = FileWrite::create(&path, session.handle()).await?;
        // `fastlanes.delta` and `vortex.zstd_buffers` belong to no core edition, so the write is
        // rejected at serialization time unless the case's own flag is honoured. Ignoring it was
        // silently dropping the two encodings this axis most needed a number for: the corpus has
        // them at 4096 rows, where ~35 us of fixed cost hides the decoder entirely.
        let mut spec = WriteSpec::forced();
        spec.disable_editions = case.disable_editions;
        match build_options(session, &spec)
            .write(sink, array.to_array_stream())
            .await
        {
            Ok(_) => {}
            Err(e) => {
                eprintln!("  skip {:<28} write failed: {e}", case.id);
                let _ = std::fs::remove_file(&path);
                continue;
            }
        }

        let size = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
        eprintln!("  {:<28} {:>12} bytes  ({})", case.id, size, case.array_id);
        records.push(FileRecord {
            id: case.id.to_string(),
            bytes: size,
            sha256: sha256_file(&path)?,
        });
        written += 1;
    }

    records.sort_by(|a, b| a.id.cmp(&b.id));
    let manifest = ThroughputManifest {
        format: THROUGHPUT_MANIFEST_FORMAT,
        rows,
        vortex: VORTEX_VERSION,
        generator: generator(),
        files: records,
    };

    let manifest_path = out.join("manifest.json");
    std::fs::write(&manifest_path, serde_json::to_string_pretty(&manifest)?)
        .with_context(|| format!("writing {}", manifest_path.display()))?;
    eprintln!(
        "manifest.json: {rows} rows, vortex {VORTEX_VERSION}, generator {}{}",
        manifest.generator.commit,
        if manifest.generator.dirty { " (dirty)" } else { "" }
    );

    Ok(written)
}

/// The generator's commit, and whether its tree was clean.
///
/// Failure is recorded rather than propagated: someone generating these files from a tarball with
/// no `.git` should still get a manifest, and "unknown" is a more useful thing for the consumer to
/// print than a generation that refused to finish.
fn generator() -> Generator {
    let commit = Command::new("git")
        .args(["rev-parse", "HEAD"])
        .output()
        .ok()
        .filter(|o| o.status.success())
        .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_string())
        .unwrap_or_else(|| "unknown".to_string());

    let dirty = Command::new("git")
        .args(["status", "--porcelain"])
        .output()
        .ok()
        .filter(|o| o.status.success())
        .map(|o| !o.stdout.is_empty())
        .unwrap_or(false);

    Generator { commit, dirty }
}
