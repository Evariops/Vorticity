// The fold a dataset's own tree runs with - docs/13-dataset.md §4.2's internal entry, which "carries
// the union of its children's key ranges and summaries", so that "a predicate that the node's
// summaries refute skips the whole subtree".
//
// SILENCE IS CONTAGIOUS, and this is where that rule is enforced. A child that summarises nothing
// may hold anything, so a parent above it can promise nothing about the columns below: one empty
// part makes the whole union empty. That looks wasteful -- a page of a thousand summarised objects
// loses its summary because one import had no statistics segment -- and it is exactly right. The
// alternative, a union over only the children that spoke, is a bound that some rows under the node
// violate, and a scan built on it drops rows silently. A lost pruning opportunity is measurable in
// bytes read; a lost row is not measurable at all.
using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>Folds the summaries an <see cref="ObjectEntry"/> carries up the tree.</summary>
public sealed class ObjectSummaryFold : ISummaryFold
{
    /// <summary>The one instance; it holds no state.</summary>
    public static ObjectSummaryFold Instance { get; } = new ObjectSummaryFold();

    private ObjectSummaryFold()
    {
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> OfLeaf(TreeEntry entry) => ObjectEntry.SummaryOf(entry.Value);

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Union(IReadOnlyList<ReadOnlyMemory<byte>> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            return default;
        }

        List<ObjectSummaries> decoded = new List<ObjectSummaries>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].IsEmpty)
            {
                return default;
            }

            decoded.Add(ObjectSummaries.FromBytes(parts[i].Span));
        }

        ObjectSummaries union = ObjectSummaries.Union(decoded);
        return union.Count == 0 ? default : union.ToBytes();
    }
}
