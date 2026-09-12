// The four core extension DTypes. They live here, with the array spine, rather than with the
// vortex.ext decoder, because they are resolved from a DTYPE rather than from an array node and
// because both canonical-decoders (contract §9.5) and columns (§12) need them.
//
// Their metadata is HAND-ROLLED, not Protobuf. Every layout below is transcribed from
// vortex-array-0.86.1/src/extension/, and the byte-level details are contract §9.5:
//   * vortex.date       1 byte, trailing ignored, storage Days->i32 / ms->i64, ns/us/s rejected;
//   * vortex.time       1 byte, trailing ignored, storage s|ms->i32 / us|ns->i64, Days rejected;
//   * vortex.timestamp  [unit][u16 LE tz_len][tz], min 3 bytes because the length prefix is ALWAYS
//                       written, storage i64 for every unit, TimeUnit::Days accepted at the dtype
//                       level and rejected only when a value is unpacked;
//   * vortex.uuid       0 or 1 byte and Length > 1 IS rejected, storage
//                       FixedSizeList(Primitive(U8, NonNullable), 16, any).
//
// Validation happens upstream at DType-parse time (`try_with_vtable` always calls
// `validate_dtype`). Phase 1 defers it to first USE of the field (contract §2.3), so the
// vortex.ext decoder and the column accessor each run it once.
using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Unicode;
using Vorticity.Types;

namespace Vorticity.Arrays;

/// <summary>
/// Vortex's own <c>TimeUnit</c> discriminants, ordered finest to coarsest. They are
/// <b>not</b> Arrow's. vortex-array-0.86.1/src/extension/datetime/unit.rs.
/// </summary>
public enum VortexTimeUnit : byte
{
    /// <summary>Nanoseconds.</summary>
    Nanoseconds = 0,

    /// <summary>Microseconds.</summary>
    Microseconds = 1,

    /// <summary>Milliseconds.</summary>
    Milliseconds = 2,

    /// <summary>Seconds.</summary>
    Seconds = 3,

    /// <summary>Whole days.</summary>
    Days = 4,
}

/// <summary>The core extension dtypes this build understands.</summary>
public enum ExtensionKind : byte
{
    /// <summary>An extension id we do not implement. Fatal only when the field is read.</summary>
    Unknown = 0,

    /// <summary><c>vortex.date</c>.</summary>
    Date = 1,

    /// <summary><c>vortex.time</c>.</summary>
    Time = 2,

    /// <summary><c>vortex.timestamp</c>.</summary>
    Timestamp = 3,

    /// <summary><c>vortex.uuid</c>.</summary>
    Uuid = 4,
}

/// <summary>A parsed <c>vortex.timestamp</c> metadata block.</summary>
public readonly ref struct TimestampOptions
{
    internal TimestampOptions(VortexTimeUnit unit, bool hasTimeZone, ReadOnlySpan<byte> timeZoneUtf8)
    {
        Unit = unit;
        HasTimeZone = hasTimeZone;
        TimeZoneUtf8 = timeZoneUtf8;
    }

    /// <summary>The time unit of the stored <c>i64</c>.</summary>
    public VortexTimeUnit Unit { get; }

    /// <summary><see langword="true"/> when a non-empty timezone string was written.</summary>
    public bool HasTimeZone { get; }

    /// <summary>
    /// The timezone as raw UTF-8, pointing into the caller's metadata. Never resolved against a
    /// zone database: docs/07-dotnet-mapping.md §3 is binding - the core resolves no timezone.
    /// </summary>
    public ReadOnlySpan<byte> TimeZoneUtf8 { get; }
}

/// <summary>A parsed <c>vortex.uuid</c> metadata block.</summary>
public readonly struct UuidOptions
{
    internal UuidOptions(bool hasVersion, byte version)
    {
        HasVersion = hasVersion;
        Version = version;
    }

    /// <summary><see langword="false"/> when the metadata was empty.</summary>
    public bool HasVersion { get; }

    /// <summary>
    /// The RFC 4122 version discriminant: 0..8 are Nil, Mac, Dce, Md5, Random, Sha1, SortMac,
    /// SortRand and Custom. Both <c>0x0F</c> and <c>0xFF</c> are normalized to
    /// <see cref="ExtensionDTypeRegistry.UuidVersionMax"/>, because the <c>uuid</c> crate changed the value in 1.23.0.
    /// </summary>
    public byte Version { get; }
}

/// <summary>Resolves and parses the four core extension dtypes.</summary>
public static class ExtensionDTypeRegistry
{
    /// <summary>The normalized discriminant for the <c>Max</c> UUID version.</summary>
    public const byte UuidVersionMax = 0xFF;

    private const byte UuidVersionMaxLegacy = 0x0F;
    private const byte MaxUuidNumberedVersion = 8;

    // Interned literals: returning one allocates nothing. U+00B5 MICRO SIGN, NOT U+03BC GREEK
    // SMALL LETTER MU - it reaches DType.ToString() and therefore every manifest-`dtype` compare.
    private static readonly string[] UnitNames = ["ns", "µs", "ms", "s", "days"];

    /// <summary>
    /// Resolves an extension id. Anything outside the four core ids is
    /// <see cref="ExtensionKind.Unknown"/>, which is not an error until the field is read.
    /// </summary>
    /// <param name="idUtf8">The id as the dtype carries it.</param>
    public static ExtensionKind Resolve(ReadOnlySpan<byte> idUtf8)
    {
        switch (idUtf8.Length)
        {
            case 11:
                switch (idUtf8[7])
                {
                    case (byte)'d': return idUtf8.SequenceEqual("vortex.date"u8) ? ExtensionKind.Date : ExtensionKind.Unknown;
                    case (byte)'t': return idUtf8.SequenceEqual("vortex.time"u8) ? ExtensionKind.Time : ExtensionKind.Unknown;
                    case (byte)'u': return idUtf8.SequenceEqual("vortex.uuid"u8) ? ExtensionKind.Uuid : ExtensionKind.Unknown;
                    default: return ExtensionKind.Unknown;
                }

            case 16:
                return idUtf8.SequenceEqual("vortex.timestamp"u8) ? ExtensionKind.Timestamp : ExtensionKind.Unknown;

            default:
                return ExtensionKind.Unknown;
        }
    }

    /// <summary>
    /// <b>The only place a <see cref="VortexUnsupportedException"/> with kind <c>"dtype"</c> is
    /// thrown</b> (contract §2.3), and only when the field carrying the extension is actually read.
    /// </summary>
    /// <param name="idUtf8">The extension id as the dtype carries it.</param>
    /// <exception cref="VortexUnsupportedException">The id is not one of the four core dtypes.</exception>
    public static void RequireSupported(ReadOnlySpan<byte> idUtf8)
    {
        if (Resolve(idUtf8) == ExtensionKind.Unknown)
        {
            ThrowUnsupportedDType(idUtf8);
        }
    }

    /// <summary>
    /// Reads <c>vortex.date</c>'s metadata and validates it against the storage dtype.
    /// </summary>
    /// <param name="metadata">The extension metadata: 1 byte, trailing bytes ignored.</param>
    /// <param name="storage">The extension's storage dtype.</param>
    /// <returns><see cref="VortexTimeUnit.Days"/> or <see cref="VortexTimeUnit.Milliseconds"/>.</returns>
    /// <exception cref="VortexFormatException">
    /// The metadata is empty, the unit is undefined, the unit is not one <c>vortex.date</c> admits,
    /// or the storage dtype does not match the unit.
    /// </exception>
    public static VortexTimeUnit ReadDateUnit(ReadOnlySpan<byte> metadata, DType storage)
    {
        VortexTimeUnit unit = ReadUnitByte(metadata, "vortex.date");

        // Days -> i32, Milliseconds -> i64. ns, us and s are rejected outright.
        PType required = unit switch
        {
            VortexTimeUnit.Days => PType.I32,
            VortexTimeUnit.Milliseconds => PType.I64,
            _ => ThrowUnit<PType>("vortex.date", unit, "days or ms"),
        };

        RequirePrimitiveStorage(storage, required, "vortex.date", unit);
        return unit;
    }

    /// <summary>
    /// Reads <c>vortex.time</c>'s metadata and validates it against the storage dtype.
    /// </summary>
    /// <param name="metadata">The extension metadata: 1 byte, trailing bytes ignored.</param>
    /// <param name="storage">The extension's storage dtype.</param>
    /// <returns>The time unit; never <see cref="VortexTimeUnit.Days"/>.</returns>
    /// <exception cref="VortexFormatException">
    /// The metadata is empty, the unit is undefined or <c>Days</c>, or the storage dtype does not
    /// match the unit.
    /// </exception>
    public static VortexTimeUnit ReadTimeUnit(ReadOnlySpan<byte> metadata, DType storage)
    {
        VortexTimeUnit unit = ReadUnitByte(metadata, "vortex.time");

        // s|ms -> i32, us|ns -> i64. Days is rejected.
        PType required = unit switch
        {
            VortexTimeUnit.Seconds or VortexTimeUnit.Milliseconds => PType.I32,
            VortexTimeUnit.Microseconds or VortexTimeUnit.Nanoseconds => PType.I64,
            _ => ThrowUnit<PType>("vortex.time", unit, "s, ms, µs or ns"),
        };

        RequirePrimitiveStorage(storage, required, "vortex.time", unit);
        return unit;
    }

    /// <summary>
    /// Reads <c>vortex.timestamp</c>'s metadata and validates it against the storage dtype.
    /// </summary>
    /// <param name="metadata">
    /// <c>[0] = unit</c>, <c>[1..3] = u16 LE timezone length</c>, <c>[3..3+len] = UTF-8 timezone</c>,
    /// not NUL-terminated. The 2-byte length prefix is <em>always</em> written, so "no timezone" is
    /// three bytes and not one.
    /// </param>
    /// <param name="storage">The extension's storage dtype; <c>i64</c> for every unit.</param>
    /// <returns>The parsed options; <see cref="TimestampOptions.TimeZoneUtf8"/> points into
    /// <paramref name="metadata"/>.</returns>
    /// <exception cref="VortexFormatException">
    /// The metadata is shorter than 3 bytes, the unit is undefined, the timezone length overruns,
    /// the timezone is not valid UTF-8, or the storage dtype is not <c>i64</c>.
    /// </exception>
    public static TimestampOptions ReadTimestamp(ReadOnlySpan<byte> metadata, DType storage)
    {
        if (metadata.Length < 3)
        {
            ArraysThrow.Format(
                $"vortex.timestamp metadata is {metadata.Length} bytes; at least 3 are required " +
                "(unit plus a u16 timezone length, which is always written).");
        }

        VortexTimeUnit unit = CheckUnit(metadata[0], "vortex.timestamp");

        int tzLength = BinaryPrimitives.ReadUInt16LittleEndian(metadata.Slice(1, 2));
        if (3 + tzLength > metadata.Length)
        {
            ArraysThrow.Format(
                $"vortex.timestamp declares a {tzLength}-byte timezone but only " +
                $"{metadata.Length - 3} bytes follow its header.");
        }

        ReadOnlySpan<byte> zone = metadata.Slice(3, tzLength);
        if (tzLength != 0 && !Utf8.IsValid(zone))
        {
            ArraysThrow.Format("vortex.timestamp's timezone is not valid UTF-8.");
        }

        // Every unit stores i64, TimeUnit::Days included: Days passes DType validation upstream and
        // fails only when a value is unpacked.
        RequirePrimitiveStorage(storage, PType.I64, "vortex.timestamp", unit);
        return new TimestampOptions(unit, tzLength != 0, zone);
    }

    /// <summary>
    /// Reads <c>vortex.uuid</c>'s metadata and validates it against the storage dtype.
    /// </summary>
    /// <param name="metadata">0 or 1 byte. Unlike the other three, 2 or more IS rejected.</param>
    /// <param name="storage">
    /// Must be <c>FixedSizeList(Primitive(U8, NonNullable), 16, any)</c>: the ELEMENT must be
    /// non-nullable, the outer list's nullability is free. Bytes are RFC 4122 network order.
    /// </param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="VortexFormatException">
    /// The metadata is longer than 1 byte, the version discriminant is undefined, or the storage
    /// dtype is not a 16-element FSL of non-nullable <c>u8</c>.
    /// </exception>
    public static UuidOptions ReadUuid(ReadOnlySpan<byte> metadata, DType storage)
    {
        if (metadata.Length > 1)
        {
            ArraysThrow.Format(
                $"vortex.uuid metadata is {metadata.Length} bytes; 0 or 1 is the whole domain.");
        }

        RequireUuidStorage(storage);

        if (metadata.IsEmpty)
        {
            return default;
        }

        byte raw = metadata[0];
        if (raw is UuidVersionMaxLegacy or UuidVersionMax)
        {
            // The uuid crate moved Max from 0x0F to 0xFF in 1.23.0; both spellings mean Max.
            return new UuidOptions(true, UuidVersionMax);
        }

        if (raw > MaxUuidNumberedVersion)
        {
            ArraysThrow.Format(
                $"vortex.uuid version {raw} is undefined; 0..{MaxUuidNumberedVersion}, 0x0F and " +
                "0xFF are the whole domain.");
        }

        return new UuidOptions(true, raw);
    }

    /// <summary>
    /// Culture-invariant rendering used by <c>DType.ToString()</c> and therefore by every
    /// manifest-<c>dtype</c> comparison: <c>"ns"</c>, <c>"µs"</c>, <c>"ms"</c>, <c>"s"</c>,
    /// <c>"days"</c>.
    /// </summary>
    /// <param name="unit">The unit to render.</param>
    /// <exception cref="VortexFormatException"><paramref name="unit"/> is not a defined tag.</exception>
    public static string Format(VortexTimeUnit unit)
    {
        if ((uint)unit > (uint)VortexTimeUnit.Days)
        {
            ArraysThrow.Format($"TimeUnit tag {(byte)unit} is not defined; 0..4 are.");
        }

        return UnitNames[(int)unit];
    }

    /// <summary>Guards a <see cref="VortexTimeUnit"/> read from a file.</summary>
    /// <param name="unit">The candidate.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDefined(VortexTimeUnit unit) => (uint)unit <= (uint)VortexTimeUnit.Days;

    private static VortexTimeUnit ReadUnitByte(ReadOnlySpan<byte> metadata, string id)
    {
        if (metadata.IsEmpty)
        {
            ArraysThrow.Format($"{id} metadata is empty; exactly one TimeUnit byte is required.");
        }

        // Trailing bytes are ignored, deliberately: upstream reads metadata[0] and stops.
        return CheckUnit(metadata[0], id);
    }

    private static VortexTimeUnit CheckUnit(byte raw, string id)
    {
        if (raw > (byte)VortexTimeUnit.Days)
        {
            ArraysThrow.Format($"{id} declares TimeUnit {raw}; 0..4 are defined.");
        }

        return (VortexTimeUnit)raw;
    }

    private static void RequirePrimitiveStorage(DType storage, PType required, string id, VortexTimeUnit unit)
    {
        // Class I in the sense that matters here: upstream's `as_ptype` is a vortex_panic! on a
        // non-Primitive storage, so the Kind check has to come first and produce a format error.
        if (storage.IsDefault || storage.Kind != DTypeKind.Primitive)
        {
            ArraysThrow.Format(
                $"{id} requires a Primitive storage dtype; this one is " +
                $"{(storage.IsDefault ? "absent" : storage.Kind.ToString())}.");
        }

        if (storage.PType != required)
        {
            ArraysThrow.Format(
                $"{id} with unit {Format(unit)} stores {required.Name()}; this dtype stores " +
                $"{storage.PType.Name()}.");
        }
    }

    private static void RequireUuidStorage(DType storage)
    {
        if (storage.IsDefault || storage.Kind != DTypeKind.FixedSizeList || storage.FixedSize != 16)
        {
            ArraysThrow.Format(
                "vortex.uuid requires a 16-element FixedSizeList storage dtype.");
        }

        DType element = storage.ElementType;
        if (element.Kind != DTypeKind.Primitive ||
            element.PType != PType.U8 ||
            element.Nullability != Nullability.NonNullable)
        {
            ArraysThrow.Format(
                "vortex.uuid's storage elements must be non-nullable u8; the outer list's " +
                "nullability is free.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static T ThrowUnit<T>(string id, VortexTimeUnit unit, string admitted) =>
        throw new VortexFormatException(
            $"{id} does not admit TimeUnit {Format(unit)}; it admits {admitted}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowUnsupportedDType(ReadOnlySpan<byte> idUtf8)
    {
        string id = System.Text.Encoding.UTF8.GetString(idUtf8);
        string? detail = EncodingRegistry.DescribeUnsupported(idUtf8);
        throw detail is null
            ? new VortexUnsupportedException(id, VortexComponentKind.DType)
            : new VortexUnsupportedException(id, VortexComponentKind.DType, detail);
    }
}
