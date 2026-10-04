using System;
using System.Buffers.Binary;
using Vorticity.Arrays;
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
    /// <exception cref="NotSupportedException">The node is of a kind a result does not hold: a list, a map.</exception>
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

            case StructStore structure:
            {
                if (!valid.IsEmpty && ArenaWords.NullCount(arena, node) > 0)
                {
                    throw new NotSupportedException("A struct column that holds a null is not gathered into one batch.");
                }

                CanonicalNode fields = arena.GetNode(node);
                for (int i = 0; i < structure.Children.Length; i++)
                {
                    Append(structure.Children[i], arena, fields.GetFieldIndex(i));
                }

                return;
            }

            default:
                throw new NotSupportedException($"A column of {store.Type} is not gathered into one batch.");
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
