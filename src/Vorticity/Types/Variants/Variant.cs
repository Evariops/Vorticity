using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types.Variants;

namespace Vorticity;

/// <summary>What a <see cref="Variant"/> holds: a primitive of one of the encoding's types, an object or an array.</summary>
public enum VariantKind : byte
{
    /// <summary>The variant null, a value in its own right, not a missing row.</summary>
    Null = 0,

    /// <summary>A boolean.</summary>
    Bool,

    /// <summary>A signed 8-bit integer.</summary>
    Int8,

    /// <summary>A signed 16-bit integer.</summary>
    Int16,

    /// <summary>A signed 32-bit integer.</summary>
    Int32,

    /// <summary>A signed 64-bit integer.</summary>
    Int64,

    /// <summary>A 32-bit float.</summary>
    Float,

    /// <summary>A 64-bit float.</summary>
    Double,

    /// <summary>UTF-8 text.</summary>
    String,

    /// <summary>Bytes.</summary>
    Binary,

    /// <summary>A decimal of up to 38 digits.</summary>
    Decimal,

    /// <summary>A day of the calendar.</summary>
    Date,

    /// <summary>An instant: microseconds or nanoseconds since the epoch, in UTC.</summary>
    Timestamp,

    /// <summary>A wall-clock date and time, of no zone: microseconds or nanoseconds since the epoch.</summary>
    LocalTimestamp,

    /// <summary>A time of day, of no zone, in microseconds.</summary>
    Time,

    /// <summary>A UUID.</summary>
    Uuid,

    /// <summary>Keys and their values.</summary>
    Object,

    /// <summary>A list of values.</summary>
    Array,
}

/// <summary>
/// A value of the format's <c>Variant</c> type: null, a primitive, an object or an array, in the
/// Parquet Variant binary encoding, as its metadata, the dictionary of the keys its objects use,
/// and its value.
/// </summary>
/// <remarks>
/// <para>
/// A variant member of a record holds one per row; <see cref="Null"/>, the <c>default</c>, is the
/// variant null, which a nullable member tells from a null row. A variant is built from .NET values
/// (<see cref="From(long)"/> and the implicit conversions), from fields (<see cref="Object(ReadOnlySpan{ValueTuple{string, Variant}})"/>),
/// from elements (<see cref="Array(ReadOnlySpan{Variant})"/>) or from JSON (<see cref="FromJson(string)"/>).
/// </para>
/// <para>
/// A value walks by key and index: <c>v["http"]["route"]</c>, <c>v[3]</c>, each a view of the same
/// bytes. <c>As…</c> gives the value as it is stored, when it is of that type, and null otherwise:
/// <see cref="AsInt64"/> takes an integer of any width and nothing else, so the text <c>"502"</c> is
/// not <c>502</c>. <c>To…</c> converts. Two variants are equal under the encoding's equivalence
/// classes: integers and decimals by their number, floats by theirs, objects by their keys and
/// values.
/// </para>
/// </remarks>
public readonly struct Variant : IEquatable<Variant>
{
    private readonly byte[]? _metadata;
    private readonly byte[]? _value;
    private readonly int _offset;
    private readonly int _length;

    internal Variant(byte[] metadata, byte[] value)
        : this(metadata, value, 0, value.Length)
    {
    }

    private Variant(byte[] metadata, byte[] value, int offset, int length)
    {
        _metadata = metadata;
        _value = value;
        _offset = offset;
        _length = length;
    }

    /// <summary>The variant null.</summary>
    public static Variant Null => default;

    /// <summary>The metadata: the encoding's header and the dictionary of the keys the value's objects use.</summary>
    public ReadOnlyMemory<byte> Metadata => _metadata ?? VariantEncoding.EmptyMetadata;

    /// <summary>The value, in the encoding.</summary>
    public ReadOnlyMemory<byte> Value => _value is null ? VariantEncoding.NullValue : new ReadOnlyMemory<byte>(_value, _offset, _length);

    internal ReadOnlySpan<byte> MetadataSpan => _metadata ?? VariantEncoding.EmptyMetadata;

    internal ReadOnlySpan<byte> ValueSpan => _value is null ? VariantEncoding.NullValue : _value.AsSpan(_offset, _length);

    /// <summary>What the value holds.</summary>
    public VariantKind Kind => VariantScalar.KindOf(ValueSpan);

    /// <summary>Whether the value is the variant null.</summary>
    public bool IsNull => Kind == VariantKind.Null;

    /// <summary>The number of an object's keys or of an array's elements, and 0 for any other value.</summary>
    public int Count => Kind is VariantKind.Object or VariantKind.Array ? VariantNested.Read(ValueSpan).Count : 0;

    /// <summary>The value of <paramref name="key"/> in an object, the whole key, dots included; null when the value is not an object or has no such key.</summary>
    /// <param name="key">The key.</param>
    public Variant? this[string key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            Span<byte> stack = stackalloc byte[256];
            using Scratch<byte> name = new(Encoding.UTF8.GetByteCount(key), stack);
            int written = Encoding.UTF8.GetBytes(key, name.Span);
            // Typed: a bare null would convert to the variant null through the string conversion.
            return VariantEncoding.TryField(MetadataSpan, ValueSpan, name.Span[..written], out ReadOnlySpan<byte> field) ? Within(field) : default(Variant?);
        }
    }

    /// <summary>Element <paramref name="index"/> of an array; null when the value is not an array or has no such element.</summary>
    /// <param name="index">The element's position, from 0.</param>
    public Variant? this[int index] => VariantEncoding.TryElement(ValueSpan, index, out ReadOnlySpan<byte> element) ? Within(element) : default(Variant?);

    /// <summary>Whether the value is an object holding <paramref name="key"/>.</summary>
    /// <param name="key">The key, whole.</param>
    /// <returns>True or false, never null.</returns>
    public bool Has(string key) => this[key].HasValue;

    /// <summary>An object's keys, in the order the encoding keeps them, the unsigned order of their bytes; empty for any other value.</summary>
    /// <returns>The keys.</returns>
    public string[] GetKeys()
    {
        if (Kind != VariantKind.Object)
        {
            return [];
        }

        VariantNested nested = VariantNested.Read(ValueSpan);
        VariantDictionary dictionary = VariantDictionary.Read(MetadataSpan);
        string[] keys = new string[nested.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = Encoding.UTF8.GetString(dictionary[nested.IdAt(i)]);
        }

        return keys;
    }

    /// <summary>The value as a boolean, when it is one.</summary>
    public bool? AsBool() => Read(out VariantScalar s) && s.Kind == VariantKind.Bool ? s.Integer != 0 : null;

    /// <summary>The value as an integer, when it is one, of any width.</summary>
    public long? AsInt64() => Read(out VariantScalar s) && s.IsInteger ? (long)s.Integer : null;

    /// <summary>The value as a double, when it is a float or a double.</summary>
    public double? AsDouble() => Read(out VariantScalar s) && s.Kind is VariantKind.Float or VariantKind.Double ? s.Number : null;

    /// <summary>The value as a decimal, when it is a decimal that <see cref="decimal"/> holds.</summary>
    public decimal? AsDecimal() =>
        Read(out VariantScalar s) && s.Kind == VariantKind.Decimal && ToDecimal(s.Integer, s.Scale, out decimal value) ? value : null;

    /// <summary>The value as text, when it is a string.</summary>
    public string? AsString() => Read(out VariantScalar s) && s.Kind == VariantKind.String ? Encoding.UTF8.GetString(s.Bytes) : null;

    /// <summary>The value's bytes, when it is a binary: a view of the variant's own.</summary>
    public ReadOnlyMemory<byte>? AsBinary()
    {
        if (!Read(out VariantScalar s) || s.Kind != VariantKind.Binary)
        {
            return null;
        }

        // The bytes end the value: four bytes of length, then the bytes, after the header.
        return Value[5..];
    }

    /// <summary>The value as a date, when it is one.</summary>
    public DateOnly? AsDate() => Read(out VariantScalar s) && s.Kind == VariantKind.Date ? DateOnly.FromDayNumber(VariantEncoding.EpochDay + (int)s.Integer) : null;

    /// <summary>The value as an instant, when it is a timestamp in UTC; nanoseconds are cut to the 100 ns of a tick.</summary>
    public DateTimeOffset? AsTimestamp() => Read(out VariantScalar s) && s.Kind == VariantKind.Timestamp ? new DateTimeOffset(Ticks(s), TimeSpan.Zero) : null;

    /// <summary>The value as a wall-clock time, when it is a timestamp of no zone; nanoseconds are cut to the 100 ns of a tick.</summary>
    public DateTime? AsLocalTimestamp() => Read(out VariantScalar s) && s.Kind == VariantKind.LocalTimestamp ? new DateTime(Ticks(s), DateTimeKind.Unspecified) : null;

    /// <summary>The value as a time of day, when it is one.</summary>
    public TimeOnly? AsTime() => Read(out VariantScalar s) && s.Kind == VariantKind.Time ? TimeOnly.FromTimeSpan(TimeSpan.FromMicroseconds((long)s.Integer)) : null;

    /// <summary>The value as a UUID, when it is one.</summary>
    public Guid? AsUuid() => Read(out VariantScalar s) && s.Kind == VariantKind.Uuid ? new Guid(s.Bytes, bigEndian: true) : null;

    /// <summary>
    /// The value converted to an integer: an integer, a float or a decimal with no fraction within
    /// 64 bits, a boolean as 0 or 1, or text that parses as an integer; null when it does not convert.
    /// </summary>
    public long? ToInt64() => Read(out VariantScalar s) && s.TryToInt64(out long value) ? value : null;

    /// <summary>
    /// The value converted to a double: any number, a boolean as 0 or 1, or text that parses as a
    /// number; null when it does not convert.
    /// </summary>
    public double? ToDouble() => Read(out VariantScalar s) && s.TryToDouble(out double value) ? value : null;

    /// <summary>The value converted to a boolean: a boolean, or the text <c>true</c> or <c>false</c> in any case; null otherwise.</summary>
    public bool? ToBool()
    {
        if (!Read(out VariantScalar s))
        {
            return null;
        }

        if (s.Kind == VariantKind.Bool)
        {
            return s.Integer != 0;
        }

        return s.Kind == VariantKind.String && bool.TryParse(Encoding.UTF8.GetString(s.Bytes), out bool parsed) ? parsed : null;
    }

    /// <summary>The value as JSON: an object's keys in their order, a date, a time, a timestamp and a UUID as ISO text, a binary as base64.</summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => VariantEncoding.Json(MetadataSpan, ValueSpan);

    /// <summary>The value as JSON.</summary>
    /// <returns>The JSON text.</returns>
    public override string ToString() => ToJson();

    /// <summary>Whether two values are equal under the encoding's equivalence classes.</summary>
    /// <param name="other">The other value.</param>
    /// <returns>True when equal.</returns>
    public bool Equals(Variant other) => VariantEncoding.ValueEquals(MetadataSpan, ValueSpan, other.MetadataSpan, other.ValueSpan);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Variant other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => VariantEncoding.ValueHash(MetadataSpan, ValueSpan);

    /// <summary>Equality under the encoding's equivalence classes.</summary>
    public static bool operator ==(Variant left, Variant right) => left.Equals(right);

    /// <summary>Inequality under the encoding's equivalence classes.</summary>
    public static bool operator !=(Variant left, Variant right) => !left.Equals(right);

    /// <summary>A boolean.</summary>
    public static implicit operator Variant(bool value) => From(value);

    /// <summary>An integer, in the narrowest width that holds it.</summary>
    public static implicit operator Variant(long value) => From(value);

    /// <summary>A double.</summary>
    public static implicit operator Variant(double value) => From(value);

    /// <summary>A string; null is the variant null.</summary>
    public static implicit operator Variant(string? value) => value is null ? Null : From(value);

    /// <summary>A boolean.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(bool value) => Primitive(VariantEncoding.Bool(value));

    /// <summary>An integer, in the narrowest of the encoding's four widths that holds it.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(long value) => Primitive(VariantEncoding.Integer(value));

    /// <summary>A double.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(double value) => Primitive(VariantEncoding.Double(value));

    /// <summary>A float.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(float value) => Primitive(VariantEncoding.Float(value));

    /// <summary>A decimal, of the value's own scale.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        Int128 unscaled = ((Int128)(uint)bits[2] << 64) | ((Int128)(uint)bits[1] << 32) | (uint)bits[0];
        return Primitive(VariantEncoding.Decimal(bits[3] < 0 ? -unscaled : unscaled, (byte)((bits[3] >> 16) & 0xFF)));
    }

    /// <summary>Text.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Primitive(VariantEncoding.String(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>Bytes, copied.</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The variant.</returns>
    public static Variant From(ReadOnlySpan<byte> value) => Primitive(VariantEncoding.Binary(value));

    /// <summary>A date.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(DateOnly value) => Primitive(VariantEncoding.Date(value.DayNumber - VariantEncoding.EpochDay));

    /// <summary>An instant, in UTC microseconds; ticks finer than a microsecond are dropped.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(DateTimeOffset value) => Primitive(VariantEncoding.Timestamp(Micros(value.UtcTicks), utc: true));

    /// <summary>A UTC <see cref="DateTime"/> as an instant, any other as a wall-clock time of no zone, in microseconds.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(DateTime value) => Primitive(VariantEncoding.Timestamp(Micros(value.Ticks), utc: value.Kind == DateTimeKind.Utc));

    /// <summary>A time of day, in microseconds.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(TimeOnly value) => Primitive(VariantEncoding.Time(value.Ticks / TimeSpan.TicksPerMicrosecond));

    /// <summary>A UUID.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The variant.</returns>
    public static Variant From(Guid value) => Primitive(VariantEncoding.Uuid(value));

    /// <summary>
    /// The variant <paramref name="metadata"/> and <paramref name="value"/> encode, copied after
    /// both are checked: the metadata's dictionary, and the value's size and every id it names.
    /// </summary>
    /// <param name="metadata">The metadata bytes.</param>
    /// <param name="value">The value bytes, exactly one value.</param>
    /// <returns>The variant.</returns>
    /// <exception cref="VortexFormatException">The bytes are not one value of the encoding.</exception>
    public static Variant FromBytes(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> value)
    {
        int size = ParquetVariant.SizeOf(value);
        if (size != value.Length)
        {
            throw new VortexFormatException($"A variant value of {size} bytes is followed by {value.Length - size} more.");
        }

        Check(VariantDictionary.Read(metadata), value, depth: 1);
        return new Variant(metadata.ToArray(), value.ToArray());
    }

    /// <summary>An object of <paramref name="fields"/>, each key whole.</summary>
    /// <param name="fields">The keys and their values; a key may not repeat.</param>
    /// <returns>The variant.</returns>
    /// <exception cref="ArgumentException">A key repeats, or is null.</exception>
    public static Variant Object(params ReadOnlySpan<(string Key, Variant Value)> fields)
    {
        List<(byte[] Key, Variant Value)> named = new(fields.Length);
        foreach ((string key, Variant value) in fields)
        {
            named.Add((Encoding.UTF8.GetBytes(key ?? throw new ArgumentException("A variant's key is not null.", nameof(fields))), value));
        }

        return ObjectOf(named);
    }

    /// <summary>An object of <paramref name="fields"/>, each key whole.</summary>
    /// <param name="fields">The keys and their values; a key may not repeat.</param>
    /// <returns>The variant.</returns>
    /// <exception cref="ArgumentException">A key repeats.</exception>
    public static Variant Object(IEnumerable<KeyValuePair<string, Variant>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        List<(byte[] Key, Variant Value)> named = [];
        foreach (KeyValuePair<string, Variant> field in fields)
        {
            named.Add((Encoding.UTF8.GetBytes(field.Key), field.Value));
        }

        return ObjectOf(named);
    }

    /// <summary>An array of <paramref name="elements"/>.</summary>
    /// <param name="elements">The elements.</param>
    /// <returns>The variant.</returns>
    public static Variant Array(params ReadOnlySpan<Variant> elements)
    {
        SortedSet<byte[]> names = new(VariantEncoding.KeyOrder);
        foreach (Variant element in elements)
        {
            VariantEncoding.CollectKeys(element.MetadataSpan, element.ValueSpan, names);
        }

        List<byte[]> keys = [.. names];
        List<byte[]> values = new(elements.Length);
        foreach (Variant element in elements)
        {
            values.Add(VariantEncoding.Rewrite(element.MetadataSpan, element.ValueSpan, keys));
        }

        return new Variant(VariantEncoding.Metadata(keys), VariantEncoding.Array(values));
    }

    /// <summary>An array of <paramref name="elements"/>.</summary>
    /// <param name="elements">The elements.</param>
    /// <returns>The variant.</returns>
    public static Variant Array(IEnumerable<Variant> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        Variant[] all = [.. elements];
        return Array(all.AsSpan());
    }

    /// <summary>
    /// The value a JSON document holds: an object's keys whole, an integer in the narrowest width
    /// that holds it, an integer past 64 bits a decimal while 38 digits hold it, any other number a
    /// double.
    /// </summary>
    /// <param name="json">One JSON document.</param>
    /// <returns>The variant.</returns>
    /// <exception cref="ArgumentException">The text is not one JSON document, or an object repeats a key.</exception>
    public static Variant FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return FromJson(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>The value a JSON document in UTF-8 holds, as <see cref="FromJson(string)"/> reads it.</summary>
    /// <param name="utf8Json">One JSON document, in UTF-8.</param>
    /// <returns>The variant.</returns>
    /// <exception cref="ArgumentException">The text is not one JSON document, or an object repeats a key.</exception>
    public static Variant FromJson(ReadOnlySpan<byte> utf8Json)
    {
        (byte[] metadata, byte[] value) = VariantEncoding.FromJson(utf8Json);
        return new Variant(metadata, value);
    }

    private static Variant ObjectOf(List<(byte[] Key, Variant Value)> fields)
    {
        fields.Sort(static (a, b) => VariantEncoding.KeyOrder.Compare(a.Key, b.Key));
        bool scalars = true;
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0 && fields[i - 1].Key.AsSpan().SequenceEqual(fields[i].Key))
            {
                throw new ArgumentException($"A variant object repeats the key '{Encoding.UTF8.GetString(fields[i].Key)}'.", nameof(fields));
            }

            scalars &= VariantScalar.KindOf(fields[i].Value.ValueSpan) is not (VariantKind.Object or VariantKind.Array);
        }

        // An object of scalars, as a record's attributes are: its keys are its fields', sorted, and each value's bytes stand
        // as they are, where the keys of every value were gathered and each value rewritten against them.
        if (scalars)
        {
            List<byte[]> own = new(fields.Count);
            List<(int Id, byte[] Value)> values = new(fields.Count);
            for (int i = 0; i < fields.Count; i++)
            {
                own.Add(fields[i].Key);
                values.Add((i, fields[i].Value.ScalarBytes()));
            }

            return new Variant(VariantEncoding.Metadata(own), VariantEncoding.Object(values));
        }

        SortedSet<byte[]> names = new(VariantEncoding.KeyOrder);
        for (int i = 0; i < fields.Count; i++)
        {
            _ = names.Add(fields[i].Key);
            VariantEncoding.CollectKeys(fields[i].Value.MetadataSpan, fields[i].Value.ValueSpan, names);
        }

        List<byte[]> keys = [.. names];
        List<(int Id, byte[] Value)> members = new(fields.Count);
        foreach ((byte[] key, Variant value) in fields)
        {
            members.Add((keys.BinarySearch(key, VariantEncoding.KeyOrder), VariantEncoding.Rewrite(value.MetadataSpan, value.ValueSpan, keys)));
        }

        return new Variant(VariantEncoding.Metadata(keys), VariantEncoding.Object(members));
    }

    private static Variant Primitive(byte[] value) => new(VariantEncoding.EmptyMetadata, value);

    /// <summary>The bytes of this value, a scalar, as many as its encoding takes: its own array when it is the whole of it.</summary>
    private byte[] ScalarBytes()
    {
        ReadOnlySpan<byte> value = ValueSpan;
        int size = ParquetVariant.SizeOf(value);
        return _value is not null && _offset == 0 && size == _value.Length ? _value : value[..size].ToArray();
    }

    /// <summary>The value <paramref name="slice"/> of this one's bytes, sharing them.</summary>
    private Variant Within(ReadOnlySpan<byte> slice)
    {
        ReadOnlySpan<byte> whole = ValueSpan;
        int offset = _offset + (int)System.Runtime.CompilerServices.Unsafe.ByteOffset(
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(whole),
            ref System.Runtime.InteropServices.MemoryMarshal.GetReference(slice));
        return new Variant(_metadata!, _value!, offset, slice.Length);
    }

    private bool Read(out VariantScalar scalar)
    {
        scalar = VariantScalar.Read(ValueSpan);
        return true;
    }

    /// <summary>A timestamp's count of ticks since year 1, its unit's finer digits cut.</summary>
    private static long Ticks(VariantScalar s)
    {
        Int128 ticks = s.Nanoseconds ? s.Integer / 100 : s.Integer * 10;
        return checked((long)(ticks + DateTime.UnixEpoch.Ticks));
    }

    private static long Micros(long ticks) => (ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;

    private static bool ToDecimal(Int128 unscaled, byte scale, out decimal value)
    {
        value = default;
        if (scale > 28 || Int128.Abs(unscaled) > (Int128)ulong.MaxValue * uint.MaxValue)
        {
            return decimal.TryParse(VariantEncoding.DecimalText(unscaled, scale), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        UInt128 magnitude = (UInt128)Int128.Abs(unscaled);
        value = new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64), unscaled < 0, scale);
        return true;
    }

    /// <summary>Checks that every object of <paramref name="value"/> names ids its dictionary holds, and that its values fit.</summary>
    private static void Check(VariantDictionary dictionary, ReadOnlySpan<byte> value, int depth)
    {
        VortexLimits.CheckDepth(depth, VortexLimits.MaxArrayDepth, "Variant");
        VariantKind kind = VariantScalar.KindOf(value);
        if (kind is not (VariantKind.Object or VariantKind.Array))
        {
            _ = VariantScalar.Read(value);
            return;
        }

        VariantNested nested = VariantNested.Read(value);
        for (int i = 0; i < nested.Count; i++)
        {
            if (kind == VariantKind.Object)
            {
                _ = dictionary[nested.IdAt(i)];
            }

            Check(dictionary, nested.ValueAt(i), depth + 1);
        }
    }
}
