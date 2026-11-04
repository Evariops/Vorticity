using System;
using System.Buffers;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Compute;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Writing;

/// <summary>
/// The rows of a canonical node appended to a store of its type: what holds a result whole, a
/// result's scan sorted in memory, gathered from the batches it came in.
/// </summary>
internal static class StoreRows
{
    /// <summary>Appends every row of <paramref name="node"/> to <paramref name="store"/>, a store of the node's type.</summary>
    /// <exception cref="NotSupportedException">The node is of a kind a result does not hold: a map.</exception>
    internal static void Append(ColumnStore store, CanonicalArena arena, int node)
    {
        while (arena.RecordRef(node).Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node).StorageIndex;
        }

        while (store is ExtensionStore extension)
        {
            store = extension.Storage;
        }

        node = EncodedForms.Canonical(arena, node);
        int rows = arena.RecordRef(node).Length;
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, node);
        switch (store)
        {
            case FixedStore fixedStore:
                AppendFixed(fixedStore, arena, node, rows, valid);
                return;
            case BoolStore bools:
            {
                ReadOnlySpan<ulong> bits = ArenaWords.Bits(arena, node);
                for (int row = 0; row < rows; row++)
                {
                    if (!IsValid(valid, row))
                    {
                        bools.AppendNull();
                    }
                    else
                    {
                        bools.Append(((bits[row >> 6] >> (row & 63)) & 1) != 0);
                    }
                }

                return;
            }

            case VarBinStore bytes:
            {
                Aggregating.BytesBlock block = Aggregating.BytesBlock.Canonical(arena, node, out _);
                for (int row = 0; row < rows; row++)
                {
                    if (!IsValid(valid, row))
                    {
                        bytes.AppendNull();
                    }
                    else
                    {
                        bytes.Append(block[row]);
                    }
                }

                return;
            }

            case FixedListStore list when list.Elements is FixedStore elements:
            {
                int size = list.Size;
                int width = elements.Width;
                ReadOnlySpan<byte> values = ColumnData.Values(arena, EncodedForms.Canonical(arena, arena.GetNode(node).ElementsIndex));
                for (int row = 0; row < rows; row++)
                {
                    if (!IsValid(valid, row))
                    {
                        list.AppendNull();
                    }
                    else
                    {
                        elements.AppendBytes(values.Slice(row * size * width, size * width));
                        list.Count++;
                    }
                }

                return;
            }

            case ListStore list when arena.RecordRef(node).Kind == CanonicalKind.ListView:
                AppendLists(list, arena, node, rows, valid);
                return;

            case StructStore structure:
            {
                // Its fields appended whole, a null row's included, whatever they hold there; then its null rows marked.
                // The node is read again for each field: a field's append may add nodes, which moves the records.
                int first = structure.Rows;
                for (int i = 0; i < structure.Children.Length; i++)
                {
                    Append(structure.Children[i], arena, arena.GetNode(node).GetFieldIndex(i));
                }

                structure.AppendedWhole(first, rows, valid);
                return;
            }

            default:
                throw new NotSupportedException($"A column of {store.Type} is not gathered into one batch.");
        }
    }

    /// <summary>
    /// Appends the lists of the list view <paramref name="node"/> to <paramref name="list"/>: the elements of its valid rows,
    /// in their order, gathered from wherever the view names them into one node and appended whole, then each row closed
    /// over its count of them; a null row holds none.
    /// </summary>
    private static void AppendLists(ListStore list, CanonicalArena arena, int node, int rows, ReadOnlySpan<ulong> valid)
    {
        // Everything is read off the node before the gathering appends records: a node read across that append is a
        // use-after-move. The spans are over buffers, which do not move.
        // The offsets and sizes are read with their types resolved once, not once a row, as the 64-bit words they become.
        CanonicalNode view = arena.GetNode(node);
        int elements = view.ElementsIndex;
        long[] words = ArrayPool<long>.Shared.Rent(Math.Max(1, 2 * rows));
        int[] counts = ArrayPool<int>.Shared.Rent(Math.Max(1, rows));
        int[]? indices = null;
        try
        {
            Span<long> offsets = words.AsSpan(0, rows);
            Span<long> sizes = words.AsSpan(rows, rows);
            CanonicalSupport.ReadIntegers(view.Offsets.Span, view.OffsetPType, 0, offsets);
            CanonicalSupport.ReadIntegers(view.Sizes.Span, view.SizePType, 0, sizes);
            int total = 0;
            for (int row = 0; row < rows; row++)
            {
                counts[row] = IsValid(valid, row) ? checked((int)sizes[row]) : 0;
                total = checked(total + counts[row]);
            }

            indices = ArrayPool<int>.Shared.Rent(Math.Max(1, total));
            int at = 0;
            for (int row = 0; row < rows; row++)
            {
                int offset = counts[row] == 0 ? 0 : checked((int)offsets[row]);
                for (int e = 0; e < counts[row]; e++)
                {
                    indices[at++] = offset + e;
                }
            }

            Append(list.Elements, arena, CanonicalFilter.Apply(arena, elements, indices.AsSpan(0, total)));
            list.AppendLists(counts.AsSpan(0, rows), valid);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(words);
            ArrayPool<int>.Shared.Return(counts);
            if (indices is not null)
            {
                ArrayPool<int>.Shared.Return(indices);
            }
        }
    }

    private static void AppendFixed(FixedStore store, CanonicalArena arena, int node, int rows, ReadOnlySpan<ulong> valid)
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        ReadOnlySpan<byte> values = ColumnData.Values(arena, node);
        int width = record.Kind == CanonicalKind.Decimal ? DecimalStorage.ByteWidth(record.Storage) : record.PType.ByteWidth();
        if (width == store.Width && valid.IsEmpty)
        {
            store.AppendBytes(values[..(rows * width)]);
            return;
        }

        Span<byte> wide = stackalloc byte[Int256.ByteCount];
        for (int row = 0; row < rows; row++)
        {
            if (!IsValid(valid, row))
            {
                store.AppendNull();
                continue;
            }

            ReadOnlySpan<byte> value = values.Slice(row * width, width);
            if (width == store.Width)
            {
                store.AppendBytes(value);
                continue;
            }

            // A decimal stored narrower or wider than the store's width: its value sign-extended,
            // then its low bytes, which hold it at the store's precision.
            byte fill = (value[^1] & 0x80) != 0 ? (byte)0xFF : (byte)0;
            wide.Fill(fill);
            value.CopyTo(wide);
            store.AppendBytes(wide[..store.Width]);
        }
    }

    private static bool IsValid(ReadOnlySpan<ulong> valid, int row) =>
        valid.IsEmpty || ((valid[row >> 6] >> (row & 63)) & 1) != 0;
}
