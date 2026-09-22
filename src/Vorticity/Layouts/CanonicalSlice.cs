using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
    /// Slicing copies nothing, within one arena or across two: a slice is a record whose buffers are
    /// narrowed <b>views</b> onto the same storage, so a window of a retained chunk costs a handful
    /// of records however many rows or bytes it spans - including a <c>VarBinView</c>'s data
    /// buffers, which travel as views and are never rebuilt.
    /// </para>
    /// <para>
    /// The result borrows <paramref name="source"/>'s memory and is valid only while that arena is.
    /// Whoever calls this owes a lifetime argument; <see cref="Vorticity.Arrays.ScanContext"/>'s
    /// is that an entry touched during the current batch is never evicted.
    /// </para>
    /// <para>
    /// <c>ListView</c> needs one extra step and no extra bytes: its offsets are absolute into an
    /// elements child named by an arena index, and an index means nothing in another arena, so the
    /// child's records are re-created here (<see cref="CanonicalArena.ReferenceFrom"/>) while its
    /// buffers stay views onto <paramref name="source"/>. Copying those bytes instead would make a
    /// batch of a large list chunk pay for the whole child, which turns a scan over many batches
    /// quadratic.
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

        // Exhaustive by construction: every kind is named, Null and Extension included, at a throw
        // the two early returns above make unreachable. There is no catch-all arm that would slice
        // a new kind field by field over fields it does not have; IDE0072 is an error here, so the
        // build fails as soon as a kind is missing. Each arm is its own method so that this stays
        // an expression, which is the form IDE0072 checks.
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
            // A window on a constant is the same constant, shorter: every row carries the same
            // value, so `start` plays no part.
            CanonicalKind.Constant =>
                destination.AddConstant(dtype, length, validity, node.ConstantElement),
            CanonicalKind.Dictionary =>
                SliceDictionary(source, destination, in node, dtype, validity, start, length),
            CanonicalKind.RunEnd =>
                SliceRunEnd(source, destination, nodeIndex, dtype, validity, start, length, depth),
            _ => throw new UnreachableException($"CanonicalKind {(byte)node.Kind} is not defined."),
        };
    }

    /// <remarks>
    /// The codes narrow like any fixed-width buffer; the distinct values are every row's, so they
    /// are shared whole, as a list's elements are.
    /// </remarks>
    private static int SliceDictionary(
        CanonicalArena source,
        CanonicalArena destination,
        in CanonicalNode node,
        DType dtype,
        Validity validity,
        int start,
        int length)
    {
        int values = ReferenceEquals(source, destination)
            ? node.EncodedValuesIndex
            : destination.ReferenceFrom(source, node.EncodedValuesIndex);
        return destination.AddDictionary(
            dtype, length, validity, node.Codes.Slice(start * sizeof(uint), length * sizeof(uint)), values);
    }

    /// <remarks>
    /// The runs the window touches are kept and their ends rebased to it, the first and the last
    /// clipped, since the ends are relative to row 0 and a window may start or stop inside a run.
    /// That is a buffer of one end per kept run; a window from row 0 that stops on a run boundary
    /// needs none and narrows the ends in place.
    /// </remarks>
    private static int SliceRunEnd(
        CanonicalArena source,
        CanonicalArena destination,
        int nodeIndex,
        DType dtype,
        Validity validity,
        int start,
        int length,
        int depth)
    {
        CanonicalNode node = source.GetNode(nodeIndex);
        VortexBuffer endsBuffer = node.RunEnds;
        ReadOnlySpan<uint> ends = MemoryMarshal.Cast<byte, uint>(endsBuffer.Span);
        int valuesIndex = node.EncodedValuesIndex;

        if (length == 0)
        {
            int none = Slice(source, destination, valuesIndex, 0, 0, depth + 1);
            return destination.AddRunEnd(dtype, 0, validity, VortexBuffer.Empty, none);
        }

        uint stop = (uint)(start + length);
        int first = FirstEndAbove(ends, (uint)start);
        int last = FirstEndAbove(ends, stop - 1);
        int count = last - first + 1;

        VortexBuffer rebased;
        if (start == 0 && ends[last] == stop)
        {
            rebased = endsBuffer.Slice(0, count * sizeof(uint));
        }
        else
        {
            rebased = destination.AllocateUninitialized(count * sizeof(uint), sizeof(uint), out Span<byte> raw);
            Span<uint> into = MemoryMarshal.Cast<byte, uint>(raw)[..count];
            for (int i = 0; i < into.Length; i++)
            {
                into[i] = Math.Min(ends[first + i], stop) - (uint)start;
            }
        }

        int values = Slice(source, destination, valuesIndex, first, count, depth + 1);
        return destination.AddRunEnd(dtype, length, validity, rebased, values);
    }

    /// <summary>The first run whose exclusive end is above <paramref name="row"/>, by binary search.</summary>
    private static int FirstEndAbove(ReadOnlySpan<uint> ends, uint row)
    {
        int low = 0;
        int high = ends.Length - 1;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (ends[middle] > row)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    /// <remarks>
    /// The bit offset moves with the window; the bitmap itself is never shifted, which would cost a
    /// copy per batch.
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

    /// <summary>
    /// The validity of rows <c>[start, start + length)</c> of a node whose validity is
    /// <paramref name="validity"/>, in the same arena: a bitmap is sliced, and every other kind holds
    /// for any range as it holds for the whole.
    /// </summary>
    /// <param name="arena">The arena holding the validity's bitmap, which receives its slice.</param>
    /// <param name="validity">The whole node's validity.</param>
    /// <param name="start">The range's first row.</param>
    /// <param name="length">The range's row count.</param>
    internal static Validity ValidityRange(CanonicalArena arena, Validity validity, int start, int length) =>
        SliceValidity(arena, arena, validity, start, length, depth: 1);

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
