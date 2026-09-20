using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Vorticity;

/// <summary>
/// A half-open range of rows, <c>[Start, End)</c>. It sits in the root namespace, rather than
/// beside the file reader, so that the layout readers and the scan both see it without a
/// <c>using</c>. Bounds are <see cref="long"/> because a file may hold more rows than an
/// <see cref="int"/> can address, which also rules out <see cref="System.Range"/>.
/// </summary>
/// <remarks>
/// A row range handed to a layout reader is <em>local to the receiving node</em>: 0-based within
/// that node's rows, and translated by each reader before it reaches a child. Bounds are always
/// non-negative; a negative or inverted range is a caller error and throws
/// <see cref="ArgumentOutOfRangeException"/>, never <see cref="VortexFormatException"/>.
/// </remarks>
public readonly struct RowRange : IEquatable<RowRange>
{
    private readonly long _start;
    private readonly long _end;

    /// <summary>Creates the half-open range <c>[start, end)</c>.</summary>
    /// <param name="start">First row, inclusive. Must be non-negative.</param>
    /// <param name="end">One past the last row. Must be at least <paramref name="start"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> is negative, or <paramref name="end"/> is below
    /// <paramref name="start"/>.
    /// </exception>
    public RowRange(long start, long end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        _start = start;
        _end = end;
    }

    /// <summary>Creates the range covering <paramref name="length"/> rows from <paramref name="start"/>.</summary>
    /// <param name="start">First row, inclusive. Must be non-negative.</param>
    /// <param name="length">Row count. Must be non-negative and must not overflow.</param>
    /// <returns>The range <c>[start, start + length)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An argument is negative, or the sum overflows <see cref="long"/>.
    /// </exception>
    public static RowRange FromLength(long start, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (long.MaxValue - length < start)
        {
            ThrowLengthOverflow(start, length);
        }

        return new RowRange(start, start + length);
    }

    /// <summary>The canonical empty range, <c>[0, 0)</c>.</summary>
    public static RowRange Empty => default;

    /// <summary>First row, inclusive.</summary>
    public long Start => _start;

    /// <summary>One past the last row.</summary>
    public long End => _end;

    /// <summary>Number of rows covered. Never negative.</summary>
    public long Length => _end - _start;

    /// <summary>True when the range covers no rows.</summary>
    public bool IsEmpty => _end == _start;

    /// <summary>
    /// The overlap of this range and <paramref name="other"/>, or <see cref="Empty"/> when they do
    /// not overlap. A non-overlapping result is always the canonical <c>[0, 0)</c>, so two disjoint
    /// intersections compare equal regardless of where they failed to meet.
    /// </summary>
    /// <param name="other">The range to intersect with.</param>
    /// <returns>The overlap.</returns>
    public RowRange Intersect(RowRange other)
    {
        long start = Math.Max(_start, other._start);
        long end = Math.Min(_end, other._end);
        return end <= start ? Empty : new RowRange(start, end);
    }

    /// <summary>True when <paramref name="row"/> lies in <c>[Start, End)</c>.</summary>
    /// <param name="row">The row index to test.</param>
    /// <returns>Whether the row is covered.</returns>
    public bool Contains(long row) => row >= _start && row < _end;

    /// <inheritdoc/>
    public bool Equals(RowRange other) => _start == other._start && _end == other._end;

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is RowRange other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_start, _end);

    /// <summary>Equality.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns>Whether both bounds match.</returns>
    public static bool operator ==(RowRange a, RowRange b) => a.Equals(b);

    /// <summary>Inequality.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns>Whether either bound differs.</returns>
    public static bool operator !=(RowRange a, RowRange b) => !a.Equals(b);

    /// <summary>Culture-invariant rendering, <c>[start, end)</c>.</summary>
    /// <returns>The rendered range.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{_start}, {_end})");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLengthOverflow(long start, long length) =>
        throw new ArgumentOutOfRangeException(
            nameof(length),
            length,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Row range starting at {start} with length {length} ends past long.MaxValue."));
}
