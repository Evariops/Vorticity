// Union tags transcribed from spec/flatbuffers/dtype.fbs (`union Type`) and cross-checked against
// spec/proto/dtype.proto (`message DType { oneof dtype_type { ... } }`). Both number the same 13
// cases identically and the numbering is stable (docs/02-format.md section 4).
//
// FixedSizeList = 10 deliberately sits AFTER Extension = 9. The .fbs says so in a comment
// ("This is after `Extension` for backwards compatibility.") and the .proto repeats it. It looks
// like a transcription mistake and is not: renumbering would break every existing file.
namespace Vorticity.Types;

/// <summary>
/// Discriminant of the Vortex dtype union. Values are the wire tags shared by
/// <c>dtype.fbs</c> and <c>dtype.proto</c>; tag <c>0</c> is the FlatBuffers <c>NONE</c> and is
/// never a valid dtype.
/// </summary>
public enum DTypeKind : byte
{
    /// <summary>The all-null type. Carries no payload: <c>table Null {}</c>.</summary>
    Null = 1,

    /// <summary>Boolean.</summary>
    Bool = 2,

    /// <summary>A fixed-width primitive selected by <see cref="Types.PType"/>.</summary>
    Primitive = 3,

    /// <summary>Fixed-point decimal with a precision and a scale.</summary>
    Decimal = 4,

    /// <summary>UTF-8 string.</summary>
    Utf8 = 5,

    /// <summary>Opaque bytes.</summary>
    Binary = 6,

    /// <summary>Named, heterogeneously typed fields. <c>Struct_</c> in the FlatBuffers schema.</summary>
    Struct = 7,

    /// <summary>Variable-length list of a single element type.</summary>
    List = 8,

    /// <summary>A logical type layered over a storage dtype, identified by a string id.</summary>
    Extension = 9,

    /// <summary>
    /// Fixed-length list. Tag 10 rather than 9: it was added after <see cref="Extension"/> and
    /// keeping the tag order preserves backward compatibility (spec/flatbuffers/dtype.fbs).
    /// </summary>
    FixedSizeList = 10,

    /// <summary>Self-describing per-row dynamic type (RFC 0015).</summary>
    Variant = 11,

    /// <summary>Tagged union of named alternatives.</summary>
    Union = 12,

    /// <summary>Key/value map.</summary>
    Map = 13,
}
