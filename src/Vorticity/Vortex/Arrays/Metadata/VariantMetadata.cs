using System;

using Vorticity.Serialization.Protobuf;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.variant</c> array metadata. The message carries no length and no physical type: all it
/// says is whether there is a shredded child, and of which dtype.
/// </summary>
/// <remarks>
/// <code>
/// message VariantMetadata {
///   optional DType shredded_dtype = 1;
/// }
/// </code>
/// The shredded dtype is the only field, and its presence is what says whether there is a second
/// child. Its bytes are kept as the file holds them, and the decoder reads the dtype from them.
/// </remarks>
internal readonly struct VariantMetadata : IEquatable<VariantMetadata>
{
    private const string MessageName = "VariantMetadata";

    /// <summary>Creates variant metadata.</summary>
    /// <param name="hasShreddedDType">Whether a shredded dtype, and so a shredded child, is present.</param>
    public VariantMetadata(bool hasShreddedDType)
        : this(hasShreddedDType ? [] : null)
    {
    }

    /// <summary>Creates variant metadata whose shredded child is of the dtype <paramref name="shreddedDType"/> serializes.</summary>
    /// <param name="shreddedDType">The dtype message of the shredded child (tag 1), or null for none.</param>
    public VariantMetadata(byte[]? shreddedDType) => ShreddedDType = shreddedDType;

    /// <summary>Whether the node carries a shredded child (tag 1 present).</summary>
    public bool HasShreddedDType => ShreddedDType is not null;

    /// <summary>The dtype message of the shredded child, as the file holds it; null when there is none.</summary>
    public byte[]? ShreddedDType { get; }

    /// <summary>Reads the message body: no outer tag, no length prefix.</summary>
    /// <param name="metadata">The metadata bytes.</param>
    /// <returns>The parsed metadata.</returns>
    /// <exception cref="VortexFormatException">A field has the wrong wire type.</exception>
    public static VariantMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        byte[]? shredded = null;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                shredded = MetadataProto.ReadBytes(ref reader, wire, MessageName, "shredded_dtype").ToArray();
                continue;
            }

            reader.SkipField(wire);
        }

        return new VariantMetadata(shredded);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    /// <exception cref="ArgumentException">The metadata declares a shredded child of no dtype.</exception>
    public static void Write(ref ProtoWriter writer, in VariantMetadata value)
    {
        if (value.ShreddedDType is { } shredded)
        {
            if (shredded.Length == 0)
            {
                throw new ArgumentException("A shredded child is written with its dtype.", nameof(value));
            }

            writer.WriteBytes(1, shredded);
        }
    }

    /// <inheritdoc/>
    public bool Equals(VariantMetadata other) =>
        HasShreddedDType == other.HasShreddedDType && ShreddedDType.AsSpan().SequenceEqual(other.ShreddedDType);

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
/// <c>vortex.parquet.variant</c> array metadata. Like <see cref="VariantMetadata"/>, it only states
/// which children are present, so the reader returns presence and not a payload.
/// </summary>
/// <remarks>
/// <code>
/// message ParquetVariantMetadata {
///   bool has_value                  = 1;
///   optional DType typed_value_dtype = 2;
///   bool value_nullable             = 3;
/// }
/// </code>
/// The child count is <c>1 + has_value + (typed_value_dtype is present)</c>, and may be one more
/// than that: an explicit validity child comes first when the array is nullable and its validity is
/// not the dtype's. That extra child is part of the format, not a tolerance invented here.
/// </remarks>
internal readonly struct ParquetVariantMetadata : IEquatable<ParquetVariantMetadata>
{
    private const string MessageName = "ParquetVariantMetadata";

    /// <summary>Creates parquet-variant metadata.</summary>
    /// <param name="hasValue">Whether the unshredded <c>value</c> child is present (tag 1).</param>
    /// <param name="hasTypedValue">Whether a shredded <c>typed_value</c> child is present (tag 2).</param>
    /// <param name="valueNullable">Whether the <c>value</c> child is nullable (tag 3).</param>
    public ParquetVariantMetadata(bool hasValue, bool hasTypedValue, bool valueNullable)
        : this(hasValue, hasTypedValue ? [] : null, valueNullable)
    {
    }

    /// <summary>Creates parquet-variant metadata whose shredded child is of the dtype <paramref name="typedValueDType"/> serializes.</summary>
    /// <param name="hasValue">Whether the unshredded <c>value</c> child is present (tag 1).</param>
    /// <param name="typedValueDType">The dtype message of the <c>typed_value</c> child (tag 2), or null for none.</param>
    /// <param name="valueNullable">Whether the <c>value</c> child is nullable (tag 3).</param>
    public ParquetVariantMetadata(bool hasValue, byte[]? typedValueDType, bool valueNullable)
    {
        HasValue = hasValue;
        TypedValueDType = typedValueDType;
        ValueNullable = valueNullable;
    }

    /// <summary>Whether the unshredded <c>value</c> child is present.</summary>
    public bool HasValue { get; }

    /// <summary>Whether a shredded <c>typed_value</c> child is present.</summary>
    public bool HasTypedValue => TypedValueDType is not null;

    /// <summary>The dtype message of the shredded <c>typed_value</c> child, as the file holds it; null when there is none.</summary>
    public byte[]? TypedValueDType { get; }

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
        byte[]? typedValueDType = null;
        bool valueNullable = false;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    hasValue = MetadataProto.ReadBool(ref reader, wire, MessageName, "has_value");
                    break;
                case 2:
                    typedValueDType = MetadataProto.ReadBytes(ref reader, wire, MessageName, "typed_value_dtype").ToArray();
                    break;
                case 3:
                    valueNullable = MetadataProto.ReadBool(ref reader, wire, MessageName, "value_nullable");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ParquetVariantMetadata(hasValue, typedValueDType, valueNullable);
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
        TypedValueDType.AsSpan().SequenceEqual(other.TypedValueDType) &&
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
