//! The footer's `field_stats`, read straight from a file's bytes.
//!
//! From Vortex 0.93 the reader hands file statistics out as aggregates (`FileStatistics`), and a
//! statistic with no aggregate function, `is_sorted`, `is_strict_sorted` or `is_constant`, has no
//! place among them (vortex-array-0.93.0/src/expr/stats/mod.rs:198). The reader also drops
//! statistics it cannot parse, with a warning, instead of failing the open
//! (vortex-file-0.93.0/src/footer/deserializer.rs:170). The legacy `field_stats`, one `ArrayStats`
//! per top-level field, is still what every writer stores and what Vorticity reads and writes, so
//! the sidecars and the cross-check read that table as it is, from the segment the postscript
//! names, and fail on a table they cannot read.
//!
//! Shared by the generator and `examples/verify_written.rs`, which includes it by path.

use vortex::array::stats::StatsSet;
use vortex::dtype::DType;
use vortex::error::VortexResult;
use vortex::error::vortex_bail;
use vortex::error::vortex_err;
use vortex::file::flatbuffers::footer::FileStatistics;
use vortex::file::flatbuffers::footer::root_as_postscript;
use vortex::session::VortexSession;

/// One `StatsSet` per top-level field of `dtype`, a single one for a non-struct root, or `None`
/// when the postscript names no statistics segment.
pub fn read_field_stats(
    bytes: &[u8],
    dtype: &DType,
    session: &VortexSession,
) -> VortexResult<Option<Vec<StatsSet>>> {
    let Some(statistics) = statistics_table(bytes)? else {
        return Ok(None);
    };

    let fields: Vec<DType> = match dtype {
        DType::Struct(fields, _) => fields.fields().collect(),
        other => vec![other.clone()],
    };
    let field_stats: Vec<_> = statistics
        .field_stats()
        .map(|table| table.iter().collect())
        .unwrap_or_default();
    if field_stats.len() != fields.len() {
        vortex_bail!(
            "field_stats holds {} entries for {} top-level fields",
            field_stats.len(),
            fields.len()
        );
    }
    field_stats
        .iter()
        .zip(&fields)
        .map(|(stats, field)| StatsSet::from_flatbuffer(stats, field, session))
        .collect::<VortexResult<Vec<_>>>()
        .map(Some)
}

/// Whether the statistics table carries the per-field aggregates of 0.93 (`entry_sets`), which a
/// writer adds only when asked to.
#[allow(dead_code)] // the generator asks; verify_written, which includes this file, does not
pub fn has_nested_statistics(bytes: &[u8]) -> VortexResult<bool> {
    Ok(statistics_table(bytes)?.is_some_and(|statistics| statistics.entry_sets().is_some()))
}

/// The statistics table the postscript names, or `None` when it names none.
fn statistics_table(bytes: &[u8]) -> VortexResult<Option<FileStatistics<'_>>> {
    // The last 8 bytes: u16 format version, u16 postscript length, `VTXF`. The postscript ends
    // where they begin.
    let n = bytes.len();
    if n < 8 || &bytes[n - 4..] != b"VTXF" {
        vortex_bail!("no VTXF end-of-file marker");
    }
    let postscript_len = usize::from(u16::from_le_bytes([bytes[n - 6], bytes[n - 5]]));
    let postscript_end = n - 8;
    let postscript_start = postscript_end
        .checked_sub(postscript_len)
        .ok_or_else(|| vortex_err!("a postscript of {postscript_len} bytes in {n}"))?;
    let postscript = root_as_postscript(&bytes[postscript_start..postscript_end])
        .map_err(|e| vortex_err!("postscript: {e}"))?;
    let Some(segment) = postscript.statistics() else {
        return Ok(None);
    };
    if segment._compression().is_some() || segment._encryption().is_some() {
        vortex_bail!("a compressed or encrypted statistics segment, which no writer produces");
    }

    let start = usize::try_from(segment.offset())?;
    let table = start
        .checked_add(segment.length() as usize)
        .and_then(|end| bytes.get(start..end))
        .ok_or_else(|| vortex_err!("the statistics segment runs past the file's {n} bytes"))?;
    flatbuffers::root::<FileStatistics>(table)
        .map(Some)
        .map_err(|e| vortex_err!("file statistics: {e}"))
}
