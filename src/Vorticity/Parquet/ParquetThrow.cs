using System;
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

    /// <summary>
    /// <paramref name="bytes"/>, a size computed wide from what the file declares, narrowed to an
    /// <see langword="int"/>: a format error past what one block holds, never a size that wraps.
    /// </summary>
    internal static int Size(long bytes, string what) =>
        bytes >= 0 && bytes <= Array.MaxLength ? (int)bytes : Format<int>($"{what} takes {bytes} bytes, past what one block holds.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Truncated(string what) =>
        throw new ParquetFormatException($"The {what} ends before its last byte.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static T Truncated<T>(string what) =>
        throw new ParquetFormatException($"The {what} ends before its last byte.");
}
