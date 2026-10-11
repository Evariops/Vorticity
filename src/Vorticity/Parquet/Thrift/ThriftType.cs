namespace Vorticity.Parquet.Thrift;

/// <summary>The wire types of the Thrift compact protocol, as a field header and a list header carry them.</summary>
/// <remarks>
/// A field's boolean value is its type: <see cref="BooleanTrue"/> or <see cref="BooleanFalse"/>, and
/// no byte follows. A list of booleans declares its element type as either, and each element is one
/// byte.
/// </remarks>
internal enum ThriftType : byte
{
    /// <summary>The end of a struct.</summary>
    Stop = 0,

    /// <summary>A boolean field holding true, or the element type of a list of booleans.</summary>
    BooleanTrue = 1,

    /// <summary>A boolean field holding false, or the element type of a list of booleans.</summary>
    BooleanFalse = 2,

    /// <summary>An <c>i8</c>, one byte.</summary>
    Byte = 3,

    /// <summary>An <c>i16</c>, a zigzag varint.</summary>
    I16 = 4,

    /// <summary>An <c>i32</c> or an enum, a zigzag varint.</summary>
    I32 = 5,

    /// <summary>An <c>i64</c>, a zigzag varint.</summary>
    I64 = 6,

    /// <summary>A <c>double</c>, eight little-endian bytes.</summary>
    Double = 7,

    /// <summary>A <c>binary</c> or a <c>string</c>: a varint length and the bytes.</summary>
    Binary = 8,

    /// <summary>A <c>list</c>.</summary>
    List = 9,

    /// <summary>A <c>set</c>, encoded as a list.</summary>
    Set = 10,

    /// <summary>A <c>map</c>.</summary>
    Map = 11,

    /// <summary>A <c>struct</c> or a <c>union</c>.</summary>
    Struct = 12,

    /// <summary>A <c>uuid</c>, sixteen bytes.</summary>
    Uuid = 13,
}
