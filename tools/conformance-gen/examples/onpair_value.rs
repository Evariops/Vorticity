//! What OnPair is worth to the reference's own compressor: each column compressed by the default
//! BtrBlocks schemes, then again without OnPair, the bytes of both and the encoding each chose.
//!
//! `cargo run --release --example onpair_value`

use vortex::VortexSessionDefault;
use vortex::array::ArrayRef;
use vortex::array::IntoArray;
use vortex::array::arrays::VarBinViewArray;
use vortex::array::VortexSessionExecute;
use vortex::compressor::BtrBlocksCompressorBuilder;
use vortex::error::VortexResult;
use vortex::session::VortexSession;
use vortex_btrblocks::SchemeExt;
use vortex_btrblocks::schemes::string::OnPairScheme;

const ROWS: u64 = 65_536;

fn mix(row: u64) -> u64 {
    let mut z = row.wrapping_add(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

fn column(f: impl Fn(u64) -> String) -> Vec<String> {
    (0..ROWS).map(f).collect()
}

fn main() -> VortexResult<()> {
    let session = VortexSession::default();
    let first = ["alice", "bob", "carol", "david", "emma", "farid", "gaelle", "hugo", "ines", "jules", "karim", "lea", "marc", "nina", "omar", "paul"];
    let last = ["martin", "bernard", "dubois", "thomas", "robert", "richard", "petit", "durand", "leroy", "moreau", "simon", "laurent", "lefebvre", "michel", "garcia", "david"];
    let domains = ["gmail.com", "yahoo.fr", "outlook.com", "orange.fr", "free.fr", "lucca.fr", "hotmail.com", "proton.me"];
    let adjectives = ["Classic", "Modern", "Vintage", "Compact", "Deluxe", "Eco", "Premium", "Rustic"];
    let materials = ["Oak", "Steel", "Cotton", "Leather", "Bamboo", "Glass", "Wool", "Linen"];
    let nouns = ["Chair", "Table", "Lamp", "Shelf", "Rug", "Mirror", "Desk", "Sofa", "Stool", "Cabinet"];
    let colors = ["Black", "White", "Grey", "Navy", "Olive", "Sand", "Terracotta", "Ivory"];
    let paths = ["/api/orders", "/api/users", "/api/search", "/health", "/api/cart/items"];
    let services = ["billing", "auth", "search", "gateway", "payroll", "timesheet"];

    let datasets: Vec<(&str, Vec<String>)> = vec![
        ("urls", column(|i| format!("https://example.com/catalog/item/{}/detail?ref={}&lang=fr", (i * 7919) % 100_003, i))),
        ("log lines", column(|i| {
            let r = mix(i);
            let level = match r % 50 { 0 => "ERROR", 1..=4 => "WARN", _ => "INFO" };
            let status = match (r >> 16) % 20 { 0 => 500, 1 | 2 => 404, _ => 200 };
            let millis = 1_700_000_000_000u64 + i * 7;
            format!("2023-11-14T{:02}:{:02}:{:02}.{:03}Z {} GET {} {} in {} ms req={:06x}",
                (millis / 3_600_000) % 24, (millis / 60_000) % 60, (millis / 1000) % 60, millis % 1000,
                level, paths[((r >> 8) % 5) as usize], status, (r >> 24) % 900, r >> 40)
        })),
        ("emails", column(|i| {
            let r = mix(i);
            format!("{}.{}{}@{}", first[(r % 16) as usize], last[((r >> 8) % 16) as usize], (r >> 16) % 1000, domains[((r >> 32) % 8) as usize])
        })),
        ("uuids", column(|i| {
            let a = mix(i);
            let b = mix(!i);
            format!("{:08x}-{:04x}-4{:03x}-{:04x}-{:012x}", a >> 32, (a >> 16) & 0xffff, a & 0xfff, (b >> 48) | 0x8000, b & 0xffff_ffff_ffff)
        })),
        ("product names", column(|i| {
            let r = mix(i);
            format!("{} {} {} - {}", adjectives[(r % 8) as usize], materials[((r >> 8) % 8) as usize], nouns[((r >> 16) % 10) as usize], colors[((r >> 24) % 8) as usize])
        })),
        ("customer ids", column(|i| format!("customer-{:05}", mix(i) % 10_000))),
        ("file paths", column(|i| {
            let r = mix(i);
            format!("/var/log/app/{}/2024/{:02}/{:02}/part-{:05}.log.gz", services[(r % 6) as usize], 1 + (r >> 8) % 12, 1 + (r >> 16) % 28, (r >> 24) % 100_000)
        })),
        ("short codes", column(|i| format!("FR-{:02}-{:04}", 1 + mix(i) % 95, mix(i + 1) % 10_000))),
    ];

    let with = BtrBlocksCompressorBuilder::default().build();
    let without = BtrBlocksCompressorBuilder::default().exclude_schemes([OnPairScheme.id()]).build();
    println!("{:<14} {:>10} {:>12} {:<22} {:>12} {:<22} {:>7}", "column", "plain", "with onpair", "", "without", "", "ratio");
    for (name, values) in datasets {
        let array: ArrayRef = VarBinViewArray::from_iter_str(values.iter().map(String::as_str)).into_array();
        let plain = array.nbytes();
        let mut ctx = session.create_execution_ctx();
        let a = with.compress(&array, &mut ctx)?;
        let b = without.compress(&array, &mut ctx)?;
        println!(
            "{:<14} {:>10} {:>12} {:<22} {:>12} {:<22} {:>7.3}",
            name, plain, a.nbytes(), a.encoding_id().to_string(), b.nbytes(), b.encoding_id().to_string(),
            a.nbytes() as f64 / b.nbytes() as f64
        );
    }

    Ok(())
}
