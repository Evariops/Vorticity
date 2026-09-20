using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.bool</c> metadata: <c>message BoolMetadata { uint32 offset = 1; }</c>.
/// </summary>
/// <remarks>
/// The offset is a <b>bit</b> offset into buffer 0 and is the only thing that distinguishes two
/// otherwise identical bool arrays.
/// </remarks>
public readonly struct BoolMetadata : IEquatable<BoolMetadata>
{
    private const string MessageName = "BoolMetadata";

    /// <summary>Exclusive upper bound on <see cref="Offset"/>: a bit offset never reaches a whole byte.</summary>
    public const uint OffsetLimit = 8;

    /// <summary>Creates the metadata for a bool array with the given bit offset.</summary>
    /// <param name="offset">Bit offset into the bitmap buffer, 0..7.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is 8 or more.</exception>
    public BoolMetadata(uint offset)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, OffsetLimit);
        Offset = offset;
    }

    /// <summary>Bit offset of the first element within buffer 0 (tag 1). Always 0..7.</summary>
    public uint Offset { get; }

    /// <summary>Reads a <c>vortex.bool</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes; empty means <c>offset = 0</c>.</param>
    /// <exception cref="VortexFormatException">
    /// The payload is malformed, or the offset is 8 or more. The bound is enforced rather than
    /// advisory: the offset is added to a bit index before the bitmap is indexed.
    /// </exception>
    public static BoolMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        uint offset = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                offset = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "offset");
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        if (offset >= OffsetLimit)
        {
            MetadataProto.ThrowOutOfDomain(MessageName, "offset", $"{offset} is not less than 8.");
        }

        return new BoolMetadata(offset);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in BoolMetadata value) =>
        writer.WriteUInt32(1, value.Offset);

    /// <inheritdoc/>
    public bool Equals(BoolMetadata other) => Offset == other.Offset;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BoolMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Offset.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(BoolMetadata left, BoolMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(BoolMetadata left, BoolMetadata right) => !left.Equals(right);
}
