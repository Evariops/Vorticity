// SPDX-License-Identifier: Apache-2.0
//
// Golden row-encoding vectors, produced by the REFERENCE implementation.
//
// docs/04-conformance.md §7: "Being merely order-compatible is not enough - two implementations
// could each be internally consistent and still disagree, which would silently break any
// cross-language comparison." So this binary builds a fixed set of tables with vortex-row, encodes
// them, and writes the resulting bytes out; Vorticity builds THE SAME tables and compares byte
// for byte.
//
// The case list here and the case list in the C# test are two halves of one table and must be kept
// in step. They are kept honest by construction rather than by discipline: if the two sides build
// different inputs for a case name, the bytes differ and the test fails - loudly, naming the case.
//
// Run by hand when the pin moves, not by CI:
//
//     CARGO_NET_GIT_FETCH_WITH_CLI=true cargo run --release -- \
//         ../../tests/Vorticity.Conformance/row-vectors/vectors.jsonl
use std::env;
use std::fs::File;
use std::io::BufWriter;
use std::io::Write;
use std::sync::Arc;

use vortex_array::ArrayRef;
use vortex_array::IntoArray;
use vortex_array::VortexSessionExecute;
use vortex_array::array_session;
use vortex_array::arrays::BoolArray;
use vortex_array::arrays::DecimalArray;
use vortex_array::arrays::FixedSizeListArray;
use vortex_array::arrays::ListViewArray;
use vortex_array::arrays::NullArray;
use vortex_array::arrays::PrimitiveArray;
use vortex_array::arrays::StructArray;
use vortex_array::arrays::VarBinViewArray;
use vortex_array::arrays::listview::ListViewArrayExt;
use vortex_array::dtype::DecimalDType;
use vortex_array::dtype::FieldName;
use vortex_array::dtype::FieldNames;
use vortex_array::dtype::half::f16;
use vortex_array::validity::Validity;
use vortex_buffer::buffer;
use vortex_error::VortexResult;
use vortex_row::RowSortField;
use vortex_row::convert_columns;

/// The Vortex release these vectors were produced by. A mismatch on the C# side is a loud failure
/// rather than a silent re-baseline (docs/04-conformance.md §7, "version pinning").
const VORTEX_VERSION: &str = "0.86.1";

fn main() -> VortexResult<()> {
    let path = env::args().nth(1).unwrap_or_else(|| "vectors.jsonl".to_string());
    let file = File::create(&path).expect("cannot create the output file");
    let mut out = BufWriter::new(file);

    writeln!(out, "{{\"vortex_version\":\"{VORTEX_VERSION}\"}}").unwrap();

    let asc = RowSortField::new(false, true);
    let asc_nulls_last = RowSortField::new(false, false);
    let desc = RowSortField::new(true, true);
    let desc_nulls_last = RowSortField::new(true, false);

    let long = "x".repeat(64);
    let text: Vec<Option<&str>> = vec![
        Some(""),
        Some("a"),
        Some(&long[..31]),
        Some(&long[..32]),
        Some(&long[..33]),
        Some(&long[..64]),
        None,
    ];

    // ---------------------------------------------------------------------------- primitives
    emit(&mut out, "i8_asc", &[prim_i8()], &[asc])?;
    emit(&mut out, "i8_desc", &[prim_i8()], &[desc])?;
    emit(&mut out, "i16_asc", &[prim_i16()], &[asc])?;
    emit(&mut out, "i16_desc", &[prim_i16()], &[desc])?;
    emit(&mut out, "i32_nulls_first", &[prim_i32_opt()], &[asc])?;
    emit(&mut out, "i32_nulls_last", &[prim_i32_opt()], &[asc_nulls_last])?;
    emit(&mut out, "i32_desc_nulls_first", &[prim_i32_opt()], &[desc])?;
    emit(&mut out, "i32_desc_nulls_last", &[prim_i32_opt()], &[desc_nulls_last])?;
    emit(&mut out, "i64_asc", &[prim_i64()], &[asc])?;
    emit(&mut out, "i64_desc", &[prim_i64()], &[desc])?;
    emit(&mut out, "u8_asc", &[prim_u8()], &[asc])?;
    emit(&mut out, "u16_desc", &[prim_u16()], &[desc])?;
    emit(&mut out, "u32_nulls_last", &[prim_u32_opt()], &[asc_nulls_last])?;
    emit(&mut out, "u64_asc", &[prim_u64()], &[asc])?;
    emit(&mut out, "f16_asc", &[prim_f16()], &[asc])?;
    emit(&mut out, "f32_asc", &[prim_f32()], &[asc])?;
    emit(&mut out, "f32_desc", &[prim_f32()], &[desc])?;
    emit(&mut out, "f64_asc", &[prim_f64()], &[asc])?;
    emit(&mut out, "f64_desc", &[prim_f64()], &[desc])?;

    // --------------------------------------------------------------------------------- other
    emit(&mut out, "bool_asc", &[bools()], &[asc])?;
    emit(&mut out, "bool_desc_nulls_last", &[bools()], &[desc_nulls_last])?;
    emit(&mut out, "null_dtype", &[NullArray::new(3).into_array()], &[asc])?;
    emit(&mut out, "null_dtype_nulls_last", &[NullArray::new(3).into_array()], &[asc_nulls_last])?;

    // The block boundary, from every angle: empty, one byte, 31, 32, 33, 64, and null.
    emit(&mut out, "utf8_asc", &[utf8(&text)], &[asc])?;
    emit(&mut out, "utf8_desc", &[utf8(&text)], &[desc])?;
    emit(&mut out, "utf8_asc_nulls_last", &[utf8(&text)], &[asc_nulls_last])?;
    emit(&mut out, "utf8_desc_nulls_last", &[utf8(&text)], &[desc_nulls_last])?;
    emit(&mut out, "binary_asc", &[binary()], &[asc])?;
    emit(&mut out, "binary_desc", &[binary()], &[desc])?;

    // ------------------------------------------------------------------------------ decimals
    emit(&mut out, "decimal_p2_i8", &[decimal_p2()], &[asc])?;
    emit(&mut out, "decimal_p4_i16", &[decimal_p4()], &[asc])?;
    emit(&mut out, "decimal_p9_i32", &[decimal_p9()], &[asc])?;
    emit(&mut out, "decimal_p18_i64", &[decimal_p18()], &[asc])?;
    emit(&mut out, "decimal_p38_i128", &[decimal_p38()], &[asc])?;
    emit(&mut out, "decimal_p38_i128_desc", &[decimal_p38()], &[desc])?;
    // The narrowing case: declared precision 7 (an i32 key) stored physically as i64.
    emit(&mut out, "decimal_p7_from_i64", &[decimal_narrowing()], &[asc])?;
    emit(&mut out, "decimal_nullable", &[decimal_nullable()], &[asc_nulls_last])?;

    // ----------------------------------------------------------------------------- composites
    emit(&mut out, "struct_fixed_nullable", &[struct_fixed()], &[asc])?;
    emit(&mut out, "struct_fixed_nullable_desc", &[struct_fixed()], &[desc])?;
    emit(&mut out, "struct_varlen_nullable", &[struct_varlen()], &[asc])?;
    emit(&mut out, "struct_varlen_nullable_desc", &[struct_varlen()], &[desc_nulls_last])?;
    emit(&mut out, "struct_mixed", &[struct_mixed()], &[asc])?;
    emit(&mut out, "struct_nested", &[struct_nested()], &[desc])?;
    emit(&mut out, "fsl_i32_nullable", &[fsl_i32()], &[asc])?;
    emit(&mut out, "fsl_i32_nullable_desc", &[fsl_i32()], &[desc_nulls_last])?;
    emit(&mut out, "fsl_utf8_nullable", &[fsl_utf8()], &[asc])?;
    emit(&mut out, "fsl_zero", &[fsl_zero()], &[asc])?;

    // ------------------------------------------------------------------------- multi-column
    // Mixed directions AND mixed null placement, because they are independent and the encoder
    // gets one chance to keep them so.
    emit(
        &mut out,
        "multi_column_mixed",
        &[prim_i32_opt(), utf8(&text[..5]), bools_short()],
        &[asc, desc_nulls_last, asc_nulls_last],
    )?;

    out.flush().unwrap();
    eprintln!("wrote {path}");
    Ok(())
}

// ------------------------------------------------------------------------------- column builders

fn prim_i8() -> ArrayRef {
    PrimitiveArray::from_iter([-128i8, -1, 0, 1, 127]).into_array()
}

fn prim_i16() -> ArrayRef {
    PrimitiveArray::from_iter([-32768i16, -1, 0, 1, 32767]).into_array()
}

fn prim_i32_opt() -> ArrayRef {
    PrimitiveArray::from_option_iter([Some(1i32), None, Some(-1), Some(i32::MIN), Some(i32::MAX)])
        .into_array()
}

fn prim_i64() -> ArrayRef {
    PrimitiveArray::from_iter([i64::MIN, -1i64, 0, 1, i64::MAX]).into_array()
}

fn prim_u8() -> ArrayRef {
    PrimitiveArray::from_iter([0u8, 1, 127, 128, 255]).into_array()
}

fn prim_u16() -> ArrayRef {
    PrimitiveArray::from_iter([0u16, 1, 32767, 32768, 65535]).into_array()
}

fn prim_u32_opt() -> ArrayRef {
    PrimitiveArray::from_option_iter([Some(0u32), None, Some(u32::MAX)]).into_array()
}

fn prim_u64() -> ArrayRef {
    PrimitiveArray::from_iter([0u64, 1, u64::MAX]).into_array()
}

fn prim_f16() -> ArrayRef {
    PrimitiveArray::from_iter([
        f16::from_f32(-1.5),
        f16::from_f32(-0.0),
        f16::from_f32(0.0),
        f16::from_f32(1.5),
        f16::NEG_INFINITY,
        f16::INFINITY,
    ])
    .into_array()
}

fn prim_f32() -> ArrayRef {
    PrimitiveArray::from_iter([
        -1.5f32,
        -0.0,
        0.0,
        1.5,
        f32::NEG_INFINITY,
        f32::INFINITY,
        f32::from_bits(0x7fc0_0000),
        f32::from_bits(0xffc0_0000),
    ])
    .into_array()
}

fn prim_f64() -> ArrayRef {
    PrimitiveArray::from_iter([
        -1.5f64,
        -0.0,
        0.0,
        1.5,
        f64::NEG_INFINITY,
        f64::INFINITY,
        f64::from_bits(0x7ff8_0000_0000_0000),
        f64::from_bits(0xfff8_0000_0000_0000),
    ])
    .into_array()
}

fn bools() -> ArrayRef {
    BoolArray::from_iter([Some(false), Some(true), None]).into_array()
}

fn bools_short() -> ArrayRef {
    BoolArray::from_iter([Some(false), Some(true), None, Some(true), Some(false)]).into_array()
}

fn utf8(values: &[Option<&str>]) -> ArrayRef {
    VarBinViewArray::from_iter_nullable_str(values.iter().copied()).into_array()
}

fn binary() -> ArrayRef {
    // Bytes no Utf8 column could hold, including a leading NUL and a lone 0x80.
    let long: Vec<u8> = (0u8..40).collect();
    VarBinViewArray::from_iter_nullable_bin([
        Some(vec![]),
        Some(vec![0x00]),
        Some(vec![0xFF, 0x00, 0x80]),
        Some(long),
        None,
    ])
    .into_array()
}

fn decimal_p2() -> ArrayRef {
    DecimalArray::new(buffer![-99i8, 0, 99], DecimalDType::new(2, 1), Validity::NonNullable)
        .into_array()
}

fn decimal_p4() -> ArrayRef {
    DecimalArray::new(buffer![-9999i16, 0, 9999], DecimalDType::new(4, 2), Validity::NonNullable)
        .into_array()
}

fn decimal_p9() -> ArrayRef {
    DecimalArray::new(
        buffer![-999_999_999i32, 0, 999_999_999],
        DecimalDType::new(9, 3),
        Validity::NonNullable,
    )
    .into_array()
}

fn decimal_p18() -> ArrayRef {
    DecimalArray::new(
        buffer![-999_999_999_999_999_999i64, 0, 999_999_999_999_999_999],
        DecimalDType::new(18, 6),
        Validity::NonNullable,
    )
    .into_array()
}

fn decimal_p38() -> ArrayRef {
    let max: i128 = 99_999_999_999_999_999_999_999_999_999_999_999_999;
    DecimalArray::new(buffer![-max, 0i128, max], DecimalDType::new(38, 10), Validity::NonNullable)
        .into_array()
}

fn decimal_narrowing() -> ArrayRef {
    DecimalArray::new(buffer![484i64, -5, 91], DecimalDType::new(7, 5), Validity::NonNullable)
        .into_array()
}

fn decimal_nullable() -> ArrayRef {
    // The middle slot is null AND holds a value far too wide for the precision-7 key: a null
    // slot's backing bytes are unspecified and must not be validated.
    DecimalArray::new(
        buffer![484i64, 10_000_000_000_000, 91],
        DecimalDType::new(7, 5),
        Validity::from_iter([true, false, true]),
    )
    .into_array()
}

fn names(fields: &[&str]) -> FieldNames {
    FieldNames::from_iter(fields.iter().map(|f| FieldName::from(*f)))
}

fn struct_fixed() -> ArrayRef {
    // Live values under the null parents, so a body that leaked them would show.
    let a = PrimitiveArray::from_iter([0x1234_5678i32, 9, -1]).into_array();
    StructArray::try_new(names(&["a"]), vec![a], 3, Validity::from_iter([false, true, false]))
        .unwrap()
        .into_array()
}

fn struct_varlen() -> ArrayRef {
    let name = VarBinViewArray::from_iter_str(["short", "x", "much longer text data"]).into_array();
    StructArray::try_new(names(&["name"]), vec![name], 3, Validity::from_iter([false, true, false]))
        .unwrap()
        .into_array()
}

fn struct_mixed() -> ArrayRef {
    let a = PrimitiveArray::from_option_iter([Some(1i32), None, Some(-7), Some(0)]).into_array();
    let b =
        VarBinViewArray::from_iter_nullable_str([Some("aa"), Some(""), None, Some("z")]).into_array();
    StructArray::try_new(
        names(&["a", "b"]),
        vec![a, b],
        4,
        Validity::from_iter([true, true, true, false]),
    )
    .unwrap()
    .into_array()
}

fn struct_nested() -> ArrayRef {
    let y = PrimitiveArray::from_iter([7i32, -7]).into_array();
    let inner = StructArray::try_new(names(&["y"]), vec![y], 2, Validity::NonNullable)
        .unwrap()
        .into_array();
    StructArray::try_new(names(&["x"]), vec![inner], 2, Validity::from_iter([true, false]))
        .unwrap()
        .into_array()
}

fn fsl_i32() -> ArrayRef {
    let elements = PrimitiveArray::from_option_iter([
        Some(9i32),
        Some(9),
        Some(1),
        None,
        Some(-3),
        Some(4),
    ])
    .into_array();
    FixedSizeListArray::try_new(elements, 2, Validity::from_iter([false, true, true]), 3)
        .unwrap()
        .into_array()
}

fn fsl_utf8() -> ArrayRef {
    let elements = VarBinViewArray::from_iter_nullable_str([
        Some("a value long enough to cross the block boundary twice over, easily"),
        Some("b"),
        Some(""),
        None,
    ])
    .into_array();
    FixedSizeListArray::try_new(elements, 2, Validity::from_iter([false, true]), 2)
        .unwrap()
        .into_array()
}

fn fsl_zero() -> ArrayRef {
    let elements = PrimitiveArray::from_iter(Vec::<i32>::new()).into_array();
    FixedSizeListArray::try_new(elements, 0, Validity::from_iter([true, false, true]), 3)
        .unwrap()
        .into_array()
}

// ------------------------------------------------------------------------------------- emission

fn emit<W: Write>(
    out: &mut W,
    case: &str,
    columns: &[ArrayRef],
    fields: &[RowSortField],
) -> VortexResult<()> {
    let mut ctx = array_session().create_execution_ctx();
    let encoded = convert_columns(columns, fields, &mut ctx)?;
    let rows = collect_row_bytes(&encoded);

    let options: Vec<String> = fields
        .iter()
        .map(|f| format!("{{\"descending\":{},\"nulls_first\":{}}}", f.descending, f.nulls_first))
        .collect();
    let hex: Vec<String> = rows.iter().map(|r| format!("\"{}\"", to_hex(r))).collect();
    writeln!(
        out,
        "{{\"case\":\"{case}\",\"fields\":[{}],\"rows\":[{}]}}",
        options.join(","),
        hex.join(",")
    )
    .unwrap();
    Ok(())
}

fn collect_row_bytes(array: &ListViewArray) -> Vec<Vec<u8>> {
    let mut ctx = array_session().create_execution_ctx();
    (0..array.len())
        .map(|i| {
            let slice = array.list_elements_at(i).unwrap();
            let p = slice.execute::<PrimitiveArray>(&mut ctx).unwrap();
            p.as_slice::<u8>().to_vec()
        })
        .collect()
}

fn to_hex(bytes: &[u8]) -> String {
    let mut text = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        text.push_str(&format!("{b:02x}"));
    }
    text
}

// Silences an unused-import warning when a builder above is commented out during bring-up.
#[allow(dead_code)]
fn _arc_marker(_: Arc<u8>) {}
