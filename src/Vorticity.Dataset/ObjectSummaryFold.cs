using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>
/// Folds the summaries an <see cref="ObjectEntry"/> carries up the tree. A child that summarises
/// nothing may hold anything, so one empty part makes the whole union empty.
/// </summary>
public sealed class ObjectSummaryFold : ISummaryFold
{
    /// <summary>The one instance.</summary>
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
