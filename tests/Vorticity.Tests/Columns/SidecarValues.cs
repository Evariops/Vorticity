// The sidecar's value grammar, turned into an oracle for the column accessors.
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
    /// so on. Used instead of the header's display string because the library's DTypeFormatter
    /// spells two constructs differently from the reference: an extension is
    /// <c>ext(vortex.date, i32)</c> rather than <c>vortex.date[days](i32)</c>, and a fixed-size
    /// list is <c>fsl(i32, 3)</c> rather than <c>fixed_size_list(i32)[3]</c>. Neither is patched
    /// around here, and comparing against the manifest's dtype is the conformance suite's job.
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
    /// vortex.struct and vortex.zoned layouts, a root dtype that is not a map, a union or a
    /// variant, and only array encodings this build implements. Everything is read off the
    /// sidecar, so the decision never depends on the code under test - except the last check,
    /// which asks the registry directly because "which encodings does this build decode" has no
    /// sidecar answer.
    /// </summary>
    internal static bool IsWalkable(string entry)
    {
        // types/no_dtype_segment deliberately omits the dtype segment; opening it needs a
        // caller-supplied schema through VortexOpenOptions, which is file-open's story.
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
    /// build. A file whose leaf reaches one that has none is left to the conformance suite, which
    /// requires the scan to name the missing component.
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

                // The storage width, with the one relaxation the corpus forces. A
                // vortex.decimal_byte_parts array canonicalizes to its msp child's width upstream
                // (`to_canonical_decimal`), but the sidecars are generated from `scalar_at`, which
                // hardcodes DecimalValue::I64 whatever the child is - so the corpus asks for i64
                // where the reference's own columnar path produces i16 or i32. A NARROWER width is
                // accepted because the unscaled value above already proved the two denote the same
                // number; a wider one is not, since nothing upstream produces one. The long form of
                // this is in Vorticity.Conformance's ValueComparer.IsAcceptableStorageWidening.
                string wantedStorage = expected.GetProperty("storage").GetString()!;
                string actualStorage = StorageName(decimals.Storage);
                if (!string.Equals(wantedStorage, actualStorage, StringComparison.Ordinal))
                {
                    Assert.True(
                        StorageWidth(actualStorage) < StorageWidth(wantedStorage),
                        $"{context}: storage {actualStorage}, sidecar {wantedStorage}");
                }

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

    /// <summary>Byte width of a sidecar storage name, or <c>-1</c> when it is not one.</summary>
    private static int StorageWidth(string name) => name switch
    {
        "i8" => 1,
        "i16" => 2,
        "i32" => 4,
        "i64" => 8,
        "i128" => 16,
        "i256" => 32,
        _ => -1,
    };

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
