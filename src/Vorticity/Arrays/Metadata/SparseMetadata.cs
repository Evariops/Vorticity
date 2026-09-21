using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.sparse</c> metadata:
/// <c>message SparseMetadata { PatchesMetadata patches = 1; }</c>.
/// </summary>
/// <remarks>
/// The field is <c>#[prost(message, required, tag = "1")]</c>: absent is malformed, not a
/// default-constructed descriptor.
/// </remarks>
internal readonly struct SparseMetadata : IEquatable<SparseMetadata>
{
    private const string MessageName = "SparseMetadata";

    /// <summary>Creates sparse metadata.</summary>
    /// <param name="patches">The patch descriptor (tag 1).</param>
    public SparseMetadata(in PatchesMetadata patches) => Patches = patches;

    /// <summary>The patch descriptor (tag 1). Always present.</summary>
    public PatchesMetadata Patches { get; }

    /// <summary>Reads a <c>vortex.sparse</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or <c>patches</c> is absent.</exception>
    public static SparseMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        PatchesMetadata patches = default;
        bool hasPatches = false;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                MetadataProto.Expect(wire, ProtoWireType.LengthDelimited, MessageName, "patches");
                patches = PatchesMetadata.Read(ref reader);
                hasPatches = true;
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        if (!hasPatches)
        {
            MetadataProto.ThrowMissingRequired(MessageName, "patches");
        }

        return new SparseMetadata(in patches);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in SparseMetadata value)
    {
        PatchesMetadata patches = value.Patches;
        PatchesMetadata.Write(ref writer, 1, in patches);
    }

    /// <inheritdoc/>
    public bool Equals(SparseMetadata other) => Patches.Equals(other.Patches);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SparseMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Patches.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SparseMetadata left, SparseMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SparseMetadata left, SparseMetadata right) => !left.Equals(right);
}
