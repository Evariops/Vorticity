//! Dumps pco chunk metadata as ground truth for the C# port of `vortex.pco`.
//!
//! WHY A PROBE AND NOT A VALUE COMPARISON. The sidecar already holds every value, so a finished
//! decoder can be checked end to end. What it cannot do is say WHERE a half-built one went wrong,
//! and pco's format is several thousand lines in its reference crate: mode, delta encoding, then a
//! bin table and an ANS state per latent variable. This prints those fields so the port can be
//! checked one at a time.
//!
//! `ChunkMeta::read_from` is private; `ChunkDecompressor::meta()` is not, so the metadata is reached
//! through the decompressor rather than the parser.
//!
//! Input is the pco HEADER bytes followed by the chunk-meta bytes, on stdin - the two buffers the
//! vortex.pco node stores. Extracting them is the caller's job, because walking a Vortex file to
//! reach them is exactly what the C# side is being written to do.

use std::io::Read;

use pco::wrapped::FileDecompressor;

fn main() -> anyhow::Result<()> {
    let mut bytes = Vec::new();
    std::io::stdin().read_to_end(&mut bytes)?;
    if bytes.is_empty() {
        anyhow::bail!("expected pco header bytes followed by chunk-meta bytes on stdin");
    }

    let (fd, rest) = FileDecompressor::new(bytes.as_slice())?;
    println!("format_version {:?}", fd.format_version());

    let (cd, _) = fd.chunk_decompressor::<i64, _>(rest)?;
    let meta = cd.meta();
    println!("mode           {:?}", meta.mode);
    println!("delta_encoding {:?}", meta.delta_encoding);
    println!("per_latent_var:");
    for (label, var) in meta.per_latent_var.as_ref().enumerated() {
        println!(
            "  {label:?}: bins {:?}, ans_size_log {}",
            var.bins,
            var.ans_size_log
        );
    }

    Ok(())
}
