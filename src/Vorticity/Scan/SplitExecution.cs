using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Layouts;

namespace Vorticity.Scan;

/// <summary>
/// Registers and executes one split of a scan — the two halves of a batch's read, shared by every
/// consumer of a split: the batch enumerator's lanes, sequential and pipelined alike, and the
/// terminals, which decode a split to count it and never build a batch from it.
/// </summary>
/// <remarks>
/// The row selection is pushed down in this one place, which is what makes a terminal unable to
/// count a row a scan would not return. It travels down the layout tree in the same coordinate
/// space as the row range beside it, so a reader that re-partitions rows re-partitions the
/// selection too, and one that does not passes it on by doing nothing. At the flat leaf it reaches
/// the array decoders, where an encoding able to honour it does — <c>fastlanes.bitpacked</c>
/// indexes positionally, <c>vortex.dict</c> takes on the codes — and every other one decodes the
/// node and gathers.
/// </remarks>
internal static class SplitExecution
{
    /// <summary>Phase 1: registers the split's segments. No I/O, no decoding, no allocation.</summary>
    /// <param name="context">The lane's context, whose request set receives the segments.</param>
    /// <param name="tree">The file's layout tree.</param>
    /// <param name="mask">The columns read.</param>
    /// <param name="split">The split, in file row coordinates.</param>
    internal static void Register(ScanContext context, LayoutTree tree, in FieldMask mask, RowRange split)
    {
        LayoutNode root = tree.Root;
        LayoutReaderTable.Require(in root).RegisterSegments(in root, split, in mask, context.Segments);
    }

    /// <summary>
    /// Phase 3, after the read: executes the split, pushing the rows of <paramref name="take"/>
    /// down the layout tree rather than gathering them back out afterwards.
    /// </summary>
    /// <param name="context">The lane's context, holding the split's segments.</param>
    /// <param name="tree">The file's layout tree.</param>
    /// <param name="mask">The columns read.</param>
    /// <param name="split">The split, in file row coordinates.</param>
    /// <param name="take">The scan's row selection, or null for every row.</param>
    /// <returns>The root of the decoded split in the context's canonical arena.</returns>
    /// <remarks>
    /// The split was already chosen because it holds at least one wanted row, so the selection
    /// never has to reject the whole split. It is in the split's coordinate space, and
    /// <c>Execute</c> is called with the split as its row range, so the two agree at the root by
    /// construction.
    /// </remarks>
    internal static int Execute(
        ScanContext context, LayoutTree tree, in FieldMask mask, RowRange split, RowSelection? take)
    {
        LayoutNode root = tree.Root;
        LayoutReader reader = LayoutReaderTable.Require(in root);
        if (take is null)
        {
            return reader.Execute(in root, split, in mask, context);
        }

        int rows = (int)(split.End - split.Start);
        int[] indices = ArrayPool<int>.Shared.Rent(Math.Max(rows, 1));
        try
        {
            int count = take.LocalIndices(split, indices);
            if (count == rows)
            {
                // Every row of the split is wanted: there is nothing to push down, and pushing an
                // identity selection would cost a gather for no reason.
                return reader.Execute(in root, split, in mask, context);
            }

            // LocalIndices produces split-relative rows; the root's row space is the file's, which
            // is what `split` is expressed in.
            for (int i = 0; i < count; i++)
            {
                indices[i] += (int)split.Start;
            }

            (int[]? Buffer, int Count) saved = context.ExchangeSelection(indices, count);
            try
            {
                return reader.Execute(in root, split, in mask, context);
            }
            finally
            {
                context.ExchangeSelection(saved.Buffer, saved.Count);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indices);
        }
    }
}
