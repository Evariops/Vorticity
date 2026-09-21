namespace Vorticity.Types;

/// <summary>
/// Discriminant of a <see cref="ScalarValue"/>. Each numbered case is the wire field number of the
/// matching message case, so a codec maps a tag to a kind with a cast instead of a switch;
/// <see cref="Absent"/> is this model's own, since the wire spells absence by setting no case at
/// all.
/// </summary>
/// <remarks>
/// There is no decimal case: a decimal scalar arrives as <see cref="Bytes"/> carrying the unscaled
/// value in little-endian two's complement, its length selecting the storage width from 1 to 32
/// bytes. Interpreting it needs the dtype, which a value does not carry.
/// </remarks>
internal enum ScalarValueKind : byte
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
    /// float.
    /// </summary>
    F16 = 10,

    /// <summary>A nested typed <see cref="Scalar"/>: a value carrying its own dtype.</summary>
    Variant = 11,

    /// <summary>A present union alternative: a type id plus a value.</summary>
    Union = 12,
}
