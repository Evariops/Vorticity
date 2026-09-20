using System;
using System.Diagnostics;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Layouts;

/// <summary>
/// Row-slices a canonical node: what a flat layout does to narrow a decoded array to the rows
/// asked for, and what a chunked layout does to the two partial chunks at the ends of a range.
/// Every case re-points at the same segment bytes through a narrower window, so a slice costs one
/// arena record per node of the canonical tree and no memory traffic at all.
/// </summary>
internal static class CanonicalSlice
{
    private const int StackBuffers = 8;

    /// <summary>
    /// Returns a canonical node covering <paramref name="length"/> rows starting at
    /// <paramref name="start"/> of <paramref name="nodeIndex"/>.
    /// </summary>
    /// <param name="context">The decode context owning the canonical arena.</param>
    /// <param name="nodeIndex">The node to slice.</param>
    /// <param name="start">First row, within the node.</param>
    /// <param name="length">Row count.</param>
    /// <returns>
    /// <paramref name="nodeIndex"/> itself when the slice covers the whole node, otherwise a new
    /// node index.
    /// </returns>
    /// <exception cref="VortexFormatException">The range escapes the node, or its dtype cannot be sliced.</exception>
    internal static int Slice(ArrayDecodeContext context, int nodeIndex, int start, int length) =>
        Slice(context.Canonical, context.Canonical, nodeIndex, start, length, depth: 1);

    /// <summary>
    /// Narrows a node held by <paramref name="source"/> into records appended to
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">The arena holding the node, and the storage the result will view.</param>
    /// <param name="destination">The arena the window's records are appended to.</param>
    /// <param name="nodeIndex">The node to narrow, indexed in <paramref name="source"/>.</param>
    /// <param name="start">First row of the window.</param>
    /// <param name="length">Rows in the window.</param>
    /// <returns>The window's index in <paramref name="destination"/>.</returns>
    /// <remarks>
    /// <para>
    /// SLICING COPIES NOTHING, AND ACROSS ARENAS IT STILL COPIES NOTHING. A slice is a record whose
    /// buffers are narrowed <b>views</b> onto the same storage, so a window of a retained chunk costs
    /// a handful of records however many rows or bytes it spans - including a <c>VarBinView</c>'s
    /// data buffers, which travel as views and are never rebuilt. That is the whole reason the
    /// string encodings can reach their ceiling.
    /// </para>
    /// <para>
    /// THE RESULT BORROWS <paramref name="source"/>'s MEMORY and is valid only while that arena is.
    /// Whoever calls this owes a lifetime argument; <see cref="Vorticity.Arrays.ScanContext"/>'s
    /// is that an entry touched during the current batch is never evicted.
    /// </para>
    /// <para>
    /// <c>ListView</c> needs one extra step and no extra bytes: its offsets are absolute into an
    /// elements CHILD named by an arena index, and an index means nothing in another arena, so the
    /// child's RECORDS are re-created here (<see cref="CanonicalArena.ReferenceFrom"/>) while its
    /// buffers stay views onto <paramref name="source"/>. Copying those bytes instead made a batch
    /// of a large list chunk cost the whole child -- the scan quadratic, restricted to one dtype,
    /// and measured at 16.9x the reference on the 1M-row axis.
    /// </para>
    /// </remarks>
    internal static int SliceAcross(
        CanonicalArena source, CanonicalArena destination, int nodeIndex, int start, int length) =>
        Slice(source, destination, nodeIndex, start, length, depth: 1);

    private static int Slice(
        CanonicalArena source, CanonicalArena destination, int nodeIndex, int start, int length, int depth)
    {
        // Depth is the canonical tree's, which mirrors the dtype's and is capped at parse time; the
        // explicit charge is what makes that a guarantee rather than an assumption.
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Canonical slice");

        CanonicalNode node = source.GetNode(nodeIndex);

        if (start < 0 || length < 0 || (long)start + length > node.Length)
        {
            LayoutsThrow.Format(
                $"A slice of [{start}, {start + (long)length}) escapes a canonical node of {node.Length} rows.");
        }

        // Only when the arenas are the same: an index is meaningless in another arena, so a
        // cross-arena whole-node window falls through and rebuilds the record with full-width views.
        if (start == 0 && length == node.Length && ReferenceEquals(source, destination))
        {
            return nodeIndex;
        }

        DType dtype = node.DType;

        // Null carries a fixed AllInvalid validity, and an Extension takes its storage child's, so
        // neither slices the parent's - which would build a second, identical bitmap slice.
        if (node.Kind == CanonicalKind.Null)
        {
            return destination.AddNull(dtype, length);
        }

        if (node.Kind == CanonicalKind.Extension)
        {
            int storageSlice = Slice(source, destination, node.StorageIndex, start, length, depth + 1);
            return destination.AddExtension(dtype, length, storageSlice);
        }

        Validity validity = SliceValidity(source, destination, node.Validity, start, length, depth);

        // EXHAUSTIVE BY CONSTRUCTION (PERF-AUDIT-v2.md §2.4bis, Z1b-c1): the `default:` this had was
        // the Struct arm, so a tenth kind would have been sliced field by field over fields it does
        // not have. Every kind is NAMED now -- Null and Extension included, at the throw the two
        // early returns above make unreachable -- and IDE0072 (error, see .editorconfig) fails the
        // build when a named kind is missing. Each arm is its own method so that this stays an
        // expression, which is the form IDE0072 checks.
        return node.Kind switch
        {
            CanonicalKind.Bool => SliceBool(destination, in node, dtype, validity, start, length),
            CanonicalKind.Primitive => SlicePrimitive(destination, in node, dtype, validity, start, length),
            CanonicalKind.Decimal => SliceDecimal(destination, in node, dtype, validity, start, length),
            CanonicalKind.VarBinView => SliceVarBinView(destination, in node, dtype, validity, start, length),
            CanonicalKind.ListView =>
                SliceListView(source, destination, in node, dtype, validity, start, length),
            CanonicalKind.FixedSizeList =>
                SliceFixedSizeList(source, destination, in node, dtype, validity, start, length, depth),
            CanonicalKind.Struct =>
                SliceStruct(source, destination, nodeIndex, dtype, validity, start, length, depth),
            CanonicalKind.Null or CanonicalKind.Extension => throw new UnreachableException(
                $"{node.Kind} returns above, before SliceValidity."),
            // EMPRUNTE (Z1b-c2b) : une fenetre sur une constante est la meme constante, plus
            // courte. `start` ne sert a rien : toutes les lignes portent la meme valeur.
            CanonicalKind.Constant =>
                destination.AddConstant(dtype, length, validity, node.ConstantElement),
            _ => throw new UnreachableException($"CanonicalKind {(byte)node.Kind} is not defined."),
        };
    }

    /// <remarks>
    /// The bit offset moves with the window; the bitmap itself is never shifted, which would be a
    /// copy per batch (contract §2.6 rule 5).
    /// </remarks>
    private static int SliceBool(
        CanonicalArena destination, in CanonicalNode node, DType dtype, Validity validity, int start, int length)
    {
        long firstBit = (long)node.BitOffset + start;
        int byteStart = (int)(firstBit >> 3);
        int bitOffset = (int)(firstBit & 7);
        int bytes = (int)(((long)bitOffset + length + 7) / 8);
        return destination.AddBool(dtype, length, validity, node.Bits.Slice(byteStart, bytes), bitOffset);
    }

    private static int SlicePrimitive(
        CanonicalArena destination, in CanonicalNode node, DType dtype, Validity validity, int start, int length)
    {
        int width = node.PType.ByteWidth();
        return destination.AddPrimitive(
            dtype, length, validity, node.PType, node.Values.Slice(start * width, length * width));
    }

    private static int SliceDecimal(
        CanonicalArena destination, in CanonicalNode node, DType dtype, Validity validity, int start, int length)
    {
        int width = DecimalStorage.ByteWidth(node.Storage);
        return destination.AddDecimal(
            dtype,
            length,
            validity,
            node.Storage,
            node.Precision,
            node.Scale,
            node.Values.Slice(start * width, length * width));
    }

    /// <remarks>
    /// Offsets are absolute into the elements array, so the elements child is shared unchanged and
    /// only the per-row offset and size vectors are narrowed.
    /// </remarks>
    private static int SliceListView(
        CanonicalArena source,
        CanonicalArena destination,
        in CanonicalNode node,
        DType dtype,
        Validity validity,
        int start,
        int length)
    {
        int offsetWidth = node.OffsetPType.ByteWidth();
        int sizeWidth = node.SizePType.ByteWidth();
        return destination.AddListView(
            dtype,
            length,
            validity,
            ReferenceEquals(source, destination)
                ? node.ElementsIndex
                : destination.ReferenceFrom(source, node.ElementsIndex),
            node.Offsets.Slice(start * offsetWidth, length * offsetWidth),
            node.OffsetPType,
            node.Sizes.Slice(start * sizeWidth, length * sizeWidth),
            node.SizePType);
    }

    private static int SliceFixedSizeList(
        CanonicalArena source,
        CanonicalArena destination,
        in CanonicalNode node,
        DType dtype,
        Validity validity,
        int start,
        int length,
        int depth)
    {
        uint size = node.FixedSize;
        int elementStart = ArrayDecodeContext.CheckedMultiply(start, (int)size, "fixed-size-list slice");
        int elementCount = ArrayDecodeContext.CheckedMultiply(length, (int)size, "fixed-size-list slice");
        int elements = Slice(source, destination, node.ElementsIndex, elementStart, elementCount, depth + 1);
        return destination.AddFixedSizeList(dtype, length, validity, elements, size);
    }

    private static int SliceVarBinView(
        CanonicalArena destination, in CanonicalNode node, DType dtype, Validity validity, int start, int length)
    {
        const int ViewWidth = 16;

        int bufferCount = node.DataBufferCount;
        Span<VortexBuffer> stack = stackalloc VortexBuffer[StackBuffers];
        Scratch<VortexBuffer> scratch = new Scratch<VortexBuffer>(bufferCount, stack);
        try
        {
            Span<VortexBuffer> buffers = scratch.Span;
            for (int i = 0; i < bufferCount; i++)
            {
                // The views keep their buffer indices, so the data buffers travel unchanged.
                buffers[i] = node.GetDataBuffer(i);
            }

            return destination.AddVarBinView(
                dtype, length, validity, node.Views.Slice(start * ViewWidth, length * ViewWidth), buffers);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int SliceStruct(
        CanonicalArena source,
        CanonicalArena destination,
        int nodeIndex,
        DType dtype,
        Validity validity,
        int start,
        int length,
        int depth)
    {
        int fieldCount = source.GetNode(nodeIndex).FieldCount;

        Span<int> stack = stackalloc int[16];
        Scratch<int> scratch = new Scratch<int>(fieldCount, stack);
        try
        {
            Span<int> fields = scratch.Span;
            for (int i = 0; i < fieldCount; i++)
            {
                // Re-read the node each iteration: the arena's record array can be reallocated by
                // the child slices this loop creates, which would invalidate a cached view.
                int child = source.GetNode(nodeIndex).GetFieldIndex(i);
                fields[i] = Slice(source, destination, child, start, length, depth + 1);
            }

            return destination.AddStruct(dtype, length, validity, fields);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static Validity SliceValidity(
        CanonicalArena source,
        CanonicalArena destination,
        Validity validity,
        int start,
        int length,
        int depth)
    {
        if (validity.Kind != ValidityKind.Bitmap)
        {
            return validity;
        }

        return Validity.Bitmap(Slice(source, destination, validity.CanonicalNodeIndex, start, length, depth + 1));
    }
}
