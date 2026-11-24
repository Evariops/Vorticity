using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Buffers;

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
    // The name of the key being looked up, decoded: a key met before is found by it, without a string.
    private char[] _chars = new char[64];

    // The run of rows of the same metadata being counted, which the counts' field ids are kept for; and the ids the counts
    // keep, every count together.
    private int _epoch;
    private long _heldIds;

    // The run's metadata, its bytes and their length, -1 before a row; and the rows of the run so far, the row being counted's included.
    private byte[] _metadata = [];
    private int _metadataLength = -1;
    private int _runRows;

    // The names met, numbered: a count finds its keys by a name's number, where it decoded the name and looked it up as
    // characters at every key of every row. The number of each field id of the run's metadata, stamped with the run; and the
    // generation of the numbers, which what is found by them is valid for.
    private readonly Aggregating.ByteKeyTable _names = new Aggregating.ByteKeyTable();
    private int[] _nameNumbers = [];
    private int[] _nameStamps = [];
    private int _generation;

    /// <summary>
    /// The names the census numbers before it forgets them and numbers them again: keys used as data, an id a key, would
    /// otherwise grow its table without end, past the keys it counts.
    /// </summary>
    private const int MaxNames = 4 * MaxKeys;

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

    /// <summary>The counts of the column's values themselves, the rows that repeated the last value counted in.</summary>
    internal PathCount Root
    {
        get
        {
            Settle();
            return _root;
        }
    }

    private readonly PathCount _root = new PathCount();

    // The value of the last row counted, and the rows since that repeated it under the same metadata, not yet counted in its
    // paths: the rows of a metric's series hold the same attributes one after the other, and parsing each again took a fifth
    // of a merge of metric records (2026-11-16).
    private byte[] _last = [];
    private int _lastLength = -1;
    private long _repeats;

    // The nodes the value being counted may still hold: one a byte of it. A node takes a byte of its own at least, so only
    // a value whose fields name the same bytes over and over, which unfolds into exponentially many, runs out.
    private int _visits;

    /// <summary>
    /// Counts the rows that repeated the last value in its paths, all at once: each count takes them, each bound the value
    /// once, and each path's spans the rows they lie in, which follow each other.
    /// </summary>
    private void Settle()
    {
        if (_repeats == 0)
        {
            return;
        }

        long times = _repeats;
        _repeats = 0;
        VariantDictionary dictionary = VariantDictionary.Read(_metadata.AsSpan(0, _metadataLength));
        _visits = _lastLength;
        _root.Add(this, in dictionary, _last.AsSpan(0, _lastLength), depth: 1, times);
    }

    /// <summary>Keeps <paramref name="value"/>, the last row's, for the rows after it to be told repeats of it.</summary>
    private void Remember(ReadOnlySpan<byte> value)
    {
        if (_last.Length < value.Length)
        {
            _last = new byte[Math.Max(value.Length, 2 * _last.Length)];
        }

        value.CopyTo(_last);
        _lastLength = value.Length;
    }

    /// <summary>The rows counted, nulls included.</summary>
    internal long Rows { get; private set; }

    /// <summary>The bytes of the rows' values, as the residual would hold them unshredded.</summary>
    internal long ValueBytes { get; private set; }

    /// <summary>The counts of keys the census's tree holds, those of the rows counted before a <see cref="Clear"/> included.</summary>
    internal int HeldKeys => _held;

    /// <summary>Counts a null row, after the rows that repeated a value: those that repeat it after it lie apart from them.</summary>
    internal void AddNull()
    {
        Settle();
        Rows++;
    }

    /// <summary>The spans the paths keep given back to the pool, once nothing reads them: the census's spans are none after.</summary>
    internal void Release()
    {
        _repeats = 0;
        _lastLength = -1;
        _root.Release(Account);
    }

    /// <summary>
    /// What counts the spans the paths keep, a block a path and class growing with the rows: the account of the work that
    /// owns the census, its writer's or its question's; null for none.
    /// </summary>
    internal Buffers.MemoryAccount? Account { get; init; }

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
        _repeats = 0;
        _lastLength = -1;
        _epoch++;
        // The keys found by number forgotten too: one past the keys counted then may be counted now.
        _generation++;
        _root.Clear();
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
    internal void Add(in Counting counting, ReadOnlySpan<ulong> selection) => Add(in counting, selection, 0, counting.Rows);

    /// <summary>
    /// Counts rows <c>[start, start + count)</c> of a variant column made ready (<see cref="Prepare(RecordBatch, int)"/>), after the
    /// rows counted before: a slice of the rows a writer counts on a lane of its own, merged after the slices before it.
    /// </summary>
    /// <exception cref="VortexFormatException">A row's value or metadata does not decode.</exception>
    internal void Add(in Counting counting, int start, int count) => Add(in counting, default, start, start + count);

    private void Add(in Counting counting, ReadOnlySpan<ulong> selection, int start, int end)
    {
        Arrays.CanonicalArena arena = counting.Arena;

        // The views and their data resolved once: a row's through the node's accessors were a sixth of what a row cost.
        Arrays.ViewValues metadata = new Arrays.ViewValues(arena.GetNode(counting.Metadata));
        Arrays.ViewValues values = new Arrays.ViewValues(arena.GetNode(counting.Value));

        // The validities read only where some row is null: each read looks the node's record up.
        bool everyRow = arena.GetNode(counting.Column).Validity.IsAllValid;
        bool everyValue = arena.GetNode(counting.Value).Validity.IsAllValid;

        // A row whose value and metadata views are the row before's, which the census counted, repeats its bytes: counted as
        // a repeat without reading them. A block's rows view a value that repeats the row before's once, and comparing each
        // repeat's bytes with the last value's was most of the census of a consolidation of metric records (2026-11-18).
        ReadOnlySpan<Int128> valueViews = MemoryMarshal.Cast<byte, Int128>(arena.GetNode(counting.Value).Views.Span);
        ReadOnlySpan<Int128> metadataViews = arena.GetNode(counting.Metadata).Kind == Arrays.CanonicalKind.VarBinView
            ? MemoryMarshal.Cast<byte, Int128>(arena.GetNode(counting.Metadata).Views.Span)
            : default;
        int counted = -2;
        for (int row = start; row < end; row++)
        {
            if (!selection.IsEmpty && ((selection[row >> 6] >> (row & 63)) & 1) == 0)
            {
                continue;
            }

            if (!everyRow && !Arrays.ArenaWords.IsValid(arena, counting.Column, row))
            {
                AddNull();
                continue;
            }

            bool valid = everyValue || Arrays.ArenaWords.IsValid(arena, counting.Value, row);
            if (valid && counted == row - 1 && !metadataViews.IsEmpty
                && valueViews[row] == valueViews[row - 1] && metadataViews[row] == metadataViews[row - 1])
            {
                Again((int)(uint)valueViews[row]);
                counted = row;
                continue;
            }

            Add(metadata.At(row), valid ? values.At(row) : VariantEncoding.NullValue);
            counted = valid ? row : -2;
        }
    }

    /// <summary>Counts a row that repeats the last row's value, of <paramref name="length"/> bytes, under its metadata.</summary>
    private void Again(int length)
    {
        Rows++;
        ValueBytes += length;
        _repeats++;
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
        // The last row's value again, under its metadata: counted with it once the repeats end.
        if (value.Length == _lastLength
            && metadata.Length == _metadataLength
            && value.SequenceEqual(_last.AsSpan(0, _lastLength))
            && metadata.SequenceEqual(_metadata.AsSpan(0, _metadataLength)))
        {
            Rows++;
            ValueBytes += value.Length;
            _repeats++;
            return;
        }

        Settle();
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
            _runRows = 0;
        }

        _runRows++;
        VariantDictionary dictionary = VariantDictionary.Read(metadata);
        _visits = value.Length;
        _root.Add(this, in dictionary, value, depth: 1);
        Remember(value);
    }

    /// <summary>
    /// Begins counting <paramref name="rows"/> rows, nulls included, whose values hold <paramref name="valueBytes"/> bytes,
    /// from what a shredding took of them rather than from the rows themselves
    /// (<c>VariantShredder.Shredding.Count</c>): the rows counted, their paths left to what each node took, at the rows
    /// from the one returned on. The rows that repeated the last value counted are counted in first.
    /// </summary>
    /// <returns>The first of the rows, among the rows the census counts.</returns>
    internal long BeginTaken(int rows, long valueBytes)
    {
        Settle();
        _lastLength = -1;
        long first = Rows;
        Rows += rows;
        ValueBytes += valueBytes;
        return first;
    }

    /// <summary>
    /// Whether the census may count <paramref name="keys"/> keys it has not met and stay under <see cref="MaxKeys"/>: past
    /// them, which keys it counts depends on the order it meets them in, a row at a time and a key at a time, which a
    /// count of what a shredding took does not follow.
    /// </summary>
    internal bool HasRoomFor(long keys) => _keyCount + keys < MaxKeys;

    /// <summary>
    /// The dictionary of <paramref name="metadata"/>, a row's, made the census's run's: the run's metadata, the row's when it
    /// differs, as a row counted makes it (<see cref="Add(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>), so that the keys a
    /// value kept whole names are found by its ids.
    /// </summary>
    internal VariantDictionary Enter(ReadOnlySpan<byte> metadata)
    {
        if (metadata.Length != _metadataLength || !metadata.SequenceEqual(_metadata.AsSpan(0, _metadataLength)))
        {
            if (_metadata.Length < metadata.Length)
            {
                _metadata = new byte[Math.Max(metadata.Length, 2 * _metadata.Length)];
            }

            metadata.CopyTo(_metadata);
            _metadataLength = metadata.Length;
            _epoch++;
            _runRows = 0;
        }

        _runRows++;
        return VariantDictionary.Read(metadata);
    }

    /// <summary>
    /// The bytes of the values of the rows a variant column made ready holds (<see cref="Prepare(RecordBatch, int)"/>), as
    /// <see cref="Add(in Counting)"/> counts them: a row's value, the variant null's for a row that holds none, nothing for
    /// a null row.
    /// </summary>
    internal static long ValueBytesOf(in Counting counting)
    {
        Arrays.CanonicalArena arena = counting.Arena;

        // The sizes alone are read: of values not put back together, from their views as they lie, and none is put back.
        Arrays.CanonicalNode values = arena.GetNode(counting.Value);
        ReadOnlySpan<Int128> views = MemoryMarshal.Cast<byte, Int128>(
            values.Kind == Arrays.CanonicalKind.Shredded ? values.HollowViews.Span : values.Views.Span);
        bool everyRow = arena.GetNode(counting.Column).Validity.IsAllValid;
        bool everyValue = arena.GetNode(counting.Value).Validity.IsAllValid;
        long bytes = 0;
        for (int row = 0; row < counting.Rows; row++)
        {
            if (!everyRow && !Arrays.ArenaWords.IsValid(arena, counting.Column, row))
            {
                continue;
            }

            bytes += everyValue || Arrays.ArenaWords.IsValid(arena, counting.Value, row) ? (uint)views[row] : VariantEncoding.NullValue.Length;
        }

        return bytes;
    }

    /// <summary>
    /// Counts the rows of the variant column <paramref name="field"/> of <paramref name="batch"/>'s root struct and the bytes of
    /// their values, as <see cref="Add(RecordBatch, int)"/> counts them, and none of their paths: those the directory of the
    /// object they come from tells (<see cref="AddPaths"/>), which a job's first pass reads in place of parsing every row.
    /// </summary>
    internal void AddRows(RecordBatch batch, int field)
    {
        Settle();
        Arrays.CanonicalArena arena = batch.Arena;
        int column = arena.GetNode(batch.RootIndex).GetFieldIndex(field);
        int value = EncodedForms.Canonical(arena, arena.GetNode(column).GetFieldIndex(1));
        Arrays.ViewValues values = new Arrays.ViewValues(arena.GetNode(value));
        int rows = batch.RowCount;
        long bytes = 0;
        for (int row = 0; row < rows; row++)
        {
            if (Arrays.ArenaWords.IsValid(arena, column, row))
            {
                bytes += Arrays.ArenaWords.IsValid(arena, value, row) ? values.At(row).Length : VariantEncoding.NullValue.Length;
            }
        }

        Rows += rows;
        ValueBytes += bytes;
    }

    /// <summary>
    /// Counts <paramref name="rows"/> rows whose values hold <paramref name="valueBytes"/> bytes, as <see cref="AddRows"/> counts
    /// a batch's, from what the directory of the object they come from keeps of them (<see cref="Indexes.VariantDirectory.ValueBytes"/>):
    /// a job's first pass then reads none of the object's column.
    /// </summary>
    internal void AddCounts(long rows, long valueBytes)
    {
        Settle();
        Rows += rows;
        ValueBytes += valueBytes;
    }

    /// <summary>
    /// Whether the directory <paramref name="body"/> tells every count a shredding is chosen from: whole, with no list, and with
    /// no number but 64-bit doubles. It keeps its counts by the class a question reads them as, which takes a 32-bit float for
    /// a double where a shredding takes it apart, and a list's elements' class it does not keep; a path whose numbers are all
    /// doubles it marks so (<see cref="Indexes.DirectoryEntry.WholeDoubles"/>).
    /// </summary>
    /// <remarks>
    /// Unmarked, every path of log records' attributes that held a duration refused its directory, and a tier merge of two
    /// objects parsed every row of their attributes again in its first pass, 6.6 s of its 11.3 at degree 14 (2026-11-18).
    /// </remarks>
    internal static bool Tells(Indexes.VariantDirectory.DirectoryBody body)
    {
        if (body.Partial)
        {
            return false;
        }

        Telling telling = default;
        body.Visit(ref telling);
        return telling.Tells;
    }

    /// <summary>
    /// Counts the paths the directory <paramref name="body"/> tells, as a count of the rows it was made of counts them, and no row:
    /// the rows and the bytes of their values come from <see cref="AddRows"/>. Only a directory that <see cref="Tells"/> them.
    /// </summary>
    internal void AddPaths(Indexes.VariantDirectory.DirectoryBody body)
    {
        Adding adding = new Adding(this);
        body.Visit(ref adding);
    }

    /// <summary>Whether every entry of a directory is of a class a count of rows keeps the same way (<see cref="Tells"/>).</summary>
    private struct Telling : Indexes.IDirectoryVisitor
    {
        private bool _refused;

        internal readonly bool Tells => !_refused;

        public void Head(long rows, long groupRows, bool partial) => _refused |= partial;

        public void Entry(in Indexes.DirectoryEntry entry) =>
            _refused |= entry.Class == PathClass.List || (entry.Class == PathClass.Double && !entry.WholeDoubles);
    }

    /// <summary>Each entry of a directory counted at its path (<see cref="AddPaths"/>).</summary>
    private readonly struct Adding(PathCensus census) : Indexes.IDirectoryVisitor
    {
        public void Head(long rows, long groupRows, bool partial)
        {
        }

        public void Entry(in Indexes.DirectoryEntry entry)
        {
            PathCount? count = census.Root;
            for (int k = 0; k < entry.Depth && count is not null; k++)
            {
                count = count.Named(census, entry.Key(k), entry.Values, last: k == entry.Depth - 1);
            }

            count?.Occurred(entry.Class, entry.Values);
        }
    }

    /// <summary>
    /// Adds what <paramref name="other"/> counted, as if its rows had been counted after these: its counts and bounds path by
    /// path, the spans of its paths' rows moved past these rows, its keys this census has not met counted as theirs while
    /// <see cref="MaxKeys"/> lets it, and as their objects' other keys past it. Under that many keys, the census is the one
    /// the rows of both, counted one after the other, make.
    /// </summary>
    /// <exception cref="InvalidOperationException">The censuses keep the spans of their rows within different gaps, or one keeps none.</exception>
    internal void Merge(PathCensus other)
    {
        if (_gap != other._gap)
        {
            throw new InvalidOperationException("Two censuses merge when both keep the spans of their rows within one gap, or neither keeps any.");
        }

        // The rows that repeated a value counted in at the rows they lie at, before the other's rows follow them.
        Settle();
        long offset = Rows;
        Rows += other.Rows;
        ValueBytes += other.ValueBytes;
        Partial |= other.Partial;
        Root.Merge(this, other.Root, offset);
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
    /// The number of the name of field id <paramref name="id"/> of <paramref name="dictionary"/>, the run's metadata: found once
    /// a run, the name hashed as its bytes lie.
    /// </summary>
    private int NameOf(in VariantDictionary dictionary, int id)
    {
        if ((uint)id < (uint)_nameStamps.Length && _nameStamps[id] == _epoch)
        {
            return _nameNumbers[id];
        }

        // The id comes from the value's bytes: held to the dictionary before it sizes the arrays below.
        if ((uint)id >= (uint)dictionary.Count)
        {
            throw new VortexFormatException($"A variant object names field id {id}, past the {dictionary.Count} keys of its metadata.");
        }

        if (_names.Count >= MaxNames)
        {
            // Every number forgotten, those of the run's ids with them: the counts find their keys by name again.
            _names.Clear();
            _generation++;
            _epoch++;
        }

        if (_nameStamps.Length <= id)
        {
            int length = Math.Max(id + 1, Math.Max(dictionary.Count, 2 * _nameStamps.Length));
            Array.Resize(ref _nameStamps, length);
            Array.Resize(ref _nameNumbers, length);
        }

        int number = _names.GetOrAdd(dictionary[id], out _);
        _nameStamps[id] = _epoch;
        _nameNumbers[id] = number;
        return number;
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
        /// <summary>
        /// Occurrences by class: the four shreddable classes, an object, a list of each class, and anything else. Held in the
        /// count, as the classes a question reads are: a value counted touches the count's own lines, where two arrays of
        /// their own were two more a key of every row.
        /// </summary>
        private SlotCounts _classes;

        [System.Runtime.CompilerServices.InlineArray(Slots)]
        private struct SlotCounts
        {
            private long _first;
        }

        [System.Runtime.CompilerServices.InlineArray(PathClasses)]
        private struct ClassCounts
        {
            private long _first;
        }

        private Dictionary<string, PathCount>? _keys;

        // The count of each field id of the run's metadata, the first ids of it the census lets this count keep, and the
        // census's epoch they were kept for, -1 before; and what a key past the census's keys stands for, counted as its
        // object's other keys.
        private PathCount?[] _byId = [];
        private int _keptIds;
        private int _byIdEpoch = -1;
        private static readonly PathCount Uncounted = new PathCount();

        // The keys met, by the number of their name in the census, a key past the census's keys among them, and the
        // census's generation of numbers they were met under: a metadata other than the row's before finds them so,
        // the name hashed as it lies, where it was decoded and looked up as characters.
        private Dictionary<int, PathCount>? _byName;
        private int _byNameGeneration = -1;

        /// <summary>Occurrences by the class a question reads them as.</summary>
        private ClassCounts _read;

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

        // The first eight bytes of each text bound, big-endian, zero past its end, when they are kept: a text whose own lie
        // strictly between them moves neither bound, its bytes compared no further, as most texts of a key do.
        private ulong _minPrefix;
        private ulong _maxPrefix;
        private bool _prefixed;

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

        /// <summary>
        /// The spans of rows the values of <paramref name="class"/> lie in, a first and a last row each, in order; empty when the
        /// census keeps none. Each class keeps its own, so that a directory names the groups that hold a path in one class:
        /// a path's spans whatever the class named, for a key of numbers sent as text by a few sources, the groups of every
        /// source as holding text.
        /// </summary>
        internal ReadOnlySpan<long> SpansOf(PathClass @class) =>
            @class == _spanClass ? _spans.View() : _classSpans is { } others ? others[(int)@class].View() : default;

        // The spans of the first class a value at this path was of, held here as most paths hold one class; those of the
        // others, made when a second class comes.
        private SpanList _spans;
        private PathClass _spanClass = NoClass;
        private SpanList[]? _classSpans;
        private const PathClass NoClass = (PathClass)byte.MaxValue;

        /// <summary>Appends <paramref name="spans"/>, a first and a last row each, <paramref name="offset"/> rows on, to the spans of <paramref name="class"/>.</summary>
        private void Spans(PathClass @class, ReadOnlySpan<long> spans, long offset, long gap, Buffers.MemoryAccount? account)
        {
            for (int i = 0; i < spans.Length; i += 2)
            {
                SpanListOf(@class).Append(spans[i] + offset, spans[i + 1] + offset, gap, account);
            }
        }

        /// <summary>The spans of <paramref name="class"/>: the first class met kept in the node, the others beside it.</summary>
        private ref SpanList SpanListOf(PathClass @class)
        {
            if (@class == _spanClass)
            {
                return ref _spans;
            }

            if (_spanClass == NoClass)
            {
                _spanClass = @class;
                return ref _spans;
            }

            return ref (_classSpans ??= new SpanList[PathClasses])[(int)@class];
        }

        /// <summary>Notes a value of <paramref name="class"/> at <paramref name="row"/>, in its class's spans.</summary>
        private void Occur(PathClass @class, long row, long gap, Buffers.MemoryAccount? account)
        {
            if (@class == _spanClass)
            {
                _spans.Occur(row, gap, account);
            }
            else if (_spanClass == NoClass)
            {
                _spanClass = @class;
                _spans.Occur(row, gap, account);
            }
            else
            {
                (_classSpans ??= new SpanList[PathClasses])[(int)@class].Occur(row, gap, account);
            }
        }

        /// <summary>The spans of rows values lie in, the open one's end kept beside them: a value within the gap of it touches the end alone.</summary>
        /// <remarks>
        /// The spans lie in a block of the pool, given back when the census is (<see cref="Release"/>): an object's census keeps
        /// spans for each of its paths, 3 bytes a row of a build of the client's log records made new. A list keeps the block's
        /// bytes alone, a field of 8 bytes in a list a path, and sees them as longs at their whole capacity.
        /// </remarks>
        private struct SpanList
        {
            private byte[]? _bytes;
            private int _count;
            private long _openEnd;

            /// <summary>The block the spans lie in, every long its bytes hold.</summary>
            private readonly Block<long> Spans => Block<long>.Over(_bytes, _bytes is null ? 0 : _bytes.Length / sizeof(long));

            /// <summary>The spans, a first and a last row each.</summary>
            internal ReadOnlySpan<long> View()
            {
                Block<long> spans = Spans;
                if (_count > 0)
                {
                    spans[(2 * _count) - 1] = _openEnd;
                }

                return spans.AsSpan(0, 2 * _count);
            }

            /// <summary>
            /// Notes a value at <paramref name="row"/>, in the open span when it is within <paramref name="gap"/> rows of its
            /// end; a block grown for it is counted by <paramref name="account"/>, when given.
            /// </summary>
            internal void Occur(long row, long gap, Buffers.MemoryAccount? account)
            {
                if (_count > 0 && row - _openEnd <= gap)
                {
                    _openEnd = row;
                    return;
                }

                int last = (2 * _count) - 1;
                Block<long> spans = Spans;
                if (_count > 0)
                {
                    spans[last] = _openEnd;
                }

                if (last + 2 >= spans.Length)
                {
                    Block<long> grown = Blocks.Rent<long>(Blocks.Grown(last + 3, spans.Length, least: 4));
                    long more = (grown.Bytes?.Length ?? 0) - (_bytes?.Length ?? 0);
                    if (account is not null && !account.TryTake(more))
                    {
                        Blocks.Return(grown);
                        throw account.Refused(more);
                    }

                    spans.AsSpan(0, 2 * _count).CopyTo(grown);
                    Blocks.Return(spans);
                    _bytes = grown.Bytes;
                    spans = Spans;
                }

                spans[last + 1] = row;
                spans[last + 2] = row;
                _openEnd = row;
                _count++;
            }

            /// <summary>
            /// Appends the span <paramref name="first"/> to <paramref name="last"/>, past every span noted: the open span's end
            /// taken on to <paramref name="last"/> when <paramref name="first"/> is within <paramref name="gap"/> rows of it, as
            /// noting its rows one by one would.
            /// </summary>
            internal void Append(long first, long last, long gap, Buffers.MemoryAccount? account)
            {
                if (_count > 0 && first - _openEnd <= gap)
                {
                    _openEnd = Math.Max(_openEnd, last);
                    return;
                }

                Occur(first, gap, account);
                _openEnd = last;
            }

            /// <summary>No span, the buffer kept.</summary>
            internal void Clear() => _count = 0;

            /// <summary>The block given back to the pool, and its bytes to <paramref name="account"/>, which counted them.</summary>
            internal void Release(Buffers.MemoryAccount? account)
            {
                account?.Give(_bytes?.Length ?? 0);
                Blocks.ReturnBytes(_bytes);
                _bytes = null;
                _count = 0;
            }
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

        /// <summary>The spans of this path and of every key below given back to the pool, and kept no more.</summary>
        internal void Release(Buffers.MemoryAccount? account)
        {
            _spans.Release(account);
            _spanClass = NoClass;
            if (_classSpans is { } others)
            {
                for (int c = 0; c < others.Length; c++)
                {
                    others[c].Release(account);
                }
            }

            if (_keys is { } keys)
            {
                foreach (PathCount key in keys.Values)
                {
                    key.Release(account);
                }
            }
        }

        /// <summary>These counts and those of every key below, back to none, the keys and the buffers kept (<see cref="PathCensus.Clear"/>).</summary>
        internal void Clear()
        {
            Values = 0;
            OtherKeys = 0;
            ((Span<long>)_classes).Clear();
            ((Span<long>)_read).Clear();
            _minInteger = long.MaxValue;
            _maxInteger = long.MinValue;
            _minDouble = double.PositiveInfinity;
            _maxDouble = double.NegativeInfinity;
            _nans = false;
            _minTime = long.MaxValue;
            _maxTime = long.MinValue;
            _false = false;
            _true = false;
            _spans.Clear();
            _spanClass = NoClass;
            if (_classSpans is { } held)
            {
                for (int c = 0; c < held.Length; c++)
                {
                    held[c].Clear();
                }
            }

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

        /// <summary>
        /// Counts <paramref name="value"/>, its keys named by <paramref name="dictionary"/>, <paramref name="times"/> times: the
        /// value of the census's last row, or of its last rows when they repeat it, each count taking them all at once and each
        /// bound the value once, which repeating it moves no further (<see cref="PathCensus.Settle"/>).
        /// </summary>
        /// <param name="census">The census.</param>
        /// <param name="dictionary">The dictionary the value's keys are named by.</param>
        /// <param name="value">The value.</param>
        /// <param name="depth">The depth of the value among the objects it lies in, 1 for a row's.</param>
        /// <param name="times">The rows that hold the value, which follow each other.</param>
        /// <param name="at">The first of the rows that hold the value, when they are not the census's last; -1 for the last.</param>
        internal void Add(PathCensus census, in VariantDictionary dictionary, ReadOnlySpan<byte> value, int depth, long times = 1, long at = -1)
        {
            if (value.IsEmpty)
            {
                throw new VortexFormatException("A variant value holds no byte, not even its header.");
            }

            if (--census._visits < 0)
            {
                throw new VortexFormatException("A variant value names more nodes than its bytes hold: its fields share their bytes.");
            }

            Values += times;

            // At the rows being counted, the census's last ones: one row, or the rows that repeated a value, which follow each
            // other. Or at the row given, of a value counted from what a shredding took of it.
            if (census._gap > 0)
            {
                PathClass spanned = ApartCensus.ClassOf(value);
                if (times == 1)
                {
                    Occur(spanned, at >= 0 ? at : census.Rows - 1, census._gap, census.Account);
                }
                else if (at >= 0)
                {
                    SpanListOf(spanned).Append(at, at + times - 1, census._gap, census.Account);
                }
                else
                {
                    SpanListOf(spanned).Append(census.Rows - times, census.Rows - 1, census._gap, census.Account);
                }
            }

            switch (value[0] & 0x03)
            {
                case ParquetVariant.BasicObject:
                {
                    _classes[ObjectSlot] += times;
                    _read[(int)PathClass.Object] += times;
                    if (depth >= VortexLimits.MaxDTypeDepth / 2)
                    {
                        return;
                    }

                    VariantNested fields = VariantNested.Read(value);
                    _keys ??= new Dictionary<string, PathCount>(StringComparer.Ordinal);

                    // The count of each field id of the metadata the rows of the run share, found by its name once a run:
                    // rows of one source share their metadata, whose names would otherwise be hashed and looked up again
                    // at every row. Kept from a run's second row: a run of one row, interleaved sources' rows each, would
                    // clear and fill the ids for nothing.
                    int kept = 0;
                    if (census._runRows > 1)
                    {
                        if (_byIdEpoch != census._epoch)
                        {
                            _byIdEpoch = census._epoch;
                            _keptIds = census.KeepIds(ref _byId, dictionary.Count);
                        }

                        kept = _keptIds;
                    }

                    for (int i = 0; i < fields.Count; i++)
                    {
                        int id = fields.IdAt(i);
                        PathCount? key = (uint)id < (uint)kept ? _byId[id] : null;
                        if (key is null)
                        {
                            key = Child(census, in dictionary, id);
                            if ((uint)id < (uint)kept)
                            {
                                _byId[id] = key;
                            }
                        }

                        if (ReferenceEquals(key, Uncounted))
                        {
                            OtherKeys += times;
                            census.Partial = true;
                            continue;
                        }

                        key.Add(census, in dictionary, fields.ValueAt(i), depth + 1, times, at);
                    }

                    return;
                }

                case ParquetVariant.BasicArray:
                {
                    _read[(int)PathClass.List] += times;
                    VariantNested elements = VariantNested.Read(value);
                    if (elements.Count == 0)
                    {
                        _classes[EmptyListSlot] += times;
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

                    _classes[shared == OtherSlot ? OtherSlot : ListSlot + shared] += times;
                    return;
                }

                default:
                    Scalar(census.StringBoundBytes, value, times);
                    return;
            }
        }

        // ---------------------------------------------------------------- what a shredding took of the rows, counted
        //
        // A value a shredding typed is counted as its bytes read would be: once at its path, at its row, in its class and in
        // the class a question reads it as, its bounds widened. The rows of one path and one class are given in their order,
        // as rows counted one after the other give them, which the spans keep.

        /// <summary>
        /// The count of the key named <paramref name="utf8"/> under this one, made when the key is new and the census lets it
        /// count one more, as a row's field makes it (<see cref="Child"/>); null when it does not.
        /// </summary>
        internal PathCount? Key(PathCensus census, ReadOnlySpan<byte> utf8)
        {
            _keys ??= new Dictionary<string, PathCount>(StringComparer.Ordinal);
            ReadOnlySpan<char> name = census.Chars(utf8);
            if (!_keys.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out PathCount? key))
            {
                if (census._keyCount >= MaxKeys)
                {
                    return null;
                }

                _keys[new string(name)] = key = new PathCount();
                census._keyCount++;
                census._held++;
            }
            else if (key.Values == 0)
            {
                if (census._keyCount >= MaxKeys)
                {
                    return null;
                }

                census._keyCount++;
            }

            // The keys found by a name's number are another table, which a row counted after these fills again.
            return key;
        }

        /// <summary>
        /// One value of <paramref name="class"/> at each of the <paramref name="times"/> rows from <paramref name="row"/> on,
        /// which follow each other: the path's values, and the spans of its class.
        /// </summary>
        private void Taken(PathCensus census, PathClass @class, long row, long times)
        {
            Values += times;
            if (census._gap <= 0)
            {
                return;
            }

            if (times == 1)
            {
                Occur(@class, row, census._gap, census.Account);
            }
            else
            {
                SpanListOf(@class).Append(row, row + times - 1, census._gap, census.Account);
            }
        }

        /// <summary>Counts an integer a shredding typed at each of the <paramref name="times"/> rows from <paramref name="row"/> on.</summary>
        internal void TakenInteger(PathCensus census, long row, long value, long times)
        {
            Taken(census, PathClass.Integer, row, times);
            Integer(value, times);
        }

        /// <summary>Counts a double a shredding typed at each of the <paramref name="times"/> rows from <paramref name="row"/> on.</summary>
        internal void TakenDouble(PathCensus census, long row, double value, long times)
        {
            Taken(census, PathClass.Double, row, times);
            _classes[(int)VariantClass.Double] += times;
            _read[(int)PathClass.Double] += times;
            if (double.IsNaN(value))
            {
                _nans = true;
                return;
            }

            _minDouble = Math.Min(_minDouble, value);
            _maxDouble = Math.Max(_maxDouble, value);
        }

        /// <summary>Counts a boolean a shredding typed at each of the <paramref name="times"/> rows from <paramref name="row"/> on.</summary>
        internal void TakenBoolean(PathCensus census, long row, bool value, long times)
        {
            Taken(census, PathClass.Boolean, row, times);
            _classes[(int)VariantClass.Boolean] += times;
            _read[(int)PathClass.Boolean] += times;
            _true |= value;
            _false |= !value;
        }

        /// <summary>Counts a text a shredding typed at each of the <paramref name="times"/> rows from <paramref name="row"/> on.</summary>
        internal void TakenString(PathCensus census, long row, ReadOnlySpan<byte> text, long times)
        {
            Taken(census, PathClass.String, row, times);
            String(census.StringBoundBytes, text, times);
        }

        /// <summary>
        /// Counts an object at each of the <paramref name="times"/> rows from <paramref name="row"/> on, its keys counted
        /// apart: a shredding's, each by its own node, and the others' (<see cref="TakenFields"/>).
        /// </summary>
        internal void TakenObject(PathCensus census, long row, long times)
        {
            Taken(census, PathClass.Object, row, times);
            _classes[ObjectSlot] += times;
            _read[(int)PathClass.Object] += times;
        }

        /// <summary>
        /// Counts a list of <paramref name="elements"/> elements all of <paramref name="class"/>, which a shredding typed at
        /// each of the <paramref name="times"/> rows from <paramref name="row"/> on.
        /// </summary>
        internal void TakenList(PathCensus census, long row, int elements, VariantClass @class, long times)
        {
            Taken(census, PathClass.List, row, times);
            _read[(int)PathClass.List] += times;
            _classes[elements == 0 ? EmptyListSlot : ListSlot + (int)@class] += times;
        }

        /// <summary>
        /// Counts <paramref name="value"/>, a value a shredding kept whole at each of the <paramref name="times"/> rows from
        /// <paramref name="row"/> on, as a row's value is counted: its class, and an object's keys below it, named by
        /// <paramref name="dictionary"/>.
        /// </summary>
        internal void TakenValue(PathCensus census, long row, in VariantDictionary dictionary, ReadOnlySpan<byte> value, int depth, long times)
        {
            census._visits = value.Length;
            Add(census, in dictionary, value, depth, times, row);
        }

        /// <summary>
        /// Counts the keys of <paramref name="kept"/>, the object of the keys a shredding left of the object at
        /// <paramref name="row"/>, which <see cref="TakenObject"/> counted: each as a field of a row's object is counted.
        /// </summary>
        internal void TakenFields(PathCensus census, long row, in VariantDictionary dictionary, ReadOnlySpan<byte> kept, int depth)
        {
            if (depth >= VortexLimits.MaxDTypeDepth / 2)
            {
                return;
            }

            census._visits = kept.Length;

            VariantNested fields = VariantNested.Read(kept);
            for (int i = 0; i < fields.Count; i++)
            {
                PathCount key = Child(census, in dictionary, fields.IdAt(i));
                if (ReferenceEquals(key, Uncounted))
                {
                    OtherKeys++;
                    census.Partial = true;
                    continue;
                }

                key.Add(census, in dictionary, fields.ValueAt(i), depth + 1, 1, row);
            }
        }

        /// <summary>
        /// Counts the scalar <paramref name="value"/> in its class and in the class a question reads it as, and widens that
        /// class's bounds: the texts, integers, doubles, booleans and nulls of log records read from their header as they lie,
        /// any other through <see cref="VariantScalar"/>, which a value of these made half of what the census cost a value.
        /// </summary>
        private void Scalar(int stringBoundBytes, ReadOnlySpan<byte> value, long times)
        {
            byte header = value[0];
            if ((header & 0x03) == ParquetVariant.BasicShortString)
            {
                if (value.Length > header >> 2)
                {
                    String(stringBoundBytes, value.Slice(1, header >> 2), times);
                    return;
                }
            }
            else if (!Short(header >> 2, value))
            {
                Typed(header, stringBoundBytes, value, times);
                return;
            }

            // Cut short, or of another type: read whole, which says which.
            VariantScalar scalar = VariantScalar.Read(value);
            _classes[SlotOf(scalar.Kind)] += times;
            Bound(stringBoundBytes, scalar, times);

            // Whether a primitive of type id `type` read as it lies holds fewer bytes than it takes, or is not one read so.
            static bool Short(int type, ReadOnlySpan<byte> value) => type switch
            {
                0 or 1 or 2 => false,
                3 => value.Length < 2,
                4 => value.Length < 3,
                5 => value.Length < 5,
                6 or 7 => value.Length < 9,
                16 => value.Length < 5 || (uint)(value.Length - 5) < System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(value[1..]),
                _ => true,
            };
        }

        /// <summary>A primitive of the types <see cref="Scalar"/> reads as it lies, whose bytes are all there, counted.</summary>
        private void Typed(byte header, int stringBoundBytes, ReadOnlySpan<byte> value, long times)
        {
            switch (header >> 2)
            {
                case 0:
                    _classes[OtherSlot] += times;
                    _read[(int)PathClass.Null] += times;
                    return;
                case 1 or 2:
                    _classes[(int)VariantClass.Boolean] += times;
                    _read[(int)PathClass.Boolean] += times;
                    _true |= header >> 2 == 1;
                    _false |= header >> 2 == 2;
                    return;
                case 3:
                    Integer((sbyte)value[1], times);
                    return;
                case 4:
                    Integer(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(value[1..]), times);
                    return;
                case 5:
                    Integer(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value[1..]), times);
                    return;
                case 6:
                    Integer(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(value[1..]), times);
                    return;
                case 7:
                {
                    _classes[(int)VariantClass.Double] += times;
                    _read[(int)PathClass.Double] += times;
                    double number = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(value[1..]);
                    if (double.IsNaN(number))
                    {
                        _nans = true;
                        return;
                    }

                    _minDouble = Math.Min(_minDouble, number);
                    _maxDouble = Math.Max(_maxDouble, number);
                    return;
                }

                default:
                    String(stringBoundBytes, value.Slice(5, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(value[1..])), times);
                    return;
            }
        }

        /// <summary>Counts an integer of any width, and widens the integers' bounds.</summary>
        private void Integer(long integer, long times)
        {
            _classes[(int)VariantClass.Integer] += times;
            _read[(int)PathClass.Integer] += times;
            _minInteger = Math.Min(_minInteger, integer);
            _maxInteger = Math.Max(_maxInteger, integer);
        }

        /// <summary>Counts a text, and widens the texts' bounds by its bytes when the census keeps them.</summary>
        private void String(int stringBoundBytes, ReadOnlySpan<byte> text, long times)
        {
            _classes[(int)VariantClass.String] += times;
            _read[(int)PathClass.String] += times;
            if (stringBoundBytes > 0)
            {
                Text(text, stringBoundBytes);
            }
        }

        /// <summary>
        /// The count of the key named by field id <paramref name="id"/> of the run's metadata, made when the key is new and the
        /// census lets it count one more, <see cref="Uncounted"/> when it does not.
        /// </summary>
        private PathCount Child(PathCensus census, in VariantDictionary dictionary, int id)
        {
            // Found by the name's number: rows whose metadata is not the row's before, log records of interleaved sources whose
            // keys differ row by row, decoded and looked up every key of every row as characters.
            int number = census.NameOf(in dictionary, id);
            if (_byNameGeneration != census._generation)
            {
                _byName?.Clear();
                _byNameGeneration = census._generation;
            }

            _byName ??= [];
            ref PathCount? found = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_byName, number, out bool exists);
            if (exists)
            {
                return found!;
            }

            // Found by the name's characters, decoded into the census's scratch: a name met for the first time takes a string.
            _keys ??= new Dictionary<string, PathCount>(StringComparer.Ordinal);
            ReadOnlySpan<char> name = census.Chars(dictionary[id]);
            if (!_keys.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out PathCount? key))
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
                // A key the tree kept from rows counted before a Clear, or a merge: new to these rows, met for the first time.
                if (census._keyCount >= MaxKeys)
                {
                    key = Uncounted;
                }
                else
                {
                    census._keyCount++;
                }
            }

            found = key;
            return key;
        }

        /// <summary>
        /// The count of the key named <paramref name="utf8"/> under this one, for a directory's entry of <paramref name="values"/>
        /// values (<see cref="AddPaths"/>): made when the key is new and the census lets it count one more, as
        /// <see cref="Merge"/> makes it; null past <see cref="MaxKeys"/>, the entry's values then its objects' other keys when
        /// the key is the entry's own, <paramref name="last"/>, as a count of the rows puts them.
        /// </summary>
        internal PathCount? Named(PathCensus census, ReadOnlySpan<byte> utf8, long values, bool last)
        {
            _keys ??= new Dictionary<string, PathCount>(StringComparer.Ordinal);
            ReadOnlySpan<char> name = census.Chars(utf8);
            bool found = _keys.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out PathCount? mine);
            if (!found || mine!.Values == 0)
            {
                if (census._keyCount >= MaxKeys)
                {
                    OtherKeys += last ? values : 0;
                    census.Partial = true;
                    return null;
                }

                if (!found)
                {
                    _keys[new string(name)] = mine = new PathCount();
                    census._held++;
                }

                census._keyCount++;
            }

            return mine;
        }

        /// <summary>
        /// Counts <paramref name="values"/> values a question reads as <paramref name="class"/>, as a count of their rows counts
        /// them by class: a directory's entry, whose bounds and spans a first pass needs none of (<see cref="AddPaths"/>).
        /// </summary>
        internal void Occurred(PathClass @class, long values)
        {
            Values += values;
            _read[(int)@class] += values;
            _classes[@class switch
            {
                PathClass.Boolean => (int)VariantClass.Boolean,
                PathClass.Integer => (int)VariantClass.Integer,
                PathClass.Double => (int)VariantClass.Double,
                PathClass.String => (int)VariantClass.String,
                PathClass.Object => ObjectSlot,
                _ => OtherSlot,
            }] += values;
        }

        /// <summary>
        /// Adds what <paramref name="other"/> counted at the same path of another census to these counts, in <paramref name="census"/>
        /// (<see cref="PathCensus.Merge"/>), its rows <paramref name="offset"/> rows past these.
        /// </summary>
        internal void Merge(PathCensus census, PathCount other, long offset)
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

            if (census._gap > 0)
            {
                if (other._spanClass != NoClass)
                {
                    Spans(other._spanClass, other._spans.View(), offset, census._gap, census.Account);
                }

                for (int @class = 0; other._classSpans is { } others && @class < others.Length; @class++)
                {
                    Spans((PathClass)@class, others[@class].View(), offset, census._gap, census.Account);
                }
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

                mine!.Merge(census, count, offset);
            }
        }

        /// <summary>Widens the text bounds by those of <paramref name="other"/>, already cut to <paramref name="bound"/> bytes.</summary>
        private void Text(PathCount other, int bound)
        {
            _prefixed = false;
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
        private void Bound(int stringBoundBytes, VariantScalar scalar, long times)
        {
            switch (scalar.Kind)
            {
                case VariantKind.Null:
                    _read[(int)PathClass.Null] += times;
                    return;
                case VariantKind.Bool:
                    _read[(int)PathClass.Boolean] += times;
                    _true |= scalar.Integer != 0;
                    _false |= scalar.Integer == 0;
                    return;
                case VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64:
                {
                    _read[(int)PathClass.Integer] += times;
                    long integer = (long)scalar.Integer;
                    _minInteger = Math.Min(_minInteger, integer);
                    _maxInteger = Math.Max(_maxInteger, integer);
                    return;
                }

                case VariantKind.Float or VariantKind.Double:
                {
                    _read[(int)PathClass.Double] += times;
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
                    _read[(int)PathClass.String] += times;
                    if (stringBoundBytes > 0)
                    {
                        Text(scalar.Bytes, stringBoundBytes);
                    }

                    return;
                case VariantKind.Timestamp:
                {
                    _read[(int)PathClass.Timestamp] += times;
                    long micros = scalar.Microseconds;
                    _minTime = Math.Min(_minTime, micros);
                    _maxTime = Math.Max(_maxTime, micros);
                    return;
                }

                default:
                    _read[(int)PathClass.Other] += times;
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

        /// <summary>The first eight bytes of <paramref name="text"/> as a big-endian number, zero past its end.</summary>
        private static ulong Prefix(ReadOnlySpan<byte> text)
        {
            if (text.Length >= sizeof(ulong))
            {
                return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(text);
            }

            ulong prefix = 0;
            for (int i = 0; i < text.Length; i++)
            {
                prefix |= (ulong)text[i] << (56 - (8 * i));
            }

            return prefix;
        }

        /// <summary>Widens the text bounds by <paramref name="text"/>, cut to <paramref name="bound"/> bytes.</summary>
        private void Text(ReadOnlySpan<byte> text, int bound)
        {
            ReadOnlySpan<byte> cut = text[..Math.Min(text.Length, bound)];
            bool longer = text.Length > bound;
            ulong prefix = Prefix(cut);
            if (_minTextLength < 0)
            {
                Bounds(bound);
                cut.CopyTo(_minText);
                cut.CopyTo(_maxText);
                _minTextLength = _maxTextLength = cut.Length;
                _maxTextCut = longer;
                _minPrefix = _maxPrefix = prefix;
                _prefixed = true;
                return;
            }

            if (!_prefixed)
            {
                _minPrefix = Prefix(MinText);
                _maxPrefix = Prefix(MaxText);
                _prefixed = true;
            }

            // Above the least and below the greatest by its first eight bytes, which order texts as their bytes do when they
            // differ: it moves neither.
            if (prefix > _minPrefix && prefix < _maxPrefix)
            {
                return;
            }

            if (cut.SequenceCompareTo(_minText.AsSpan(0, _minTextLength)) < 0)
            {
                cut.CopyTo(_minText);
                _minTextLength = cut.Length;
                _minPrefix = prefix;
            }

            int order = cut.SequenceCompareTo(_maxText.AsSpan(0, _maxTextLength));
            if (order > 0)
            {
                cut.CopyTo(_maxText);
                _maxTextLength = cut.Length;
                _maxTextCut = longer;
                _maxPrefix = prefix;
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
