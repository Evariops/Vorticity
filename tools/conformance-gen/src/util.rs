// Small self-contained helpers: a recorded PRNG, base64, hex, SHA-256 of a file.
//
// The PRNG is hand-rolled on purpose. The corpus contract is that
// two runs produce byte-identical files, which means the random stream must be reproducible
// across machines *and* across dependency updates. A crates.io RNG can change its stream in a
// minor version without breaking semver; sixteen lines of SplitMix64 cannot.

use std::path::Path;

use sha2::Digest;
use sha2::Sha256;

/// Name of the PRNG algorithm, recorded in the manifest so a future reader can reproduce it.
pub const PRNG_NAME: &str = "splitmix64";

/// SplitMix64 — Steele/Lea/Flood, as used to seed xoshiro. Deterministic, tiny, stateless apart
/// from the counter, and identical on every platform because every operation is on `u64`.
#[derive(Debug, Clone)]
pub struct Rng {
    state: u64,
}

impl Rng {
    pub fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    pub fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    /// Uniform in `[0, n)`. Uses the wide-multiply reduction, which is deterministic and has
    /// negligible bias for the small `n` this generator uses.
    pub fn below(&mut self, n: u64) -> u64 {
        if n == 0 {
            return 0;
        }
        ((u128::from(self.next_u64()) * u128::from(n)) >> 64) as u64
    }
}

/// Derive a stable per-entry seed from the master seed and the entry id, so that adding or
/// reordering entries never changes the values in an unrelated file.
pub fn entry_seed(master: u64, id: &str) -> u64 {
    // FNV-1a over the id, mixed with the master seed and run through one SplitMix64 round.
    let mut h: u64 = 0xCBF2_9CE4_8422_2325;
    for b in id.as_bytes() {
        h ^= u64::from(*b);
        h = h.wrapping_mul(0x0000_0100_0000_01B3);
    }
    Rng::new(master ^ h).next_u64()
}

const B64: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/// Standard base64 with padding. Used for every string and binary value in a sidecar, because a
/// JSON string cannot carry invalid UTF-8 and a corpus that silently repaired it would hide
/// exactly the bug we are looking for.
pub fn base64(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let b0 = u32::from(chunk[0]);
        let b1 = chunk.get(1).map_or(0, |b| u32::from(*b));
        let b2 = chunk.get(2).map_or(0, |b| u32::from(*b));
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(char::from(B64[((n >> 18) & 0x3F) as usize]));
        out.push(char::from(B64[((n >> 12) & 0x3F) as usize]));
        out.push(if chunk.len() > 1 {
            char::from(B64[((n >> 6) & 0x3F) as usize])
        } else {
            '='
        });
        out.push(if chunk.len() > 2 {
            char::from(B64[(n & 0x3F) as usize])
        } else {
            '='
        });
    }
    out
}

/// Does this base64 string decode to bytes containing a `0x00`?
///
/// Used only to answer "does the corpus contain a Utf8 value with an embedded U+0000" for the
/// coverage gate, so it decodes rather than guesses: an `A` sextet is necessary but not sufficient
/// for a zero byte, since it may be a partial sextet of a neighbouring value.
pub fn base64_has_zero_byte(b64: &str) -> bool {
    let mut acc: u32 = 0;
    let mut bits = 0u32;
    for c in b64.bytes() {
        let Some(v) = B64.iter().position(|b| *b == c) else {
            // Padding or anything unexpected: nothing more to decode.
            break;
        };
        acc = (acc << 6) | v as u32;
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            if ((acc >> bits) & 0xFF) == 0 {
                return true;
            }
        }
    }
    false
}

/// Lowercase hex, no prefix.
pub fn hex(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        out.push_str(&format!("{b:02x}"));
    }
    out
}

/// SHA-256 of a file on disk, lowercase hex.
pub fn sha256_file(path: &Path) -> std::io::Result<String> {
    Ok(sha256_bytes(&std::fs::read(path)?))
}

/// SHA-256 of bytes already in hand, lowercase hex.
pub fn sha256_bytes(bytes: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(bytes);
    hex(&hasher.finalize())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn base64_matches_rfc4648_vectors() {
        assert_eq!(base64(b""), "");
        assert_eq!(base64(b"f"), "Zg==");
        assert_eq!(base64(b"fo"), "Zm8=");
        assert_eq!(base64(b"foo"), "Zm9v");
        assert_eq!(base64(b"foob"), "Zm9vYg==");
        assert_eq!(base64(b"fooba"), "Zm9vYmE=");
        assert_eq!(base64(b"foobar"), "Zm9vYmFy");
    }

    #[test]
    fn base64_zero_byte_detection() {
        assert!(base64_has_zero_byte(&base64(b"a\0b")));
        assert!(base64_has_zero_byte(&base64(b"\0")));
        assert!(!base64_has_zero_byte(&base64(b"abc")));
        assert!(!base64_has_zero_byte(&base64("\u{00E9}\u{6F22}".as_bytes())));
        assert!(!base64_has_zero_byte(&base64(b"")));
    }

    #[test]
    fn rng_is_reproducible() {
        let a: Vec<u64> = (0..8).map(|_| Rng::new(42).next_u64()).collect();
        assert!(a.iter().all(|v| *v == a[0]));
        let mut r = Rng::new(0);
        assert_eq!(r.next_u64(), 0xE220_A839_7B1D_CDAF);
    }
}
