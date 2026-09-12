// Row-slicing a canonical node, which is what PHASE1-CONTRACTS.md §11.3 asks the flat layout to do
// ("decode the whole array, then slice to `rows`") and what a chunked layout needs for the two
// partial chunks at the ends of a range.
//
// Every case is ZERO-COPY: a slice re-points at the same segment bytes with a narrower window, so
// slicing costs one arena record per node of the canonical tree and no memory traffic. That is the
// whole reason the flat reader can decode a 8192-row chunk and hand back rows 1023..1025 without
// copying anything.
using System;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Layouts;

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
        Slice(context, nodeIndex, start, length, depth: 1);

    private static int Slice(ArrayDecodeContext context, int nodeIndex, int start, int length, int depth)
    {
        // Depth is the canonical tree's, which mirrors the dtype's and is capped at parse time; the
        // explicit charge is what makes that a guarantee rather than an assumption.
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Canonical slice");

        CanonicalArena arena = context.Canonical;
        CanonicalNode node = arena.GetNode(nodeIndex);

        if (start < 0 || length < 0 || (long)start + length > node.Length)
        {
            LayoutsThrow.Format(
                $"A slice of [{start}, {start + (long)length}) escapes a canonical node of {node.Length} rows.");
        }

        if (start == 0 && length == node.Length)
        {
            return nodeIndex;
        }

        DType dtype = node.DType;

        // Null carries a fixed AllInvalid validity, and an Extension takes its storage child's, so
        // neither slices the parent's - which would build a second, identical bitmap slice.
        if (node.Kind == CanonicalKind.Null)
        {
            return arena.AddNull(dtype, length);
        }

        if (node.Kind == CanonicalKind.Extension)
        {
            int storageSlice = Slice(context, node.StorageIndex, start, length, depth + 1);
            return arena.AddExtension(dtype, length, storageSlice);
        }

        Validity validity = SliceValidity(context, node.Validity, start, length, depth);

        switch (node.Kind)
        {
            case CanonicalKind.Bool:
            {
                // The bit offset moves with the window; the bitmap itself is never shifted, which
                // would be a copy per batch (contract §2.6 rule 5).
                long firstBit = (long)node.BitOffset + start;
                int byteStart = (int)(firstBit >> 3);
                int bitOffset = (int)(firstBit & 7);
                int bytes = (int)(((long)bitOffset + length + 7) / 8);
                return arena.AddBool(dtype, length, validity, node.Bits.Slice(byteStart, bytes), bitOffset);
            }

            case CanonicalKind.Primitive:
            {
                int width = node.PType.ByteWidth();
                return arena.AddPrimitive(
                    dtype, length, validity, node.PType, node.Values.Slice(start * width, length * width));
            }

            case CanonicalKind.Decimal:
            {
                int width = DecimalStorage.ByteWidth(node.Storage);
                return arena.AddDecimal(
                    dtype,
                    length,
                    validity,
                    node.Storage,
                    node.Precision,
                    node.Scale,
                    node.Values.Slice(start * width, length * width));
            }

            case CanonicalKind.VarBinView:
                return SliceVarBinView(arena, in node, dtype, validity, start, length);

            case CanonicalKind.ListView:
            {
                // Offsets are absolute into the elements array, so the elements child is shared
                // unchanged and only the per-row offset and size vectors are narrowed.
                int offsetWidth = node.OffsetPType.ByteWidth();
                int sizeWidth = node.SizePType.ByteWidth();
                return arena.AddListView(
                    dtype,
                    length,
                    validity,
                    node.ElementsIndex,
                    node.Offsets.Slice(start * offsetWidth, length * offsetWidth),
                    node.OffsetPType,
                    node.Sizes.Slice(start * sizeWidth, length * sizeWidth),
                    node.SizePType);
            }

            case CanonicalKind.FixedSizeList:
            {
                uint size = node.FixedSize;
                int elementStart = ArrayDecodeContext.CheckedMultiply(start, (int)size, "fixed-size-list slice");
                int elementCount = ArrayDecodeContext.CheckedMultiply(length, (int)size, "fixed-size-list slice");
                int elements = Slice(context, node.ElementsIndex, elementStart, elementCount, depth + 1);
                return arena.AddFixedSizeList(dtype, length, validity, elements, size);
            }

            default:
                // Struct: every field is narrowed to the same window.
                return SliceStruct(context, nodeIndex, dtype, validity, start, length, depth);
        }
    }

    private static int SliceVarBinView(
        CanonicalArena arena, in CanonicalNode node, DType dtype, Validity validity, int start, int length)
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

            return arena.AddVarBinView(
                dtype, length, validity, node.Views.Slice(start * ViewWidth, length * ViewWidth), buffers);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static int SliceStruct(
        ArrayDecodeContext context,
        int nodeIndex,
        DType dtype,
        Validity validity,
        int start,
        int length,
        int depth)
    {
        CanonicalArena arena = context.Canonical;
        int fieldCount = arena.GetNode(nodeIndex).FieldCount;

        Span<int> stack = stackalloc int[16];
        Scratch<int> scratch = new Scratch<int>(fieldCount, stack);
        try
        {
            Span<int> fields = scratch.Span;
            for (int i = 0; i < fieldCount; i++)
            {
                // Re-read the node each iteration: the arena's record array can be reallocated by
                // the child slices this loop creates, which would invalidate a cached view.
                int child = arena.GetNode(nodeIndex).GetFieldIndex(i);
                fields[i] = Slice(context, child, start, length, depth + 1);
            }

            return arena.AddStruct(dtype, length, validity, fields);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static Validity SliceValidity(
        ArrayDecodeContext context, Validity validity, int start, int length, int depth)
    {
        if (validity.Kind != ValidityKind.Bitmap)
        {
            return validity;
        }

        return Validity.Bitmap(Slice(context, validity.CanonicalNodeIndex, start, length, depth + 1));
    }
}
