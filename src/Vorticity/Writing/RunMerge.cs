using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Writing;

/// <summary>
/// One window of a raw run in the scratch. <c>KeyBytes</c> counts bytes only for byte keys and
/// <c>BlockCount</c> counts blocks only for postings; each is 0 otherwise.
/// </summary>
internal readonly record struct RawWindow(long Offset, int Entries, int KeyBytes, int BlockCount);

/// <summary>
/// A run raw: its entries in key order, either held as arrays while the scratch admits them, or
/// laid in the scratch window by window.
/// </summary>
internal sealed class RawRun
{
    /// <summary>Whether its entries are <c>(key, row)</c> pairs rather than keys with block lists.</summary>
    internal required bool HasRows { get; init; }

    /// <summary>Bytes per key, or 0 for byte keys.</summary>
    internal required int KeyWidth { get; init; }

    internal required int FirstBlock { get; init; }

    internal required int BlockCount { get; init; }

    internal long Entries { get; set; }

    /// <summary>Its windows, in key order, when it is laid in the scratch.</summary>
    internal List<RawWindow> Windows { get; } = [];

    /// <summary>Its keys, rented; null when it is laid in the scratch.</summary>
    internal byte[]? HeldKeys { get; set; }

    /// <summary>Byte keys: where each key starts, and one past the last; rented.</summary>
    internal int[]? HeldKeyOffsets { get; set; }

    /// <summary>A sorted run: each entry's file row; rented.</summary>
    internal long[]? HeldRows { get; set; }

    /// <summary>Postings: where each key's list starts, and one past the last; rented.</summary>
    internal int[]? HeldBlockOffsets { get; set; }

    /// <summary>Postings: the lists end to end; rented.</summary>
    internal uint[]? HeldBlocks { get; set; }

    /// <summary>What the scratch admitted for it.</summary>
    internal long Admitted { get; set; }

    internal bool Held => HeldKeys is not null;

    /// <summary>The bytes its arrays hold, for the scratch's budget.</summary>
    internal static long HeldBytes(long entries, long keyBytes, bool hasRows, long blocks) =>
        keyBytes + ((entries + 1) * sizeof(int)) + (hasRows ? entries * sizeof(long) : ((entries + 1) * sizeof(int)) + (blocks * sizeof(uint)));

    /// <summary>Gives its arrays back and its bytes to the scratch.</summary>
    internal void Release(RunScratch scratch)
    {
        Return(HeldKeys);
        Return(HeldKeyOffsets);
        Return(HeldRows);
        Return(HeldBlockOffsets);
        Return(HeldBlocks);
        HeldKeys = null;
        HeldKeyOffsets = null;
        HeldRows = null;
        HeldBlockOffsets = null;
        HeldBlocks = null;
        scratch.Release(Admitted);
        Admitted = 0;
    }

    private static void Return<T>(T[]? array)
    {
        if (array is { Length: > 0 })
        {
            ArrayPool<T>.Shared.Return(array);
        }
    }
}

/// <summary>A held run, handed to its cursor as one window, with no copy.</summary>
internal sealed class HeldWindowSource(RawRun run) : IWindowSource
{
    private bool _done;

    public bool Fill(RunCursor cursor)
    {
        if (_done || run.Entries == 0)
        {
            return false;
        }

        _done = true;
        cursor.Adopt(run.HeldKeys!, run.HeldKeyOffsets, run.HeldRows, run.HeldBlockOffsets, run.HeldBlocks, checked((int)run.Entries));
        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>Where a merge puts its entries.</summary>
internal interface IRunSink
{
    /// <summary>A sorted run's next entry.</summary>
    void Add(ReadOnlySpan<byte> key, long row);

    /// <summary>A postings run's next key; its block lists follow.</summary>
    void BeginKey(ReadOnlySpan<byte> key);

    /// <summary>Blocks of the current key, in block order.</summary>
    void AddBlocks(ReadOnlySpan<uint> blocks);

    /// <summary>The current key is complete.</summary>
    void EndKey();
}

/// <summary>Fills a cursor with the next window of a run.</summary>
internal interface IWindowSource : IDisposable
{
    /// <summary>Loads the next window; false when a window with at least one entry does not come.</summary>
    bool Fill(RunCursor cursor);
}

/// <summary>A position in one run, a window at a time.</summary>
internal sealed class RunCursor : IDisposable
{
    private readonly IWindowSource _source;
    private readonly KeyLayout _layout;

    /// <summary>The ordinal is the run's place in block order, which breaks a postings tie.</summary>
    internal RunCursor(IWindowSource source, KeyLayout layout, bool hasRows, int ordinal)
    {
        _source = source;
        _layout = layout;
        HasRows = hasRows;
        Ordinal = ordinal;
    }

    internal int Ordinal { get; }

    internal bool HasRows { get; }

    /// <summary>The window's keys: fixed-width keys end to end, or byte keys' heap.</summary>
    internal byte[] Keys { get; private set; } = [];

    /// <summary>For byte keys, where each starts in <see cref="Keys"/>, and one past the last.</summary>
    internal int[] KeyOffsets { get; private set; } = [];

    /// <summary>For a sorted run, each entry's file row.</summary>
    internal long[] Rows { get; private set; } = [];

    /// <summary>For postings, where each key's list starts in <see cref="Blocks"/>, and one past the last.</summary>
    internal int[] BlockOffsets { get; private set; } = [];

    /// <summary>For postings, the window's block lists end to end.</summary>
    internal uint[] Blocks { get; private set; } = [];

    /// <summary>The window's entries.</summary>
    internal int Count { get; set; }

    private int _index = -1;

    /// <summary>The current entry's fixed-width key as an ordered integer.</summary>
    internal ulong SortKey { get; private set; }

    internal ReadOnlySpan<byte> Key => _layout.Shape == KeyShape.Bytes
        ? Keys.AsSpan(KeyOffsets[_index], KeyOffsets[_index + 1] - KeyOffsets[_index])
        : Keys.AsSpan(_index * _layout.Width, _layout.Width);

    internal long Row => Rows[_index];

    internal ReadOnlySpan<uint> BlockList =>
        Blocks.AsSpan(BlockOffsets[_index], BlockOffsets[_index + 1] - BlockOffsets[_index]);

    /// <summary>Steps to the next entry, loading a window when the current one is spent.</summary>
    internal bool MoveNext()
    {
        _index++;
        if (_index >= Count)
        {
            _index = 0;
            Count = 0;
            if (!_source.Fill(this) || Count == 0)
            {
                return false;
            }
        }

        if (_layout.Shape != KeyShape.Bytes)
        {
            SortKey = _layout.SortKey(Key);
        }

        return true;
    }

    internal int Index => _index;

    /// <summary>
    /// Takes a held run's arrays as the window, with no copy; they stay the run's, and the cursor
    /// never returns them.
    /// </summary>
    internal void Adopt(byte[] keys, int[]? keyOffsets, long[]? rows, int[]? blockOffsets, uint[]? blocks, int count)
    {
        DropOwned();
        _borrowed = true;
        Keys = keys;
        KeyOffsets = keyOffsets ?? [];
        Rows = rows ?? [];
        BlockOffsets = blockOffsets ?? [];
        Blocks = blocks ?? [];
        Count = count;
    }

    private bool _borrowed;

    internal Span<byte> KeysFor(int bytes)
    {
        OwnBuffers();
        if (Keys.Length < bytes)
        {
            Return(Keys);
            Keys = ArrayPool<byte>.Shared.Rent(Math.Max(bytes, 1));
        }

        return Keys.AsSpan(0, bytes);
    }

    internal Span<int> KeyOffsetsFor(int count)
    {
        OwnBuffers();
        if (KeyOffsets.Length < count)
        {
            Return(KeyOffsets);
            KeyOffsets = ArrayPool<int>.Shared.Rent(count);
        }

        return KeyOffsets.AsSpan(0, count);
    }

    internal Span<long> RowsFor(int count)
    {
        OwnBuffers();
        if (Rows.Length < count)
        {
            Return(Rows);
            Rows = ArrayPool<long>.Shared.Rent(Math.Max(count, 1));
        }

        return Rows.AsSpan(0, count);
    }

    internal Span<int> BlockOffsetsFor(int count)
    {
        OwnBuffers();
        if (BlockOffsets.Length < count)
        {
            Return(BlockOffsets);
            BlockOffsets = ArrayPool<int>.Shared.Rent(count);
        }

        return BlockOffsets.AsSpan(0, count);
    }

    internal Span<uint> BlocksFor(int count)
    {
        OwnBuffers();
        if (Blocks.Length < count)
        {
            Return(Blocks);
            Blocks = ArrayPool<uint>.Shared.Rent(Math.Max(count, 1));
        }

        return Blocks.AsSpan(0, count);
    }

    /// <summary>After an adopted window, the cursor's buffers are its own again, and empty.</summary>
    private void OwnBuffers()
    {
        if (_borrowed)
        {
            _borrowed = false;
            Keys = [];
            KeyOffsets = [];
            Rows = [];
            BlockOffsets = [];
            Blocks = [];
        }
    }

    private void DropOwned()
    {
        if (!_borrowed)
        {
            Return(Keys);
            Return(KeyOffsets);
            Return(Rows);
            Return(BlockOffsets);
            Return(Blocks);
        }

        Keys = [];
        KeyOffsets = [];
        Rows = [];
        BlockOffsets = [];
        Blocks = [];
    }

    private static void Return<T>(T[] array)
    {
        if (array.Length > 0)
        {
            ArrayPool<T>.Shared.Return(array);
        }
    }

    public void Dispose()
    {
        _source.Dispose();
        DropOwned();
        _borrowed = false;
    }
}

/// <summary>The windows of a raw run, read back from the scratch.</summary>
internal sealed class RawWindowSource(RunScratch scratch, RawRun run) : IWindowSource
{
    private int _next;

    public bool Fill(RunCursor cursor)
    {
        if (_next >= run.Windows.Count)
        {
            return false;
        }

        RawWindow window = run.Windows[_next++];
        long at = window.Offset;
        int n = window.Entries;
        if (run.KeyWidth == 0)
        {
            Span<int> offsets = cursor.KeyOffsetsFor(n + 1);
            scratch.Read(at, offsets);
            at += (n + 1) * sizeof(int);
            scratch.Read(at, cursor.KeysFor(window.KeyBytes));
            at += window.KeyBytes;
        }
        else
        {
            int bytes = n * run.KeyWidth;
            scratch.Read(at, cursor.KeysFor(bytes));
            at += bytes;
        }

        if (run.HasRows)
        {
            scratch.Read(at, cursor.RowsFor(n));
        }
        else
        {
            scratch.Read(at, cursor.BlockOffsetsFor(n + 1));
            at += (n + 1) * sizeof(int);
            scratch.Read(at, cursor.BlocksFor(window.BlockCount));
        }

        cursor.Count = n;
        return true;
    }

    public void Dispose()
    {
    }
}

/// <summary>Lays a run into the scratch, window by window.</summary>
internal sealed class RawRunWriter : IRunSink, IDisposable
{
    /// <summary>The most entries a window holds; the merge's memory is its fan-in times a window.</summary>
    internal const int WindowEntries = 4_096;

    private readonly RunScratch _scratch;
    private readonly RawRun _run;
    private byte[] _keys = ArrayPool<byte>.Shared.Rent(1 << 16);
    private int[] _keyOffsets = ArrayPool<int>.Shared.Rent(WindowEntries + 1);
    private long[] _rows = ArrayPool<long>.Shared.Rent(WindowEntries);
    private int[] _blockOffsets = ArrayPool<int>.Shared.Rent(WindowEntries + 1);
    private uint[] _blocks = ArrayPool<uint>.Shared.Rent(1 << 12);
    private int _count;
    private int _keyBytes;
    private int _blockCount;

    /// <summary>A key width of 0 means byte keys; otherwise it is the bytes per key.</summary>
    internal RawRunWriter(RunScratch scratch, bool hasRows, int keyWidth, int firstBlock, int blockCount)
    {
        _scratch = scratch;
        _run = new RawRun { HasRows = hasRows, KeyWidth = keyWidth, FirstBlock = firstBlock, BlockCount = blockCount };
        _keyOffsets[0] = 0;
        _blockOffsets[0] = 0;
    }

    public void Add(ReadOnlySpan<byte> key, long row)
    {
        AddKey(key);
        _rows[_count] = row;
        Next();
    }

    public void BeginKey(ReadOnlySpan<byte> key) => AddKey(key);

    public void AddBlocks(ReadOnlySpan<uint> blocks)
    {
        if (_blockCount + blocks.Length > _blocks.Length)
        {
            _blocks = Grow(_blocks, _blockCount, _blockCount + blocks.Length);
        }

        blocks.CopyTo(_blocks.AsSpan(_blockCount));
        _blockCount += blocks.Length;
    }

    public void EndKey()
    {
        _blockOffsets[_count + 1] = _blockCount;
        Next();
    }

    private void AddKey(ReadOnlySpan<byte> key)
    {
        if (_keyBytes + key.Length > _keys.Length)
        {
            _keys = Grow(_keys, _keyBytes, _keyBytes + key.Length);
        }

        key.CopyTo(_keys.AsSpan(_keyBytes));
        _keyBytes += key.Length;
        _keyOffsets[_count + 1] = _keyBytes;
    }

    private void Next()
    {
        _count++;
        _run.Entries++;
        if (_count == WindowEntries)
        {
            Flush();
        }
    }

    private void Flush()
    {
        if (_count == 0)
        {
            return;
        }

        long offset = _scratch.Length;
        if (_run.KeyWidth == 0)
        {
            _scratch.Append<int>(_keyOffsets.AsSpan(0, _count + 1));
        }

        _scratch.Append(_keys.AsSpan(0, _keyBytes));
        if (_run.HasRows)
        {
            _scratch.Append<long>(_rows.AsSpan(0, _count));
        }
        else
        {
            _scratch.Append<int>(_blockOffsets.AsSpan(0, _count + 1));
            _scratch.Append<uint>(_blocks.AsSpan(0, _blockCount));
        }

        _run.Windows.Add(new RawWindow(offset, _count, _run.KeyWidth == 0 ? _keyBytes : 0, _blockCount));
        _count = 0;
        _keyBytes = 0;
        _blockCount = 0;
    }

    /// <summary>Lays the last window and returns the run.</summary>
    internal RawRun Finish()
    {
        Flush();
        return _run;
    }

    private static T[] Grow<T>(T[] array, int used, int needed)
    {
        T[] grown = ArrayPool<T>.Shared.Rent(Math.Max(needed, array.Length * 2));
        array.AsSpan(0, used).CopyTo(grown);
        ArrayPool<T>.Shared.Return(array);
        return grown;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_keys);
        ArrayPool<int>.Shared.Return(_keyOffsets);
        ArrayPool<long>.Shared.Return(_rows);
        ArrayPool<int>.Shared.Return(_blockOffsets);
        ArrayPool<uint>.Shared.Return(_blocks);
        _keys = [];
        _keyOffsets = [];
        _rows = [];
        _blockOffsets = [];
        _blocks = [];
    }
}

/// <summary>The k-way merge of runs.</summary>
internal static class RunMerger
{
    /// <summary>The most runs one pass merges; more are merged in passes.</summary>
    internal const int MaxFanIn = 64;

    /// <summary>
    /// Merges the cursors into the sink, and disposes them. They arrive in block order, and a
    /// postings key's lists are concatenated in that order.
    /// </summary>
    internal static void Merge(List<RunCursor> cursors, KeyLayout layout, bool hasRows, IRunSink sink)
    {
        try
        {
            MergeCore(cursors, layout.Shape == KeyShape.Bytes ? 0 : layout.Width, hasRows, sink);
        }
        finally
        {
            foreach (RunCursor cursor in cursors)
            {
                cursor.Dispose();
            }
        }
    }

    /// <summary>
    /// A run's head. For byte keys the ordered key is only a prefix, so a tie is settled by a full
    /// comparison.
    /// </summary>
    private struct Head
    {
        public ulong Key;
        public long Tie;
        public int Cursor;
    }

    /// <summary>A byte key's first eight bytes, zero-padded, as an integer in <c>memcmp</c> order.</summary>
    private static ulong Prefix(ReadOnlySpan<byte> key)
    {
        if (key.Length >= sizeof(ulong))
        {
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(key);
        }

        Span<byte> padded = stackalloc byte[sizeof(ulong)];
        padded.Clear();
        key.CopyTo(padded);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(padded);
    }

    private static ulong KeyOf(RunCursor cursor, int width) => width > 0 ? cursor.SortKey : Prefix(cursor.Key);

    private static ReadOnlySpan<byte> KeySpan(RunCursor cursor, int width) =>
        width > 0 ? cursor.Keys.AsSpan(cursor.Index * width, width) : cursor.Key;

    /// <summary>
    /// The merge. Entries of the heap's top that stay below the runner-up go out with no heap work.
    /// </summary>
    private static void MergeCore(List<RunCursor> cursors, int width, bool hasRows, IRunSink sink)
    {
        Head[] heap = new Head[cursors.Count];
        int size = 0;
        for (int i = 0; i < cursors.Count; i++)
        {
            RunCursor cursor = cursors[i];
            if (cursor.MoveNext())
            {
                heap[size++] = new Head { Key = KeyOf(cursor, width), Tie = hasRows ? cursor.Row : cursor.Ordinal, Cursor = i };
            }
        }

        for (int i = (size / 2) - 1; i >= 0; i--)
        {
            SiftDown(heap, size, i, cursors, width);
        }

        if (hasRows)
        {
            while (size > 0)
            {
                RunCursor cursor = cursors[heap[0].Cursor];
                bool bounded = size > 1;
                int bound = 0;
                if (bounded)
                {
                    bound = size > 2 && Less(heap[2], heap[1], cursors, width) ? 2 : 1;
                }

                Head runnerUp = heap[bound];
                while (true)
                {
                    sink.Add(KeySpan(cursor, width), heap[0].Tie);
                    if (!cursor.MoveNext())
                    {
                        heap[0] = heap[--size];
                        SiftDown(heap, size, 0, cursors, width);
                        break;
                    }

                    heap[0].Key = KeyOf(cursor, width);
                    heap[0].Tie = cursor.Row;
                    if (bounded && !Less(heap[0], runnerUp, cursors, width))
                    {
                        SiftDown(heap, size, 0, cursors, width);
                        break;
                    }
                }
            }

            return;
        }

        // Postings: every run holding the key is next, in block order; the key is copied by the
        // sink before any run steps past it, and again here to recognise the runs that follow.
        byte[] current = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            while (size > 0)
            {
                RunCursor first = cursors[heap[0].Cursor];
                ReadOnlySpan<byte> key = KeySpan(first, width);
                if (current.Length < key.Length)
                {
                    ArrayPool<byte>.Shared.Return(current);
                    current = ArrayPool<byte>.Shared.Rent(key.Length);
                }

                int length = key.Length;
                key.CopyTo(current);
                ulong ordered = heap[0].Key;
                sink.BeginKey(key);
                do
                {
                    RunCursor cursor = cursors[heap[0].Cursor];
                    sink.AddBlocks(cursor.BlockList);
                    if (cursor.MoveNext())
                    {
                        heap[0].Key = KeyOf(cursor, width);
                    }
                    else
                    {
                        heap[0] = heap[--size];
                    }

                    SiftDown(heap, size, 0, cursors, width);
                }
                while (size > 0 && heap[0].Key == ordered
                    && (width > 0 || KeySpan(cursors[heap[0].Cursor], width).SequenceEqual(current.AsSpan(0, length))));

                sink.EndKey();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(current);
        }
    }

    private static bool Less(in Head a, in Head b, List<RunCursor> cursors, int width)
    {
        if (a.Key != b.Key)
        {
            return a.Key < b.Key;
        }

        if (width == 0)
        {
            int order = KeySpan(cursors[a.Cursor], 0).SequenceCompareTo(KeySpan(cursors[b.Cursor], 0));
            if (order != 0)
            {
                return order < 0;
            }
        }

        return a.Tie < b.Tie;
    }

    private static void SiftDown(Head[] heap, int size, int at, List<RunCursor> cursors, int width)
    {
        while (true)
        {
            int left = (2 * at) + 1;
            if (left >= size)
            {
                return;
            }

            int child = left + 1 < size && Less(heap[left + 1], heap[left], cursors, width) ? left + 1 : left;
            if (!Less(heap[child], heap[at], cursors, width))
            {
                return;
            }

            (heap[at], heap[child]) = (heap[child], heap[at]);
            at = child;
        }
    }
}

/// <summary>
/// The final output of a merge: a run's segments as payloads, and its segment table. Rows and
/// blocks are written relative to the run's first row and block.
/// </summary>
internal sealed class PayloadRunSink : IRunSink
{
    private readonly KeyLayout _layout;
    private readonly bool _utf8;
    private readonly bool _hasRows;
    private readonly int _segmentEntries;
    private readonly int _firstBlock;
    private readonly int _blockCount;
    private readonly long _firstRow;
    private readonly bool _wide;
    private readonly List<KeySegment> _segments = [];
    private readonly List<PendingPayload> _payloads = [];
    private byte[] _keys = [];
    private int[] _keyOffsets = [];
    private ulong[] _rows = [];
    private uint[] _listOffsets = [];
    private uint[] _blocks = [];
    private int _count;
    private int _keyBytes;
    private int _listed;
    private long _entries;

    /// <summary>
    /// Rows per block places the run's first row, and a row span past <c>wideAbove</c> writes its
    /// rows at 64 bits.
    /// </summary>
    internal PayloadRunSink(
        KeyLayout layout, bool utf8, bool hasRows, int segmentEntries, int firstBlock, int blockCount, long blockRows,
        long wideAbove = uint.MaxValue)
    {
        _layout = layout;
        _utf8 = utf8;
        _hasRows = hasRows;
        _segmentEntries = Math.Max(segmentEntries, 1);
        _firstBlock = firstBlock;
        _blockCount = blockCount;
        _firstRow = (long)firstBlock * Math.Max(blockRows, 1);
        _wide = (long)blockCount * Math.Max(blockRows, 1) > Math.Min(wideAbove, uint.MaxValue);
        Open();
    }

    /// <summary>Whether its rows need 64 bits, the run spanning too many for 32.</summary>
    internal bool WideRows => _wide;

    public void Add(ReadOnlySpan<byte> key, long row)
    {
        AddKey(key);
        long relative = row - _firstRow;
        if (relative < 0)
        {
            throw new InvalidOperationException($"row {row} is before the run's first row {_firstRow}");
        }

        _rows[_count] = (ulong)relative;
        Next();
    }

    public void BeginKey(ReadOnlySpan<byte> key) => AddKey(key);

    public void AddBlocks(ReadOnlySpan<uint> blocks)
    {
        if (_listed + blocks.Length > _blocks.Length)
        {
            uint[] grown = ArrayPool<uint>.Shared.Rent(Math.Max(_listed + blocks.Length, _blocks.Length * 2));
            _blocks.AsSpan(0, _listed).CopyTo(grown);
            ArrayPool<uint>.Shared.Return(_blocks);
            _blocks = grown;
        }

        for (int i = 0; i < blocks.Length; i++)
        {
            _blocks[_listed + i] = checked(blocks[i] - (uint)_firstBlock);
        }

        _listed += blocks.Length;
    }

    public void EndKey()
    {
        _listOffsets[_count + 1] = (uint)_listed;
        Next();
    }

    private void AddKey(ReadOnlySpan<byte> key)
    {
        if (_keyBytes + key.Length > _keys.Length)
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(_keyBytes + key.Length, _keys.Length * 2));
            _keys.AsSpan(0, _keyBytes).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_keys);
            _keys = grown;
        }

        key.CopyTo(_keys.AsSpan(_keyBytes));
        _keyBytes += key.Length;
        if (_layout.Shape == KeyShape.Bytes)
        {
            _keyOffsets[_count + 1] = _keyBytes;
        }
    }

    private void Next()
    {
        _count++;
        _entries++;
        if (_count == _segmentEntries)
        {
            Cut();
            Open();
        }
    }

    private void Open()
    {
        int capacity = Math.Min(_segmentEntries, 1 << 16);
        _keys = ArrayPool<byte>.Shared.Rent(Math.Max(capacity * Math.Max(_layout.Width, 8), 64));
        _keyOffsets = ArrayPool<int>.Shared.Rent(_segmentEntries + 1);
        _keyOffsets[0] = 0;
        if (_hasRows)
        {
            _rows = ArrayPool<ulong>.Shared.Rent(_segmentEntries);
        }
        else
        {
            _listOffsets = ArrayPool<uint>.Shared.Rent(_segmentEntries + 1);
            _listOffsets[0] = 0;
            _blocks = ArrayPool<uint>.Shared.Rent(Math.Max(capacity, 64));
        }

        _count = 0;
        _keyBytes = 0;
        _listed = 0;
    }

    /// <summary>Hands the open segment's arrays to its payloads.</summary>
    private void Cut()
    {
        int count = _count;
        int[] offsets = _keyOffsets;
        byte[] heap = _keys;
        int length = _keyBytes;
        _segments.Add(new KeySegment(
            (ulong)count,
            count == 0 ? [] : Key(heap, offsets, 0).ToArray(),
            count == 0 ? [] : Key(heap, offsets, count - 1).ToArray()));
        _payloads.Add(KeyPayloads.Keys(_layout, _utf8, heap, length, offsets, count));
        if (_hasRows)
        {
            _payloads.Add(_wide
                ? PendingPayload.RentedU64(_rows, count, compress: true)
                : PendingPayload.NarrowedU32(_rows, count, compress: true));
        }
        else
        {
            _payloads.Add(PendingPayload.RentedU32(_listOffsets, count + 1, compress: true));
            _payloads.Add(PendingPayload.RentedU32(_blocks, _listed, compress: true));
        }

        _keys = [];
        _keyOffsets = [];
        _rows = [];
        _listOffsets = [];
        _blocks = [];
    }

    private ReadOnlySpan<byte> Key(byte[] heap, int[] offsets, int index) => _layout.Shape == KeyShape.Bytes
        ? heap.AsSpan(offsets[index], offsets[index + 1] - offsets[index])
        : heap.AsSpan(index * _layout.Width, _layout.Width);

    /// <summary>Cuts the last segment and returns the run.</summary>
    internal KeyRun Finish()
    {
        if (_count > 0 || _segments.Count == 0)
        {
            Cut();
        }
        else
        {
            ReturnOpen();
        }

        return new KeyRun(_firstBlock, _blockCount, _entries, _segments, _payloads);
    }

    private void ReturnOpen()
    {
        ArrayPool<byte>.Shared.Return(_keys);
        ArrayPool<int>.Shared.Return(_keyOffsets);
        if (_hasRows)
        {
            ArrayPool<ulong>.Shared.Return(_rows);
        }
        else
        {
            ArrayPool<uint>.Shared.Return(_listOffsets);
            ArrayPool<uint>.Shared.Return(_blocks);
        }

        _keys = [];
        _keyOffsets = [];
        _rows = [];
        _listOffsets = [];
        _blocks = [];
    }
}

/// <summary>The keys array of a locating segment, as a payload.</summary>
internal static class KeyPayloads
{
    /// <summary>A payload over a rented heap and its rented offsets, both given back once laid.</summary>
    internal static PendingPayload Keys(KeyLayout layout, bool utf8, byte[] heap, int length, int[] offsets, int count) =>
        new PendingPayload(
            (arena, types) => layout.Shape == KeyShape.Bytes
                ? Views(arena, types, utf8, heap.AsSpan(0, length), offsets.AsSpan(0, count + 1))
                : Fixed(arena, types, layout, heap.AsSpan(0, length), count),
            compress: true,
            estimate: length + (layout.Shape == KeyShape.Bytes ? 16L * count : 0),
            () =>
            {
                ArrayPool<byte>.Shared.Return(heap);
                ArrayPool<int>.Shared.Return(offsets);
            });

    private static int Fixed(CanonicalArena arena, DTypeArena types, KeyLayout layout, ReadOnlySpan<byte> heap, int count)
    {
        VortexBuffer buffer = arena.AllocateUninitialized(heap.Length, layout.Width, out Span<byte> bytes);
        heap.CopyTo(bytes);
        return arena.AddPrimitive(
            types.Primitive(layout.PType, Nullability.NonNullable), count, Validity.NonNullable, layout.PType, buffer);
    }

    private static int Views(
        CanonicalArena arena, DTypeArena types, bool utf8, ReadOnlySpan<byte> heap, ReadOnlySpan<int> offsets)
    {
        int count = offsets.Length - 1;
        VortexBuffer data = arena.AllocateUninitialized(Math.Max(heap.Length, 1), 1, out Span<byte> dataBytes);
        heap.CopyTo(dataBytes);
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();
        for (int i = 0; i < count; i++)
        {
            int length = offsets[i + 1] - offsets[i];
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            MemoryMarshal.Write(view, in length);
            if (length <= 12)
            {
                heap.Slice(offsets[i], length).CopyTo(view[4..]);
                continue;
            }

            heap.Slice(offsets[i], 4).CopyTo(view[4..]);
            int buffer = 0;
            int offset = offsets[i];
            MemoryMarshal.Write(view[8..], in buffer);
            MemoryMarshal.Write(view[12..], in offset);
        }

        DType dtype = utf8 ? types.Utf8(Nullability.NonNullable) : types.Binary(Nullability.NonNullable);
        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, [data]);
    }
}
