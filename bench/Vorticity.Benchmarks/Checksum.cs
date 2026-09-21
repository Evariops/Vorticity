// The decoded VALUES of a file, as one 64-bit number both implementations can compute.
//
// `--ffi-check` compared row counts, and this repository has already learned that a row count is
// not evidence of a decode -- upstream's lazy scan answers `len()` from metadata without
// materializing a byte, which is exactly how a "0.96x" ratio came to compare a decode against an
// absence of one. A checksum over the values is the precondition that sentence needed: same rows,
// same order, same bytes.
//
// IT IS A CHECKSUM OF VALUES, NOT OF BUFFERS, and that is the only shape that can agree across two
// implementations. An Arrow view carries a buffer index and an offset that are both legitimately
// different here and there; a validity bitmap may be absent on one side and all-ones on the other;
// a null slot holds whatever the allocator left. None of that is data. What is data is: for each
// row, in file order, is it null, and if not, what is its value.
//
// THE ENCODING IS SPELLED OUT because the other side has to reproduce it byte for byte
// (tools/vxbench-rs/src/lib.rs, `vxbench_scan_checksum`):
//
//   null                0x00
//   bool                0x01, one byte, 0 or 1
//   signed integer      0x02, width byte (1/2/4/8), that many bytes little-endian
//   unsigned integer    0x03, width byte, that many bytes little-endian
//   float               0x04, width byte (2/4/8), the raw bits little-endian
//   utf8 or binary      0x05, u32 little-endian length, the bytes
//   struct              0x06, u32 field count, each field in schema order
//   list                0x07, u32 element count, each element
//   decimal             0x08, width byte, the storage bits little-endian
//   extension           0x09, then the storage value
//
// The raw float bits rather than a decimal rendering, because a text form would make this a test of
// two formatters. The width byte, because a u8 5 and a u16 5 are not the same value.
//
// FNV-1a over that stream: twelve lines on both sides, no dependency, and the mixing does not have
// to be good -- it has to be identical.
using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>A value-by-value checksum of a scan, comparable with the reference's.</summary>
internal static class Checksum
{
    private const ulong Offset = 0xcbf29ce484222325;
    private const ulong Prime = 0x100000001b3;

    /// <summary>Scans <paramref name="path"/> and folds every decoded value into one number.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The checksum, as the reference returns it.</returns>
    internal static async Task<long> OfFileAsync(string path)
    {
        ulong hash = Offset;
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            for (int row = 0; row < batch.RowCount; row++)
            {
                Value(ref hash, batch.Root, row);
            }
        }

        return unchecked((long)hash);
    }

    /// <summary>Folds one row of one column in.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="column">The column.</param>
    /// <param name="row">The row within it.</param>
    private static void Value(ref ulong hash, VortexColumn column, int row)
    {
        if (!column.IsValid(row))
        {
            Byte(ref hash, 0x00);
            return;
        }

        switch (column.Kind)
        {
            case CanonicalKind.Null:
                Byte(ref hash, 0x00);
                break;

            case CanonicalKind.Bool:
                Byte(ref hash, 0x01);
                Byte(ref hash, column.AsBool()[row] ? (byte)1 : (byte)0);
                break;

            case CanonicalKind.Primitive:
                Primitive(ref hash, column, row);
                break;

            case CanonicalKind.VarBinView:
                ReadOnlySpan<byte> value = column.AsBinary().GetSpan(row);
                Byte(ref hash, 0x05);
                Count(ref hash, value.Length);
                Bytes(ref hash, value);
                break;

            case CanonicalKind.Struct:
                StructColumn fields = column.AsStruct();
                Byte(ref hash, 0x06);
                Count(ref hash, fields.FieldCount);
                for (int field = 0; field < fields.FieldCount; field++)
                {
                    Value(ref hash, fields.GetField(field), row);
                }

                break;

            case CanonicalKind.ListView:
                ListColumn list = column.AsList();
                int start = (int)list.GetOffset(row);
                int length = list.GetLength(row);
                Byte(ref hash, 0x07);
                Count(ref hash, length);
                for (int i = 0; i < length; i++)
                {
                    Value(ref hash, list.Elements, start + i);
                }

                break;

            case CanonicalKind.FixedSizeList:
                FixedSizeListColumn fixedList = column.AsFixedSizeList();
                fixedList.GetRange(row, out int from, out int count);
                Byte(ref hash, 0x07);
                Count(ref hash, count);
                for (int i = 0; i < count; i++)
                {
                    Value(ref hash, fixedList.Elements, from + i);
                }

                break;

            case CanonicalKind.Decimal:
                DecimalColumn dec = column.AsDecimal();
                int width = dec.StorageBytes.Length / Math.Max(1, dec.Length);
                Byte(ref hash, 0x08);
                Byte(ref hash, (byte)width);
                Bytes(ref hash, dec.StorageBytes.Slice(row * width, width));
                break;

            case CanonicalKind.Extension:
                Byte(ref hash, 0x09);
                Value(ref hash, column.AsExtension().Storage, row);
                break;

            default:
                throw new NotSupportedException(
                    $"The checksum has no encoding for {column.Kind}. Add one HERE AND IN " +
                    "tools/vxbench-rs/src/lib.rs, or the two sides stop agreeing silently.");
        }
    }

    /// <summary>Folds one primitive in: tag, width, then the value's own little-endian bytes.</summary>
    /// <remarks>
    /// ELEVEN ARMS AND NOT FOUR WIDTHS, because `AsPrimitive&lt;T&gt;` requires the exactly matching
    /// .NET type -- reinterpreting an i64 column as `ulong` to read its eight bytes is refused by
    /// design, and rightly: that refusal is the thing keeping a width
    /// confusion from becoming a silent wrong answer somewhere less visible than here.
    /// </remarks>
    private static void Primitive(ref ulong hash, VortexColumn column, int row)
    {
        PType ptype = column.DType.PType;
        Byte(ref hash, ptype.IsFloat() ? (byte)0x04 : ptype.IsSignedInteger() ? (byte)0x02 : (byte)0x03);
        Byte(ref hash, (byte)ptype.ByteWidth());
        Span<byte> bits = stackalloc byte[8];
        switch (ptype)
        {
            case PType.U8:
                bits[0] = column.AsPrimitive<byte>()[row];
                break;
            case PType.I8:
                bits[0] = unchecked((byte)column.AsPrimitive<sbyte>()[row]);
                break;
            case PType.U16:
                BinaryPrimitives.WriteUInt16LittleEndian(bits, column.AsPrimitive<ushort>()[row]);
                break;
            case PType.I16:
                BinaryPrimitives.WriteInt16LittleEndian(bits, column.AsPrimitive<short>()[row]);
                break;
            case PType.F16:
                BinaryPrimitives.WriteUInt16LittleEndian(
                    bits, BitConverter.HalfToUInt16Bits(column.AsPrimitive<Half>()[row]));
                break;
            case PType.U32:
                BinaryPrimitives.WriteUInt32LittleEndian(bits, column.AsPrimitive<uint>()[row]);
                break;
            case PType.I32:
                BinaryPrimitives.WriteInt32LittleEndian(bits, column.AsPrimitive<int>()[row]);
                break;
            case PType.F32:
                BinaryPrimitives.WriteSingleLittleEndian(bits, column.AsPrimitive<float>()[row]);
                break;
            case PType.U64:
                BinaryPrimitives.WriteUInt64LittleEndian(bits, column.AsPrimitive<ulong>()[row]);
                break;
            case PType.I64:
                BinaryPrimitives.WriteInt64LittleEndian(bits, column.AsPrimitive<long>()[row]);
                break;
            default:
                BinaryPrimitives.WriteDoubleLittleEndian(bits, column.AsPrimitive<double>()[row]);
                break;
        }

        Bytes(ref hash, bits[..ptype.ByteWidth()]);
    }

    private static void Count(ref ulong hash, int count)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)count);
        Bytes(ref hash, bytes);
    }

    private static void Bytes(ref ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            Byte(ref hash, b);
        }
    }

    private static void Byte(ref ulong hash, byte value) =>
        hash = unchecked((hash ^ value) * Prime);
}
