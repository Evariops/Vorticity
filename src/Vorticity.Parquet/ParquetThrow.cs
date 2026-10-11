using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vorticity.Parquet;

/// <summary>
/// The throws of the hot paths, kept out of line so that a check costs its branch and nothing of the
/// message it would build.
/// </summary>
internal static class ParquetThrow
{
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Format(string message) => throw new ParquetFormatException(message);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T Format<T>(string message) => throw new ParquetFormatException(message);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Unsupported(string componentId, ParquetComponentKind kind) =>
        throw new ParquetUnsupportedException(componentId, kind);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Unsupported(string componentId, ParquetComponentKind kind, string detail) =>
        throw new ParquetUnsupportedException(componentId, kind, detail);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Truncated(string what) =>
        throw new ParquetFormatException($"The {what} ends before its last byte.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T Truncated<T>(string what) =>
        throw new ParquetFormatException($"The {what} ends before its last byte.");
}
