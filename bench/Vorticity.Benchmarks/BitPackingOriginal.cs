using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Vorticity.Benchmarks;

/// <summary>
/// The row-at-a-time vector unpack, kept here unchanged as the baseline every change to
/// <c>FastLanes.UnpackBlocks</c> is measured against in the same process.
/// </summary>
/// <remarks>
/// Self-contained on purpose: nothing here calls into the library, so editing the library's kernel
/// can never move this arm.
/// </remarks>
internal static class BitPackingOriginal
{
    private const int BlockSize = 1024;

    private static ReadOnlySpan<byte> Order => [0, 4, 2, 6, 1, 5, 3, 7];

    /// <summary>Unpacks <paramref name="blocks"/> consecutive blocks, bit width strictly between 0 and the element width.</summary>
    internal static void UnpackBlocks<T>(ReadOnlySpan<T> packed, int bitWidth, Span<T> output, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        int lanes = BlockSize / elementBits;
        Vectorized(packed, ShapesOf<T>(bitWidth), output, lanes, lanes * bitWidth, blocks);
    }

    private static Row<T>[] ShapesOf<T>(int bitWidth)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        int elementBits = Unsafe.SizeOf<T>() * 8;
        Row<T>[]?[] widths = Shapes<T>.ByWidth ??= new Row<T>[]?[elementBits + 1];
        Row<T>[]? shapes = widths[bitWidth];
        if (shapes is null)
        {
            shapes = new Row<T>[elementBits];
            for (int row = 0; row < elementBits; row++)
            {
                shapes[row] = new Row<T>(row, bitWidth, elementBits);
            }

            widths[bitWidth] = shapes;
        }

        return shapes;
    }

    private static class Shapes<T>
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        internal static Row<T>[]?[]? ByWidth;
    }

    private readonly struct Row<T>
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        internal Row(int row, int bitWidth, int elementBits)
        {
            Destination = (Order[row / 8] * 16) + ((row % 8) * 128);
            CurrentWord = row * bitWidth / elementBits;
            Shift = (row * bitWidth) % elementBits;

            int nextWord = ((row + 1) * bitWidth) / elementBits;
            NextWord = nextWord;

            if (nextWord > CurrentWord)
            {
                int remainingBits = ((row + 1) * bitWidth) % elementBits;
                LowMask = Mask<T>(bitWidth - remainingBits);
                CurrentBits = bitWidth - remainingBits;
                Spills = nextWord < bitWidth;
                HighMask = Mask<T>(remainingBits);
            }
            else
            {
                LowMask = Mask<T>(bitWidth);
                CurrentBits = 0;
                Spills = false;
                HighMask = default;
            }
        }

        internal int Destination { get; }

        internal int CurrentWord { get; }

        internal int NextWord { get; }

        internal int Shift { get; }

        internal int CurrentBits { get; }

        internal T LowMask { get; }

        internal T HighMask { get; }

        internal bool Spills { get; }
    }

    private static void Vectorized<T>(
        ReadOnlySpan<T> packed, ReadOnlySpan<Row<T>> shapes, Span<T> output, int lanes,
        int wordsPerBlock, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        if (Vector512.IsHardwareAccelerated)
        {
            Unpack512(packed, shapes, output, lanes, wordsPerBlock, blocks);
            return;
        }

        if (Vector256.IsHardwareAccelerated)
        {
            Unpack256(packed, shapes, output, lanes, wordsPerBlock, blocks);
            return;
        }

        Unpack128(packed, shapes, output, lanes, wordsPerBlock, blocks);
    }

    private static void Unpack512<T>(
        ReadOnlySpan<T> packed, ReadOnlySpan<Row<T>> shapes, Span<T> output, int lanes,
        int wordsPerBlock, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref T destination = ref MemoryMarshal.GetReference(output);
        ref Row<T> shapeTable = ref MemoryMarshal.GetReference(shapes);
        int rows = shapes.Length;
        int step = Vector512<T>.Count;

        for (int block = 0; block < blocks; block++)
        {
            nuint blockSource = (nuint)((nint)block * wordsPerBlock);
            nuint blockDestination = (nuint)((nint)block * BlockSize);

            for (int row = 0; row < rows; row++)
            {
                ref Row<T> shape = ref Unsafe.Add(ref shapeTable, row);
                Vector512<T> low = Vector512.Create(shape.LowMask);
                Vector512<T> high = Vector512.Create(shape.HighMask);
                nuint current = blockSource + (nuint)(lanes * shape.CurrentWord);
                nuint next = blockSource + (nuint)(lanes * shape.NextWord);
                nuint into = blockDestination + (nuint)shape.Destination;
                int shift = shape.Shift;
                int currentBits = shape.CurrentBits;
                if (shape.Spills)
                {
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector512<T> value = ShiftRight(
                            Vector512.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                        value |= ShiftLeft(
                            Vector512.LoadUnsafe(ref source, next + (nuint)lane) & high, currentBits);
                        value.StoreUnsafe(ref destination, into + (nuint)lane);
                    }

                    continue;
                }

                for (int lane = 0; lane < lanes; lane += step)
                {
                    Vector512<T> value = ShiftRight(
                        Vector512.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                    value.StoreUnsafe(ref destination, into + (nuint)lane);
                }
            }
        }
    }

    private static void Unpack256<T>(
        ReadOnlySpan<T> packed, ReadOnlySpan<Row<T>> shapes, Span<T> output, int lanes,
        int wordsPerBlock, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref T destination = ref MemoryMarshal.GetReference(output);
        ref Row<T> shapeTable = ref MemoryMarshal.GetReference(shapes);
        int rows = shapes.Length;
        int step = Vector256<T>.Count;

        for (int block = 0; block < blocks; block++)
        {
            nuint blockSource = (nuint)((nint)block * wordsPerBlock);
            nuint blockDestination = (nuint)((nint)block * BlockSize);

            for (int row = 0; row < rows; row++)
            {
                ref Row<T> shape = ref Unsafe.Add(ref shapeTable, row);
                Vector256<T> low = Vector256.Create(shape.LowMask);
                Vector256<T> high = Vector256.Create(shape.HighMask);
                nuint current = blockSource + (nuint)(lanes * shape.CurrentWord);
                nuint next = blockSource + (nuint)(lanes * shape.NextWord);
                nuint into = blockDestination + (nuint)shape.Destination;
                int shift = shape.Shift;
                int currentBits = shape.CurrentBits;
                if (shape.Spills)
                {
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector256<T> value = ShiftRight(
                            Vector256.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                        value |= ShiftLeft(
                            Vector256.LoadUnsafe(ref source, next + (nuint)lane) & high, currentBits);
                        value.StoreUnsafe(ref destination, into + (nuint)lane);
                    }

                    continue;
                }

                for (int lane = 0; lane < lanes; lane += step)
                {
                    Vector256<T> value = ShiftRight(
                        Vector256.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                    value.StoreUnsafe(ref destination, into + (nuint)lane);
                }
            }
        }
    }

    private static void Unpack128<T>(
        ReadOnlySpan<T> packed, ReadOnlySpan<Row<T>> shapes, Span<T> output, int lanes,
        int wordsPerBlock, int blocks)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
    {
        ref T source = ref MemoryMarshal.GetReference(packed);
        ref T destination = ref MemoryMarshal.GetReference(output);
        ref Row<T> shapeTable = ref MemoryMarshal.GetReference(shapes);
        int rows = shapes.Length;
        int step = Vector128<T>.Count;

        for (int block = 0; block < blocks; block++)
        {
            nuint blockSource = (nuint)((nint)block * wordsPerBlock);
            nuint blockDestination = (nuint)((nint)block * BlockSize);

            for (int row = 0; row < rows; row++)
            {
                ref Row<T> shape = ref Unsafe.Add(ref shapeTable, row);
                Vector128<T> low = Vector128.Create(shape.LowMask);
                Vector128<T> high = Vector128.Create(shape.HighMask);
                nuint current = blockSource + (nuint)(lanes * shape.CurrentWord);
                nuint next = blockSource + (nuint)(lanes * shape.NextWord);
                nuint into = blockDestination + (nuint)shape.Destination;
                int shift = shape.Shift;
                int currentBits = shape.CurrentBits;
                if (shape.Spills)
                {
                    for (int lane = 0; lane < lanes; lane += step)
                    {
                        Vector128<T> value = ShiftRight(
                            Vector128.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                        value |= ShiftLeft(
                            Vector128.LoadUnsafe(ref source, next + (nuint)lane) & high, currentBits);
                        value.StoreUnsafe(ref destination, into + (nuint)lane);
                    }

                    continue;
                }

                for (int lane = 0; lane < lanes; lane += step)
                {
                    Vector128<T> value = ShiftRight(
                        Vector128.LoadUnsafe(ref source, current + (nuint)lane), shift) & low;
                    value.StoreUnsafe(ref destination, into + (nuint)lane);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> ShiftRight<T>(Vector512<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector512.ShiftRightLogical(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector512.ShiftRightLogical(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector512.ShiftRightLogical(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector512.ShiftRightLogical(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<T> ShiftLeft<T>(Vector512<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector512.ShiftLeft(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector512.ShiftLeft(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector512.ShiftLeft(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector512.ShiftLeft(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> ShiftRight<T>(Vector256<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector256.ShiftRightLogical(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector256.ShiftRightLogical(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector256.ShiftRightLogical(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector256.ShiftRightLogical(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> ShiftLeft<T>(Vector256<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector256.ShiftLeft(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector256.ShiftLeft(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector256.ShiftLeft(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector256.ShiftLeft(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> ShiftRight<T>(Vector128<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector128.ShiftRightLogical(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector128.ShiftRightLogical(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector128.ShiftRightLogical(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector128.ShiftRightLogical(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> ShiftLeft<T>(Vector128<T> value, int count)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            return Vector128.ShiftLeft(value.AsByte(), count).As<byte, T>();
        }

        if (typeof(T) == typeof(ushort))
        {
            return Vector128.ShiftLeft(value.AsUInt16(), count).As<ushort, T>();
        }

        if (typeof(T) == typeof(uint))
        {
            return Vector128.ShiftLeft(value.AsUInt32(), count).As<uint, T>();
        }

        return Vector128.ShiftLeft(value.AsUInt64(), count).As<ulong, T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Mask<T>(int width)
        where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T> =>
        (T.One << width) - T.One;
}
