using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.sequence</c> metadata:
/// <c>message SequenceMetadata { ScalarValue base = 1; ScalarValue multiplier = 2; }</c>,
/// describing <c>A[i] = base + i * multiplier</c>.
/// </summary>
/// <remarks>
/// Both values stay <b>untyped</b>. A wire <c>ScalarValue</c> carries no type tag, so it can only
/// be interpreted against the node's inherited dtype, which this codec does not have; giving the
/// values a type is the caller's job.
/// </remarks>
internal readonly struct SequenceMetadata : IEquatable<SequenceMetadata>
{
    private const string MessageName = "SequenceMetadata";

    /// <summary>Creates sequence metadata.</summary>
    /// <param name="baseValue">The value at index 0 (tag 1).</param>
    /// <param name="multiplier">The per-index step (tag 2).</param>
    /// <exception cref="ArgumentException">Either value is absent.</exception>
    public SequenceMetadata(ScalarValue baseValue, ScalarValue multiplier)
    {
        if (baseValue.IsAbsent)
        {
            throw new ArgumentException("A sequence base value cannot be absent.", nameof(baseValue));
        }

        if (multiplier.IsAbsent)
        {
            throw new ArgumentException("A sequence multiplier cannot be absent.", nameof(multiplier));
        }

        Base = baseValue;
        Multiplier = multiplier;
    }

    /// <summary>The value at index 0 (tag 1). Never absent.</summary>
    public ScalarValue Base { get; }

    /// <summary>The per-index step (tag 2). Never absent.</summary>
    public ScalarValue Multiplier { get; }

    /// <summary>Reads a <c>vortex.sequence</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <param name="store">Store the two value nodes are appended to.</param>
    /// <param name="dtypes">Arena for a dtype nested inside a <c>variant_value</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="dtypes"/> is null.</exception>
    /// <exception cref="VortexFormatException">
    /// The payload is malformed, or either value is missing, or either value is present but sets no
    /// case at all; a field that is there but empty is rejected just like an absent one.
    /// </exception>
    public static SequenceMetadata Read(ReadOnlySpan<byte> metadata, ScalarStore store, DTypeArena dtypes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dtypes);

        ProtoReader reader = new ProtoReader(metadata);
        ScalarValue baseValue = default;
        ScalarValue multiplier = default;
        bool hasBase = false;
        bool hasMultiplier = false;

        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                {
                    ReadOnlySpan<byte> body = MetadataProto.ReadBytes(ref reader, wire, MessageName, "base");
                    baseValue = ScalarProtobuf.ReadValue(body, store, dtypes);
                    hasBase = true;
                    break;
                }

                case 2:
                {
                    ReadOnlySpan<byte> body = MetadataProto.ReadBytes(ref reader, wire, MessageName, "multiplier");
                    multiplier = ScalarProtobuf.ReadValue(body, store, dtypes);
                    hasMultiplier = true;
                    break;
                }

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (!hasBase)
        {
            MetadataProto.ThrowMissingRequired(MessageName, "base");
        }

        if (!hasMultiplier)
        {
            MetadataProto.ThrowMissingRequired(MessageName, "multiplier");
        }

        if (baseValue.IsAbsent)
        {
            ThrowEmptyValue("base");
        }

        if (multiplier.IsAbsent)
        {
            ThrowEmptyValue("multiplier");
        }

        return new SequenceMetadata(baseValue, multiplier);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in SequenceMetadata value)
    {
        ScalarProtobuf.WriteValueField(ref writer, 1, value.Base);
        ScalarProtobuf.WriteValueField(ref writer, 2, value.Multiplier);
    }

    /// <inheritdoc/>
    public bool Equals(SequenceMetadata other) =>
        Base.Equals(other.Base) && Multiplier.Equals(other.Multiplier);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SequenceMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Base, Multiplier);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SequenceMetadata left, SequenceMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SequenceMetadata left, SequenceMetadata right) => !left.Equals(right);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEmptyValue(string field) =>
        throw new VortexFormatException(
            $"{MessageName}.{field} is present but sets no ScalarValue case, which is not a value.");
}
