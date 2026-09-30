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

/// A case of any pco number type: `bits` is each value's bit pattern, zero-extended to 64 bits, a
/// JSON number the C# side reads as a `ulong` and compares exactly -- a float's NaN payload and
/// sign of zero included.
fn case<T: pco::data_types::Number>(
    name: &str,
    ptype: &str,
    values: Vec<T>,
    config: ChunkConfig,
    bits: impl Fn(&T) -> u64,
) -> anyhow::Result<String> {
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

    let join = |items: Vec<String>| items.join(",");
    Ok(format!(
        "{{\"name\":\"{}\",\"ptype\":\"{}\",\"mode\":\"{}\",\"delta\":\"{}\",\"latents\":[{}],\"header\":\"{}\",\"meta\":\"{}\",\"pages\":[{}],\"n_per_page\":[{}],\"bits\":[{}]}}",
        name,
        ptype,
        mode,
        delta,
        latents.join(","),
        hex(&header),
        hex(&meta),
        join(pages.iter().map(|p| format!("\"{p}\"")).collect()),
        join(n_per_page.iter().map(|n| n.to_string()).collect()),
        join(values.iter().map(|v| bits(v).to_string()).collect()),
    ))
}

fn case_i64(name: &str, values: Vec<i64>, config: ChunkConfig) -> anyhow::Result<String> {
    case(name, "i64", values, config, |v| *v as u64)
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// Values per case of the per-type cases: four batches of 256, the last one partial.
const N: u64 = 1000;

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

    // Every other type pco has, and every mode and delta the corpus cannot show, each over values
    // shaped for it: the reader's latent widths, its float orderings and its joins.
    let mut rng: u64 = 99;
    let mut next = move || {
        rng ^= rng << 13;
        rng ^= rng >> 7;
        rng ^= rng << 17;
        rng
    };
    let classic = || ChunkConfig::default().with_mode_spec(ModeSpec::Classic).with_delta_spec(DeltaSpec::NoOp);

    let lows: Vec<u64> = (0..N).map(|_| next() % 5_000_000).collect();
    cases.push(case("u64_classic", "u64", lows.clone(), classic(), |v| *v)?);
    cases.push(case(
        "u64_intmult",
        "u64",
        lows.iter().map(|v| v * 1000 + (v % 3)).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::TryIntMult(1000)).with_delta_spec(DeltaSpec::NoOp),
        |v| *v,
    )?);

    let doubles: Vec<f64> = (0..N).map(|_| ((next() % 2_000_000) as f64 - 1_000_000.0) / 7.3).collect();
    cases.push(case("f64_classic", "f64", doubles.clone(), classic(), |v| v.to_bits())?);
    cases.push(case(
        "f64_floatmult",
        "f64",
        (0..N).map(|_| ((next() % 100_000) as f64) * 0.01).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::TryFloatMult(0.01)).with_delta_spec(DeltaSpec::NoOp),
        |v| v.to_bits(),
    )?);
    cases.push(case(
        "f64_floatquant",
        "f64",
        doubles.iter().map(|v| f64::from_bits(v.to_bits() & !((1u64 << 30) - 1))).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::TryFloatQuant(30)).with_delta_spec(DeltaSpec::NoOp),
        |v| v.to_bits(),
    )?);
    cases.push(case(
        "f64_dict",
        "f64",
        (0..N).map(|_| [0.5f64, -2.25, 1e300, -0.0, 3.75][(next() % 5) as usize]).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::TryDict).with_delta_spec(DeltaSpec::NoOp),
        |v| v.to_bits(),
    )?);
    cases.push(case(
        "f64_delta1",
        "f64",
        (0..N).map(|i| 1000.0 + (i as f64) * 0.25).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::Classic).with_delta_spec(DeltaSpec::TryConsecutive(1)),
        |v| v.to_bits(),
    )?);
    cases.push(case(
        "i64_dict",
        "i64",
        (0..N).map(|_| [-7i64, 1 << 40, 3, i64::MIN, 42][(next() % 5) as usize]).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::TryDict).with_delta_spec(DeltaSpec::NoOp),
        |v| *v as u64,
    )?);
    cases.push(case(
        "i64_lookback",
        "i64",
        (0..2000i64).map(|i| [11, 250_000, -3, 999_999_999, 17, 5][(i % 6) as usize] + (i / 600)).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::Classic).with_delta_spec(DeltaSpec::TryLookback),
        |v| *v as u64,
    )?);
    // Conv1 predicts from the previous values, which pco offers types of 32 bits at most.
    cases.push(case(
        "i32_conv1",
        "i32",
        (0..2000i32).map(|i| ((i as f64 / 40.0).sin() * 1_000_000.0) as i32 + (next() % 50) as i32).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::Classic).with_delta_spec(DeltaSpec::TryConv1(2)),
        |v| *v as u32 as u64,
    )?);

    cases.push(case(
        "i32_classic",
        "i32",
        (0..N).map(|_| ((next() % 4_000_000) as i32) - 2_000_000).collect(),
        classic(),
        |v| *v as u32 as u64,
    )?);
    cases.push(case(
        "i32_delta1",
        "i32",
        (0..N as i32).map(|i| i * 13 - 7000 + ((next() % 5) as i32)).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::Classic).with_delta_spec(DeltaSpec::TryConsecutive(1)),
        |v| *v as u32 as u64,
    )?);
    cases.push(case(
        "u32_intmult",
        "u32",
        (0..N).map(|_| ((next() % 100_000) as u32) * 7 + 1).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::TryIntMult(7)).with_delta_spec(DeltaSpec::NoOp),
        |v| *v as u64,
    )?);
    cases.push(case(
        "f32_classic",
        "f32",
        (0..N).map(|_| ((next() % 2_000_000) as f32 - 1_000_000.0) / 3.1).collect(),
        classic(),
        |v| v.to_bits() as u64,
    )?);
    cases.push(case(
        "f32_floatmult",
        "f32",
        (0..N).map(|_| ((next() % 10_000) as f32) * 0.5).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::TryFloatMult(0.5)).with_delta_spec(DeltaSpec::NoOp),
        |v| v.to_bits() as u64,
    )?);
    cases.push(case(
        "i16_classic",
        "i16",
        (0..N).map(|_| ((next() % 60_000) as i32 - 30_000) as i16).collect(),
        classic(),
        |v| *v as u16 as u64,
    )?);
    cases.push(case(
        "u16_classic",
        "u16",
        (0..N).map(|_| (next() % 65_536) as u16).collect(),
        classic(),
        |v| *v as u64,
    )?);
    cases.push(case(
        "f16_classic",
        "f16",
        (0..N).map(|_| vortex::array::dtype::half::f16::from_f32(((next() % 20_000) as f32 - 10_000.0) / 9.0)).collect(),
        classic(),
        |v| v.to_bits() as u64,
    )?);

    // The narrow latents' own arithmetic: a convolution computed in i32 over 16-bit latents, a
    // quantized f32, a dictionary of 32-bit latents, and a second-order delta wrapping at 16 bits.
    cases.push(case(
        "i16_conv1",
        "i16",
        (0..N as i32).map(|i| (((i as f64) / 25.0).cos() * 20_000.0) as i16 + (next() % 9) as i16).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::Classic).with_delta_spec(DeltaSpec::TryConv1(3)),
        |v| *v as u16 as u64,
    )?);
    cases.push(case(
        "f32_floatquant",
        "f32",
        (0..N).map(|_| f32::from_bits(((((next() % 2_000_000) as f32) - 1_000_000.0) / 3.7).to_bits() & !((1u32 << 12) - 1))).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::TryFloatQuant(12)).with_delta_spec(DeltaSpec::NoOp),
        |v| v.to_bits() as u64,
    )?);
    cases.push(case(
        "i32_dict",
        "i32",
        (0..N).map(|_| [-70_000i32, 12, i32::MIN, i32::MAX, 0, 99][(next() % 6) as usize]).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::TryDict).with_delta_spec(DeltaSpec::NoOp),
        |v| *v as u32 as u64,
    )?);
    cases.push(case(
        "u16_delta2",
        "u16",
        (0..N).map(|i| ((i * i / 5) % 65_536) as u16).collect(),
        ChunkConfig::default().with_mode_spec(ModeSpec::Classic).with_delta_spec(DeltaSpec::TryConsecutive(2)),
        |v| *v as u64,
    )?);

    println!("[{}]", cases.join(","));
    Ok(())
}
