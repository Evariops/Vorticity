using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using Vorticity.Types;

namespace Vorticity;

/// <summary>What a <see cref="VortexType"/> is, before its parameters.</summary>
public enum VortexTypeKind : byte
{
    /// <summary>Every value is null.</summary>
    Null,

    /// <summary>A boolean.</summary>
    Bool,

    /// <summary>A fixed-width integer or float; which one is the type's identity (<see cref="VortexType.Int32"/>...).</summary>
    Primitive,

    /// <summary>A fixed-point decimal of <see cref="VortexType.Precision"/> digits and <see cref="VortexType.Scale"/>.</summary>
    Decimal,

    /// <summary>UTF-8 text.</summary>
    Utf8,

    /// <summary>Bytes.</summary>
    Binary,

    /// <summary>Named fields, <see cref="VortexType.Fields"/>.</summary>
    Struct,

    /// <summary>A variable-length list of <see cref="VortexType.ElementType"/>.</summary>
    List,

    /// <summary>A list of exactly <see cref="VortexType.FixedSize"/> elements.</summary>
    FixedSizeList,

    /// <summary>An extension over <see cref="VortexType.StorageType"/>: dates, times, timestamps, uuids, or a registered type.</summary>
    Extension,

    /// <summary>A map from keys to values, as a file may declare it; read only.</summary>
    Map,

    /// <summary>A tagged union, as a file may declare it; read only.</summary>
    Union,

    /// <summary>A self-describing value, as a file may declare it; read only.</summary>
    Variant,
}

/// <summary>The unit of a time or a timestamp.</summary>
public enum TimeUnit : byte
{
    /// <summary>10⁻⁹ s.</summary>
    Nanoseconds,

    /// <summary>10⁻⁶ s.</summary>
    Microseconds,

    /// <summary>10⁻³ s.</summary>
    Milliseconds,

    /// <summary>Seconds.</summary>
    Seconds,

    /// <summary>Days, for a date.</summary>
    Days,
}

/// <summary>A named, typed field of a struct or a schema.</summary>
/// <param name="Name">The field's name, which may be empty or hold a <c>.</c>.</param>
/// <param name="Type">The field's type.</param>
public readonly record struct VortexField(string Name, VortexType Type)
{
    /// <summary>Builds a field from a <c>(name, type)</c> tuple.</summary>
    /// <param name="field">The tuple.</param>
    public static implicit operator VortexField((string Name, VortexType Type) field) => new VortexField(field.Name, field.Type);
}

/// <summary>A Vortex logical type, immutable and compared by value.</summary>
/// <remarks>
/// Nullability is part of the type: <c>VortexType.Float64</c> and <c>VortexType.Float64.Nullable</c>
/// are two types. The factories validate their parameters as the format does, so a type that exists
/// is one a file can carry.
/// </remarks>
public sealed class VortexType : IEquatable<VortexType>, ISpanFormattable, IUtf8SpanFormattable
{
    /// <summary>
    /// What the types kept for reuse take of the heap at most, 8 MiB, each weighed at its bytes (<see cref="InternBytes"/>).
    /// Past it, a new type is not kept and compares by structure. A weight of one a type, one a field and one a name's
    /// character let a family of types of one field or none each, fixed-size lists of every size, keep a million of them,
    /// 173 MB for the life of the process. 8 MiB keeps some 1 600 structs of 50 fields named in 15 characters, where that
    /// weight kept 1 300, and 48 000 types without fields.
    /// </summary>
    private const long MaxInternedBytes = 8L << 20;

    /// <summary>
    /// What a type kept takes beside its fields, names and metadata: the type, its node in the table and its slot there.
    /// 100 000 fixed-size lists of new sizes kept 173 bytes each (2026-11-24).
    /// </summary>
    private const int InternedTypeBytes = 176;

    private static readonly InternTable<VortexType> Interned = new InternTable<VortexType>(MaxInternedBytes);

    /// <summary>What the types kept for reuse take of the heap, as <see cref="InternBytes"/> weighs them: for the tests.</summary>
    internal static long InternedBytes => Interned.Weight;

    private readonly VortexField[]? _fields;
    private readonly byte[]? _metadata;
    private readonly int _hash;
    private VortexType? _twin;
    private string? _text;
    private Dictionary<string, int>? _names;

    private VortexType(
        VortexTypeKind kind, bool nullable, PType ptype = default, int precision = 0, int scale = 0,
        VortexType? element = null, int size = 0, VortexField[]? fields = null, string? extensionId = null,
        VortexType? storage = null, byte[]? metadata = null)
    {
        Kind = kind;
        IsNullable = nullable;
        PrimitiveType = ptype;
        Precision = precision;
        Scale = scale;
        ElementType = element;
        FixedSize = size;
        _fields = fields;
        ExtensionId = extensionId;
        StorageType = storage;
        _metadata = metadata;
        _hash = ComputeHash();
    }

    /// <summary>The null type; every value is null, so it is always nullable.</summary>
    public static VortexType Null { get; } = Intern(new VortexType(VortexTypeKind.Null, nullable: true));

    /// <summary>A non-nullable boolean.</summary>
    public static VortexType Bool { get; } = Intern(new VortexType(VortexTypeKind.Bool, nullable: false));

    /// <summary>A non-nullable 8-bit signed integer.</summary>
    public static VortexType Int8 { get; } = Primitive(PType.I8);

    /// <summary>A non-nullable 16-bit signed integer.</summary>
    public static VortexType Int16 { get; } = Primitive(PType.I16);

    /// <summary>A non-nullable 32-bit signed integer.</summary>
    public static VortexType Int32 { get; } = Primitive(PType.I32);

    /// <summary>A non-nullable 64-bit signed integer.</summary>
    public static VortexType Int64 { get; } = Primitive(PType.I64);

    /// <summary>A non-nullable 8-bit unsigned integer.</summary>
    public static VortexType UInt8 { get; } = Primitive(PType.U8);

    /// <summary>A non-nullable 16-bit unsigned integer.</summary>
    public static VortexType UInt16 { get; } = Primitive(PType.U16);

    /// <summary>A non-nullable 32-bit unsigned integer.</summary>
    public static VortexType UInt32 { get; } = Primitive(PType.U32);

    /// <summary>A non-nullable 64-bit unsigned integer.</summary>
    public static VortexType UInt64 { get; } = Primitive(PType.U64);

    /// <summary>A non-nullable half-precision float.</summary>
    public static VortexType Float16 { get; } = Primitive(PType.F16);

    /// <summary>A non-nullable single-precision float.</summary>
    public static VortexType Float32 { get; } = Primitive(PType.F32);

    /// <summary>A non-nullable double-precision float.</summary>
    public static VortexType Float64 { get; } = Primitive(PType.F64);

    /// <summary>Non-nullable UTF-8 text.</summary>
    public static VortexType Utf8 { get; } = Intern(new VortexType(VortexTypeKind.Utf8, nullable: false));

    /// <summary>Non-nullable bytes.</summary>
    public static VortexType Binary { get; } = Intern(new VortexType(VortexTypeKind.Binary, nullable: false));

    /// <summary>A non-nullable date, stored as days since the Unix epoch in an <c>i32</c>.</summary>
    public static VortexType Date { get; } = Intern(
        new VortexType(VortexTypeKind.Extension, nullable: false, extensionId: ExtensionIds.Date, storage: Int32, metadata: [(byte)TimeUnit.Days]));

    /// <summary>A non-nullable UUID, stored as sixteen bytes in network order.</summary>
    public static VortexType Uuid { get; } = Intern(
        new VortexType(VortexTypeKind.Extension, nullable: false, extensionId: ExtensionIds.Uuid, storage: FixedSizeList(UInt8, 16), metadata: []));

    /// <summary>A non-nullable variant: per row, a value of the Parquet Variant encoding, read and written as a <see cref="Vorticity.Variant"/>.</summary>
    public static VortexType Variant { get; } = Intern(new VortexType(VortexTypeKind.Variant, nullable: false));

    /// <summary>What the type is, before its parameters.</summary>
    public VortexTypeKind Kind { get; }

    /// <summary>Whether a value of this type may be null.</summary>
    public bool IsNullable { get; }

    /// <summary>The same type, nullable.</summary>
    public VortexType Nullable => IsNullable ? this : Twin();

    /// <summary>The same type, not nullable. The null type has no non-nullable form and returns itself.</summary>
    public VortexType NonNullable => !IsNullable || Kind == VortexTypeKind.Null ? this : Twin();

    /// <summary>The element type of a list or a fixed-size list; null for any other kind.</summary>
    public VortexType? ElementType { get; }

    /// <summary>The number of elements of a fixed-size list; 0 for any other kind.</summary>
    public int FixedSize { get; }

    /// <summary>The fields of a struct, a map (key, value) or a union; empty for any other kind.</summary>
    public ReadOnlySpan<VortexField> Fields => _fields;

    /// <summary>The index of the first of <see cref="Fields"/> named exactly <paramref name="name"/>, or -1.</summary>
    internal int IndexOfField(ReadOnlySpan<char> name) => _fields is null ? -1 : FieldNames.IndexOf(_fields, ref _names, name);

    /// <summary>The fields, as the array they are held in; never written to.</summary>
    internal VortexField[] FieldArray => _fields ?? [];

    /// <summary>The number of decimal digits of a decimal; 0 for any other kind.</summary>
    public int Precision { get; }

    /// <summary>The decimal scale; 0 for any other kind.</summary>
    public int Scale { get; }

    /// <summary>The id of an extension, such as <c>vortex.timestamp</c>; null for any other kind.</summary>
    public string? ExtensionId { get; }

    /// <summary>The type an extension is stored as; null for any other kind.</summary>
    public VortexType? StorageType { get; }

    /// <summary>The metadata of an extension, which is part of its identity; empty for any other kind.</summary>
    public ReadOnlyMemory<byte> ExtensionMetadata => _metadata;

    /// <summary>The physical type of a primitive.</summary>
    internal PType PrimitiveType { get; }

    /// <summary>A decimal of <paramref name="precision"/> digits, <paramref name="scale"/> of them after the point.</summary>
    /// <param name="precision">1 to 76.</param>
    /// <param name="scale">At most 76, and at most the precision when positive; a negative scale multiplies by a power of ten.</param>
    /// <returns>The non-nullable decimal type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The precision or the scale is outside what the format allows.</exception>
    public static VortexType Decimal(int precision, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, DTypeArena.MinDecimalPrecision);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, DTypeArena.MaxDecimalPrecision);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, DTypeArena.MaxDecimalScale);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, sbyte.MinValue);
        if (scale > 0 && scale > precision)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, $"A positive scale is at most the precision, {precision}.");
        }

        return Intern(new VortexType(VortexTypeKind.Decimal, nullable: false, precision: precision, scale: scale));
    }

    /// <summary>A variable-length list of <paramref name="element"/>.</summary>
    /// <param name="element">The element type; nullable when the elements may be null.</param>
    /// <returns>The non-nullable list type.</returns>
    public static VortexType List(VortexType element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Intern(new VortexType(VortexTypeKind.List, nullable: false, element: element));
    }

    /// <summary>A list of exactly <paramref name="size"/> elements of <paramref name="element"/>.</summary>
    /// <param name="element">The element type.</param>
    /// <param name="size">The number of elements per value; zero is legal.</param>
    /// <returns>The non-nullable fixed-size list type.</returns>
    public static VortexType FixedSizeList(VortexType element, int size)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return Intern(new VortexType(VortexTypeKind.FixedSizeList, nullable: false, element: element, size: size));
    }

    /// <summary>A struct of <paramref name="fields"/>, in order.</summary>
    /// <param name="fields">The fields; names need not be unique, as the format allows.</param>
    /// <returns>The non-nullable struct type.</returns>
    public static VortexType Struct(ReadOnlySpan<VortexField> fields)
    {
        VortexField[] copy = fields.ToArray();
        foreach (VortexField field in copy)
        {
            ArgumentNullException.ThrowIfNull(field.Name, nameof(fields));
            ArgumentNullException.ThrowIfNull(field.Type, nameof(fields));
        }

        return Intern(new VortexType(VortexTypeKind.Struct, nullable: false, fields: copy));
    }

    /// <summary>A time of day in <paramref name="unit"/> since midnight.</summary>
    /// <param name="unit">Seconds or milliseconds (stored as <c>i32</c>), microseconds or nanoseconds (<c>i64</c>).</param>
    /// <returns>The non-nullable time type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unit"/> is <see cref="TimeUnit.Days"/>.</exception>
    public static VortexType Time(TimeUnit unit)
    {
        VortexType storage = unit switch
        {
            TimeUnit.Seconds or TimeUnit.Milliseconds => Int32,
            TimeUnit.Microseconds or TimeUnit.Nanoseconds => Int64,
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "A time is in seconds, milliseconds, microseconds or nanoseconds."),
        };
        return Intern(new VortexType(VortexTypeKind.Extension, nullable: false, extensionId: ExtensionIds.Time, storage: storage, metadata: [(byte)unit]));
    }

    /// <summary>An instant in <paramref name="unit"/> since the Unix epoch, naive or in a zone.</summary>
    /// <param name="unit">The unit, stored as <c>i64</c>.</param>
    /// <param name="timeZone">An IANA zone id such as <c>Europe/Paris</c> or <c>UTC</c>; null for a naive timestamp.</param>
    /// <returns>The non-nullable timestamp type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unit"/> is <see cref="TimeUnit.Days"/> or not a defined unit.</exception>
    public static VortexType Timestamp(TimeUnit unit, string? timeZone = null)
    {
        // Days has no instant, and the reference refuses a timestamp in days as it reads its type: a file written with one
        // would not be read there.
        if (unit is not (TimeUnit.Seconds or TimeUnit.Milliseconds or TimeUnit.Microseconds or TimeUnit.Nanoseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "A timestamp is in seconds, milliseconds, microseconds or nanoseconds.");
        }

        int zoneBytes = timeZone is null ? 0 : Encoding.UTF8.GetByteCount(timeZone);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zoneBytes, ushort.MaxValue, nameof(timeZone));
        byte[] metadata = new byte[3 + zoneBytes];
        metadata[0] = (byte)unit;
        BinaryPrimitives.WriteUInt16LittleEndian(metadata.AsSpan(1), (ushort)zoneBytes);
        if (timeZone is not null)
        {
            Encoding.UTF8.GetBytes(timeZone, metadata.AsSpan(3));
        }

        return Intern(new VortexType(VortexTypeKind.Extension, nullable: false, extensionId: ExtensionIds.Timestamp, storage: Int64, metadata: metadata));
    }

    /// <summary>An extension type: <paramref name="storage"/> read and written as <paramref name="id"/>.</summary>
    /// <param name="id">The extension id, such as <c>acme.money</c>.</param>
    /// <param name="storage">The type the values are stored as.</param>
    /// <param name="metadata">The extension's parameters, part of its identity.</param>
    /// <returns>The extension type; its nullability is its storage's.</returns>
    public static VortexType Extension(string id, VortexType storage, ReadOnlyMemory<byte> metadata = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(storage);
        return Intern(new VortexType(VortexTypeKind.Extension, storage.IsNullable, extensionId: id, storage: storage, metadata: metadata.ToArray()));
    }

    internal static VortexType Primitive(PType ptype) =>
        Intern(new VortexType(VortexTypeKind.Primitive, nullable: false, ptype: ptype));

    /// <summary>A non-nullable map from <paramref name="key"/> to <paramref name="value"/>: a list of entries per row, each a key and its value.</summary>
    /// <param name="key">The keys' type, which a map holds no null of.</param>
    /// <param name="value">The values' type.</param>
    /// <returns>The map type.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is nullable.</exception>
    public static VortexType Map(VortexType key, VortexType value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.IsNullable)
        {
            throw new ArgumentException($"A map's keys are never null; {key} is nullable.", nameof(key));
        }

        return Map(key, value, nullable: false);
    }

    internal static VortexType Map(VortexType key, VortexType value, bool nullable) =>
        Intern(new VortexType(VortexTypeKind.Map, nullable, fields: [new VortexField("key", key), new VortexField("value", value)]));

    internal static VortexType Union(VortexField[] variants, bool nullable) =>
        Intern(new VortexType(VortexTypeKind.Union, nullable, fields: variants));

    internal static VortexType WithNullability(VortexType type, bool nullable) =>
        nullable ? type.Nullable : type.NonNullable;

    /// <summary>The unit of a time, a timestamp or a date; null for any other type.</summary>
    internal TimeUnit? Unit =>
        ExtensionId is ExtensionIds.Date or ExtensionIds.Time or ExtensionIds.Timestamp && _metadata is { Length: > 0 } m
            ? (TimeUnit)m[0]
            : null;

    /// <summary>The zone of a timestamp, resolved once on this host; UTC for a naive one.</summary>
    internal TimeZoneInfo ZoneInfo
    {
        get
        {
            if (Volatile.Read(ref _zoneState) == 0)
            {
                ReadZone();
            }

            return _zone!;
        }
    }

    /// <summary>
    /// What a timestamp's zone is on this host, read once: <see cref="ZoneState.Naive"/> for a naive timestamp and any
    /// other type. What a check of a column asks at every batch, where the zone decoded was a string a batch.
    /// </summary>
    internal ZoneState Zone
    {
        get
        {
            byte state = Volatile.Read(ref _zoneState);
            if (state == 0)
            {
                ReadZone();
                state = _zoneState;
            }

            return (ZoneState)state;
        }
    }

    /// <summary>Reads the zone and what it is once; two threads that read it at once read the same.</summary>
    private void ReadZone()
    {
        TimeZoneInfo zone = TimeZoneInfo.Utc;
        ZoneState state = ZoneState.Naive;
        if (TimeZone is { } id)
        {
            if (TimeZones.TryResolve(id, out TimeZoneInfo found))
            {
                zone = found;
                state = TimeZones.IsUtc(id) ? ZoneState.Utc : ZoneState.Resolved;
            }
            else
            {
                state = ZoneState.Unresolved;
            }
        }

        // The zone before its state: a reader that sees the state finds the zone.
        _zone = zone;
        Volatile.Write(ref _zoneState, (byte)state);
    }

    private TimeZoneInfo? _zone;
    private byte _zoneState;

    /// <summary>The zone of a timestamp, or null for a naive one and any other type.</summary>
    /// <remarks>Decoded at each call: a test of whether there is one is <see cref="IsZoned"/>.</remarks>
    internal string? TimeZone
    {
        get
        {
            int length = ZoneLength();
            return length == 0 ? null : Encoding.UTF8.GetString(_metadata!, 3, length);
        }
    }

    /// <summary>
    /// Whether <see cref="TimeZone"/> is not null, read without decoding it: a conversion asks it of
    /// every row it turns into a <see cref="DateTime"/>, where the zone decoded was 32 bytes a row of
    /// a UTC timestamp.
    /// </summary>
    internal bool IsZoned => ZoneLength() > 0;

    /// <summary>The length of a timestamp's zone in its metadata; 0 for none, a naive one and any other type.</summary>
    private int ZoneLength()
    {
        if (ExtensionId != ExtensionIds.Timestamp || _metadata is not { Length: >= 3 } m)
        {
            return 0;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(1));
        return 3 + length > m.Length ? 0 : length;
    }

    private VortexType Twin()
    {
        VortexType? twin = _twin;
        if (twin is not null)
        {
            return twin;
        }

        bool nullable = !IsNullable;
        twin = Kind == VortexTypeKind.Extension
            ? Intern(new VortexType(Kind, nullable, extensionId: ExtensionId, storage: WithNullability(StorageType!, nullable), metadata: _metadata))
            : Intern(new VortexType(Kind, nullable, PrimitiveType, Precision, Scale, ElementType, FixedSize, _fields, ExtensionId, StorageType, _metadata));
        _twin = twin;
        return twin;
    }

    private static VortexType Intern(VortexType type) => Interned.Intern(type, type.InternBytes());

    /// <summary>
    /// What keeping this type takes of the heap: <see cref="InternedTypeBytes"/>, an extension's id and metadata, and for
    /// each field its place in the type's array, its name, and its entry in the index of names a lookup builds. A struct
    /// of 1 000 fields kept 16 400 bytes past the names it was given (2026-11-24).
    /// </summary>
    private long InternBytes()
    {
        long bytes = InternedTypeBytes + StringBytes(ExtensionId) + (_metadata is null ? 0 : ArrayHeaderBytes + _metadata.Length);
        if (_fields is not null)
        {
            bytes += ArrayHeaderBytes;
            foreach (VortexField field in _fields)
            {
                bytes += FieldBytes + StringBytes(field.Name) + NameEntryBytes;
            }
        }

        return bytes;
    }

    /// <summary>An array's header, its length included.</summary>
    private const int ArrayHeaderBytes = 24;

    /// <summary>A <see cref="VortexField"/> in the type's array: its name and its type.</summary>
    private const int FieldBytes = 16;

    /// <summary>A name's entry in the index a lookup by name builds, and its bucket: its hash, its next, its key, its value.</summary>
    private const int NameEntryBytes = 28;

    private static long StringBytes(string? text) => text is null ? 0 : ArrayHeaderBytes + (2L * text.Length);

    private int ComputeHash()
    {
        HashCode hash = default;
        hash.Add(Kind);
        hash.Add(IsNullable);
        hash.Add(PrimitiveType);
        hash.Add(Precision);
        hash.Add(Scale);
        hash.Add(FixedSize);
        hash.Add(ElementType?._hash ?? 0);
        hash.Add(StorageType?._hash ?? 0);
        hash.Add(ExtensionId);
        if (_fields is not null)
        {
            foreach (VortexField field in _fields)
            {
                hash.Add(field.Name);
                hash.Add(field.Type._hash);
            }
        }

        if (_metadata is not null)
        {
            hash.AddBytes(_metadata);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] VortexType? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || other._hash != _hash || other.Kind != Kind || other.IsNullable != IsNullable
            || other.PrimitiveType != PrimitiveType || other.Precision != Precision || other.Scale != Scale
            || other.FixedSize != FixedSize || other.ExtensionId != ExtensionId)
        {
            return false;
        }

        if (!Equals(ElementType, other.ElementType) || !Equals(StorageType, other.StorageType))
        {
            return false;
        }

        if (!ExtensionMetadata.Span.SequenceEqual(other.ExtensionMetadata.Span))
        {
            return false;
        }

        ReadOnlySpan<VortexField> mine = Fields;
        ReadOnlySpan<VortexField> theirs = other.Fields;
        if (mine.Length != theirs.Length)
        {
            return false;
        }

        for (int i = 0; i < mine.Length; i++)
        {
            if (mine[i].Name != theirs[i].Name || !mine[i].Type.Equals(theirs[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Equals(VortexType? left, VortexType? right) =>
        left is null ? right is null : left.Equals(right);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VortexType other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _hash;

    /// <summary>Value equality.</summary>
    public static bool operator ==(VortexType? left, VortexType? right) => Equals(left, right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(VortexType? left, VortexType? right) => !Equals(left, right);

    /// <summary>The type as the format's tools print it: <c>i32</c>, <c>f64?</c>, <c>struct{a: utf8}</c>, <c>timestamp(µs, UTC)</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _text ??= VortexTypeFormatter.Format(this);

    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        string text = ToString();
        if (text.AsSpan().TryCopyTo(destination))
        {
            charsWritten = text.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>A destination too short takes nothing: false, and no byte said written, as the base types do.</remarks>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (System.Text.Unicode.Utf8.FromUtf16(ToString(), utf8Destination, out _, out bytesWritten) == System.Buffers.OperationStatus.Done)
        {
            return true;
        }

        bytesWritten = 0;
        return false;
    }
}

/// <summary>What a timestamp's zone is on this host (<see cref="VortexType.Zone"/>).</summary>
internal enum ZoneState : byte
{
    /// <summary>No zone: a naive timestamp, or a type that is no timestamp.</summary>
    Naive = 1,

    /// <summary>A zone named as UTC.</summary>
    Utc,

    /// <summary>A zone this host resolves, named otherwise.</summary>
    Resolved,

    /// <summary>A zone this host does not know.</summary>
    Unresolved,
}

/// <summary>The extension ids the core knows by name.</summary>
internal static class ExtensionIds
{
    internal const string Date = "vortex.date";
    internal const string Time = "vortex.time";
    internal const string Timestamp = "vortex.timestamp";
    internal const string Uuid = "vortex.uuid";
}
