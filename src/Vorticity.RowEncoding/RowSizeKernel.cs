using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.RowEncoding;

/// <summary>
/// The sizing pass. Sizes accumulate rather than being assigned, so several columns sum into one
/// buffer.
/// </summary>
internal static class RowSizeKernel
{
    /// <summary>
    /// Adds one column's per-row encoded size into <paramref name="sizes"/>, one accumulator per
    /// row. Every child inherits <paramref name="field"/> unchanged.
    /// </summary>
    internal static void Add(CanonicalArena arena, int nodeIndex, RowSortField field, Span<int> sizes)
    {
        CanonicalNode node = arena.GetNode(nodeIndex);
        if (node.Length != sizes.Length)
        {
            throw new VortexFormatException(
                $"A {node.Length}-row column was sized against {sizes.Length} rows.");
        }

        switch (node.Kind)
        {
            case CanonicalKind.Null:
                AddConstant(sizes, 1);
                return;

            case CanonicalKind.Bool:
                AddConstant(sizes, 2);
                return;

            case CanonicalKind.Primitive:
                AddConstant(sizes, 1 + node.PType.ByteWidth());
                return;

            case CanonicalKind.Decimal:
                AddConstant(sizes, 1 + DecimalStorage.ByteWidth(RowWidths.KeyStorage(node.Precision)));
                return;

            case CanonicalKind.VarBinView:
                AddVarBin(arena, node, sizes);
                return;

            case CanonicalKind.Struct:
                AddStruct(arena, node, field, sizes);
                return;

            case CanonicalKind.FixedSizeList:
                AddFixedSizeList(arena, node, field, sizes);
                return;

            default:
                // Rejected here as well as by width classification: the canonical form is the last
                // place a dtype and its physical shape can disagree.
                throw RowThrow.UnsupportedCanonical(node);
        }
    }

    private static void AddConstant(Span<int> sizes, int add)
    {
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = Checked(sizes[i], add);
        }
    }

    private static void AddVarBin(CanonicalArena arena, CanonicalNode node, Span<int> sizes)
    {
        RowValidity validity = RowValidity.Resolve(arena, node);
        ReadOnlySpan<byte> views = node.Views.Span;
        for (int i = 0; i < sizes.Length; i++)
        {
            int contribution;
            if (!validity.IsValid(i))
            {
                contribution = RowWidths.VarNullSize;
            }
            else
            {
                int length = RowBytes.ViewLength(RowBytes.View(views, i));
                contribution = length == 0 ? RowWidths.VarEmptySize : RowWidths.NonEmptyVarSize(length);
            }

            sizes[i] = Checked(sizes[i], contribution);
        }
    }

    private static void AddStruct(
        CanonicalArena arena, CanonicalNode node, RowSortField field, Span<int> sizes)
    {
        RowValidity validity = RowValidity.Resolve(arena, node);
        AddConstant(sizes, 1);

        int rows = sizes.Length;
        for (int f = 0; f < node.FieldCount; f++)
        {
            int childIndex = node.GetFieldIndex(f);
            DType childType = arena.GetNode(childIndex).DType;

            RowWidth width = RowWidths.For(childType);
            if (width.IsFixed)
            {
                // A fixed child costs the same under a null parent row as under a non-null one, so
                // the parent's mask never enters the arithmetic.
                AddConstant(sizes, width.Width);
                continue;
            }

            // A variable child under a null parent collapses to one byte, which is what makes two
            // null parent rows byte-equal whatever their children hold.
            int[] rented = ArrayPool<int>.Shared.Rent(rows);
            try
            {
                Span<int> childSizes = rented.AsSpan(0, rows);
                childSizes.Clear();
                Add(arena, childIndex, field, childSizes);
                for (int i = 0; i < rows; i++)
                {
                    sizes[i] = Checked(sizes[i], validity.IsValid(i) ? childSizes[i] : 1);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    private static void AddFixedSizeList(
        CanonicalArena arena, CanonicalNode node, RowSortField field, Span<int> sizes)
    {
        RowValidity validity = RowValidity.Resolve(arena, node);
        AddConstant(sizes, 1);

        int rows = sizes.Length;
        int listSize = checked((int)node.FixedSize);
        int elementsIndex = node.ElementsIndex;
        DType elementType = arena.GetNode(elementsIndex).DType;
        RowWidth width = RowWidths.For(elementType);
        if (width.IsFixed)
        {
            AddConstant(sizes, checked(width.Width * listSize));
            return;
        }

        int elementCount = checked(rows * listSize);
        int[] rented = ArrayPool<int>.Shared.Rent(elementCount);
        try
        {
            Span<int> elementSizes = rented.AsSpan(0, elementCount);
            elementSizes.Clear();
            Add(arena, elementsIndex, field, elementSizes);
            for (int i = 0; i < rows; i++)
            {
                int body;
                if (validity.IsValid(i))
                {
                    body = 0;
                    int at = i * listSize;
                    for (int j = 0; j < listSize; j++)
                    {
                        body = Checked(body, elementSizes[at + j]);
                    }
                }
                else
                {
                    // One null sentinel per element: the canonical null body.
                    body = listSize;
                }

                sizes[i] = Checked(sizes[i], body);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static int Checked(int current, int add)
    {
        int total = current + add;
        if (total < current)
        {
            throw new VortexFormatException("A row's encoded size exceeds 2 GiB.");
        }

        return total;
    }
}
