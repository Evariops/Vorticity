using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// A digest of the rows a query delivers, whatever their order and however their columns are encoded: each
/// row's values hashed as values (a dictionary's code read through, a string's bytes wherever they lie, a
/// null as a null), the rows' hashes summed. Two paths that answer the same rows give the same digest, so a
/// bench compares the synopses' answer with the rows' without holding either.
/// </summary>
internal struct ResultDigest
{
    private const ulong NullHash = 0x9E3779B97F4A7C15UL;

    /// <summary>The sum of the rows' hashes.</summary>
    internal ulong Sum { get; private set; }

    /// <summary>The rows digested.</summary>
    internal long Rows { get; private set; }

    /// <summary>Adds the rows <paramref name="batch"/> selects.</summary>
    internal void Add(RecordBatch batch)
    {
        ReadOnlySpan<ulong> selection = batch.SelectionWords;
        for (int row = 0; row < batch.RowCount; row++)
        {
            if (!selection.IsEmpty && (selection[row >> 6] & (1UL << (row & 63))) == 0)
            {
                continue;
            }

            Sum += Mix(Value(batch.Arena, batch.RootIndex, row));
            Rows++;
        }
    }

    /// <summary>The digest as text, for a line of the bench.</summary>
    public override readonly string ToString() => $"{Rows}:{Sum:x16}";

    private static ulong Value(CanonicalArena arena, int index, int row)
    {
        CanonicalNode node = arena.GetNode(index);
        if (node.Kind != CanonicalKind.Extension && IsNull(arena, node.Validity, row))
        {
            return NullHash;
        }

        switch (node.Kind)
        {
            case CanonicalKind.Null:
                return NullHash;
            case CanonicalKind.Bool:
            {
                int bit = node.BitOffset + row;
                return (ulong)((node.Bits.Span[bit >> 3] >> (bit & 7)) & 1);
            }

            case CanonicalKind.Primitive:
            {
                int width = node.PType.ByteWidth();
                return Bytes(node.Values.Span.Slice(row * width, width));
            }

            case CanonicalKind.Decimal:
            {
                int width = DecimalStorage.ByteWidth(node.Storage);
                return Bytes(node.Values.Span.Slice(row * width, width));
            }

            case CanonicalKind.VarBinView:
            {
                ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
                int length = BinaryPrimitives.ReadInt32LittleEndian(view);
                if (length <= 12)
                {
                    return Bytes(view.Slice(4, length)) ^ (ulong)length;
                }

                int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..]);
                int offset = BinaryPrimitives.ReadInt32LittleEndian(view[12..]);
                return Bytes(node.DataBuffers[buffer].Span.Slice(offset, length)) ^ (ulong)length;
            }

            case CanonicalKind.Struct:
            {
                ulong hash = 17;
                for (int field = 0; field < node.FieldCount; field++)
                {
                    hash = Mix(hash ^ Value(arena, node.GetFieldIndex(field), row)) + (ulong)field;
                }

                return hash;
            }

            case CanonicalKind.Extension:
                return Value(arena, node.StorageIndex, row);
            case CanonicalKind.Constant:
                return Bytes(node.ConstantElement);
            case CanonicalKind.Dictionary:
            {
                uint code = MemoryMarshal.Cast<byte, uint>(node.Codes.Span)[row];
                return Value(arena, node.EncodedValuesIndex, (int)code);
            }

            default:
                throw new NotSupportedException($"A result delivered a {node.Kind} node, which the digest does not read.");
        }
    }

    private static bool IsNull(CanonicalArena arena, Validity validity, int row)
    {
        switch (validity.Kind)
        {
            case ValidityKind.AllInvalid:
                return true;
            case ValidityKind.Bitmap:
            {
                CanonicalNode bits = arena.GetNode(validity.CanonicalNodeIndex);
                int bit = bits.BitOffset + row;
                return ((bits.Bits.Span[bit >> 3] >> (bit & 7)) & 1) == 0;
            }

            default:
                return false;
        }
    }

    private static ulong Bytes(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 0xCBF29CE484222325UL;
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * 0x100000001B3UL;
        }

        return hash;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 33;
        value *= 0xFF51AFD7ED558CCDUL;
        value ^= value >> 33;
        value *= 0xC4CEB9FE1A85EC53UL;
        return value ^ (value >> 33);
    }
}
