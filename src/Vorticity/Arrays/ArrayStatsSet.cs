using System;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Arrays;

/// <summary>
/// Per-node statistics read off <c>ArrayNode.stats</c>.
/// </summary>
/// <remarks>
/// A view over one entry of an <see cref="ArrayNodeArena"/>'s stats list, so it is only meaningful
/// until that arena is <see cref="ArrayNodeArena.Reset"/>. Statistics are advisory: they are
/// surfaced as the file wrote them and are never trusted for a correctness decision unless
/// <c>VortexReadOptions.VerifyStatistics</c> is on. A statistic that the file omits is unknown, not
/// false or zero, which is why those are reachable only through a <c>TryGet</c>.
/// </remarks>
public readonly ref struct ArrayStatsSet
{
    private readonly ArrayNodeArena? _arena;
    private readonly int _index;

    internal ArrayStatsSet(ArrayNodeArena arena, int index)
    {
        _arena = arena;
        _index = index;
    }

    /// <summary><see langword="true"/> when the node carried no <c>stats</c> table at all.</summary>
    public bool IsEmpty => _arena is null;

    /// <summary><see langword="true"/> when a <c>min</c> is present and non-empty.</summary>
    public bool HasMin => !IsEmpty && Record.MinLength > 0;

    /// <summary>The <c>min</c> as a Protobuf-serialized <c>ScalarValue</c>. Empty when absent.</summary>
    public ReadOnlySpan<byte> MinBytes
    {
        get
        {
            if (IsEmpty)
            {
                return default;
            }

            ref readonly ArrayStatsRecord r = ref Record;
            return r.MinLength > 0 ? _arena!.TreeSpan.Slice(r.MinOffset, r.MinLength) : default;
        }
    }

    /// <summary>Exactness of <see cref="MinBytes"/>. Defaults to <see cref="StatPrecision.Inexact"/>.</summary>
    public StatPrecision MinPrecision => IsEmpty ? StatPrecision.Inexact : Record.MinPrecision;

    /// <summary><see langword="true"/> when a <c>max</c> is present and non-empty.</summary>
    public bool HasMax => !IsEmpty && Record.MaxLength > 0;

    /// <summary>The <c>max</c> as a Protobuf-serialized <c>ScalarValue</c>. Empty when absent.</summary>
    public ReadOnlySpan<byte> MaxBytes
    {
        get
        {
            if (IsEmpty)
            {
                return default;
            }

            ref readonly ArrayStatsRecord r = ref Record;
            return r.MaxLength > 0 ? _arena!.TreeSpan.Slice(r.MaxOffset, r.MaxLength) : default;
        }
    }

    /// <summary>Exactness of <see cref="MaxBytes"/>. Defaults to <see cref="StatPrecision.Inexact"/>.</summary>
    public StatPrecision MaxPrecision => IsEmpty ? StatPrecision.Inexact : Record.MaxPrecision;

    /// <summary><see langword="true"/> when a <c>sum</c> is present and non-empty.</summary>
    public bool HasSum => !IsEmpty && Record.SumLength > 0;

    /// <summary>The <c>sum</c> as a Protobuf-serialized <c>ScalarValue</c>. Empty when absent.</summary>
    public ReadOnlySpan<byte> SumBytes
    {
        get
        {
            if (IsEmpty)
            {
                return default;
            }

            ref readonly ArrayStatsRecord r = ref Record;
            return r.SumLength > 0 ? _arena!.TreeSpan.Slice(r.SumOffset, r.SumLength) : default;
        }
    }

    /// <summary>Reads the tri-state <c>is_sorted</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetIsSorted(out bool value) => TryGetFlag(ArrayStatsRecord.FlagIsSorted, ArrayStatsRecord.ValueIsSorted, out value);

    /// <summary>Reads the tri-state <c>is_strict_sorted</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetIsStrictSorted(out bool value) => TryGetFlag(ArrayStatsRecord.FlagIsStrictSorted, ArrayStatsRecord.ValueIsStrictSorted, out value);

    /// <summary>Reads the tri-state <c>is_constant</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetIsConstant(out bool value) => TryGetFlag(ArrayStatsRecord.FlagIsConstant, ArrayStatsRecord.ValueIsConstant, out value);

    /// <summary>Reads the tri-state <c>null_count</c>. A present 0 is not the same as absent.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetNullCount(out ulong value) => TryGetCount(ArrayStatsRecord.FlagNullCount, 0, out value);

    /// <summary>Reads the tri-state <c>uncompressed_size_in_bytes</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetUncompressedSizeInBytes(out ulong value) => TryGetCount(ArrayStatsRecord.FlagUncompressedSize, 1, out value);

    /// <summary>Reads the tri-state <c>nan_count</c>.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns><see langword="false"/> when the statistic is unknown.</returns>
    public bool TryGetNanCount(out ulong value) => TryGetCount(ArrayStatsRecord.FlagNanCount, 2, out value);

    private ref readonly ArrayStatsRecord Record => ref _arena!.StatsRef(_index);

    private bool TryGetFlag(uint presenceMask, uint valueMask, out bool value)
    {
        if (IsEmpty)
        {
            value = false;
            return false;
        }

        uint flags = Record.Flags;
        value = (flags & valueMask) != 0;
        return (flags & presenceMask) != 0;
    }

    private bool TryGetCount(uint presenceMask, int slot, out ulong value)
    {
        if (IsEmpty)
        {
            value = 0;
            return false;
        }

        ref readonly ArrayStatsRecord r = ref Record;
        if ((r.Flags & presenceMask) == 0)
        {
            value = 0;
            return false;
        }

        value = slot switch
        {
            0 => r.NullCount,
            1 => r.UncompressedSizeInBytes,
            _ => r.NanCount,
        };
        return true;
    }
}

/// <summary>
/// One flattened <c>ArrayStats</c> table. The three scalar statistics are recorded as
/// (offset, length) into the arena's retained copy of the <c>Array</c> FlatBuffer, so nothing is
/// copied and nothing is parsed until a caller asks.
/// </summary>
internal struct ArrayStatsRecord
{
    internal const uint FlagIsSorted = 1u << 0;
    internal const uint FlagIsStrictSorted = 1u << 1;
    internal const uint FlagIsConstant = 1u << 2;
    internal const uint FlagNullCount = 1u << 3;
    internal const uint FlagUncompressedSize = 1u << 4;
    internal const uint FlagNanCount = 1u << 5;
    internal const uint ValueIsSorted = 1u << 6;
    internal const uint ValueIsStrictSorted = 1u << 7;
    internal const uint ValueIsConstant = 1u << 8;

    internal int MinOffset;
    internal int MinLength;
    internal int MaxOffset;
    internal int MaxLength;
    internal int SumOffset;
    internal int SumLength;
    internal ulong NullCount;
    internal ulong UncompressedSizeInBytes;
    internal ulong NanCount;
    internal uint Flags;
    internal StatPrecision MinPrecision;
    internal StatPrecision MaxPrecision;
}
