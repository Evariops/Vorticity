using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>
/// An approximate distinct count per group: a <see cref="CardinalitySketch"/> each, made at a group's
/// first value, over a fixed-seed XXH3-64 of the value's bytes. The sketches are counted by the query's
/// memory as they grow, and spill as their hashes or their set registers.
/// </summary>
internal abstract class CardinalitySlot : SketchSlot<CardinalitySketch, long>
{
    /// <summary>What a value may add at most: a register entry, or a hash kept exactly.</summary>
    private protected override long ValueBytes => sizeof(ulong);

    private protected override CardinalitySketch New() => new CardinalitySketch();

    private protected override CardinalitySketch Read(ref SpillReader reader) => CardinalitySketch.Read(ref reader);

    /// <summary>Adds a value's hash to a group's sketch, counting what it grows by.</summary>
    private protected void Add(int group, ulong hash)
    {
        CardinalitySketch sketch = Sketch(group);
        long before = sketch.Bytes;
        sketch.Add(hash);
        Grew(sketch.Bytes - before);
    }

    internal override long Result(int group) => Held(group) is { } sketch ? sketch.Estimate() : 0;

    /// <summary>The hash every distinct count takes of a value's bytes: XXH3-64 of seed 0, the same in every process.</summary>
    internal static ulong Hash(ReadOnlySpan<byte> value) => XxHash3.HashToUInt64(value);
}

/// <summary>An approximate distinct count of a column of fixed-width values: numbers, decimals, uuids.</summary>
/// <typeparam name="TValue">The column's storage type.</typeparam>
internal sealed class FixedCardinalitySlot<TValue>(StorageKind kind) : CardinalitySlot
    where TValue : unmanaged
{
    private TValue[] _values = [];
    private MaskCache _rows;
    private ulong[] _clip = [];

    internal override void StepRange(in BatchInput input, int start, int end, int group)
    {
        ReadOnlySpan<TValue> values = FixedReader.Values(input.Arena, input.Node, kind, ref _values, out ReadOnlySpan<ulong> valid);
        bool whole = start == 0 && end == input.Rows && input.Selection.IsEmpty;
        Selection rows = whole ? new Selection(input.Rows) : RowMasks.Window(input.Selection, input.Rows, start, end, ref _clip);
        foreach (int row in rows)
        {
            if (valid.IsEmpty || (valid[row >> 6] & (1UL << row)) != 0)
            {
                Add(group, HashOf(values[row]));
            }
        }

        if (!whole)
        {
            RowMasks.Unclip(_clip, start, end);
        }
    }

    internal override void StepRows(in BatchInput input, ReadOnlySpan<int> groups)
    {
        ReadOnlySpan<TValue> values = FixedReader.Values(input.Arena, input.Node, kind, ref _values, out ReadOnlySpan<ulong> valid);
        RowCursor rows = new RowCursor(_rows.And(input, input.Selection, valid), input.Start, input.End);
        while (rows.Next(out int row))
        {
            Add(groups[row], HashOf(values[row]));
        }
    }

    /// <summary>
    /// A value's hash, a float's zeros and NaNs each taken as one value, as the exact distinct count
    /// takes them equal.
    /// </summary>
    private static ulong HashOf(TValue value)
    {
        if (typeof(TValue) == typeof(double))
        {
            double d = Unsafe.As<TValue, double>(ref value);
            d = FloatKey.Normal(d);
            return Hash(MemoryMarshal.AsBytes(new ReadOnlySpan<double>(ref d)));
        }

        if (typeof(TValue) == typeof(float))
        {
            float f = Unsafe.As<TValue, float>(ref value);
            f = FloatKey.Normal(f);
            return Hash(MemoryMarshal.AsBytes(new ReadOnlySpan<float>(ref f)));
        }

        if (typeof(TValue) == typeof(Half))
        {
            Half h = Unsafe.As<TValue, Half>(ref value);
            h = FloatKey.Normal(h);
            return Hash(MemoryMarshal.AsBytes(new ReadOnlySpan<Half>(ref h)));
        }

        return Hash(MemoryMarshal.AsBytes(new ReadOnlySpan<TValue>(ref value)));
    }
}

/// <summary>An approximate distinct count of a text or binary column, each value hashed as its bytes.</summary>
internal sealed class BytesCardinalitySlot : CardinalitySlot
{
    private MaskCache _rows;
    private CodeSet _distinct;

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

    private readonly struct Sink(BytesCardinalitySlot slot) : IBytesSink
    {
        public void Take(int group, ReadOnlySpan<byte> value) => slot.Add(group, Hash(value));
    }
}
