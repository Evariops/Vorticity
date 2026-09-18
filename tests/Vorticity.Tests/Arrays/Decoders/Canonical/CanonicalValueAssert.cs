// Compares a decoded canonical node against the sidecar's `rows` values, per corpus/SIDECAR.md.
//
// The value grammar is normative and travels with each file: integers are decimal STRINGS (a JSON
// number is an f64 in most parsers and u64::MAX does not survive one), floats are {bits, dec} with
// `bits` normative so a wrong NaN payload or a -0.0 read as +0.0 fails, utf8 and binary are base64
// of the BYTES, and decimals are {unscaled, storage}.
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Types.Variant;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

internal static class CanonicalValueAssert
{
    internal static void AssertRow(ScanContext scan, int nodeIndex, DType dtype, int row, JsonElement expected)
    {
        CanonicalNode node = scan.Canonical.GetNode(nodeIndex);

        if (!IsValid(scan, in node, row))
        {
            Assert.Equal(JsonValueKind.Null, expected.ValueKind);
            return;
        }

        switch (node.Kind)
        {
            case CanonicalKind.Null:
                Assert.Equal(JsonValueKind.Null, expected.ValueKind);
                return;

            case CanonicalKind.Extension:
                AssertRow(scan, node.StorageIndex, dtype.StorageType, row, expected);
                return;

            case CanonicalKind.Bool:
                Assert.Equal(
                    expected.GetBoolean(),
                    CanonicalBits.Get(node.Bits.Span, node.BitOffset + row));
                return;

            case CanonicalKind.Primitive:
                AssertPrimitive(in node, row, expected);
                return;

            case CanonicalKind.Decimal:
                AssertDecimal(in node, row, expected);
                return;

            case CanonicalKind.VarBinView:
                AssertVarBinView(in node, row, expected);
                return;

            case CanonicalKind.ListView:
            {
                long offset = ReadIndex(node.Offsets.Span, node.OffsetPType, row);
                long size = ReadIndex(node.Sizes.Span, node.SizePType, row);
                AssertElements(scan, node.ElementsIndex, dtype.ElementType, offset, size, expected);
                return;
            }

            case CanonicalKind.FixedSizeList:
            {
                long size = node.FixedSize;
                AssertElements(scan, node.ElementsIndex, dtype.ElementType, row * size, size, expected);
                return;
            }

            case CanonicalKind.Constant:
                // One element standing for every row. Asserting through the materialized twin,
                // rather than against the element here, is deliberate: it makes this test read the
                // column the way a caller does, so a twin that expanded the wrong bytes fails here
                // instead of passing because both sides read the same element.
                AssertRow(scan, scan.Canonical.MaterializeConstant(nodeIndex), dtype, row, expected);
                return;

            case CanonicalKind.Struct when dtype.Kind == DTypeKind.Variant:
                AssertVariant(in node, scan, row, expected);
                return;

            default:
            {
                Assert.Equal(JsonValueKind.Object, expected.ValueKind);
                Assert.Equal(dtype.FieldCount, node.FieldCount);
                for (int f = 0; f < node.FieldCount; f++)
                {
                    JsonElement field = expected.GetProperty(dtype.GetFieldName(f));
                    AssertRow(scan, node.GetFieldIndex(f), dtype.GetField(f), row, field);
                }

                return;
            }
        }
    }

    internal static bool IsValid(ScanContext scan, ref readonly CanonicalNode node, int row)
    {
        Validity validity = node.Validity;
        switch (validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return true;
            case ValidityKind.AllInvalid:
                return false;
            default:
            {
                CanonicalNode bits = scan.Canonical.GetNode(validity.CanonicalNodeIndex);
                return CanonicalBits.Get(bits.Bits.Span, bits.BitOffset + row);
            }
        }
    }

    private static void AssertElements(
        ScanContext scan, int elementsIndex, DType elementType, long offset, long size, JsonElement expected)
    {
        Assert.Equal(JsonValueKind.Array, expected.ValueKind);
        Assert.Equal(size, expected.GetArrayLength());

        int i = 0;
        foreach (JsonElement element in expected.EnumerateArray())
        {
            AssertRow(scan, elementsIndex, elementType, (int)(offset + i), element);
            i++;
        }
    }

    private static void AssertPrimitive(ref readonly CanonicalNode node, int row, JsonElement expected)
    {
        ReadOnlySpan<byte> values = node.Values.Span;
        switch (node.PType)
        {
            case PType.U8:
                AssertInteger(expected, values[row].ToString(CultureInfo.InvariantCulture));
                return;
            case PType.U16:
                AssertInteger(expected, BinaryPrimitives.ReadUInt16LittleEndian(values.Slice(row * 2, 2))
                    .ToString(CultureInfo.InvariantCulture));
                return;
            case PType.U32:
                AssertInteger(expected, BinaryPrimitives.ReadUInt32LittleEndian(values.Slice(row * 4, 4))
                    .ToString(CultureInfo.InvariantCulture));
                return;
            case PType.U64:
                AssertInteger(expected, BinaryPrimitives.ReadUInt64LittleEndian(values.Slice(row * 8, 8))
                    .ToString(CultureInfo.InvariantCulture));
                return;
            case PType.I8:
                AssertInteger(expected, ((sbyte)values[row]).ToString(CultureInfo.InvariantCulture));
                return;
            case PType.I16:
                AssertInteger(expected, BinaryPrimitives.ReadInt16LittleEndian(values.Slice(row * 2, 2))
                    .ToString(CultureInfo.InvariantCulture));
                return;
            case PType.I32:
                AssertInteger(expected, BinaryPrimitives.ReadInt32LittleEndian(values.Slice(row * 4, 4))
                    .ToString(CultureInfo.InvariantCulture));
                return;
            case PType.I64:
                AssertInteger(expected, BinaryPrimitives.ReadInt64LittleEndian(values.Slice(row * 8, 8))
                    .ToString(CultureInfo.InvariantCulture));
                return;
            case PType.F16:
                AssertFloatBits(expected, values.Slice(row * 2, 2));
                return;
            case PType.F32:
                AssertFloatBits(expected, values.Slice(row * 4, 4));
                return;
            default:
                AssertFloatBits(expected, values.Slice(row * 8, 8));
                return;
        }
    }

    private static void AssertInteger(JsonElement expected, string actual) =>
        Assert.Equal(expected.GetString(), actual);

    private static void AssertFloatBits(JsonElement expected, ReadOnlySpan<byte> littleEndian)
    {
        // The sidecar's `bits` is big-endian hex of the raw IEEE bytes and is normative.
        Span<char> hex = stackalloc char[littleEndian.Length * 2];
        for (int i = 0; i < littleEndian.Length; i++)
        {
            byte b = littleEndian[littleEndian.Length - 1 - i];
            hex[i * 2] = "0123456789abcdef"[b >> 4];
            hex[(i * 2) + 1] = "0123456789abcdef"[b & 0xF];
        }

        string? want = expected.GetProperty("bits").GetString();
        Assert.Equal(want, new string(hex), ignoreCase: true);
    }

    private static void AssertDecimal(ref readonly CanonicalNode node, int row, JsonElement expected)
    {
        int width = DecimalStorage.ByteWidth(node.Storage);
        ReadOnlySpan<byte> element = node.Values.Span.Slice(row * width, width);

        Span<byte> full = stackalloc byte[Int256.ByteCount];
        full.Fill((element[^1] & 0x80) != 0 ? (byte)0xFF : (byte)0x00);
        element.CopyTo(full);

        Int256 unscaled = Int256.FromLittleEndianBytes(full);
        Assert.Equal(expected.GetProperty("unscaled").GetString(), unscaled.ToString());
        Assert.Equal(expected.GetProperty("storage").GetString(), StorageName(node.Storage));
    }

    private static string StorageName(DecimalStorageType storage) => storage switch
    {
        DecimalStorageType.I8 => "i8",
        DecimalStorageType.I16 => "i16",
        DecimalStorageType.I32 => "i32",
        DecimalStorageType.I64 => "i64",
        DecimalStorageType.I128 => "i128",
        _ => "i256",
    };

    private static void AssertVarBinView(ref readonly CanonicalNode node, int row, JsonElement expected)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);

        ReadOnlySpan<byte> value;
        if (size <= 12)
        {
            value = view.Slice(4, (int)size);
        }
        else
        {
            uint bufferIndex = BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
            VortexBuffer data = node.GetDataBuffer((int)bufferIndex);
            value = data.Span.Slice((int)offset, (int)size);
        }

        Assert.Equal(expected.GetProperty("len").GetInt32(), value.Length);
        Assert.Equal(expected.GetProperty("b64").GetString(), Convert.ToBase64String(value));
    }

    /// <summary>
    /// Asserts one variant row against the sidecar's <c>{dtype, value}</c>.
    /// </summary>
    /// <remarks>
    /// A variant column is a `Struct{metadata, value}` wearing the variant dtype -- see
    /// `VariantDecoder`'s header -- so the generic struct branch above would compare it against the
    /// variant dtype's own field count, which is zero. The bytes are decoded instead, because the
    /// sidecar spells a variant row as the TYPED SCALAR it holds and comparing buffers would only
    /// check that the reference's encoder agrees with itself.
    /// </remarks>
    private static void AssertVariant(
        ref readonly CanonicalNode node, ScanContext scan, int row, JsonElement expected)
    {
        Assert.Equal(JsonValueKind.Object, expected.ValueKind);
        Assert.Equal(2, node.FieldCount);

        CanonicalNode metadataNode = scan.Canonical.GetNode(node.GetFieldIndex(0));
        CanonicalNode valueNode = scan.Canonical.GetNode(node.GetFieldIndex(1));
        VariantValue actual = ParquetVariant.Read(
            ViewOf(in metadataNode, row), ViewOf(in valueNode, row));

        JsonElement dtype = expected.GetProperty("dtype");
        JsonElement payload = expected.GetProperty("value");
        string kind = dtype.GetProperty("kind").GetString() ?? string.Empty;

        switch (actual.Kind)
        {
            case VariantKind.Null:
                Assert.Equal("null", kind);
                Assert.Equal(JsonValueKind.Null, payload.ValueKind);
                return;
            case VariantKind.Bool:
                Assert.Equal(actual.Integer != 0 ? "true" : "false", Text(payload));
                return;
            case VariantKind.Int8:
            case VariantKind.Int16:
            case VariantKind.Int32:
            case VariantKind.Int64:
                Assert.Equal(
                    actual.Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Text(payload));
                return;
            case VariantKind.String:
                Assert.Equal(System.Text.Encoding.UTF8.GetString(actual.Bytes), Text(payload));
                return;
            case VariantKind.Binary:
                Assert.Equal(Convert.ToBase64String(actual.Bytes), Text(payload));
                return;
            default:
                Assert.Fail($"the harness does not spell a variant {actual.Kind}");
                return;
        }
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.ToString(),
    };

    /// <summary>One row of a VarBinView as bytes, inline or through a data buffer.</summary>
    private static ReadOnlySpan<byte> ViewOf(ref readonly CanonicalNode node, int row)
    {
        ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size <= 12)
        {
            return view.Slice(4, (int)size);
        }

        uint bufferIndex = BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
        return node.GetDataBuffer((int)bufferIndex).Span.Slice((int)offset, (int)size);
    }

    private static long ReadIndex(ReadOnlySpan<byte> bytes, PType ptype, int index) => ptype switch
    {
        PType.U8 => bytes[index],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * 4, 4)),
        PType.U64 => (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(index * 8, 8)),
        PType.I8 => (sbyte)bytes[index],
        PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index * 2, 2)),
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(index * 4, 4)),
        _ => BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(index * 8, 8)),
    };
}

internal static class CanonicalBits
{
    internal static bool Get(ReadOnlySpan<byte> bits, int index) =>
        (bits[index >> 3] & (1 << (index & 7))) != 0;
}
