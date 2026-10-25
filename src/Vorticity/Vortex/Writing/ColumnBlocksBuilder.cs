using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Indexes;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Writing;

/// <summary>
/// Where a file's blocks end (<see cref="BlockTable"/>), each as the rows before it: declared by the caller, which writes
/// its rows in the key's order, before it writes the rows a block ends in, and read by every column's
/// <see cref="ColumnBlocksBuilder"/>, which may take its rows on another thread.
/// </summary>
internal sealed class BlockCuts
{
    private readonly object _gate = new object();
    private readonly List<long> _ends = [];

    /// <summary>Declares that a block ends after the file's first <paramref name="rows"/> rows, past the last declared.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The end is not past the last.</exception>
    internal void Add(long rows)
    {
        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rows, _ends.Count == 0 ? 0 : _ends[^1]);
            _ends.Add(rows);
        }
    }

    /// <summary>The first end declared past <paramref name="row"/>, from the cut <paramref name="from"/> on, and its place; <see cref="long.MaxValue"/> for none yet.</summary>
    internal (long End, int Cut) After(long row, int from)
    {
        lock (_gate)
        {
            while (from < _ends.Count && _ends[from] <= row)
            {
                from++;
            }

            return (from < _ends.Count ? _ends[from] : long.MaxValue, from);
        }
    }
}

/// <summary>
/// Builds a column's statistics over the file's blocks (<see cref="ColumnBlocks"/>) and where each of its values lives
/// (<see cref="ColumnWhere"/>) as the column is written, cut where the caller's <see cref="BlockCuts"/> say. A block's
/// values are those the column's sheet numbers, fed right before it, each counted in an array by its number; a measure's
/// values are those the counts' plan read once for the batch, folded into one record a block in
/// <see cref="StateWords"/>' layout.
/// </summary>
/// <remarks>
/// <para>
/// A row costs a look at its value's mark and a count, and a measure's fold; a block, a sort of the numbers it met. A
/// value's blocks are kept as the ranges of rows they make, joined while they follow one another, at most
/// <see cref="RangesABlock"/> a block over the file: a column whose values spread wider is no column a filter skips
/// blocks by, and its values all live everywhere.
/// </para>
/// <para>
/// The values' part holds while the sheet numbers every row and keeps every value's bytes; the measure's while the plan
/// carries the column as a measure, read from every batch. A column that keeps neither keeps no statistics.
/// </para>
/// </remarks>
internal sealed class ColumnBlocksBuilder : IndexBuilder
{
    /// <summary>The ranges of rows a column's values hold at most, a block of the file, before they all live everywhere.</summary>
    internal const int RangesABlock = 4;

    private const int NoRange = -1;

    private readonly ColumnSheetBuilder _sheet;
    private readonly CountsPlan _plan;
    private readonly int _measure;
    private readonly BlockCuts _cuts;

    // The rows taken; the open block's first row and nulls; the blocks closed; the next cut and its place.
    private long _row;
    private long _blockStart;
    private long _blockNulls;
    private int _blocks;
    private long _nextEnd = -1;
    private int _nextCut;

    // Whether each part still holds: every row numbered, every batch of the measure read.
    private bool _values = true;
    private bool _measured;

    // The values met in the open block: each number's rows and the block it was last met in, plus one; the numbers met.
    private int[] _rowsOf = [];
    private int[] _seenIn = [];
    private int[] _met = new int[64];
    private int _metCount;

    // Each value's ranges of rows, as a list through _next from its first, and its last; the blocks it was met in.
    private int[] _first = [];
    private int[] _last = [];
    private int[] _blocksOf = [];
    private long[] _starts = new long[256];
    private long[] _ends = new long[256];
    private int[] _next = new int[256];
    private int _ranges;
    private bool _rangesWhole = true;

    // Whether a block listed its values, by their numbers, which only where the values live names.
    private bool _listedAny;

    // The open block's measure, a record in StateWords' layout; the rows' entries, all of it.
    private readonly long[] _state;
    private int[] _zeros = new int[1_024];

    // The sections, written as blocks close.
    private ProtoWriter _rowsSection = new ProtoWriter(256);
    private ProtoWriter _valuesSection = new ProtoWriter(256);
    private ProtoWriter _measureSection = new ProtoWriter(256);

    /// <param name="sheet">The column's sheet, fed first, which numbers its rows' values.</param>
    /// <param name="plan">What the file's counts share, which reads the measures.</param>
    /// <param name="field">The column.</param>
    /// <param name="cuts">Where the blocks end.</param>
    internal ColumnBlocksBuilder(ColumnSheetBuilder sheet, CountsPlan plan, int field, BlockCuts cuts)
    {
        _sheet = sheet;
        _plan = plan;
        _cuts = cuts;
        _measure = plan.MeasureOf(field);
        _measured = _measure >= 0;
        _state = new long[_measured ? MeasureLayout.Words(Kind == MeasureKind.Float) : 0];
        if (_measured)
        {
            ColumnCountsBuilder.OpenState(_state, Kind);
        }

        sheet.NumberRows();
    }

    private MeasureKind Kind => _plan.Measures[_measure].Kind;

    /// <inheritdoc/>
    protected override long OpenBytes =>
        _rowsSection.WrittenSpan.Length + _valuesSection.WrittenSpan.Length + _measureSection.WrittenSpan.Length
        + ((long)_rowsOf.Length * 2 * sizeof(int)) + ((long)_first.Length * 3 * sizeof(int)) + ((long)_starts.Length * (2 * sizeof(long) + sizeof(int)));

    internal override void Accumulate(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        if (count <= 0 || (!_values && !_measured))
        {
            _row += count;
            return;
        }

        ReadOnlySpan<int> ids = _sheet.RowIds;
        _values &= !ids.IsEmpty;
        _measured = _measured && _plan.Measures[_measure].Live;
        for (int at = 0; at < count;)
        {
            if (_nextEnd <= _row || _nextEnd == long.MaxValue)
            {
                (_nextEnd, _nextCut) = _cuts.After(_row, _nextCut);
            }

            int take = (int)Math.Min(count - at, _nextEnd - _row);
            _blockNulls += Nulls(arena, nodeIndex, start + at, take);
            if (_values)
            {
                Count(ids.Slice(at, take));
            }

            if (_measured)
            {
                Fold(start + at, take);
            }

            _row += take;
            at += take;
            if (_row == _nextEnd)
            {
                End();
            }
        }
    }

    internal override void CloseBlock()
    {
        // The format's blocks are not the file's: these end where the cuts say.
    }

    /// <summary>
    /// The nulls among rows <c>[start, start + count)</c> of the batch's column node <paramref name="nodeIndex"/>: by its
    /// validity and that of the extension around it, as the sheet reads them.
    /// </summary>
    private static long Nulls(CanonicalArena arena, int nodeIndex, int start, int count)
    {
        CanonicalNode outer = arena.GetNode(nodeIndex);
        CanonicalNode node = outer;
        while (node.Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node.StorageIndex);
        }

        ValidityMask own = ValidityMask.From(arena, node.Validity);
        ValidityMask wrapper = ValidityMask.From(arena, outer.Validity);
        if (own.AllInvalid || wrapper.AllInvalid)
        {
            return count;
        }

        if (own.AllValid && wrapper.AllValid)
        {
            return 0;
        }

        long nulls = 0;
        for (int row = start; row < start + count; row++)
        {
            nulls += own.IsValid(row) && wrapper.IsValid(row) ? 0 : 1;
        }

        return nulls;
    }

    /// <summary>Counts the rows' values in the open block, each by its number, a null's -1 aside.</summary>
    private void Count(ReadOnlySpan<int> ids)
    {
        int mark = _blocks + 1;
        int[] rowsOf = _rowsOf;
        int[] seenIn = _seenIn;
        foreach (int id in ids)
        {
            if (id < 0)
            {
                continue;
            }

            if ((uint)id >= (uint)rowsOf.Length)
            {
                Grow(id);
                (rowsOf, seenIn) = (_rowsOf, _seenIn);
            }

            if (seenIn[id] != mark)
            {
                seenIn[id] = mark;
                if (_metCount == _met.Length)
                {
                    Array.Resize(ref _met, _met.Length * 2);
                }

                _met[_metCount++] = id;
            }

            rowsOf[id]++;
        }
    }

    /// <summary>Makes room for the values numbered up to <paramref name="id"/>.</summary>
    private void Grow(int id)
    {
        int size = Math.Max(id + 1, Math.Max(64, _rowsOf.Length * 2));
        int old = _rowsOf.Length;
        Array.Resize(ref _rowsOf, size);
        Array.Resize(ref _seenIn, size);
        Array.Resize(ref _first, size);
        Array.Resize(ref _last, size);
        Array.Resize(ref _blocksOf, size);
        _first.AsSpan(old).Fill(NoRange);
        _last.AsSpan(old).Fill(NoRange);
    }

    /// <summary>Folds the measure's values of the batch's rows <c>[start, start + count)</c> into the open block's record.</summary>
    private void Fold(int start, int count)
    {
        // A batch whose values are all null leaves the plan's arrays as they were, perhaps shorter, and holds no value.
        CountedMeasure measure = _plan.Measures[_measure];
        if ((measure.Kind == MeasureKind.Float ? measure.Floats.Length : measure.Integers.Length) < start + count)
        {
            return;
        }

        if (_zeros.Length < count)
        {
            _zeros = new int[Math.Max(count, _zeros.Length * 2)];
        }

        ReadOnlySpan<int> entries = _zeros.AsSpan(0, count);
        ReadOnlySpan<byte> valid = measure.Valid is { } flags ? flags.AsSpan(start, count) : default;
        ref long state = ref MemoryMarshal.GetArrayDataReference(_state);
        switch (measure.Kind)
        {
            case MeasureKind.Float:
                ColumnCountsBuilder.FoldFloats(ref state, entries, measure.Splits.AsSpan(start, count), measure.Floats.AsSpan(start, count), valid);
                break;
            case MeasureKind.Signed:
                ColumnCountsBuilder.FoldSigned(ref state, entries, measure.Integers.AsSpan(start, count), valid);
                break;
            default:
                ColumnCountsBuilder.FoldUnsigned(ref state, entries, measure.Integers.AsSpan(start, count), valid);
                break;
        }
    }

    /// <summary>Ends the open block: its rows and nulls, its values and their ranges, its measure.</summary>
    private void End()
    {
        long rows = _row - _blockStart;
        if (rows <= 0)
        {
            return;
        }

        ColumnBlocks.WriteRows(ref _rowsSection, rows, _blockNulls);
        if (_values)
        {
            Span<int> met = _met.AsSpan(0, _metCount);
            met.Sort();
            int listed = met.Length <= ColumnBlocks.ListedValues ? met.Length : 0;
            _listedAny |= listed > 0;
            Span<long> counted = listed <= 64 ? stackalloc long[listed] : new long[listed];
            for (int i = 0; i < met.Length; i++)
            {
                int id = met[i];
                if (i < listed)
                {
                    counted[i] = _rowsOf[id];
                }

                _rowsOf[id] = 0;
                _blocksOf[id]++;
                Place(id);
            }

            ColumnBlocks.WriteValues(ref _valuesSection, met.Length, met[..listed], counted);
        }

        if (_measured)
        {
            ColumnCountsBuilder.CloseState(_state, Kind);
            ColumnBlocks.WriteMeasure(ref _measureSection, Kind, _state);
            _state.AsSpan().Clear();
            ColumnCountsBuilder.OpenState(_state, Kind);
        }

        _metCount = 0;
        _blockNulls = 0;
        _blockStart = _row;
        _blocks++;
    }

    /// <summary>Adds the open block's rows to value <paramref name="id"/>'s ranges: its last one grown when it ends where the block starts.</summary>
    private void Place(int id)
    {
        if (!_rangesWhole)
        {
            return;
        }

        int last = _last[id];
        if (last != NoRange && _ends[last] == _blockStart)
        {
            _ends[last] = _row;
            return;
        }

        if (_ranges >= ((long)(_blocks + 1) * RangesABlock) + 1_024)
        {
            // The values spread wider than a filter skips blocks by: every one lives everywhere, and their ranges go.
            _rangesWhole = false;
            _starts = [];
            _ends = [];
            _next = [];
            return;
        }

        if (_ranges == _starts.Length)
        {
            Array.Resize(ref _starts, _ranges * 2);
            Array.Resize(ref _ends, _ranges * 2);
            Array.Resize(ref _next, _ranges * 2);
        }

        int range = _ranges++;
        (_starts[range], _ends[range], _next[range]) = (_blockStart, _row, NoRange);
        if (last == NoRange)
        {
            _first[id] = range;
        }
        else
        {
            _next[last] = range;
        }

        _last[id] = range;
    }

    /// <summary>
    /// The statistics and the index, once every row of the file's <paramref name="table"/> is in: null for a column that
    /// keeps neither part, and for blocks other than the table's, which the cuts declared wrong. The statistics' measure
    /// is the plan's verdict, asked once every column's counts settled it.
    /// </summary>
    internal (byte[] Blocks, byte[]? Where)? Finish(BlockTable table)
    {
        End();
        _measured = _measured && _plan.Measures[_measure].Live;
        byte[]?[]? values = _values && _sheet.Counting ? _sheet.ValuesByNumber() : null;
        if (values is not null && Array.Exists(values, value => value is null))
        {
            values = null;
        }

        if ((values is null && !_measured) || _blocks != table.Count || _row != table.TotalRows)
        {
            return null;
        }

        ProtoWriter writer = new ProtoWriter(_rowsSection.WrittenSpan.Length + _valuesSection.WrittenSpan.Length + _measureSection.WrittenSpan.Length + 16);
        try
        {
            ColumnBlocks.WriteHeader(ref writer, _blocks, values is not null, _measured ? Kind : null);
            writer.WriteRawBytes(_rowsSection.WrittenSpan);
            if (values is not null)
            {
                writer.WriteRawBytes(_valuesSection.WrittenSpan);
            }

            if (_measured)
            {
                writer.WriteRawBytes(_measureSection.WrittenSpan);
            }

            return (writer.WrittenSpan.ToArray(), values is null ? null : Where(values));
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// The index of the column's <paramref name="values"/>, by their bytes: each value's ranges, or none where it lives
    /// everywhere, in more than half the blocks or in more ranges than a sixteenth of them, which would spare a reader
    /// nothing. Null when every value lives everywhere and no block lists its values by their numbers: such an index
    /// would only weigh.
    /// </summary>
    private byte[]? Where(byte[]?[] values)
    {
        int[] order = new int[values.Length];
        byte[][] keys = new byte[values.Length][];
        for (int id = 0; id < values.Length; id++)
        {
            (order[id], keys[id]) = (id, values[id]!);
        }

        Array.Sort(keys, order, Comparer<byte[]>.Create(static (left, right) => left.AsSpan().SequenceCompareTo(right)));
        int[] rangeStarts = new int[values.Length + 1];
        List<long> starts = [];
        List<long> ends = [];
        int spread = Math.Max(1, _blocks / 16);
        for (int v = 0; v < order.Length; v++)
        {
            int id = order[v];
            rangeStarts[v] = starts.Count;
            if (!_rangesWhole || (long)_blocksOf[id] * 2 > _blocks)
            {
                continue;
            }

            int from = starts.Count;
            for (int range = _first[id]; range != NoRange; range = _next[range])
            {
                starts.Add(_starts[range]);
                ends.Add(_ends[range]);
            }

            if (starts.Count - from > spread)
            {
                starts.RemoveRange(from, starts.Count - from);
                ends.RemoveRange(from, ends.Count - from);
            }
        }

        rangeStarts[values.Length] = starts.Count;
        if (starts.Count == 0 && !_listedAny)
        {
            return null;
        }

        return ColumnWhere.ToBytes(_row, keys, order, rangeStarts, CollectionsMarshal.AsSpan(starts), CollectionsMarshal.AsSpan(ends));
    }

    public override void Dispose()
    {
        _rowsSection.Dispose();
        _valuesSection.Dispose();
        _measureSection.Dispose();
    }
}
