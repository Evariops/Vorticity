// The value comparison itself: one decoded row against one sidecar value, recursively.
//
// THE RULE THAT SHAPES EVERY LINE BELOW. SIDECAR.md encodes a float as `{bits, dec}` and says
// "`bits` is normative", precisely so that -0.0 read as +0.0, or a NaN whose payload was
// canonicalized on the way through, fails the comparison. So nothing here goes through a decimal
// rendering, a `double` compare, or `Equals`: the f16/f32/f64 paths compare RAW IEEE BITS, and
// `NaN == NaN` never enters the picture. The same reasoning applies to the other three encodings
// that a "natural" .NET comparison would corrupt:
//
//   * integers are decimal STRINGS, because u64::MAX does not survive a JSON f64. They are parsed
//     into the exact CLR width of the column and compared as integers;
//   * Utf8 and Binary are base64 of the BYTES, plus the byte length and the scalar count. All three
//     are checked - `len != char_count` on every non-ASCII value is the point of carrying both;
//   * decimals are `{unscaled, storage}` and are compared as the unscaled i256 and the storage
//     width, never as a `System.Decimal`, which cannot hold precision 76. The one documented
//     relaxation is in IsAcceptableStorageWidening, and it exists because the reference disagrees
//     with itself rather than because the check was inconvenient.
//
// `null` is JSON null and nothing else ever is: an empty string, an empty list and an empty map are
// values. That is one branch, taken before the dtype is even looked at.
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Conformance.Sidecar;
using Vorticity.Types;
using Vorticity.Types.Numerics;

namespace Vorticity.Conformance.Comparison;

/// <summary>Compares decoded columns against the sidecar's expected values.</summary>
internal static class ValueComparer
{
    /// <summary>
    /// Compares one row of one column against its sidecar value, recursing into structs, lists and
    /// extension storage.
    /// </summary>
    /// <param name="expected">The sidecar value for this row.</param>
    /// <param name="column">The decoded column.</param>
    /// <param name="index">The row index within <paramref name="column"/>.</param>
    /// <param name="path">The dotted column path, for localization. Empty at the root.</param>
    /// <param name="fileRow">The absolute file row index, for localization.</param>
    /// <param name="log">Where mismatches go.</param>
    internal static void Compare(
        JsonValue expected, VortexColumn column, int index, string path, long fileRow, MismatchLog log)
    {
        // Every node counts: the validity bit is compared here whatever the dtype, and a container
        // node's own null-ness is a value in its own right.
        log.CountValue();
        bool valid = column.IsValid(index);

        // SIDECAR.md: "`null` is JSON null and nothing else ever is."
        if (expected.IsNull)
        {
            if (valid)
            {
                log.Add(path, fileRow, "a null row read back as a value", "null", Describe(column, index));
            }

            return;
        }

        if (!valid)
        {
            log.Add(path, fileRow, "a value read back as null", expected.Summary(), "null");
            return;
        }

        DType dtype = column.DType;
        switch (dtype.Kind)
        {
            case DTypeKind.Null:
                // Unreachable in a well-formed file: every row of a Null column is null, so the
                // branch above took it. Reaching here means validity disagreed with the dtype.
                log.Add(path, fileRow, "a Null-dtype column reported a valid row", "null", "a value");
                return;

            case DTypeKind.Bool:
                CompareBool(expected, column, index, path, fileRow, log);
                return;

            case DTypeKind.Primitive:
                ComparePrimitive(expected, column, index, path, fileRow, log);
                return;

            case DTypeKind.Decimal:
                CompareDecimal(expected, column, index, path, fileRow, log);
                return;

            case DTypeKind.Utf8:
            case DTypeKind.Binary:
                CompareBytes(expected, column, index, path, fileRow, log, dtype.Kind == DTypeKind.Utf8);
                return;

            case DTypeKind.Struct:
                CompareStruct(expected, column, index, path, fileRow, log);
                return;

            case DTypeKind.List:
                CompareList(expected, column, index, path, fileRow, log);
                return;

            case DTypeKind.FixedSizeList:
                CompareFixedSizeList(expected, column, index, path, fileRow, log);
                return;

            case DTypeKind.Extension:
                // SIDECAR.md: "extension: the storage value; the extension id and metadata are in
                // the dtype line." The dtype line is checked by SchemaComparer.
                CompareExtension(expected, column, index, path, fileRow, log);
                return;

            default:
                // Map, Variant and Union carry values this harness cannot spell. They are reachable
                // only from a file with rows of that dtype, which Phase 1 does not claim; saying so
                // out loud beats comparing nothing and reporting a pass.
                log.Add(
                    path,
                    fileRow,
                    $"the harness cannot compare a {dtype.Kind} value",
                    expected.Summary(),
                    $"a {dtype.Kind} column");
                return;
        }
    }

    private static void CompareBool(
        JsonValue expected, VortexColumn column, int index, string path, long fileRow, MismatchLog log)
    {
        if (expected.Kind != JsonKind.Boolean)
        {
            log.Add(path, fileRow, "a bool column met a non-bool sidecar value", expected.Summary(), "bool");
            return;
        }

        bool actual = column.AsBool()[index];
        if (actual != expected.Boolean)
        {
            log.Add(path, fileRow, "bool value", Text(expected.Boolean), Text(actual));
        }
    }

    private static void ComparePrimitive(
        JsonValue expected, VortexColumn column, int index, string path, long fileRow, MismatchLog log)
    {
        switch (column.DType.PType)
        {
            case PType.U8:
                CompareUnsigned(expected, column.AsPrimitive<byte>()[index], path, fileRow, log);
                return;
            case PType.U16:
                CompareUnsigned(expected, column.AsPrimitive<ushort>()[index], path, fileRow, log);
                return;
            case PType.U32:
                CompareUnsigned(expected, column.AsPrimitive<uint>()[index], path, fileRow, log);
                return;
            case PType.U64:
                CompareUnsigned(expected, column.AsPrimitive<ulong>()[index], path, fileRow, log);
                return;
            case PType.I8:
                CompareSigned(expected, column.AsPrimitive<sbyte>()[index], path, fileRow, log);
                return;
            case PType.I16:
                CompareSigned(expected, column.AsPrimitive<short>()[index], path, fileRow, log);
                return;
            case PType.I32:
                CompareSigned(expected, column.AsPrimitive<int>()[index], path, fileRow, log);
                return;
            case PType.I64:
                CompareSigned(expected, column.AsPrimitive<long>()[index], path, fileRow, log);
                return;
            case PType.F16:
                CompareFloatBits(
                    expected, BitConverter.HalfToUInt16Bits(column.AsPrimitive<Half>()[index]), 4, path, fileRow, log);
                return;
            case PType.F32:
                CompareFloatBits(
                    expected, BitConverter.SingleToUInt32Bits(column.AsPrimitive<float>()[index]), 8, path, fileRow, log);
                return;
            default:
                CompareFloatBits(
                    expected, BitConverter.DoubleToUInt64Bits(column.AsPrimitive<double>()[index]), 16, path, fileRow, log);
                return;
        }
    }

    private static void CompareUnsigned(
        JsonValue expected, ulong actual, string path, long fileRow, MismatchLog log)
    {
        if (expected.Kind != JsonKind.String ||
            !ulong.TryParse(expected.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong wanted))
        {
            log.Add(path, fileRow, "an unsigned column met a non-integer sidecar value",
                expected.Summary(), actual.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (wanted != actual)
        {
            log.Add(path, fileRow, "integer value", wanted.ToString(CultureInfo.InvariantCulture),
                actual.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void CompareSigned(
        JsonValue expected, long actual, string path, long fileRow, MismatchLog log)
    {
        if (expected.Kind != JsonKind.String ||
            !long.TryParse(expected.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long wanted))
        {
            log.Add(path, fileRow, "a signed column met a non-integer sidecar value",
                expected.Summary(), actual.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (wanted != actual)
        {
            log.Add(path, fileRow, "integer value", wanted.ToString(CultureInfo.InvariantCulture),
                actual.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The normative float comparison: raw IEEE bits, big-endian hex, exactly as SIDECAR.md defines
    /// them. <paramref name="hexDigits"/> is 4, 8 or 16 - a bit pattern of the wrong width is a
    /// mismatch, not a value to be widened into agreement.
    /// </summary>
    private static void CompareFloatBits(
        JsonValue expected, ulong actualBits, int hexDigits, string path, long fileRow, MismatchLog log)
    {
        if (expected.Kind != JsonKind.Object)
        {
            log.Add(path, fileRow, "a float column met a non-object sidecar value",
                expected.Summary(), Hex(actualBits, hexDigits));
            return;
        }

        JsonValue? bits = expected.Find("bits");
        if (bits is null || bits.Kind != JsonKind.String || !TryParseHex(bits.Text, hexDigits, out ulong wanted))
        {
            log.Add(path, fileRow, "a float value with no usable 'bits'",
                expected.Summary(), Hex(actualBits, hexDigits));
            return;
        }

        if (wanted != actualBits)
        {
            // `dec` is carried along in the message because it is what a human reads, but it is
            // never what the comparison consults.
            string dec = expected.Find("dec")?.Text ?? "?";
            log.Add(path, fileRow, "float bits",
                $"{Hex(wanted, hexDigits)} ({dec})", Hex(actualBits, hexDigits));
        }
    }

    private static void CompareDecimal(
        JsonValue expected, VortexColumn column, int index, string path, long fileRow, MismatchLog log)
    {
        if (expected.Kind != JsonKind.Object)
        {
            log.Add(path, fileRow, "a decimal column met a non-object sidecar value",
                expected.Summary(), "a decimal");
            return;
        }

        DecimalColumn decimals = column.AsDecimal();
        VortexDecimal actual = decimals[index];

        string wanted = expected.Find("unscaled")?.Text ?? "?";
        string unscaled = actual.Unscaled.ToString();
        bool valueMatches = string.Equals(wanted, unscaled, StringComparison.Ordinal);
        if (!valueMatches)
        {
            log.Add(path, fileRow, "decimal unscaled value", wanted, unscaled);
        }

        string wantedStorage = expected.Find("storage")?.Text ?? "?";
        string actualStorage = StorageName(decimals.Storage);
        if (!string.Equals(wantedStorage, actualStorage, StringComparison.Ordinal) &&
            !IsAcceptableStorageWidening(wantedStorage, actualStorage, valueMatches))
        {
            log.Add(path, fileRow, "decimal storage width", wantedStorage, actualStorage);
        }
    }

    /// <summary>
    /// Whether a storage width narrower than the sidecar's is the known upstream widening rather
    /// than a defect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference implementation disagrees with ITSELF about the storage width of a
    /// <c>vortex.decimal_byte_parts</c> array, and the corpus records the losing side. Its columnar
    /// path, <c>to_canonical_decimal</c>, builds
    /// <c>DecimalArray::new_unchecked(prim.to_buffer::&lt;P&gt;(), ..)</c> and so keeps the msp
    /// child's own width; its scalar path, <c>OperationsVTable::scalar_at</c>, ends in a hardcoded
    /// <c>ScalarValue::Decimal(DecimalValue::I64(value))</c> and so reports i64 whatever the child
    /// is. The sidecars are generated from scalars, so every decimal_byte_parts row in the corpus
    /// claims i64 while the same file's canonical array is i16 or i32 - visible in the corpus
    /// itself, where <c>types/decimal4_2_nonnull_r1024</c> (byte parts) says i64 and
    /// <c>types/decimal4_2_nonnull_r1025</c> (plain <c>vortex.decimal</c>, same dtype) says i16.
    /// </para>
    /// <para>
    /// Vorticity follows the columnar path: it is the one a reader materializes, it is zero-copy,
    /// and the scalar path's widening is a stated upstream shortcut that cannot survive an i128 msp
    /// (<c>as_::&lt;i64&gt;()</c>). So a width NARROWER than the sidecar's is accepted when the
    /// unscaled value is identical - the two representations denote the same number. A width WIDER
    /// than the sidecar's is still a mismatch: nothing upstream produces one, so it would mean we
    /// invented precision.
    /// </para>
    /// </remarks>
    /// <param name="wantedStorage">The sidecar's storage name.</param>
    /// <param name="actualStorage">The storage name we produced.</param>
    /// <param name="valueMatches">Whether the unscaled values were identical.</param>
    private static bool IsAcceptableStorageWidening(
        string wantedStorage, string actualStorage, bool valueMatches) =>
        valueMatches && StorageWidth(actualStorage) < StorageWidth(wantedStorage);

    /// <summary>Byte width of a sidecar storage name, or <c>-1</c> when it is not one.</summary>
    /// <param name="name">The storage name, e.g. <c>i32</c>.</param>
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

    private static void CompareBytes(
        JsonValue expected, VortexColumn column, int index, string path, long fileRow, MismatchLog log, bool utf8)
    {
        if (expected.Kind != JsonKind.Object)
        {
            log.Add(path, fileRow, "a binary column met a non-object sidecar value",
                expected.Summary(), "bytes");
            return;
        }

        JsonValue? b64 = expected.Find("b64");
        if (b64 is null || b64.Kind != JsonKind.String)
        {
            log.Add(path, fileRow, "a binary value with no 'b64'", expected.Summary(), "bytes");
            return;
        }

        byte[] wanted;
        try
        {
            wanted = Convert.FromBase64String(b64.Text);
        }
        catch (FormatException)
        {
            log.Add(path, fileRow, "a 'b64' that is not base64", expected.Summary(), "bytes");
            return;
        }

        BinaryColumn bytes = column.AsBinary();
        ReadOnlySpan<byte> actual = bytes.GetSpan(index);

        if (!actual.SequenceEqual(wanted))
        {
            log.Add(path, fileRow, "byte value", Bytes(wanted), Bytes(actual));
            return;
        }

        // `len` is the BYTE length and is carried separately from the payload on purpose.
        JsonValue? length = expected.Find("len");
        if (length is not null && length.Kind == JsonKind.Number &&
            int.TryParse(length.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wantedLength) &&
            wantedLength != actual.Length)
        {
            log.Add(path, fileRow, "byte length",
                wantedLength.ToString(CultureInfo.InvariantCulture),
                actual.Length.ToString(CultureInfo.InvariantCulture));
        }

        if (!utf8)
        {
            return;
        }

        // `char_count` is the Unicode SCALAR count and differs from `len` on every non-ASCII value,
        // "which is the point": comparing only the bytes would let a UTF-8 decoder that splits a
        // multi-byte sequence pass.
        JsonValue? chars = expected.Find("char_count");
        if (chars is null || chars.Kind != JsonKind.Number ||
            !int.TryParse(chars.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wantedChars))
        {
            return;
        }

        int actualChars = ScalarCount(actual);
        if (wantedChars != actualChars)
        {
            log.Add(path, fileRow, "utf8 scalar count",
                wantedChars.ToString(CultureInfo.InvariantCulture),
                actualChars.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void CompareStruct(
        JsonValue expected, VortexColumn column, int index, string path, long fileRow, MismatchLog log)
    {
        if (expected.Kind != JsonKind.Object)
        {
            log.Add(path, fileRow, "a struct column met a non-object sidecar value",
                expected.Summary(), "a struct");
            return;
        }

        StructColumn fields = column.AsStruct();
        if (expected.Keys.Length != fields.FieldCount)
        {
            log.Add(path, fileRow, "struct field count",
                expected.Keys.Length.ToString(CultureInfo.InvariantCulture),
                fields.FieldCount.ToString(CultureInfo.InvariantCulture));
            return;
        }

        for (int i = 0; i < fields.FieldCount; i++)
        {
            // Positional, not by name: SIDECAR.md says the object is "keyed by field name, in
            // schema order", and a Vortex field name may be empty, contain a '.', or repeat.
            string name = fields.GetFieldName(i);
            string child = path.Length == 0 ? name : path + "." + name;

            if (!string.Equals(expected.Keys[i], name, StringComparison.Ordinal))
            {
                log.Add(child, fileRow, $"struct field {i} name",
                    Quote(expected.Keys[i]), Quote(name));
                continue;
            }

            Compare(expected.Values[i], fields.GetField(i), index, child, fileRow, log);
        }
    }

    private static void CompareList(
        JsonValue expected, VortexColumn column, int index, string path, long fileRow, MismatchLog log)
    {
        if (expected.Kind != JsonKind.Array)
        {
            log.Add(path, fileRow, "a list column met a non-array sidecar value",
                expected.Summary(), "a list");
            return;
        }

        ListColumn list = column.AsList();
        int count = list.GetLength(index);
        if (count != expected.Items.Length)
        {
            log.Add(path, fileRow, "list length",
                expected.Items.Length.ToString(CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture));
            return;
        }

        long offset = list.GetOffset(index);
        VortexColumn elements = list.Elements;
        for (int i = 0; i < count; i++)
        {
            long at = offset + i;
            if (at < 0 || at >= elements.Length)
            {
                log.Add($"{path}[{i}]", fileRow, "a list element outside the elements column",
                    "an element", $"offset {at.ToString(CultureInfo.InvariantCulture)} of " +
                    $"{elements.Length.ToString(CultureInfo.InvariantCulture)}");
                return;
            }

            Compare(expected.Items[i], elements, (int)at, $"{path}[{i}]", fileRow, log);
        }
    }

    private static void CompareFixedSizeList(
        JsonValue expected, VortexColumn column, int index, string path, long fileRow, MismatchLog log)
    {
        if (expected.Kind != JsonKind.Array)
        {
            log.Add(path, fileRow, "a fixed_size_list column met a non-array sidecar value",
                expected.Summary(), "a fixed-size list");
            return;
        }

        FixedSizeListColumn list = column.AsFixedSizeList();
        list.GetRange(index, out int start, out int count);
        if (count != expected.Items.Length)
        {
            log.Add(path, fileRow, "fixed_size_list length",
                expected.Items.Length.ToString(CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture));
            return;
        }

        VortexColumn elements = list.Elements;
        for (int i = 0; i < count; i++)
        {
            int at = start + i;
            if ((uint)at >= (uint)elements.Length)
            {
                log.Add($"{path}[{i}]", fileRow, "a fixed_size_list element outside the elements column",
                    "an element", $"offset {at.ToString(CultureInfo.InvariantCulture)} of " +
                    $"{elements.Length.ToString(CultureInfo.InvariantCulture)}");
                return;
            }

            Compare(expected.Items[i], elements, at, $"{path}[{i}]", fileRow, log);
        }
    }

    private static void CompareExtension(
        JsonValue expected, VortexColumn column, int index, string path, long fileRow, MismatchLog log)
    {
        ExtensionColumn extension = column.AsExtension();
        Compare(expected, extension.Storage, index, path, fileRow, log);
    }

    /// <summary>The number of Unicode scalars in valid UTF-8: every byte that is not a continuation.</summary>
    private static int ScalarCount(ReadOnlySpan<byte> utf8)
    {
        int count = 0;
        foreach (byte b in utf8)
        {
            if ((b & 0xC0) != 0x80)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryParseHex(string text, int hexDigits, out ulong value)
    {
        value = 0;
        ReadOnlySpan<char> digits = text.AsSpan();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        // A pattern of the wrong width means the sidecar and the column disagree about the float's
        // size, which is a mismatch and not something to normalize away.
        if (digits.Length != hexDigits)
        {
            return false;
        }

        return ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string Hex(ulong bits, int hexDigits) =>
        "0x" + bits.ToString("x" + hexDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Text(bool value) => value ? "true" : "false";

    private static string Quote(string text) => "\"" + text + "\"";

    private static string Bytes(ReadOnlySpan<byte> bytes)
    {
        const int Max = 24;
        StringBuilder builder = new StringBuilder();
        builder.Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes 0x");
        for (int i = 0; i < bytes.Length && i < Max; i++)
        {
            builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        if (bytes.Length > Max)
        {
            builder.Append("...");
        }

        return builder.ToString();
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

    /// <summary>A short rendering of a decoded value, for the "expected null" message.</summary>
    private static string Describe(VortexColumn column, int index)
    {
        DType dtype = column.DType;
        switch (dtype.Kind)
        {
            case DTypeKind.Bool:
                return Text(column.AsBool()[index]);
            case DTypeKind.Primitive:
                return DescribePrimitive(column, index);
            case DTypeKind.Decimal:
                return column.AsDecimal()[index].ToString();
            case DTypeKind.Utf8:
            case DTypeKind.Binary:
                return Bytes(column.AsBinary().GetSpan(index));
            case DTypeKind.List:
                return $"a list of {column.AsList().GetLength(index).ToString(CultureInfo.InvariantCulture)}";
            case DTypeKind.FixedSizeList:
                return $"a fixed-size list of {column.AsFixedSizeList().Size.ToString(CultureInfo.InvariantCulture)}";
            case DTypeKind.Struct:
                return $"a struct of {column.AsStruct().FieldCount.ToString(CultureInfo.InvariantCulture)} fields";
            case DTypeKind.Extension:
                return "an extension value";
            default:
                return "a value";
        }
    }

    private static string DescribePrimitive(VortexColumn column, int index) => column.DType.PType switch
    {
        PType.U8 => column.AsPrimitive<byte>()[index].ToString(CultureInfo.InvariantCulture),
        PType.U16 => column.AsPrimitive<ushort>()[index].ToString(CultureInfo.InvariantCulture),
        PType.U32 => column.AsPrimitive<uint>()[index].ToString(CultureInfo.InvariantCulture),
        PType.U64 => column.AsPrimitive<ulong>()[index].ToString(CultureInfo.InvariantCulture),
        PType.I8 => column.AsPrimitive<sbyte>()[index].ToString(CultureInfo.InvariantCulture),
        PType.I16 => column.AsPrimitive<short>()[index].ToString(CultureInfo.InvariantCulture),
        PType.I32 => column.AsPrimitive<int>()[index].ToString(CultureInfo.InvariantCulture),
        PType.I64 => column.AsPrimitive<long>()[index].ToString(CultureInfo.InvariantCulture),
        PType.F16 => Hex(BitConverter.HalfToUInt16Bits(column.AsPrimitive<Half>()[index]), 4),
        PType.F32 => Hex(BitConverter.SingleToUInt32Bits(column.AsPrimitive<float>()[index]), 8),
        _ => Hex(BitConverter.DoubleToUInt64Bits(column.AsPrimitive<double>()[index]), 16),
    };
}
