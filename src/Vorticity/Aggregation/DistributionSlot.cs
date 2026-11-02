using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vorticity.Compute;

namespace Vorticity.Aggregating;

/// <summary>
/// A distribution per group: a <see cref="QuantileSketch"/> each, made at a group's first value. The
/// sketches' arrays are counted as they grow, so the query's memory holds them as it holds every other
/// state, and they spill as their buckets. A slot whose answers read their sketches, a quantile's, takes
/// the arrays from its query's shelf and gives them back as it lets each sketch go; one whose answers are
/// the sketches themselves leaves them to the answers.
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
    private readonly bool _shelved;
    private TValue[] _values = [];
    private MaskCache _rows;
    private ulong[] _clip = [];

    // The values a filter keeps of a range, compacted.
    private TValue[] _picked = [];

    // The bytes the sketches grew by, and the values and states stepped into them, over the slot's life: what a value
    // costs them on average once their windows densify and widen, where a sparse bucket's twelve bytes left the budget
    // short of a window's growth.
    private long _grown;
    private long _stepped;

    /// <param name="bits">The grid's mantissa bits.</param>
    /// <param name="final">A group's answer from its sketch.</param>
    /// <param name="shelved">Whether the answers only read the sketches, which may then take their arrays from the query's shelf.</param>
    internal DistributionSlot(int bits, Func<QuantileSketch?, TResult> final, bool shelved)
    {
        _bits = bits;
        _final = final;
        _shelved = shelved;
    }

    private protected override long ValueBytes => Math.Max(BytesPerValue, _stepped == 0 ? 0 : _grown / _stepped);

    private protected override QuantileSketch New() => new QuantileSketch(_bits, _shelved ? Shelf : null);

    private protected override QuantileSketch Read(ref SpillReader reader) => QuantileSketch.Read(_bits, ref reader, _shelved ? Shelf : null);

    /// <summary>
    /// Another table's sketch taken as it is: one whose arrays no query's memory counts, small ones, given back to this
    /// slot's shelf from now on; or any, where the answers hold the sketches and nothing is given back.
    /// </summary>
    private protected override bool Adopts(QuantileSketch sketch) => !_shelved || sketch.TryRehome(Shelf);

    private protected override void Released(QuantileSketch sketch)
    {
        if (_shelved)
        {
            sketch.Release();
        }
    }

    private protected override void Stepped(long grown, long rows)
    {
        _grown += grown;
        _stepped += rows;
    }

    /// <summary>Lets the sketches go, and gives the slot's buffers back to the process's shelf, which the next query's take.</summary>
    internal override void Release()
    {
        base.Release();
        Scratch.Return(ref _values);
        Scratch.Return(ref _clip);
        Scratch.Return(ref _picked);
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
        if (rows.Count > 0 && valid.IsEmpty && end > start && Unsafe.SizeOf<TValue>() is 1 or 2 or 4 or 8)
        {
            // The rows a filter keeps of the range, none of them null, compacted a register at a time, then counted as a
            // block: a row at a time, through the selection's enumerator and the sketch's checks, cost a question of a
            // percentile by service 30 % of its time (2026-11-02).
            int from = start & ~63;
            Scratch.Grow(ref _picked, rows.Count);
            MaskFilter.Compress(values[from..end], _clip.AsSpan(from >> 6, (end - from + 63) >> 6), _picked.AsSpan(0, rows.Count));
            Took(Sketch(group).AddAll<TValue>(_picked.AsSpan(0, rows.Count)), rows.Count);
        }
        else if (rows.Count > 0)
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
