using System;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// <c>vortex.runend</c> metadata:
/// <c>message RunEndMetadata { PType ends_ptype = 1; uint64 num_runs = 2; uint64 offset = 3; }</c>.
/// </summary>
public readonly struct RunEndMetadata : IEquatable<RunEndMetadata>
{
    private const string MessageName = "RunEndMetadata";

    /// <summary>Creates run-end metadata.</summary>
    /// <param name="endsPType">Physical type of the run-ends child (tag 1).</param>
    /// <param name="runCount">Number of runs (tag 2).</param>
    /// <param name="offset">Row offset of the first visible element within its run (tag 3).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="endsPType"/> is undefined.</exception>
    public RunEndMetadata(PType endsPType, ulong runCount, ulong offset)
    {
        if (!PTypeExtensions.IsDefined(endsPType))
        {
            throw new ArgumentOutOfRangeException(nameof(endsPType), endsPType, "Undefined PType.");
        }

        EndsPType = endsPType;
        NumRuns = runCount;
        Offset = offset;
    }

    /// <summary>Physical type of the run-ends child (tag 1); it fixes the stride of that buffer.</summary>
    public PType EndsPType { get; }

    /// <summary>Number of runs (tag 2).</summary>
    public ulong NumRuns { get; }

    /// <summary>Row offset of the first visible element within its run (tag 3).</summary>
    public ulong Offset { get; }

    /// <summary>Reads a <c>vortex.runend</c> metadata payload.</summary>
    /// <param name="metadata">The raw metadata bytes.</param>
    /// <exception cref="VortexFormatException">The payload is malformed or the ends type is undefined.</exception>
    public static RunEndMetadata Read(ReadOnlySpan<byte> metadata)
    {
        ProtoReader reader = new ProtoReader(metadata);
        PType endsPType = PType.U8;
        ulong numRuns = 0;
        ulong offset = 0;
        while (reader.TryReadTag(out int field, out ProtoWireType wire))
        {
            switch (field)
            {
                case 1:
                    endsPType = MetadataProto.ReadPType(ref reader, wire, MessageName, "ends_ptype");
                    break;
                case 2:
                    numRuns = MetadataProto.ReadUInt64(ref reader, wire, MessageName, "num_runs");
                    break;
                case 3:
                    offset = MetadataProto.ReadUInt64(ref reader, wire, MessageName, "offset");
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new RunEndMetadata(endsPType, numRuns, offset);
    }

    /// <summary>Writes the message body: no outer tag, no length prefix.</summary>
    /// <param name="writer">Writer to append to; must be passed by reference.</param>
    /// <param name="value">The metadata.</param>
    public static void Write(ref ProtoWriter writer, in RunEndMetadata value)
    {
        writer.WriteEnum(1, (int)value.EndsPType);
        writer.WriteUInt64(2, value.NumRuns);
        writer.WriteUInt64(3, value.Offset);
    }

    /// <inheritdoc/>
    public bool Equals(RunEndMetadata other) =>
        EndsPType == other.EndsPType && NumRuns == other.NumRuns && Offset == other.Offset;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RunEndMetadata other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(EndsPType, NumRuns, Offset);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(RunEndMetadata left, RunEndMetadata right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(RunEndMetadata left, RunEndMetadata right) => !left.Equals(right);
}
