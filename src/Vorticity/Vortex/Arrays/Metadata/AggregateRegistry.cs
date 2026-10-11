using System;
using System.Buffers.Binary;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// The six core-edition zone-map aggregates, plus <see cref="Unknown"/>.
/// </summary>
/// <remarks>
/// An id outside this set disables that aggregate's pruning and is <b>never</b> an error: an
/// aggregate nobody understands simply contributes nothing, and the read still succeeds.
/// </remarks>
internal enum AggregateId : byte
{
    /// <summary>An id this library does not know. Pruning with it is disabled; the read succeeds.</summary>
    Unknown = 0,

    /// <summary><c>vortex.min</c>: the exact minimum.</summary>
    Min = 1,

    /// <summary><c>vortex.max</c>: the exact maximum.</summary>
    Max = 2,

    /// <summary><c>vortex.bounded_min</c>: a truncated lower bound, not an extreme.</summary>
    BoundedMin = 3,

    /// <summary><c>vortex.bounded_max</c>: a truncated upper bound, not an extreme.</summary>
    BoundedMax = 4,

    /// <summary><c>vortex.nan_count</c>.</summary>
    NanCount = 5,

    /// <summary><c>vortex.null_count</c>.</summary>
    NullCount = 6,
}

/// <summary>Resolves zone-map aggregate ids and decodes their options payloads.</summary>
internal static class AggregateRegistry
{
    private static ReadOnlySpan<byte> IdMin => "vortex.min"u8;
    private static ReadOnlySpan<byte> IdMax => "vortex.max"u8;
    private static ReadOnlySpan<byte> IdBoundedMin => "vortex.bounded_min"u8;
    private static ReadOnlySpan<byte> IdBoundedMax => "vortex.bounded_max"u8;
    private static ReadOnlySpan<byte> IdNanCount => "vortex.nan_count"u8;
    private static ReadOnlySpan<byte> IdNullCount => "vortex.null_count"u8;

    /// <summary>
    /// Maps an aggregate id to its <see cref="AggregateId"/>. Anything unrecognized maps to
    /// <see cref="AggregateId.Unknown"/>; that is a supported outcome, not a failure.
    /// </summary>
    /// <param name="idUtf8">The id as UTF-8 bytes, e.g. <c>vortex.bounded_min</c>.</param>
    public static AggregateId Resolve(ReadOnlySpan<byte> idUtf8)
    {
        // Length first, then one discriminating byte, then a full compare: no hashing of
        // file-controlled bytes and no allocation.
        switch (idUtf8.Length)
        {
            case 10:
                // "vortex.min" and "vortex.max" differ only at index 8.
                if (idUtf8[8] == (byte)'i')
                {
                    return idUtf8.SequenceEqual(IdMin) ? AggregateId.Min : AggregateId.Unknown;
                }

                return idUtf8.SequenceEqual(IdMax) ? AggregateId.Max : AggregateId.Unknown;

            case 16:
                return idUtf8.SequenceEqual(IdNanCount) ? AggregateId.NanCount : AggregateId.Unknown;

            case 17:
                return idUtf8.SequenceEqual(IdNullCount) ? AggregateId.NullCount : AggregateId.Unknown;

            case 18:
                if (idUtf8[16] == (byte)'i')
                {
                    return idUtf8.SequenceEqual(IdBoundedMin) ? AggregateId.BoundedMin : AggregateId.Unknown;
                }

                return idUtf8.SequenceEqual(IdBoundedMax) ? AggregateId.BoundedMax : AggregateId.Unknown;

            default:
                return AggregateId.Unknown;
        }
    }

    /// <summary>
    /// Decodes the <c>n</c> of <c>vortex.bounded_min(n)</c> / <c>vortex.bounded_max(n)</c> from an
    /// <c>AggregateSpecProto.options</c> payload.
    /// </summary>
    /// <param name="aggregate">The resolved aggregate.</param>
    /// <param name="options">The raw options bytes.</param>
    /// <param name="boundLength">Receives the maximum bound length in bytes.</param>
    /// <returns>
    /// False when <paramref name="aggregate"/> is not a bounded aggregate, or the payload is not
    /// exactly eight bytes, or the value is zero or does not fit in a <see cref="uint"/>.
    /// </returns>
    /// <remarks>
    /// The payload is <b>not</b> a Protobuf message: it is the bound length as eight raw
    /// little-endian bytes, and only exactly that length with a non-zero value is accepted.
    /// Returning false rather than throwing is deliberate: an unusable bound disables pruning,
    /// and pruning is advisory.
    /// </remarks>
    public static bool TryGetBoundLength(
        AggregateId aggregate, ReadOnlySpan<byte> options, out uint boundLength)
    {
        boundLength = 0;
        if (aggregate is not (AggregateId.BoundedMin or AggregateId.BoundedMax))
        {
            return false;
        }

        if (options.Length != sizeof(ulong))
        {
            return false;
        }

        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(options);
        if (value == 0 || value > uint.MaxValue)
        {
            return false;
        }

        boundLength = (uint)value;
        return true;
    }

    /// <summary>The canonical UTF-8 id of an aggregate, or an empty span for <see cref="AggregateId.Unknown"/>.</summary>
    /// <param name="aggregate">The aggregate.</param>
    public static ReadOnlySpan<byte> IdUtf8(AggregateId aggregate) => aggregate switch
    {
        AggregateId.Min => IdMin,
        AggregateId.Max => IdMax,
        AggregateId.BoundedMin => IdBoundedMin,
        AggregateId.BoundedMax => IdBoundedMax,
        AggregateId.NanCount => IdNanCount,
        AggregateId.NullCount => IdNullCount,
        _ => default,
    };
}
