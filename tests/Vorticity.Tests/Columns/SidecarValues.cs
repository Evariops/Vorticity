// The sidecar's value grammar (corpus/SIDECAR.md), turned into an oracle for the column accessors.
//
// The two rules that catch readers: integers are decimal STRINGS, because JSON numbers are f64 in
// most parsers and u64::MAX does not survive one; and a float's `bits` is NORMATIVE - the hex of
// the raw IEEE bytes - so -0.0 read as +0.0 and a wrong NaN payload both fail here, which `==`
// would not catch.
using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Columns;

internal static class SidecarValues
{
    /// <summary>Compares every value a sidecar's <c>rows</c> lines carry against the batch.</summary>
    /// <returns>The number of values compared.</returns>
    internal static int AssertRows(RecordBatch batch, string entry)
    {
        int compared = 0;
        foreach (string line in System.IO.File.ReadLines(CorpusColumns.PathOf(entry, ".jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("kind").GetString() != "rows")
            {
                continue;
            }

            int from = root.GetProperty("from").GetInt32();
            JsonElement values = root.GetProperty("v");
            int i = 0;
            foreach (JsonElement value in values.EnumerateArray())
            {
                AssertValue(batch.Root, from + i, value, entry);
                i++;
                compared++;
            }
        }

        return compared;
    }

    /// <summary>The header line's <c>row_count</c>.</summary>
    internal static int RowCount(string entry)
    {
        foreach (string line in System.IO.File.ReadLines(CorpusColumns.PathOf(entry, ".jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("kind").GetString() == "header")
            {
                return root.GetProperty("row_count").GetInt32();
            }
        }

        throw new System.IO.InvalidDataException($"{entry} has no header line.");
    }

    /// <summary>
    /// The <c>kind</c> of the sidecar's root dtype node - <c>"struct"</c>, <c>"primitive"</c>, and
    /// so on. Used instead of the header's display string because Phase 0's DTypeFormatter spells
    /// two constructs differently from the reference: an extension is <c>ext(vortex.date, i32)</c>
    /// rather than <c>vortex.date[days](i32)</c>, and a fixed-size list is <c>fsl(i32, 3)</c>
    /// rather than <c>fixed_size_list(i32)[3]</c>. Both are Phase 0 gaps, reported rather than
    /// patched (contract §15 item 9), and the manifest-dtype comparison belongs to §14 anyway.
    /// </summary>
    internal static string RootKind(string entry)
    {
        foreach (string line in System.IO.File.ReadLines(CorpusColumns.PathOf(entry, ".jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("kind").GetString() == "dtype")
            {
                return root.GetProperty("tree").GetProperty("kind").GetString() ?? string.Empty;
            }
        }

        throw new System.IO.InvalidDataException($"{entry} has no dtype line.");
    }

    /// <summary>
    /// Whether this entry's values are reachable through the test walker: only vortex.flat,
    /// vortex.struct and vortex.zoned layouts, a root dtype Phase 1 decodes, and only array
    /// encodings this build implements. Everything is read off the sidecar, so the decision never
    /// depends on the code under test - except the last check, which asks the registry directly
    /// because "which encodings does this build decode" has no sidecar answer.
    /// </summary>
    internal static bool IsWalkable(string entry)
    {
        // types/no_dtype_segment deliberately omits the dtype segment; opening it needs a
        // caller-supplied schema through VortexOpenOptions, which is file-open's story (§7).
        if (entry == "types/no_dtype_segment")
        {
            return false;
        }

        DecoderBootstrap.Ensure();

        bool sawLayout = false;
        bool ok = true;
        foreach (string line in System.IO.File.ReadLines(CorpusColumns.PathOf(entry, ".jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            switch (root.GetProperty("kind").GetString())
            {
                case "layout":
                    sawLayout = true;
                    ok &= LayoutIsWalkable(root.GetProperty("tree"));
                    break;
                case "dtype":
                    ok &= root.GetProperty("tree").GetProperty("kind").GetString()
                        is not ("map" or "union" or "variant");
                    break;
                default:
                    break;
            }
        }

        return sawLayout && ok;
    }

    /// <summary>The sidecar's independently computed null count for the root path.</summary>
    internal static int RootNullCount(string entry)
    {
        foreach (string line in System.IO.File.ReadLines(CorpusColumns.PathOf(entry, ".jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.GetProperty("kind").GetString() == "null_counts")
            {
                return root.GetProperty("by_path").GetProperty(string.Empty).GetInt32();
            }
        }

        throw new System.IO.InvalidDataException($"{entry} has no null_counts line.");
    }

    private static bool LayoutIsWalkable(JsonElement node)
    {
        if (node.GetProperty("encoding_id").GetString()
            is not ("vortex.flat" or "vortex.struct" or "vortex.zoned"))
        {
            return false;
        }

        if (node.TryGetProperty("array_tree", out JsonElement arrayTree) && !ArrayIsDecodable(arrayTree))
        {
            return false;
        }

        if (node.TryGetProperty("children", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                if (!LayoutIsWalkable(child))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether every array encoding in one <c>vortex.flat</c> leaf's tree has a decoder in this
    /// build. vortex.fsst, vortex.alp, vortex.zstd, vortex.onpair, vortex.decimal_byte_parts,
    /// fastlanes.delta and vortex.patched are all out of Phase 1 scope (contract §2.8 and
    /// docs/90-registry.md), and the files carrying them belong to the conformance suite's
    /// expected-failure list rather than here.
    /// </summary>
    private static bool ArrayIsDecodable(JsonElement node)
    {
        string id = node.GetProperty("id").GetString()!;
        int byteCount = Encoding.UTF8.GetByteCount(id);
        Span<byte> utf8 = byteCount <= 64 ? stackalloc byte[64] : new byte[byteCount];
        int written = Encoding.UTF8.GetBytes(id, utf8);
        if (!ArrayDecoderTable.IsImplemented(EncodingRegistry.ResolveArray(utf8[..written])))
        {
            return false;
        }

        if (node.TryGetProperty("children", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                if (!ArrayIsDecodable(child))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void AssertValue(VortexColumn column, int row, JsonElement expected, string where)
    {
        string context = $"{where} row {row.ToString(CultureInfo.InvariantCulture)}";

        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.False(column.IsValid(row), context + ": expected null");
            return;
        }

        Assert.True(column.IsValid(row), context + ": expected a value");

        switch (column.Kind)
        {
            case CanonicalKind.Null:
                // Only reachable for a field this walker stood in for (Map/Union/Variant); the
                // caller filters those out before it gets here.
                Assert.Fail(context + ": a Null column cannot hold a value");
                break;

            case CanonicalKind.Bool:
                Assert.Equal(expected.GetBoolean(), column.AsBool()[row]);
                break;

            case CanonicalKind.Primitive:
                AssertPrimitive(column, row, expected);
                break;

            case CanonicalKind.Decimal:
            {
                DecimalColumn decimals = column.AsDecimal();
                Assert.Equal(
                    expected.GetProperty("unscaled").GetString(),
                    decimals[row].Unscaled.ToString());
                Assert.Equal(
                    expected.GetProperty("storage").GetString(),
                    StorageName(decimals.Storage));
                break;
            }

            case CanonicalKind.VarBinView:
            {
                byte[] want = Convert.FromBase64String(expected.GetProperty("b64").GetString()!);
                BinaryColumn bytes = column.AsBinary();
                Assert.Equal(expected.GetProperty("len").GetInt32(), bytes.GetLength(row));
                Assert.True(column.AsBinary().GetSpan(row).SequenceEqual(want), context);

                if (expected.TryGetProperty("char_count", out JsonElement charCount))
                {
                    string text = column.AsBinary().GetString(row)!;
                    int runes = 0;
                    foreach (System.Text.Rune _ in text.EnumerateRunes())
                    {
                        runes++;
                    }

                    Assert.Equal(charCount.GetInt32(), runes);
                    Assert.Equal(Encoding.UTF8.GetString(want), text);
                }

                break;
            }

            case CanonicalKind.ListView:
            {
                ListColumn list = column.AsList();
                long offset = list.GetOffset(row);
                int length = list.GetLength(row);
                Assert.Equal(expected.GetArrayLength(), length);
                int i = 0;
                foreach (JsonElement element in expected.EnumerateArray())
                {
                    AssertValue(column.AsList().Elements, checked((int)(offset + i)), element, where);
                    i++;
                }

                break;
            }

            case CanonicalKind.FixedSizeList:
            {
                column.AsFixedSizeList().GetRange(row, out int start, out int count);
                Assert.Equal(expected.GetArrayLength(), count);
                int i = 0;
                foreach (JsonElement element in expected.EnumerateArray())
                {
                    AssertValue(column.AsFixedSizeList().Elements, start + i, element, where);
                    i++;
                }

                break;
            }

            case CanonicalKind.Struct:
            {
                StructColumn fields = column.AsStruct();
                for (int i = 0; i < fields.FieldCount; i++)
                {
                    VortexColumn field = column.AsStruct().GetField(i);
                    if (field.DType.Kind is DTypeKind.Map or DTypeKind.Union or DTypeKind.Variant)
                    {
                        continue;
                    }

                    string name = column.AsStruct().GetFieldName(i);
                    Assert.True(expected.TryGetProperty(name, out JsonElement value), context + ": field " + name);
                    AssertValue(field, row, value, where);
                }

                break;
            }

            default:
                // Extension: the sidecar stores the RAW STORAGE value, so recurse into it.
                AssertValue(column.AsExtension().Storage, row, expected, where);
                break;
        }
    }

    private static void AssertPrimitive(VortexColumn column, int row, JsonElement expected)
    {
        switch (column.DType.PType)
        {
            case PType.U8:
                Assert.Equal(ulong.Parse(expected.GetString()!, CultureInfo.InvariantCulture), (ulong)column.AsPrimitive<byte>()[row]);
                break;
            case PType.U16:
                Assert.Equal(ulong.Parse(expected.GetString()!, CultureInfo.InvariantCulture), (ulong)column.AsPrimitive<ushort>()[row]);
                break;
            case PType.U32:
                Assert.Equal(ulong.Parse(expected.GetString()!, CultureInfo.InvariantCulture), (ulong)column.AsPrimitive<uint>()[row]);
                break;
            case PType.U64:
                Assert.Equal(ulong.Parse(expected.GetString()!, CultureInfo.InvariantCulture), column.AsPrimitive<ulong>()[row]);
                break;
            case PType.I8:
                Assert.Equal(long.Parse(expected.GetString()!, CultureInfo.InvariantCulture), (long)column.AsPrimitive<sbyte>()[row]);
                break;
            case PType.I16:
                Assert.Equal(long.Parse(expected.GetString()!, CultureInfo.InvariantCulture), (long)column.AsPrimitive<short>()[row]);
                break;
            case PType.I32:
                Assert.Equal(long.Parse(expected.GetString()!, CultureInfo.InvariantCulture), (long)column.AsPrimitive<int>()[row]);
                break;
            case PType.I64:
                Assert.Equal(long.Parse(expected.GetString()!, CultureInfo.InvariantCulture), column.AsPrimitive<long>()[row]);
                break;
            case PType.F16:
                Assert.Equal(
                    (ushort)Hex(expected),
                    BitConverter.HalfToUInt16Bits(column.AsPrimitive<Half>()[row]));
                break;
            case PType.F32:
                Assert.Equal(
                    (uint)Hex(expected),
                    BitConverter.SingleToUInt32Bits(column.AsPrimitive<float>()[row]));
                break;
            default:
                Assert.Equal(
                    Hex(expected),
                    BitConverter.DoubleToUInt64Bits(column.AsPrimitive<double>()[row]));
                break;
        }
    }

    /// <summary>The normative float encoding: hex of the raw IEEE bytes, big-endian.</summary>
    private static ulong Hex(JsonElement expected)
    {
        string bits = expected.GetProperty("bits").GetString()!;
        ReadOnlySpan<char> digits = bits.StartsWith("0x", StringComparison.Ordinal) ? bits.AsSpan(2) : bits.AsSpan();
        return ulong.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string StorageName(Vorticity.Types.Numerics.DecimalStorageType storage) => storage switch
    {
        Vorticity.Types.Numerics.DecimalStorageType.I8 => "i8",
        Vorticity.Types.Numerics.DecimalStorageType.I16 => "i16",
        Vorticity.Types.Numerics.DecimalStorageType.I32 => "i32",
        Vorticity.Types.Numerics.DecimalStorageType.I64 => "i64",
        Vorticity.Types.Numerics.DecimalStorageType.I128 => "i128",
        _ => "i256",
    };
}
