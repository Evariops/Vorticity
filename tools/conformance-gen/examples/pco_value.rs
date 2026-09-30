//! What pco is worth to the reference's own compressor: each numeric column compressed by the
//! default BtrBlocks schemes, then by the compact ones (which add pco), then by pco alone, the
//! bytes of each and the encoding the compressors chose. Each column's raw values are written to
//! `<dir>/<name>.<ptype>` as well, little-endian, so another writer can be measured on the same.
//!
//! `cargo run --release --example pco_value -- <dir>`

use std::fs;
use std::path::PathBuf;

use vortex::VortexSessionDefault;
use vortex::array::ArrayRef;
use vortex::array::IntoArray;
use vortex::array::VortexSessionExecute;
use vortex::array::arrays::PrimitiveArray;
use vortex::array::validity::Validity;
use vortex::buffer::Buffer;
use vortex::compressor::BtrBlocksCompressorBuilder;
use vortex::encodings::pco::Pco;
use vortex::error::VortexResult;
use vortex::session::VortexSession;
use vortex_btrblocks::SchemeExt;
use vortex_btrblocks::schemes::integer::DeltaScheme;

const ROWS: usize = 65_536;

fn mix(row: u64) -> u64 {
    let mut z = row.wrapping_add(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

/// A uniform draw in [0, 1) from the row's hash.
fn unit(row: u64) -> f64 {
    (mix(row) >> 11) as f64 / (1u64 << 53) as f64
}

enum Column {
    I64(Vec<i64>),
    I32(Vec<i32>),
    F64(Vec<f64>),
    F32(Vec<f32>),
}

impl Column {
    fn primitive(&self) -> PrimitiveArray {
        match self {
            Column::I64(v) => PrimitiveArray::new(Buffer::copy_from(v), Validity::NonNullable),
            Column::I32(v) => PrimitiveArray::new(Buffer::copy_from(v), Validity::NonNullable),
            Column::F64(v) => PrimitiveArray::new(Buffer::copy_from(v), Validity::NonNullable),
            Column::F32(v) => PrimitiveArray::new(Buffer::copy_from(v), Validity::NonNullable),
        }
    }

    fn dump(&self, dir: &PathBuf, name: &str) -> std::io::Result<()> {
        let (ext, bytes): (&str, Vec<u8>) = match self {
            Column::I64(v) => ("i64", v.iter().flat_map(|x| x.to_le_bytes()).collect()),
            Column::I32(v) => ("i32", v.iter().flat_map(|x| x.to_le_bytes()).collect()),
            Column::F64(v) => ("f64", v.iter().flat_map(|x| x.to_le_bytes()).collect()),
            Column::F32(v) => ("f32", v.iter().flat_map(|x| x.to_le_bytes()).collect()),
        };
        fs::write(dir.join(format!("{name}.{ext}")), bytes)
    }
}

fn main() -> VortexResult<()> {
    let dir = PathBuf::from(std::env::args().nth(1).unwrap_or_else(|| ".".into()));
    fs::create_dir_all(&dir)?;
    let session = VortexSession::default();
    let n = ROWS as u64;

    let mut walk = 20.0f64;
    let mut clock = 1_700_000_000_000i64;
    let columns: Vec<(&str, Column)> = vec![
        // A sensor: a random walk read to two decimals.
        ("sensor_2dp", Column::F64((0..n).map(|i| { walk += unit(i) - 0.5; (walk * 100.0).round() / 100.0 }).collect())),
        // Computed doubles with every mantissa bit in use.
        ("computed_f64", Column::F64((0..n).map(|i| ((i as f64) * 0.001).sin() * (1.0 + unit(i)).ln() * 1234.5678).collect())),
        // Log-normal amounts: a skew most values small, a few large.
        ("lognormal_f64", Column::F64((0..n).map(|i| (unit(i) * 6.0).exp() * 3.7).collect())),
        // Prices in cents as f32.
        ("prices_f32", Column::F32((0..n).map(|i| ((mix(i) % 100_000) as f32) * 0.01).collect())),
        // Event times in ms, gaps exponentially distributed around 40 ms.
        ("events_ms", Column::I64((0..n).map(|i| { clock += (-(1.0 - unit(i)).ln() * 40.0) as i64; clock }).collect())),
        // Zipf-like counts: most tiny, a long tail.
        ("zipf_i64", Column::I64((0..n).map(|i| (1.0 / (unit(i) + 1e-6)).floor() as i64).collect())),
        // Amounts in multiples of a hundred.
        ("mult100_i64", Column::I64((0..n).map(|i| ((mix(i) % 50_000) as i64) * 100).collect())),
        // Quantities: uniform in a small range.
        ("uniform_i32", Column::I32((0..n).map(|i| (mix(i) % 1000) as i32).collect())),
        // A smooth signal in i32.
        ("wave_i32", Column::I32((0..n).map(|i| (((i as f64) / 50.0).sin() * 1_000_000.0) as i32 + (mix(i) % 100) as i32).collect())),
        // Random 64-bit ids: nothing to find.
        ("random_i64", Column::I64((0..n).map(|i| mix(i) as i64).collect())),
    ];

    // `fastlanes.delta` belongs to no edition, so a file writer's compressor never has it: it is
    // left out of both, as the edition filter would.
    let default = BtrBlocksCompressorBuilder::default().exclude_schemes([DeltaScheme::default().id()]).build();
    let compact = BtrBlocksCompressorBuilder::default().with_compact().exclude_schemes([DeltaScheme::default().id()]).build();
    println!("{:<14} {:>9} {:>9} {:<22} {:>9} {:<22} {:>9} {:>7}", "column", "plain", "default", "", "compact", "", "pco", "c/d");
    for (name, column) in &columns {
        column.dump(&dir, name)?;
        let parray = column.primitive();
        let array: ArrayRef = parray.clone().into_array();
        let plain = array.nbytes();
        let mut ctx = session.create_execution_ctx();
        let d = default.compress(&array, &mut ctx)?;
        let c = compact.compress(&array, &mut ctx)?;
        let p = Pco::from_primitive(parray.as_view(), pco::DEFAULT_COMPRESSION_LEVEL, 8192, &mut ctx)?.into_array();
        println!(
            "{:<14} {:>9} {:>9} {:<22} {:>9} {:<22} {:>9} {:>7.3}",
            name, plain, d.nbytes(), d.encoding_id().to_string(), c.nbytes(), c.encoding_id().to_string(), p.nbytes(),
            c.nbytes() as f64 / d.nbytes() as f64
        );
    }

    Ok(())
}
