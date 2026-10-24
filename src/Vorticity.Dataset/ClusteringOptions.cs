using System;
using System.Globalization;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// How the jobs of a dataset group its rows into objects: by a value of its clustering key's first column, by the bytes an
/// object aims at, or both (<see cref="DatasetOptions.Clustering"/>). The application chooses both; the dataset knows
/// nothing of what the values mean.
/// </summary>
/// <remarks>
/// Fixed when the dataset is created and carried by every commit, so that every writer cuts its objects alike; a handle
/// opened on an existing dataset follows the dataset's, whatever its own options say.
/// </remarks>
public sealed record ClusteringOptions
{
    private readonly long _objectBytes;

    /// <summary>
    /// The value a job cuts its objects by: above level 0, no object holds rows of two values, so that the rows of a value
    /// lie in objects of their own. Null, the default, cuts by size alone. A value needs a clustering key.
    /// </summary>
    public ClusteringValue? Value { get; init; }

    /// <summary>
    /// The bytes an object a job writes aims at, past which the job starts another, never splitting a key: the bound of
    /// every level's target, for every writer of the dataset (<see cref="CompactionOptions.MaxObjectBytes"/>, which it
    /// replaces). An object goes past it by the rows of one run at most. 0, the default, leaves the compaction's own bound.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The bytes are negative.</exception>
    public long ObjectBytes
    {
        get => _objectBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _objectBytes = value;
        }
    }
}

/// <summary>
/// The value a dataset's objects are cut by (<see cref="ClusteringOptions.Value"/>): the first column of its clustering
/// key, or a non-decreasing function of it, so that the rows of one value lie together in the key's order and the values
/// of a level's objects follow its keys. The column may be of any type a key takes: an instant, a date, a number, a text.
/// </summary>
/// <remarks>
/// To cut by another column, an application leads its key with it: a key <c>(Tenant, At)</c> cut by
/// <see cref="Column"/> keeps each tenant's rows in objects of their own.
/// </remarks>
public sealed record ClusteringValue
{
    private ClusteringValue(ClusteringFunction function, CalendarUnit unit = default, long width = 0, double floatWidth = 0)
    {
        Function = function;
        Unit = unit;
        Width = width;
        FloatWidth = floatWidth;
    }

    /// <summary>The column's own values, whatever its type: an object holds rows of one of them.</summary>
    public static ClusteringValue Column { get; } = new ClusteringValue(ClusteringFunction.Column);

    /// <summary>An instant's or a date's start of a calendar unit, as stored: <c>Truncate(CalendarUnit.Day)</c> cuts at every midnight.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The unit is undefined.</exception>
    public static ClusteringValue Truncate(CalendarUnit unit) =>
        Enum.IsDefined(unit)
            ? new ClusteringValue(ClusteringFunction.Truncate, unit)
            : throw new ArgumentOutOfRangeException(nameof(unit), unit, "Not a calendar unit.");

    /// <summary>An instant's bucket of <paramref name="width"/> holding it, counted from the epoch.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive.</exception>
    public static ClusteringValue Bucket(TimeSpan width) =>
        width > TimeSpan.Zero
            ? new ClusteringValue(ClusteringFunction.InstantBucket, width: width.Ticks)
            : throw new ArgumentOutOfRangeException(nameof(width), width, "A bucket's width is positive.");

    /// <summary>A number's bucket of <paramref name="width"/> holding it: <c>⌊v / w⌋ × w</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive.</exception>
    public static ClusteringValue Bucket(long width) =>
        width > 0
            ? new ClusteringValue(ClusteringFunction.IntegerBucket, width: width)
            : throw new ArgumentOutOfRangeException(nameof(width), width, "A bucket's width is positive.");

    /// <summary>A float's bucket of <paramref name="width"/> holding it: <c>⌊v / w⌋ × w</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive and finite.</exception>
    public static ClusteringValue Bucket(double width) =>
        width > 0 && double.IsFinite(width)
            ? new ClusteringValue(ClusteringFunction.FloatBucket, floatWidth: width)
            : throw new ArgumentOutOfRangeException(nameof(width), width, "A bucket's width is positive and finite.");

    /// <summary>Which function of the column the value is.</summary>
    internal ClusteringFunction Function { get; }

    /// <summary>The calendar unit of a truncation.</summary>
    internal CalendarUnit Unit { get; }

    /// <summary>The width of an instant's bucket, in ticks, or of an integer's.</summary>
    internal long Width { get; }

    /// <summary>The width of a float's bucket.</summary>
    internal double FloatWidth { get; }

    /// <summary>The value as written: <c>Truncate(Day)</c>.</summary>
    public override string ToString() => Function switch
    {
        ClusteringFunction.Column => "Column",
        ClusteringFunction.Truncate => $"Truncate({Unit})",
        ClusteringFunction.InstantBucket => $"Bucket({TimeSpan.FromTicks(Width)})",
        ClusteringFunction.IntegerBucket => string.Create(CultureInfo.InvariantCulture, $"Bucket({Width})"),
        _ => string.Create(CultureInfo.InvariantCulture, $"Bucket({FloatWidth:R})"),
    };

    /// <summary>A value as a header records it.</summary>
    /// <exception cref="CommitFormatException">The fields name no value this library knows.</exception>
    internal static ClusteringValue Of(ulong function, ulong unit, long width, double floatWidth)
    {
        try
        {
            return function switch
            {
                (ulong)ClusteringFunction.Column => Column,
                (ulong)ClusteringFunction.Truncate when unit <= byte.MaxValue => Truncate((CalendarUnit)unit),
                (ulong)ClusteringFunction.InstantBucket => Bucket(TimeSpan.FromTicks(width)),
                (ulong)ClusteringFunction.IntegerBucket => Bucket(width),
                (ulong)ClusteringFunction.FloatBucket => Bucket(floatWidth),
                _ => throw new CommitFormatException($"A commit header names clustering function {function}, which this library does not know."),
            };
        }
        catch (ArgumentOutOfRangeException cause)
        {
            throw new CommitFormatException($"A commit header names a clustering value it cannot hold: {cause.Message}", cause);
        }
    }

    /// <summary>
    /// The function of a key's first column of <paramref name="column"/>, named <paramref name="path"/>, this value is:
    /// the one a question applies (<c>r.At.Truncate(CalendarUnit.Day)</c>), or null for the column itself.
    /// </summary>
    /// <exception cref="ArgumentException">The function does not apply to the column's type.</exception>
    internal ValueFunction? Resolve(DType column, string path)
    {
        VortexType type = VortexTypes.FromDType(column);
        try
        {
            return Function switch
            {
                ClusteringFunction.Column => null,
                ClusteringFunction.Truncate => SymFunctions.Truncation(type, path, Unit, zone: null, offsets: false),
                ClusteringFunction.InstantBucket => SymFunctions.Buckets(type, path, TimeSpan.FromTicks(Width)),
                ClusteringFunction.IntegerBucket => SymFunctions.Buckets(type, path, Width),
                _ => SymFunctions.Buckets(type, path, FloatWidth),
            };
        }
        catch (NotSupportedException cause)
        {
            throw new ArgumentException($"The clustering value {this} does not apply to '{path}': {cause.Message}", nameof(column), cause);
        }
    }
}

/// <summary>The functions a clustering value is, as a header records them.</summary>
internal enum ClusteringFunction : byte
{
    /// <summary>The column itself.</summary>
    Column = 0,

    /// <summary>The start of a calendar unit.</summary>
    Truncate = 1,

    /// <summary>An instant's bucket of a time span.</summary>
    InstantBucket = 2,

    /// <summary>A number's bucket of an integer width.</summary>
    IntegerBucket = 3,

    /// <summary>A number's bucket of a float width.</summary>
    FloatBucket = 4,
}

/// <summary>
/// A dataset's clustering value resolved against its key's first column: what tells two keys of one value apart as a job
/// writes its rows, and the one value an object's summaries prove it holds.
/// </summary>
internal sealed class ValueCut
{
    private readonly ValueFunction? _function;
    private readonly DType _column;
    private readonly string _path;
    private readonly bool _mayHoldNull;

    private ValueCut(ClusteringValue value, ClusteringKey key)
    {
        Value = value;
        _column = key.LeadingType;
        _path = key.InstantPath;
        _mayHoldNull = key.MayHoldNull;
        _function = value.Resolve(_column, _path);
    }

    /// <summary>The cut a dataset's clustering declares over its key; null when it declares no value.</summary>
    /// <exception cref="ArgumentException">The value does not apply to the key's first column.</exception>
    internal static ValueCut? Of(ClusteringOptions? clustering, ClusteringKey? key)
    {
        if (clustering?.Value is not { } value)
        {
            return null;
        }

        return key is null
            ? throw new ArgumentException("A clustering value is a function of the clustering key's first column: declare a clustering key.", nameof(clustering))
            : new ValueCut(value, key);
    }

    /// <summary>The value declared.</summary>
    internal ClusteringValue Value { get; }

    /// <summary>Whether two row-encoded keys of the clustering key, in ascending order, hold one clustering value.</summary>
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

    /// <summary>The clustering value of a row-encoded key: its first value through the function, a null as a null.</summary>
    private FilterLiteral Image(ReadOnlySpan<byte> key)
    {
        FilterLiteral value = RowEncoder.ReadFirstValue(key, _column);
        return value.Kind != FilterLiteralKind.Null && _function!.TryMap(value, out FilterLiteral mapped) ? mapped : value;
    }

    /// <summary>A bound's clustering value; null when the function does not read it.</summary>
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
