//! Dimension B — one file per array encoding, with that encoding *forced*.
//!
//! docs/04-conformance.md §3 wants "each target encoding, forced individually … so that a single
//! encoding is exercised in isolation rather than whichever one sampling happens to pick".
//!
//! A compressor allowlist cannot deliver that. `vortex-compressor` "compresses with the best
//! scheme and verifies the result is smaller" (`compressor/mod.rs:34`), so restricting the scheme
//! set makes an encoding *possible*, never *guaranteed* — API-NOTES.md §3.0 demonstrates a
//! FoR-only configuration that produced no FoR at all on 64 rows.
//!
//! So every case here builds the array *already in the target encoding* and the caller writes it
//! with a strategy that neither repartitions, canonicalizes, nor compresses. Whether it worked is
//! then decided by reading the ids back out of the file, never by assuming: `emit::produce`
//! compares [`EncodingCase::array_id`] against the ids it walks out of the written bytes and
//! records any miss in the manifest's `missing_expected_array_ids`.

use std::sync::Arc;

use vortex::array::ArrayRef;
use vortex::array::IntoArray;
use vortex::array::VortexSessionExecute;
use vortex::array::arrays::BoolArray;
use vortex::array::arrays::ChunkedArray;
use vortex::array::arrays::ConstantArray;
use vortex::array::arrays::DecimalArray;
use vortex::array::arrays::DictArray;
use vortex::array::arrays::ExtensionArray;
use vortex::array::arrays::FixedSizeListArray;
use vortex::array::arrays::ListArray;
use vortex::array::arrays::MaskedArray;
use vortex::array::arrays::NullArray;
use vortex::array::arrays::PrimitiveArray;
use vortex::array::arrays::StructArray;
use vortex::array::arrays::VarBinArray;
use vortex::array::arrays::VarBinViewArray;
use vortex::array::arrays::VariantArray;
use vortex::array::patches::Patches;
use vortex::array::validity::Validity;
use vortex::buffer::BitBuffer;
use vortex::buffer::Buffer;
use vortex::buffer::ByteBuffer;
use vortex::dtype::DType;
use vortex::dtype::DecimalDType;
use vortex::dtype::MapDType;
use vortex::dtype::Nullability;
use vortex::dtype::PType;
use vortex::encodings::alp::RDEncoder;
use vortex::encodings::alp::RDEncoderExt;
use vortex::encodings::alp::ALP;
use vortex::encodings::alp::ALPArrayExt;
use vortex::encodings::alp::ALPArraySlotsExt;
use vortex::encodings::alp::alp_encode;
use vortex::encodings::bytebool::ByteBool;
use vortex::encodings::datetime_parts::DateTimeParts;
use vortex::encodings::decimal_byte_parts::DecimalByteParts;
use vortex::encodings::fastlanes::BitPacked;
use vortex::encodings::fastlanes::Delta;
use vortex::encodings::fastlanes::delta_compress;
use vortex::encodings::fastlanes::FoR;
use vortex::encodings::fastlanes::RLE;
use vortex::encodings::fsst::fsst_compress;
use vortex::encodings::fsst::fsst_train_compressor;
use vortex::encodings::pco::Pco;
use vortex::encodings::runend::RunEnd;
use vortex::encodings::sequence::Sequence;
use vortex::encodings::sparse::Sparse;
use vortex::encodings::zigzag::zigzag_encode;
use vortex::encodings::zstd::Zstd;
use vortex::encodings::zstd::ZstdBuffers;
use vortex::error::VortexResult;
use vortex::extension::datetime::TimeUnit;
use vortex::extension::datetime::Timestamp;
use vortex::scalar::Scalar;
use vortex::session::VortexSession;

use crate::schema::build_column_with;

const NN: Nullability = Nullability::NonNullable;
const NUL: Nullability = Nullability::Nullable;

/// Four FastLanes blocks: enough that a 1024-block encoder has real blocks to fill, small enough
/// that the corpus stays cheap. This is the length of the entry that carries the bare
/// `encodings/<id>` name.
pub const ROWS: usize = 4096;

/// Degenerate and off-block lengths every encoding is also emitted at, as `encodings/<id>_r<N>`.
///
/// A bit-packed array with zero blocks, a run-end array with a single run covering everything, a
/// dict with zero codes, an FSST array with an empty symbol table: these are where a decoder
/// divides by a block count or indexes `ends[n - 1]`, and every forced-encoding file in corpus
/// format/1 was exactly 4096 rows. A constructor that genuinely refuses one of these lengths
/// becomes a `skipped` record, not a silent omission.
pub const DEGENERATE_ROWS: &[usize] = &[0, 1, 1023, 1025];

/// Cases whose construction needs a minimum length and so are emitted only at [`ROWS`].
///
/// `Patches::new` rejects an empty index array (vortex-array-0.86.1/src/patches.rs:262), so the
/// hand-built patch shapes have nothing to express below one patch interval.
pub const FIXED_LENGTH_CASES: &[&str] = &[
    "alp_patched_no_chunk_offsets",
    "fastlanes_bitpacked_patched_no_chunk_offsets",
];

/// One forced-encoding corpus file.
pub struct EncodingCase {
    /// File id stem.
    pub id: &'static str,
    /// The array encoding id that MUST appear in the written file.
    pub array_id: &'static str,
    /// How it is forced — copied into the manifest so a reader knows what the file proves.
    pub how: &'static str,
    pub build: fn(&VortexSession, usize) -> VortexResult<ArrayRef>,
    /// Write with `disable_editions()`. Needed only by the two ids that belong to no *core*
    /// edition: the writer's per-kind allowlist rejects them otherwise, and rightly so — these
    /// files are forward-compatibility fixtures, not conformance targets. Recorded per file.
    pub disable_editions: bool,
}

// -------------------------------------------------------------------------------------------
// Canonical and structural
// -------------------------------------------------------------------------------------------

fn b_null(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    Ok(NullArray::new(rows).into_array())
}

fn b_bool(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let bits = BitBuffer::collect_bool(rows, |i| i % 3 == 0);
    Ok(BoolArray::new(bits, Validity::NonNullable).into_array())
}

fn b_primitive(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let values: Buffer<i64> = (0..rows as i64).map(|i| i.wrapping_mul(2_654_435_761)).collect();
    Ok(PrimitiveArray::new(values, Validity::NonNullable).into_array())
}

fn b_varbinview(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // Lengths straddling the 12-byte inline boundary, so both view forms are present.
    Ok(
        VarBinViewArray::from_iter_str((0..rows).map(|i| "x".repeat(i % 20)))
            .into_array(),
    )
}

fn b_varbin(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // Not canonical for Utf8 in 0.86.1 — the builders canonicalize to VarBinView — so this has to
    // be constructed directly.
    Ok(
        VarBinArray::from_iter_nonnull((0..rows).map(|i| format!("value-{i}")), DType::Utf8(NN))
            .into_array(),
    )
}

fn b_struct(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let a: Buffer<i32> = (0..rows as i32).collect();
    Ok(StructArray::try_from_iter([
        ("a", PrimitiveArray::new(a, Validity::NonNullable).into_array()),
        (
            "b",
            VarBinViewArray::from_iter_str((0..rows).map(|i| format!("s{i}"))).into_array(),
        ),
    ])?
    .into_array())
}

/// A table the compressor sees as a table: integers, floats, timestamps and strings together.
///
/// BENCH-AUDIT.md B6 / D3: every ratio in this repository is measured on ONE file of 65 536 rows
/// and five columns, or on single-encoding files of a million. Neither is what a reader meets. This
/// is the mixture -- a monotone key, a high-cardinality measure, a low-cardinality label, a price
/// and a timestamp -- at a million rows, so the compressor makes five different decisions in one
/// file and the scan pays for all of them.
fn b_table_mixed(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let key: Buffer<i64> = (0..rows as i64).collect();
    let measure: Buffer<i64> = (0..rows as i64).map(|i| i.wrapping_mul(2_654_435_761) >> 7).collect();
    let price: Buffer<f64> = (0..rows).map(|i| (i as f64) * 0.01 + 1.23).collect();
    let stamp: Buffer<i64> = (0..rows as i64).map(|i| 1_700_000_000_000 + i * 137).collect();
    let ext = Timestamp::new(TimeUnit::Milliseconds, NN).erased();

    Ok(StructArray::try_from_iter([
        ("key", PrimitiveArray::new(key, Validity::NonNullable).into_array()),
        ("measure", PrimitiveArray::new(measure, Validity::NonNullable).into_array()),
        ("price", PrimitiveArray::new(price, Validity::NonNullable).into_array()),
        (
            "label",
            // Sixteen distinct labels: a dictionary's case, and the one a column of a million
            // distinct strings would never exercise.
            VarBinViewArray::from_iter_str((0..rows).map(|i| format!("label-{:02}", i % 16)))
                .into_array(),
        ),
        (
            "name",
            // Distinct per row and long enough to straddle the inline boundary: FSST's case.
            VarBinViewArray::from_iter_str((0..rows).map(|i| format!("subject-name-{i:09}")))
                .into_array(),
        ),
        (
            "stamp",
            ExtensionArray::try_new(
                ext,
                PrimitiveArray::new(stamp, Validity::NonNullable).into_array(),
            )?
            .into_array(),
        ),
    ])?
    .into_array())
}

/// Fifty columns, so a projection can keep one of fifty.
///
/// docs/05 §3 describes the projection axis as "1 column of 50" and the only file it could run on
/// had five (BENCH-AUDIT.md B6). Keeping one column of fifty is a different question from keeping
/// one of five: it is mostly about how much of the layout tree a reader walks to decide it does not
/// need a column, and that cost does not show at all at five.
fn b_table_wide(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // ONE TWENTIETH OF THE ROWS, DELIBERATELY. Fifty columns is the point; a million rows each is
    // not, and at eight bytes a value that file is 400 MB -- more than the other fifty-one put
    // together, in a cache directory. The projection question is how much layout a reader walks to
    // decline forty-nine columns, and that does not need more rows than it needs columns.
    let rows = rows / 20;
    let mut fields: Vec<(String, ArrayRef)> = Vec::with_capacity(50);
    for column in 0..50usize {
        let values: Buffer<i64> = (0..rows as i64)
            .map(|i| i.wrapping_mul(column as i64 + 1) % 100_003)
            .collect();
        fields.push((
            format!("c{column:02}"),
            PrimitiveArray::new(values, Validity::NonNullable).into_array(),
        ));
    }

    Ok(StructArray::try_from_iter(
        fields.iter().map(|(name, array)| (name.as_str(), array.clone())),
    )?
    .into_array())
}

fn b_listview(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // ListView is the canonical encoding for `DType::List` in 0.86.1, so the builder produces it.
    let elem = Arc::new(DType::Primitive(PType::I32, NN));
    let dtype = DType::List(Arc::clone(&elem), NN);
    build_column_with(&dtype, rows, |i| {
        Ok(Scalar::list(
            Arc::clone(&elem),
            (0..(i % 4)).map(|k| Scalar::primitive((i + k) as i32, NN)).collect(),
            NN,
        ))
    })
}

fn b_list(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // The Arrow-compatible offsets form, which no builder produces.
    let mut offsets: Vec<i32> = Vec::with_capacity(rows + 1);
    let mut elements: Vec<i32> = Vec::new();
    offsets.push(0);
    for i in 0..rows {
        for k in 0..(i % 4) {
            elements.push((i + k) as i32);
        }
        offsets.push(elements.len() as i32);
    }
    let offsets = PrimitiveArray::new(Buffer::from(offsets), Validity::NonNullable).into_array();
    let elements = PrimitiveArray::new(Buffer::from(elements), Validity::NonNullable).into_array();
    Ok(ListArray::try_new(elements, offsets, Validity::NonNullable)?.into_array())
}

fn b_fixed_size_list(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let elements: Buffer<i32> = (0..(rows * 3) as i32).collect();
    Ok(FixedSizeListArray::try_new(
        PrimitiveArray::new(elements, Validity::NonNullable).into_array(),
        3,
        Validity::NonNullable,
        rows,
    )?
    .into_array())
}

fn b_decimal(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let decimal = DecimalDType::try_new(18, 4)?;
    let values: Buffer<i64> = (0..rows as i64).map(|i| i * 1_000_003 - 7).collect();
    Ok(DecimalArray::try_new(values, decimal, Validity::NonNullable)?.into_array())
}

fn b_ext(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let ext = Timestamp::new(TimeUnit::Milliseconds, NN).erased();
    let storage: Buffer<i64> = (0..rows as i64)
        .map(|i| 1_700_000_000_000i64 + i * 1_000)
        .collect();
    Ok(ExtensionArray::try_new(
        ext,
        PrimitiveArray::new(storage, Validity::NonNullable).into_array(),
    )?
    .into_array())
}

fn b_chunked(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // A top-level ChunkedArray is split by `to_array_stream()` into one stream item per chunk, so
    // it would become a chunked *layout* and never a chunked *array*. Nesting it inside a struct
    // field keeps it as a serialized child.
    // Four chunks that between them cover exactly `rows` rows, so the degenerate lengths land as
    // empty and single-row chunks rather than being rounded away.
    let mut chunks: Vec<ArrayRef> = Vec::with_capacity(4);
    let mut start = 0usize;
    for c in 0..4 {
        let end = rows * (c + 1) / 4;
        let values: Buffer<i64> = (start..end).map(|i| (c * 10_000 + i) as i64).collect();
        chunks.push(PrimitiveArray::new(values, Validity::NonNullable).into_array());
        start = end;
    }
    let dtype = chunks[0].dtype().clone();
    let chunked = ChunkedArray::try_new(chunks, dtype)?.into_array();
    Ok(StructArray::try_from_iter([("chunked", chunked)])?.into_array())
}

fn b_constant(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    Ok(ConstantArray::new(Scalar::primitive(7i64, NN), rows).into_array())
}

fn b_masked(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let values: Buffer<i32> = (0..rows as i32).collect();
    let child = PrimitiveArray::new(values, Validity::NonNullable).into_array();
    let validity = BoolArray::new(
        BitBuffer::collect_bool(rows, |i| i % 5 != 2),
        Validity::NonNullable,
    )
    .into_array();
    Ok(MaskedArray::try_new(child, Validity::Array(validity))?.into_array())
}

// -------------------------------------------------------------------------------------------
// Compressed integer
// -------------------------------------------------------------------------------------------

fn b_for(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    // A tight band far from zero: exactly what frame-of-reference is for.
    let values: Buffer<i64> = (0..rows as i64).map(|i| 1_000_000_000 + (i % 97)).collect();
    let parray = PrimitiveArray::new(values, Validity::NonNullable);
    Ok(FoR::encode(parray, &mut ctx)?.into_array())
}

fn b_bitpacked(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let values: Buffer<u32> = (0..rows as u32).map(|i| i % 1024).collect();
    let array = PrimitiveArray::new(values, Validity::NonNullable).into_array();
    Ok(BitPacked::encode(&array, 10, &mut ctx)?.into_array())
}

fn b_fastlanes_rle(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let values: Buffer<u32> = (0..rows as u32).map(|i| i / 16).collect();
    let parray = PrimitiveArray::new(values, Validity::NonNullable);
    Ok(RLE::encode(parray.as_view(), &mut ctx)?.into_array())
}

fn b_zigzag(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let values: Buffer<i32> = (0..rows as i32).map(|i| if i % 2 == 0 { i } else { -i }).collect();
    let parray = PrimitiveArray::new(values, Validity::NonNullable);
    Ok(zigzag_encode(parray.as_view())?.into_array())
}

fn b_runend(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let values: Buffer<i32> = (0..rows as i32).map(|i| i / 64).collect();
    let array = PrimitiveArray::new(values, Validity::NonNullable).into_array();
    Ok(RunEnd::encode(array, &mut ctx)?.into_array())
}

fn b_dict(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let codes: Buffer<u32> = (0..rows as u32).map(|i| i % 5).collect();
    let codes = PrimitiveArray::new(codes, Validity::NonNullable).into_array();
    let values = VarBinViewArray::from_iter_str(["", "a", "bb", "ccc", "dddd"]).into_array();
    Ok(DictArray::try_new(codes, values)?.into_array())
}

fn b_sparse(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // One patch every 64 rows over a constant fill: the shape `vortex.sparse` exists for.
    let indices: Buffer<u64> = (0..(rows / 64) as u64).map(|i| i * 64).collect();
    let values: Buffer<i32> = (0..(rows / 64) as i32).map(|i| 1_000 + i).collect();
    Ok(Sparse::try_new(
        PrimitiveArray::new(indices, Validity::NonNullable).into_array(),
        PrimitiveArray::new(values, Validity::NonNullable).into_array(),
        rows,
        Scalar::primitive(0i32, NN),
    )?
    .into_array())
}

fn b_sequence(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    Ok(Sequence::try_new_typed(1_000i64, 7i64, NN, rows)?.into_array())
}

fn b_bytebool(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let data: Vec<bool> = (0..rows).map(|i| i % 3 == 0).collect();
    Ok(ByteBool::from_vec(data, Validity::NonNullable).into_array())
}

// -------------------------------------------------------------------------------------------
// Float, string, temporal
// -------------------------------------------------------------------------------------------

fn b_alp(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let values: Buffer<f64> = (0..rows).map(|i| (i as f64) * 0.01 + 1.23).collect();
    let parray = PrimitiveArray::new(values, Validity::NonNullable);
    Ok(alp_encode(parray.as_view(), None, &mut ctx)?.into_array())
}

fn b_alprd(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // Random mantissas in a narrow exponent band: ALP cannot fit these, ALPrd splits them into a
    // small dictionary of left parts plus raw right parts.
    let mut state: u64 = 0x5EED_1234_5678_9ABC;
    let mut next = || {
        state = state.wrapping_mul(6_364_136_223_846_793_005).wrapping_add(1_442_695_040_888_963_407);
        state
    };
    let raw: Vec<f64> = (0..rows)
        .map(|_| f64::from_bits(0x3FE0_0000_0000_0000u64 | (next() >> 12)))
        .collect();
    // The encoder is trained on a sample of the values, exactly as the compressor would do.
    let sample: Vec<f64> = raw.iter().step_by(64).copied().collect();
    let parray = PrimitiveArray::new(Buffer::from(raw), Validity::NonNullable);
    Ok(RDEncoder::new(&sample).encode(parray.as_view()).into_array())
}

fn b_fsst(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let array = VarBinViewArray::from_iter_str(
        (0..rows).map(|i| format!("https://example.invalid/vortex/conformance/{i:09}")),
    )
    .into_array();
    let compressor = fsst_train_compressor(&array, &mut ctx)?;
    Ok(fsst_compress(&array, &compressor, &mut ctx)?.into_array())
}

fn b_onpair(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let array = VarBinViewArray::from_iter_str(
        (0..rows).map(|i| format!("prefix-{}-suffix-{}", i % 32, i % 7)),
    )
    .into_array();
    vortex_onpair::onpair_compress(&array, vortex_onpair::DEFAULT_CONFIG, &mut ctx)
}

fn b_datetimeparts(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let ext = Timestamp::new(TimeUnit::Milliseconds, NN).erased();
    let days: Buffer<i64> = (0..rows as i64).map(|i| 19_000 + i / 86_400).collect();
    let seconds: Buffer<i32> = (0..rows as i32).map(|i| i % 86_400).collect();
    let subseconds: Buffer<i32> = (0..rows as i32).map(|i| (i * 7) % 1_000).collect();
    Ok(DateTimeParts::try_new(
        DType::Extension(ext),
        PrimitiveArray::new(days, Validity::NonNullable).into_array(),
        PrimitiveArray::new(seconds, Validity::NonNullable).into_array(),
        PrimitiveArray::new(subseconds, Validity::NonNullable).into_array(),
    )?
    .into_array())
}

fn b_decimal_byte_parts(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let decimal = DecimalDType::try_new(18, 4)?;
    let msp: Buffer<i64> = (0..rows as i64).map(|i| i * 1_000_003 - 7).collect();
    Ok(DecimalByteParts::try_new(
        PrimitiveArray::new(msp, Validity::NonNullable).into_array(),
        decimal,
    )?
    .into_array())
}

fn b_zstd(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let vbv = VarBinViewArray::from_iter_str(
        (0..rows).map(|i| format!("zstd-compressible-payload-{}", i % 64)),
    );
    Ok(Zstd::from_var_bin_view_without_dict(&vbv, 3, 1024, &mut ctx)?.into_array())
}

fn b_pco(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let values: Buffer<i64> = (0..rows as i64).map(|i| i * 3 + 11).collect();
    let parray = PrimitiveArray::new(values, Validity::NonNullable);
    Ok(Pco::from_primitive(parray.as_view(), 8, 1024, &mut ctx)?.into_array())
}

// -------------------------------------------------------------------------------------------
// Recent members: map, variant
// -------------------------------------------------------------------------------------------

fn b_map(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let map_dtype = MapDType::try_new(DType::Utf8(NN), DType::Primitive(PType::I64, NUL), true)?;
    let dtype = DType::Map(map_dtype, NN);
    build_column_with(&dtype, rows, |i| {
        let entries = (0..(i % 3)).map(|k| {
            (
                Scalar::utf8(format!("k{:03}", k), NN),
                Scalar::primitive((i * 31 + k) as i64, NUL),
            )
        });
        Scalar::try_map(dtype.clone(), entries)
    })
}

fn b_variant(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // A logical `DType::Variant` column whose core storage is a constant scalar. The generic
    // builder refuses Variant (`builders/mod.rs:463`, `unimplemented!()`), so the array has to be
    // assembled directly.
    let core = ConstantArray::new(
        Scalar::variant(Scalar::primitive(1i32, NN)),
        rows,
    )
    .into_array();
    Ok(VariantArray::try_new(core, None)?.into_array())
}

fn b_parquet_variant(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // Minimal well-formed Parquet Variant bytes: metadata = version 1, empty dictionary; value =
    // the primitive `null`.
    let metadata = VarBinArray::from_iter_nonnull(
        (0..rows).map(|_| ByteBuffer::copy_from([0x01u8, 0x00, 0x00].as_slice())),
        DType::Binary(NN),
    )
    .into_array();
    let value = VarBinArray::from_iter_nonnull(
        (0..rows).map(|_| ByteBuffer::copy_from([0x00u8].as_slice())),
        DType::Binary(NN),
    )
    .into_array();
    Ok(
        vortex::encodings::parquet_variant::ParquetVariant::try_new(
            Validity::NonNullable,
            metadata,
            Some(value),
            None,
        )?
        .into_array(),
    )
}

/// Every array encoding docs/90-registry.md claims, one case each.
///
/// Two ids from the registry are deliberately absent and are reported as skips instead:
///
/// * `fastlanes.delta` — a real encoding with a real id, but a member of **no** core edition
///   (spec/editions/*.toml), so a conformant writer cannot emit it.
/// * `vortex.zstd_buffers` — belongs to the draft `zstd2026.02.0` family, not to core.
pub fn encoding_cases() -> Vec<EncodingCase> {
    vec![
        EncodingCase {
            id: "null",
            array_id: "vortex.null",
            how: "NullArray::new",
            build: b_null,
            disable_editions: false,
        },
        EncodingCase {
            id: "bool",
            array_id: "vortex.bool",
            how: "BoolArray::new over a BitBuffer",
            build: b_bool,
            disable_editions: false,
        },
        EncodingCase {
            id: "primitive",
            array_id: "vortex.primitive",
            how: "PrimitiveArray::new",
            build: b_primitive,
            disable_editions: false,
        },
        EncodingCase {
            id: "varbinview",
            array_id: "vortex.varbinview",
            how: "VarBinViewArray::from_iter_str, lengths straddling the 12-byte inline boundary",
            build: b_varbinview,
            disable_editions: false,
        },
        EncodingCase {
            id: "varbin",
            array_id: "vortex.varbin",
            how: "VarBinArray::from_iter_nonnull (not canonical in 0.86.1)",
            build: b_varbin,
            disable_editions: false,
        },
        EncodingCase {
            id: "struct",
            array_id: "vortex.struct",
            how: "StructArray::try_from_iter",
            build: b_struct,
            disable_editions: false,
        },
        EncodingCase {
            id: "listview",
            array_id: "vortex.listview",
            how: "canonical builder for DType::List",
            build: b_listview,
            disable_editions: false,
        },
        EncodingCase {
            id: "list",
            array_id: "vortex.list",
            how: "ListArray::try_new from explicit offsets",
            build: b_list,
            disable_editions: false,
        },
        EncodingCase {
            id: "fixed_size_list",
            array_id: "vortex.fixed_size_list",
            how: "FixedSizeListArray::try_new",
            build: b_fixed_size_list,
            disable_editions: false,
        },
        EncodingCase {
            id: "decimal",
            array_id: "vortex.decimal",
            how: "DecimalArray::try_new, decimal(18,4) over i64 storage",
            build: b_decimal,
            disable_editions: false,
        },
        EncodingCase {
            id: "ext",
            array_id: "vortex.ext",
            how: "ExtensionArray::try_new wrapping vortex.timestamp",
            build: b_ext,
            disable_editions: false,
        },
        EncodingCase {
            id: "chunked",
            array_id: "vortex.chunked",
            how: "ChunkedArray nested as a struct field (a top-level one becomes a chunked layout)",
            build: b_chunked,
            disable_editions: false,
        },
        EncodingCase {
            id: "constant",
            array_id: "vortex.constant",
            how: "ConstantArray::new",
            build: b_constant,
            disable_editions: false,
        },
        EncodingCase {
            id: "masked",
            array_id: "vortex.masked",
            how: "MaskedArray::try_new over a non-nullable child",
            build: b_masked,
            disable_editions: false,
        },
        EncodingCase {
            id: "fastlanes_for",
            array_id: "fastlanes.for",
            how: "FoR::encode over a tight band far from zero",
            build: b_for,
            disable_editions: false,
        },
        EncodingCase {
            id: "fastlanes_bitpacked",
            array_id: "fastlanes.bitpacked",
            how: "BitPacked::encode at bit_width=10 over 4 FastLanes blocks",
            build: b_bitpacked,
            disable_editions: false,
        },
        EncodingCase {
            id: "fastlanes_rle",
            array_id: "fastlanes.rle",
            how: "RLE::encode over runs of 16",
            build: b_fastlanes_rle,
            disable_editions: false,
        },
        EncodingCase {
            id: "zigzag",
            array_id: "vortex.zigzag",
            how: "zigzag_encode over alternating signs",
            build: b_zigzag,
            disable_editions: false,
        },
        EncodingCase {
            id: "runend",
            array_id: "vortex.runend",
            how: "RunEnd::encode over runs of 64",
            build: b_runend,
            disable_editions: false,
        },
        EncodingCase {
            id: "dict",
            array_id: "vortex.dict",
            how: "DictArray::try_new over 5 string values",
            build: b_dict,
            disable_editions: false,
        },
        EncodingCase {
            id: "sparse",
            array_id: "vortex.sparse",
            how: "Sparse::try_new, one patch every 64 rows over a zero fill",
            build: b_sparse,
            disable_editions: false,
        },
        EncodingCase {
            id: "sequence",
            array_id: "vortex.sequence",
            how: "Sequence::try_new_typed(base=1000, multiplier=7)",
            build: b_sequence,
            disable_editions: false,
        },
        EncodingCase {
            id: "bytebool",
            array_id: "vortex.bytebool",
            how: "ByteBoolArray::from_vec",
            build: b_bytebool,
            disable_editions: false,
        },
        EncodingCase {
            id: "alp",
            array_id: "vortex.alp",
            how: "alp_encode over two-decimal-place doubles",
            build: b_alp,
            disable_editions: false,
        },
        EncodingCase {
            id: "alprd",
            array_id: "vortex.alprd",
            how: "RDEncoder trained on a sample, over random mantissas in a narrow exponent band",
            build: b_alprd,
            disable_editions: false,
        },
        EncodingCase {
            id: "fsst",
            array_id: "vortex.fsst",
            how: "fsst_train_compressor + fsst_compress over long shared prefixes",
            build: b_fsst,
            disable_editions: false,
        },
        EncodingCase {
            id: "onpair",
            array_id: "vortex.onpair",
            how: "onpair_compress with the default config",
            build: b_onpair,
            disable_editions: false,
        },
        EncodingCase {
            id: "datetimeparts",
            array_id: "vortex.datetimeparts",
            how: "DateTimeParts::try_new from explicit days/seconds/subseconds",
            build: b_datetimeparts,
            disable_editions: false,
        },
        EncodingCase {
            id: "decimal_byte_parts",
            array_id: "vortex.decimal_byte_parts",
            how: "DecimalByteParts::try_new over a single i64 MSP array (lower_part_count == 0)",
            build: b_decimal_byte_parts,
            disable_editions: false,
        },
        EncodingCase {
            id: "zstd",
            array_id: "vortex.zstd",
            how: "Zstd::from_var_bin_view_without_dict, level 3, 1024 values per frame",
            build: b_zstd,
            disable_editions: false,
        },
        EncodingCase {
            id: "pco",
            array_id: "vortex.pco",
            how: "Pco::from_primitive, level 8",
            build: b_pco,
            disable_editions: false,
        },
        EncodingCase {
            id: "map",
            array_id: "vortex.map",
            how: "canonical builder for DType::Map (utf8 -> i64?, keys_sorted)",
            build: b_map,
            disable_editions: false,
        },
        EncodingCase {
            id: "variant",
            array_id: "vortex.variant",
            how: "VariantArray::try_new over constant variant storage",
            build: b_variant,
            disable_editions: false,
        },
        EncodingCase {
            id: "parquet_variant",
            array_id: "vortex.parquet.variant",
            how: "ParquetVariant::try_new over minimal well-formed variant metadata/value bytes",
            build: b_parquet_variant,
            disable_editions: false,
        },
        // --- shapes the compressor never elects on its own -----------------------------------
        EncodingCase {
            id: "bool_bit_offset3",
            array_id: "vortex.bool",
            how: "BoolArray over a BitBuffer sliced to start at bit 3 (metadata offset = 3)",
            build: b_bool_bit_offset3,
            disable_editions: false,
        },
        EncodingCase {
            id: "bool_bit_offset7",
            array_id: "vortex.bool",
            how: "the byte-straddling worst case: metadata offset = 7",
            build: b_bool_bit_offset7,
            disable_editions: false,
        },
        EncodingCase {
            id: "bool_bit_offset_straddle",
            array_id: "vortex.bool",
            how: "offset 5 over a byte-aligned length: the last element spills into an extra byte",
            build: b_bool_bit_offset_straddle,
            disable_editions: false,
        },
        EncodingCase {
            id: "dict_nullable_codes",
            array_id: "vortex.dict",
            how: "DictArray with Validity::Array codes over nullable values (is_nullable_codes = true)",
            build: b_dict_nullable_codes,
            disable_editions: false,
        },
        EncodingCase {
            id: "dict_nullable_values_nonnull_codes",
            array_id: "vortex.dict",
            how: "non-nullable codes over a dictionary that contains a null value",
            build: b_dict_nullable_values,
            disable_editions: false,
        },
        EncodingCase {
            id: "dict_u8_codes",
            array_id: "vortex.dict",
            how: "u8 codes over 200 distinct values",
            build: b_dict_u8_codes,
            disable_editions: false,
        },
        EncodingCase {
            id: "dict_u64_codes",
            array_id: "vortex.dict",
            how: "u64 codes over 5 values",
            build: b_dict_u64_codes,
            disable_editions: false,
        },
        EncodingCase {
            id: "alp_no_patches",
            array_id: "vortex.alp",
            how: "alp_encode over exactly representable values: 1 child, no patch slots",
            build: b_alp_no_patches,
            disable_editions: false,
        },
        EncodingCase {
            id: "alp_patched_no_chunk_offsets",
            array_id: "vortex.alp",
            how: "Patches::new(.., chunk_offsets = None) handed to ALP::new: the 3-child shape",
            build: b_alp_patched_no_chunk_offsets,
            disable_editions: false,
        },
        EncodingCase {
            id: "fastlanes_bitpacked_patched_no_chunk_offsets",
            array_id: "fastlanes.bitpacked",
            how: "BitPacked::try_new with patches carrying no chunk offsets: the 2-child shape",
            build: b_bitpacked_patched_no_chunk_offsets,
            disable_editions: false,
        },
        EncodingCase {
            id: "chunked_one_chunk",
            array_id: "vortex.chunked",
            how: "a ChunkedArray of exactly one chunk, nested as a struct field",
            build: b_chunked_one_chunk,
            disable_editions: false,
        },
        EncodingCase {
            id: "chunked_empty_chunks",
            array_id: "vortex.chunked",
            how: "zero-row chunks first, in the middle and last, around two non-empty ones",
            build: b_chunked_empty_chunks,
            disable_editions: false,
        },
        EncodingCase {
            id: "masked_all_invalid",
            array_id: "vortex.masked",
            how: "MaskedArray over a materialized child with an all-false validity array",
            build: b_masked_all_invalid,
            disable_editions: false,
        },
        EncodingCase {
            id: "masked_all_valid",
            array_id: "vortex.masked",
            how: "MaskedArray with an all-true validity array: the one a reader may optimize away",
            build: b_masked_all_valid,
            disable_editions: false,
        },
        // --- ids that belong to no core edition -------------------------------------------
        // Both are reachable only with `disable_editions()`. They are NOT 1.0 conformance
        // targets — docs/90-registry.md puts `fastlanes.delta` in no edition at all and defers
        // `vortex.zstd_buffers` to 1.1 — but "no core edition contains it" is not the same
        // claim as "this release cannot write one", and the corpus should not assert the
        // stronger one. They are the fixtures docs/04-conformance.md §6 needs: a structurally
        // valid file carrying an encoding a conformant reader may legitimately not know.
        EncodingCase {
            id: "fastlanes_delta",
            array_id: "fastlanes.delta",
            how: "delta_compress + Delta::try_new over a monotone i64 ramp, editions disabled",
            build: b_fastlanes_delta,
            disable_editions: true,
        },
        EncodingCase {
            id: "zstd_buffers",
            array_id: "vortex.zstd_buffers",
            how: "ZstdBuffers::compress at level 3 over a primitive array, editions disabled",
            build: b_zstd_buffers,
            disable_editions: true,
        },
        EncodingCase {
            id: "table_mixed",
            array_id: "vortex.struct",
            how: "six columns: monotone key, high-cardinality measure, f64 price, 16 labels, distinct names, timestamp",
            build: b_table_mixed,
            disable_editions: false,
        },
        EncodingCase {
            id: "table_wide",
            array_id: "vortex.struct",
            how: "fifty i64 columns, for the projection axis docs/05 §3 describes",
            build: b_table_wide,
            disable_editions: false,
        },
    ]
}

/// `fastlanes.delta` — adjacent deltas in 1024-element transposed chunks.
///
/// A member of no core edition, so the write needs `disable_editions()`; the encoding itself is
/// ordinary. A monotone ramp is its best case: every delta is the same small constant.
fn b_fastlanes_delta(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let values: Buffer<i64> = (0..rows as i64).map(|i| 5_000_000 + i * 7).collect();
    let parray = PrimitiveArray::new(values, Validity::NonNullable);
    let (bases, deltas) = delta_compress(&parray, &mut ctx)?;
    Ok(
        Delta::try_new(bases.into_array(), deltas.into_array(), 0, rows)?
            .into_array(),
    )
}

/// `vortex.zstd_buffers` — every top-level buffer of a wrapped array compressed independently.
///
/// Belongs to the draft `zstd2026.02.0` family rather than to core, so the write needs
/// `disable_editions()` for the same reason as `fastlanes.delta`.
fn b_zstd_buffers(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    // Compressible on purpose: a low-entropy ramp, so the compressed buffers really are smaller
    // and the fixture exercises a decompression that changes the length.
    let values: Buffer<i64> = (0..rows as i64).map(|i| i % 17).collect();
    let inner = PrimitiveArray::new(values, Validity::NonNullable).into_array();
    Ok(ZstdBuffers::compress(&inner, 3, session)?.into_array())
}

// -------------------------------------------------------------------------------------------
// Shapes the compressor never produces on its own
// -------------------------------------------------------------------------------------------

/// A `vortex.bool` whose bits start `offset` bits into their buffer.
///
/// docs/90-registry.md names the bit offset as the distinguishing feature of `vortex.bool`, and
/// the reader applies it as a *bit* index (`BoolData::try_new_from_handle`,
/// vortex-array-0.86.1/src/arrays/bool/vtable/mod.rs:187). Through corpus format/1 every bool
/// array and every bool validity child had `metadata_len == 0` — a protobuf field equal to zero is
/// not serialized, so the offset was provably 0 everywhere and a reader that ignored it, or that
/// added it to a byte index, passed the whole corpus.
fn bool_at_offset(rows: usize, offset: usize) -> VortexResult<ArrayRef> {
    let bits = BitBuffer::collect_bool(rows + offset, |i| (i * 7) % 11 < 5);
    Ok(BoolArray::try_new(bits.slice(offset..offset + rows), Validity::NonNullable)?.into_array())
}

fn b_bool_bit_offset3(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    bool_at_offset(rows, 3)
}

fn b_bool_bit_offset7(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    bool_at_offset(rows, 7)
}

/// Offset 5 over a length that is a whole number of bytes: `offset + len` crosses into one more
/// byte than `len` alone needs, so the last element lives in a byte a length-only reader never
/// reads.
fn b_bool_bit_offset_straddle(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    bool_at_offset(rows, 5)
}

/// Five dictionary values, one of them null.
fn dict_values_nullable() -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NUL);
    build_column_with(&dtype, 5, |i| {
        Ok(if i == 2 {
            Scalar::null(dtype.clone())
        } else {
            Scalar::utf8(["", "a", "<null>", "ccc", "dddd"][i], NUL)
        })
    })
}

fn dict_codes_validity(rows: usize) -> ArrayRef {
    BoolArray::new(
        BitBuffer::collect_bool(rows, |i| i % 9 != 4),
        Validity::NonNullable,
    )
    .into_array()
}

/// Nullable codes over nullable values — the combination `DictArray::try_new`'s doc comment says
/// is legal, and the one that makes `is_nullable_codes` observable.
///
/// The field is optional in the metadata because it was added after stabilisation
/// (vortex-array-0.86.1/src/arrays/dict/array.rs:35), so absent / false / true are three distinct
/// read paths and a reader that ignores it reports a null-coded row as `values[0]`.
fn b_dict_nullable_codes(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let codes: Buffer<u16> = (0..rows).map(|i| (i % 5) as u16).collect();
    let codes = PrimitiveArray::new(codes, Validity::Array(dict_codes_validity(rows))).into_array();
    Ok(DictArray::try_new(codes, dict_values_nullable()?)?.into_array())
}

/// Non-nullable codes over nullable values: the null comes from the dictionary, not the code.
fn b_dict_nullable_values(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let codes: Buffer<u16> = (0..rows).map(|i| (i % 5) as u16).collect();
    let codes = PrimitiveArray::new(codes, Validity::NonNullable).into_array();
    Ok(DictArray::try_new(codes, dict_values_nullable()?)?.into_array())
}

/// `u8` codes. Every dict in the corpus had `u16` codes, so a decoder hard-coded to a 2-byte
/// stride read every one of them correctly.
fn b_dict_u8_codes(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let vocab: Vec<String> = (0..200).map(|k| format!("v{k:03}")).collect();
    let codes: Buffer<u8> = (0..rows).map(|i| (i % 200) as u8).collect();
    let codes = PrimitiveArray::new(codes, Validity::NonNullable).into_array();
    let values = VarBinViewArray::from_iter_str(vocab).into_array();
    Ok(DictArray::try_new(codes, values)?.into_array())
}

/// `u64` codes: the other end of the same problem.
fn b_dict_u64_codes(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let codes: Buffer<u64> = (0..rows).map(|i| (i % 5) as u64).collect();
    let codes = PrimitiveArray::new(codes, Validity::NonNullable).into_array();
    let values = VarBinViewArray::from_iter_str(["", "a", "bb", "ccc", "dddd"]).into_array();
    Ok(DictArray::try_new(codes, values)?.into_array())
}

/// ALP over values that are all exactly representable at the chosen exponents: **no patches**.
///
/// Every `vortex.alp` node in corpus format/1 had exactly 4 children — encoded, indices, values,
/// chunk_offsets — so a reader that hard-codes "ALP always has patches" or "child 3 is always
/// chunk_offsets" was indistinguishable from a correct one.
fn b_alp_no_patches(session: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let values: Buffer<f64> = (0..rows).map(|i| (i % 100_000) as f64).collect();
    let parray = PrimitiveArray::new(values, Validity::NonNullable);
    Ok(alp_encode(parray.as_view(), None, &mut ctx)?.into_array())
}

/// One patch every 512 rows, attached with `chunk_offsets = None`.
///
/// `Patches` serialize as child slots, so the child count *is* the shape: 3 children means patches
/// without chunk offsets, 4 means with. The compressor only ever builds the chunked form
/// (vortex-alp-0.86.1/src/alp/compress.rs:140), so the 3-child shape has to be constructed here.
fn b_alp_patched_no_chunk_offsets(
    session: &VortexSession,
    rows: usize,
) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let values: Buffer<f64> = (0..rows).map(|i| (i % 100_000) as f64).collect();
    let parray = PrimitiveArray::new(values, Validity::NonNullable);
    let alp = alp_encode(parray.as_view(), None, &mut ctx)?;
    let encoded = alp.encoded().clone();
    let exponents = alp.exponents();

    let n = rows.div_ceil(512);
    let indices: Buffer<u64> = (0..n).map(|k| (k * 512) as u64).collect();
    let patch_values: Buffer<f64> = (0..n).map(|k| 1.0e30 + k as f64).collect();
    let patches = Patches::new(
        rows,
        0,
        PrimitiveArray::new(indices, Validity::NonNullable).into_array(),
        PrimitiveArray::new(patch_values, Validity::NonNullable).into_array(),
        None,
    )?;
    Ok(ALP::new(encoded, exponents, Some(patches)).into_array())
}

/// The same shape for `fastlanes.bitpacked`: patches present, `chunk_offsets_dtype() == None`.
///
/// That is the 2-child form of `vortex-fastlanes-0.86.1/src/bitpacking/vtable/mod.rs:211-215`,
/// which appeared nowhere in the corpus — bitpacked arrays were seen with 0, 1, 3 and 4 children
/// and never 2.
fn b_bitpacked_patched_no_chunk_offsets(
    session: &VortexSession,
    rows: usize,
) -> VortexResult<ArrayRef> {
    let mut ctx = session.create_execution_ctx();
    let values: Buffer<u32> = (0..rows).map(|i| (i % 1024) as u32).collect();
    let array = PrimitiveArray::new(values, Validity::NonNullable).into_array();
    let parts = BitPacked::into_parts(BitPacked::encode(&array, 10, &mut ctx)?);

    let n = rows.div_ceil(512);
    let indices: Buffer<u64> = (0..n).map(|k| (k * 512) as u64).collect();
    let patch_values: Buffer<u32> = (0..n).map(|k| 1_000_000 + k as u32).collect();
    let patches = Patches::new(
        rows,
        0,
        PrimitiveArray::new(indices, Validity::NonNullable).into_array(),
        PrimitiveArray::new(patch_values, Validity::NonNullable).into_array(),
        None,
    )?;
    Ok(BitPacked::try_new(
        parts.packed,
        PType::U32,
        Validity::NonNullable,
        Some(patches),
        parts.bit_width,
        rows,
        parts.offset,
    )?
    .into_array())
}

/// A materialized values buffer under an explicit **all-false** validity array.
///
/// Vortex never serializes `Validity::AllInvalid` as a child, so every all-null column in the
/// corpus collapsed to `vortex.constant`. The shape "nullable array whose values buffer is real
/// and whose validity child is entirely zero" therefore appeared in no file, and a reader with a
/// bug in the `null_count == len` short-circuit was not caught.
fn b_masked_all_invalid(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let values: Buffer<i32> = (0..rows as i32).collect();
    let child = PrimitiveArray::new(values, Validity::NonNullable).into_array();
    let validity =
        BoolArray::new(BitBuffer::collect_bool(rows, |_| false), Validity::NonNullable).into_array();
    Ok(MaskedArray::try_new(child, Validity::Array(validity))?.into_array())
}

/// A `vortex.chunked` array of exactly one chunk.
///
/// The chunked *layout* cannot be one chunk long — `ChunkedLayoutStrategy` collapses a
/// single-child layout into that child (vortex-layout-0.86.1/src/layouts/chunked/writer.rs:86) —
/// so the array is where the single-chunk row-offset translation can be exercised at all.
fn b_chunked_one_chunk(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let values: Buffer<i64> = (0..rows as i64).map(|i| i * 5 - 3).collect();
    let chunk = PrimitiveArray::new(values, Validity::NonNullable).into_array();
    let dtype = chunk.dtype().clone();
    let chunked = ChunkedArray::try_new(vec![chunk], dtype)?.into_array();
    Ok(StructArray::try_from_iter([("chunked", chunked)])?.into_array())
}

/// A `vortex.chunked` array with a zero-row chunk first, in the middle and last.
///
/// The file writer filters empty chunks out of the write stream outright
/// (vortex-file-0.86.1/src/writer.rs:270), so an empty chunk can never reach a chunked *layout*.
/// Inside a serialized array it survives, and it is the same offset arithmetic: a running sum that
/// mishandles a 0-length chunk puts every later row one chunk off.
fn b_chunked_empty_chunks(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let half = rows / 2;
    let lens = [0usize, half, 0, rows - half, 0];
    let mut chunks: Vec<ArrayRef> = Vec::with_capacity(lens.len());
    for (chunk, len) in lens.into_iter().enumerate() {
        // Each chunk's values are disjoint from every other chunk's, so a row read out of the
        // wrong chunk is a wrong value rather than a coincidence.
        let base = chunk as i64 * 100_000;
        let values: Buffer<i64> = (0..len as i64).map(|i| base + i).collect();
        chunks.push(PrimitiveArray::new(values, Validity::NonNullable).into_array());
    }
    let dtype = DType::Primitive(PType::I64, NN);
    let chunked = ChunkedArray::try_new(chunks, dtype)?.into_array();
    Ok(StructArray::try_from_iter([("chunked", chunked)])?.into_array())
}

/// Its mirror: an explicit **all-true** validity array, which is the shape a reader may wrongly
/// optimize away to "no validity" and then report the wrong nullability for.
fn b_masked_all_valid(_: &VortexSession, rows: usize) -> VortexResult<ArrayRef> {
    let values: Buffer<i32> = (0..rows as i32).collect();
    let child = PrimitiveArray::new(values, Validity::NonNullable).into_array();
    let validity =
        BoolArray::new(BitBuffer::collect_bool(rows, |_| true), Validity::NonNullable).into_array();
    Ok(MaskedArray::try_new(child, Validity::Array(validity))?.into_array())
}
