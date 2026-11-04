using System;
using System.Collections.Generic;
using System.Globalization;
using Vorticity.Types.Variants;

namespace Vorticity.Dataset;

/// <summary>
/// How a dataset lays out its rows (<see cref="DatasetOptions.Layout"/>): the partition no object holds two values of, the
/// key that orders the rows of every object, the prefix of it each object indexes, the size its objects aim at, and how its
/// jobs organize them. The application chooses the values; the dataset knows nothing of what they mean.
/// </summary>
/// <remarks>
/// <para>
/// The partition, the key, the prefix and the organization are fixed when the dataset is created and carried by every
/// commit; a handle opened on an existing dataset follows the dataset's, whatever its own options say. The size, the
/// fan-out, the calm and the share are policy, which a later commit may change.
/// </para>
/// <para>
/// Under a key, every object a writer adds is sorted by it, nulls after every value, rows of equal keys in the order they
/// came; each object indexes the first key of each granule of its rows, the prefix of it <see cref="IndexedPrefix"/> keeps.
/// </para>
/// </remarks>
public sealed record DatasetLayout
{
    /// <summary>The most components a key holds.</summary>
    public const int MostComponents = 16;

    private readonly long _objectBytes = 1L << 30;
    private readonly int _mergeFanout = 8;
    private readonly int _lateShare = 8;
    private readonly TimeSpan? _calmAfter = TimeSpan.FromHours(1);
    private readonly int? _indexedPrefix;

    /// <summary>
    /// The value no object holds two of, worked out on every row: what an expiration removes whole objects by, what
    /// a filter on its source skips whole parts of the dataset by, and what merges and consolidations stay within.
    /// Null, the default, for one partition.
    /// </summary>
    public LayoutValue? Partition { get; init; }

    /// <summary>
    /// The values that order the rows of every object, the first first, each ascending with nulls after every value:
    /// from 1 to <see cref="MostComponents"/>. Empty, the default, keeps the order the rows arrive in.
    /// </summary>
    public IReadOnlyList<LayoutValue> SortKey { get; init; } = [];

    /// <summary>
    /// How many of the key's first components each object indexes, a granule at a time: those that group rows, where
    /// the components after them vary within a granule and its zone maps bound them. Null, the default, for every
    /// component.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The prefix is under 1.</exception>
    public int? IndexedPrefix
    {
        get => _indexedPrefix;
        init => _indexedPrefix = value is null or >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "An indexed prefix holds one component or more.");
    }

    /// <summary>
    /// The bytes an object aims at once its partition is consolidated, past which a job starts another, never cutting a
    /// key: an object goes past it by the rows of one key at most. 1 GiB by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The bytes are not positive.</exception>
    public long ObjectBytes
    {
        get => _objectBytes;
        init => _objectBytes = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "An object aims at a positive number of bytes.");
    }

    /// <summary>
    /// How the dataset's jobs organize its objects; null, the default, for the organization the partition and the key
    /// call for: tiers, but levels for a key without a partition, whose one partition is never calm.
    /// </summary>
    public LayoutOrganization? Organization { get; init; }

    /// <summary>How many objects of one size class a tiered merge takes, and makes one of the class above: 8 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The fan-out is under 2.</exception>
    public int MergeFanout
    {
        get => _mergeFanout;
        init => _mergeFanout = value >= 2
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A merge takes two objects or more.");
    }

    /// <summary>
    /// How long a partition receives no row before its objects are consolidated into objects disjoint by key: an hour by
    /// default. Null consolidates only on demand.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The delay is negative.</exception>
    public TimeSpan? CalmAfter
    {
        get => _calmAfter;
        init => _calmAfter = value is not { } delay || delay >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A partition is calm after a delay that is not negative.");
    }

    /// <summary>
    /// How many times the bytes of the consolidated objects a partition's new rows overlap may outweigh them before
    /// their consolidation waits for more: 8 by default, so that a late row costs at most nine writes there.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The share is under 1.</exception>
    public int LateShare
    {
        get => _lateShare;
        init => _lateShare = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A share is one or more.");
    }

    /// <summary>The prefix the objects index: <see cref="IndexedPrefix"/>, or every component.</summary>
    internal int Prefix => _indexedPrefix ?? SortKey.Count;

    /// <summary>
    /// The organization the dataset keeps: the one declared, or the one the partition and the key call for. Levels for
    /// a key without a partition; tiers otherwise, by concatenation without a key.
    /// </summary>
    internal LayoutOrganization Organized =>
        Organization ?? (Partition is null && SortKey.Count > 0 ? LayoutOrganization.Leveled : LayoutOrganization.Tiered);

    /// <summary>Whether two layouts order and partition the rows alike, the part of a layout no commit changes.</summary>
    internal bool SameShape(DatasetLayout other) =>
        Equals(Partition, other.Partition) && Prefix == other.Prefix && Organized == other.Organized && SameKey(other);

    private bool SameKey(DatasetLayout other)
    {
        if (SortKey.Count != other.SortKey.Count)
        {
            return false;
        }

        for (int i = 0; i < SortKey.Count; i++)
        {
            if (!SortKey[i].Equals(other.SortKey[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public bool Equals(DatasetLayout? other) =>
        other is not null && SameShape(other) && ObjectBytes == other.ObjectBytes && MergeFanout == other.MergeFanout
        && CalmAfter == other.CalmAfter && LateShare == other.LateShare;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new HashCode();
        hash.Add(Partition);
        foreach (LayoutValue component in SortKey)
        {
            hash.Add(component);
        }

        hash.Add(Prefix);
        hash.Add(ObjectBytes);
        hash.Add(Organized);
        return hash.ToHashCode();
    }
}

/// <summary>How a dataset's jobs organize its objects (<see cref="DatasetLayout.Organization"/>).</summary>
public enum LayoutOrganization
{
    /// <summary>
    /// Within each partition, objects of one size class merged a fan-out at a time into one of the class above, then, the
    /// partition calm, consolidated into objects disjoint by key; without a key, concatenated.
    /// </summary>
    Tiered = 1,

    /// <summary>Levels disjoint on the partition and the key above level 0, each a fan-out times the size of the one below.</summary>
    Leveled = 2,
}

/// <summary>
/// A value a dataset's layout partitions or orders its rows by (<see cref="DatasetLayout"/>): a source, a column or a key
/// of a variant column read in one class, and a non-decreasing function of it, so that a range of the source is a range
/// of the value and a value names a range of the source.
/// </summary>
/// <remarks>
/// A null source makes a null value, which comes after every other value of its component. A key of a variant column is
/// null where a row does not hold it, or holds a value of another class.
/// </remarks>
public sealed record LayoutValue
{
    private LayoutValue(string column, string? key, VariantClass @class, LayoutFunction function, CalendarUnit unit, long width, double floatWidth)
    {
        SourcePath = column;
        Key = key;
        Class = @class;
        Function = function;
        Unit = unit;
        Width = width;
        FloatWidth = floatWidth;
    }

    /// <summary>The source's column: a top-level column, or a field of a struct by its path of names, dots between them.</summary>
    internal string SourcePath { get; }

    /// <summary>The key of a variant column the value reads; null for a column's own values.</summary>
    internal string? Key { get; }

    /// <summary>The class a variant key is read in.</summary>
    internal VariantClass Class { get; }

    /// <summary>Which function of the source the value is.</summary>
    internal LayoutFunction Function { get; }

    /// <summary>The calendar unit of a truncation.</summary>
    internal CalendarUnit Unit { get; }

    /// <summary>The width of an instant's bucket, in ticks, or of an integer's.</summary>
    internal long Width { get; }

    /// <summary>The width of a float's bucket.</summary>
    internal double FloatWidth { get; }

    /// <summary>A column's own values: a top-level column, or a field of a struct by its path, <c>Request.Host</c>.</summary>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    public static LayoutValue Column(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new LayoutValue(path, null, default, LayoutFunction.Identity, default, 0, 0);
    }

    /// <summary>
    /// A key of the variant column <paramref name="column"/> read in <paramref name="class"/>: null where a row does not
    /// hold the key, or holds a value of another class.
    /// </summary>
    /// <exception cref="ArgumentException">The column or the key is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The class is undefined.</exception>
    public static LayoutValue Path(string column, string key, VariantClass @class)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Enum.IsDefined(@class)
            ? new LayoutValue(column, key, @class, LayoutFunction.Identity, default, 0, 0)
            : throw new ArgumentOutOfRangeException(nameof(@class), @class, "Not a class of variant values.");
    }

    /// <summary>An instant's or a date's start of a calendar unit, as stored: <c>Truncate(CalendarUnit.Day)</c> starts at every midnight.</summary>
    /// <exception cref="InvalidOperationException">The value is already a function of its source.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The unit is undefined.</exception>
    public LayoutValue Truncate(CalendarUnit unit) =>
        Enum.IsDefined(unit)
            ? Of(LayoutFunction.Truncate, unit, 0, 0)
            : throw new ArgumentOutOfRangeException(nameof(unit), unit, "Not a calendar unit.");

    /// <summary>An instant's bucket of <paramref name="width"/> holding it, counted from the epoch.</summary>
    /// <exception cref="InvalidOperationException">The value is already a function of its source.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive.</exception>
    public LayoutValue Bucket(TimeSpan width) =>
        width > TimeSpan.Zero
            ? Of(LayoutFunction.InstantBucket, default, width.Ticks, 0)
            : throw new ArgumentOutOfRangeException(nameof(width), width, "A bucket's width is positive.");

    /// <summary>A number's bucket of <paramref name="width"/> holding it: <c>⌊v / w⌋ × w</c>.</summary>
    /// <exception cref="InvalidOperationException">The value is already a function of its source.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive.</exception>
    public LayoutValue Bucket(long width) =>
        width > 0
            ? Of(LayoutFunction.IntegerBucket, default, width, 0)
            : throw new ArgumentOutOfRangeException(nameof(width), width, "A bucket's width is positive.");

    /// <summary>A float's bucket of <paramref name="width"/> holding it: <c>⌊v / w⌋ × w</c>.</summary>
    /// <exception cref="InvalidOperationException">The value is already a function of its source.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive and finite.</exception>
    public LayoutValue Bucket(double width) =>
        width > 0 && double.IsFinite(width)
            ? Of(LayoutFunction.FloatBucket, default, 0, width)
            : throw new ArgumentOutOfRangeException(nameof(width), width, "A bucket's width is positive and finite.");

    private LayoutValue Of(LayoutFunction function, CalendarUnit unit, long width, double floatWidth) =>
        Function == LayoutFunction.Identity
            ? new LayoutValue(SourcePath, Key, Class, function, unit, width, floatWidth)
            : throw new InvalidOperationException($"{this} is a function of its source already; a value applies one.");

    /// <summary>A value as a header records it.</summary>
    /// <exception cref="CommitFormatException">The fields name no value this library knows.</exception>
    internal static LayoutValue Of(string column, string? key, ulong @class, ulong function, ulong unit, long width, double floatWidth)
    {
        try
        {
            LayoutValue source = key is null
                ? Column(column)
                : @class <= byte.MaxValue ? Path(column, key, (VariantClass)@class) : throw new ArgumentOutOfRangeException(nameof(@class));
            return function switch
            {
                (ulong)LayoutFunction.Identity => source,
                (ulong)LayoutFunction.Truncate when unit <= byte.MaxValue => source.Truncate((CalendarUnit)unit),
                (ulong)LayoutFunction.InstantBucket => source.Bucket(TimeSpan.FromTicks(width)),
                (ulong)LayoutFunction.IntegerBucket => source.Bucket(width),
                (ulong)LayoutFunction.FloatBucket => source.Bucket(floatWidth),
                _ => throw new CommitFormatException($"A commit header names layout function {function}, which this library does not know."),
            };
        }
        catch (ArgumentException cause)
        {
            throw new CommitFormatException($"A commit header names a layout value it cannot hold: {cause.Message}", cause);
        }
    }

    /// <summary>The value as written: <c>At.Truncate(Day)</c>, <c>Attributes['http.route'].AsString()</c>.</summary>
    public override string ToString()
    {
        string source = Key is null ? SourcePath : $"{SourcePath}[{Compute.VariantStep.Quoted(Key)}].{ReadOf(Class)}()";
        return Function switch
        {
            LayoutFunction.Identity => source,
            LayoutFunction.Truncate => $"{source}.Truncate({Unit})",
            LayoutFunction.InstantBucket => $"{source}.Bucket({TimeSpan.FromTicks(Width)})",
            LayoutFunction.IntegerBucket => string.Create(CultureInfo.InvariantCulture, $"{source}.Bucket({Width})"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{source}.Bucket({FloatWidth:R})"),
        };
    }

    /// <summary>The read of a variant key in <paramref name="class"/>, as a filter writes it.</summary>
    internal static Compute.VariantRead ReadOf(VariantClass @class) => @class switch
    {
        VariantClass.Integer => Compute.VariantRead.AsInt64,
        VariantClass.Double => Compute.VariantRead.AsDouble,
        VariantClass.Boolean => Compute.VariantRead.AsBool,
        _ => Compute.VariantRead.AsString,
    };
}

/// <summary>The functions a layout value is of its source, as a header records them.</summary>
internal enum LayoutFunction : byte
{
    /// <summary>The source itself.</summary>
    Identity = 0,

    /// <summary>The start of a calendar unit.</summary>
    Truncate = 1,

    /// <summary>An instant's bucket of a time span.</summary>
    InstantBucket = 2,

    /// <summary>A number's bucket of an integer width.</summary>
    IntegerBucket = 3,

    /// <summary>A number's bucket of a float width.</summary>
    FloatBucket = 4,

    // 5 is reserved for the first bytes of a text or a binary, Prefix(n), which no application has asked for yet.
}
