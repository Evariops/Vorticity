// A ROW-MAJOR digest of a canonical tree: for each row, every leaf value under it, in tree order.
//
// Row-major is the whole point. It makes the digest COMPOSABLE, so
//
//     Digest(0, n)  ==  Digest(0, k) ++ Digest(k, n)
//
// which is what turns "read the whole column" and "read it in three ranges and glue the answers
// together" into a single byte comparison. A column-major digest would interleave differently and
// prove nothing about row ranges.
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;

using Vorticity.Arrays;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Tests.Layouts;

internal static class CanonicalDigest
{
    /// <summary>Digests rows <c>[start, start + length)</c> of a canonical node.</summary>
    internal static byte[] Of(ScanContext context, int nodeIndex, int start, int length)
    {
        using MemoryStream output = new MemoryStream();
        for (int row = start; row < start + length; row++)
        {
            WriteRow(context, nodeIndex, row, output, depth: 1);
        }

        return output.ToArray();
    }

    /// <summary>Digests every row of a canonical node.</summary>
    internal static byte[] Of(ScanContext context, int nodeIndex) =>
        Of(context, nodeIndex, 0, context.Canonical.GetNode(nodeIndex).Length);

    private static void WriteRow(ScanContext context, int nodeIndex, int row, MemoryStream o, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidOperationException("Canonical tree deeper than 64 while digesting.");
        }

        CanonicalNode node = context.Canonical.GetNode(nodeIndex);
        bool valid = IsValid(context, node.Validity, row);
        o.WriteByte(valid ? (byte)1 : (byte)0);

        switch (node.Kind)
        {
            case CanonicalKind.Null:
                return;

            case CanonicalKind.Bool:
                if (valid)
                {
                    int bit = node.BitOffset + row;
                    o.WriteByte((byte)((node.Bits.Span[bit >> 3] >> (bit & 7)) & 1));
                }

                return;

            case CanonicalKind.Primitive:
                if (valid)
                {
                    int width = node.PType.ByteWidth();
                    o.Write(node.Values.Span.Slice(row * width, width));
                }

                return;

            case CanonicalKind.Decimal:
                if (valid)
                {
                    int width = DecimalStorage.ByteWidth(node.Storage);
                    o.Write(node.Values.Span.Slice(row * width, width));
                }

                return;

            case CanonicalKind.VarBinView:
                if (valid)
                {
                    WriteVarBin(in node, row, o);
                }

                return;

            case CanonicalKind.ListView:
            {
                long offset = ReadInteger(node.Offsets.Span, node.OffsetPType, row);
                long size = ReadInteger(node.Sizes.Span, node.SizePType, row);
                WriteInt64(o, size);
                for (long i = 0; i < size; i++)
                {
                    WriteRow(context, node.ElementsIndex, checked((int)(offset + i)), o, depth + 1);
                }

                return;
            }

            case CanonicalKind.FixedSizeList:
            {
                uint size = node.FixedSize;
                for (uint i = 0; i < size; i++)
                {
                    WriteRow(context, node.ElementsIndex, checked((int)((row * (long)size) + i)), o, depth + 1);
                }

                return;
            }

            case CanonicalKind.Constant:
                // One element standing for every row. The element's bytes are written, NOT a
                // recursion into the materialized twin: the twin would write its own validity byte
                // first, and the digest has to be identical whichever form the read produced --
                // that is the only reason it is worth digesting at all.
                //
                // A STRING WRITES ITS LENGTH FIRST, exactly as WriteVarBin does: the two forms of
                // one file meet here whenever a read splits, and one of the two chunks folded to a
                // constant while the other did not. `repeated_prefix_utf8` is that file, and eight
                // missing length bytes is how it said so.
                if (valid)
                {
                    ReadOnlySpan<byte> element = node.ConstantElement;
                    if (node.DType.Kind is DTypeKind.Utf8 or DTypeKind.Binary)
                    {
                        WriteInt64(o, element.Length);
                    }

                    o.Write(element);
                }

                return;

            case CanonicalKind.Struct:
            {
                int fields = node.FieldCount;
                for (int f = 0; f < fields; f++)
                {
                    WriteRow(context, context.Canonical.GetNode(nodeIndex).GetFieldIndex(f), row, o, depth + 1);
                }

                return;
            }

            default:
                WriteRow(context, node.StorageIndex, row, o, depth + 1);
                return;
        }
    }

    private static void WriteVarBin(in CanonicalNode node, int row, MemoryStream o)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
        int length = BinaryPrimitives.ReadInt32LittleEndian(view);
        WriteInt64(o, length);

        if (length <= 12)
        {
            o.Write(view.Slice(4, length));
            return;
        }

        int buffer = BinaryPrimitives.ReadInt32LittleEndian(view.Slice(8, 4));
        int offset = BinaryPrimitives.ReadInt32LittleEndian(view.Slice(12, 4));
        o.Write(node.GetDataBuffer(buffer).Span.Slice(offset, length));
    }

    private static void WriteInt64(MemoryStream o, long value)
    {
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(scratch, value);
        o.Write(scratch);
    }

    private static long ReadInteger(ReadOnlySpan<byte> bytes, PType ptype, int index) => ptype switch
    {
        PType.U8 => bytes[index],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * 4, 4)),
        PType.U64 => (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(index * 8, 8)),
        PType.I8 => (sbyte)bytes[index],
        PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(index * 4, 4)),
        PType.I64 => BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(index * 8, 8)),
        _ => throw new InvalidOperationException(
            $"A list offset cannot be {ptype.Name()} (row {index.ToString(CultureInfo.InvariantCulture)})."),
    };

    private static bool IsValid(ScanContext context, Validity validity, int row) => validity.Kind switch
    {
        ValidityKind.NonNullable => true,
        ValidityKind.AllValid => true,
        ValidityKind.AllInvalid => false,
        _ => BitOf(context, validity.CanonicalNodeIndex, row),
    };

    private static bool BitOf(ScanContext context, int nodeIndex, int row)
    {
        CanonicalNode bits = context.Canonical.GetNode(nodeIndex);
        int bit = bits.BitOffset + row;
        return ((bits.Bits.Span[bit >> 3] >> (bit & 7)) & 1) != 0;
    }
}
