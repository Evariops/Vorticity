namespace Vorticity.Serialization.Protobuf;

/// <summary>
/// Wire-level constants shared by <see cref="ProtoReader"/> and <see cref="ProtoWriter"/>. The
/// varint and ZigZag conversions are <see cref="Varint"/>'s, shared with the other formats that
/// write them.
/// </summary>
internal static class ProtoWire
{
    /// <summary>
    /// Largest legal field number. A tag is a <c>uint32</c> holding
    /// <c>(field_number &lt;&lt; 3) | wire_type</c>, which leaves 29 bits for the number.
    /// </summary>
    internal const int MaxFieldNumber = (1 << 29) - 1;

    /// <summary>Bit count the field number is shifted by inside a tag.</summary>
    internal const int TagTypeBits = 3;

    /// <summary>Mask selecting the wire type out of a tag.</summary>
    internal const uint WireTypeMask = 7;
}
