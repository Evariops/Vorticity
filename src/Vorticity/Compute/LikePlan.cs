using System;

namespace Vorticity.Compute;

/// <summary>
/// A <c>like</c> pattern without <c>_</c>, read once: its literal segments, escapes resolved, and
/// whether its first and last segments are held to the value's ends.
/// </summary>
/// <remarks>
/// A value matches when the segments occur in it in order and without overlapping, the first at its
/// start and the last at its end when the pattern does not begin or end with <c>%</c>. The leftmost
/// place of a free segment leaves the most room to the ones after it, so one forward search per
/// segment decides, where a backtracking walk retries every place a <c>%</c> could stop: the
/// searches are the runtime's vectorised ones, and each byte of the value is passed once per
/// segment at most.
/// </remarks>
internal readonly ref struct LikePlan
{
    private readonly ReadOnlySpan<byte> _literals;
    private readonly ReadOnlySpan<int> _ends;
    private readonly bool _anchoredStart;
    private readonly bool _anchoredEnd;

    /// <summary>Builds a plan over segments already read.</summary>
    /// <param name="literals">The segments, back to back.</param>
    /// <param name="ends">Where each segment ends in <paramref name="literals"/>.</param>
    /// <param name="anchoredStart">Whether the pattern does not begin with <c>%</c>.</param>
    /// <param name="anchoredEnd">Whether the pattern does not end with <c>%</c>.</param>
    internal LikePlan(ReadOnlySpan<byte> literals, ReadOnlySpan<int> ends, bool anchoredStart, bool anchoredEnd)
    {
        _literals = literals;
        _ends = ends;
        _anchoredStart = anchoredStart;
        _anchoredEnd = anchoredEnd;
    }

    /// <summary>The number of literal segments.</summary>
    internal int Count => _ends.Length;

    /// <summary>Whether the first segment is held to the value's start.</summary>
    internal bool AnchoredStart => _anchoredStart;

    /// <summary>Whether the last segment is held to the value's end.</summary>
    internal bool AnchoredEnd => _anchoredEnd;

    /// <summary>Whether <paramref name="value"/> matches the pattern.</summary>
    /// <param name="value">The row's bytes.</param>
    internal bool Holds(ReadOnlySpan<byte> value)
    {
        int count = _ends.Length;
        if (count == 0)
        {
            // Wildcards alone match everything, and the empty pattern the empty value alone.
            return !_anchoredStart || value.IsEmpty;
        }

        if (_anchoredStart && _anchoredEnd && count == 1)
        {
            return value.SequenceEqual(_literals);
        }

        int first = 0;
        int start = 0;
        if (_anchoredStart)
        {
            ReadOnlySpan<byte> head = Segment(0);
            if (!value.StartsWith(head))
            {
                return false;
            }

            start = head.Length;
            first = 1;
        }

        int last = count;
        int limit = value.Length;
        if (_anchoredEnd)
        {
            ReadOnlySpan<byte> tail = Segment(count - 1);
            if (limit - start < tail.Length || !value.EndsWith(tail))
            {
                return false;
            }

            limit -= tail.Length;
            last = count - 1;
        }

        for (int i = first; i < last; i++)
        {
            ReadOnlySpan<byte> segment = Segment(i);
            int at = value[start..limit].IndexOf(segment);
            if (at < 0)
            {
                return false;
            }

            start += at + segment.Length;
        }

        return true;
    }

    /// <summary>The literal bytes of segment <paramref name="index"/>.</summary>
    internal ReadOnlySpan<byte> Segment(int index)
    {
        int start = index == 0 ? 0 : _ends[index - 1];
        return _literals[start.._ends[index]];
    }
}
