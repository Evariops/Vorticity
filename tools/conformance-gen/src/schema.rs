//! The type matrix and the value distributions.
//!
//! docs/04-conformance.md §3 asks for *each DType × nullability × row count*, plus an adversarial
//! set of value distributions, because the distribution is what selects the encoding.
//!
//! Two rules keep the matrix honest:
//!
//! 1. **Nullability is orthogonal to value.** Every `TypeCase::value` produces a *non-null* value;
//!    [`build_column`] injects the nulls on a fixed row pattern when the dtype is nullable. A
//!    nullable file and its non-nullable twin therefore differ in exactly one dimension.
//! 2. **Extremes live at rows 0, 1 and 2**, so every file with at least three rows carries the
//!    representation edges (`i64::MIN`, `u64::MAX`, `-0.0`, NaN, …) rather than leaving them to
//!    the distribution files, which only cover a few dtypes.

use std::sync::Arc;

use vortex::array::ArrayRef;
use vortex::array::builders::builder_with_capacity_in;
use vortex::array::memory::BufferAllocatorRef;
use vortex::dtype::DType;
use vortex::dtype::DecimalDType;
use vortex::dtype::FieldName;
use vortex::dtype::FieldNames;
use vortex::dtype::Nullability;
use vortex::dtype::PType;
use vortex::dtype::StructFields;
use vortex::dtype::extension::ExtDType;
use vortex::dtype::half::f16;
use vortex::dtype::i256;
use vortex::error::VortexResult;
use vortex::error::vortex_bail;
use vortex::error::vortex_err;
use vortex::extension::datetime::AnyTemporal;
use vortex::extension::datetime::Date;
use vortex::extension::datetime::TemporalMetadata;
use vortex::extension::datetime::Time;
use vortex::extension::datetime::TimeUnit;
use vortex::extension::datetime::Timestamp;
use vortex::extension::uuid::Uuid;
use vortex::extension::uuid::UuidMetadata;
use vortex::scalar::DecimalValue;
use vortex::scalar::Scalar;

use crate::util::Rng;

/// The row counts of docs/04-conformance.md §3. 1024 is the FastLanes block, 8192 the default row
/// block; the values either side of each are where off-by-one bugs live.
pub const ROW_COUNTS: &[usize] = &[0, 1, 1023, 1024, 1025, 8191, 8192, 8193];

/// Rows on which [`build_column`] emits a null for a nullable dtype.
///
/// Coprime with 1024 and 8192 so the null pattern never aligns with a block boundary, and dense
/// enough that every non-empty file has at least one null from row 3 on.
fn is_null_row(i: usize) -> bool {
    i % 7 == 3
}

/// One logical type in the matrix.
pub struct TypeCase {
    /// Stable id, used in file names and in the manifest.
    pub id: &'static str,
    /// The column dtype at the requested nullability.
    pub dtype: fn(Nullability) -> VortexResult<DType>,
    /// A non-null value for row `i`. Nulls are injected by [`build_column`].
    pub value: fn(&DType, usize) -> VortexResult<Scalar>,
    /// `false` only for [`DType::Null`], whose nullability is not a free parameter.
    pub nullability_is_free: bool,
}

/// Build one column of `rows` rows, injecting nulls on [`is_null_row`] when `dtype` is nullable.
pub fn build_column(
    dtype: &DType,
    rows: usize,
    value: fn(&DType, usize) -> VortexResult<Scalar>,
) -> VortexResult<ArrayRef> {
    let mut builder = builder_with_capacity_in(dtype, rows, BufferAllocatorRef::static_ref());
    // Built lazily: `Scalar::null` panics on a non-nullable dtype, and half the matrix is
    // non-nullable.
    let null = dtype.is_nullable().then(|| Scalar::null(dtype.clone()));
    for i in 0..rows {
        match &null {
            Some(null) if is_null_row(i) => builder.append_scalar(null)?,
            _ => builder.append_scalar(&value(dtype, i)?)?,
        }
    }
    Ok(builder.finish())
}

/// Build a column from an explicit per-row scalar factory (used by the distribution matrix, which
/// controls its own nulls).
pub fn build_column_with(
    dtype: &DType,
    rows: usize,
    mut value: impl FnMut(usize) -> VortexResult<Scalar>,
) -> VortexResult<ArrayRef> {
    let mut builder = builder_with_capacity_in(dtype, rows, BufferAllocatorRef::static_ref());
    for i in 0..rows {
        builder.append_scalar(&value(i)?)?;
    }
    Ok(builder.finish())
}

// -------------------------------------------------------------------------------------------
// dtype constructors
// -------------------------------------------------------------------------------------------

fn d_null(_: Nullability) -> VortexResult<DType> {
    Ok(DType::Null)
}
fn d_bool(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Bool(n))
}
macro_rules! prim_dtype {
    ($name:ident, $ptype:ident) => {
        fn $name(n: Nullability) -> VortexResult<DType> {
            Ok(DType::Primitive(PType::$ptype, n))
        }
    };
}
prim_dtype!(d_u8, U8);
prim_dtype!(d_u16, U16);
prim_dtype!(d_u32, U32);
prim_dtype!(d_u64, U64);
prim_dtype!(d_i8, I8);
prim_dtype!(d_i16, I16);
prim_dtype!(d_i32, I32);
prim_dtype!(d_i64, I64);
prim_dtype!(d_f16, F16);
prim_dtype!(d_f32, F32);
prim_dtype!(d_f64, F64);

/// Precision 2 -> `i8` storage. `DecimalType::smallest_decimal_value_type` maps 1..=2 to i8
/// (vortex-array-0.86.1/src/dtype/decimal/types.rs:47), and a reader that dispatches decimal
/// storage on byte width mis-slices a stride-1 buffer it has never seen.
fn d_decimal_2_1(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Decimal(DecimalDType::try_new(2, 1)?, n))
}
/// Precision 4 -> `i16` storage.
fn d_decimal_4_2(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Decimal(DecimalDType::try_new(4, 2)?, n))
}
fn d_decimal_9_2(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Decimal(DecimalDType::try_new(9, 2)?, n))
}
fn d_decimal_18_4(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Decimal(DecimalDType::try_new(18, 4)?, n))
}
fn d_decimal_38_10(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Decimal(DecimalDType::try_new(38, 10)?, n))
}
/// Precision 40 -> `i256` storage, the width with no BCL primitive behind it. Its values run past
/// `i128::MAX`, so the high limb is non-zero and a reader that quietly narrows to 128 bits is
/// wrong rather than merely lucky.
fn d_decimal_40_10(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Decimal(DecimalDType::try_new(40, 10)?, n))
}
fn d_utf8(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Utf8(n))
}
fn d_binary(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Binary(n))
}
fn d_list_i32(n: Nullability) -> VortexResult<DType> {
    Ok(DType::List(
        Arc::new(DType::Primitive(PType::I32, Nullability::NonNullable)),
        n,
    ))
}
fn d_list_utf8_nullable_elems(n: Nullability) -> VortexResult<DType> {
    Ok(DType::List(
        Arc::new(DType::Utf8(Nullability::Nullable)),
        n,
    ))
}
fn d_fsl_i32_3(n: Nullability) -> VortexResult<DType> {
    Ok(DType::FixedSizeList(
        Arc::new(DType::Primitive(PType::I32, Nullability::NonNullable)),
        3,
        n,
    ))
}

fn struct_of(fields: Vec<(&str, DType)>, n: Nullability) -> DType {
    let names: FieldNames = fields.iter().map(|(name, _)| FieldName::from(*name)).collect();
    let dtypes: Vec<DType> = fields.into_iter().map(|(_, dtype)| dtype).collect();
    DType::Struct(StructFields::new(names, dtypes), n)
}

fn d_struct_flat(n: Nullability) -> VortexResult<DType> {
    Ok(struct_of(
        vec![
            ("a", DType::Primitive(PType::I32, Nullability::NonNullable)),
            ("b", DType::Utf8(Nullability::Nullable)),
            ("c", DType::Bool(Nullability::NonNullable)),
        ],
        n,
    ))
}

/// Four levels of nesting, with a list at the bottom. docs/04-conformance.md §3 calls this out
/// specifically: a shallow struct exercises none of the recursive layout/child machinery.
fn d_struct_nested(n: Nullability) -> VortexResult<DType> {
    let nn = Nullability::NonNullable;
    let level3 = struct_of(
        vec![
            ("e", DType::Bool(nn)),
            ("f", DType::List(Arc::new(DType::Primitive(PType::I64, nn)), nn)),
        ],
        nn,
    );
    let level2 = struct_of(
        vec![
            ("c", DType::Utf8(Nullability::Nullable)),
            ("d", level3),
        ],
        Nullability::Nullable,
    );
    Ok(struct_of(
        vec![
            ("a", DType::Primitive(PType::I32, nn)),
            ("b", level2),
        ],
        n,
    ))
}

fn d_map_utf8_i64(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Map(
        vortex::dtype::MapDType::try_new(
            DType::Utf8(Nullability::NonNullable),
            DType::Primitive(PType::I64, Nullability::Nullable),
            true,
        )?,
        n,
    ))
}

fn d_date_days(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Extension(Date::new(TimeUnit::Days, n).erased()))
}
fn d_date_ms(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Extension(
        Date::new(TimeUnit::Milliseconds, n).erased(),
    ))
}
fn d_time_us(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Extension(
        Time::new(TimeUnit::Microseconds, n).erased(),
    ))
}
fn d_timestamp_ms(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Extension(
        Timestamp::new(TimeUnit::Milliseconds, n).erased(),
    ))
}
/// A timezone-carrying timestamp. The tz string is part of the extension metadata on the wire,
/// so a reader that drops it silently produces the wrong civil time.
fn d_timestamp_ns_tz(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Extension(
        Timestamp::new_with_tz(
            TimeUnit::Nanoseconds,
            Some(Arc::from("Europe/Paris")),
            n,
        )
        .erased(),
    ))
}
fn d_uuid(n: Nullability) -> VortexResult<DType> {
    Ok(DType::Extension(
        ExtDType::<Uuid>::try_new(
            UuidMetadata::default(),
            DType::FixedSizeList(
                Arc::new(DType::Primitive(PType::U8, Nullability::NonNullable)),
                16,
                n,
            ),
        )?
        .erased(),
    ))
}

// -------------------------------------------------------------------------------------------
// value constructors
// -------------------------------------------------------------------------------------------

fn v_null(_: &DType, _: usize) -> VortexResult<Scalar> {
    Ok(Scalar::null(DType::Null))
}

fn v_bool(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    Ok(Scalar::bool(i % 3 != 0, dtype.nullability()))
}

/// Unsigned ramp with `0`, `MAX` and `MAX/2` pinned to rows 0..2.
macro_rules! uint_value {
    ($name:ident, $t:ty) => {
        fn $name(dtype: &DType, i: usize) -> VortexResult<Scalar> {
            let v: $t = match i {
                0 => 0,
                1 => <$t>::MAX,
                2 => <$t>::MAX / 2,
                // Widened first so the multiplier is expressible for every width, then
                // truncated: the point is a value that varies per row, not the exact arithmetic.
                _ => (i as u64).wrapping_mul(2_654_435_761).wrapping_add(11) as $t,
            };
            Ok(Scalar::primitive(v, dtype.nullability()))
        }
    };
}
uint_value!(v_u8, u8);
uint_value!(v_u16, u16);
uint_value!(v_u32, u32);
uint_value!(v_u64, u64);

/// Signed ramp with `0`, `MIN` and `MAX` pinned to rows 0..2. `MIN` is the value that a naive
/// negate-then-zigzag implementation gets wrong.
macro_rules! int_value {
    ($name:ident, $t:ty) => {
        fn $name(dtype: &DType, i: usize) -> VortexResult<Scalar> {
            let v: $t = match i {
                0 => 0,
                1 => <$t>::MIN,
                2 => <$t>::MAX,
                _ => {
                    let widened = (i as i64).wrapping_mul(7).wrapping_sub(1_000);
                    (if i % 2 == 0 { widened } else { -widened }) as $t
                }
            };
            Ok(Scalar::primitive(v, dtype.nullability()))
        }
    };
}
int_value!(v_i8, i8);
int_value!(v_i16, i16);
int_value!(v_i32, i32);
int_value!(v_i64, i64);

fn v_f16(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let v = match i {
        0 => f16::from_f32(0.0),
        1 => f16::from_f32(-0.0),
        2 => f16::NAN,
        3 => f16::INFINITY,
        4 => f16::NEG_INFINITY,
        5 => f16::from_bits(0x0001), // smallest positive subnormal
        _ => f16::from_f32((i as f32) * 0.5 - 64.0),
    };
    Ok(Scalar::primitive(v, dtype.nullability()))
}

fn v_f32(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let v: f32 = match i {
        0 => 0.0,
        1 => -0.0,
        2 => f32::NAN,
        3 => f32::INFINITY,
        4 => f32::NEG_INFINITY,
        5 => f32::from_bits(0x0000_0001), // smallest positive subnormal
        6 => f32::MIN_POSITIVE,
        _ => (i as f32) * 1.25 - 512.0,
    };
    Ok(Scalar::primitive(v, dtype.nullability()))
}

fn v_f64(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let v: f64 = match i {
        0 => 0.0,
        1 => -0.0,
        2 => f64::NAN,
        3 => f64::INFINITY,
        4 => f64::NEG_INFINITY,
        5 => f64::from_bits(0x0000_0000_0000_0001), // smallest positive subnormal
        6 => f64::MIN_POSITIVE,
        _ => (i as f64) * 0.125 - 1_024.0,
    };
    Ok(Scalar::primitive(v, dtype.nullability()))
}

fn decimal_of(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let DType::Decimal(decimal, nullability) = dtype else {
        vortex_bail!("decimal value factory used on {dtype}");
    };
    // A decimal scalar is rejected if its unscaled value needs more digits than the precision
    // allows, so the extremes here are the *decimal* extremes (+/-(10^precision - 1)), not the
    // storage type's. Pinned to rows 1 and 2 as everywhere else in the matrix.
    //
    // Everything is computed in i256 and narrowed at the end, because precision 40 puts the
    // extremes an order of magnitude past `i128::MAX` and there is no smaller type that can hold
    // them.
    let precision = u32::from(decimal.precision());
    let limit: i256 = i256::from_i128(10)
        .checked_pow(precision)
        .ok_or_else(|| vortex_err!("precision {precision} is out of range"))?
        - i256::ONE;
    let unscaled: i256 = match i {
        0 => i256::ZERO,
        1 => -limit,
        2 => limit,
        _ => {
            // A third of the range plus a per-row step wrapped inside that same third: large
            // enough that at precision 40 the high 128 bits are non-zero on every row, and never
            // past the precision limit even at precision 2, where the whole range is 0..=99.
            let third = limit / i256::from_i128(3);
            let step = i256::from_i128(i as i128) % (third + i256::ONE);
            let ramp = third + step;
            if i % 2 == 0 { ramp } else { -ramp }
        }
    };
    // The narrowest storage the precision allows, matching what `builder_with_capacity_in`
    // chooses (`DecimalType::smallest_decimal_value_type`).
    let narrowed = || {
        unscaled
            .maybe_i128()
            .ok_or_else(|| vortex_err!("decimal value does not fit in i128 at precision {precision}"))
    };
    let value = match decimal.precision() {
        0..=2 => DecimalValue::I8(narrowed()? as i8),
        3..=4 => DecimalValue::I16(narrowed()? as i16),
        5..=9 => DecimalValue::I32(narrowed()? as i32),
        10..=18 => DecimalValue::I64(narrowed()? as i64),
        19..=38 => DecimalValue::I128(narrowed()?),
        _ => DecimalValue::I256(unscaled),
    };
    Ok(Scalar::decimal(value, *decimal, *nullability))
}

/// String lengths chosen for VarBinView: 0, 4, 12, 13, 16 and 31 bytes straddle the inline/
/// indirect boundary (a view inlines up to 12 bytes and otherwise stores a 4-byte prefix).
///
/// These are **byte** lengths. That distinction is the point: on ASCII the byte count and the
/// character count coincide, so a byte-vs-char confusion straddles 12/13 identically in both
/// implementations and never shows. [`nth_string`] therefore hits each length exactly while
/// varying the encoded width of the characters that fill it.
const STRING_LENGTHS: &[usize] = &[0, 1, 4, 11, 12, 13, 16, 31, 32, 64];

/// Alphabets by UTF-8 encoded width, cycled per row.
///
/// .NET strings are UTF-16, so the whole class of bug that lives in the UTF-8 -> UTF-16
/// conversion — multi-byte sequence assembly, surrogate pairs for U+10000 and above, a byte
/// length used as a char length, truncation at U+0000 — is invisible on ASCII input. Through
/// corpus format/1 every Utf8 value in every file was pure ASCII.
const ALPHABETS: &[&[char]] = &[
    // 1 byte: the original ASCII filler.
    &[
        'v', 'o', 'r', 't', 'e', 'x', '-', 'c', 'o', 'n', 'f', 'o', 'r', 'm', 'a', 'n', 'c', 'e',
        '-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
    ],
    // 2 bytes: Latin-1 supplement and Greek.
    &['\u{00E9}', '\u{00FC}', '\u{00F1}', '\u{00F8}', '\u{00E5}', '\u{03BB}', '\u{03B2}'],
    // 3 bytes: CJK, plus a combining acute that must not be split from its base.
    &['\u{6F22}', '\u{5B57}', '\u{540D}', '\u{524D}', '\u{8A66}', '\u{0301}'],
    // 4 bytes: astral plane — one UTF-16 surrogate PAIR per character.
    &['\u{1D11E}', '\u{1F600}', '\u{1F680}', '\u{10348}', '\u{2070E}'],
    // Mixed, including an embedded NUL: a C-string reader truncates here and nothing else says so.
    &['a', '\u{0000}', '\u{00E9}', '\u{6F22}', '\u{1D11E}', 'z'],
];

/// Build a string of **exactly** `len` bytes from alphabet `flavour`, padded with ASCII.
///
/// Padding is what makes the byte length exact when the alphabet's characters do not divide it,
/// and it is deliberate: a 13-byte string of 5 characters is the shape that separates a
/// byte-indexed VarBinView reader from a char-indexed one.
fn fill_to_bytes(len: usize, flavour: usize, offset: usize) -> String {
    let alphabet = ALPHABETS[flavour % ALPHABETS.len()];
    let mut s = String::with_capacity(len);
    let mut k = 0usize;
    while s.len() < len {
        let c = alphabet[(k + offset) % alphabet.len()];
        if s.len() + c.len_utf8() > len {
            break;
        }
        s.push(c);
        k += 1;
    }
    // Pad the remainder one ASCII byte at a time.
    while s.len() < len {
        s.push(char::from(b'a' + ((s.len() + offset) % 26) as u8));
    }
    s
}

fn nth_string(i: usize) -> String {
    let len = STRING_LENGTHS[i % STRING_LENGTHS.len()];
    // Rotate the alphabet independently of the length so every length class is seen at every
    // encoded width across a file of more than 50 rows.
    fill_to_bytes(len, i / STRING_LENGTHS.len(), i)
}

fn v_utf8(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    Ok(Scalar::utf8(nth_string(i), dtype.nullability()))
}

fn v_binary(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let len = STRING_LENGTHS[i % STRING_LENGTHS.len()];
    // Deliberately includes 0x00 and bytes that are invalid UTF-8, which is why the sidecar
    // encodes binary as base64 rather than as a JSON string.
    let bytes: Vec<u8> = (0..len)
        .map(|k| ((k * 31 + i * 17) % 256) as u8)
        .collect();
    Ok(Scalar::binary(bytes, dtype.nullability()))
}

fn v_list_i32(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let DType::List(elem, nullability) = dtype else {
        vortex_bail!("list value factory used on {dtype}");
    };
    // Lengths 0..3 so empty lists are covered and are distinct from null lists.
    let n = i % 4;
    let children = (0..n)
        .map(|k| Scalar::primitive((i * 10 + k) as i32, elem.nullability()))
        .collect();
    Ok(Scalar::list(Arc::clone(elem), children, *nullability))
}

fn v_list_utf8_nullable_elems(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let DType::List(elem, nullability) = dtype else {
        vortex_bail!("list value factory used on {dtype}");
    };
    let n = i % 4;
    let children = (0..n)
        .map(|k| {
            if (i + k) % 5 == 2 {
                Scalar::null(elem.as_ref().clone())
            } else {
                Scalar::utf8(nth_string(i + k), elem.nullability())
            }
        })
        .collect();
    Ok(Scalar::list(Arc::clone(elem), children, *nullability))
}

fn v_fsl_i32_3(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let DType::FixedSizeList(elem, size, nullability) = dtype else {
        vortex_bail!("fixed-size-list value factory used on {dtype}");
    };
    let children = (0..*size as usize)
        .map(|k| Scalar::primitive((i as i32) * 3 + k as i32, elem.nullability()))
        .collect();
    Ok(Scalar::fixed_size_list(
        Arc::clone(elem),
        children,
        *nullability,
    ))
}

fn v_struct_flat(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let DType::Struct(fields, _) = dtype else {
        vortex_bail!("struct value factory used on {dtype}");
    };
    let b_dtype = fields.field_by_index(1).expect("field b");
    let children = vec![
        Scalar::primitive(i as i32 - 7, Nullability::NonNullable),
        if i % 4 == 1 {
            Scalar::null(b_dtype)
        } else {
            Scalar::utf8(nth_string(i), Nullability::Nullable)
        },
        Scalar::bool(i % 2 == 0, Nullability::NonNullable),
    ];
    Ok(Scalar::struct_(dtype.clone(), children))
}

fn v_struct_nested(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let DType::Struct(fields, _) = dtype else {
        vortex_bail!("struct value factory used on {dtype}");
    };
    let nn = Nullability::NonNullable;
    let b_dtype = fields.field_by_index(1).expect("field b");
    let b = if i % 6 == 5 {
        // A null at an intermediate level: the whole subtree below it must read back as null.
        Scalar::null(b_dtype)
    } else {
        let DType::Struct(b_fields, _) = &b_dtype else {
            vortex_bail!("expected nested struct, got {b_dtype}");
        };
        let c_dtype = b_fields.field_by_index(0).expect("field c");
        let d_dtype = b_fields.field_by_index(1).expect("field d");
        let DType::Struct(d_fields, _) = &d_dtype else {
            vortex_bail!("expected nested struct, got {d_dtype}");
        };
        let f_dtype = d_fields.field_by_index(1).expect("field f");
        let DType::List(f_elem, f_null) = &f_dtype else {
            vortex_bail!("expected list, got {f_dtype}");
        };
        let f = Scalar::list(
            Arc::clone(f_elem),
            (0..(i % 3))
                .map(|k| Scalar::primitive((i * 100 + k) as i64, nn))
                .collect(),
            *f_null,
        );
        let d = Scalar::struct_(d_dtype.clone(), vec![Scalar::bool(i % 3 == 0, nn), f]);
        let c = if i % 5 == 4 {
            Scalar::null(c_dtype)
        } else {
            Scalar::utf8(nth_string(i + 3), Nullability::Nullable)
        };
        Scalar::struct_(b_dtype.clone(), vec![c, d])
    };
    Ok(Scalar::struct_(
        dtype.clone(),
        vec![Scalar::primitive(i as i32, nn), b],
    ))
}

fn v_map_utf8_i64(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let DType::Map(map, _) = dtype else {
        vortex_bail!("map value factory used on {dtype}");
    };
    let value_dtype = map.value_dtype();
    // Keys are sorted because the dtype asserts `keys_sorted`.
    let n = i % 3;
    let mut entries: Vec<(Scalar, Scalar)> = (0..n)
        .map(|k| {
            (
                Scalar::utf8(format!("k{:04}", i % 97 + k), Nullability::NonNullable),
                if (i + k) % 4 == 3 {
                    Scalar::null(value_dtype.clone())
                } else {
                    Scalar::primitive((i * 31 + k) as i64, Nullability::Nullable)
                },
            )
        })
        .collect();
    entries.sort_by(|a, b| {
        a.0.as_utf8()
            .value()
            .map(|s| s.as_str().to_string())
            .cmp(&b.0.as_utf8().value().map(|s| s.as_str().to_string()))
    });
    Scalar::try_map(dtype.clone(), entries)
}

/// Extension values.
///
/// Each extension type validates its storage value, so the magnitudes are not interchangeable:
/// a `vortex.time` value must stay inside one day *in its own unit* (a timestamp-sized number of
/// microseconds overflows it), and `vortex.date` stores days as `i32` but milliseconds as `i64`.
fn v_extension(dtype: &DType, i: usize) -> VortexResult<Scalar> {
    let DType::Extension(ext) = dtype else {
        vortex_bail!("extension value factory used on {dtype}");
    };
    let storage_nullability = ext.storage_dtype().nullability();

    if let Some(temporal) = ext.metadata_opt::<AnyTemporal>() {
        let unit = temporal.time_unit();
        let per_second: i64 = match unit {
            TimeUnit::Nanoseconds => 1_000_000_000,
            TimeUnit::Microseconds => 1_000_000,
            TimeUnit::Milliseconds => 1_000,
            TimeUnit::Seconds => 1,
            TimeUnit::Days => 0, // handled per-variant below
        };
        let storage = match temporal {
            TemporalMetadata::Date(_) => match unit {
                // Days since the epoch, stored as i32.
                TimeUnit::Days => {
                    Scalar::primitive(19_000i32.wrapping_add(i as i32), storage_nullability)
                }
                // Milliseconds since the epoch, day-aligned so the value is a real date.
                _ => Scalar::primitive(
                    (19_000i64 + i as i64) * 86_400_000,
                    storage_nullability,
                ),
            },
            TemporalMetadata::Time(_) => {
                // Strictly inside one day, in this unit.
                let day = 86_400i64.saturating_mul(per_second.max(1));
                Scalar::primitive(
                    (i as i64).wrapping_mul(7).rem_euclid(day),
                    storage_nullability,
                )
            }
            TemporalMetadata::Timestamp(..) => {
                // 2023-11-14T22:13:20Z plus one step per row, expressed in this unit. Even at
                // nanosecond resolution this is 1.7e18, comfortably inside i64.
                let base = 1_700_000_000i64.saturating_mul(per_second.max(1));
                Scalar::primitive(
                    base.wrapping_add((i as i64).wrapping_mul(per_second.max(1) / 1_000 + 1)),
                    storage_nullability,
                )
            }
        };
        return Ok(Scalar::extension_ref(ext.clone(), storage));
    }

    match ext.storage_dtype() {
        // vortex.uuid: 16 big-endian bytes.
        DType::FixedSizeList(_, 16, n) => {
            let bytes: Vec<Scalar> = (0..16)
                .map(|k| Scalar::primitive(((i * 16 + k) % 251) as u8, Nullability::NonNullable))
                .collect();
            let storage = Scalar::fixed_size_list(
                Arc::new(DType::Primitive(PType::U8, Nullability::NonNullable)),
                bytes,
                *n,
            );
            Ok(Scalar::extension_ref(ext.clone(), storage))
        }
        other => vortex_bail!("no value factory for extension {} over {other}", ext.id()),
    }
}

/// The type matrix of docs/04-conformance.md §3.
///
/// `DType::Union` is deliberately absent: `vortex.union` belongs to no core edition
/// (spec/editions/*.toml), so the writer rejects it and no conformant file can contain one. The
/// omission is recorded as a skip in the manifest rather than silently dropped.
pub fn type_matrix() -> Vec<TypeCase> {
    vec![
        TypeCase {
            id: "null",
            dtype: d_null,
            value: v_null,
            nullability_is_free: false,
        },
        TypeCase {
            id: "bool",
            dtype: d_bool,
            value: v_bool,
            nullability_is_free: true,
        },
        TypeCase {
            id: "u8",
            dtype: d_u8,
            value: v_u8,
            nullability_is_free: true,
        },
        TypeCase {
            id: "u16",
            dtype: d_u16,
            value: v_u16,
            nullability_is_free: true,
        },
        TypeCase {
            id: "u32",
            dtype: d_u32,
            value: v_u32,
            nullability_is_free: true,
        },
        TypeCase {
            id: "u64",
            dtype: d_u64,
            value: v_u64,
            nullability_is_free: true,
        },
        TypeCase {
            id: "i8",
            dtype: d_i8,
            value: v_i8,
            nullability_is_free: true,
        },
        TypeCase {
            id: "i16",
            dtype: d_i16,
            value: v_i16,
            nullability_is_free: true,
        },
        TypeCase {
            id: "i32",
            dtype: d_i32,
            value: v_i32,
            nullability_is_free: true,
        },
        TypeCase {
            id: "i64",
            dtype: d_i64,
            value: v_i64,
            nullability_is_free: true,
        },
        TypeCase {
            id: "f16",
            dtype: d_f16,
            value: v_f16,
            nullability_is_free: true,
        },
        TypeCase {
            id: "f32",
            dtype: d_f32,
            value: v_f32,
            nullability_is_free: true,
        },
        TypeCase {
            id: "f64",
            dtype: d_f64,
            value: v_f64,
            nullability_is_free: true,
        },
        TypeCase {
            id: "decimal2_1",
            dtype: d_decimal_2_1,
            value: decimal_of,
            nullability_is_free: true,
        },
        TypeCase {
            id: "decimal4_2",
            dtype: d_decimal_4_2,
            value: decimal_of,
            nullability_is_free: true,
        },
        TypeCase {
            id: "decimal9_2",
            dtype: d_decimal_9_2,
            value: decimal_of,
            nullability_is_free: true,
        },
        TypeCase {
            id: "decimal18_4",
            dtype: d_decimal_18_4,
            value: decimal_of,
            nullability_is_free: true,
        },
        TypeCase {
            id: "decimal38_10",
            dtype: d_decimal_38_10,
            value: decimal_of,
            nullability_is_free: true,
        },
        TypeCase {
            id: "decimal40_10",
            dtype: d_decimal_40_10,
            value: decimal_of,
            nullability_is_free: true,
        },
        TypeCase {
            id: "utf8",
            dtype: d_utf8,
            value: v_utf8,
            nullability_is_free: true,
        },
        TypeCase {
            id: "binary",
            dtype: d_binary,
            value: v_binary,
            nullability_is_free: true,
        },
        TypeCase {
            id: "list_i32",
            dtype: d_list_i32,
            value: v_list_i32,
            nullability_is_free: true,
        },
        TypeCase {
            id: "list_utf8_nullable_elems",
            dtype: d_list_utf8_nullable_elems,
            value: v_list_utf8_nullable_elems,
            nullability_is_free: true,
        },
        TypeCase {
            id: "fsl_i32_3",
            dtype: d_fsl_i32_3,
            value: v_fsl_i32_3,
            nullability_is_free: true,
        },
        TypeCase {
            id: "map_utf8_i64",
            dtype: d_map_utf8_i64,
            value: v_map_utf8_i64,
            nullability_is_free: true,
        },
        TypeCase {
            id: "struct_flat",
            dtype: d_struct_flat,
            value: v_struct_flat,
            nullability_is_free: true,
        },
        TypeCase {
            id: "struct_nested_deep",
            dtype: d_struct_nested,
            value: v_struct_nested,
            nullability_is_free: true,
        },
        TypeCase {
            id: "date_days",
            dtype: d_date_days,
            value: v_extension,
            nullability_is_free: true,
        },
        TypeCase {
            id: "date_ms",
            dtype: d_date_ms,
            value: v_extension,
            nullability_is_free: true,
        },
        TypeCase {
            id: "time_us",
            dtype: d_time_us,
            value: v_extension,
            nullability_is_free: true,
        },
        TypeCase {
            id: "timestamp_ms",
            dtype: d_timestamp_ms,
            value: v_extension,
            nullability_is_free: true,
        },
        TypeCase {
            id: "timestamp_ns_tz",
            dtype: d_timestamp_ns_tz,
            value: v_extension,
            nullability_is_free: true,
        },
        TypeCase {
            id: "uuid",
            dtype: d_uuid,
            value: v_extension,
            nullability_is_free: true,
        },
    ]
}

// -------------------------------------------------------------------------------------------
// Dimension C — value distributions
// -------------------------------------------------------------------------------------------

/// One adversarial value distribution. The distribution is what selects the encoding, so these
/// are not "extra data": they are the only way several encodings are ever reached.
pub struct Distribution {
    pub id: &'static str,
    pub description: &'static str,
    /// Builds the column. `rows` is the requested length; `rng` is seeded per entry.
    pub build: fn(rows: usize, rng: &mut Rng) -> VortexResult<ArrayRef>,
}

const NN: Nullability = Nullability::NonNullable;
const NUL: Nullability = Nullability::Nullable;

fn dist_constant_i64(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::I64, NN);
    build_column_with(&dtype, rows, |_| Ok(Scalar::primitive(42i64, NN)))
}

fn dist_long_runs_i32(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::I32, NN);
    build_column_with(&dtype, rows, |i| {
        Ok(Scalar::primitive((i / 257) as i32, NN))
    })
}

fn dist_short_runs_i32(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::I32, NN);
    build_column_with(&dtype, rows, |i| Ok(Scalar::primitive((i / 3) as i32, NN)))
}

fn dist_low_cardinality_utf8(rows: usize, rng: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NN);
    let vocab = ["alpha", "beta", "gamma", "delta"];
    build_column_with(&dtype, rows, |_| {
        Ok(Scalar::utf8(vocab[rng.below(4) as usize], NN))
    })
}

fn dist_high_cardinality_utf8(rows: usize, rng: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NN);
    build_column_with(&dtype, rows, |i| {
        Ok(Scalar::utf8(format!("{i}-{:016x}", rng.next_u64()), NN))
    })
}

fn dist_high_cardinality_i64(rows: usize, rng: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::I64, NN);
    build_column_with(&dtype, rows, |_| {
        Ok(Scalar::primitive(rng.next_u64() as i64, NN))
    })
}

fn dist_sorted_i64(rows: usize, rng: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::I64, NN);
    let mut acc: i64 = -1_000_000;
    build_column_with(&dtype, rows, |_| {
        acc = acc.wrapping_add(rng.below(16) as i64);
        Ok(Scalar::primitive(acc, NN))
    })
}

fn dist_reverse_sorted_i64(rows: usize, rng: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::I64, NN);
    let mut acc: i64 = 1_000_000;
    build_column_with(&dtype, rows, |_| {
        acc = acc.wrapping_sub(rng.below(16) as i64);
        Ok(Scalar::primitive(acc, NN))
    })
}

fn dist_sequence_i64(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    // A perfect arithmetic progression: what `vortex.sequence` exists for.
    let dtype = DType::Primitive(PType::I64, NN);
    build_column_with(&dtype, rows, |i| {
        Ok(Scalar::primitive(1_000i64 + 7 * i as i64, NN))
    })
}

fn dist_mostly_null_i64(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::I64, NUL);
    build_column_with(&dtype, rows, |i| {
        Ok(if i % 97 == 0 {
            Scalar::primitive(i as i64, NUL)
        } else {
            Scalar::null(dtype.clone())
        })
    })
}

fn dist_all_null_i64(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::I64, NUL);
    build_column_with(&dtype, rows, |_| Ok(Scalar::null(dtype.clone())))
}

fn dist_all_null_utf8(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NUL);
    build_column_with(&dtype, rows, |_| Ok(Scalar::null(dtype.clone())))
}

/// Every float special the spec names, cycled so each appears on both sides of every block
/// boundary: NaN (two payloads), ±Inf, −0.0, subnormals.
fn dist_float_specials_f64(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::F64, NN);
    let specials: Vec<f64> = vec![
        0.0,
        -0.0,
        f64::NAN,
        f64::from_bits(0x7FF8_0000_0000_0001), // quiet NaN, non-zero payload
        f64::from_bits(0xFFF8_0000_0000_0000), // negative NaN
        f64::INFINITY,
        f64::NEG_INFINITY,
        f64::from_bits(0x0000_0000_0000_0001), // smallest subnormal
        f64::from_bits(0x000F_FFFF_FFFF_FFFF), // largest subnormal
        f64::MIN_POSITIVE,
        f64::MIN,
        f64::MAX,
        f64::EPSILON,
        1.0,
        -1.0,
    ];
    build_column_with(&dtype, rows, |i| {
        Ok(Scalar::primitive(specials[i % specials.len()], NN))
    })
}

fn dist_float_specials_f32(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::F32, NN);
    let specials: Vec<f32> = vec![
        0.0,
        -0.0,
        f32::NAN,
        f32::from_bits(0x7FC0_0001),
        f32::from_bits(0xFFC0_0000),
        f32::INFINITY,
        f32::NEG_INFINITY,
        f32::from_bits(0x0000_0001),
        f32::from_bits(0x007F_FFFF),
        f32::MIN_POSITIVE,
        f32::MIN,
        f32::MAX,
        f32::EPSILON,
        1.0,
        -1.0,
    ];
    build_column_with(&dtype, rows, |i| {
        Ok(Scalar::primitive(specials[i % specials.len()], NN))
    })
}

/// Two-decimal-place values: exactly the shape ALP is designed for.
fn dist_alp_friendly_f64(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::F64, NN);
    build_column_with(&dtype, rows, |i| {
        Ok(Scalar::primitive((i as f64) * 0.01 + 1.23, NN))
    })
}

/// Full-entropy mantissas, which ALP cannot fit and ALPrd is meant to catch.
fn dist_denormal_heavy_f64(rows: usize, rng: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Primitive(PType::F64, NN);
    build_column_with(&dtype, rows, |i| {
        // A narrow exponent band with random mantissas: high left-part sharing, random right part.
        let bits = 0x3FE0_0000_0000_0000u64 | (rng.next_u64() >> 12) | ((i as u64 & 0x3) << 52);
        Ok(Scalar::primitive(f64::from_bits(bits), NN))
    })
}

/// Long shared prefixes: the FSST case.
fn dist_repeated_prefix_utf8(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NN);
    build_column_with(&dtype, rows, |i| {
        Ok(Scalar::utf8(
            format!("https://example.invalid/vortex/conformance/item/{i:09}"),
            NN,
        ))
    })
}

/// Every VarBinView length class, including the empty string, which must stay distinct from null.
fn dist_varbinview_lengths_utf8(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NUL);
    let lengths = [0usize, 4, 12, 13, 16, 31];
    build_column_with(&dtype, rows, |i| {
        if i % 11 == 7 {
            return Ok(Scalar::null(dtype.clone()));
        }
        let len = lengths[i % lengths.len()];
        Ok(Scalar::utf8("x".repeat(len), NUL))
    })
}

/// Non-ASCII from end to end: 2-, 3- and 4-byte sequences, a combining mark, an embedded U+0000.
///
/// Byte lengths straddle both VarBinView boundaries (12/13 and 31/32) while the character counts
/// do not, which is what makes a byte-vs-char confusion fail rather than agree. The sidecar
/// records `len` and `char_count` separately for exactly this column.
fn dist_unicode_utf8(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NUL);
    build_column_with(&dtype, rows, |i| {
        if i % 23 == 11 {
            return Ok(Scalar::null(dtype.clone()));
        }
        // Skip the pure-ASCII alphabet: this distribution is the one that must never be ASCII.
        let flavour = 1 + (i / STRING_LENGTHS.len()) % (ALPHABETS.len() - 1);
        Ok(Scalar::utf8(
            fill_to_bytes(STRING_LENGTHS[i % STRING_LENGTHS.len()], flavour, i),
            NUL,
        ))
    })
}

/// Strings longer than the 64-byte zone-map bound, whose maximum has no representable upper bound.
///
/// Two things have zero coverage without this. (1) Every string in the corpus fits inside
/// `vortex.bounded_min(64)` / `vortex.bounded_max(64)`, so every zone bound is really an exact
/// value and the `unknown: true` branch — a bound that is *not* a maximum — never appears.
/// (2) `Precision::Inexact` on file statistics had exactly one witness corpus-wide.
///
/// U+007F is the largest one-byte scalar: incrementing it yields U+0080, which is two bytes, and
/// `BufferString::increment` refuses to change a character's encoded width
/// (vortex-array-0.86.1/src/scalar/typed_view/utf8.rs:171). So a value whose 64-byte prefix ends
/// in U+007F has no upper bound inside the limit, and the aggregate stores `unknown`.
fn dist_over_bound_utf8(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NN);
    build_column_with(&dtype, rows, |i| {
        // 65..=104 copies of U+007F: every value is past the 64-byte bound, so the minimum is
        // truncated (inexact) and the maximum is unbounded (unknown).
        Ok(Scalar::utf8("\u{007F}".repeat(65 + i % 40), NN))
    })
}

fn dist_empty_strings(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NUL);
    build_column_with(&dtype, rows, |i| {
        Ok(if i % 3 == 0 {
            Scalar::null(dtype.clone())
        } else {
            Scalar::utf8("", NUL)
        })
    })
}

/// A single string well over 1 MiB, which docs/04-conformance.md §3 calls out explicitly.
fn dist_huge_string(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NN);
    build_column_with(&dtype, rows, |i| {
        Ok(if i == 0 {
            Scalar::utf8("A".repeat(1_100_000), NN)
        } else {
            Scalar::utf8(format!("small-{i}"), NN)
        })
    })
}

fn dist_single_value_dictionary(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NN);
    build_column_with(&dtype, rows, |_| Ok(Scalar::utf8("only-one-value", NN)))
}

/// Sorted, with every batch's value set disjoint from every other's.
///
/// THE ADVERSARIAL CASE FOR A SHARED DICTIONARY, and it is adversarial to the *decision rule*
/// rather than to the encoding. The rule for committing a column to a dictionary shared across
/// chunks reads batch 1's distinct-per-row ratio and treats it as an upper bound on the whole
/// column's. That much this column obeys — 0.1252 in batch 1 against 0.1251 over the column. It is
/// still the wrong question, and this file exists to make that visible rather than arguable.
///
/// Measured over the corpus file, against the closest thing already in the corpus:
///
/// | | distinct per batch | column distinct | a shared dictionary saves |
/// |---|---|---|---|
/// | `sorted_disjoint_utf8_r8193` | 513, 513 | 1025 | **one entry** |
/// | `types/binary_nonnull_r8193` | 1153, 1153 | 1153 | **1153 entries** |
///
/// The batches here are disjoint; there, they are identical. A shared dictionary is paid for by
/// deduplication ACROSS chunks, and distinct-per-row measures repetition WITHIN a batch. The two
/// are independent: this column's batch-1 ratio is 0.125 against the other's 0.281 — it looks the
/// MORE dictionary-friendly of the two by the rule that decides — and sharing buys it nothing.
///
/// Nothing observable in batch 1 predicts cross-batch overlap, so no refinement of a batch-1 ratio
/// can separate these two files. A column of timestamped strings is enough to produce the shape,
/// which is why it is worth a corpus file: sorted-by-time data is ordinary, not exotic.
fn dist_sorted_disjoint_utf8(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Utf8(NN);
    build_column_with(&dtype, rows, |i| {
        Ok(Scalar::utf8(format!("2024-06-01T00:{:07}Z", i / 8), NN))
    })
}

fn dist_bool_alternating(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Bool(NUL);
    build_column_with(&dtype, rows, |i| {
        Ok(match i % 5 {
            4 => Scalar::null(dtype.clone()),
            k => Scalar::bool(k % 2 == 0, NUL),
        })
    })
}

/// A bool array whose bit offset within the first byte is non-zero after slicing is the classic
/// bug; here we at least guarantee a non-byte-aligned run length.
fn dist_bool_sparse_true(rows: usize, _: &mut Rng) -> VortexResult<ArrayRef> {
    let dtype = DType::Bool(NN);
    build_column_with(&dtype, rows, |i| Ok(Scalar::bool(i % 1031 == 0, NN)))
}

pub fn distributions() -> Vec<Distribution> {
    vec![
        Distribution {
            id: "constant_i64",
            description: "every row the same value",
            build: dist_constant_i64,
        },
        Distribution {
            id: "long_runs_i32",
            description: "runs of 257 identical values",
            build: dist_long_runs_i32,
        },
        Distribution {
            id: "short_runs_i32",
            description: "runs of 3 identical values",
            build: dist_short_runs_i32,
        },
        Distribution {
            id: "low_cardinality_utf8",
            description: "4 distinct strings, randomly drawn",
            build: dist_low_cardinality_utf8,
        },
        Distribution {
            id: "high_cardinality_utf8",
            description: "every string distinct",
            build: dist_high_cardinality_utf8,
        },
        Distribution {
            id: "high_cardinality_i64",
            description: "full-entropy 64-bit integers",
            build: dist_high_cardinality_i64,
        },
        Distribution {
            id: "sorted_i64",
            description: "non-decreasing",
            build: dist_sorted_i64,
        },
        Distribution {
            id: "reverse_sorted_i64",
            description: "non-increasing",
            build: dist_reverse_sorted_i64,
        },
        Distribution {
            id: "sequence_i64",
            description: "exact arithmetic progression",
            build: dist_sequence_i64,
        },
        Distribution {
            id: "mostly_null_i64",
            description: "1 in 97 rows non-null",
            build: dist_mostly_null_i64,
        },
        Distribution {
            id: "all_null_i64",
            description: "every row null, nullable i64",
            build: dist_all_null_i64,
        },
        Distribution {
            id: "all_null_utf8",
            description: "every row null, nullable utf8",
            build: dist_all_null_utf8,
        },
        Distribution {
            id: "float_specials_f64",
            description: "NaN payloads, +/-Inf, -0.0, subnormals, MIN/MAX/EPSILON",
            build: dist_float_specials_f64,
        },
        Distribution {
            id: "float_specials_f32",
            description: "NaN payloads, +/-Inf, -0.0, subnormals, MIN/MAX/EPSILON",
            build: dist_float_specials_f32,
        },
        Distribution {
            id: "alp_friendly_f64",
            description: "two decimal places: the ALP case",
            build: dist_alp_friendly_f64,
        },
        Distribution {
            id: "denormal_heavy_f64",
            description: "random mantissas in a narrow exponent band: the ALPrd case",
            build: dist_denormal_heavy_f64,
        },
        Distribution {
            id: "repeated_prefix_utf8",
            description: "long shared prefixes: the FSST case",
            build: dist_repeated_prefix_utf8,
        },
        Distribution {
            id: "varbinview_lengths_utf8",
            description: "lengths 0, 4, 12, 13, 16, 31: VarBinView inline vs indirect",
            build: dist_varbinview_lengths_utf8,
        },
        Distribution {
            id: "unicode_utf8",
            description: "2-, 3- and 4-byte UTF-8, a combining mark and an embedded U+0000",
            build: dist_unicode_utf8,
        },
        Distribution {
            id: "over_bound_utf8",
            description: "every string past the 64-byte zone bound: inexact min, unknown max",
            build: dist_over_bound_utf8,
        },
        Distribution {
            id: "empty_strings",
            description: "empty strings interleaved with nulls",
            build: dist_empty_strings,
        },
        Distribution {
            id: "huge_string",
            description: "one string over 1 MiB",
            build: dist_huge_string,
        },
        Distribution {
            id: "single_value_dictionary",
            description: "one distinct string for the whole column",
            build: dist_single_value_dictionary,
        },
        Distribution {
            id: "sorted_disjoint_utf8",
            description: "sorted strings whose batches share no value: cross-batch dedup buys nothing",
            build: dist_sorted_disjoint_utf8,
        },
        Distribution {
            id: "bool_alternating",
            description: "alternating booleans with nulls",
            build: dist_bool_alternating,
        },
        Distribution {
            id: "bool_sparse_true",
            description: "one true every 1031 rows",
            build: dist_bool_sparse_true,
        },
    ]
}

/// Row counts used for the distribution matrix. Small enough to keep the corpus tractable, but
/// still straddling both block boundaries.
pub const DISTRIBUTION_ROW_COUNTS: &[usize] = &[1024, 8193];

/// The `huge_string` distribution is capped: a 1.1 MiB value repeated over 8193 rows would be a
/// 9 GiB corpus file, which tests nothing the 16-row file does not.
pub const HUGE_STRING_ROW_COUNT: usize = 16;
