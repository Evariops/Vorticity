using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.fsst</c> metadata:
/// <c>message FSSTMetadata { PType uncompressed_lengths_ptype = 1; PType codes_offsets_ptype = 2; }</c>.
/// </summary>
internal readonly struct FsstMetadata : IEquatable<FsstMetadata>
{
    private const string MessageName = "FSSTMetadata";

    /// <summary>Creates FSST metadata.</summary>
    /// <param name="uncompressedLengthsPType">Physical type of the uncompressed-lengths child (tag 1).</param>
    /// <param name="codesOffsetsPType">Physical type of the code-offsets child (tag 2).</param>
    /// <exception cref="ArgumentOutOfRangeException">A physical type is undefined.</exception>
    public FsstMetadata(PType uncompressedLengthsPType, PType codesOffsetsPType)
    {
        if (!PTypeExtensions.IsDefined(uncompressedLengthsPType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(uncompressedLengthsPType), uncompressedLengthsPType, "Undefined PType.");
        }

        if (!PTypeExtensions.IsDefined(codesOffsetsPType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(codesOffsetsPType), codesOffsetsPType, "Undefined PType.");
        }

        UncompressedLengthsPType = uncompressedLengthsPType;
        CodesOffsetsPType = codesOffsetsPType;
    }

    /// <summary>Physical type of the uncompressed-lengths child (tag 1).</summary>
    public PType UncompressedLengthsPType { get; }

    /// <summary>Physical type of the code-offsets child (tag 2).</summary>
    public PType CodesOffsetsPType { get; }

    /// <summary>Reads a <c>vortex.fsst</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or a physical type is undefined.</exception>
    public static FsstMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        PType lengths = PType.U8;
        PType offsets = PType.U8;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    lengths = MetadataProto.ReadPType(ref reader, wire, MessageName, "uncompressed_lengths_ptype");
                    break;
                case 2:
                    offsets = MetadataProto.ReadPType(ref reader, wire, MessageName, "codes_offsets_ptype");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new FsstMetadata(lengths, offsets);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in FsstMetadata value)
    {
        writer.WriteEnum(1, (int)value.UncompressedLengthsPType);
        writer.WriteEnum(2, (int)value.CodesOffsetsPType);
    }

    /// <inheritdoc/>
    public bool Equals(FsstMetadata other) =>
        UncompressedLengthsPType == other.UncompressedLengthsPType
        && CodesOffsetsPType == other.CodesOffsetsPType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FsstMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(UncompressedLengthsPType, CodesOffsetsPType);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(FsstMetadata left, FsstMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(FsstMetadata left, FsstMetadata right) => !left.Equals(right);
}
