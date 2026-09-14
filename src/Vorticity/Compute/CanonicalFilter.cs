// Materializing the rows a filter selected - the second half of docs/01-scope.md F7.
//
// Given a decoded batch and the row indices that passed, produce a batch of just those rows. It is
// a gather, one canonical kind at a time, and two of the kinds are gathers of something OTHER than
// values, which is where the interesting decisions are:
//
//   * VARBINVIEW keeps its data buffers untouched and gathers only the 16-byte views. A view is
//     (length, prefix, buffer, offset), so a selected row's view still addresses the same bytes in
//     the same buffer -- the heap does not need compacting and copying it would be the single most
//     expensive thing this file could do.
//   * LISTVIEW is the same trick one level up: offsets and sizes are gathered, the elements child
//     is shared whole.
//
// FIXEDSIZELIST is the exception, and the reason it is not: its elements child has no offsets, so
// row i IS elements[i * size .. (i+1) * size]. Selecting rows therefore has to gather the elements
// too, through an expanded index list.
//
// Validity is gathered alongside, and collapses: a filtered column whose selected rows are all
// valid becomes AllValid rather than carrying a bitmap of ones.
using System;
using System.Buffers;
using System.Diagnostics;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>Rebuilds a canonical subtree over a subset of its rows.</summary>
internal static class CanonicalFilter
{
    private const int ViewSize = 16;

    /// <summary>
    /// Turns a per-row truth table into the list of selected row indices.
    /// </summary>
    /// <param name="states">One <see cref="Trilean"/> state per row.</param>
    /// <param name="indices">Receives the selected rows, ascending; must hold every row.</param>
    /// <returns>How many rows were selected.</returns>
    internal static int Select(ReadOnlySpan<byte> states, Span<int> indices)
    {
        int count = 0;
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i] == Trilean.True)
            {
                indices[count++] = i;
            }
        }

        return count;
    }

    /// <summary>
    /// Produces a node holding only <paramref name="indices"/> of <paramref name="nodeIndex"/>.
    /// </summary>
    /// <param name="arena">The arena, which receives the new nodes and buffers.</param>
    /// <param name="nodeIndex">The node to filter.</param>
    /// <param name="indices">The selected rows, ascending and within the node's length.</param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="NotSupportedException">The node's canonical form has no gather.</exception>
    /// <remarks>
    /// EXHAUSTIVE BY CONSTRUCTION (PERF-AUDIT-v2.md §2.4bis, Z1b-c1): every kind is NAMED, and the
    /// <c>_</c> arm throws instead of gathering. It used to be <c>default: FilterExtension</c>, so a
    /// tenth kind was gathered through an extension's storage child it does not have. IDE0072 --
    /// error here, see <c>.editorconfig</c> -- now fails the build when a named kind is missing.
    /// </remarks>
    internal static int Apply(CanonicalArena arena, int nodeIndex, ReadOnlySpan<int> indices)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        int count = indices.Length;

        return node.Kind switch
        {
            CanonicalKind.Null => arena.AddNull(node.DType, count),
            CanonicalKind.Bool => FilterBool(arena, node, indices),
            CanonicalKind.Primitive => FilterPrimitive(arena, node, indices),
            CanonicalKind.Decimal => FilterDecimal(arena, node, indices),
            CanonicalKind.VarBinView => FilterVarBinView(arena, node, indices),
            CanonicalKind.ListView => FilterListView(arena, node, indices),
            CanonicalKind.FixedSizeList => FilterFixedSizeList(arena, node, nodeIndex, indices),
            CanonicalKind.Struct => FilterStruct(arena, node, indices),
            CanonicalKind.Extension => FilterExtension(arena, node, indices),
            _ => throw new UnreachableException($"CanonicalKind {(byte)node.Kind} is not defined."),
        };
    }

    private static int FilterBool(CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices)
    {
        int count = indices.Length;
        Validity validity = FilterValidity(arena, node.Validity, indices);
        int bytes = CanonicalSupport.BitmapByteCount(count);
        VortexBuffer bits = arena.Allocate(Math.Max(bytes, 1), 1, out Span<byte> destination);

        ReadOnlySpan<byte> source = node.Bits.Span;
        int offset = node.BitOffset;
        for (int i = 0; i < count; i++)
        {
            if (CanonicalSupport.BitAt(source, offset + indices[i]))
            {
                CanonicalSupport.SetBit(destination, i);
            }
        }

        return arena.AddBool(node.DType, count, validity, bits, 0);
    }

    private static int FilterPrimitive(
        CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices)
    {
        int count = indices.Length;
        Validity validity = FilterValidity(arena, node.Validity, indices);
        int width = node.PType.ByteWidth();
        VortexBuffer values = Gather(arena, node.Values.Span, indices, width);
        return arena.AddPrimitive(node.DType, count, validity, node.PType, values);
    }

    private static int FilterDecimal(
        CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices)
    {
        int count = indices.Length;
        Validity validity = FilterValidity(arena, node.Validity, indices);
        int width = Types.Numerics.DecimalStorage.ByteWidth(node.Storage);
        VortexBuffer values = Gather(arena, node.Values.Span, indices, width);
        return arena.AddDecimal(
            node.DType, count, validity, node.Storage, node.Precision, node.Scale, values);
    }

    private static int FilterVarBinView(
        CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices)
    {
        int count = indices.Length;
        Validity validity = FilterValidity(arena, node.Validity, indices);

        // The views are gathered; the heap they point into is shared as it is.
        VortexBuffer views = Gather(arena, node.Views.Span, indices, ViewSize);

        int buffers = node.DataBufferCount;
        if (buffers == 0)
        {
            return arena.AddVarBinView(node.DType, count, validity, views, default);
        }

        VortexBuffer[] rented = ArrayPool<VortexBuffer>.Shared.Rent(buffers);
        try
        {
            for (int i = 0; i < buffers; i++)
            {
                rented[i] = node.GetDataBuffer(i);
            }

            return arena.AddVarBinView(
                node.DType, count, validity, views, rented.AsSpan(0, buffers));
        }
        finally
        {
            ArrayPool<VortexBuffer>.Shared.Return(rented, clearArray: true);
        }
    }

    private static int FilterListView(
        CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices)
    {
        int count = indices.Length;
        Validity validity = FilterValidity(arena, node.Validity, indices);
        VortexBuffer offsets = Gather(
            arena, node.Offsets.Span, indices, node.OffsetPType.ByteWidth());
        VortexBuffer sizes = Gather(arena, node.Sizes.Span, indices, node.SizePType.ByteWidth());

        return arena.AddListView(
            node.DType, count, validity, node.ElementsIndex, offsets, node.OffsetPType, sizes,
            node.SizePType);
    }

    private static int FilterFixedSizeList(
        CanonicalArena arena, CanonicalNode node, int nodeIndex, ReadOnlySpan<int> indices)
    {
        int count = indices.Length;
        Validity validity = FilterValidity(arena, node.Validity, indices);
        int size = checked((int)node.FixedSize);

        if (size == 0)
        {
            return arena.AddFixedSizeList(node.DType, count, validity, node.ElementsIndex, 0);
        }

        // No offsets to gather: the elements themselves have to be, `size` at a time.
        int elementCount = checked(count * size);
        int[] expanded = ArrayPool<int>.Shared.Rent(Math.Max(elementCount, 1));
        try
        {
            for (int i = 0; i < count; i++)
            {
                int start = indices[i] * size;
                for (int j = 0; j < size; j++)
                {
                    expanded[(i * size) + j] = start + j;
                }
            }

            int elements = Apply(arena, node.ElementsIndex, expanded.AsSpan(0, elementCount));
            return arena.AddFixedSizeList(node.DType, count, validity, elements, node.FixedSize);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(expanded);
        }
    }

    private static int FilterStruct(
        CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices)
    {
        int count = indices.Length;
        Validity validity = FilterValidity(arena, node.Validity, indices);
        int fields = node.FieldCount;

        if (fields == 0)
        {
            return arena.AddStruct(node.DType, count, validity, default);
        }

        int[] children = ArrayPool<int>.Shared.Rent(fields);
        try
        {
            for (int i = 0; i < fields; i++)
            {
                // Re-read the node each time: Apply adds nodes, and the arena may have moved its
                // record array out from under a stale CanonicalNode.
                children[i] = Apply(arena, arena.GetNode(NodeIndexOfField(arena, node, i)), indices);
            }

            return arena.AddStruct(node.DType, count, validity, children.AsSpan(0, fields));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(children);
        }
    }

    private static int FilterExtension(
        CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices)
    {
        if (node.Kind != CanonicalKind.Extension)
        {
            throw new NotSupportedException($"A filter cannot gather a {node.Kind} column.");
        }

        int storage = Apply(arena, node.StorageIndex, indices);
        return arena.AddExtension(node.DType, indices.Length, storage);
    }

    /// <summary>Gathers fixed-width elements into a fresh buffer.</summary>
    private static VortexBuffer Gather(
        CanonicalArena arena, ReadOnlySpan<byte> source, ReadOnlySpan<int> indices, int width)
    {
        int count = indices.Length;
        if (count == 0 || width == 0)
        {
            return VortexBuffer.Empty;
        }

        VortexBuffer buffer = arena.Allocate(count * width, width, out Span<byte> destination);
        for (int i = 0; i < count; i++)
        {
            source.Slice(indices[i] * width, width).CopyTo(destination.Slice(i * width, width));
        }

        return buffer;
    }

    /// <summary>
    /// Gathers a validity, collapsing the result: a selection that happens to contain no null is
    /// AllValid, not a bitmap of ones.
    /// </summary>
    /// <summary>
    /// Gathers a validity, collapsing to AllValid or AllInvalid where the selection allows.
    /// </summary>
    /// <param name="arena">The arena.</param>
    /// <param name="validity">The validity to gather.</param>
    /// <param name="indices">The selected rows.</param>
    /// <remarks>Internal so a specialized selective decoder can reuse it rather than copy it.</remarks>
    internal static Validity FilterValidity(
        CanonicalArena arena, Validity validity, ReadOnlySpan<int> indices)
    {
        if (validity.Kind != ValidityKind.Bitmap)
        {
            return validity;
        }

        ValidityMask mask = ValidityMask.From(arena, validity);
        int count = indices.Length;
        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            if (mask.IsValid(indices[i]))
            {
                valid++;
            }
        }

        if (valid == count)
        {
            return Validity.AllValid;
        }

        if (valid == 0)
        {
            return Validity.AllInvalid;
        }

        int bytes = CanonicalSupport.BitmapByteCount(count);
        VortexBuffer bits = arena.Allocate(Math.Max(bytes, 1), 1, out Span<byte> destination);
        for (int i = 0; i < count; i++)
        {
            if (mask.IsValid(indices[i]))
            {
                CanonicalSupport.SetBit(destination, i);
            }
        }

        // The bitmap node's own dtype is non-nullable bool, which is what Validity::DTYPE is.
        int node = arena.AddBool(
            arena.GetNode(validity.CanonicalNodeIndex).DType, count, Validity.NonNullable, bits, 0);
        return Validity.Bitmap(node);
    }

    private static int NodeIndexOfField(CanonicalArena arena, CanonicalNode node, int field) =>
        node.GetFieldIndex(field);

    private static int Apply(CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices) =>
        Apply(arena, node.Index, indices);
}
