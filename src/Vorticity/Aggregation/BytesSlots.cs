using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Writing;

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
/// A slot whose answers are bytes, a text's or a binary's: written into a result's column as they lie,
/// where a string a group decoded, then encoded again, made a million objects for the collector at a
/// million groups.
/// </summary>
internal interface IBytesResults
{
    /// <summary>Whether the slot's answers are bytes: a joined slot's are when its parts' are.</summary>
    bool HoldsBytes { get; }

    /// <summary>The bytes of <paramref name="groups"/>' answers appended to <paramref name="store"/>, a null where a group has none.</summary>
    void AppendBytes(VarBinStore store, ReadOnlySpan<int> groups);
}

/// <summary>
/// The smallest or largest text or binary value of each group, in byte order: the values' bytes in
/// pages the slot shares among its groups, no object per group nor an allocation per value as values
/// come and go. A value longer than the room of the one it
/// replaces moves to the end of the last page and leaves that room behind; the pages are compacted
/// when what they leave behind outgrows what they hold.
/// </summary>
internal sealed class BytesExtremeSlot<TResult> : AggregateSlot<TResult>, IBytesResults
{
    /// <summary>
    /// The bytes of a page; a value longer than a quarter of one takes a page of its own. Past the large
    /// objects' threshold: a page lives as long as its groups, which no collection should copy. At 64 KiB,
    /// a million groups' pages on fourteen lanes were copied by every compacting collection, a fifth of
    /// the query's cycles.
    /// </summary>
    internal const int PageBytes = 1 << 17;

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

    // The bytes of the page the values fill: 4 KiB at first, then twice the one before, up to a page. A
    // lane's table that goes to the scratch, or a part of one read back, holds a few groups.
    private int _fillBytes;

    /// <summary>The bytes of the first page the values fill.</summary>
    private const int FirstPageBytes = 4096;

    // The bytes of the pages, and the rooms of the values they hold.
    private long _paged;
    private long _held;

    private MaskCache _rows;
    private CodeSet _distinct;

    // The shelf the slot's arrays and pages grow from, under the query's memory.
    private ArrayShelf? _shelf;

    internal override void Govern(ArrayShelf shelf) => _shelf = shelf;

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
            ArrayShelf.Resize(_shelf, ref _at, grown);
            ArrayShelf.Resize(_shelf, ref _lengths, grown);
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
        CanonicalArena arena = input.Arena;
        int node = input.Node;
        if (EncodedForms.EncodingOf(arena, node) is not (ColumnEncoding.Constant or ColumnEncoding.Dictionary))
        {
            BytesBlock values = BytesBlock.Canonical(arena, node, out ReadOnlySpan<ulong> valid);
            if (_rows.And(input, input.Selection, valid).IsEmpty)
            {
                Chunked(values, groups, input.Start, input.End);
                return;
            }
        }

        Sink sink = new Sink(this);
        BytesWalk.Rows(ref sink, input, groups, ref _rows);
    }

    /// <summary>The rows <see cref="Chunked"/> takes at a time: where their groups' values lie, on the stack.</summary>
    private const int Chunk = 256;

    /// <summary>
    /// Every row of the window offered, <see cref="Chunk"/> rows at a time:
    /// where each row's group holds its value, then each compared with its row's, no row waiting on
    /// another; then the rows that beat it offered in their order, a group two rows beat taking the
    /// better. At a million groups a row's offer read three lines, one after the other: the group's
    /// length, where its value lies, then its bytes, each a miss.
    /// </summary>
    [SkipLocalsInit]
    private void Chunked(BytesBlock values, ReadOnlySpan<int> groups, int start, int end)
    {
        Span<long> at = stackalloc long[Chunk];
        Span<int> lengths = stackalloc int[Chunk];
        for (int first = start; first < end; first += Chunk)
        {
            int count = Math.Min(Chunk, end - first);
            for (int i = 0; i < count; i++)
            {
                int group = groups[first + i];
                lengths[i] = _lengths[group];
                at[i] = _at[group];
            }

            // A row its group's value beats, or ties, is left out: the value only gets better.
            for (int i = 0; i < count; i++)
            {
                int length = lengths[i];
                if (length >= 0)
                {
                    long place = at[i];
                    int order = values[first + i].SequenceCompareTo(_pages[(int)(place >> 32)].AsSpan((int)place, length));
                    lengths[i] = (_max ? order <= 0 : order >= 0) ? int.MinValue : length;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (lengths[i] != int.MinValue)
                {
                    Offer(groups[first + i], values[first + i]);
                }
            }
        }
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
            held += length < 0 ? 0 : RoomOf(length);
        }

        // The groups past them are emptied again when they are made.
        _groups = groups.Length;
        _held = held;
    }

    internal override TResult Result(int group) =>
        _lengths[group] < 0 ? default! : StorageValues.BytesToClr<TResult>(ValueOf(group), _shape);

    internal override bool SpillsStates => true;

    /// <summary>Where each group's value lies, and the pages new values take at the mean of those held, past the last page's room.</summary>
    internal override long GrowthFor(int more)
    {
        long bytes = TableGrowth.Of(_groups, more, _at.Length, _at.Length, sizeof(long) + sizeof(int));
        long mean = _groups > 0 ? (_held / _groups) + 8 : 16;
        long past = (more * mean) - (_fill < 0 ? 0 : _fillBytes - _used);
        return past <= 0 ? bytes : bytes + (((past + PageBytes - 1) / PageBytes) * PageBytes);
    }

    /// <summary>Each group's value after its length, -1 for a group with none.</summary>
    internal override void WriteStates(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        foreach (int group in groups)
        {
            if (_lengths[group] < 0)
            {
                buffer.Write(-1);
            }
            else
            {
                buffer.WriteBytes(ValueOf(group));
            }
        }
    }

    internal override void ReadStates(ref SpillReader reader, int count)
    {
        EnsureGroups(count);
        for (int group = 0; group < count; group++)
        {
            int length = reader.Read<int>();
            if (length >= 0)
            {
                Offer(group, reader.Bytes(length));
            }
        }
    }

    public bool HoldsBytes => true;

    /// <summary>The bytes of <paramref name="groups"/>' values written as they lie, a null for a group with none.</summary>
    public void AppendBytes(VarBinStore store, ReadOnlySpan<int> groups)
    {
        for (int i = 0; i < groups.Length; i++)
        {
            int group = groups[i];
            if (_lengths[group] < 0)
            {
                store.AppendNulls(1);
            }
            else
            {
                // A value of the column the reader checked as it decoded it.
                store.AppendValidated(ValueOf(group));
            }
        }
    }

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
            if (value.Length <= RoomOf(length))
            {
                long at = _at[group];
                value.CopyTo(_pages[(int)(at >> 32)].AsSpan((int)at));
                _held += RoomOf(value.Length) - RoomOf(length);
                _lengths[group] = value.Length;
                return;
            }

            _held -= RoomOf(length);
            _lengths[group] = -1;
        }

        _at[group] = Place(value);
        _lengths[group] = value.Length;
        _held += RoomOf(value.Length);
    }

    /// <summary>Copies <paramref name="value"/> to the end of the last page, or to a page of its own when it is long.</summary>
    private long Place(ReadOnlySpan<byte> value)
    {
        int room = RoomOf(value.Length);
        if (room > PageBytes / 4)
        {
            int own = AddPage(room);
            value.CopyTo(_pages[own]);
            return (long)own << 32;
        }

        if (_fill < 0 || _used + room > _fillBytes)
        {
            // A page more, unless the pages leave behind more than they hold: they are compacted first.
            if (_paged - _held > _held + PageBytes)
            {
                Compact();
            }

            if (_fill < 0 || _used + room > _fillBytes)
            {
                _fillBytes = Math.Min(PageBytes, Math.Max(FirstPageBytes, Math.Max(room, 2 * _fillBytes)));
                _fill = AddPage(_fillBytes);
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
        int held = _pageCount;
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

        // The old pages, and the array that listed them, go back to the shelf.
        for (int p = 0; p < held; p++)
        {
            _shelf?.Give(pages[p]);
        }

        _shelf?.Give(pages);
    }

    private int AddPage(int bytes)
    {
        if (_pageCount == _pages.Length)
        {
            ArrayShelf.Resize(_shelf, ref _pages, Math.Max(4, _pageCount * 2));
        }

        _pages[_pageCount] = _shelf is null ? GC.AllocateUninitializedArray<byte>(bytes) : _shelf.Take<byte>(bytes, zeroed: false);
        _paged += bytes;
        return _pageCount++;
    }

    /// <summary>The bytes a value of <paramref name="length"/> takes in a page: whole words, so that a value a little longer still fits.</summary>
    private static int RoomOf(int length) => (length + 7) & ~7;

    private readonly struct Sink : IBytesSink
    {
        private readonly BytesExtremeSlot<TResult> _slot;

        internal Sink(BytesExtremeSlot<TResult> slot) => _slot = slot;

        public void Take(int group, ReadOnlySpan<byte> value) => _slot.Offer(group, value);
    }
}

/// <summary>The distinct non-null text or binary values of each group, keyed by group and value in one table.</summary>
internal sealed class BytesDistinctSlot : AggregateSlot<long>, IPairedSlot
{
    private ByteKeyTable _seen = new ByteKeyTable();
    private long[] _counts = [];
    private int _groups;
    private byte[] _key = new byte[64];
    private MaskCache _rows;
    private CodeSet _distinct;

    // Each pair chained to the pair its group met before, by their numbers in the table plus one, and
    // each group's last pair: a group's pairs read without another's.
    private int[] _next = [];
    private int[] _first = [];

    // The shelf the table, the counts and the chains grow from, under the query's memory.
    private ArrayShelf? _shelf;

    /// <summary>The shelf the slot grows from, set as it is made, before any pair: its table made again on it.</summary>
    internal override void Govern(ArrayShelf shelf)
    {
        _shelf = shelf;
        _seen = new ByteKeyTable(shelf);
    }

    internal override void EnsureGroups(int groups)
    {
        if (groups > _counts.Length)
        {
            ArrayShelf.Resize(_shelf, ref _counts, Scratch.Capacity(groups, _counts.Length));
        }

        if (groups > _first.Length)
        {
            ArrayShelf.Resize(_shelf, ref _first, Scratch.Capacity(groups, _first.Length));
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
        // of them, a part of a parallel merge: their chains alone.
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

    internal override bool SpillsStates => true;

    /// <summary>The counts and chains of the groups, and the table of pairs and their chains, a new pair a row at most.</summary>
    internal override long GrowthFor(int more) =>
        TableGrowth.Of(_groups, more, Math.Min(_counts.Length, _first.Length), Math.Min(_counts.Length, _first.Length), sizeof(long) + sizeof(int))
        + _seen.GrowthFor(more)
        + TableGrowth.Of(_seen.Count, more, _next.Length, _next.Length, sizeof(int));

    /// <summary>Each group's values, their number first, each after its length.</summary>
    internal override void WriteStates(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        foreach (int group in groups)
        {
            int at = buffer.Length;
            buffer.Write(0);
            int values = 0;
            for (int number = group < _first.Length ? _first[group] : 0; number != 0; number = _next[number - 1])
            {
                buffer.WriteBytes(_seen.KeyOf(number - 1)[4..]);
                values++;
            }

            buffer.Patch(at, values);
        }
    }

    internal override void ReadStates(ref SpillReader reader, int count)
    {
        EnsureGroups(count);
        for (int group = 0; group < count; group++)
        {
            int values = reader.Read<int>();
            for (int v = 0; v < values; v++)
            {
                Add(group, reader.Bytes());
            }
        }
    }

    public long Pairs => _seen.Count;

    public async Task MergeInPartsAsync(AggregateSlot[] slots, int[][] maps, int groups, int parts, int degree, QueryMemory? memory, CancellationToken cancellationToken)
    {
        BytesDistinctSlot[] all = new BytesDistinctSlot[slots.Length];
        for (int p = 0; p < slots.Length; p++)
        {
            all[p] = (BytesDistinctSlot)slots[p];
        }

        // Each lane's pairs placed by part, the hash of the value under its group's target, then each
        // part's pairs from every lane made distinct.
        int shift = 64 - BitOperations.Log2((uint)parts);
        (int[] Placed, int[] Starts)[] cuts = new (int[], int[])[all.Length];
        await SideBySide.RunAsync(all.Length, p => cuts[p] = all[p].Cut(maps[p], shift, parts), degree, cancellationToken).ConfigureAwait(false);
        long[][] counts = new long[parts][];
        await SideBySide.RunAsync(
            parts,
            part =>
            {
                ByteKeyTable distinct = new ByteKeyTable();
                byte[] key = new byte[64];
                long[] count = new long[groups];
                for (int p = 0; p < all.Length; p++)
                {
                    (int[] placed, int[] starts) = cuts[p];
                    int[] map = maps[p];
                    ByteKeyTable lane = all[p]._seen;
                    for (int i = starts[part]; i < starts[part + 1]; i++)
                    {
                        ReadOnlySpan<byte> pair = lane.KeyOf(placed[i]);
                        int group = map[BinaryPrimitives.ReadInt32LittleEndian(pair)];
                        Scratch.Grow(ref key, pair.Length);
                        BinaryPrimitives.WriteInt32LittleEndian(key, group);
                        pair[4..].CopyTo(key.AsSpan(4));
                        distinct.GetOrAdd(key.AsSpan(0, pair.Length), out bool added);
                        if (added)
                        {
                            count[group]++;
                        }
                    }
                }

                // The part's table, held until its pairs are counted.
                long bytes = distinct.Footprint;
                if (memory is not null && !memory.TryGrow(bytes))
                {
                    throw memory.Exceeded("merge of a distinct count", groups, bytes);
                }

                counts[part] = count;
                memory?.Shrink(bytes);
                memory?.Discard(bytes);
            },
            degree,
            cancellationToken).ConfigureAwait(false);

        EnsureGroups(groups);
        Array.Clear(_counts);
        foreach (long[] count in counts)
        {
            for (int group = 0; group < groups; group++)
            {
                _counts[group] += count[group];
            }
        }

        _seen.Clear();
        Array.Clear(_first);
    }

    /// <summary>The table's pairs placed by part, the part of a pair the top bits past <paramref name="shift"/> of its value's hash under its group's target.</summary>
    private (int[] Placed, int[] Starts) Cut(int[] map, int shift, int parts)
    {
        int count = _seen.Count;
        int[] partOf = GC.AllocateUninitializedArray<int>(count);
        int[] starts = new int[parts + 1];
        for (int number = 0; number < count; number++)
        {
            ReadOnlySpan<byte> pair = _seen.KeyOf(number);
            int part = (int)(KeyHash.Pair(KeyHash.Bytes(pair[4..]), 0, map[BinaryPrimitives.ReadInt32LittleEndian(pair)]) >> shift);
            partOf[number] = part;
            starts[part + 1]++;
        }

        for (int part = 0; part < parts; part++)
        {
            starts[part + 1] += starts[part];
        }

        int[] next = starts[..^1];
        int[] placed = GC.AllocateUninitializedArray<int>(count);
        for (int number = 0; number < count; number++)
        {
            placed[next[partOf[number]]++] = number;
        }

        return (placed, starts);
    }

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
                ArrayShelf.Resize(_shelf, ref _next, Scratch.Capacity(number + 1, _next.Length));
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
