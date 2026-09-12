// The element-wise integer kernels the compressed decoders need, written once and monomorphized
// over the physical widths (docs/03-architecture.md §4 invariant 3: the enum switch happens once
// per node, never once per element).
//
// Both are correct on signed types too, because two's-complement addition and the zigzag identity
// are bit-pattern operations: `x + r` and `(x >> 1) ^ -(x & 1)` produce the same bits whether the
// operands are read as signed or unsigned. That is why FoR does not need a signed variant, and why
// its wrapping is free rather than something to guard against
// (vortex-fastlanes-0.86.1/src/for/array/for_decompress.rs uses `wrapping_add`).
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vorticity.Types;

namespace Vorticity.Arrays.Decoders.Compressed;

internal static class IntegerKernels
{
    /// <summary>
    /// <c>destination[i] = source[i] + reference</c>, wrapping, over elements of
    /// <paramref name="elementWidth"/> bytes.
    /// </summary>
    /// <param name="source">The encoded values.</param>
    /// <param name="destination">Same byte length as <paramref name="source"/>.</param>
    /// <param name="elementWidth">1, 2, 4 or 8.</param>
    /// <param name="reference">The reference value's low bits.</param>
    /// <exception cref="ArgumentException">The spans disagree or the width is not supported.</exception>
    public static void AddWrapping(
        ReadOnlySpan<byte> source, Span<byte> destination, int elementWidth, ulong reference)
    {
        RequireSameLength(source.Length, destination.Length);
        switch (elementWidth)
        {
            case 1:
                AddWrapping<byte>(source, destination, (byte)reference);
                break;
            case 2:
                AddWrapping<ushort>(source, destination, (ushort)reference);
                break;
            case 4:
                AddWrapping<uint>(source, destination, (uint)reference);
                break;
            case 8:
                AddWrapping<ulong>(source, destination, reference);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(elementWidth), elementWidth, "Integer element widths are 1, 2, 4 and 8.");
        }
    }

    /// <summary>
    /// <c>destination[i] = (source[i] &gt;&gt; 1) ^ -(source[i] &amp; 1)</c>, the zigzag inverse,
    /// over elements of <paramref name="elementWidth"/> bytes.
    /// </summary>
    /// <param name="source">The zigzag-encoded unsigned values.</param>
    /// <param name="destination">Same byte length as <paramref name="source"/>.</param>
    /// <param name="elementWidth">1, 2, 4 or 8.</param>
    /// <exception cref="ArgumentException">The spans disagree or the width is not supported.</exception>
    public static void ZigZagDecode(ReadOnlySpan<byte> source, Span<byte> destination, int elementWidth)
    {
        RequireSameLength(source.Length, destination.Length);
        switch (elementWidth)
        {
            case 1:
                ZigZagDecode<byte>(source, destination);
                break;
            case 2:
                ZigZagDecode<ushort>(source, destination);
                break;
            case 4:
                ZigZagDecode<uint>(source, destination);
                break;
            case 8:
                ZigZagDecode<ulong>(source, destination);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(elementWidth), elementWidth, "Integer element widths are 1, 2, 4 and 8.");
        }
    }

    private static void AddWrapping<T>(ReadOnlySpan<byte> source, Span<byte> destination, T reference)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source);
        Span<T> dst = MemoryMarshal.Cast<byte, T>(destination);
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = unchecked(src[i] + reference);
        }
    }

    private static void ZigZagDecode<T>(ReadOnlySpan<byte> source, Span<byte> destination)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source);
        Span<T> dst = MemoryMarshal.Cast<byte, T>(destination);
        for (int i = 0; i < src.Length; i++)
        {
            T value = src[i];
            dst[i] = (value >> 1) ^ unchecked(T.Zero - (value & T.One));
        }
    }

    /// <summary>
    /// <c>destination[i] = (long)source[i] * scale</c>, wrapping, widening from any integer
    /// physical type.
    /// </summary>
    /// <param name="source">The values, little-endian, at least <c>destination.Length</c> of them.</param>
    /// <param name="ptype">The element's physical type; must be an integer.</param>
    /// <param name="destination">The i64 accumulator.</param>
    /// <param name="scale">The multiplier applied to every widened value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ptype"/> is not an integer.</exception>
    public static void WidenScaled(
        ReadOnlySpan<byte> source, PType ptype, Span<long> destination, long scale)
    {
        switch (ptype)
        {
            case PType.U8: WidenScaled<byte>(source, destination, scale); break;
            case PType.U16: WidenScaled<ushort>(source, destination, scale); break;
            case PType.U32: WidenScaled<uint>(source, destination, scale); break;
            case PType.U64: WidenScaled<ulong>(source, destination, scale); break;
            case PType.I8: WidenScaled<sbyte>(source, destination, scale); break;
            case PType.I16: WidenScaled<short>(source, destination, scale); break;
            case PType.I32: WidenScaled<int>(source, destination, scale); break;
            case PType.I64: WidenScaled<long>(source, destination, scale); break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(ptype), ptype, "Widening needs an integer physical type.");
        }
    }

    /// <summary>
    /// <c>destination[i] += (long)source[i] * scale</c>, wrapping, widening from any integer
    /// physical type.
    /// </summary>
    /// <param name="source">The values, little-endian, at least <c>destination.Length</c> of them.</param>
    /// <param name="ptype">The element's physical type; must be an integer.</param>
    /// <param name="destination">The i64 accumulator.</param>
    /// <param name="scale">The multiplier applied to every widened value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ptype"/> is not an integer.</exception>
    public static void AddWidenScaled(
        ReadOnlySpan<byte> source, PType ptype, Span<long> destination, long scale)
    {
        switch (ptype)
        {
            case PType.U8: AddWidenScaled<byte>(source, destination, scale); break;
            case PType.U16: AddWidenScaled<ushort>(source, destination, scale); break;
            case PType.U32: AddWidenScaled<uint>(source, destination, scale); break;
            case PType.U64: AddWidenScaled<ulong>(source, destination, scale); break;
            case PType.I8: AddWidenScaled<sbyte>(source, destination, scale); break;
            case PType.I16: AddWidenScaled<short>(source, destination, scale); break;
            case PType.I32: AddWidenScaled<int>(source, destination, scale); break;
            case PType.I64: AddWidenScaled<long>(source, destination, scale); break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(ptype), ptype, "Widening needs an integer physical type.");
        }
    }

    // CreateTruncating, never CreateChecked or CreateSaturating: the reference widens with Rust's
    // `as` cast (num_traits AsPrimitive), which keeps the low 64 bits of a u64 rather than clamping
    // it to i64::MAX. CompressedValues.ReadInteger saturates instead, and is right to - its callers
    // compare against a non-negative bound - but a value-producing path must not.
    private static void WidenScaled<T>(ReadOnlySpan<byte> source, Span<long> destination, long scale)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = unchecked(long.CreateTruncating(src[i]) * scale);
        }
    }

    private static void AddWidenScaled<T>(ReadOnlySpan<byte> source, Span<long> destination, long scale)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = unchecked(destination[i] + (long.CreateTruncating(src[i]) * scale));
        }
    }

    private static void RequireSameLength(int source, int destination)
    {
        if (source != destination)
        {
            throw new ArgumentException(
                $"An element-wise kernel needs equal spans; got {source} and {destination} bytes.",
                nameof(destination));
        }
    }
}
