// vortex.dict — vortex-array-0.86.1/src/arrays/dict/array.rs. spec/METADATA.md.
using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.dict</c> array metadata.
/// </summary>
/// <remarks>
/// <code>
/// message DictMetadata {
///   uint32 values_len                   = 1;
///   PType  codes_ptype                  = 2;
///   optional bool is_nullable_codes     = 3;   // added after stabilisation
///   optional bool all_values_referenced = 4;   // absent/false = unknown (conservative)
/// }
/// </code>
/// Both booleans are modelled as <see cref="bool"/>? because absent and present-and-false are
/// different states. <c>is_nullable_codes</c> absent means "fall back to the parent dtype's
/// nullability", which is a different child DType from <c>Nullable</c>
/// (vortex-array-0.86.1/src/arrays/dict/vtable/mod.rs). The corpus contains both spellings:
/// <c>encodings/dict.vortex</c> carries <c>08 05 10 02 18 00 20 00</c>, i.e. both optionals
/// present and false.
/// </remarks>
public readonly struct DictMetadata : IEquatable<DictMetadata>
{
    private const string MessageName = "DictMetadata";

    /// <summary>Creates dictionary metadata.</summary>
    /// <param name="valuesLength">Number of dictionary entries (tag 1).</param>
    /// <param name="codesPType">Physical type of the codes child (tag 2).</param>
    /// <param name="isNullableCodes">Whether the codes child is nullable (tag 3), or null when absent.</param>
    /// <param name="allValuesReferenced">Whether every value is referenced (tag 4), or null when absent.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="codesPType"/> is undefined.</exception>
    public DictMetadata(uint valuesLength, PType codesPType, bool? isNullableCodes, bool? allValuesReferenced)
    {
        if (!PTypeExtensions.IsDefined(codesPType))
        {
            throw new ArgumentOutOfRangeException(nameof(codesPType), codesPType, "Undefined PType.");
        }

        ValuesLength = valuesLength;
        CodesPType = codesPType;
        IsNullableCodes = isNullableCodes;
        AllValuesReferenced = allValuesReferenced;
    }

    /// <summary>
    /// Number of dictionary entries (tag 1). Class I: the decoder must validate every code against
    /// <c>[0, ValuesLength)</c> and <c>ValuesLength</c> against the actual values child
    /// (docs/08-semantics.md §5).
    /// </summary>
    public uint ValuesLength { get; }

    /// <summary>Physical type of the codes child (tag 2). Class I: it is the code stride.</summary>
    public PType CodesPType { get; }

    /// <summary>
    /// Whether the codes child is nullable (tag 3). <c>null</c> means the field was absent and the
    /// decoder must fall back to the parent dtype's nullability.
    /// </summary>
    public bool? IsNullableCodes { get; }

    /// <summary>
    /// Whether every dictionary value is referenced by at least one code (tag 4). <c>null</c> and
    /// <c>false</c> both mean "unknown"; only <c>true</c> is an assertion.
    /// </summary>
    public bool? AllValuesReferenced { get; }

    /// <summary>Reads a <c>vortex.dict</c> array metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or <c>codes_ptype</c> is undefined.</exception>
    public static DictMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        uint valuesLength = 0;
        PType codesPType = PType.U8;
        bool? isNullableCodes = null;
        bool? allValuesReferenced = null;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    valuesLength = MetadataProto.ReadUInt32(ref reader, wire, MessageName, "values_len");
                    break;
                case 2:
                    codesPType = MetadataProto.ReadPType(ref reader, wire, MessageName, "codes_ptype");
                    break;
                case 3:
                    isNullableCodes = MetadataProto.ReadBool(ref reader, wire, MessageName, "is_nullable_codes");
                    break;
                case 4:
                    allValuesReferenced =
                        MetadataProto.ReadBool(ref reader, wire, MessageName, "all_values_referenced");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new DictMetadata(valuesLength, codesPType, isNullableCodes, allValuesReferenced);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in DictMetadata value)
    {
        writer.WriteUInt32(1, value.ValuesLength);
        writer.WriteEnum(2, (int)value.CodesPType);
        if (value.IsNullableCodes.HasValue)
        {
            writer.WriteBoolAlways(3, value.IsNullableCodes.GetValueOrDefault());
        }

        if (value.AllValuesReferenced.HasValue)
        {
            writer.WriteBoolAlways(4, value.AllValuesReferenced.GetValueOrDefault());
        }
    }

    /// <inheritdoc/>
    public bool Equals(DictMetadata other) =>
        ValuesLength == other.ValuesLength
        && CodesPType == other.CodesPType
        && IsNullableCodes == other.IsNullableCodes
        && AllValuesReferenced == other.AllValuesReferenced;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DictMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(ValuesLength, CodesPType, IsNullableCodes, AllValuesReferenced);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DictMetadata left, DictMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DictMetadata left, DictMetadata right) => !left.Equals(right);
}
