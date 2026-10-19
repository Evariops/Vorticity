using System;
using System.Collections.Generic;
using System.Text;

namespace Vorticity.Types.Variants;

/// <summary>
/// The paths a variant column's rows hold, counted: of every value, its class, and of an object's keys
/// the same, key by key and as deep as objects nest. What a shredding is chosen from.
/// </summary>
/// <remarks>
/// A list counts by the class all its elements share, or as of no class when they do not share one,
/// since only a list whose elements all fit is shredded. Key names are read from a row's metadata once
/// for every run of rows whose metadata bytes are the same. Each path also counts its values by the
/// class a question reads them as (<see cref="PathClass"/>) and bounds them in each, what the path
/// directory holds: text bounds cut to <paramref name="stringBoundBytes"/>, as a text column's zones
/// cut theirs, none at 0.
/// </remarks>
internal sealed class PathCensus(int stringBoundBytes = 0)
{
    private byte[] _metadata = [];
    private int _metadataLength = -1;
    private string?[] _names = [];

    /// <summary>The bytes a text bound keeps; 0 for none.</summary>
    internal int StringBoundBytes { get; } = stringBoundBytes;

    /// <summary>The counts of the column's values themselves.</summary>
    internal PathCount Root { get; } = new PathCount();

    /// <summary>The rows counted, nulls included.</summary>
    internal long Rows { get; private set; }

    /// <summary>The bytes of the rows' values, as the residual would hold them unshredded.</summary>
    internal long ValueBytes { get; private set; }

    /// <summary>Counts a null row.</summary>
    internal void AddNull() => Rows++;

    /// <summary>Counts every row of the variant column <paramref name="field"/> of <paramref name="batch"/>'s root struct.</summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal void Add(RecordBatch batch, int field)
    {
        Arrays.CanonicalArena arena = batch.Arena;
        int column = arena.GetNode(batch.RootIndex).GetFieldIndex(field);
        Arrays.CanonicalNode variant = arena.GetNode(column);
        int metadata = EncodedForms.Canonical(arena, variant.GetFieldIndex(0));
        int value = EncodedForms.Canonical(arena, variant.GetFieldIndex(1));
        Arrays.CanonicalNode metadataNode = arena.GetNode(metadata);
        Arrays.CanonicalNode valueNode = arena.GetNode(value);
        for (int row = 0; row < batch.RowCount; row++)
        {
            if (!Arrays.ArenaWords.IsValid(arena, column, row))
            {
                AddNull();
                continue;
            }

            Add(
                Compute.LiteralReader.ViewAt(metadataNode, row),
                Arrays.ArenaWords.IsValid(arena, value, row) ? Compute.LiteralReader.ViewAt(valueNode, row) : VariantEncoding.NullValue);
        }
    }

    /// <summary>Counts a row of <paramref name="value"/>, its keys named by <paramref name="metadata"/>.</summary>
    /// <exception cref="VortexFormatException">The value or the metadata does not decode.</exception>
    internal void Add(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value)
    {
        Rows++;
        ValueBytes += value.Length;
        if (metadata.Length != _metadataLength || !metadata.SequenceEqual(_metadata.AsSpan(0, _metadataLength)))
        {
            if (_metadata.Length < metadata.Length)
            {
                _metadata = new byte[Math.Max(metadata.Length, 2 * _metadata.Length)];
            }

            metadata.CopyTo(_metadata);
            _metadataLength = metadata.Length;
            Array.Clear(_names);
        }

        VariantDictionary dictionary = VariantDictionary.Read(metadata);
        Root.Add(this, in dictionary, value, depth: 1);
    }

    /// <summary>Key <paramref name="id"/> of the current metadata, decoded once per run of rows that share it.</summary>
    private string Name(in VariantDictionary dictionary, int id)
    {
        if (_names.Length < dictionary.Count)
        {
            _names = new string?[Math.Max(dictionary.Count, 2 * _names.Length)];
        }

        return _names[id] ??= Encoding.UTF8.GetString(dictionary[id]);
    }

    /// <summary>The counts of the values at one path: by class, and of an object's keys.</summary>
    internal sealed class PathCount
    {
        /// <summary>Occurrences by class: the four shreddable classes, an object, a list of each class, and anything else.</summary>
        private readonly long[] _classes = new long[Slots];

        private Dictionary<string, PathCount>? _keys;

        /// <summary>Occurrences by the class a question reads them as.</summary>
        private readonly long[] _read = new long[PathClasses];

        private long _minInteger = long.MaxValue;
        private long _maxInteger = long.MinValue;
        private double _minDouble = double.PositiveInfinity;
        private double _maxDouble = double.NegativeInfinity;
        private bool _nans;
        private long _minTime = long.MaxValue;
        private long _maxTime = long.MinValue;
        private bool _false;
        private bool _true;

        // The text bounds, each cut to the census's bound, in buffers of that length; -1 before a text.
        private byte[]? _minText;
        private byte[]? _maxText;
        private int _minTextLength = -1;
        private int _maxTextLength = -1;
        private bool _maxTextCut;

        private const int ObjectSlot = 4;
        private const int ListSlot = 5;
        private const int OtherSlot = 9;
        private const int EmptyListSlot = 10;
        private const int Slots = 11;
        private const int PathClasses = (int)PathClass.Other + 1;

        /// <summary>The values counted at this path.</summary>
        internal long Values { get; private set; }

        /// <summary>The keys of the objects at this path, each with its counts; empty when no object was.</summary>
        internal IReadOnlyDictionary<string, PathCount> Keys => _keys ?? (IReadOnlyDictionary<string, PathCount>)EmptyKeys;

        private static readonly Dictionary<string, PathCount> EmptyKeys = [];

        /// <summary>The values of <paramref name="class"/>.</summary>
        internal long Of(VariantClass @class) => _classes[(int)@class];

        /// <summary>The lists whose elements are all of <paramref name="class"/>, the empty ones included.</summary>
        internal long ListsOf(VariantClass @class) => _classes[ListSlot + (int)@class] + _classes[EmptyListSlot];

        /// <summary>The objects.</summary>
        internal long Objects => _classes[ObjectSlot];

        /// <summary>The values a question reads as <paramref name="class"/>.</summary>
        internal long Read(PathClass @class) => _read[(int)@class];

        /// <summary>The least and greatest integer; meaningless without one.</summary>
        internal (long Min, long Max) Integers => (_minInteger, _maxInteger);

        /// <summary>The least and greatest number of a float or a double, NaNs aside, and whether one was NaN.</summary>
        internal (double Min, double Max, bool NaNs) Doubles => (_minDouble, _maxDouble, _nans);

        /// <summary>The least and greatest timestamp, in microseconds.</summary>
        internal (long Min, long Max) Times => (_minTime, _maxTime);

        /// <summary>Whether a false and whether a true was seen.</summary>
        internal (bool False, bool True) Booleans => (_false, _true);

        /// <summary>Whether a text was bounded: false without one, or when the census keeps no text bounds.</summary>
        internal bool HasTextBounds => _minTextLength >= 0;

        /// <summary>The least text cut to the bound, which no text is below.</summary>
        internal ReadOnlySpan<byte> MinText => _minText.AsSpan(0, Math.Max(_minTextLength, 0));

        /// <summary>The greatest text cut to the bound.</summary>
        internal ReadOnlySpan<byte> MaxText => _maxText.AsSpan(0, Math.Max(_maxTextLength, 0));

        /// <summary>Whether a text that cut to <see cref="MaxText"/> was longer, in which case it bounds nothing from above.</summary>
        internal bool MaxTextCut => _maxTextCut;

        internal void Add(PathCensus census, in VariantDictionary dictionary, ReadOnlySpan<byte> value, int depth)
        {
            Values++;
            switch (value[0] & 0x03)
            {
                case ParquetVariant.BasicObject:
                {
                    _classes[ObjectSlot]++;
                    _read[(int)PathClass.Object]++;
                    if (depth >= VortexLimits.MaxDTypeDepth / 2)
                    {
                        return;
                    }

                    VariantNested fields = VariantNested.Read(value);
                    _keys ??= new Dictionary<string, PathCount>(StringComparer.Ordinal);
                    for (int i = 0; i < fields.Count; i++)
                    {
                        string name = census.Name(in dictionary, fields.IdAt(i));
                        if (!_keys.TryGetValue(name, out PathCount? key))
                        {
                            _keys[name] = key = new PathCount();
                        }

                        key.Add(census, in dictionary, fields.ValueAt(i), depth + 1);
                    }

                    return;
                }

                case ParquetVariant.BasicArray:
                {
                    _read[(int)PathClass.List]++;
                    VariantNested elements = VariantNested.Read(value);
                    if (elements.Count == 0)
                    {
                        _classes[EmptyListSlot]++;
                        return;
                    }

                    int shared = -1;
                    for (int i = 0; i < elements.Count; i++)
                    {
                        int slot = ClassOf(elements.ValueAt(i));
                        if (slot >= ObjectSlot || (shared >= 0 && slot != shared))
                        {
                            shared = OtherSlot;
                            break;
                        }

                        shared = slot;
                    }

                    _classes[shared == OtherSlot ? OtherSlot : ListSlot + shared]++;
                    return;
                }

                default:
                {
                    VariantScalar scalar = VariantScalar.Read(value);
                    _classes[SlotOf(scalar.Kind)]++;
                    Bound(census, scalar);
                    return;
                }
            }
        }

        /// <summary>Counts <paramref name="scalar"/> in the class a question reads it as, and widens that class's bounds.</summary>
        private void Bound(PathCensus census, VariantScalar scalar)
        {
            switch (scalar.Kind)
            {
                case VariantKind.Null:
                    _read[(int)PathClass.Null]++;
                    return;
                case VariantKind.Bool:
                    _read[(int)PathClass.Boolean]++;
                    _true |= scalar.Integer != 0;
                    _false |= scalar.Integer == 0;
                    return;
                case VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64:
                {
                    _read[(int)PathClass.Integer]++;
                    long integer = (long)scalar.Integer;
                    _minInteger = Math.Min(_minInteger, integer);
                    _maxInteger = Math.Max(_maxInteger, integer);
                    return;
                }

                case VariantKind.Float or VariantKind.Double:
                {
                    _read[(int)PathClass.Double]++;
                    double number = scalar.Number;
                    if (double.IsNaN(number))
                    {
                        _nans = true;
                        return;
                    }

                    _minDouble = Math.Min(_minDouble, number);
                    _maxDouble = Math.Max(_maxDouble, number);
                    return;
                }

                case VariantKind.String:
                    _read[(int)PathClass.String]++;
                    if (census.StringBoundBytes > 0)
                    {
                        Text(scalar.Bytes, census.StringBoundBytes);
                    }

                    return;
                case VariantKind.Timestamp:
                {
                    _read[(int)PathClass.Timestamp]++;
                    long micros = scalar.Microseconds;
                    _minTime = Math.Min(_minTime, micros);
                    _maxTime = Math.Max(_maxTime, micros);
                    return;
                }

                default:
                    _read[(int)PathClass.Other]++;
                    return;
            }
        }

        /// <summary>Widens the text bounds by <paramref name="text"/>, cut to <paramref name="bound"/> bytes.</summary>
        private void Text(ReadOnlySpan<byte> text, int bound)
        {
            ReadOnlySpan<byte> cut = text[..Math.Min(text.Length, bound)];
            bool longer = text.Length > bound;
            if (_minTextLength < 0)
            {
                _minText = new byte[bound];
                _maxText = new byte[bound];
                cut.CopyTo(_minText);
                cut.CopyTo(_maxText);
                _minTextLength = _maxTextLength = cut.Length;
                _maxTextCut = longer;
                return;
            }

            if (cut.SequenceCompareTo(_minText.AsSpan(0, _minTextLength)) < 0)
            {
                cut.CopyTo(_minText);
                _minTextLength = cut.Length;
            }

            int order = cut.SequenceCompareTo(_maxText.AsSpan(0, _maxTextLength));
            if (order > 0)
            {
                cut.CopyTo(_maxText);
                _maxTextLength = cut.Length;
                _maxTextCut = longer;
            }
            else if (order == 0)
            {
                _maxTextCut |= longer;
            }
        }

        /// <summary>The slot of a value that is not an object or a list: its class, or anything else.</summary>
        private static int ClassOf(ReadOnlySpan<byte> value) =>
            (value[0] & 0x03) is ParquetVariant.BasicObject or ParquetVariant.BasicArray
                ? OtherSlot
                : SlotOf(VariantScalar.Read(value).Kind);

        /// <summary>The slot of a scalar of <paramref name="kind"/>: its shreddable class, or anything else.</summary>
        private static int SlotOf(VariantKind kind) => kind switch
        {
            VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64 => (int)VariantClass.Integer,
            VariantKind.Double => (int)VariantClass.Double,
            VariantKind.String => (int)VariantClass.String,
            VariantKind.Bool => (int)VariantClass.Boolean,
            _ => OtherSlot,
        };
    }
}

/// <summary>
/// The class a question reads a path's value as: what <c>As…</c> takes of it, and what the path
/// directory counts and bounds.
/// </summary>
internal enum PathClass : byte
{
    /// <summary>The variant null.</summary>
    Null = 0,

    /// <summary>A boolean, which <c>AsBool</c> reads.</summary>
    Boolean = 1,

    /// <summary>An integer of any width, which <c>AsInt64</c> reads.</summary>
    Integer = 2,

    /// <summary>A float or a double, which <c>AsDouble</c> reads.</summary>
    Double = 3,

    /// <summary>A string, which <c>AsString</c> reads.</summary>
    String = 4,

    /// <summary>A timestamp, which <c>AsTimestamp</c> reads.</summary>
    Timestamp = 5,

    /// <summary>An object.</summary>
    Object = 6,

    /// <summary>A list.</summary>
    List = 7,

    /// <summary>Any other value: a decimal, a date, a time, binary bytes, a uuid.</summary>
    Other = 8,
}
