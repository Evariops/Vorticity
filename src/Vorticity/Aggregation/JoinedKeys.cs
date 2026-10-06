using System;
using Vorticity.Arrays;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The keys of a parallel merge's parts, read as one index: group <c>g</c> is group
/// <c>g - offsets[p]</c> of part <c>p</c>, the parts in their order and none of them empty, so that
/// no part is copied into a table of every group. Read, never assigned rows nor merged again.
/// </summary>
internal sealed class JoinedKeys : GroupKeys
{
    private readonly GroupKeys[] _parts;
    private readonly int[] _offsets;
    private readonly int _null;
    private int[] _local = [];

    internal JoinedKeys(GroupKeys[] parts, int[] offsets, int count)
    {
        _parts = parts;
        _offsets = offsets;
        Count = count;

        // The null group hashes to the first part, which holds it if any does.
        _null = -1;
        for (int p = 0; p < parts.Length && _null < 0; p++)
        {
            _null = parts[p].NullNumber >= 0 ? offsets[p] + parts[p].NullNumber : -1;
        }
    }

    internal override int NullNumber => _null;

    internal override bool Orders(int component) => _parts[0].Orders(component);

    internal override int CompareKeys(int a, int b, int component)
    {
        int left = JoinedParts.PartOf(_offsets, a);
        int right = JoinedParts.PartOf(_offsets, b);
        return _parts[left].CompareKeys(_parts[right], a - _offsets[left], b - _offsets[right], component);
    }

    internal override Func<int, T> Reader<T>(int component)
    {
        Func<int, T>[] readers = new Func<int, T>[_parts.Length];
        for (int p = 0; p < readers.Length; p++)
        {
            readers[p] = _parts[p].Reader<T>(component);
        }

        int[] offsets = _offsets;
        return group =>
        {
            int part = JoinedParts.PartOf(offsets, group);
            return readers[part](group - offsets[part]);
        };
    }

    /// <summary>The groups a run at a time of one part, their numbers in it: a batch in delivery order comes in long runs.</summary>
    internal override void Append(int component, ColumnStore store, ReadOnlySpan<int> groups)
    {
        Scratch.Grow(ref _local, groups.Length);
        int start = 0;
        while (start < groups.Length)
        {
            int part = JoinedParts.PartOf(_offsets, groups[start]);
            int low = _offsets[part];
            int high = part + 1 < _offsets.Length ? _offsets[part + 1] : Count;
            int end = start;
            while (end < groups.Length && groups[end] >= low && groups[end] < high)
            {
                _local[end] = groups[end] - low;
                end++;
            }

            _parts[part].Append(component, store, _local.AsSpan(start, end - start));
            start = end;
        }
    }

    internal override int[] Order(bool sorted) =>
        sorted ? throw new NotSupportedException("The parts of a merge are read in their order, not their keys'.") : Identity(Count);

    internal override bool Assign(CanonicalArena arena, ReadOnlySpan<int> nodes, int rows, ReadOnlySpan<ulong> selection, int[] rowGroups, GroupRanges ranges) =>
        throw JoinedParts.Read();

    internal override GroupKeys Fresh() => throw JoinedParts.Read();

    /// <summary>Its parts' indexes, and the numbers a read of one of them takes.</summary>
    internal override long Footprint
    {
        get
        {
            long bytes = (long)_local.Length * sizeof(int);
            foreach (GroupKeys part in _parts)
            {
                bytes += part.Footprint;
            }

            return bytes;
        }
    }

    internal override void MergeInto(GroupKeys target, ReadOnlySpan<int> groups, Span<int> map) => throw JoinedParts.Read();

    internal override void Parts(ulong seed, int shift, Span<byte> parts) => throw JoinedParts.Read();

    internal override void Keep(ReadOnlySpan<int> groups) => throw JoinedParts.Read();
}
