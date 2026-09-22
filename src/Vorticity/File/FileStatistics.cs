using System;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.File;

namespace Vorticity;

/// <summary>
/// File-level statistics, parsed once when the file opens. A struct root carries exactly one entry
/// per top-level field, matched by position and never descending: a nested struct field gets a
/// single entry decoded against the nested DType, and its own fields get none. Any other root
/// carries exactly one entry. Each statistic is typed against its own DType rather than the
/// field's, which is why the two are exposed separately here. The values are surfaced as the file
/// records them, and no correctness decision is ever taken from them on the caller's behalf.
/// </summary>
internal sealed class FileStatistics
{
    private readonly DType[] _fieldDTypes;
    private readonly DType[] _sumDTypes;
    private readonly FieldStatistics[] _fields;

    internal FileStatistics(DType[] fieldDTypes, DType[] sumDTypes, FieldStatistics[] fields)
    {
        _fieldDTypes = fieldDTypes;
        _sumDTypes = sumDTypes;
        _fields = fields;
    }

    /// <summary>Number of fields carrying statistics.</summary>
    public int FieldCount => _fields.Length;

    /// <summary>
    /// The DType field <paramref name="index"/>'s <c>min</c> and <c>max</c> statistics are typed
    /// against — the top-level field's DType, or the whole file DType for a non-struct root.
    /// </summary>
    /// <param name="index">0-based field index, below <see cref="FieldCount"/>.</param>
    /// <returns>The field's DType.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public DType GetFieldDType(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _fields.Length);
        return _fieldDTypes[index];
    }

    /// <summary>
    /// The DType field <paramref name="index"/>'s <c>sum</c> statistic is typed against, which is
    /// <em>not</em> the field's: integers widen to <c>i64</c>/<c>u64</c>, floats to <c>f64</c>,
    /// decimals gain ten digits of precision, and every one of them is nullable because an
    /// overflowing sum is recorded as null.
    /// <see cref="DType.IsDefault"/> when the field has no summable DType, in which case
    /// <see cref="FieldStatistics.HasSum"/> is always <see langword="false"/>.
    /// </summary>
    /// <param name="index">0-based field index, below <see cref="FieldCount"/>.</param>
    /// <returns>The sum statistic's DType, or <c>default</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public DType GetSumDType(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _fields.Length);
        return _sumDTypes[index];
    }

    /// <summary>The statistics recorded for one field.</summary>
    /// <param name="index">0-based field index, below <see cref="FieldCount"/>.</param>
    /// <returns>The field's statistics.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is out of range.</exception>
    public FieldStatistics GetField(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _fields.Length);
        return _fields[index];
    }
}

/// <summary>One field's file-level statistics.</summary>
/// <remarks>
/// Every value is optional and absence is meaningful: an absent statistic licenses nothing, while
/// a present one that is <see cref="StatPrecision.Inexact"/> is a bound rather than a value. The
/// <c>min == max ⇒ constant</c> shortcut is forbidden unless both are
/// <see cref="StatPrecision.Exact"/>.
/// </remarks>
public readonly struct FieldStatistics
{
    [Flags]
    private enum Present : ushort
    {
        None = 0,
        Min = 1,
        Max = 2,
        Sum = 4,
        IsSorted = 8,
        IsStrictSorted = 16,
        IsConstant = 32,
        NullCount = 64,
        UncompressedSizeInBytes = 128,
        NanCount = 256,
    }

    private readonly ScalarValue _min;
    private readonly ScalarValue _max;
    private readonly ScalarValue _sum;
    private readonly ulong _nullCount;
    private readonly ulong _uncompressedSizeInBytes;
    private readonly ulong _nanCount;
    private readonly Present _present;
    private readonly StatPrecision _minPrecision;
    private readonly StatPrecision _maxPrecision;
    private readonly bool _isSorted;
    private readonly bool _isStrictSorted;
    private readonly bool _isConstant;

    internal FieldStatistics(
        ScalarValue min,
        StatPrecision minPrecision,
        bool hasMin,
        ScalarValue max,
        StatPrecision maxPrecision,
        bool hasMax,
        ScalarValue sum,
        bool hasSum,
        bool? isSorted,
        bool? isStrictSorted,
        bool? isConstant,
        ulong? nullCount,
        ulong? uncompressedSizeInBytes,
        ulong? nanCount)
    {
        Present present = Present.None;
        if (hasMin) { present |= Present.Min; }
        if (hasMax) { present |= Present.Max; }
        if (hasSum) { present |= Present.Sum; }
        if (isSorted.HasValue) { present |= Present.IsSorted; }
        if (isStrictSorted.HasValue) { present |= Present.IsStrictSorted; }
        if (isConstant.HasValue) { present |= Present.IsConstant; }
        if (nullCount.HasValue) { present |= Present.NullCount; }
        if (uncompressedSizeInBytes.HasValue) { present |= Present.UncompressedSizeInBytes; }
        if (nanCount.HasValue) { present |= Present.NanCount; }

        _min = min;
        _max = max;
        _sum = sum;
        _minPrecision = minPrecision;
        _maxPrecision = maxPrecision;
        _isSorted = isSorted.GetValueOrDefault();
        _isStrictSorted = isStrictSorted.GetValueOrDefault();
        _isConstant = isConstant.GetValueOrDefault();
        _nullCount = nullCount.GetValueOrDefault();
        _uncompressedSizeInBytes = uncompressedSizeInBytes.GetValueOrDefault();
        _nanCount = nanCount.GetValueOrDefault();
        _present = present;
    }

    /// <summary>True when a <c>min</c> statistic is recorded.</summary>
    internal bool HasMin => (_present & Present.Min) != 0;

    /// <summary>
    /// The <c>min</c> statistic, untyped. Interpret it against
    /// <see cref="FileStatistics.GetFieldDType"/>; <see cref="ScalarValue.IsAbsent"/> when
    /// <see cref="HasMin"/> is false.
    /// </summary>
    internal ScalarValue Min => _min;

    /// <summary>Whether <see cref="Min"/> is the true minimum or only a lower bound.</summary>
    internal StatPrecision MinPrecision => _minPrecision;

    /// <summary>True when a <c>max</c> statistic is recorded.</summary>
    internal bool HasMax => (_present & Present.Max) != 0;

    /// <summary>The <c>max</c> statistic, untyped. See <see cref="Min"/>.</summary>
    internal ScalarValue Max => _max;

    /// <summary>Whether <see cref="Max"/> is the true maximum or only an upper bound.</summary>
    internal StatPrecision MaxPrecision => _maxPrecision;

    /// <summary>True when a <c>sum</c> statistic is recorded. A sum is always exact when present.</summary>
    internal bool HasSum => (_present & Present.Sum) != 0;

    /// <summary>
    /// The <c>sum</c> statistic, untyped. Interpret it against
    /// <see cref="FileStatistics.GetSumDType"/>, not the field DType. On a float column that
    /// contains an infinity this is <em>not</em> the IEEE sum: the aggregate is bound with
    /// NaN-skipping semantics and the result is still marked exact.
    /// </summary>
    internal ScalarValue Sum => _sum;

    /// <summary>Reads the <c>is_sorted</c> statistic.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns>Whether the statistic was recorded.</returns>
    public bool TryGetIsSorted(out bool value)
    {
        value = _isSorted;
        return (_present & Present.IsSorted) != 0;
    }

    /// <summary>Reads the <c>is_strict_sorted</c> statistic.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns>Whether the statistic was recorded.</returns>
    internal bool TryGetIsStrictSorted(out bool value)
    {
        value = _isStrictSorted;
        return (_present & Present.IsStrictSorted) != 0;
    }

    /// <summary>Reads the <c>is_constant</c> statistic.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns>Whether the statistic was recorded.</returns>
    public bool TryGetIsConstant(out bool value)
    {
        value = _isConstant;
        return (_present & Present.IsConstant) != 0;
    }

    /// <summary>Reads the <c>null_count</c> statistic. Zero present is different from absent.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns>Whether the statistic was recorded.</returns>
    internal bool TryGetStoredNullCount(out ulong value)
    {
        value = _nullCount;
        return (_present & Present.NullCount) != 0;
    }

    /// <summary>Reads the <c>uncompressed_size_in_bytes</c> statistic.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns>Whether the statistic was recorded.</returns>
    internal bool TryGetUncompressedSizeInBytes(out ulong value)
    {
        value = _uncompressedSizeInBytes;
        return (_present & Present.UncompressedSizeInBytes) != 0;
    }

    /// <summary>Reads the <c>nan_count</c> statistic.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns>Whether the statistic was recorded.</returns>
    internal bool TryGetNanCount(out ulong value)
    {
        value = _nanCount;
        return (_present & Present.NanCount) != 0;
    }

    /// <summary>The column's type, which reads <see cref="Min"/> and <see cref="Max"/>.</summary>
    private VortexType? FieldType { get; init; }

    /// <summary>The widened type <see cref="Sum"/> is stored at.</summary>
    private VortexType? SumType { get; init; }

    /// <summary>These statistics, able to read their values back as the column's .NET type.</summary>
    internal FieldStatistics Typed(VortexType field, VortexType sum) => this with { FieldType = field, SumType = sum };

    /// <summary>The column's exact minimum among its non-null values, as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A .NET type the column maps to.</typeparam>
    /// <param name="value">The minimum.</param>
    /// <returns>Whether the file records it exactly; a bound is not reported.</returns>
    public bool TryGetMin<T>(out T value) => TryRead(HasMin && _minPrecision == StatPrecision.Exact, _min, FieldType, out value);

    /// <summary>The column's exact maximum among its non-null values, as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A .NET type the column maps to.</typeparam>
    /// <param name="value">The maximum.</param>
    /// <returns>Whether the file records it exactly; a bound is not reported.</returns>
    public bool TryGetMax<T>(out T value) => TryRead(HasMax && _maxPrecision == StatPrecision.Exact, _max, FieldType, out value);

    /// <summary>The sum of the column's non-null values, at the widened type the file stores it: <c>long</c> for a signed integer, <c>ulong</c> for an unsigned one, <c>double</c> for a float, <c>decimal</c> for a decimal.</summary>
    /// <typeparam name="T">The sum's .NET type.</typeparam>
    /// <param name="value">The sum.</param>
    /// <returns>Whether the file records it; an overflowed sum is not.</returns>
    public bool TryGetSum<T>(out T value) => TryRead(HasSum, _sum, SumType, out value);

    /// <summary>The number of null values of the column.</summary>
    /// <param name="count">The count.</param>
    /// <returns>Whether the file records it.</returns>
    public bool TryGetNullCount(out long count)
    {
        bool present = TryGetStoredNullCount(out ulong nulls) && nulls <= long.MaxValue;
        count = present ? (long)nulls : 0;
        return present;
    }

    private static bool TryRead<T>(bool present, ScalarValue scalar, VortexType? type, out T value)
    {
        value = default!;
        if (!present || type is null || !Compute.FileStatisticsPruner.TryLiteral(scalar, out Expressions.FilterLiteral literal))
        {
            return false;
        }

        if (!ClrFit.Fits(ClrShape.For<T>.Value, type.NonNullable, null, out _) && !ClrFit.Fits(ClrShape.For<T>.Value, type, null, out _))
        {
            throw new VortexSchemaException($"The statistic is of a column of {type}, which {ClrFit.Name(typeof(T))} does not map to.");
        }

        value = LiteralValues.ToValue<T>(literal, type)!;
        return true;
    }
}
