using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace Vorticity.Types.Variants;

/// <summary>
/// Builds values of the Parquet Variant encoding: a metadata of one sorted dictionary of every key
/// the value's objects use, and the value with its objects' fields in their keys' order, ids,
/// offsets and counts each in the fewest bytes that hold them.
/// </summary>
/// <remarks>
/// What <see cref="Variant"/>'s factories build. An object or an array of variants of their own
/// metadata is written again under one dictionary, its values rewritten with the new ids: a
/// convenience for values a caller assembles, not a path rows take in bulk.
/// </remarks>
internal static class VariantEncoding
{
    /// <summary>The empty dictionary of a value that names no key.</summary>
    internal static readonly byte[] EmptyMetadata = [0x01, 0x00, 0x00];

    /// <summary>The variant null.</summary>
    internal static readonly byte[] NullValue = [0x00];

    /// <summary>The order the standard sorts keys in: the unsigned order of their UTF-8 bytes.</summary>
    internal static readonly Comparer<byte[]> KeyOrder = Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b));

    /// <summary>A primitive of type <paramref name="typeId"/> and <paramref name="payload"/>.</summary>
    internal static byte[] Primitive(int typeId, ReadOnlySpan<byte> payload)
    {
        byte[] value = new byte[1 + payload.Length];
        value[0] = ParquetVariant.Primitive(typeId);
        payload.CopyTo(value.AsSpan(1));
        return value;
    }

    internal static byte[] Bool(bool value) => [ParquetVariant.Primitive(value ? 1 : 2)];

    /// <summary>An integer in the narrowest of the four widths that holds it.</summary>
    internal static byte[] Integer(long value)
    {
        Span<byte> written = stackalloc byte[9];
        return written[..PutInteger(written, value)].ToArray();
    }

    /// <summary><see cref="Integer"/>'s bytes written into <paramref name="into"/>, nine at most. The bytes written.</summary>
    internal static int PutInteger(Span<byte> into, long value)
    {
        if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            into[0] = ParquetVariant.Primitive(3);
            into[1] = (byte)(sbyte)value;
            return 2;
        }

        if (value is >= short.MinValue and <= short.MaxValue)
        {
            into[0] = ParquetVariant.Primitive(4);
            BinaryPrimitives.WriteInt16LittleEndian(into[1..], (short)value);
            return 3;
        }

        if (value is >= int.MinValue and <= int.MaxValue)
        {
            into[0] = ParquetVariant.Primitive(5);
            BinaryPrimitives.WriteInt32LittleEndian(into[1..], (int)value);
            return 5;
        }

        into[0] = ParquetVariant.Primitive(6);
        BinaryPrimitives.WriteInt64LittleEndian(into[1..], value);
        return 9;
    }

    internal static byte[] Double(double value)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(payload, value);
        return Primitive(7, payload);
    }

    internal static byte[] Float(float value)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(payload, value);
        return Primitive(14, payload);
    }

    /// <summary>A decimal of <paramref name="scale"/> in the narrowest of its three widths that holds its unscaled value.</summary>
    internal static byte[] Decimal(Int128 unscaled, byte scale)
    {
        Span<byte> written = stackalloc byte[18];
        return written[..PutDecimal(written, unscaled, scale)].ToArray();
    }

    /// <summary><see cref="Decimal"/>'s bytes written into <paramref name="into"/>, eighteen at most. The bytes written.</summary>
    internal static int PutDecimal(Span<byte> into, Int128 unscaled, byte scale)
    {
        into[1] = scale;
        if (unscaled >= int.MinValue && unscaled <= int.MaxValue)
        {
            into[0] = ParquetVariant.Primitive(8);
            BinaryPrimitives.WriteInt32LittleEndian(into[2..], (int)unscaled);
            return 6;
        }

        if (unscaled >= long.MinValue && unscaled <= long.MaxValue)
        {
            into[0] = ParquetVariant.Primitive(9);
            BinaryPrimitives.WriteInt64LittleEndian(into[2..], (long)unscaled);
            return 10;
        }

        into[0] = ParquetVariant.Primitive(10);
        BinaryPrimitives.WriteInt128LittleEndian(into[2..], unscaled);
        return 18;
    }

    internal static byte[] Date(int days)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, days);
        return Primitive(11, payload);
    }

    /// <summary>A timestamp of microseconds, adjusted to UTC or local.</summary>
    internal static byte[] Timestamp(long microseconds, bool utc)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, microseconds);
        return Primitive(utc ? 12 : 13, payload);
    }

    internal static byte[] Time(long microseconds)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, microseconds);
        return Primitive(17, payload);
    }

    internal static byte[] Uuid(Guid value)
    {
        Span<byte> payload = stackalloc byte[16];
        _ = value.TryWriteBytes(payload, bigEndian: true, out _);
        return Primitive(20, payload);
    }

    /// <summary>A string, in the short form when it fits in the header's six bits.</summary>
    internal static byte[] String(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length <= ParquetVariant.MaxShortString)
        {
            byte[] shortValue = new byte[1 + utf8.Length];
            shortValue[0] = (byte)((utf8.Length << 2) | ParquetVariant.BasicShortString);
            utf8.CopyTo(shortValue.AsSpan(1));
            return shortValue;
        }

        return Sized(16, utf8);
    }

    internal static byte[] Binary(ReadOnlySpan<byte> bytes) => Sized(15, bytes);

    private static byte[] Sized(int typeId, ReadOnlySpan<byte> bytes)
    {
        byte[] value = new byte[5 + bytes.Length];
        value[0] = ParquetVariant.Primitive(typeId);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(1), (uint)bytes.Length);
        bytes.CopyTo(value.AsSpan(5));
        return value;
    }

    /// <summary>The metadata of the dictionary <paramref name="keys"/>, sorted and unique.</summary>
    internal static byte[] Metadata(IReadOnlyList<byte[]> keys)
    {
        if (keys.Count == 0)
        {
            return EmptyMetadata;
        }

        int bytes = 0;
        foreach (byte[] key in keys)
        {
            bytes = checked(bytes + key.Length);
        }

        int width = ParquetVariant.WidthOf((uint)Math.Max(bytes, keys.Count));
        byte[] metadata = new byte[1 + width + ((keys.Count + 1) * width) + bytes];

        // Version 1, the strings sorted and unique, the offsets' width.
        metadata[0] = (byte)(0x01 | 0x10 | ((width - 1) << 6));
        int at = 1;
        ParquetVariant.Put(metadata, ref at, (uint)keys.Count, width);
        int offset = 0;
        foreach (byte[] key in keys)
        {
            ParquetVariant.Put(metadata, ref at, (uint)offset, width);
            offset += key.Length;
        }

        ParquetVariant.Put(metadata, ref at, (uint)offset, width);
        foreach (byte[] key in keys)
        {
            key.CopyTo(metadata, at);
            at += key.Length;
        }

        return metadata;
    }

    /// <summary>An object of <paramref name="fields"/>, in their keys' order, under ids of one dictionary.</summary>
    /// <param name="fields">Each field's id and value, sorted by key.</param>
    internal static byte[] Object(IReadOnlyList<(int Id, byte[] Value)> fields)
    {
        int count = fields.Count;
        int maxId = 0;
        int size = 0;
        foreach ((int id, byte[] value) in fields)
        {
            maxId = Math.Max(maxId, id);
            size = checked(size + value.Length);
        }

        int countWidth = count > 0xFF ? 4 : 1;
        int idWidth = ParquetVariant.WidthOf((uint)maxId);
        int offsetWidth = ParquetVariant.WidthOf((uint)size);
        byte[] result = new byte[checked(1 + countWidth + (count * idWidth) + ((count + 1) * offsetWidth) + size)];
        result[0] = (byte)(((((countWidth == 4 ? 1 : 0) << 4) | ((idWidth - 1) << 2) | (offsetWidth - 1)) << 2) | ParquetVariant.BasicObject);
        int at = 1;
        ParquetVariant.Put(result, ref at, (uint)count, countWidth);
        foreach ((int id, _) in fields)
        {
            ParquetVariant.Put(result, ref at, (uint)id, idWidth);
        }

        int offset = 0;
        foreach ((_, byte[] value) in fields)
        {
            ParquetVariant.Put(result, ref at, (uint)offset, offsetWidth);
            offset += value.Length;
        }

        ParquetVariant.Put(result, ref at, (uint)offset, offsetWidth);
        foreach ((_, byte[] value) in fields)
        {
            value.CopyTo(result, at);
            at += value.Length;
        }

        return result;
    }

    /// <summary>An array of <paramref name="elements"/>.</summary>
    internal static byte[] Array(IReadOnlyList<byte[]> elements)
    {
        int count = elements.Count;
        int size = 0;
        foreach (byte[] element in elements)
        {
            size = checked(size + element.Length);
        }

        int countWidth = count > 0xFF ? 4 : 1;
        int offsetWidth = ParquetVariant.WidthOf((uint)size);
        byte[] result = new byte[checked(1 + countWidth + ((count + 1) * offsetWidth) + size)];
        result[0] = (byte)(((((countWidth == 4 ? 1 : 0) << 2) | (offsetWidth - 1)) << 2) | ParquetVariant.BasicArray);
        int at = 1;
        ParquetVariant.Put(result, ref at, (uint)count, countWidth);
        int offset = 0;
        foreach (byte[] element in elements)
        {
            ParquetVariant.Put(result, ref at, (uint)offset, offsetWidth);
            offset += element.Length;
        }

        ParquetVariant.Put(result, ref at, (uint)offset, offsetWidth);
        foreach (byte[] element in elements)
        {
            element.CopyTo(result, at);
            at += element.Length;
        }

        return result;
    }

    /// <summary>Adds every key the objects of <paramref name="value"/> use to <paramref name="keys"/>.</summary>
    internal static void CollectKeys(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value, SortedSet<byte[]> keys)
    {
        VariantKind kind = VariantScalar.KindOf(value);
        if (kind is not (VariantKind.Object or VariantKind.Array))
        {
            return;
        }

        VariantNested nested = VariantNested.Read(value);
        VariantDictionary dictionary = kind == VariantKind.Object ? VariantDictionary.Read(metadata) : default;
        for (int i = 0; i < nested.Count; i++)
        {
            if (kind == VariantKind.Object)
            {
                // The set orders by bytes, so a key already in it is not added twice.
                _ = keys.Add(dictionary[nested.IdAt(i)].ToArray());
            }

            CollectKeys(metadata, nested.ValueAt(i), keys);
        }
    }

    /// <summary>
    /// <paramref name="value"/>, read under <paramref name="metadata"/>, written again under the
    /// dictionary <paramref name="keys"/>, which holds every key it uses.
    /// </summary>
    internal static byte[] Rewrite(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value, List<byte[]> keys)
    {
        VariantKind kind = VariantScalar.KindOf(value);
        if (kind == VariantKind.Array)
        {
            VariantNested array = VariantNested.Read(value);
            List<byte[]> elements = new(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                elements.Add(Rewrite(metadata, array.ValueAt(i), keys));
            }

            return Array(elements);
        }

        if (kind != VariantKind.Object)
        {
            return value[..ParquetVariant.SizeOf(value)].ToArray();
        }

        VariantNested nested = VariantNested.Read(value);
        VariantDictionary dictionary = VariantDictionary.Read(metadata);
        List<(int Id, byte[] Value)> fields = new(nested.Count);
        for (int i = 0; i < nested.Count; i++)
        {
            int id = keys.BinarySearch(dictionary[nested.IdAt(i)].ToArray(), KeyOrder);
            fields.Add((id, Rewrite(metadata, nested.ValueAt(i), keys)));
        }

        // The new ids follow the keys' order, which is the order the fields are written in.
        fields.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return Object(fields);
    }

    /// <summary>The value of key <paramref name="name"/> in the object <paramref name="value"/>, or false when it has no such key.</summary>
    internal static bool TryField(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value, ReadOnlySpan<byte> name, out ReadOnlySpan<byte> field)
    {
        field = default;
        if (VariantScalar.KindOf(value) != VariantKind.Object)
        {
            return false;
        }

        VariantDictionary dictionary = VariantDictionary.Read(metadata);
        int id = dictionary.Find(name);
        if (id < 0)
        {
            return false;
        }

        return TryField(VariantNested.Read(value), id, out field);
    }

    /// <summary>The field of id <paramref name="id"/> in <paramref name="nested"/>, an object.</summary>
    internal static bool TryField(VariantNested nested, int id, out ReadOnlySpan<byte> field)
    {
        for (int i = 0; i < nested.Count; i++)
        {
            if (nested.IdAt(i) == id)
            {
                field = nested.ValueAt(i);
                return true;
            }
        }

        field = default;
        return false;
    }

    /// <summary>Element <paramref name="index"/> of the array <paramref name="value"/>, or false when there is none.</summary>
    internal static bool TryElement(ReadOnlySpan<byte> value, int index, out ReadOnlySpan<byte> element)
    {
        element = default;
        if (VariantScalar.KindOf(value) != VariantKind.Array)
        {
            return false;
        }

        VariantNested array = VariantNested.Read(value);
        if ((uint)index >= (uint)array.Count)
        {
            return false;
        }

        element = array.ValueAt(index);
        return true;
    }

    /// <summary>
    /// Whether two values are equal under the encoding's equivalence classes: integers and decimals
    /// by their number, floats and doubles by theirs, an object by its keys and their values
    /// whatever ids name them, an array element by element, anything else by its type and bytes.
    /// </summary>
    internal static bool ValueEquals(ReadOnlySpan<byte> leftMetadata, ReadOnlySpan<byte> left, ReadOnlySpan<byte> rightMetadata, ReadOnlySpan<byte> right)
    {
        VariantScalar a = VariantScalar.Read(left);
        VariantScalar b = VariantScalar.Read(right);
        if (IsExact(a.Kind) && IsExact(b.Kind))
        {
            return Normalized(a) == Normalized(b);
        }

        if (a.Kind is VariantKind.Float or VariantKind.Double && b.Kind is VariantKind.Float or VariantKind.Double)
        {
            return a.Number.Equals(b.Number);
        }

        if (a.Kind != b.Kind)
        {
            return false;
        }

        switch (a.Kind)
        {
            case VariantKind.Object:
            {
                VariantNested x = VariantNested.Read(left);
                VariantNested y = VariantNested.Read(right);
                if (x.Count != y.Count)
                {
                    return false;
                }

                VariantDictionary xs = VariantDictionary.Read(leftMetadata);
                VariantDictionary ys = VariantDictionary.Read(rightMetadata);
                for (int i = 0; i < x.Count; i++)
                {
                    int id = ys.Find(xs[x.IdAt(i)]);
                    if (id < 0 || !TryField(y, id, out ReadOnlySpan<byte> other) || !ValueEquals(leftMetadata, x.ValueAt(i), rightMetadata, other))
                    {
                        return false;
                    }
                }

                return true;
            }

            case VariantKind.Array:
            {
                VariantNested x = VariantNested.Read(left);
                VariantNested y = VariantNested.Read(right);
                if (x.Count != y.Count)
                {
                    return false;
                }

                for (int i = 0; i < x.Count; i++)
                {
                    if (!ValueEquals(leftMetadata, x.ValueAt(i), rightMetadata, y.ValueAt(i)))
                    {
                        return false;
                    }
                }

                return true;
            }

            case VariantKind.Timestamp:
            case VariantKind.LocalTimestamp:
                return NanosecondsOf(a) == NanosecondsOf(b);
            default:
                return a.Integer == b.Integer && a.Bytes.SequenceEqual(b.Bytes);
        }
    }

    /// <summary>A hash that equal values under <see cref="ValueEquals"/> share.</summary>
    internal static int ValueHash(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value)
    {
        VariantScalar scalar = VariantScalar.Read(value);
        HashCode hash = default;
        if (IsExact(scalar.Kind))
        {
            (Int128 unscaled, int scale) = Normalized(scalar);
            hash.Add(1);
            hash.Add(unscaled);
            hash.Add(scale);
            return hash.ToHashCode();
        }

        switch (scalar.Kind)
        {
            case VariantKind.Float:
            case VariantKind.Double:
                hash.Add(2);
                hash.Add(scalar.Number);
                return hash.ToHashCode();
            case VariantKind.Object:
            {
                // Order-free: the fields of two equal objects need not lie in the same order of ids.
                VariantNested nested = VariantNested.Read(value);
                VariantDictionary dictionary = VariantDictionary.Read(metadata);
                int sum = 0;
                for (int i = 0; i < nested.Count; i++)
                {
                    HashCode field = default;
                    field.AddBytes(dictionary[nested.IdAt(i)]);
                    field.Add(ValueHash(metadata, nested.ValueAt(i)));
                    sum = unchecked(sum + field.ToHashCode());
                }

                hash.Add(VariantKind.Object);
                hash.Add(sum);
                return hash.ToHashCode();
            }

            case VariantKind.Array:
            {
                VariantNested nested = VariantNested.Read(value);
                hash.Add(VariantKind.Array);
                for (int i = 0; i < nested.Count; i++)
                {
                    hash.Add(ValueHash(metadata, nested.ValueAt(i)));
                }

                return hash.ToHashCode();
            }

            case VariantKind.Timestamp:
            case VariantKind.LocalTimestamp:
                hash.Add(scalar.Kind);
                hash.Add(scalar.Microseconds);
                return hash.ToHashCode();
            default:
                hash.Add(scalar.Kind);
                hash.Add(scalar.Integer);
                hash.AddBytes(scalar.Bytes);
                return hash.ToHashCode();
        }
    }

    /// <summary>A timestamp as nanoseconds, whichever unit it counts: the one measure two units compare in.</summary>
    private static Int128 NanosecondsOf(VariantScalar scalar) => scalar.Nanoseconds ? scalar.Integer : scalar.Integer * 1000;

    /// <summary>Whether a kind is of the exact numerics, the integers of every width and the decimals.</summary>
    internal static bool IsExact(VariantKind kind) =>
        kind is VariantKind.Int8 or VariantKind.Int16 or VariantKind.Int32 or VariantKind.Int64 or VariantKind.Decimal;

    /// <summary>An exact number as its unscaled value and scale, trailing zeros taken off.</summary>
    private static (Int128 Unscaled, int Scale) Normalized(VariantScalar scalar)
    {
        Int128 unscaled = scalar.Integer;
        int scale = scalar.Kind == VariantKind.Decimal ? scalar.Scale : 0;
        while (scale > 0 && unscaled % 10 == 0)
        {
            unscaled /= 10;
            scale--;
        }

        return (unscaled, scale);
    }

    /// <summary>
    /// Writes <paramref name="value"/> as JSON: an object's keys in their order, a decimal as the
    /// number it is, a date, a time, a timestamp and a uuid as their ISO text, a binary as base64.
    /// </summary>
    internal static void WriteJson(Utf8JsonWriter json, ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value)
    {
        VariantScalar scalar = VariantScalar.Read(value);
        switch (scalar.Kind)
        {
            case VariantKind.Null:
                json.WriteNullValue();
                return;
            case VariantKind.Bool:
                json.WriteBooleanValue(scalar.Integer != 0);
                return;
            case VariantKind.Int8:
            case VariantKind.Int16:
            case VariantKind.Int32:
            case VariantKind.Int64:
                json.WriteNumberValue((long)scalar.Integer);
                return;
            case VariantKind.Float:
            case VariantKind.Double:
                if (double.IsFinite(scalar.Number))
                {
                    json.WriteNumberValue(scalar.Number);
                }
                else
                {
                    json.WriteStringValue(scalar.Number.ToString(CultureInfo.InvariantCulture));
                }

                return;
            case VariantKind.Decimal:
                json.WriteRawValue(DecimalText(scalar.Integer, scalar.Scale), skipInputValidation: true);
                return;
            case VariantKind.String:
                json.WriteStringValue(scalar.Bytes);
                return;
            case VariantKind.Binary:
                json.WriteBase64StringValue(scalar.Bytes);
                return;
            case VariantKind.Date:
                json.WriteStringValue(DateOnly.FromDayNumber(EpochDay + (int)scalar.Integer).ToString("O", CultureInfo.InvariantCulture));
                return;
            case VariantKind.Time:
                json.WriteStringValue(TimeOnly.FromTimeSpan(TimeSpan.FromMicroseconds((long)scalar.Integer)).ToString("O", CultureInfo.InvariantCulture));
                return;
            case VariantKind.Timestamp:
                json.WriteStringValue(TimestampText(scalar, utc: true));
                return;
            case VariantKind.LocalTimestamp:
                json.WriteStringValue(TimestampText(scalar, utc: false));
                return;
            case VariantKind.Uuid:
                json.WriteStringValue(new Guid(scalar.Bytes, bigEndian: true).ToString("D", CultureInfo.InvariantCulture));
                return;
            case VariantKind.Object:
            {
                VariantNested nested = VariantNested.Read(value);
                VariantDictionary dictionary = VariantDictionary.Read(metadata);
                json.WriteStartObject();
                for (int i = 0; i < nested.Count; i++)
                {
                    json.WritePropertyName(dictionary[nested.IdAt(i)]);
                    WriteJson(json, metadata, nested.ValueAt(i));
                }

                json.WriteEndObject();
                return;
            }

            default:
            {
                VariantNested nested = VariantNested.Read(value);
                json.WriteStartArray();
                for (int i = 0; i < nested.Count; i++)
                {
                    WriteJson(json, metadata, nested.ValueAt(i));
                }

                json.WriteEndArray();
                return;
            }
        }
    }

    /// <summary>The day number of 1970-01-01, which a variant's dates count from.</summary>
    internal const int EpochDay = 719_162;

    /// <summary>A decimal's text: its unscaled digits with the point <paramref name="scale"/> digits from the right.</summary>
    internal static string DecimalText(Int128 unscaled, int scale)
    {
        string digits = Int128.Abs(unscaled).ToString(CultureInfo.InvariantCulture);
        if (scale > 0)
        {
            digits = digits.PadLeft(scale + 1, '0');
            digits = digits[..^scale] + "." + digits[^scale..];
        }

        return unscaled < 0 ? "-" + digits : digits;
    }

    private static string TimestampText(VariantScalar scalar, bool utc)
    {
        long micros = scalar.Microseconds;
        DateTime time = DateTime.UnixEpoch.AddTicks(checked(micros * 10));
        string text = time.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        if (scalar.Nanoseconds)
        {
            long nanos = (long)(scalar.Integer - ((Int128)micros * 1000));
            text += nanos.ToString("000", CultureInfo.InvariantCulture);
        }

        return utc ? text + "Z" : text;
    }

    /// <summary>The variant a JSON document holds: an integer in the narrowest width that holds it, any other number a double.</summary>
    /// <remarks>
    /// Two passes over the text and no tree. The first gathers every key, sorts them into the dictionary and gives each
    /// occurrence its id; the second writes each value where it ends up, a container's members first and its header put in
    /// front of them once their count and sizes are known, an object's values put in its keys' order then. A tree of nodes
    /// took a list a container, an array a key and a scalar, a set of the keys and a copy of each level into the next:
    /// 7.86 KB and 4.08 us for an object of fifteen members in 400 bytes of JSON.
    /// </remarks>
    /// <exception cref="ArgumentException">The text is not one JSON document of UTF-8, or an object repeats a key.</exception>
    internal static (byte[] Metadata, byte[] Value) FromJson(ReadOnlySpan<byte> utf8)
    {
        JsonReaderOptions options = new() { CommentHandling = JsonCommentHandling.Skip };
        JsonWriting writing = new(utf8.Length);
        try
        {
            Utf8JsonReader reader = new(utf8, options);
            byte[] metadata = writing.Dictionary(ref reader);
            reader = new Utf8JsonReader(utf8, options);
            _ = reader.Read();
            writing.Value(ref reader);
            return (metadata, writing.Written.ToArray());
        }
        finally
        {
            writing.Return();
        }
    }

    /// <summary>The scratch of a JSON document's two passes, rented and given back whole.</summary>
    private struct JsonWriting
    {
        /// <summary>The value's bytes as far as they are written.</summary>
        private byte[] _value;
        private int _length;

        /// <summary>Each key's bytes, in the order the text names them, then each occurrence's place and id.</summary>
        private byte[] _keys;
        private Occurrence[] _occurrences;
        private int[] _ids;

        /// <summary>The occurrence the second pass meets next.</summary>
        private int _next;

        /// <summary>The members of the containers open, the innermost last.</summary>
        private Member[] _members;
        private int _open;

        /// <summary>An object's values while they are put in its keys' order.</summary>
        private byte[] _moved;

        /// <summary>The dictionary, which names a key an object repeats.</summary>
        private byte[]? _metadata;

        internal JsonWriting(int textBytes)
        {
            _value = ArrayPool<byte>.Shared.Rent(Math.Max(256, textBytes + 64));
            _keys = ArrayPool<byte>.Shared.Rent(Math.Max(16, textBytes));
            _occurrences = ArrayPool<Occurrence>.Shared.Rent(32);
            _ids = [];
            _members = ArrayPool<Member>.Shared.Rent(32);
            _moved = [];
        }

        internal readonly ReadOnlySpan<byte> Written => _value.AsSpan(0, _length);

        /// <summary>
        /// The first pass: the text checked to be one document, its keys gathered, sorted and made unique, each
        /// occurrence given its key's id. The metadata of the dictionary.
        /// </summary>
        internal byte[] Dictionary(ref Utf8JsonReader reader)
        {
            int count = 0;
            int bytes = 0;
            try
            {
                if (!reader.Read())
                {
                    throw new ArgumentException("The JSON text holds no value.", "utf8");
                }

                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            if (count == _occurrences.Length)
                            {
                                Grow(ref _occurrences, count, count * 2);
                            }

                            int length = Unescaped(ref reader, _keys.AsSpan(bytes));
                            _occurrences[count] = new Occurrence(bytes, length, count);
                            bytes += length;
                            count++;
                        }
                        else if (reader.CurrentDepth == 0)
                        {
                            // The root's end.
                            break;
                        }
                    }
                }

                if (reader.Read())
                {
                    throw new ArgumentException("The JSON text holds more than one value.", "utf8");
                }
            }
            catch (JsonException malformed)
            {
                throw new ArgumentException($"The text is not JSON: {malformed.Message}", "utf8", malformed);
            }

            if (count == 0)
            {
                return _metadata = EmptyMetadata;
            }

            Span<Occurrence> sorted = _occurrences.AsSpan(0, count);
            if (count <= 32)
            {
                // In place, as the runtime's sort does below 16: its comparer, a struct, would be boxed.
                for (int i = 1; i < count; i++)
                {
                    Occurrence moved = sorted[i];
                    int j = i - 1;
                    for (; j >= 0 && Key(sorted[j]).SequenceCompareTo(Key(moved)) > 0; j--)
                    {
                        sorted[j + 1] = sorted[j];
                    }

                    sorted[j + 1] = moved;
                }
            }
            else
            {
                sorted.Sort(new KeyBytes(_keys));
            }

            _ids = ArrayPool<int>.Shared.Rent(count);
            int unique = 0;
            int uniqueBytes = 0;
            for (int i = 0; i < count; i++)
            {
                if (i == 0 || !Key(sorted[i]).SequenceEqual(Key(sorted[i - 1])))
                {
                    unique++;
                    uniqueBytes += sorted[i].Length;
                }

                _ids[sorted[i].Index] = unique - 1;
            }

            int width = ParquetVariant.WidthOf((uint)Math.Max(uniqueBytes, unique));
            byte[] metadata = new byte[1 + width + ((unique + 1) * width) + uniqueBytes];

            // Version 1, the strings sorted and unique, the offsets' width.
            metadata[0] = (byte)(0x01 | 0x10 | ((width - 1) << 6));
            int at = 1;
            ParquetVariant.Put(metadata, ref at, (uint)unique, width);
            int strings = at + ((unique + 1) * width);
            int offset = 0;
            for (int i = 0; i < count; i++)
            {
                if (i == 0 || _ids[sorted[i].Index] != _ids[sorted[i - 1].Index])
                {
                    ParquetVariant.Put(metadata, ref at, (uint)offset, width);
                    Key(sorted[i]).CopyTo(metadata.AsSpan(strings + offset));
                    offset += sorted[i].Length;
                }
            }

            ParquetVariant.Put(metadata, ref at, (uint)offset, width);
            return _metadata = metadata;
        }

        /// <summary>The second pass: the value at <paramref name="reader"/>'s token written, and every value it holds.</summary>
        internal void Value(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    Object(ref reader);
                    break;
                case JsonTokenType.StartArray:
                    Array(ref reader);
                    break;
                case JsonTokenType.String:
                    String(ref reader);
                    break;
                case JsonTokenType.Number:
                    Number(ref reader);
                    break;
                default:
                    Reserve(1);
                    _value[_length++] = reader.TokenType switch
                    {
                        JsonTokenType.True => ParquetVariant.Primitive(1),
                        JsonTokenType.False => ParquetVariant.Primitive(2),
                        _ => NullValue[0],
                    };
                    break;
            }
        }

        internal void Return()
        {
            ArrayPool<byte>.Shared.Return(_value);
            ArrayPool<byte>.Shared.Return(_keys);
            ArrayPool<Occurrence>.Shared.Return(_occurrences);
            ArrayPool<Member>.Shared.Return(_members);
            if (_ids.Length > 0)
            {
                ArrayPool<int>.Shared.Return(_ids);
            }

            if (_moved.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_moved);
            }
        }

        private readonly ReadOnlySpan<byte> Key(Occurrence occurrence) => _keys.AsSpan(occurrence.Start, occurrence.Length);

        /// <summary>An object: its values written in the text's order, then moved behind its header in its keys' order.</summary>
        private void Object(ref Utf8JsonReader reader)
        {
            int start = _length;
            int first = _open;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                int id = _ids[_next++];
                _ = reader.Read();
                int at = _length;
                Value(ref reader);
                Open(new Member(id, at, _length - at));
            }

            int count = _open - first;
            Span<Member> members = _members.AsSpan(first, count);
            bool ordered = true;
            int maxId = 0;
            for (int i = 0; i < count; i++)
            {
                maxId = Math.Max(maxId, members[i].Id);
                ordered &= i == 0 || members[i].Id > members[i - 1].Id;
            }

            if (!ordered)
            {
                members.Sort(static (a, b) => a.Id.CompareTo(b.Id));
                for (int i = 1; i < count; i++)
                {
                    if (members[i].Id == members[i - 1].Id)
                    {
                        throw new ArgumentException(
                            $"A JSON object repeats the key '{Encoding.UTF8.GetString(VariantDictionary.Read(_metadata)[members[i].Id])}'.");
                    }
                }
            }

            int size = _length - start;
            int countWidth = count > 0xFF ? 4 : 1;
            int idWidth = ParquetVariant.WidthOf((uint)maxId);
            int offsetWidth = ParquetVariant.WidthOf((uint)size);
            int header = checked(1 + countWidth + (count * idWidth) + ((count + 1) * offsetWidth));
            Reserve(header);
            Span<byte> value = _value;
            if (ordered)
            {
                value.Slice(start, size).CopyTo(value[(start + header)..]);
            }
            else
            {
                if (_moved.Length < size)
                {
                    if (_moved.Length > 0)
                    {
                        ArrayPool<byte>.Shared.Return(_moved);
                    }

                    _moved = ArrayPool<byte>.Shared.Rent(size);
                }

                value.Slice(start, size).CopyTo(_moved);
                int to = start + header;
                foreach (Member member in members)
                {
                    _moved.AsSpan(member.Start - start, member.Length).CopyTo(value[to..]);
                    to += member.Length;
                }
            }

            value[start] = (byte)(((((countWidth == 4 ? 1 : 0) << 4) | ((idWidth - 1) << 2) | (offsetWidth - 1)) << 2) | ParquetVariant.BasicObject);
            int put = start + 1;
            ParquetVariant.Put(value, ref put, (uint)count, countWidth);
            foreach (Member member in members)
            {
                ParquetVariant.Put(value, ref put, (uint)member.Id, idWidth);
            }

            int offset = 0;
            foreach (Member member in members)
            {
                ParquetVariant.Put(value, ref put, (uint)offset, offsetWidth);
                offset += member.Length;
            }

            ParquetVariant.Put(value, ref put, (uint)offset, offsetWidth);
            _length += header;
            _open = first;
        }

        /// <summary>An array: its elements written in order, then moved behind its header.</summary>
        private void Array(ref Utf8JsonReader reader)
        {
            int start = _length;
            int first = _open;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                int at = _length;
                Value(ref reader);
                Open(new Member(0, at, _length - at));
            }

            int count = _open - first;
            int size = _length - start;
            int countWidth = count > 0xFF ? 4 : 1;
            int offsetWidth = ParquetVariant.WidthOf((uint)size);
            int header = checked(1 + countWidth + ((count + 1) * offsetWidth));
            Reserve(header);
            Span<byte> value = _value;
            value.Slice(start, size).CopyTo(value[(start + header)..]);
            value[start] = (byte)(((((countWidth == 4 ? 1 : 0) << 2) | (offsetWidth - 1)) << 2) | ParquetVariant.BasicArray);
            int put = start + 1;
            ParquetVariant.Put(value, ref put, (uint)count, countWidth);
            foreach (Member element in _members.AsSpan(first, count))
            {
                ParquetVariant.Put(value, ref put, (uint)(element.Start - start), offsetWidth);
            }

            ParquetVariant.Put(value, ref put, (uint)size, offsetWidth);
            _length += header;
            _open = first;
        }

        /// <summary>A string, in the short form when it fits in the header's six bits.</summary>
        private void String(ref Utf8JsonReader reader)
        {
            // The text's bytes bound the string's: an escape only shortens it.
            int most = reader.ValueSpan.Length;
            int header = most <= ParquetVariant.MaxShortString ? 1 : 5;
            Reserve(header + most);
            int length = Unescaped(ref reader, _value.AsSpan(_length + header, most));
            if (length <= ParquetVariant.MaxShortString)
            {
                if (header != 1)
                {
                    _value.AsSpan(_length + header, length).CopyTo(_value.AsSpan(_length + 1));
                }

                _value[_length] = (byte)((length << 2) | ParquetVariant.BasicShortString);
                _length += 1 + length;
                return;
            }

            _value[_length] = ParquetVariant.Primitive(16);
            BinaryPrimitives.WriteUInt32LittleEndian(_value.AsSpan(_length + 1), (uint)length);
            _length += 5 + length;
        }

        /// <summary>
        /// A number: an integer in the narrowest width that holds it, past 64 bits a decimal of scale 0 while 38 digits
        /// hold it, any other a double.
        /// </summary>
        private void Number(ref Utf8JsonReader reader)
        {
            Reserve(18);
            Span<byte> into = _value.AsSpan(_length);
            if (reader.TryGetInt64(out long integer))
            {
                _length += PutInteger(into, integer);
                return;
            }

            ReadOnlySpan<byte> text = reader.ValueSpan;
            if (text.IndexOfAny(".eE"u8) < 0 && text.Length <= 39
                && Int128.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out Int128 wide)
                && Int128.Abs(wide) < DecimalLimit)
            {
                _length += PutDecimal(into, wide, 0);
                return;
            }

            into[0] = ParquetVariant.Primitive(7);
            BinaryPrimitives.WriteDoubleLittleEndian(into[1..], reader.GetDouble());
            _length += 9;
        }

        /// <summary>Room for <paramref name="bytes"/> more bytes of the value.</summary>
        private void Reserve(int bytes)
        {
            if (_value.Length - _length < bytes)
            {
                Grow(ref _value, _length, Math.Max(_value.Length * 2, _length + bytes));
            }
        }

        private void Open(Member member)
        {
            if (_open == _members.Length)
            {
                Grow(ref _members, _open, _open * 2);
            }

            _members[_open++] = member;
        }

        /// <summary>The string at <paramref name="reader"/> unescaped into <paramref name="into"/>, checked to be UTF-8. Its length.</summary>
        private static int Unescaped(ref Utf8JsonReader reader, Span<byte> into)
        {
            int length;
            if (reader.ValueIsEscaped)
            {
                try
                {
                    length = reader.CopyString(into);
                }
                catch (InvalidOperationException invalid)
                {
                    throw new ArgumentException("The text is not JSON: a string is not UTF-8.", "utf8", invalid);
                }
            }
            else
            {
                reader.ValueSpan.CopyTo(into);
                length = reader.ValueSpan.Length;
            }

            if (!Utf8.IsValid(into[..length]))
            {
                throw new ArgumentException("The text is not JSON: a string is not UTF-8.", "utf8");
            }

            return length;
        }

        private static void Grow<T>(ref T[] array, int kept, int size)
        {
            T[] grown = ArrayPool<T>.Shared.Rent(size);
            array.AsSpan(0, kept).CopyTo(grown);
            ArrayPool<T>.Shared.Return(array);
            array = grown;
        }
    }

    /// <summary>A key as the text names it: its bytes' place among the keys gathered, and which occurrence it is.</summary>
    private readonly record struct Occurrence(int Start, int Length, int Index);

    /// <summary>A member of a container being written: its key's id, and where its value's bytes lie.</summary>
    private readonly record struct Member(int Id, int Start, int Length);

    /// <summary>Keys in the standard's order, their bytes in the keys gathered.</summary>
    private readonly struct KeyBytes(byte[] keys) : IComparer<Occurrence>
    {
        public int Compare(Occurrence x, Occurrence y) =>
            keys.AsSpan(x.Start, x.Length).SequenceCompareTo(keys.AsSpan(y.Start, y.Length));
    }

    /// <summary>10^38, the first unscaled value past the 38 digits a decimal holds.</summary>
    private static readonly Int128 DecimalLimit = Int128.Parse("100000000000000000000000000000000000000", CultureInfo.InvariantCulture);

    /// <summary>A value as JSON text.</summary>
    internal static string Json(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            WriteJson(json, metadata, value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
