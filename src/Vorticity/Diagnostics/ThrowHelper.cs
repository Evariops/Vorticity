using System.Runtime.CompilerServices;

namespace Vorticity;

/// <summary>
/// Cold throw helpers. Hot paths return codes or use a <c>TryXxx</c> form; where a throw is
/// unavoidable it goes through one of these non-inlined helpers, so the throwing code stays out
/// of the caller and the caller stays small enough to inline.
/// </summary>
internal static class ThrowHelper
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowFormat(string message) => throw new VortexFormatException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T ThrowFormat<T>(string message) => throw new VortexFormatException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowTruncated(string what, long need, long have) =>
        throw new VortexFormatException(
            $"Truncated {what}: needed {need} bytes, {have} available.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowOutOfBounds(string what, long offset, long length, long limit) =>
        throw new VortexFormatException(
            $"{what} range [{offset}, {offset + length}) is outside the available {limit} bytes.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T ThrowUnsupported<T>(string componentId, string kind) =>
        throw new VortexUnsupportedException(componentId, kind);
}
