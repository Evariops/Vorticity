using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.list</c> metadata:
/// <c>message ListMetadata { uint64 elements_len = 1; PType offset_ptype = 2; }</c>.
/// </summary>
/// <remarks>
/// The offset type is <em>not</em> restricted to unsigned: Arrow-style list offsets are signed, so
/// both signed and unsigned offsets occur here. Only patch indices are required to be unsigned.
/// </remarks>
public readonly struct ListMetadata : IEquatable<ListMetadata>
{
    private const string MessageName = "ListMetadata";

    /// <summary>Creates list metadata.</summary>
    /// <param name="elementsLength">Length of the elements child (tag 1).</param>
    /// <param name="offsetPType">Physical type of the offsets child (tag 2).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offsetPType"/> is undefined.</exception>
    public ListMetadata(ulong elementsLength, PType offsetPType)
    {
        if (!PTypeExtensions.IsDefined(offsetPType))
        {
            throw new ArgumentOutOfRangeException(nameof(offsetPType), offsetPType, "Undefined PType.");
        }

        ElementsLength = elementsLength;
        OffsetPType = offsetPType;
    }

    /// <summary>Length of the elements child (tag 1).</summary>
    public ulong ElementsLength { get; }

    /// <summary>Physical type of the offsets child (tag 2); it fixes the stride of the offsets buffer.</summary>
    public PType OffsetPType { get; }

    /// <summary>Reads a <c>vortex.list</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or the offset type is undefined.</exception>
    public static ListMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        ulong elementsLength = 0;
        PType offsetPType = PType.U8;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    elementsLength = MetadataProto.ReadUInt64(ref reader, wire, MessageName, "elements_len");
                    break;
                case 2:
                    offsetPType = MetadataProto.ReadPType(ref reader, wire, MessageName, "offset_ptype");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ListMetadata(elementsLength, offsetPType);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in ListMetadata value)
    {
        writer.WriteUInt64(1, value.ElementsLength);
        writer.WriteEnum(2, (int)value.OffsetPType);
    }

    /// <inheritdoc/>
    public bool Equals(ListMetadata other) =>
        ElementsLength == other.ElementsLength && OffsetPType == other.OffsetPType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ListMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ElementsLength, OffsetPType);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ListMetadata left, ListMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ListMetadata left, ListMetadata right) => !left.Equals(right);
}
