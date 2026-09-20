using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// Restricts a decoded struct to the fields the caller actually projected: filtering reads columns
/// the projection drops, so a scan with a filter decodes the union of both and this step removes
/// the difference, reusing every kept node as it is and rebuilding only the structs above them.
/// The target dtype is walked alongside the decoded tree rather than rebuilt, since both orders are
/// the schema's; the correspondence is asserted rather than trusted, because a mismatch would
/// otherwise return the wrong column in silence.
/// </summary>
internal static class ProjectionTrim
{
    /// <summary>
    /// Rebuilds <paramref name="nodeIndex"/> keeping only what <paramref name="keep"/> selects.
    /// </summary>
    /// <param name="arena">The arena, which receives the rebuilt struct nodes.</param>
    /// <param name="nodeIndex">The decoded node, whose fields are <paramref name="read"/>'s.</param>
    /// <param name="read">The mask the batch was decoded under: the union.</param>
    /// <param name="keep">The mask the caller projected.</param>
    /// <param name="target">The dtype the trimmed node must have.</param>
    /// <returns>The trimmed node's index, or <paramref name="nodeIndex"/> when nothing is dropped.</returns>
    internal static int Apply(
        CanonicalArena arena, int nodeIndex, in FieldMask read, in FieldMask keep, DType target)
    {
        if (keep.IsAll)
        {
            return nodeIndex;
        }

        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Kind != CanonicalKind.Struct)
        {
            // A non-struct has no fields, so the union never widened it.
            return nodeIndex;
        }

        int decodedFields = node.FieldCount;
        if (!read.IsAll && read.NamedFieldCount != decodedFields)
        {
            throw new InvalidOperationException(
                $"The scan decoded {decodedFields} fields under a mask naming " +
                $"{read.NamedFieldCount}; the projection and the decode disagree.");
        }

        int[] children = ArrayPool<int>.Shared.Rent(Math.Max(target.FieldCount, 1));
        try
        {
            int kept = 0;
            for (int p = 0; p < decodedFields; p++)
            {
                int schemaField = read.IsAll ? p : read.GetNamedField(p);
                if (!keep.Includes(schemaField))
                {
                    continue;
                }

                if (kept >= target.FieldCount)
                {
                    throw new InvalidOperationException(
                        "The projection keeps more fields than its own schema declares.");
                }

                // Re-read the node each time: Apply adds nodes below, and the arena may have grown
                // its record array out from under a stale CanonicalNode.
                int child = arena.GetNode(nodeIndex).GetFieldIndex(p);
                FieldMask childRead = read.IsAll ? FieldMask.All : read.Descend(schemaField);
                FieldMask childKeep = keep.Descend(schemaField);
                children[kept] = Apply(arena, child, in childRead, in childKeep, target.GetField(kept));
                kept++;
            }

            if (kept != target.FieldCount)
            {
                throw new InvalidOperationException(
                    $"The projection kept {kept} fields where its schema declares {target.FieldCount}.");
            }

            CanonicalNode current = arena.GetNode(nodeIndex);
            return arena.AddStruct(target, current.Length, current.Validity, children.AsSpan(0, kept));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(children);
        }
    }
}
