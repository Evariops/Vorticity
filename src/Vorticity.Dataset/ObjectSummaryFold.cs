using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>
/// Folds what an <see cref="ObjectEntry"/> carries up the tree: its summaries, whose union a walk
/// prunes by, and its tally, which a compaction plans by. A child that summarises nothing may hold
/// anything, so one empty part makes the union of the summaries empty; a child whose page was
/// written without a tally leaves its parent's tally unknown.
/// </summary>
/// <remarks>
/// A page's summary is the tally, behind a leading zero, then the summaries. Summaries alone start
/// with their column count, which is never zero, since a set of none is written as nothing: a page
/// written before tallies is read as one without, and a planner reads its leaves instead.
/// </remarks>
internal sealed class ObjectSummaryFold : ISummaryFold
{
    /// <summary>The first byte of a summary that carries a tally.</summary>
    private const byte Tallied = 0;

    /// <summary>The one instance.</summary>
    public static ObjectSummaryFold Instance { get; } = new ObjectSummaryFold();

    private ObjectSummaryFold()
    {
    }

    /// <inheritdoc/>
    /// <remarks>The whole entry, in place: <see cref="Union"/> reads its summaries and its tally out of it.</remarks>
    public ReadOnlyMemory<byte> OfLeaf(TreeEntry entry) => entry.Value;

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts, bool leaves)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            return default;
        }

        ObjectTally? tally = null;
        List<ObjectSummaries>? decoded = new List<ObjectSummaries>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            ReadOnlySpan<byte> summaries;
            ObjectTally? part;
            if (leaves)
            {
                summaries = ObjectEntry.SummaryOf(parts[i]).Span;
                part = ObjectTally.Of(parts[i].Span);
            }
            else
            {
                summaries = SummariesOf(parts[i].Span, out part);
            }

            tally = i == 0 ? part : tally is { } sum && part is { } more ? sum.With(more) : null;
            if (summaries.IsEmpty)
            {
                decoded = null;
            }
            else
            {
                decoded?.Add(ObjectSummaries.FromBytes(summaries));
            }
        }

        ObjectSummaries union = decoded is null ? ObjectSummaries.Empty : ObjectSummaries.Union(decoded);
        byte[] bounds = union.Count == 0 ? [] : union.ToBytes();
        if (tally is not { } known)
        {
            return bounds;
        }

        byte[] summary = new byte[1 + known.EncodedBytes + bounds.Length];
        summary[0] = Tallied;
        bounds.CopyTo(known.Write(summary.AsSpan(1)));
        return summary;
    }

    /// <summary>
    /// The summaries a page's summary carries, and its tally when it has one: the tally a page
    /// written before tallies does not.
    /// </summary>
    /// <exception cref="CommitFormatException">The summary's tally is not one.</exception>
    public static ReadOnlySpan<byte> SummariesOf(ReadOnlySpan<byte> summary, out ObjectTally? tally)
    {
        if (summary.IsEmpty || summary[0] != Tallied)
        {
            tally = null;
            return summary;
        }

        int at = 1;
        tally = ObjectTally.Read(summary, ref at);
        return summary[at..];
    }
}
