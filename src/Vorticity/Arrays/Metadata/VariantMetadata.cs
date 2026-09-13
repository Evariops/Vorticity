// vortex.variant and vortex.parquet.variant — vortex-array-0.86.1/src/arrays/variant/vtable/mod.rs
// and vortex-parquet-variant-0.86.1/src/vtable.rs. spec/METADATA.md.
//
// Both messages exist to say the same thing in two ways: WHICH CHILDREN ARE PRESENT. Neither
// carries a length, a physical type or anything else the decoder could get wrong by arithmetic;
// what it can get wrong is counting children, which is why both readers return presence rather
// than a payload.
using System;

using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.variant</c> array metadata.
/// </summary>
/// <remarks>
/// <code>
/// message VariantMetadata {
///   optional DType shredded_dtype = 1;
/// }
/// </code>
/// The shredded dtype is the ONLY field, and its presence is what says whether there is a second
/// child. The dtype itself is not decoded here: this build refuses a shredded variant, so the
/// bytes are counted and skipped rather than parsed into a type nothing will use.
/// </remarks>
public readonly struct VariantMetadata : IEquatable<VariantMetadata>
{
    private const string MessageName = "VariantMetadata";

    /// <summary>Creates variant metadata.</summary>
    /// <param name="hasShreddedDType">Whether a shredded dtype, and so a shredded child, is present.</param>
    public VariantMetadata(bool hasShreddedDType) => HasShreddedDType = hasShreddedDType;

    /// <summary>Whether the node carries a shredded child (tag 1 present).</summary>
    public bool HasShreddedDType { get; }

    /// <summary>Reads the message body: no outer tag, no length prefix.</summary>
    /// <param name="metadata">The metadata bytes.</param>
    /// <returns>The parsed metadata.</returns>
    /// <exception cref="VortexFormatException">A field has the wrong wire type.</exception>
    public static VariantMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        bool shredded = false;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                _ = MetadataProto.ReadBytes(ref reader, wire, MessageName, "shredded_dtype");
                shredded = true;
                continue;
            }

            reader.SkipField(wire);
        }

        return new VariantMetadata(shredded);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    /// <exception cref="ArgumentException">The metadata declares a shredded child.</exception>
    public static void Write(ref ProtoWriter writer, in VariantMetadata value)
    {
        if (value.HasShreddedDType)
        {
            throw new ArgumentException(
                "This build does not write a shredded variant.", nameof(value));
        }
    }

    /// <inheritdoc/>
    public bool Equals(VariantMetadata other) => HasShreddedDType == other.HasShreddedDType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VariantMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HasShreddedDType.GetHashCode();

    /// <summary>Equality.</summary>
    /// <param name="left">Left.</param>
    /// <param name="right">Right.</param>
    public static bool operator ==(VariantMetadata left, VariantMetadata right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    /// <param name="left">Left.</param>
    /// <param name="right">Right.</param>
    public static bool operator !=(VariantMetadata left, VariantMetadata right) => !left.Equals(right);
}

/// <summary>
/// <c>vortex.parquet.variant</c> array metadata.
/// </summary>
/// <remarks>
/// <code>
/// message ParquetVariantMetadata {
///   bool has_value                  = 1;
///   optional DType typed_value_dtype = 2;
///   bool value_nullable             = 3;
/// }
/// </code>
/// The child count is <c>1 + has_value + (typed_value_dtype is present)</c>, and MAY be one more
/// than that: an explicit validity child comes first when the array is nullable and its validity is
/// not the dtype's. That "or one more" is upstream's own rule, not a tolerance invented here.
/// </remarks>
public readonly struct ParquetVariantMetadata : IEquatable<ParquetVariantMetadata>
{
    private const string MessageName = "ParquetVariantMetadata";

    /// <summary>Creates parquet-variant metadata.</summary>
    /// <param name="hasValue">Whether the unshredded <c>value</c> child is present (tag 1).</param>
    /// <param name="hasTypedValue">Whether a shredded <c>typed_value</c> child is present (tag 2).</param>
    /// <param name="valueNullable">Whether the <c>value</c> child is nullable (tag 3).</param>
    public ParquetVariantMetadata(bool hasValue, bool hasTypedValue, bool valueNullable)
    {
        HasValue = hasValue;
        HasTypedValue = hasTypedValue;
        ValueNullable = valueNullable;
    }

    /// <summary>Whether the unshredded <c>value</c> child is present.</summary>
    public bool HasValue { get; }

    /// <summary>Whether a shredded <c>typed_value</c> child is present.</summary>
    public bool HasTypedValue { get; }

    /// <summary>Whether the <c>value</c> child is nullable.</summary>
    public bool ValueNullable { get; }

    /// <summary>Reads the message body: no outer tag, no length prefix.</summary>
    /// <param name="metadata">The metadata bytes.</param>
    /// <returns>The parsed metadata.</returns>
    /// <exception cref="VortexFormatException">A field has the wrong wire type.</exception>
    public static ParquetVariantMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        bool hasValue = false;
        bool hasTypedValue = false;
        bool valueNullable = false;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    hasValue = MetadataProto.ReadBool(ref reader, wire, MessageName, "has_value");
                    break;
                case 2:
                    _ = MetadataProto.ReadBytes(ref reader, wire, MessageName, "typed_value_dtype");
                    hasTypedValue = true;
                    break;
                case 3:
                    valueNullable = MetadataProto.ReadBool(ref reader, wire, MessageName, "value_nullable");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ParquetVariantMetadata(hasValue, hasTypedValue, valueNullable);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    /// <exception cref="ArgumentException">The metadata declares a shredded child.</exception>
    public static void Write(ref ProtoWriter writer, in ParquetVariantMetadata value)
    {
        if (value.HasTypedValue)
        {
            throw new ArgumentException(
                "This build does not write a shredded parquet variant.", nameof(value));
        }

        writer.WriteBoolAlways(1, value.HasValue);
        writer.WriteBoolAlways(3, value.ValueNullable);
    }

    /// <inheritdoc/>
    public bool Equals(ParquetVariantMetadata other) =>
        HasValue == other.HasValue &&
        HasTypedValue == other.HasTypedValue &&
        ValueNullable == other.ValueNullable;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ParquetVariantMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(HasValue, HasTypedValue, ValueNullable);

    /// <summary>Equality.</summary>
    /// <param name="left">Left.</param>
    /// <param name="right">Right.</param>
    public static bool operator ==(ParquetVariantMetadata left, ParquetVariantMetadata right) =>
        left.Equals(right);

    /// <summary>Inequality.</summary>
    /// <param name="left">Left.</param>
    /// <param name="right">Right.</param>
    public static bool operator !=(ParquetVariantMetadata left, ParquetVariantMetadata right) =>
        !left.Equals(right);
}
