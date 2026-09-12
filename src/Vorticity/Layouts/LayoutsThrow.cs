// Every throw in this component goes through a NoInlining helper so the walk stays inlineable
// (PHASE1-CONTRACTS.md §1.4). A file says it -> VortexFormatException; a caller says it ->
// Argument*; a component we do not implement -> VortexUnsupportedException, and only from
// LayoutReaderTable.Get (contract §2.3).
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Vorticity.Layouts;

internal static class LayoutsThrow
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void Format(string message) => throw new VortexFormatException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static T Format<T>(string message) => throw new VortexFormatException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void NodeIndex(int index, int count) =>
        throw new VortexFormatException(
            $"Layout node index {index} is outside [0, {count}) of the layout tree.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void ChildIndex(int index, int count) =>
        throw new VortexFormatException($"Layout child index {index} is outside [0, {count}).");

    /// <summary>
    /// The eager segment check contract §11.3 requires: upstream resolves a segment id lazily, at
    /// request time; we turn an I/O-time failure into a parse-time one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void MissingSegment(uint segmentId, int segmentCount) =>
        throw new VortexFormatException(
            $"layout references missing segment {segmentId.ToString(CultureInfo.InvariantCulture)} " +
            $"(segment count: {segmentCount.ToString(CultureInfo.InvariantCulture)})");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void Arity(string encodingId, string what, int actual, int expected) =>
        throw new VortexFormatException(
            $"A {encodingId} layout must have {expected} {what}; this one has {actual}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void RowCountMismatch(string encodingId, string childName, long actual, long expected) =>
        throw new VortexFormatException(
            $"A {encodingId} layout's {childName} child covers {actual} rows; the parent covers {expected}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void RowCountRange(ulong rowCount) =>
        throw new VortexFormatException(
            $"A layout declares {rowCount.ToString(CultureInfo.InvariantCulture)} rows, which does " +
            "not fit a signed 64-bit row index.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void TooManyNodes(long limit, int layoutBytes) =>
        throw new VortexFormatException(
            $"The layout tree expands to more than {limit.ToString(CultureInfo.InvariantCulture)} " +
            $"nodes from {layoutBytes.ToString(CultureInfo.InvariantCulture)} bytes; child layouts " +
            "shared between parents are not supported.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void TooManyInspectedBytes(int layoutBytes) =>
        throw new VortexFormatException(
            "The layout tree inspects far more metadata and segment bytes than its " +
            $"{layoutBytes.ToString(CultureInfo.InvariantCulture)}-byte buffer can honestly " +
            "contain; vectors shared between layout nodes at this scale are not supported.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void BatchTooLarge(long rows) =>
        throw new ArgumentOutOfRangeException(
            "rows", rows, "A single batch cannot exceed int.MaxValue rows.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void RangeOutsideNode(RowRange rows, long rowCount) =>
        throw new ArgumentOutOfRangeException(
            nameof(rows),
            rows.ToString(),
            $"The range escapes the layout node, which covers {rowCount.ToString(CultureInfo.InvariantCulture)} rows.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static T Detached<T>() =>
        throw new InvalidOperationException(
            "This LayoutTree was parsed without a VortexFile; only its shape and dtypes are " +
            "available, and no reader can execute against it.");
}
