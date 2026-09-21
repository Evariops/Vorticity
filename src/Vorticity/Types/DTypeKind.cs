namespace Vorticity.Types;

/// <summary>
/// Discriminant of the Vortex dtype union. The values are wire tags: the FlatBuffers and protobuf
/// schemas number the same 13 cases identically and the numbering is stable, so renumbering any of
/// them would break every existing file. Tag <c>0</c> is the FlatBuffers "no variant" marker and
/// is never a valid dtype.
/// </summary>
internal enum DTypeKind : byte
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
    /// Fixed-length list. Tag 10 rather than 9, sitting after <see cref="Extension"/>: the gap
    /// looks like a transcription mistake and is not, it is the order the tags were assigned in
    /// and the only one existing files can be read with.
    /// </summary>
    FixedSizeList = 10,

    /// <summary>Self-describing per-row dynamic type (RFC 0015).</summary>
    Variant = 11,

    /// <summary>Tagged union of named alternatives.</summary>
    Union = 12,

    /// <summary>Key/value map.</summary>
    Map = 13,
}
