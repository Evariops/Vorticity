// fastlanes.bitpacked — vortex-fastlanes-0.86.1/src/bitpacking/vtable/mod.rs. spec/METADATA.md.
using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>fastlanes.bitpacked</c> metadata.
/// </summary>
/// <remarks>
/// <code>
/// message BitPackedMetadata {
///   uint32 bit_width = 1;
///   uint32 offset    = 2;                   // must be &lt; 1024
///   optional PatchesMetadata patches = 3;
/// }
/// </code>
/// <see cref="HasPatches"/> and <see cref="PatchesMetadata.HasChunkOffsets"/> together determine
/// the node's child layout — no patches means the validity child is child 0, patches without chunk
/// offsets moves it to child 2, patches with them to child 3 — so presence is exposed explicitly
/// and is never inferred from an all-zero descriptor.
/// </remarks>
public readonly struct BitPackedMetadata : IEquatable<BitPackedMetadata>
{
    private const string MessageName = "BitPackedMetadata";

    /// <summary>
    /// Exclusive upper bound on <see cref="Offset"/>: the FastLanes block is 1024 elements, so an
    /// offset into it is always below that (vortex-fastlanes-0.86.1/src/bitpacking/array/mod.rs).
    /// </summary>
    public const uint OffsetLimit = 1024;

    private readonly PatchesMetadata _patches;

    /// <summary>Creates bit-packed metadata without patches.</summary>
    /// <param name="bitWidth">Bits per packed value (tag 1).</param>
    /// <param name="offset">Offset of the first element within its 1024-element block (tag 2).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is 1024 or more.</exception>
    public BitPackedMetadata(uint bitWidth, uint offset)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, OffsetLimit);
        BitWidth = bitWidth;
        Offset = offset;
        _patches = default;
        HasPatches = false;
    }

    /// <summary>Creates bit-packed metadata with patches.</summary>
    /// <param name="bitWidth">Bits per packed value (tag 1).</param>
    /// <param name="offset">Offset of the first element within its 1024-element block (tag 2).</param>
    /// <param name="patches">The patch descriptor (tag 3).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is 1024 or more.</exception>
    public BitPackedMetadata(uint bitWidth, uint offset, in PatchesMetadata patches)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, OffsetLimit);
        BitWidth = bitWidth;
        Offset = offset;
        _patches = patches;
        HasPatches = true;
    }

    /// <summary>
    /// Bits per packed value (tag 1). Not range-checked here: the legal maximum is the width of the
    /// node's inherited primitive type, which this codec does not know. The decoder validates it
    /// against that width.
    /// </summary>
    public uint BitWidth { get; }

    /// <summary>Offset of the first element within its 1024-element FastLanes block (tag 2).</summary>
    public uint Offset { get; }

    /// <summary>True when tag 3 was present.</summary>
    public bool HasPatches { get; }

    /// <summary>The patch descriptor (tag 3). Meaningful only when <see cref="HasPatches"/> is true.</summary>
    public PatchesMetadata Patches => _patches;

    /// <summary>Reads a <c>fastlanes.bitpacked</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">
    /// The payload is malformed, the offset is 1024 or more, or the nested patch descriptor is invalid.
    /// </exception>
    public static BitPackedMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        uint bitWidth = 0;
        uint offset = 0;
        PatchesMetadata patches = default;
        bool hasPatches = false;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    bitWidth = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "bit_width");
                    break;
                case 2:
                    offset = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "offset");
                    break;
                case 3:
                    MetadataProto.Expect(wire, ProtoWireType.LengthDelimited, MessageName, "patches");
                    patches = PatchesMetadata.Read(ref reader);
                    hasPatches = true;
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

        return hasPatches
            ? new BitPackedMetadata(bitWidth, offset, in patches)
            : new BitPackedMetadata(bitWidth, offset);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in BitPackedMetadata value)
    {
        writer.WriteUInt32(1, value.BitWidth);
        writer.WriteUInt32(2, value.Offset);
        if (value.HasPatches)
        {
            PatchesMetadata patches = value._patches;
            PatchesMetadata.Write(ref writer, 3, in patches);
        }
    }

    /// <inheritdoc/>
    public bool Equals(BitPackedMetadata other) =>
        BitWidth == other.BitWidth
        && Offset == other.Offset
        && HasPatches == other.HasPatches
        && (!HasPatches || _patches.Equals(other._patches));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BitPackedMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(BitWidth, Offset, HasPatches, HasPatches ? _patches.GetHashCode() : 0);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(BitPackedMetadata left, BitPackedMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(BitPackedMetadata left, BitPackedMetadata right) => !left.Equals(right);
}
