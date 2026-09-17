//! Generates Parquet split-block Bloom filter vectors for docs/10-indexes.md §10: the keys, and the
//! bitset PARQUET's own `Sbbf` builds over them.
//!
//! WHY THIS EXISTS, next to `gen_bloom_vectors`. That one proves our default filter is upstream
//! Vortex's, byte for byte. This one proves the other claim of §5.1: `BloomHash.XxHash64` makes a
//! filter a Parquet reader can use unchanged. Both claims are about bits, and the only honest way
//! to make either is to have the other implementation build the bits. So `parquet`'s own `Sbbf`
//! inserts the keys here -- its hash (xxHash64, seed 0), its block choice, its eight salts in its
//! order, its sizing -- and the C# side must reproduce every byte.
//!
//! WHAT IS HASHED is the caller's business on both sides: Parquet hashes a value's plain encoding
//! (little-endian for the fixed widths, the bytes themselves for a byte array), and our builder
//! hashes the row's bytes. So the keys below are given as bytes, inserted as bytes, and recorded as
//! bytes: the vector then says nothing about either side's encoding rules and everything about the
//! filter.
//!
//! Output is JSON on stdout. Regenerate with:
//!
//!   cd tools/conformance-gen && cargo run --release --example gen_parquet_bloom_vectors > \
//!     ../../tests/Vorticity.Tests/Indexes/ParquetBloomVectors.json

use parquet::bloom_filter::Sbbf;

/// One block, a few, and enough that the block choice spreads (64 blocks is 2 KiB).
const BLOCK_COUNTS: [usize; 3] = [1, 8, 64];

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

/// Builds Parquet's filter over the keys and returns its bitset, hex.
fn filter(blocks: usize, keys: &[Vec<u8>]) -> Result<String, parquet::errors::ParquetError> {
    let mut sbbf = Sbbf::new_with_num_of_bytes(blocks * 32);
    assert_eq!(sbbf.num_blocks(), blocks, "the byte count must name the blocks exactly");
    for key in keys {
        sbbf.insert::<[u8]>(key.as_slice());
    }

    let mut bitset: Vec<u8> = Vec::new();
    sbbf.write_bitset(&mut bitset)?;
    Ok(hex(&bitset))
}

fn case(name: &str, blocks: usize, keys: &[Vec<u8>], bitset: String) -> String {
    let keys: Vec<String> = keys.iter().map(|k| format!("\"{}\"", hex(k))).collect();
    format!(
        "{{\"name\":\"{name}\",\"blocks\":{blocks},\"keys\":[{}],\"filter\":\"{bitset}\"}}",
        keys.join(",")
    )
}

fn main() -> Result<(), parquet::errors::ParquetError> {
    let mut mix = Mix(0x5EED);
    let mut out: Vec<String> = Vec::new();

    for &blocks in &BLOCK_COUNTS {
        // Fixed widths, as Parquet lays them out: INT32, INT64, FLOAT, DOUBLE.
        let i32s: Vec<Vec<u8>> = (0..120)
            .map(|i: i32| if i % 3 == 0 { i } else { mix.next() as i32 })
            .map(|v| v.to_le_bytes().to_vec())
            .collect();
        out.push(case("int32", blocks, &i32s, filter(blocks, &i32s)?));

        let i64s: Vec<Vec<u8>> = (0..120)
            .map(|i: i64| if i % 4 == 0 { -i } else { mix.next() as i64 })
            .map(|v| v.to_le_bytes().to_vec())
            .collect();
        out.push(case("int64", blocks, &i64s, filter(blocks, &i64s)?));

        // Both zeros, a NaN and an infinity are keys like any other: a filter never interprets.
        let f64s: Vec<Vec<u8>> = (0..120)
            .map(|i| match i % 7 {
                0 => -0.0f64,
                1 => 0.0,
                2 => f64::NAN,
                3 => f64::NEG_INFINITY,
                _ => (mix.next() % 100_000) as f64 / 16.0,
            })
            .map(|v| v.to_le_bytes().to_vec())
            .collect();
        out.push(case("double", blocks, &f64s, filter(blocks, &f64s)?));

        // BYTE_ARRAY, on both sides of twelve bytes and including the empty one.
        let bytes: Vec<Vec<u8>> = (0..100)
            .map(|i| match i % 5 {
                0 => Vec::new(),
                1 => format!("k{}", mix.next() % 50).into_bytes(),
                2 => format!("twelve-bytes{}", mix.next() % 10).into_bytes()[..12].to_vec(),
                _ => format!("a longer value, out of line: {}", mix.next() % 500).into_bytes(),
            })
            .collect();
        out.push(case("byte_array", blocks, &bytes, filter(blocks, &bytes)?));
    }

    println!("[{}]", out.join(",\n"));
    Ok(())
}
