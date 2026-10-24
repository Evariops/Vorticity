using System;
using Vorticity.Arrays;

namespace Vorticity.Aggregating;

/// <summary>A sketch a slot keeps a group's values in: what it holds, its merges, and the words a spill keeps of it.</summary>
/// <typeparam name="TSelf">The sketch.</typeparam>
internal interface IGroupSketch<TSelf>
    where TSelf : class, IGroupSketch<TSelf>
{
    /// <summary>The bytes the sketch holds.</summary>
    long Bytes { get; }

    /// <summary>Merges <paramref name="other"/> into this sketch.</summary>
    void Merge(TSelf other);

    /// <summary>Merges a sketch in the form a table of sketches keeps it into this one.</summary>
    void MergeEncoded(ReadOnlySpan<byte> bytes);

    /// <summary>Writes the sketch as words, which a spill reads back.</summary>
    void Write(Action<long> word);
}

/// <summary>
/// An aggregate whose state is a sketch per group, made at a group's first value: the sketches are counted by the query's
/// memory as they grow, merged from a table of sketches' rows and from other lanes, kept and released with their groups,
/// and spilled as their words.
/// </summary>
/// <typeparam name="TSketch">The sketch.</typeparam>
/// <typeparam name="TResult">The answer.</typeparam>
internal abstract class SketchSlot<TSketch, TResult> : AggregateSlot<TResult>
    where TSketch : class, IGroupSketch<TSketch>
{
    private TSketch?[] _sketches = [];
    private int _groups;
    private long _bytes;
    private ArrayShelf? _shelf;

    /// <summary>A sketch of no value.</summary>
    private protected abstract TSketch New();

    /// <summary>A sketch a spill keeps as the words <see cref="IGroupSketch{TSelf}.Write"/> wrote, read back.</summary>
    private protected abstract TSketch Read(ref SpillReader reader);

    /// <summary>What a value adds at least to the growth the slot asks for.</summary>
    private protected abstract long ValueBytes { get; }

    /// <summary>What a merge of rows of states grew the sketches by, and the rows it merged.</summary>
    private protected virtual void Stepped(long grown, long rows)
    {
    }

    /// <summary>A group's sketch, made at its first value.</summary>
    private protected TSketch Sketch(int group) => _sketches[group] ??= New();

    /// <summary>A group's sketch; null before its first value.</summary>
    private protected TSketch? Held(int group) => group < _groups ? _sketches[group] : null;

    /// <summary>Counts <paramref name="bytes"/> more that the sketches hold.</summary>
    private protected void Grew(long bytes) => _bytes += bytes;

    internal override void Govern(ArrayShelf shelf) => _shelf = shelf;

    internal override void EnsureGroups(int groups)
    {
        if (groups > _sketches.Length)
        {
            ArrayShelf.Resize(_shelf, ref _sketches, Scratch.Capacity(groups, _sketches.Length));
        }

        _groups = Math.Max(_groups, groups);
    }

    /// <summary>
    /// A row of states merges the sketch of the values it stands for, a table of sketches', into its group's, so that the
    /// group's sketch is the one its rows would have made: a distinct count's hashes or registers, a distribution's
    /// buckets on a grid as fine as the slot's or finer.
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
        Stepped(grown, groups.Length);
        return true;
    }

    /// <summary>Merges an encoded sketch into a group's: what it grew by.</summary>
    private long Merge(int group, ReadOnlySpan<byte> encoded)
    {
        TSketch sketch = Sketch(group);
        long before = sketch.Bytes;
        sketch.MergeEncoded(encoded);
        return sketch.Bytes - before;
    }

    internal override void MergeFrom(AggregateSlot other, ReadOnlySpan<int> from, ReadOnlySpan<int> into)
    {
        TSketch?[] theirs = ((SketchSlot<TSketch, TResult>)other)._sketches;
        for (int i = 0; i < from.Length; i++)
        {
            if (theirs[from[i]] is { } sketch)
            {
                TSketch mine = Sketch(into[i]);
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
            TSketch? kept = _sketches[groups[i]];
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
        + (more * ValueBytes);

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

            TSketch sketch = Read(ref reader);
            _sketches[group] = sketch;
            _bytes += sketch.Bytes;
        }
    }
}
