using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.datetimeparts</c> metadata:
/// <c>message DateTimePartsMetadata { PType days_ptype = 1; PType seconds_ptype = 2; PType subseconds_ptype = 3; }</c>.
/// </summary>
/// <remarks>Validity lives in the days array, not in the parent node.</remarks>
public readonly struct DateTimePartsMetadata : IEquatable<DateTimePartsMetadata>
{
    private const string MessageName = "DateTimePartsMetadata";

    /// <summary>Creates date-time-parts metadata.</summary>
    /// <param name="daysPType">Physical type of the days child (tag 1).</param>
    /// <param name="secondsPType">Physical type of the seconds child (tag 2).</param>
    /// <param name="subsecondsPType">Physical type of the subseconds child (tag 3).</param>
    /// <exception cref="ArgumentOutOfRangeException">A physical type is undefined.</exception>
    public DateTimePartsMetadata(PType daysPType, PType secondsPType, PType subsecondsPType)
    {
        Require(daysPType, nameof(daysPType));
        Require(secondsPType, nameof(secondsPType));
        Require(subsecondsPType, nameof(subsecondsPType));
        DaysPType = daysPType;
        SecondsPType = secondsPType;
        SubsecondsPType = subsecondsPType;
    }

    /// <summary>Physical type of the days child (tag 1).</summary>
    public PType DaysPType { get; }

    /// <summary>Physical type of the seconds child (tag 2).</summary>
    public PType SecondsPType { get; }

    /// <summary>Physical type of the subseconds child (tag 3).</summary>
    public PType SubsecondsPType { get; }

    /// <summary>Reads a <c>vortex.datetimeparts</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or a physical type is undefined.</exception>
    public static DateTimePartsMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        PType days = PType.U8;
        PType seconds = PType.U8;
        PType subseconds = PType.U8;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    days = MetadataProto.ReadPType(ref reader, wire, MessageName, "days_ptype");
                    break;
                case 2:
                    seconds = MetadataProto.ReadPType(ref reader, wire, MessageName, "seconds_ptype");
                    break;
                case 3:
                    subseconds = MetadataProto.ReadPType(ref reader, wire, MessageName, "subseconds_ptype");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new DateTimePartsMetadata(days, seconds, subseconds);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in DateTimePartsMetadata value)
    {
        writer.WriteEnum(1, (int)value.DaysPType);
        writer.WriteEnum(2, (int)value.SecondsPType);
        writer.WriteEnum(3, (int)value.SubsecondsPType);
    }

    /// <inheritdoc/>
    public bool Equals(DateTimePartsMetadata other) =>
        DaysPType == other.DaysPType
        && SecondsPType == other.SecondsPType
        && SubsecondsPType == other.SubsecondsPType;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DateTimePartsMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(DaysPType, SecondsPType, SubsecondsPType);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(DateTimePartsMetadata left, DateTimePartsMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(DateTimePartsMetadata left, DateTimePartsMetadata right) => !left.Equals(right);

    private static void Require(PType ptype, string parameterName)
    {
        if (!PTypeExtensions.IsDefined(ptype))
        {
            throw new ArgumentOutOfRangeException(parameterName, ptype, "Undefined PType.");
        }
    }
}
