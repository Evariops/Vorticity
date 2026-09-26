using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Buffers;
using Vorticity.Types;

namespace Vorticity.Compute;

/// <summary>
/// Rebuilds a canonical subtree over a subset of its rows. Views, offsets and sizes are gathered
/// while the buffers they address are shared untouched, so a filter never compacts a string heap
/// nor a list's elements; a fixed-size list is the exception, since its elements carry no offsets
/// and have to be gathered through an expanded index list.
/// </summary>
internal static class CanonicalFilter
{
    private const int ViewSize = 16;

    /// <summary>
    /// Turns a per-row truth table into the list of selected row indices.
    /// </summary>
    /// <param name="states">One <see cref="Trilean"/> state per row.</param>
    /// <param name="indices">
    /// Receives the selected rows, ascending; must have room for every row, selected or not,
    /// because each row is written at the next free slot before the state decides whether it keeps it.
    /// </param>
    /// <returns>How many rows were selected.</returns>
    /// <remarks>
    /// No branch on the state: a filter's verdicts follow the data, and a branch taken on a row's
    /// verdict is mispredicted as often as the data is irregular.
    /// </remarks>
    internal static int Select(ReadOnlySpan<byte> states, Span<int> indices)
    {
        Span<int> slots = indices[..states.Length];
        int count = 0;
        for (int i = 0; i < states.Length; i++)
        {
            slots[count] = i;
            count += states[i] == Trilean.True ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    /// Produces a node holding only <paramref name="indices"/> of <paramref name="nodeIndex"/>.
    /// </summary>
    /// <param name="arena">The arena, which receives the new nodes and buffers.</param>
    /// <param name="nodeIndex">The node to filter.</param>
    /// <param name="indices">
    /// The selected rows, within the node's length, in the order the result carries them: every
    /// gather below is positional, so a permutation lays a batch out in another order and a
    /// filter's ascending selection is the special case.
    /// </param>
    /// <returns>The new node's index.</returns>
    /// <exception cref="NotSupportedException">The node's canonical form has no gather.</exception>
    /// <remarks>
    /// Every kind is named and the <c>_</c> arm throws rather than gathering, so a kind added
    /// without its own arm fails the build instead of being gathered through a child it does not
    /// have.
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
            CanonicalKind.FixedSizeList => FilterFixedSizeList(arena, node, indices),
            CanonicalKind.Struct => FilterStruct(arena, node, indices),
            CanonicalKind.Extension => FilterExtension(arena, node, indices),
            // Filtering a constant yields a constant: only the length and the validity change, the
            // element is the same one, and a gather over rows that all hold it would gather
            // nothing.
            CanonicalKind.Constant => arena.AddConstant(
                node.DType,
                count,
                FilterValidity(arena, node.Validity, indices),
                node.ConstantElement),

            // The codes are one per row and are what the selection picks; the distinct values are
            // shared by every row, so they travel untouched.
            CanonicalKind.Dictionary => FilterDictionary(arena, node, indices),

            // A selection breaks runs apart, and a gather through the ends would cost a search per
            // row for a form the consumer may never read: the runs are expanded once and gathered.
            CanonicalKind.RunEnd => Apply(arena, arena.MaterializeEncoded(nodeIndex), indices),
            _ => throw new UnreachableException($"CanonicalKind {(byte)node.Kind} is not defined."),
        };
    }

    private static int FilterDictionary(
        CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices)
    {
        int count = indices.Length;
        Validity validity = FilterValidity(arena, node.Validity, indices);
        VortexBuffer codes = VortexBuffer.Empty;
        if (count > 0)
        {
            // Uninitialized: the loop writes every code.
            codes = arena.AllocateUninitialized(
                ArrayDecodeContext.CheckedMultiply(count, sizeof(uint), "filtered dictionary codes"), sizeof(uint), out Span<byte> raw);
            ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(node.Codes.Span);
            Span<uint> target = MemoryMarshal.Cast<byte, uint>(raw)[..count];
            for (int i = 0; i < target.Length; i++)
            {
                target[i] = source[indices[i]];
            }
        }

        return arena.AddDictionary(node.DType, count, validity, codes, node.EncodedValuesIndex);
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
        CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices)
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
                children[i] = Apply(arena, arena.GetNode(node.GetFieldIndex(i)), indices);
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
    /// <remarks>
    /// The width is dispatched once, to a loop that moves each row with one load and one store.
    /// The buffer is left uninitialized: the gather writes every row, or throws before the buffer
    /// reaches a node.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">An index lies past the source's rows.</exception>
    private static VortexBuffer Gather(
        CanonicalArena arena, ReadOnlySpan<byte> source, ReadOnlySpan<int> indices, int width)
    {
        int count = indices.Length;
        if (count == 0 || width == 0)
        {
            return VortexBuffer.Empty;
        }

        VortexBuffer buffer = arena.AllocateUninitialized(
            ArrayDecodeContext.CheckedMultiply(count, width, "gathered values"), width, out Span<byte> destination);
        int outside = RowKernels.Gather(
            MemoryMarshal.AsBytes(indices), PType.I32, source, width, source.Length / width, destination, count);
        if (outside >= 0)
        {
            ThrowOutside(indices[outside]);
        }

        return buffer;
    }

    [DoesNotReturn]
    private static void ThrowOutside(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "A selected row lies past the rows it selects from.");

    /// <summary>
    /// Gathers a validity, collapsing the result: a selection that happens to contain no null
    /// becomes all-valid rather than a bitmap of ones, and one with no valid row becomes
    /// all-invalid.
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

        // A validity bitmap's own dtype is always non-nullable bool.
        int node = arena.AddBool(
            arena.GetNode(validity.CanonicalNodeIndex).DType, count, Validity.NonNullable, bits, 0);
        return Validity.Bitmap(node);
    }

    private static int Apply(CanonicalArena arena, CanonicalNode node, ReadOnlySpan<int> indices) =>
        Apply(arena, node.Index, indices);
}
