using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Layouts;

/// <summary>
/// Applies a <see cref="FieldMask"/> to a canonical struct a layout produced whole, so that the
/// batch's schema matches the projection either way.
/// </summary>
/// <remarks>
/// A <c>vortex.struct</c> layout hands the mask to the struct decoder and never materializes the
/// fields it does not name, which is far cheaper than decoding every field and discarding most of
/// them. But a struct column need not be stored that way: a <c>vortex.flat</c> layout can hold an
/// entire struct array in one segment, and a <c>vortex.dict</c> layout's values may be a struct.
/// Those decode whole, so this narrows afterwards — on a struct the mask reaches through a dict
/// layout's values, on a projection that narrows below the top level, and on every path that
/// decodes with a selection rather than whole.
/// </remarks>
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
