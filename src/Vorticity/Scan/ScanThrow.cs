// Every throw in this component goes through a NoInlining helper so MoveNextAsync and the split
// walk stay inlineable (PHASE1-CONTRACTS.md §1.4).
//
// The line this file exists to keep straight: a FILE says it -> VortexFormatException; a CALLER
// says it -> Argument*. The scan is the one component whose inputs are mostly the caller's - a
// projection path, a row range, a batch cap, a degree of parallelism - so almost everything here
// is an Argument* and that is correct, not a lapse.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Vorticity.Scan;

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
