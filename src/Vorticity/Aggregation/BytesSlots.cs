using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>What a text aggregate does with one value of one group; a struct, so the walk calls it without a virtual call.</summary>
internal interface IBytesSink
{
    void Take(int group, ReadOnlySpan<byte> value);
}

/// <summary>The values of a text or binary block handed to a sink in the form the block arrives in: once per constant, per run, per distinct code, or per row.</summary>
internal static class BytesWalk
{
    internal static void Range<TSink>(ref TSink sink, in BatchInput input, int start, int end, int group, ref MaskCache mask, ref CodeSet distinct)
        where TSink : struct, IBytesSink
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (EncodedForms.EncodingOf(arena, node))
        {
            case ColumnEncoding.Constant:
                if (RowMasks.Count(mask.And(input, input.Selection, ArenaWords.Validity(arena, node)), start, end) > 0)
                {
                    sink.Take(group, BytesBlock.Constant(arena, node));
                }

                return;

            case ColumnEncoding.RunEnd:
            {
                int runs = EncodedForms.RunEnd(arena, node, out ReadOnlySpan<uint> ends);
                BytesBlock values = BytesBlock.Canonical(arena, runs, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = mask.And(input, input.Selection, ArenaWords.Validity(arena, node));
                int r = Runs.FirstEndingAfter(ends, start);
                int runStart = r == 0 ? 0 : (int)ends[r - 1];
                for (; r < ends.Length && runStart < end; r++)
                {
                    int runEnd = Math.Min((int)ends[r], input.Rows);
                    if (StorageValues.IsValid(valid, r) && RowMasks.Count(rows, Math.Max(runStart, start), Math.Min(runEnd, end)) > 0)
                    {
                        sink.Take(group, values[r]);
                    }

                    runStart = runEnd;
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                BytesBlock dictionary = BytesBlock.Canonical(arena, entries, out ReadOnlySpan<ulong> valid);
                ReadOnlySpan<ulong> rows = mask.And(input, input.Selection, ArenaWords.Validity(arena, node));
                if (end - start < dictionary.Length)
                {
                    FewCodes(ref sink, ref distinct, codes, rows, start, end, group, dictionary, valid);
                }
                else
                {
                    ManyCodes(ref sink, ref distinct, codes, rows, start, end, group, dictionary, valid);
                }

                return;
            }

            default:
            {
                BytesBlock values = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(mask.And(input, input.Selection, valid), start, end);
                while (rows.Next(out int row))
                {
                    sink.Take(group, values[row]);
                }

                return;
            }
        }
    }

    /// <summary>The values of a range of fewer rows than its dictionary has codes, each once.</summary>
    /// <remarks>Each walk of a dictionary range is a method of its own, so that neither loop takes its shape from the other.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FewCodes<TSink>(
        ref TSink sink, ref CodeSet distinct, ReadOnlySpan<uint> codes, ReadOnlySpan<ulong> rows, int start, int end,
        int group, BytesBlock dictionary, ReadOnlySpan<ulong> valid)
        where TSink : struct, IBytesSink
    {
        foreach (int code in distinct.Few(codes, rows, start, end, dictionary.Length))
        {
            if (StorageValues.IsValid(valid, code))
            {
                sink.Take(group, dictionary[code]);
            }
        }
    }

    /// <summary>The values of a range as long as its dictionary or longer, each once, in code order.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ManyCodes<TSink>(
        ref TSink sink, ref CodeSet distinct, ReadOnlySpan<uint> codes, ReadOnlySpan<ulong> rows, int start, int end,
        int group, BytesBlock dictionary, ReadOnlySpan<ulong> valid)
        where TSink : struct, IBytesSink
    {
        Span<byte> seen = distinct.Table(dictionary.Length);
        RowCursor all = new RowCursor(rows, start, end);
        while (all.Next(out int row))
        {
            seen[(int)codes[row]] = 1;
        }

        for (int code = 0; code < seen.Length; code++)
        {
            if (seen[code] != 0 && StorageValues.IsValid(valid, code))
            {
                sink.Take(group, dictionary[code]);
            }
        }
    }

    internal static void Rows<TSink>(ref TSink sink, in BatchInput input, ReadOnlySpan<int> groups, ref MaskCache mask)
        where TSink : struct, IBytesSink
    {
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        switch (EncodedForms.EncodingOf(arena, node))
        {
            case ColumnEncoding.Constant:
            {
                ReadOnlySpan<byte> value = BytesBlock.Constant(arena, node);
                RowCursor rows = new RowCursor(mask.And(input, input.Selection, ArenaWords.Validity(arena, node)), input.Start, input.End);
                while (rows.Next(out int row))
                {
                    sink.Take(groups[row], value);
                }

                return;
            }

            case ColumnEncoding.Dictionary:
            {
                int entries = EncodedForms.Dictionary(arena, node, out ReadOnlySpan<uint> codes);
                BytesBlock dictionary = BytesBlock.Canonical(arena, entries, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(mask.And(input, input.Selection, ArenaWords.Validity(arena, node)), input.Start, input.End);
                while (rows.Next(out int row))
                {
                    int code = (int)codes[row];
                    if (StorageValues.IsValid(valid, code))
                    {
                        sink.Take(groups[row], dictionary[code]);
                    }
                }

                return;
            }

            default:
            {
                BytesBlock values = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> valid);
                RowCursor rows = new RowCursor(mask.And(input, input.Selection, valid), input.Start, input.End);
                while (rows.Next(out int row))
                {
                    sink.Take(groups[row], values[row]);
                }

                return;
            }
        }
    }
}

/// <summary>
/// The smallest or largest text or binary value of each group, in byte order: the values' bytes in
/// pages the slot shares among its groups, no object per group nor an allocation per value as values
/// come and go (PLAN-HIGH-CARDINALITY, H1, reduction 5). A value longer than the room of the one it
/// replaces moves to the end of the last page and leaves that room behind; the pages are compacted
/// when what they leave behind outgrows what they hold.
/// </summary>
internal sealed class BytesExtremeSlot<TResult> : AggregateSlot<TResult>
{
    /// <summary>The bytes of a page; a value longer than a quarter of one takes a page of its own.</summary>
    internal const int PageBytes = 1 << 16;

    private readonly bool _max;
    private readonly ColumnShape _shape;

    // Each group's value: the page and the offset its bytes start at, and its length, -1 for none.
    private long[] _at = [];
    private int[] _lengths = [];
    private int _groups;

    private byte[][] _pages = [];
    private int _pageCount;
    private int _fill = -1;
    private int _used;

    // The bytes of the pages, and the rooms of the values they hold.
    private long _paged;
    private long _held;

    private MaskCache _rows;
    private CodeSet _distinct;

    internal BytesExtremeSlot(ColumnShape shape, bool max)
    {
        _shape = shape;
        _max = max;
    }

    /// <summary>The arrays of bytes the slot holds its values in: its pages, whatever its groups.</summary>
    internal int Pages => _pageCount;

    /// <summary>The bytes of its pages, counted as it takes them.</summary>
    internal long PagedBytes => _paged;

    /// <summary>Its pages, counted as it takes them, and where each group's value lies.</summary>
    internal override long Footprint =>
        _paged + ((long)_at.Length * sizeof(long)) + ((long)_lengths.Length * sizeof(int)) + ((long)_pages.Length * IntPtr.Size);

    internal override void EnsureGroups(int groups)
    {
        if (groups > _at.Length)
        {
            int grown = Scratch.Capacity(groups, _at.Length);
            Array.Resize(ref _at, grown);
            Array.Resize(ref _lengths, grown);
        }

        _lengths.AsSpan(_groups, Math.Max(0, groups - _groups)).Fill(-1);
        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        Sink sink = new Sink(this);
        BytesWalk.Range(ref sink, input, start, end, group, ref _rows, ref _distinct);
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        Sink sink = new Sink(this);
        BytesWalk.Rows(ref sink, input, groups, ref _rows);
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        BytesExtremeSlot<TResult> source = (BytesExtremeSlot<TResult>)other;
        for (int i = 0; i < from.Length; i++)
        {
            int g = from[i];
            if (source._lengths[g] >= 0)
            {
                Offer(into[i], source.ValueOf(g));
            }
        }
    }

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        // The values of the groups left out stay in the pages until the next compaction.
        long held = 0;
        for (int i = 0; i < groups.Length; i++)
        {
            _at[i] = _at[groups[i]];
            int length = _lengths[i] = _lengths[groups[i]];
            held += length < 0 ? 0 : Room(length);
        }

        // The groups past them are emptied again when they are made.
        _groups = groups.Length;
        _held = held;
    }

    internal override TResult Result(int group) =>
        _lengths[group] < 0 ? default! : StorageValues.BytesToClr<TResult>(ValueOf(group), _shape);

    private ReadOnlySpan<byte> ValueOf(int group)
    {
        long at = _at[group];
        return _pages[(int)(at >> 32)].AsSpan((int)at, _lengths[group]);
    }

    /// <summary>Keeps <paramref name="value"/> for <paramref name="group"/> when it beats the value the group holds.</summary>
    internal void Offer(int group, ReadOnlySpan<byte> value)
    {
        int length = _lengths[group];
        if (length >= 0)
        {
            int order = value.SequenceCompareTo(ValueOf(group));
            if (_max ? order <= 0 : order >= 0)
            {
                return;
            }

            // In the room of the value it replaces when it fits; that room left behind otherwise.
            if (value.Length <= Room(length))
            {
                long at = _at[group];
                value.CopyTo(_pages[(int)(at >> 32)].AsSpan((int)at));
                _held += Room(value.Length) - Room(length);
                _lengths[group] = value.Length;
                return;
            }

            _held -= Room(length);
            _lengths[group] = -1;
        }

        _at[group] = Place(value);
        _lengths[group] = value.Length;
        _held += Room(value.Length);
    }

    /// <summary>Copies <paramref name="value"/> to the end of the last page, or to a page of its own when it is long.</summary>
    private long Place(ReadOnlySpan<byte> value)
    {
        int room = Room(value.Length);
        if (room > PageBytes / 4)
        {
            int own = AddPage(room);
            value.CopyTo(_pages[own]);
            return (long)own << 32;
        }

        if (_fill < 0 || _used + room > PageBytes)
        {
            // A page more, unless the pages leave behind more than they hold: they are compacted first.
            if (_paged - _held > _held + PageBytes)
            {
                Compact();
            }

            if (_fill < 0 || _used + room > PageBytes)
            {
                _fill = AddPage(PageBytes);
                _used = 0;
            }
        }

        long at = ((long)_fill << 32) | (uint)_used;
        value.CopyTo(_pages[_fill].AsSpan(_used));
        _used += room;
        return at;
    }

    /// <summary>Copies every value held into new pages, in the order of the groups, and drops the old ones.</summary>
    private void Compact()
    {
        byte[][] pages = _pages;
        _pages = [];
        _pageCount = 0;
        _fill = -1;
        _used = 0;
        _paged = 0;
        for (int g = 0; g < _groups; g++)
        {
            int length = _lengths[g];
            if (length >= 0)
            {
                long at = _at[g];
                _at[g] = Place(pages[(int)(at >> 32)].AsSpan((int)at, length));
            }
        }
    }

    private int AddPage(int bytes)
    {
        if (_pageCount == _pages.Length)
        {
            Array.Resize(ref _pages, Math.Max(4, _pageCount * 2));
        }

        _pages[_pageCount] = GC.AllocateUninitializedArray<byte>(bytes);
        _paged += bytes;
        return _pageCount++;
    }

    /// <summary>The bytes a value of <paramref name="length"/> takes in a page: whole words, so that a value a little longer still fits.</summary>
    private static int Room(int length) => (length + 7) & ~7;

    private readonly struct Sink : IBytesSink
    {
        private readonly BytesExtremeSlot<TResult> _slot;

        internal Sink(BytesExtremeSlot<TResult> slot) => _slot = slot;

        public void Take(int group, ReadOnlySpan<byte> value) => _slot.Offer(group, value);
    }
}

/// <summary>The distinct non-null text or binary values of each group, keyed by group and value in one table.</summary>
internal sealed class BytesDistinctSlot : AggregateSlot<long>
{
    private readonly ByteKeyTable _seen = new ByteKeyTable();
    private long[] _counts = [];
    private int _groups;
    private byte[] _key = new byte[64];
    private MaskCache _rows;
    private CodeSet _distinct;

    // Each pair chained to the pair its group met before, by their numbers in the table plus one, and
    // each group's last pair (PLAN-HIGH-CARDINALITY, H9): a group's pairs read without another's.
    private int[] _next = [];
    private int[] _first = [];

    internal override void EnsureGroups(int groups)
    {
        if (groups > _counts.Length)
        {
            Array.Resize(ref _counts, Scratch.Capacity(groups, _counts.Length));
        }

        if (groups > _first.Length)
        {
            Array.Resize(ref _first, Scratch.Capacity(groups, _first.Length));
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        Sink sink = new Sink(this);
        BytesWalk.Range(ref sink, input, start, end, group, ref _rows, ref _distinct);
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        Sink sink = new Sink(this);
        BytesWalk.Rows(ref sink, input, groups, ref _rows);
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        // Every group of the other: its pairs in the order they lie, each to its group's target. Some
        // of them, a part of a parallel merge: their chains alone (PLAN-HIGH-CARDINALITY, H9).
        BytesDistinctSlot source = (BytesDistinctSlot)other;
        ByteKeyTable seen = source._seen;
        if (from.Length == source._groups)
        {
            for (int i = 0; i < seen.Count; i++)
            {
                ReadOnlySpan<byte> key = seen.KeyOf(i);
                Add(into[BinaryPrimitives.ReadInt32LittleEndian(key)], key[4..]);
            }

            return;
        }

        int[] next = source._next;
        for (int i = 0; i < from.Length; i++)
        {
            int group = from[i];
            for (int number = group < source._first.Length ? source._first[group] : 0; number != 0; number = next[number - 1])
            {
                Add(into[i], seen.KeyOf(number - 1)[4..]);
            }
        }
    }

    internal override long Result(int group) => _counts[group];

    /// <summary>The table of its (group, value) pairs, their chains, and the counts.</summary>
    internal override long Footprint =>
        _seen.Footprint + ((long)_counts.Length * sizeof(long)) + ((long)(_next.Length + _first.Length) * sizeof(int)) + _key.Length;

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        // The pairs of the groups kept, their chains alone read: the values, each after its length, in
        // one buffer, then a table emptied of every pair, which takes them back under their new groups.
        byte[] values = [];
        int used = 0;
        for (int i = 0; i < groups.Length; i++)
        {
            for (int number = _first[groups[i]]; number != 0; number = _next[number - 1])
            {
                ReadOnlySpan<byte> value = _seen.KeyOf(number - 1)[4..];
                if (values.Length < used + 8 + value.Length)
                {
                    Array.Resize(ref values, Scratch.Capacity(used + 8 + value.Length, values.Length));
                }

                BinaryPrimitives.WriteInt32LittleEndian(values.AsSpan(used), i);
                BinaryPrimitives.WriteInt32LittleEndian(values.AsSpan(used + 4), value.Length);
                value.CopyTo(values.AsSpan(used + 8));
                used += 8 + value.Length;
            }
        }

        _seen.Clear();
        Array.Clear(_first);
        for (int i = 0; i < groups.Length; i++)
        {
            _counts[i] = _counts[groups[i]];
        }

        _counts.AsSpan(groups.Length, _groups - groups.Length).Clear();
        _groups = groups.Length;
        for (int at = 0; at < used;)
        {
            int group = BinaryPrimitives.ReadInt32LittleEndian(values.AsSpan(at));
            int length = BinaryPrimitives.ReadInt32LittleEndian(values.AsSpan(at + 4));
            Chain(group, values.AsSpan(at + 8, length));
            at += 8 + length;
        }
    }

    private void Add(int group, ReadOnlySpan<byte> value)
    {
        if (Chain(group, value))
        {
            _counts[group]++;
        }
    }

    /// <summary>Adds the pair to the table, chained to its group's pair before it; whether it was new.</summary>
    private bool Chain(int group, ReadOnlySpan<byte> value)
    {
        int length = 4 + value.Length;
        Scratch.Grow(ref _key, length);
        BinaryPrimitives.WriteInt32LittleEndian(_key, group);
        value.CopyTo(_key.AsSpan(4));
        int number = _seen.GetOrAdd(_key.AsSpan(0, length), out bool added);
        if (added)
        {
            if (number >= _next.Length)
            {
                Array.Resize(ref _next, Scratch.Capacity(number + 1, _next.Length));
            }

            _next[number] = _first[group];
            _first[group] = number + 1;
        }

        return added;
    }

    private readonly struct Sink : IBytesSink
    {
        private readonly BytesDistinctSlot _slot;

        internal Sink(BytesDistinctSlot slot) => _slot = slot;

        public void Take(int group, ReadOnlySpan<byte> value) => _slot.Add(group, value);
    }
}
