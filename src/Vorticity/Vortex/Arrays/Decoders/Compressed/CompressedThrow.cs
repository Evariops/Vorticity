using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays.Decoders.Compressed;

/// <summary>
/// Throw sites for the compressed decoders, kept out of line so the decode loops stay inlineable.
/// Malformed input always raises <see cref="VortexFormatException"/>; an argument exception raised
/// here would mean a bug in this library rather than a bad file.
/// </summary>
internal static class CompressedThrow
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void Format(string message) => throw new VortexFormatException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static T Format<T>(string message) => throw new VortexFormatException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void ChildKind(string encodingId, string what, CanonicalKind actual, string expected) =>
        throw new VortexFormatException(
            $"{encodingId}'s {what} child decoded to {actual}; {expected} was required.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void ChildLength(string encodingId, string what, int actual, int expected) =>
        throw new VortexFormatException(
            $"{encodingId}'s {what} child holds {actual} rows; {expected} were required.");
}
