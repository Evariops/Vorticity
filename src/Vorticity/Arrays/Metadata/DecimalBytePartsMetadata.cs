using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.decimal_byte_parts</c> metadata:
/// <c>message DecimalBytesPartsMetadata { PType zeroth_child_ptype = 1; uint32 lower_part_count = 2; }</c>.
/// </summary>
/// <remarks>
/// <c>lower_part_count</c> must be zero: wide decimals belong to a future encoding id, so a
/// non-zero value is a domain violation and is rejected rather than carried forward as a shape
/// this codec cannot honour.
/// </remarks>
public readonly struct DecimalBytePartsMetadata : IEquatable<DecimalBytePartsMetadata>
{
    private const string MessageName = "DecimalBytesPartsMetadata";

    /// <summary>Creates decimal-byte-parts metadata.</summary>
    /// <param name="zerothChildPType">Physical type of child 0 (tag 1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="zerothChildPType"/> is undefined.</exception>
    public DecimalBytePartsMetadata(PType zerothChildPType)
    {
        if (!PTypeExtensions.IsDefined(zerothChildPType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(zerothChildPType), zerothChildPType, "Undefined PType.");
        }

        ZerothChildPType = zerothChildPType;
    }

    /// <summary>Physical type of child 0 (tag 1).</summary>
    public PType ZerothChildPType { get; }

    /// <summary>Always zero (tag 2): the only value this encoding id admits.</summary>
    public uint LowerPartCount => 0;

    /// <summary>Reads a <c>vortex.decimal_byte_parts</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">
    /// The payload is malformed, the physical type is undefined, or <c>lower_part_count</c> is non-zero.
    /// </exception>
    public static DecimalBytePartsMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        PType zerothChild = PType.U8;
        uint lowerPartCount = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    zerothChild = MetadataProto.ReadPType(ref reader, wire, MessageName, "zeroth_child_ptype");
                    break;
                case 2:
                    lowerPartCount = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "lower_part_count");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (lowerPartCount != 0)
        {
            MetadataProto.ThrowOutOfDomain(
                MessageName, "lower_part_count",
                $"{lowerPartCount} is non-zero; wide decimals belong to a future encoding id.");
        }

        return new DecimalBytePartsMetadata(zerothChild);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in DecimalBytePartsMetadata value) =>
        writer.WriteEnum(1, (int)value.ZerothChildPType);

    /// <inheritdoc/>
    public bool Equals(DecimalBytePartsMetadata other) => ZerothChildPType == other.ZerothChildPType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DecimalBytePartsMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ZerothChildPType.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DecimalBytePartsMetadata left, DecimalBytePartsMetadata right) =>
        left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DecimalBytePartsMetadata left, DecimalBytePartsMetadata right) =>
        !left.Equals(right);
}
