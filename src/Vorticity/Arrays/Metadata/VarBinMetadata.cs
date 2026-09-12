// vortex.varbin — vortex-array-0.86.1/src/arrays/varbin/vtable/mod.rs. spec/METADATA.md.
using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.varbin</c> metadata: <c>message VarBinMetadata { PType offsets_ptype = 1; }</c>.
/// </summary>
public readonly struct VarBinMetadata : IEquatable<VarBinMetadata>
{
    private const string MessageName = "VarBinMetadata";

    /// <summary>Creates var-bin metadata.</summary>
    /// <param name="offsetsPType">Physical type of the offsets child (tag 1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offsetsPType"/> is undefined.</exception>
    public VarBinMetadata(PType offsetsPType)
    {
        if (!PTypeExtensions.IsDefined(offsetsPType))
        {
            throw new ArgumentOutOfRangeException(nameof(offsetsPType), offsetsPType, "Undefined PType.");
        }

        OffsetsPType = offsetsPType;
    }

    /// <summary>Physical type of the offsets child (tag 1). Class I: it is the offset stride.</summary>
    public PType OffsetsPType { get; }

    /// <summary>Reads a <c>vortex.varbin</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes; empty means <c>u8</c>.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or the offset type is undefined.</exception>
    public static VarBinMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
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

        return new VarBinMetadata(offsetsPType);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in VarBinMetadata value) =>
        writer.WriteEnum(1, (int)value.OffsetsPType);

    /// <inheritdoc/>
    public bool Equals(VarBinMetadata other) => OffsetsPType == other.OffsetsPType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VarBinMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => OffsetsPType.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(VarBinMetadata left, VarBinMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(VarBinMetadata left, VarBinMetadata right) => !left.Equals(right);
}
