using System;

using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// Metadata of a <c>fastlanes.delta</c> node: the length of the deltas child, and the first row's
/// offset within the same 1024-element block that bit-packing works in.
/// </summary>
internal readonly struct DeltaMetadata : IEquatable<DeltaMetadata>
{
    private const string MessageName = "DeltaMetadata";

    /// <summary>Exclusive upper bound on <see cref="Offset"/>: the block is 1024 elements.</summary>
    public const uint OffsetLimit = 1024;

    /// <summary>Creates delta metadata.</summary>
    /// <param name="deltasLength">Rows in the deltas child (tag 1).</param>
    /// <param name="offset">First row's offset within its 1024-element block (tag 2).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is 1024 or more.</exception>
    public DeltaMetadata(ulong deltasLength, uint offset)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, OffsetLimit);
        DeltasLength = deltasLength;
        Offset = offset;
    }

    /// <summary>Rows in the deltas child, which is a whole number of 1024-element blocks.</summary>
    public ulong DeltasLength { get; }

    /// <summary>The first row's offset within its block; below <see cref="OffsetLimit"/>.</summary>
    public uint Offset { get; }

    /// <summary>Reads the message body.</summary>
    /// <param name="metadata">The node's metadata bytes.</param>
    /// <returns>The parsed metadata.</returns>
    public static DeltaMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        ulong deltasLength = 0;
        uint offset = 0;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    deltasLength = MetadataProto.ReadUInt64(ref reader, wire, MessageName, "deltas_len");
                    break;
                case 2:
                    offset = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "offset");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (offset >= OffsetLimit)
        {
            MetadataProto.ThrowOutOfDomain(MessageName, "offset", $"{offset} is not less than 1024.");
        }

        return new DeltaMetadata(deltasLength, offset);
    }

    /// <inheritdoc/>
    public bool Equals(DeltaMetadata other) =>
        DeltasLength == other.DeltasLength && Offset == other.Offset;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DeltaMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(DeltasLength, Offset);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DeltaMetadata left, DeltaMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DeltaMetadata left, DeltaMetadata right) => !left.Equals(right);
}
