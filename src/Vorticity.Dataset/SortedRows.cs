using System;
using System.Collections.Generic;
using Vorticity.Indexes;
using Vorticity.RowEncoding;

namespace Vorticity.Dataset;

/// <summary>The row-encoded keys of rows that come in the key's order, by their place among them.</summary>
internal interface IOrderedKeys
{
    /// <summary>The key of row <paramref name="row"/>.</summary>
    ReadOnlySpan<byte> KeyAt(int row);
}

/// <summary>
/// What a sorted object's rows give its writer as they come in the key's order (<see cref="SortKey"/>): the indexed prefix
/// of the key of each granule's first row, which its sparse index keeps, the blocks the prefix cuts, which its statistics
/// are kept over, and its last key. What a compaction, an append and a rewrite share of every object they write sorted.
/// </summary>
/// <remarks>
/// A block ends at a change of the indexed prefix once it holds <see cref="ColumnBlocks.LeastRows"/> rows, at a change of
/// the prefix's last component, its finest grain, once it holds <see cref="ColumnBlocks.FewestRows"/> when the prefix has
/// several, and at <see cref="ColumnBlocks.MostRows"/> rows. The keys come in order, and so their prefixes, so a bisection
/// finds each change. The keys a block starts and ends with lie in buffers that grow and are kept.
/// </remarks>
/// <param name="keys">The key the rows come in the order of.</param>
/// <param name="prefix">The components of the key the object indexes and cuts its blocks on.</param>
/// <param name="blocks">Whether the object keeps statistics over blocks, a job's, which are cut then.</param>
internal sealed class SortedRows(SortKey keys, int prefix, bool blocks = true)
{
    // The keys of the object's granules, as they come; the rows taken; the key of the last.
    private readonly List<byte[]> _granuleKeys = [];
    private long _rows;
    private byte[] _lastKey = [];
    private int _lastKeyLength = -1;

    // The blocks: each closed block's rows and the rows of those of one value of the prefix; then the rows of the one
    // being written, its first key and its last.
    private readonly List<long> _blockRows = [];
    private long _pureRows;
    private long _blockFilled;
    private byte[] _blockLow = [];
    private int _blockLowLength;
    private byte[] _blockHigh = [];
    private int _blockHighLength;

    // The blocks already declared to the writer, and the rows they end at.
    private int _declaredBlocks;
    private long _declaredEnd;

    /// <summary>The rows taken since the object started.</summary>
    internal long Rows => _rows;

    /// <summary>
    /// Takes rows <paramref name="from"/> to <paramref name="to"/> of <paramref name="source"/>, the object's next rows in the
    /// key's order: the keys of those that start a granule of <paramref name="granuleRows"/> rows, the blocks they close,
    /// and the last one's key.
    /// </summary>
    internal void Take<TKeys>(TKeys source, int from, int to, int granuleRows)
        where TKeys : IOrderedKeys
    {
        if (to <= from)
        {
            return;
        }

        // The keys of the rows that start a granule: the object's rows before them are those taken.
        for (long row = (_rows + granuleRows - 1) / granuleRows * granuleRows; row < _rows + (to - from); row += granuleRows)
        {
            _granuleKeys.Add(Prefixed(source.KeyAt(from + (int)(row - _rows))).ToArray());
        }

        if (blocks)
        {
            Block(source, from, to);
        }

        _rows += to - from;
        _lastKeyLength = Keep(ref _lastKey, source.KeyAt(to - 1));
    }

    /// <summary>
    /// Declares to <paramref name="writer"/> where the blocks closed since the last call end, before the rows they end in
    /// are written: every column's statistics over them cut there.
    /// </summary>
    internal void DeclareEnds(VortexFileWriter writer)
    {
        for (; _declaredBlocks < _blockRows.Count; _declaredBlocks++)
        {
            _declaredEnd += _blockRows[_declaredBlocks];
            writer.EndBlock(_declaredEnd);
        }
    }

    /// <summary>
    /// Declares to <paramref name="writer"/>, before it completes, the object's sparse index, from the keys taken and the
    /// last one, and its blocks, the last one closed; then starts over for the next object.
    /// </summary>
    internal void Finish(VortexFileWriter writer)
    {
        CloseBlock();
        long covered = 0;
        foreach (long rows in _blockRows)
        {
            covered += rows;
        }

        if (_granuleKeys.Count > 0 && _lastKeyLength >= 0)
        {
            writer.DeclareKeyIndex(prefix, [.. _granuleKeys], Prefixed(_lastKey.AsSpan(0, _lastKeyLength)).ToArray());
        }

        if (_blockRows.Count > 0 && covered == _rows)
        {
            writer.DeclareBlocks(_blockRows.Count, _pureRows);
        }

        Clear();
    }

    /// <summary>Starts over for the next object, with nothing declared: what an object given up leaves.</summary>
    internal void Clear()
    {
        _granuleKeys.Clear();
        _rows = 0;
        _lastKeyLength = -1;
        _blockRows.Clear();
        _pureRows = 0;
        _blockFilled = 0;
        _declaredBlocks = 0;
        _declaredEnd = 0;
    }

    /// <summary>The indexed prefix of the row-encoded key <paramref name="key"/>; the whole key when its width is not told.</summary>
    private ReadOnlySpan<byte> Prefixed(ReadOnlySpan<byte> key)
    {
        int length = keys.PrefixLength(key, prefix);
        return length < 0 ? key : key[..length];
    }

    /// <summary>Notes rows <paramref name="from"/> to <paramref name="to"/> of the source, the object's next, in its blocks.</summary>
    private void Block<TKeys>(TKeys source, int from, int to)
        where TKeys : IOrderedKeys
    {
        // The last component, the finest grain the prefix groups rows by, ends a block where it changes; the rows a block
        // takes before any change may end it.
        int last = prefix > 1 ? prefix - 1 : -1;
        int open = last >= 0 ? ColumnBlocks.FewestRows : ColumnBlocks.LeastRows;
        for (int at = from; at < to;)
        {
            if (_blockFilled == 0)
            {
                _blockLowLength = Keep(ref _blockLow, source.KeyAt(at));
            }

            // Past those rows, the first change of the prefix ends a block of the least rows, and under them the first that
            // changes the last component, each change found in turn. Up to the most a block holds.
            int limit = (int)Math.Min(to, at + (ColumnBlocks.MostRows - _blockFilled));
            int end = limit;
            int checkFrom = at + (int)Math.Clamp(open - _blockFilled, 0, limit - at);
            ReadOnlySpan<byte> before = checkFrom > at ? source.KeyAt(checkFrom - 1) : _blockHigh.AsSpan(0, _blockHighLength);
            while (checkFrom < limit)
            {
                int change = FirstChange(source, before, checkFrom, limit);
                if (change >= limit
                    || _blockFilled + (change - at) >= ColumnBlocks.LeastRows
                    || (last >= 0 && !keys.SameAt(before, source.KeyAt(change), last)))
                {
                    end = change;
                    break;
                }

                before = source.KeyAt(change);
                checkFrom = change + 1;
            }

            if (end > at)
            {
                _blockHighLength = Keep(ref _blockHigh, source.KeyAt(end - 1));
                _blockFilled += end - at;
                at = end;
            }

            if (end < limit || _blockFilled == ColumnBlocks.MostRows)
            {
                CloseBlock();
            }
        }
    }

    /// <summary>
    /// The first row from <paramref name="from"/> to <paramref name="to"/> of the source whose indexed prefix is not
    /// <paramref name="before"/>'s, or <paramref name="to"/>.
    /// </summary>
    private int FirstChange<TKeys>(TKeys source, ReadOnlySpan<byte> before, int from, int to)
        where TKeys : IOrderedKeys
    {
        if (keys.SamePrefix(before, source.KeyAt(to - 1), prefix))
        {
            return to;
        }

        if (!keys.SamePrefix(before, source.KeyAt(from), prefix))
        {
            return from;
        }

        // Row `low` holds the prefix of `before`, row `high` another.
        int low = from;
        int high = to - 1;
        while (high - low > 1)
        {
            int middle = low + ((high - low) >> 1);
            if (keys.SamePrefix(before, source.KeyAt(middle), prefix))
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
    /// Closes the block being written at its last row; its rows are of one value of the prefix when its first and last keys
    /// hold one, since the keys follow one another and every row between holds it too.
    /// </summary>
    private void CloseBlock()
    {
        if (_blockFilled == 0)
        {
            return;
        }

        if (keys.SamePrefix(_blockLow.AsSpan(0, _blockLowLength), _blockHigh.AsSpan(0, _blockHighLength), prefix))
        {
            _pureRows += _blockFilled;
        }

        _blockRows.Add(_blockFilled);
        _blockFilled = 0;
    }

    /// <summary>Copies <paramref name="key"/> into <paramref name="buffer"/>, grown when short; its length.</summary>
    internal static int Keep(ref byte[] buffer, ReadOnlySpan<byte> key)
    {
        if (buffer.Length < key.Length)
        {
            buffer = new byte[Math.Max(key.Length, buffer.Length * 2)];
        }

        key.CopyTo(buffer);
        return key.Length;
    }
}

/// <summary>
/// The keys of rows in the key's order, by their places among <paramref name="keys"/>: through <paramref name="order"/>
/// from <paramref name="start"/> on, or the rows of <paramref name="keys"/> themselves from there when there is none.
/// </summary>
internal readonly struct OrderedRows(RowKeys keys, int[]? order, int start) : IOrderedKeys
{
    public ReadOnlySpan<byte> KeyAt(int row) => keys.Row(order is null ? start + row : order[start + row]);
}
