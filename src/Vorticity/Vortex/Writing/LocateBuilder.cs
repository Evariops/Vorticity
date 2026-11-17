using System;
using System.Numerics;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
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
/// and its pairs with it. At the end, a value that lies in more than three granules in four lies everywhere. Its arrays are
/// blocks of the pool, at powers of two, given back with the builder: a consolidation writes a builder for each column
/// and shredded key of each object, and each finds the blocks the one before gave back.
/// </remarks>
internal sealed class LocateBuilder : IndexBuilder
{
    /// <summary>The pairs of a value and a granule a granule makes at most, on average, before the index is given up.</summary>
    internal const int MostPairsAGranule = 256;

    private const int None = -1;

    // The pairs the first granules make room for at once.
    private const int FirstPairs = 1_024;

    private readonly ColumnSheetBuilder _sheet;

    // The open granule; the rows taken.
    private int _granule;
    private long _rows;

    // Each number's granule it was last met in, plus one; its first and last pair.
    private Block<int> _seenIn;
    private Block<int> _first;
    private Block<int> _last;

    // The pairs, a value's as a list through _next: each pair's granule.
    private Block<int> _granules;
    private Block<int> _next;
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
        if (ValidityMask.OneBitmap(arena, nodeIndex, out ReadOnlySpan<byte> bits, out int bitOffset)
            && BitmapKernels.CountSet(bits, bitOffset + start, count) * 2 < count)
        {
            // Most rows hold no value, a shredded key's: the rows that do, found a word of the bitmap at a time. A row at a
            // time, the hundreds of keys of a log's attributes walked every row of every block.
            for (int at = 0; at < count; at += 64)
            {
                ulong word = BitmapKernels.Word(bits, bitOffset + start + at, Math.Min(64, count - at));
                while (word != 0)
                {
                    Take(ids[at + BitOperations.TrailingZeroCount(word)], mark);
                    word &= word - 1;
                }
            }
        }
        else
        {
            Span<int> seenIn = _seenIn;
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
        }

        if (_pairs > ((long)(_granule + 1) * MostPairsAGranule) + 4_096)
        {
            Abandon($"its values make more than {MostPairsAGranule} pairs a granule, which no filter skips granules by");
        }
    }

    internal override void CloseBlock() => _granule++;

    /// <summary>Notes value <paramref name="id"/> in the open granule, once, a null's -1 aside.</summary>
    private void Take(int id, int mark)
    {
        if (id < 0)
        {
            return;
        }

        if ((uint)id >= (uint)_seenIn.Length)
        {
            Grow(id);
        }

        Span<int> seenIn = _seenIn;
        if (seenIn[id] != mark)
        {
            seenIn[id] = mark;
            Note(id);
        }
    }

    internal override void Abandon(string reason)
    {
        base.Abandon(reason);
        GiveBack();
    }

    public override void Dispose() => GiveBack();

    /// <summary>Makes room for the values numbered up to <paramref name="id"/>.</summary>
    private void Grow(int id)
    {
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(id + 1, Math.Max(64, _seenIn.Length * 2)));
        int old = _seenIn.Length;
        Blocks.Grow(ref _seenIn, size);
        Blocks.Grow(ref _first, size);
        Blocks.Grow(ref _last, size);
        _seenIn.AsSpan(old).Clear();
        _first.AsSpan(old).Fill(None);
        _last.AsSpan(old).Fill(None);
    }

    /// <summary>Adds the open granule to value <paramref name="id"/>'s.</summary>
    private void Note(int id)
    {
        if (_pairs == _granules.Length)
        {
            int size = Math.Max(FirstPairs, _pairs * 2);
            Blocks.Grow(ref _granules, size);
            Blocks.Grow(ref _next, size);
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
        if (Abandoned is not null || !_sheet.Counting || _rows != rows || rows == 0 || granuleRows <= 0 || !_sheet.KeptEveryValue)
        {
            return null;
        }

        // The values in the order of their bytes, read where the sheet keeps them, each with its granules, ascending as they
        // were met.
        int values = _sheet.Numbered;
        int granules = (int)((rows + granuleRows - 1) / granuleRows);
        int most = granules - (granules / 4);
        Block<int> order = Blocks.Rent<int>(values);
        Block<int> starts = Blocks.Rent<int>(values + 1);
        Block<int> placed = Blocks.Rent<int>(_pairs);
        try
        {
            Span<int> sorted = order;
            for (int id = 0; id < values; id++)
            {
                sorted[id] = id;
            }

            sorted.Sort(new ColumnSheetBuilder.ByValue(_sheet));
            int at = 0;
            bool spares = false;
            for (int v = 0; v < values; v++)
            {
                starts[v] = at;
                int id = sorted[v];
                for (int pair = id < _first.Length ? _first[id] : None; pair != None; pair = _next[pair])
                {
                    placed[at++] = _granules[pair];
                }

                spares |= at - starts[v] <= most;
            }

            starts[values] = at;
            return spares
                ? ColumnLocate.ToBytes(rows, granuleRows, new ColumnSheetBuilder.Values(_sheet, order, values), starts.AsSpan(0, values + 1), placed.AsSpan(0, at), most)
                : null;
        }
        finally
        {
            Blocks.Return(order);
            Blocks.Return(starts);
            Blocks.Return(placed);
        }
    }

    /// <summary>Gives every block back to the pool, once.</summary>
    private void GiveBack()
    {
        Give(ref _seenIn);
        Give(ref _first);
        Give(ref _last);
        Give(ref _granules);
        Give(ref _next);
    }

    private static void Give(ref Block<int> block)
    {
        Blocks.Return(block);
        block = default;
    }
}
