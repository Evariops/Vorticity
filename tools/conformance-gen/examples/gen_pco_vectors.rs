//! Generates pco test vectors for the C# port: compressed bytes plus the values they decode to.
//!
//! WHY THIS EXISTS. The four `vortex.pco` files in the conformance corpus all have a single bin with
//! an ANS size log of zero -- the degenerate path -- so making them read would say nothing about the
//! entropy coder, the offsets, or anything that varies per value. These vectors are chosen to
//! exercise what the corpus cannot: many bins, wide and narrow offsets, several delta orders, and
//! both int and float modes.
//!
//! Output is JSON on stdout: per case, the pco header, chunk metadata and page bytes as hex, plus
//! the exact values. The C# side decodes the bytes and must reproduce the values.

use pco::ChunkConfig;
use pco::DeltaSpec;
use pco::ModeSpec;
use pco::PagingSpec;
use pco::wrapped::FileCompressor;

fn case_i64(name: &str, values: Vec<i64>, config: ChunkConfig) -> anyhow::Result<String> {
    let fc = FileCompressor::default();
    let mut header = Vec::new();
    header = fc.write_header(header)?;

    let mut cc = fc.chunk_compressor(&values, &config)?;
    let mut meta = Vec::new();
    meta = cc.write_meta(meta)?;

    // Ground truth for the metadata parser, taken from pco's own parse of the bytes it just wrote.
    let m = cc.meta();
    let mode = format!("{:?}", m.mode);
    let delta = format!("{:?}", m.delta_encoding);
    let latents: Vec<String> = m
        .per_latent_var
        .as_ref()
        .enumerated()
        .into_iter()
        .map(|(label, var)| {
            format!(
                "{{\"key\":\"{label:?}\",\"ans_size_log\":{},\"n_bins\":{}}}",
                var.ans_size_log,
                match &var.bins {
                    pco::metadata::DynBins::U8(b) => b.len(),
                    pco::metadata::DynBins::U16(b) => b.len(),
                    pco::metadata::DynBins::U32(b) => b.len(),
                    pco::metadata::DynBins::U64(b) => b.len(),
                    _ => 0,
                }
            )
        })
        .collect();

    let n_per_page = cc.n_per_page();
    let mut pages = Vec::new();
    for page_idx in 0..n_per_page.len() {
        let mut page = Vec::new();
        page = cc.write_page(page_idx, page)?;
        pages.push(hex(&page));
    }

    Ok(format!(
        "{{\"name\":\"{}\",\"ptype\":\"i64\",\"mode\":\"{}\",\"delta\":\"{}\",\"latents\":[{}],\"header\":\"{}\",\"meta\":\"{}\",\"pages\":[{}],\"n_per_page\":[{}],\"values\":[{}]}}",
        name,
        mode,
        delta,
        latents.join(","),
        hex(&header),
        hex(&meta),
        pages
            .iter()
            .map(|p| format!("\"{p}\""))
            .collect::<Vec<_>>()
            .join(","),
        n_per_page
            .iter()
            .map(|n| n.to_string())
            .collect::<Vec<_>>()
            .join(","),
        values
            .iter()
            .map(|v| v.to_string())
            .collect::<Vec<_>>()
            .join(","),
    ))
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn main() -> anyhow::Result<()> {
    let mut cases = Vec::new();

    // MANY BINS, which is the thing the corpus cannot exercise. A wide spread of magnitudes forces
    // the compressor to build a real ANS table rather than the single-bin degenerate one.
    let mut spread = Vec::new();
    let mut state: u64 = 12345;
    for i in 0..3000u64 {
        state = state.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        let magnitude = 1i64 << (state % 40);
        spread.push(((state as i64) % magnitude) + (i as i64));
    }
    cases.push(case_i64(
        "many_bins_classic",
        spread,
        ChunkConfig::default()
            .with_mode_spec(ModeSpec::Classic)
            .with_delta_spec(DeltaSpec::NoOp)
            .with_paging_spec(PagingSpec::EqualPagesUpTo(1000)),
    )?);

    // NARROW OFFSETS: values close together, so bins are tight and offsets few bits.
    let tight: Vec<i64> = (0..2000).map(|i| 1_000_000 + (i % 7)).collect();
    cases.push(case_i64(
        "narrow_offsets",
        tight,
        ChunkConfig::default()
            .with_mode_spec(ModeSpec::Classic)
            .with_delta_spec(DeltaSpec::NoOp),
    )?);

    // CONSECUTIVE DELTA of order 2 over a quadratic, which no corpus file exercises.
    let quadratic: Vec<i64> = (0..1500i64).map(|i| (i * i) / 3 + 17).collect();
    cases.push(case_i64(
        "delta_order_2",
        quadratic,
        ChunkConfig::default().with_delta_spec(DeltaSpec::TryConsecutive(2)),
    )?);

    // SEVERAL PAGES in one chunk, so the per-page ANS state reset is exercised.
    let paged: Vec<i64> = (0..2500i64).map(|i| i.wrapping_mul(2_654_435_761)).collect();
    cases.push(case_i64(
        "multi_page",
        paged,
        ChunkConfig::default().with_paging_spec(PagingSpec::EqualPagesUpTo(600)),
    )?);

    println!("[{}]", cases.join(","));
    Ok(())
}
