// Every throw in this component goes through a NoInlining helper (Phase 1 contract §1.4) so the
// decode loops stay inlineable. Malformed input is ALWAYS VortexFormatException; an Argument*
// exception here would mean a bug in this library, never a bad file.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays.Decoders.Compressed;

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
