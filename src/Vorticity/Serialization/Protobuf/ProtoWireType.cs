namespace Vorticity.Serialization.Protobuf;

/// <summary>
/// The three low bits of a Protobuf tag: how the following payload is framed. Part of a
/// hand-written proto3 runtime: Vorticity takes no Protobuf dependency, so the wire format is
/// transcribed from the encoding rules the vendored schemas rely on.
/// </summary>
/// <remarks>
/// <para>
/// Values 3 and 4 are the deprecated <em>group</em> framing. proto3 never emits them, so
/// Vorticity rejects them outright rather than implementing a second nesting mechanism. They
/// are named here only so a rejection can report what it saw.
/// </para>
/// <para>
/// Values 6 and 7 have never been assigned and have no name: a tag carrying one is malformed.
/// </para>
/// </remarks>
public enum ProtoWireType : byte
{
    /// <summary>Base-128 varint: <c>int32</c>, <c>int64</c>, <c>uint32</c>, <c>uint64</c>,
    /// <c>sint32</c>, <c>sint64</c>, <c>bool</c>, and enums.</summary>
    Varint = 0,

    /// <summary>Eight little-endian bytes: <c>fixed64</c>, <c>sfixed64</c>, <c>double</c>.</summary>
    Fixed64 = 1,

    /// <summary>A varint byte count followed by that many bytes: <c>string</c>, <c>bytes</c>,
    /// embedded messages, and packed repeated fields.</summary>
    LengthDelimited = 2,

    /// <summary>Deprecated group start. Rejected: proto3 never emits it.</summary>
    StartGroup = 3,

    /// <summary>Deprecated group end. Rejected: proto3 never emits it.</summary>
    EndGroup = 4,

    /// <summary>Four little-endian bytes: <c>fixed32</c>, <c>sfixed32</c>, <c>float</c>.</summary>
    Fixed32 = 5,
}
