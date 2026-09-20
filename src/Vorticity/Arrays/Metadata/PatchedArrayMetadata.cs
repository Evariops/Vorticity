using System;

using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>Metadata of a <c>vortex.patched</c> node.</summary>
public readonly struct PatchedArrayMetadata : IEquatable<PatchedArrayMetadata>
{
    private const string MessageName = "PatchedMetadata";

    /// <summary>Exclusive upper bound on <see cref="Offset"/>: patches are indexed per 1024-value chunk.</summary>
    public const uint OffsetLimit = 1024;

    /// <summary>Creates patched metadata.</summary>
    /// <param name="patchCount">Patches, and the length of the indices and values children (tag 1).</param>
    /// <param name="laneCount">Lanes used for patch indexing; a power of two in [1, 128] (tag 2).</param>
    /// <param name="offset">Offset into the first chunk that is in view (tag 3).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is 1024 or more.</exception>
    public PatchedArrayMetadata(uint patchCount, uint laneCount, uint offset)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, OffsetLimit);
        PatchCount = patchCount;
        LaneCount = laneCount;
        Offset = offset;
    }

    /// <summary>Patches, and the length of the indices and values children.</summary>
    public uint PatchCount { get; }

    /// <summary>Lanes used for patch indexing; a power of two in <c>[1, 128]</c>.</summary>
    public uint LaneCount { get; }

    /// <summary>Offset into the first chunk that is in view; below <see cref="OffsetLimit"/>.</summary>
    public uint Offset { get; }

    /// <summary>Reads the message body.</summary>
    /// <param name="metadata">The node's metadata bytes.</param>
    /// <returns>The parsed metadata.</returns>
    public static PatchedArrayMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        uint patchCount = 0;
        uint laneCount = 0;
        uint offset = 0;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    patchCount = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "n_patches");
                    break;
                case 2:
                    laneCount = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "n_lanes");
                    break;
                case 3:
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

        // The lane count is checked rather than trusted, because `chunk * n_lanes + n_lanes` indexes
        // the lane-offsets child and a wild value reads past it.
        if (laneCount == 0 || laneCount > 128 || (laneCount & (laneCount - 1)) != 0)
        {
            MetadataProto.ThrowOutOfDomain(
                MessageName, "n_lanes", $"{laneCount} is not a power of two in [1, 128].");
        }

        return new PatchedArrayMetadata(patchCount, laneCount, offset);
    }

    /// <inheritdoc/>
    public bool Equals(PatchedArrayMetadata other) =>
        PatchCount == other.PatchCount && LaneCount == other.LaneCount && Offset == other.Offset;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PatchedArrayMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(PatchCount, LaneCount, Offset);

    /// <summary>Equality.</summary>
    /// <param name="left">Left.</param>
    /// <param name="right">Right.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(PatchedArrayMetadata left, PatchedArrayMetadata right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    /// <param name="left">Left.</param>
    /// <param name="right">Right.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(PatchedArrayMetadata left, PatchedArrayMetadata right) => !left.Equals(right);
}
