using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>fastlanes.rle</c> metadata.
/// </summary>
/// <remarks>
/// <code>
/// message RLEMetadata {
///   uint64 values_len               = 1;
///   uint64 indices_len              = 2;
///   PType  indices_ptype            = 3;
///   uint64 values_idx_offsets_len   = 4;
///   PType  values_idx_offsets_ptype = 5;
///   uint64 offset                   = 6;   // default 0
/// }
/// </code>
/// <c>offset</c> has a declared default of zero, so absent and present-zero are the same state —
/// unlike the <c>optional bool</c>s elsewhere in this namespace, no tri-state is needed.
/// </remarks>
internal readonly struct RleMetadata : IEquatable<RleMetadata>
{
    private const string MessageName = "RLEMetadata";

    /// <summary>
    /// Exclusive upper bound on <see cref="Offset"/>: a block holds 1024 elements, so the first
    /// visible row always lies inside the first chunk. The bound is not decoration: decoding walks
    /// chunks from the first one and slices the offset off the result afterwards, so a larger
    /// offset names a chunk mapping no valid file can describe.
    /// </summary>
    public const ulong OffsetLimit = 1024;

    /// <summary>Creates RLE metadata.</summary>
    /// <param name="valuesLength">Length of the values child (tag 1).</param>
    /// <param name="indicesLength">Length of the indices child (tag 2).</param>
    /// <param name="indicesPType">Physical type of the indices child (tag 3).</param>
    /// <param name="valuesIdxOffsetsLength">Length of the value-index offsets child (tag 4).</param>
    /// <param name="valuesIdxOffsetsPType">Physical type of the value-index offsets child (tag 5).</param>
    /// <param name="offset">Row offset of the first visible element (tag 6); below 1024.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A physical type is undefined, or <paramref name="offset"/> is 1024 or more.
    /// </exception>
    public RleMetadata(
        ulong valuesLength,
        ulong indicesLength,
        PType indicesPType,
        ulong valuesIdxOffsetsLength,
        PType valuesIdxOffsetsPType,
        ulong offset)
    {
        if (!PTypeExtensions.IsDefined(indicesPType))
        {
            throw new ArgumentOutOfRangeException(nameof(indicesPType), indicesPType, "Undefined PType.");
        }

        if (!PTypeExtensions.IsDefined(valuesIdxOffsetsPType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(valuesIdxOffsetsPType), valuesIdxOffsetsPType, "Undefined PType.");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, OffsetLimit);

        ValuesLength = valuesLength;
        IndicesLength = indicesLength;
        IndicesPType = indicesPType;
        ValuesIdxOffsetsLength = valuesIdxOffsetsLength;
        ValuesIdxOffsetsPType = valuesIdxOffsetsPType;
        Offset = offset;
    }

    /// <summary>Length of the values child (tag 1).</summary>
    public ulong ValuesLength { get; }

    /// <summary>Length of the indices child (tag 2).</summary>
    public ulong IndicesLength { get; }

    /// <summary>Physical type of the indices child (tag 3).</summary>
    public PType IndicesPType { get; }

    /// <summary>Length of the value-index offsets child (tag 4).</summary>
    public ulong ValuesIdxOffsetsLength { get; }

    /// <summary>Physical type of the value-index offsets child (tag 5).</summary>
    public PType ValuesIdxOffsetsPType { get; }

    /// <summary>Row offset of the first visible element (tag 6); zero when absent.</summary>
    public ulong Offset { get; }

    /// <summary>Reads a <c>fastlanes.rle</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">
    /// The payload is malformed, a physical type is undefined, or <c>offset</c> is not below
    /// <see cref="OffsetLimit"/>.
    /// </exception>
    public static RleMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        ulong valuesLength = 0;
        ulong indicesLength = 0;
        PType indicesPType = PType.U8;
        ulong valuesIdxOffsetsLength = 0;
        PType valuesIdxOffsetsPType = PType.U8;
        ulong offset = 0;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    valuesLength = MetadataProto.ReadUInt64(ref reader, wire, MessageName, "values_len");
                    break;
                case 2:
                    indicesLength = MetadataProto.ReadUInt64(ref reader, wire, MessageName, "indices_len");
                    break;
                case 3:
                    indicesPType = MetadataProto.ReadPType(ref reader, wire, MessageName, "indices_ptype");
                    break;
                case 4:
                    valuesIdxOffsetsLength =
                        MetadataProto.ReadUInt64(ref reader, wire, MessageName, "values_idx_offsets_len");
                    break;
                case 5:
                    valuesIdxOffsetsPType =
                        MetadataProto.ReadPType(ref reader, wire, MessageName, "values_idx_offsets_ptype");
                    break;
                case 6:
                    offset = MetadataProto.ReadUInt64(ref reader, wire, MessageName, "offset");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        // Checked on the raw u64, before any narrowing, so that a huge offset reports this domain
        // error rather than a length error from further down the decoder.
        if (offset >= OffsetLimit)
        {
            MetadataProto.ThrowOutOfDomain(MessageName, "offset", $"{offset} is not less than 1024.");
        }

        return new RleMetadata(
            valuesLength, indicesLength, indicesPType, valuesIdxOffsetsLength, valuesIdxOffsetsPType, offset);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in RleMetadata value)
    {
        writer.WriteUInt64(1, value.ValuesLength);
        writer.WriteUInt64(2, value.IndicesLength);
        writer.WriteEnum(3, (int)value.IndicesPType);
        writer.WriteUInt64(4, value.ValuesIdxOffsetsLength);
        writer.WriteEnum(5, (int)value.ValuesIdxOffsetsPType);
        writer.WriteUInt64(6, value.Offset);
    }

    /// <inheritdoc/>
    public bool Equals(RleMetadata other) =>
        ValuesLength == other.ValuesLength
        && IndicesLength == other.IndicesLength
        && IndicesPType == other.IndicesPType
        && ValuesIdxOffsetsLength == other.ValuesIdxOffsetsLength
        && ValuesIdxOffsetsPType == other.ValuesIdxOffsetsPType
        && Offset == other.Offset;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RleMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            ValuesLength, IndicesLength, IndicesPType, ValuesIdxOffsetsLength, ValuesIdxOffsetsPType, Offset);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RleMetadata left, RleMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RleMetadata left, RleMetadata right) => !left.Equals(right);
}
