using System;
using System.Numerics;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>
/// A distribution per group: a <see cref="QuantileSketch"/> each, made at a group's first value. The
/// sketches' arrays are counted as they grow, so the query's memory holds them as it holds every other
/// state, and they spill as their buckets.
/// </summary>
/// <typeparam name="TValue">The column's storage type.</typeparam>
/// <typeparam name="TResult">The answer: the distribution itself, or a quantile of it.</typeparam>
internal sealed class DistributionSlot<TValue, TResult> : AggregateSlot<TResult>
    where TValue : unmanaged, INumberBase<TValue>
{
    /// <summary>What a value adds at least to the growth the slot asks for: a sparse bucket's index and count.</summary>
    private const long BytesPerValue = sizeof(int) + sizeof(long);

    private readonly int _bits;
    private readonly Func<Distribution?, TResult> _final;
    private QuantileSketch?[] _sketches = [];
    private int _groups;
    private long _bytes;
    private ArrayShelf? _shelf;
    private TValue[] _values = [];
    private MaskCache _rows;
    private ulong[] _clip = [];

    // The bytes the sketches grew by, and the values and states stepped into them, over the slot's life: what a value
    // costs them on average once their windows densify and widen, where a sparse bucket's twelve bytes left the budget
    // short of a window's growth.
    private long _grown;
    private long _stepped;

    internal DistributionSlot(int bits, Func<Distribution?, TResult> final)
    {
        _bits = bits;
        _final = final;
    }

    internal override void Govern(ArrayShelf shelf) => _shelf = shelf;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _sketches.Length)
        {
            ArrayShelf.Resize(_shelf, ref _sketches, Scratch.Capacity(groups, _sketches.Length));
        }

        _groups = Math.Max(_groups, groups);
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<TValue> values = FixedReader.Values(input.Arena, input.Node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);

        // Every row of the range, none of them null: the group's values a block at a time.
        if (input.Selection.IsEmpty && valid.IsEmpty)
        {
            if (end > start)
            {
                long added = Sketch(group).AddAll(values[start..end]);
                _bytes += added;
                _grown += added;
                _stepped += end - start;
            }

            return;
        }

        Selection rows = start == 0 && end == input.Rows && input.Selection.IsEmpty
            ? new Selection(input.Rows)
            : RowMasks.Window(input.Selection, input.Rows, start, end, ref _clip);
        if (rows.Count > 0)
        {
            QuantileSketch sketch = Sketch(group);
            long grown = 0;
            foreach (int row in rows)
            {
                if (valid.IsEmpty || (valid[row >> 6] & (1UL << row)) != 0)
                {
                    grown += sketch.Add(double.CreateTruncating(values[row]));
                }
            }

            _bytes += grown;
            _grown += grown;
            _stepped += rows.Count;
        }

        if (!(start == 0 && end == input.Rows && input.Selection.IsEmpty))
        {
            RowMasks.Unclip(_clip, start, end);
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        ReadOnlySpan<TValue> values = FixedReader.Values(input.Arena, input.Node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), input.Start, input.End);
        long grown = 0;
        long stepped = 0;
        while (rows.Next(out int row))
        {
            grown += Sketch(groups[row]).Add(double.CreateTruncating(values[row]));
            stepped++;
        }

        _bytes += grown;
        _grown += grown;
        _stepped += stepped;
    }

    /// <summary>
    /// A row of states merges its sketch, a table of sketches', into its group's: the counts of the values it stands for,
    /// on a grid as fine as this one's or finer, so that the group's buckets are those its rows would have filled.
    /// </summary>
    internal override bool StepStates(in StateInput input, ReadOnlySpan<ulong> selection, ReadOnlySpan<int> groups)
    {
        if (input.SketchViews.IsEmpty && groups.Length > 0)
        {
            return false;
        }

        long grown = 0;
        if (selection.IsEmpty)
        {
            for (int row = 0; row < groups.Length; row++)
            {
                grown += Merge(groups[row], input.Sketch(row));
            }
        }
        else
        {
            RowCursor cursor = new RowCursor(selection, 0, groups.Length);
            while (cursor.Next(out int row))
            {
                grown += Merge(groups[row], input.Sketch(row));
            }
        }

        _bytes += grown;
        _grown += grown;
        _stepped += groups.Length;
        return true;

        long Merge(int group, ReadOnlySpan<byte> sketch)
        {
            QuantileSketch mine = Sketch(group);
            long before = mine.Bytes;
            mine.MergeEncoded(sketch);
            return mine.Bytes - before;
        }
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        QuantileSketch?[] theirs = ((DistributionSlot<TValue, TResult>)other)._sketches;
        for (int i = 0; i < from.Length; i++)
        {
            if (theirs[from[i]] is { } sketch)
            {
                QuantileSketch mine = Sketch(into[i]);
                long before = mine.Bytes;
                mine.Merge(sketch);
                _bytes += mine.Bytes - before;
            }
        }
    }

    internal override void Keep(ReadOnlySpan<int> groups)
    {
        long bytes = 0;
        for (int i = 0; i < groups.Length; i++)
        {
            QuantileSketch? kept = _sketches[groups[i]];
            _sketches[i] = kept;
            bytes += kept?.Bytes ?? 0;
        }

        Array.Clear(_sketches, groups.Length, _groups - groups.Length);
        _groups = groups.Length;
        _bytes = bytes;
    }

    internal override void Release()
    {
        if (_sketches.Length > 0)
        {
            Array.Clear(_sketches);
            _shelf?.Give(_sketches);
            _sketches = [];
        }

        _groups = 0;
        _bytes = 0;
    }

    internal override long Footprint => ((long)_sketches.Length * IntPtr.Size) + _bytes;

    internal override long GrowthFor(int more) =>
        (_groups + (long)more > _sketches.Length ? (long)Scratch.Capacity(_groups + more, _sketches.Length) * IntPtr.Size : 0)
        + (more * Math.Max(BytesPerValue, _stepped == 0 ? 0 : _grown / _stepped));

    internal override bool SpillsStates => true;

    internal override void WriteStates(ReadOnlySpan<int> groups, SpillBuffer buffer)
    {
        foreach (int group in groups)
        {
            if (_sketches[group] is { } sketch)
            {
                buffer.Write(1);
                sketch.Write(buffer.Write);
            }
            else
            {
                buffer.Write(0);
            }
        }
    }

    internal override void ReadStates(ref SpillReader reader, int count)
    {
        EnsureGroups(count);
        for (int group = 0; group < count; group++)
        {
            if (reader.Read<int>() == 0)
            {
                continue;
            }

            QuantileSketch sketch = ReadSketch(ref reader, _bits);
            _sketches[group] = sketch;
            _bytes += sketch.Bytes;
        }
    }

    internal override TResult Result(int group) =>
        _final(group < _groups && _sketches[group] is { Count: > 0 } sketch ? new Distribution(sketch) : null);

    private QuantileSketch Sketch(int group) => _sketches[group] ??= new QuantileSketch(_bits);

    /// <summary>A sketch read from a spill, its words in the order <see cref="QuantileSketch.Write"/> wrote them.</summary>
    private static QuantileSketch ReadSketch(ref SpillReader reader, int bits)
    {
        long count = reader.Read<long>();
        long zeros = reader.Read<long>();
        QuantileSketch sketch = new QuantileSketch(bits);
        for (int side = 0; side < 2; side++)
        {
            long entries = reader.Read<long>();
            for (long e = 0; e < entries; e++)
            {
                int index = checked((int)reader.Read<long>());
                sketch.AddBucket(negative: side == 0, index, reader.Read<long>());
            }
        }

        sketch.SetTotals(count, zeros);
        return sketch;
    }
}
