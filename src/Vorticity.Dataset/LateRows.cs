using System;
using Vorticity.Expressions;
using Vorticity.Keys;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The rows of a leveled exit of level 0 whose keys lie at or before the last key of the objects of the destination level
/// it meets: rows that came late, past the rows those objects already hold. Merged in key order, they come first. The exit
/// keeps them apart, in level 0, an object a clustering value, instead of reading and writing again every object they fall
/// among: a row a few seconds late made each exit rewrite the destination's last object whole. Level 0 holds objects that
/// overlap any other, which every question already reads by their summaries, so they are found as before. They stay apart
/// while they weigh an eighth of the rows of the objects they meet at most and fit the room the planner left them in level
/// 0, half its ceiling with the late objects it already holds; past either, the exit merges them in as it did, once. A
/// merge of neighbours takes in those its range holds whole.
/// </summary>
/// <remarks>
/// The bound is the largest last key of those objects where every one states it (<see cref="ObjectEntry.LastKey"/>),
/// else the greatest value of the key's first column their summaries state, which bounds it outwards: a row whose key or
/// first value lies at or before it is late. The rows after it meet no object of the destination, which the exit's
/// outputs then fill without overlap.
/// </remarks>
internal sealed class LateRows
{
    /// <summary>The rows of the objects met, over the late rows the exit keeps apart at most.</summary>
    internal const int Share = 8;

    /// <summary>The place the late rows' output takes among a job's outputs, for the inputs that sent rows to it.</summary>
    internal const int Output = -1;

    private readonly byte[] _bound;
    private readonly DType? _leading;

    private LateRows(byte[] bound, DType? leading, long limit, int room)
    {
        _bound = bound;
        _leading = leading;
        Limit = limit;
        Room = room;
    }

    /// <summary>The late rows the exit keeps apart at most.</summary>
    internal long Limit { get; }

    /// <summary>The objects the exit writes its late rows to at most (<see cref="CompactionJob.LateRoom"/>).</summary>
    internal int Room { get; }

    /// <summary>The late rows seen so far.</summary>
    internal long Count { get; private set; }

    /// <summary>
    /// The late rows of <paramref name="job"/>, a leveled exit of level 0 that meets objects of its destination, under the
    /// clustering key <paramref name="key"/>; null for any other job, one the planner left no room in level 0, or one whose
    /// met objects state no bound on the key.
    /// </summary>
    internal static LateRows? Of(CompactionJob job, ClusteringKey key)
    {
        if (job.Trigger != CompactionTrigger.LevelZeroCeiling || job.Style != CompactionStyle.Leveled || job.FromLevel != 0 || job.LateRoom <= 0)
        {
            return null;
        }

        long met = 0;
        byte[]? last = null;
        bool keyed = true;
        bool summarized = true;
        FilterLiteral greatest = default;
        bool any = false;
        foreach (CompactionInput input in job.Inputs)
        {
            if (input.Level != job.ToLevel)
            {
                continue;
            }

            met += input.Entry.Rows;
            ReadOnlySpan<byte> lastKey = input.Entry.LastKey.Span;
            if (lastKey.IsEmpty)
            {
                keyed = false;
            }
            else if (last is null || lastKey.SequenceCompareTo(last) > 0)
            {
                last = lastKey.ToArray();
            }

            if (summarized && input.Entry.Summaries.TryGet(key.InstantPath, out ColumnSummary column) && column.HasMax)
            {
                greatest = !any || KeyCursor.Compare(column.Max, greatest) > 0 ? column.Max : greatest;
            }
            else
            {
                summarized = false;
            }

            any = true;
        }

        if (!any || !Ahead(job, key, keyed ? last : null, summarized ? greatest : default))
        {
            return null;
        }

        if (keyed && last is not null)
        {
            return new LateRows(last, null, met / Share, job.LateRoom);
        }

        if (!summarized || greatest.Kind == FilterLiteralKind.Null)
        {
            return null;
        }

        try
        {
            return new LateRows(key.Encode([greatest]), key.LeadingType, met / Share, job.LateRoom);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a source of <paramref name="job"/> holds a key past the bound, its last key past <paramref name="last"/> or
    /// its first column's greatest value past <paramref name="greatest"/>: an exit whose every source lies at or before it
    /// merges them in, so that the late rows leave level 0 rather than come back to it at every exit.
    /// </summary>
    private static bool Ahead(CompactionJob job, ClusteringKey key, byte[]? last, FilterLiteral greatest)
    {
        foreach (CompactionInput input in job.Inputs)
        {
            if (input.Level != job.FromLevel)
            {
                continue;
            }

            if (last is not null && !input.Entry.LastKey.IsEmpty)
            {
                if (input.Entry.LastKey.Span.SequenceCompareTo(last) > 0)
                {
                    return true;
                }
            }
            else if (greatest.Kind != FilterLiteralKind.Null
                && input.Entry.Summaries.TryGet(key.InstantPath, out ColumnSummary column) && column.HasMax
                && KeyCursor.Compare(column.Max, greatest) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The job without the objects of its destination: its sources alone, which the late rows of let those stand.</summary>
    internal static CompactionJob Sources(CompactionJob job)
    {
        System.Collections.Generic.List<CompactionInput> sources = new System.Collections.Generic.List<CompactionInput>(job.Inputs.Count);
        foreach (CompactionInput input in job.Inputs)
        {
            if (input.Level == job.FromLevel)
            {
                sources.Add(input);
            }
        }

        return new CompactionJob(job.FromLevel, job.ToLevel, job.Style, job.Trigger, sources, job.TargetBytes, job.FirstRow)
        {
            Stop = job.Stop,
        };
    }

    /// <summary>Whether the row-encoded key <paramref name="key"/> came late: at or before the bound.</summary>
    internal bool IsLate(ReadOnlySpan<byte> key) =>
        (_leading is { } leading ? key[..RowEncoder.FirstValueLength(key, leading)] : key).SequenceCompareTo(_bound) <= 0;

    /// <summary>
    /// The first row from <paramref name="from"/> to <paramref name="to"/> of the merge's run that did not come late, or
    /// <paramref name="to"/>: found by halves, the run's keys ascending.
    /// </summary>
    internal int End(KeyOrderedMerge merge, int from, int to)
    {
        if (from >= to || !IsLate(merge.KeyAt(from)))
        {
            return from;
        }

        if (IsLate(merge.KeyAt(to - 1)))
        {
            return to;
        }

        // Row `low` came late, row `high` did not.
        int low = from;
        int high = to - 1;
        while (high - low > 1)
        {
            int middle = low + ((high - low) >> 1);
            if (IsLate(merge.KeyAt(middle)))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return high;
    }

    /// <summary>Counts <paramref name="rows"/> late rows more; past <see cref="Limit"/>, the exit merges them in instead.</summary>
    /// <exception cref="Overflow">They pass the limit.</exception>
    internal void Add(int rows)
    {
        Count += rows;
        if (Count > Limit)
        {
            throw new Overflow();
        }
    }

    /// <summary>Starts the late rows' object <paramref name="objects"/>; past <see cref="Room"/>, the exit merges them in instead.</summary>
    /// <exception cref="Overflow">They pass the room.</exception>
    internal void Opens(int objects)
    {
        if (objects > Room)
        {
            throw new Overflow();
        }
    }

    /// <summary>
    /// The greatest key the levels past 0 hold, from the last object of each: an object of level 0 whose every key lies at
    /// or before it holds rows that came late, which an exit kept apart or an append brought; past it, rows that came in
    /// order. By the last keys where every one states its own, by the first column's summaries otherwise.
    /// </summary>
    internal sealed class Frontier
    {
        private readonly string _path;
        private readonly byte[]? _last;
        private readonly FilterLiteral _greatest;
        private readonly bool _summarized;

        private Frontier(string path, byte[]? last, FilterLiteral greatest, bool summarized)
        {
            _path = path;
            _last = last;
            _greatest = greatest;
            _summarized = summarized;
        }

        /// <summary>The frontier of the last objects <paramref name="lasts"/> of the levels past 0; none for none.</summary>
        internal static Frontier? Of(System.Collections.Generic.IEnumerable<ObjectEntry> lasts, ClusteringKey key)
        {
            byte[]? last = null;
            bool keyed = true;
            bool summarized = true;
            bool any = false;
            FilterLiteral greatest = default;
            foreach (ObjectEntry entry in lasts)
            {
                if (entry.LastKey.IsEmpty)
                {
                    keyed = false;
                }
                else if (last is null || entry.LastKey.Span.SequenceCompareTo(last) > 0)
                {
                    last = entry.LastKey.ToArray();
                }

                if (summarized && entry.Summaries.TryGet(key.InstantPath, out ColumnSummary column) && column.HasMax)
                {
                    greatest = !any || KeyCursor.Compare(column.Max, greatest) > 0 ? column.Max : greatest;
                }
                else
                {
                    summarized = false;
                }

                any = true;
            }

            return any && ((keyed && last is not null) || summarized) ? new Frontier(key.InstantPath, keyed ? last : null, greatest, summarized) : null;
        }

        /// <summary>Whether every key of the object <paramref name="entry"/> describes lies at or before the frontier.</summary>
        internal bool Behind(ObjectEntry entry)
        {
            if (_last is not null && !entry.LastKey.IsEmpty)
            {
                return entry.LastKey.Span.SequenceCompareTo(_last) <= 0;
            }

            return _summarized && entry.Summaries.TryGet(_path, out ColumnSummary column) && column.HasMax
                && KeyCursor.Compare(column.Max, _greatest) <= 0;
        }
    }

    /// <summary>
    /// The objects of level 0 an exit takes once they pass the ceiling <paramref name="ceiling"/>, from
    /// <paramref name="level0"/>, and the objects of late rows it may leave there: those that came in order alone while
    /// the late, left to the merges of neighbours, hold less than half the ceiling, with room for what makes it up; every
    /// one, merged in, otherwise. Level 0 keeps within its ceiling whatever it holds. Null when no exit is due.
    /// </summary>
    internal static (System.Collections.Generic.List<CompactionInput> Sources, int Room)? Exit(
        System.Collections.Generic.List<CompactionInput> level0, Frontier? frontier, int ceiling)
    {
        if (level0.Count <= ceiling)
        {
            return null;
        }

        if (frontier is null)
        {
            return (level0, 0);
        }

        System.Collections.Generic.List<CompactionInput> fresh = level0.FindAll(input => !frontier.Behind(input.Entry));
        int room = (ceiling / 2) - (level0.Count - fresh.Count);
        return fresh.Count == 0 || room <= 0 ? (level0, 0) : (fresh, room);
    }

    /// <summary>The late rows passed their limit: the exit is run again as a merge with the objects they meet.</summary>
    internal sealed class Overflow : Exception
    {
        internal Overflow()
            : base("The late rows of a level-0 exit passed an eighth of the rows of the objects they meet, or the room level 0 has for them.")
        {
        }
    }
}
