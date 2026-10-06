using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>Row ranges of one batch that each belong to a single group, for the aggregates to fold a range at a time.</summary>
internal sealed class GroupRanges
{
    private int[] _starts = new int[16];
    private int[] _ends = new int[16];
    private int[] _groups = new int[16];

    internal int Count { get; private set; }

    /// <summary>The ranges' first rows, ascending: a range starts where the one before it ends, or past it.</summary>
    internal ReadOnlySpan<int> Starts => _starts.AsSpan(0, Count);

    /// <summary>The rows past the ranges.</summary>
    internal ReadOnlySpan<int> Ends => _ends.AsSpan(0, Count);

    /// <summary>The ranges' groups.</summary>
    internal ReadOnlySpan<int> Groups => _groups.AsSpan(0, Count);

    internal int StartAt(int index) => _starts[index];

    internal int EndAt(int index) => _ends[index];

    internal int GroupAt(int index) => _groups[index];

    internal void Clear() => Count = 0;

    internal void Add(int start, int end, int group)
    {
        if (Count > 0 && _groups[Count - 1] == group && _ends[Count - 1] == start)
        {
            _ends[Count - 1] = end;
            return;
        }

        if (Count == _starts.Length)
        {
            int grown = Count * 2;
            Array.Resize(ref _starts, grown);
            Array.Resize(ref _ends, grown);
            Array.Resize(ref _groups, grown);
        }

        _starts[Count] = start;
        _ends[Count] = end;
        _groups[Count] = group;
        Count++;
    }
}

/// <summary>
/// The groups of one partition of a grouped scan: the distinct keys numbered in the order they are
/// first seen, and how each batch's rows map to them.
/// </summary>
internal abstract class GroupKeys
{
    private long _dictionaryBlocks;
    private long _otherBlocks;
    private int[] _codeGroups = [];
    private CanonicalOrigin _codeOrigin;

    internal int Count { get; private protected set; }

    /// <summary>Whether every key block was dictionary-encoded, which makes the key source ordered.</summary>
    internal bool OnlyDictionaries => _dictionaryBlocks > 0 && _otherBlocks == 0;

    /// <summary>
    /// Maps the selected rows of a batch to groups, creating the groups of keys not seen before: as
    /// ranges of one group each when the key's form yields them, a group per row otherwise.
    /// </summary>
    /// <returns>True when <paramref name="ranges"/> holds the answer, false when <paramref name="rowGroups"/> does.</returns>
    internal abstract bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges);

    /// <summary>An empty index of the same kind, for another partition.</summary>
    internal abstract GroupKeys Fresh();

    /// <summary>The bytes the index holds, its tables and its keys at their capacity (PLAN-HIGH-CARDINALITY, H1).</summary>
    internal abstract long Footprint { get; }

    /// <summary>Adds this partition's keys to <paramref name="target"/>; group <c>g</c> here is <c>map[g]</c> there.</summary>
    internal void MergeInto(GroupKeys target, Span<int> map) => MergeInto(target, Numbers.Upto(Count), map);

    /// <summary>
    /// Adds the keys of <paramref name="groups"/> to <paramref name="target"/>; group
    /// <c>groups[i]</c> here is <c>map[i]</c> there: the part of a partition a task of a parallel
    /// merge takes.
    /// </summary>
    internal abstract void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map);

    /// <summary>
    /// The part of a parallel merge each group's key falls in: the top bits, past
    /// <paramref name="shift"/>, of a hash of the key under <paramref name="seed"/>, the same for one
    /// key in every partition of a query whatever its group there; the first part for the null group.
    /// </summary>
    internal abstract void Parts(ulong seed, int shift, Span<byte> parts);

    /// <summary>An empty index a part of a parallel merge is merged into: <see cref="Fresh"/>, or one sharing what the partitions were rebased on.</summary>
    internal virtual GroupKeys ForPart() => Fresh();

    /// <summary>Makes room for <paramref name="groups"/> groups at once, where the index can: the part of a merge, whose keys the partitions count.</summary>
    internal virtual void Reserve(int groups)
    {
    }

    /// <summary>
    /// Readies the indexes of a parallel merge's partitions, this one the first of them, for their
    /// hashes and their merge in parts: what they share is merged once, a composite's indexes of its
    /// columns, so that a key has the same words in every partition. Nothing for a key of one column.
    /// </summary>
    internal virtual Task RebaseAsync(GroupKeys[] partitions, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Group <paramref name="a"/> here and group <paramref name="b"/> of <paramref name="other"/>, an
    /// index of the same kind and the same merge, by component <paramref name="component"/> of their
    /// keys, as <see cref="CompareKeys(int, int, int)"/> orders them: what orders the parts of a
    /// parallel merge read as one.
    /// </summary>
    /// <exception cref="NotSupportedException">The keys are not ordered here (<see cref="Orders"/>).</exception>
    internal virtual int CompareKeys(GroupKeys other, int a, int b, int component) =>
        throw new NotSupportedException("These keys are ordered by the column of their values.");

    /// <summary>The groups in the order they are delivered: by key, nulls last, when <paramref name="sorted"/>; as first seen otherwise.</summary>
    internal abstract int[] Order(bool sorted);

    /// <summary>Reads component <paramref name="component"/> of a group's key as <typeparamref name="T"/>.</summary>
    internal abstract Func<int, T> Reader<T>(int component);

    /// <summary>
    /// Appends component <paramref name="component"/> of the keys of <paramref name="groups"/>, in
    /// order, to a store of the key column's own type: the values as the index holds them, a null
    /// group as a null.
    /// </summary>
    internal abstract void Append(int component, ColumnStore store, ReadOnlySpan<int> groups);

    /// <summary>
    /// Keeps the keys of <paramref name="groups"/> alone, group <c>groups[i]</c> becoming group
    /// <c>i</c>: the groups a streaming group by has not closed. <paramref name="groups"/> ascend.
    /// </summary>
    internal abstract void Keep(ReadOnlySpan<int> groups);

    /// <summary>
    /// The rows of <paramref name="selection"/> whose key does not lie past the key of group
    /// <paramref name="frontier"/> in an order on it, into <paramref name="narrowed"/>, or false when
    /// these keys do not tell: the rows a top that holds its best groups need not group. A null lies
    /// past every value, and the frontier's own key is kept.
    /// </summary>
    internal virtual bool Narrow(
        CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int frontier, bool descending, Span<ulong> narrowed) => false;

    /// <summary>
    /// Whether group <paramref name="group"/>'s key, an integer, lies below <paramref name="bound"/>:
    /// the groups a floor the zones give proves final. False for the null group, and for keys these
    /// do not read as integers.
    /// </summary>
    internal virtual bool Below(int group, long bound) => false;

    /// <summary>Sorts <paramref name="groups"/>, the null group not among them, by their keys, compared as <see cref="CompareKeys(GroupKeys, int, int, int)"/> compares them.</summary>
    internal virtual void SortByKey(Span<int> groups) => groups.Sort(new KeyComparer(this));

    /// <summary>Groups of one index by their keys, a call to the index a comparison.</summary>
    private sealed class KeyComparer(GroupKeys keys) : IComparer<int>
    {
        public int Compare(int x, int y) => keys.CompareKeys(keys, x, y, 0);
    }

    /// <summary>The group of the null key, or -1 when there is none: of a key of one column.</summary>
    internal virtual int NullNumber => -1;

    /// <summary>
    /// Whether <see cref="CompareKeys(int, int, int)"/> orders the groups by component
    /// <paramref name="component"/> of their keys as the column of those keys would, so that an order
    /// breaks its ties without the keys built: an integer's, a decimal's, a text's, a boolean's, a
    /// composite's part of one of those; not a float's, whose NaN and zeros the column orders apart
    /// from their values' comparison.
    /// </summary>
    internal virtual bool Orders(int component) => false;

    /// <summary>Two groups by component <paramref name="component"/> of their keys, ascending, the null group last.</summary>
    /// <exception cref="NotSupportedException">The keys are not ordered here (<see cref="Orders"/>).</exception>
    internal virtual int CompareKeys(int a, int b, int component) =>
        throw new NotSupportedException("These keys are ordered by the column of their values.");

    /// <summary>Forgets the groups the codes of the last dictionary were given: they were numbered again.</summary>
    private protected void Renumbered() => _codeOrigin = default;

    private protected void Saw(ColumnEncoding encoding)
    {
        if (encoding == ColumnEncoding.Dictionary)
        {
            _dictionaryBlocks++;
        }
        else
        {
            _otherBlocks++;
        }
    }

    private protected void MergeSeen(GroupKeys target)
    {
        target._dictionaryBlocks += _dictionaryBlocks;
        target._otherBlocks += _otherBlocks;
    }

    /// <summary>
    /// The group of each code of a dictionary whose values are node <paramref name="values"/>, -1 for
    /// a code not met yet: the table of the batch before when both view the same retained values,
    /// since a key keeps its group, and a cleared one otherwise.
    /// </summary>
    /// <param name="arena">The batch's arena.</param>
    /// <param name="values">The dictionary's values node.</param>
    /// <param name="entries">How many values the dictionary has.</param>
    private protected Span<int> CodeGroups(CanonicalArena arena, int values, int entries)
    {
        CanonicalOrigin origin = arena.OriginOf(values);
        if (!origin.IsKnown || origin != _codeOrigin)
        {
            Scratch.Grow(ref _codeGroups, entries);
            _codeGroups.AsSpan(0, entries).Fill(-1);
            _codeOrigin = origin;
        }

        return _codeGroups.AsSpan(0, entries);
    }

    private protected static int[] Identity(int count)
    {
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        return order;
    }
}

/// <summary>The smallest and the largest value of an integer key, as the file statistics hold them exactly.</summary>
internal readonly record struct KeyBounds(long Min, long Max);

/// <summary>
/// The hash a parallel merge cuts the keys by: a mix of 64 bits under a seed drawn once a merge, the
/// same in every partition, apart from each table's own hash, which a table may draw again alone:
/// a key hashed by its table could fall in two parts. The default hash of an integer is its value,
/// whose high bits cut nothing.
/// </summary>
internal static class MergeHash
{
    /// <summary>
    /// A key of one word: one round of the mix, a bijection whose every input bit reaches every bit
    /// of the hash, where the key of two words takes two (PLAN-HIGH-CARDINALITY, H15).
    /// </summary>
    internal static ulong Of(ulong word, ulong seed) => Mix(word ^ seed);

    /// <summary>A key of two words, every bit of each reaching every bit of the hash.</summary>
    internal static ulong Of(ulong low, ulong high, ulong seed) => Mix(Mix(low ^ seed) ^ high);

    /// <summary>A key of bytes.</summary>
    internal static ulong Of(ReadOnlySpan<byte> bytes, ulong seed) => System.IO.Hashing.XxHash3.HashToUInt64(bytes, unchecked((long)seed));

    private static ulong Mix(ulong x)
    {
        x ^= x >> 33;
        x *= 0xFF51_AFD7_ED55_8CCDUL;
        x ^= x >> 33;
        x *= 0xC4CE_B9FE_1A85_EC53UL;
        return x ^ (x >> 33);
    }
}

/// <summary>
/// A key of one fixed-width column: a hash map from the storage value to its group, and a group for
/// null. An integer key the statistics bound to <see cref="DirectValues"/> values has a table from
/// the value to its group in front of the map, so that a value is hashed once a partition.
/// </summary>
internal sealed class FixedKeys<TValue> : GroupKeys
    where TValue : unmanaged, IEquatable<TValue>, IComparable<TValue>
{
    /// <summary>The most values a table of groups covers: 2^16, a quarter of a megabyte.</summary>
    internal const long DirectValues = 1 << 16;

    private readonly ColumnShape _shape;
    private readonly bool _sorted;
    private readonly KeyBounds? _bounds;
    private GroupIndex<TValue> _index = new GroupIndex<TValue>();
    private TValue[] _keys = new TValue[16];
    private int _null = -1;
    private TValue[] _values = [];
    private ValuesCache<TValue> _entries;

    // The group of each value from the statistics' smallest, -1 for a value not met yet: what the
    // index would answer, read without a hash.
    private readonly int[]? _direct;
    private readonly long _directMin;

    internal FixedKeys(ColumnShape shape, bool sorted, KeyBounds? bounds = null)
    {
        _shape = shape;
        _sorted = sorted;
        _bounds = bounds;
        if (Integers && bounds is { } known && known.Max >= known.Min && (ulong)(known.Max - known.Min) < DirectValues)
        {
            _direct = new int[(int)(known.Max - known.Min + 1)];
            _direct.AsSpan().Fill(-1);
            _directMin = known.Min;
        }
    }

    /// <summary>Whether the values are integers, which a table of groups can be indexed by.</summary>
    private static readonly bool Integers =
        typeof(TValue) == typeof(sbyte) || typeof(TValue) == typeof(short) || typeof(TValue) == typeof(int) || typeof(TValue) == typeof(long)
        || typeof(TValue) == typeof(byte) || typeof(TValue) == typeof(ushort) || typeof(TValue) == typeof(uint) || typeof(TValue) == typeof(ulong);

    /// <summary>
    /// Integers within a span of four times as many as there are groups placed at their value less the
    /// least, one key a group, and read back in order; any other keys copied beside the groups and
    /// sorted with them, by the values' own order: no call to an index a comparison.
    /// </summary>
    internal override void SortByKey(Span<int> groups)
    {
        if (Integers && typeof(TValue) != typeof(ulong) && TryPlace(groups))
        {
            return;
        }

        TValue[] keys = System.Buffers.ArrayPool<TValue>.Shared.Rent(groups.Length);
        try
        {
            Span<TValue> values = keys.AsSpan(0, groups.Length);
            for (int i = 0; i < groups.Length; i++)
            {
                values[i] = _keys[groups[i]];
            }

            values.Sort(groups);
        }
        finally
        {
            System.Buffers.ArrayPool<TValue>.Shared.Return(keys);
        }
    }

    /// <summary>The groups placed by their key less the least, when the keys' span is narrow enough to pay; false otherwise.</summary>
    private bool TryPlace(Span<int> groups)
    {
        long least = long.MaxValue;
        long most = long.MinValue;
        foreach (int group in groups)
        {
            long key = AsLong(_keys[group]);
            least = Math.Min(least, key);
            most = Math.Max(most, key);
        }

        if ((ulong)(most - least) >= 4UL * (ulong)groups.Length)
        {
            return false;
        }

        int span = (int)(most - least) + 1;
        int[] places = System.Buffers.ArrayPool<int>.Shared.Rent(span);
        try
        {
            Span<int> at = places.AsSpan(0, span);
            at.Fill(-1);
            foreach (int group in groups)
            {
                at[(int)(AsLong(_keys[group]) - least)] = group;
            }

            int next = 0;
            foreach (int group in at)
            {
                if (group >= 0)
                {
                    groups[next++] = group;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(places);
        }

        return true;
    }

    /// <summary>An integer key of any type but <see cref="ulong"/> as a long, which holds every one of them.</summary>
    private static long AsLong(TValue key) =>
        typeof(TValue) == typeof(long) ? Unsafe.As<TValue, long>(ref key)
        : typeof(TValue) == typeof(int) ? Unsafe.As<TValue, int>(ref key)
        : typeof(TValue) == typeof(short) ? Unsafe.As<TValue, short>(ref key)
        : typeof(TValue) == typeof(sbyte) ? Unsafe.As<TValue, sbyte>(ref key)
        : typeof(TValue) == typeof(uint) ? Unsafe.As<TValue, uint>(ref key)
        : typeof(TValue) == typeof(ushort) ? Unsafe.As<TValue, ushort>(ref key)
        : Unsafe.As<TValue, byte>(ref key);

    internal override bool Below(int group, long bound)
    {
        if (group == _null)
        {
            return false;
        }

        TValue key = _keys[group];
        return typeof(TValue) == typeof(long) ? Unsafe.As<TValue, long>(ref key) < bound
            : typeof(TValue) == typeof(int) ? Unsafe.As<TValue, int>(ref key) < bound
            : typeof(TValue) == typeof(short) ? Unsafe.As<TValue, short>(ref key) < bound
            : typeof(TValue) == typeof(sbyte) ? Unsafe.As<TValue, sbyte>(ref key) < bound
            : typeof(TValue) == typeof(uint) ? Unsafe.As<TValue, uint>(ref key) < bound
            : typeof(TValue) == typeof(ushort) ? Unsafe.As<TValue, ushort>(ref key) < bound
            : typeof(TValue) == typeof(byte) && Unsafe.As<TValue, byte>(ref key) < bound;
    }

    /// <summary>
    /// A column of integers read as it is, whose order is their values': the encoded forms, which
    /// group per run or per code, and the floats, whose NaN and zeros an order places apart from
    /// their values, are left whole.
    /// </summary>
    internal override bool Narrow(
        CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int frontier, bool descending, Span<ulong> narrowed)
    {
        int node = nodes[0];
        StorageKind kind = _shape.Kind;
        if (!Integers || frontier == _null || FixedReader.EncodingOf(arena, node, kind) != ColumnEncoding.Canonical)
        {
            return false;
        }

        ReadOnlySpan<TValue> values = FixedReader.Values(arena, node, kind, ref _values, out ReadOnlySpan<ulong> validity);
        TValue edge = _keys[frontier];
        int words = (rows + 63) >> 6;
        for (int w = 0; w < words; w++)
        {
            ulong word = selection.IsEmpty ? (w < words - 1 || (rows & 63) == 0 ? ulong.MaxValue : (1UL << (rows & 63)) - 1) : selection[w];
            if (!validity.IsEmpty)
            {
                word &= validity[w];
            }

            ulong kept = 0;
            while (word != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(word);
                int order = values[(w << 6) + bit].CompareTo(edge);
                kept |= (descending ? order >= 0 : order <= 0) ? 1UL << bit : 0;
                word &= word - 1;
            }

            narrowed[w] = kept;
        }

        return true;
    }

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        int node = nodes[0];
        StorageKind kind = _shape.Kind;
        ColumnEncoding encoding = FixedReader.EncodingOf(arena, node, kind);
        Saw(encoding);
        switch (encoding)
        {
            case ColumnEncoding.Constant:
            {
                ValidityKind uniform = arena.RecordRef(node).Validity.Kind;
                if (uniform is ValidityKind.NonNullable or ValidityKind.AllValid or ValidityKind.AllInvalid)
                {
                    ranges.Add(0, rows, uniform == ValidityKind.AllInvalid ? NullGroup() : Lookup(FixedReader.Constant<TValue>(arena, node, kind)));
                    return true;
                }

                break;
            }

            case ColumnEncoding.RunEnd:
            {
                if (ArenaWords.NullCount(arena, node) > 0)
                {
                    break;
                }

                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                ReadOnlySpan<TValue> values = FixedReader.Values(arena, runs, kind, ref _values, out ReadOnlySpan<ulong> valid);
                int runStart = 0;
                for (int r = 0; r < ends.Length && runStart < rows; r++)
                {
                    int runEnd = Math.Min((int)ends[r], rows);
                    if (RowMasks.Count(selection, runStart, runEnd) > 0)
                    {
                        ranges.Add(runStart, runEnd, StorageValues.IsValid(valid, r) ? Lookup(values[r]) : NullGroup());
                    }

                    runStart = runEnd;
                }

                return true;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                ReadOnlySpan<TValue> dictionary = _entries.Of(arena, 0, entries, kind, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> present = ArenaWords.Validity(arena, node);
                Span<int> codeGroups = CodeGroups(arena, entries, dictionary.Length);
                RowCursor cursor = new RowCursor(selection, 0, rows);
                while (cursor.Next(out int row))
                {
                    if (!StorageValues.IsValid(present, row))
                    {
                        rowGroups[row] = NullGroup();
                        continue;
                    }

                    int code = (int)codes[row];
                    int group = codeGroups[code];
                    if (group < 0)
                    {
                        group = codeGroups[code] = StorageValues.IsValid(valid, code) ? Lookup(dictionary[code]) : NullGroup();
                    }

                    rowGroups[row] = group;
                }

                return false;
            }

            default:
                break;
        }

        ReadOnlySpan<TValue> canonical = FixedReader.Values(arena, node, kind, ref _values, out ReadOnlySpan<ulong> validity);
        if (_sorted)
        {
            Runs(canonical, validity, rows, selection, ranges);
            return true;
        }

        if (_direct is not null)
        {
            Direct(canonical, validity, rows, selection, rowGroups);
            return false;
        }

        RowCursor selected = new RowCursor(selection, 0, rows);
        bool hasLast = false;
        TValue last = default;
        int lastGroup = -1;
        while (selected.Next(out int row))
        {
            if (!StorageValues.IsValid(validity, row))
            {
                rowGroups[row] = NullGroup();
                continue;
            }

            TValue value = canonical[row];
            if (!hasLast || !value.Equals(last))
            {
                last = value;
                lastGroup = Lookup(value);
                hasLast = true;
            }

            rowGroups[row] = lastGroup;
        }

        return false;
    }

    /// <summary>
    /// Each selected row's group read from the table of groups, a value not met yet looked up in
    /// the index once; a value past the statistics' bounds, which exact statistics never leave, in
    /// the index alone.
    /// </summary>
    private void Direct(ReadOnlySpan<TValue> canonical, ReadOnlySpan<ulong> validity, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups)
    {
        int[] direct = _direct!;
        long min = _directMin;
        RowCursor selected = new RowCursor(selection, 0, rows);
        while (selected.Next(out int row))
        {
            if (!StorageValues.IsValid(validity, row))
            {
                rowGroups[row] = NullGroup();
                continue;
            }

            TValue value = canonical[row];
            ulong slot = (ulong)(Integer(value) - min);
            if (slot >= (ulong)direct.Length)
            {
                rowGroups[row] = Lookup(value);
                continue;
            }

            int group = direct[(int)slot];
            if (group < 0)
            {
                group = direct[(int)slot] = Lookup(value);
            }

            rowGroups[row] = group;
        }
    }

    /// <summary>An integer value as a long; an unsigned one past the longs as a negative, which no table holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Integer(TValue value) =>
        typeof(TValue) == typeof(sbyte) ? Unsafe.BitCast<TValue, sbyte>(value)
        : typeof(TValue) == typeof(short) ? Unsafe.BitCast<TValue, short>(value)
        : typeof(TValue) == typeof(int) ? Unsafe.BitCast<TValue, int>(value)
        : typeof(TValue) == typeof(long) ? Unsafe.BitCast<TValue, long>(value)
        : typeof(TValue) == typeof(byte) ? Unsafe.BitCast<TValue, byte>(value)
        : typeof(TValue) == typeof(ushort) ? Unsafe.BitCast<TValue, ushort>(value)
        : typeof(TValue) == typeof(uint) ? Unsafe.BitCast<TValue, uint>(value)
        : (long)Unsafe.BitCast<TValue, ulong>(value);

    /// <summary>Where the table of groups holds <paramref name="value"/>'s group; false past its bounds.</summary>
    private bool DirectSlot(TValue value, out int slot)
    {
        ulong at = (ulong)(Integer(value) - _directMin);
        slot = (int)at;
        return at < (ulong)_direct!.Length;
    }

    internal override GroupKeys Fresh() => new FixedKeys<TValue>(_shape, _sorted, _bounds);

    /// <summary>The index, the keys of the groups, the table of small integers and the values a batch reads.</summary>
    internal override long Footprint =>
        _index.Footprint + ((long)(_keys.Length + _values.Length) * Unsafe.SizeOf<TValue>()) + ((long)(_direct?.Length ?? 0) * sizeof(int));

    /// <summary>A part of a merge is merged into, never assigned rows: no table of groups.</summary>
    internal override GroupKeys ForPart() => new FixedKeys<TValue>(_shape, _sorted);

    internal override void Reserve(int groups)
    {
        _index.Reserve(groups);
        if (_keys.Length < groups)
        {
            Array.Resize(ref _keys, groups);
        }
    }

    internal override int NullNumber => _null;

    /// <summary>A float orders its NaN last and its zeros together, which its comparison does not.</summary>
    private static readonly bool Floats = typeof(TValue) == typeof(double) || typeof(TValue) == typeof(float) || typeof(TValue) == typeof(Half);

    internal override bool Orders(int component) => !Floats;

    internal override int CompareKeys(int a, int b, int component)
    {
        if (a == _null || b == _null)
        {
            return a == b ? 0 : a == _null ? 1 : -1;
        }

        return _keys[a].CompareTo(_keys[b]);
    }

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        // The table of groups forgets every group's number, then learns the kept ones' new ones.
        if (_direct is int[] direct)
        {
            for (int g = 0; g < Count; g++)
            {
                if (g != _null && DirectSlot(_keys[g], out int slot))
                {
                    direct[slot] = -1;
                }
            }
        }

        int nullGroup = -1;
        for (int i = 0; i < groups.Length; i++)
        {
            nullGroup = groups[i] == _null ? i : nullGroup;
            _keys[i] = _keys[groups[i]];
        }

        _index.Clear();
        for (int i = 0; i < groups.Length; i++)
        {
            if (i != nullGroup)
            {
                _index.Slot(_keys[i], out _) = i;
                if (_direct is not null && DirectSlot(_keys[i], out int slot))
                {
                    _direct[slot] = i;
                }
            }
        }

        _null = nullGroup;
        Count = groups.Length;
        Renumbered();
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        FixedKeys<TValue> into = (FixedKeys<TValue>)target;
        for (int i = 0; i < groups.Length; i++)
        {
            int g = groups[i];
            map[i] = g == _null ? into.NullGroup() : into.Lookup(_keys[g]);
        }

        MergeSeen(target);
    }

    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        for (int g = 0; g < Count; g++)
        {
            (ulong low, ulong high) = KeyWords.Of(_keys[g]);
            ulong hash = Unsafe.SizeOf<TValue>() <= sizeof(ulong) ? MergeHash.Of(low, seed) : MergeHash.Of(low, high, seed);
            parts[g] = g == _null ? (byte)0 : (byte)(hash >> shift);
        }
    }

    internal override int CompareKeys(GroupKeys other, int a, int b, int component)
    {
        FixedKeys<TValue> right = (FixedKeys<TValue>)other;
        bool leftNull = a == _null;
        bool rightNull = b == right._null;
        if (leftNull || rightNull)
        {
            return leftNull == rightNull ? 0 : leftNull ? 1 : -1;
        }

        return _keys[a].CompareTo(right._keys[b]);
    }

    internal override int[] Order(bool sorted)
    {
        int[] order = Identity(Count);
        if (sorted)
        {
            SpanSort.Sort(order.AsSpan(), new ByKey(_keys, _null));
        }

        return order;
    }

    /// <summary>Groups by their key, the null group last.</summary>
    private readonly struct ByKey : IComparer<int>
    {
        private readonly TValue[] _keys;
        private readonly int _null;

        internal ByKey(TValue[] keys, int nullGroup)
        {
            _keys = keys;
            _null = nullGroup;
        }

        public int Compare(int a, int b) =>
            a == _null ? (b == _null ? 0 : 1) : b == _null ? -1 : _keys[a].CompareTo(_keys[b]);
    }

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _shape;
        return group => group == _null ? default! : StorageValues.ToClr<TValue, T>(_keys[group], shape);
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        TValue[] keys = _keys;
        int nullGroup = _null;
        StorageKind kind = _shape.Kind;
        if (kind == StorageKind.Primitive && store.Leaf is FixedStore leaf && leaf.Width == Unsafe.SizeOf<TValue>())
        {
            foreach (int group in groups)
            {
                if (group == nullGroup)
                {
                    KeyStores.AppendNull(store);
                }
                else
                {
                    leaf.Append(keys[group]);
                }
            }

            return;
        }

        foreach (int group in groups)
        {
            if (group == nullGroup)
            {
                KeyStores.AppendNull(store);
            }
            else
            {
                KeyStores.AppendFixed(store, keys[group], kind);
            }
        }
    }

    /// <summary>The rows of a sorted column as runs of one key: one comparison per row, one lookup per run.</summary>
    private void Runs(ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> validity, int rows, ReadOnlySpan<ulong> selection, GroupRanges ranges)
    {
        int start = 0;
        for (int row = 1; row <= rows; row++)
        {
            if (row < rows && SameKey(values, validity, start, row))
            {
                continue;
            }

            if (RowMasks.Count(selection, start, row) > 0)
            {
                ranges.Add(start, row, StorageValues.IsValid(validity, start) ? Lookup(values[start]) : NullGroup());
            }

            start = row;
        }
    }

    private static bool SameKey(ReadOnlySpan<TValue> values, ReadOnlySpan<ulong> validity, int left, int right)
    {
        if (validity.IsEmpty)
        {
            return values[left].Equals(values[right]);
        }

        bool leftValid = StorageValues.IsValid(validity, left);
        return leftValid == StorageValues.IsValid(validity, right) && (!leftValid || values[left].Equals(values[right]));
    }

    private int Lookup(TValue value)
    {
        ref int group = ref _index.Slot(value, out bool exists);
        if (!exists)
        {
            // Filled before the key is stored: storing it may double the keys and move the index to
            // another dictionary, which copies the slot; the one read below keeps the number too.
            group = Count;
            Add(value);
        }

        return group;
    }

    private int NullGroup()
    {
        if (_null < 0)
        {
            _null = Add(default);
        }

        return _null;
    }

    private int Add(TValue value)
    {
        if (Count == _keys.Length)
        {
            Array.Resize(ref _keys, Count * 2);
            _index.Doubled();
        }

        _keys[Count] = value;
        return Count++;
    }
}

/// <summary>A key of one text or binary column, in a byte table, and a group for null.</summary>
internal sealed class BytesKeys : GroupKeys
{
    private readonly ColumnShape _shape;
    private readonly bool _sorted;

    // A group is the table's entry of its key; the null group an entry no key finds.
    private readonly ByteKeyTable _table = new ByteKeyTable();
    private int _null = -1;

    internal BytesKeys(ColumnShape shape, bool sorted)
    {
        _shape = shape;
        _sorted = sorted;
    }

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        int node = nodes[0];
        ColumnEncoding encoding = EncodedForms.EncodingOf(arena, node);
        Saw(encoding);
        switch (encoding)
        {
            case ColumnEncoding.Constant:
            {
                ValidityKind uniform = arena.RecordRef(node).Validity.Kind;
                if (uniform is ValidityKind.NonNullable or ValidityKind.AllValid or ValidityKind.AllInvalid)
                {
                    ranges.Add(0, rows, uniform == ValidityKind.AllInvalid ? NullGroup() : Lookup(BytesBlock.Constant(arena, node)));
                    return true;
                }

                break;
            }

            case ColumnEncoding.RunEnd:
            {
                if (ArenaWords.NullCount(arena, node) > 0)
                {
                    break;
                }

                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                BytesBlock values = BytesBlock.Canonical(arena, runs, out ReadOnlySpan<ulong> valid);
                int runStart = 0;
                for (int r = 0; r < ends.Length && runStart < rows; r++)
                {
                    int runEnd = Math.Min((int)ends[r], rows);
                    if (RowMasks.Count(selection, runStart, runEnd) > 0)
                    {
                        ranges.Add(runStart, runEnd, StorageValues.IsValid(valid, r) ? Lookup(values[r]) : NullGroup());
                    }

                    runStart = runEnd;
                }

                return true;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                BytesBlock dictionary = BytesBlock.Canonical(arena, entries, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> present = ArenaWords.Validity(arena, node);
                Span<int> codeGroups = CodeGroups(arena, entries, dictionary.Length);
                RowCursor cursor = new RowCursor(selection, 0, rows);
                while (cursor.Next(out int row))
                {
                    if (!StorageValues.IsValid(present, row))
                    {
                        rowGroups[row] = NullGroup();
                        continue;
                    }

                    int code = (int)codes[row];
                    int group = codeGroups[code];
                    if (group < 0)
                    {
                        group = codeGroups[code] = StorageValues.IsValid(valid, code) ? Lookup(dictionary[code]) : NullGroup();
                    }

                    rowGroups[row] = group;
                }

                return false;
            }

            default:
                break;
        }

        BytesBlock canonical = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> validity);
        if (_sorted)
        {
            int start = 0;
            for (int row = 1; row <= rows; row++)
            {
                if (row < rows && SameKey(canonical, validity, start, row))
                {
                    continue;
                }

                if (RowMasks.Count(selection, start, row) > 0)
                {
                    ranges.Add(start, row, StorageValues.IsValid(validity, start) ? Lookup(canonical[start]) : NullGroup());
                }

                start = row;
            }

            return true;
        }

        RowCursor selected = new RowCursor(selection, 0, rows);
        int lastRow = -1;
        int lastGroup = -1;
        while (selected.Next(out int row))
        {
            if (!StorageValues.IsValid(validity, row))
            {
                rowGroups[row] = NullGroup();
                continue;
            }

            ReadOnlySpan<byte> value = canonical[row];
            if (lastRow < 0 || !value.SequenceEqual(canonical[lastRow]))
            {
                lastGroup = Lookup(value);
            }

            lastRow = row;
            rowGroups[row] = lastGroup;
        }

        return false;
    }

    internal override GroupKeys Fresh() => new BytesKeys(_shape, _sorted);

    internal override long Footprint => _table.Footprint;

    internal override int NullNumber => _null;

    internal override bool Orders(int component) => true;

    internal override int CompareKeys(int a, int b, int component) => Compare(a, b);

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        // The kept groups ascend, and are the entries the table keeps, the null group's with them.
        int nullGroup = groups.IndexOf(_null);
        _table.Retain(groups);
        _null = _null < 0 ? -1 : nullGroup;
        Count = groups.Length;
        Renumbered();
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        BytesKeys into = (BytesKeys)target;
        for (int i = 0; i < groups.Length; i++)
        {
            int g = groups[i];
            map[i] = g == _null ? into.NullGroup() : into.Lookup(_table.KeyOf(g));
        }

        MergeSeen(target);
    }

    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        for (int g = 0; g < Count; g++)
        {
            parts[g] = g == _null ? (byte)0 : (byte)(MergeHash.Of(_table.KeyOf(g), seed) >> shift);
        }
    }

    internal override int CompareKeys(GroupKeys other, int a, int b, int component)
    {
        BytesKeys right = (BytesKeys)other;
        bool leftNull = a == _null;
        bool rightNull = b == right._null;
        if (leftNull || rightNull)
        {
            return leftNull == rightNull ? 0 : leftNull ? 1 : -1;
        }

        return _table.KeyOf(a).SequenceCompareTo(right._table.KeyOf(b));
    }

    internal override int[] Order(bool sorted)
    {
        int[] order = Identity(Count);
        if (sorted)
        {
            SpanSort.Sort(order.AsSpan(), new ByKey(this));
        }

        return order;
    }

    /// <summary>Groups by their key's bytes, the null group last.</summary>
    private readonly struct ByKey : IComparer<int>
    {
        private readonly BytesKeys _keys;

        internal ByKey(BytesKeys keys) => _keys = keys;

        public int Compare(int a, int b) => _keys.Compare(a, b);
    }

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _shape;
        return group => group == _null ? default! : StorageValues.BytesToClr<T>(_table.KeyOf(group), shape);
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        VarBinStore leaf = (VarBinStore)store.Leaf;
        foreach (int group in groups)
        {
            if (group == _null)
            {
                KeyStores.AppendNull(store);
            }
            else
            {
                leaf.Append(_table.KeyOf(group));
            }
        }
    }

    private int Compare(int a, int b)
    {
        if (a == _null || b == _null)
        {
            return a == b ? 0 : a == _null ? 1 : -1;
        }

        return _table.KeyOf(a).SequenceCompareTo(_table.KeyOf(b));
    }

    private static bool SameKey(BytesBlock values, ReadOnlySpan<ulong> validity, int left, int right)
    {
        bool leftValid = StorageValues.IsValid(validity, left);
        return leftValid == StorageValues.IsValid(validity, right) && (!leftValid || values[left].SequenceEqual(values[right]));
    }

    /// <summary>The group of <paramref name="value"/>, its entry in the table, numbered as it first comes.</summary>
    private int Lookup(ReadOnlySpan<byte> value)
    {
        int group = _table.GetOrAdd(value, out bool added);
        if (added)
        {
            Count = _table.Count;
        }

        return group;
    }

    private int NullGroup()
    {
        if (_null < 0)
        {
            _null = _table.AddDetached();
            Count = _table.Count;
        }

        return _null;
    }
}

/// <summary>A key of one boolean column: at most false, true and null.</summary>
internal sealed class BoolKeys : GroupKeys
{
    private readonly ColumnShape _shape;
    private readonly int[] _groups = [-1, -1, -1];
    private readonly byte[] _keyOf = new byte[3];
    private ulong[] _bits = [];
    private ulong[] _validity = [];

    internal BoolKeys(ColumnShape shape) => _shape = shape;

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges)
    {
        int node = nodes[0];
        Saw(EncodedForms.EncodingOf(arena, node));
        ReadOnlySpan<ulong> bits = BoolWords.Values(arena, ref node, ref _bits);
        ReadOnlySpan<ulong> validity = BoolWords.Validity(arena, node, ref _validity);
        RowCursor cursor = new RowCursor(selection, 0, rows);
        while (cursor.Next(out int row))
        {
            int key = !StorageValues.IsValid(validity, row) ? 2 : ((bits[row >> 6] >> (row & 63)) & 1) != 0 ? 1 : 0;
            rowGroups[row] = GroupOf(key);
        }

        return false;
    }

    internal override GroupKeys Fresh() => new BoolKeys(_shape);

    /// <summary>Three groups at most; the words a batch reads.</summary>
    internal override long Footprint => (long)(_bits.Length + _validity.Length) * sizeof(ulong);

    internal override int NullNumber => _groups[2];

    internal override bool Orders(int component) => true;

    /// <summary>False, true, then null: the order of their codes.</summary>
    internal override int CompareKeys(int a, int b, int component) => _keyOf[a].CompareTo(_keyOf[b]);

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        _groups.AsSpan().Fill(-1);
        for (int i = 0; i < groups.Length; i++)
        {
            _keyOf[i] = _keyOf[groups[i]];
            _groups[_keyOf[i]] = i;
        }

        Count = groups.Length;
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map)
    {
        BoolKeys into = (BoolKeys)target;
        for (int i = 0; i < groups.Length; i++)
        {
            map[i] = into.GroupOf(_keyOf[groups[i]]);
        }

        MergeSeen(target);
    }

    /// <summary>False and true by their codes, the null group, code 2, to the first part.</summary>
    internal override void Parts(ulong seed, int shift, Span<byte> parts)
    {
        for (int g = 0; g < Count; g++)
        {
            parts[g] = _keyOf[g] == 2 ? (byte)0 : (byte)(MergeHash.Of(_keyOf[g] + 1UL, seed) >> shift);
        }
    }

    internal override int CompareKeys(GroupKeys other, int a, int b, int component) => _keyOf[a].CompareTo(((BoolKeys)other)._keyOf[b]);

    internal override int[] Order(bool sorted)
    {
        int[] order = Identity(Count);
        if (sorted)
        {
            SpanSort.Sort(order.AsSpan(), new ByKey(_keyOf));
        }

        return order;
    }

    /// <summary>Groups by false, true, then null: the order of their codes.</summary>
    private readonly struct ByKey : IComparer<int>
    {
        private readonly byte[] _keys;

        internal ByKey(byte[] keys) => _keys = keys;

        public int Compare(int a, int b) => _keys[a].CompareTo(_keys[b]);
    }

    internal override Func<int, T> Reader<T>(int component)
    {
        ColumnShape shape = _shape;
        return group => _keyOf[group] == 2 ? default! : StorageValues.BoolToClr<T>(_keyOf[group] == 1, shape);
    }

    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        BoolStore leaf = (BoolStore)store.Leaf;
        foreach (int group in groups)
        {
            byte key = _keyOf[group];
            if (key == 2)
            {
                KeyStores.AppendNull(store);
            }
            else
            {
                leaf.Append(key == 1);
            }
        }
    }

    /// <summary>The group of false (0), true (1) or null (2).</summary>
    private int GroupOf(int key)
    {
        if (_groups[key] < 0)
        {
            _groups[key] = Count;
            _keyOf[Count] = (byte)key;
            Count++;
        }

        return _groups[key];
    }
}
