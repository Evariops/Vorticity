using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Vorticity.Scanning;

/// <summary>
/// The throw sites of the scan, each one non-inlineable so that the batch loop and the split walk
/// stay inlineable. What the file says is wrong raises <see cref="VortexFormatException"/>; what
/// the caller says is wrong raises an argument exception. The scan's inputs are mostly the
/// caller's — a projection path, a row range, a batch cap, a degree of parallelism — so nearly
/// every helper here is an argument exception by design, not by oversight.
/// </summary>
internal static class ScanThrow
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void UnknownPath(string path, string parameterName) =>
        throw new ArgumentException(
            $"The projection path '{path}' does not name a field of the file's schema.",
            parameterName);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void PathThroughLeaf(string path, string segment, string parameterName) =>
        throw new ArgumentException(
            $"The projection path '{path}' descends through '{segment}', which is not a struct.",
            parameterName);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void NonStructRoot(string parameterName) =>
        throw new ArgumentException(
            "A projection on a non-struct root may only be Projection.All: there are no named " +
            "fields to select.",
            parameterName);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void PathTooDeep(string path, string parameterName) =>
        throw new ArgumentException(
            $"The projection path '{path}' nests deeper than " +
            $"{VortexLimits.MaxDTypeDepth.ToString(CultureInfo.InvariantCulture)} levels.",
            parameterName);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void FieldIndexOutOfRange(int fieldIndex, int fieldCount, string parameterName) =>
        throw new ArgumentOutOfRangeException(
            parameterName,
            fieldIndex,
            $"The schema's root struct has {fieldCount.ToString(CultureInfo.InvariantCulture)} fields.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void Disposed(string what) =>
        throw new ObjectDisposedException(what);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static T NoCurrentBatch<T>() =>
        throw new InvalidOperationException(
            "There is no current batch: MoveNextAsync has not been called, or it returned false.");
}
