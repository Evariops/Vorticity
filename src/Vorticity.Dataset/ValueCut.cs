using System;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// A dataset's partition resolved against its key's first column, when the partition is that column or a function of it:
/// what tells two keys of one partition apart as a job writes its rows, and the one partition an object's summaries prove
/// it holds.
/// </summary>
internal sealed class ValueCut
{
    private readonly ValueFunction? _function;
    private readonly DType _column;
    private readonly string _path;
    private readonly bool _mayHoldNull;

    private ValueCut(LayoutColumn partition, ClusteringKey key)
    {
        Partition = partition;
        _column = key.LeadingType;
        _path = key.InstantPath;
        _mayHoldNull = key.MayHoldNull;
        _function = partition.Function;
    }

    /// <summary>
    /// The cut of <paramref name="plan"/>'s partition over the key's first column; null for no partition, or one of
    /// another source.
    /// </summary>
    internal static ValueCut? Of(LayoutPlan? plan, ClusteringKey? key) =>
        plan?.Partition is { Read: null } partition && key is not null
            && string.Equals(partition.Value.SourcePath, key.InstantPath, StringComparison.Ordinal)
            ? new ValueCut(partition, key)
            : null;

    /// <summary>The partition.</summary>
    internal LayoutColumn Partition { get; }

    /// <summary>
    /// The integer storage of the key's first column, whose values a part of a range starts at, one partition
    /// after the other: an integer's, an instant's, a date's. Null for a text, a float or a binary, whose next value no
    /// function tells.
    /// </summary>
    internal PType? Integers
    {
        get
        {
            DType storage = _column;
            while (storage.Kind == DTypeKind.Extension)
            {
                storage = storage.StorageType;
            }

            return storage.Kind == DTypeKind.Primitive && !storage.PType.IsFloat() ? storage.PType : null;
        }
    }

    /// <summary>The first stored value whose partition is <paramref name="value"/>'s: where its part starts.</summary>
    internal Int128 StartOf(Int128 value) =>
        _function is not null && _function.TryLeast(ImageOf(value), out Int128 start) ? start : value;

    /// <summary>
    /// The first stored value of the partition after <paramref name="value"/>'s: where the next part starts; false
    /// when no stored value has one.
    /// </summary>
    internal bool TryNextStart(Int128 value, out Int128 next)
    {
        if (_function is not null)
        {
            return _function.TryLeast(ImageOf(value) + 1, out next);
        }

        next = value + 1;
        return value < ValueFunction.Integers(Integers ?? PType.I64).Max;
    }

    /// <summary>The partition of a stored integer, in the storage's terms.</summary>
    private Int128 ImageOf(Int128 value) =>
        _function!.TryMap(_function.Literal(value), out FilterLiteral image) && ValueFunction.TryInteger(image, out Int128 at) ? at : value;

    /// <summary>Whether two row-encoded keys of the clustering key, in ascending order, hold one partition.</summary>
    internal bool Same(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (_function is null)
        {
            // A value's bytes end where they end whatever follows them, so the same bytes from the start are the same value.
            int length = RowEncoder.FirstValueLength(left, _column);
            return right.Length >= length && left[..length].SequenceEqual(right[..length]);
        }

        return Equal(Image(left), Image(right));
    }

    /// <summary>
    /// The end of the rows of one value from row <paramref name="from"/> of the merge's run: the first row before
    /// <paramref name="to"/> whose key holds another value, or <paramref name="to"/>. The keys of a run are in order, and
    /// so their values, so a bisection finds it.
    /// </summary>
    internal int EndOfValue(KeyOrderedMerge merge, int from, int to)
    {
        if (Same(merge.KeyAt(from), merge.KeyAt(to - 1)))
        {
            return to;
        }

        // Row `low` holds the value of row `from`, row `high` another.
        int low = from;
        int high = to - 1;
        while (high - low > 1)
        {
            int middle = low + ((high - low) >> 1);
            if (Same(merge.KeyAt(from), merge.KeyAt(middle)))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return high;
    }

    /// <summary>
    /// The one value an object holds, when its summaries prove it: the images of its key's first column's bounds the same,
    /// and no null. Bounds that are not exact lie outwards, which keeps the proof: a non-decreasing function sends every
    /// value between them between their images. Null when they prove none.
    /// </summary>
    internal FilterLiteral? Single(ObjectSummaries summaries)
    {
        if (!summaries.TryGet(_path, out ColumnSummary column) || !column.HasMin || !column.HasMax
            || (_mayHoldNull && !(column.HasNullCount && column.NullCount == 0)))
        {
            return null;
        }

        return Map(column.Min) is { } low && Map(column.Max) is { } high && Equal(low, high) ? low : null;
    }

    /// <summary>Whether an object's summaries prove it holds <paramref name="value"/> alone.</summary>
    internal bool HoldsOnly(ObjectSummaries summaries, FilterLiteral value) => Single(summaries) is { } held && Equal(held, value);

    /// <summary>
    /// Whether the object <paramref name="input"/> names holds one value: its first key, which its tree key leads with, and
    /// its last, of one, for a key of any type; its summaries' bounds, when its entry states no last key.
    /// </summary>
    internal bool HoldsOne(CompactionInput input) =>
        input.Entry.LastKey.IsEmpty ? Single(input.Entry.Summaries) is not null : Same(FirstKey(input), input.Entry.LastKey.Span);

    /// <summary>
    /// Whether two objects that each hold one value hold the same: by their first keys when both entries state their
    /// last, by their summaries otherwise.
    /// </summary>
    internal bool SameValue(CompactionInput left, CompactionInput right) =>
        !left.Entry.LastKey.IsEmpty && !right.Entry.LastKey.IsEmpty
            ? Same(FirstKey(left), FirstKey(right))
            : Single(left.Entry.Summaries) is { } one && Single(right.Entry.Summaries) is { } other && Equal(one, other);

    /// <summary>The smallest key of the object an input names: its tree key without the uid that ends it.</summary>
    private static ReadOnlySpan<byte> FirstKey(CompactionInput input) => input.Key.Span[..^VortexDataset.UidBytes];

    /// <summary>The partition of a row-encoded key: its first value through the function, a null as a null.</summary>
    private FilterLiteral Image(ReadOnlySpan<byte> key)
    {
        FilterLiteral value = RowEncoder.ReadFirstValue(key, _column);
        return value.Kind != FilterLiteralKind.Null && _function!.TryMap(value, out FilterLiteral mapped) ? mapped : value;
    }

    /// <summary>A bound's partition; null when the function does not read it.</summary>
    private FilterLiteral? Map(FilterLiteral bound)
    {
        if (_function is null)
        {
            return bound;
        }

        return _function.TryMap(bound, out FilterLiteral mapped) ? mapped : null;
    }

    private static bool Equal(FilterLiteral left, FilterLiteral right) =>
        left.Kind == FilterLiteralKind.Null || right.Kind == FilterLiteralKind.Null
            ? left.Kind == right.Kind
            : KeyCursor.Compare(left, right) == 0;
}
