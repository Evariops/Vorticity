using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.listview</c> metadata:
/// <c>message ListViewMetadata { uint64 elements_len = 1; PType offset_ptype = 2; PType size_ptype = 3; }</c>.
/// </summary>
public readonly struct ListViewMetadata : IEquatable<ListViewMetadata>
{
    private const string MessageName = "ListViewMetadata";

    /// <summary>Creates list-view metadata.</summary>
    /// <param name="elementsLength">Length of the elements child (tag 1).</param>
    /// <param name="offsetPType">Physical type of the offsets child (tag 2).</param>
    /// <param name="sizePType">Physical type of the sizes child (tag 3).</param>
    /// <exception cref="ArgumentOutOfRangeException">A physical type is undefined.</exception>
    public ListViewMetadata(ulong elementsLength, PType offsetPType, PType sizePType)
    {
        if (!PTypeExtensions.IsDefined(offsetPType))
        {
            throw new ArgumentOutOfRangeException(nameof(offsetPType), offsetPType, "Undefined PType.");
        }

        if (!PTypeExtensions.IsDefined(sizePType))
        {
            throw new ArgumentOutOfRangeException(nameof(sizePType), sizePType, "Undefined PType.");
        }

        ElementsLength = elementsLength;
        OffsetPType = offsetPType;
        SizePType = sizePType;
    }

    /// <summary>Length of the elements child (tag 1).</summary>
    public ulong ElementsLength { get; }

    /// <summary>Physical type of the offsets child (tag 2).</summary>
    public PType OffsetPType { get; }

    /// <summary>Physical type of the sizes child (tag 3).</summary>
    public PType SizePType { get; }

    /// <summary>Reads a <c>vortex.listview</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or a physical type is undefined.</exception>
    public static ListViewMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        ulong elementsLength = 0;
        PType offsetPType = PType.U8;
        PType sizePType = PType.U8;
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
                case 3:
                    sizePType = MetadataProto.ReadPType(ref reader, wire, MessageName, "size_ptype");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ListViewMetadata(elementsLength, offsetPType, sizePType);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in ListViewMetadata value)
    {
        writer.WriteUInt64(1, value.ElementsLength);
        writer.WriteEnum(2, (int)value.OffsetPType);
        writer.WriteEnum(3, (int)value.SizePType);
    }

    /// <inheritdoc/>
    public bool Equals(ListViewMetadata other) =>
        ElementsLength == other.ElementsLength
        && OffsetPType == other.OffsetPType
        && SizePType == other.SizePType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ListViewMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ElementsLength, OffsetPType, SizePType);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ListViewMetadata left, ListViewMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ListViewMetadata left, ListViewMetadata right) => !left.Equals(right);
}
