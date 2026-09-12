// Shared field-level decoding helpers for the per-encoding metadata messages of spec/METADATA.md.
//
// Two rules from the Phase 1 contract §1.7 are implemented here once, so that no individual codec
// can get them wrong:
//
//   * an UNRECOGNIZED field number is skipped, dispatching on its wire type alone (read-forever,
//     docs/02-format.md §5.3);
//   * a RECOGNIZED field number carrying the wrong wire type, or a value outside the message's
//     domain, is rejected with a VortexFormatException. `prost` rejects the first
//     ("invalid wire type") and silently coerces the second — an unknown `enumeration` varint
//     becomes the enum's zero value, so a corrupt `codes_ptype = 99` reads as `u8` upstream. We
//     reject instead, because that field decides how wide a buffer the decoder reads
//     (docs/08-semantics.md §5, class I).
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;

namespace Vorticity.Arrays.Metadata;

/// <summary>
/// Field readers shared by every metadata codec in this namespace. Internal: the codecs are the
/// public surface.
/// </summary>
internal static class MetadataProto
{
    /// <summary>
    /// Reads a <c>uint32</c> field. Rejects any wire type but varint, and any value above
    /// <see cref="uint.MaxValue"/> — a <c>uint32</c> is out of domain there, unlike an
    /// <c>int32</c>, which is legally sign-extended to ten bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint ReadUInt32(scoped ref ProtoReader reader, ProtoWireType wireType, string message, string field)
    {
        Expect(wireType, ProtoWireType.Varint, message, field);
        return reader.ReadVarint32();
    }

    /// <summary>Reads a <c>uint64</c> field.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong ReadUInt64(scoped ref ProtoReader reader, ProtoWireType wireType, string message, string field)
    {
        Expect(wireType, ProtoWireType.Varint, message, field);
        return reader.ReadVarint();
    }

    /// <summary>Reads a <c>bool</c> field. Any non-zero varint is true, as the wire format requires.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ReadBool(scoped ref ProtoReader reader, ProtoWireType wireType, string message, string field)
    {
        Expect(wireType, ProtoWireType.Varint, message, field);
        return reader.ReadBool();
    }

    /// <summary>
    /// Reads a <c>PType</c> enumeration field and validates it against the eleven defined tags.
    /// </summary>
    /// <remarks>
    /// prost encodes an <c>enumeration</c> field as <c>int32</c>, so a negative value arrives as a
    /// ten-byte varint. <see cref="ProtoReader.ReadInt32"/> reproduces that truncation exactly;
    /// the range test then rejects it, rather than letting <c>-1</c> alias some defined tag.
    /// </remarks>
    internal static PType ReadPType(scoped ref ProtoReader reader, ProtoWireType wireType, string message, string field)
    {
        Expect(wireType, ProtoWireType.Varint, message, field);
        int raw = reader.ReadInt32();
        if ((uint)raw > (uint)PType.F64)
        {
            ThrowEnumOutOfRange(message, field, raw, "PType", (int)PType.F64);
        }

        return (PType)raw;
    }

    /// <summary>
    /// Reads a length-delimited <c>bytes</c> / <c>string</c> / embedded-message field as a slice of
    /// the input. No copy: the span borrows the caller's metadata buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<byte> ReadBytes(
        scoped ref ProtoReader reader, ProtoWireType wireType, string message, string field)
    {
        Expect(wireType, ProtoWireType.LengthDelimited, message, field);
        return reader.ReadLengthDelimited();
    }

    /// <summary>
    /// Reads one element of a <c>repeated uint32</c> field, or every element of its packed form,
    /// appending into <paramref name="destination"/> from <paramref name="count"/>.
    /// </summary>
    /// <remarks>
    /// proto3 packs repeated numeric fields by default but a conforming reader must accept the
    /// unpacked spelling too, so both wire types are legal here. Every element is bounded by the
    /// enclosing message body, so the loop is linear in the input and cannot spin.
    /// </remarks>
    internal static void ReadRepeatedUInt32(
        scoped ref ProtoReader reader,
        ProtoWireType wireType,
        string message,
        string field,
        Span<uint> destination,
        ref int count)
    {
        if (wireType == ProtoWireType.Varint)
        {
            Append(destination, ref count, reader.ReadVarint32(), message, field);
            return;
        }

        if (wireType != ProtoWireType.LengthDelimited)
        {
            ThrowWireType(message, field, wireType, "varint or length-delimited");
        }

        ProtoReader packed = reader.ReadMessage();
        while (!packed.End)
        {
            Append(destination, ref count, packed.ReadVarint32(), message, field);
        }
    }

    /// <summary>Counts the elements of a <c>repeated uint32</c> field without storing them.</summary>
    internal static int CountRepeatedUInt32(scoped ref ProtoReader reader, ProtoWireType wireType)
    {
        if (wireType == ProtoWireType.Varint)
        {
            reader.ReadVarint();
            return 1;
        }

        if (wireType != ProtoWireType.LengthDelimited)
        {
            reader.SkipField(wireType);
            return 0;
        }

        ProtoReader packed = reader.ReadMessage();
        int n = 0;
        while (!packed.End)
        {
            packed.ReadVarint();
            n++;
        }

        return n;
    }

    private static void Append(Span<uint> destination, ref int count, uint value, string message, string field)
    {
        if ((uint)count >= (uint)destination.Length)
        {
            ThrowTooManyElements(message, field, destination.Length);
        }

        destination[count++] = value;
    }

    /// <summary>Rejects a recognized field number that arrived with the wrong framing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Expect(ProtoWireType actual, ProtoWireType expected, string message, string field)
    {
        if (actual != expected)
        {
            ThrowWireType(message, field, actual, expected.ToString());
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowWireType(string message, string field, ProtoWireType actual, string expected) =>
        throw new VortexFormatException(
            $"{message}.{field} has wire type {actual}; the schema requires {expected}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowEnumOutOfRange(string message, string field, int raw, string enumName, int max) =>
        throw new VortexFormatException(
            $"{message}.{field} carries {enumName} value {raw}; the schema defines 0..{max}. " +
            "The value is rejected rather than coerced to the enum default.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowTooManyElements(string message, string field, int capacity) =>
        throw new VortexFormatException(
            $"{message}.{field} declares more than {capacity} elements, which is the capacity the " +
            "caller supplied.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowMissingRequired(string message, string field) =>
        throw new VortexFormatException($"{message}.{field} is required and is absent.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowOutOfDomain(string message, string field, string detail) =>
        throw new VortexFormatException($"{message}.{field} is out of domain: {detail}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T ThrowOutOfDomain<T>(string message, string field, string detail) =>
        throw new VortexFormatException($"{message}.{field} is out of domain: {detail}");
}
