// Every throw in this component goes through a NoInlining helper so the accessors stay inlineable
// (Phase 1 contract §1.4). Malformed input is always VortexFormatException and never anything else.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vorticity.Serialization.Schemas;

internal static class SchemaThrow
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MissingRequired(string table, string field) =>
        throw new VortexFormatException($"{table} is missing its required '{field}' field.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MetadataCount(int count) =>
        throw new VortexFormatException(
            $"Postscript declares {count} user metadata segments; at most " +
            $"{VortexLimits.MaxMetadataSegments} are permitted.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MetadataKeyEmpty() =>
        throw new VortexFormatException("Postscript metadata key must not be empty.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MetadataKeyTooLong(int length) =>
        throw new VortexFormatException(
            $"Postscript metadata key is {length} UTF-8 bytes; at most " +
            $"{VortexLimits.MaxMetadataKeyLength} are permitted.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void DuplicateMetadataKey(int first, int second) =>
        throw new VortexFormatException(
            $"Postscript metadata entries {first} and {second} share a key; keys must be unique.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MissingSegmentSpecs() =>
        throw new VortexFormatException("FileLayout missing segment specs.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void CompressionSpecCount(int count) =>
        throw new VortexFormatException(
            $"Footer declares {count} compression specs; at most " +
            $"{VortexLimits.MaxCompressionSpecs} are permitted.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void UnalignedStructVector(string what, long offset, int alignment) =>
        throw new VortexFormatException(
            $"{what} starts at byte {offset} of its FlatBuffer, which is not a multiple of " +
            $"{alignment}, so it cannot be reinterpreted in place.");

    /// <summary>
    /// Byte offset of <paramref name="elements"/> inside <paramref name="buffer"/>. Both spans come
    /// from the same buffer, so the difference is the in-buffer position FlatBuffers aligns —
    /// unlike the absolute address, which depends on where the caller's buffer happens to sit.
    /// </summary>
    internal static long InBufferOffset<T>(ReadOnlySpan<byte> buffer, ReadOnlySpan<T> elements)
        where T : unmanaged =>
        (long)Unsafe.ByteOffset(
            ref MemoryMarshal.GetReference(buffer),
            ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(elements)));
}
