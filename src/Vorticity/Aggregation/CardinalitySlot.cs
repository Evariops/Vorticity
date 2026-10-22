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
internal abstract class CardinalitySlot : AggregateSlot<long>
{
    /// <summary>What a value may add at most: a register entry, or a hash kept exactly.</summary>
    private const long BytesPerValue = sizeof(ulong);

    private CardinalitySketch?[] _sketches = [];
    private int _groups;
    private long _bytes;
    private ArrayShelf? _shelf;

    internal override void Govern(ArrayShelf shelf) => _shelf = shelf;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _sketches.Length)
        {
            ArrayShelf.Resize(_shelf, ref _sketches, Scratch.Capacity(groups, _sketches.Length));
        }

        _groups = Math.Max(_groups, groups);
    }

    /// <summary>Adds a value's hash to a group's sketch, counting what it grows by.</summary>
    private protected void Add(int group, ulong hash)
    {
        CardinalitySketch sketch = _sketches[group] ??= new CardinalitySketch();
        long before = sketch.Bytes;
        sketch.Add(hash);
        _bytes += sketch.Bytes - before;
    }

    /// <summary>
    /// A row of states merges the sketch of the values it stands for, a table of sketches', into its group's: the union
    /// of their hashes or each register's maximum, so that the group's sketch is the one its rows would have made.
    /// </summary>
    internal override bool StepStates(in StateInput input, ReadOnlySpan<ulong> selection, ReadOnlySpan<int> groups)
    {
        if (input.SketchViews.IsEmpty && groups.Length > 0)
        {
            return false;
        }

        if (selection.IsEmpty)
        {
            for (int row = 0; row < groups.Length; row++)
            {
                Merge(groups[row], input.Sketch(row));
            }
        }
        else
        {
            RowCursor cursor = new RowCursor(selection, 0, groups.Length);
            while (cursor.Next(out int row))
            {
                Merge(groups[row], input.Sketch(row));
            }
        }

        return true;
    }

    /// <summary>Merges an encoded sketch into a group's, counting what it grows by.</summary>
    private void Merge(int group, ReadOnlySpan<byte> encoded)
    {
        CardinalitySketch sketch = _sketches[group] ??= new CardinalitySketch();
        long before = sketch.Bytes;
        sketch.MergeEncoded(encoded);
        _bytes += sketch.Bytes - before;
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        CardinalitySketch?[] theirs = ((CardinalitySlot)other)._sketches;
        for (int i = 0; i < from.Length; i++)
        {
            if (theirs[from[i]] is { } sketch)
            {
                CardinalitySketch mine = _sketches[into[i]] ??= new CardinalitySketch();
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
            CardinalitySketch? kept = _sketches[groups[i]];
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
        (_groups + (long)more > _sketches.Length ? (long)Scratch.Capacity(_groups + more, _sketches.Length) * IntPtr.Size : 0) + (more * BytesPerValue);

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

            CardinalitySketch sketch = CardinalitySketch.Read(ref reader);
            _sketches[group] = sketch;
            _bytes += sketch.Bytes;
        }
    }

    internal override long Result(int group) => group < _groups && _sketches[group] is { } sketch ? sketch.Estimate() : 0;

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
            h = Half.IsNaN(h) ? Half.NaN : h == Half.Zero ? Half.Zero : h;
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
