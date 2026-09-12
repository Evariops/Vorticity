// Every throw in this component goes through a NoInlining helper so the hot accessors stay
// inlineable (Phase 1 contract §1.4). Malformed input is always VortexFormatException; a caller
// mistake is always an Argument* exception; a component we do not implement is always
// VortexUnsupportedException, and only from the three sites contract §2.3 names.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays;

internal static class ArraysThrow
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
            $"Array node index {index} is outside [0, {count}) of the node arena.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void ChildIndex(int index, int count) =>
        throw new VortexFormatException(
            $"Array child index {index} is outside [0, {count}).");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static Vorticity.Buffers.VortexBuffer BufferIndex(int index, int count) =>
        throw new VortexFormatException(
            $"Array buffer index {index} is outside [0, {count}) of the node's buffer list.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void CanonicalIndex(int index, int count) =>
        throw new VortexFormatException(
            $"Canonical node index {index} is outside [0, {count}) of the canonical arena.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void NotLoaded() =>
        throw new VortexFormatException("The array node arena holds no array: call Load first.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    internal static void Kind(CanonicalKind actual, string expected) =>
        throw new VortexFormatException(
            $"Canonical node kind is {actual}; this member requires {expected}.");
}
