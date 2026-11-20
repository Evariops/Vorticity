using System;
using System.Collections.Generic;
using Vorticity.Layouts;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Types.Variants;

/// <summary>
/// A variant column a job writes under one shredding, whose rows the job's inputs take as their own files' shreddings hold
/// them, beside the rows they put back together (<see cref="Writing.VariantShredder.Shredding.TakeGroup"/>): the job's
/// writers then take the runs of a block from what the inputs took, and read no row of it
/// (<see cref="Writing.VariantShredder.Shredding.AppendRange"/>). One for the job, which its inputs' scans and its writers
/// share.
/// </summary>
/// <remarks>
/// A writer says after each block whether its rows were worth taking so (<see cref="Note"/>), and the inputs take the next
/// windows they decode only while they are (<see cref="Wanted"/>): what a window took of rows a writer then reads is work
/// for nothing. No window is taken before the first block says so.
/// </remarks>
internal sealed class VariantTake(VariantShredding plan)
{
    private volatile bool _wanted;

    /// <summary>The shredding the job writes the column under.</summary>
    internal VariantShredding Plan { get; } = plan;

    /// <summary>Whether the inputs take the windows they decode next, as the last block a writer assembled said.</summary>
    internal bool Wanted => _wanted;

    /// <summary>A writer assembled a block of the column, whose rows were worth taking from what the inputs took of them, or not.</summary>
    internal void Note(bool worth) => _wanted = worth;

    // The shreddings no window holds now, lent again before another is made: one holds a node for each key of the plan, some
    // four hundred for a log's attributes, and an input decodes a window every couple of thousand rows.
    private readonly Stack<Writing.VariantShredder.Shredding> _spare = [];

    /// <summary>
    /// A shredding of the plan for a decode to take a window's rows into, whose rows other shreddings take ranges of
    /// (<see cref="Writing.VariantShredder.Shredding.Index"/>): one given back, or a new one. It comes back once the arena
    /// the window lies in is reset, or from the decode that took nothing into it (<see cref="Return"/>).
    /// </summary>
    internal Writing.VariantShredder.Shredding Rent()
    {
        lock (_spare)
        {
            if (_spare.TryPop(out Writing.VariantShredder.Shredding? spare))
            {
                return spare;
            }
        }

        return new Writing.VariantShredder.Shredding(Plan, ranged: true) { Lender = this };
    }

    /// <summary>Takes back <paramref name="lent"/>, which no window holds any more.</summary>
    internal void Return(Writing.VariantShredder.Shredding lent)
    {
        lock (_spare)
        {
            _spare.Push(lent);
        }
    }
}

/// <summary>
/// The chunks of a file's variant columns a scan of it takes as their shredding holds them (<see cref="VariantTake"/>),
/// each found by the segment the file's layout lays it in; per scan, shared by its lanes.
/// </summary>
internal sealed class TakenVariants
{
    private readonly ulong[] _starts;
    private readonly VariantTake[] _takes;

    private TakenVariants(ulong[] starts, VariantTake[] takes)
    {
        _starts = starts;
        _takes = takes;
    }

    /// <summary>
    /// The chunks of <paramref name="file"/>'s columns <paramref name="columns"/> names, a top-level field of the file and
    /// what takes it each; null when none is laid out in chunks of one segment each.
    /// </summary>
    internal static TakenVariants? Of(VortexFile file, ReadOnlySpan<(int Field, VariantTake Take)> columns)
    {
        List<(ulong Start, VariantTake Take)> found = [];
        ReadOnlySpan<SegmentSpec> specs = file.SegmentSpecs;
        foreach ((int field, VariantTake take) in columns)
        {
            try
            {
                (List<(LayoutNode Flat, long Start)> laid, _) = VortexFileWriter.AppendPlan.ColumnChunks(file.LayoutTree, field);
                foreach ((LayoutNode flat, _) in laid)
                {
                    if (flat.Segments is [uint segment] && segment < (uint)specs.Length)
                    {
                        found.Add((specs[(int)segment].Offset, take));
                    }
                }
            }
            catch (VortexUnsupportedException)
            {
                // A column laid out otherwise: its rows are read, as any column's.
            }
        }

        if (found.Count == 0)
        {
            return null;
        }

        found.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        ulong[] starts = new ulong[found.Count];
        VariantTake[] takes = new VariantTake[found.Count];
        for (int i = 0; i < found.Count; i++)
        {
            (starts[i], takes[i]) = found[i];
        }

        return new TakenVariants(starts, takes);
    }

    /// <summary>What takes the chunk laid in the segment at <paramref name="offset"/>; null for a chunk of another column.</summary>
    internal VariantTake? Find(ulong offset)
    {
        int at = Array.BinarySearch(_starts, offset);
        return at >= 0 ? _takes[at] : null;
    }
}
