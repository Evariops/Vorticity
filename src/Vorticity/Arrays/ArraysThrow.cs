using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vorticity.Arrays;

/// <summary>
/// The throw sites of this component, each one non-inlineable so that the accessors calling them
/// stay small enough to inline. Malformed input raises <see cref="VortexFormatException"/>; a
/// caller mistake raises an argument exception instead.
/// </summary>
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
