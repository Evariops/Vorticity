// Transcribed from spec/proto/scalar.proto: `message ScalarValue { oneof kind { ... } }`. The
// numbering here is the proto field number of each case, so a codec can map tag to kind with a
// cast instead of a switch:
//
//   1 null_value (google.protobuf.NullValue)   7 string_value (UTF-8)
//   2 bool_value                               8 bytes_value
//   3 int64_value  (sint64, zigzag)            9 list_value  (ListValue)
//   4 uint64_value (plain varint)             10 f16_value   (uint64 varint of the binary16 bits)
//   5 f32_value    (fixed32)                  11 variant_value (a nested Scalar)
//   6 f64_value    (fixed64)                  12 union_value (UnionValue)
//
// Absent = 0 is ours, not the wire's: proto3 cannot distinguish "no case set" from a default, and
// docs/08-semantics.md section 1 makes the distinction load-bearing -- "a statistic with no value
// licenses nothing", so a missing statistic must never be readable as a null one.
//
// There is no decimal case among the twelve, and none is needed. A Decimal scalar arrives as
// bytes_value carrying the unscaled value in little-endian two's complement, and its LENGTH
// selects the storage width: 1, 2, 4, 8, 16 or 32 bytes for i8 through i256, so a precision past
// 18 costs nothing special. Reading that requires the dtype, which a ScalarValue does not carry,
// so it happens in TypedScalar and nowhere else.
namespace Vorticity.Types;

/// <summary>Discriminant of a <see cref="ScalarValue"/>.</summary>
public enum ScalarValueKind : byte
{
    /// <summary>
    /// No value at all: an empty <c>ScalarValue</c> message, or a statistic the file does not
    /// carry. Distinct from <see cref="Null"/>, which asserts that the value *is* null.
    /// </summary>
    Absent = 0,

    /// <summary>The null value (<c>null_value</c>).</summary>
    Null = 1,

    /// <summary>A boolean.</summary>
    Bool = 2,

    /// <summary>A signed 64-bit integer. Encoded as zigzag <c>sint64</c> on the wire.</summary>
    Int64 = 3,

    /// <summary>An unsigned 64-bit integer. Encoded as a plain varint.</summary>
    UInt64 = 4,

    /// <summary>An IEEE-754 binary32. Encoded as <c>fixed32</c>.</summary>
    F32 = 5,

    /// <summary>An IEEE-754 binary64. Encoded as <c>fixed64</c>.</summary>
    F64 = 6,

    /// <summary>A UTF-8 string.</summary>
    String = 7,

    /// <summary>Opaque bytes.</summary>
    Bytes = 8,

    /// <summary>A list of scalar values.</summary>
    List = 9,

    /// <summary>
    /// An IEEE-754 binary16. Encoded as a <c>uint64</c> varint carrying the raw 16 bits, not as a
    /// float — see spec/proto/scalar.proto.
    /// </summary>
    F16 = 10,

    /// <summary>A nested typed <see cref="Scalar"/> (RFC 0015 variant scalars).</summary>
    Variant = 11,

    /// <summary>A present union alternative: a type id plus a value.</summary>
    Union = 12,
}
