using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Vorticity.Aggregating;

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
/// <para>
/// Given <paramref name="groupRows"/>, each path also keeps the spans of rows its values lie in, from
/// which the directory says which groups of that many rows of the object hold it. Two values of a path
/// that many rows apart or fewer share a span, so a path keeps about a span per group at most and the
/// groups a span names are a superset of those that hold it.
/// </para>
/// </remarks>
internal sealed class PathCensus(int stringBoundBytes = 0, long groupRows = 0)
{
    private byte[] _metadata = [];
    private int _metadataLength = -1;

    // The name of the key being looked up, decoded: a key met before is found by it, without a string.
    private char[] _chars = new char[64];

    // The run of rows of the same metadata being counted, which the counts' field ids are kept for; and the ids the counts
    // keep, every count together.
    private int _epoch;
    private long _heldIds;

    /// <summary>
    /// The field ids the counts keep the count of, every count together: 2 MB of references. An object's count keeps the
    /// ids of the metadata its rows share, as many as the dictionary holds, which a census of many objects under a large
    /// dictionary would otherwise multiply.
    /// </summary>
    private const long MaxHeldIds = 1 << 18;

    // The rows within which two values of a path share a span; 0 when the paths keep no spans.
    private readonly long _gap = groupRows;

    /// <summary>
    /// The most keys the census counts, every depth of objects together: past them, the values of a key it has not met
    /// are counted as their object's other keys', and the census is <see cref="Partial"/>. Keys used as data, an id a
    /// key, would otherwise grow it by a count of a few hundred bytes a key, without end.
    /// </summary>
    internal const int MaxKeys = 16_384;

    // The keys counted so far, every depth together; and the counts the tree holds, those of rows counted before a Clear
    // included.
    private int _keyCount;
    private int _held;

    /// <summary>The bytes a text bound keeps; 0 for none.</summary>
    internal int StringBoundBytes { get; } = stringBoundBytes;

    /// <summary>
    /// Whether keys past <see cref="MaxKeys"/> were counted as their objects' other keys': a directory of what the census
    /// counted then refutes nothing by a path's absence.
    /// </summary>
    internal bool Partial { get; private set; }

    /// <summary>The counts of the column's values themselves.</summary>
    internal PathCount Root { get; } = new PathCount();

    /// <summary>The rows counted, nulls included.</summary>
    internal long Rows { get; private set; }

    /// <summary>The bytes of the rows' values, as the residual would hold them unshredded.</summary>
    internal long ValueBytes { get; private set; }

    /// <summary>The counts of keys the census's tree holds, those of the rows counted before a <see cref="Clear"/> included.</summary>
    internal int HeldKeys => _held;

    /// <summary>Counts a null row.</summary>
    internal void AddNull() => Rows++;

    /// <summary>The spans the paths keep given back to the process's shelf, once nothing reads them: the census's spans are none after.</summary>
    internal void Release() => Root.Release();

    /// <summary>
    /// Forgets every row counted and keeps its tree of paths for the next rows: a key met again counts from nothing, as
    /// one met for the first time does, its name and its keys' kept; one not met again counts nothing, which
    /// <see cref="Merge"/> passes over. A job's first pass counts its inputs one after the other in the census of a lane,
    /// whose tree, made new for every input, was a few hundred bytes a key.
    /// </summary>
    internal void Clear()
    {
        Rows = 0;
        ValueBytes = 0;
        Partial = false;
        _keyCount = 0;
        _metadataLength = -1;
        _epoch++;
        Root.Clear();
    }

    /// <summary>
    /// Counts every row of the variant column <paramref name="field"/> of <paramref name="batch"/>'s root
    /// struct, after the rows counted before.
    /// </summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal void Add(RecordBatch batch, int field) => Add(Prepare(batch, field));

    /// <summary>
    /// Counts the rows of the variant column <paramref name="field"/> of <paramref name="batch"/>'s root struct that its
    /// selection keeps, every row when it has none, after the rows counted before.
    /// </summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal void AddSelected(RecordBatch batch, int field) => Add(Prepare(batch, field), batch.SelectionWords);

    /// <summary>
    /// The variant column <paramref name="field"/> of <paramref name="batch"/>'s root struct made ready to count: its metadata
    /// and its values in canonical form, which may add nodes to the batch's arena; counting them after only reads it, and may
    /// run on another thread while the batch is written.
    /// </summary>
    internal static Counting Prepare(RecordBatch batch, int field)
    {
        Arrays.CanonicalArena arena = batch.Arena;
        int column = arena.GetNode(batch.RootIndex).GetFieldIndex(field);
        Arrays.CanonicalNode variant = arena.GetNode(column);
        return new Counting(
            arena, column, EncodedForms.Canonical(arena, variant.GetFieldIndex(0)), EncodedForms.Canonical(arena, variant.GetFieldIndex(1)), batch.RowCount);
    }

    /// <summary>Counts every row of a variant column made ready (<see cref="Prepare(RecordBatch, int)"/>), after the rows counted before.</summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal void Add(in Counting counting) => Add(in counting, default);

    /// <summary>
    /// Counts the rows of a variant column made ready (<see cref="Prepare(RecordBatch, int)"/>) that <paramref name="selection"/> keeps, a bit
    /// a row, every row when it is empty, after the rows counted before.
    /// </summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal void Add(in Counting counting, ReadOnlySpan<ulong> selection)
    {
        Arrays.CanonicalArena arena = counting.Arena;
        Arrays.CanonicalNode metadataNode = arena.GetNode(counting.Metadata);
        Arrays.CanonicalNode valueNode = arena.GetNode(counting.Value);
        for (int row = 0; row < counting.Rows; row++)
        {
            if (!selection.IsEmpty && ((selection[row >> 6] >> (row & 63)) & 1) == 0)
            {
                continue;
            }

            if (!Arrays.ArenaWords.IsValid(arena, counting.Column, row))
            {
                AddNull();
                continue;
            }

            Add(
                Compute.LiteralReader.ViewAt(metadataNode, row),
                Arrays.ArenaWords.IsValid(arena, counting.Value, row) ? Compute.LiteralReader.ViewAt(valueNode, row) : VariantEncoding.NullValue);
        }
    }

    /// <summary>
    /// The variant column at <paramref name="column"/>, a node of <paramref name="arena"/> of <paramref name="rows"/> rows, made
    /// ready to count, as <see cref="Prepare(RecordBatch, int)"/> makes a batch's top-level one: a nested column too.
    /// </summary>
    internal static Counting Prepare(Arrays.CanonicalArena arena, int column, int rows)
    {
        column = EncodedForms.Canonical(arena, column);
        Arrays.CanonicalNode variant = arena.GetNode(column);
        int metadata = variant.GetFieldIndex(0);
        int value = arena.GetNode(column).GetFieldIndex(1);
        return new Counting(arena, column, EncodedForms.Canonical(arena, metadata), EncodedForms.Canonical(arena, value), rows);
    }

    /// <summary>A batch's variant column made ready to count (<see cref="Prepare(RecordBatch, int)"/>): the arena, the column, its metadata and its values in canonical form, its rows.</summary>
    internal readonly record struct Counting(Arrays.CanonicalArena Arena, int Column, int Metadata, int Value, int Rows);

    /// <summary>
    /// Counts a row of <paramref name="value"/>, its keys named by <paramref name="metadata"/>, after the rows counted
    /// before.
    /// </summary>
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
            _epoch++;
        }

        VariantDictionary dictionary = VariantDictionary.Read(metadata);
        Root.Add(this, in dictionary, value, depth: 1);
    }

    /// <summary>
    /// Adds what <paramref name="other"/> counted, as if its rows had been counted after these: its counts and bounds path by
    /// path, its keys this census has not met counted as theirs while <see cref="MaxKeys"/> lets it, and as their objects'
    /// other keys past it. Under that many keys, the census is the one the rows of both, counted one after the other, make.
    /// </summary>
    /// <exception cref="InvalidOperationException">Either census keeps the spans of its paths' rows, which only a count in order makes.</exception>
    internal void Merge(PathCensus other)
    {
        if (_gap > 0 || other._gap > 0)
        {
            throw new InvalidOperationException("A census that keeps the spans of its rows counts them in order and merges with none.");
        }

        Rows += other.Rows;
        ValueBytes += other.ValueBytes;
        Partial |= other.Partial;
        Root.Merge(this, other.Root);
    }

    /// <summary>The UTF-8 name <paramref name="utf8"/> decoded into the census's scratch, valid until the next name decoded.</summary>
    private ReadOnlySpan<char> Chars(ReadOnlySpan<byte> utf8)
    {
        int most = Encoding.UTF8.GetMaxCharCount(utf8.Length);
        if (_chars.Length < most)
        {
            _chars = new char[Math.Max(most, 2 * _chars.Length)];
        }

        return _chars.AsSpan(0, Encoding.UTF8.GetChars(utf8, _chars));
    }

    /// <summary>
    /// Room in <paramref name="byId"/> for the counts of the current metadata's <paramref name="ids"/> ids, cleared, as far
    /// as <see cref="MaxHeldIds"/> lets the counts' rooms grow together: the ids it keeps, from the first.
    /// </summary>
    private int KeepIds(ref PathCount?[] byId, int ids)
    {
        if (byId.Length < ids && _heldIds < MaxHeldIds)
        {
            int length = (int)Math.Min(Math.Max(ids, 2L * byId.Length), byId.Length + (MaxHeldIds - _heldIds));
            _heldIds += length - byId.Length;
            byId = new PathCount?[length];
        }

        int kept = Math.Min(byId.Length, ids);
        Array.Clear(byId, 0, kept);
        return kept;
    }

    /// <summary>The counts of the values at one path: by class, and of an object's keys.</summary>
    internal sealed class PathCount
    {
        /// <summary>Occurrences by class: the four shreddable classes, an object, a list of each class, and anything else.</summary>
        private readonly long[] _classes = new long[Slots];

        private Dictionary<string, PathCount>? _keys;

        // The count of each field id of the run's metadata, the first ids of it the census lets this count keep, and the
        // census's epoch they were kept for, -1 before; and what a key past the census's keys stands for, counted as its
        // object's other keys.
        private PathCount?[] _byId = [];
        private int _keptIds;
        private int _byIdEpoch = -1;
        private static readonly PathCount Uncounted = new PathCount();

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

        // The spans of rows the values lie in, a first and a last row each, the last span open.
        private long[]? _spans;
        private int _spanCount;

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

        /// <summary>The values of the keys of the objects at this path past the census's <see cref="MaxKeys"/>, which it counts no further.</summary>
        internal long OtherKeys { get; private set; }

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

        /// <summary>The spans of rows the values lie in, a first and a last row each, in order; empty when the census keeps none.</summary>
        internal ReadOnlySpan<long> Spans => _spans.AsSpan(0, 2 * _spanCount);

        /// <summary>Notes a value at <paramref name="row"/>, in the open span when it is within <paramref name="gap"/> rows of its end.</summary>
        private void Occur(long row, long gap)
        {
            int last = (2 * _spanCount) - 1;
            if (_spanCount > 0 && row - _spans![last] <= gap)
            {
                _spans[last] = row;
                return;
            }

            if (_spans is null || last + 2 >= _spans.Length)
            {
                // From the process's shelf, given back when the census is (Release): an object's census keeps spans for
                // each of its paths, 3 bytes a row of a build of the client's log records made new.
                long[] held = _spans ?? [];
                long[] grown = ArrayShelf.Retained.Take<long>(ArrayShelf.Grown(last + 3, held.Length, least: 4), zeroed: false);
                held.AsSpan(0, 2 * _spanCount).CopyTo(grown);
                ArrayShelf.Retained.Give(held);
                _spans = grown;
            }

            _spans[last + 1] = row;
            _spans[last + 2] = row;
            _spanCount++;
        }

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

        /// <summary>The spans of this path and of every key below given back to the process's shelf, and kept no more.</summary>
        internal void Release()
        {
            if (_spans is { } spans)
            {
                ArrayShelf.Retained.Give(spans);
                _spans = null;
                _spanCount = 0;
            }

            if (_keys is { } keys)
            {
                foreach (PathCount key in keys.Values)
                {
                    key.Release();
                }
            }
        }

        /// <summary>These counts and those of every key below, back to none, the keys and the buffers kept (<see cref="PathCensus.Clear"/>).</summary>
        internal void Clear()
        {
            Values = 0;
            OtherKeys = 0;
            Array.Clear(_classes);
            Array.Clear(_read);
            _minInteger = long.MaxValue;
            _maxInteger = long.MinValue;
            _minDouble = double.PositiveInfinity;
            _maxDouble = double.NegativeInfinity;
            _nans = false;
            _minTime = long.MaxValue;
            _maxTime = long.MinValue;
            _false = false;
            _true = false;
            _spanCount = 0;
            _minTextLength = -1;
            _maxTextLength = -1;
            _maxTextCut = false;
            _keptIds = 0;
            _byIdEpoch = -1;
            if (_keys is { } keys)
            {
                foreach (PathCount key in keys.Values)
                {
                    key.Clear();
                }
            }
        }

        internal void Add(PathCensus census, in VariantDictionary dictionary, ReadOnlySpan<byte> value, int depth)
        {
            Values++;
            // At the row being counted, the census's last.
            if (census._gap > 0)
            {
                Occur(census.Rows - 1, census._gap);
            }

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

                    // The count of each field id of the metadata the rows of the run share, found by its name once a run:
                    // rows of one source share their metadata, whose names would otherwise be hashed and looked up again
                    // at every row.
                    if (_byIdEpoch != census._epoch)
                    {
                        _byIdEpoch = census._epoch;
                        _keptIds = census.KeepIds(ref _byId, dictionary.Count);
                    }

                    for (int i = 0; i < fields.Count; i++)
                    {
                        int id = fields.IdAt(i);
                        PathCount? key = (uint)id < (uint)_keptIds ? _byId[id] : null;
                        if (key is null)
                        {
                            // Found by the name's characters, decoded into the census's scratch: rows of a merge come from
                            // sources of their own metadata, a run of one row each, and a string a key a row was half of what
                            // a consolidation allocated. A name met for the first time takes a string.
                            ReadOnlySpan<char> name = census.Chars(dictionary[id]);
                            if (!_keys.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out key))
                            {
                                if (census._keyCount >= MaxKeys)
                                {
                                    key = Uncounted;
                                }
                                else
                                {
                                    _keys[new string(name)] = key = new PathCount();
                                    census._keyCount++;
                                    census._held++;
                                }
                            }
                            else if (key.Values == 0)
                            {
                                // A key the tree kept from rows counted before a Clear: new to these rows, met for the
                                // first time.
                                if (census._keyCount >= MaxKeys)
                                {
                                    key = Uncounted;
                                }
                                else
                                {
                                    census._keyCount++;
                                }
                            }

                            if ((uint)id < (uint)_keptIds)
                            {
                                _byId[id] = key;
                            }
                        }

                        if (ReferenceEquals(key, Uncounted))
                        {
                            OtherKeys++;
                            census.Partial = true;
                            continue;
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

        /// <summary>Adds what <paramref name="other"/> counted at the same path of another census to these counts, in <paramref name="census"/> (<see cref="PathCensus.Merge"/>).</summary>
        internal void Merge(PathCensus census, PathCount other)
        {
            Values += other.Values;
            OtherKeys += other.OtherKeys;
            for (int slot = 0; slot < Slots; slot++)
            {
                _classes[slot] += other._classes[slot];
            }

            for (int @class = 0; @class < PathClasses; @class++)
            {
                _read[@class] += other._read[@class];
            }

            _minInteger = Math.Min(_minInteger, other._minInteger);
            _maxInteger = Math.Max(_maxInteger, other._maxInteger);
            _minDouble = Math.Min(_minDouble, other._minDouble);
            _maxDouble = Math.Max(_maxDouble, other._maxDouble);
            _nans |= other._nans;
            _minTime = Math.Min(_minTime, other._minTime);
            _maxTime = Math.Max(_maxTime, other._maxTime);
            _false |= other._false;
            _true |= other._true;
            if (other.HasTextBounds)
            {
                Text(other, census.StringBoundBytes);
            }

            if (other._keys is not { Count: > 0 } keys)
            {
                return;
            }

            _keys ??= new Dictionary<string, PathCount>(StringComparer.Ordinal);
            foreach ((string name, PathCount count) in keys)
            {
                // A key the other's tree kept from rows it counted before a Clear, which its rows did not meet.
                if (count.Values == 0)
                {
                    continue;
                }

                bool found = _keys.TryGetValue(name, out PathCount? mine);
                if (!found || mine!.Values == 0)
                {
                    // A key past the census's: its values go to its objects' other keys, as a count in order puts them.
                    if (census._keyCount >= MaxKeys)
                    {
                        OtherKeys += count.Values;
                        census.Partial = true;
                        continue;
                    }

                    if (!found)
                    {
                        _keys[name] = mine = new PathCount();
                        census._held++;
                    }

                    census._keyCount++;
                }

                mine!.Merge(census, count);
            }
        }

        /// <summary>Widens the text bounds by those of <paramref name="other"/>, already cut to <paramref name="bound"/> bytes.</summary>
        private void Text(PathCount other, int bound)
        {
            if (!HasTextBounds)
            {
                Bounds(bound);
                other.MinText.CopyTo(_minText);
                other.MaxText.CopyTo(_maxText);
                _minTextLength = other._minTextLength;
                _maxTextLength = other._maxTextLength;
                _maxTextCut = other._maxTextCut;
                return;
            }

            if (other.MinText.SequenceCompareTo(MinText) < 0)
            {
                other.MinText.CopyTo(_minText);
                _minTextLength = other._minTextLength;
            }

            int order = other.MaxText.SequenceCompareTo(MaxText);
            if (order > 0)
            {
                other.MaxText.CopyTo(_maxText);
                _maxTextLength = other._maxTextLength;
                _maxTextCut = other._maxTextCut;
            }
            else if (order == 0)
            {
                _maxTextCut |= other._maxTextCut;
            }
        }

        /// <summary>Counts <paramref name="scalar"/> in the class a question reads it as, and widens that class's bounds.</summary>
        private void Bound(PathCensus census, VariantScalar scalar) => Bound(census.StringBoundBytes, scalar);

        private void Bound(int stringBoundBytes, VariantScalar scalar)
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
                    if (stringBoundBytes > 0)
                    {
                        Text(scalar.Bytes, stringBoundBytes);
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

        /// <summary>The buffers of the text bounds, of <paramref name="bound"/> bytes: those a cleared count kept, or new.</summary>
        [MemberNotNull(nameof(_minText), nameof(_maxText))]
        private void Bounds(int bound)
        {
            if (_minText?.Length != bound || _maxText?.Length != bound)
            {
                _minText = new byte[bound];
                _maxText = new byte[bound];
            }
        }

        /// <summary>Widens the text bounds by <paramref name="text"/>, cut to <paramref name="bound"/> bytes.</summary>
        private void Text(ReadOnlySpan<byte> text, int bound)
        {
            ReadOnlySpan<byte> cut = text[..Math.Min(text.Length, bound)];
            bool longer = text.Length > bound;
            if (_minTextLength < 0)
            {
                Bounds(bound);
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
        private static int SlotOf(VariantKind kind) => VariantClasses.Of(kind) is { } @class ? (int)@class : OtherSlot;
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
