// vortex.decimal — vortex-array-0.86.1/src/arrays/decimal/vtable/mod.rs. spec/METADATA.md.
using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types.Numerics;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.decimal</c> metadata: <c>message DecimalMetadata { DecimalType values_type = 1; }</c>.
/// </summary>
/// <remarks>
/// The storage width is class I — it is the stride of buffer 0. prost would coerce an unknown
/// enumeration value to <c>DecimalType::I8 = 0</c> and read the buffer one byte at a time; this
/// codec rejects it instead. An <em>absent</em> field is still a legal <c>I8</c>: the corpus file
/// <c>types/decimal2_1_nullable_r1024</c> carries zero metadata bytes.
/// </remarks>
public readonly struct DecimalMetadata : IEquatable<DecimalMetadata>
{
    private const string MessageName = "DecimalMetadata";

    /// <summary>Creates the metadata for a decimal array with the given storage width.</summary>
    /// <param name="valuesType">Storage width of the unscaled values.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="valuesType"/> is outside 0..5.</exception>
    public DecimalMetadata(DecimalStorageType valuesType)
    {
        if (!DecimalStorage.IsDefined(valuesType))
        {
            throw new ArgumentOutOfRangeException(nameof(valuesType), valuesType, "Undefined DecimalStorageType.");
        }

        ValuesType = valuesType;
    }

    /// <summary>Storage width of the unscaled values (tag 1).</summary>
    public DecimalStorageType ValuesType { get; }

    /// <summary>Reads a <c>vortex.decimal</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes; empty means <c>I8</c>.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or the storage width is undefined.</exception>
    public static DecimalMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        int raw = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            if (field == 1)
            {
                MetadataProto.Expect(wire, ProtoWireType.Varint, MessageName, "values_type");
                raw = reader.ReadInt32();
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        if ((uint)raw > (uint)DecimalStorageType.I256)
        {
            MetadataProto.ThrowEnumOutOfRange(
                MessageName, "values_type", raw, "DecimalType", (int)DecimalStorageType.I256);
        }

        return new DecimalMetadata((DecimalStorageType)raw);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in DecimalMetadata value) =>
        writer.WriteEnum(1, (int)value.ValuesType);

    /// <inheritdoc/>
    public bool Equals(DecimalMetadata other) => ValuesType == other.ValuesType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DecimalMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => ValuesType.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DecimalMetadata left, DecimalMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DecimalMetadata left, DecimalMetadata right) => !left.Equals(right);
}
