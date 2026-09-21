using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vorticity.Types;

namespace Vorticity.Columns;

/// <summary>
/// The throw helpers of the column accessors. Each one is kept out of line so that the accessor
/// calling it stays inlineable. They also fix which exception each kind of error gets: a file that
/// says something impossible is a format error, while a row or field index that does not exist, a
/// request for the wrong .NET type and a use of a disposed batch are all caller errors and get the
/// matching argument, operation or disposal exception.
/// </summary>
internal static class ColumnsThrow
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void Format(string message) => throw new VortexFormatException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static T Format<T>(string message) => throw new VortexFormatException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void RowIndex(int index, int length) =>
        throw new ArgumentOutOfRangeException(
            "index",
            index,
            $"Row index {index} is outside [0, {length}) of this column.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void FieldIndex(int index, int count) =>
        throw new ArgumentOutOfRangeException(
            "index",
            index,
            $"Field index {index} is outside [0, {count}) of this schema.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void UnknownField(ReadOnlySpan<byte> nameUtf8) =>
        throw new ArgumentException(
            $"No field named '{System.Text.Encoding.UTF8.GetString(nameUtf8)}' in this schema.",
            "nameUtf8");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void WrongKind(string what, string expected) =>
        throw new InvalidOperationException(
            $"This column is {what}; the accessor requires {expected}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static T WrongKind<T>(string what, string expected) =>
        throw new InvalidOperationException(
            $"This column is {what}; the accessor requires {expected}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void WrongElementType(PType actual, Type requested) =>
        throw new InvalidOperationException(
            $"This column holds {actual.Name()}; AsPrimitive<{requested.Name}>() requires the " +
            "exactly matching .NET type. Reinterpreting one width as another is the caller's job.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void NotConvertible(string what) => throw new InvalidOperationException(what);
}
