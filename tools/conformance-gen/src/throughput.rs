//! Single-encoding files large enough to measure a decoder instead of ranking one.
//!
//! WHY THESE ARE NOT CORPUS FILES. The conformance corpus answers "does this read correctly", and
//! for that 4096 rows is plenty — every file carries a sidecar holding all of its values, 128 to a
//! line, which is the oracle the .NET tests compare against. These answer "how fast", and for that
//! 4096 rows is useless: `bench/BASELINE.md`'s per-encoding table says so in its own words, "this is
//! a ranking, not a measurement", because ~35 µs of every row in it is the fixed open-and-walk cost
//! both implementations pay before a single value is decoded. `fastlanes.bitpacked` reads 40.9 µs
//! against Rust's 35.4 — five microseconds of signal under thirty-five of noise, which is why that
//! row barely moved when its kernel got 3.6x faster.
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

use std::path::Path;

use anyhow::Context;
use vortex::array::session::ArraySessionExt;
use vortex::io::session::RuntimeSessionExt;
use vortex::io::std_file::FileWrite;
use vortex::session::VortexSession;

use crate::emit::WriteSpec;
use crate::emit::build_options;
use crate::encodings;

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
        let spec = WriteSpec::forced();
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
        written += 1;
    }

    Ok(written)
}
