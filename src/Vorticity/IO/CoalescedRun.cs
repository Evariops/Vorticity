using System;

namespace Vorticity.IO;

/// <summary>
/// One contiguous file read that satisfies a consecutive group of segment specs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Start"/> is <b>always</b> a multiple of <see cref="VortexLimits.MaxAlignment"/>
/// (64). That is the whole point of the type. A native buffer allocated for the run places the
/// segment at file offset <c>o</c> at memory offset <c>o - Start</c>; if <c>Start</c> is a
/// multiple of 64 and <c>o</c> is a multiple of <c>2^k</c> with <c>k ≤ 6</c> — which the writer
/// guarantees and <see cref="VortexLimits.MaxAlignmentExponent"/> enforces — then
/// <c>o - Start</c> is a multiple of <c>2^k</c>, so alignment survives coalescing.
/// </para>
/// <para>
/// The bytes in <c>[Start, specs[FirstIndex].Offset)</c> and any bytes in the gaps between
/// segments are read and discarded. That is the cost of one round trip instead of several.
/// </para>
/// </remarks>
internal readonly struct CoalescedRun : IEquatable<CoalescedRun>
{
    /// <summary>Creates a run. Used by <see cref="SegmentCoalescer"/> and by external sources.</summary>
    /// <param name="start">The file offset to read from; must be a multiple of 64.</param>
    /// <param name="length">The number of bytes to read.</param>
    /// <param name="firstIndex">Index of the first covered spec in the caller's sorted list.</param>
    /// <param name="count">How many consecutive specs this run covers.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> is negative or not 64-aligned, <paramref name="length"/> or
    /// <paramref name="firstIndex"/> is negative, or <paramref name="count"/> is not positive.
    /// </exception>
    public CoalescedRun(long start, int length, int firstIndex, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(firstIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if ((start & (VortexLimits.MaxAlignment - 1)) != 0)
        {
            ThrowUnaligned(start);
        }

        Start = start;
        Length = length;
        FirstIndex = firstIndex;
        Count = count;
    }

    /// <summary>The file offset to read from. Always a multiple of 64.</summary>
    public long Start { get; }

    /// <summary>The number of bytes to read, covering every spec in the run.</summary>
    public int Length { get; }

    /// <summary>Index of the first covered spec, in the sorted list handed to the planner.</summary>
    public int FirstIndex { get; }

    /// <summary>How many consecutive specs this run covers. At least one.</summary>
    public int Count { get; }

    /// <summary>Exclusive end offset of the read.</summary>
    public long End => Start + Length;

    /// <inheritdoc/>
    public bool Equals(CoalescedRun other) =>
        Start == other.Start && Length == other.Length &&
        FirstIndex == other.FirstIndex && Count == other.Count;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CoalescedRun other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Start, Length, FirstIndex, Count);

    /// <summary>Value equality.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    public static bool operator ==(CoalescedRun a, CoalescedRun b) => a.Equals(b);

    /// <summary>Value inequality.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    public static bool operator !=(CoalescedRun a, CoalescedRun b) => !a.Equals(b);

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"[{Start}, {End}) covering specs [{FirstIndex}, {FirstIndex + Count})");

    private static void ThrowUnaligned(long start) =>
        throw new ArgumentOutOfRangeException(
            nameof(start),
            start,
            $"A coalesced run must start on a {VortexLimits.MaxAlignment}-byte boundary.");
}
