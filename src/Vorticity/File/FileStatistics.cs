// File-level statistics: one entry per ROOT field (docs/02-format.md §3), parsed once at open.
//
// The shape rule and the per-statistic DType rule both come from
// vortex-file-0.86.1/src/footer/file_statistics.rs and
// vortex-array-0.86.1/src/stats/flatbuffers.rs (`StatsSet::from_flatbuffer`):
//
//   * a struct root needs exactly one entry per top-level field, zipped positionally and SHALLOW -
//     a nested struct field gets one entry decoded against the nested struct DType and its own
//     fields get none; any other root needs exactly one entry;
//   * each statistic has its OWN DType, not the field's: min/max are the field DType, sum is the
//     widened aggregate DType, and null_count / nan_count / uncompressed_size_in_bytes are u64
//     read straight off the FlatBuffer. Upstream has a regression test for this because it was
//     once wrong.
//
// Everything here is class II/III (docs/08-semantics.md §5): surfaced faithfully, never trusted
// for a correctness decision. Two traps the caller must respect and this type deliberately does
// not paper over: an `Inexact` precision is a BOUND, not a value, and the float `sum` on a column
// containing an infinity is not the IEEE sum (corpus manifest caveat 0).
using System;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;

namespace Vorticity.File;

/// <summary>File-level statistics, one entry per root field (docs/02-format.md §3).</summary>
public sealed class FileStatistics
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
    /// overflowing sum is recorded as null
    /// (vortex-array-0.86.1/src/aggregate_fn/fns/sum/mod.rs, <c>return_dtype</c>).
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
/// a present one that is <see cref="StatPrecision.Inexact"/> is a bound rather than a value
/// (docs/08-semantics.md §1). The <c>min == max ⇒ constant</c> shortcut is forbidden unless both
/// are <see cref="StatPrecision.Exact"/>.
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
    public bool HasMin => (_present & Present.Min) != 0;

    /// <summary>
    /// The <c>min</c> statistic, untyped. Interpret it against
    /// <see cref="FileStatistics.GetFieldDType"/>; <see cref="ScalarValue.IsAbsent"/> when
    /// <see cref="HasMin"/> is false.
    /// </summary>
    public ScalarValue Min => _min;

    /// <summary>Whether <see cref="Min"/> is the true minimum or only a lower bound.</summary>
    public StatPrecision MinPrecision => _minPrecision;

    /// <summary>True when a <c>max</c> statistic is recorded.</summary>
    public bool HasMax => (_present & Present.Max) != 0;

    /// <summary>The <c>max</c> statistic, untyped. See <see cref="Min"/>.</summary>
    public ScalarValue Max => _max;

    /// <summary>Whether <see cref="Max"/> is the true maximum or only an upper bound.</summary>
    public StatPrecision MaxPrecision => _maxPrecision;

    /// <summary>True when a <c>sum</c> statistic is recorded. A sum is always exact when present.</summary>
    public bool HasSum => (_present & Present.Sum) != 0;

    /// <summary>
    /// The <c>sum</c> statistic, untyped. Interpret it against
    /// <see cref="FileStatistics.GetSumDType"/>, not the field DType. On a float column that
    /// contains an infinity this is <em>not</em> the IEEE sum: Vortex binds the aggregate with
    /// NaN-skipping semantics and still marks the result exact (corpus manifest caveat 0).
    /// </summary>
    public ScalarValue Sum => _sum;

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
    public bool TryGetIsStrictSorted(out bool value)
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
    public bool TryGetNullCount(out ulong value)
    {
        value = _nullCount;
        return (_present & Present.NullCount) != 0;
    }

    /// <summary>Reads the <c>uncompressed_size_in_bytes</c> statistic.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns>Whether the statistic was recorded.</returns>
    public bool TryGetUncompressedSizeInBytes(out ulong value)
    {
        value = _uncompressedSizeInBytes;
        return (_present & Present.UncompressedSizeInBytes) != 0;
    }

    /// <summary>Reads the <c>nan_count</c> statistic.</summary>
    /// <param name="value">The value when present.</param>
    /// <returns>Whether the statistic was recorded.</returns>
    public bool TryGetNanCount(out ulong value)
    {
        value = _nanCount;
        return (_present & Present.NanCount) != 0;
    }
}
