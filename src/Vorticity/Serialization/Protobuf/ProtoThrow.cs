using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vorticity.Serialization.Protobuf;

/// <summary>
/// Cold throw helpers for the Protobuf runtime. Every failure on malformed input is a
/// <see cref="VortexFormatException"/> and nothing else, and no throw sits inline in a hot method,
/// so the reader's fast paths stay inlineable. <see cref="DoesNotReturnAttribute"/> also lets the
/// caller's flow analysis treat a failed bounds check as terminal.
/// </summary>
internal static class ProtoThrow
{
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void TruncatedVarint(int position) =>
        throw new VortexFormatException(
            $"Truncated Protobuf varint at offset {position}: the continuation bit is set on the " +
            "last available byte.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T TruncatedVarint<T>(int position)
    {
        TruncatedVarint(position);
        return default!;
    }

    /// <summary>
    /// A varint that needs an eleventh byte, or whose tenth byte carries bits above bit 63.
    /// </summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T MalformedVarint<T>(int position, byte tenthByte) =>
        throw new VortexFormatException(
            $"Malformed Protobuf varint at offset {position}: its tenth byte is 0x{tenthByte:X2}, " +
            "but only bit 0 of that byte is part of a 64-bit value (a longer varint, or any bit " +
            "above bit 63, cannot be represented).");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T VarintExceeds32Bits<T>(int position, ulong value) =>
        throw new VortexFormatException(
            $"Protobuf varint at offset {position} is {value}, which does not fit in 32 bits.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void TagExceeds32Bits(int position, ulong tag) =>
        throw new VortexFormatException(
            $"Protobuf tag at offset {position} is {tag}: a tag is a uint32 and cannot exceed " +
            $"{uint.MaxValue}.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void FieldNumberZero(int position) =>
        throw new VortexFormatException(
            $"Protobuf tag at offset {position} has field number 0, which the wire format reserves " +
            "and never assigns.");

    /// <summary>
    /// Groups are wire types 3 and 4. proto3 never emits them, so a payload that contains one is
    /// not a forward-compatible extension but a malformed message.
    /// This is the "reject" half of the tolerate/reject distinction: an unknown <em>field
    /// number</em> is skipped, an unrepresentable <em>framing</em> is refused.
    /// </summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void GroupWireType(int position, int wireType) =>
        throw new VortexFormatException(
            $"Protobuf group wire type {wireType} at offset {position}: groups are a proto2 " +
            "framing that proto3 never emits and Vorticity does not accept.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ReservedWireType(int position, int wireType) =>
        throw new VortexFormatException(
            $"Protobuf wire type {wireType} at offset {position} is reserved and has no defined " +
            "framing.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T Truncated<T>(string what, int position, int need, int have) =>
        throw new VortexFormatException(
            $"Truncated Protobuf {what} at offset {position}: needed {need} bytes, {have} available.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void TruncatedVoid(string what, int position, int need, int have) =>
        throw new VortexFormatException(
            $"Truncated Protobuf {what} at offset {position}: needed {need} bytes, {have} available.");

    /// <summary>
    /// A length-delimited field whose declared length cannot even be expressed as a span length.
    /// Checked before it is narrowed to <see cref="int"/>, so the narrowing can never wrap.
    /// </summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T LengthTooLarge<T>(int position, ulong length)
        where T : allows ref struct =>
        throw new VortexFormatException(
            $"Protobuf length-delimited field at offset {position} declares {length} bytes, which " +
            $"exceeds the {int.MaxValue}-byte maximum addressable length.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T LengthEscapesBuffer<T>(int position, int length, int available)
        where T : allows ref struct =>
        throw new VortexFormatException(
            $"Protobuf length-delimited field at offset {position} declares {length} bytes but " +
            $"only {available} remain in the message.");
}
