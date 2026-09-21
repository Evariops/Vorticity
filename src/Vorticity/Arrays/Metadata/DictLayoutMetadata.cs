using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.dict</c> <b>layout</b> metadata.
/// </summary>
/// <remarks>
/// <code>
/// message DictLayoutMetadata {
///   PType codes_ptype                   = 1;
///   optional bool is_nullable_codes     = 2;
///   optional bool all_values_referenced = 3;
/// }
/// </code>
/// The tag numbers differ from the <em>array</em> <see cref="DictMetadata"/>, which starts with
/// <c>values_len</c> at tag 1: the two messages are not interchangeable.
/// </remarks>
internal readonly struct DictLayoutMetadata : IEquatable<DictLayoutMetadata>
{
    private const string MessageName = "DictLayoutMetadata";

    /// <summary>Creates dictionary layout metadata.</summary>
    /// <param name="codesPType">Physical type of the codes child (tag 1).</param>
    /// <param name="isNullableCodes">Whether the codes child is nullable (tag 2), or null when absent.</param>
    /// <param name="allValuesReferenced">Whether every value is referenced (tag 3), or null when absent.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codesPType"/> is undefined.</exception>
    public DictLayoutMetadata(PType codesPType, bool? isNullableCodes, bool? allValuesReferenced)
    {
        if (!PTypeExtensions.IsDefined(codesPType))
        {
            throw new ArgumentOutOfRangeException(nameof(codesPType), codesPType, "Undefined PType.");
        }

        CodesPType = codesPType;
        IsNullableCodes = isNullableCodes;
        AllValuesReferenced = allValuesReferenced;
    }

    /// <summary>Physical type of the codes child (tag 1).</summary>
    public PType CodesPType { get; }

    /// <summary>
    /// Whether the codes child is nullable (tag 2). <c>null</c> means absent: fall back to the
    /// layout dtype's nullability.
    /// </summary>
    public bool? IsNullableCodes { get; }

    /// <summary>Whether every dictionary value is referenced (tag 3); <c>null</c> means unknown.</summary>
    public bool? AllValuesReferenced { get; }

    /// <summary>Reads a <c>vortex.dict</c> layout metadata payload.</summary>
    /// <param name="layoutMetadata">The raw layout metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or <c>codes_ptype</c> is undefined.</exception>
    public static DictLayoutMetadata Read(ReadOnlySpan<byte> layoutMetadata)
    {
        ProtoReader reader = new ProtoReader(layoutMetadata);
        PType codesPType = PType.U8;
        bool? isNullableCodes = null;
        bool? allValuesReferenced = null;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    codesPType = MetadataProto.ReadPType(ref reader, wire, MessageName, "codes_ptype");
                    break;
                case 2:
                    isNullableCodes = MetadataProto.ReadBool(ref reader, wire, MessageName, "is_nullable_codes");
                    break;
                case 3:
                    allValuesReferenced =
                        MetadataProto.ReadBool(ref reader, wire, MessageName, "all_values_referenced");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new DictLayoutMetadata(codesPType, isNullableCodes, allValuesReferenced);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in DictLayoutMetadata value)
    {
        writer.WriteEnum(1, (int)value.CodesPType);
        if (value.IsNullableCodes.HasValue)
        {
            writer.WriteBoolAlways(2, value.IsNullableCodes.GetValueOrDefault());
        }

        if (value.AllValuesReferenced.HasValue)
        {
            writer.WriteBoolAlways(3, value.AllValuesReferenced.GetValueOrDefault());
        }
    }

    /// <inheritdoc/>
    public bool Equals(DictLayoutMetadata other) =>
        CodesPType == other.CodesPType
        && IsNullableCodes == other.IsNullableCodes
        && AllValuesReferenced == other.AllValuesReferenced;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DictLayoutMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(CodesPType, IsNullableCodes, AllValuesReferenced);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DictLayoutMetadata left, DictLayoutMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DictLayoutMetadata left, DictLayoutMetadata right) => !left.Equals(right);
}
