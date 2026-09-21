using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.alp</c> metadata:
/// <c>message ALPMetadata { uint32 exp_e = 1; uint32 exp_f = 2; optional PatchesMetadata patches = 3; }</c>.
/// </summary>
/// <remarks>
/// As with <see cref="BitPackedMetadata"/>, the patch shape is the child count: no patches, patches
/// without chunk offsets, patches with them. The corpus spells all three under the same encoding id.
/// </remarks>
internal readonly struct AlpMetadata : IEquatable<AlpMetadata>
{
    private const string MessageName = "ALPMetadata";

    private readonly PatchesMetadata _patches;

    /// <summary>Creates ALP metadata without patches.</summary>
    /// <param name="exponentE">The <c>e</c> exponent (tag 1).</param>
    /// <param name="exponentF">The <c>f</c> exponent (tag 2).</param>
    public AlpMetadata(uint exponentE, uint exponentF)
    {
        ExponentE = exponentE;
        ExponentF = exponentF;
        _patches = default;
        HasPatches = false;
    }

    /// <summary>Creates ALP metadata with patches.</summary>
    /// <param name="exponentE">The <c>e</c> exponent (tag 1).</param>
    /// <param name="exponentF">The <c>f</c> exponent (tag 2).</param>
    /// <param name="patches">The patch descriptor (tag 3).</param>
    public AlpMetadata(uint exponentE, uint exponentF, in PatchesMetadata patches)
    {
        ExponentE = exponentE;
        ExponentF = exponentF;
        _patches = patches;
        HasPatches = true;
    }

    /// <summary>The <c>e</c> exponent (tag 1).</summary>
    public uint ExponentE { get; }

    /// <summary>The <c>f</c> exponent (tag 2).</summary>
    public uint ExponentF { get; }

    /// <summary>True when tag 3 was present.</summary>
    public bool HasPatches { get; }

    /// <summary>The patch descriptor (tag 3). Meaningful only when <see cref="HasPatches"/> is true.</summary>
    public PatchesMetadata Patches => _patches;

    /// <summary>Reads a <c>vortex.alp</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed.</exception>
    public static AlpMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        uint exponentE = 0;
        uint exponentF = 0;
        PatchesMetadata patches = default;
        bool hasPatches = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    exponentE = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "exp_e");
                    break;
                case 2:
                    exponentF = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "exp_f");
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

        return hasPatches
            ? new AlpMetadata(exponentE, exponentF, in patches)
            : new AlpMetadata(exponentE, exponentF);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in AlpMetadata value)
    {
        writer.WriteUInt32(1, value.ExponentE);
        writer.WriteUInt32(2, value.ExponentF);
        if (value.HasPatches)
        {
            PatchesMetadata patches = value._patches;
            PatchesMetadata.Write(ref writer, 3, in patches);
        }
    }

    /// <inheritdoc/>
    public bool Equals(AlpMetadata other) =>
        ExponentE == other.ExponentE
        && ExponentF == other.ExponentF
        && HasPatches == other.HasPatches
        && (!HasPatches || _patches.Equals(other._patches));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AlpMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(ExponentE, ExponentF, HasPatches, HasPatches ? _patches.GetHashCode() : 0);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(AlpMetadata left, AlpMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(AlpMetadata left, AlpMetadata right) => !left.Equals(right);
}
