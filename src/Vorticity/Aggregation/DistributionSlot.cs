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
internal sealed class DistributionSlot<TValue, TResult> : SketchSlot<QuantileSketch, TResult>
    where TValue : unmanaged, INumberBase<TValue>
{
    /// <summary>What a value adds at least to the growth the slot asks for: a sparse bucket's index and count.</summary>
    private const long BytesPerValue = sizeof(int) + sizeof(long);

    private readonly int _bits;
    private readonly Func<QuantileSketch?, TResult> _final;
    private TValue[] _values = [];
    private MaskCache _rows;
    private ulong[] _clip = [];

    // The bytes the sketches grew by, and the values and states stepped into them, over the slot's life: what a value
    // costs them on average once their windows densify and widen, where a sparse bucket's twelve bytes left the budget
    // short of a window's growth.
    private long _grown;
    private long _stepped;

    internal DistributionSlot(int bits, Func<QuantileSketch?, TResult> final)
    {
        _bits = bits;
        _final = final;
    }

    private protected override long ValueBytes => Math.Max(BytesPerValue, _stepped == 0 ? 0 : _grown / _stepped);

    private protected override QuantileSketch New() => new QuantileSketch(_bits);

    private protected override QuantileSketch Read(ref SpillReader reader) => QuantileSketch.Read(_bits, ref reader);

    private protected override void Stepped(long grown, long rows)
    {
        _grown += grown;
        _stepped += rows;
    }

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<TValue> values = FixedReader.Values(input.Arena, input.Node, StorageKind.Primitive, ref _values, out ReadOnlySpan<ulong> valid);

        // Every row of the range, none of them null: the group's values a block at a time.
        if (input.Selection.IsEmpty && valid.IsEmpty)
        {
            if (end > start)
            {
                Took(Sketch(group).AddAll(values[start..end]), end - start);
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

            Took(grown, rows.Count);
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

        Took(grown, stepped);
    }

    internal override TResult Result(int group) =>
        _final(Held(group) is { Count: > 0 } sketch ? sketch : null);

    /// <summary>Counts what <paramref name="values"/> values stepped into the sketches grew them by.</summary>
    private void Took(long grown, long values)
    {
        Grew(grown);
        _grown += grown;
        _stepped += values;
    }
}
