using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.list</c> <b>layout</b> metadata:
/// <c>message ListLayoutMetadata { PType offsets_ptype = 1; }</c>.
/// No core edition declares this layout, so a reader that meets it resolves an unknown layout
/// encoding; the codec exists so that such a file can still be described.
/// </summary>
internal readonly struct ListLayoutMetadata : IEquatable<ListLayoutMetadata>
{
    private const string MessageName = "ListLayoutMetadata";

    /// <summary>Creates list layout metadata.</summary>
    /// <param name="offsetsPType">Physical type of the offsets child (tag 1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offsetsPType"/> is undefined.</exception>
    public ListLayoutMetadata(PType offsetsPType)
    {
        if (!PTypeExtensions.IsDefined(offsetsPType))
        {
            throw new ArgumentOutOfRangeException(nameof(offsetsPType), offsetsPType, "Undefined PType.");
        }

        OffsetsPType = offsetsPType;
    }

    /// <summary>Physical type of the offsets child (tag 1).</summary>
    public PType OffsetsPType { get; }

    /// <summary>Reads a <c>vortex.list</c> layout metadata payload.</summary>
    /// <param name="layoutMetadata">The raw layout metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or the offset type is undefined.</exception>
    public static ListLayoutMetadata Read(ReadOnlySpan<byte> layoutMetadata)
    {
        ProtoReader reader = new ProtoReader(layoutMetadata);
        PType offsetsPType = PType.U8;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                offsetsPType = MetadataProto.ReadPType(ref reader, wire, MessageName, "offsets_ptype");
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        return new ListLayoutMetadata(offsetsPType);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in ListLayoutMetadata value) =>
        writer.WriteEnum(1, (int)value.OffsetsPType);

    /// <inheritdoc/>
    public bool Equals(ListLayoutMetadata other) => OffsetsPType == other.OffsetsPType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ListLayoutMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => OffsetsPType.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ListLayoutMetadata left, ListLayoutMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ListLayoutMetadata left, ListLayoutMetadata right) => !left.Equals(right);
}
