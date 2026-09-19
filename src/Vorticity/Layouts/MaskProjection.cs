// Applying a FieldMask to a canonical struct that a layout produced whole.
//
// A `vortex.struct` LAYOUT reads only the fields the mask selects and never materializes the rest,
// which is the lazy-resolution path of docs/08-semantics.md §4. But a struct column does not have
// to be stored that way: a `vortex.flat` layout can hold an entire struct array in one segment (the
// zones child of every zoned layout in the corpus is exactly that), and a `vortex.dict` layout's
// values may be a struct too. Those decode whole, so the mask is applied afterwards - here - and the
// batch's schema still matches the projection.
//
// WHAT THAT COST, AND WHAT IT COSTS NOW. On a file of fifty columns and fifty thousand rows stored
// as one flat node, a scan projecting ONE column used to read 66 and 67 microseconds against 64 and
// 63 for the whole thing: projecting was very slightly DEARER than not projecting, because it
// decoded the same fifty columns and then threw forty-nine away. The struct decoder now takes the
// mask itself and decodes only what is named, which is 78 microseconds down to 65 on that file --
// eighteen per cent, and less than the ratio of columns because a primitive column is cheap to
// decode and the rest of a scan is not.
//
// So this runs on what the decode did not narrow: a struct the mask reaches through a dict layout's
// values, a projection that narrows DEEPER than the top level, and every path that decodes with a
// selection rather than whole. `FlatLayoutReader` asks which happened before calling this.
using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Layouts;

internal static class MaskProjection
{
    /// <summary>
    /// Narrows a canonical node to the fields <paramref name="mask"/> selects.
    /// </summary>
    /// <param name="context">The decode context owning the canonical arena.</param>
    /// <param name="nodeIndex">The node to narrow.</param>
    /// <param name="mask">The projection.</param>
    /// <returns>
    /// <paramref name="nodeIndex"/> itself when the mask selects everything or the node is not a
    /// struct; otherwise a new struct node whose dtype holds only the selected fields, in order.
    /// </returns>
    internal static int Apply(ArrayDecodeContext context, int nodeIndex, in FieldMask mask) =>
        mask.IsAll ? nodeIndex : Apply(context, nodeIndex, in mask, depth: 1);

    private static int Apply(ArrayDecodeContext context, int nodeIndex, in FieldMask mask, int depth)
    {
        if (mask.IsAll)
        {
            return nodeIndex;
        }

        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Projection");

        CanonicalArena arena = context.Canonical;
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Kind != CanonicalKind.Struct)
        {
            // A mask names struct fields; anything else it reaches is a leaf and travels whole.
            return nodeIndex;
        }

        DType dtype = node.DType;
        int fieldCount = node.FieldCount;
        int length = node.Length;
        Validity validity = node.Validity;

        int selected = 0;
        for (int i = 0; i < fieldCount; i++)
        {
            if (mask.Includes(i))
            {
                selected++;
            }
        }

        if (selected == fieldCount)
        {
            // Every field is named at this level; the sub-masks may still narrow deeper, so the
            // walk continues rather than returning early.
            bool narrowsDeeper = false;
            for (int i = 0; i < fieldCount && !narrowsDeeper; i++)
            {
                narrowsDeeper = !mask.Descend(i).IsAll;
            }

            if (!narrowsDeeper)
            {
                return nodeIndex;
            }
        }

        // A DType is a managed type and cannot be stackalloc'd, so both scratch buffers come from
        // the pool; Scratch returns them in its finally.
        Scratch<int> children = new Scratch<int>(selected, default);
        Scratch<int> names = new Scratch<int>(selected, default);
        Scratch<DType> fieldTypes = new Scratch<DType>(selected, default);
        try
        {
            Span<int> childSpan = children.Span;
            Span<int> nameSpan = names.Span;
            Span<DType> typeSpan = fieldTypes.Span;

            int next = 0;
            for (int i = 0; i < fieldCount; i++)
            {
                if (!mask.Includes(i))
                {
                    continue;
                }

                FieldMask child = mask.Descend(i);

                // Re-read the node: the arena's records can be reallocated by the recursion below.
                int fieldNode = arena.GetNode(nodeIndex).GetFieldIndex(i);
                int projected = Apply(context, fieldNode, in child, depth + 1);

                childSpan[next] = projected;
                nameSpan[next] = context.Types.InternName(dtype.GetFieldNameUtf8(i));
                typeSpan[next] = DTypeImport.Into(context.Types, arena.GetNode(projected).DType);
                next++;
            }

            DType projectedType = context.Types.Struct(nameSpan, typeSpan, dtype.Nullability);
            return arena.AddStruct(projectedType, length, validity, childSpan);
        }
        finally
        {
            fieldTypes.Dispose();
            names.Dispose();
            children.Dispose();
        }
    }
}
