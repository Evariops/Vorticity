using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

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
        Span<byte> payload = stackalloc byte[8];
        if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            payload[0] = (byte)(sbyte)value;
            return Primitive(3, payload[..1]);
        }

        if (value is >= short.MinValue and <= short.MaxValue)
        {
            BinaryPrimitives.WriteInt16LittleEndian(payload, (short)value);
            return Primitive(4, payload[..2]);
        }

        if (value is >= int.MinValue and <= int.MaxValue)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload, (int)value);
            return Primitive(5, payload[..4]);
        }

        BinaryPrimitives.WriteInt64LittleEndian(payload, value);
        return Primitive(6, payload);
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
        Span<byte> payload = stackalloc byte[17];
        payload[0] = scale;
        if (unscaled >= int.MinValue && unscaled <= int.MaxValue)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[1..], (int)unscaled);
            return Primitive(8, payload[..5]);
        }

        if (unscaled >= long.MinValue && unscaled <= long.MaxValue)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload[1..], (long)unscaled);
            return Primitive(9, payload[..9]);
        }

        BinaryPrimitives.WriteInt128LittleEndian(payload[1..], unscaled);
        return Primitive(10, payload);
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
    /// <exception cref="ArgumentException">The text is not one JSON document, or an object repeats a key.</exception>
    internal static (byte[] Metadata, byte[] Value) FromJson(ReadOnlySpan<byte> utf8)
    {
        Utf8JsonReader reader = new(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        JsonNode root;
        try
        {
            if (!reader.Read())
            {
                throw new ArgumentException("The JSON text holds no value.", nameof(utf8));
            }

            root = Parse(ref reader);
            if (reader.Read())
            {
                throw new ArgumentException("The JSON text holds more than one value.", nameof(utf8));
            }
        }
        catch (JsonException malformed)
        {
            throw new ArgumentException($"The text is not JSON: {malformed.Message}", nameof(utf8), malformed);
        }

        SortedSet<byte[]> names = new(KeyOrder);
        root.Collect(names);
        List<byte[]> keys = [.. names];
        return (Metadata(keys), root.Write(keys));
    }

    private static JsonNode Parse(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                List<(byte[] Key, JsonNode Value)> fields = [];
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    byte[] key = Encoding.UTF8.GetBytes(reader.GetString()!);
                    _ = reader.Read();
                    fields.Add((key, Parse(ref reader)));
                }

                fields.Sort(static (a, b) => KeyOrder.Compare(a.Key, b.Key));
                for (int i = 1; i < fields.Count; i++)
                {
                    if (fields[i - 1].Key.AsSpan().SequenceEqual(fields[i].Key))
                    {
                        throw new ArgumentException($"A JSON object repeats the key '{Encoding.UTF8.GetString(fields[i].Key)}'.");
                    }
                }

                return new JsonNode(null, fields, null);
            }

            case JsonTokenType.StartArray:
            {
                List<JsonNode> elements = [];
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    elements.Add(Parse(ref reader));
                }

                return new JsonNode(null, null, elements);
            }

            case JsonTokenType.String:
                return new JsonNode(String(Encoding.UTF8.GetBytes(reader.GetString()!)), null, null);
            case JsonTokenType.Number:
                return new JsonNode(Number(ref reader), null, null);
            case JsonTokenType.True:
                return new JsonNode(Bool(true), null, null);
            case JsonTokenType.False:
                return new JsonNode(Bool(false), null, null);
            default:
                return new JsonNode(NullValue, null, null);
        }
    }

    private static byte[] Number(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out long integer))
        {
            return Integer(integer);
        }

        // An integer past 64 bits is a decimal of scale 0 while 38 digits hold it.
        ReadOnlySpan<byte> text = reader.ValueSpan;
        if (text.IndexOfAny(".eE"u8) < 0 && text.Length <= 39
            && Int128.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out Int128 wide)
            && Int128.Abs(wide) < DecimalLimit)
        {
            return Decimal(wide, 0);
        }

        return Double(reader.GetDouble());
    }

    /// <summary>10^38, the first unscaled value past the 38 digits a decimal holds.</summary>
    private static readonly Int128 DecimalLimit = Int128.Parse("100000000000000000000000000000000000000", CultureInfo.InvariantCulture);

    /// <summary>A JSON value parsed, before the dictionary its objects' ids index is known.</summary>
    private sealed class JsonNode(byte[]? primitive, List<(byte[] Key, JsonNode Value)>? fields, List<JsonNode>? elements)
    {
        internal void Collect(SortedSet<byte[]> names)
        {
            if (fields is not null)
            {
                foreach ((byte[] key, JsonNode value) in fields)
                {
                    _ = names.Add(key);
                    value.Collect(names);
                }
            }
            else if (elements is not null)
            {
                foreach (JsonNode element in elements)
                {
                    element.Collect(names);
                }
            }
        }

        internal byte[] Write(List<byte[]> keys)
        {
            if (primitive is not null)
            {
                return primitive;
            }

            if (elements is not null)
            {
                List<byte[]> written = new(elements.Count);
                foreach (JsonNode element in elements)
                {
                    written.Add(element.Write(keys));
                }

                return Array(written);
            }

            List<(int Id, byte[] Value)> members = new(fields!.Count);
            foreach ((byte[] key, JsonNode value) in fields)
            {
                members.Add((keys.BinarySearch(key, KeyOrder), value.Write(keys)));
            }

            return Object(members);
        }
    }

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
