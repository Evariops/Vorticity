// Layout vortex.list — vortex-layout-0.86.1/src/layouts/list/mod.rs. spec/METADATA.md.
// Phase 2 consumer: `vortex.list` is a member of no core edition as of core2026.08.3, so it
// resolves to LayoutEncodingId.Unknown in Phase 1 (Phase 1 contract §2.8). The codec is
// transcribed now because the file was open; nothing in Phase 1 calls it.
using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.list</c> <b>layout</b> metadata:
/// <c>message ListLayoutMetadata { PType offsets_ptype = 1; }</c>.
/// </summary>
public readonly struct ListLayoutMetadata : IEquatable<ListLayoutMetadata>
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
