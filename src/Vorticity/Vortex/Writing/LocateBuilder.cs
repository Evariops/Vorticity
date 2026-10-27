using System;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Indexes;

namespace Vorticity.Writing;

/// <summary>
/// Builds where each value of a column lies, granule by granule (<see cref="ColumnLocate"/>, spec D, 3), as the column is
/// written: the values its sheet numbers, fed right before it, each noted once a granule, the writer's block, in the
/// granules it lies in.
/// </summary>
/// <remarks>
/// Like a sheet and a column's counts, what an object says of its columns: no index budget weighs it, its own rule does.
/// A row costs a look at its value's mark; a value met in a granule, a pair of eight bytes. A column whose values make more
/// than <see cref="MostPairsAGranule"/> pairs a granule is no column a filter skips granules by: its index is given up,
/// and its pairs with it. At the end, a value that lies in more than three granules in four lies everywhere.
/// </remarks>
internal sealed class LocateBuilder : IndexBuilder
{
    /// <summary>The pairs of a value and a granule a granule makes at most, on average, before the index is given up.</summary>
    internal const int MostPairsAGranule = 256;

    private const int None = -1;

    private readonly ColumnSheetBuilder _sheet;

    // The open granule; the rows taken.
    private int _granule;
    private long _rows;

    // Each number's granule it was last met in, plus one; its first and last pair.
    private int[] _seenIn = [];
    private int[] _first = [];
    private int[] _last = [];

    // The pairs, a value's as a list through _next: each pair's granule.
    private int[] _granules = new int[1_024];
    private int[] _next = new int[1_024];
    private int _pairs;

    /// <param name="sheet">The column's sheet, fed first, which numbers its rows' values.</param>
    internal LocateBuilder(ColumnSheetBuilder sheet)
    {
        _sheet = sheet;
        sheet.NumberRows();
    }

    internal override void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        _rows += count;
        if (Abandoned is not null || count <= 0)
        {
            return;
        }

        ReadOnlySpan<int> ids = _sheet.RowIds;
        if (ids.IsEmpty)
        {
            Abandon("the column's values are not numbered: its distinct values passed what a sheet counts");
            return;
        }

        int mark = _granule + 1;
        int[] seenIn = _seenIn;
        foreach (int id in ids[..count])
        {
            if (id < 0)
            {
                continue;
            }

            if ((uint)id >= (uint)seenIn.Length)
            {
                Grow(id);
                seenIn = _seenIn;
            }

            if (seenIn[id] != mark)
            {
                seenIn[id] = mark;
                Note(id);
            }
        }

        if (_pairs > ((long)(_granule + 1) * MostPairsAGranule) + 4_096)
        {
            Abandon($"its values make more than {MostPairsAGranule} pairs a granule, which no filter skips granules by");
        }
    }

    internal override void CloseBlock() => _granule++;

    internal override void Abandon(string reason)
    {
        base.Abandon(reason);
        (_seenIn, _first, _last, _granules, _next) = ([], [], [], [], []);
    }

    /// <summary>Makes room for the values numbered up to <paramref name="id"/>.</summary>
    private void Grow(int id)
    {
        int size = Math.Max(id + 1, Math.Max(64, _seenIn.Length * 2));
        int old = _seenIn.Length;
        Array.Resize(ref _seenIn, size);
        Array.Resize(ref _first, size);
        Array.Resize(ref _last, size);
        _first.AsSpan(old).Fill(None);
        _last.AsSpan(old).Fill(None);
    }

    /// <summary>Adds the open granule to value <paramref name="id"/>'s.</summary>
    private void Note(int id)
    {
        if (_pairs == _granules.Length)
        {
            Array.Resize(ref _granules, _pairs * 2);
            Array.Resize(ref _next, _pairs * 2);
        }

        int pair = _pairs++;
        (_granules[pair], _next[pair]) = (_granule, None);
        if (_last[id] == None)
        {
            _first[id] = pair;
        }
        else
        {
            _next[_last[id]] = pair;
        }

        _last[id] = pair;
    }

    /// <summary>
    /// The index, once every one of the file's <paramref name="rows"/> is in, granules of <paramref name="granuleRows"/>:
    /// null when it was given up, when its sheet stopped numbering or kept a value's bytes no more, or when every value lies
    /// everywhere, which an index would only weigh.
    /// </summary>
    internal byte[]? Finish(long rows, int granuleRows)
    {
        if (Abandoned is not null || !_sheet.Counting || _rows != rows || rows == 0 || granuleRows <= 0)
        {
            return null;
        }

        byte[]?[] values = _sheet.ValuesByNumber();
        if (Array.Exists(values, value => value is null))
        {
            return null;
        }

        // The values in the order of their bytes, each with its granules, ascending as they were met.
        int granules = (int)((rows + granuleRows - 1) / granuleRows);
        int most = granules - (granules / 4);
        int[] order = new int[values.Length];
        byte[][] keys = new byte[values.Length][];
        for (int id = 0; id < values.Length; id++)
        {
            (order[id], keys[id]) = (id, values[id]!);
        }

        Array.Sort(keys, order, Comparer<byte[]>.Create(static (left, right) => left.AsSpan().SequenceCompareTo(right)));
        int[] starts = new int[values.Length + 1];
        int[] placed = new int[_pairs];
        int at = 0;
        bool spares = false;
        for (int v = 0; v < order.Length; v++)
        {
            starts[v] = at;
            int id = order[v];
            for (int pair = id < _first.Length ? _first[id] : None; pair != None; pair = _next[pair])
            {
                placed[at++] = _granules[pair];
            }

            spares |= at - starts[v] <= most;
        }

        starts[values.Length] = at;
        return spares ? ColumnLocate.ToBytes(rows, granuleRows, keys, starts, placed.AsSpan(0, at), most) : null;
    }
}
