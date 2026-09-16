//! Generates split-block Bloom filter vectors for docs/10-indexes.md §5.1: typed values, and the
//! filter bytes the REFERENCE's own `vortex.bloom_filter.sbbf` accumulator builds over them.
//!
//! WHY THIS EXISTS. The spec claims our filter is bit-identical to upstream's `BloomPartial`: same
//! hash, same seed, same block choice, same salts in the same order, and the same bytes hashed per
//! dtype. A claim like that is only as good as the comparison behind it, and no file in the corpus
//! carries a Bloom filter -- upstream marks the aggregate unstable and never persists it. So the
//! reference builds the filters here, through its public `Accumulator`, and the C# side must
//! reproduce every byte.
//!
//! Output is JSON on stdout. Regenerate with:
//!
//!   cd tools/conformance-gen && cargo run --release --example gen_bloom_vectors > \
//!     ../../tests/Vorticity.Tests/Indexes/BloomVectors.json

use std::num::NonZeroU32;

use vortex::aggregate_fn::Accumulator;
use vortex::aggregate_fn::DynAccumulator;
use vortex::array::ArrayRef;
use vortex::array::IntoArray;
use vortex::array::VortexSessionExecute;
use vortex::array::arrays::PrimitiveArray;
use vortex::array::arrays::VarBinViewArray;
use vortex::dtype::DType;
use vortex::dtype::Nullability;
use vortex::dtype::PType;
use vortex::layout::layouts::zoned::aggregates::bloom_filter::BloomFilter;
use vortex::layout::layouts::zoned::aggregates::bloom_filter::BloomOptions;
use vortex::layout::layouts::zoned::aggregates::bloom_filter::HashFn;
use vortex::session::VortexSession;
use vortex::VortexSessionDefault;

/// One block (every value in it), a few, and enough that the block choice spreads: 64 blocks is
/// 2 KiB a filter, which keeps the committed file small.
const BLOCK_COUNTS: [u32; 3] = [1, 8, 64];

/// A deterministic generator: splitmix64, so the vectors never depend on a crate's RNG.
struct Mix(u64);

impl Mix {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn filter(session: &VortexSession, array: ArrayRef, dtype: DType, blocks: u32) -> anyhow::Result<String> {
    let mut ctx = session.create_execution_ctx();
    let options = BloomOptions::new(NonZeroU32::new(blocks).expect("non-zero"), HashFn::XxHash3_64);
    let mut accumulator = Accumulator::try_new(BloomFilter, options, dtype)?;
    accumulator.accumulate(&array, &mut ctx)?;
    let state = accumulator.finish()?;
    let bytes = state
        .as_binary()
        .value()
        .ok_or_else(|| anyhow::anyhow!("a bloom state is never null"))?;
    Ok(hex(bytes.as_slice()))
}

/// One case: the dtype name, the block count, every row as its hashed bytes (or null), the filter.
fn case(name: &str, dtype: &str, blocks: u32, rows: &[Option<Vec<u8>>], filter: String) -> String {
    let rows: Vec<String> = rows
        .iter()
        .map(|row| match row {
            Some(bytes) => format!("\"{}\"", hex(bytes)),
            None => "null".to_string(),
        })
        .collect();
    format!(
        "{{\"name\":\"{name}\",\"dtype\":\"{dtype}\",\"blocks\":{blocks},\"rows\":[{}],\"filter\":\"{filter}\"}}",
        rows.join(",")
    )
}

macro_rules! primitive_cases {
    ($out:expr, $session:expr, $mix:expr, $t:ty, $ptype:expr, $name:literal, $gen:expr) => {{
        let gen: fn(&mut Mix, usize) -> $t = $gen;
        for &blocks in &BLOCK_COUNTS {
            // All valid: 120 rows, some over a domain small enough to repeat.
            let values: Vec<$t> = (0..120).map(|i| gen(&mut $mix, i)).collect();
            let array = PrimitiveArray::from_iter(values.iter().copied()).into_array();
            let rows: Vec<Option<Vec<u8>>> = values.iter().map(|v| Some(v.to_le_bytes().to_vec())).collect();
            let bytes = filter(&$session, array, DType::Primitive($ptype, Nullability::NonNullable), blocks)?;
            $out.push(case(concat!($name, "_valid"), $name, blocks, &rows, bytes));

            // Nullable: every fifth row null, so a null never reaches the filter.
            let options: Vec<Option<$t>> = (0..80)
                .map(|i| if i % 5 == 0 { None } else { Some(gen(&mut $mix, i)) })
                .collect();
            let array = PrimitiveArray::from_option_iter(options.iter().copied()).into_array();
            let rows: Vec<Option<Vec<u8>>> =
                options.iter().map(|v| v.map(|v| v.to_le_bytes().to_vec())).collect();
            let bytes = filter(&$session, array, DType::Primitive($ptype, Nullability::Nullable), blocks)?;
            $out.push(case(concat!($name, "_nullable"), $name, blocks, &rows, bytes));
        }
    }};
}

fn main() -> anyhow::Result<()> {
    let session = VortexSession::default();
    let mut mix = Mix(0x5EED);
    let mut out: Vec<String> = Vec::new();

    primitive_cases!(out, session, mix, i8, PType::I8, "i8", |m, _| m.next() as i8);
    primitive_cases!(out, session, mix, u8, PType::U8, "u8", |m, _| (m.next() % 40) as u8);
    primitive_cases!(out, session, mix, i16, PType::I16, "i16", |m, _| m.next() as i16);
    primitive_cases!(out, session, mix, u16, PType::U16, "u16", |m, _| m.next() as u16);
    primitive_cases!(out, session, mix, i32, PType::I32, "i32", |m, i| if i % 3 == 0 { i as i32 } else { m.next() as i32 });
    primitive_cases!(out, session, mix, u32, PType::U32, "u32", |m, _| (m.next() % 1000) as u32);
    primitive_cases!(out, session, mix, i64, PType::I64, "i64", |m, i| if i % 4 == 0 { -(i as i64) } else { m.next() as i64 });
    primitive_cases!(out, session, mix, u64, PType::U64, "u64", |m, _| m.next());
    // Floats by bit pattern: both zeros, a NaN and the infinities are values like any other.
    primitive_cases!(out, session, mix, f32, PType::F32, "f32", |m, i| match i % 7 {
        0 => -0.0,
        1 => 0.0,
        2 => f32::NAN,
        3 => f32::INFINITY,
        _ => (m.next() % 10_000) as f32 / 8.0,
    });
    primitive_cases!(out, session, mix, f64, PType::F64, "f64", |m, i| match i % 7 {
        0 => -0.0,
        1 => 0.0,
        2 => f64::NAN,
        3 => f64::NEG_INFINITY,
        _ => (m.next() % 100_000) as f64 / 16.0,
    });

    for &blocks in &BLOCK_COUNTS {
        // Strings on both sides of the twelve-byte inline limit of a view.
        let strings: Vec<Option<String>> = (0..100)
            .map(|i| match i % 9 {
                0 => None,
                1 => Some(String::new()),
                2 | 3 => Some(format!("k{}", mix.next() % 50)),
                4 => Some(format!("twelve-bytes{}", mix.next() % 10).chars().take(12).collect()),
                _ => Some(format!("a longer value, out of line: {}", mix.next() % 500)),
            })
            .collect();
        let array = VarBinViewArray::from_iter_nullable_str(strings.iter().map(|s| s.as_deref())).into_array();
        let rows: Vec<Option<Vec<u8>>> = strings.iter().map(|s| s.as_ref().map(|s| s.as_bytes().to_vec())).collect();
        let bytes = filter(&session, array, DType::Utf8(Nullability::Nullable), blocks)?;
        out.push(case("utf8_nullable", "utf8", blocks, &rows, bytes));

        let binaries: Vec<Vec<u8>> = (0..60)
            .map(|i| (0..(i % 31)).map(|_| (mix.next() % 4) as u8).collect())
            .collect();
        let array = VarBinViewArray::from_iter_bin(binaries.iter()).into_array();
        let rows: Vec<Option<Vec<u8>>> = binaries.iter().map(|b| Some(b.clone())).collect();
        let bytes = filter(&session, array, DType::Binary(Nullability::NonNullable), blocks)?;
        out.push(case("binary_valid", "binary", blocks, &rows, bytes));
    }

    println!("[\n{}\n]", out.join(",\n"));
    Ok(())
}
