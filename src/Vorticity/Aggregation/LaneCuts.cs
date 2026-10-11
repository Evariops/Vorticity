using System;
using System.Collections.Generic;

namespace Vorticity.Aggregating;

/// <summary>
/// The ranges of rows a queue hands its lanes, cut at a source's boundaries as they are met in order:
/// a quarter of a lane's share of the live rows each, then, once half of them are handed out, a share
/// of what is left that shrinks down to a block.
/// </summary>
/// <remarks>
/// Lanes do not all go at one pace: one runs on a slower core, waits on its reads, or shares its core
/// with another process. Whatever the cause, the queue does not ask: each range is at most a share of
/// what is left of the rows, so the lane that takes the last ones finishes soon after the others, down
/// to a lane half as fast as the mean: guided self-scheduling, halved. A source cuts at its own
/// boundaries — a file's chunks, a Parquet file's row groups and pages — so that no lane reads what
/// another reads.
/// </remarks>
internal sealed class LaneCuts
{
    /// <summary>The ranges a queue holds per lane at first, before they shrink: a quarter of a lane's share each.</summary>
    private const int RangesPerLane = 4;

    private readonly List<RowRange> _parts = [];
    private readonly RowRange _rows;
    private readonly int _degree;
    private readonly long _blockRows;
    private readonly long _first;
    private long _left;
    private long _target;
    private long _start;
    private long _held;

    /// <param name="rows">The rows cut.</param>
    /// <param name="alive">The live rows among them, which the shares are of.</param>
    /// <param name="degree">The lanes.</param>
    /// <param name="blockRows">The smallest share: a block.</param>
    internal LaneCuts(RowRange rows, long alive, int degree, long blockRows)
    {
        _rows = rows;
        _degree = degree;
        _blockRows = blockRows;
        _first = Math.Max(blockRows, alive / (degree * (long)RangesPerLane));
        _left = alive;
        _target = Shares(_left);
        _start = rows.Start;
    }

    /// <summary>
    /// Meets the boundary at <paramref name="at"/>, past <paramref name="live"/> live rows since the
    /// one before: a range ends here when they reach the next share.
    /// </summary>
    internal void Boundary(long at, long live)
    {
        _held += live;
        if (_held >= _target)
        {
            _parts.Add(new RowRange(_start, at));
            _left -= _held;
            _target = Shares(_left);
            _start = at;
            _held = 0;
        }
    }

    /// <summary>The ranges, the last one to the rows' end; null when they make one.</summary>
    internal RowRange[]? Finish()
    {
        _parts.Add(new RowRange(_start, _rows.End));
        return _parts.Count > 1 ? [.. _parts] : null;
    }

    /// <summary>
    /// The live rows of the next range: the first share, until what is left is half of the rows; then
    /// a share of what is left, <paramref name="left"/> over twice the degree, down to a block.
    /// </summary>
    private long Shares(long left) => Math.Max(_blockRows, Math.Min(_first, left / (2L * _degree)));
}
