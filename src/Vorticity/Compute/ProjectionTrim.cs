// Dropping the columns a filter needed and the caller did not ask for.
//
// docs/03-architecture.md §3.4 fixes the order: "Where then Project. The filter sees columns that
// are not projected; they are read for filtering and discarded before the batch is produced."
//
// So a scan with a filter decodes the UNION of the projected and the filtered fields, and this is
// the step that removes the difference. It is a pure restructuring: every kept column's node is
// reused as it is, and only the struct nodes above them are rebuilt. No values are copied.
//
// The TARGET SCHEMA is computed once per scan (Projection.ProjectedSchema) and walked alongside the
// decoded tree, rather than being rebuilt here level by level. That is what keeps this file free of
// DType construction: the i-th kept field of a decoded struct is the i-th field of the target, by
// construction, because both orders are the schema's.
//
// The two masks line up for the same reason: a FieldMaskBuilder keeps its fields sorted and the
// struct layout reader emits selected fields in schema order, so decoded position p is the read
// mask's p-th named field. That is the one correspondence this file assumes, and it is asserted
// rather than trusted -- a mismatch is a planner bug, and it fails loudly instead of silently
// returning the wrong column.
using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>Restricts a decoded struct to the fields the caller actually projected.</summary>
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
